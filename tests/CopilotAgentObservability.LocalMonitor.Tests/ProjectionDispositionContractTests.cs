using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ProjectionDispositionContractTests
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 7, 17, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Commit_PersistsRawObservationAndNotStartedDispositionInOneTransaction()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, ValidPayload("atomic"));
        var store = ProjectionStore(temp.DatabasePath);

        Assert.Single(new RawTelemetryStore(temp.DatabasePath, RetentionCatalogContext.AdoptExistingCatalogV1(temp.DatabasePath)).ListRecords());
        Assert.Single(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.NotStarted, 1, ObservedAt);
    }

    [Fact]
    public void GetProjectionDisposition_UsesOnlyExactRawIdAndNeverSynthesizesAbsence()
    {
        using var temp = new MonitorTempDirectory();
        var first = Commit(temp.DatabasePath, ValidPayload("exact-first"));
        var second = Commit(temp.DatabasePath, ValidPayload("exact-second"));
        var store = ProjectionStore(temp.DatabasePath);

        Assert.NotEqual(first.RawRecordId, second.RawRecordId);
        Assert.Equal(first.RawRecordId, store.GetProjectionDisposition(first.RawRecordId)!.RawRecordId);
        Assert.Equal(second.RawRecordId, store.GetProjectionDisposition(second.RawRecordId)!.RawRecordId);
        Assert.Null(store.GetProjectionDisposition(second.RawRecordId + 1));
    }

    [Fact]
    public void Commit_WhenDispositionInsertAborts_RollsBackRawObservationAndDisposition()
    {
        using var temp = new MonitorTempDirectory();
        CreateSchema(temp.DatabasePath);
        Execute(
            temp.DatabasePath,
            """
            CREATE TRIGGER abort_projection_disposition_insert
            BEFORE INSERT ON monitor_projection_dispositions
            BEGIN
                SELECT RAISE(ABORT, 'injected disposition insert failure');
            END;
            """);

        var exception = Assert.Throws<SqliteException>(() => CommitWithoutSchema(temp.DatabasePath, ValidPayload("rollback")));

        Assert.Contains("injected disposition insert failure", exception.Message, StringComparison.Ordinal);
        Assert.Empty(new RawTelemetryStore(temp.DatabasePath, RetentionCatalogContext.AdoptExistingCatalogV1(temp.DatabasePath)).ListRecords());
        Assert.Empty(new SqliteSourceCompatibilityStore(temp.DatabasePath).List(after: null, limit: 200));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM monitor_projection_dispositions;"));
    }

    [Fact]
    public async Task TryBeginProjection_UsesRevisionCasSoExactlyOneConcurrentCallerWins()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, ValidPayload("cas"));
        var store = ProjectionStore(temp.DatabasePath);
        using var barrier = new Barrier(participantCount: 3);

        var first = Task.Run(() => BeginAtBarrier(store, committed.RawRecordId, barrier));
        var second = Task.Run(() => BeginAtBarrier(store, committed.RawRecordId, barrier));
        barrier.SignalAndWait();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result);
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Pending, 2, ObservedAt.AddMinutes(1));
        Assert.False(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 1, ObservedAt.AddMinutes(2)));
    }

    [Fact]
    public void FailedDisposition_CanRetryOnlyFromItsExactRevision()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, ValidPayload("retry"));
        var store = ProjectionStore(temp.DatabasePath);
        Assert.True(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 1, ObservedAt.AddMinutes(1)));

        Assert.False(store.RecordProjectionFailure(
            committed.RawRecordId,
            expectedRevision: 1,
            ObservedAt.AddMinutes(2)));
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Pending, 2, ObservedAt.AddMinutes(1));
        Assert.True(store.RecordProjectionFailure(
            committed.RawRecordId,
            expectedRevision: 2,
            ObservedAt.AddMinutes(2)));

        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Failed, 3, ObservedAt.AddMinutes(2));
        Assert.False(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 2, ObservedAt.AddMinutes(3)));
        Assert.True(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 3, ObservedAt.AddMinutes(4)));
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Pending, 4, ObservedAt.AddMinutes(4));
    }

    [Fact]
    public void ApplyProjection_AtomicallyPersistsProjectionAndCompletedDisposition()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, ValidPayload("completed"));
        var store = ProjectionStore(temp.DatabasePath);
        Assert.True(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 1, ObservedAt.AddMinutes(1)));

        Assert.False(store.ApplyProjection(
            committed.RawRecordId,
            RawTelemetrySources.RawOtlp,
            ObservedAt,
            EmptyProjection(),
            ObservedAt.AddMinutes(2),
            expectedDispositionRevision: 1));
        Assert.Empty(store.ListMonitorIngestions(afterRawRecordId: 0, limit: 10).Items);
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Pending, 2, ObservedAt.AddMinutes(1));

        Assert.True(store.ApplyProjection(
            committed.RawRecordId,
            RawTelemetrySources.RawOtlp,
            ObservedAt,
            EmptyProjection(),
            ObservedAt.AddMinutes(2),
            expectedDispositionRevision: 2));

        Assert.Single(store.ListMonitorIngestions(afterRawRecordId: 0, limit: 10).Items);
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Completed, 3, ObservedAt.AddMinutes(2));
    }

    [Fact]
    public void ApplyProjection_WhenCompletedTransitionAborts_RollsBackProjectionAndLeavesPending()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, ValidPayload("completion-rollback"));
        var store = ProjectionStore(temp.DatabasePath);
        Assert.True(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 1, ObservedAt.AddMinutes(1)));
        Execute(
            temp.DatabasePath,
            """
            CREATE TRIGGER abort_projection_completed
            BEFORE UPDATE OF state ON monitor_projection_dispositions
            WHEN NEW.state = 'completed'
            BEGIN
                SELECT RAISE(ABORT, 'injected completed transition failure');
            END;
            """);

        var exception = Assert.Throws<SqliteException>(() => store.ApplyProjection(
            committed.RawRecordId,
            RawTelemetrySources.RawOtlp,
            ObservedAt,
            EmptyProjection(),
            ObservedAt.AddMinutes(2),
            expectedDispositionRevision: 2));

        Assert.Contains("injected completed transition failure", exception.Message, StringComparison.Ordinal);
        Assert.Empty(store.ListMonitorIngestions(afterRawRecordId: 0, limit: 10).Items);
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Pending, 2, ObservedAt.AddMinutes(1));
    }

    [Fact]
    public async Task ProjectionWorker_CaughtErrorPersistsFailedDispositionWithoutErrorOrRawContent()
    {
        const string sensitiveMarker = "sensitive-projection-exception-marker";
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, $"{{\"{sensitiveMarker}\":");
        var store = ProjectionStore(temp.DatabasePath);
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var worker = new ProjectionWorker(
            new RawTelemetryStoreProjectionStore(store),
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath),
            new MutableTimeProvider(ObservedAt.AddMinutes(1)));

        await worker.RunProjectionPassAsync();

        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Failed, 3, ObservedAt.AddMinutes(1));
        Assert.Equal(
            new[] { "raw_record_id", "state", "revision", "updated_at" },
            ReadColumnNames(temp.DatabasePath, "monitor_projection_dispositions"));
        Assert.DoesNotContain(sensitiveMarker, ReadDispositionRow(temp.DatabasePath, committed.RawRecordId), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectionWorker_AfterRestartClaimsExactPendingRevisionAndCompletesProjection()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, ValidPayload("pending-restart-complete"));
        var store = ProjectionStore(temp.DatabasePath);
        Assert.True(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 1, ObservedAt.AddMinutes(1)));
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Pending, 2, ObservedAt.AddMinutes(1));

        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var restartedWorker = new ProjectionWorker(
            new RawTelemetryStoreProjectionStore(ProjectionStore(temp.DatabasePath)),
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath),
            new MutableTimeProvider(ObservedAt.AddMinutes(2)));

        await restartedWorker.RunProjectionPassAsync();

        Assert.Single(store.ListMonitorIngestions(afterRawRecordId: 0, limit: 10).Items);
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Completed, 4, ObservedAt.AddMinutes(2));
    }

    [Fact]
    public async Task ProjectionWorker_AfterRestartClaimsExactPendingRevisionAndRecordsCaughtFailure()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, "{\"corrupt_pending_restart\":");
        var store = ProjectionStore(temp.DatabasePath);
        Assert.True(store.TryBeginProjection(committed.RawRecordId, expectedRevision: 1, ObservedAt.AddMinutes(1)));
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Pending, 2, ObservedAt.AddMinutes(1));

        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        var restartedWorker = new ProjectionWorker(
            new RawTelemetryStoreProjectionStore(ProjectionStore(temp.DatabasePath)),
            health,
            new SqliteSourceCompatibilityStore(temp.DatabasePath),
            new MutableTimeProvider(ObservedAt.AddMinutes(2)));

        await restartedWorker.RunProjectionPassAsync();

        Assert.Empty(store.ListMonitorIngestions(afterRawRecordId: 0, limit: 10).Items);
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Failed, 4, ObservedAt.AddMinutes(2));
    }

    [Fact]
    public async Task ProjectionWorker_CaughtErrorUsesOnlyItsOwnedRevisionWhenAnotherWorkerReclaimsAndCompletes()
    {
        using var temp = new MonitorTempDirectory();
        var committed = Commit(temp.DatabasePath, "{\"corrupt_concurrent_projection\":");
        var store = ProjectionStore(temp.DatabasePath);
        using var firstClaimed = new Barrier(participantCount: 2);
        using var secondClaimed = new Barrier(participantCount: 2);
        using var firstFailureAttempted = new Barrier(participantCount: 2);
        var interleavedStore = new InterleavedProjectionStore(
            new RawTelemetryStoreProjectionStore(store),
            firstClaimed,
            secondClaimed,
            firstFailureAttempted);
        var firstWorker = new ProjectionWorker(
            interleavedStore,
            ReadyHealth(),
            timeProvider: new MutableTimeProvider(ObservedAt.AddMinutes(1)));
        var secondWorker = new ProjectionWorker(
            interleavedStore,
            ReadyHealth(),
            timeProvider: new MutableTimeProvider(ObservedAt.AddMinutes(2)));

        var firstPass = Task.Run(() => firstWorker.RunProjectionPassAsync());
        firstClaimed.SignalAndWait();
        var secondPass = Task.Run(() => secondWorker.RunProjectionPassAsync());
        await Task.WhenAll(firstPass, secondPass);

        Assert.Equal(new[] { 1, 2 }, interleavedStore.BeginExpectedRevisions);
        Assert.Equal(2, interleavedStore.FailureExpectedRevision);
        Assert.False(interleavedStore.FailureRecorded);
        Assert.True(interleavedStore.CompletionRecorded);
        AssertDisposition(store.GetProjectionDisposition(committed.RawRecordId), ProjectionDispositionState.Completed, 4, ObservedAt.AddMinutes(2));
        Assert.Single(store.ListMonitorIngestions(afterRawRecordId: 0, limit: 10).Items);
    }

    [Fact]
    public void ProjectionDispositionStoreMethods_AreRequiredInterfaceMembersWithoutDefaultBodies()
    {
        var methods = typeof(IMonitorProjectionStore).GetMethods()
            .Where(method =>
                method.Name is nameof(IMonitorProjectionStore.GetProjectionDisposition)
                    or nameof(IMonitorProjectionStore.TryBeginProjection)
                    or nameof(IMonitorProjectionStore.RecordProjectionFailure) ||
                method.Name == nameof(IMonitorProjectionStore.ApplyProjection) && method.GetParameters().Length == 6)
            .ToArray();

        Assert.Equal(4, methods.Length);
        Assert.All(methods, method =>
        {
            Assert.True(method.IsAbstract, $"{method.Name} must be implemented explicitly by every projection store.");
            Assert.Null(method.GetMethodBody());
        });
    }

    [Fact]
    public void ProjectionDispositionDto_ExposesOnlySanitizedStateAndCasMembers()
    {
        Assert.Equal(
            new[] { "RawRecordId", "Revision", "State", "UpdatedAt" },
            typeof(ProjectionDisposition).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    private static bool BeginAtBarrier(RawTelemetryStore store, long rawRecordId, Barrier barrier)
    {
        barrier.SignalAndWait();
        return store.TryBeginProjection(rawRecordId, expectedRevision: 1, ObservedAt.AddMinutes(1));
    }

    private static void AssertDisposition(
        ProjectionDisposition? disposition,
        ProjectionDispositionState expectedState,
        int expectedRevision,
        DateTimeOffset expectedUpdatedAt)
    {
        Assert.NotNull(disposition);
        Assert.Equal(expectedState, disposition.State);
        Assert.Equal(expectedRevision, disposition.Revision);
        Assert.Equal(expectedUpdatedAt, disposition.UpdatedAt);
    }

    private static CommittedIngestionIds Commit(string databasePath, string payload)
    {
        CreateSchema(databasePath);
        return CommitWithoutSchema(databasePath, payload);
    }

    private static CommittedIngestionIds CommitWithoutSchema(string databasePath, string payload)
    {
        var inventory = OtlpJsonStructuralWalker.Build(ValidPayload("inventory"), ObservedAt);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            RawTelemetrySources.RawOtlp,
            sourceApplicationVersion: null,
            RawTelemetrySources.RawOtlp,
            "1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                RawTelemetrySources.RawOtlp,
                sourceApplicationVersion: null,
                inventory,
                observedRecognizedCount: 1,
                registry: VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Unsupported,
            ObservedAt);
        var raw = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: null,
            ReceivedAt: ObservedAt,
            ResourceAttributesJson: null,
            PayloadJson: payload);
        return new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(raw, observation));
    }

    private static void CreateSchema(string databasePath)
    {
        RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath);
        new SqliteSourceCompatibilityStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
    }

    private static RawTelemetryStore ProjectionStore(string databasePath) =>
        new(databasePath, RetentionCatalogContext.AdoptExistingCatalogV1(databasePath), connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter);

    private static MonitorRecordProjection EmptyProjection() =>
        new(TraceId: null, ClientKind: null, SpanCount: 0, TraceContributions: []);

    private static MonitorHealthState ReadyHealth()
    {
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        return health;
    }

    private static string ValidPayload(string traceId) =>
        $$"""{"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"{{traceId}}","spanId":"span"}]}]}]}""";

    private static void Execute(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private static string[] ReadColumnNames(string databasePath, string table)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }
        return names.ToArray();
    }

    private static string ReadDispositionRow(string databasePath, long rawRecordId)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT raw_record_id || '|' || state || '|' || revision || '|' || updated_at FROM monitor_projection_dispositions WHERE raw_record_id = $id;";
        command.Parameters.AddWithValue("$id", rawRecordId);
        return (string)command.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private sealed class InterleavedProjectionStore : ProjectionStoreTestDouble
    {
        private readonly IMonitorProjectionStore inner;
        private readonly Barrier firstClaimed;
        private readonly Barrier secondClaimed;
        private readonly Barrier firstFailureAttempted;
        private int listCallCount;
        private int beginCallCount;

        public InterleavedProjectionStore(
            IMonitorProjectionStore inner,
            Barrier firstClaimed,
            Barrier secondClaimed,
            Barrier firstFailureAttempted)
        {
            this.inner = inner;
            this.firstClaimed = firstClaimed;
            this.secondClaimed = secondClaimed;
            this.firstFailureAttempted = firstFailureAttempted;
        }

        public List<int> BeginExpectedRevisions { get; } = [];

        public int? FailureExpectedRevision { get; private set; }

        public bool FailureRecorded { get; private set; }

        public bool CompletionRecorded { get; private set; }

        public override async ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForProjectionAsync(int limit, CancellationToken cancellationToken)
        {
            var result = await inner.ListUnprocessedForProjectionAsync(limit, cancellationToken);
            if (result.Lease is null) return result;
            await using var lease = result.Lease;
            var records = lease.Value;
            if (Interlocked.Increment(ref listCallCount) == 1)
            {
                return Granted<IReadOnlyList<RawTelemetryRecord>>(records);
            }

            return Granted<IReadOnlyList<RawTelemetryRecord>>(records
                .Select(record => record with { PayloadJson = ValidPayload("concurrent-complete") })
                .ToArray());
        }

        public override bool ApplyProjection(
            long rawRecordId,
            string source,
            DateTimeOffset receivedAt,
            MonitorRecordProjection projection,
            DateTimeOffset projectedAt) =>
            inner.ApplyProjection(rawRecordId, source, receivedAt, projection, projectedAt);

        public override ProjectionDisposition? GetProjectionDisposition(long rawRecordId) =>
            inner.GetProjectionDisposition(rawRecordId);

        public override bool TryBeginProjection(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt)
        {
            var call = Interlocked.Increment(ref beginCallCount);
            lock (BeginExpectedRevisions)
            {
                BeginExpectedRevisions.Add(expectedRevision);
            }

            var claimed = inner.TryBeginProjection(rawRecordId, expectedRevision, updatedAt);
            Assert.True(claimed);
            if (call == 1)
            {
                firstClaimed.SignalAndWait();
                secondClaimed.SignalAndWait();
            }
            else
            {
                secondClaimed.SignalAndWait();
            }
            return claimed;
        }

        public override bool RecordProjectionFailure(long rawRecordId, int expectedRevision, DateTimeOffset updatedAt)
        {
            FailureExpectedRevision = expectedRevision;
            FailureRecorded = inner.RecordProjectionFailure(rawRecordId, expectedRevision, updatedAt);
            firstFailureAttempted.SignalAndWait();
            return FailureRecorded;
        }

        public override bool ApplyProjection(
            long rawRecordId,
            string source,
            DateTimeOffset receivedAt,
            MonitorRecordProjection projection,
            DateTimeOffset projectedAt,
            int expectedDispositionRevision)
        {
            firstFailureAttempted.SignalAndWait();
            CompletionRecorded = inner.ApplyProjection(
                rawRecordId,
                source,
                receivedAt,
                projection,
                projectedAt,
                expectedDispositionRevision);
            return CompletionRecorded;
        }

        public override MonitorProjectionStatus GetProjectionStatus() => inner.GetProjectionStatus();

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListUnprocessedForSpanProjectionAsync(int limit, CancellationToken cancellationToken) =>
            inner.ListUnprocessedForSpanProjectionAsync(limit, cancellationToken);

        public override bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt) =>
            inner.ApplySpanProjection(rawRecordId, spans, projectedAt);

        public override MonitorProjectionStatus GetSpanProjectionStatus() => inner.GetSpanProjectionStatus();

        public override MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) =>
            inner.ListMonitorIngestions(afterRawRecordId, limit);

        public override MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) =>
            inner.ListMonitorTraces(afterId, limit);

        public override MonitorTraceRow? GetMonitorTrace(string traceId) => inner.GetMonitorTrace(traceId);

        public override MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) =>
            inner.ListMonitorSpans(traceId, afterId, limit);

        public override IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) => inner.GetSpansForTrace(traceId);

        public override ValueTask<RetentionReadResult<RawTelemetryRecord>> GetRawRecordByIdAsync(long id, RetentionReadKind readKind, CancellationToken cancellationToken) =>
            inner.GetRawRecordByIdAsync(id, readKind, cancellationToken);

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadRawRecordsAsync(IReadOnlyList<long> ids, RetentionReadKind readKind, CancellationToken cancellationToken) =>
            inner.ReadRawRecordsAsync(ids, readKind, cancellationToken);

        public override ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ListRawRecordsByTraceIdAsync(string traceId, int limit, RetentionReadKind readKind, CancellationToken cancellationToken) =>
            inner.ListRawRecordsByTraceIdAsync(traceId, limit, readKind, cancellationToken);

        public override MonitorPeriodSummaryRow GetPeriodSummary(string startInclusive, string endExclusive) =>
            inner.GetPeriodSummary(startInclusive, endExclusive);

        public override IReadOnlyList<MonitorModelPeriodSummaryRow> GetPerModelPeriodSummary(string startInclusive, string endExclusive) =>
            inner.GetPerModelPeriodSummary(startInclusive, endExclusive);

        public override IReadOnlyList<MonitorHourlyTokensRow> GetHourlyTokenDistribution(string startInclusive, string endExclusive) =>
            inner.GetHourlyTokenDistribution(startInclusive, endExclusive);

        public override IReadOnlyList<MonitorTraceRow> ListTopTokenTraces(string startInclusive, string endExclusive, int limit) =>
            inner.ListTopTokenTraces(startInclusive, endExclusive, limit);

        public override IReadOnlyList<MonitorTraceRow> ListRecentMonitorTraces(int limit) => inner.ListRecentMonitorTraces(limit);

        public override MonitorTraceListPage ListMonitorTracesFiltered(MonitorTraceListQuery query) => inner.ListMonitorTracesFiltered(query);

        public override MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) => inner.GetMonitorSpan(traceId, spanId);

        public override IReadOnlyList<MonitorConversationTraceRow> ListConversationTraces(string conversationId) =>
            inner.ListConversationTraces(conversationId);
    }
}

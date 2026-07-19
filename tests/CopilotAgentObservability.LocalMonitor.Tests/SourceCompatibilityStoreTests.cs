using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceCompatibilityStoreTests
{
    [Fact]
    public void ValidatedBatch_RejectsCallerSuppliedRawRecordIdentity()
    {
        var valid = CreateBatch("batch-validation", BuildOverflowInventory());

        var exception = Assert.Throws<ArgumentException>(() =>
            ValidatedIngestionBatch.Create(valid.RawRecord with { Id = 42 }, valid.Observation));

        Assert.Equal("rawRecord", exception.ParamName);
    }

    [Fact]
    public void Commit_PersistsRawObservationAndCanonicalUnknownsAtomically()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var batch = CreateBatch("batch-atomic", BuildOverflowInventory());

        var result = new SqliteIngestionCommitStore(database.Path).Commit(batch);

        var raw = Assert.Single(database.CreateRawStore().ListRecords());
        Assert.Equal(result.RawRecordId, raw.Id);

        var observation = Assert.Single(new SqliteSourceCompatibilityStore(database.Path).List(after: null, limit: 200));
        Assert.Equal(result.ObservationId, observation.Id);
        Assert.Equal("batch-atomic", observation.ObservationId);
        Assert.Equal("batch-atomic", observation.IngestBatchId);
        Assert.Equal(result.RawRecordId, observation.RawRecordId);
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, observation.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.SchemaDriftDetected], observation.ReasonCodes);
        Assert.Equal(SourceCompatibilityNextActions.CaptureFixtureAndReviewMapping, observation.NextAction);
        Assert.Equal(SourceCaptureContentState.Available, observation.CaptureContentState);
        Assert.Equal(257, observation.UnknownAttributeCount);
        Assert.Equal(3, observation.UnknownSpanCount);
        Assert.Equal(4, observation.UnknownEventCount);
        Assert.Equal(3, observation.OverflowDistinctCount);
        Assert.Equal(3, observation.OverflowOccurrenceCount);
        Assert.Equal(256, observation.UnknownObservations.Count);
        Assert.Equal(254, observation.UnknownObservations.Count(child => child.Kind == SourceUnknownKind.Attribute));
        Assert.Equal(1, observation.UnknownObservations.Count(child => child.Kind == SourceUnknownKind.Span));
        Assert.Equal(1, observation.UnknownObservations.Count(child => child.Kind == SourceUnknownKind.Event));
        Assert.All(observation.UnknownObservations, child =>
        {
            Assert.Equal("unverified", child.SourceVersionLabel);
            Assert.DoesNotContain("@example.test", child.Name, StringComparison.Ordinal);
            Assert.StartsWith("sample:v1:", child.OpaqueSampleReference, StringComparison.Ordinal);
        });
        Assert.Equal(3, Assert.Single(observation.UnknownObservations, child => child.Kind == SourceUnknownKind.Span).Count);
        Assert.Equal(4, Assert.Single(observation.UnknownObservations, child => child.Kind == SourceUnknownKind.Event).Count);
    }

    [Fact]
    public void Commit_WhenSecondUnknownInsertFails_RollsBackRawParentAndFirstChild()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using (var connection = Open(database.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "CREATE TRIGGER inject_unknown_failure BEFORE INSERT ON source_unknown_observations " +
                "WHEN (SELECT COUNT(*) FROM source_unknown_observations WHERE source_observation_id = NEW.source_observation_id) >= 1 " +
                "BEGIN SELECT RAISE(ABORT, 'injected unknown failure after first child'); END;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() =>
            new SqliteIngestionCommitStore(database.Path).Commit(CreateBatch("batch-rollback", BuildOverflowInventory())));

        using var verification = Open(database.Path);
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    [Fact]
    public void Commit_DuplicateBatchIsIdempotentAndReturnsExactIds()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path);
        var batch = CreateBatch("batch-duplicate", BuildOverflowInventory());

        var first = store.Commit(batch);
        var second = store.Commit(batch);

        Assert.Equal(first, second);
        using var connection = Open(database.Path);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(256L, Scalar(connection, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    [Fact]
    public void RecordAdapterFailure_PersistsNullableMetadataAndListUsesBoundedCursor()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var store = new SqliteSourceCompatibilityStore(database.Path);
        var at = new DateTimeOffset(2026, 7, 12, 3, 4, 5, TimeSpan.FromHours(9));

        var firstId = store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
            "failure-1", null, null, null, null, null, null, at));
        var secondId = store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateAdapterException(
            "failure-2", "batch-failed", "claude-code", "1.2.3", "claude-code-otel", "adapter-v1",
            SourceCaptureContentState.NotCaptured, at.AddMinutes(1)));
        var thirdId = store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
            "failure-3", null, null, null, null, null, null, at.AddMinutes(2)));

        var firstPage = store.List(after: null, limit: 2);
        Assert.Equal([firstId, secondId], firstPage.Select(row => row.Id));
        var first = firstPage[0];
        Assert.Equal("failure-1", first.ObservationId);
        Assert.Null(first.RawRecordId);
        Assert.Null(first.IngestBatchId);
        Assert.Null(first.SourceSurface);
        Assert.Null(first.SourceApplicationVersion);
        Assert.Null(first.SourceAdapter);
        Assert.Null(first.AdapterVersion);
        Assert.Null(first.SchemaFingerprint);
        Assert.Null(first.InventoryHash);
        Assert.Null(first.CaptureContentState);
        Assert.Empty(first.UnknownObservations);
        Assert.Equal(SourceCompatibilityState.AdapterFailure, first.CompatibilityState);
        Assert.Equal([SourceCompatibilityReasonCodes.AdapterParseFailure], first.ReasonCodes);
        Assert.Equal(at.ToUniversalTime(), first.ObservedAt);

        var secondPage = store.List(after: secondId, limit: 2);
        Assert.Equal([thirdId], secondPage.Select(row => row.Id));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(after: null, limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(after: null, limit: 201));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.List(after: -1, limit: 1));
    }

    [Fact]
    public void List_UsesStableAscendingCursorsForLessThanExactAndMultiplePages()
    {
        using var database = new TestDatabase();
        var store = new SqliteSourceCompatibilityStore(database.Path);
        store.CreateSchema();
        var observedAt = DateTimeOffset.UnixEpoch;
        for (var index = 1; index <= 51; index++)
        {
            store.RecordAdapterFailure(SourceAdapterFailureDraft.CreateParseFailure(
                $"cursor-{index}", null, null, null, null, null, null, observedAt.AddMinutes(index)));
        }

        var firstPage = store.List(after: null, limit: 50);
        var secondPage = store.List(after: firstPage[^1].Id, limit: 50);
        var finalPage = store.List(after: secondPage[^1].Id, limit: 50);
        var underLimitPage = store.List(after: null, limit: 200);

        Assert.Equal(Enumerable.Range(1, 50).Select(value => (long)value), firstPage.Select(row => row.Id));
        Assert.Equal([51L], secondPage.Select(row => row.Id));
        Assert.Empty(finalPage);
        Assert.Equal(51, underLimitPage.Count);
        Assert.Equal(Enumerable.Range(1, 51).Select(value => (long)value), underLimitPage.Select(row => row.Id));
    }

    [Fact]
    public void List_MapsConcreteSqliteReadLockToPersistenceBusyException()
    {
        using var database = new TestDatabase();
        var connectionOptions = new RawTelemetryStoreConnectionOptions(EnableWriteAheadLog: false, BusyTimeoutMilliseconds: 0);
        var store = new SqliteSourceCompatibilityStore(database.Path, connectionOptions);
        store.CreateSchema();
        using var lockConnection = Open(database.Path);
        Execute(lockConnection, "PRAGMA locking_mode = EXCLUSIVE;");
        Execute(lockConnection, "BEGIN EXCLUSIVE;");
        try
        {
            Assert.Throws<PersistenceBusyException>(() => store.List(after: null, limit: 1));
        }
        finally
        {
            Execute(lockConnection, "ROLLBACK;");
        }
    }

    [Fact]
    public void CreateSchema_RejectsNewerVersionWithoutRewritingStamp()
    {
        using var database = new TestDatabase();
        var compatibilityStore = new SqliteSourceCompatibilityStore(database.Path);
        compatibilityStore.CreateSchema();
        using (var connection = Open(database.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_version SET version = 8 WHERE component = 'monitor';";
            command.ExecuteNonQuery();
        }

        var exception = Assert.Throws<InvalidOperationException>(compatibilityStore.CreateSchema);

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        using var verification = Open(database.Path);
        Assert.Equal(8L, Scalar(verification, "SELECT version FROM schema_version WHERE component = 'monitor';"));
    }

    [Fact]
    public void RawInitialization_PreservesV6AndFutureMonitorStamps()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var rawStore = database.CreateRawStore();

        rawStore.CreateMonitorSchema();

        using var connection = Open(database.Path);
        Assert.Equal(7L, Scalar(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE schema_version SET version = 8 WHERE component = 'monitor';";
            command.ExecuteNonQuery();
        }

        rawStore.CreateMonitorSchema();

        Assert.Equal(8L, Scalar(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));
    }

    [Fact]
    public void CreateSchema_WhenSecondV6ObjectFails_RollsBackFirstObjectAndVersionStamp()
    {
        using var database = new TestDatabase();
        database.CreateRawStore().CreateMonitorSchema();
        using (var connection = Open(database.Path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE VIEW source_unknown_observations AS SELECT 1 AS conflict;";
            command.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => new SqliteSourceCompatibilityStore(database.Path).CreateSchema());

        using var verification = Open(database.Path);
        Assert.Empty(Columns(verification, "source_schema_observations"));
        Assert.Equal(RawTelemetryStore.MonitorSchemaVersion, Scalar(verification, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name = 'source_unknown_observations';"));
    }

    [Fact]
    public void CreateSchema_FocusedStoreOwnsSanitizedSourceTablesAndV6Stamp()
    {
        using var database = new TestDatabase();
        database.CreateRawStore().CreateMonitorSchema();
        using var connection = Open(database.Path);

        Assert.Empty(Columns(connection, "source_schema_observations"));
        Assert.Empty(Columns(connection, "source_unknown_observations"));
        Assert.Equal(RawTelemetryStore.MonitorSchemaVersion, Scalar(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        connection.Close();

        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        connection.Open();
        Assert.Equal(SqliteSourceCompatibilityStore.MonitorSchemaVersion, Scalar(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));

        Assert.Equal(
            [
                "id", "observation_id", "raw_record_id", "ingest_batch_id", "source_surface",
                "source_application_version", "source_adapter", "adapter_version", "schema_fingerprint",
                "inventory_hash", "compatibility_state", "reason_code", "next_action", "capture_content_state",
                "unknown_span_count", "unknown_event_count", "unknown_attribute_count", "overflow_distinct_count",
                "overflow_occurrence_count", "observed_at",
            ],
            Columns(connection, "source_schema_observations"));
        Assert.Equal(
            [
                "id", "source_observation_id", "kind", "name", "occurrence_count", "source_version_label",
                "first_observed_at", "last_observed_at", "opaque_sample_reference",
            ],
            Columns(connection, "source_unknown_observations"));

        string[] forbidden = ["payload_json", "resource_attributes_json", "raw_value", "value", "user_id", "user_email", "path"];
        foreach (var table in new[] { "source_schema_observations", "source_unknown_observations" })
        {
            Assert.DoesNotContain(Columns(connection, table), forbidden.Contains);
        }

        Assert.Equal(
            ["IX_source_schema_observations_cursor", "IX_source_unknown_observations_cursor"],
            Indexes(connection).Where(name => name.EndsWith("_cursor", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void SourceObservationSchema_RejectsArbitraryNextActionViaDirectSql()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO source_schema_observations (
                observation_id, compatibility_state, reason_code, next_action, capture_content_state,
                unknown_span_count, unknown_event_count, unknown_attribute_count,
                overflow_distinct_count, overflow_occurrence_count, observed_at
            ) VALUES (
                'invalid-action', 'schema_drift_detected', 'schema_drift_detected', 'arbitrary_action', 'available',
                0, 0, 0, 0, 0, '2026-07-12T00:00:00.0000000+00:00'
            );
            """;

        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM source_schema_observations;"));
    }

    [Fact]
    public void Commit_WriteLockReturnsTypedBusyWithoutRowsOrCursor_ExplicitReplaySucceedsOnce()
    {
        using var database = new TestDatabase();
        var options = new RawTelemetryStoreConnectionOptions(EnableWriteAheadLog: true, BusyTimeoutMilliseconds: 0);
        new SqliteSourceCompatibilityStore(database.Path, options).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path, options);
        var batch = CreateBatch("batch-busy", BuildOverflowInventory());
        using var lockConnection = Open(database.Path);
        Execute(lockConnection, "BEGIN IMMEDIATE;");
        try
        {
            Assert.Throws<IngestionCommitBusyException>(() => store.Commit(batch));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COUNT(*) FROM raw_records;"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COUNT(*) FROM source_schema_observations;"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COUNT(*) FROM source_unknown_observations;"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'raw_records'), 0);"));
            Assert.Equal(0L, Scalar(lockConnection, "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name = 'source_schema_observations'), 0);"));
        }
        finally
        {
            Execute(lockConnection, "ROLLBACK;");
        }

        var committed = store.Commit(batch);
        var replayed = store.Commit(batch);

        Assert.Equal(committed, replayed);
        using var verification = Open(database.Path);
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(256L, Scalar(verification, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    [Theory]
    [InlineData("AfterRawRecordInsert")]
    [InlineData("AfterCatalogRegistration")]
    [InlineData("BeforeCommit")]
    public void Commit_CheckpointFailureRollsBackRawCatalogAndSourceTogether(string phaseName)
    {
        var phase = Enum.Parse<IngestionCommitWritePhase>(phaseName, ignoreCase: false);
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new RetentionCatalogStore(database.Path).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path, connectionOptions: null, actual =>
        {
            if (actual == phase) throw new InvalidOperationException("injected direct-ingestion failure");
        });

        Assert.Throws<InvalidOperationException>(() => store.Commit(CreateBatch("batch-atomic-" + phase, BuildOverflowInventory())));

        using var connection = Open(database.Path);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM monitor_projection_dispositions;"));
    }

    [Fact]
    public void Commit_RegistersExactRawReceiptAndReplayPreservesIds()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path);
        var batch = CreateBatch("batch-catalog-receipt", BuildOverflowInventory());

        var committed = store.Commit(batch);
        Assert.Equal(committed, store.Commit(batch));

        using var connection = Open(database.Path);
        Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM raw_records WHERE id={committed.RawRecordId.ToString(CultureInfo.InvariantCulture)} AND received_at='2026-07-12T00:00:00.0000000+00:00' AND length(retention_owner_token)=32;"));
        Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId.ToString(CultureInfo.InvariantCulture)}' AND captured_at='2026-07-12T00:00:00.0000000+00:00';"));
    }

    [Fact]
    public async Task Commit_DuplicateDeliveryReleasedAtBarrier_ProducesOneIdentityOrTypedBusyThenReplaysExactIds()
    {
        using var database = new TestDatabase();
        var options = new RawTelemetryStoreConnectionOptions(EnableWriteAheadLog: true, BusyTimeoutMilliseconds: 0);
        new SqliteSourceCompatibilityStore(database.Path, options).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path, options);
        var batch = CreateBatch("batch-race", BuildOverflowInventory());
        using var barrier = new Barrier(participantCount: 3);

        Task<CommitAttempt> StartAttempt() => Task.Run(() =>
        {
            barrier.SignalAndWait();
            try
            {
                return new CommitAttempt(store.Commit(batch), Busy: false);
            }
            catch (IngestionCommitBusyException)
            {
                return new CommitAttempt(null, Busy: true);
            }
        });

        var firstTask = StartAttempt();
        var secondTask = StartAttempt();
        barrier.SignalAndWait();
        var attempts = await Task.WhenAll(firstTask, secondTask);
        var committedIds = attempts.Where(attempt => attempt.Ids is not null).Select(attempt => attempt.Ids!).ToArray();
        Assert.NotEmpty(committedIds);
        var committed = committedIds[0];
        Assert.All(committedIds, ids => Assert.Equal(committed, ids));
        Assert.All(attempts, attempt => Assert.True(attempt.Busy || attempt.Ids == committed));

        var replayed = store.Commit(batch);

        Assert.Equal(committed, replayed);
        using var verification = Open(database.Path);
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Equal(256L, Scalar(verification, "SELECT COUNT(*) FROM source_unknown_observations;"));
    }

    private static ValidatedIngestionBatch CreateBatch(string ingestBatchId, SourceStructuralInventory inventory)
    {
        var observation = SourceObservationBatchDraft.Create(
            ingestBatchId,
            "claude-code",
            "unverified",
            "claude-code-otel",
            "adapter-v1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                "claude-code",
                "unverified",
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Available,
            new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
        return ValidatedIngestionBatch.Create(
            new RawTelemetryRecord(
                Id: null,
                Source: RawTelemetrySources.RawOtlp,
                TraceId: "11111111111111111111111111111111",
                ReceivedAt: new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero),
                ResourceAttributesJson: null,
                PayloadJson: "{}"),
            observation);
    }

    private static SourceStructuralInventory BuildOverflowInventory()
    {
        var json = new StringBuilder("{");
        for (var index = 0; index < 257; index++)
        {
            if (index != 0)
            {
                json.Append(',');
            }
            json.Append('"').Append("unknown-").Append(index.ToString("D3", CultureInfo.InvariantCulture)).Append("@example.test\":\"secret-value\"");
        }
        json.Append(",\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{}]}]}]}");
        var walked = OtlpJsonStructuralWalker.Build(json.ToString(), DateTimeOffset.UnixEpoch);
        var occurrences = walked.StructuralOccurrences.ToList();
        occurrences.Add(UnknownProducerOccurrence(
            SourceUnknownKind.Span,
            SourceStructuralEnvelope.Span,
            SourceStructuralRole.SpanName,
            "unrecognized-span@example.test",
            count: 3,
            sampleHex: 'c'));
        occurrences.Add(UnknownProducerOccurrence(
            SourceUnknownKind.Event,
            SourceStructuralEnvelope.Event,
            SourceStructuralRole.EventName,
            "unrecognized-event@example.test",
            count: 4,
            sampleHex: 'd'));
        return SourceStructuralInventory.Create(occurrences, hasRequiredTraceSignal: true);
    }

    private static SourceStructuralOccurrence UnknownProducerOccurrence(
        SourceUnknownKind kind,
        SourceStructuralEnvelope envelope,
        SourceStructuralRole role,
        string rawName,
        int count,
        char sampleHex)
    {
        var name = SourceStructuralNameToken.FromProducerName(role, rawName);
        var occurrenceCount = SourceOccurrenceCount.Create(count);
        var identity = SourceUnknownIdentity.Create(
            kind,
            name,
            occurrenceCount,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            $"sample:v1:{new string(sampleHex, 64)}");
        return SourceStructuralOccurrence.Create(
            envelope,
            role,
            name,
            SourceStructuralType.String,
            occurrenceCount,
            identity);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_xinfo($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string[] Indexes(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"source-compatibility-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }

        public TimeProvider TimeProvider { get; } = global::System.TimeProvider.System;

        public RetentionCatalogContext RetentionContext =>
            retentionContext ??= RetentionCatalogContext.InitializeNewOwnedDatabase(Path, TimeProvider);

        private RetentionCatalogContext? retentionContext;

        public RawTelemetryStore CreateRawStore(RawTelemetryStoreConnectionOptions? connectionOptions = null) =>
            new(Path, RetentionContext, TimeProvider, connectionOptions);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed record CommitAttempt(CommittedIngestionIds? Ids, bool Busy);
}

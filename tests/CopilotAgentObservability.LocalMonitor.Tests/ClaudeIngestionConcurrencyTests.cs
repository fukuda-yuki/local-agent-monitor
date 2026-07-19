using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ClaudeIngestionConcurrencyTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-13T00:00:00Z");

    [Fact]
    public async Task DuplicateHookRequestsQueuedTogetherCommitOneSourceIdentity()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        store.CreateSchema();
        var queue = new SessionEventQueue(capacity: 2);
        var worker = new SessionEventWriterWorker(
            queue,
            new SessionEventNormalizer(store, new FixedTimeProvider(ObservedAt)));
        var envelope = ClaudeEnvelope("claude-hook-duplicate");
        Assert.True(queue.TryEnqueue(envelope, out var first));
        Assert.True(queue.TryEnqueue(envelope, out var second));

        await worker.StartAsync(CancellationToken.None);
        var statuses = await Task.WhenAll(first!.Completion, second!.Completion);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal([SessionEventCommitStatus.Committed, SessionEventCommitStatus.Committed], statuses);
        var session = Assert.Single(store.ListMostRecent(10));
        var detail = Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId));
        Assert.Single(detail.Events, item => item.SourceAdapter == "claude-code-hook" && item.SourceEventId == "claude-hook-duplicate");
    }

    [Fact]
    public void ConflictingClaudeSourceIdentityThrowsTypedConflictAndRollsBackWholeBatch()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        store.CreateSchema();
        var first = ClaudeBatch(Guid.CreateVersion7(), "native-a", "shared-source-id");
        store.Write(first);
        var conflicting = ClaudeBatch(Guid.CreateVersion7(), "native-b", "shared-source-id");

        var exception = Assert.Throws<SessionIdentityConflictException>(() => store.Write(conflicting));
        Assert.DoesNotContain("shared-source-id", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("native-a", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("native-b", exception.Message, StringComparison.Ordinal);

        Assert.Single(store.ListMostRecent(10));
        using var connection = Open(temp.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_native_ids;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Fact]
    public void CompetingOwnerClaudeOtelIdentityThrowsTypedConflictInsteadOfSilentReplay()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        store.CreateSchema();
        var owner = ClaudeBatch(Guid.CreateVersion7(), "otel-owner-a", "shared-otel-id", "claude-code-otel");
        store.Write(owner);
        var competing = ClaudeBatch(Guid.CreateVersion7(), "otel-owner-b", "shared-otel-id", "claude-code-otel");

        Assert.Throws<SessionIdentityConflictException>(() => store.Write(competing));

        Assert.Single(store.ListMostRecent(10));
        using var connection = Open(temp.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_events WHERE source_adapter='claude-code-otel';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public async Task BarrierRaceBetweenDifferentOwnersHasOneTypedConflictAndNoPartialLoser()
    {
        using var temp = new MonitorTempDirectory();
        new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext).CreateSchema();
        using var beforeWrite = new Barrier(participantCount: 2);
        Action<string> checkpoint = name =>
        {
            if (name == "before-session-write")
            {
                beforeWrite.SignalAndWait();
            }
        };
        var firstStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, new FixedTimeProvider(ObservedAt), checkpoint);
        var secondStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, new FixedTimeProvider(ObservedAt), checkpoint);
        var first = ClaudeBatch(Guid.CreateVersion7(), "racing-native-a", "racing-source-id");
        var second = ClaudeBatch(Guid.CreateVersion7(), "racing-native-b", "racing-source-id");

        var outcomes = await Task.WhenAll(
            Task.Run(() => CaptureWrite(firstStore, first)),
            Task.Run(() => CaptureWrite(secondStore, second)));

        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is SessionIdentityConflictException);
        using var connection = Open(temp.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_native_ids;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Fact]
    public async Task ControlledWriteLockReturnsBusyWithoutPartialHookRows()
    {
        using var temp = new MonitorTempDirectory();
        var schemaStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        schemaStore.CreateSchema();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        using var blocker = Open(temp.DatabasePath);
        using var blockingTransaction = blocker.BeginTransaction(deferred: false);
        var queue = new SessionEventQueue(capacity: 1);
        var worker = new SessionEventWriterWorker(queue, new SessionEventNormalizer(store, new FixedTimeProvider(ObservedAt)));
        Assert.True(queue.TryEnqueue(ClaudeEnvelope("busy-hook"), out var request));

        await worker.StartAsync(CancellationToken.None);
        var status = await request!.Completion;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(SessionEventCommitStatus.Busy, status);
        Assert.Empty(store.ListMostRecent(10));
        blockingTransaction.Commit();

        var replayQueue = new SessionEventQueue(capacity: 1);
        var replayWorker = new SessionEventWriterWorker(
            replayQueue,
            new SessionEventNormalizer(store, new FixedTimeProvider(ObservedAt)));
        Assert.True(replayQueue.TryEnqueue(ClaudeEnvelope("busy-hook"), out var replay));
        await replayWorker.StartAsync(CancellationToken.None);
        Assert.Equal(SessionEventCommitStatus.Committed, await replay!.Completion);
        await replayWorker.StopAsync(CancellationToken.None);
        Assert.Single(store.ListMostRecent(10));
    }

    [Fact]
    public async Task TriggerFailureReturnsFailedAndRollsBackWholeHookBatch()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext);
        store.CreateSchema();
        using (var connection = Open(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_claude_hook BEFORE INSERT ON session_events
                WHEN NEW.source_adapter='claude-code-hook'
                BEGIN SELECT RAISE(ABORT,'synthetic hook failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        var queue = new SessionEventQueue(capacity: 1);
        var worker = new SessionEventWriterWorker(queue, new SessionEventNormalizer(store, new FixedTimeProvider(ObservedAt)));
        Assert.True(queue.TryEnqueue(ClaudeEnvelope("failed-hook"), out var request));

        await worker.StartAsync(CancellationToken.None);
        var status = await request!.Completion;
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(SessionEventCommitStatus.Failed, status);
        Assert.Empty(store.ListMostRecent(10));
        using var verification = Open(temp.DatabasePath);
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM session_native_ids;"));
        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM session_event_content;"));
        using (var command = verification.CreateCommand())
        {
            command.CommandText = "DROP TRIGGER fail_claude_hook;";
            command.ExecuteNonQuery();
        }

        var replayQueue = new SessionEventQueue(capacity: 1);
        var replayWorker = new SessionEventWriterWorker(
            replayQueue,
            new SessionEventNormalizer(store, new FixedTimeProvider(ObservedAt)));
        Assert.True(replayQueue.TryEnqueue(ClaudeEnvelope("failed-hook"), out var replay));
        await replayWorker.StartAsync(CancellationToken.None);
        Assert.Equal(SessionEventCommitStatus.Committed, await replay!.Completion);
        await replayWorker.StopAsync(CancellationToken.None);
        Assert.Single(store.ListMostRecent(10));
    }

    [Fact]
    public async Task OperationLeaseContentionReturnsBusyWithoutMutationThenReplayCommitsOnce()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeOtelFixture(temp.DatabasePath);
        using var connection = Open(temp.DatabasePath);
        var rawRecordId = Scalar(connection, "SELECT raw_record_id FROM monitor_spans ORDER BY id LIMIT 1;");
        temp.TimeProvider = new FixedTimeProvider(ObservedAt);
        var rawStore = temp.CreateRawStore();
        var held = await rawStore.ReadRawRecordsAsync([rawRecordId], RetentionReadKind.Operation, CancellationToken.None);
        Assert.Equal(RetentionReadDisposition.Granted, held.Disposition);

        await using (Assert.IsAssignableFrom<IAsyncDisposable>(held.Lease))
        {
            var blocked = new SqliteSessionOtelEnricher(
                temp.DatabasePath, store, temp.RetentionContext, new FixedTimeProvider(ObservedAt));

            Assert.Equal(0, blocked.ProcessNextBatch(1));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM session_events WHERE source_adapter='claude-code-otel';"));
            Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM session_runs;"));
            Assert.Null(store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey));
        }

        var replay = new SqliteSessionOtelEnricher(
            temp.DatabasePath, store, temp.RetentionContext, new FixedTimeProvider(ObservedAt));
        Assert.Equal(1, replay.ProcessNextBatch(1));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_events WHERE source_adapter='claude-code-otel';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(1L, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Fact]
    public void ClaudeOtelCursorReplayIsIdempotentAndRecompletesCursor()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeOtelFixture(temp.DatabasePath);
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, new FixedTimeProvider(ObservedAt));
        Assert.Equal(1, enricher.ProcessNextBatch(1));
        store.UpsertProjectionState(new(SqliteSessionOtelEnricher.ProjectorKey, 0, 0, ObservedAt));

        Assert.Equal(1, enricher.ProcessNextBatch(1));

        using var connection = Open(temp.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_events WHERE source_adapter='claude-code-otel';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(1L, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Fact]
    public async Task HookCommitAfterOtelCommitPreservesMonotonicOwnerStateAndBothEvents()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeOtelFixture(temp.DatabasePath);
        var sessionId = SeedExactClaudeOwner(store, "SYNTHETIC_SESSION_001");
        var hookStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, new FixedTimeProvider(ObservedAt));
        var queue = new SessionEventQueue(capacity: 1);
        var worker = new SessionEventWriterWorker(queue, new SessionEventNormalizer(hookStore, new FixedTimeProvider(ObservedAt)));

        Assert.Equal(1, new SqliteSessionOtelEnricher(
            temp.DatabasePath, store, temp.RetentionContext, new FixedTimeProvider(ObservedAt.AddMinutes(1))).ProcessNextBatch(1));
        var afterOtel = Assert.IsType<SessionDetail>(store.GetDetail(sessionId));
        Assert.True(queue.TryEnqueue(ClaudeEnvelope("hook-after-otel", "SYNTHETIC_SESSION_001"), out var request));
        await worker.StartAsync(CancellationToken.None);
        Assert.Equal(SessionEventCommitStatus.Committed, await request!.Completion);
        await worker.StopAsync(CancellationToken.None);

        var final = Assert.IsType<SessionDetail>(store.GetDetail(sessionId));
        Assert.Equal(SessionCompleteness.Full, final.Session.Completeness);
        Assert.True(final.Session.LastSeenAt >= afterOtel.Session.LastSeenAt);
        Assert.Single(final.Events, item => item.SourceAdapter == "claude-code-otel");
        Assert.Single(final.Events, item => item.SourceAdapter == "claude-code-hook" && item.SourceEventId == "hook-after-otel");
        Assert.Equal(1L, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    [Fact]
    public void ClaudeOtelFailureRollsBackAggregateAndCursorThenExplicitReplayCommitsOnce()
    {
        using var temp = new MonitorTempDirectory();
        var store = PrepareClaudeOtelFixture(temp.DatabasePath);
        using (var connection = Open(temp.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TRIGGER fail_claude_otel BEFORE INSERT ON session_events
                WHEN NEW.source_adapter='claude-code-otel'
                BEGIN SELECT RAISE(ABORT,'synthetic otel failure'); END;
                """;
            command.ExecuteNonQuery();
        }
        var enricher = new SqliteSessionOtelEnricher(temp.DatabasePath, store, temp.RetentionContext, new FixedTimeProvider(ObservedAt));

        Assert.Throws<SqliteException>(() => enricher.ProcessNextBatch(1));

        Assert.Null(store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey));
        using (var verification = Open(temp.DatabasePath))
        {
            Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM sessions;"));
            Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM session_runs;"));
            Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM session_events WHERE source_adapter='claude-code-otel';"));
            Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM source_schema_observations;"));
            using var command = verification.CreateCommand();
            command.CommandText = "DROP TRIGGER fail_claude_otel;";
            command.ExecuteNonQuery();
        }

        Assert.Equal(1, enricher.ProcessNextBatch(1));
        using var committed = Open(temp.DatabasePath);
        Assert.Equal(1L, Scalar(committed, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1L, Scalar(committed, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(1L, Scalar(committed, "SELECT COUNT(*) FROM session_events WHERE source_adapter='claude-code-otel';"));
        Assert.Equal(1L, store.GetProjectionState(SqliteSessionOtelEnricher.ProjectorKey)!.ProjectionCursor);
    }

    private static SessionIngestEnvelope ClaudeEnvelope(
        string sourceEventId,
        string nativeSessionId = "claude-native-session") => new(
        SchemaVersion: 1,
        SourceAdapter: "claude-code-hook",
        SourceSurface: "claude-code",
        NativeSessionId: nativeSessionId,
        Events:
        [
            new SessionIngestEvent(
                sourceEventId,
                "UserPromptSubmit",
                ObservedAt.ToString("O"),
                JsonDocument.Parse("""{"message":"synthetic"}""").RootElement.Clone()),
        ],
        SourceApplicationVersion: "fixture-v1",
        AdapterVersion: "claude-hook-v1",
        NormalizationVersion: "session-normalization-v1");

    private static SessionWriteBatch ClaudeBatch(
        Guid sessionId,
        string nativeSessionId,
        string sourceEventId,
        string sourceAdapter = "claude-code-hook")
    {
        var eventId = Guid.CreateVersion7();
        return new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId,
                    ObservedSessionStatus.Active,
                    SessionCompleteness.Partial,
                    Repository: null,
                    Workspace: null,
                    StartedAt: ObservedAt,
                    EndedAt: null,
                    LastSeenAt: ObservedAt,
                    SessionRawRetentionState.Expiring,
                    CreatedAt: ObservedAt,
                    UpdatedAt: ObservedAt),
                [new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, nativeSessionId, SessionBindingKind.Native, ObservedAt)],
                [],
                [new ObservedSessionEvent(
                    eventId,
                    sessionId,
                    RunId: null,
                    SessionSourceSurface.ClaudeCode,
                    ParentEventId: null,
                    TraceId: null,
                    Status: null,
                    SourceAdapter: sourceAdapter,
                    SourceEventId: sourceEventId,
                    Type: "UserPromptSubmit",
                    OccurredAt: ObservedAt,
                    SessionContentState.Available,
                    SourceApplicationVersion: "fixture-v1",
                    AdapterVersion: "claude-hook-v1",
                    SchemaFingerprint: null,
                    NormalizationVersion: "session-normalization-v1")]),
            [new SessionEventContent(eventId, "application/json", "{}", ObservedAt, ObservedAt.AddDays(90))]);
    }

    private static SqliteSessionStore PrepareClaudeOtelFixture(string databasePath)
    {
        var payload = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestData", "Claude", "otel", "content-disabled.json"));
        var compatibilityStore = new SqliteSourceCompatibilityStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        compatibilityStore.CreateSchema();
        var store = new SqliteSessionStore(databasePath);
        store.CreateSchema();
        var inventory = OtlpJsonStructuralWalker.Build(payload, ObservedAt);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            "claude-code",
            sourceApplicationVersion: null,
            "claude-code-otel",
            "claude-otel-v1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                "claude-code", null, inventory, 6, VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.NotCaptured,
            ObservedAt);
        var raw = RawOtlpIngestor.CreateRecordFromPayloadJson(payload, ObservedAt);
        var committed = new SqliteIngestionCommitStore(
            databasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(raw, observation));
        var persisted = raw with { Id = committed.RawRecordId };
        var projections = new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        projections.ApplyProjection(
            committed.RawRecordId,
            persisted.Source,
            persisted.ReceivedAt,
            MonitorProjectionBuilder.Build(persisted),
            ObservedAt);
        projections.ApplySpanProjection(
            committed.RawRecordId,
            MonitorSpanProjectionBuilder.Build(persisted),
            ObservedAt);
        return store;
    }

    private static Guid SeedExactClaudeOwner(SqliteSessionStore store, string nativeSessionId)
    {
        var sessionId = Guid.CreateVersion7();
        var eventTypes = new[] { "SessionStart", "UserPromptSubmit", "SessionEnd" };
        var events = eventTypes.Select((type, index) => new ObservedSessionEvent(
            Guid.CreateVersion7(),
            sessionId,
            RunId: null,
            SessionSourceSurface.ClaudeCode,
            ParentEventId: null,
            TraceId: null,
            Status: null,
            SourceAdapter: "claude-code-hook",
            SourceEventId: $"seed-{index}",
            Type: type,
            OccurredAt: ObservedAt.AddSeconds(index),
            SessionContentState.NotCaptured,
            SourceApplicationVersion: "fixture-v1",
            AdapterVersion: "claude-hook-v1",
            SchemaFingerprint: null,
            NormalizationVersion: "session-normalization-v1")).ToArray();
        store.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId,
                    ObservedSessionStatus.Completed,
                    SessionCompleteness.Rich,
                    Repository: null,
                    Workspace: null,
                    StartedAt: ObservedAt,
                    EndedAt: ObservedAt.AddSeconds(2),
                    LastSeenAt: ObservedAt.AddSeconds(2),
                    SessionRawRetentionState.NotCaptured,
                    CreatedAt: ObservedAt,
                    UpdatedAt: ObservedAt),
                [new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, nativeSessionId, SessionBindingKind.Native, ObservedAt)],
                [],
                events),
            []));
        return sessionId;
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static Exception? CaptureWrite(SqliteSessionStore store, SessionWriteBatch batch)
    {
        try
        {
            store.Write(batch);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

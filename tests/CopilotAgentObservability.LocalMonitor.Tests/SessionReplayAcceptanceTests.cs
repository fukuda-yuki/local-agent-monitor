using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionReplayAcceptanceTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-08-10T00:00:00+00:00");

    public static TheoryData<string> IndependentEventVectorFields => new()
    {
        "run_id",
        "source_surface",
        "parent_event_id",
        "trace_id",
        "status",
        "type",
        "occurred_at",
        "content_state",
        "source_application_version",
        "adapter_version",
        "schema_fingerprint",
        "normalization_version",
        "match_kind",
    };

    [Theory]
    [MemberData(nameof(IndependentEventVectorFields))]
    public void Write_ReplayRejectsEachIndependentEventVectorMismatch(string field)
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var fixture = CreateComparatorFixture();
        store.Write(fixture.Batch);
        using var beforeConnection = database.Open();
        var before = Snapshot(beforeConnection);
        beforeConnection.Close();
        var replay = field switch
        {
            "run_id" => fixture.Target with { RunId = fixture.AlternateRunId },
            "source_surface" => fixture.Target with { SourceSurface = SessionSourceSurface.CopilotCli },
            "parent_event_id" => fixture.Target with { ParentEventId = fixture.AlternateParentId },
            "trace_id" => fixture.Target with { TraceId = "trace-comparator-other" },
            "status" => fixture.Target with { Status = "completed" },
            "type" => fixture.Target with { Type = "event.changed" },
            "occurred_at" => fixture.Target with { OccurredAt = fixture.Target.OccurredAt.AddTicks(1) },
            "content_state" => fixture.Target with { ContentState = SessionContentState.Redacted },
            "source_application_version" => fixture.Target with { SourceApplicationVersion = "2.0.0" },
            "adapter_version" => fixture.Target with { AdapterVersion = "adapter-v2" },
            "schema_fingerprint" => fixture.Target with { SchemaFingerprint = new string('b', 64) },
            "normalization_version" => fixture.Target with { NormalizationVersion = "normalization-v2" },
            "match_kind" => fixture.Target with { MatchKind = SessionMatchKind.ExplicitLink },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };
        replay = replay with { EventId = Guid.CreateVersion7() };

        Assert.Throws<InvalidOperationException>(() =>
            store.Write(ReplayBatch(fixture.Batch.Detail.Session, replay)));

        using var afterConnection = database.Open();
        Assert.Equal(before, Snapshot(afterConnection));
    }

    [Fact]
    public void Write_ReplayRejectsIndependentSessionIdMismatch()
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var firstSeed = CreateSingleEventBatch("session-owner-a");
        var firstEvent = firstSeed.Detail.Events[0] with { RunId = null };
        var first = firstSeed with
        {
            Detail = firstSeed.Detail with { Runs = [], Events = [firstEvent] },
        };
        var secondSeed = CreateSingleEventBatch("session-owner-b");
        var second = secondSeed with
        {
            Detail = secondSeed.Detail with { Runs = [], Events = [] },
            Content = [],
        };
        store.Write(first);
        store.Write(second);
        using var beforeConnection = database.Open();
        var before = Snapshot(beforeConnection);
        beforeConnection.Close();
        var replay = firstEvent with
        {
            EventId = Guid.CreateVersion7(),
            SessionId = second.Detail.Session.SessionId,
        };

        Assert.Throws<InvalidOperationException>(() =>
            store.Write(ReplayBatch(second.Detail.Session, replay)));

        using var afterConnection = database.Open();
        Assert.Equal(before, Snapshot(afterConnection));
    }

    [Fact]
    public void Write_EventIdCollisionWithDistinctSourceIdentityRollsBack()
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var batch = CreateSingleEventBatch("event-id-collision");
        store.Write(batch);
        using var beforeConnection = database.Open();
        var before = Snapshot(beforeConnection);
        beforeConnection.Close();
        var collision = batch.Detail.Events[0] with
        {
            SourceAdapter = "collision-adapter",
            SourceEventId = "collision-source-event",
        };

        Assert.Throws<InvalidOperationException>(() =>
            store.Write(ReplayBatch(batch.Detail.Session, collision)));

        using var afterConnection = database.Open();
        Assert.Equal(before, Snapshot(afterConnection));
    }

    [Theory]
    [InlineData("source_adapter")]
    [InlineData("source_event_id")]
    public void Write_FreshEventIdWithDistinctSourceIdentityIsANewCandidate(string field)
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var batch = CreateSingleEventBatch("identity-boundary");
        store.Write(batch);
        var original = batch.Detail.Events[0];
        var candidate = field switch
        {
            "source_adapter" => original with
            {
                EventId = Guid.CreateVersion7(),
                SourceAdapter = "identity-adapter-other",
            },
            "source_event_id" => original with
            {
                EventId = Guid.CreateVersion7(),
                SourceEventId = "identity-source-other",
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        store.Write(ReplayBatch(batch.Detail.Session, candidate));

        using var connection = database.Open();
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(
            2L,
            Scalar<long>(
                connection,
                field == "source_adapter"
                    ? "SELECT COUNT(DISTINCT source_adapter) FROM session_events;"
                    : "SELECT COUNT(DISTINCT source_event_id) FROM session_events;"));
    }

    [Fact]
    public void Write_ReplayRejectsIndependentTerminalOutcomeMismatch()
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var batch = CreateTerminalBatch();
        var terminal = Assert.Single(batch.Detail.Events);
        WriteClassified(
            store,
            batch,
            new SessionTerminalFact(terminal.EventId, SessionTerminalOutcome.Clean));
        using var beforeConnection = database.Open();
        var before = Snapshot(beforeConnection);
        beforeConnection.Close();
        var replay = terminal with { EventId = Guid.CreateVersion7() };

        Assert.Throws<InvalidOperationException>(() =>
            WriteClassified(
                store,
                ReplayBatch(batch.Detail.Session, replay),
                new SessionTerminalFact(replay.EventId, SessionTerminalOutcome.Failed)));

        using var afterConnection = database.Open();
        Assert.Equal(before, Snapshot(afterConnection));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Write_ReplayComparesCanonicalTimestampInstants(bool sameInstant)
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var batch = CreateSingleEventBatch("timestamp-canonical");
        store.Write(batch);
        using var beforeConnection = database.Open();
        var before = Snapshot(beforeConnection);
        beforeConnection.Close();
        var persisted = batch.Detail.Events[0];
        var replay = persisted with
        {
            EventId = Guid.CreateVersion7(),
            OccurredAt = sameInstant
                ? persisted.OccurredAt.ToOffset(TimeSpan.FromHours(9))
                : persisted.OccurredAt.AddTicks(1).ToOffset(TimeSpan.FromHours(9)),
        };

        if (sameInstant)
            store.Write(ReplayBatch(batch.Detail.Session, replay));
        else
            Assert.Throws<InvalidOperationException>(() =>
                store.Write(ReplayBatch(batch.Detail.Session, replay)));

        using var afterConnection = database.Open();
        Assert.Equal(before, Snapshot(afterConnection));
    }

    [Fact]
    public void Write_ExactReplayRejectsConflictingExistingContentBytes()
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var seed = CreateSingleEventBatch("existing-content-conflict");
        var persistedEvent = seed.Detail.Events[0] with
        {
            ContentState = SessionContentState.Available,
        };
        seed = seed with
        {
            Detail = seed.Detail with { Events = [persistedEvent] },
            Content =
            [
                new SessionEventContent(
                    persistedEvent.EventId,
                    "application/json",
                    "{\"value\":\"durable\"}",
                    ObservedAt,
                    ObservedAt.AddDays(90)),
            ],
        };
        store.Write(seed);
        using var beforeConnection = database.Open();
        var before = Snapshot(beforeConnection);
        beforeConnection.Close();
        var replayEvent = persistedEvent with { EventId = Guid.CreateVersion7() };
        var replay = new SessionWriteBatch(
            new SessionDetail(seed.Detail.Session, [], [], [replayEvent]),
            [
                new SessionEventContent(
                    replayEvent.EventId,
                    "application/json",
                    "{\"value\":\"conflicting\"}",
                    ObservedAt,
                    ObservedAt.AddDays(90)),
            ]);

        Assert.Throws<InvalidOperationException>(() => store.Write(replay));

        using var afterConnection = database.Open();
        Assert.Equal(before, Snapshot(afterConnection));
    }

    [Fact]
    public void Write_ClaudeOtelReplayUsesTheCompleteEventComparator()
    {
        using var database = new ReplayDatabase();
        var store = CreateStore(database.Path);
        var seed = CreateSingleEventBatch("claude-otel-comparator");
        var durableEvent = seed.Detail.Events[0] with
        {
            SourceSurface = SessionSourceSurface.ClaudeCode,
            SourceAdapter = "claude-code-otel",
            SourceEventId = "11111111111111111111111111111111/2222222222222222",
            Type = "otel.span",
            TraceId = "11111111111111111111111111111111",
        };
        seed = seed with
        {
            Detail = seed.Detail with { Events = [durableEvent] },
        };
        store.Write(seed);
        using var beforeConnection = database.Open();
        var before = Snapshot(beforeConnection);
        beforeConnection.Close();
        var exactReplay = durableEvent with { EventId = Guid.CreateVersion7() };

        store.Write(ReplayBatch(seed.Detail.Session, exactReplay));

        using (var exactConnection = database.Open())
            Assert.Equal(before, Snapshot(exactConnection));
        var mismatch = exactReplay with
        {
            EventId = Guid.CreateVersion7(),
            Status = "completed",
        };

        Assert.Throws<InvalidOperationException>(() =>
            store.Write(ReplayBatch(seed.Detail.Session, mismatch)));

        using var afterConnection = database.Open();
        Assert.Equal(before, Snapshot(afterConnection));
    }

    private static ComparatorFixture CreateComparatorFixture()
    {
        var batch = CreateSingleEventBatch("comparator-vector");
        var session = batch.Detail.Session;
        var run = batch.Detail.Runs[0];
        var alternateRun = run with
        {
            RunId = Guid.CreateVersion7(),
            NativeRunId = "comparator-run-other",
        };
        var parent = batch.Detail.Events[0] with
        {
            EventId = Guid.CreateVersion7(),
            SourceAdapter = "comparator-parent",
            SourceEventId = "comparator-parent-a",
            Type = "parent.event",
            ParentEventId = null,
            MatchKind = null,
        };
        var alternateParent = parent with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "comparator-parent-b",
        };
        var target = batch.Detail.Events[0] with
        {
            EventId = Guid.CreateVersion7(),
            ParentEventId = parent.EventId,
            SourceAdapter = "comparator-adapter",
            SourceEventId = "comparator-source",
            Type = "event.original",
            SourceApplicationVersion = "1.0.0",
            AdapterVersion = "adapter-v1",
            SchemaFingerprint = new string('a', 64),
            NormalizationVersion = "normalization-v1",
            MatchKind = SessionMatchKind.ExactNative,
        };
        var comparatorBatch = batch with
        {
            Detail = batch.Detail with
            {
                Runs = [run, alternateRun],
                Events = [parent, alternateParent, target],
            },
            Content = [],
        };
        return new(comparatorBatch, target, alternateRun.RunId, alternateParent.EventId);
    }

    private static SessionWriteBatch CreateSingleEventBatch(string nativeId)
    {
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            "owner/repository",
            "workspace",
            ObservedAt.AddMinutes(-2),
            null,
            ObservedAt,
            SessionRawRetentionState.NotCaptured,
            ObservedAt.AddMinutes(-2),
            ObservedAt);
        var native = new SessionNativeId(
            sessionId,
            SessionSourceSurface.CopilotSdk,
            nativeId,
            SessionBindingKind.Native,
            ObservedAt.AddMinutes(-2));
        var run = new ObservedSessionRun(
            Guid.CreateVersion7(),
            sessionId,
            SessionSourceSurface.CopilotSdk,
            nativeId + "-run",
            "trace-comparator",
            null,
            "gpt-5",
            ObservedSessionStatus.Active,
            ObservedAt.AddMinutes(-1),
            null,
            1,
            2,
            3);
        var @event = new ObservedSessionEvent(
            Guid.CreateVersion7(),
            sessionId,
            run.RunId,
            SessionSourceSurface.CopilotSdk,
            null,
            "trace-comparator",
            "received",
            "fixture-adapter",
            nativeId + "-event",
            "fixture.event",
            ObservedAt,
            SessionContentState.NotCaptured,
            "1.0.0",
            "adapter-v1",
            new string('a', 64),
            "normalization-v1",
            SessionMatchKind.ExactNative);
        return new(new(session, [native], [run], [@event]), []);
    }

    private static SessionWriteBatch CreateTerminalBatch()
    {
        var batch = CreateSingleEventBatch("terminal-outcome");
        var terminal = batch.Detail.Events[0] with
        {
            SourceSurface = SessionSourceSurface.VisualStudioCode,
            SourceAdapter = "copilot-compatible-hook",
            SourceEventId = "terminal-outcome-event",
            Type = "SessionEnd",
            MatchKind = null,
        };
        return batch with
        {
            Detail = batch.Detail with { Events = [terminal] },
        };
    }

    private static SessionWriteBatch ReplayBatch(
        ObservedSession session,
        ObservedSessionEvent @event) =>
        new(new(session, [], [], [@event]), []);

    private static SqliteSessionStore CreateStore(string databasePath)
    {
        var clock = new ReplayTimeProvider(ObservedAt);
        var retention = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, clock);
        var store = new SqliteSessionStore(databasePath, retention, clock);
        store.CreateSchema();
        return store;
    }

    private static string Snapshot(SqliteConnection connection) => string.Join(
        "\n--\n",
        ReadRows(connection, "SELECT * FROM sessions ORDER BY session_id;"),
        ReadRows(connection, "SELECT * FROM session_native_ids ORDER BY session_id,source_surface,native_session_id;"),
        ReadRows(connection, "SELECT * FROM session_runs ORDER BY session_id,run_id;"),
        ReadRows(connection, "SELECT * FROM session_events ORDER BY session_id,event_id;"),
        ReadRows(connection, "SELECT * FROM session_event_content ORDER BY event_id;"),
        ReadRows(connection, "SELECT * FROM retention_items ORDER BY item_id;"));

    private static string ReadRows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join(
                "|",
                Enumerable.Range(0, reader.FieldCount).Select(index =>
                    reader.IsDBNull(index)
                        ? "null"
                        : reader.GetValue(index) is byte[] bytes
                            ? "blob:" + Convert.ToHexString(bytes)
                            : reader.GetFieldType(index).Name + ":" + reader.GetValue(index))));
        }
        return string.Join("\n", rows);
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static void WriteClassified(
        SqliteSessionStore store,
        SessionWriteBatch batch,
        params SessionTerminalFact[] facts) =>
        ((IClassifiedSessionStore)store).WriteClassified(batch, facts);

    private sealed record ComparatorFixture(
        SessionWriteBatch Batch,
        ObservedSessionEvent Target,
        Guid AlternateRunId,
        Guid AlternateParentId);

    private sealed class ReplayDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"session-replay-acceptance-{Guid.NewGuid():N}");

        internal ReplayDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "sessions.db");
        }

        internal string Path { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class ReplayTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

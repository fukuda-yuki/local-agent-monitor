using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionNormalizerReplayAcceptanceTests
{
    private static readonly DateTimeOffset ObservedAt =
        DateTimeOffset.Parse("2026-08-11T00:00:00Z", CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("copilot-sdk-stream", "copilot-sdk", "session.task_complete", "{}")]
    [InlineData("copilot-compatible-hook", "vscode", "SessionEnd", "{\"reason\":\"complete\"}")]
    public void NormalizeAndWrite_AdvancedClockExactTerminalReplayIsWholeDatabaseNoOp(
        string adapter,
        string surface,
        string type,
        string payload)
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        var envelope = Envelope(adapter, surface, Event("replay-event", type, payload));
        normalizer.NormalizeAndWrite(envelope);
        using (var connection = Open(temp.DatabasePath))
            Execute(connection, "UPDATE sessions SET raw_retention_state='expired_pending_deletion';");
        var before = Snapshot(temp.DatabasePath);

        clock.Advance(TimeSpan.FromDays(1));
        normalizer.NormalizeAndWrite(envelope);

        Assert.Equal(before, Snapshot(temp.DatabasePath));
        using var after = Open(temp.DatabasePath);
        Assert.Equal("expired_pending_deletion", Scalar<string>(after, "SELECT raw_retention_state FROM sessions;"));
    }

    [Fact]
    public void NormalizeAndWrite_ReplayContentMismatchRejectsAndRollsBack()
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        var before = Snapshot(temp.DatabasePath);

        Assert.Throws<InvalidOperationException>(() => normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"B\"}"))));

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Fact]
    public void NormalizeAndWrite_LaterClockExactContentReplayIsWholeDatabaseNoOp()
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        var envelope = Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}"));
        normalizer.NormalizeAndWrite(envelope);
        var before = Snapshot(temp.DatabasePath);

        clock.Advance(TimeSpan.FromDays(1));
        normalizer.NormalizeAndWrite(envelope);

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Fact]
    public void NormalizeAndWrite_PostDeletionExactReplayDoesNotBackfillContent()
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        var envelope = Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}"));
        normalizer.NormalizeAndWrite(envelope);
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, "DELETE FROM retention_items WHERE store_kind='session_event_content'; DELETE FROM session_event_content;");
        }
        var before = Snapshot(temp.DatabasePath);

        normalizer.NormalizeAndWrite(envelope);

        Assert.Equal(before, Snapshot(temp.DatabasePath));
        using var after = Open(temp.DatabasePath);
        Assert.Equal(0L, Scalar<long>(after, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(0L, Scalar<long>(after, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
    }

    [Fact]
    public void NormalizeAndWrite_SameEnvelopeExactReplayDuplicatesAreWholeDatabaseNoOp()
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        var contentEvent = Event("content-event", "user.message", "{\"value\":\"A\"}");
        normalizer.NormalizeAndWrite(Envelope("copilot-compatible-hook", "copilot-cli", contentEvent));
        var before = Snapshot(temp.DatabasePath);

        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            contentEvent,
            contentEvent));

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Fact]
    public void NormalizeAndWrite_SameEnvelopeConflictingReplayDuplicatesRollBackWholeBatch()
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        var before = Snapshot(temp.DatabasePath);

        Assert.Throws<InvalidOperationException>(() => normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}"),
            Event("content-event", "user.message", "{\"value\":\"B\"}"))));

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void NormalizeAndWrite_DeleteNowDeniedContentReplayIsWholeDatabaseNoOp(string replayValue)
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, $"""
                UPDATE retention_items
                SET state='deletion_queued',read_denied_at='{ObservedAt:O}',queued_at='{ObservedAt:O}',revision=revision+1
                WHERE store_kind='session_event_content' AND source_item_id=(SELECT event_id FROM session_events WHERE source_event_id='content-event');
                """);
            Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
            Assert.Equal(32L, Scalar<long>(connection, "SELECT length(ownership_receipt) FROM retention_items WHERE store_kind='session_event_content';"));
        }
        var before = Snapshot(temp.DatabasePath);

        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", $"{{\"value\":\"{replayValue}\"}}")));

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Fact]
    public void NormalizeAndWrite_DeleteNowDeniedContentNeverSelectsContentJson()
    {
        using var temp = CreateTemp(out var clock);
        var seedNormalizer = new SessionEventNormalizer(CreateStore(temp), clock);
        seedNormalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, $"""
                UPDATE retention_items
                SET state='deletion_queued',read_denied_at='{ObservedAt:O}',queued_at='{ObservedAt:O}',revision=revision+1
                WHERE store_kind='session_event_content' AND source_item_id=(SELECT event_id FROM session_events WHERE source_event_id='content-event');
                """);
        }
        var statements = new List<string>();
        var observedStore = new SqliteSessionStore(
            temp.DatabasePath,
            temp.RetentionContext,
            clock,
            _ => { },
            statements.Add);
        var normalizer = new SessionEventNormalizer(observedStore, clock);

        var exception = Record.Exception(() => normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"B\"}"))));

        Assert.DoesNotContain(
            statements,
            statement => statement.Contains("content_json", StringComparison.OrdinalIgnoreCase));
        Assert.Null(exception);
    }

    [Theory]
    [InlineData("A")]
    [InlineData("B")]
    public void NormalizeAndWrite_UnscannedExpiredContentReplayIsWholeDatabaseNoOp(string replayValue)
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        clock.Advance(TimeSpan.FromDays(91));
        var before = Snapshot(temp.DatabasePath);

        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", $"{{\"value\":\"{replayValue}\"}}")));

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Theory]
    [InlineData("A", false)]
    [InlineData("B", true)]
    public void NormalizeAndWrite_PinnedPastExpiryContentPreservesReplayConflictContract(
        string replayValue,
        bool expectsConflict)
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, """
                UPDATE retention_items
                SET state='retained_by_policy',revision=revision+1
                WHERE store_kind='session_event_content' AND source_item_id=(SELECT event_id FROM session_events WHERE source_event_id='content-event');
                """);
        }
        clock.Advance(TimeSpan.FromDays(91));
        var before = Snapshot(temp.DatabasePath);
        var replay = () => normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", $"{{\"value\":\"{replayValue}\"}}")));

        if (expectsConflict)
            Assert.Throws<InvalidOperationException>(replay);
        else
            replay();

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Theory]
    [InlineData("expiring", true)]
    [InlineData("deletion_queued", false)]
    public void NormalizeAndWrite_ImpossibleRetentionLifecycleAndReadDenialFailsClosed(
        string state,
        bool hasReadDenial)
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        using (var connection = Open(temp.DatabasePath))
        {
            Execute(connection, $"""
                UPDATE retention_items
                SET state='{state}',read_denied_at={(hasReadDenial ? $"'{ObservedAt:O}'" : "NULL")},revision=revision+1
                WHERE store_kind='session_event_content' AND source_item_id=(SELECT event_id FROM session_events WHERE source_event_id='content-event');
                """);
        }
        var before = Snapshot(temp.DatabasePath);

        Assert.Throws<RetentionCatalogUnavailableException>(() => normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}"))));

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    [Fact]
    public void NormalizeAndWrite_MixedNewEventAndReplayContentMismatchRollsBackWholeBatch()
    {
        using var temp = CreateTemp(out var clock);
        var store = CreateStore(temp);
        var normalizer = new SessionEventNormalizer(store, clock);
        normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"A\"}")));
        var before = Snapshot(temp.DatabasePath);

        Assert.Throws<InvalidOperationException>(() => normalizer.NormalizeAndWrite(Envelope(
            "copilot-compatible-hook",
            "copilot-cli",
            Event("content-event", "user.message", "{\"value\":\"B\"}"),
            Event("new-event", "assistant.message", "{\"value\":\"new\"}"))));

        Assert.Equal(before, Snapshot(temp.DatabasePath));
    }

    private static MonitorTempDirectory CreateTemp(out MutableTimeProvider clock)
    {
        clock = new MutableTimeProvider(ObservedAt);
        return new MonitorTempDirectory { TimeProvider = clock };
    }

    private static SqliteSessionStore CreateStore(MonitorTempDirectory temp)
    {
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        return store;
    }

    private static SessionIngestEnvelope Envelope(
        string adapter,
        string surface,
        params SessionIngestEvent[] events) =>
        new(1, adapter, surface, "normalizer-replay-session", events);

    private static SessionIngestEvent Event(string sourceEventId, string type, string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return new(sourceEventId, type, ObservedAt.ToString("O", CultureInfo.InvariantCulture), document.RootElement.Clone());
    }

    private static string Snapshot(string path)
    {
        using var connection = Open(path);
        return string.Join(
            "\n--\n",
            ReadRows(connection, "SELECT * FROM sessions ORDER BY session_id;"),
            ReadRows(connection, "SELECT * FROM session_native_ids ORDER BY session_id,source_surface,native_session_id;"),
            ReadRows(connection, "SELECT * FROM session_runs ORDER BY session_id,run_id;"),
            ReadRows(connection, "SELECT * FROM session_events ORDER BY session_id,event_id;"),
            ReadRows(connection, "SELECT * FROM session_event_content ORDER BY event_id;"),
            ReadRows(connection, "SELECT * FROM retention_items ORDER BY item_id;"),
            ReadRows(connection, "SELECT * FROM retention_leases ORDER BY item_id,lease_kind;"),
            ReadRows(connection, "SELECT * FROM retention_tombstones ORDER BY item_id;"),
            ReadRows(connection, "SELECT * FROM retention_worker_state ORDER BY id;"));
    }

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

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
}

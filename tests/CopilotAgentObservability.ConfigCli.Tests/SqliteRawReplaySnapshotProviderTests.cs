using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SqliteRawReplaySnapshotProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task CaptureAsync_SelectsExactRawRowsAndHoldsOneCompositeOperationLease()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var first = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-a", Now.AddMinutes(-2), null, "{\"resourceSpans\":[]}"));
        var second = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.CollectorOutput, "trace-b", Now.AddMinutes(-1), "{\"service.name\":\"fixture\"}", "{\"resourceSpans\":[]}"));

        var provider = new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now));
        var capture = await provider.CaptureAsync(new RawReplaySelection(
            RawRecordIds: [first, second], Sources: [RawTelemetrySources.CollectorOutput],
            StartInclusive: Now.AddMinutes(-2), EndExclusive: Now), false, CancellationToken.None);

        Assert.True(capture.Success, capture.ErrorCode);
        var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        var record = Assert.Single(lease.Snapshot.Records);
        Assert.Equal(second, record.RawRecordId);
        Assert.Equal("trace-b", record.TraceId);
        Assert.Equal(Now.AddMinutes(-1), record.ReceivedAt);
        Assert.Equal("{\"resourceSpans\":[]}", record.PayloadJson);
        Assert.Equal(1, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));

        await lease.DisposeAsync();
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Fact]
    public async Task CaptureAsync_UsesTraceAndSessionAxesWithoutHeuristicMerging()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var selected = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-exact", Now, null, "{\"resourceSpans\":[]}"));
        var other = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-other", Now, null, "{\"resourceSpans\":[]}"));
        new SqliteSessionStore(temp.DatabasePath, context, new FixedTimeProvider(Now)).CreateSchema();
        var sessionId = Guid.CreateVersion7();
        SeedTraceAndSession(temp.DatabasePath, selected, sessionId, "trace-exact");

        var provider = new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now));
        var capture = await provider.CaptureAsync(new RawReplaySelection(
            SessionIds: [sessionId.ToString("D")], TraceIds: ["trace-missing", "trace-exact"]), false,
            CancellationToken.None);

        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(selected, Assert.Single(lease.Snapshot.Records).RawRecordId);

        var disjoint = await provider.CaptureAsync(new RawReplaySelection(
            RawRecordIds: [other], TraceIds: ["trace-exact"]), false, CancellationToken.None);
        Assert.True(disjoint.Success, disjoint.ErrorCode);
        await using var disjointLease = Assert.IsType<RawReplaySnapshotLease>(disjoint.Lease);
        Assert.Empty(disjointLease.Snapshot.Records);
    }

    [Fact]
    public async Task CaptureAsync_DeniedMemberFailsTheWholeSelectionWithoutAResidualLease()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var first = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-a", Now, null, "{}"));
        var second = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-b", Now, null, "{}"));
        Execute(temp.DatabasePath, "UPDATE retention_items SET read_denied_at=$now WHERE store_kind='raw_record' AND source_item_id=$id;",
            ("$now", (object)Now.ToString("O", CultureInfo.InvariantCulture)), ("$id", second.ToString(CultureInfo.InvariantCulture)));

        var result = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(new RawReplaySelection(RawRecordIds: [first, second]), false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("snapshot_read_denied", result.ErrorCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task CaptureAsync_MissingExplicitRawMemberFailsTheWholeSelection()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var existing = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-a", Now, null, "{}"));

        var result = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(new RawReplaySelection(RawRecordIds: [existing, existing + 1000]), false, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("snapshot_member_missing", result.ErrorCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases;"));
    }

    [Fact]
    public async Task CaptureAsync_IncludesExactSessionContentInTheSameCompositeLease()
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var rawStore = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now)); rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-content", Now, null, "{}"));
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, context, new FixedTimeProvider(Now)); sessionStore.CreateSchema();
        var sessionId = Guid.CreateVersion7(); var runId = Guid.CreateVersion7(); var eventId = Guid.CreateVersion7();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
            Now, Now, Now, SessionRawRetentionState.Expiring, Now, Now);
        var run = new ObservedSessionRun(runId, sessionId, SessionSourceSurface.CopilotCli, "run-native", "trace-content", null,
            "fixture-model", ObservedSessionStatus.Completed, Now, Now, 1, 2, 3);
        var @event = new ObservedSessionEvent(eventId, sessionId, runId, SessionSourceSurface.CopilotCli, null, "trace-content", "ok",
            "copilot-compatible-hook", "source-event", "assistant.completed", Now, SessionContentState.Available,
            "app-v1", "adapter-v1", "schema-v1", "normalization-v1", SessionMatchKind.ExactNative);
        sessionStore.Write(new SessionWriteBatch(new SessionDetail(session, [], [run], [@event]),
            [new SessionEventContent(eventId, "assistant_response", "{\"text\":\"synthetic\"}", Now, Now.AddDays(1))]));
        using (var connection = Open(temp.DatabasePath))
            Execute(connection, null, "INSERT INTO monitor_spans(raw_record_id,span_ordinal,trace_id,span_id,projected_at) VALUES($raw,0,'trace-content','span',$now);",
                ("$raw", rawId), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(new RawReplaySelection(SessionIds: [sessionId.ToString("D")]), true, CancellationToken.None);

        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(rawId, Assert.Single(lease.Snapshot.Records).RawRecordId);
        var content = Assert.Single(lease.Snapshot.SessionContents);
        Assert.Equal(eventId.ToString("D"), content.EventId);
        Assert.Equal(Now, content.OccurredAt);
        Assert.Equal("adapter-v1", content.AdapterVersion);
        Assert.Equal("{\"text\":\"synthetic\"}", content.ContentJson);
        Assert.Equal(2, Scalar<long>(temp.DatabasePath, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(1, Scalar<long>(temp.DatabasePath, "SELECT COUNT(DISTINCT owner) FROM retention_leases WHERE lease_kind='operation';"));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CaptureAsync_AcceptsTheExactRawMemberLimitAndRejectsAnOversizedMember(int excessBytes, bool accepted)
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var rawId = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-large", Now, null, "{}"));
        SetTextLength(temp.DatabasePath, "raw_records", "payload_json", "id", rawId, RawReplayLimits.MaximumRawRecordBytes + excessBytes);

        var selection = new RawReplaySelection(RawRecordIds: [rawId]);
        if (!accepted)
        {
            await AssertPreflightFailure(temp.DatabasePath, context, selection, includeSessionContent: false, "entry_too_large");
            return;
        }

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent: false, CancellationToken.None);
        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(rawId, Assert.Single(lease.Snapshot.Records).RawRecordId);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CaptureAsync_AcceptsTheExactSessionMemberLimitAndRejectsAnOversizedMember(int excessBytes, bool accepted)
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var rawStore = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        rawStore.CreateMonitorSchema();
        var rawId = rawStore.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "trace-content", Now, null, "{}"));
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        sessionStore.CreateSchema();
        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        sessionStore.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
                    Now, Now, Now, SessionRawRetentionState.Expiring, Now, Now),
                [],
                [new ObservedSessionRun(runId, sessionId, SessionSourceSurface.CopilotCli, "run-native", "trace-content", null,
                    "fixture-model", ObservedSessionStatus.Completed, Now, Now, 1, 2, 3)],
                [new ObservedSessionEvent(eventId, sessionId, runId, SessionSourceSurface.CopilotCli, null, "trace-content", "ok",
                    "copilot-compatible-hook", "source-event", "assistant.completed", Now, SessionContentState.Available,
                    "app-v1", "adapter-v1", "schema-v1", "normalization-v1", SessionMatchKind.ExactNative)]),
            [new SessionEventContent(eventId, "assistant_response", "{}", Now, Now.AddDays(1))]));
        Execute(temp.DatabasePath,
            "INSERT INTO monitor_spans(raw_record_id,span_ordinal,trace_id,span_id,projected_at) VALUES($raw,0,'trace-content','span',$now);",
            ("$raw", rawId), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));
        SetTextLength(temp.DatabasePath, "session_event_content", "content_json", "event_id", eventId.ToString("D"), RawReplayLimits.MaximumSessionContentBytes + excessBytes);

        var selection = new RawReplaySelection(SessionIds: [sessionId.ToString("D")]);
        if (!accepted)
        {
            await AssertPreflightFailure(temp.DatabasePath, context, selection, includeSessionContent: true, "entry_too_large");
            return;
        }

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent: true, CancellationToken.None);
        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(eventId.ToString("D"), Assert.Single(lease.Snapshot.SessionContents).EventId);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task CaptureAsync_AcceptsTheExactAggregateLimitAndRejectsAnOversizedAggregate(int excessBytes, bool accepted)
    {
        using var temp = new TempDirectory();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath, new FixedTimeProvider(Now));
        var store = new RawTelemetryStore(temp.DatabasePath, context, new FixedTimeProvider(Now));
        store.CreateMonitorSchema();
        var sizes = new[]
        {
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumRawRecordBytes,
            RawReplayLimits.MaximumArchiveBytes - 4 * RawReplayLimits.MaximumRawRecordBytes + excessBytes,
        };
        var ids = new List<long>();
        for (var index = 0; index < sizes.Length; index++)
        {
            var id = store.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, $"trace-{index}", Now, null, "{}"));
            SetTextLength(temp.DatabasePath, "raw_records", "payload_json", "id", id, sizes[index]);
            ids.Add(id);
        }

        var selection = new RawReplaySelection(RawRecordIds: ids);
        if (!accepted)
        {
            await AssertPreflightFailure(temp.DatabasePath, context, selection, includeSessionContent: false, "archive_too_large");
            return;
        }

        var capture = await new SqliteRawReplaySnapshotProvider(temp.DatabasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent: false, CancellationToken.None);
        Assert.True(capture.Success, capture.ErrorCode);
        await using var lease = Assert.IsType<RawReplaySnapshotLease>(capture.Lease);
        Assert.Equal(ids, lease.Snapshot.Records.Select(static record => record.RawRecordId));
    }

    private static void SeedTraceAndSession(string path, long rawRecordId, Guid sessionId, string traceId)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, "INSERT INTO monitor_spans(raw_record_id,span_ordinal,trace_id,span_id,projected_at) VALUES($raw,0,$trace,'span-1',$now);", ("$raw", rawRecordId), ("$trace", traceId), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));
        Execute(connection, transaction, "INSERT INTO sessions(session_id,status,completeness,started_at,last_seen_at,raw_retention_state,created_at,updated_at) VALUES($session,'completed','full',$now,$now,'not_captured',$now,$now);", ("$session", sessionId.ToString("D")), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)));
        Execute(connection, transaction, "INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status) VALUES($run,$session,'copilot-cli',$trace,'completed');", ("$run", Guid.CreateVersion7().ToString("D")), ("$session", sessionId.ToString("D")), ("$trace", traceId));
        transaction.Commit();
    }

    private static async Task AssertPreflightFailure(
        string databasePath,
        RetentionCatalogContext context,
        RawReplaySelection selection,
        bool includeSessionContent,
        string expectedError)
    {
        var before = CatalogState(databasePath);

        var result = await new SqliteRawReplaySnapshotProvider(databasePath, context, new FixedTimeProvider(Now))
            .CaptureAsync(selection, includeSessionContent, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expectedError, result.ErrorCode);
        Assert.Null(result.Lease);
        Assert.Equal(0, Scalar<long>(databasePath, "SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(before, CatalogState(databasePath));
    }

    private static IReadOnlyList<string> CatalogState(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT item_id,state,revision,COALESCE(read_denied_at,''),COALESCE(queued_at,''),COALESCE(error_code,'')
            FROM retention_items ORDER BY item_id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue)));
        return rows;
    }

    private static void SetTextLength(string path, string table, string column, string keyColumn, object key, int size)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE {table} SET {column}=CAST(zeroblob($size) AS TEXT) WHERE {keyColumn}=$key;";
        command.Parameters.AddWithValue("$size", size);
        command.Parameters.AddWithValue("$key", key);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long Scalar<T>(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = Open(path);
        Execute(connection, null, sql, parameters);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"raw-replay-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string DatabasePath => System.IO.Path.Combine(Path, "raw-store.db");

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

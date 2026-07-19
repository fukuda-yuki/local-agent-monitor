using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class MonitorAnalysisRetentionAdapterTests
{
    [Fact]
    public async Task AnalysisAdapter_RemovesOnlyRunOwnedRawFields()
    {
        using var fixture = await Fixture.CreateAsync();
        var targetBefore = fixture.RunSnapshot(fixture.TargetRunId);
        var otherBefore = fixture.RunSnapshot(fixture.OtherRunId);
        var targetSummaryBefore = fixture.SafeSummary(fixture.TargetRunId);
        var otherSummaryBefore = fixture.SafeSummary(fixture.OtherRunId);

        var result = await new MonitorAnalysisRetentionAdapter(fixture.Catalog).DeleteAsync(fixture.Context);

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal(targetBefore with { ResultMarkdown = null, ErrorMessage = null, Events = null, EventCount = 0 }, fixture.RunSnapshot(fixture.TargetRunId));
        Assert.Equal(otherBefore, fixture.RunSnapshot(fixture.OtherRunId));
        Assert.Equal(targetSummaryBefore, fixture.SafeSummary(fixture.TargetRunId));
        Assert.Equal(otherSummaryBefore, fixture.SafeSummary(fixture.OtherRunId));
        Assert.Equal(1L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item_id;"));
        Assert.Equal("deleted", fixture.Text("SELECT state FROM retention_items WHERE item_id=$item_id;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item_id;"));
    }

    [Fact]
    public async Task AnalysisAdapter_ContextIdentityMismatchReturnsLeaseLostWithoutMutating()
    {
        using var fixture = await Fixture.CreateAsync();
        var before = fixture.RunSnapshot(fixture.TargetRunId);
        var adapter = new MonitorAnalysisRetentionAdapter(fixture.Catalog);

        var wrongKind = await adapter.DeleteAsync(fixture.Context with { StoreKind = RetentionStoreKind.RawRecord });
        var wrongSource = await adapter.DeleteAsync(fixture.Context with
        {
            SourceIdentity = fixture.Context.SourceIdentity with { SourceItemId = "999999" },
        });

        Assert.Same(RetentionAdapterResult.LeaseLost, wrongKind);
        Assert.Same(RetentionAdapterResult.LeaseLost, wrongSource);
        Assert.Equal(before, fixture.RunSnapshot(fixture.TargetRunId));
        Assert.Equal(0L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item_id;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item_id;"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, RetentionCatalogStore catalog, RetentionDeleteContext context, long targetRunId, long otherRunId)
            => (Path, Catalog, Context, TargetRunId, OtherRunId) = (path, catalog, context, targetRunId, otherRunId);

        internal string Path { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal RetentionDeleteContext Context { get; }
        internal long TargetRunId { get; }
        internal long OtherRunId { get; }

        internal static async Task<Fixture> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"analysis-retention-adapter-{Guid.NewGuid():N}.db");
            var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var catalogContext = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var analysis = new SqliteMonitorAnalysisStore(path, catalogContext, time);
            analysis.CreateSchema();
            var target = analysis.StartRun("trace-target", 101, "span-target", MonitorAnalysisFocus.Errors, now);
            var targetFence = analysis.AppendEvent(target.RunId, target.OperationToken, null, "progress", "target raw event", now.AddMinutes(1));
            _ = analysis.CompleteRun(target.RunId, target.OperationToken, targetFence, "target raw result", now.AddMinutes(2));
            var other = analysis.StartRun("trace-other", 202, "span-other", MonitorAnalysisFocus.Latency, now.AddMinutes(3));
            var otherFence = analysis.AppendEvent(other.RunId, other.OperationToken, null, "progress", "other raw event", now.AddMinutes(4));
            _ = analysis.CompleteRun(other.RunId, other.OperationToken, otherFence, "other raw result", now.AddMinutes(5));
            _ = analysis.GenerateRepositorySafeSummary(target.RunId, now.AddMinutes(6));
            _ = analysis.GenerateRepositorySafeSummary(other.RunId, now.AddMinutes(7));

            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            var catalog = new RetentionCatalogStore(catalogContext, time);
            var itemId = Text(path, "SELECT item_id FROM retention_items WHERE store_kind='analysis_run_raw' AND source_item_id=$source;", ("$source", target.RunId.ToString()));
            Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$item_id;", ("$now", now.ToString("O")), ("$item_id", itemId));
            var claim = (await catalog.TryClaimDeletionAsync(new(itemId, 1, RetentionWorkKind.Queued), "analysis-adapter", now, CancellationToken.None)).Claim!;
            var intent = await catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);
            Assert.Equal(RetentionIntentDisposition.Committed, intent.Disposition);
            return new Fixture(path, catalog, new RetentionDeleteContext(claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration, claim.SourceIdentity, null, intent.IntentCursor, CancellationToken.None), target.RunId, other.RunId);
        }

        internal RunSnapshot RunSnapshot(long runId)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT trace_id,raw_record_id,span_id,focus,status,requested_at,started_at,completed_at,result_markdown,error_message,hex(retention_owner_token),(SELECT COUNT(*) FROM monitor_analysis_events WHERE run_id=$run_id),(SELECT group_concat(event_type || '|' || message || '|' || occurred_at, '||') FROM monitor_analysis_events WHERE run_id=$run_id ORDER BY id) FROM monitor_analysis_runs WHERE id=$run_id;";
            command.Parameters.AddWithValue("$run_id", runId);
            using var row = command.ExecuteReader();
            Assert.True(row.Read());
            return new RunSnapshot(row.GetString(0), row.GetInt64(1), row.GetString(2), row.GetString(3), row.GetString(4), row.GetString(5), row.IsDBNull(6) ? null : row.GetString(6), row.IsDBNull(7) ? null : row.GetString(7), row.IsDBNull(8) ? null : row.GetString(8), row.IsDBNull(9) ? null : row.GetString(9), row.GetString(10), row.GetInt32(11), row.IsDBNull(12) ? null : row.GetString(12));
        }

        internal string SafeSummary(long runId) => Text(Path, "SELECT markdown || '|' || generated_at FROM monitor_analysis_safe_summaries WHERE run_id=$run_id;", ("$run_id", runId));
        internal long Scalar(string sql) => Convert.ToInt64(Value(sql), System.Globalization.CultureInfo.InvariantCulture);
        internal string Text(string sql) => (string)Value(sql)!;
        private object? Value(string sql) { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.Parameters.AddWithValue("$item_id", Context.ItemId); return command.ExecuteScalar(); }
        private SqliteConnection Open() { var connection = new SqliteConnection($"Data Source={Path};Pooling=False"); connection.Open(); return connection; }
        public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
        private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value); command.ExecuteNonQuery(); }
        private static string Text(string path, string sql, params (string Name, object Value)[] values) { using var connection = new SqliteConnection($"Data Source={path};Pooling=False"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value); return (string)command.ExecuteScalar()!; }
    }

    private sealed record RunSnapshot(string TraceId, long RawRecordId, string SpanId, string Focus, string Status, string RequestedAt, string? StartedAt, string? CompletedAt, string? ResultMarkdown, string? ErrorMessage, string OwnerToken, int EventCount, string? Events);
}

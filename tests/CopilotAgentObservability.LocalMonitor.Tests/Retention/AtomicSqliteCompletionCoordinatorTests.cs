using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class AtomicSqliteCompletionCoordinatorTests
{
    [Fact]
    public async Task AtomicSqliteFinalization_IsCountedAfterLeaseDisappears()
    {
        using var fixture = await Fixture.CreateAsync();
        var raw = new AtomicRawAdapter(fixture.Catalog);
        var registry = new RetentionAdapterRegistry([
            new FixedAdapter(RetentionStoreKind.SessionEventContent), raw,
            new FixedAdapter(RetentionStoreKind.AnalysisRunRaw), new FixedAdapter(RetentionStoreKind.SensitiveBundle), new FixedAdapter(RetentionStoreKind.AnalysisSdkDirectory)]);

        var cycle = new RetentionCleanupCoordinator(fixture.Catalog, registry, fixture.Time).RunOneCycleAsync(CancellationToken.None, CancellationToken.None).AsTask();
        await raw.Finalized.Task;
        fixture.Time.Advance(RetentionV1Constants.LeaseRenewalDeadline);
        await raw.LeaseLossObserved.Task;
        raw.Release.TrySetResult();
        var result = await cycle;

        Assert.Equal(1, result.Completed);
        Assert.True(result.Clean);
        Assert.True(result.QualifiedSqliteBatch);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones;"));
        Assert.Equal(1, raw.Mutations);
    }

    private sealed class AtomicRawAdapter(RetentionCatalogStore catalog) : IRetentionDeletionAdapter
    {
        internal readonly TaskCompletionSource Finalized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource LeaseLossObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Mutations;
        public RetentionStoreKind StoreKind => RetentionStoreKind.RawRecord;
        public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
        {
            var result = await catalog.ExecuteSqliteDeletionAsync(context, (connection, transaction, grant) =>
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$token;";
                delete.Parameters.AddWithValue("$id", long.Parse(context.SourceIdentity.SourceItemId));
                grant.BindSourceToken(delete, "$token");
                Interlocked.Increment(ref Mutations);
                return ValueTask.FromResult(delete.ExecuteNonQuery());
            });
            Finalized.TrySetResult();
            using var registration = context.CancellationToken.Register(() => LeaseLossObserved.TrySetResult());
            await Release.Task;
            return result;
        }
    }

    private sealed class FixedAdapter(RetentionStoreKind kind) : IRetentionDeletionAdapter
    {
        public RetentionStoreKind StoreKind => kind;
        public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) => ValueTask.FromResult(RetentionAdapterResult.Deleted);
    }

    private sealed record Fixture(string Path, MutableTimeProvider Time, RetentionCatalogStore Catalog) : IDisposable
    {
        internal static async Task<Fixture> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-atomic-coordinator-{Guid.NewGuid():N}.db");
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero));
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var raw = new RawTelemetryStore(path, context, time);
            raw.CreateMonitorSchema();
            var rawId = raw.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, time.GetUtcNow(), null, "{}"));
            var catalog = new RetentionCatalogStore(context, time);
            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE store_kind='raw_record' AND source_item_id=$source;", ("$now", time.GetUtcNow().ToString("O")), ("$source", rawId.ToString()));
            return new Fixture(path, time, catalog);
        }

        internal long Scalar(string sql) { using var c = Open(Path); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt64(q.ExecuteScalar()); }
        public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
        private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
        private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); return c; }
    }
}

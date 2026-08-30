using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.TestSupport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCleanupCoordinatorConcurrencyTests
{
    private static readonly TimeSpan CoordinationTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task ParallelAdapters_SerializeCatalogCompletion()
    {
        using var fixture = Fixture.Create();
        var adapter = new ConvergingAdapter();
        var firstCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completionEntries = 0;
        var coordinator = new RetentionCleanupCoordinator(
            fixture.Catalog,
            Registry(adapter),
            fixture.Time,
            async mutation =>
            {
                if (mutation != RetentionCatalogMutation.CompleteDeletion)
                    return;

                if (Interlocked.Increment(ref completionEntries) == 1)
                {
                    firstCompletion.TrySetResult();
                    await releaseCompletion.Task;
                }
            });

        var cycle = coordinator.RunOneCycleAsync(CancellationToken.None, CancellationToken.None).AsTask();
        RetentionCycleResult? result = null;
        try
        {
            await adapter.BothEntered.Task.WaitAsync(CoordinationTimeout);
            adapter.Release();
            await firstCompletion.Task.WaitAsync(CoordinationTimeout);

            Assert.Equal(1, Volatile.Read(ref completionEntries));
            releaseCompletion.TrySetResult();
            result = await cycle.WaitAsync(CoordinationTimeout);
        }
        finally
        {
            adapter.Release();
            releaseCompletion.TrySetResult();
            await cycle.WaitAsync(CoordinationTimeout);
        }

        Assert.Equal(2, result.Completed);
        Assert.Equal(2, Volatile.Read(ref completionEntries));
        Assert.Equal(2, adapter.Calls);
    }

    private static RetentionAdapterRegistry Registry(ConvergingAdapter raw) => new([
        new FixedAdapter(RetentionStoreKind.SessionEventContent),
        raw,
        new FixedAdapter(RetentionStoreKind.AnalysisRunRaw),
        new FixedAdapter(RetentionStoreKind.SensitiveBundle),
        new FixedAdapter(RetentionStoreKind.AnalysisSdkDirectory)]);

    private sealed class ConvergingAdapter : IRetentionDeletionAdapter
    {
        internal readonly TaskCompletionSource BothEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Calls;
        public RetentionStoreKind StoreKind => RetentionStoreKind.RawRecord;

        internal void Release() => release.TrySetResult();

        public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
        {
            if (Interlocked.Increment(ref Calls) == 2)
                BothEntered.TrySetResult();
            await release.Task.WaitAsync(context.CancellationToken);
            return RetentionAdapterResult.Deleted;
        }
    }

    private sealed class FixedAdapter(RetentionStoreKind kind) : IRetentionDeletionAdapter
    {
        public RetentionStoreKind StoreKind => kind;
        public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) =>
            ValueTask.FromResult(RetentionAdapterResult.Deleted);
    }

    private sealed record Fixture(string Path, MutableTimeProvider Time, RetentionCatalogStore Catalog) : IDisposable
    {
        internal static Fixture Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-coordinator-concurrency-{Guid.NewGuid():N}.db");
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
            var catalog = new RetentionCatalogStore(path, time);
            catalog.CreateSchema();
            SeedCoverage(path);
            AddQueuedItem(path, "first", time.GetUtcNow());
            AddQueuedItem(path, "second", time.GetUtcNow());
            return new Fixture(path, time, catalog);
        }

        public void Dispose()
        {
            TestFileSystemCleanup.DeleteFile(Path);
            TestFileSystemCleanup.DeleteFile(Path + "-wal");
            TestFileSystemCleanup.DeleteFile(Path + "-shm");
        }

        private static void SeedCoverage(string path) => Execute(
            path,
            "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");

        private static void AddQueuedItem(string path, string itemId, DateTimeOffset now)
        {
            var source = new RawTelemetryStore(path);
            source.CreateMonitorSchema();
            var rawId = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, now, null, "{}"));
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE retention_items SET item_id=$item,state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now,expires_at=$now WHERE store_kind='raw_record' AND source_item_id=$source;";
            command.Parameters.AddWithValue("$item", itemId);
            command.Parameters.AddWithValue("$now", now.ToString("O"));
            command.Parameters.AddWithValue("$source", rawId.ToString());
            command.ExecuteNonQuery();
        }

        private static void Execute(string path, string sql)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            connection.Open();
            return connection;
        }
    }
}

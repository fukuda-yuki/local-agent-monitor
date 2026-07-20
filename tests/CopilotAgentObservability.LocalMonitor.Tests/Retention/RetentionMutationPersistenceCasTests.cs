using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationPersistenceCasTests
{
    [Fact]
    public void MaterializeMutationVersionVector_OrdersExactItemsAndRepeatsByteIdentically()
    {
        using var fixture = Fixture.Create();
        var first = fixture.Store.MaterializeMutationVersionVector(["item-b", "item-a"]);
        var second = new RetentionCatalogStore(fixture.Path).MaterializeMutationVersionVector(["item-a", "item-b"]);

        Assert.Equal(["item-a", "item-b"], first.ExpectedItems.Select(static item => item.ItemId));
        Assert.Equal(first.ExpectedItems, second.ExpectedItems);
        Assert.Equal(first.TargetItems, second.TargetItems);
        Assert.Equal(first.ExpectedStateVersion, second.ExpectedStateVersion);
        Assert.Equal(first.TargetItemSetDigest, second.TargetItemSetDigest);
        Assert.Equal(
            RetentionMutationDigests.ExpectedStateVersion(first.ExpectedItems),
            first.ExpectedStateVersion);
        Assert.Equal(
            RetentionMutationDigests.TargetItemSetDigest(first.TargetItems),
            first.TargetItemSetDigest);
    }

    [Fact]
    public async Task TryCompareAndSwapMutationAsync_CallbackExceptionRollsBackIntermediateWriteAfterReopen()
    {
        using var fixture = Fixture.Create();
        var expected = fixture.Store.MaterializeMutationVersionVector(["item-a"]);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await fixture.Store.TryCompareAndSwapMutationAsync(
                expected,
                (connection, transaction, _) =>
                {
                    using var update = connection.CreateCommand();
                    update.Transaction = transaction;
                    update.CommandText = "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE item_id='item-a' AND revision=1;";
                    Assert.Equal(1, update.ExecuteNonQuery());
                    throw new InvalidOperationException("injected callback failure");
                },
                (_, _, _) => throw new InvalidOperationException("result writer must not run"),
                CancellationToken.None));

        SqliteConnection.ClearAllPools();
        var reopened = new RetentionCatalogStore(fixture.Path);
        Assert.Equal(RetentionItemLifecycle.Expiring, reopened.Find(new RetentionOwnershipKey(
            reopened.StoreInstanceId,
            RetentionStoreKind.RawRecord,
            "source-item-a"))!.State);
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT revision FROM retention_items WHERE item_id='item-a';"));
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM retention_operation_receipts;"));
    }

    [Fact]
    public async Task TryCompareAndSwapMutationAsync_StaleRevisionSkipsCallbackAndResultWrite()
    {
        using var fixture = Fixture.Create();
        var expected = fixture.Store.MaterializeMutationVersionVector(["item-a", "item-b"]);
        Execute(fixture.Path, "UPDATE retention_items SET revision=revision+1 WHERE item_id='item-b';");
        var callbackCalls = 0;
        var resultWriterCalls = 0;

        var result = await fixture.Store.TryCompareAndSwapMutationAsync(
            expected,
            (_, _, _) =>
            {
                Interlocked.Increment(ref callbackCalls);
                return ValueTask.CompletedTask;
            },
            (_, _, _) =>
            {
                Interlocked.Increment(ref resultWriterCalls);
                return ValueTask.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(RetentionMutationCasDisposition.Stale, result.Disposition);
        Assert.Null(result.ResultVersion);
        Assert.Equal(0, callbackCalls);
        Assert.Equal(0, resultWriterCalls);
        Assert.Equal(0L, Scalar(fixture.Path, "SELECT COUNT(*) FROM retention_operation_receipts;"));
    }

    [Fact]
    public async Task TryCompareAndSwapMutationAsync_TwoConcurrentWritersHaveOneWinnerAndOneAtomicStaleResult()
    {
        using var fixture = Fixture.Create();
        var expected = fixture.Store.MaterializeMutationVersionVector(["item-a"]);
        var writerA = new RetentionCatalogStore(fixture.Path);
        var writerB = new RetentionCatalogStore(fixture.Path);
        using var barrier = new Barrier(2);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackCalls = 0;

        async ValueTask Apply(SqliteConnection connection, SqliteTransaction transaction, RetentionMutationVersionVector _)
        {
            if (Interlocked.Increment(ref callbackCalls) == 1)
            {
                callbackEntered.SetResult();
                await releaseWinner.Task;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE item_id='item-a' AND revision=1;";
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        ValueTask PersistResult(SqliteConnection connection, SqliteTransaction transaction, string resultVersion)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO retention_operation_receipts(operation_id,schema_version,result_code,target_kind,target_id,operation,scope,target_item_count,result_json,completion_code,expected_version,result_version,target_item_set_digest,created_at,completed_at) VALUES('op-winner',1,'retention_pin_applied','item','item-a','pin','single_item',1,'{}','retention_pin_applied',$expected,$result,$target,'2026-07-20T00:00:00.0000000+00:00','2026-07-20T00:00:00.0000000+00:00');";
            insert.Parameters.AddWithValue("$expected", expected.ExpectedStateVersion);
            insert.Parameters.AddWithValue("$result", resultVersion);
            insert.Parameters.AddWithValue("$target", expected.TargetItemSetDigest);
            insert.ExecuteNonQuery();
            return ValueTask.CompletedTask;
        }

        async Task<RetentionMutationCasResult> Run(RetentionCatalogStore writer)
        {
            barrier.SignalAndWait();
            return await writer.TryCompareAndSwapMutationAsync(expected, Apply, PersistResult, CancellationToken.None);
        }

        var taskA = Task.Run(() => Run(writerA));
        var taskB = Task.Run(() => Run(writerB));
        await callbackEntered.Task;
        releaseWinner.SetResult();
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Equal(1, results.Count(result => result.Disposition == RetentionMutationCasDisposition.Committed));
        Assert.Equal(1, results.Count(result => result.Disposition == RetentionMutationCasDisposition.Stale));
        Assert.Equal(1, callbackCalls);
        var committed = Assert.Single(results, result => result.Disposition == RetentionMutationCasDisposition.Committed);
        Assert.Equal(committed.ResultVersion, ScalarText(fixture.Path, "SELECT result_version FROM retention_operation_receipts WHERE operation_id='op-winner';"));
        Assert.Equal(committed.ResultVersion, RetentionMutationDigests.ExpectedStateVersion([
            new("item-a", 2, RetentionPinState.Pinned, RetentionItemLifecycle.RetainedByPolicy)
        ]));
        Assert.Equal(2L, Scalar(fixture.Path, "SELECT revision FROM retention_items WHERE item_id='item-a';"));
        Assert.Equal("retained_by_policy", ScalarText(fixture.Path, "SELECT state FROM retention_items WHERE item_id='item-a';"));
        Assert.Equal(1L, Scalar(fixture.Path, "SELECT COUNT(*) FROM retention_operation_receipts;"));
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, RetentionCatalogStore store)
        {
            Path = path;
            Store = store;
        }

        internal string Path { get; }
        internal RetentionCatalogStore Store { get; }

        internal static Fixture Create()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-mutation-cas-{Guid.NewGuid():N}.sqlite");
            var store = new RetentionCatalogStore(path);
            store.CreateSchema();
            SeedItem(path, "item-a", "expiring", 1);
            SeedItem(path, "item-b", "retained_by_policy", 1);
            return new(path, store);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var candidate in new[] { Path, Path + "-wal", Path + "-shm" })
                if (File.Exists(candidate)) File.Delete(candidate);
        }
    }

    private static void SeedItem(string path, string itemId, string state, long revision)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO retention_items(
                item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,private_locator,
                captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,lease_owner,
                lease_expires_at,lease_generation,attempt_count,next_retry_at,error_code,retry_exhausted,deleted_at,
                adapter_coverage_version,deletion_started_at)
            VALUES($id,$store,'raw_record',$source,1,zeroblob(32),NULL,
                '2026-07-20T00:00:00.0000000+00:00','2026-10-18T00:00:00.0000000+00:00','raw-default-90d',1,$state,$revision,
                NULL,NULL,NULL,NULL,0,0,NULL,NULL,0,NULL,1,NULL);
            """;
        command.Parameters.AddWithValue("$id", itemId);
        command.Parameters.AddWithValue("$store", ScalarText(path, "SELECT store_instance_id FROM retention_store_instances WHERE id=1;"));
        command.Parameters.AddWithValue("$source", $"source-{itemId}");
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$revision", revision);
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ScalarText(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}

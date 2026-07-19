using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class SqliteRetentionDeletionBridgeTests
{
    [Fact]
    public async Task SqliteRetentionAdapter_DeletesSourceAndFinalizesReceiptInOneTransaction()
    {
        using var fixture = await Fixture.CreateAsync();

        var result = await fixture.Catalog.ExecuteSqliteDeletionAsync(
            fixture.Context,
            (connection, transaction, grant) =>
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$token;";
                delete.Parameters.AddWithValue("$id", long.Parse(fixture.Context.SourceIdentity.SourceItemId));
                grant.BindSourceToken(delete, "$token");
                Assert.Equal(1, delete.ExecuteNonQuery());
                return ValueTask.FromResult(1);
            });

        Assert.Same(RetentionAdapterResult.Deleted, result);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id;"));
        Assert.Equal("deleted", fixture.Text("SELECT state FROM retention_items WHERE item_id=$id;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$id;"));
    }

    [Fact]
    public async Task SqliteRetentionAdapter_CancellationAfterSourceMutationRollsBackThenFreshReclaimCompletesOnce()
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancelled = new CancellationTokenSource();

        var operation = fixture.Catalog.ExecuteSqliteDeletionAsync(
            fixture.Context with { CancellationToken = cancelled.Token },
            (connection, transaction, grant) =>
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$token;";
                delete.Parameters.AddWithValue("$id", long.Parse(fixture.Context.SourceIdentity.SourceItemId));
                grant.BindSourceToken(delete, "$token");
                Assert.Equal(1, delete.ExecuteNonQuery());
                cancelled.Cancel();
                return ValueTask.FromResult(1);
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.AsTask());
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id;"));
        Assert.Equal("deleting", fixture.Text("SELECT state FROM retention_items WHERE item_id=$id;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$id;"));

        SqliteConnection.ClearAllPools();
        fixture.Time.Advance(RetentionV1Constants.LeaseDuration);
        var reopened = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(fixture.Path), fixture.Time);
        var claim = (await reopened.TryClaimDeletionAsync(new(fixture.Context.ItemId, fixture.Context.ExpectedRevision, RetentionWorkKind.IntentRecovery), "reopened", fixture.Time.GetUtcNow(), CancellationToken.None)).Claim!;
        var intent = await reopened.EnsureDeleteIntentAsync(claim.Fence, 0, fixture.Time.GetUtcNow(), CancellationToken.None);
        Assert.Equal(RetentionIntentDisposition.AlreadyCommitted, intent.Disposition);
        var recoveryContext = new RetentionDeleteContext(claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration, claim.SourceIdentity, claim.PrivateLocator, intent.IntentCursor, CancellationToken.None);
        Assert.Same(RetentionAdapterResult.Deleted, await reopened.ExecuteSqliteDeletionAsync(recoveryContext, fixture.DeleteMutation(recoveryContext)));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$id;"));
        Assert.Equal(1L, fixture.Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id;"));
        Assert.Equal("deleted", fixture.Text("SELECT state FROM retention_items WHERE item_id=$id;"));
        Assert.Equal(1L, fixture.Scalar("SELECT attempt_count FROM retention_items WHERE item_id=$id;"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task AdapterFailureMatrix_RollsBackSourceAndReceipt(int phaseValue)
    {
        using var fixture = await Fixture.CreateAsync();
        var phase = (RetentionSqliteDeletePhase)phaseValue;
        var result = await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context, fixture.DeleteMutation(), checkpoint =>
        {
            if (checkpoint == phase) throw new InvalidOperationException();
        });

        Assert.Equal(RetentionAdapterDisposition.TransientFailure, result.Disposition);
        fixture.AssertPreFinalizationFresh();
    }

    [Fact]
    public async Task SqliteRetentionAdapter_StaleEnvelopeOrCursorNeverInvokesSourceMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        var stale = fixture.Context with { ExpectedRevision = fixture.Context.ExpectedRevision + 1 };
        var staleResult = await fixture.Catalog.ExecuteSqliteDeletionAsync(stale, (_, _, _) => { calls++; return ValueTask.FromResult(1); });
        var cursorResult = await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context with { IntentCursor = 1 }, (_, _, _) => { calls++; return ValueTask.FromResult(1); });

        Assert.Equal(RetentionAdapterDisposition.LeaseLost, staleResult.Disposition);
        Assert.Equal(RetentionAdapterDisposition.LeaseLost, cursorResult.Disposition);
        Assert.Equal(0, calls);
        fixture.AssertPreFinalization();
    }

    [Fact]
    public async Task SqliteRetentionAdapter_RejectsEveryStaleLeaseAndEnvelopeFieldBeforeCallback()
    {
        using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        var contexts = new[]
        {
            fixture.Context with { LeaseOwner = "other" }, fixture.Context with { LeaseGeneration = 99 },
            fixture.Context with { StoreInstanceId = "00000000000000000000000000000000" },
            fixture.Context with { StoreKind = RetentionStoreKind.AnalysisRunRaw },
            fixture.Context with { SourceIdentity = fixture.Context.SourceIdentity with { SourceItemId = "2" } },
            fixture.Context with { SourceIdentity = fixture.Context.SourceIdentity with { SourceItemId = "not-a-number" } },
            fixture.Context with { SourceIdentity = fixture.Context.SourceIdentity with { OwnershipReceipt = Convert.ToBase64String(new byte[32]) } }
        };
        foreach (var context in contexts)
            Assert.Equal(RetentionAdapterDisposition.LeaseLost, (await fixture.Catalog.ExecuteSqliteDeletionAsync(context, (_, _, _) => { calls++; return ValueTask.FromResult(0); })).Disposition);
        fixture.Execute("DELETE FROM retention_delete_journal WHERE item_id=$id;");
        Assert.Equal(RetentionAdapterDisposition.LeaseLost, (await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context, (_, _, _) => { calls++; return ValueTask.FromResult(0); })).Disposition);
        Assert.Equal(0, calls);
        fixture.AssertPreFinalization();
    }

    [Fact]
    public async Task SqliteRetentionAdapter_MalformedCanonicalIdentityIsTerminalBeforeCallback()
    {
        using var fixture = await Fixture.CreateAsync();
        var calls = 0;
        fixture.Execute("UPDATE retention_items SET source_item_id='not-a-number' WHERE item_id=$id;");
        var result = await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context with { SourceIdentity = fixture.Context.SourceIdentity with { SourceItemId = "not-a-number" } }, (_, _, _) => { calls++; return ValueTask.FromResult(0); });
        Assert.Equal(RetentionErrorCode.InvalidIdentity, result.ErrorCode);
        Assert.Equal(0, calls);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$id;"));
    }

    [Fact]
    public async Task SqliteRetentionAdapter_HeldImmediateTransactionMapsToDeleteBusyWithoutMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        using var blocker = new SqliteConnection($"Data Source={fixture.Path};Pooling=False");
        blocker.Open();
        using var begin = blocker.CreateCommand();
        begin.CommandText = "BEGIN IMMEDIATE;";
        begin.ExecuteNonQuery();
        var calls = 0;
        var operation = Task.Run(async () => await fixture.Catalog.ExecuteSqliteDeletionAsync(
            fixture.Context,
            (_, _, _) => { Interlocked.Increment(ref calls); return ValueTask.FromResult(0); }));
        RetentionAdapterResult? result = null;
        var timedOut = false;
        try
        {
            try { result = await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { timedOut = true; }
        }
        finally
        {
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }
        if (timedOut)
        {
            await operation;
            throw new Xunit.Sdk.XunitException("SQLite deletion did not finish within the bounded busy timeout.");
        }

        Assert.NotNull(result);
        Assert.Equal(RetentionErrorCode.DeleteBusy, result.ErrorCode);
        Assert.Equal(0, calls);
        fixture.AssertPreFinalization();
        fixture.AssertPreFinalizationFresh();
    }

    [Fact]
    public async Task SqliteRetentionAdapter_MissingSourceIsTerminalWithoutTombstone()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Execute("DELETE FROM raw_records;");
        var result = await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context, (_, _, _) => throw new Xunit.Sdk.XunitException("callback"));

        Assert.Equal(RetentionErrorCode.UnexpectedSourceMissing, result.ErrorCode);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$id;"));
    }

    [Fact]
    public async Task SqliteRetentionAdapter_NegativeMutationInvariantRollsBackAsTransientIoFailure()
    {
        using var fixture = await Fixture.CreateAsync();
        var result = await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context, (connection, transaction, grant) =>
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$token;";
            delete.Parameters.AddWithValue("$id", long.Parse(fixture.Context.SourceIdentity.SourceItemId));
            grant.BindSourceToken(delete, "$token");
            Assert.Equal(1, delete.ExecuteNonQuery());
            return ValueTask.FromResult(-1);
        });

        Assert.Equal(RetentionAdapterDisposition.TransientFailure, result.Disposition);
        Assert.Equal(RetentionErrorCode.DeleteIoFailed, result.ErrorCode);
        fixture.AssertPreFinalizationFresh();
    }

    [Fact]
    public async Task SqliteRetentionAdapter_ReplacedOwnershipIsTerminalAndPreservesReplacement()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Execute("DROP TRIGGER retention_raw_records_token_immutable; UPDATE raw_records SET retention_owner_token=zeroblob(32);");
        var result = await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context, (_, _, _) => throw new Xunit.Sdk.XunitException("callback"));

        Assert.Equal(RetentionErrorCode.OwnershipMismatch, result.ErrorCode);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM raw_records;"));
    }

    [Fact]
    public async Task SqliteRetentionAdapter_AlreadyFinalizedIsIdempotentWithoutSourceMutation()
    {
        using var fixture = await Fixture.CreateAsync();
        fixture.Execute("UPDATE retention_items SET state='deleted' WHERE item_id=$id; INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES($id,$now,$now);", ("$now", DateTimeOffset.UtcNow.ToString("O")));
        var result = await fixture.Catalog.ExecuteSqliteDeletionAsync(fixture.Context, (_, _, _) => throw new Xunit.Sdk.XunitException("callback"));

        Assert.Same(RetentionAdapterResult.Deleted, result);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string path, MutableTimeProvider time, RetentionCatalogStore catalog, RetentionDeleteContext context, byte[] token) => (Path, Time, Catalog, Context, Token) = (path, time, catalog, context, token);
        internal string Path { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Catalog { get; }
        internal RetentionDeleteContext Context { get; }
        internal byte[] Token { get; }
        internal RetentionSqliteSourceMutation DeleteMutation(RetentionDeleteContext? deleteContext = null) => (connection, transaction, grant) =>
        {
            var target = deleteContext ?? Context;
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$token;";
            delete.Parameters.AddWithValue("$id", long.Parse(target.SourceIdentity.SourceItemId));
            grant.BindSourceToken(delete, "$token");
            return ValueTask.FromResult(delete.ExecuteNonQuery());
        };
        internal void AssertPreFinalization()
        {
            Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM raw_records;"));
            Assert.Equal(0L, Scalar("SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id;"));
            Assert.Equal("deleting", Text("SELECT state FROM retention_items WHERE item_id=$id;"));
            Assert.Equal(Context.ExpectedRevision, Scalar("SELECT revision FROM retention_items WHERE item_id=$id;"));
            Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$id;"));
            Assert.Equal(1L, Scalar("SELECT attempt_count FROM retention_items WHERE item_id=$id;"));
        }
        internal void AssertPreFinalizationFresh()
        {
            SqliteConnection.ClearAllPools();
            var reopened = new RetentionCatalogStore(Path);
            Assert.NotNull(reopened.Find(new RetentionOwnershipKey(Context.StoreInstanceId, Context.StoreKind, Context.SourceIdentity.SourceItemId)));
            AssertPreFinalization();
        }

        internal static async Task<Fixture> CreateAsync()
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-delete-bridge-{Guid.NewGuid():N}.db");
            var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var catalogContext = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var source = new RawTelemetryStore(path, catalogContext, time);
            source.CreateMonitorSchema();
            var id = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, now, null, "{}"));
            var catalog = new RetentionCatalogStore(catalogContext, time);
            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            var item = Text(path, "SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$source;", id.ToString());
            Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$item;", ("$now", now.ToString("O")), ("$item", item));
            var claim = (await catalog.TryClaimDeletionAsync(new(item, 1, RetentionWorkKind.Queued), "bridge", now, CancellationToken.None)).Claim!;
            var intent = await catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);
            Assert.Equal(RetentionIntentDisposition.Committed, intent.Disposition);
            var token = Blob(path, "SELECT retention_owner_token FROM raw_records WHERE id=$id;", id);
            return new Fixture(path, time, catalog, new RetentionDeleteContext(claim.Fence.ItemId, claim.StoreInstanceId, claim.StoreKind, claim.Fence.ExpectedRevision, claim.Fence.LeaseOwner, claim.Fence.LeaseGeneration, claim.SourceIdentity, null, intent.IntentCursor, CancellationToken.None), token);
        }

        internal long Scalar(string sql) => Convert.ToInt64(ExecuteScalar(sql));
        internal string Text(string sql) => (string)ExecuteScalar(sql)!;
        internal void Execute(string sql, params (string Name, object Value)[] values) { using var c = Open(Path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$id", Context.ItemId); foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
        private object? ExecuteScalar(string sql) { using var c = Open(Path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$id", Context.ItemId); return q.ExecuteScalar(); }
        public void Dispose() { SqliteConnection.ClearAllPools(); foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
        private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
        private static string Text(string path, string sql, string source) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$source", source); return (string)q.ExecuteScalar()!; }
        private static byte[] Blob(string path, string sql, long id) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$id", id); return (byte[])q.ExecuteScalar()!; }
        private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); return c; }
    }
}

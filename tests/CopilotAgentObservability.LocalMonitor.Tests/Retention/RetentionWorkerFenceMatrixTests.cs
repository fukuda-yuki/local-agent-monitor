using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionWorkerFenceMatrixTests
{
    [Fact]
    public async Task OperationLease_QuiescesThenReclaimsOnceWhenItExpiresBeforeBound()
    {
        var fixture = await CreateQueuedRawAsync();
        try
        {
            InsertLease(fixture.Path, fixture.ItemId, "operation", "reader", fixture.Now + TimeSpan.FromMinutes(1), 1);

            var first = await fixture.Catalog.TryClaimDeletionAsync(new(fixture.ItemId, 1, RetentionWorkKind.Queued), "worker", fixture.Now, CancellationToken.None);
            Assert.Equal(RetentionClaimDisposition.Quiescing, first.Disposition);
            Assert.Equal(fixture.Now + TimeSpan.FromMinutes(1), first.QuiescenceRetryAt);
            Assert.Equal("deletion_queued", Text(fixture.Path, "SELECT state FROM retention_items WHERE item_id=$item", fixture.ItemId));

            fixture.Time.Advance(TimeSpan.FromMinutes(1));
            var claimed = await fixture.Catalog.TryClaimDeletionAsync(new(fixture.ItemId, 1, RetentionWorkKind.Queued), "worker", fixture.Time.GetUtcNow(), CancellationToken.None);
            Assert.Equal(RetentionClaimDisposition.Claimed, claimed.Disposition);
            Assert.Equal(1L, Number(fixture.Path, "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='deletion'", fixture.ItemId));
            Assert.Equal(0L, Number(fixture.Path, "SELECT attempt_count FROM retention_items WHERE item_id=$item", fixture.ItemId));
        }
        finally { Delete(fixture.Path); }
    }

    [Fact]
    public async Task PreIntentProcessLoss_RequeuesOnceAndPreservesOlderJournalCursor()
    {
        var fixture = await CreateQueuedRawAsync();
        try
        {
            InsertJournal(fixture.Path, fixture.ItemId, 7, 1, fixture.Now);
            var claim = (await fixture.Catalog.TryClaimDeletionAsync(new(fixture.ItemId, 1, RetentionWorkKind.Queued), "lost", fixture.Now, CancellationToken.None)).Claim!;
            Assert.Equal(RetentionMutationDisposition.Applied, await fixture.Catalog.TryCancelBeforeIntentAsync(claim.Fence, fixture.Now, CancellationToken.None));

            Assert.Equal("deletion_queued", Text(fixture.Path, "SELECT state FROM retention_items WHERE item_id=$item", fixture.ItemId));
            Assert.Equal(3L, Number(fixture.Path, "SELECT revision FROM retention_items WHERE item_id=$item", fixture.ItemId));
            Assert.Equal(0L, Number(fixture.Path, "SELECT attempt_count FROM retention_items WHERE item_id=$item", fixture.ItemId));
            Assert.Equal(7L, Number(fixture.Path, "SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item", fixture.ItemId));
            Assert.Equal(1L, Number(fixture.Path, "SELECT expected_revision FROM retention_delete_journal WHERE item_id=$item", fixture.ItemId));
        }
        finally { Delete(fixture.Path); }
    }

    [Fact]
    public async Task RetryClaimCancellation_PreservesOlderCursorWithoutCurrentIntentOrAttempt()
    {
        var fixture = await CreateQueuedRawAsync();
        try
        {
            InsertJournal(fixture.Path, fixture.ItemId, 4, 1, fixture.Now);
            var claim = (await fixture.Catalog.TryClaimDeletionAsync(new(fixture.ItemId, 1, RetentionWorkKind.Queued), "retry", fixture.Now, CancellationToken.None)).Claim!;

            Assert.Equal(RetentionMutationDisposition.Applied, await fixture.Catalog.TryCancelBeforeIntentAsync(claim.Fence, fixture.Now, CancellationToken.None));
            Assert.Equal(0L, Number(fixture.Path, "SELECT attempt_count FROM retention_items WHERE item_id=$item", fixture.ItemId));
            Assert.Equal(4L, Number(fixture.Path, "SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item", fixture.ItemId));
            Assert.Equal(1L, Number(fixture.Path, "SELECT expected_revision FROM retention_delete_journal WHERE item_id=$item", fixture.ItemId));
            Assert.Equal(0L, Number(fixture.Path, "SELECT COUNT(*) FROM retention_delete_journal j JOIN retention_items i ON i.item_id=j.item_id WHERE j.item_id=$item AND j.expected_revision=i.revision", fixture.ItemId));
        }
        finally { Delete(fixture.Path); }
    }

    [Fact]
    public async Task Renewal_RequiresExactLiveFenceAndCapsAtDrainDeadlineWithoutItemMutation()
    {
        var fixture = await CreateClaimWithIntentAsync();
        try
        {
            var cap = fixture.Now + TimeSpan.FromSeconds(90);
            Assert.Equal(RetentionRenewalResult.Renewed, await fixture.Catalog.TryRenewDeletionLeaseAsync(fixture.Claim.Fence, fixture.Now, cap, CancellationToken.None));
            Assert.Equal(cap, Date(fixture.Path, "SELECT expires_at FROM retention_leases WHERE item_id=$item AND lease_kind='deletion'", fixture.ItemId));
            var snapshot = ItemSnapshot(fixture.Path, fixture.ItemId);

            var stale = fixture.Claim.Fence with { LeaseGeneration = fixture.Claim.Fence.LeaseGeneration + 1 };
            Assert.Equal(RetentionRenewalResult.LeaseLost, await fixture.Catalog.TryRenewDeletionLeaseAsync(stale, fixture.Now, cap, CancellationToken.None));
            Assert.Equal(snapshot, ItemSnapshot(fixture.Path, fixture.ItemId));

            var expiredCapSnapshot = ItemSnapshot(fixture.Path, fixture.ItemId);
            Assert.Equal(RetentionRenewalResult.LeaseLost, await fixture.Catalog.TryRenewDeletionLeaseAsync(fixture.Claim.Fence, fixture.Now, fixture.Now, CancellationToken.None));
            Assert.Equal(expiredCapSnapshot, ItemSnapshot(fixture.Path, fixture.ItemId));
        }
        finally { Delete(fixture.Path); }
    }

    [Fact]
    public async Task StaleWorkAndLateResults_AreByteForByteNoOpsAfterAnotherWinner()
    {
        var fixture = await CreateClaimWithIntentAsync();
        try
        {
            fixture.Time.Advance(RetentionV1Constants.LeaseDuration);
            var winner = (await fixture.Catalog.TryClaimDeletionAsync(new(fixture.ItemId, fixture.Claim.Fence.ExpectedRevision, RetentionWorkKind.IntentRecovery), "winner", fixture.Time.GetUtcNow(), CancellationToken.None)).Claim!;
            var before = ItemSnapshot(fixture.Path, fixture.ItemId);

            Assert.Equal(RetentionMutationDisposition.StaleNoOp, await fixture.Catalog.TryCompleteDeletionAsync(fixture.Claim.Fence, fixture.Time.GetUtcNow(), CancellationToken.None));
            Assert.Equal(RetentionMutationDisposition.StaleNoOp, await fixture.Catalog.TryAdvanceDeleteCursorAsync(fixture.Claim.Fence, 0, 1, fixture.Time.GetUtcNow(), CancellationToken.None));
            Assert.Equal(RetentionMutationDisposition.StaleNoOp, (await fixture.Catalog.TryRecordTransientFailureAsync(fixture.Claim.Fence, RetentionErrorCode.DeleteBusy, fixture.Time.GetUtcNow(), CancellationToken.None)).Disposition);
            Assert.Equal(RetentionRenewalResult.LeaseLost, await fixture.Catalog.TryRenewDeletionLeaseAsync(fixture.Claim.Fence, fixture.Time.GetUtcNow(), null, CancellationToken.None));
            Assert.Equal(RetentionIntentDisposition.LeaseLost, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Time.GetUtcNow(), CancellationToken.None)).Disposition);
            Assert.Equal(before, ItemSnapshot(fixture.Path, fixture.ItemId));

            Assert.Equal(RetentionMutationDisposition.Applied, await fixture.Catalog.TryCompleteDeletionAsync(winner.Fence, fixture.Time.GetUtcNow(), CancellationToken.None));
            var final = ItemSnapshot(fixture.Path, fixture.ItemId);
            Assert.Equal(RetentionIntentDisposition.StaleNoOp, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Time.GetUtcNow(), CancellationToken.None)).Disposition);
            Assert.Equal(final, ItemSnapshot(fixture.Path, fixture.ItemId));
        }
        finally { Delete(fixture.Path); }
    }

    [Fact]
    public async Task Recovery_OnlyReclaimsLiveDeletionAfterExpiryAndIncreasesGeneration()
    {
        var fixture = await CreateClaimWithIntentAsync();
        try
        {
            var work = new RetentionWorkReference(fixture.ItemId, fixture.Claim.Fence.ExpectedRevision, RetentionWorkKind.IntentRecovery);
            Assert.Equal(RetentionClaimDisposition.Contended, (await fixture.Catalog.TryClaimDeletionAsync(work, "recovery", fixture.Now, CancellationToken.None)).Disposition);
            var before = ItemSnapshot(fixture.Path, fixture.ItemId);

            fixture.Time.Advance(RetentionV1Constants.LeaseDuration);
            var recovered = (await fixture.Catalog.TryClaimDeletionAsync(work, "recovery", fixture.Time.GetUtcNow(), CancellationToken.None)).Claim!;
            Assert.True(recovered.Fence.LeaseGeneration > fixture.Claim.Fence.LeaseGeneration);
            Assert.Equal(fixture.Claim.Fence.ExpectedRevision, recovered.Fence.ExpectedRevision);
            Assert.True(recovered.HasCurrentIntent);
            Assert.Equal(before, ItemSnapshot(fixture.Path, fixture.ItemId));
        }
        finally { Delete(fixture.Path); }
    }

    private static async Task<Fixture> CreateQueuedRawAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-fence-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
        var source = new RawTelemetryStore(path, context, time);
        source.CreateMonitorSchema();
        var rawId = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, now, null, "{}"));
        var catalog = new RetentionCatalogStore(context, time);
        var itemId = Text(path, "SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$source", rawId.ToString());
        Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
        Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$item", ("$now", now.ToString("O")), ("$item", itemId));
        return new(path, now, time, catalog, itemId);
    }

    private static async Task<ClaimFixture> CreateClaimWithIntentAsync()
    {
        var fixture = await CreateQueuedRawAsync();
        var claim = (await fixture.Catalog.TryClaimDeletionAsync(new(fixture.ItemId, 1, RetentionWorkKind.Queued), "owner", fixture.Now, CancellationToken.None)).Claim!;
        Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
        return new(fixture.Path, fixture.Now, fixture.Time, fixture.Catalog, fixture.ItemId, claim);
    }

    private static void InsertLease(string path, string item, string kind, string owner, DateTimeOffset expiry, long generation) =>
        Execute(path, "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item,$kind,$owner,$expiry,$generation)", ("$item", item), ("$kind", kind), ("$owner", owner), ("$expiry", expiry.ToString("O")), ("$generation", generation));
    private static void InsertJournal(string path, string item, int cursor, long revision, DateTimeOffset now) =>
        Execute(path, "INSERT INTO retention_delete_journal(item_id,durable_cursor,intent_at,expected_revision) VALUES($item,$cursor,$now,$revision)", ("$item", item), ("$cursor", cursor), ("$now", now.ToString("O")), ("$revision", revision));
    private static string ItemSnapshot(string path, string item) => Text(path, "SELECT state || ':' || revision || ':' || attempt_count || ':' || COALESCE(error_code,'') || ':' || COALESCE(next_retry_at,'') || ':' || COALESCE((SELECT durable_cursor FROM retention_delete_journal WHERE item_id=retention_items.item_id),'') FROM retention_items WHERE item_id=$item", item);
    private static void Execute(string path, string sql, params (string Name, object Value)[] values) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
    private static string Text(string path, string sql, string item) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue(sql.Contains("$source", StringComparison.Ordinal) ? "$source" : "$item", item); return (string)q.ExecuteScalar()!; }
    private static long Number(string path, string sql, string item) { using var c = Open(path); using var q = c.CreateCommand(); q.CommandText = sql; q.Parameters.AddWithValue("$item", item); return Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static DateTimeOffset Date(string path, string sql, string item) => DateTimeOffset.Parse(Text(path, sql, item), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); return c; }
    private static void Delete(string path) { SqliteConnection.ClearAllPools(); foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
    private sealed record Fixture(string Path, DateTimeOffset Now, MutableTimeProvider Time, RetentionCatalogStore Catalog, string ItemId);
    private sealed record ClaimFixture(string Path, DateTimeOffset Now, MutableTimeProvider Time, RetentionCatalogStore Catalog, string ItemId, RetentionDeletionClaim Claim);
}

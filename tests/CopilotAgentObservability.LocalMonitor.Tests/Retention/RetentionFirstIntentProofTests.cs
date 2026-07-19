using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionFirstIntentProofTests
{
    [Fact]
    public async Task MissingSqliteSource_FirstIntentRecordsZeroAttemptTerminalFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-first-intent-{Guid.NewGuid():N}.db");
        try
        {
            var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
            var time = new MutableTimeProvider(now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var source = new RawTelemetryStore(path, context, time);
            source.CreateMonitorSchema();
            var rawId = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, now, null, "{}"));
            var catalog = new RetentionCatalogStore(context, time);
            var itemId = Scalar<string>(path, "SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$source", ("$source", rawId.ToString()));
            Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
            Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$item", ("$now", now.ToString("O")), ("$item", itemId));
            Execute(path, "DELETE FROM raw_records WHERE id=$id", ("$id", rawId));

            var prepared = await catalog.PrepareCleanupBatchAsync(now, 100, 100, TimeSpan.FromSeconds(30), CancellationToken.None);
            var claim = (await catalog.TryClaimDeletionAsync(Assert.Single(prepared.Work), "worker", now, CancellationToken.None)).Claim!;

            var result = await catalog.EnsureDeleteIntentAsync(claim.Fence, 0, now, CancellationToken.None);

            Assert.Equal(RetentionIntentDisposition.TerminalFailureRecorded, result.Disposition);
            Assert.Equal(0, result.AttemptNumber);
            Assert.Equal("deletion_failed", Scalar<string>(path, "SELECT state FROM retention_items WHERE item_id=$item", ("$item", itemId)));
            Assert.Equal("retention_unexpected_source_missing", Scalar<string>(path, "SELECT error_code FROM retention_items WHERE item_id=$item", ("$item", itemId)));
            Assert.Equal(0L, Scalar<long>(path, "SELECT attempt_count FROM retention_items WHERE item_id=$item", ("$item", itemId)));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_delete_journal WHERE item_id=$item", ("$item", itemId)));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='deletion'", ("$item", itemId)));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item", ("$item", itemId)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task InvalidSqliteIdentity_FirstIntentRecordsZeroAttemptTerminalFailure()
    {
        var fixture = await CreateClaimAsync();
        try
        {
            Execute(fixture.Path, "UPDATE retention_items SET source_item_id='not-a-number' WHERE item_id=$item", ("$item", fixture.ItemId));
            var result = await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None);
            Assert.Equal(RetentionIntentDisposition.TerminalFailureRecorded, result.Disposition);
            Assert.Equal("retention_invalid_identity", Scalar<string>(fixture.Path, "SELECT error_code FROM retention_items WHERE item_id=$item", ("$item", fixture.ItemId)));
            AssertTerminalWithoutAttempt(fixture.Path, fixture.ItemId);
        }
        finally { Delete(fixture.Path); }
    }

    [Fact]
    public async Task OwnershipMismatch_FirstIntentRecordsZeroAttemptTerminalFailure()
    {
        var fixture = await CreateClaimAsync();
        try
        {
            Execute(fixture.Path, "UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE item_id=$item", ("$item", fixture.ItemId));
            var result = await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None);
            Assert.Equal(RetentionIntentDisposition.TerminalFailureRecorded, result.Disposition);
            Assert.Equal("retention_ownership_mismatch", Scalar<string>(fixture.Path, "SELECT error_code FROM retention_items WHERE item_id=$item", ("$item", fixture.ItemId)));
            AssertTerminalWithoutAttempt(fixture.Path, fixture.ItemId);
        }
        finally { Delete(fixture.Path); }
    }

    [Fact]
    public async Task OlderJournal_WithMatchingCursorReactivatesWithoutOverwritingCursor()
    {
        var fixture = await CreateClaimAsync();
        try
        {
            Assert.Equal(RetentionIntentDisposition.Committed, (await fixture.Catalog.EnsureDeleteIntentAsync(fixture.Claim.Fence, 0, fixture.Now, CancellationToken.None)).Disposition);
            await fixture.Catalog.TryRecordTransientFailureAsync(fixture.Claim.Fence, RetentionErrorCode.DeleteBusy, fixture.Now, CancellationToken.None);
            fixture.Time.Advance(TimeSpan.FromMinutes(1));
            var prepared = await fixture.Catalog.PrepareCleanupBatchAsync(fixture.Time.GetUtcNow(), 100, 100, TimeSpan.FromSeconds(30), CancellationToken.None);
            var retry = (await fixture.Catalog.TryClaimDeletionAsync(Assert.Single(prepared.Work), "retry", fixture.Time.GetUtcNow(), CancellationToken.None)).Claim!;

            var result = await fixture.Catalog.EnsureDeleteIntentAsync(retry.Fence, 0, fixture.Time.GetUtcNow(), CancellationToken.None);

            Assert.Equal(RetentionIntentDisposition.Committed, result.Disposition);
            Assert.Equal(2, result.AttemptNumber);
            Assert.Equal(0, result.IntentCursor);
            Assert.Equal(0L, Number(fixture.Path, "SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item", ("$item", fixture.ItemId)));
            Assert.Equal(retry.Fence.ExpectedRevision, Scalar<long>(fixture.Path, "SELECT expected_revision FROM retention_delete_journal WHERE item_id=$item", ("$item", fixture.ItemId)));
            Assert.Equal(RetentionIntentDisposition.StaleNoOp, (await fixture.Catalog.EnsureDeleteIntentAsync(retry.Fence, 1, fixture.Time.GetUtcNow(), CancellationToken.None)).Disposition);
            Assert.Equal(0L, Number(fixture.Path, "SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$item", ("$item", fixture.ItemId)));
        }
        finally { Delete(fixture.Path); }
    }

    private static async Task<(string Path, DateTimeOffset Now, MutableTimeProvider Time, long RawId, string ItemId, RetentionCatalogStore Catalog, RetentionDeletionClaim Claim)> CreateClaimAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-first-intent-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
        var source = new RawTelemetryStore(path, context, time);
        source.CreateMonitorSchema();
        var rawId = source.Insert(new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, null, now, null, "{}"));
        var catalog = new RetentionCatalogStore(context, time);
        var itemId = Scalar<string>(path, "SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id=$source", ("$source", rawId.ToString()));
        Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");
        Execute(path, "UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$item", ("$now", now.ToString("O")), ("$item", itemId));
        var prepared = await catalog.PrepareCleanupBatchAsync(now, 100, 100, TimeSpan.FromSeconds(30), CancellationToken.None);
        var claim = (await catalog.TryClaimDeletionAsync(Assert.Single(prepared.Work), "worker", now, CancellationToken.None)).Claim!;
        return (path, now, time, rawId, itemId, catalog, claim);
    }

    private static void AssertTerminalWithoutAttempt(string path, string itemId)
    {
        Assert.Equal(0L, Scalar<long>(path, "SELECT attempt_count FROM retention_items WHERE item_id=$item", ("$item", itemId)));
        Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_delete_journal WHERE item_id=$item", ("$item", itemId)));
        Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases WHERE item_id=$item AND lease_kind='deletion'", ("$item", itemId)));
        Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_tombstones WHERE item_id=$item", ("$item", itemId)));
    }

    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (T)command.ExecuteScalar()!;
    }

    private static long Number(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}

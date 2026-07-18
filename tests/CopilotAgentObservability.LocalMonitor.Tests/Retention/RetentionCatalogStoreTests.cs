using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCatalogStoreTests
{
    [Fact]
    public void CreateSchema_RebuildsRawSourceWithRequiredImmutableOwnerToken()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            new RetentionCatalogStore(path).CreateSchema();

            var sql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='raw_records';");
            Assert.Contains("retention_owner_token BLOB NOT NULL", sql, StringComparison.Ordinal);
            Assert.Contains("length(retention_owner_token) = 32", sql, StringComparison.Ordinal);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM raw_records WHERE typeof(retention_owner_token) <> 'blob' OR length(retention_owner_token) <> 32;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RebuildsSessionContentWithRequiredOwnerTokenAndContentKindReceiptBinding()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new RetentionCatalogStore(path).CreateSchema();

            Assert.Equal(13L, Scalar<long>(path, "SELECT version FROM schema_version WHERE component='session';"));
            var sql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='session_event_content';");
            Assert.Contains("retention_owner_token BLOB NOT NULL", sql, StringComparison.Ordinal);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM session_event_content WHERE typeof(retention_owner_token) <> 'blob' OR length(retention_owner_token) <> 32;"));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND ownership_receipt IS NULL;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RebuildsAnalysisSourcesWithRequiredTokenAndForeignKey()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            CreateActualAnalysisRaw(path);
            new RetentionCatalogStore(path).CreateSchema();

            var runsSql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='monitor_analysis_runs';");
            var eventsSql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='monitor_analysis_events';");
            Assert.Contains("retention_owner_token BLOB NOT NULL", runsSql, StringComparison.Ordinal);
            Assert.Contains("FOREIGN KEY (run_id) REFERENCES monitor_analysis_runs(id)", eventsSql, StringComparison.Ordinal);
            Assert.Throws<SqliteException>(() => Execute(path, "INSERT INTO monitor_analysis_events(run_id,event_type,message,occurred_at) VALUES(999,'test','synthetic','2026-07-12T00:00:00.0000000+00:00');"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_BackfillsRealSessionFixtureAndTwoReopensKeepExactIdentity()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(path).CreateSchema();
            var first = new RetentionCatalogStore(path); first.CreateSchema();
            var firstInstance = first.StoreInstanceId;
            var firstItems = ReadItems(path);

            new RetentionCatalogStore(path).CreateSchema();
            var third = new RetentionCatalogStore(path); third.CreateSchema();

            Assert.NotEmpty(firstItems);
            Assert.Equal(firstInstance, third.StoreInstanceId);
            Assert.Equal(firstItems, ReadItems(path));
            Assert.Equal(1L, Scalar<long>(path, "SELECT version FROM retention_component_versions WHERE component='retention';"));
            Assert.Equal(1L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_component_versions;"));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND (captured_at <> (SELECT captured_at FROM session_event_content WHERE event_id=retention_items.source_item_id) OR expires_at <> (SELECT expires_at FROM session_event_content WHERE event_id=retention_items.source_item_id));"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_BackfillsRealMonitorFixtureAndActualAnalysisRawSchema()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            CreateActualAnalysisRaw(path);
            var store = new RetentionCatalogStore(path); store.CreateSchema();

            Assert.Contains(ReadItems(path), item => item.Kind == "raw_record");
            var analysis = Assert.Single(ReadItems(path), item => item.Kind == "analysis_run_raw");
            Assert.Equal("2026-07-12T01:02:03.0000000+00:00", analysis.CapturedAt);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND captured_at <> (SELECT received_at FROM raw_records WHERE id=retention_items.source_item_id);"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_InvalidLegacyTimestampRollsBackEntireCatalogAndReturnsSanitizedCode()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            Execute(path, "UPDATE raw_records SET received_at='not-a-timestamp';");
            var exception = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(path).CreateSchema());

            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.False(TableExists(path, "retention_component_versions"));
            Assert.False(TableExists(path, "retention_items"));
            Execute(path, "UPDATE raw_records SET received_at='2026-07-12T00:00:00.0000000+00:00';");
            new RetentionCatalogStore(path).CreateSchema();
            Assert.True(TableExists(path, "retention_items"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_OrphanedSessionContentBlocksWithoutPartialCatalog()
    {
        var path = CopyFixture("session", "session-v10.sqlite");
        try
        {
            new CopilotAgentObservability.Persistence.Sqlite.Sessions.SqliteSessionStore(path).CreateSchema();
            Execute(path, "PRAGMA foreign_keys=OFF; DELETE FROM session_events WHERE event_id IN (SELECT event_id FROM session_event_content); PRAGMA foreign_keys=ON;");

            var exception = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(path).CreateSchema());

            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.False(TableExists(path, "retention_items"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_ExpiryCommitsIrreversibleDenialAndStaleRevisionChangesNothing()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var store = new RetentionCatalogStore(path); store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            Assert.Null(await store.TryAcquireAsync(key, item.Revision - 1, RetentionLeaseKind.Access, item.ExpiresAt, CancellationToken.None));
            Assert.Equal(item, store.Find(key));
            Assert.Null(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, item.ExpiresAt, CancellationToken.None));
            var denied = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Assert.Equal(RetentionItemLifecycle.ExpiredPendingDeletion, denied.State);
            Assert.NotNull(denied.ReadDeniedAt);

            var reopened = new RetentionCatalogStore(path).Find(key);
            Assert.Equal(denied, reopened);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_UsesClosedRetentionDomains()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            new RetentionCatalogStore(path).CreateSchema();
            var sql = Scalar<string>(path, "SELECT sql FROM sqlite_master WHERE type='table' AND name='retention_items';");
            Assert.Contains("CHECK (store_kind IN", sql, StringComparison.Ordinal);
            Assert.Contains("CHECK (state IN", sql, StringComparison.Ordinal);
            Assert.Contains("UNIQUE(store_instance_id, store_kind, source_item_id)", sql, StringComparison.Ordinal);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RejectsInvalidCatalogRowsWithoutChangingCommittedState()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var store = new RetentionCatalogStore(path); store.CreateSchema();
            var before = Scalar<long>(path, "SELECT COUNT(*) FROM retention_items;");

            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,owner_reference,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES('bad-fk','not-a-store','raw_record','1','1','2026-07-12T00:00:00.0000000+00:00','2026-10-10T00:00:00.0000000+00:00','raw-default-90d',1,'expiring',1,{RetentionV1Constants.AdapterCoverageVersion});"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,owner_reference,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES('bad-domain',(SELECT store_instance_id FROM retention_store_instances),'unknown','2','2','2026-07-12T00:00:00.0000000+00:00','2026-10-10T00:00:00.0000000+00:00','raw-default-90d',1,'expiring',1,{RetentionV1Constants.AdapterCoverageVersion});"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,owner_reference,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES('bad-null',(SELECT store_instance_id FROM retention_store_instances),'raw_record','3','3','2026-07-12T00:00:00.0000000+00:00',NULL,'raw-default-90d',1,'expiring',1,{RetentionV1Constants.AdapterCoverageVersion});"));

            Assert.Equal(before, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_ExpiredLeaseIsReclaimedWithNewGenerationAndStaleDisposeCannotReleaseIt()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time); store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var first = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));

            time.Advance(RetentionV1Constants.LeaseDuration);
            var replacement = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            Assert.True(replacement.Generation > first.Generation);
            first.Dispose();

            Assert.Null(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            replacement.Dispose();
            Assert.NotNull(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_AlreadyExpiredLegacyRowsAreDeniedAtInjectedStartupTime()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var now = new DateTimeOffset(2026, 10, 11, 0, 0, 0, TimeSpan.Zero);
            new RetentionCatalogStore(path, new MutableTimeProvider(now)).CreateSchema();

            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE state <> 'expired_pending_deletion' OR read_denied_at <> '2026-10-11T00:00:00.0000000+00:00' OR queued_at <> '2026-10-11T00:00:00.0000000+00:00';"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_MissingExactRawRecordFailsClosedWithoutGrantingLease()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var store = new RetentionCatalogStore(path); store.CreateSchema();
            var key = new RetentionOwnershipKey(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
            var item = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            Execute(path, "DELETE FROM raw_records WHERE id=(SELECT MIN(id) FROM raw_records);");

            Assert.Null(await store.TryAcquireAsync(key, item.Revision, RetentionLeaseKind.Access, item.CapturedAt, CancellationToken.None));
            Assert.Equal(RetentionItemLifecycle.DeletionFailed, Assert.IsType<RetentionCatalogItem>(store.Find(key)).State);
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_leases;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_DeletionLeaseRequiresQueuedDenialAndExcludesLiveReadOperationLeases()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time); store.CreateSchema();
            var key = RawKey(path, store);
            var readable = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            Assert.Null(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None));
            using var access = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            PromoteToQueued(path, readable.ItemId, time.GetUtcNow());
            var queued = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            Assert.Null(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None));
            access.Dispose();
            using var deletion = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None));
            Assert.Null(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Access, time.GetUtcNow(), CancellationToken.None));
            Assert.Null(await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Operation, time.GetUtcNow(), CancellationToken.None));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_ExpiredReadLeaseDoesNotBlockSingleConcurrentDeletionClaim()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var store = new RetentionCatalogStore(path, time); store.CreateSchema();
            var key = RawKey(path, store);
            var readable = Assert.IsType<RetentionCatalogItem>(store.Find(key));
            var expiredAccess = Assert.IsType<RetentionReadLeaseHandle>(await store.TryAcquireAsync(key, readable.Revision, RetentionLeaseKind.Operation, time.GetUtcNow(), CancellationToken.None));
            time.Advance(RetentionV1Constants.LeaseDuration);
            PromoteToQueued(path, readable.ItemId, time.GetUtcNow());
            var queued = Assert.IsType<RetentionCatalogItem>(store.Find(key));

            using var start = new Barrier(3);
            var first = Task.Run(async () => { start.SignalAndWait(); return await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None); });
            var second = Task.Run(async () => { start.SignalAndWait(); return await store.TryAcquireAsync(key, queued.Revision, RetentionLeaseKind.Deletion, time.GetUtcNow(), CancellationToken.None); });
            start.SignalAndWait();
            var claims = await Task.WhenAll(first, second);

            Assert.Single(claims, static claim => claim is not null);
            expiredAccess.Dispose();
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task ReadGate_DeletingRecoveryAfterRestartRequiresCurrentDeleteIntentAndReclaimsExpiredLease()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 12, 1, 0, 0, TimeSpan.Zero));
            var initialized = new RetentionCatalogStore(path, time);
            initialized.CreateSchema();
            var key = RawKey(path, initialized);
            var item = Assert.IsType<RetentionCatalogItem>(initialized.Find(key));
            var now = time.GetUtcNow();
            Execute(path, $"UPDATE retention_items SET state='deleting', read_denied_at='{now:O}', revision=revision+1 WHERE item_id='{item.ItemId}'; DELETE FROM raw_records WHERE id={key.SourceItemId};");
            var deleting = Assert.IsType<RetentionCatalogItem>(initialized.Find(key));
            Execute(path, $"INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('{deleting.ItemId}','deletion','expired-owner','{now.AddSeconds(-1):O}',9);");

            var reopened = new RetentionCatalogStore(path, time);
            Assert.Null(await reopened.TryAcquireAsync(key, deleting.Revision, RetentionLeaseKind.Deletion, now, CancellationToken.None));

            Execute(path, $"INSERT INTO retention_delete_journal(item_id,intent_at,expected_revision) VALUES('{deleting.ItemId}','{now:O}',{deleting.Revision - 1});");
            Assert.Null(await reopened.TryAcquireAsync(key, deleting.Revision, RetentionLeaseKind.Deletion, now, CancellationToken.None));

            Execute(path, $"DELETE FROM retention_delete_journal; INSERT INTO retention_delete_journal(item_id,intent_at,expected_revision) VALUES('{deleting.ItemId}','{now:O}',{deleting.Revision});");
            using var claim = Assert.IsType<RetentionReadLeaseHandle>(await reopened.TryAcquireAsync(key, deleting.Revision, RetentionLeaseKind.Deletion, now, CancellationToken.None));
            Assert.Equal(10, claim.Generation);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_RejectsInvalidJournalLeaseAndCoverageDomains()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var store = new RetentionCatalogStore(path); store.CreateSchema();
            var itemId = Scalar<string>(path, "SELECT item_id FROM retention_items ORDER BY item_id LIMIT 1;");

            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_capture_journal(item_id,phase) VALUES('{itemId}','invalid');"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('{itemId}','2026-07-12T00:00:00.0000000+00:00','2026-07-12T00:00:01.0000000+00:00');"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_delete_journal(item_id,intent_at,expected_revision) VALUES('{itemId}',NULL,1);"));
            Assert.Throws<SqliteException>(() => Execute(path, $"INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('{itemId}','invalid','owner','2026-07-12T00:00:00.0000000+00:00',1);"));
            Assert.Throws<SqliteException>(() => Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES('raw_record',0);"));
            Assert.Throws<SqliteException>(() => Execute(path, "INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES('invalid',1);"));
            Assert.Equal(0L, Scalar<long>(path, "SELECT COUNT(*) FROM retention_adapter_coverage;"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_NewerComponentVersionIsSanitizedAndAtomic()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            Execute(path, "CREATE TABLE retention_component_versions(component TEXT PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO retention_component_versions VALUES('retention',2);");
            var exception = Assert.Throws<RetentionMigrationBlockedException>(() => new RetentionCatalogStore(path).CreateSchema());
            Assert.Equal("retention_migration_blocked", exception.Message);
            Assert.False(TableExists(path, "retention_items"));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void CreateSchema_PostBackfillSourceMismatchRollsBackThenRepairsAcrossTwoReopens()
    {
        var path = CopyFixture("monitor", "monitor-v5.sqlite");
        try
        {
            var originalRawCount = Scalar<long>(path, "SELECT COUNT(*) FROM raw_records;");
            var injected = new RetentionCatalogStore(path, backfillValidationCheckpoint: static (connection, transaction) =>
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM raw_records WHERE id=(SELECT MIN(id) FROM raw_records);";
                delete.ExecuteNonQuery();
            });

            Assert.Throws<RetentionMigrationBlockedException>(injected.CreateSchema);
            Assert.Equal(5L, Scalar<long>(path, "SELECT version FROM schema_version WHERE component='monitor';"));
            Assert.Equal(originalRawCount, Scalar<long>(path, "SELECT COUNT(*) FROM raw_records;"));
            Assert.False(TableExists(path, "retention_items"));

            var repaired = new RetentionCatalogStore(path);
            repaired.CreateSchema();
            repaired.CreateSchema();
            Assert.Equal(originalRawCount, Scalar<long>(path, "SELECT COUNT(*) FROM raw_records;"));
            Assert.Equal(originalRawCount, Scalar<long>(path, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record';"));
        }
        finally { Delete(path); }
    }

    private static IReadOnlyList<ItemRow> ReadItems(string path)
    {
        using var connection = Open(path); using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id,store_kind,source_item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items ORDER BY store_kind,source_item_id;";
        using var reader = command.ExecuteReader(); var rows = new List<ItemRow>();
        while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt64(6), reader.IsDBNull(7) ? null : reader.GetString(7)));
        return rows;
    }

    private static void CreateActualAnalysisRaw(string path) => Execute(path, """
        CREATE TABLE monitor_analysis_runs (id INTEGER PRIMARY KEY, trace_id TEXT NOT NULL, raw_record_id INTEGER NULL, span_id TEXT NULL, focus TEXT NOT NULL, status TEXT NOT NULL, requested_at TEXT NOT NULL, started_at TEXT NULL, completed_at TEXT NULL, result_markdown TEXT NULL, error_message TEXT NULL);
        CREATE TABLE monitor_analysis_events (id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL, event_type TEXT NOT NULL, message TEXT NOT NULL, occurred_at TEXT NOT NULL);
        INSERT INTO monitor_analysis_runs VALUES(7,'fixture-trace',1,NULL,'trace','completed','2026-07-12T01:02:03.0000000+00:00',NULL,NULL,'synthetic result',NULL);
        """);
    private static RetentionOwnershipKey RawKey(string path, RetentionCatalogStore store) => new(store.StoreInstanceId, RetentionStoreKind.RawRecord, Scalar<long>(path, "SELECT id FROM raw_records ORDER BY id LIMIT 1;").ToString());
    private static void PromoteToQueued(string path, string itemId, DateTimeOffset now) => Execute(path, $"UPDATE retention_items SET state='deletion_queued', read_denied_at='{now:O}', queued_at='{now:O}', revision=revision+1 WHERE item_id='{itemId}';");
    private static string CopyFixture(string component, string file) { var path = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", component, file); var copy = Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}.sqlite"); File.Copy(path, copy); return copy; }
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); using var q=c.CreateCommand();q.CommandText="PRAGMA foreign_keys=ON;";q.ExecuteNonQuery(); return c; }
    private static T Scalar<T>(string path, string sql) { using var c=Open(path);using var q=c.CreateCommand();q.CommandText=sql;return (T)Convert.ChangeType(q.ExecuteScalar()!,typeof(T)); }
    private static bool TableExists(string path, string name) { using var c=Open(path);using var q=c.CreateCommand();q.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";q.Parameters.AddWithValue("$name",name);return Convert.ToInt64(q.ExecuteScalar())==1; }
    private static void Execute(string path, string sql) { using var c=Open(path);using var q=c.CreateCommand();q.CommandText=sql;q.ExecuteNonQuery(); }
    private static void Delete(string path) { if(File.Exists(path)) File.Delete(path); if(File.Exists(path+"-wal")) File.Delete(path+"-wal"); if(File.Exists(path+"-shm")) File.Delete(path+"-shm"); }
    private sealed record ItemRow(string Id, string Kind, string Source, string CapturedAt, string ExpiresAt, string State, long Revision, string? ReadDeniedAt);
}

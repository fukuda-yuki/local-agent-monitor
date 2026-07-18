using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCatalogStoreTests
{
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
    private static string CopyFixture(string component, string file) { var path = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", component, file); var copy = Path.Combine(Path.GetTempPath(), $"retention-{Guid.NewGuid():N}.sqlite"); File.Copy(path, copy); return copy; }
    private static SqliteConnection Open(string path) { var c = new SqliteConnection($"Data Source={path};Pooling=False"); c.Open(); return c; }
    private static T Scalar<T>(string path, string sql) { using var c=Open(path);using var q=c.CreateCommand();q.CommandText=sql;return (T)Convert.ChangeType(q.ExecuteScalar()!,typeof(T)); }
    private static bool TableExists(string path, string name) { using var c=Open(path);using var q=c.CreateCommand();q.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";q.Parameters.AddWithValue("$name",name);return Convert.ToInt64(q.ExecuteScalar())==1; }
    private static void Execute(string path, string sql) { using var c=Open(path);using var q=c.CreateCommand();q.CommandText=sql;q.ExecuteNonQuery(); }
    private static void Delete(string path) { if(File.Exists(path)) File.Delete(path); if(File.Exists(path+"-wal")) File.Delete(path+"-wal"); if(File.Exists(path+"-shm")) File.Delete(path+"-shm"); }
    private sealed record ItemRow(string Id, string Kind, string Source, string CapturedAt, string ExpiresAt, string State, long Revision, string? ReadDeniedAt);
}

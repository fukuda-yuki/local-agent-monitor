using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedImportMigrationTests
{
    [Fact]
    public void CreateSchema_FreshDatabaseCreatesIndependentVersionOneComponent()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "fresh-import.sqlite");

        new SqliteSanitizedImportStore(database).CreateSchema();

        Assert.Equal(1L, Scalar(database, "SELECT version FROM schema_version WHERE component='sanitized_import';"));
        Assert.Equal(6L, Scalar(database, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name LIKE 'sanitized_import_%';"));
        Assert.Equal(
            ["import_id", "local_node_id", "declared_state"],
            Rows(database, "SELECT name FROM pragma_table_info('sanitized_import_graph_declarations') ORDER BY cid;"));
        Assert.Contains("first_import_id",
            Rows(database, "SELECT name FROM pragma_table_info('sanitized_import_records') ORDER BY cid;"));
    }

    [Fact]
    public void CreateSchema_SupportedMonitorAndSessionVectorPreservesVersionsAndData()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "supported-vector.sqlite");
        Execute(database, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('monitor',7),('session',13); CREATE TABLE kept(value TEXT NOT NULL); INSERT INTO kept VALUES('same');");

        new SqliteSanitizedImportStore(database).CreateSchema();

        Assert.Equal(["monitor:7", "sanitized_import:1", "session:13"], Rows(database, "SELECT component || ':' || version FROM schema_version ORDER BY component;"));
        Assert.Equal("same", Rows(database, "SELECT value FROM kept;").Single());
    }

    [Fact]
    public void CreateSchema_FutureComponentFailsBeforeMutation()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "future-import.sqlite");
        Execute(database, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('sanitized_import',2); CREATE TABLE kept(value TEXT NOT NULL); INSERT INTO kept VALUES('same');");

        Assert.Throws<InvalidOperationException>(() => new SqliteSanitizedImportStore(database).CreateSchema());

        Assert.Equal(2L, Scalar(database, "SELECT version FROM schema_version WHERE component='sanitized_import';"));
        Assert.Equal("same", Rows(database, "SELECT value FROM kept;").Single());
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name LIKE 'sanitized_import_%';"));
    }

    [Fact]
    public void CreateSchema_UnstampedImportTableFailsWithoutAdoptingIt()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "unstamped-import.sqlite");
        Execute(database, "CREATE TABLE sanitized_import_history(kept TEXT NOT NULL); INSERT INTO sanitized_import_history VALUES('same');");

        Assert.Throws<InvalidOperationException>(() => new SqliteSanitizedImportStore(database).CreateSchema());

        Assert.Equal("same", Rows(database, "SELECT kept FROM sanitized_import_history;").Single());
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='schema_version';"));
    }

    [Fact]
    public void CreateSchema_StampedComponentWithAlteredTableShapeFails()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "altered-import.sqlite");
        var store = new SqliteSanitizedImportStore(database);
        store.CreateSchema();
        Execute(database, "ALTER TABLE sanitized_import_records ADD COLUMN unexpected TEXT NULL;");

        Assert.Throws<InvalidOperationException>(() => store.CreateSchema());

        Assert.Equal(1L, Scalar(database, "SELECT version FROM schema_version WHERE component='sanitized_import';"));
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM pragma_table_info('sanitized_import_records') WHERE name='unexpected';"));
    }

    [Fact]
    public void CreateSchema_StampedComponentWithMissingIndexFails()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "missing-index.sqlite");
        var store = new SqliteSanitizedImportStore(database);
        store.CreateSchema();
        Execute(database, "DROP INDEX sanitized_import_graph_edges_target_idx;");

        Assert.Throws<InvalidOperationException>(() => store.CreateSchema());

        Assert.Equal(1L, Scalar(database, "SELECT version FROM schema_version WHERE component='sanitized_import';"));
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM sqlite_schema WHERE type='index' AND name='sanitized_import_graph_edges_target_idx';"));
    }

    [Fact]
    public void CreateSchema_StampedComponentWithSameColumnsButWeakenedDefinitionFails()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "weakened-import.sqlite");
        var store = new SqliteSanitizedImportStore(database);
        store.CreateSchema();
        Execute(database, """
            PRAGMA foreign_keys=OFF;
            DROP INDEX sanitized_import_origins_record_idx;
            DROP TABLE sanitized_import_origins;
            CREATE TABLE sanitized_import_origins (
                import_id BLOB,
                local_record_id BLOB,
                entry_path BLOB,
                source_snapshot_id BLOB,
                source_local_monitor_version BLOB,
                source_created_at BLOB,
                imported_at BLOB
            );
            CREATE INDEX sanitized_import_origins_record_idx ON sanitized_import_origins(local_record_id,import_id);
            """);

        Assert.Throws<InvalidOperationException>(() => store.CreateSchema());

        Assert.Equal(
            ["import_id", "local_record_id", "entry_path", "source_snapshot_id", "source_local_monitor_version", "source_created_at", "imported_at"],
            Rows(database, "SELECT name FROM pragma_table_info('sanitized_import_origins') ORDER BY cid;"));
    }

    [Fact]
    public void CreateSchema_StampedComponentWithOffNamespaceTriggerFails()
    {
        using var temp = new MonitorTempDirectory();
        var database = Path.Combine(temp.Path, "trigger-import.sqlite");
        var store = new SqliteSanitizedImportStore(database);
        store.CreateSchema();
        Execute(database, """
            CREATE TABLE copied_import_id(value TEXT NOT NULL);
            CREATE TRIGGER copy_import_id_after_insert
            AFTER INSERT ON sanitized_import_records
            BEGIN
                INSERT INTO copied_import_id(value) VALUES(NEW.local_record_id);
            END;
            """);

        Assert.Throws<InvalidOperationException>(() => store.CreateSchema());

        Assert.Equal(1L, Scalar(database,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name='copy_import_id_after_insert';"));
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }

    private static long Scalar(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string[] Rows(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database}"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader(); var values = new List<string>(); while (reader.Read()) values.Add(reader.GetString(0)); return values.ToArray();
    }
}

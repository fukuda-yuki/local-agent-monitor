using System.IO.Compression;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupLocalArchiveComponentTests
{
    [Fact]
    public void Preflight_orders_local_archive_immediately_after_catalog_before_retention()
    {
        using var database = new TestDatabase();

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.True(result.Success, result.ErrorCode);
        var steps = result.MigrationSteps!.ToArray();
        var catalog = Array.IndexOf(steps, "local_repository_catalog:0->1");
        var archive = Array.IndexOf(steps, "local_archive:0->1");
        var retention = Array.IndexOf(steps, "retention:0->1");
        Assert.Equal(catalog + 1, archive);
        Assert.Equal(archive + 1, retention);
    }

    [Fact]
    public void Declared_local_archive_requires_exact_session_14_and_catalog_1()
    {
        using var database = new TestDatabase();
        database.InstallParents();
        Assert.True(database.Service.PreflightForMigration(database.Path).Success, "parent preflight");
        database.InstallArchive();
        database.Execute("UPDATE schema_version SET version=13 WHERE component='session';");

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("tbl_name")]
    public void Undeclared_local_archive_reserved_namespace_is_rejected_ascii_case_insensitively(string field)
    {
        using var database = new TestDatabase();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = field == "name"
            ? "CREATE TABLE LoCaL_ArChIvE_intruder(id INTEGER);"
            : "CREATE TABLE safe_archive_target(id INTEGER); CREATE INDEX safe_archive_index ON safe_archive_target(id);";
        command.ExecuteNonQuery();
        if (field == "tbl_name")
        {
            using var rewrite = connection.CreateCommand();
            rewrite.CommandText = "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET tbl_name='Ix_LoCaL_ArChIvE_intruder' WHERE name='safe_archive_index'; PRAGMA writable_schema=OFF;";
            rewrite.ExecuteNonQuery();
        }
        var versions = database.ReadVersions(connection);

        Assert.False(SqliteRuntimeBackupService.ValidateOwnedComponentNamespaces(connection, versions));
    }

    [Fact]
    public void Declared_local_archive_allows_exact_owner_triggers_and_rejects_a_changed_trigger()
    {
        using var database = new TestDatabase();
        database.InstallParents();
        Assert.True(database.Service.PreflightForMigration(database.Path).Success, "parent preflight");
        database.InstallArchiveOnly();
        Assert.True(database.Service.PreflightForMigration(database.Path).Success, "preflight");
        database.Execute(
            "DROP TRIGGER local_archive_events_delete_rejected; " +
            "CREATE TRIGGER local_archive_events_delete_rejected BEFORE DELETE ON local_archive_events BEGIN SELECT RAISE(ABORT,'changed'); END;");

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Fact]
    public void Backup_manifest_pins_local_archive_component_and_both_table_counts()
    {
        using var database = new TestDatabase();
        database.InstallArchive();
        database.SeedSessionArchive();
        var bundle = System.IO.Path.Combine(database.Root, "archive.zip");

        var created = database.Service.CreateAndPublish(database.Path, bundle);

        Assert.True(created.Success, created.ErrorCode);
        using var archive = ZipFile.OpenRead(bundle);
        var manifestEntry = Assert.Single(archive.Entries, entry => entry.FullName == "manifest.json");
        using var stream = manifestEntry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var manifest = RuntimeBackupJson.ParseManifest(memory.ToArray());
        Assert.Equal(1, manifest.ComponentVersions["local_archive"]);
        Assert.Equal(1, manifest.RowCounts["local_archive_current"]);
        Assert.Equal(1, manifest.RowCounts["local_archive_events"]);
    }

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"runtime-backup-local-archive-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "monitor.db");
            Service = new SqliteRuntimeBackupService();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
            transaction.Commit();
        }

        internal string Root { get; }
        internal string Path { get; }
        internal SqliteRuntimeBackupService Service { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void InstallArchive()
        {
            InstallParents();
            InstallArchiveOnly();
        }

        internal void InstallArchiveOnly()
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            LocalArchiveSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        internal void InstallParents()
        {
            using (var retentionConnection = Open())
            using (var retentionTransaction = retentionConnection.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(retentionConnection, retentionTransaction);
                retentionTransaction.Commit();
            }
            new SqliteSourceCompatibilityStore(Path).CreateSchema();
            new SqliteSessionStore(Path).CreateSchema();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            SkillProjectionSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        internal void Execute(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal void SeedSessionArchive()
        {
            const string sessionId = "01900000-0000-7000-8000-000000000001";
            const string eventId = "01900000-0000-7000-8000-000000000002";
            const string at = "2026-08-15T00:00:00.0000000+00:00";
            Execute($"""
                INSERT INTO sessions(
                    session_id,status,completeness,last_seen_at,raw_retention_state,
                    created_at,updated_at)
                VALUES(
                    '{sessionId}','active','unbound','{at}','not_captured',
                    '{at}','{at}');
                INSERT INTO local_archive_current(
                    target_kind,target_id,state,revision,archived_at,updated_at)
                VALUES('session','{sessionId}','archived',1,'{at}','{at}');
                INSERT INTO local_archive_events(
                    event_id,target_kind,target_id,action,previous_revision,new_revision,
                    occurred_at)
                VALUES(
                    '{eventId}','session','{sessionId}','archive',0,1,'{at}');
                """);
        }

        internal Dictionary<string, int> ReadVersions(SqliteConnection connection)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var table in new[] { "schema_version", "retention_component_versions" })
            {
                using var existence = connection.CreateCommand();
                existence.CommandText = $"SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='{table}');";
                if (Convert.ToInt64(existence.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                    continue;
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT component,version FROM {table} ORDER BY component;";
                using var reader = command.ExecuteReader();
                while (reader.Read()) result.Add(reader.GetString(0), reader.GetInt32(1));
            }
            return result;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}

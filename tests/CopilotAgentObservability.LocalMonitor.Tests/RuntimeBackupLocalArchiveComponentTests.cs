using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
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
    public void Declared_local_archive_rejects_a_physically_valid_session_13_and_legacy_catalog()
    {
        using var database = new TestDatabase();
        database.InstallArchive();
        Assert.True(database.Service.PreflightForMigration(database.Path).Success, "current parent preflight");
        database.DowngradeSessionToVersion13();
        database.AssertExactArchiveSchema();
        database.AssertValidLegacyCatalog();

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(13, result.ComponentVersions!["session"]);
        Assert.Equal(1, result.ComponentVersions["local_repository_catalog"]);
        Assert.Equal(1, result.ComponentVersions["local_archive"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    public void Declared_local_archive_rejects_a_missing_or_wrong_catalog_1_parent(string catalog)
    {
        using var database = new TestDatabase();
        if (catalog == "missing")
        {
            database.InstallArchive();
            Assert.True(database.Service.PreflightForMigration(database.Path).Success, "current parent preflight");
            database.RemoveCatalog();
        }
        else
        {
            database.InstallArchive();
            Assert.True(database.Service.PreflightForMigration(database.Path).Success, "current parent preflight");
            database.Execute("UPDATE schema_version SET version=2 WHERE component='local_repository_catalog';");
        }
        database.AssertExactArchiveSchema();

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(1, result.ComponentVersions!["local_archive"]);
        if (catalog == "missing")
            Assert.DoesNotContain("local_repository_catalog", result.ComponentVersions);
        else
            Assert.Equal(2, result.ComponentVersions["local_repository_catalog"]);
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

    [Fact]
    public void Invalid_archive_parent_has_one_fixed_runtime_backup_surface_classification()
    {
        using var database = new TestDatabase();
        database.InstallArchive();
        var validBundle = System.IO.Path.Combine(database.Root, "valid.zip");
        var invalidBundle = System.IO.Path.Combine(database.Root, "invalid.zip");
        var previewTarget = System.IO.Path.Combine(database.Root, "preview.db");
        var restoreTarget = System.IO.Path.Combine(database.Root, "restore.db");
        Assert.True(database.Service.CreateAndPublish(database.Path, validBundle).Success, "valid backup");
        RewriteArchiveDatabase(validBundle, invalidBundle, SeedMissingSessionParentChain);
        SeedMissingSessionParentChain(database.Path);
        database.AssertExactArchiveSchema();

        var preflight = database.Service.PreflightForMigration(database.Path);
        var inspection = database.Service.Inspect(invalidBundle);
        var preview = database.Service.Preview(invalidBundle, previewTarget);
        var restored = database.Service.Restore(invalidBundle, restoreTarget, new RuntimeRestoreOptions());

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restored.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restored.ErrorCode);
        Assert.False(File.Exists(previewTarget));
        Assert.False(File.Exists(restoreTarget));
    }

    private static void SeedMissingSessionParentChain(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_archive_current(
                target_kind,target_id,state,revision,archived_at,updated_at)
            VALUES(
                'session','01900000-0000-7000-8000-000000000099','archived',1,
                '2026-08-15T00:00:00.0000000+00:00','2026-08-15T00:00:00.0000000+00:00');
            INSERT INTO local_archive_events(
                event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at)
            VALUES(
                '01900000-0000-7000-8000-000000000098','session',
                '01900000-0000-7000-8000-000000000099','archive',0,1,
                '2026-08-15T00:00:00.0000000+00:00');
            """;
        command.ExecuteNonQuery();
    }

    private static void RewriteArchiveDatabase(string source, string output, Action<string> mutate)
    {
        byte[] manifest;
        byte[] database;
        using (var archive = ZipFile.OpenRead(source))
        {
            manifest = Read(archive.GetEntry("manifest.json")!);
            database = Read(archive.GetEntry("database.sqlite")!);
        }
        var mutated = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(output)!, $".archive-rewrite-{Guid.NewGuid():N}.sqlite");
        File.WriteAllBytes(mutated, database);
        mutate(mutated);
        using (var connection = Open(mutated))
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
            checkpoint.ExecuteNonQuery();
        }
        database = File.ReadAllBytes(mutated);
        var parsed = RuntimeBackupJson.ParseManifest(manifest);
        var rowCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        using (var connection = Open(mutated))
        {
            foreach (var table in parsed.RowCounts.Keys)
            {
                using var count = connection.CreateCommand();
                count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                rowCounts.Add(table, Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture));
            }
        }
        File.Delete(mutated);
        File.Delete(mutated + "-wal");
        File.Delete(mutated + "-shm");
        manifest = RuntimeBackupJson.WriteManifest(parsed with
        {
            DatabaseSha256 = Convert.ToHexStringLower(SHA256.HashData(database)),
            DatabaseSize = database.LongLength,
            RowCounts = rowCounts,
        });
        using var target = ZipFile.Open(output, ZipArchiveMode.Create);
        Write(target, "manifest.json", manifest);
        Write(target, "database.sqlite", database);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        return memory.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(bytes);
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
            InstallSessionParentWithoutCatalog();
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        internal void InstallSessionParentWithoutCatalog()
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
            SkillProjectionSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        internal void DowngradeSessionToVersion13()
        {
            using var connection = Open();
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
        }

        internal void RemoveCatalog()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=OFF;";
            command.ExecuteNonQuery();
            foreach (var table in LocalRepositoryCatalogSchemaV1.TableNames.Reverse())
            {
                command.CommandText = $"DROP TABLE \"{table}\";";
                command.ExecuteNonQuery();
            }
            command.CommandText = "DELETE FROM schema_version WHERE component='local_repository_catalog'; PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
        }

        internal void AssertExactArchiveSchema()
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            Assert.True(LocalArchiveSchemaV1.HasExactOwnedSchema(connection, transaction));
            transaction.Rollback();
        }

        internal void AssertValidLegacyCatalog()
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            LocalRepositoryCatalogBackupValidation.ValidateLegacySession13(connection, transaction);
            transaction.Rollback();
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

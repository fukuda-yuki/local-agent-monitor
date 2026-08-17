using System.IO.Compression;
using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupSkillInvocationSnapshotComponentTests
{
    [Fact]
    public void Preflight_orders_skill_invocation_snapshot_immediately_after_skill_projection()
    {
        using var database = new TestDatabase();

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.True(result.Success, result.ErrorCode);
        var steps = result.MigrationSteps!.ToArray();
        var projection = Array.IndexOf(steps, "skill_projection:0->1");
        var snapshot = Array.IndexOf(steps, "skill_invocation_snapshot:0->1");
        Assert.NotEqual(-1, projection);
        Assert.Equal(projection + 1, snapshot);
    }

    [Fact]
    public void Declared_component_rejects_a_session_13_parent()
    {
        using var database = new TestDatabase();
        database.InstallComponent();
        Assert.True(database.Service.PreflightForMigration(database.Path).Success, "current parent preflight");
        database.DowngradeSessionToVersion13();

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(13, result.ComponentVersions!["session"]);
        Assert.Equal(1, result.ComponentVersions["skill_invocation_snapshot"]);
    }

    // Retention records its version in its own retention_component_versions table, not in
    // schema_version, so each parent is removed from the store that actually owns it.
    [Theory]
    [InlineData("DELETE FROM retention_component_versions WHERE component='retention';")]
    [InlineData("DELETE FROM schema_version WHERE component='skill_projection';")]
    public void Declared_component_rejects_a_missing_parent(string mutation)
    {
        using var database = new TestDatabase();
        database.InstallComponent();
        Assert.True(database.Service.PreflightForMigration(database.Path).Success, "current parent preflight");
        database.Execute(mutation);

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Theory]
    [InlineData("DROP TABLE skill_invocation_snapshot_receipts;")]
    [InlineData("DROP TRIGGER skill_invocation_snapshot_rows_delete_rejected;")]
    [InlineData("DROP TRIGGER skill_invocation_snapshot_session_event_update_rejected;")]
    [InlineData("CREATE INDEX skill_invocation_snapshot_extra ON skill_invocation_snapshots(session_id);")]
    public void Partial_or_extended_component_is_rejected(string mutation)
    {
        using var database = new TestDatabase();
        database.InstallComponent();
        Assert.True(database.Service.PreflightForMigration(database.Path).Success, "current parent preflight");
        database.Execute(mutation);

        var result = database.Service.PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Fact]
    public void Present_empty_component_round_trips_through_create_and_restore()
    {
        using var database = new TestDatabase();
        database.InstallComponent();
        var bundle = System.IO.Path.Combine(database.Root, "present.zip");
        Assert.True(database.Service.CreateAndPublish(database.Path, bundle).Success);

        var target = System.IO.Path.Combine(database.Root, "restored-present.db");
        var restored = database.Service.Restore(bundle, target, new RuntimeRestoreOptions());

        Assert.True(restored.Success, restored.ErrorCode);
        using var connection = TestDatabase.OpenAt(target);
        Assert.Equal(1L, TestDatabase.ScalarLong(connection, "SELECT version FROM schema_version WHERE component='skill_invocation_snapshot';"));
        Assert.True(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));
        Assert.Equal(0L, TestDatabase.ScalarLong(connection, "SELECT COUNT(*) FROM skill_invocation_snapshots;"));
        Assert.Equal(0L, TestDatabase.ScalarLong(connection, "SELECT COUNT(*) FROM skill_invocation_snapshot_receipts;"));
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, null));
    }

    // The specification's "older backup" case: a bundle predating the component installs an empty
    // current component only after all parents, then restores no snapshot rows.
    [Fact]
    public void Absent_component_installs_empty_after_its_parents_and_restores_no_rows()
    {
        using var database = new TestDatabase();
        database.InstallComponent();
        var current = System.IO.Path.Combine(database.Root, "current.zip");
        Assert.True(database.Service.CreateAndPublish(database.Path, current).Success);
        var older = database.RewriteBundleWithoutSnapshotComponent(current, "older.zip");

        var inspected = database.Service.Inspect(older);
        var target = System.IO.Path.Combine(database.Root, "restored-absent.db");
        var restored = database.Service.Restore(older, target, new RuntimeRestoreOptions());

        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.DoesNotContain("skill_invocation_snapshot", inspected.ComponentVersions!.Keys);
        Assert.True(restored.Success, restored.ErrorCode);
        using var connection = TestDatabase.OpenAt(target);
        Assert.Equal(1L, TestDatabase.ScalarLong(connection, "SELECT version FROM schema_version WHERE component='skill_invocation_snapshot';"));
        Assert.True(SkillInvocationSnapshotSchemaV1Validator.IsValid(connection, null));
        Assert.Equal(0L, TestDatabase.ScalarLong(connection, "SELECT COUNT(*) FROM skill_invocation_snapshots;"));
        Assert.Equal(0L, TestDatabase.ScalarLong(connection, "SELECT COUNT(*) FROM skill_invocation_snapshot_receipts;"));
    }

    [Fact]
    public void Backup_validation_accepts_the_installed_empty_component()
    {
        using var database = new TestDatabase();
        database.InstallComponent();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.True(SkillInvocationSnapshotBackupValidation.IsValid(connection, transaction));

        transaction.Rollback();
    }

    private sealed class TestDatabase : IDisposable
    {
        internal TestDatabase()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"runtime-backup-skill-snapshot-{Guid.NewGuid():N}");
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

        internal SqliteConnection Open() => OpenAt(Path);

        internal static SqliteConnection OpenAt(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal static long ScalarLong(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal void InstallComponent()
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
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            LocalArchiveSchemaV1.Ensure(connection, transaction);
            SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        internal void DowngradeSessionToVersion13()
        {
            using var connection = Open();
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
        }

        internal void Execute(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal string RewriteBundleWithoutSnapshotComponent(string sourceBundle, string outputFileName)
        {
            var output = System.IO.Path.Combine(Root, outputFileName);
            byte[] manifest;
            byte[] database;
            using (var archive = ZipFile.OpenRead(sourceBundle))
            {
                manifest = Read(archive.GetEntry("manifest.json")!);
                database = Read(archive.GetEntry("database.sqlite")!);
            }

            var mutated = System.IO.Path.Combine(Root, $".older-{Guid.NewGuid():N}.sqlite");
            File.WriteAllBytes(mutated, database);
            using (var connection = OpenAt(mutated))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    "DELETE FROM schema_version WHERE component='skill_invocation_snapshot';"
                    + "DROP TRIGGER skill_invocation_snapshot_session_event_update_rejected;"
                    + "DROP TRIGGER skill_invocation_snapshot_session_event_delete_rejected;"
                    + "DROP TABLE skill_invocation_snapshot_receipts;"
                    + "DROP TABLE skill_invocation_snapshots;"
                    + "PRAGMA wal_checkpoint(TRUNCATE);";
                command.ExecuteNonQuery();
            }

            var parsed = RuntimeBackupJson.ParseManifest(manifest);
            var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var connection = OpenAt(mutated))
            {
                foreach (var table in parsed.RowCounts.Keys.Where(static table => table is not (
                    "skill_invocation_snapshots" or "skill_invocation_snapshot_receipts")))
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";";
                    rowCounts[table] = Convert.ToInt64(
                        command.ExecuteScalar(),
                        System.Globalization.CultureInfo.InvariantCulture);
                }
            }

            SqliteConnection.ClearAllPools();
            database = File.ReadAllBytes(mutated);
            File.Delete(mutated);
            File.Delete(mutated + "-wal");
            File.Delete(mutated + "-shm");
            var componentVersions = parsed.ComponentVersions.ToDictionary(
                static item => item.Key,
                static item => item.Value,
                StringComparer.Ordinal);
            componentVersions.Remove("skill_invocation_snapshot");
            manifest = RuntimeBackupJson.WriteManifest(parsed with
            {
                DatabaseSha256 = Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant(),
                DatabaseSize = database.LongLength,
                ComponentVersions = componentVersions,
                RowCounts = rowCounts,
            });

            using var target = ZipFile.Open(output, ZipArchiveMode.Create);
            Write(target, "manifest.json", manifest);
            Write(target, "database.sqlite", database);
            return output;
        }

        private static byte[] Read(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        private static void Write(ZipArchive archive, string name, byte[] content)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var stream = entry.Open();
            stream.Write(content);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

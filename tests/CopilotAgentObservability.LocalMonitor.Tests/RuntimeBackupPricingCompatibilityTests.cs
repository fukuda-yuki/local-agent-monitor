using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupPricingCompatibilityTests
{
    [Fact]
    public void Current_pricing_vector_is_accepted_without_migrations()
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 2);
        database.EnsureRuntimeBackupAndPricing();

        var result = new SqliteRuntimeBackupService(database.Clock)
            .PreflightForMigration(database.Path);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(2, result.ComponentVersions!["alert_engine"]);
        Assert.Equal(1, result.ComponentVersions["runtime_backup"]);
        Assert.Equal(1, result.ComponentVersions["pricing"]);
        Assert.DoesNotContain(result.MigrationSteps!, step =>
            step.StartsWith("alert_engine:", StringComparison.Ordinal)
            || step.StartsWith("runtime_backup:", StringComparison.Ordinal)
            || step.StartsWith("pricing:", StringComparison.Ordinal));
    }

    [Fact]
    public void Exact_legacy_session_13_pricing_parent_is_admitted_for_session_first_migration()
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 2);
        database.EnsureRuntimeBackupAndPricing();
        using (var connection = database.Open())
            SessionVersion13TestFixture.DowngradeSessionEvents(connection);
        database.Checkpoint();
        var before = File.ReadAllBytes(database.Path);

        var result = new SqliteRuntimeBackupService(database.Clock)
            .PreflightForMigration(database.Path);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(13, result.ComponentVersions!["session"]);
        Assert.Contains("session:13->14", result.MigrationSteps!);
        Assert.DoesNotContain(result.MigrationSteps!, step =>
            step.StartsWith("pricing:", StringComparison.Ordinal));
        Assert.Equal(before, File.ReadAllBytes(database.Path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    public void Pricing_parent_rejects_every_pre_13_session_stamp_without_mutation(int version)
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 2);
        database.EnsureRuntimeBackupAndPricing();
        using (var connection = database.Open())
        {
            database.Execute(
                connection,
                $"UPDATE schema_version SET version={version} WHERE component='session';");
            database.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = File.ReadAllBytes(database.Path);

        var result = new SqliteRuntimeBackupService(database.Clock)
            .PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(database.Path));
    }

    [Fact]
    public void P1_source_reports_alert_upgrade_then_fixed_runtime_backup_pricing_tail()
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 1);

        var result = new SqliteRuntimeBackupService(database.Clock)
            .PreflightForMigration(database.Path);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Contains("alert_engine:1->2", result.MigrationSteps!);
        Assert.Equal(
            [
                "historical_instruction_analysis:0->1",
                "historical_import:0->1",
                "sanitized_import:0->1",
                "runtime_backup:0->1",
                "pricing:0->1",
            ],
            result.MigrationSteps!.Where(step =>
                step.StartsWith("historical_instruction_analysis:", StringComparison.Ordinal)
                || step.StartsWith("historical_import:", StringComparison.Ordinal)
                || step.StartsWith("sanitized_import:", StringComparison.Ordinal)
                || step.StartsWith("runtime_backup:", StringComparison.Ordinal)
                || step.StartsWith("pricing:", StringComparison.Ordinal)));
    }

    [Fact]
    public void InitializeExecutesEveryMissingComponentInRegisteredOrder()
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 1);
        var migrated = new List<string>();
        var service = new SqliteRuntimeBackupService(database.Clock, checkpoint =>
        {
            const string prefix = "component-migration:";
            if (checkpoint.StartsWith(prefix, StringComparison.Ordinal))
                migrated.Add(checkpoint[prefix.Length..]);
        });

        var result = service.Initialize(database.Path);

        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(
        [
            "local_repository_catalog",
            "local_archive",
            "skill_projection",
            "skill_invocation_snapshot",
            "local_workspace_projection",
            "doctor",
            "alert_engine",
            "alert_lifecycle",
            "first_trace_navigation",
            "historical_instruction_analysis",
            "historical_import",
            "sanitized_import",
            "runtime_backup",
            "pricing",
        ], migrated);
    }

    [Fact]
    public void InitializeRollsBackAlertUpgradeAndEveryMissingComponentWhenFinalMigrationFails()
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 1);
        database.Checkpoint();
        var before = File.ReadAllBytes(database.Path);
        var service = new SqliteRuntimeBackupService(database.Clock, checkpoint =>
        {
            if (checkpoint == "component-migration:pricing")
                throw new InvalidOperationException("synthetic migration failure");
        });

        var result = service.Initialize(database.Path);
        database.Checkpoint();

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(database.Path));
        using var connection = database.Open(readOnly: true);
        Assert.Equal(1L, database.Scalar(connection,
            "SELECT version FROM schema_version WHERE component='alert_engine';"));
        Assert.Equal(0L, database.Scalar(connection,
            "SELECT COUNT(*) FROM schema_version WHERE component IN " +
            "('local_repository_catalog','local_archive','skill_projection','skill_invocation_snapshot'," +
            "'local_workspace_projection','doctor','alert_lifecycle','first_trace_navigation'," +
            "'historical_instruction_analysis','historical_import','sanitized_import','runtime_backup','pricing');"));
    }

    [Fact]
    public void Backup_restore_preserves_pricing_catalog_canonical_bytes()
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 2);
        database.EnsureRuntimeBackupAndPricing();
        var catalog = PricingCatalog.Create(BundledPricingRegistry.Load());
        var canonicalBytes = PricingCanonicalJson.SerializeCatalogSnapshot(catalog);
        var store = new SqlitePricingStore(database.Path, database.Clock);
        Assert.Equal(PricingStoreStatus.Success, store.PutCatalogSnapshot(canonicalBytes).Status);
        database.Checkpoint();
        var bundle = System.IO.Path.Combine(database.Root, "pricing.backup.zip");
        var target = System.IO.Path.Combine(database.Root, "restored.db");
        var service = new SqliteRuntimeBackupService(database.Clock);

        var created = service.CreateAndPublish(database.Path, bundle);
        var restored = service.Restore(bundle, target, new RuntimeRestoreOptions());
        var restoredCatalog = new SqlitePricingStore(target, database.Clock)
            .GetCatalogSnapshot(catalog.CatalogSha256);

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.NotNull(restoredCatalog);
        Assert.Equal(canonicalBytes, restoredCatalog.CanonicalBytes);
        using var connection = database.Open(target, readOnly: true);
        Assert.Equal(2L, database.Scalar(connection,
            "SELECT version FROM schema_version WHERE component='alert_engine';"));
        Assert.Equal(1L, database.Scalar(connection,
            "SELECT version FROM schema_version WHERE component='runtime_backup';"));
        Assert.Equal(1L, database.Scalar(connection,
            "SELECT version FROM schema_version WHERE component='pricing';"));
    }

    [Theory]
    [InlineData("future_pricing")]
    [InlineData("missing_runtime_backup")]
    [InlineData("missing_alert_parent")]
    [InlineData("missing_session_parent")]
    [InlineData("extra_pricing_object")]
    public void Pricing_component_forgeries_fail_closed_without_mutation(string caseName)
    {
        using var database = new PricingBackupDatabase();
        database.CreateCore(alertVersion: 2);
        database.EnsureRuntimeBackupAndPricing();
        using (var connection = database.Open())
        {
            database.Execute(connection, "PRAGMA foreign_keys=OFF;");
            database.Execute(connection, caseName switch
            {
                "future_pricing" =>
                    "UPDATE schema_version SET version=2 WHERE component='pricing';",
                "missing_runtime_backup" =>
                    "DELETE FROM schema_version WHERE component='runtime_backup';",
                "missing_alert_parent" =>
                    "DELETE FROM schema_version WHERE component='alert_engine';",
                "missing_session_parent" =>
                    "DELETE FROM schema_version WHERE component='session';",
                "extra_pricing_object" =>
                    "CREATE TABLE pricing_private_locator(value TEXT);",
                _ => throw new InvalidOperationException(),
            });
            database.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = File.ReadAllBytes(database.Path);

        var result = new SqliteRuntimeBackupService(database.Clock)
            .PreflightForMigration(database.Path);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(database.Path));
    }

    [Fact]
    public void Monitor_terminal_ensure_rejects_forged_session_parent_without_partial_tail()
    {
        using var database = new PricingBackupDatabase();
        database.CreateForgedSessionVersionOnly();
        var service = new SqliteRuntimeBackupService(database.Clock);
        var initialization = service.InitializeForMonitor(database.Path);
        Assert.True(initialization.Result.Success, initialization.Result.ErrorCode);
        using var lease = initialization.Lease!;

        var completed = service.CompleteMonitorInitialization(lease);

        Assert.False(completed.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, completed.ErrorCode);
        using var connection = database.Open(readOnly: true);
        Assert.Equal(0L, database.Scalar(connection,
            "SELECT COUNT(*) FROM schema_version WHERE component IN ('alert_engine','runtime_backup','pricing');"));
    }

    private sealed class PricingBackupDatabase : IDisposable
    {
        internal PricingBackupDatabase()
        {
            Root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"runtime-backup-pricing-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Path = System.IO.Path.Combine(Root, "monitor.db");
            Clock = new FixedTimeProvider(
                new DateTimeOffset(2026, 7, 24, 1, 2, 3, TimeSpan.Zero));
        }

        internal string Root { get; }
        internal string Path { get; }
        internal TimeProvider Clock { get; }

        internal void CreateCore(int alertVersion)
        {
            using (var connection = Open())
            {
                Execute(connection, "PRAGMA journal_mode=WAL;");
                using (var transaction = connection.BeginTransaction())
                {
                    MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                    transaction.Commit();
                }
                using (var transaction = connection.BeginTransaction())
                {
                    RetentionSchemaMigrator.Apply(connection, transaction);
                    transaction.Commit();
                }
            }
            new SqliteSessionStore(Path).CreateSchema();
            var alertStore = new SqliteAlertEngineStore(
                new SqliteConnectionStringBuilder
                {
                    DataSource = Path,
                    Pooling = false,
                }.ToString());
            if (alertVersion == 1)
                Assert.Equal(AlertStoreStatus.Success, alertStore.Initialize().Status);
            else
                Assert.Equal(AlertEngineStoreStatusV2.Success, alertStore.InitializeV2().Status);
        }

        internal void CreateForgedSessionVersionOnly()
        {
            using var connection = Open();
            using (var transaction = connection.BeginTransaction())
            {
                MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                transaction.Commit();
            }
            using (var transaction = connection.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(connection, transaction);
                transaction.Commit();
            }
            Execute(connection,
                "INSERT INTO schema_version(component,version) VALUES('session',13);");
        }

        internal void EnsureRuntimeBackupAndPricing()
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            RuntimeBackupSchemaV1.Ensure(connection, transaction);
            PricingSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
        }

        internal void Checkpoint()
        {
            using var connection = Open();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal SqliteConnection Open(string? path = null, bool readOnly = false)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path ?? Path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                ForeignKeys = true,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal long Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

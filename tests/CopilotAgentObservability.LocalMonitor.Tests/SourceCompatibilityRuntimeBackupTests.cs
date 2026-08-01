using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceCompatibilityRuntimeBackupTests
{
    [Fact]
    public void NegativeRawReferenceArchive_IsRestoreIncompatibleWithoutDestinationMutation()
    {
        using var files = new TemporaryFiles();
        CreateCurrentDatabase(files.Source, "source-observation");
        var service = new SqliteRuntimeBackupService();
        var created = service.CreateAndPublish(files.Source, files.ValidBundle);
        Assert.True(created.Success, created.ErrorCode);
        CreateNegativeRawReferenceArchive(files.ValidBundle, files.InvalidBundle, files.MutatedDatabase);
        CreateCurrentDatabase(files.Target, "target-observation");
        var before = CanonicalDatabaseHash(files.Target);

        var inspection = service.Inspect(files.InvalidBundle);
        var preview = service.Preview(files.InvalidBundle, files.Target);
        var restore = service.Restore(
            files.InvalidBundle,
            files.Target,
            new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, CanonicalDatabaseHash(files.Target));
    }

    private static void CreateCurrentDatabase(string path, string observationId)
    {
        new SqliteSourceCompatibilityStore(path).CreateSchema();
        using (var connection = Open(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO source_schema_observations(
                    observation_id,compatibility_state,reason_code,next_action,
                    capture_content_state,unknown_span_count,unknown_event_count,
                    unknown_attribute_count,overflow_distinct_count,overflow_occurrence_count,
                    observed_at)
                VALUES(
                    $observation_id,'supported',NULL,'none','available',0,0,0,0,0,
                    '2026-07-31T00:00:00.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$observation_id", observationId);
            command.ExecuteNonQuery();
        }
        using (var connection = Open(path))
        using (var transaction = connection.BeginTransaction())
        {
            RetentionSchemaMigrator.Apply(connection, transaction);
            transaction.Commit();
        }
        new RetentionCatalogStore(
                RetentionCatalogContext.AdoptExistingCatalogV1(path))
            .CreateSchema();
    }

    private static void CreateNegativeRawReferenceArchive(
        string validBundle,
        string invalidBundle,
        string mutatedDatabase)
    {
        byte[] manifest;
        byte[] database;
        using (var archive = ZipFile.OpenRead(validBundle))
        {
            manifest = Read(archive.GetEntry("manifest.json")!);
            database = Read(archive.GetEntry("database.sqlite")!);
        }
        File.WriteAllBytes(mutatedDatabase, database);
        using (var connection = Open(mutatedDatabase))
        {
            Execute(
                connection,
                """
                PRAGMA ignore_check_constraints=ON;
                UPDATE source_schema_observations SET raw_record_id=-1;
                PRAGMA ignore_check_constraints=OFF;
                PRAGMA wal_checkpoint(TRUNCATE);
                PRAGMA journal_mode=DELETE;
                """);
        }
        database = File.ReadAllBytes(mutatedDatabase);
        manifest = ReplaceDatabaseHash(manifest, database);
        using var target = ZipFile.Open(invalidBundle, ZipArchiveMode.Create);
        Write(target, "manifest.json", manifest);
        Write(target, "database.sqlite", database);
    }

    private static byte[] CanonicalDatabaseHash(string path)
    {
        using (var connection = Open(path))
        {
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "PRAGMA journal_mode=DELETE;");
        }
        return SHA256.HashData(File.ReadAllBytes(path));
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static byte[] ReplaceDatabaseHash(byte[] manifest, byte[] database)
    {
        using var document = JsonDocument.Parse(manifest);
        var oldHash = document.RootElement
            .GetProperty("snapshot")
            .GetProperty("snapshot_id")
            .GetString()!;
        var oldSize = document.RootElement
            .GetProperty("files")[0]
            .GetProperty("size")
            .GetInt64();
        var newHash = Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant();
        var json = Encoding.UTF8.GetString(manifest)
            .Replace(oldHash, newHash, StringComparison.Ordinal)
            .Replace(
                $"\"size\":{oldSize}",
                $"\"size\":{database.LongLength}",
                StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(json);
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryFiles : IDisposable
    {
        public TemporaryFiles()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"source-compatibility-backup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "source.sqlite");
            Target = Path.Combine(Root, "target.sqlite");
            MutatedDatabase = Path.Combine(Root, "mutated.sqlite");
            ValidBundle = Path.Combine(Root, "valid.zip");
            InvalidBundle = Path.Combine(Root, "negative-raw-reference.zip");
        }

        public string Root { get; }
        public string Source { get; }
        public string Target { get; }
        public string MutatedDatabase { get; }
        public string ValidBundle { get; }
        public string InvalidBundle { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Root, recursive: true);
        }
    }
}

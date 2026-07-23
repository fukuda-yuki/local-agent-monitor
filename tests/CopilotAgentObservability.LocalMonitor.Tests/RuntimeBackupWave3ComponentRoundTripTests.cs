using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.SanitizedExport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupWave3ComponentRoundTripTests
{
    private static readonly string[] ComponentPrefixes =
    [
        "historical_instruction_analysis_",
        "historical_import_",
        "sanitized_import_",
    ];

    [Fact]
    public void Backup_and_restore_preserve_non_empty_wave_3_component_data_exactly()
    {
        using var temp = new RoundTripTemp();
        temp.CreateBaseDatabase();
        var historicalRunId = temp.SeedHistoricalInstructionAnalysis();
        temp.SeedHistoricalImportPreview();
        var sanitizedImportId = temp.SeedSanitizedImport();
        temp.Checkpoint();
        var expected = ReadSnapshots(temp.Source);
        var service = new SqliteRuntimeBackupService(temp.Clock);

        var created = service.CreateAndPublish(temp.Source, temp.Bundle);
        var restored = service.Restore(temp.Bundle, temp.Target, new RuntimeRestoreOptions());
        var preflight = service.PreflightForMigration(temp.Target);
        var actual = ReadSnapshots(temp.Target);

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.True(preflight.Success, Describe(preflight));
        Assert.Equal(1, preflight.ComponentVersions!["historical_instruction_analysis"]);
        Assert.Equal(1, preflight.ComponentVersions["historical_import"]);
        Assert.Equal(1, preflight.ComponentVersions["sanitized_import"]);
        Assert.Empty(preflight.MigrationSteps!);
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var prefix in ComponentPrefixes)
        {
            Assert.True(expected[prefix].RowCounts.Values.Sum() > 0, $"{prefix} must contain owned rows.");
            Assert.Equal(expected[prefix].RowCounts, actual[prefix].RowCounts);
            Assert.Equal(expected[prefix].Digest, actual[prefix].Digest);
        }

        var analysis = new SqliteHistoricalInstructionAnalysisStoreV1(temp.Target).Get(historicalRunId);
        Assert.NotNull(analysis);
        Assert.Equal("historical-extraction-00000000000000000000000000000000", analysis.Request.ExtractionId);
        Assert.Equal("hip_runtime_backup_round_trip", Text(temp.Target,
            "SELECT preview_id FROM historical_import_previews;"));
        var sanitizedHistory = new SqliteSanitizedImportStore(temp.Target, temp.Clock).GetHistory(sanitizedImportId);
        Assert.NotNull(sanitizedHistory);
        Assert.Equal(sanitizedImportId, sanitizedHistory.ImportId);
    }

    private static IReadOnlyDictionary<string, ComponentSnapshot> ReadSnapshots(string databasePath) =>
        ComponentPrefixes.ToDictionary(prefix => prefix, prefix => ReadSnapshot(databasePath, prefix), StringComparer.Ordinal);

    private static ComponentSnapshot ReadSnapshot(string databasePath, string prefix)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        using var tablesCommand = connection.CreateCommand();
        tablesCommand.CommandText =
            "SELECT name,sql FROM sqlite_schema WHERE type='table' AND name GLOB $pattern ORDER BY name;";
        tablesCommand.Parameters.AddWithValue("$pattern", prefix + "*");
        var tables = new List<(string Name, string Sql)>();
        using (var reader = tablesCommand.ExecuteReader())
            while (reader.Read()) tables.Add((reader.GetString(0), reader.GetString(1)));

        var canonical = new MemoryStream();
        var rowCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            Append(canonical, table.Name);
            Append(canonical, table.Sql);
            var columns = ReadColumns(connection, table.Name);
            foreach (var column in columns) Append(canonical, column);
            using var command = connection.CreateCommand();
            var quotedTable = Quote(table.Name);
            command.CommandText = $"SELECT * FROM {quotedTable} ORDER BY {string.Join(',', columns.Select(Quote))};";
            using var reader = command.ExecuteReader();
            long rowCount = 0;
            while (reader.Read())
            {
                rowCount++;
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                    Append(canonical, reader.GetValue(ordinal));
            }
            rowCounts.Add(table.Name, rowCount);
        }

        return new(rowCounts, Convert.ToHexString(SHA256.HashData(canonical.ToArray())).ToLowerInvariant());
    }

    private static string[] ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({Quote(table)});";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(1));
        return columns.ToArray();
    }

    private static void Append(Stream destination, object value)
    {
        var (kind, bytes) = value switch
        {
            DBNull => ("null", Array.Empty<byte>()),
            byte[] blob => ("blob", blob),
            long number => ("integer", Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture))),
            double number => ("real", Encoding.UTF8.GetBytes(number.ToString("R", CultureInfo.InvariantCulture))),
            string text => ("text", Encoding.UTF8.GetBytes(text)),
            _ => throw new InvalidOperationException($"Unexpected SQLite value type {value.GetType().FullName}."),
        };
        var header = Encoding.UTF8.GetBytes($"{kind}:{bytes.Length}:");
        destination.Write(header);
        destination.Write(bytes);
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string Text(string databasePath, string sql)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static string Describe(RuntimeBackupPreflightResult result) =>
        $"{result.ErrorCode}; components={string.Join(',', result.ComponentVersions?.Select(item => $"{item.Key}:{item.Value}") ?? [])}; migrations={string.Join(',', result.MigrationSteps ?? [])}";

    private sealed class RoundTripTemp : IDisposable
    {
        internal RoundTripTemp()
        {
            Root = Path.Combine(Path.GetTempPath(), $"runtime-backup-wave3-round-trip-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "source.db");
            Target = Path.Combine(Root, "restored.db");
            Bundle = Path.Combine(Root, "wave3-components.backup.zip");
            Clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 3, 4, 5, TimeSpan.Zero));
        }

        internal string Root { get; }
        internal string Source { get; }
        internal string Target { get; }
        internal string Bundle { get; }
        internal TimeProvider Clock { get; }

        internal void CreateBaseDatabase()
        {
            using var connection = Open(Source);
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

        internal long SeedHistoricalInstructionAnalysis()
        {
            var store = new SqliteHistoricalInstructionAnalysisStoreV1(Source);
            store.CreateSchema();
            return store.Start(
                new(
                    HistoricalInstructionAnalysisContractsV1.RequestSchemaVersion,
                    "historical-extraction-00000000000000000000000000000000",
                    new string('a', 64),
                    "gpt-5",
                    "copilot",
                    new string('b', 64),
                    30_000,
                    HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion),
                new(
                    TruncatedBefore: false,
                    SanitizedOnly: true,
                    ContentAvailable: false,
                    new HistoricalEvidenceDistributionV1(
                        [new HistoricalDistributionCountV1("partial", 1)],
                        [new HistoricalDistributionCountV1("historical", 1)],
                        [new HistoricalDistributionCountV1("metadata_only", 1)])),
                Clock.GetUtcNow());
        }

        internal void SeedHistoricalImportPreview()
        {
            new SqliteHistoricalImportStore(Source).CreateSchema();
            using var connection = Open(Source);
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO historical_import_previews(
                    preview_id,preview_digest,snapshot_version,snapshot_digest,source_selection_id,
                    private_selection_json,probe_json,candidate_batch_json,preview_json,eligible,expires_at,created_at)
                VALUES(
                    'hip_runtime_backup_round_trip',$preview_digest,'hsv_1',$snapshot_digest,'hss_runtime_backup_round_trip',
                    NULL,NULL,NULL,'{"schema_version":"historical-import-workflow-preview/v1","result":"metadata_only"}',0,
                    '2026-07-23T03:09:05.0000000+00:00','2026-07-23T03:04:05.0000000+00:00');
                """;
            command.Parameters.AddWithValue("$preview_digest", new string('c', 64));
            command.Parameters.AddWithValue("$snapshot_digest", new string('d', 64));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        internal string SeedSanitizedImport()
        {
            var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
            const string recordId = "runtime-backup-synthetic-record";
            var canonicalBytes = RepositoryMetadataProjectionV1.Serialize(
                recordId, recordId, "trace-synthetic", "github-copilot-cli", "synthetic-repository",
                "synthetic-workspace", "synthetic-snapshot", observedAt, "partial", "not_captured", "retained_by_policy");
            var record = new SanitizedExportRecord(
                $"repository-metadata/{recordId}.json", "repository_metadata_projection", recordId,
                recordId, "trace-synthetic", "github-copilot-cli", "synthetic-repository", "synthetic-workspace",
                "synthetic-snapshot", observedAt, canonicalBytes, [], "partial", "not_captured", "retained_by_policy");
            var snapshot = new SanitizedExportSourceSnapshot(
                "synthetic-runtime-backup-snapshot", "local-monitor-test", [new("github-copilot-cli", "1.0.73")],
                [record], new("missing", "missing", "unavailable", "unavailable", "unavailable"));
            var archive = new SanitizedExportService().Create(new(observedAt, snapshot, new()));
            Assert.True(archive.Success, archive.ErrorCode);
            var store = new SqliteSanitizedImportStore(Source, Clock);
            store.CreateSchema();
            var preview = store.Preview(archive.ArchiveBytes!);
            Assert.True(preview.Success, preview.ErrorCode);
            var result = store.Commit(archive.ArchiveBytes!, preview.PreviewDigest!);
            Assert.True(result.Success, result.ErrorCode);
            return result.ImportId!;
        }

        internal void Checkpoint()
        {
            using var connection = Open(Source);
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record ComponentSnapshot(
        IReadOnlyDictionary<string, long> RowCounts,
        string Digest);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

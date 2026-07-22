using System.Text.Json;
using CopilotAgentObservability.SanitizedImport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SanitizedImportCliTests
{
    [Fact]
    public void PreviewImportReplayAndHistoryUseSharedContracts()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "monitor.db");
        var bundle = GoldenBundlePath();

        var preview = Run(["sanitized-import", "preview", "--bundle", bundle, "--database", database]);
        Assert.Equal(0, preview.ExitCode);
        Assert.Equal(string.Empty, preview.Error);
        using var previewJson = JsonDocument.Parse(preview.Output);
        Assert.Equal(SanitizedImportContractVersions.Preview, previewJson.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(1, previewJson.RootElement.GetProperty("eligible_records").GetInt32());
        Assert.Equal(1, previewJson.RootElement.GetProperty("new_records").GetInt32());
        Assert.Equal(0, previewJson.RootElement.GetProperty("updated_records").GetInt32());
        Assert.Equal(0, previewJson.RootElement.GetProperty("skipped_records").GetInt32());
        Assert.Equal(0, previewJson.RootElement.GetProperty("rejected_records").GetInt32());
        Assert.Equal(0, previewJson.RootElement.GetProperty("graph_state_updates").GetInt32());
        Assert.Equal(0, previewJson.RootElement.GetProperty("expected_changes").GetProperty("graph_state_updates").GetInt32());
        var digest = previewJson.RootElement.GetProperty("preview_digest").GetString()!;

        var first = Run(["sanitized-import", "import", "--database", database, "--preview-digest", digest, "--bundle", bundle]);
        var replay = Run(["sanitized-import", "import", "--bundle", bundle, "--database", database, "--preview-digest", digest]);
        var history = Run(["sanitized-import", "history", "--limit", "1", "--database", database]);

        Assert.Equal(0, first.ExitCode);
        Assert.Equal(0, replay.ExitCode);
        Assert.Equal(0, history.ExitCode);
        Assert.All(new[] { first.Error, replay.Error, history.Error }, Assert.Empty);
        using var firstJson = JsonDocument.Parse(first.Output);
        using var replayJson = JsonDocument.Parse(replay.Output);
        using var historyJson = JsonDocument.Parse(history.Output);
        Assert.False(firstJson.RootElement.GetProperty("idempotent_replay").GetBoolean());
        Assert.True(replayJson.RootElement.GetProperty("idempotent_replay").GetBoolean());
        Assert.Equal(1, firstJson.RootElement.GetProperty("eligible_records").GetInt32());
        Assert.Equal(0, firstJson.RootElement.GetProperty("graph_state_updates").GetInt32());
        Assert.Equal(SanitizedImportContractVersions.History, historyJson.RootElement.GetProperty("schema_version").GetString());
        var historyItem = Assert.Single(historyJson.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(1, historyItem.GetProperty("eligible_records").GetInt32());
        Assert.Equal(0, historyItem.GetProperty("graph_state_updates").GetInt32());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("1.0")]
    public void HistoryRejectsInvalidLimitWithoutCreatingDatabase(string limit)
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "monitor.db");

        var result = Run(["sanitized-import", "history", "--database", database, "--limit", limit]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("invalid_arguments" + Environment.NewLine, result.Error);
        Assert.False(File.Exists(database));
    }

    [Fact]
    public void ImportRejectsInvalidDigestWithSharedResultAndValidationExit()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "monitor.db");

        var result = Run(["sanitized-import", "import", "--database", database,
            "--bundle", GoldenBundlePath(), "--preview-digest", "not-a-digest"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("preview_digest_invalid" + Environment.NewLine, result.Error);
        using var json = JsonDocument.Parse(result.Output);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("preview_digest_invalid", json.RootElement.GetProperty("error_code").GetString());
        Assert.False(File.Exists(database));
    }

    [Fact]
    public void ImportValidShapeStaleDigestLeavesFreshTargetWithoutImportSchema()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "monitor.db");

        var result = Run(["sanitized-import", "import", "--database", database,
            "--bundle", GoldenBundlePath(), "--preview-digest", new string('b', 64)]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("preview_changed" + Environment.NewLine, result.Error);
        Assert.Equal(0L, Scalar(database, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name='schema_version' OR name LIKE 'sanitized_import_%' OR tbl_name LIKE 'sanitized_import_%';
            """));
    }

    [Fact]
    public void ImportForeignKeyFailureRollsBackImportSchemaAndPreservesBaseVector()
    {
        using var temp = new TempDirectory();
        var previewDatabase = Path.Combine(temp.Path, "preview.db");
        var database = Path.Combine(temp.Path, "monitor.db");
        var preview = Run(["sanitized-import", "preview", "--database", previewDatabase,
            "--bundle", GoldenBundlePath()]);
        using var previewJson = JsonDocument.Parse(preview.Output);
        var digest = previewJson.RootElement.GetProperty("preview_digest").GetString()!;
        Execute(database, """
            PRAGMA foreign_keys=OFF;
            CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);
            INSERT INTO schema_version VALUES('monitor',7),('session',13);
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            CREATE TABLE child(parent_id INTEGER NOT NULL REFERENCES parent(id));
            INSERT INTO child VALUES(42);
            CREATE TABLE kept(value TEXT NOT NULL);
            INSERT INTO kept VALUES('same');
            """);

        var result = Run(["sanitized-import", "import", "--database", database,
            "--bundle", GoldenBundlePath(), "--preview-digest", digest]);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("import_integrity_failed" + Environment.NewLine, result.Error);
        Assert.Equal(0L, Scalar(database,
            "SELECT COUNT(*) FROM schema_version WHERE component='sanitized_import';"));
        Assert.Equal(0L, Scalar(database, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name LIKE 'sanitized_import_%' OR tbl_name LIKE 'sanitized_import_%';
            """));
        Assert.Equal(2L, Scalar(database, "SELECT COUNT(*) FROM schema_version;"));
        Assert.Equal(1L, Scalar(database, "SELECT COUNT(*) FROM kept WHERE value='same';"));
    }

    [Fact]
    public void ReplayIntegrityFailureUsesStoreUnavailableExitWithoutRepair()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "monitor.db");
        var bundle = GoldenBundlePath();
        var preview = Run(["sanitized-import", "preview", "--database", database, "--bundle", bundle]);
        using var previewJson = JsonDocument.Parse(preview.Output);
        var digest = previewJson.RootElement.GetProperty("preview_digest").GetString()!;
        Assert.Equal(0, Run(["sanitized-import", "import", "--database", database,
            "--bundle", bundle, "--preview-digest", digest]).ExitCode);
        Execute(database, "DELETE FROM sanitized_import_graph_edges;");

        var corruptPreview = Run(["sanitized-import", "preview", "--database", database, "--bundle", bundle]);
        var replay = Run(["sanitized-import", "import", "--database", database,
            "--bundle", bundle, "--preview-digest", digest]);

        Assert.Equal(3, corruptPreview.ExitCode);
        Assert.Equal("import_integrity_failed" + Environment.NewLine, corruptPreview.Error);
        Assert.Equal(3, replay.ExitCode);
        Assert.Equal("import_integrity_failed" + Environment.NewLine, replay.Error);
        using var replayJson = JsonDocument.Parse(replay.Output);
        Assert.False(replayJson.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("import_integrity_failed", replayJson.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(0L, Scalar(database, "SELECT COUNT(*) FROM sanitized_import_graph_edges;"));
    }

    [Fact]
    public void PreviewOwnedForeignKeyCorruptionUsesIntegrityFailureJson()
    {
        using var temp = new TempDirectory();
        var database = Path.Combine(temp.Path, "monitor.db");
        var bundle = GoldenBundlePath();
        var preview = Run(["sanitized-import", "preview", "--database", database, "--bundle", bundle]);
        using var previewJson = JsonDocument.Parse(preview.Output);
        var digest = previewJson.RootElement.GetProperty("preview_digest").GetString()!;
        Assert.Equal(0, Run(["sanitized-import", "import", "--database", database,
            "--bundle", bundle, "--preview-digest", digest]).ExitCode);
        Execute(database, $"PRAGMA foreign_keys=OFF; UPDATE sanitized_import_records SET first_import_id='{new string('b', 64)}';");

        var corruptPreview = Run(["sanitized-import", "preview", "--database", database, "--bundle", bundle]);

        Assert.Equal(3, corruptPreview.ExitCode);
        Assert.Equal("import_integrity_failed" + Environment.NewLine, corruptPreview.Error);
        using var result = JsonDocument.Parse(corruptPreview.Output);
        Assert.False(result.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("import_integrity_failed", result.RootElement.GetProperty("error_code").GetString());
        Assert.Equal(new string('b', 64), Text(database,
            "SELECT first_import_id FROM sanitized_import_records LIMIT 1;"));
    }

    [Fact]
    public void PreviewMapsArchiveAndIoFailuresWithoutEchoingPaths()
    {
        using var temp = new TempDirectory();
        var invalidBundle = Path.Combine(temp.Path, "invalid.zip");
        File.WriteAllBytes(invalidBundle, [1, 2, 3]);
        var database = Path.Combine(temp.Path, "monitor.db");

        var invalid = Run(["sanitized-import", "preview", "--database", database, "--bundle", invalidBundle]);
        Assert.False(File.Exists(database));
        var missing = Run(["sanitized-import", "preview", "--database", database, "--bundle", Path.Combine(temp.Path, "missing.zip")]);

        Assert.Equal(2, invalid.ExitCode);
        Assert.Equal("archive_invalid" + Environment.NewLine, invalid.Error);
        Assert.DoesNotContain(temp.Path, invalid.Output + invalid.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, missing.ExitCode);
        Assert.Equal(string.Empty, missing.Output);
        Assert.Equal("io_failed" + Environment.NewLine, missing.Error);
        Assert.DoesNotContain(temp.Path, missing.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidationErrorsPrecedeUnavailableDatabaseWithoutMutation()
    {
        using var temp = new TempDirectory();
        var invalidBundle = Path.Combine(temp.Path, "invalid.zip");
        File.WriteAllBytes(invalidBundle, [1, 2, 3]);

        var preview = Run(["sanitized-import", "preview", "--database", temp.Path, "--bundle", invalidBundle]);
        var import = Run(["sanitized-import", "import", "--database", temp.Path,
            "--bundle", GoldenBundlePath(), "--preview-digest", "invalid"]);

        Assert.Equal(2, preview.ExitCode);
        Assert.Equal("archive_invalid" + Environment.NewLine, preview.Error);
        Assert.Equal(2, import.ExitCode);
        Assert.Equal("preview_digest_invalid" + Environment.NewLine, import.Error);
    }

    [Fact]
    public void HelpListsAllSanitizedImportCommands()
    {
        var result = Run(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("sanitized-import preview --database <monitor.db> --bundle <bundle.zip>", result.Output, StringComparison.Ordinal);
        Assert.Contains("sanitized-import import --database <monitor.db> --bundle <bundle.zip> --preview-digest <sha256>", result.Output, StringComparison.Ordinal);
        Assert.Contains("sanitized-import history --database <monitor.db> [--limit <1..100>]", result.Output, StringComparison.Ordinal);
    }

    private static string GoldenBundlePath() => Path.Combine(
        FindRepositoryRoot(), "tests", "CopilotAgentObservability.LocalMonitor.Tests",
        "TestData", "SanitizedExport", "sanitized-evidence.v1.zip");

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException();
    }

    private static (int ExitCode, string Output, string Error) Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (CliApplication.Run(args, output, error), output.ToString(), error.ToString());
    }

    private static void Execute(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Text(string database, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={database};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar())!;
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sanitized-import-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

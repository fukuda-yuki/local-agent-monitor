using System.Text.Json;
using CopilotAgentObservability.SanitizedImport;

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
        Assert.Equal(SanitizedImportContractVersions.History, historyJson.RootElement.GetProperty("schema_version").GetString());
        Assert.Single(historyJson.RootElement.GetProperty("items").EnumerateArray());
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

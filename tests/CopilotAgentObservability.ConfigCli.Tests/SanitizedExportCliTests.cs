using System.Text;
using System.Text.Json;
using CopilotAgentObservability.SanitizedExport;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SanitizedExportCliTests
{
    [Fact]
    public void PreviewExportAndResultProjectOneSharedContract()
    {
        using var temp = new TempDirectory();
        var requestPath = Path.Combine(temp.Path, "request.json");
        var archivePath = Path.Combine(temp.Path, "bundle.zip");
        File.WriteAllBytes(requestPath, SanitizedExportJson.SerializeRequest(Request()));

        var preview = Run(["sanitized-export", "preview", "--request", requestPath]);
        Assert.Equal(0, preview.ExitCode);
        Assert.Equal(string.Empty, preview.Error);
        using (var json = JsonDocument.Parse(preview.Output))
            Assert.Equal("sanitized-evidence", json.RootElement.GetProperty("bundle_profile").GetString());

        var export = Run(["sanitized-export", "export", "--request", requestPath, "--output", archivePath]);
        Assert.Equal(0, export.ExitCode);
        Assert.True(File.Exists(archivePath));
        using var created = JsonDocument.Parse(export.Output);

        var result = Run(["sanitized-export", "result", "--bundle", archivePath]);
        Assert.Equal(0, result.ExitCode);
        using var inspected = JsonDocument.Parse(result.Output);
        Assert.Equal(created.RootElement.GetProperty("archive_sha256").GetString(), inspected.RootElement.GetProperty("archive_sha256").GetString());
        Assert.Equal("sanitized-evidence-bundle.v1", inspected.RootElement.GetProperty("bundle_schema_version").GetString());
        Assert.Equal("sanitized-evidence", inspected.RootElement.GetProperty("bundle_profile").GetString());
        Assert.Equal(1, inspected.RootElement.GetProperty("record_count").GetInt32());
    }

    private static SanitizedExportRequest Request()
    {
        var time = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        return new(
            time,
            new("snapshot-cli-85", "local-monitor-test", [],
                [new("sessions/session-a.json", "session_projection", "session-a", "session-a", "trace-a", "github-copilot-cli", null, null, null, time, Encoding.UTF8.GetBytes("{\"schema_version\":\"session-workspace.v1\"}"), [])],
                new("missing", "missing", "unavailable", "unavailable", "unavailable")),
            new(SessionIds: ["session-a"]),
            []);
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
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sanitized-export-cli-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

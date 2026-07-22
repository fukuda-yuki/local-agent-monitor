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
        File.WriteAllBytes(requestPath, SanitizedExportJson.SerializeControlRequest(Control()));
        var provider = new Provider(Snapshot());

        var preview = Run(["preview", "--database", Path.Combine(temp.Path, "monitor.db"), "--request", requestPath], provider);
        Assert.Equal(0, preview.ExitCode);
        Assert.Equal(string.Empty, preview.Error);
        using (var json = JsonDocument.Parse(preview.Output))
            Assert.Equal("sanitized-evidence", json.RootElement.GetProperty("bundle_profile").GetString());

        var export = Run(["export", "--database", Path.Combine(temp.Path, "monitor.db"), "--request", requestPath, "--output", archivePath], provider);
        Assert.Equal(0, export.ExitCode);
        Assert.True(File.Exists(archivePath));
        using var created = JsonDocument.Parse(export.Output);

        var result = Run(["result", "--bundle", archivePath], provider);
        Assert.Equal(0, result.ExitCode);
        using var inspected = JsonDocument.Parse(result.Output);
        Assert.Equal(created.RootElement.GetProperty("archive_sha256").GetString(), inspected.RootElement.GetProperty("archive_sha256").GetString());
        Assert.Equal("sanitized-evidence-bundle.v1", inspected.RootElement.GetProperty("bundle_schema_version").GetString());
        Assert.Equal("sanitized-evidence", inspected.RootElement.GetProperty("bundle_profile").GetString());
        Assert.Equal(1, inspected.RootElement.GetProperty("record_count").GetInt32());
    }

    [Fact]
    public void Preview_RejectsCallerSnapshotAndFailsClosedWithoutOwnerProvider()
    {
        using var temp = new TempDirectory();
        var injectedPath = Path.Combine(temp.Path, "injected.json");
        var controlPath = Path.Combine(temp.Path, "control.json");
        File.WriteAllBytes(injectedPath, SanitizedExportJson.SerializeRequest(Request()));
        File.WriteAllBytes(controlPath, SanitizedExportJson.SerializeControlRequest(Control()));

        var injected = Run(["preview", "--database", Path.Combine(temp.Path, "monitor.db"), "--request", injectedPath], new Provider(Snapshot()));
        var unavailable = Run(["sanitized-export", "preview", "--request", controlPath]);

        Assert.Equal("request_invalid" + Environment.NewLine, injected.Error);
        Assert.Equal("invalid_arguments" + Environment.NewLine, unavailable.Error);
    }

    [Fact]
    public void Result_RejectsBundleLargerThanFixedReadLimitBeforeAllocation()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "oversized.zip");
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            stream.SetLength(SanitizedExportLimits.MaximumUncompressedBytes + 1);

        var result = Run(["sanitized-export", "result", "--bundle", path]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("bundle_too_large" + Environment.NewLine, result.Error);
    }

    [Theory]
    [InlineData("{\"schema_version\":\"sanitized-export-control.v2\",\"created_at\":\"2026-07-22T00:00:00+00:00\",\"selection\":{}}")]
    [InlineData("{\"schema_version\":\"sanitized-export-control.v1\",\"created_at\":\"2026-07-22T00:00:00+00:00\",\"selection\":{},\"forbidden_markers\":[]}")]
    public void Preview_RejectsUnknownControlVersionAndFormerMarkerAuthority(string json)
    {
        using var temp = new TempDirectory();
        var requestPath = Path.Combine(temp.Path, "request.json");
        File.WriteAllText(requestPath, json);

        var result = Run(["preview", "--database", Path.Combine(temp.Path, "monitor.db"), "--request", requestPath], new Provider(Snapshot()));

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("request_invalid" + Environment.NewLine, result.Error);
    }

    private static SanitizedExportControlRequest Control() => new(
        "sanitized-export-control.v1", new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), new(SessionIds: ["session-a"]));

    private static SanitizedExportRequest Request() => new(Control().CreatedAt, Snapshot(), Control().Selection);

    private static SanitizedExportSourceSnapshot Snapshot()
    {
        var time = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        return new(
            "snapshot-cli-85", "local-monitor-test", [],
            [new("repository-metadata/session-a.json", "repository_metadata_projection", "session-a", "session-a", "trace-a", "github-copilot-cli", null, null, null, time,
                Encoding.UTF8.GetBytes("{\"schema_version\":\"repository-metadata-projection.v1\",\"record_id\":\"session-a\",\"session_id\":\"session-a\",\"trace_id\":\"trace-a\",\"source_surface\":\"github-copilot-cli\",\"repository_name\":null,\"workspace_label\":null,\"repo_snapshot\":null,\"observed_at\":\"2026-07-22T00:00:00.0000000Z\",\"completeness\":\"unknown\",\"content_state\":\"unknown\",\"retention_state\":\"unknown\"}"), [])],
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
    }

    private static (int ExitCode, string Output, string Error) Run(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (CliApplication.Run(args, output, error), output.ToString(), error.ToString());
    }

    private static (int ExitCode, string Output, string Error) Run(string[] args, ISanitizedExportSnapshotProvider provider)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        return (SanitizedExportCli.Run(args, output, error, provider), output.ToString(), error.ToString());
    }

    private sealed class Provider(SanitizedExportSourceSnapshot snapshot) : ISanitizedExportSnapshotProvider
    {
        public SanitizedExportSnapshotCapture Capture(SanitizedExportSelection selection) => new(true, null, snapshot);
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

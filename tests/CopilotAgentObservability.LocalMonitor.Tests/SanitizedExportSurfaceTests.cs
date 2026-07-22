using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.SanitizedExport;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedExportSurfaceTests
{
    [Fact]
    public async Task Api_PreviewUsesProductionSqliteSnapshotProvider()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
        });

        using var response = await host.Client.SendAsync(Post(
            "/api/sanitized-export/v1/previews", SanitizedExportJson.SerializeControlRequest(Control())));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
    }

    [Fact]
    public void Cli_PreviewExportAndResultUseTrustedSnapshotProvider()
    {
        using var temp = new MonitorTempDirectory();
        var requestPath = Path.Combine(temp.Path, "request.json");
        var bundlePath = Path.Combine(temp.Path, "bundle.zip");
        File.WriteAllBytes(requestPath, SanitizedExportJson.SerializeControlRequest(Control()));
        var provider = new Provider(Snapshot());

        using var previewOutput = new StringWriter();
        using var previewError = new StringWriter();
        var previewExit = SanitizedExportCli.Run(["preview", "--database", temp.DatabasePath, "--request", requestPath], previewOutput, previewError, provider);
        Assert.Equal(0, previewExit);
        Assert.Equal(string.Empty, previewError.ToString());
        using var preview = JsonDocument.Parse(previewOutput.ToString());
        Assert.True(preview.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("sanitized-evidence-bundle.v1", preview.RootElement.GetProperty("bundle_schema_version").GetString());
        Assert.Equal("sanitized-evidence", preview.RootElement.GetProperty("bundle_profile").GetString());
        Assert.Equal(1, preview.RootElement.GetProperty("record_count").GetInt32());
        Assert.Equal("repository-safe-scanner.v1", preview.RootElement.GetProperty("validation_profile").GetString());

        using var createOutput = new StringWriter();
        using var createError = new StringWriter();
        var createExit = SanitizedExportCli.Run(["export", "--database", temp.DatabasePath, "--request", requestPath, "--output", bundlePath], createOutput, createError, provider);
        Assert.Equal(0, createExit);
        Assert.True(File.Exists(bundlePath));
        Assert.Equal(string.Empty, createError.ToString());
        using var created = JsonDocument.Parse(createOutput.ToString());
        Assert.True(created.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("bundle.zip", created.RootElement.GetProperty("published_file_name").GetString());

        using var resultOutput = new StringWriter();
        using var resultError = new StringWriter();
        var resultExit = SanitizedExportCli.Run(["result", "--bundle", bundlePath], resultOutput, resultError, provider);
        Assert.Equal(0, resultExit);
        Assert.Equal(string.Empty, resultError.ToString());
        using var inspected = JsonDocument.Parse(resultOutput.ToString());
        Assert.True(inspected.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(created.RootElement.GetProperty("archive_sha256").GetString(), inspected.RootElement.GetProperty("archive_sha256").GetString());
        Assert.Equal("sanitized-evidence-bundle.v1", inspected.RootElement.GetProperty("bundle_schema_version").GetString());
        Assert.Equal("sanitized-evidence", inspected.RootElement.GetProperty("bundle_profile").GetString());
        Assert.Equal(1, inspected.RootElement.GetProperty("record_count").GetInt32());
    }

    [Fact]
    public async Task Api_PreviewCreateResultAndDownloadAreSameOriginCsrfNoStoreAndServerManaged()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
            SanitizedExportSnapshotProvider = new Provider(Snapshot()),
        });
        var body = SanitizedExportJson.SerializeControlRequest(Control());

        using var missingCsrf = await host.Client.PostAsync("/api/sanitized-export/v1/previews", Json(body));
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        Assert.Equal("no-store", missingCsrf.Headers.CacheControl?.ToString());

        using var crossSiteRequest = Post("/api/sanitized-export/v1/previews", body);
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);

        using var unknownFieldRequest = Post("/api/sanitized-export/v1/previews", body[..^1].Concat(Encoding.UTF8.GetBytes(",\"output_path\":\"C:\\\\private.zip\"}")).ToArray());
        using var unknownField = await host.Client.SendAsync(unknownFieldRequest);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        Assert.Equal("{\"error\":\"request_invalid\"}", await unknownField.Content.ReadAsStringAsync());

        var injectedSnapshotBody = JsonNode.Parse(body)!;
        injectedSnapshotBody["snapshot"] = JsonNode.Parse("{\"records\":[]}");
        using var injectedSnapshotRequest = Post("/api/sanitized-export/v1/previews", Encoding.UTF8.GetBytes(injectedSnapshotBody.ToJsonString()));
        using var injectedSnapshot = await host.Client.SendAsync(injectedSnapshotRequest);
        Assert.Equal(HttpStatusCode.BadRequest, injectedSnapshot.StatusCode);
        Assert.Equal("{\"error\":\"request_invalid\"}", await injectedSnapshot.Content.ReadAsStringAsync());

        using var previewRequest = Post("/api/sanitized-export/v1/previews", body);
        using var preview = await host.Client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("no-store", preview.Headers.CacheControl?.ToString());
        Assert.False(preview.Headers.Contains("Access-Control-Allow-Origin"));

        using var createRequest = Post("/api/sanitized-export/v1/exports", body);
        using var create = await host.Client.SendAsync(createRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        Assert.Equal("no-store", create.Headers.CacheControl?.ToString());
        using var result = JsonDocument.Parse(await create.Content.ReadAsByteArrayAsync());
        var exportId = result.RootElement.GetProperty("export_id").GetString();
        Assert.Equal(64, exportId?.Length);
        Assert.False(result.RootElement.TryGetProperty("output_path", out _));

        using var read = await host.Client.GetAsync($"/api/sanitized-export/v1/exports/{exportId}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal("no-store", read.Headers.CacheControl?.ToString());
        using var readResult = JsonDocument.Parse(await read.Content.ReadAsByteArrayAsync());
        Assert.Equal(exportId, readResult.RootElement.GetProperty("export_id").GetString());

        using var download = await host.Client.GetAsync($"/api/sanitized-export/v1/exports/{exportId}/archive");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
        Assert.NotEmpty(await download.Content.ReadAsByteArrayAsync());

        var storedPath = Path.Combine(temp.Path, "sanitized-exports", $"{exportId}.zip");
        await File.AppendAllTextAsync(storedPath, "changed");
        using var changedDownload = await host.Client.GetAsync($"/api/sanitized-export/v1/exports/{exportId}/archive");
        Assert.Equal(HttpStatusCode.NotFound, changedDownload.StatusCode);
        Assert.Equal("{\"error\":\"export_not_found\"}", await changedDownload.Content.ReadAsStringAsync());

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/sanitized-export/v1/exports/{exportId}");
        invalidHostRequest.Headers.Host = "example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidHost.StatusCode);
        Assert.Equal("no-store", invalidHost.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Api_CreateScannerFailurePublishesNoArtifactOrResult()
    {
        using var temp = new MonitorTempDirectory();
        var unsafeSnapshot = Snapshot() with
        {
            Records = [Snapshot().Records[0] with { CanonicalBytes = Encoding.UTF8.GetBytes("{\"prompt\":\"blocked\"}") }],
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
            SanitizedExportSnapshotProvider = new Provider(unsafeSnapshot),
        });

        using var createRequest = Post("/api/sanitized-export/v1/exports", SanitizedExportJson.SerializeControlRequest(Control()));
        using var create = await host.Client.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal("{\"error\":\"snapshot_store_unavailable\"}", await create.Content.ReadAsStringAsync());
        var exportDirectory = Path.Combine(temp.Path, "sanitized-exports");
        Assert.False(Directory.Exists(exportDirectory) && Directory.EnumerateFiles(exportDirectory).Any());
    }

    private static SanitizedExportControlRequest Control() => new(
        "sanitized-export-control.v1", new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero), new(SessionIds: ["session-a"]));

    private static SanitizedExportSourceSnapshot Snapshot()
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        return new(
            "snapshot-surface-85",
            "local-monitor-test",
            [new("github-copilot-cli", "1.0.73")],
            [new(
                    "repository-metadata/session-a.json",
                    "repository_metadata_projection",
                    "session-a",
                    "session-a",
                    "trace-a",
                    "github-copilot-cli",
                    "sample-repository",
                    "sample-workspace",
                    "sample-snapshot",
                    observedAt,
                    Encoding.UTF8.GetBytes("{\"schema_version\":\"repository-metadata-projection.v1\",\"record_id\":\"session-a\",\"session_id\":\"session-a\",\"trace_id\":\"trace-a\",\"source_surface\":\"github-copilot-cli\",\"repository_name\":\"sample-repository\",\"workspace_label\":\"sample-workspace\",\"repo_snapshot\":\"sample-snapshot\",\"observed_at\":\"2026-07-22T00:00:00.0000000Z\",\"completeness\":\"unknown\",\"content_state\":\"unknown\",\"retention_state\":\"unknown\"}"),
                    [])],
            new("missing", "missing", "unavailable", "unavailable", "unavailable"));
    }

    private static HttpRequestMessage Post(string path, byte[] body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static ByteArrayContent Json(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private sealed class Provider(SanitizedExportSourceSnapshot snapshot) : ISanitizedExportSnapshotProvider
    {
        public SanitizedExportSnapshotCapture Capture(SanitizedExportSelection selection) => new(true, null, snapshot);
    }
}

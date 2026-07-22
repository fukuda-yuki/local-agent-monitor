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
    public void Cli_PreviewExportAndResultUseTheSharedRequestAndResultContract()
    {
        using var temp = new MonitorTempDirectory();
        var requestPath = Path.Combine(temp.Path, "request.json");
        var bundlePath = Path.Combine(temp.Path, "bundle.zip");
        File.WriteAllBytes(requestPath, SanitizedExportJson.SerializeRequest(Request()));

        using var previewOutput = new StringWriter();
        using var previewError = new StringWriter();
        var previewExit = CliApplication.Run(["sanitized-export", "preview", "--request", requestPath], previewOutput, previewError);
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
        var createExit = CliApplication.Run(["sanitized-export", "export", "--request", requestPath, "--output", bundlePath], createOutput, createError);
        Assert.Equal(0, createExit);
        Assert.True(File.Exists(bundlePath));
        Assert.Equal(string.Empty, createError.ToString());
        using var created = JsonDocument.Parse(createOutput.ToString());
        Assert.True(created.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("bundle.zip", created.RootElement.GetProperty("published_file_name").GetString());

        using var resultOutput = new StringWriter();
        using var resultError = new StringWriter();
        var resultExit = CliApplication.Run(["sanitized-export", "result", "--bundle", bundlePath], resultOutput, resultError);
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
        });
        var body = SanitizedExportJson.SerializeRequest(Request());

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

        var nullSnapshotBody = JsonNode.Parse(body)!;
        nullSnapshotBody["snapshot"] = null;
        using var nullSnapshotRequest = Post("/api/sanitized-export/v1/previews", Encoding.UTF8.GetBytes(nullSnapshotBody.ToJsonString()));
        using var nullSnapshot = await host.Client.SendAsync(nullSnapshotRequest);
        Assert.Equal(HttpStatusCode.BadRequest, nullSnapshot.StatusCode);
        Assert.Equal("{\"error\":\"request_invalid\"}", await nullSnapshot.Content.ReadAsStringAsync());

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
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
        });
        var unsafeRequest = Request() with
        {
            Snapshot = Request().Snapshot with
            {
                Records = [Request().Snapshot.Records[0] with { CanonicalBytes = Encoding.UTF8.GetBytes("{\"prompt\":\"blocked\"}") }],
            },
        };

        using var createRequest = Post("/api/sanitized-export/v1/exports", SanitizedExportJson.SerializeRequest(unsafeRequest));
        using var create = await host.Client.SendAsync(createRequest);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, create.StatusCode);
        Assert.Equal("{\"error\":\"forbidden_field\"}", await create.Content.ReadAsStringAsync());
        var exportDirectory = Path.Combine(temp.Path, "sanitized-exports");
        Assert.False(Directory.Exists(exportDirectory) && Directory.EnumerateFiles(exportDirectory).Any());
    }

    private static SanitizedExportRequest Request()
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        return new(
            observedAt,
            new(
                "snapshot-surface-85",
                "local-monitor-test",
                [new("github-copilot-cli", "1.0.73")],
                [new(
                    "sessions/session-a.json",
                    "session_projection",
                    "session-a",
                    "session-a",
                    "trace-a",
                    "github-copilot-cli",
                    "sample-repository",
                    "sample-workspace",
                    "sample-snapshot",
                    observedAt,
                    Encoding.UTF8.GetBytes("{\"schema_version\":\"session-workspace.v1\",\"session_id\":\"session-a\"}"),
                    [])],
                new("missing", "missing", "unavailable", "unavailable", "unavailable")),
            new(SessionIds: ["session-a"]),
            []);
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
}

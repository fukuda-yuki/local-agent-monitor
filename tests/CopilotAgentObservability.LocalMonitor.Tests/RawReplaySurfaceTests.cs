using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RawReplaySurfaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Api_RequiresSameOriginCsrfConsentAndStagesReplayThroughRetention()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(Now) };
        var provider = new Provider(Snapshot());
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(provider));
        var control = ExportControl();

        using var missingCsrf = await host.Client.PostAsync("/api/raw-replay/v1/export-previews", Json(RawReplayJson.Serialize(control)));
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        using var crossSiteRequest = PostJson("/api/raw-replay/v1/export-previews", control);
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);

        using var previewResponse = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", control));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = RawReplayJson.DeserializeExact<RawReplayPreview>(await previewResponse.Content.ReadAsByteArrayAsync());
        using var exportResponse = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/exports", control with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = Consent(),
        }));
        Assert.Equal(HttpStatusCode.Created, exportResponse.StatusCode);
        using var exportJson = JsonDocument.Parse(await exportResponse.Content.ReadAsByteArrayAsync());
        var exportId = exportJson.RootElement.GetProperty("export_id").GetString()!;
        using var download = await host.Client.GetAsync($"/api/raw-replay/v1/exports/{exportId}/archive");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        var archive = await download.Content.ReadAsByteArrayAsync();

        using var invalidHostRequest = PostZip("/api/raw-replay/v1/replay-previews", archive);
        invalidHostRequest.Headers.Host = "example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidHost.StatusCode);
        Assert.Equal("{\"error\":\"invalid_host\"}", await invalidHost.Content.ReadAsStringAsync());

        using var replayPreviewResponse = await host.Client.SendAsync(PostZip("/api/raw-replay/v1/replay-previews", archive));
        Assert.Equal(HttpStatusCode.OK, replayPreviewResponse.StatusCode);
        using var replayPreview = JsonDocument.Parse(await replayPreviewResponse.Content.ReadAsByteArrayAsync());
        var digest = replayPreview.RootElement.GetProperty("preview_digest").GetString()!;
        var archiveSha = replayPreview.RootElement.GetProperty("archive_sha256").GetString()!;
        var replayControl = new RawReplayControl(RawReplayContractVersions.ReplayControl, RawReplayContractVersions.BundleProfile,
            "replay-api-one", archiveSha, RawReplayContractVersions.Normalization, RawReplayContractVersions.Projection,
            RawReplayContractVersions.Dashboard, false, digest, Consent());
        using var missingConsent = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/replays", replayControl with { Consent = null }));
        Assert.Equal(HttpStatusCode.Forbidden, missingConsent.StatusCode);
        Assert.Equal("{\"error\":\"consent_required\"}", await missingConsent.Content.ReadAsStringAsync());
        using var invalidReplay = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/replays", replayControl with { ReplayId = "bad!" }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidReplay.StatusCode);
        Assert.Equal("{\"error\":\"replay_id_invalid\"}", await invalidReplay.Content.ReadAsStringAsync());
        using var invalidStatus = await host.Client.GetAsync("/api/raw-replay/v1/replays/bad!");
        Assert.Equal(HttpStatusCode.BadRequest, invalidStatus.StatusCode);
        Assert.Equal("{\"error\":\"replay_id_invalid\"}", await invalidStatus.Content.ReadAsStringAsync());
        using var replayResponse = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/replays", replayControl));
        Assert.Equal(HttpStatusCode.Created, replayResponse.StatusCode);
        using var replayJson = JsonDocument.Parse(await replayResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, replayJson.RootElement.GetProperty("result").GetProperty("external_model_invocations").GetInt32());
        using var status = await host.Client.GetAsync("/api/raw-replay/v1/replays/replay-api-one");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("no-store", status.Headers.CacheControl?.ToString());
        Assert.False(status.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(1, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle';"));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM monitor_ingestions;"));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM monitor_traces;"));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM monitor_analysis_runs;"));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM historical_evidence_datasets;"));

        var bundlePath = Scalar<string>(temp.DatabasePath, "SELECT private_locator FROM retention_items WHERE store_kind='sensitive_bundle';");
        Assert.True(Directory.Exists(bundlePath));
        var callerArchive = Path.Combine(temp.Path, "raw-local-replay.zip");
        File.WriteAllBytes(callerArchive, archive);
        var time = Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
        time.Advance(TimeSpan.FromDays(8));
        var catalog = new RetentionCatalogStore(RetentionCatalogContext.AdoptExistingCatalogV1(temp.DatabasePath), time);
        var registry = new RetentionAdapterRegistry([
            new SessionEventContentRetentionAdapter(catalog),
            new RawRecordRetentionAdapter(catalog),
            new MonitorAnalysisRetentionAdapter(catalog),
            new SensitiveBundleRetentionAdapter(catalog, time),
            new AnalysisSdkDirectoryRetentionAdapter(catalog, time),
        ]);
        var cycle = await new RetentionCleanupCoordinator(catalog, registry, time)
            .RunOneCycleAsync(CancellationToken.None, CancellationToken.None);
        Assert.Equal(1, cycle.Completed);
        Assert.False(Directory.Exists(bundlePath));
        Assert.True(File.Exists(callerArchive));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(0, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM session_runs;"));
    }

    [Fact]
    public async Task Api_SanitizedOnlyRejectsBeforeProviderOrArchiveRead()
    {
        using var temp = new MonitorTempDirectory(); var provider = new Provider(Snapshot());
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: Options(provider));
        using var response = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", ExportControl()));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("{\"error\":\"sanitized_only_denied\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public async Task Api_UnexpectedProviderFailureReturnsFixedNoLeakError()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new ThrowingProvider()));

        using var response = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", ExportControl()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"raw_replay_unavailable\"}", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("synthetic-sensitive-value", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Api_UsesRawReplayBodyLimitInsteadOfTheIngestionLimit()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, maxRequestBodyBytes: 64, testOptions: Options(new Provider(Snapshot())));

        using var response = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", ExportControl()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_MapsRawSelectionBoundsToPayloadTooLarge()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new FailingProvider("selection_limit_exceeded")));

        using var response = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", ExportControl()));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"selection_limit_exceeded\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_MapsMissingSnapshotMemberToServiceUnavailable()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new FailingProvider("snapshot_member_missing")));

        using var response = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", ExportControl()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"snapshot_member_missing\"}", await response.Content.ReadAsStringAsync());
    }

    private static MonitorHostTestOptions Options(IRawReplaySnapshotProvider provider) => new()
    {
        StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
        StartSessionOtelEnrichment = false, StartRetentionCleanupWorker = false,
        RawReplaySnapshotProvider = provider,
    };

    private static HttpRequestMessage PostJson<T>(string path, T value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(RawReplayJson.Serialize(value)) };
        request.Headers.Add("x-monitor-csrf", "local-monitor"); return request;
    }

    private static HttpRequestMessage PostZip(string path, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("x-monitor-csrf", "local-monitor"); return request;
    }

    private static ByteArrayContent Json(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue("application/json"); return content;
    }

    private static RawReplayConsent Consent() => new(RawReplayContractVersions.BundleProfile, true, RawReplayConsent.RequiredPhrase);
    private static RawReplayExportControl ExportControl() => new(RawReplayContractVersions.ExportControl,
        RawReplayContractVersions.BundleProfile, Now, new(RawRecordIds: [1]), false, false, null, null);
    private static RawReplaySnapshot Snapshot() => new("snapshot", Now, "monitor-v1",
        [new(1, "raw-otlp", "trace-one", Now, null,
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"trace-one\",\"spanId\":\"span\"}]}]}]}", 1,
            new("copilot-cli", "1", "otlp-json", "adapter-v1", "schema-v1", new string('a', 64), "supported", "available", "not_applied_raw_capture", RawReplayContractVersions.CredentialScanner))],
        [], ["session_content_not_requested"]);

    private static T Scalar<T>(string path, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long Scalar(string path, string sql) => Scalar<long>(path, sql);

    private sealed class Provider(RawReplaySnapshot snapshot) : IRawReplaySnapshotProvider
    {
        public int CaptureCount { get; private set; }
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(RawReplaySelection selection, bool includeSessionContent, CancellationToken cancellationToken)
        {
            CaptureCount++;
            return ValueTask.FromResult(new RawReplaySnapshotCapture(true, null, new RawReplaySnapshotLease(snapshot, static () => ValueTask.CompletedTask)));
        }
    }

    private sealed class ThrowingProvider : IRawReplaySnapshotProvider
    {
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(RawReplaySelection selection, bool includeSessionContent, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("synthetic-sensitive-value");
    }

    private sealed class FailingProvider(string code) : IRawReplaySnapshotProvider
    {
        public ValueTask<RawReplaySnapshotCapture> CaptureAsync(RawReplaySelection selection, bool includeSessionContent, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new RawReplaySnapshotCapture(false, code, null));
    }
}

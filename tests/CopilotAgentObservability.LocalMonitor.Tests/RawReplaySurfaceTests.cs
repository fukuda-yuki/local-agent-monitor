using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;
using Microsoft.AspNetCore.Http;

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
        using var foreignExportStatus = await host.Client.SendAsync(CrossSiteGet($"/api/raw-replay/v1/exports/{exportId}"));
        Assert.Equal(HttpStatusCode.Forbidden, foreignExportStatus.StatusCode);
        using var foreignExportDownload = await host.Client.SendAsync(CrossSiteGet($"/api/raw-replay/v1/exports/{exportId}/archive"));
        Assert.Equal(HttpStatusCode.Forbidden, foreignExportDownload.StatusCode);
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
        using var foreignReplayStatus = await host.Client.SendAsync(CrossSiteGet("/api/raw-replay/v1/replays/replay-api-one"));
        Assert.Equal(HttpStatusCode.Forbidden, foreignReplayStatus.StatusCode);
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
    public async Task Api_SanitizedOnlyRejectsEveryRouteBeforeBodyOrStoreRead()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(Now) };
        const string replayId = "sanitized-existing";
        var replayParent = Path.Combine(temp.Path, "raw-replays");
        var replayStore = new RetentionRawReplayStore(
            new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider),
            replayParent,
            temp.TimeProvider);
        var seeded = await replayStore.ReplayAsync(replayId, CreateArchive(Snapshot(), ExportControl()), CancellationToken.None);
        Assert.True(seeded.Success, seeded.ErrorCode);
        File.AppendAllText(Path.Combine(replayParent, RetentionRawReplayStore.CaptureId(replayId), "manifest.json"), "tampered");
        var provider = new Provider(Snapshot());
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: Options(provider));
        var catalogState = Scalar<string>(temp.DatabasePath, """
            SELECT state || '|' || revision || '|' || COALESCE(read_denied_at,'') || '|' || COALESCE(error_code,'')
            FROM retention_items WHERE store_kind='sensitive_bundle';
            """);
        var writes = new[]
        {
            PostRaw("/api/raw-replay/v1/export-previews", "application/json", "not-json"u8.ToArray()),
            PostRaw("/api/raw-replay/v1/exports", "application/json", "not-json"u8.ToArray()),
            PostRaw("/api/raw-replay/v1/replay-previews", "application/zip", "not-a-zip"u8.ToArray()),
            PostRaw("/api/raw-replay/v1/replays", "application/json", "not-json"u8.ToArray()),
        };
        foreach (var request in writes)
        {
            using (request)
            using (var response = await host.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("{\"error\":\"sanitized_only_denied\"}", await response.Content.ReadAsStringAsync());
            }
        }
        foreach (var path in new[]
        {
            "/api/raw-replay/v1/exports/missing",
            "/api/raw-replay/v1/exports/missing/archive",
            $"/api/raw-replay/v1/replays/{replayId}",
        })
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal("{\"error\":\"sanitized_only_denied\"}", await response.Content.ReadAsStringAsync());
        }
        Assert.Equal(0, provider.CaptureCount);
        Assert.Equal(catalogState, Scalar<string>(temp.DatabasePath, """
            SELECT state || '|' || revision || '|' || COALESCE(read_denied_at,'') || '|' || COALESCE(error_code,'')
            FROM retention_items WHERE store_kind='sensitive_bundle';
            """));
        Assert.Equal(1, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM retention_items WHERE store_kind='sensitive_bundle';"));
    }

    [Fact]
    public async Task Api_ReturnsSerialized415ErrorsBeforeParsingUnsupportedBodies()
    {
        using var temp = new MonitorTempDirectory();
        var provider = new Provider(Snapshot());
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(provider));

        using var jsonResponse = await host.Client.SendAsync(PostRaw(
            "/api/raw-replay/v1/export-previews", "text/plain", "not-json"u8.ToArray()));
        using var zipResponse = await host.Client.SendAsync(PostRaw(
            "/api/raw-replay/v1/replay-previews", "application/octet-stream", "not-a-zip"u8.ToArray()));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, jsonResponse.StatusCode);
        Assert.Equal("{\"error\":\"unsupported_media_type\"}", await jsonResponse.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, zipResponse.StatusCode);
        Assert.Equal("{\"error\":\"unsupported_media_type\"}", await zipResponse.Content.ReadAsStringAsync());
        Assert.Equal(0, provider.CaptureCount);
    }

    [Fact]
    public async Task Api_InvalidTransientLookupKeysReturnFixedMissingOrExpiredErrors()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new Provider(Snapshot())));
        using var missingExport = await host.Client.GetAsync("/api/raw-replay/v1/exports/%20");
        var replay = new RawReplayControl(
            RawReplayContractVersions.ReplayControl,
            RawReplayContractVersions.BundleProfile,
            "invalid-preview-key",
            new string('0', 64),
            RawReplayContractVersions.Normalization,
            RawReplayContractVersions.Projection,
            RawReplayContractVersions.Dashboard,
            false,
            " ",
            Consent());
        using var expiredPreview = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/replays", replay));

        Assert.Equal(HttpStatusCode.NotFound, missingExport.StatusCode);
        Assert.Equal("{\"error\":\"export_not_found\"}", await missingExport.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, expiredPreview.StatusCode);
        Assert.Equal("{\"error\":\"preview_expired\"}", await expiredPreview.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Error_writer_uses_json_serialization_for_control_characters()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        const string value = "fixed\"\ncode";

        await RawReplayRoutes.ErrorAsync(context, StatusCodes.Status418ImATeapot, value);

        context.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(context.Response.Body);
        Assert.Equal(value, json.RootElement.GetProperty("error").GetString());
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
    public async Task Api_MapsProviderControlledErrorTextToAValidFixedJsonError()
    {
        using var temp = new MonitorTempDirectory();
        const string hostile = "private\"\nprovider-code";
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new FailingProvider(hostile)));

        using var previewResponse = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", ExportControl()));
        using var exportResponse = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/exports", ExportControl() with
        {
            PreviewDigest = new string('0', 64),
            Consent = Consent(),
        }));

        foreach (var response in new[] { previewResponse, exportResponse })
        {
            var body = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("snapshot_store_unavailable", json.RootElement.GetProperty("error").GetString());
            Assert.DoesNotContain("private", Encoding.UTF8.GetString(body), StringComparison.Ordinal);
        }
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
    public async Task Api_MapsProviderInvalidSelectionToThePublicRequestCode()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new FailingProvider("invalid_selection")));

        using var response = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", ExportControl()));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"request_invalid\"}", await response.Content.ReadAsStringAsync());
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

    [Fact]
    public async Task Api_EnforcesTheSharedEntryLimitAcrossExportsAndReplayPreviews()
    {
        using var temp = new MonitorTempDirectory();
        var limits = new RawReplayTransientLimits(2, RawReplayLimits.MaximumArchiveBytes * 2L, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new Provider(Snapshot()), limits));

        var first = await ExportAsync(host.Client, ExportControl() with { CreatedAt = Now.AddSeconds(1) });
        var preview = await ReplayPreviewAsync(host.Client, first.Archive);
        var second = await ExportAsync(host.Client, ExportControl() with { CreatedAt = Now.AddSeconds(2) });

        using var evicted = await host.Client.GetAsync($"/api/raw-replay/v1/exports/{first.Id}");
        using var retained = await host.Client.GetAsync($"/api/raw-replay/v1/exports/{second.Id}");
        using var replayed = await ReplayAsync(host.Client, "shared-count", preview);
        Assert.Equal(HttpStatusCode.NotFound, evicted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, retained.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayed.StatusCode);
    }

    [Fact]
    public async Task Api_EnforcesTheSharedByteLimitAcrossExportsAndReplayPreviews()
    {
        var firstControl = ExportControl() with { CreatedAt = Now.AddSeconds(1) };
        var expectedArchive = CreateArchive(Snapshot(), firstControl);
        using var temp = new MonitorTempDirectory();
        var limits = new RawReplayTransientLimits(8, expectedArchive.Length + 128L, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(1));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new Provider(Snapshot()), limits));

        var first = await ExportAsync(host.Client, firstControl);
        var preview = await ReplayPreviewAsync(host.Client, first.Archive);

        using var evicted = await host.Client.GetAsync($"/api/raw-replay/v1/exports/{first.Id}");
        using var replayed = await ReplayAsync(host.Client, "shared-bytes", preview);
        Assert.Equal(HttpStatusCode.NotFound, evicted.StatusCode);
        Assert.Equal(HttpStatusCode.Created, replayed.StatusCode);
    }

    [Fact]
    public async Task Api_ExpiresExportAndReplayPreviewBytesOnTheIdleTimer()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(Now) };
        var limits = new RawReplayTransientLimits(8, RawReplayLimits.MaximumArchiveBytes * 2L, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new Provider(Snapshot()), limits));
        var exported = await ExportAsync(host.Client, ExportControl());
        using var previewResponse = await host.Client.SendAsync(PostZip("/api/raw-replay/v1/replay-previews", exported.Archive));
        using var previewJson = JsonDocument.Parse(await previewResponse.Content.ReadAsByteArrayAsync());
        var previewDigest = previewJson.RootElement.GetProperty("preview_digest").GetString()!;
        var archiveSha = previewJson.RootElement.GetProperty("archive_sha256").GetString()!;

        Assert.IsType<MutableTimeProvider>(temp.TimeProvider).Advance(TimeSpan.FromMinutes(2));

        using var missingExport = await host.Client.GetAsync($"/api/raw-replay/v1/exports/{exported.Id}");
        var replay = new RawReplayControl(
            RawReplayContractVersions.ReplayControl,
            RawReplayContractVersions.BundleProfile,
            "expired-preview",
            archiveSha,
            RawReplayContractVersions.Normalization,
            RawReplayContractVersions.Projection,
            RawReplayContractVersions.Dashboard,
            false,
            previewDigest,
            Consent());
        using var expiredPreview = await host.Client.SendAsync(PostJson("/api/raw-replay/v1/replays", replay));
        Assert.Equal(HttpStatusCode.NotFound, missingExport.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, expiredPreview.StatusCode);
        Assert.Equal("{\"error\":\"preview_expired\"}", await expiredPreview.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Transient_store_sweeps_expired_bytes_without_request_activity()
    {
        var time = new MutableTimeProvider(Now);
        using var store = new RawReplayTransientStore(
            time,
            new RawReplayTransientLimits(2, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)));

        Assert.True(store.Put("export", "one", "raw-bytes"u8.ToArray(), "metadata"));
        Assert.Equal(1, store.Count);

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.TotalBytes);
    }

    [Fact]
    public void Transient_store_defaults_match_the_public_bounds()
    {
        var limits = RawReplayTransientLimits.Default;

        Assert.Equal(8, limits.MaximumEntries);
        Assert.Equal(256L * 1024 * 1024, limits.MaximumBytes);
        Assert.Equal(TimeSpan.FromMinutes(10), limits.Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(1), limits.SweepInterval);
    }

    [Fact]
    public void Transient_store_disposal_clears_bytes_and_rejects_lookups()
    {
        var store = new RawReplayTransientStore(
            new MutableTimeProvider(Now),
            new RawReplayTransientLimits(2, 1024, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(10)));
        Assert.True(store.Put("export", "one", "raw-bytes"u8.ToArray(), "metadata"));

        store.Dispose();

        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.TotalBytes);
        Assert.False(store.TryGet<string>("export", "one", out _, out _));
        store.Dispose();
    }

    private static MonitorHostTestOptions Options(IRawReplaySnapshotProvider provider, RawReplayTransientLimits? transientLimits = null) => new()
    {
        StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false,
        StartSessionOtelEnrichment = false, StartRetentionCleanupWorker = false,
        RawReplaySnapshotProvider = provider,
        RawReplayTransientLimits = transientLimits,
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

    private static HttpRequestMessage PostRaw(string path, string mediaType, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static HttpRequestMessage CrossSiteGet(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Sec-Fetch-Site", "cross-site");
        return request;
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

    private static async Task<(string Id, byte[] Archive)> ExportAsync(HttpClient client, RawReplayExportControl control)
    {
        using var previewResponse = await client.SendAsync(PostJson("/api/raw-replay/v1/export-previews", control));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = RawReplayJson.DeserializeExact<RawReplayPreview>(await previewResponse.Content.ReadAsByteArrayAsync());
        using var exportResponse = await client.SendAsync(PostJson("/api/raw-replay/v1/exports", control with
        {
            PreviewDigest = preview.PreviewDigest,
            Consent = Consent(),
        }));
        Assert.Equal(HttpStatusCode.Created, exportResponse.StatusCode);
        using var json = JsonDocument.Parse(await exportResponse.Content.ReadAsByteArrayAsync());
        var id = json.RootElement.GetProperty("export_id").GetString()!;
        using var download = await client.GetAsync($"/api/raw-replay/v1/exports/{id}/archive");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        return (id, await download.Content.ReadAsByteArrayAsync());
    }

    private static async Task<(string Digest, string ArchiveSha)> ReplayPreviewAsync(HttpClient client, byte[] archive)
    {
        using var response = await client.SendAsync(PostZip("/api/raw-replay/v1/replay-previews", archive));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        return (
            json.RootElement.GetProperty("preview_digest").GetString()!,
            json.RootElement.GetProperty("archive_sha256").GetString()!);
    }

    private static Task<HttpResponseMessage> ReplayAsync(
        HttpClient client,
        string replayId,
        (string Digest, string ArchiveSha) preview) => client.SendAsync(PostJson(
            "/api/raw-replay/v1/replays",
            new RawReplayControl(
                RawReplayContractVersions.ReplayControl,
                RawReplayContractVersions.BundleProfile,
                replayId,
                preview.ArchiveSha,
                RawReplayContractVersions.Normalization,
                RawReplayContractVersions.Projection,
                RawReplayContractVersions.Dashboard,
                false,
                preview.Digest,
                Consent())));

    private static byte[] CreateArchive(RawReplaySnapshot snapshot, RawReplayExportControl control)
    {
        var service = new RawReplayArchiveService();
        var preview = service.Preview(snapshot, control);
        var created = service.Create(snapshot, control with { PreviewDigest = preview.PreviewDigest, Consent = Consent() });
        Assert.True(created.Success, created.ErrorCode);
        return created.ArchiveBytes!;
    }

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

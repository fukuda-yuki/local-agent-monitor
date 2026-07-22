using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupSurfaceTests
{
    [Fact]
    public async Task Sanitized_only_removes_every_runtime_backup_route_before_body_or_store_processing()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            maxRequestBodyBytes: 128,
            testOptions: DisabledWorkers());

        using var createRequest = CreateRequest();
        using var create = await host.Client.SendAsync(createRequest);
        using var invalidCreateRequest = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/backups")
        {
            Content = Json("{\"output_path\":\"must-not-be-processed\"}"u8.ToArray()),
        };
        invalidCreateRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        using var invalidCreate = await host.Client.SendAsync(invalidCreateRequest);
        using var result = await host.Client.GetAsync($"/api/runtime-backup/v1/backups/{new string('a', 64)}");
        using var download = await host.Client.GetAsync($"/api/runtime-backup/v1/backups/{new string('a', 64)}/archive");
        using var oversizedPreviewRequest = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/previews")
        {
            Content = Zip(new byte[1024]),
        };
        oversizedPreviewRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        using var oversizedPreview = await host.Client.SendAsync(oversizedPreviewRequest);
        using var ui = await host.Client.GetAsync("/backup-restore");
        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/backup-restore");
        invalidHostRequest.Headers.Host = "monitor.example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);

        Assert.All(
            new[] { create, invalidCreate, result, download, oversizedPreview, ui, invalidHost },
            response => Assert.Equal(HttpStatusCode.NotFound, response.StatusCode));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "runtime-backups")));
    }

    [Fact]
    public async Task Overview_exposes_raw_mode_backup_affordance_without_expanding_sidebar_information_architecture()
    {
        using var rawTemp = new MonitorTempDirectory();
        await using var rawHost = await MonitorTestHost.StartAsync(rawTemp, testOptions: DisabledWorkers());
        var rawHtml = await rawHost.Client.GetStringAsync("/");

        Assert.Contains("id=\"runtime-backup-link\"", rawHtml, StringComparison.Ordinal);
        Assert.Contains("href=\"/backup-restore\"", rawHtml, StringComparison.Ordinal);

        using var sanitizedTemp = new MonitorTempDirectory();
        await using var sanitizedHost = await MonitorTestHost.StartAsync(
            sanitizedTemp,
            sanitizedOnly: true,
            testOptions: DisabledWorkers());
        var sanitizedHtml = await sanitizedHost.Client.GetStringAsync("/");

        Assert.DoesNotContain("id=\"runtime-backup-link\"", sanitizedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/backup-restore\"", sanitizedHtml, StringComparison.Ordinal);
        Assert.Equal(
            CountOccurrences(sanitizedHtml, "class=\"sidebar-link"),
            CountOccurrences(rawHtml, "class=\"sidebar-link"));
    }

    [Fact]
    public async Task Api_create_result_download_and_preview_are_same_origin_csrf_no_store_and_server_managed()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());

        using var missingCsrf = await host.Client.PostAsync("/api/runtime-backup/v1/backups", Json("{}"u8.ToArray()));
        Assert.Equal(HttpStatusCode.Forbidden, missingCsrf.StatusCode);
        Assert.Equal("no-store", missingCsrf.Headers.CacheControl?.ToString());

        using var crossSiteRequest = CreateRequest();
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);

        using var missingPreviewCsrf = await host.Client.PostAsync("/api/runtime-backup/v1/previews", Zip([]));
        Assert.Equal(HttpStatusCode.Forbidden, missingPreviewCsrf.StatusCode);
        Assert.Equal("no-store", missingPreviewCsrf.Headers.CacheControl?.ToString());
        using var crossSitePreviewRequest = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/previews") { Content = Zip([]) };
        crossSitePreviewRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        crossSitePreviewRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSitePreview = await host.Client.SendAsync(crossSitePreviewRequest);
        Assert.Equal(HttpStatusCode.Forbidden, crossSitePreview.StatusCode);

        using var unknownFieldRequest = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/backups") { Content = Json("{\"output_path\":\"private\"}"u8.ToArray()) };
        unknownFieldRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        using var unknownField = await host.Client.SendAsync(unknownFieldRequest);
        Assert.Equal(HttpStatusCode.BadRequest, unknownField.StatusCode);
        Assert.Equal("{\"error\":\"request_invalid\"}", await unknownField.Content.ReadAsStringAsync());

        using var createRequest = CreateRequest();
        using var create = await host.Client.SendAsync(createRequest);
        var createBytes = await create.Content.ReadAsByteArrayAsync();
        Assert.True(create.StatusCode == HttpStatusCode.Created, Encoding.UTF8.GetString(createBytes));
        Assert.Equal("no-store", create.Headers.CacheControl?.ToString());
        Assert.False(create.Headers.Contains("Access-Control-Allow-Origin"));
        using var created = JsonDocument.Parse(createBytes);
        var backupId = created.RootElement.GetProperty("backup_id").GetString();
        Assert.Equal(64, backupId?.Length);
        Assert.False(created.RootElement.TryGetProperty("output_path", out _));
        Assert.Contains("retention_backup_not_purged", created.RootElement.GetProperty("warnings").EnumerateArray().Select(item => item.GetString()));

        using var result = await host.Client.GetAsync($"/api/runtime-backup/v1/backups/{backupId}");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("no-store", result.Headers.CacheControl?.ToString());
        Assert.False(result.Headers.Contains("Access-Control-Allow-Origin"));

        using var crossSiteResultRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/runtime-backup/v1/backups/{backupId}");
        crossSiteResultRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSiteResult = await host.Client.SendAsync(crossSiteResultRequest);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteResult.StatusCode);

        using var download = await host.Client.GetAsync($"/api/runtime-backup/v1/backups/{backupId}/archive");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("no-store", download.Headers.CacheControl?.ToString());
        Assert.False(download.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal("application/zip", download.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Matches("^local-runtime-backup-[0-9a-f]{12}\\.zip$", download.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var archive = await download.Content.ReadAsByteArrayAsync();
        Assert.NotEmpty(archive);

        using var crossSiteDownloadRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/runtime-backup/v1/backups/{backupId}/archive");
        crossSiteDownloadRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSiteDownload = await host.Client.SendAsync(crossSiteDownloadRequest);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteDownload.StatusCode);

        using var previewRequest = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/previews") { Content = Zip(archive) };
        previewRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        using var preview = await host.Client.SendAsync(previewRequest);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal("no-store", preview.Headers.CacheControl?.ToString());
        Assert.False(preview.Headers.Contains("Access-Control-Allow-Origin"));
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsByteArrayAsync());
        Assert.True(previewJson.RootElement.GetProperty("success").GetBoolean());

        var stored = Assert.Single(Directory.EnumerateFiles(
            Path.Combine(temp.Path, "runtime-backups"),
            "runtime-backup-*.zip"));
        await File.AppendAllTextAsync(stored, "changed");
        using var changed = await host.Client.GetAsync($"/api/runtime-backup/v1/backups/{backupId}/archive");
        Assert.Equal(HttpStatusCode.NotFound, changed.StatusCode);
        Assert.Equal("{\"error\":\"backup_not_found\"}", await changed.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Web_surface_has_no_restore_endpoint_and_explains_offline_restore()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());

        using var ui = await host.Client.GetAsync("/backup-restore");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Equal("no-store", ui.Headers.CacheControl?.ToString());
        var html = await ui.Content.ReadAsStringAsync();
        Assert.Contains("raw_content_included", html, StringComparison.Ordinal);
        Assert.Contains("config-cli runtime-backup restore", html, StringComparison.Ordinal);

        using var restore = await host.Client.PostAsync("/api/runtime-backup/v1/restores", Json("{}"u8.ToArray()));
        Assert.Equal(HttpStatusCode.NotFound, restore.StatusCode);
    }

    [Fact]
    public async Task Preview_respects_lower_configured_kestrel_body_limit()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, maxRequestBodyBytes: 128, testOptions: DisabledWorkers());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/previews") { Content = Zip(new byte[1024]) };
        request.Headers.Add("x-monitor-csrf", "local-monitor");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"error\":\"request_too_large\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Zero_byte_preview_is_archive_invalid_instead_of_too_large()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/previews") { Content = Zip([]) };
        request.Headers.Add("x-monitor-csrf", "local-monitor");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"archive_invalid\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Preview_rejects_reparse_backup_store_before_writing_request_bytes()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());
        var external = Path.Combine(Path.GetTempPath(), $"runtime-backup-preview-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        var linkedStore = Path.Combine(temp.Path, "runtime-backups");
        try
        {
            try { _ = Directory.CreateSymbolicLink(linkedStore, external); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Cannot create directory reparse fixture: {exception.GetType().Name}");
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/previews")
            {
                Content = Zip("request bytes must not reach the linked target"u8.ToArray()),
            };
            request.Headers.Add("x-monitor-csrf", "local-monitor");

            using var response = await host.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("{\"error\":\"snapshot_store_unavailable\"}", await response.Content.ReadAsStringAsync());
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally
        {
            try { if (Directory.Exists(linkedStore)) Directory.Delete(linkedStore); } catch (IOException) { }
            try { Directory.Delete(external, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task Preview_reports_storage_unavailable_when_backup_store_is_a_regular_file()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());
        var store = Path.Combine(temp.Path, "runtime-backups");
        await File.WriteAllTextAsync(store, "not-a-directory");
        using var createRequest = CreateRequest();
        using var create = await host.Client.SendAsync(createRequest);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/previews")
        {
            Content = Zip("must-not-be-written"u8.ToArray()),
        };
        request.Headers.Add("x-monitor-csrf", "local-monitor");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, create.StatusCode);
        Assert.Equal("{\"error\":\"snapshot_store_unavailable\"}", await create.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"snapshot_store_unavailable\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal("not-a-directory", await File.ReadAllTextAsync(store));
    }

    [Fact]
    public async Task Preview_rejects_reparse_backup_store_before_reading_the_request_body()
    {
        using var temp = new MonitorTempDirectory();
        var external = Path.Combine(Path.GetTempPath(), $"runtime-backup-no-read-target-{Guid.NewGuid():N}");
        Directory.CreateDirectory(external);
        var linkedStore = Path.Combine(temp.Path, "runtime-backups");
        try
        {
            try { _ = Directory.CreateSymbolicLink(linkedStore, external); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"Cannot create directory reparse fixture: {exception.GetType().Name}");
            }
            var body = new ForbiddenReadStream();

            var result = await new SqliteRuntimeBackupService(temp.TimeProvider)
                .PreviewAsync(body, temp.DatabasePath, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, result.ErrorCode);
            Assert.Equal(0, body.ReadCount);
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally
        {
            try { if (Directory.Exists(linkedStore)) Directory.Delete(linkedStore); } catch (IOException) { }
            try { Directory.Delete(external, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preview_rejects_malformed_or_active_transient_owner_before_reading_the_request_body(bool active)
    {
        using var temp = new MonitorTempDirectory();
        var store = Directory.CreateDirectory(Path.Combine(temp.Path, "runtime-backups"));
        var raw = Path.Combine(store.FullName, $".runtime-backup-{new string('a', 32)}.partial");
        var marker = raw + ".owner.v1";
        await File.WriteAllTextAsync(raw, "private-partial");
        await File.WriteAllTextAsync(marker, active ? "runtime-backup-transient-owner.v1\n" : "malformed-owner");
        FileStream? activeHandle = null;
        if (active) activeHandle = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var body = new ForbiddenReadStream();

            var result = await new SqliteRuntimeBackupService(temp.TimeProvider)
                .PreviewAsync(body, temp.DatabasePath, CancellationToken.None);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, result.ErrorCode);
            Assert.Equal(0, body.ReadCount);
            Assert.True(File.Exists(raw));
            Assert.True(File.Exists(marker));
        }
        finally
        {
            activeHandle?.Dispose();
        }
    }

    [Fact]
    public async Task Running_host_holds_restore_lease_until_application_disposal()
    {
        using var source = new MonitorTempDirectory();
        byte[] archive;
        await using (var sourceHost = await MonitorTestHost.StartAsync(source, testOptions: DisabledWorkers()))
        {
            using var createRequest = CreateRequest();
            using var create = await sourceHost.Client.SendAsync(createRequest);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            using var created = JsonDocument.Parse(await create.Content.ReadAsByteArrayAsync());
            var backupId = created.RootElement.GetProperty("backup_id").GetString();
            archive = await sourceHost.Client.GetByteArrayAsync($"/api/runtime-backup/v1/backups/{backupId}/archive");
        }

        using var target = new MonitorTempDirectory();
        var bundleDirectory = Directory.CreateDirectory(Path.Combine(target.Path, "runtime-backups"));
        var bundle = Path.Combine(bundleDirectory.FullName, "source.zip");
        await File.WriteAllBytesAsync(bundle, archive);
        var service = new SqliteRuntimeBackupService(target.TimeProvider);

        await using (var targetHost = await MonitorTestHost.StartAsync(target, testOptions: DisabledWorkers()))
        {
            Assert.False(File.Exists(Path.Combine(target.Path, "local-monitor.state.json")));

            var blocked = service.Restore(bundle, target.DatabasePath, new RuntimeRestoreOptions());

            Assert.False(blocked.Success);
            Assert.Equal(RuntimeBackupErrorCodes.MonitorMustBeStopped, blocked.ErrorCode);
        }

        var restored = service.Restore(bundle, target.DatabasePath, new RuntimeRestoreOptions());
        Assert.True(restored.Success, restored.ErrorCode);
    }

    private static MonitorHostTestOptions DisabledWorkers() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
    };

    private static HttpRequestMessage CreateRequest()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/runtime-backup/v1/backups") { Content = Json("{}"u8.ToArray()) };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static ByteArrayContent Json(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    private static ByteArrayContent Zip(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return content;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private sealed class ForbiddenReadStream : Stream
    {
        internal int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { ReadCount++; throw new InvalidOperationException("request body was read"); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { ReadCount++; throw new InvalidOperationException("request body was read"); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

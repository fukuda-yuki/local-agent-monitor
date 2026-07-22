using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.SanitizedImport;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SanitizedImportRouteTests
{
    [Fact]
    public async Task Api_PreviewCommitHistoryDetailAndReplayUseOneTransactionalContract()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var bundle = SanitizedImportServiceTests.GoldenBundle();

        using var previewResponse = await host.Client.SendAsync(Post("/api/sanitized-import/v1/previews", bundle));
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        AssertNoStoreAndNoCors(previewResponse);
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(SanitizedImportContractVersions.Preview, preview.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(1, preview.RootElement.GetProperty("eligible_records").GetInt32());
        Assert.Equal(0, preview.RootElement.GetProperty("graph_state_updates").GetInt32());
        Assert.Equal(0, preview.RootElement.GetProperty("manifest_declaration_count").GetInt32());
        Assert.Empty(preview.RootElement.GetProperty("manifest_declarations").EnumerateArray());
        Assert.Equal(0, preview.RootElement.GetProperty("unresolved_reference_count").GetInt32());
        var digest = preview.RootElement.GetProperty("preview_digest").GetString()!;

        using var commitResponse = await host.Client.SendAsync(Post("/api/sanitized-import/v1/imports", bundle, digest));
        Assert.Equal(HttpStatusCode.Created, commitResponse.StatusCode);
        AssertNoStoreAndNoCors(commitResponse);
        using var committed = JsonDocument.Parse(await commitResponse.Content.ReadAsByteArrayAsync());
        var importId = committed.RootElement.GetProperty("import_id").GetString()!;
        Assert.Equal($"/api/sanitized-import/v1/imports/{importId}", commitResponse.Headers.Location?.ToString());
        Assert.False(committed.RootElement.GetProperty("idempotent_replay").GetBoolean());
        Assert.Equal(1, committed.RootElement.GetProperty("eligible_records").GetInt32());
        Assert.Equal(0, committed.RootElement.GetProperty("graph_state_updates").GetInt32());

        using var listResponse = await host.Client.GetAsync("/api/sanitized-import/v1/imports?limit=1");
        using var detailResponse = await host.Client.GetAsync($"/api/sanitized-import/v1/imports/{importId}");
        using var replayResponse = await host.Client.SendAsync(Post("/api/sanitized-import/v1/imports", bundle, digest));

        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replayResponse.StatusCode);
        AssertNoStoreAndNoCors(listResponse);
        AssertNoStoreAndNoCors(detailResponse);
        AssertNoStoreAndNoCors(replayResponse);
        using var history = JsonDocument.Parse(await listResponse.Content.ReadAsByteArrayAsync());
        using var detail = JsonDocument.Parse(await detailResponse.Content.ReadAsByteArrayAsync());
        using var replay = JsonDocument.Parse(await replayResponse.Content.ReadAsByteArrayAsync());
        Assert.Single(history.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(importId, detail.RootElement.GetProperty("import_id").GetString());
        Assert.True(replay.RootElement.GetProperty("idempotent_replay").GetBoolean());
    }

    [Fact]
    public async Task Api_ReplayIntegrityFailureIsUnavailableAndDoesNotRepairOwnedRows()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var bundle = SanitizedImportServiceTests.GoldenBundle();
        using var previewResponse = await host.Client.SendAsync(Post("/api/sanitized-import/v1/previews", bundle));
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsByteArrayAsync());
        var digest = preview.RootElement.GetProperty("preview_digest").GetString()!;
        using var first = await host.Client.SendAsync(Post("/api/sanitized-import/v1/imports", bundle, digest));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Execute(temp.DatabasePath, "DELETE FROM sanitized_import_graph_edges;");

        using var corruptPreview = await host.Client.SendAsync(Post("/api/sanitized-import/v1/previews", bundle));
        using var replay = await host.Client.SendAsync(Post("/api/sanitized-import/v1/imports", bundle, digest));

        AssertError(corruptPreview, HttpStatusCode.ServiceUnavailable, "import_integrity_failed");
        AssertError(replay, HttpStatusCode.ServiceUnavailable, "import_integrity_failed");
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_graph_edges;"));
    }

    [Fact]
    public async Task Api_EnforcesHostOriginCsrfMediaDigestLimitsAndStrictArchiveStatus()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var bundle = SanitizedImportServiceTests.GoldenBundle();

        using var missingCsrf = await host.Client.PostAsync("/api/sanitized-import/v1/previews", Zip(bundle));
        using var crossSiteRequest = Post("/api/sanitized-import/v1/previews", bundle);
        crossSiteRequest.Headers.Remove("x-monitor-csrf");
        crossSiteRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);
        using var wrongMediaRequest = new HttpRequestMessage(HttpMethod.Post, "/api/sanitized-import/v1/previews")
        {
            Content = new ByteArrayContent(bundle),
        };
        wrongMediaRequest.Content.Headers.ContentType = new("application/json");
        wrongMediaRequest.Headers.Add("x-monitor-csrf", "local-monitor");
        using var wrongMedia = await host.Client.SendAsync(wrongMediaRequest);
        using var missingDigest = await host.Client.SendAsync(Post("/api/sanitized-import/v1/imports", bundle));
        using var invalidArchive = await host.Client.SendAsync(Post("/api/sanitized-import/v1/previews", [1, 2, 3]));
        using var crcArchive = await host.Client.SendAsync(Post(
            "/api/sanitized-import/v1/previews", MatchingButIncorrectCrc(bundle)));
        using var invalidLimit = await host.Client.GetAsync("/api/sanitized-import/v1/imports?limit=0");
        using var unknown = await host.Client.GetAsync($"/api/sanitized-import/v1/imports/{new string('a', 64)}");
        using var detailQuery = await host.Client.GetAsync($"/api/sanitized-import/v1/imports/{new string('a', 64)}?unexpected=true");
        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/api/sanitized-import/v1/imports");
        invalidHostRequest.Headers.Host = "example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);

        AssertError(missingCsrf, HttpStatusCode.Forbidden, "csrf_required");
        AssertError(crossSite, HttpStatusCode.Forbidden, "cross_origin_forbidden");
        AssertError(wrongMedia, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
        AssertError(missingDigest, HttpStatusCode.BadRequest, "preview_digest_invalid");
        AssertError(invalidArchive, HttpStatusCode.UnprocessableEntity, "archive_invalid");
        AssertError(crcArchive, HttpStatusCode.UnprocessableEntity, "archive_invalid");
        AssertError(invalidLimit, HttpStatusCode.BadRequest, "invalid_request");
        AssertError(unknown, HttpStatusCode.NotFound, "import_not_found");
        AssertError(detailQuery, HttpStatusCode.BadRequest, "invalid_request");
        AssertError(invalidHost, HttpStatusCode.Forbidden, "invalid_host");
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
    }

    [Fact]
    public async Task Api_MapsChangedPreviewAndRecordConflictToConflictStatus()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var first = SanitizedImportServiceTests.RepositoryBundle("same-route-record", "repository-a");
        var second = SanitizedImportServiceTests.RepositoryBundle("same-route-record", "repository-b");

        using var changed = await host.Client.SendAsync(Post(
            "/api/sanitized-import/v1/imports", first, new string('0', 64)));
        using var firstPreviewResponse = await host.Client.SendAsync(Post("/api/sanitized-import/v1/previews", first));
        using var firstPreview = JsonDocument.Parse(await firstPreviewResponse.Content.ReadAsByteArrayAsync());
        using var firstCommit = await host.Client.SendAsync(Post(
            "/api/sanitized-import/v1/imports", first, firstPreview.RootElement.GetProperty("preview_digest").GetString()!));
        using var conflictPreviewResponse = await host.Client.SendAsync(Post("/api/sanitized-import/v1/previews", second));
        using var conflictPreview = JsonDocument.Parse(await conflictPreviewResponse.Content.ReadAsByteArrayAsync());
        using var conflict = await host.Client.SendAsync(Post(
            "/api/sanitized-import/v1/imports", second, conflictPreview.RootElement.GetProperty("preview_digest").GetString()!));

        AssertError(changed, HttpStatusCode.Conflict, "preview_changed");
        Assert.Equal(HttpStatusCode.Created, firstCommit.StatusCode);
        AssertError(conflict, HttpStatusCode.Conflict, "record_conflict");
    }

    [Fact(Timeout = 120_000)]
    public async Task Api_HostileArchiveMatrixUsesFixedErrorsAndNeverWritesPreviewOrCommitRows()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);

        foreach (var item in SanitizedImportArchiveValidationTests.HostileArchives())
        {
            using var preview = await host.Client.SendAsync(
                Post("/api/sanitized-import/v1/previews", item.Archive));
            using var commit = await host.Client.SendAsync(
                Post("/api/sanitized-import/v1/imports", item.Archive, new string('a', 64)));
            var expectedStatus = item.Error == "entry_limit_exceeded"
                ? HttpStatusCode.RequestEntityTooLarge
                : HttpStatusCode.UnprocessableEntity;

            AssertError(preview, expectedStatus, item.Error);
            AssertError(commit, expectedStatus, item.Error);
            Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
            Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Api_UnknownLengthStreamingBodyAtMaximumPlusOneReturns413WithoutImportWrites()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sanitized-import/v1/imports")
        {
            Content = new UnknownLengthContent(SanitizedExportLimits.MaximumUncompressedBytes + 1L),
        };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        request.Headers.Add("X-Sanitized-Import-Preview-Digest", new string('a', 64));
        Assert.Null(request.Content.Headers.ContentLength);

        using var response = await host.Client.SendAsync(request);

        AssertError(response, HttpStatusCode.RequestEntityTooLarge, "bundle_too_large");
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_history;"));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sanitized_import_records;"));
    }

    [Fact]
    public async Task RouteHandlers_RejectAuthorizationAndDigestBeforeReadingBody()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSanitizedImportStore(temp.DatabasePath, temp.TimeProvider);
        store.CreateSchema();
        var previewContext = Context(new ThrowOnReadStream());
        previewContext.Request.ContentLength = SanitizedExportLimits.MaximumUncompressedBytes + 1;
        var commitContext = Context(new ThrowOnReadStream());
        commitContext.Request.Headers["x-monitor-csrf"] = "local-monitor";
        var oversizedContext = Context(new ThrowOnReadStream());
        oversizedContext.Request.Headers["x-monitor-csrf"] = "local-monitor";
        oversizedContext.Request.ContentLength = SanitizedExportLimits.MaximumUncompressedBytes + 1;

        await SanitizedImportRoutes.PreviewAsync(previewContext, store);
        await SanitizedImportRoutes.CommitAsync(commitContext, store);
        await SanitizedImportRoutes.PreviewAsync(oversizedContext, store);

        Assert.Equal(StatusCodes.Status403Forbidden, previewContext.Response.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, commitContext.Response.StatusCode);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, oversizedContext.Response.StatusCode);
        Assert.Equal(0, ((ThrowOnReadStream)previewContext.Request.Body).ReadCount);
        Assert.Equal(0, ((ThrowOnReadStream)commitContext.Request.Body).ReadCount);
        Assert.Equal(0, ((ThrowOnReadStream)oversizedContext.Request.Body).ReadCount);
    }

    [Theory]
    [InlineData(400, 400, "invalid_request")]
    [InlineData(413, 413, "bundle_too_large")]
    public void ExceptionMap_UsesFixedMalformedBodyStatus(int exceptionStatus, int expectedStatus, string expectedError)
    {
        var mapped = SanitizedImportRoutes.MapUnhandledException(new BadHttpRequestException("synthetic", exceptionStatus));

        Assert.Equal(expectedStatus, mapped.Status);
        Assert.Equal(expectedError, mapped.Error);
    }

    private static Task<RunningMonitorHost> StartAsync(MonitorTempDirectory temp) => MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
    });

    private static HttpRequestMessage Post(string path, byte[] body, string? digest = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = Zip(body) };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        if (digest is not null) request.Headers.Add("X-Sanitized-Import-Preview-Digest", digest);
        return request;
    }

    private static ByteArrayContent Zip(byte[] body)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return content;
    }

    private static byte[] MatchingButIncorrectCrc(byte[] source)
    {
        var archive = source.ToArray();
        var central = FindLast(archive, 0x02014b50);
        var local = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(central + 42, 4)));
        archive[central + 16] ^= 0x01;
        archive[local + 14] ^= 0x01;
        return archive;
    }

    private static int FindLast(byte[] bytes, uint signature)
    {
        for (var index = bytes.Length - 4; index >= 0; index--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4)) == signature) return index;
        throw new InvalidDataException();
    }

    private static DefaultHttpContext Context(Stream body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.ContentType = "application/zip";
        context.Request.Body = body;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static void AssertError(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        using var json = JsonDocument.Parse(response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
        Assert.Equal(["error"], json.RootElement.EnumerateObject().Select(item => item.Name));
        Assert.Equal(error, json.RootElement.GetProperty("error").GetString());
    }

    private static void AssertNoStoreAndNoCors(HttpResponseMessage response)
    {
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private sealed class ThrowOnReadStream : Stream
    {
        internal int ReadCount { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) { ReadCount++; throw new InvalidOperationException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { ReadCount++; throw new InvalidOperationException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class UnknownLengthContent : HttpContent
    {
        private readonly long length;

        internal UnknownLengthContent(long length)
        {
            this.length = length;
            Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[1024 * 1024];
            var remaining = length;
            while (remaining > 0)
            {
                var count = (int)Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, count));
                remaining -= count;
            }
        }

        protected override bool TryComputeLength(out long contentLength)
        {
            contentLength = 0;
            return false;
        }

    }
}

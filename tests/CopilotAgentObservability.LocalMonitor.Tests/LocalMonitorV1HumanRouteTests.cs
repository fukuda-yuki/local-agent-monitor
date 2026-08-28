using System.Net;
using System.Net.Sockets;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1HumanRouteTests
{
    private const string UnsupportedEndpoint =
        """{"accepted":false,"error":"unsupported_endpoint","message":"Only /v1/traces is supported."}""";
    private const string RepositoryId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";
    private const string SessionId = "018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072";
    private const string ComparisonId = "018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073";

    private static readonly string[] ExactPrimaryPathValues =
    {
        "/",
        $"/repositories/{RepositoryId}/sessions",
        "/sessions",
        "/sessions/unassigned",
        $"/sessions/{SessionId}",
        $"/repositories/{RepositoryId}/comparisons/{ComparisonId}",
    };

    private static readonly string[] NearPathValues =
    {
        $"/sess%69ons/{SessionId}",
        $"/repositories/{RepositoryId}/sess%69ons",
        $"/repos%69tories/{RepositoryId}/comparisons/{ComparisonId}",
        "//",
        "//sessions",
        "///sessions/unassigned",
        "/sessions//unassigned",
        "/sessions%2Funassigned",
        "/sessions%2funassigned",
        $"/repositories%2F{RepositoryId}%2Fsessions",
        $"/repositories/{RepositoryId}%2Fsessions",
        $"/repositories/{RepositoryId}//sessions",
        $"/sessions%2F{SessionId}%2Fevents%2F{ComparisonId}%2Fcontent",
        $"/sessions/{SessionId}%2Fevents/{ComparisonId}/content",
        $"/sessions/{SessionId}/events%2F{ComparisonId}%2Fcontent",
        "/sessions-extra",
        "/repositories-extra",
    };

    public static TheoryData<string> ExactPrimaryPaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in ExactPrimaryPathValues) data.Add(path);
            return data;
        }
    }

    public static TheoryData<string> NearPaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in NearPathValues) data.Add(path);
            return data;
        }
    }

    public static TheoryData<string> HumanCandidatePaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in ExactPrimaryPathValues.Concat(NearPathValues)) data.Add(path);
            return data;
        }
    }

    public static TheoryData<string, string> DetailFailures => new()
    {
        { "workspace_too_large", "409|workspace_too_large|open_all_sessions" },
        { "local_monitor_ui_unavailable", "503|local_monitor_ui_unavailable|retry" },
        { "session_not_found", "404|session_not_found|open_all_sessions" },
    };

    public static TheoryData<string, string> InvalidPrimaryQueries => new()
    {
        { "/?unknown=secret", "open_repository_selection" },
        { $"/repositories/{RepositoryId}/sessions?settings=AI", "open_repository_selection" },
        { "/sessions?status=failed&status=failed", "open_all_sessions" },
        { "/sessions/unassigned?unknown=secret", "open_all_sessions" },
        { $"/sessions/{SessionId}?execution={SessionId}&execution={SessionId}", "open_all_sessions" },
        { $"/repositories/{RepositoryId}/comparisons/{ComparisonId}?unknown=secret", "open_repository_selection" },
    };

    [Theory]
    [MemberData(nameof(ExactPrimaryPaths))]
    public async Task UnintegratedOwnerRoutesReturnClosedUnavailableInsteadOfPlaceholderSuccess(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions());

        using var response = await host.Client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("data-page-state=\"local_monitor_ui_unavailable\"", html, StringComparison.Ordinal);
        Assert.Contains("data-recovery-action=\"retry\"", html, StringComparison.Ordinal);
        Assert.Contains("data-local-monitor-v1-host", html, StringComparison.Ordinal);
        Assert.Contains("data-route-kind", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-requested-section", html, StringComparison.Ordinal);
        Assert.Contains("/local-monitor-v1-shared.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Head_UsesGetRepresentationLengthAndWritesNoBody()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions());
        var get = await host.Client.GetAsync("/sessions");
        using var request = new HttpRequestMessage(HttpMethod.Head, "/sessions");

        using var head = await host.Client.SendAsync(request);

        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [MemberData(nameof(ExactPrimaryPaths))]
    public async Task EveryExactPrimaryRouteRejectsEveryUnsupportedMethodBeforeResolution(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions());

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete, HttpMethod.Options })
        {
            using var response = await host.Client.SendAsync(new HttpRequestMessage(method, path + "?unknown=secret"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
            Assert.Equal(["GET", "HEAD"], response.Content.Headers.Allow.Order(StringComparer.Ordinal));
            Assert.Equal(0, response.Content.Headers.ContentLength);
            Assert.Null(response.Content.Headers.ContentType);
        }
    }

    [Fact]
    public async Task MatchedMalformedAndNearPathsRemainDistinctAndDoNotReflectInput()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var malformed = await host.Client.GetAsync("/sessions/not-a-session?settings=ai");
        var malformedHtml = await malformed.Content.ReadAsStringAsync();
        using var near = await host.Client.GetAsync("/sessions/Unassigned");

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Contains("data-page-state=\"invalid_request\"", malformedHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-session", malformedHtml, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, near.StatusCode);
        Assert.Equal(0, near.Content.Headers.ContentLength);
        Assert.Null(near.Content.Headers.ContentType);
    }

    [Theory]
    [InlineData("/repositories/not-a-repository/sessions", "open_repository_selection")]
    [InlineData("/sessions/not-a-session", "open_all_sessions")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/comparisons/not-a-comparison", "open_repository_selection")]
    public async Task EveryIdentityBearingPrimaryTemplateUsesClosedMalformedRecovery(
        string path,
        string recovery)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var get = await host.Client.GetAsync(path);
        using var post = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Post, path + "?unknown=secret"));
        var html = await get.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        Assert.Contains("data-page-state=\"invalid_request\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-recovery-action=\"{recovery}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-", html, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(["GET", "HEAD"], post.Content.Headers.Allow.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(InvalidPrimaryQueries))]
    public async Task InvalidQueriesAcrossAllSixRoutesUseClosedRecoveryWithoutActivatingClientState(
        string path,
        string recovery)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions());

        using var get = await host.Client.GetAsync(path);
        using var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        var html = await get.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, get.StatusCode);
        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Contains("data-page-state=\"invalid_request\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-recovery-action=\"{recovery}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("settings=AI", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-route-kind", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/local-monitor-v1-shared.js", html, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NearPaths))]
    public async Task DuplicateSlashAndEncodedSeparatorNearPathsUseTheCanonicalEmpty404ForEveryMethod(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head, HttpMethod.Post })
        {
            var response = await SendRawAsync(host.Url, method.Method, path);
            Assert.StartsWith("HTTP/1.1 404", response.StatusLine, StringComparison.Ordinal);
            Assert.Equal("no-store", response.Headers["Cache-Control"]);
            Assert.Equal("0", response.Headers["Content-Length"]);
            Assert.DoesNotContain("Content-Type", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Allow", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(response.Body);
        }
    }

    [Theory]
    [MemberData(nameof(NearPaths))]
    public async Task SanitizedOnlyNearPathsUseTheCanonicalEmpty404ForGetAndHead(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true);

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            var response = await SendRawAsync(host.Url, method.Method, path);
            Assert.StartsWith("HTTP/1.1 404", response.StatusLine, StringComparison.Ordinal);
            Assert.Equal("no-store", response.Headers["Cache-Control"]);
            Assert.Equal("0", response.Headers["Content-Length"]);
            Assert.DoesNotContain("Content-Type", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("Allow", response.Headers.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Empty(response.Body);
        }
    }

    [Theory]
    [MemberData(nameof(HumanCandidatePaths))]
    public async Task SanitizedOnlyUnsupportedMethodsOnHumanCandidatesFallThroughToTheHostFallback(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true);

        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put })
        {
            var response = await SendRawAsync(host.Url, method.Method, path);
            Assert.StartsWith("HTTP/1.1 404", response.StatusLine, StringComparison.Ordinal);
            Assert.Equal("no-store", response.Headers["Cache-Control"]);
            Assert.Equal("application/json", response.Headers["Content-Type"]);
            Assert.Contains(UnsupportedEndpoint, Encoding.UTF8.GetString(response.Body), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task MatchedUnsupportedMethodWinsBeforeQueryAndHasTheClosedEnvelope()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/sessions/{SessionId}?unknown=secret");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET", "HEAD"], response.Content.Headers.Allow.Order(StringComparer.Ordinal));
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Null(response.Content.Headers.ContentType);
    }

    [Fact]
    public async Task SanitizedOnly_DoesNotExposePrimaryHostOrKnownSharedAsset()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true);

        foreach (var path in ExactPrimaryPathValues.Append("/local-monitor-v1-shared.js"))
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                using var response = await host.Client.SendAsync(new HttpRequestMessage(method, path));
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.True(
                    response.Content.Headers.ContentLength == 0,
                    $"{method} {path} returned Content-Length {response.Content.Headers.ContentLength?.ToString() ?? "<null>"}.");
                Assert.Null(response.Content.Headers.ContentType);
            }
        }
    }

    [Fact]
    public async Task RawDefault_ServesOnlyThePhysicalSharedAssetWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var shared = await host.Client.GetAsync("/local-monitor-v1-shared.js");
        using var future = await host.Client.GetAsync("/local-monitor-explorer.js");

        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal("no-store", shared.Headers.CacheControl?.ToString());
        Assert.Contains("LocalMonitorV1History", await shared.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, future.StatusCode);
        Assert.Equal(0, future.Content.Headers.ContentLength);
        Assert.Null(future.Content.Headers.ContentType);
    }

    [Fact]
    public async Task InvalidHostPrecedesHumanDispatchWithTheStrictJsonContract()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        request.Headers.Host = "example.com";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_host\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TechnicalRawEventAndExistingHumanPagesRetainTheirOwners()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var technicalPath = $"/sessions/{SessionId}/events/{ComparisonId}/content";

        using var technical = await host.Client.GetAsync(technicalPath);
        Assert.Equal(HttpStatusCode.NotFound, technical.StatusCode);
        Assert.Equal("{\"error\":\"session_event_content_not_found\"}", await technical.Content.ReadAsStringAsync());

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, technicalPath);
        invalidHostRequest.Headers.Host = "example.com";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
        Assert.Equal(HttpStatusCode.BadRequest, invalidHost.StatusCode);
        Assert.Equal(
            "{\"accepted\":false,\"error\":\"invalid_host\",\"message\":\"Host header must be loopback.\"}",
            await invalidHost.Content.ReadAsStringAsync());

        using var trace = await host.Client.GetAsync("/traces/trace-preserved");
        using var historical = await host.Client.GetAsync("/historical-analysis");
        Assert.Equal(HttpStatusCode.NotFound, trace.StatusCode);
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
        Assert.DoesNotContain("data-local-monitor-v1-host", await trace.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("data-local-monitor-v1-host", await historical.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DetailFailures))]
    public async Task DetailAuthorityFailuresMapToClosedHumanStatesWithoutGeneric500(string error, string expected)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: OwnerReadyOptions(new ThrowingDetailService(error)));

        using var response = await host.Client.GetAsync($"/sessions/{SessionId}?settings=ai");
        var html = await response.Content.ReadAsStringAsync();
        var parts = expected.Split('|');

        Assert.Equal(int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), (int)response.StatusCode);
        Assert.Contains($"data-page-state=\"{parts[1]}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-recovery-action=\"{parts[2]}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("internal_error", html, StringComparison.Ordinal);
        Assert.Contains("data-route-kind=\"SessionDetail\"", html, StringComparison.Ordinal);
        Assert.Contains("/local-monitor-v1-shared.js", html, StringComparison.Ordinal);
    }

    private static MonitorHostTestOptions OwnerReadyOptions(
        ILocalRepositorySessionDetailSnapshotService? detailService = null) => new()
    {
        AdditionalServices = services =>
        {
            services.AddSingleton<ILocalRepositoryScopeSnapshotService>(new ReadyScopeService());
            services.AddSingleton(detailService ?? new ReadyDetailService());
        },
    };

    private sealed class ReadyScopeService : ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new LocalRepositoryScopeSnapshot(request, [], []));
    }

    private sealed class ReadyDetailService : ILocalRepositorySessionDetailSnapshotService
    {
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
            LocalRepositorySessionDetailRequest request,
            CancellationToken cancellationToken)
        {
            var session = new LocalRepositoryScopeSessionSnapshot(
                request.SessionId,
                new SessionRow(request.SessionId),
                0,
                LocalRepositoryScopeAssignmentState.Unassigned,
                LocalRepositoryScopeAssignmentAuthority.None,
                null,
                [],
                true,
                true,
                true,
                LocalArchiveState.Active,
                0,
                true,
                null);
            return ValueTask.FromResult(new LocalRepositorySessionDetailSnapshot(
                session,
                new LocalWorkspaceSessionDetailContribution([], [], [], []),
                new string('1', 64)));
        }
    }

    private sealed class ThrowingDetailService(string error) : ILocalRepositorySessionDetailSnapshotService
    {
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
            LocalRepositorySessionDetailRequest request,
            CancellationToken cancellationToken) =>
            throw new LocalWorkspaceSessionDetailException(error);
    }

    private sealed record SessionRow(string SessionId) : ILocalRepositorySessionSnapshotRow;

    private static async Task<RawResponse> SendRawAsync(string hostUrl, string method, string rawTarget)
    {
        var uri = new Uri(hostUrl);
        using var client = new TcpClient();
        await client.ConnectAsync(uri.Host, uri.Port);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"{method} {rawTarget} HTTP/1.1\r\nHost: {uri.Host}:{uri.Port}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        var bytes = buffer.ToArray();
        var separator = "\r\n\r\n"u8;
        var headerLength = bytes.AsSpan().IndexOf(separator);
        Assert.True(headerLength >= 0);
        var headerText = Encoding.ASCII.GetString(bytes, 0, headerLength);
        var lines = headerText.Split("\r\n", StringSplitOptions.None);
        var headers = lines.Skip(1).ToDictionary(
            line => line[..line.IndexOf(':')],
            line => line[(line.IndexOf(':') + 1)..].Trim(),
            StringComparer.OrdinalIgnoreCase);
        return new(lines[0], headers, bytes[(headerLength + separator.Length)..]);
    }

    private sealed record RawResponse(
        string StatusLine,
        IReadOnlyDictionary<string, string> Headers,
        byte[] Body);
}

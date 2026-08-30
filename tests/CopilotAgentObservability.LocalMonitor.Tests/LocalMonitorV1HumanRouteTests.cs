using System.Net;
using System.Net.Sockets;
using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.LocalMonitor.Pages;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewEngines;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class LocalMonitorV1HumanRouteTests
{
    private const string UnsupportedEndpoint =
        """{"accepted":false,"error":"unsupported_endpoint","message":"Only /v1/traces is supported."}""";
    private const string RepositoryId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";
    private const string SessionId = "018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072";
    private const string ComparisonId = "018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073";
    private const string RepositorySelectionRenderer = "/Pages/Shared/LocalMonitorV1/_RepositorySelection.cshtml";
    private const string SessionExplorerRenderer = "/Pages/Shared/LocalMonitorV1/_SessionExplorer.cshtml";
    private const string SessionWorkspaceRenderer = "/Pages/Shared/LocalMonitorV1/_SessionWorkspace.cshtml";
    private const string RepositoryCompareRenderer = "/Pages/Shared/LocalMonitorV1/_RepositoryCompare.cshtml";

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

    public static TheoryData<string> UnintegratedPrimaryPaths
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var path in ExactPrimaryPathValues.Where(path =>
                         path == $"/repositories/{RepositoryId}/comparisons/{ComparisonId}")) data.Add(path);
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
        { $"/sessions/{SessionId}?analysis=not-a-run", "open_all_sessions" },
        { $"/sessions/{SessionId}?analysis={SessionId.ToUpperInvariant()}", "open_all_sessions" },
        { $"/sessions/{SessionId}?analysis={SessionId}&analysis={SessionId}", "open_all_sessions" },
        { $"/repositories/{RepositoryId}/comparisons/{ComparisonId}?unknown=secret", "open_repository_selection" },
    };

    public static TheoryData<string, string[]> RendererOwnership => new()
    {
        { RepositorySelectionRenderer, ["/"] },
        { SessionExplorerRenderer, [$"/repositories/{RepositoryId}/sessions", "/sessions", "/sessions/unassigned"] },
        { SessionWorkspaceRenderer, [$"/sessions/{SessionId}"] },
    };

    [Theory]
    [MemberData(nameof(UnintegratedPrimaryPaths))]
    public async Task UnknownComparisonReturnsClosedNotFoundInsteadOfPlaceholderUnavailable(string path)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions());

        using var response = await host.Client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Contains("data-page-state=\"comparison_not_found\"", html, StringComparison.Ordinal);
        Assert.Contains("data-recovery-action=\"open_repository_selection\"", html, StringComparison.Ordinal);
        Assert.Contains("data-local-monitor-v1-host", html, StringComparison.Ordinal);
        Assert.Contains("data-route-kind", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-requested-section", html, StringComparison.Ordinal);
        Assert.Contains("/local-monitor-v1-shared.js", html, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RendererOwnership))]
    public async Task ExactRendererWithSuccessfulResourceResolutionUpgradesOnlyItsOwnedRoutes(
        string renderer,
        string[] ownedRoutes)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: RendererOptions(renderer));

        foreach (var path in ExactPrimaryPathValues)
        {
            using var response = await host.Client.GetAsync(path);
            var expected = path == $"/repositories/{RepositoryId}/comparisons/{ComparisonId}"
                ? HttpStatusCode.NotFound
                : ownedRoutes.Contains(path) ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable;
            Assert.Equal(expected, response.StatusCode);
        }
    }

    [Theory]
    [InlineData(RendererLookupOutcome.Missing)]
    [InlineData(RendererLookupOutcome.Failed)]
    [InlineData(RendererLookupOutcome.Ambiguous)]
    public async Task MissingFailedOrAmbiguousRendererRemainsClosedUnavailable(RendererLookupOutcome outcome)
    {
        using var temp = new MonitorTempDirectory();
        var viewEngine = new ControlledRazorViewEngine(RepositorySelectionRenderer, outcome);
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: RendererOptions(viewEngine));

        using var response = await host.Client.GetAsync("/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(1, viewEngine.RendererLookupCount);
    }

    [Theory]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(FormatException))]
    [InlineData(typeof(NotSupportedException))]
    public async Task NonFatalRendererLookupFailureRemainsClosedUnavailableWithoutLeaking(Type exceptionType)
    {
        using var temp = new MonitorTempDirectory();
        const string exceptionMessage = "controlled_nonfatal_renderer_lookup_failure";
        var exception = (Exception)Activator.CreateInstance(exceptionType, exceptionMessage)!;
        var viewEngine = new ControlledRazorViewEngine(
            RepositorySelectionRenderer,
            RendererLookupOutcome.Available,
            exception);
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: RendererOptions(viewEngine));

        using var response = await host.Client.GetAsync("/");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.DoesNotContain(exceptionMessage, body, StringComparison.Ordinal);
        Assert.Equal(1, viewEngine.RendererLookupCount);
    }

    [Theory]
    [InlineData("repository_not_found", "/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/sessions", 404)]
    [InlineData("scope_unavailable", "/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/sessions", 503)]
    [InlineData("session_not_found", "/sessions/018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072", 404)]
    [InlineData("local_monitor_ui_unavailable", "/sessions/018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072", 503)]
    public async Task ExactRendererCannotUpgradeAMissingOrUnavailableResource(string error, string path, int status)
    {
        using var temp = new MonitorTempDirectory();
        var scopeService = path.StartsWith("/repositories/", StringComparison.Ordinal)
            ? new ThrowingScopeService(error)
            : null;
        var detailService = path.StartsWith("/sessions/", StringComparison.Ordinal)
            ? new ThrowingDetailService(error)
            : null;
        var renderer = path.StartsWith("/repositories/", StringComparison.Ordinal)
            ? SessionExplorerRenderer
            : SessionWorkspaceRenderer;
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: RendererOptions(renderer, scopeService: scopeService, detailService: detailService));

        using var response = await host.Client.GetAsync(path);

        Assert.Equal(status, (int)response.StatusCode);
    }

    [Theory]
    [InlineData(200, null, 200, null)]
    [InlineData(404, "comparison_not_found", 404, "comparison_not_found")]
    [InlineData(410, "comparison_expired", 410, "comparison_expired")]
    [InlineData(503, "persistence_busy", 503, "persistence_busy")]
    public async Task ComparisonRendererMapsImmutableReadDispositionToClosedHumanState(
        int applicationStatus,
        string? applicationError,
        int expectedStatus,
        string? expectedPageState)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: RendererOptions(
                RepositoryCompareRenderer,
                comparisonApplication: new StubComparisonApplication(applicationStatus, applicationError)));

        using var response = await host.Client.GetAsync($"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        _ = expectedPageState;
    }

    [Theory]
    [InlineData(404, "comparison_not_found", "open_repository_selection")]
    [InlineData(410, "comparison_expired", "open_repository_selection")]
    [InlineData(503, "persistence_busy", "retry")]
    public async Task ComparisonFailuresRenderClosedHumanState(
        int applicationStatus,
        string pageState,
        string recoveryAction)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: OwnerReadyOptions(comparisonApplication: new StubComparisonApplication(applicationStatus, pageState)));

        using var response = await host.Client.GetAsync($"/repositories/{RepositoryId}/comparisons/{ComparisonId}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(applicationStatus, (int)response.StatusCode);
        Assert.Contains($"data-page-state=\"{pageState}\"", html, StringComparison.Ordinal);
        Assert.Contains($"data-recovery-action=\"{recoveryAction}\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("internal_error", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComparisonResolvesRepositoryBeforeReadingComparison()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: RendererOptions(
                RepositoryCompareRenderer,
                scopeService: new ThrowingScopeService("repository_not_found"),
                comparisonApplication: new FailOnCallComparisonApplication()));

        using var response = await host.Client.GetAsync($"/repositories/{RepositoryId}/comparisons/{ComparisonId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SuccessfulRendererHeadMatchesTheGetRepresentationWithoutWritingABody()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: RendererOptions(RepositorySelectionRenderer));
        using var get = await host.Client.GetAsync("/");
        using var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/"));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
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
    public async Task RawDefault_ServesIntegratedPhysicalAssetsWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var shared = await host.Client.GetAsync("/local-monitor-v1-shared.js");
        using var explorer = await host.Client.GetAsync("/local-monitor-explorer.js");
        using var compare = await host.Client.GetAsync("/local-monitor-compare.js");
        using var future = await host.Client.GetAsync("/local-monitor-future.js");

        Assert.Equal(HttpStatusCode.OK, shared.StatusCode);
        Assert.Equal("no-store", shared.Headers.CacheControl?.ToString());
        Assert.Contains("LocalMonitorV1History", await shared.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, explorer.StatusCode);
        Assert.Equal("no-store", explorer.Headers.CacheControl?.ToString());
        Assert.Contains("local-monitor-session-search.request.v1", await explorer.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, compare.StatusCode);
        Assert.Equal("no-store", compare.Headers.CacheControl?.ToString());
        Assert.Contains("local-monitor-comparison-read.response.v1", await compare.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, future.StatusCode);
        Assert.Equal(0, future.Content.Headers.ContentLength);
        Assert.Null(future.Content.Headers.ContentType);
    }

    [Fact]
    public async Task RawDefault_SessionDetailRendersItsWorkspaceAndDedicatedAssetOnlyOnItsRoute()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions());

        using var detail = await host.Client.GetAsync($"/sessions/{SessionId}");
        using var explorer = await host.Client.GetAsync("/sessions");
        var detailHtml = await detail.Content.ReadAsStringAsync();
        var explorerHtml = await explorer.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains($"data-session-id=\"{SessionId}\"", detailHtml, StringComparison.Ordinal);
        Assert.Contains("data-session-workspace", detailHtml, StringComparison.Ordinal);
        Assert.Contains("/local-monitor-session-workspace.js", detailHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("data-session-workspace", explorerHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("/local-monitor-session-workspace.js", explorerHtml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("analysis=018f0000-0000-7000-8000-000000000071&settings=ai")]
    [InlineData("settings=ai&analysis=018f0000-0000-7000-8000-000000000071")]
    public async Task RawDefault_CanonicalExistingSessionAnalysisServesNormalSessionWorkspace(string query)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions(
            localAiApplication: new StubLocalAiApplication(new("018f0000-0000-7000-8000-000000000071", "succeeded", "session", SessionId, null, null))));
        using var response = await host.Client.GetAsync($"/sessions/{SessionId}?{query}"); var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("data-session-workspace", html, StringComparison.Ordinal); Assert.Contains("data-page-state=\"\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("&node=node-a8a773d6614d5030f505ff195b452dd6")]
    [InlineData("&execution=9a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6")]
    public async Task RawDefault_NodeAnalysisAcceptsOmittedOrExactAnchorSelection(string selection)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions(
            localAiApplication: new StubLocalAiApplication(new("018f0000-0000-7000-8000-000000000071", "succeeded", "node", SessionId, "node-a8a773d6614d5030f505ff195b452dd6", null))));

        using var response = await host.Client.GetAsync($"/sessions/{SessionId}?analysis=018f0000-0000-7000-8000-000000000071{selection}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data-session-workspace", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("&node=node-11111111111111111111111111111111")]
    [InlineData("&execution=8a5590c8-46e3-7069-af48-3844d2bf17a4")]
    [InlineData("&execution=8a5590c8-46e3-7069-af48-3844d2bf17a4&node=node-a8a773d6614d5030f505ff195b452dd6")]
    public async Task RawDefault_NodeAnalysisRejectsSelectionOutsideItsExactAnchor(string selection)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions(
            localAiApplication: new StubLocalAiApplication(new("018f0000-0000-7000-8000-000000000071", "succeeded", "node", SessionId, "node-a8a773d6614d5030f505ff195b452dd6", null))));

        using var response = await host.Client.GetAsync($"/sessions/{SessionId}?analysis=018f0000-0000-7000-8000-000000000071{selection}");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("data-page-state=\"analysis_run_not_found\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong_scope")]
    [InlineData("wrong_session")]
    public async Task RawDefault_AbsentOrMismatchedSessionAnalysisReturnsClosedNotFound(string disposition)
    {
        var run = disposition switch
        {
            "missing" => null,
            "wrong_scope" => new LocalAiRunStatusV1("018f0000-0000-7000-8000-000000000071", "succeeded", "repository", SessionId, null, null),
            _ => new LocalAiRunStatusV1("018f0000-0000-7000-8000-000000000071", "succeeded", "session", "018f0000-0000-7000-8000-000000000099", null, null),
        };
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OwnerReadyOptions(
            localAiApplication: new StubLocalAiApplication(run)));

        using var response = await host.Client.GetAsync($"/sessions/{SessionId}?analysis=018f0000-0000-7000-8000-000000000071");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("data-page-state=\"analysis_run_not_found\"", html, StringComparison.Ordinal);
        Assert.Contains("data-recovery-action=\"open_session_overview\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-session-workspace", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SanitizedOnly_DoesNotExposeSessionWorkspaceAssetOrSurface()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true);

        using var route = await host.Client.GetAsync($"/sessions/{SessionId}");
        using var asset = await host.Client.GetAsync("/local-monitor-session-workspace.js");
        using var compareAsset = await host.Client.GetAsync("/local-monitor-compare.js");

        Assert.Equal(HttpStatusCode.NotFound, route.StatusCode);
        Assert.DoesNotContain("data-session-workspace", await route.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, asset.StatusCode);
        Assert.Equal(0, asset.Content.Headers.ContentLength);
        Assert.Equal(HttpStatusCode.NotFound, compareAsset.StatusCode);
        Assert.Equal(0, compareAsset.Content.Headers.ContentLength);
    }

    [Fact]
    public async Task RawDefault_ExactPhysicalPrimaryAssetsUseTheExistingStaticMiddlewareForGetAndHead()
    {
        using var temp = new MonitorTempDirectory();
        var webRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "primary-assets"));
        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["local-monitor-repositories.js"] = "window.repositories = true;"u8.ToArray(),
            ["local-monitor-explorer.js"] = "window.explorer = true;"u8.ToArray(),
            ["local-monitor-compare.js"] = "window.compare = true;"u8.ToArray(),
            ["local-monitor-workspace.js"] = "window.workspace = true;"u8.ToArray(),
        };
        foreach (var (name, body) in assets) await File.WriteAllBytesAsync(Path.Combine(webRoot.FullName, name), body);
        using var provider = new PhysicalFileProvider(webRoot.FullName);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: AssetOptions(provider));

        foreach (var (name, body) in assets)
        {
            using var get = await host.Client.GetAsync('/' + name);
            using var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, '/' + name));

            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal(get.StatusCode, head.StatusCode);
            Assert.Equal("no-store", get.Headers.CacheControl?.ToString());
            Assert.Equal(get.Headers.CacheControl?.ToString(), head.Headers.CacheControl?.ToString());
            Assert.NotNull(get.Content.Headers.ContentType);
            Assert.Equal(get.Content.Headers.ContentType?.ToString(), head.Content.Headers.ContentType?.ToString());
            Assert.Equal(body.Length, get.Content.Headers.ContentLength);
            Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
            Assert.Equal(body, await get.Content.ReadAsByteArrayAsync());
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("throw")]
    [InlineData("directory")]
    public async Task RawDefault_UnavailablePrimaryAssetProviderRemainsClosedEmpty404(string outcome)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: AssetOptions(new ControlledPrimaryAssetProvider(outcome)));

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var response = await host.Client.SendAsync(
                new HttpRequestMessage(method, "/local-monitor-repositories.js"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(0, response.Content.Headers.ContentLength);
            Assert.Null(response.Content.Headers.ContentType);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
    }

    [Fact]
    public async Task PrimaryAssetAvailabilityCannotActivateAnotherAssetOrNonExactPath()
    {
        using var temp = new MonitorTempDirectory();
        var webRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "isolated-primary-asset"));
        await File.WriteAllTextAsync(
            Path.Combine(webRoot.FullName, "local-monitor-repositories.js"),
            "window.repositories = true;");
        using var provider = new PhysicalFileProvider(webRoot.FullName);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: AssetOptions(provider));

        foreach (var path in new[]
                 {
                     "/local-monitor-explorer.js",
                     "/LOCAL-MONITOR-REPOSITORIES.JS",
                     "/local-monitor-repositories.js/child",
                 })
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(0, response.Content.Headers.ContentLength);
            Assert.Null(response.Content.Headers.ContentType);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        var encoded = await SendRawAsync(host.Url, "GET", "/local-monitor-%72epositories.js");
        Assert.StartsWith("HTTP/1.1 404", encoded.StatusLine, StringComparison.Ordinal);
        Assert.Equal("no-store", encoded.Headers["Cache-Control"]);
        Assert.Equal("0", encoded.Headers["Content-Length"]);
        Assert.DoesNotContain("Content-Type", encoded.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(encoded.Body);
    }

    [Fact]
    public async Task SanitizedOnly_NeverServesAPhysicalPrimaryAsset()
    {
        using var temp = new MonitorTempDirectory();
        var webRoot = Directory.CreateDirectory(Path.Combine(temp.Path, "sanitized-primary-asset"));
        await File.WriteAllTextAsync(
            Path.Combine(webRoot.FullName, "local-monitor-repositories.js"),
            "window.repositories = true;");
        using var provider = new PhysicalFileProvider(webRoot.FullName);
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: true,
            testOptions: AssetOptions(provider));

        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            using var response = await host.Client.SendAsync(
                new HttpRequestMessage(method, "/local-monitor-repositories.js"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal(0, response.Content.Headers.ContentLength);
            Assert.Null(response.Content.Headers.ContentType);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
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
        ILocalRepositorySessionDetailSnapshotService? detailService = null,
        ILocalMonitorV1ComparisonApplication? comparisonApplication = null,
        ILocalAiAnalysisApplicationV1? localAiApplication = null) => new()
    {
        LocalMonitorV1ComparisonApplication = comparisonApplication,
        LocalAiAnalysisApplication = localAiApplication,
        AdditionalServices = services =>
        {
            services.AddSingleton<ILocalRepositoryScopeSnapshotService>(new ReadyScopeService());
            services.AddSingleton(detailService ?? new ReadyDetailService());
        },
    };

    private sealed class StubLocalAiApplication(LocalAiRunStatusV1? run) : ILocalAiAnalysisApplicationV1
    {
        public ValueTask<LocalAiStartResponseV1> StartSessionAsync(LocalAiSessionStartRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiStartResponseV1> StartNodeAsync(LocalAiNodeStartRequestV1 request, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiRunStatusV1?> ReadRunAsync(string runId, CancellationToken token) => ValueTask.FromResult(run?.RunId == runId ? run : null);
        public ValueTask<bool> CancelAsync(string runId, CancellationToken token) => throw new NotSupportedException();
        public ValueTask<LocalAiReportPageResponseV1> ReadReportsAsync(string sessionId, int? limit, string? cursor, CancellationToken token) => throw new NotSupportedException();
    }

    private static MonitorHostTestOptions AssetOptions(IFileProvider provider) => new()
    {
        PrimaryAssetFileProvider = provider,
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartLocalRepositoryCatalogHostedService = false,
    };

    private static MonitorHostTestOptions RendererOptions(
        string renderer,
        RendererLookupOutcome outcome = RendererLookupOutcome.Available,
        ILocalRepositoryScopeSnapshotService? scopeService = null,
        ILocalRepositorySessionDetailSnapshotService? detailService = null,
        ILocalMonitorV1ComparisonApplication? comparisonApplication = null) =>
        RendererOptions(new ControlledRazorViewEngine(renderer, outcome), scopeService, detailService, comparisonApplication);

    private static MonitorHostTestOptions RendererOptions(
        ControlledRazorViewEngine viewEngine,
        ILocalRepositoryScopeSnapshotService? scopeService = null,
        ILocalRepositorySessionDetailSnapshotService? detailService = null,
        ILocalMonitorV1ComparisonApplication? comparisonApplication = null) => new()
    {
        LocalMonitorV1ComparisonApplication = comparisonApplication,
        AdditionalServices = services =>
        {
            services.AddSingleton(scopeService ?? new ReadyScopeService());
            services.AddSingleton(detailService ?? new ReadyDetailService());
            services.AddSingleton<IRazorViewEngine>(viewEngine);
        },
    };

    private sealed class StubComparisonApplication(int status, string? error) : ILocalMonitorV1ComparisonApplication
    {
        public ValueTask<LocalMonitorV1ComparisonResponse> ExecuteAsync(
            LocalMonitorV1ComparisonOperation operation,
            string repositoryId,
            string? comparisonId,
            ReadOnlyMemory<byte> requestBody,
            string query,
            CancellationToken cancellationToken)
        {
            Assert.Equal(LocalMonitorV1ComparisonOperation.Read, operation);
            Assert.Equal(RepositoryId, repositoryId);
            Assert.Equal(ComparisonId, comparisonId);
            var entity = status == 200
                ? "{}"u8.ToArray()
                : Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}");
            return ValueTask.FromResult(new LocalMonitorV1ComparisonResponse(status, entity));
        }
    }

    private sealed class FailOnCallComparisonApplication : ILocalMonitorV1ComparisonApplication
    {
        public ValueTask<LocalMonitorV1ComparisonResponse> ExecuteAsync(
            LocalMonitorV1ComparisonOperation operation,
            string repositoryId,
            string? comparisonId,
            ReadOnlyMemory<byte> requestBody,
            string query,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("Comparison read must not run before repository resolution succeeds.");
    }

    private sealed class ReadyScopeService : ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<LocalRepositoryCatalogSnapshot> repositories = request.ScopeKind == LocalRepositoryScopeKind.Repository
                ? [new(request.RepositoryId!, "対象リポジトリ", 0, null, 0, LocalArchiveState.Active, 0)]
                : [];
            return ValueTask.FromResult(new LocalRepositoryScopeSnapshot(request, repositories, []));
        }
    }

    private sealed class ThrowingScopeService(string error) : ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                error == "repository_not_found" ? "local_repository_scope_repository_not_found" : error);
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
            var fact = new LocalWorkspaceFact<long>("not_observed", null);
            var activity = new LocalWorkspaceActivityFacts(fact, fact, fact, fact, fact);
            var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 1, fact, fact, fact, fact, fact, fact, fact, fact);
            var execution = new LocalWorkspaceExecutionDetail("9a5590c8-46e3-7069-af48-3844d2bf17a4", request.SessionId,
                "session_run", "run", 0, "completed", "completed", null, null, "missing", null, null, null, activity, tokens, ChildCount: 1, Latest: true);
            var previousExecution = execution with { ExecutionId = "8a5590c8-46e3-7069-af48-3844d2bf17a4", Latest = false };
            var node = new LocalWorkspaceNodeDetail("node-a8a773d6614d5030f505ff195b452dd6", request.SessionId, execution.ExecutionId,
                "session_event", "event", 0, null, "exact", "event", "recorded", "user.message", "completed", "completed", "missing", null, null, null,
                activity, tokens, null, null, null);
            return ValueTask.FromResult(new LocalRepositorySessionDetailSnapshot(
                session,
                new LocalWorkspaceSessionDetailContribution([execution, previousExecution], [node], [], []),
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

    public enum RendererLookupOutcome
    {
        Available,
        Missing,
        Failed,
        Ambiguous,
    }

    private sealed class ControlledRazorViewEngine(
        string renderer,
        RendererLookupOutcome outcome,
        Exception? lookupException = null) : IRazorViewEngine
    {
        public int RendererLookupCount { get; private set; }

        public ViewEngineResult FindView(ActionContext context, string viewName, bool isMainPage) =>
            GetView(null, viewName, isMainPage);

        public ViewEngineResult GetView(string? executingFilePath, string viewPath, bool isMainPage)
        {
            if (viewPath == "/Pages/LocalMonitorV1.cshtml")
                return ViewEngineResult.Found(viewPath, new ControlledView(viewPath));
            RendererLookupCount++;
            if (viewPath != renderer || outcome == RendererLookupOutcome.Missing)
                return ViewEngineResult.NotFound(viewPath, [viewPath]);
            if (lookupException is not null)
                throw lookupException;
            if (outcome == RendererLookupOutcome.Failed)
                throw new InvalidOperationException("controlled_renderer_failure");

            var resolvedPath = outcome == RendererLookupOutcome.Ambiguous
                ? viewPath + "|duplicate"
                : viewPath;
            return ViewEngineResult.Found(viewPath, new ControlledView(resolvedPath));
        }

        public RazorPageResult FindPage(ActionContext context, string pageName) =>
            new(pageName, [pageName]);

        public RazorPageResult GetPage(string? executingFilePath, string pagePath) =>
            new(pagePath, [pagePath]);

        public string? GetAbsolutePath(string? executingFilePath, string? pagePath) => pagePath;
    }

    private sealed class ControlledView(string path) : IView
    {
        public string Path { get; } = path;

        public Task RenderAsync(ViewContext context) => context.Writer.WriteAsync("<main data-controlled-view></main>");
    }

    private sealed class ControlledPrimaryAssetProvider(string outcome) : IFileProvider
    {
        public IFileInfo GetFileInfo(string subpath) => outcome switch
        {
            "missing" => new NotFoundFileInfo(subpath),
            "null" => null!,
            "throw" => throw new ArgumentException("controlled_primary_asset_failure"),
            "directory" => new ControlledDirectoryInfo(subpath),
            _ => throw new InvalidOperationException("unexpected controlled provider outcome"),
        };

        public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

        public IChangeToken Watch(string filter) => NullChangeToken.Singleton;
    }

    private sealed class ControlledDirectoryInfo(string name) : IFileInfo
    {
        public bool Exists => true;
        public long Length => -1;
        public string? PhysicalPath => null;
        public string Name { get; } = name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => true;
        public Stream CreateReadStream() => throw new InvalidOperationException("directory has no stream");
    }

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

using System.Net;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionExplorerRouteTests
{
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000138";

    public static TheoryData<string, string, string, string?> ExactScopes => new()
    {
        { $"/repositories/{RepositoryId}/sessions", "repository", "対象リポジトリ", RepositoryId },
        { "/sessions", "all", "すべてのセッション", null },
        { "/sessions/unassigned", "unassigned", "リポジトリ未設定のセッション", null },
    };

    [Theory]
    [MemberData(nameof(ExactScopes))]
    public async Task ExactScopeUsesTheSharedPhysicalExplorerAndOnlyItsAsset(
        string path,
        string expectedScope,
        string heading,
        string? expectedRepositoryId)
    {
        var snapshots = new RecordingScopeService();
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(snapshots));

        using var response = await host.Client.GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("data-session-explorer", html, StringComparison.Ordinal);
        Assert.Contains($"data-explorer-scope=\"{expectedScope}\"", html, StringComparison.Ordinal);
        Assert.Contains(heading, WebUtility.HtmlDecode(html), StringComparison.Ordinal);
        if (expectedRepositoryId is null)
            Assert.DoesNotContain("data-repository-id=", html, StringComparison.Ordinal);
        else
            Assert.Contains($"data-repository-id=\"{expectedRepositoryId}\"", html, StringComparison.Ordinal);
        Assert.Contains("/local-monitor-v1-shared.js", html, StringComparison.Ordinal);
        Assert.Contains("/local-monitor-explorer.js", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/local-monitor-repositories.js", html, StringComparison.Ordinal);

        var request = Assert.Single(snapshots.Requests);
        Assert.Equal(expectedScope, request.ScopeKind.ToString().ToLowerInvariant());
        Assert.Equal(expectedRepositoryId, request.RepositoryId);
    }

    [Fact]
    public async Task ExplorerPartialCannotActivateUnintegratedWorkspaceOrCompareRoutes()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new RecordingScopeService()));

        using var workspace = await host.Client.GetAsync("/sessions/018f0000-0000-7000-8000-000000000139");
        using var compare = await host.Client.GetAsync($"/repositories/{RepositoryId}/comparisons/018f0000-0000-7000-8000-000000000166");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, workspace.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, compare.StatusCode);
    }

    [Fact]
    public async Task ExactExplorerScopesRejectCrossOriginBeforeReadingRepositoryScope()
    {
        var snapshots = new RecordingScopeService();
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(snapshots));

        foreach (var path in new[] { $"/repositories/{RepositoryId}/sessions", "/sessions", "/sessions/unassigned" })
        {
            foreach (var (method, header, value) in new[]
                     {
                         ("GET", "Sec-Fetch-Site", "cross-site"),
                         ("HEAD", "Sec-Fetch-Site", "same-site"),
                         ("GET", "Origin", "http://evil.example.test"),
                         ("HEAD", "Origin", "http://evil.example.test"),
                     })
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                request.Headers.TryAddWithoutValidation(header, value);
                using var response = await host.Client.SendAsync(request);

                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
                Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
                Assert.Equal(34, response.Content.Headers.ContentLength);
                Assert.Equal(
                    method == "HEAD" ? string.Empty : "{\"error\":\"cross_origin_forbidden\"}",
                    await response.Content.ReadAsStringAsync());
                Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            }
        }

        Assert.Empty(snapshots.Requests);
    }

    private static MonitorHostTestOptions Options(ILocalRepositoryScopeSnapshotService snapshots) => new()
    {
        AdditionalServices = services =>
        {
            services.AddSingleton(snapshots);
            services.AddSingleton<ILocalRepositorySessionDetailSnapshotService>(new ReadyDetailService());
        },
    };

    private sealed class RecordingScopeService : ILocalRepositoryScopeSnapshotService
    {
        internal List<LocalRepositoryScopeRequest> Requests { get; } = [];

        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            IReadOnlyList<LocalRepositoryCatalogSnapshot> repositories =
            [
                new(
                    RepositoryId,
                    "対象リポジトリ",
                    3,
                    null,
                    0,
                    LocalArchiveState.Active,
                    0),
            ];
            return ValueTask.FromResult(new LocalRepositoryScopeSnapshot(request, repositories, []));
        }
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

    private sealed record SessionRow(string SessionId) : ILocalRepositorySessionSnapshotRow;
}

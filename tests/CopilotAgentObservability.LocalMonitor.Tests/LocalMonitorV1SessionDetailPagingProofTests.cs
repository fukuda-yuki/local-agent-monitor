using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailPagingProofTests
{
    [Fact]
    public async Task RuntimeSummaryAndTimelineEmptyResponsesEqualLiteralGoldensAndHeadLengths()
    {
        var snapshot = EmptySnapshot();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, new FixedDetailService(snapshot), new byte[32]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var repositoryRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repositoryRoot is not null && !File.Exists(Path.Combine(repositoryRoot.FullName, "CopilotAgentObservability.slnx")))
            repositoryRoot = repositoryRoot.Parent;
        Assert.NotNull(repositoryRoot);
        var fixtureRoot = Path.Combine(repositoryRoot.FullName, "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "LocalMonitorV1SessionDetail");

        await AssertExact("summary", "summary-empty.json", "");
        await AssertExact("timeline", "timeline-empty.json", $"?workspace_revision={snapshot.WorkspaceRevision}");

        async Task AssertExact(string route, string fixture, string query)
        {
            var expected = await File.ReadAllBytesAsync(Path.Combine(fixtureRoot, fixture));
            using var get = await client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/{route}{query}");
            Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
            Assert.Equal(expected, await get.Content.ReadAsByteArrayAsync());
            Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
            Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
            Assert.Equal(["no-store"], get.Headers.GetValues("Cache-Control"));

            using var head = await client.SendAsync(new(HttpMethod.Head, $"/api/local-monitor/v1/sessions/{SessionId}/{route}{query}"));
            Assert.Equal(get.StatusCode, head.StatusCode);
            Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        }
    }

    [Theory]
    [InlineData("summary", "")]
    [InlineData("timeline", "?workspace_revision=1111111111111111111111111111111111111111111111111111111111111111")]
    [InlineData("nodes/node-00000000000000000000000000000001", "?workspace_revision=1111111111111111111111111111111111111111111111111111111111111111")]
    public async Task EveryJsonRouteHasExactGetHeadAndMethodNotAllowedTransport(string route, string query)
    {
        var snapshot = Snapshot([]);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, new FixedDetailService(snapshot), new byte[32]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var uri = $"/api/local-monitor/v1/sessions/{SessionId}/{route}{query}";

        using var get = await client.GetAsync(uri);
        Assert.Equal(System.Net.HttpStatusCode.OK, get.StatusCode);
        var getBytes = await get.Content.ReadAsByteArrayAsync();
        Assert.Equal(getBytes.Length, get.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], get.Headers.GetValues("Cache-Control"));

        using var head = await client.SendAsync(new(HttpMethod.Head, uri));
        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(getBytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var put = await client.SendAsync(new(HttpMethod.Put, uri));
        Assert.Equal(System.Net.HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(["GET", "HEAD"], put.Content.Headers.Allow);
        Assert.Equal("{\"error\":\"method_not_allowed\"}", await put.Content.ReadAsStringAsync());
        Assert.Equal(30, put.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", put.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], put.Headers.GetValues("Cache-Control"));
    }

    [Fact]
    public async Task StaleRevisionWinsBeforeExecutionAndNodeMembershipWithoutMixingFacts()
    {
        var oldSnapshot = EmptySnapshot();
        var service = new FixedDetailService(oldSnapshot);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, new byte[32]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var summary = await client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        Assert.Equal(System.Net.HttpStatusCode.OK, summary.StatusCode);
        service.Snapshot = oldSnapshot with { WorkspaceRevision = new string('2', 64) };

        using var timeline = await client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={oldSnapshot.WorkspaceRevision}&execution_id={ExecutionId}");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, timeline.StatusCode);
        Assert.Equal("{\"error\":\"workspace_snapshot_stale\"}", await timeline.Content.ReadAsStringAsync());
        Assert.DoesNotContain(ExecutionId, await timeline.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var node = await client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/nodes/{RootNodeId}?workspace_revision={oldSnapshot.WorkspaceRevision}");
        Assert.Equal(System.Net.HttpStatusCode.Conflict, node.StatusCode);
        Assert.Equal("{\"error\":\"workspace_snapshot_stale\"}", await node.Content.ReadAsStringAsync());
        Assert.DoesNotContain(RootNodeId, await node.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public void TimelinePagesRecordedMissingAndInvalidNodesWithoutDuplicateOrDrop()
    {
        var nodes = Enumerable.Range(0, 241)
            .Select(index => Node(
                $"node-{index + 10:x32}",
                index < 80 ? "recorded" : index < 160 ? "missing" : "invalid",
                index < 80 ? 638917344000000000L + index : null,
                300 - index))
            .ToArray();
        var snapshot = Snapshot(nodes);
        var key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        var seen = new List<string>();
        string? after = null;

        do
        {
            using var page = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(
                snapshot, ExecutionId, null, 100, after, key));
            seen.AddRange(page.RootElement.GetProperty("items").EnumerateArray()
                .Select(static item => item.GetProperty("node_id").GetString()!));
            after = page.RootElement.GetProperty("next_cursor").GetString();
        }
        while (after is not null);

        Assert.Equal(241, seen.Count);
        Assert.Equal(241, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(nodes.OrderBy(TimeGroup).ThenBy(static node => node.StartUtcTicks ?? 0)
            .ThenBy(static node => node.SourceOrdinal).ThenBy(static node => node.NodeId, StringComparer.Ordinal)
            .Select(static node => node.NodeId), seen);
    }

    [Fact]
    public void TimelineCursorIsBoundToFilterAndRestartKey()
    {
        var snapshot = Snapshot(Enumerable.Range(0, 2).Select(index => Node($"node-{index + 10:x32}", "missing", null, index)).ToArray());
        var firstKey = new byte[32];
        using var first = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 1, null, firstKey));
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();
        Assert.NotNull(cursor);

        Assert.Equal("invalid_cursor", Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 2, cursor, firstKey)).Error);
        Assert.Equal("invalid_cursor", Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 1, cursor, Enumerable.Repeat((byte)1, 32).ToArray())).Error);

        var tampered = cursor![..^1] + (cursor[^1] == 'A' ? "E" : "A");
        Assert.Equal("invalid_cursor", Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, null, 1, tampered, firstKey)).Error);
    }

    [Fact]
    public void NodeChildrenUseTheSameFullStableOrderingAsTimeline()
    {
        var children = new[]
        {
            Node("node-00000000000000000000000000000012", "invalid", null, 0),
            Node("node-00000000000000000000000000000011", "missing", null, 99) with { RelationshipAuthority = "explicit" },
            Node("node-00000000000000000000000000000010", "recorded", 638917344000000010, 100),
            Node("node-00000000000000000000000000000013", "missing", null, 0) with { RelationshipAuthority = "unknown" },
        };
        var snapshot = Snapshot(children);
        using var json = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeNode(snapshot, RootNodeId));

        Assert.Equal(new[] { "node-00000000000000000000000000000010", "node-00000000000000000000000000000011", "node-00000000000000000000000000000012" },
            json.RootElement.GetProperty("related").GetProperty("children").EnumerateArray()
                .Select(static item => item.GetProperty("node_id").GetString()));
        Assert.Equal(4, json.RootElement.GetProperty("node").GetProperty("child_count").GetInt32());
        using var timeline = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot, ExecutionId, RootNodeId, 200, null, new byte[32]));
        Assert.Equal(new[] { "node-00000000000000000000000000000010", "node-00000000000000000000000000000013", "node-00000000000000000000000000000011", "node-00000000000000000000000000000012" },
            timeline.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("node_id").GetString()));
        Assert.Equal("unknown", timeline.RootElement.GetProperty("items")[1].GetProperty("relationship_authority").GetString());
    }

    [Fact]
    public void NodeRejectsTwoHundredAndOneChildrenWithoutPartialResponse()
    {
        var children = Enumerable.Range(0, 201).Select(index => Node($"node-{index + 10:x32}", "missing", null, index)).ToArray();
        var exception = Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeNode(Snapshot(children), RootNodeId));
        Assert.Equal("workspace_too_large", exception.Error);
        children[0] = children[0] with { RelationshipAuthority = "explicit" };
        children[^1] = children[^1] with { RelationshipAuthority = "unknown" };
        using var json = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeNode(Snapshot(children.Reverse().ToArray()), RootNodeId));
        var relatedChildren = json.RootElement.GetProperty("related").GetProperty("children");
        Assert.Equal(200, relatedChildren.GetArrayLength());
        Assert.Equal(Enumerable.Range(10, 200).Select(index => $"node-{index:x32}"),
            relatedChildren.EnumerateArray().Select(static item => item.GetProperty("node_id").GetString()));
        Assert.Equal("explicit", relatedChildren[0].GetProperty("relationship_authority").GetString());
        Assert.Equal(201, json.RootElement.GetProperty("node").GetProperty("child_count").GetInt32());
    }

    private static LocalRepositorySessionDetailSnapshot Snapshot(IReadOnlyList<LocalWorkspaceNodeDetail> children)
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(SessionId, 0, 0, "not_observed", null, "completed", "full",
            new("not_observed", []), new("not_observed", []), activity, tokens,
            "recorded", "2026-08-26T00:00:00.0000000+00:00", "2026-08-26T00:00:01.0000000+00:00", "2026-08-26T00:00:01.0000000+00:00", 1000, [], "revision");
        var scope = new LocalRepositoryScopeSessionSnapshot(SessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned,
            LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null);
        var execution = new LocalWorkspaceExecutionDetail(ExecutionId, SessionId, "session_run", "run-1", 0, "completed", "completed", null, null,
            "missing", null, null, null, activity, tokens, null, null, children.Count);
        var root = new LocalWorkspaceNodeDetail(RootNodeId, SessionId, ExecutionId, "execution_root", "run-1", 0, null, "exact", "execution",
            "not_observed", null, "completed", "completed", "missing", null, null, null, activity, tokens, null, null, null, children.Count);
        var detail = new LocalWorkspaceSessionDetailContribution([execution], [root, .. children], [], [], [], [], null, null, "canonical", "registry");
        return new(scope, detail, new string('1', 64));
    }

    private static LocalRepositorySessionDetailSnapshot EmptySnapshot()
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(SessionId, 0, 0, "not_observed", null, "unknown", "unbound",
            new("not_observed", []), new("not_observed", []), activity, tokens,
            "not_observed", null, null, null, null, [], "revision");
        var scope = new LocalRepositoryScopeSessionSnapshot(SessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned,
            LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null);
        return new(scope, new([], [], [], [], [], [], null, null, "canonical", "registry"), new string('0', 64));
    }

    private static LocalWorkspaceNodeDetail Node(string id, string timeAuthority, long? startTicks, long ordinal)
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        return new(id, SessionId, ExecutionId, "session_event", id, ordinal, RootNodeId, "exact", "event", "not_observed", null,
            "completed", "completed", timeAuthority, startTicks, startTicks, startTicks is null ? null : 0,
            new(zero, zero, zero, zero, zero), new("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none), null, null, id);
    }

    private static int TimeGroup(LocalWorkspaceNodeDetail node) => node.TimeAuthority switch { "recorded" => 0, "missing" => 1, _ => 2 };

    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string ExecutionId = "018f0000-0000-7000-8000-000000000003";
    private const string RootNodeId = "node-00000000000000000000000000000001";

    private sealed class FixedDetailService(LocalRepositorySessionDetailSnapshot snapshot) : ILocalRepositorySessionDetailSnapshotService
    {
        internal LocalRepositorySessionDetailSnapshot Snapshot { get; set; } = snapshot;

        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
            LocalRepositorySessionDetailRequest request,
            CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot);
    }
}

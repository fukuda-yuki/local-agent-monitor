using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1InvestigationStoreTests
{
    private const string Search = """{"schema_version":"local-monitor-session-search.request.v1","scope":"all","repository_id":null,"archive_scope":"active_only","from":null,"to":null,"source":[],"model":["Exact-Model"],"status":[],"has_skill":null,"has_subagent":null,"has_error":null,"has_retry":null,"q":"synthetic label","cursor":null,"limit":25,"investigation_unit":"work_session"}""";

    [Fact]
    public void Receipt_RetainsOnlyValidatedNavigationAndExpiresAbsolutely()
    {
        var clock = new Clock();
        var store = new LocalMonitorV1InvestigationStore(clock);
        using var body = JsonDocument.Parse(Body());
        var handle = store.Save(body.RootElement);
        Assert.Matches("^[0-9a-f]{64}$", handle!);
        Assert.True(store.TryGet(handle!, out var saved, out var search));
        Assert.Equal("synthetic label", search!.QueryOriginal);
        Assert.Equal("work_session", search.InvestigationUnit);
        Assert.Equal(25, search.Limit);
        Assert.Equal(500, saved.GetProperty("scroll_y").GetInt32());
        clock.Now += TimeSpan.FromMinutes(29);
        Assert.True(store.TryGet(handle!, out _, out _));
        clock.Now += TimeSpan.FromMinutes(1);
        Assert.False(store.TryGet(handle!, out _, out _));
        Assert.False(new LocalMonitorV1InvestigationStore(clock).TryGet(handle!, out _, out _));
    }

    [Fact]
    public void Receipt_EvictsOldestAtThirtyTwoAndRejectsUnboundedOrRawFields()
    {
        var clock = new Clock();
        var store = new LocalMonitorV1InvestigationStore(clock);
        using var body = JsonDocument.Parse(Body());
        var first = store.Save(body.RootElement)!;
        string last = first;
        for (var i = 0; i < 32; i++) { clock.Now += TimeSpan.FromSeconds(1); last = store.Save(body.RootElement)!; }
        Assert.False(store.TryGet(first, out _, out _));
        Assert.True(store.TryGet(last, out _, out _));
        var invalid = JsonNode.Parse(Body())!.AsObject();
        invalid["raw_content"] = "synthetic forbidden content";
        using var invalidBody = JsonDocument.Parse(invalid.ToJsonString());
        Assert.Null(store.Save(invalidBody.RootElement));
        invalid.Remove("raw_content"); invalid["scroll_y"] = 10000001;
        using var tooLarge = JsonDocument.Parse(invalid.ToJsonString());
        Assert.Null(store.Save(tooLarge.RootElement));
        invalid["scroll_y"] = "500";
        using var wrongType = JsonDocument.Parse(invalid.ToJsonString());
        Assert.Null(store.Save(wrongType.RootElement));
    }

    private static string Body() => new JsonObject
    {
        ["schema_version"] = "local-monitor-investigation.request.v1", ["search"] = JsonNode.Parse(Search),
        ["workspace_revision"] = new string('a', 64), ["cohorts"] = new JsonObject { ["a"] = new JsonArray(), ["b"] = new JsonArray() }, ["scroll_y"] = 500,
    }.ToJsonString();
    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}

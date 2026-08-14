using System.Net;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2IsolationIntegrationTests
{
    [Fact]
    public async Task ProducerParserAndHostV2Path_LeaveSessionQueueAndStoreUntouched()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        var queue = new SessionEventQueue();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            SessionStore = store,
            SessionEventQueue = queue,
            StartSessionWriter = false,
        });
        var capability = new OpaqueRuntimeCapability();

        Assert.True(SkillInvocationNormalizedJsonV1.TryWrite("native-v2-session", RequiredOnlyEvent(), out var body));
        var batch = SkillInvocationV2Parser.Parse(Assert.IsType<byte[]>(body), capability);

        Assert.Same(capability, batch.RuntimeCapability);
        Assert.Equal(0, queue.Count);
        Assert.Empty(store.ListMostRecent(10));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v2/events")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, queue.Count);
        Assert.Empty(store.ListMostRecent(10));
        Assert.Null(store.GetProjectionState("session-normalizer"));
    }

    [Fact]
    public async Task FrozenV1SkillInvoked_KeepsNoContentAndUnsupportedMetricWithEmpty204Response()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            SessionStore = store,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent(FrozenV1SkillInvokedEnvelope, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-CAO-Session-Event-Version", "1");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());
        var session = Assert.IsType<ObservedSession>(store.Resolve(SessionSourceSurface.CopilotSdk, "frozen-v1-session"));
        var persisted = Assert.Single(Assert.IsType<SessionDetail>(store.GetDetail(session.SessionId)).Events);
        Assert.Equal("skill.invoked", persisted.Type);
        Assert.Equal(SessionContentState.Unsupported, persisted.ContentState);
        var content = await store.ReadContentAsync(session.SessionId, persisted.EventId, CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.NotFound, content.Disposition);
        Assert.Null(content.Lease);
        Assert.Equal(1L, store.GetProjectionState("session-normalizer")?.UnsupportedEventVersionCount);
    }

    private const string FrozenV1SkillInvokedEnvelope = """
        {"schema_version":1,"source_adapter":"copilot-sdk-stream","source_surface":"copilot-sdk","native_session_id":"frozen-v1-session","events":[{"source_event_id":"frozen-v1-skill","type":"skill.invoked","occurred_at":"2026-08-14T00:00:00Z","payload":{"name":"synthetic-skill","path":"skills/SKILL.md","content":"must-not-be-persisted"}}]}
        """;

    private static SkillInvokedEvent RequiredOnlyEvent() => new()
    {
        Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
        Timestamp = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
        Data = new SkillInvokedData
        {
            Name = "skill-name",
            Path = "skills/SKILL.md",
            Content = "body",
        },
    };

    private sealed class OpaqueRuntimeCapability : ISkillInvocationV2RuntimeCapability;
}

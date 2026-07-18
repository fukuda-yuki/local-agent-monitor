using System.Reflection;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionCompatibilityContractTests
{
    [Fact]
    public async Task ExpiredContentRoute_PreservesExactUtf8Bytes()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { TimeProvider = time });
        using var ingest = IngestRequest(Envelope());
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(ingest)).StatusCode);
        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var sessionId = list!.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString();
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        var eventId = detail!.RootElement.GetProperty("events")[0].GetProperty("event_id").GetString();
        time.Advance(TimeSpan.FromDays(90));

        using var response = await host.Client.GetAsync($"/sessions/{sessionId}/events/{eventId}/content");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var responseBytes = await response.Content.ReadAsByteArrayAsync();
        var projection = RetentionType("RetentionSessionV1Projection");
        var bytes = (byte[])projection.GetProperty("ExpiredContentResponseUtf8", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        Assert.Equal(bytes, responseBytes);
        Assert.Equal("{\"error\":\"raw_content_expired\",\"content_state\":\"expired_pending_deletion\"}"u8.ToArray(), bytes);
        Assert.DoesNotContain((byte)'\n', bytes);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    [Fact]
    public void RetentionDtos_ExposeOnlyAllowlistedFields()
    {
        var type = RetentionType("RetentionItemSummary");
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AttemptCount", "CapturedAt", "DeletedAt", "DeletionStartedAt", "ErrorCode", "ExpiresAt",
                "InventoryCategory", "ItemId", "PolicyId", "PolicyVersion", "QueuedAt", "ReadDeniedAt",
                "RetryAt", "RetryExhausted", "State", "StoreKind"
            },
            properties);
    }

    [Fact]
    public async Task FrozenSessionRoutes_ExecuteNotCapturedReadableUnknownAndSanitizedControls()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var ingest = IngestRequest("""{"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"hook-unknown","native_session_id":"native-1","events":[{"source_event_id":"event-1","type":"UserPromptSubmit","occurred_at":"2026-07-11T00:00:00Z","payload":{"message":"synthetic"}},{"source_event_id":"usage-1","type":"assistant.usage","occurred_at":"2026-07-11T00:00:01Z","payload":{}}]}""");
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(ingest)).StatusCode);
        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var sessionId = list!.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString()!;
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        var events = detail!.RootElement.GetProperty("events").EnumerateArray().ToArray();
        var readableEvent = events.Single(item => item.GetProperty("content_state").GetString() == "available").GetProperty("event_id").GetString();
        var uncapturedEvent = events.Single(item => item.GetProperty("content_state").GetString() == "not_captured").GetProperty("event_id").GetString();

        using var readable = await host.Client.GetAsync($"/sessions/{sessionId}/events/{readableEvent}/content");
        Assert.Equal(HttpStatusCode.OK, readable.StatusCode);
        Assert.Equal("application/json", readable.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", readable.Headers.CacheControl?.ToString());
        using var readableJson = JsonDocument.Parse(await readable.Content.ReadAsStreamAsync());
        Assert.Equal(new[] { "captured_at", "content", "content_kind", "event_id", "expires_at" }, readableJson.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(x => x).ToArray());
        Assert.Equal("synthetic", readableJson.RootElement.GetProperty("content").GetProperty("message").GetString());

        foreach (var id in new[] { uncapturedEvent!, Guid.CreateVersion7().ToString() })
        {
            using var missing = await host.Client.GetAsync($"/sessions/{sessionId}/events/{id}/content");
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal("application/json", missing.Content.Headers.ContentType?.MediaType);
            Assert.Equal("{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), await missing.Content.ReadAsByteArrayAsync());
        }
        using var unknownSession = await host.Client.GetAsync($"/sessions/{Guid.CreateVersion7()}/events/{Guid.CreateVersion7()}/content");
        Assert.Equal("{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), await unknownSession.Content.ReadAsByteArrayAsync());

        await using var sanitized = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true);
        using var metadata = await sanitized.Client.GetAsync($"/api/session-workspace/sessions/{sessionId}");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        using var absent = await sanitized.Client.GetAsync($"/sessions/{sessionId}/events/{readableEvent}/content");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Null(absent.Content.Headers.ContentType);
    }

    private static Type RetentionType(string name) =>
        typeof(SqliteSessionStore).Assembly.GetType($"CopilotAgentObservability.Persistence.Sqlite.Retention.{name}")
        ?? throw new Xunit.Sdk.XunitException($"Retention contract type '{name}' is missing.");

    private static HttpRequestMessage IngestRequest(string json) => new(HttpMethod.Post, "/api/session-ingest/v1/events")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
        Headers = { { "X-CAO-Session-Event-Version", "1" } },
    };

    private static string Envelope() => """
        {"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"hook-unknown","native_session_id":"native-1","events":[{"source_event_id":"event-1","type":"UserPromptSubmit","occurred_at":"2026-07-11T00:00:00Z","payload":{"message":"synthetic"}}]}
        """;
}

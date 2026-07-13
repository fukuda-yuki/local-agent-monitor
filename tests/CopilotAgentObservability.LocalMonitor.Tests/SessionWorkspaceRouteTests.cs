using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionWorkspaceRouteTests
{
    [Fact]
    public async Task Ingest_CommitsBatchAndExposesSanitizedAndRawReads()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var request = IngestRequest(Envelope(payload: """{"message":"synthetic token=remove-me sk-abcdefgh","api_key":"remove-me","nested":{"password":"remove-me","kept":7}}"""));
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync());

        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var item = Assert.Single(list!.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(
            ["session_id", "status", "completeness", "source_surfaces", "repository", "workspace", "started_at", "ended_at", "last_seen_at", "raw_retention_state", "source_diagnostic", "binding_state", "completeness_reason_codes", "content_state"],
            item.EnumerateObject().Select(property => property.Name));
        var sessionId = item.GetProperty("session_id").GetString();
        Assert.Equal("partial", item.GetProperty("completeness").GetString());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("source_diagnostic").ValueKind);
        Assert.Equal("otel_only", item.GetProperty("binding_state").GetString());
        Assert.Empty(item.GetProperty("completeness_reason_codes").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, item.GetProperty("content_state").ValueKind);
        Assert.False(item.TryGetProperty("payload", out _));

        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        Assert.Equal(
            ["session", "human_evaluation", "native_ids", "runs", "events"],
            detail!.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Null, detail.RootElement.GetProperty("human_evaluation").ValueKind);
        var eventItem = Assert.Single(detail!.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(
            ["event_id", "run_id", "source_surface", "parent_event_id", "status", "type", "occurred_at", "content_state"],
            eventItem.EnumerateObject().Select(property => property.Name));
        var eventId = eventItem.GetProperty("event_id").GetString();
        Assert.Equal("available", eventItem.GetProperty("content_state").GetString());
        Assert.False(eventItem.TryGetProperty("payload", out _));

        using var resolve = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/resolve?source_surface=hook-unknown&native_session_id=native-1");
        Assert.Equal("bound", resolve!.RootElement.GetProperty("binding_status").GetString());
        Assert.Equal(sessionId, resolve.RootElement.GetProperty("session_id").GetString());

        using var contentRequest = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{sessionId}/events/{eventId}/content");
        contentRequest.Headers.Add("Sec-Fetch-Site", "same-origin");
        using var contentResponse = await host.Client.SendAsync(contentRequest);
        Assert.Equal(HttpStatusCode.OK, contentResponse.StatusCode);
        Assert.Equal("no-store", contentResponse.Headers.CacheControl?.ToString());
        var content = JsonDocument.Parse(await contentResponse.Content.ReadAsStringAsync());
        var raw = content.RootElement.GetProperty("content").GetString();
        Assert.Contains("synthetic", raw, StringComparison.Ordinal);
        Assert.Contains("\"kept\":7", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remove-me", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abcdefgh", raw, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "application/json", 400, "unsupported_session_event_version")]
    [InlineData("2", "application/json", 400, "unsupported_session_event_version")]
    [InlineData("1", "text/plain", 415, "unsupported_media_type")]
    public async Task Ingest_UsesFixedVersionAndMediaTypeFailures(string? version, string contentType, int status, string error)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = IngestRequest(Envelope(), version, contentType);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal($$"""{"error":"{{error}}"}""", await response.Content.ReadAsStringAsync());
        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        Assert.Empty(list!.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Ingest_RejectsInvalidBatchWithoutWriting()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = IngestRequest("""{"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"hook-unknown","native_session_id":"native-1","events":[]}""");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"invalid_session_event_request"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("""{"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"hook-unknown","native_session_id":"native-1","unknown_envelope_field":true,"events":[{"source_event_id":"event-1","type":"SessionStart","occurred_at":"2026-07-11T00:00:00Z","payload":{}}]}""")]
    [InlineData("""{"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"hook-unknown","native_session_id":"native-1","events":[{"source_event_id":"event-1","type":"SessionStart","occurred_at":"2026-07-11T00:00:00Z","payload":{},"unknown_event_field":true}]}""")]
    [InlineData("""{"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"copilot-cli","native_session_id":"native-2","explicit_link":{"source_surface":"hook-unknown","native_session_id":"native-1","kind":"resume","unknown_link_field":true},"events":[{"source_event_id":"event-1","type":"SessionStart","occurred_at":"2026-07-11T00:00:00Z","payload":{}}]}""")]
    public async Task Ingest_RejectsUnknownFieldsAtEveryEnvelopeLevel(string json)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var response = await host.Client.SendAsync(IngestRequest(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("""{"error":"invalid_session_event_request"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task IngestToNormalizer_CopiesEnvelopeProvenanceAndPreservesExistingIdentity()
    {
        using var temp = new MonitorTempDirectory();
        var queue = new SessionEventQueue();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            SessionEventQueue = queue,
            StartSessionWriter = false,
            SessionCommitTimeout = TimeSpan.FromSeconds(5),
        });
        var sessionId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var observedAt = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
        var session = new ObservedSession(
            sessionId, ObservedSessionStatus.Active, SessionCompleteness.Partial,
            null, null, observedAt, null, observedAt, SessionRawRetentionState.Expiring, observedAt, observedAt);
        var existingEvent = new ObservedSessionEvent(
            eventId, sessionId, null, SessionSourceSurface.ClaudeCode, null, null, null,
            "claude-code-hook", "event-1", "SessionStart", observedAt, SessionContentState.Available,
            "old-source", "old-adapter", new string('b', 64), "old-normalization");
        var store = DispatchProxy.Create<ISessionStore, RecordingSessionStoreProxy>();
        var recorder = (RecordingSessionStoreProxy)(object)store;
        recorder.ExistingSession = session;
        recorder.ExistingDetail = new SessionDetail(
            session,
            [new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, "native-1", SessionBindingKind.Native, observedAt)],
            [],
            [existingEvent]);
        var json = """
            {
              "schema_version":1,
              "source_adapter":"claude-code-hook",
              "source_surface":"claude-code",
              "native_session_id":"native-1",
              "source_application_version":"2.1.207+exact",
              "adapter_version":"claude-hook-v1",
              "schema_fingerprint":"__FINGERPRINT__",
              "normalization_version":"session-normalization-v1",
              "events":[
                {"source_event_id":"event-1","type":"UserPromptSubmit","occurred_at":"2026-07-12T00:01:00Z","payload":{"source_application_version":"payload-must-not-win","adapter_version":"payload-must-not-win"}},
                {"source_event_id":"event-2","type":"Stop","occurred_at":"2026-07-12T00:02:00Z","payload":{"normalization_version":"payload-must-not-win"}}
              ]
            }
            """.Replace("__FINGERPRINT__", new string('a', 64), StringComparison.Ordinal);

        var responseTask = host.Client.SendAsync(IngestRequest(json));
        var queued = await queue.Reader.ReadAsync();
        queue.MarkDequeued();
        new SessionEventNormalizer(store, new FixedTimeProvider(observedAt.AddMinutes(3))).NormalizeAndWrite(queued.Envelope);
        queued.Complete(SessionEventCommitStatus.Committed);
        using var response = await responseTask;

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var batch = Assert.IsType<SessionWriteBatch>(recorder.WrittenBatch);
        Assert.Equal(sessionId, batch.Detail.Session.SessionId);
        Assert.Equal(eventId, Assert.Single(batch.Detail.Events, item => item.SourceEventId == "event-1").EventId);
        Assert.All(batch.Detail.Events, item =>
        {
            Assert.Equal("2.1.207+exact", item.SourceApplicationVersion);
            Assert.Equal("claude-hook-v1", item.AdapterVersion);
            Assert.Equal(new string('a', 64), item.SchemaFingerprint);
            Assert.Equal("session-normalization-v1", item.NormalizationVersion);
        });
    }

    [Theory]
    [InlineData("2026-02-30T00:00:00Z", "valid.type", "{}")]
    [InlineData("2026-07-11T00:00:00", "valid.type", "{}")]
    [InlineData("2026-07-11T00:00:00Z", "1invalid", "{}")]
    [InlineData("2026-07-11T00:00:00Z", "valid.type", "[]")]
    public async Task Ingest_RejectsInvalidEventGrammarWithoutWriting(string occurredAt, string type, string payload)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var json = $$"""
            {"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"hook-unknown","native_session_id":"native-1","events":[
              {"source_event_id":"event-1","type":"{{type}}","occurred_at":"{{occurredAt}}","payload":{{payload}}}
            ]}
            """;

        using var response = await host.Client.SendAsync(IngestRequest(json));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        Assert.Empty(list!.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Ingest_ExplicitResumeBindsOnlyToExactExistingNativeIdentity()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(IngestRequest(Envelope()))).StatusCode);
        var linked = """
            {"schema_version":1,"source_adapter":"copilot-compatible-hook","source_surface":"copilot-cli","native_session_id":"native-2",
             "explicit_link":{"source_surface":"hook-unknown","native_session_id":"native-1","kind":"resume"},
             "events":[{"source_event_id":"event-2","type":"Stop","occurred_at":"2026-07-11T00:01:00Z","payload":{}}]}
            """;

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(IngestRequest(linked))).StatusCode);
        using var first = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/resolve?source_surface=hook-unknown&native_session_id=native-1");
        using var second = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/resolve?source_surface=copilot-cli&native_session_id=native-2");
        Assert.Equal(first!.RootElement.GetProperty("session_id").GetString(), second!.RootElement.GetProperty("session_id").GetString());
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{first.RootElement.GetProperty("session_id").GetString()}");
        Assert.Contains(detail!.RootElement.GetProperty("native_ids").EnumerateArray(), item => item.GetProperty("binding_kind").GetString() == "explicit_resume");

        var missing = linked.Replace("native-2", "native-3", StringComparison.Ordinal).Replace("native-1", "missing", StringComparison.Ordinal).Replace("event-2", "event-3", StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.SendAsync(IngestRequest(missing))).StatusCode);
    }

    [Fact]
    public async Task Ingest_StoresUnknownValidTypeAsUnsupportedForThatSession()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var unknown = Envelope().Replace("UserPromptSubmit", "future.valid_event", StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(IngestRequest(unknown))).StatusCode);
        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var sessionId = list!.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString();
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        Assert.Equal("unsupported", detail!.RootElement.GetProperty("events")[0].GetProperty("content_state").GetString());
        using var status = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/status");
        Assert.Equal(1, status!.RootElement.GetProperty("unsupported_event_version_count").GetInt64());
    }

    [Fact]
    public async Task Reads_UseFrozenQueryAndIdentifierErrors()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        Assert.Equal("{\"error\":\"invalid_session_workspace_query\"}", await (await host.Client.GetAsync("/api/session-workspace/sessions?limit=0")).Content.ReadAsStringAsync());
        Assert.Equal("{\"error\":\"invalid_session_id\"}", await (await host.Client.GetAsync("/api/session-workspace/sessions/not-a-uuid")).Content.ReadAsStringAsync());
        Assert.Equal("{\"error\":\"session_not_found\"}", await (await host.Client.GetAsync($"/api/session-workspace/sessions/{Guid.CreateVersion7()}")).Content.ReadAsStringAsync());
        Assert.Equal("{\"error\":\"invalid_session_resolution_request\"}", await (await host.Client.GetAsync("/api/session-workspace/resolve?source_surface=bad&native_session_id=x")).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawContent_IsSameOriginAndAbsentInSanitizedOnlyMode()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var ingest = IngestRequest(Envelope());
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.SendAsync(ingest)).StatusCode);
        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var sessionId = list!.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString();
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        var eventId = detail!.RootElement.GetProperty("events")[0].GetProperty("event_id").GetString();

        using var crossSite = new HttpRequestMessage(HttpMethod.Get, $"/sessions/{sessionId}/events/{eventId}/content");
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSiteResponse = await host.Client.SendAsync(crossSite);
        Assert.Equal(HttpStatusCode.Forbidden, crossSiteResponse.StatusCode);
        Assert.Equal("no-store", crossSiteResponse.Headers.CacheControl?.ToString());

        await using var sanitized = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true);
        using var absent = await sanitized.Client.GetAsync($"/sessions/{sessionId}/events/{eventId}/content");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);

        using var obsoletePath = await host.Client.GetAsync($"/api/session-workspace/sessions/{sessionId}/events/{eventId}/content");
        Assert.Equal(HttpStatusCode.NotFound, obsoletePath.StatusCode);
    }

    [Fact]
    public async Task Status_UsesFixedSanitizedShape()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var status = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/status");

        var root = status!.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Equal("ready", root.GetProperty("normalizer_status").GetString());
        Assert.Equal(0, root.GetProperty("unsupported_event_version_count").GetInt64());
        Assert.True(root.TryGetProperty("projection_cursor", out _));
        Assert.Equal(0, root.GetProperty("projection_backlog").GetInt64());
    }

    [Fact]
    public async Task Ingest_DropsReasoningAndDeltasAndKeepsUsageMetadataWithoutContent()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var json = """
            {"schema_version":1,"source_adapter":"copilot-sdk-stream","source_surface":"copilot-sdk","native_session_id":"sdk-1","events":[
              {"source_event_id":"capture","type":"capture.started","occurred_at":"2026-07-11T00:00:00Z","payload":{"gap_before_capture":true}},
              {"source_event_id":"usage","type":"assistant.usage","occurred_at":"2026-07-11T00:00:01Z","payload":{"input_tokens":10}},
              {"source_event_id":"reasoning","type":"assistant.reasoning","occurred_at":"2026-07-11T00:00:02Z","payload":{"content":"do-not-store"}},
              {"source_event_id":"delta","type":"assistant.message_delta","occurred_at":"2026-07-11T00:00:03Z","payload":{"deltaContent":"do-not-store"}}
            ]}
            """;

        using var response = await host.Client.SendAsync(IngestRequest(json));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var list = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        var sessionId = list!.RootElement.GetProperty("items")[0].GetProperty("session_id").GetString();
        using var detail = await host.Client.GetFromJsonAsync<JsonDocument>($"/api/session-workspace/sessions/{sessionId}");
        var events = detail!.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(2, events.Length);
        Assert.Contains(events, item => item.GetProperty("type").GetString() == "capture.started" && item.GetProperty("status").GetString() == "gap_before_capture");
        Assert.Contains(events, item => item.GetProperty("type").GetString() == "assistant.usage" && item.GetProperty("content_state").GetString() == "not_captured");
        Assert.DoesNotContain(events, item => item.GetProperty("type").GetString()!.Contains("reasoning", StringComparison.Ordinal));
        Assert.DoesNotContain(events, item => item.GetProperty("type").GetString()!.Contains("delta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ingest_EnforcesOneMiBAndCommitTimeoutMappings()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartSessionWriter = false,
            SessionCommitTimeout = TimeSpan.FromMilliseconds(20),
        });
        using var oversized = IngestRequest(new string('x', 1_048_577));
        using var tooLarge = await host.Client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("""{"error":"request_too_large"}""", await tooLarge.Content.ReadAsStringAsync());

        using var valid = IngestRequest(Envelope());
        using var timeout = await host.Client.SendAsync(valid);
        Assert.Equal(HttpStatusCode.GatewayTimeout, timeout.StatusCode);
        Assert.Equal("""{"error":"session_event_commit_timeout"}""", await timeout.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Ingest_ReturnsQueueFullWhenBoundedWriterQueueCannotAcceptWork()
    {
        using var temp = new MonitorTempDirectory();
        var queue = new SessionEventQueue(capacity: 1);
        var queuedEnvelope = JsonSerializer.Deserialize<SessionIngestEnvelope>(Envelope())!;
        Assert.True(queue.TryEnqueue(queuedEnvelope, out _));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            SessionEventQueue = queue,
            StartSessionWriter = false,
        });

        using var response = await host.Client.SendAsync(IngestRequest(Envelope().Replace("event-1", "event-2", StringComparison.Ordinal)));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("""{"error":"session_event_queue_full"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawContent_ReturnsExpiredContract()
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

        using var expiredList = await host.Client.GetFromJsonAsync<JsonDocument>("/api/session-workspace/sessions");
        Assert.Equal("expired_pending_deletion", expiredList!.RootElement.GetProperty("items")[0].GetProperty("raw_retention_state").GetString());

        using var response = await host.Client.GetAsync($"/sessions/{sessionId}/events/{eventId}/content");
        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("""{"error":"raw_content_expired","content_state":"expired_pending_deletion"}""", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("permission-request.json", "PermissionRequest")]
    [InlineData("post-tool-use-failure.json", "PostToolUseFailure")]
    [InlineData("stop-failure.json", "StopFailure")]
    [InlineData("session-end.json", "SessionEnd")]
    public void ClaudeHookMappedEvent_NormalizerPersistsRecognizedRawOnlyEvidenceAsSupported(
        string fixtureName,
        string eventType)
    {
        var capturedAt = DateTimeOffset.Parse("2026-07-13T12:34:56Z");
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "TestData", "Claude", "hooks", fixtureName)));
        var metadata = new ClaudeHookSourceMetadata(
            "claude-hook-v1", "session-normalization-v1", "3.4.5-synthetic", null);
        Assert.True(ClaudeHookEventMapper.TryMap(fixture.RootElement, capturedAt, metadata, true, out var envelope));
        var store = DispatchProxy.Create<ISessionStore, RecordingSessionStoreProxy>();
        var recorder = (RecordingSessionStoreProxy)(object)store;

        new SessionEventNormalizer(store, new FixedTimeProvider(capturedAt.AddMinutes(1))).NormalizeAndWrite(envelope!);

        var batch = Assert.IsType<SessionWriteBatch>(recorder.WrittenBatch);
        var mappedEvent = Assert.Single(batch.Detail.Events);
        Assert.Equal(eventType, mappedEvent.Type);
        Assert.Equal(SessionContentState.Available, mappedEvent.ContentState);
        Assert.Null(mappedEvent.ParentEventId);
        Assert.Null(mappedEvent.TraceId);
        Assert.Null(mappedEvent.Status);
        Assert.Single(batch.Content);
    }

    private static HttpRequestMessage IngestRequest(string json, string? version = "1", string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
        {
            Content = new StringContent(json, Encoding.UTF8, contentType),
        };
        if (version is not null)
        {
            request.Headers.Add("X-CAO-Session-Event-Version", version);
        }
        return request;
    }

    private static string Envelope(string payload = """{"message":"synthetic"}""") => $$"""
        {
          "schema_version": 1,
          "source_adapter": "copilot-compatible-hook",
          "source_surface": "hook-unknown",
          "native_session_id": "native-1",
          "events": [
            {
              "source_event_id": "event-1",
              "type": "UserPromptSubmit",
              "occurred_at": "2026-07-11T00:00:00Z",
              "payload": {{payload}}
            }
          ]
        }
        """;

    private class RecordingSessionStoreProxy : DispatchProxy
    {
        public ObservedSession? ExistingSession { get; set; }
        public SessionDetail? ExistingDetail { get; set; }
        public SessionWriteBatch? WrittenBatch { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            nameof(ISessionStore.Resolve) => ExistingSession,
            nameof(ISessionStore.GetDetail) => ExistingDetail,
            nameof(ISessionStore.Write) => Capture(args),
            _ => throw new NotSupportedException(targetMethod?.Name),
        };

        private object? Capture(object?[]? args)
        {
            WrittenBatch = Assert.IsType<SessionWriteBatch>(Assert.Single(args!));
            return null;
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

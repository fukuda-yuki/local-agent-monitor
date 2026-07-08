using System.Net;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorRawViewTests
{
    [Fact]
    public async Task RawDetail_AbsentUnderSanitizedOnly_Returns404()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync($"/traces/{id}/raw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RawDetail_ByDefault_RendersInertEscapedHtmlWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync($"/traces/{id}/raw");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        // Stored markup is shown as escaped, inert text — never as live markup.
        Assert.Contains("&lt;script&gt;", body);
        Assert.DoesNotContain("<script", body);
    }

    [Fact]
    public async Task RawDetail_CrossSiteFetchIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("cross_origin_forbidden", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawDetail_ForeignOriginIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        request.Headers.TryAddWithoutValidation("Origin", "http://evil.example.com");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task RawDetail_SameOriginTopLevelNavigationIsAllowed()
    {
        using var temp = new MonitorTempDirectory();
        var id = SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/traces/{id}/raw");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RawDetail_UnknownIdReturns404()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/999999/raw");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("raw_record_not_found", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawDetail_CursorApisStaySanitized()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecord(temp);
        await using var host = await StartHostAsync(temp);

        var ingestions = await host.Client.GetStringAsync("/api/monitor/ingestions");
        var traces = await host.Client.GetStringAsync("/api/monitor/traces");

        foreach (var marker in new[] { "<script>", "leak-marker@example.com" })
        {
            Assert.DoesNotContain(marker, ingestions);
            Assert.DoesNotContain(marker, traces);
        }
    }

    [Fact]
    public async Task PromptLabel_AbsentUnderSanitizedOnly_Returns404()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecordWithPrompt(temp, "trace-with-prompt", "What does this function do?");
        await using var host = await StartHostAsync(temp, sanitizedOnly: true);

        var response = await host.Client.GetAsync("/traces/trace-with-prompt/prompt-label");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PromptLabel_ByDefault_ReturnsExtractedLabelWithNoStore()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecordWithPrompt(temp, "trace-with-prompt", "What does this function do?");
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/trace-with-prompt/prompt-label");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("\"trace_id\":\"trace-with-prompt\"", body);
        Assert.Contains("\"prompt_label\":\"What does this function do?\"", body);
    }

    [Fact]
    public async Task PromptLabel_ScansPastFirstRawRecordWithoutPrompt()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecordWithoutPrompt(temp, "trace-with-prompt");
        SeedRawRecordWithPrompt(temp, "trace-with-prompt", "What does this function do?");
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/trace-with-prompt/prompt-label");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"prompt_label\":\"What does this function do?\"", body);
    }

    [Fact]
    public async Task PromptLabel_CrossSiteFetchIsForbidden()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecordWithPrompt(temp, "trace-with-prompt", "What does this function do?");
        await using var host = await StartHostAsync(temp);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/traces/trace-with-prompt/prompt-label");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("cross_origin_forbidden", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task PromptLabel_UnknownTraceId_ReturnsNullLabelNot404()
    {
        using var temp = new MonitorTempDirectory();
        SeedRawRecordWithPrompt(temp, "trace-with-prompt", "What does this function do?");
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/traces/unknown-trace-id/prompt-label");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"trace_id\":\"unknown-trace-id\"", body);
        Assert.Contains("\"prompt_label\":null", body);
    }

    private static long SeedRawRecord(MonitorTempDirectory temp)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-raw",
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: ScriptAndPiiPayload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
        return id;
    }

    private static long SeedRawRecordWithPrompt(MonitorTempDirectory temp, string traceId, string promptText)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var payloadJson = PromptPayloadTemplate
            .Replace("TRACE_ID_PLACEHOLDER", traceId)
            .Replace("PROMPT_TEXT_PLACEHOLDER", promptText);
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(3));
        return id;
    }

    private static long SeedRawRecordWithoutPrompt(MonitorTempDirectory temp, string traceId)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var payloadJson = PromptlessPayloadTemplate.Replace("TRACE_ID_PLACEHOLDER", traceId);
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(1));
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
        return id;
    }

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp, bool sanitizedOnly = false) =>
        MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

    private const string ScriptAndPiiPayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-raw","spanId":"1111111111111111","name":"<script>alert(1)</script>"}
        ]}]}]}
        """;

    private const string PromptPayloadTemplate = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"TRACE_ID_PLACEHOLDER","spanId":"1000","name":"chat gpt-4o",
           "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"PROMPT_TEXT_PLACEHOLDER"}}
           ]}
        ]}]}]}
        """;

    private const string PromptlessPayloadTemplate = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"TRACE_ID_PLACEHOLDER","spanId":"0001","name":"setup",
           "startTimeUnixNano":"1709999999000000000","endTimeUnixNano":"1710000000000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"setup"}}
           ]}
        ]}]}]}
        """;

}

using System.Net;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// Sprint18 sanitized overview endpoint (`GET /api/monitor/overview`, D044).
/// Time is pinned through <see cref="MonitorHostTestOptions.TimeProvider"/> so the
/// local-calendar period windows are deterministic.
/// </summary>
public class MonitorOverviewEndpointTests
{
    private static readonly DateTimeOffset PinnedNow = new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Overview_EmptyStore_ReturnsZeroKpiAnd24HourBuckets()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        await using var host = await StartHostAsync(temp);

        var overview = await GetJsonAsync(host, "/api/monitor/overview?period=today");
        var root = overview.RootElement;

        Assert.Equal("today", root.GetProperty("period").GetString());
        Assert.False(string.IsNullOrEmpty(root.GetProperty("range").GetProperty("start").GetString()));
        Assert.False(string.IsNullOrEmpty(root.GetProperty("range").GetProperty("end").GetString()));
        var kpi = root.GetProperty("kpi");
        Assert.Equal(0, kpi.GetProperty("tokens_total").GetInt64());
        Assert.Equal(0, kpi.GetProperty("trace_count").GetInt32());
        Assert.Equal(0, kpi.GetProperty("error_trace_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, kpi.GetProperty("cache_read_rate_pct").ValueKind);
        Assert.Equal(JsonValueKind.Null, kpi.GetProperty("tokens_change_pct").ValueKind);
        Assert.Equal(0, root.GetProperty("per_model").GetArrayLength());
        Assert.Equal(24, root.GetProperty("hourly_tokens").GetArrayLength());
    }

    [Fact]
    public async Task Overview_OmittedPeriod_DefaultsToToday()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        await using var host = await StartHostAsync(temp);

        var overview = await GetJsonAsync(host, "/api/monitor/overview");

        Assert.Equal("today", overview.RootElement.GetProperty("period").GetString());
    }

    [Theory]
    [InlineData("/api/monitor/overview?period=yesterday")]
    [InlineData("/api/monitor/overview?period=90d")]
    public async Task Overview_RejectsUnsupportedPeriodWith400(string path)
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_query", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Overview_ComputesKpiFromCacheAwareRows()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        // Today: one cache-bearing trace (input 1000 / output 200 / cache read 700 /
        // cache creation 90) and one pre-v4-style trace without cache attributes
        // (input 500 / output 100) that must not skew the cache rates.
        SeedTrace(store, "trace-cache", ChatPayload("trace-cache", "gpt-4o", input: 1000, output: 200, cacheRead: 700, cacheCreation: 90), PinnedNow.AddHours(-1));
        SeedTrace(store, "trace-plain", ChatPayload("trace-plain", "gpt-4.1", input: 500, output: 100, cacheRead: null, cacheCreation: null), PinnedNow.AddHours(-2));
        // Previous period (yesterday): 600 tokens for the comparison KPI.
        SeedTrace(store, "trace-prev", ChatPayload("trace-prev", "gpt-4o", input: 500, output: 100, cacheRead: null, cacheCreation: null), PinnedNow.AddHours(-24));
        await using var host = await StartHostAsync(temp);

        var overview = await GetJsonAsync(host, "/api/monitor/overview?period=today");
        var kpi = overview.RootElement.GetProperty("kpi");

        Assert.Equal(1800, kpi.GetProperty("tokens_total").GetInt64());
        Assert.Equal(600, kpi.GetProperty("tokens_previous_period").GetInt64());
        Assert.Equal(200.0, kpi.GetProperty("tokens_change_pct").GetDouble());
        // effective input = (1500 - 700) + 700 * 0.1 = 870
        Assert.Equal(870, kpi.GetProperty("effective_input_tokens").GetInt64());
        // rates over the cache-aware input only (1000, not 1500)
        Assert.Equal(70.0, kpi.GetProperty("cache_read_rate_pct").GetDouble());
        Assert.Equal(63.0, kpi.GetProperty("cache_compression_pct").GetDouble());
        Assert.Equal(2, kpi.GetProperty("trace_count").GetInt32());
        Assert.Equal(0, kpi.GetProperty("error_trace_count").GetInt32());

        var perModel = overview.RootElement.GetProperty("per_model").EnumerateArray().ToList();
        Assert.Equal(2, perModel.Count);
        Assert.Equal("gpt-4o", perModel[0].GetProperty("model").GetString());
        Assert.Equal(1200, perModel[0].GetProperty("total_tokens").GetInt64());
        Assert.Equal(700, perModel[0].GetProperty("cache_read_tokens").GetInt64());
        Assert.Equal(70.0, perModel[0].GetProperty("cache_read_rate_pct").GetDouble());
        Assert.Equal(JsonValueKind.Null, perModel[1].GetProperty("cache_read_rate_pct").ValueKind);

        var hourlySum = overview.RootElement.GetProperty("hourly_tokens").EnumerateArray()
            .Sum(hour => hour.GetProperty("total_tokens").GetInt64());
        Assert.Equal(1800, hourlySum);
    }

    [Fact]
    public async Task Overview_SevenDayPeriod_IncludesYesterday()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedTrace(store, "trace-today", ChatPayload("trace-today", "gpt-4o", input: 100, output: 0, cacheRead: null, cacheCreation: null), PinnedNow.AddHours(-1));
        SeedTrace(store, "trace-yesterday", ChatPayload("trace-yesterday", "gpt-4o", input: 200, output: 0, cacheRead: null, cacheCreation: null), PinnedNow.AddHours(-24));
        SeedTrace(store, "trace-ancient", ChatPayload("trace-ancient", "gpt-4o", input: 400, output: 0, cacheRead: null, cacheCreation: null), PinnedNow.AddDays(-40));
        await using var host = await StartHostAsync(temp);

        var overview = await GetJsonAsync(host, "/api/monitor/overview?period=7d");
        var kpi = overview.RootElement.GetProperty("kpi");

        Assert.Equal(300, kpi.GetProperty("tokens_total").GetInt64());
        Assert.Equal(2, kpi.GetProperty("trace_count").GetInt32());
    }

    [Fact]
    public async Task Overview_NeverReturnsRawContentOrPii()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        SeedTrace(store, "trace-pii", SensitivePayload, PinnedNow.AddHours(-1));
        await using var host = await StartHostAsync(temp);

        var response = await host.Client.GetAsync("/api/monitor/overview?period=today");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();

        foreach (var marker in new[] { "SECRET_PROMPT_TEXT_MARKER", "SECRET_TOOL_ARGS_MARKER", "USER-ID-SECRET-MARKER", "leak-marker@example.com" })
        {
            Assert.DoesNotContain(marker, json);
        }
    }

    private static Task<RunningMonitorHost> StartHostAsync(MonitorTempDirectory temp) =>
        MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            TimeProvider = new UtcPinnedTimeProvider(PinnedNow),
        });

    /// <summary>Pinned clock with a UTC local zone so the local-calendar period windows are machine-timezone independent.</summary>
    private sealed class UtcPinnedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static void SeedTrace(RawTelemetryStore store, string traceId, string payloadJson, DateTimeOffset receivedAt)
    {
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: receivedAt,
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), receivedAt);
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), receivedAt);
    }

    private static string ChatPayload(string traceId, string model, int input, int output, int? cacheRead, int? cacheCreation)
    {
        var cacheAttributes = string.Empty;
        if (cacheRead is not null)
        {
            cacheAttributes += $$$""",{"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"{{{cacheRead}}}"}}""";
        }

        if (cacheCreation is not null)
        {
            cacheAttributes += $$$""",{"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"{{{cacheCreation}}}"}}""";
        }

        return $$$"""
            {"resourceSpans":[{"resource":{"attributes":[
              {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
            ]},"scopeSpans":[{"spans":[
              {"traceId":"{{{traceId}}}","spanId":"1111","name":"chat {{{model}}}","attributes":[
                {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
                {"key":"gen_ai.response.model","value":{"stringValue":"{{{model}}}"}},
                {"key":"gen_ai.usage.input_tokens","value":{"intValue":"{{{input}}}"}},
                {"key":"gen_ai.usage.output_tokens","value":{"intValue":"{{{output}}}"}}{{{cacheAttributes}}}
              ]}
            ]}]}]}
            """;
    }

    private const string SensitivePayload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.id","value":{"stringValue":"USER-ID-SECRET-MARKER"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-pii","spanId":"1111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

    private static async Task<JsonDocument> GetJsonAsync(RunningMonitorHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }
}

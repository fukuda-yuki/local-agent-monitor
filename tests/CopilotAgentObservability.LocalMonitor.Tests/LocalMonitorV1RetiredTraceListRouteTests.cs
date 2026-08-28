using System.Net;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1RetiredTraceListRouteTests
{
    public static TheoryData<string, string> RetiredSpellingsAndMethods
    {
        get
        {
            var values = new TheoryData<string, string>();
            foreach (var path in new[]
                     {
                         "/traces", "/traces/", "/TRACES", "/TrAcEs/",
                         "/traces?unknown=value", "/TrAcEs/?period=all&x=1",
                     })
            {
                foreach (var method in new[] { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS", "RETIRE" })
                    values.Add(path, method);
            }
            return values;
        }
    }

    public static TheoryData<string> SurvivingOrNearPaths => new()
    {
        "/traces//",
        "/tr%61ces",
        "/traces%2f",
        "/traces/technical-evidence",
        "/traces/1/raw",
        "/traces/technical-evidence/spans/span/detail",
        "/traces/technical-evidence/prompt-label",
        "/traces/technical-evidence/analysis",
        "/traces/technical-evidence/analysis/runs/1",
    };

    [Theory]
    [MemberData(nameof(RetiredSpellingsAndMethods))]
    public async Task ExactRetiredListSpellingsReturnTheSameEmptyNoStore404ForEveryMethod(
        string path,
        string method)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(0, response.Content.Headers.ContentLength);
        Assert.Null(response.Content.Headers.ContentType);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        foreach (var forbidden in new[] { "Allow", "Location", "ETag", "Set-Cookie" })
        {
            Assert.False(response.Headers.NonValidated.Contains(forbidden));
            Assert.False(response.Content.Headers.NonValidated.Contains(forbidden));
        }
    }

    [Theory]
    [MemberData(nameof(SurvivingOrNearPaths))]
    public void RetiredClassifierDoesNotClaimEncodedDoubleSlashOrTechnicalDescendantPaths(string rawTarget)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<IHttpRequestFeature>(new HttpRequestFeature { RawTarget = rawTarget });

        Assert.False(LocalMonitorV1HumanRoutes.IsRetiredTraceList(context));
    }

    [Fact]
    public async Task RetiringTheListPreservesTechnicalDetailFrozenApiAndHistoricalAnalysisOwners()
    {
        using var temp = new MonitorTempDirectory();
        var rawRecordId = SeedTechnicalTrace(temp);
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var detail = await host.Client.GetAsync("/traces/technical-evidence");
        using var raw = await host.Client.GetAsync($"/traces/{rawRecordId}/raw");
        using var spans = await host.Client.GetAsync("/traces/technical-evidence/spans/span-1/detail");
        using var promptLabel = await host.Client.GetAsync("/traces/technical-evidence/prompt-label");
        using var analysisRun = await host.Client.GetAsync("/traces/technical-evidence/analysis/runs/1");
        using var traceListApi = await host.Client.GetAsync("/api/monitor/trace-list?period=all&limit=1");
        using var historical = await host.Client.GetAsync("/historical-analysis");

        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Contains("technical-evidence", await detail.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, raw.StatusCode);
        Assert.Contains("technical-evidence", await raw.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, spans.StatusCode);
        Assert.Contains("span-1", await spans.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, promptLabel.StatusCode);
        Assert.Contains("\"prompt_label\":null", await promptLabel.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, analysisRun.StatusCode);
        Assert.Contains("analysis_run_not_found", await analysisRun.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, traceListApi.StatusCode);
        Assert.Equal(HttpStatusCode.OK, historical.StatusCode);
    }

    private static long SeedTechnicalTrace(MonitorTempDirectory temp)
    {
        const string payload = """
            {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[
              {"traceId":"technical-evidence","spanId":"span-1","name":"chat",
               "startTimeUnixNano":"1710000000000000000","endTimeUnixNano":"1710000001000000000",
               "attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}}]}
            ]}]}]}
            """;
        var store = new RawTelemetryStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "technical-evidence",
            DateTimeOffset.UnixEpoch.AddMinutes(1),
            null,
            payload);
        var id = store.Insert(record);
        store.ApplyProjection(
            id,
            record.Source,
            record.ReceivedAt,
            MonitorProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(
            id,
            MonitorSpanProjectionBuilder.Build(record),
            DateTimeOffset.UnixEpoch.AddMinutes(3));
        return id;
    }

    [Fact]
    public async Task InvalidHostStillPrecedesTheRetiredListOutcome()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/traces");
        request.Headers.Host = "example.com";

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("{\"error\":\"invalid_host\"}", await response.Content.ReadAsStringAsync());
    }
}

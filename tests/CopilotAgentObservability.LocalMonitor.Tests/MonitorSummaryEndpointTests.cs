using System.Net;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Projection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorSummaryEndpointTests
{
    [Fact]
    public async Task Summary_EmptyStore_ReturnsZeroCountAndNullHighlights()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var summary = await GetJsonAsync(host, "/api/monitor/summary");
        var root = summary.RootElement;

        Assert.Equal(50, root.GetProperty("scope").GetProperty("limit").GetInt32());
        Assert.Equal(0, root.GetProperty("scope").GetProperty("trace_count").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("latest_trace").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("top_token_trace").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("error_trace").ValueKind);
        Assert.Equal(0, root.GetProperty("per_model_summary").GetArrayLength());
        Assert.Equal(0, root.GetProperty("per_client_kind_summary").GetArrayLength());
    }

    [Fact]
    public async Task Summary_MultiModelMultiClientKind_SubtotalsReconcileWithTraceCount()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        InsertAndProject(store, "trace-1", TraceJson("trace-1", "vscode-copilot-chat", "gpt-4o"), minute: 1);
        InsertAndProject(store, "trace-2", TraceJson("trace-2", "vscode-copilot-chat", "gpt-4.1"), minute: 2);
        InsertAndProject(store, "trace-3", TraceJson("trace-3", "copilot-cli", "gpt-4o"), minute: 3);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var summary = await GetJsonAsync(host, "/api/monitor/summary");
        var root = summary.RootElement;
        var traceCount = root.GetProperty("scope").GetProperty("trace_count").GetInt32();
        Assert.Equal(3, traceCount);

        var modelSum = root.GetProperty("per_model_summary").EnumerateArray()
            .Sum(item => item.GetProperty("trace_count").GetInt32());
        var clientKindSum = root.GetProperty("per_client_kind_summary").EnumerateArray()
            .Sum(item => item.GetProperty("trace_count").GetInt32());

        Assert.Equal(traceCount, modelSum);
        Assert.Equal(traceCount, clientKindSum);
    }

    [Fact]
    public async Task Summary_TraceWithErrors_AppearsAsErrorTrace()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        InsertAndProjectSpans(store, "trace-err", ErrorSpanJson);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var summary = await GetJsonAsync(host, "/api/monitor/summary");
        var errorTrace = summary.RootElement.GetProperty("error_trace");

        Assert.NotEqual(JsonValueKind.Null, errorTrace.ValueKind);
        Assert.Equal("trace-err", errorTrace.GetProperty("trace_id").GetString());
    }

    [Fact]
    public async Task Summary_HighlightTraceObjectsExposeRepositoryMetadata()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        InsertAndProject(store, "trace-repo-summary", RepositoryMetadataJson, minute: 1);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var summary = await GetJsonAsync(host, "/api/monitor/summary");
        var latestTrace = summary.RootElement.GetProperty("latest_trace");

        Assert.Equal("repo-summary", latestTrace.GetProperty("repository_name").GetString());
        Assert.Equal("workspace-summary", latestTrace.GetProperty("workspace_label").GetString());
        Assert.Equal("snapshot-summary", latestTrace.GetProperty("repo_snapshot").GetString());
    }

    [Fact]
    public async Task Summary_NullModelAndClientKind_GroupIntoUnknownBucket()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        InsertAndProject(store, "trace-unknown", NoAttributeTraceJson("trace-unknown"), minute: 1);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var summary = await GetJsonAsync(host, "/api/monitor/summary");
        var root = summary.RootElement;

        var model = Assert.Single(root.GetProperty("per_model_summary").EnumerateArray());
        Assert.Equal("unknown", model.GetProperty("model").GetString());

        var clientKind = Assert.Single(root.GetProperty("per_client_kind_summary").EnumerateArray());
        Assert.Equal("unknown", clientKind.GetProperty("client_kind").GetString());
    }

    [Theory]
    [InlineData("/api/monitor/summary?limit=0")]
    [InlineData("/api/monitor/summary?limit=201")]
    [InlineData("/api/monitor/summary?limit=abc")]
    public async Task Summary_RejectsInvalidLimitWith400(string path)
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var response = await host.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("invalid_query", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Summary_OmittedLimit_DefaultsTo50()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var summary = await GetJsonAsync(host, "/api/monitor/summary");

        Assert.Equal(50, summary.RootElement.GetProperty("scope").GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Summary_NeverReturnsRawContentOrPii()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        InsertAndProjectSpans(store, "trace-pii", RawAndPiiJson);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false });

        var json = await GetStringAsync(host, "/api/monitor/summary");

        foreach (var marker in new[] { "SECRET_PROMPT_TEXT_MARKER", "SECRET_TOOL_ARGS_MARKER", "USER-ID-SECRET-MARKER", "leak-marker@example.com" })
        {
            Assert.DoesNotContain(marker, json);
        }
    }

    [Fact]
    public void BuildSummary_NullPrimaryModelAndClientKind_GroupedAsUnknown()
    {
        var rows = new[]
        {
            new MonitorTraceRow(
                Id: 1,
                TraceId: "t1",
                ClientKind: null,
                ExperimentId: null,
                TaskId: null,
                TaskCategory: null,
                AgentVariant: null,
                PromptVersion: null,
                SpanCount: 1,
                ToolCallCount: 0,
                ErrorCount: 0,
                FirstSeenAt: "2024-01-01T00:00:00Z",
                LastSeenAt: "2024-01-01T00:00:00Z",
                ProjectedAt: "2024-01-01T00:00:00Z",
                InputTokens: 10,
                OutputTokens: 5,
                TotalTokens: 15,
                TurnCount: 1,
                AgentInvocationCount: 0,
                DurationMs: 100,
                PrimaryModel: null,
                RepositoryName: null,
                WorkspaceLabel: null,
                RepoSnapshot: null),
        };
        var store = new FakeProjectionStore(rows);
        var service = new MonitorSummaryService(store);

        var summary = service.BuildSummary(50);

        var modelSummary = Assert.Single(summary.PerModelSummary);
        Assert.Equal("unknown", modelSummary.Model);
        Assert.Equal(1, modelSummary.TraceCount);

        var clientKindSummary = Assert.Single(summary.PerClientKindSummary);
        Assert.Equal("unknown", clientKindSummary.ClientKind);
        Assert.Equal(1, clientKindSummary.TraceCount);
    }

    private static void InsertAndProject(RawTelemetryStore store, string traceId, string payloadJson, int minute)
    {
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(minute),
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(100));
    }

    private static void InsertAndProjectSpans(RawTelemetryStore store, string traceId, string payloadJson)
    {
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: payloadJson);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(100));
        var spans = MonitorSpanProjectionBuilder.Build(record);
        store.ApplySpanProjection(id, spans, DateTimeOffset.UnixEpoch.AddMinutes(101));
    }

    private static async Task<JsonDocument> GetJsonAsync(RunningMonitorHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static async Task<string> GetStringAsync(RunningMonitorHost host, string path)
    {
        var response = await host.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static string TraceJson(string traceId, string clientKind, string model) =>
        TraceTemplate
            .Replace("__TRACE__", traceId)
            .Replace("__CLIENT_KIND__", clientKind)
            .Replace("__MODEL__", model);

    private const string TraceTemplate = """
        {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"__CLIENT_KIND__"}}]},"scopeSpans":[{"spans":[{"traceId":"__TRACE__","spanId":"2222222222222222","name":"chat __MODEL__","attributes":[{"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},{"key":"gen_ai.response.model","value":{"stringValue":"__MODEL__"}}]}]}]}]}
        """;

    private static string NoAttributeTraceJson(string traceId) =>
        NoAttributeTraceTemplate.Replace("__TRACE__", traceId);

    private const string NoAttributeTraceTemplate = """
        {"resourceSpans":[{"resource":{"attributes":[]},"scopeSpans":[{"spans":[{"traceId":"__TRACE__","spanId":"3333333333333333","name":"chat"}]}]}]}
        """;

    private const string ErrorSpanJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-err","spanId":"4001","name":"chat gpt-4o",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"error.type","value":{"stringValue":"timeout"}}
           ]}
        ]}]}]}
        """;

    private const string RawAndPiiJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.id","value":{"stringValue":"USER-ID-SECRET-MARKER"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-pii","spanId":"1111111111111111","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

    private const string RepositoryMetadataJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"repo.name","value":{"stringValue":"repo-summary"}},
          {"key":"workspace.name","value":{"stringValue":"workspace-summary"}},
          {"key":"repo.snapshot","value":{"stringValue":"snapshot-summary"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-repo-summary","spanId":"1111111111111111","name":"chat gpt-4o"}
        ]}]}]}
        """;

    /// <summary>Minimal in-memory <see cref="IMonitorProjectionStore"/> for unit-testing <see cref="MonitorSummaryService"/> directly, without a SQLite-backed store.</summary>
    private sealed class FakeProjectionStore : IMonitorProjectionStore
    {
        private readonly IReadOnlyList<MonitorTraceRow> traces;

        public FakeProjectionStore(IReadOnlyList<MonitorTraceRow> traces)
        {
            this.traces = traces;
        }

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit) => [];

        public bool ApplyProjection(long rawRecordId, string source, DateTimeOffset receivedAt, MonitorRecordProjection projection, DateTimeOffset projectedAt) => false;

        public MonitorProjectionStatus GetProjectionStatus() => new(0, null);

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForSpanProjection(int limit) => [];

        public bool ApplySpanProjection(long rawRecordId, IReadOnlyList<MonitorSpanProjection> spans, DateTimeOffset projectedAt) => false;

        public MonitorProjectionStatus GetSpanProjectionStatus() => new(0, null);

        public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) => new([], false);

        public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) => new(traces, false);

        public MonitorTraceRow? GetMonitorTrace(string traceId) => traces.FirstOrDefault(t => t.TraceId == traceId);

        public MonitorProjectionPage<MonitorSpanRow> ListMonitorSpans(string traceId, long afterId, int limit) => new([], false);

        public IReadOnlyList<MonitorSpanRow> GetSpansForTrace(string traceId) => [];

        public RawTelemetryRecord? GetRawRecordById(long id) => null;

        public IReadOnlyList<RawTelemetryRecord> ListRawRecordsByTraceId(string traceId, int limit) => [];
    }
}

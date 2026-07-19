using CopilotAgentObservability.LocalMonitor.Health;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

/// <summary>
/// A deterministic <see cref="TimeProvider"/> whose clock only moves when the test
/// calls <see cref="Advance"/>. Shared by the health and readiness-failure tests so
/// stall / projection-lag windows are exercised without real waiting.
/// </summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset now;
    private long timestamp;
    private readonly object timerGate = new();
    private readonly List<MutableTimer> timers = [];

    public MutableTimeProvider(DateTimeOffset start)
    {
        now = start;
    }

    internal TimeSpan TimestampAdvancePerRead { get; set; }
    internal Action? TimerCreated { get; set; }

    public override DateTimeOffset GetUtcNow() => now;

    public override long GetTimestamp()
    {
        var current = timestamp;
        timestamp += TimestampAdvancePerRead.Ticks;
        return current;
    }

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan delta)
    {
        List<MutableTimer> due;
        lock (timerGate)
        {
            now += delta;
            due = timers.Where(timer => timer.IsDue(now)).ToList();
        }
        foreach (var timer in due) timer.Fire(now);
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new MutableTimer(this, callback, state, period);
        lock (timerGate)
        {
            timer.Change(dueTime, period, now);
            timers.Add(timer);
        }
        TimerCreated?.Invoke();
        return timer;
    }

    private sealed class MutableTimer(MutableTimeProvider provider, TimerCallback callback, object? state, TimeSpan period) : ITimer
    {
        private DateTimeOffset? dueAt;
        private bool disposed;

        internal bool IsDue(DateTimeOffset current) => !disposed && dueAt is { } due && due <= current;
        internal void Fire(DateTimeOffset current)
        {
            if (!IsDue(current)) return;
            if (period > TimeSpan.Zero) dueAt = current + period; else dueAt = null;
            callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan nextPeriod)
        {
            lock (provider.timerGate) { Change(dueTime, nextPeriod, provider.now); }
            return true;
        }
        internal void Change(TimeSpan dueTime, TimeSpan nextPeriod, DateTimeOffset current) => dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : current + dueTime;
        public void Dispose() { lock (provider.timerGate) { disposed = true; dueAt = null; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

internal static class MonitorTestHealth
{
    /// <summary>
    /// A fully healthy, caught-up readiness state bound to <paramref name="time"/>:
    /// loopback bound, migration complete, writer and projection worker running, and
    /// a known projection status with zero backlog / zero lag.
    /// </summary>
    public static MonitorHealthState Ready(MutableTimeProvider time)
    {
        var health = new MonitorHealthState(time);
        health.SetLoopbackBound(true);
        health.MarkMigrationComplete();
        health.SetWriterRunning(true);
        health.SetProjectionWorkerRunning(true);
        health.SetProjectionStatus(backlog: 0, oldestUnprocessedReceivedAt: null);
        return health;
    }
}

/// <summary>
/// Sprint18 rich trace fixture (reused M5–M8): an agent root, three chat turns
/// with cache usage, a parallel tool trio on turn 1, a failed tool + later
/// successful retry on turn 2 (recovered pair), and — when
/// <c>unrecovered: true</c> — a terminal failing tool after turn 3 so the trace
/// rolls up as unrecovered.
/// </summary>
internal static class MonitorRichTrace
{
    public const string TraceId = "trace-rich";

    public static long Seed(MonitorTempDirectory temp, bool unrecovered = false)
    {
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var payload = unrecovered
            ? Payload.Replace("__TERMINAL__", TerminalErrorSpan)
            : Payload.Replace("__TERMINAL__", string.Empty);
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: TraceId,
            ReceivedAt: DateTimeOffset.UnixEpoch.AddMinutes(1),
            ResourceAttributesJson: null,
            PayloadJson: payload);
        var id = store.Insert(record);
        store.ApplyProjection(id, record.Source, record.ReceivedAt, MonitorProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(2));
        store.ApplySpanProjection(id, MonitorSpanProjectionBuilder.Build(record), DateTimeOffset.UnixEpoch.AddMinutes(3));
        return id;
    }

    private const string TerminalErrorSpan = """
        ,{"traceId":"trace-rich","spanId":"x301","parentSpanId":"t300","name":"execute_tool run_tests",
         "startTimeUnixNano":"1751500041000000000","endTimeUnixNano":"1751500045000000000",
         "status":{"code":"STATUS_CODE_ERROR"},
         "attributes":[
           {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
           {"key":"gen_ai.tool.name","value":{"stringValue":"run_tests"}},
           {"key":"error.type","value":{"stringValue":"test_failure"}}
         ]}
        """;

    private const string Payload = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"trace-rich","spanId":"a000","name":"invoke_agent copilot",
           "startTimeUnixNano":"1751500000000000000","endTimeUnixNano":"1751500060000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"invoke_agent"}},
             {"key":"gen_ai.agent.name","value":{"stringValue":"copilot"}}
           ]},
          {"traceId":"trace-rich","spanId":"t100","name":"chat gpt-4o",
           "startTimeUnixNano":"1751500001000000000","endTimeUnixNano":"1751500008000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"8000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"600"}},
             {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"5000"}},
             {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"300"}}
           ]},
          {"traceId":"trace-rich","spanId":"p101","parentSpanId":"t100","name":"execute_tool read_file",
           "startTimeUnixNano":"1751500008000000000","endTimeUnixNano":"1751500010000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"read_file"}}
           ]},
          {"traceId":"trace-rich","spanId":"p102","parentSpanId":"t100","name":"execute_tool grep_search",
           "startTimeUnixNano":"1751500008500000000","endTimeUnixNano":"1751500011000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"grep_search"}}
           ]},
          {"traceId":"trace-rich","spanId":"p103","parentSpanId":"t100","name":"execute_tool glob_files",
           "startTimeUnixNano":"1751500009000000000","endTimeUnixNano":"1751500012000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"glob_files"}}
           ]},
          {"traceId":"trace-rich","spanId":"t200","name":"chat gpt-4o",
           "startTimeUnixNano":"1751500013000000000","endTimeUnixNano":"1751500020000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"9000"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"700"}},
             {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"6500"}},
             {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"100"}}
           ]},
          {"traceId":"trace-rich","spanId":"f201","parentSpanId":"t200","name":"execute_tool str_replace",
           "startTimeUnixNano":"1751500020000000000","endTimeUnixNano":"1751500022000000000",
           "status":{"code":"STATUS_CODE_ERROR"},
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"str_replace"}},
             {"key":"error.type","value":{"stringValue":"tool_failure"}}
           ]},
          {"traceId":"trace-rich","spanId":"r202","parentSpanId":"t200","name":"execute_tool str_replace",
           "startTimeUnixNano":"1751500023000000000","endTimeUnixNano":"1751500025000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
             {"key":"gen_ai.tool.name","value":{"stringValue":"str_replace"}}
           ]},
          {"traceId":"trace-rich","spanId":"t300","name":"chat gpt-4o",
           "startTimeUnixNano":"1751500026000000000","endTimeUnixNano":"1751500040000000000",
           "attributes":[
             {"key":"gen_ai.operation.name","value":{"stringValue":"chat"}},
             {"key":"gen_ai.response.model","value":{"stringValue":"gpt-4o"}},
             {"key":"gen_ai.usage.input_tokens","value":{"intValue":"9500"}},
             {"key":"gen_ai.usage.output_tokens","value":{"intValue":"800"}},
             {"key":"gen_ai.usage.cache_read.input_tokens","value":{"intValue":"7000"}},
             {"key":"gen_ai.usage.cache_creation.input_tokens","value":{"intValue":"50"}}
           ]}__TERMINAL__
        ]}]}]}
        """;
}

internal static class MonitorTestHost
{
    public static async Task<RunningMonitorHost> StartAsync(
        MonitorTempDirectory temp,
        bool sanitizedOnly = false,
        int maxRequestBodyBytes = MonitorOptions.DefaultMaxRequestBodyBytes,
        int ingestionStallThresholdSeconds = MonitorOptions.DefaultIngestionStallThresholdSeconds,
        int projectionLagThresholdSeconds = MonitorOptions.DefaultProjectionLagThresholdSeconds,
        MonitorHostTestOptions? testOptions = null)
    {
        var options = new MonitorOptions(
            temp.DatabasePath,
            Url: "http://127.0.0.1:0",
            SanitizedOnly: sanitizedOnly,
            MaxRequestBodyBytes: maxRequestBodyBytes,
            ingestionStallThresholdSeconds,
            projectionLagThresholdSeconds);
        testOptions ??= new MonitorHostTestOptions();
        testOptions.TimeProvider ??= temp.TimeProvider;
        var app = MonitorHost.Build(options, testOptions);
        await app.StartAsync();

        var url = GetSingleBoundAddress(app);
        return new RunningMonitorHost(app, new HttpClient { BaseAddress = new Uri(url) }, url);
    }

    private static string GetSingleBoundAddress(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        var addresses = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?
            .Addresses
            .ToArray();
        Assert.NotNull(addresses);
        var address = Assert.Single(addresses);
        Assert.StartsWith("http://127.0.0.1:", address, StringComparison.Ordinal);
        Assert.False(address.EndsWith(":0", StringComparison.Ordinal));
        return address;
    }
}

internal sealed class RunningMonitorHost(
    Microsoft.AspNetCore.Builder.WebApplication app,
    HttpClient client,
    string url) : IAsyncDisposable
{
    public HttpClient Client { get; } = client;

    public string Url { get; } = url;

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try
        {
            await app.StopAsync();
        }
        catch
        {
            // Ignore stop faults during teardown.
        }

        await app.DisposeAsync();
    }
}

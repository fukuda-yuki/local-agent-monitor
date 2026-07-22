using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection("Retention mutation API routes")]
public sealed class AlertLifecycleRouteTests
{
    private const string AlertId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task FreshHost_InitializesAcceptedParentThenReturnsNotFoundForMissingReceipt()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        await AssertError(await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle"), HttpStatusCode.NotFound, "alert_not_found");
    }

    [Fact]
    public async Task CounterfeitReceiptTableWithoutAcceptedParent_FailsClosedWithoutLifecycleCreation()
    {
        using var temp = NewTemp();
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE alert_receipts(alert_id TEXT PRIMARY KEY,canonical_json TEXT NOT NULL);";
            await command.ExecuteNonQueryAsync();
        }
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        await AssertError(await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle"), HttpStatusCode.ServiceUnavailable, "alert_lifecycle_store_unavailable");
        await using var check = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString());
        await check.OpenAsync();
        using var count = check.CreateCommand();
        count.CommandText = "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'alert_lifecycle_%';";
        Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task ReadAndMutationRoutes_ReturnStrictVersionedNoStoreDtos()
    {
        using var temp = NewTemp();
        SeedReceipt(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        using var lazy = await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle");
        Assert.Equal(HttpStatusCode.OK, lazy.StatusCode);
        Assert.Equal("no-store", lazy.Headers.CacheControl?.ToString());
        using (var json = JsonDocument.Parse(await lazy.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(["schema_version", "alert_id", "state", "revision", "last_occurred_at"], json.RootElement.EnumerateObject().Select(item => item.Name));
            Assert.Equal("open", json.RootElement.GetProperty("state").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("last_occurred_at").ValueKind);
        }

        using var mutation = Request("""{"schema_version":"alert.lifecycle.v1","action":"acknowledge","expected_revision":0,"reason_code":"user_reviewed","comment":"reviewed locally"}""");
        using var updated = await host.Client.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal("no-store", updated.Headers.CacheControl?.ToString());
        using (var json = JsonDocument.Parse(await updated.Content.ReadAsStreamAsync()))
        {
            Assert.Equal(["schema_version", "alert_id", "state", "revision", "last_occurred_at", "event", "idempotent_replay"], json.RootElement.EnumerateObject().Select(item => item.Name));
            Assert.Equal("acknowledged", json.RootElement.GetProperty("state").GetString());
            Assert.False(json.RootElement.GetProperty("idempotent_replay").GetBoolean());
        }

        using var history = await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle/history?limit=1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal("no-store", history.Headers.CacheControl?.ToString());
        using var historyJson = JsonDocument.Parse(await history.Content.ReadAsStreamAsync());
        Assert.Equal(["schema_version", "alert_id", "events"], historyJson.RootElement.EnumerateObject().Select(item => item.Name));
        Assert.Equal(1, historyJson.RootElement.GetProperty("events").GetArrayLength());
        var historyEvent = historyJson.RootElement.GetProperty("events")[0];
        Assert.Equal(["schema_version", "event_id", "alert_id", "revision", "expected_revision", "action", "previous_state", "state", "occurred_at", "actor", "reason_code", "comment", "old_alert_id", "new_alert_id", "result_code"], historyEvent.EnumerateObject().Select(item => item.Name));
        Assert.Equal(JsonValueKind.Null, historyEvent.GetProperty("old_alert_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, historyEvent.GetProperty("new_alert_id").ValueKind);
    }

    [Fact]
    public async Task MutationRoute_EnforcesSameOriginCsrfStrictDtoAndSanitizedComment()
    {
        using var temp = NewTemp();
        SeedReceipt(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        const string valid = """{"schema_version":"alert.lifecycle.v1","action":"acknowledge","expected_revision":0,"reason_code":"user_reviewed","comment":null}""";

        using var missingCsrf = Request(valid, csrf: false);
        await AssertError(await host.Client.SendAsync(missingCsrf), HttpStatusCode.Forbidden, "csrf_required");

        using var crossSite = Request(valid);
        crossSite.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(await host.Client.SendAsync(crossSite), HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var unknown = Request(valid[..^1] + ",\"unknown\":1}");
        await AssertError(await host.Client.SendAsync(unknown), HttpStatusCode.BadRequest, "alert_invalid_request");

        using var internalActor = Request(valid[..^1] + ",\"actor\":\"local_system\"}");
        await AssertError(await host.Client.SendAsync(internalActor), HttpStatusCode.BadRequest, "alert_invalid_request");

        using var duplicate = Request(valid.Replace("\"expected_revision\":0", "\"expected_revision\":0,\"expected_revision\":0", StringComparison.Ordinal));
        await AssertError(await host.Client.SendAsync(duplicate), HttpStatusCode.BadRequest, "alert_invalid_request");

        using var sensitive = Request(valid.Replace("null", "\"C:\\\\Users\\\\person\\\\raw.json\"", StringComparison.Ordinal));
        await AssertError(await host.Client.SendAsync(sensitive), HttpStatusCode.BadRequest, "alert_comment_not_sanitized");
    }

    [Fact]
    public async Task MutationRoute_MapsStaleAndExactReplayWithoutLeakingInput()
    {
        using var temp = NewTemp();
        SeedReceipt(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        const string body = """{"schema_version":"alert.lifecycle.v1","action":"dismiss","expected_revision":0,"reason_code":"user_reviewed","comment":"reviewed locally"}""";

        using var firstRequest = Request(body);
        using var first = await host.Client.SendAsync(firstRequest);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var replayRequest = Request(body);
        using var replay = await host.Client.SendAsync(replayRequest);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (var json = JsonDocument.Parse(await replay.Content.ReadAsStreamAsync())) Assert.True(json.RootElement.GetProperty("idempotent_replay").GetBoolean());

        using var mismatchRequest = Request(body.Replace("reviewed locally", "different review", StringComparison.Ordinal));
        await AssertError(await host.Client.SendAsync(mismatchRequest), HttpStatusCode.Conflict, "alert_idempotency_conflict");

        using var invalidTransitionRequest = Request(body.Replace("\"expected_revision\":0", "\"expected_revision\":1", StringComparison.Ordinal).Replace("\"dismiss\"", "\"resolve\"", StringComparison.Ordinal), key: "aid1_" + new string('c', 43));
        await AssertError(await host.Client.SendAsync(invalidTransitionRequest), HttpStatusCode.Conflict, "alert_invalid_transition");

        using var staleRequest = Request(body, key: "aid1_" + new string('b', 43));
        await AssertError(await host.Client.SendAsync(staleRequest), HttpStatusCode.Conflict, "alert_revision_conflict");
    }

    [Fact]
    public async Task MutationRoute_AcceptsOmittedOptionalComment()
    {
        using var temp = NewTemp();
        SeedReceipt(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        using var request = Request("""{"schema_version":"alert.lifecycle.v1","action":"acknowledge","expected_revision":0,"reason_code":"user_reviewed"}""");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task LifecycleStoreFailure_IsRouteLocalAndDoesNotChangeHealth()
    {
        using var temp = NewTemp();
        var options = Options(new UnavailableStore(), MonitorTestHealth.Ready((MutableTimeProvider)temp.TimeProvider));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);

        await AssertError(await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle"), HttpStatusCode.ServiceUnavailable, "alert_lifecycle_store_unavailable");
        using var live = await host.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        using var ready = await host.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task MalformedStoreStatusCodePairs_MapToFixedUnavailableWithoutReflection()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new MalformedResultStore()));

        await AssertError(await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle"), HttpStatusCode.ServiceUnavailable, "alert_lifecycle_store_unavailable");
        await AssertError(await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle/history"), HttpStatusCode.ServiceUnavailable, "alert_lifecycle_store_unavailable");
        using var mutation = Request("""{"schema_version":"alert.lifecycle.v1","action":"acknowledge","expected_revision":0,"reason_code":"user_reviewed","comment":null}""");
        await AssertError(await host.Client.SendAsync(mutation), HttpStatusCode.ServiceUnavailable, "alert_lifecycle_store_unavailable");
    }

    [Fact]
    public async Task KestrelOversizedLifecycleBody_MapsRouteLocallyToFixedRequestTooLarge()
    {
        using var temp = NewTemp();
        SeedReceipt(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, maxRequestBodyBytes: 128, testOptions: Options());
        using var request = Request(new string('x', 1024));

        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("{\"schema_version\":\"alert.lifecycle.v1\",\"error\":\"request_too_large\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task LifecycleRoutes_RejectInvalidHostAndCrossSiteRead()
    {
        using var temp = NewTemp();
        SeedReceipt(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/alerts/v1/{AlertId}/lifecycle");
        invalidHostRequest.Headers.Host = "example.invalid";
        await AssertError(await host.Client.SendAsync(invalidHostRequest), HttpStatusCode.BadRequest, "invalid_host");

        using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/alerts/v1/{AlertId}/lifecycle");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        await AssertError(await host.Client.SendAsync(crossSiteRequest), HttpStatusCode.Forbidden, "cross_origin_forbidden");
    }

    private static MonitorTempDirectory NewTemp() => new() { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero)) };
    private static MonitorHostTestOptions Options(IAlertLifecycleStore? store = null, CopilotAgentObservability.LocalMonitor.Health.MonitorHealthState? health = null) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        AlertLifecycleStore = store,
        Health = health,
    };

    private static HttpRequestMessage Request(string body, bool csrf = true, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/alerts/v1/{AlertId}/lifecycle/actions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", key ?? "aid1_" + new string('a', 43));
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.Equal($"{{\"schema_version\":\"alert.lifecycle.v1\",\"error\":\"{code}\"}}", await response.Content.ReadAsStringAsync());
        }
    }

    private static void SeedReceipt(MonitorTempDirectory temp)
    {
        var connection = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString();
        var engine = new SqliteAlertEngineStore(connection);
        Assert.Equal(AlertStoreStatus.Success, engine.Initialize().Status);
        var observed = temp.TimeProvider.GetUtcNow();
        var evidence = new AlertEvidenceReference(AlertEvidenceKind.Event, "evidence-1", "session-1", "trace-1", "span-1", null, "event-1", null, observed);
        var receipt = new AlertReceipt(AlertContractVersions.Receipt, AlertContractVersions.SanitizedReceiptProfile, AlertId, new string('e', 64), "fixture-rule", "1", AlertSeverity.Warning,
            AlertInitialState.Open, "github-copilot", "1.2.3", "session-1", "trace-1", [evidence], [new("count", "calls", 2)], [new("count.warning", "calls", 1)],
            "fixture-v1", new string('c', 64), ["tool-events"], AlertCompleteness.Partial, ["ingest_gap"], observed, observed, new string('d', 64), "Fixture summary");
        var evaluation = new AlertEvaluationResult(AlertContractVersions.Evaluation, new string('e', 64), new string('d', 64), "fixture-v1", new string('c', 64), [receipt], [], []);
        Assert.Equal(AlertStoreStatus.Success, engine.Append(evaluation).Status);
    }

    private sealed class UnavailableStore : IAlertLifecycleStore
    {
        private static AlertLifecycleStoreResult Unavailable() => new(AlertLifecycleStoreStatus.Unavailable, "alert_lifecycle_store_unavailable");
        public AlertLifecycleStoreResult Initialize() => Unavailable();
        public AlertLifecycleStoreResult Get(string alertId) => Unavailable();
        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) => new(AlertLifecycleStoreStatus.Unavailable, [], "alert_lifecycle_store_unavailable");
        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) => Unavailable();
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) => Unavailable();
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) => Unavailable();
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) => Unavailable();
    }

    private sealed class MalformedResultStore : IAlertLifecycleStore
    {
        public AlertLifecycleStoreResult Initialize() => new(AlertLifecycleStoreStatus.Success);
        public AlertLifecycleStoreResult Get(string alertId) => new(AlertLifecycleStoreStatus.NotFound, "raw_secret");
        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) => new(AlertLifecycleStoreStatus.Invalid, [], "raw_secret");
        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Conflict, "raw_secret");
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Conflict, "raw_secret");
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Conflict, "raw_secret");
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Conflict, "raw_secret");
    }
}

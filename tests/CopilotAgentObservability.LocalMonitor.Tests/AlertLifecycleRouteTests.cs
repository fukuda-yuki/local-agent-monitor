using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection("Retention mutation API routes")]
public sealed class AlertLifecycleRouteTests
{
    private static readonly AlertEvaluationResult ReceiptEvaluation = CreateReceiptEvaluation();
    private static readonly string AlertId = ReceiptEvaluation.Receipts.Single().AlertId;

    [Fact]
    public async Task FreshHost_InitializesAcceptedParentThenReturnsNotFoundForMissingReceipt()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        await AssertError(await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle"), HttpStatusCode.NotFound, "alert_not_found");
    }

    [Fact]
    public async Task InvalidAcceptedParentAfterValidHostStartup_FailsClosedWithoutLifecycleCreation()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_version WHERE component='alert_engine';";
            await command.ExecuteNonQueryAsync();
        }

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
        var updatedBytes = await updated.Content.ReadAsStringAsync();
        Assert.Equal(
            $$"""{"schema_version":"alert.lifecycle.v1","alert_id":"{{AlertId}}","state":"acknowledged","revision":1,"last_occurred_at":"2026-07-22T00:00:00.0000000\u002B00:00","event":{"schema_version":"alert.lifecycle.v1","event_id":"128183c3c9d95293a465423cccabbef08c7676e471a227cf94a011764c8c92bd","alert_id":"{{AlertId}}","revision":1,"expected_revision":0,"action":"acknowledge","previous_state":"open","state":"acknowledged","occurred_at":"2026-07-22T00:00:00.0000000\u002B00:00","actor":"local_user","reason_code":"user_reviewed","comment":"reviewed locally","old_alert_id":null,"new_alert_id":null,"result_code":"alert_lifecycle_updated"},"idempotent_replay":false}""",
            updatedBytes);
        using (var json = JsonDocument.Parse(updatedBytes))
        {
            Assert.Equal(["schema_version", "alert_id", "state", "revision", "last_occurred_at", "event", "idempotent_replay"], json.RootElement.EnumerateObject().Select(item => item.Name));
            Assert.Equal("acknowledged", json.RootElement.GetProperty("state").GetString());
            Assert.False(json.RootElement.GetProperty("idempotent_replay").GetBoolean());
        }

        using var history = await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle/history?limit=1");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        Assert.Equal("no-store", history.Headers.CacheControl?.ToString());
        var historyBytes = await history.Content.ReadAsStringAsync();
        Assert.Equal(
            $$"""{"schema_version":"alert.lifecycle.v1","alert_id":"{{AlertId}}","events":[{"schema_version":"alert.lifecycle.v1","event_id":"128183c3c9d95293a465423cccabbef08c7676e471a227cf94a011764c8c92bd","alert_id":"{{AlertId}}","revision":1,"expected_revision":0,"action":"acknowledge","previous_state":"open","state":"acknowledged","occurred_at":"2026-07-22T00:00:00.0000000\u002B00:00","actor":"local_user","reason_code":"user_reviewed","comment":"reviewed locally","old_alert_id":null,"new_alert_id":null,"result_code":"alert_lifecycle_updated"}]}""",
            historyBytes);
        using var historyJson = JsonDocument.Parse(historyBytes);
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

        using var sensitive = Request(valid.Replace("null", "\"C:\\\\Users\\\\person\\\\raw.json\"", StringComparison.Ordinal));
        await AssertError(await host.Client.SendAsync(sensitive), HttpStatusCode.BadRequest, "alert_comment_not_sanitized");
    }

    [Theory]
    [InlineData("\"schema_version\":\"alert.lifecycle.v1\"")]
    [InlineData("\"action\":\"acknowledge\"")]
    [InlineData("\"expected_revision\":0")]
    [InlineData("\"reason_code\":\"user_reviewed\"")]
    [InlineData("\"comment\":null")]
    public async Task MutationRoute_RejectsEveryDuplicateJsonProperty(string duplicateMember)
    {
        using var temp = NewTemp();
        SeedReceipt(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        var body = "{\"schema_version\":\"alert.lifecycle.v1\",\"action\":\"acknowledge\",\"expected_revision\":0,\"reason_code\":\"user_reviewed\",\"comment\":null," + duplicateMember + "}";
        using var request = Request(body);

        await AssertError(await host.Client.SendAsync(request), HttpStatusCode.BadRequest, "alert_invalid_request");
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

    [Theory]
    [InlineData("read")]
    [InlineData("history")]
    [InlineData("mutation")]
    public async Task HostileSuccessPayloadsForEveryRoute_MapToFixedUnavailableWithoutReflection(string route)
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new HostileSuccessStore()));

        var response = route switch
        {
            "read" => await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle"),
            "history" => await host.Client.GetAsync($"/api/alerts/v1/{AlertId}/lifecycle/history?limit=2"),
            "mutation" => await host.Client.SendAsync(Request("""{"schema_version":"alert.lifecycle.v1","action":"acknowledge","expected_revision":0,"reason_code":"user_reviewed","comment":null}""")),
            _ => throw new InvalidOperationException(),
        };
        await AssertError(response, HttpStatusCode.ServiceUnavailable, "alert_lifecycle_store_unavailable");
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
        Assert.Equal(AlertStoreStatus.Success, engine.Append(ReceiptEvaluation).Status);
    }

    private static AlertEvaluationResult CreateReceiptEvaluation()
    {
        var observed = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        var evidence = new AlertEvidenceReference(
            AlertEvidenceKind.Session,
            "session-evidence",
            "session-1",
            null,
            null,
            null,
            null,
            null,
            observed);
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "github-copilot",
            "1",
            "session-1",
            null,
            AlertCompleteness.Full,
            [],
            observed,
            observed,
            [new("tool-events", AlertCapabilityAvailability.Available)],
            [
                new(
                    "signal-1",
                    AlertSignalKind.SessionEvent,
                    0,
                    observed,
                    null,
                    AlertSignalStatus.Success,
                    [],
                    [],
                    evidence),
            ]);
        var descriptor = new AlertRuleDescriptor(
            "migration-fixture",
            "1",
            "Migration fixture",
            "Produces one strict v1 receipt for migration.",
            ["tool-events"],
            AlertRuleScope.Session,
            [],
            "session",
            [],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"],
            ["github-copilot"]);
        var rule = new FixedRule(
            descriptor,
            new(
                [new(AlertSeverity.Warning, [new("count", "calls", 1)], [evidence], observed, observed)],
                []));
        return new AlertEvaluationEngine(
            new AlertRuleRegistry([rule]),
            new ExistingResolver()).Evaluate(
                snapshot,
                new(AlertContractVersions.Configuration, "migration-v1", []));
    }

    private sealed class FixedRule(
        AlertRuleDescriptor descriptor,
        AlertRuleOutcome outcome) : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = descriptor;
        public AlertRuleOutcome Evaluate(AlertRuleContext context) => outcome;
    }

    private sealed class ExistingResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
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

    private sealed class HostileSuccessStore : IAlertLifecycleStore
    {
        private static readonly DateTimeOffset OccurredAt = new(2026, 7, 22, 0, 0, 0, TimeSpan.Zero);
        private static AlertLifecycleEvent Event() => new(
            AlertLifecycleContractVersions.Lifecycle, new string('f', 64), AlertId, 1, 0, AlertLifecycleAction.Acknowledge,
            AlertLifecycleState.Open, AlertLifecycleState.Acknowledged, OccurredAt, "local_user", "user_reviewed", null,
            "aid1_" + new string('a', 43), null, null, "alert_lifecycle_updated");
        private static AlertLifecycleEvent InconsistentNewerHistoryEvent() => new(
            AlertLifecycleContractVersions.Lifecycle, new string('e', 64), AlertId, 2, 1, AlertLifecycleAction.Reopen,
            AlertLifecycleState.Dismissed, AlertLifecycleState.Open, OccurredAt, "local_user", "user_reviewed", null,
            "aid1_" + new string('b', 43), null, null, "alert_lifecycle_updated");

        public AlertLifecycleStoreResult Initialize() => new(AlertLifecycleStoreStatus.Success);
        public AlertLifecycleStoreResult Get(string alertId) => new(AlertLifecycleStoreStatus.Success, "raw_secret",
            new(AlertLifecycleContractVersions.Lifecycle, AlertId, AlertLifecycleState.Open, 0, null));
        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) => new(AlertLifecycleStoreStatus.Success, [InconsistentNewerHistoryEvent(), Event()]);
        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Success, Lifecycle:
            new(AlertLifecycleContractVersions.Lifecycle, AlertId, AlertLifecycleState.Resolved, 1, OccurredAt), Event: Event());
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) => Mutate(mutation);
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) => Mutate(mutation);
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) => Mutate(mutation);
    }
}

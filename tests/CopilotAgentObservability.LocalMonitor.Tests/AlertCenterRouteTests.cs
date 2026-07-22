using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterRouteTests
{
    [Fact]
    public async Task ReadRoute_UsesFrozenReceiptLifecycleAndExactProjectionForRecurringGroups()
    {
        using var temp = NewTemp();
        var firstSession = SeedPersistedTraceAndSession(temp, "trace-a", authoritativeToolStatus: true);
        var secondSession = SeedPersistedTraceAndSession(temp, "trace-b", authoritativeToolStatus: true);
        var thirdSession = SeedPersistedTraceAndSession(temp, "trace-c", authoritativeToolStatus: true);
        var first = AppendPersistedAlert(temp, firstSession, "trace-a");
        var second = AppendPersistedAlert(temp, secondSession, "trace-b");
        _ = AppendPersistedAlert(temp, thirdSession, "trace-c");
        Acknowledge(temp, second);

        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());
        using var response = await host.Client.GetAsync(
            "/api/alert-center/v1/alerts?severity=critical&repository=repo-persisted&workspace=workspace-persisted&source_surface=github-copilot-vscode&completeness=partial&period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-review-note", responseText, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(responseText);
        var root = json.RootElement;
        Assert.Equal("alert.center.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("complete", root.GetProperty("snapshot_state").GetString());
        Assert.Equal(0, root.GetProperty("omitted_receipt_count").GetInt32());
        Assert.Equal("complete", root.GetProperty("coverage_state").GetString());
        Assert.Equal(0, root.GetProperty("omitted_coverage_fact_count").GetInt32());
        Assert.Equal(3, root.GetProperty("total_count").GetInt32());

        var alerts = root.GetProperty("alerts");
        Assert.Equal(3, alerts.GetArrayLength());
        var acknowledged = alerts.EnumerateArray().Single(item => item.GetProperty("alert_id").GetString() == second);
        Assert.Equal("acknowledged", acknowledged.GetProperty("lifecycle").GetProperty("state").GetString());
        Assert.Equal(["dismiss", "resolve"], acknowledged.GetProperty("lifecycle").GetProperty("allowed_actions").EnumerateArray().Select(item => item.GetString()));
        var transition = Assert.Single(acknowledged.GetProperty("lifecycle").GetProperty("history").EnumerateArray());
        Assert.Equal(1, transition.GetProperty("revision").GetInt64());
        Assert.Equal("acknowledge", transition.GetProperty("action").GetString());
        Assert.Equal("open", transition.GetProperty("previous_state").GetString());
        Assert.Equal("acknowledged", transition.GetProperty("state").GetString());
        Assert.Equal("local_user", transition.GetProperty("actor").GetString());
        Assert.Equal("user_reviewed", transition.GetProperty("reason_code").GetString());
        Assert.False(transition.TryGetProperty("comment", out _));
        Assert.False(transition.TryGetProperty("idempotency_key", out _));
        Assert.Equal("registered", acknowledged.GetProperty("rule").GetProperty("contract_state").GetString());
        Assert.Equal("High tool failure ratio", acknowledged.GetProperty("rule").GetProperty("title").GetString());
        Assert.Equal("trace", acknowledged.GetProperty("rule").GetProperty("evaluation_window").GetString());
        Assert.Contains("authoritative success and error", acknowledged.GetProperty("rule").GetProperty("formula").GetString());
        Assert.Equal("supported_at_evaluation", acknowledged.GetProperty("source").GetProperty("capability_state").GetString());
        Assert.Equal("exact_agreement", acknowledged.GetProperty("scope").GetProperty("state").GetString());
        Assert.Equal("repo-persisted", acknowledged.GetProperty("scope").GetProperty("repository").GetString());
        Assert.Equal("workspace-persisted", acknowledged.GetProperty("scope").GetProperty("workspace").GetString());
        Assert.Equal("partial", acknowledged.GetProperty("completeness").GetProperty("state").GetString());
        Assert.Equal("available", acknowledged.GetProperty("evidence")[0].GetProperty("availability_state").GetString());
        Assert.Equal("/traces/trace-b?span=tool-0", acknowledged.GetProperty("evidence")[0].GetProperty("href").GetString());
        Assert.Equal(5, acknowledged.GetProperty("evidence_count").GetInt32());

        var group = Assert.Single(root.GetProperty("recurring_groups").EnumerateArray());
        Assert.Equal("supported", group.GetProperty("aggregation_state").GetString());
        Assert.Equal(3, group.GetProperty("occurrence_count").GetInt32());
        Assert.Equal(3, group.GetProperty("distinct_session_count").GetInt32());
        Assert.Equal("2026-07-22", group.GetProperty("observation_date").GetString());
        Assert.Equal(
            new[] { firstSession, secondSession, thirdSession }.Select(item => item.ToString("D")).Order(StringComparer.Ordinal),
            group.GetProperty("session_ids").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(first, group.GetProperty("alert_ids").EnumerateArray().Select(item => item.GetString()));

        using var tracePage = await host.Client.GetAsync("/traces/trace-b");
        Assert.Equal(HttpStatusCode.OK, tracePage.StatusCode);
        Assert.Contains("href=\"/alerts?trace_id=trace-b\"", await tracePage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRoute_ReportsUnknownEvidenceAndScopeWithoutInventingRecurrence()
    {
        using var temp = NewTemp();
        _ = AppendAlert(temp, "opaque-session", "trace-missing", "span-missing", 8);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new ExactProjectionStore()));

        using var response = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var alert = Assert.Single(json.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Equal("unknown", alert.GetProperty("scope").GetProperty("state").GetString());
        Assert.Equal("unknown", alert.GetProperty("evidence")[0].GetProperty("availability_state").GetString());
        Assert.Equal(JsonValueKind.Null, alert.GetProperty("evidence")[0].GetProperty("href").ValueKind);
        var group = Assert.Single(json.RootElement.GetProperty("recurring_groups").EnumerateArray());
        Assert.Equal("unsupported_scope", group.GetProperty("aggregation_state").GetString());
        Assert.Equal(1, group.GetProperty("distinct_session_count").GetInt32());
    }

    [Fact]
    public async Task ReadRoute_DoesNotLinkEvidenceWhenReceiptSourceDisagreesWithPersistedPartition()
    {
        using var temp = NewTemp();
        var traceId = "receipt-source-mismatch-trace";
        var sessionId = SeedPersistedTraceAndSession(temp, traceId, authoritativeToolStatus: true);
        var snapshot = PersistedSnapshot(temp, sessionId, traceId) with
        {
            SourceSurface = "github-copilot-cli",
        };
        var evaluation = Engine().Evaluate(snapshot, Configuration());
        Assert.Single(evaluation.Receipts);
        Assert.Equal(AlertStoreStatus.Success, Store(temp).Append(evaluation).Status);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());

        using var response = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var alert = Assert.Single(json.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Equal("github-copilot-cli", alert.GetProperty("source").GetProperty("surface").GetString());
        Assert.All(alert.GetProperty("evidence").EnumerateArray(), evidence =>
        {
            Assert.Equal("unknown", evidence.GetProperty("availability_state").GetString());
            Assert.Equal(JsonValueKind.Null, evidence.GetProperty("href").ValueKind);
        });
    }

    [Fact]
    public async Task ReadRoute_ProjectsGenericSessionAndTraceEvidenceThroughExactOwners()
    {
        using var temp = NewTemp();
        var sessionOwner = SeedPersistedTraceAndSession(temp, "generic-session-route", authoritativeToolStatus: true);
        var traceOwner = SeedPersistedTraceAndSession(temp, "generic-trace-route", authoritativeToolStatus: true);
        var eventOwner = SeedPersistedTraceAndSession(temp, "generic-event-route", authoritativeToolStatus: true);
        var sessionOnlyOwner = Guid.CreateVersion7();
        var expiredSessionOwner = Guid.CreateVersion7();
        var missingSessionOwner = Guid.CreateVersion7();
        WriteBareSession(temp, sessionOnlyOwner, SessionRawRetentionState.Expiring);
        WriteBareSession(temp, expiredSessionOwner, SessionRawRetentionState.ExpiredPendingDeletion);
        _ = AppendGenericEvidenceAlert(temp, sessionOwner, "generic-session-route", AlertEvidenceKind.Session);
        _ = AppendGenericEvidenceAlert(temp, traceOwner, "generic-trace-route", AlertEvidenceKind.Trace);
        _ = AppendGenericEvidenceAlert(temp, eventOwner, "generic-event-route", AlertEvidenceKind.Event);
        _ = AppendSessionOnlyEvidenceAlert(temp, sessionOnlyOwner);
        _ = AppendSessionOnlyEvidenceAlert(temp, expiredSessionOwner);
        _ = AppendSessionOnlyEvidenceAlert(temp, missingSessionOwner);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());

        using var response = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var alerts = json.RootElement.GetProperty("alerts").EnumerateArray().ToArray();
        var sessionAlert = Assert.Single(alerts, item => item.GetProperty("session_id").GetString() == sessionOwner.ToString("D"));
        Assert.All(sessionAlert.GetProperty("evidence").EnumerateArray(), evidence =>
        {
            Assert.Equal("session", evidence.GetProperty("kind").GetString());
            Assert.Equal("available", evidence.GetProperty("availability_state").GetString());
            Assert.Equal("not_captured", evidence.GetProperty("content_state").GetString());
            Assert.Equal($"/diagnostics?session_id={sessionOwner:D}", evidence.GetProperty("href").GetString());
        });
        var traceAlert = Assert.Single(alerts, item => item.GetProperty("session_id").GetString() == traceOwner.ToString("D"));
        Assert.All(traceAlert.GetProperty("evidence").EnumerateArray(), evidence =>
        {
            Assert.Equal("trace", evidence.GetProperty("kind").GetString());
            Assert.Equal("available", evidence.GetProperty("availability_state").GetString());
            Assert.Equal("/traces/generic-trace-route", evidence.GetProperty("href").GetString());
        });
        var eventAlert = Assert.Single(alerts, item => item.GetProperty("session_id").GetString() == eventOwner.ToString("D"));
        Assert.All(eventAlert.GetProperty("evidence").EnumerateArray(), evidence =>
        {
            Assert.Equal("event", evidence.GetProperty("kind").GetString());
            Assert.Equal("available", evidence.GetProperty("availability_state").GetString());
            Assert.Equal("not_captured", evidence.GetProperty("content_state").GetString());
            Assert.Equal($"/diagnostics?session_id={eventOwner:D}", evidence.GetProperty("href").GetString());
        });
        var sessionOnlyAlert = Assert.Single(alerts, item => item.GetProperty("session_id").GetString() == sessionOnlyOwner.ToString("D"));
        var sessionOnlyEvidence = Assert.Single(sessionOnlyAlert.GetProperty("evidence").EnumerateArray());
        Assert.Equal("available", sessionOnlyEvidence.GetProperty("availability_state").GetString());
        Assert.Equal("available", sessionOnlyEvidence.GetProperty("content_state").GetString());
        Assert.Equal($"/diagnostics?session_id={sessionOnlyOwner:D}", sessionOnlyEvidence.GetProperty("href").GetString());
        var expiredAlert = Assert.Single(alerts, item => item.GetProperty("session_id").GetString() == expiredSessionOwner.ToString("D"));
        var expiredEvidence = Assert.Single(expiredAlert.GetProperty("evidence").EnumerateArray());
        Assert.Equal("expired", expiredEvidence.GetProperty("availability_state").GetString());
        Assert.Equal("expired", expiredEvidence.GetProperty("content_state").GetString());
        Assert.Equal($"/diagnostics?session_id={expiredSessionOwner:D}", expiredEvidence.GetProperty("href").GetString());
        var missingAlert = Assert.Single(alerts, item => item.GetProperty("session_id").GetString() == missingSessionOwner.ToString("D"));
        var missingEvidence = Assert.Single(missingAlert.GetProperty("evidence").EnumerateArray());
        Assert.Equal("missing", missingEvidence.GetProperty("availability_state").GetString());
        Assert.Equal(JsonValueKind.Null, missingEvidence.GetProperty("href").ValueKind);
    }

    [Theory]
    [InlineData("C:/Users/person/private-repository")]
    [InlineData(@"\Users\person\private-repository")]
    [InlineData(@"\Device\HarddiskVolume1\private-repository")]
    [InlineData("private/repository")]
    [InlineData(@"private\repository")]
    [InlineData("~/private-repository")]
    [InlineData("token=private-repository")]
    public async Task ReadRoute_DropsUnsafePersistedScopeAndNeverCopiesRawPayload(string unsafeRepository)
    {
        const string rawMarker = "RAW_ALERT_CENTER_TOOL_ARGUMENT_MARKER";
        using var temp = NewTemp();
        var sessionId = SeedPersistedTraceAndSession(
            temp,
            "unsafe-scope-route",
            authoritativeToolStatus: true,
            rawPayloadMarker: rawMarker,
            repository: unsafeRepository,
            workspace: "safe-logical-workspace");
        _ = AppendPersistedAlert(temp, sessionId, "unsafe-scope-route");
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());

        using var response = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var responseText = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawMarker, responseText, StringComparison.Ordinal);
        Assert.DoesNotContain(unsafeRepository, responseText, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(responseText);
        var scope = Assert.Single(json.RootElement.GetProperty("alerts").EnumerateArray())
            .GetProperty("scope");
        Assert.Equal("unknown", scope.GetProperty("state").GetString());
        foreach (var member in new[]
        {
            "repository", "workspace", "trace_repository", "trace_workspace",
            "session_repository", "session_workspace",
        })
        {
            Assert.Equal(JsonValueKind.Null, scope.GetProperty(member).ValueKind);
        }
    }

    [Fact]
    public async Task ReadRoute_ExposesSuppressionAsUnknownCoverageFactNotAlert()
    {
        using var temp = NewTemp();
        AppendMissingCapabilitySuppression(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: Options(new ExactProjectionStore()));

        using var response = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Empty(json.RootElement.GetProperty("alerts").EnumerateArray());
        var coverage = Assert.Single(json.RootElement.GetProperty("coverage").EnumerateArray());
        Assert.Equal("missing_required_capability", coverage.GetProperty("code").GetString());
        Assert.Equal(["tool-call-status"], coverage.GetProperty("missing_capabilities").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal("unknown", coverage.GetProperty("context_state").GetString());
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("source_surface").ValueKind);
        Assert.Equal(JsonValueKind.Null, coverage.GetProperty("session_id").ValueKind);
    }

    [Fact]
    public async Task ReadRoute_AttachesCoverageContextOnlyFromTheCompleteExactEvaluationReceiptSet()
    {
        using var temp = NewTemp();
        var traceId = "coverage-context-trace";
        var sessionId = SeedPersistedTraceAndSession(temp, traceId, authoritativeToolStatus: true);
        var snapshot = PersistedSnapshot(temp, sessionId, traceId);
        var registry = new AlertRuleRegistry([new HighToolFailureRatioAlertRule(), new MissingCapabilityFixtureRule()]);
        var configuration = new AlertEngineConfiguration(
            AlertContractVersions.Configuration,
            "coverage-context-fixture-v1",
            []);
        var evaluation = new AlertEvaluationEngine(registry, new ExistingEvidenceResolver())
            .Evaluate(snapshot, configuration);
        Assert.Single(evaluation.Receipts);
        Assert.Single(evaluation.Suppressions);
        Assert.Equal(AlertStoreStatus.Success, Store(temp).Append(evaluation).Status);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());

        using var response = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var coverage = Assert.Single(json.RootElement.GetProperty("coverage").EnumerateArray());
        Assert.Equal("exact_evaluation", coverage.GetProperty("context_state").GetString());
        Assert.Equal("github-copilot-vscode", coverage.GetProperty("source_surface").GetString());
        Assert.Equal("1.0.4", coverage.GetProperty("source_version").GetString());
        Assert.Equal(sessionId.ToString("D"), coverage.GetProperty("session_id").GetString());
        Assert.Equal(traceId, coverage.GetProperty("trace_id").GetString());
        Assert.Equal("2026-07-22", coverage.GetProperty("observation_date").GetString());
    }

    [Fact]
    public async Task ExplicitEvaluation_UsesExactPersistedSessionAndTraceButDoesNotPromoteUnmanifestedCapabilities()
    {
        using var temp = NewTemp();
        var sessionId = SeedPersistedTraceAndSession(temp, "persisted-alert-trace", authoritativeToolStatus: true);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());

        using var firstRequest = EvaluationRequest(sessionId, "persisted-alert-trace");
        using var first = await host.Client.SendAsync(firstRequest);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStreamAsync());
        Assert.Equal("alert.center.evaluation-result.v1", firstJson.RootElement.GetProperty("schema_version").GetString());
        Assert.Empty(firstJson.RootElement.GetProperty("receipt_ids").EnumerateArray());
        Assert.Equal(10, firstJson.RootElement.GetProperty("suppressions").GetArrayLength());
        Assert.All(firstJson.RootElement.GetProperty("suppressions").EnumerateArray(), item =>
            Assert.Equal("missing_required_capability", item.GetProperty("code").GetString()));
        Assert.Contains(firstJson.RootElement.GetProperty("suppressions").EnumerateArray(), item =>
            item.GetProperty("rule_id").GetString() == "high-tool-failure-ratio"
            && item.GetProperty("code").GetString() == "missing_required_capability"
            && item.GetProperty("missing_capabilities").EnumerateArray().Any(capability => capability.GetString() == "tool-call-status"));

        using var repeatRequest = EvaluationRequest(sessionId, "persisted-alert-trace");
        using var repeat = await host.Client.SendAsync(repeatRequest);
        Assert.Equal(HttpStatusCode.OK, repeat.StatusCode);
        using var repeatJson = JsonDocument.Parse(await repeat.Content.ReadAsStreamAsync());
        Assert.Equal(firstJson.RootElement.GetProperty("evaluation_id").GetString(), repeatJson.RootElement.GetProperty("evaluation_id").GetString());
        Assert.Empty(repeatJson.RootElement.GetProperty("receipt_ids").EnumerateArray());

        using var read = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStreamAsync());
        Assert.Empty(readJson.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Contains(readJson.RootElement.GetProperty("coverage").EnumerateArray(), item =>
            item.GetProperty("rule_id").GetString() == "high-tool-failure-ratio"
            && item.GetProperty("code").GetString() == "missing_required_capability");
    }

    [Fact]
    public async Task ExplicitEvaluation_MissingExactStatusProducesSuppressionInsteadOfInferredZero()
    {
        using var temp = NewTemp();
        var sessionId = SeedPersistedTraceAndSession(temp, "persisted-suppressed-trace", authoritativeToolStatus: false);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());

        using var request = EvaluationRequest(sessionId, "persisted-suppressed-trace");
        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Empty(json.RootElement.GetProperty("receipt_ids").EnumerateArray());
        Assert.Equal(10, json.RootElement.GetProperty("suppressions").GetArrayLength());
        Assert.All(json.RootElement.GetProperty("suppressions").EnumerateArray(), item =>
            Assert.Equal("missing_required_capability", item.GetProperty("code").GetString()));
        var suppression = Assert.Single(
            json.RootElement.GetProperty("suppressions").EnumerateArray(),
            item => item.GetProperty("rule_id").GetString() == "high-tool-failure-ratio");
        Assert.Equal("missing_required_capability", suppression.GetProperty("code").GetString());
        Assert.Contains("tool-call-status", suppression.GetProperty("missing_capabilities").EnumerateArray().Select(item => item.GetString()));

        using var read = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStreamAsync());
        Assert.Empty(readJson.RootElement.GetProperty("alerts").EnumerateArray());
        Assert.Contains(readJson.RootElement.GetProperty("coverage").EnumerateArray(), item =>
            item.GetProperty("rule_id").GetString() == "high-tool-failure-ratio"
            && item.GetProperty("code").GetString() == "missing_required_capability");
    }

    [Fact]
    public async Task ExplicitEvaluation_ReconstructsStoredCoverageAfterMonitorRestart()
    {
        using var temp = NewTemp();
        var sessionId = SeedPersistedTraceAndSession(temp, "restart-coverage-trace", authoritativeToolStatus: true);
        string evaluationId;
        await using (var firstHost = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore()))
        {
            using var request = EvaluationRequest(sessionId, "restart-coverage-trace");
            using var response = await firstHost.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
            evaluationId = json.RootElement.GetProperty("evaluation_id").GetString()!;
        }

        await using var restartedHost = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());
        using var read = await restartedHost.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var readJson = JsonDocument.Parse(await read.Content.ReadAsStreamAsync());
        Assert.Empty(readJson.RootElement.GetProperty("alerts").EnumerateArray());
        var coverage = readJson.RootElement.GetProperty("coverage").EnumerateArray().ToArray();
        Assert.Equal(10, coverage.Length);
        Assert.All(coverage, item =>
        {
            Assert.Equal(evaluationId, item.GetProperty("evaluation_id").GetString());
            Assert.Equal("unknown", item.GetProperty("context_state").GetString());
        });
    }

    [Fact]
    public async Task PageAndGetRoutesNeverEvaluateOrAppendInBackground()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: OptionsForProductionStore());

        using var overview = await host.Client.GetAsync("/");
        using var page = await host.Client.GetAsync("/alerts");
        using var read = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=today");

        Assert.Equal(HttpStatusCode.OK, overview.StatusCode);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        var store = Store(temp);
        var evaluations = store.ListEvaluations(null, 1);
        Assert.Equal(AlertEngineQueryStatus.Success, evaluations.Status);
        Assert.Empty(evaluations.Items);
    }

    [Fact]
    public async Task ReadRoute_RejectsInvalidHostCrossSiteAndNonCanonicalQueries()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new ExactProjectionStore()));

        using var repeated = await host.Client.GetAsync("/api/alert-center/v1/alerts?severity=critical&severity=warning");
        await AssertError(repeated, HttpStatusCode.BadRequest, "alert_center_invalid_query");

        using var invalidRule = await host.Client.GetAsync("/api/alert-center/v1/alerts?rule_id=.invalid");
        await AssertError(invalidRule, HttpStatusCode.BadRequest, "alert_center_invalid_query");

        using var invalidSource = await host.Client.GetAsync("/api/alert-center/v1/alerts?source_surface=-invalid");
        await AssertError(invalidSource, HttpStatusCode.BadRequest, "alert_center_invalid_query");

        foreach (var (member, value) in new[]
        {
            ("repository", @"C:\Users\person\private-repository"),
            ("repository", "C:/Users/person/private-repository"),
            ("repository", "C:private-repository"),
            ("repository", @"\Users\person\private-repository"),
            ("workspace", @"\Device\HarddiskVolume1\private-workspace"),
            ("repository", "private/repository"),
            ("workspace", @"private\workspace"),
            ("repository", "~/private-repository"),
            ("workspace", "/home/person/private-workspace"),
            ("workspace", "/Users/person/private-workspace"),
            ("workspace", "file:///Users/person/private-workspace"),
            ("repository", "leak-marker@example.com"),
            ("workspace", "Authorization: Bearer synthetic-token"),
            ("repository", "credential=synthetic-marker"),
            ("workspace", "token=synthetic-marker"),
        })
        {
            using var unsafeLabel = await host.Client.GetAsync(
                $"/api/alert-center/v1/alerts?{member}={Uri.EscapeDataString(value)}");
            await AssertError(unsafeLabel, HttpStatusCode.BadRequest, "alert_center_invalid_query");
        }

        foreach (var path in new[]
        {
            "/api/alert-center/v1/alerts?from=2026-07-22",
            "/api/alert-center/v1/alerts?from=2026-07-23&to=2026-07-22",
            "/api/alert-center/v1/alerts?from=2025-07-22&to=2026-07-23",
        })
        {
            using var invalidPeriod = await host.Client.GetAsync(path);
            await AssertError(invalidPeriod, HttpStatusCode.BadRequest, "alert_center_invalid_query");
        }
        using var maximumPeriod = await host.Client.GetAsync(
            "/api/alert-center/v1/alerts?from=2025-07-23&to=2026-07-23");
        Assert.Equal(HttpStatusCode.OK, maximumPeriod.StatusCode);

        const string exactLabel = " exact repository label ";
        using var exactLabelResponse = await host.Client.GetAsync(
            $"/api/alert-center/v1/alerts?repository={Uri.EscapeDataString(exactLabel)}&period=30d");
        Assert.Equal(HttpStatusCode.OK, exactLabelResponse.StatusCode);
        using var exactLabelJson = JsonDocument.Parse(await exactLabelResponse.Content.ReadAsStreamAsync());
        Assert.Equal(exactLabel, exactLabelJson.RootElement.GetProperty("query").GetProperty("repository").GetString());

        using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, "/api/alert-center/v1/alerts");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);
        await AssertError(crossSite, HttpStatusCode.Forbidden, "cross_origin_forbidden");

        using var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/api/alert-center/v1/alerts");
        invalidHostRequest.Headers.Host = "example.invalid";
        using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
        await AssertError(invalidHost, HttpStatusCode.BadRequest, "invalid_host");
    }

    [Fact]
    public async Task ReadRoute_FailsClosedWhenStoredReceiptDoesNotPassFrozenConsumer()
    {
        using var temp = NewTemp();
        _ = AppendAlert(temp, "session-a", "trace-a", "span-a", 8);
        await using (var connection = new SqliteConnection(ConnectionString(temp)))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE alert_receipts SET canonical_json=json_set(canonical_json,'$.unexpected','not-a-contract-member');";
            await command.ExecuteNonQueryAsync();
        }
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new ExactProjectionStore()));

        using var response = await host.Client.GetAsync("/api/alert-center/v1/alerts");

        await AssertError(response, HttpStatusCode.ServiceUnavailable, "alert_center_store_unavailable");
    }

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
    };

    private static MonitorHostTestOptions Options(IMonitorProjectionStore projection) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        ProjectionStore = projection,
    };

    internal static MonitorHostTestOptions OptionsForProductionStore() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
    };

    private static string AppendAlert(MonitorTempDirectory temp, string sessionId, string traceId, string spanPrefix, int minute)
    {
        var store = Store(temp);
        var result = Engine().Evaluate(Snapshot(sessionId, traceId, spanPrefix, minute, capabilityAvailable: true), Configuration());
        Assert.Single(result.Receipts);
        Assert.Equal(AlertStoreStatus.Success, store.Append(result).Status);
        return result.Receipts[0].AlertId;
    }

    internal static string AppendPersistedAlert(MonitorTempDirectory temp, Guid sessionId, string traceId)
    {
        var result = Engine().Evaluate(PersistedSnapshot(temp, sessionId, traceId), Configuration());
        Assert.Single(result.Receipts);
        Assert.Equal(AlertStoreStatus.Success, Store(temp).Append(result).Status);
        return result.Receipts[0].AlertId;
    }

    private static string AppendGenericEvidenceAlert(
        MonitorTempDirectory temp,
        Guid sessionId,
        string traceId,
        AlertEvidenceKind kind)
    {
        var snapshot = PersistedSnapshot(temp, sessionId, traceId);
        var sessionEvent = kind == AlertEvidenceKind.Event
            ? Assert.Single(new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider)
                .GetDetail(sessionId)!.Events)
            : null;
        snapshot = snapshot with
        {
            Signals = snapshot.Signals.Select((signal, index) => signal with
            {
                Evidence = new AlertEvidenceReference(
                    kind,
                    $"generic-{kind.ToString().ToLowerInvariant()}-{index}",
                    sessionId.ToString("D"),
                    traceId,
                    null,
                    null,
                    sessionEvent?.EventId.ToString("D"),
                    null,
                    sessionEvent?.OccurredAt ?? signal.ObservedAt),
            }).ToArray(),
        };
        var result = Engine().Evaluate(snapshot, Configuration());
        Assert.Single(result.Receipts);
        Assert.Equal(AlertStoreStatus.Success, Store(temp).Append(result).Status);
        return result.Receipts[0].AlertId;
    }

    private static string AppendSessionOnlyEvidenceAlert(MonitorTempDirectory temp, Guid sessionId)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero);
        var reference = new AlertEvidenceReference(
            AlertEvidenceKind.Session,
            $"session-only-{sessionId:D}",
            sessionId.ToString("D"),
            null,
            null,
            null,
            null,
            null,
            observedAt);
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "github-copilot-vscode",
            "1.0.4",
            sessionId.ToString("D"),
            null,
            AlertCompleteness.Partial,
            [],
            observedAt,
            observedAt,
            [],
            [new AlertSignal("session-only-signal", AlertSignalKind.SessionEvent, 0, observedAt, null, AlertSignalStatus.Unknown, [], [], reference)]);
        var result = new AlertEvaluationEngine(
                new AlertRuleRegistry([new SessionOnlyEvidenceFixtureRule()]),
                new ExistingEvidenceResolver())
            .Evaluate(snapshot, new AlertEngineConfiguration(
                AlertContractVersions.Configuration,
                "session-only-evidence-fixture-v1",
                []));
        Assert.Single(result.Receipts);
        Assert.Equal(AlertStoreStatus.Success, Store(temp).Append(result).Status);
        return result.Receipts[0].AlertId;
    }

    private static void WriteBareSession(
        MonitorTempDirectory temp,
        Guid sessionId,
        SessionRawRetentionState retentionState)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 11, 0, 0, TimeSpan.Zero);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        store.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId,
                    ObservedSessionStatus.Completed,
                    SessionCompleteness.Partial,
                    "repo-session-only",
                    "workspace-session-only",
                    observedAt,
                    observedAt,
                    observedAt,
                    retentionState,
                    observedAt,
                    observedAt),
                [],
                [],
                []),
            []));
    }

    private static AlertNormalizedSnapshot PersistedSnapshot(MonitorTempDirectory temp, Guid sessionId, string traceId)
    {
        var rawStore = new RawTelemetryStore(
            temp.DatabasePath,
            temp.RetentionContext,
            temp.TimeProvider,
            RawTelemetryStoreConnectionOptions.MonitorWriter);
        var spans = rawStore.GetSpansForTrace(traceId).OrderBy(item => item.Id).ToArray();
        Assert.Equal(5, spans.Length);
        var signals = spans.Select((row, index) =>
        {
            var observedAt = Assert.IsType<DateTimeOffset>(AlertCenterEvidenceResolver.MonitorObservedAt(row));
            return new AlertSignal(
                $"signal-{row.Id}",
                AlertSignalKind.ToolCall,
                index,
                observedAt,
                null,
                index < 4 ? AlertSignalStatus.Error : AlertSignalStatus.Success,
                [],
                [],
                new AlertEvidenceReference(
                    AlertEvidenceKind.Span,
                    AlertCenterEvidenceResolver.MonitorEvidenceId(row.Id),
                    sessionId.ToString("D"),
                    traceId,
                    row.SpanId,
                    null,
                    null,
                    null,
                    observedAt));
        }).ToArray();
        return new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "github-copilot-vscode",
            "1.0.4",
            sessionId.ToString("D"),
            traceId,
            AlertCompleteness.Partial,
            ["schema_drift_detected"],
            signals[0].ObservedAt,
            signals[^1].ObservedAt,
            [new AlertCapabilityFact("tool-call-status", AlertCapabilityAvailability.Available)],
            signals);
    }

    private static void AppendMissingCapabilitySuppression(MonitorTempDirectory temp)
    {
        var store = Store(temp);
        var result = Engine().Evaluate(Snapshot("suppressed-session", "suppressed-trace", "suppressed-span", 7, capabilityAvailable: false), Configuration());
        Assert.Empty(result.Receipts);
        Assert.Contains(result.Suppressions, item => item.Code == "missing_required_capability");
        Assert.Equal(AlertStoreStatus.Success, store.Append(result).Status);
    }

    private static AlertEvaluationEngine Engine() => new(
        new AlertRuleRegistry([new HighToolFailureRatioAlertRule()]),
        new ExistingEvidenceResolver());

    private static AlertEngineConfiguration Configuration() => new(
        AlertContractVersions.Configuration,
        "alert-center-fixture-v1",
        [new AlertRuleConfiguration("high-tool-failure-ratio", "1", true, new Dictionary<string, decimal>(), null)]);

    private static AlertNormalizedSnapshot Snapshot(
        string sessionId,
        string traceId,
        string spanPrefix,
        int minute,
        bool capabilityAvailable)
    {
        var first = new DateTimeOffset(2026, 7, 22, 10, minute, 0, TimeSpan.Zero);
        var signals = Enumerable.Range(0, 5).Select(index =>
        {
            var observedAt = first.AddSeconds(index);
            var spanId = $"{spanPrefix}-{index}";
            return new AlertSignal(
                $"signal-{spanPrefix}-{index}",
                AlertSignalKind.ToolCall,
                index,
                observedAt,
                null,
                index < 4 ? AlertSignalStatus.Error : AlertSignalStatus.Success,
                [],
                [],
                new AlertEvidenceReference(AlertEvidenceKind.Span, $"evidence-{spanPrefix}-{index}", sessionId, traceId, spanId, null, null, null, observedAt));
        }).ToArray();
        return new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "github-copilot-vscode",
            "1.0.4",
            sessionId,
            traceId,
            AlertCompleteness.Partial,
            ["schema_drift_detected"],
            first,
            first.AddSeconds(4),
            [new AlertCapabilityFact("tool-call-status", capabilityAvailable ? AlertCapabilityAvailability.Available : AlertCapabilityAvailability.Unavailable)],
            signals);
    }

    private static SqliteAlertEngineStore Store(MonitorTempDirectory temp)
    {
        var store = new SqliteAlertEngineStore(ConnectionString(temp));
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        return store;
    }

    private static void Acknowledge(MonitorTempDirectory temp, string alertId)
    {
        var store = new SqliteAlertLifecycleStore(ConnectionString(temp), temp.TimeProvider);
        Assert.Equal(AlertLifecycleStoreStatus.Success, store.Initialize().Status);
        var result = store.Mutate(new AlertLifecycleMutation(
            AlertId: alertId,
            Action: AlertLifecycleAction.Acknowledge,
            ExpectedRevision: 0,
            ReasonCode: "user_reviewed",
            Comment: "private-review-note",
            IdempotencyKey: "aid1_" + new string('a', 43)));
        Assert.Equal(AlertLifecycleStoreStatus.Success, result.Status);
    }

    private static HttpRequestMessage EvaluationRequest(Guid sessionId, string traceId)
    {
        var body = JsonSerializer.Serialize(new
        {
            schema_version = "alert.center.evaluation-request.v1",
            session_id = sessionId.ToString("D"),
            trace_id = traceId,
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/alert-center/v1/evaluations")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    internal static Guid SeedPersistedTraceAndSession(
        MonitorTempDirectory temp,
        string traceId,
        bool authoritativeToolStatus,
        string? rawPayloadMarker = null,
        string? repository = "repo-persisted",
        string? workspace = "workspace-persisted")
    {
        var observed = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var baseNanos = observed.ToUnixTimeMilliseconds() * 1_000_000;
        var rawMarkerAttribute = rawPayloadMarker is null
            ? string.Empty
            : "," + JsonSerializer.Serialize(new
            {
                key = "gen_ai.tool.call.arguments",
                value = new { stringValue = rawPayloadMarker },
            });
        var spans = string.Join(',', Enumerable.Range(0, 5).Select(index =>
        {
            var status = authoritativeToolStatus
                ? index < 4 ? "\"status\":{\"code\":\"STATUS_CODE_ERROR\"}," : "\"status\":{\"code\":\"STATUS_CODE_OK\"},"
                : string.Empty;
            return $$$"""
                {
                  "traceId":"{{{traceId}}}","spanId":"tool-{{{index}}}","name":"execute_tool fixture",
                  "startTimeUnixNano":"{{{baseNanos + index * 1_000_000L}}}","endTimeUnixNano":"{{{baseNanos + index * 1_000_000L + 500_000L}}}",
                  {{{status}}}
                  "attributes":[
                    {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
                    {"key":"gen_ai.tool.name","value":{"stringValue":"fixture-tool"}}{{{rawMarkerAttribute}}}
                  ]
                }
                """;
        }));
        var payload = $$$"""
            {"resourceSpans":[{"resource":{"attributes":[{"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}}]},"scopeSpans":[{"spans":[{{{spans}}}]}]}]}
            """;
        var rawStore = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider, RawTelemetryStoreConnectionOptions.MonitorWriter);
        rawStore.CreateMonitorSchema();
        var compatibilityStore = new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        compatibilityStore.CreateSchema();
        var raw = new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, traceId, observed, null, payload);
        var inventory = OtlpJsonStructuralWalker.Build(payload, observed);
        var observation = SourceObservationBatchDraft.Create(
            $"alert-center-{traceId}",
            "github-copilot-vscode",
            "1.0.4",
            "github-copilot-vscode-otel",
            "monitor-projection-v1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                "github-copilot-vscode",
                "1.0.4",
                inventory,
                observedRecognizedCount: 5,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.NotCaptured,
            observed);
        var committed = new SqliteIngestionCommitStore(
            temp.DatabasePath,
            RawTelemetryStoreConnectionOptions.MonitorWriter,
            temp.TimeProvider).Commit(ValidatedIngestionBatch.Create(raw, observation));
        var rawId = committed.RawRecordId;
        var persisted = raw with { Id = rawId };
        Assert.True(rawStore.ApplyProjection(rawId, raw.Source, raw.ReceivedAt, MonitorProjectionBuilder.Build(persisted), observed.AddMinutes(1)));
        Assert.True(rawStore.ApplySpanProjection(rawId, MonitorSpanProjectionBuilder.Build(persisted), observed.AddMinutes(1)));

        var sessionId = Guid.CreateVersion7();
        var runId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        sessionStore.CreateSchema();
        sessionStore.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId,
                    ObservedSessionStatus.Completed,
                    SessionCompleteness.Rich,
                    Repository: repository,
                    Workspace: workspace,
                    StartedAt: observed,
                    EndedAt: observed.AddMinutes(1),
                    LastSeenAt: observed.AddMinutes(1),
                    SessionRawRetentionState.NotCaptured,
                    CreatedAt: observed,
                    UpdatedAt: observed.AddMinutes(1)),
                [new SessionNativeId(sessionId, SessionSourceSurface.VisualStudioCode, $"native-{traceId}", SessionBindingKind.Native, observed)],
                [new ObservedSessionRun(
                    runId,
                    sessionId,
                    SessionSourceSurface.VisualStudioCode,
                    NativeRunId: null,
                    TraceId: traceId,
                    ParentRunId: null,
                    Model: null,
                    ObservedSessionStatus.Completed,
                    StartedAt: observed,
                    EndedAt: observed.AddMinutes(1),
                    InputTokens: null,
                    OutputTokens: null,
                    TotalTokens: null)],
                [new ObservedSessionEvent(
                    eventId,
                    sessionId,
                    runId,
                    SessionSourceSurface.VisualStudioCode,
                    ParentEventId: null,
                    TraceId: traceId,
                    Status: "completed",
                    SourceAdapter: "github-copilot-vscode-otel",
                    SourceEventId: $"event-{traceId}",
                    Type: "trace",
                    OccurredAt: observed,
                    SessionContentState.NotCaptured,
                    SourceApplicationVersion: "1.0.4",
                    AdapterVersion: "monitor-projection-v1",
                    SchemaFingerprint: null,
                    NormalizationVersion: "session-normalization-v1")]),
            []));
        return sessionId;
    }

    private static string ConnectionString(MonitorTempDirectory temp) =>
        new SqliteConnectionStringBuilder { DataSource = temp.DatabasePath, Pooling = false }.ToString();

    private static async Task AssertError(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal($"{{\"schema_version\":\"alert.center.v1\",\"error\":\"{code}\"}}", await response.Content.ReadAsStringAsync());
    }

    private sealed class ExistingEvidenceResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }

    private sealed class MissingCapabilityFixtureRule : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = new(
            "missing-capability-fixture",
            "1",
            "Missing capability fixture",
            "Exercises exact suppression context without evaluating a rule formula.",
            ["fixture-capability"],
            AlertRuleScope.Trace,
            [],
            "trace",
            [],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"],
            ["github-copilot-vscode"]);

        public AlertRuleOutcome Evaluate(AlertRuleContext context) => throw new InvalidOperationException();
    }

    private sealed class SessionOnlyEvidenceFixtureRule : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = new(
            "session-only-evidence-fixture",
            "1",
            "Session-only evidence fixture",
            "Projects one exact Session reference for Alert Center resolver coverage.",
            [],
            AlertRuleScope.Session,
            [],
            "session",
            [],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"],
            ["github-copilot-vscode"]);

        public AlertRuleOutcome Evaluate(AlertRuleContext context)
        {
            var signal = Assert.Single(context.Snapshot.Signals);
            return new(
                [new AlertRuleMatch(
                    AlertSeverity.Info,
                    [new AlertObservedValue("session-count", "sessions", 1)],
                    [signal.Evidence],
                    signal.ObservedAt,
                    signal.ObservedAt)],
                []);
        }
    }

    private sealed class ExactProjectionStore : ProjectionStoreTestDouble
    {
        private readonly Dictionary<string, MonitorTraceRow> traces = new(StringComparer.Ordinal);
        private readonly Dictionary<(string TraceId, string SpanId), MonitorSpanRow> spans = new();

        internal void Add(string traceId, string spanPrefix, string repository, string workspace)
        {
            traces[traceId] = new MonitorTraceRow(
                Id: traces.Count + 1,
                TraceId: traceId,
                ClientKind: "vscode-copilot-chat",
                ExperimentId: null,
                TaskId: null,
                TaskCategory: null,
                AgentVariant: null,
                PromptVersion: null,
                SpanCount: 5,
                ToolCallCount: 5,
                ErrorCount: 4,
                FirstSeenAt: "2026-07-22T10:00:00.0000000Z",
                LastSeenAt: "2026-07-22T10:00:04.0000000Z",
                ProjectedAt: "2026-07-22T10:01:00.0000000Z",
                InputTokens: null,
                OutputTokens: null,
                TotalTokens: null,
                TurnCount: null,
                AgentInvocationCount: null,
                DurationMs: 4000,
                PrimaryModel: null,
                RepositoryName: repository,
                WorkspaceLabel: workspace,
                RepoSnapshot: null,
                CacheReadTokens: null,
                CacheCreationTokens: null,
                TraceStatus: "unrecovered");
            for (var index = 0; index < 5; index++)
            {
                var spanId = $"{spanPrefix}-{index}";
                spans[(traceId, spanId)] = new MonitorSpanRow(
                    Id: spans.Count + 1,
                    RawRecordId: 1,
                    TraceId: traceId,
                    SpanId: spanId,
                    ParentSpanId: null,
                    SpanOrdinal: index,
                    Operation: "execute_tool",
                    Category: "tool",
                    ToolName: "fixture",
                    ToolType: null,
                    McpToolName: null,
                    McpServerHash: null,
                    AgentName: null,
                    RequestModel: null,
                    ResponseModel: null,
                    InputTokens: null,
                    OutputTokens: null,
                    TotalTokens: null,
                    ReasoningTokens: null,
                    CacheReadTokens: null,
                    CacheCreationTokens: null,
                    Status: index < 4 ? "error" : "ok",
                    ErrorType: index < 4 ? "fixture" : null,
                    FinishReasons: null,
                    ConversationId: null,
                    DurationMs: 1,
                    StartTime: null,
                    EndTime: null,
                    ProjectedAt: "2026-07-22T10:01:00.0000000Z");
            }
        }

        public override MonitorTraceRow? GetMonitorTrace(string traceId) => traces.GetValueOrDefault(traceId);

        public override MonitorSpanRow? GetMonitorSpan(string traceId, string spanId) => spans.GetValueOrDefault((traceId, spanId));
    }
}

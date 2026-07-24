using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterV2ContractMatrixTests
{
    [Fact]
    public async Task ExactSevenThousandByteFiltersLeaveRoomForStrictContinuationCursor()
    {
        using var temp = NewTemp();
        var (store, lifecycle) = Stores(temp);
        var label = new string('あ', 256);
        var encodedLabel = Uri.EscapeDataString(label);
        var firstEvaluation = AppendCost(store, 1);
        var secondEvaluation = AppendCost(store, 2);
        var queryStore = new SessionOverrideQueryStore(
            store,
            [firstEvaluation, secondEvaluation],
            label);
        await using var host = await StartAsync(
            temp,
            queryStore,
            lifecycle,
            label,
            label);
        var exactFilters =
            $"repository={encodedLabel}&workspace={encodedLabel}&session_id={encodedLabel}"
            + "&period=30d&limit=1&%63%75%72%72%65%6E%63%79=%61%6C%6C";
        Assert.Equal(7_000, Encoding.UTF8.GetByteCount(exactFilters));

        using var firstResponse = await host.Client.GetAsync(
            $"/api/alert-center/v2/alerts?{exactFilters}");
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStreamAsync());
        var cursor = Assert.IsType<string>(
            first.RootElement.GetProperty("next_cursor").GetString());
        var continuation =
            $"/api/alert-center/v2/alerts?{exactFilters}&cursor={cursor}";
        Assert.InRange(Encoding.UTF8.GetByteCount(continuation), 1, 8_192);

        using var secondResponse = await host.Client.GetAsync(continuation);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStreamAsync());
        Assert.NotEqual(
            CostAlertId(first.RootElement.GetProperty("items")[0]),
            CostAlertId(second.RootElement.GetProperty("items")[0]));
    }

    [Fact]
    public async Task MixedV1AndV2ReceiptsPaginateAndRespectKindFilters()
    {
        using var temp = NewTemp();
        var (store, lifecycle) = Stores(temp);
        var v1 = V1Evaluation();
        Assert.Equal(AlertStoreStatus.Success, store.Append(v1).Status);
        var cost = AppendCost(store, 1);
        await using var host = await StartAsync(temp, store, lifecycle);
        const string all = "/api/alert-center/v2/alerts?period=30d&limit=1";

        using var first = await GetJsonAsync(host, all);
        var cursor = Assert.IsType<string>(
            first.RootElement.GetProperty("next_cursor").GetString());
        using var second = await GetJsonAsync(host, $"{all}&cursor={cursor}");
        var pagedKinds = new[]
        {
            ReceiptKind(first.RootElement.GetProperty("items")[0]),
            ReceiptKind(second.RootElement.GetProperty("items")[0]),
        };

        using var v1Only = await GetJsonAsync(
            host,
            "/api/alert-center/v2/alerts?period=30d&receipt_kind=receipt_v1");
        using var costOnly = await GetJsonAsync(
            host,
            "/api/alert-center/v2/alerts?period=30d&receipt_kind=cost_receipt_v2");

        Assert.Equal(
            ["cost_receipt_v2", "receipt_v1"],
            pagedKinds.Order(StringComparer.Ordinal));
        var v1Item = Assert.Single(v1Only.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("receipt_v1", ReceiptKind(v1Item));
        Assert.Equal(
            v1.Receipts[0].AlertId,
            v1Item.GetProperty("receipt_v1").GetProperty("alert_id").GetString());
        var costItem = Assert.Single(costOnly.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("cost_receipt_v2", ReceiptKind(costItem));
        Assert.Equal(
            cost.Receipts[0].AlertId,
            CostAlertId(costItem));
    }

    [Fact]
    public async Task CostReceiptReflectsActualLifecycleMutation()
    {
        using var temp = NewTemp();
        var (store, lifecycle) = Stores(temp);
        var evaluation = AppendCost(store, 1);
        var alertId = evaluation.Receipts[0].AlertId;
        var mutation = lifecycle.Mutate(new(
            alertId,
            AlertLifecycleAction.Acknowledge,
            0,
            "user_reviewed",
            "sanitized note",
            "aid1_" + new string('a', 43)));
        Assert.Equal(AlertLifecycleStoreStatus.Success, mutation.Status);
        await using var host = await StartAsync(temp, store, lifecycle);

        using var response = await host.Client.GetAsync(
            $"/api/alert-center/v2/alerts?period=30d&receipt_kind=cost_receipt_v2&alert_id={alertId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("sanitized note", text, StringComparison.Ordinal);
        Assert.DoesNotContain("aid1_", text, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(text);
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        var projected = item.GetProperty("cost_receipt_v2").GetProperty("lifecycle");
        Assert.Equal("acknowledged", projected.GetProperty("state").GetString());
        Assert.Equal(1, projected.GetProperty("revision").GetInt64());
        var transition = Assert.Single(projected.GetProperty("history").EnumerateArray());
        Assert.Equal("acknowledge", transition.GetProperty("action").GetString());
        Assert.Equal("open", transition.GetProperty("previous_state").GetString());
        Assert.Equal("acknowledged", transition.GetProperty("state").GetString());
        Assert.Equal("user_reviewed", transition.GetProperty("reason_code").GetString());
    }

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
    };

    private static (SqliteAlertEngineStore Store, SqliteAlertLifecycleStore Lifecycle)
        Stores(MonitorTempDirectory temp)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = temp.DatabasePath,
            Pooling = false,
        }.ToString();
        var store = new SqliteAlertEngineStore(connectionString);
        Assert.Equal(AlertStoreStatus.Success, store.Initialize().Status);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.InitializeV2().Status);
        var lifecycle = new SqliteAlertLifecycleStore(connectionString, temp.TimeProvider);
        Assert.Equal(AlertLifecycleStoreStatus.Success, lifecycle.Initialize().Status);
        return (store, lifecycle);
    }

    private static Task<RunningMonitorHost> StartAsync(
        MonitorTempDirectory temp,
        IAlertEngineVersionedQueryStore store,
        IAlertLifecycleStore lifecycle,
        string repository = "repo-safe",
        string workspace = "workspace-safe") =>
        MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
            UseUserSecrets = false,
            AlertCenterReadModelV2 = new SqliteAlertCenterReadModelV2(
                store,
                lifecycle,
                new FixtureV1Projector(repository, workspace),
                new ExactPresentationResolver(repository, workspace)),
        });

    private static async Task<JsonDocument> GetJsonAsync(
        RunningMonitorHost host,
        string uri)
    {
        using var response = await host.Client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    }

    private static string ReceiptKind(JsonElement item) =>
        Assert.IsType<string>(item.GetProperty("receipt_kind").GetString());

    private static string CostAlertId(JsonElement item) =>
        Assert.IsType<string>(
            item.GetProperty("cost_receipt_v2").GetProperty("alert_id").GetString());

    private static AlertEvaluationResultV2 AppendCost(
        SqliteAlertEngineStore store,
        int index)
    {
        var evaluation = CostEvaluation(index);
        Assert.Equal(AlertEngineStoreStatusV2.Success, store.Append(evaluation).Status);
        return evaluation;
    }

    private static AlertEvaluationResultV2 CostEvaluation(int index)
    {
        var observedAt = new DateTimeOffset(2026, 7, 23, 1, 2, 3, TimeSpan.Zero)
            .AddMinutes(index - 1);
        var sessionId = $"01984045-9d80-7000-8000-{index:D12}";
        var estimateId = "pricing-estimate-" + index.ToString("x64");
        var eligibilityDigest = index.ToString("x64");
        var scope = new AlertCostScopeV2(
            AlertCostScopeIdentityV2.Create(
                AlertCostScopeKindV2.Session,
                null,
                null,
                eligibilityDigest,
                [sessionId]),
            AlertCostScopeKindV2.Session,
            null,
            null,
            [sessionId]);
        var member = new AlertCostMemberV2(
            sessionId,
            observedAt,
            observedAt.AddSeconds(1),
            "github-copilot",
            "1.2.3",
            AlertCostMemberStateV2.Estimated,
            1,
            AlertCostAttemptResultKindV2.Estimate,
            null,
            1,
            estimateId,
            observedAt.AddSeconds(2),
            new string('c', 64),
            "pricing-registry-v1",
            "github",
            "gpt-5",
            "api",
            2m,
            "USD");
        var snapshot = new AlertNormalizedSnapshotV2(
            AlertContractVersionsV2.Snapshot,
            "estimated_cost",
            "local-monitor-cost-analytics",
            "1",
            AlertCostAcquisitionStateV2.Complete,
            [],
            AlertCostAggregateStateV2.Available,
            eligibilityDigest,
            1,
            null,
            scope,
            "USD",
            2m,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            1,
            1,
            10_000,
            [member],
            [
                new(AlertEvidenceKindV2.Session, sessionId, sessionId, observedAt),
                new(
                    AlertEvidenceKindV2.PricingEstimate,
                    estimateId,
                    sessionId,
                    observedAt.AddSeconds(2)),
            ],
            AlertCostCompletenessV2.Full,
            [],
            observedAt,
            observedAt);
        var configuration = new AlertEngineConfigurationV2(
            AlertContractVersionsV2.Configuration,
            "cost.configuration.v1",
            "cost-configuration-" + new string('d', 64),
            1,
            new string('e', 64),
            [
                new(
                    "session-estimated-cost-threshold",
                    "1",
                    true,
                    "USD",
                    1m,
                    2m,
                    10_000,
                    AlertCostScopeKindV2.Session,
                    null),
            ]);
        var result = new AlertEvaluationEngine(
            new AlertRuleRegistryV2(),
            new ResolvedEvidenceV2()).Evaluate(
                new("session-estimated-cost-threshold", "1"),
                snapshot,
                configuration,
                new(AlertEvidenceReadViewV2.Instance, []));
        return Assert.IsType<AlertEvaluationResultV2>(result.Evaluation);
    }

    private static AlertEvaluationResult V1Evaluation()
    {
        var first = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        const string sessionId = "01984045-9d80-7000-8000-000000000100";
        const string traceId = "mixed-v1-trace";
        var signals = Enumerable.Range(0, 5).Select(index =>
        {
            var observedAt = first.AddSeconds(index);
            return new AlertSignal(
                $"mixed-signal-{index}",
                AlertSignalKind.ToolCall,
                index,
                observedAt,
                null,
                index < 4 ? AlertSignalStatus.Error : AlertSignalStatus.Success,
                [],
                [],
                new(
                    AlertEvidenceKind.Span,
                    $"mixed-evidence-{index}",
                    sessionId,
                    traceId,
                    $"mixed-span-{index}",
                    null,
                    null,
                    null,
                    observedAt));
        }).ToArray();
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "github-copilot-vscode",
            "1.0.4",
            sessionId,
            traceId,
            AlertCompleteness.Partial,
            ["schema_drift_detected"],
            first,
            signals[^1].ObservedAt,
            [new("tool-call-status", AlertCapabilityAvailability.Available)],
            signals);
        var configuration = new AlertEngineConfiguration(
            AlertContractVersions.Configuration,
            "alert-center-mixed-v1",
            [
                new(
                    "high-tool-failure-ratio",
                    "1",
                    true,
                    new Dictionary<string, decimal>(),
                    null),
            ]);
        var result = new AlertEvaluationEngine(
            new AlertRuleRegistry([new HighToolFailureRatioAlertRule()]),
            new ExistingEvidenceV1()).Evaluate(snapshot, configuration);
        Assert.Single(result.Receipts);
        return result;
    }

    private sealed class ResolvedEvidenceV2 : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) =>
            AlertEvidenceResolutionStatusV2.Resolved;
    }

    private sealed class ExistingEvidenceV1 : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }

    private sealed class SessionOverrideQueryStore(
        IAlertEngineVersionedQueryStore inner,
        IReadOnlyList<AlertEvaluationResultV2> evaluations,
        string sessionId) : IAlertEngineVersionedQueryStore
    {
        private readonly IReadOnlyDictionary<string, AlertEvaluationResultV2> byAlertId =
            evaluations.ToDictionary(
                item => item.Receipts[0].AlertId,
                StringComparer.Ordinal);

        public AlertVersionedReceiptQueryPage ListReceiptsVersioned(
            string? afterAlertId,
            int limit)
        {
            var page = inner.ListReceiptsVersioned(afterAlertId, limit);
            return page with
            {
                Items = page.Items.Select(item =>
                {
                    if (item.ContractVersion != AlertContractKind.V2)
                    {
                        return item;
                    }
                    var evaluation = byAlertId[item.ReceiptV2!.AlertId];
                    var receipt = evaluation.Receipts[0];
                    var scope = receipt.Scope with { SessionIds = [sessionId] };
                    var overridden = receipt with
                    {
                        Scope = scope,
                        Members = receipt.Members.Select(member =>
                            member with { SessionId = sessionId }).ToArray(),
                        Evidence = receipt.Evidence.Select(reference =>
                            reference with { SessionId = sessionId }).ToArray(),
                    };
                    return new AlertVersionedReceiptQueryItem(
                        AlertContractKind.V2,
                        item.CanonicalBytes,
                        null,
                        Project(overridden, evaluation.EligibilityDigest));
                }).ToArray(),
            };
        }

        private static AlertCenterReceiptProjectionV2 Project(
            AlertReceiptV2 receipt,
            string eligibilityDigest) =>
            (AlertCenterReceiptProjectionV2)typeof(AlertCenterReceiptProjectionV2)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    binder: null,
                    [typeof(AlertReceiptV2), typeof(string)],
                    modifiers: null)!
                .Invoke([receipt, eligibilityDigest]);

        public AlertVersionedEvaluationQueryPage ListEvaluationsVersioned(
            string? afterEvaluationId,
            int limit) =>
            inner.ListEvaluationsVersioned(afterEvaluationId, limit);

        public AlertVersionedSuppressionQueryPage ListSuppressionsVersioned(
            string evaluationId,
            long? afterSuppressionOrdinal,
            int limit) =>
            inner.ListSuppressionsVersioned(
                evaluationId,
                afterSuppressionOrdinal,
                limit);
    }

    private sealed class ExactPresentationResolver(
        string repository,
        string workspace) : ICostAlertPresentationResolverV1
    {
        public CostAlertPresentationResolutionV1 Resolve(
            IReadOnlyList<AlertCostMemberV2> members,
            IReadOnlyList<AlertEvidenceReferenceV2> evidence) =>
            new(
                "success",
                members.Select(item => new CostAlertPresentationMemberV1(
                    item.SessionId,
                    item.SessionEffectiveAtUtc,
                    "available",
                    repository,
                    workspace,
                    "available",
                    $"/costs?session_id={Uri.EscapeDataString(item.SessionId)}",
                    item.EstimateId,
                    item.EstimateId is null ? null : "available",
                    item.EstimateId is null
                        ? null
                        : $"/costs?session_id={Uri.EscapeDataString(item.SessionId)}&estimate_id={Uri.EscapeDataString(item.EstimateId)}"))
                    .ToArray());
    }

    private sealed class FixtureV1Projector(
        string repository,
        string workspace) :
        IAlertCenterReadModel,
        IAlertCenterOwnedReceiptProjectorV1
    {
        public AlertCenterReadResult Read(AlertCenterQuery query) =>
            new(AlertCenterReadStatus.Unavailable);

        public AlertCenterOwnedProjectionResult ProjectOwned(
            IReadOnlyList<AlertCenterReceiptProjectionV1> receipts,
            AlertCenterQuery query,
            bool incomplete)
        {
            var alerts = receipts.Select(Project).ToArray();
            return new(
                AlertCenterReadStatus.Success,
                alerts,
                alerts,
                []);
        }

        private AlertCenterAlert Project(AlertCenterReceiptProjectionV1 receipt)
        {
            var lifecycle = new AlertCenterLifecycle(
                "open",
                0,
                null,
                ["acknowledge", "dismiss", "resolve"],
                []);
            return new(
                receipt.AlertId,
                Wire(receipt.Severity),
                "open",
                lifecycle,
                new(
                    receipt.RuleId,
                    receipt.RuleVersion,
                    "registered",
                    "High tool failure ratio",
                    "Fixture projection",
                    null,
                    "trace",
                    "trace",
                    receipt.RequiredCapabilities,
                    []),
                receipt.ObservedValues.Select(item =>
                    new AlertCenterValue(item.Name, item.Unit, item.Value)).ToArray(),
                receipt.EffectiveThresholds.Select(item =>
                    new AlertCenterValue(item.Name, item.Unit, item.Value)).ToArray(),
                new(receipt.SourceSurface, receipt.SourceVersion, "supported_at_evaluation"),
                receipt.SessionId,
                receipt.TraceId,
                new("available", repository, workspace, null, null, null, null),
                new(Wire(receipt.Completeness), receipt.CompletenessReasons),
                Timestamp(receipt.FirstObservedAt),
                Timestamp(receipt.LastObservedAt),
                receipt.Summary,
                receipt.Evidence.Select(item => new AlertCenterEvidence(
                    item.Kind.ToString().ToLowerInvariant(),
                    item.EvidenceId,
                    item.SessionId,
                    item.TraceId,
                    item.SpanId,
                    item.TurnId,
                    item.EventId,
                    item.ToolCallId,
                    Timestamp(item.ObservedAt),
                    "unknown",
                    null,
                    null)).ToArray(),
                receipt.Evidence.Count,
                new([], []),
                "fixture",
                receipt.EvaluationId);
        }

        private static string Timestamp(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        private static string Wire<T>(T value) where T : struct, Enum =>
            value.ToString().ToLowerInvariant();
    }
}

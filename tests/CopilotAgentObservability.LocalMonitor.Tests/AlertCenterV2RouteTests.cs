using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterV2RouteTests
{
    [Fact]
    public async Task ReadRoute_UsesAdditiveV2ErrorEnvelopeWithoutChangingV1()
    {
        using var temp = NewTemp();
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            testOptions: Options(new FixtureReadModelV2(AlertCenterReadStatusV2.Unavailable)));

        using var v2 = await host.Client.GetAsync("/api/alert-center/v2/alerts?period=30d");
        using var v1 = await host.Client.GetAsync("/api/alert-center/v1/alerts?period=30d");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, v2.StatusCode);
        Assert.Equal("no-store", v2.Headers.CacheControl?.ToString());
        Assert.Equal(
            """{"schema_version":"alert.center.error.v2","error":"alert_center_store_unavailable"}""",
            await v2.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, v1.StatusCode);
        Assert.Contains(
            "\"schema_version\":\"alert.center.v1\"",
            await v1.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRoute_RejectsV1OffsetAndUnknownOrRepeatedV2QueryMembers()
    {
        using var temp = NewTemp();
        var model = new FixtureReadModelV2(AlertCenterReadStatusV2.Unavailable);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(model));

        foreach (var query in new[]
        {
            "offset=0",
            "period=30d&period=7d",
            "unknown=value",
            "receipt_kind=unknown",
            "scope_kind=unknown",
            "currency=EUR",
            "coverage_state=unknown",
            "period=30d&&limit=1",
        })
        {
            using var response = await host.Client.GetAsync($"/api/alert-center/v2/alerts?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal(
                """{"schema_version":"alert.center.error.v2","error":"alert_center_invalid_query"}""",
                await response.Content.ReadAsStringAsync());
        }

        Assert.Equal(0, model.ReadCount);
    }

    [Fact]
    public async Task ReadRoute_ReservesExactSevenThousandByteFilterBudgetForCursor()
    {
        using var temp = NewTemp();
        var model = new FixtureReadModelV2(AlertCenterReadStatusV2.Unavailable);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(model));
        var label = Uri.EscapeDataString(new string('あ', 256));
        var prefix = $"repository={label}&workspace={label}&session_id={label}&period=30d&rule_id=";
        var atLimit = prefix + new string('a', 34);
        var overLimit = prefix + new string('a', 35);
        Assert.Equal(7_000, System.Text.Encoding.UTF8.GetByteCount(atLimit));
        Assert.Equal(7_001, System.Text.Encoding.UTF8.GetByteCount(overLimit));

        using var accepted = await host.Client.GetAsync($"/api/alert-center/v2/alerts?{atLimit}");
        using var rejected = await host.Client.GetAsync($"/api/alert-center/v2/alerts?{overLimit}");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(1, model.ReadCount);
    }

    [Fact]
    public async Task ReadRoute_ReplaysDirectForwardAndBackwardCursorsAndRejectsNoncanonicalCursor()
    {
        using var temp = NewTemp();
        var evaluations = Enumerable.Range(1, 4).Select(CostEvaluation).ToArray();
        var owner = new FixtureVersionedOwner(evaluations);
        var model = new SqliteAlertCenterReadModelV2(
            owner,
            new FixtureLifecycleStore(evaluations.Select(item => item.Receipts[0].AlertId).ToArray()),
            new EmptyV1ReadModel(),
            new ExactCostPresentationResolver());
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(model));
        const string pageOneUri = "/api/alert-center/v2/alerts?period=30d&limit=1";

        using var pageOne = await GetJsonAsync(host, pageOneUri);
        var pageOneCursor = RequiredString(pageOne.RootElement, "next_cursor");
        using var pageTwo = await GetJsonAsync(host, $"{pageOneUri}&cursor={pageOneCursor}");
        var pageTwoCursor = RequiredString(pageTwo.RootElement, "next_cursor");
        using var directPageTwo = await GetJsonAsync(host, $"{pageOneUri}&cursor={pageOneCursor}");
        using var pageThree = await GetJsonAsync(host, $"{pageOneUri}&cursor={pageTwoCursor}");
        var previousCursor = RequiredString(pageThree.RootElement, "previous_cursor");
        using var backwardPageTwo = await GetJsonAsync(host, $"{pageOneUri}&cursor={previousCursor}");

        Assert.Equal(ItemAlertId(pageTwo), ItemAlertId(directPageTwo));
        Assert.Equal(ItemAlertId(pageTwo), ItemAlertId(backwardPageTwo));
        Assert.Equal(pageOneCursor, previousCursor);

        using var rejected = await host.Client.GetAsync(
            $"{pageOneUri}&cursor={Uri.EscapeDataString(pageOneCursor + "=")}");
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(
            """{"schema_version":"alert.center.error.v2","error":"alert_center_invalid_query"}""",
            await rejected.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadRoute_RejectsCanonicalMemberCursorThatWasNotAPageBoundary()
    {
        using var temp = NewTemp();
        var evaluations = Enumerable.Range(1, 4).Select(CostEvaluation).ToArray();
        var model = new SqliteAlertCenterReadModelV2(
            new FixtureVersionedOwner(evaluations),
            new FixtureLifecycleStore(evaluations.Select(item => item.Receipts[0].AlertId).ToArray()),
            new EmptyV1ReadModel(),
            new ExactCostPresentationResolver());
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(model));
        const string pageOneUri = "/api/alert-center/v2/alerts?period=30d&limit=2";
        using var pageOne = await GetJsonAsync(host, pageOneUri);
        var boundaryCursor = RequiredString(pageOne.RootElement, "next_cursor");
        var firstItem = pageOne.RootElement.GetProperty("items")[0].GetProperty("cost_receipt_v2");
        var memberCursor = ReplaceCursorTuple(
            boundaryCursor,
            firstItem.GetProperty("severity").GetString()!,
            firstItem.GetProperty("last_observed_at").GetString()!,
            firstItem.GetProperty("alert_id").GetString()!);

        using var response = await host.Client.GetAsync($"{pageOneUri}&cursor={memberCursor}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            """{"schema_version":"alert.center.error.v2","error":"alert_center_invalid_query"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadRoute_ProjectsVersionedCostReceiptWithLifecycleAndExactEvidenceLinks()
    {
        using var temp = NewTemp();
        var evaluation = CostEvaluation();
        var owner = new FixtureVersionedOwner(evaluation);
        var model = new SqliteAlertCenterReadModelV2(
            owner,
            new FixtureLifecycleStore(evaluation.Receipts[0].AlertId),
            new EmptyV1ReadModel(),
            new ExactCostPresentationResolver());
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
            UseUserSecrets = false,
            AlertCenterReadModelV2 = model,
        });

        using var response = await host.Client.GetAsync(
            "/api/alert-center/v2/alerts?receipt_kind=cost_receipt_v2&scope_kind=session&currency=USD&coverage_state=full&period=30d");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        var root = json.RootElement;
        Assert.Equal("alert.center.v2", root.GetProperty("schema_version").GetString());
        Assert.Equal("complete", root.GetProperty("acquisition_state").GetString());
        Assert.Equal("exact", root.GetProperty("match_count_state").GetString());
        var union = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal("cost_receipt_v2", union.GetProperty("receipt_kind").GetString());
        Assert.Equal(JsonValueKind.Null, union.GetProperty("receipt_v1").ValueKind);
        var receipt = union.GetProperty("cost_receipt_v2");
        Assert.Equal(evaluation.Receipts[0].AlertId, receipt.GetProperty("alert_id").GetString());
        Assert.Equal("open", receipt.GetProperty("lifecycle").GetProperty("state").GetString());
        Assert.Equal("registered", receipt.GetProperty("rule").GetProperty("contract_state").GetString());
        Assert.Equal("estimated USD amount >= configured threshold", receipt.GetProperty("formula").GetString());
        Assert.Equal("USD", receipt.GetProperty("currency").GetString());
        Assert.Equal(10_000, receipt.GetProperty("coverage_basis_points").GetInt32());
        var member = Assert.Single(receipt.GetProperty("members").EnumerateArray());
        Assert.Equal("/costs?session_id=01984045-9d80-7000-8000-000000000001", member.GetProperty("session_href").GetString());
        Assert.Contains("estimate_id=pricing-estimate-", member.GetProperty("estimate_href").GetString(), StringComparison.Ordinal);
        Assert.All(receipt.GetProperty("evidence").EnumerateArray(), item =>
            Assert.Equal("available", item.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task ReadRoute_FailsClosedWhenPresentationResolverReturnsUnsafeHref()
    {
        using var temp = NewTemp();
        var evaluation = CostEvaluation();
        var owner = new FixtureVersionedOwner(evaluation);
        var model = new SqliteAlertCenterReadModelV2(
            owner,
            new FixtureLifecycleStore(evaluation.Receipts[0].AlertId),
            new EmptyV1ReadModel(),
            new UnsafeCostPresentationResolver());
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(model));

        using var response = await host.Client.GetAsync("/api/alert-center/v2/alerts?period=30d");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(
            """{"schema_version":"alert.center.error.v2","error":"alert_center_store_unavailable"}""",
            text);
        Assert.DoesNotContain("private-host-path", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRoute_FailsClosedWhenLifecycleSuccessNamesAnotherAlert()
    {
        using var temp = NewTemp();
        var evaluation = CostEvaluation();
        var model = new SqliteAlertCenterReadModelV2(
            new FixtureVersionedOwner(evaluation),
            new WrongAlertLifecycleStore(),
            new EmptyV1ReadModel(),
            new ExactCostPresentationResolver());
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(model));

        using var response = await host.Client.GetAsync("/api/alert-center/v2/alerts?period=30d");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            """{"schema_version":"alert.center.error.v2","error":"alert_center_store_unavailable"}""",
            await response.Content.ReadAsStringAsync());
    }

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(
            new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
    };

    private static MonitorHostTestOptions Options(IAlertCenterReadModelV2 readModel) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        AlertCenterReadModelV2 = readModel,
    };

    private static async Task<JsonDocument> GetJsonAsync(RunningMonitorHost host, string uri)
    {
        using var response = await host.Client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
    }

    private static string RequiredString(JsonElement element, string propertyName) =>
        Assert.IsType<string>(element.GetProperty(propertyName).GetString());

    private static string ItemAlertId(JsonDocument page) =>
        RequiredString(
            page.RootElement.GetProperty("items")[0].GetProperty("cost_receipt_v2"),
            "alert_id");

    private static string ReplaceCursorTuple(
        string cursor,
        string severity,
        string lastObservedAt,
        string alertId)
    {
        const string prefix = "alert-center-cursor-v2.";
        var encoded = cursor[prefix.Length..];
        var bytes = Convert.FromBase64String(
            encoded.Replace('-', '+').Replace('_', '/')
            + new string('=', (4 - encoded.Length % 4) % 4));
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = root.GetProperty("schema_version").GetString(),
            snapshot_id = root.GetProperty("snapshot_id").GetString(),
            filter_digest = root.GetProperty("filter_digest").GetString(),
            limit = root.GetProperty("limit").GetInt32(),
            severity_rank = severity switch
            {
                "critical" => 0,
                "warning" => 1,
                _ => 2,
            },
            last_observed_at = lastObservedAt,
            alert_id = alertId,
        });
        return prefix
            + Convert.ToBase64String(canonical)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }

    private static AlertEvaluationResultV2 CostEvaluation(int index = 1)
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
                new(AlertEvidenceKindV2.PricingEstimate, estimateId, sessionId, observedAt.AddSeconds(2)),
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

    private sealed class FixtureReadModelV2(AlertCenterReadStatusV2 status)
        : IAlertCenterReadModelV2
    {
        internal int ReadCount { get; private set; }

        public AlertCenterReadResultV2 Read(AlertCenterQueryV2 query)
        {
            ReadCount++;
            return new(status);
        }
    }

    private sealed class ResolvedEvidenceV2 : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) =>
            AlertEvidenceResolutionStatusV2.Resolved;
    }

    private sealed class ExactCostPresentationResolver
        : ICostAlertPresentationResolverV1
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
                    "repo-safe",
                    "workspace-safe",
                    "available",
                    $"/costs?session_id={Uri.EscapeDataString(item.SessionId)}",
                    item.EstimateId,
                    item.EstimateId is null ? null : "available",
                    item.EstimateId is null
                        ? null
                        : $"/costs?session_id={Uri.EscapeDataString(item.SessionId)}&estimate_id={Uri.EscapeDataString(item.EstimateId)}"))
                    .ToArray());
    }

    private sealed class UnsafeCostPresentationResolver
        : ICostAlertPresentationResolverV1
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
                    "private-host-path",
                    null,
                    "available",
                    @"C:\private-host-path",
                    item.EstimateId,
                    "available",
                    @"C:\private-host-path"))
                .ToArray());
    }

    private sealed class FixtureVersionedOwner(params AlertEvaluationResultV2[] evaluations)
        : IAlertEngineStoreV2, IAlertEngineVersionedQueryStore
    {
        private readonly IReadOnlyList<AlertVersionedReceiptQueryItem> receipts =
            evaluations.Select(evaluation =>
                {
                    var receiptBytes = AlertCanonicalJsonV2.SerializeReceipt(evaluation.Receipts[0]);
                    return new AlertVersionedReceiptQueryItem(
                        AlertContractKind.V2,
                        receiptBytes,
                        null,
                        AlertCenterReceiptConsumerV2.Validate(
                            receiptBytes,
                            evaluation.EligibilityDigest));
                })
                .OrderBy(item => item.ReceiptV2!.AlertId, StringComparer.Ordinal)
                .ToArray();
        private readonly IReadOnlyList<AlertVersionedEvaluationQueryItem> evaluationItems =
            evaluations.Select(evaluation =>
                {
                    var evaluationBytes = AlertCanonicalJsonV2.SerializeEvaluation(evaluation);
                    return new AlertVersionedEvaluationQueryItem(
                        AlertContractKind.V2,
                        evaluationBytes,
                        null,
                        AlertEvaluationConsumerV2.Validate(evaluationBytes));
                })
                .OrderBy(item => item.EvaluationV2!.EvaluationId, StringComparer.Ordinal)
                .ToArray();

        public AlertEngineStoreResultV2 InitializeV2() =>
            new(AlertEngineStoreStatusV2.Success);

        public AlertVersionedReceiptQueryPage ListReceiptsVersioned(
            string? afterAlertId,
            int limit)
        {
            var start = afterAlertId is null
                ? 0
                : receipts.Select((item, index) => (item, index))
                    .Single(pair => pair.item.ReceiptV2!.AlertId == afterAlertId).index + 1;
            var items = receipts.Skip(start).Take(limit).ToArray();
            var exhausted = start + items.Length == receipts.Count;
            return new(
                AlertEngineQueryStatus.Success,
                items,
                exhausted ? null : items[^1].ReceiptV2!.AlertId,
                exhausted,
                items.Sum(item => item.CanonicalBytes.Count));
        }

        public AlertVersionedEvaluationQueryPage ListEvaluationsVersioned(
            string? afterEvaluationId,
            int limit)
        {
            var start = afterEvaluationId is null
                ? 0
                : evaluationItems.Select((item, index) => (item, index))
                    .Single(pair => pair.item.EvaluationV2!.EvaluationId == afterEvaluationId).index + 1;
            var items = evaluationItems.Skip(start).Take(limit).ToArray();
            var exhausted = start + items.Length == evaluationItems.Count;
            return new(
                AlertEngineQueryStatus.Success,
                items,
                exhausted ? null : items[^1].EvaluationV2!.EvaluationId,
                exhausted,
                items.Sum(item => item.CanonicalBytes.Count));
        }

        public AlertVersionedSuppressionQueryPage ListSuppressionsVersioned(
            string evaluationId,
            long? afterSuppressionOrdinal,
            int limit) =>
            new(AlertEngineQueryStatus.Success, [], Exhausted: true);

        public AlertEngineStoreResultV2 Append(AlertEvaluationResultV2 value) =>
            new(AlertEngineStoreStatusV2.Unavailable);
        public AlertEngineStoreReadResultV2 GetEvaluationV2(string evaluationId) =>
            new(AlertEngineQueryStatus.Unavailable, []);
        public AlertEngineStoreReadResultV2 GetReceiptV2(string alertId) =>
            new(AlertEngineQueryStatus.Unavailable, []);
        public AlertEngineStoreListResultV2 ListSuppressionsV2(string evaluationId) =>
            new(AlertEngineQueryStatus.Unavailable, []);
    }

    private sealed class FixtureLifecycleStore(params string[] alertIds) : IAlertLifecycleStore
    {
        private readonly HashSet<string> knownAlertIds = alertIds.ToHashSet(StringComparer.Ordinal);

        public AlertLifecycleStoreResult Initialize() =>
            new(AlertLifecycleStoreStatus.Success);
        public AlertLifecycleStoreResult Get(string requestedAlertId) =>
            knownAlertIds.Contains(requestedAlertId)
                ? new(
                    AlertLifecycleStoreStatus.Success,
                    Lifecycle: new(
                        AlertLifecycleContractVersions.Lifecycle,
                        requestedAlertId,
                        AlertLifecycleState.Open,
                        0,
                        null))
                : new(AlertLifecycleStoreStatus.NotFound);
        public AlertLifecycleHistoryResult History(string requestedAlertId, int limit = 50) =>
            knownAlertIds.Contains(requestedAlertId)
                ? new(AlertLifecycleStoreStatus.Success, [])
                : new(AlertLifecycleStoreStatus.NotFound, []);
        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
    }

    private sealed class WrongAlertLifecycleStore : IAlertLifecycleStore
    {
        private static readonly string OtherAlertId = new('f', 64);
        public AlertLifecycleStoreResult Initialize() => new(AlertLifecycleStoreStatus.Success);
        public AlertLifecycleStoreResult Get(string alertId) => new(
            AlertLifecycleStoreStatus.Success,
            Lifecycle: new(
                AlertLifecycleContractVersions.Lifecycle,
                OtherAlertId,
                AlertLifecycleState.Open,
                0,
                null));
        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) =>
            new(AlertLifecycleStoreStatus.Success, []);
        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) => new(AlertLifecycleStoreStatus.Unavailable);
    }

    private sealed class EmptyV1ReadModel : IAlertCenterReadModel
    {
        public AlertCenterReadResult Read(AlertCenterQuery query) =>
            new(
                AlertCenterReadStatus.Success,
                new(
                    AlertCenterContractVersions.Center,
                    "2026-07-23T12:00:00.0000000Z",
                    new(
                        query.AlertId,
                        query.SessionId,
                        query.TraceId,
                        query.Severity,
                        query.State,
                        query.RuleId,
                        query.SourceSurface,
                        query.Repository,
                        query.Workspace,
                        query.Completeness,
                        query.From.ToString("yyyy-MM-dd"),
                        query.To.ToString("yyyy-MM-dd"),
                        query.Offset,
                        query.Limit),
                    "complete",
                    0,
                    "complete",
                    0,
                    0,
                    [],
                    [],
                    []));
    }
}

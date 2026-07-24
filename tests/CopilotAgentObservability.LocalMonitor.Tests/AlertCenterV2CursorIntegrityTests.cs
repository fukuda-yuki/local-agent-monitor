using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterV2CursorIntegrityTests
{
    [Fact]
    public void Read_RejectsPriorCursorAsStaleWhenAcquiredOwnerOrderChanges()
    {
        var initial = Enumerable.Range(1, 4).Select(CostEvaluation).ToArray();
        var owner = new MutableVersionedOwner(initial);
        var model = Model(owner, initial.Concat([CostEvaluation(5)]).ToArray());
        var query = Query(limit: 1);
        var firstPage = Snapshot(model.Read(query));
        var cursor = Assert.IsType<string>(firstPage.NextCursor);

        owner.Replace(initial.Concat([CostEvaluation(5)]).ToArray());

        var result = model.Read(query with { Cursor = cursor });

        Assert.Equal(AlertCenterReadStatusV2.SnapshotChanged, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Read_RejectsCanonicalMemberCursorThatIsNotAPlannedPageBoundary()
    {
        var evaluations = Enumerable.Range(1, 4).Select(CostEvaluation).ToArray();
        var model = Model(new MutableVersionedOwner(evaluations), evaluations);
        var query = Query(limit: 2);
        var firstPage = Snapshot(model.Read(query));
        var boundaryCursor = Assert.IsType<string>(firstPage.NextCursor);
        var nonBoundaryMember = Assert.IsType<AlertCenterCostReceiptV2>(
            firstPage.Items[0].CostReceiptV2);
        var cursor = RewriteCursor(
            boundaryCursor,
            nonBoundaryMember.Severity,
            nonBoundaryMember.LastObservedAt,
            nonBoundaryMember.AlertId);

        var result = model.Read(query with { Cursor = cursor });

        Assert.Equal(AlertCenterReadStatusV2.InvalidQuery, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Read_RejectsCanonicalCursorInventoryContainingUnknownMember()
    {
        var evaluations = Enumerable.Range(1, 4).Select(CostEvaluation).ToArray();
        var model = Model(new MutableVersionedOwner(evaluations), evaluations);
        var query = Query(limit: 1);
        var firstPage = Snapshot(model.Read(query));
        var cursor = AddUnknownCursorMember(
            Assert.IsType<string>(firstPage.NextCursor));

        var result = model.Read(query with { Cursor = cursor });

        Assert.Equal(AlertCenterReadStatusV2.InvalidQuery, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Read_RejectsCanonicalCursorTupleNamingAbsentAlert()
    {
        var evaluations = Enumerable.Range(1, 4).Select(CostEvaluation).ToArray();
        var model = Model(new MutableVersionedOwner(evaluations), evaluations);
        var query = Query(limit: 1);
        var firstPage = Snapshot(model.Read(query));
        var boundaryCursor = Assert.IsType<string>(firstPage.NextCursor);
        var boundary = Assert.IsType<AlertCenterCostReceiptV2>(
            firstPage.Items[0].CostReceiptV2);
        var cursor = RewriteCursor(
            boundaryCursor,
            boundary.Severity,
            boundary.LastObservedAt,
            new string('f', 64));

        var result = model.Read(query with { Cursor = cursor });

        Assert.Equal(AlertCenterReadStatusV2.InvalidQuery, result.Status);
        Assert.Null(result.Snapshot);
    }

    private static SqliteAlertCenterReadModelV2 Model(
        MutableVersionedOwner owner,
        IReadOnlyList<AlertEvaluationResultV2> lifecycleEvaluations) =>
        new(
            owner,
            new FixtureLifecycleStore(
                lifecycleEvaluations.Select(item => item.Receipts[0].AlertId)),
            new EmptyV1ReadModel(),
            new ExactCostPresentationResolver());

    private static AlertCenterQueryV2 Query(int limit) => new(
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        null,
        new DateOnly(2026, 7, 1),
        new DateOnly(2026, 8, 1),
        "all",
        "all",
        "all",
        "all",
        null,
        limit);

    private static AlertCenterSnapshotV2 Snapshot(AlertCenterReadResultV2 result)
    {
        Assert.Equal(AlertCenterReadStatusV2.Success, result.Status);
        return Assert.IsType<AlertCenterSnapshotV2>(result.Snapshot);
    }

    private static string RewriteCursor(
        string cursor,
        string severity,
        string lastObservedAt,
        string alertId)
    {
        using var document = DecodeCursor(cursor);
        var root = document.RootElement;
        return EncodeCursor(JsonSerializer.SerializeToUtf8Bytes(new
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
        }));
    }

    private static string AddUnknownCursorMember(string cursor)
    {
        using var document = DecodeCursor(cursor);
        var root = document.RootElement;
        return EncodeCursor(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = root.GetProperty("schema_version").GetString(),
            snapshot_id = root.GetProperty("snapshot_id").GetString(),
            filter_digest = root.GetProperty("filter_digest").GetString(),
            limit = root.GetProperty("limit").GetInt32(),
            severity_rank = root.GetProperty("severity_rank").GetInt32(),
            last_observed_at = root.GetProperty("last_observed_at").GetString(),
            alert_id = root.GetProperty("alert_id").GetString(),
            unknown_member = "must-be-rejected",
        }));
    }

    private static JsonDocument DecodeCursor(string cursor)
    {
        const string prefix = "alert-center-cursor-v2.";
        Assert.StartsWith(prefix, cursor, StringComparison.Ordinal);
        var encoded = cursor[prefix.Length..];
        return JsonDocument.Parse(Convert.FromBase64String(
            encoded.Replace('-', '+').Replace('_', '/')
            + new string('=', (4 - encoded.Length % 4) % 4)));
    }

    private static string EncodeCursor(byte[] canonicalBytes) =>
        "alert-center-cursor-v2."
        + Convert.ToBase64String(canonicalBytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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

    private sealed class ResolvedEvidenceV2 : IAlertEvidenceResolverV2
    {
        public AlertEvidenceResolutionStatusV2 Resolve(
            AlertEvidenceReferenceV2 reference,
            AlertEvidenceResolutionScopeV2 scope) =>
            AlertEvidenceResolutionStatusV2.Resolved;
    }

    private sealed class MutableVersionedOwner : IAlertEngineVersionedQueryStore
    {
        private IReadOnlyList<AlertVersionedReceiptQueryItem> receipts = [];
        private IReadOnlyList<AlertVersionedEvaluationQueryItem> evaluationItems = [];

        public MutableVersionedOwner(IReadOnlyList<AlertEvaluationResultV2> evaluations)
        {
            Replace(evaluations);
        }

        public void Replace(IReadOnlyList<AlertEvaluationResultV2> evaluations)
        {
            receipts = evaluations.Select(evaluation =>
                {
                    var bytes = AlertCanonicalJsonV2.SerializeReceipt(evaluation.Receipts[0]);
                    return new AlertVersionedReceiptQueryItem(
                        AlertContractKind.V2,
                        bytes,
                        null,
                        AlertCenterReceiptConsumerV2.Validate(
                            bytes,
                            evaluation.EligibilityDigest));
                })
                .OrderBy(item => item.ReceiptV2!.AlertId, StringComparer.Ordinal)
                .ToArray();
            evaluationItems = evaluations.Select(evaluation =>
                {
                    var bytes = AlertCanonicalJsonV2.SerializeEvaluation(evaluation);
                    return new AlertVersionedEvaluationQueryItem(
                        AlertContractKind.V2,
                        bytes,
                        null,
                        AlertEvaluationConsumerV2.Validate(bytes));
                })
                .OrderBy(item => item.EvaluationV2!.EvaluationId, StringComparer.Ordinal)
                .ToArray();
        }

        public AlertVersionedReceiptQueryPage ListReceiptsVersioned(
            string? afterAlertId,
            int limit) =>
            Page(
                receipts,
                afterAlertId,
                limit,
                item => item.ReceiptV2!.AlertId);

        public AlertVersionedEvaluationQueryPage ListEvaluationsVersioned(
            string? afterEvaluationId,
            int limit)
        {
            var start = StartAfter(
                evaluationItems,
                afterEvaluationId,
                item => item.EvaluationV2!.EvaluationId);
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

        private static AlertVersionedReceiptQueryPage Page(
            IReadOnlyList<AlertVersionedReceiptQueryItem> source,
            string? after,
            int limit,
            Func<AlertVersionedReceiptQueryItem, string> key)
        {
            var start = StartAfter(source, after, key);
            var items = source.Skip(start).Take(limit).ToArray();
            var exhausted = start + items.Length == source.Count;
            return new(
                AlertEngineQueryStatus.Success,
                items,
                exhausted ? null : key(items[^1]),
                exhausted,
                items.Sum(item => item.CanonicalBytes.Count));
        }

        private static int StartAfter<T>(
            IReadOnlyList<T> source,
            string? after,
            Func<T, string> key) =>
            after is null
                ? 0
                : source.Select((item, index) => (item, index))
                    .Single(pair => key(pair.item) == after).index + 1;
    }

    private sealed class FixtureLifecycleStore(IEnumerable<string> alertIds)
        : IAlertLifecycleStore
    {
        private readonly HashSet<string> knownAlertIds =
            alertIds.ToHashSet(StringComparer.Ordinal);

        public AlertLifecycleStoreResult Initialize() =>
            new(AlertLifecycleStoreStatus.Success);

        public AlertLifecycleStoreResult Get(string alertId) =>
            knownAlertIds.Contains(alertId)
                ? new(
                    AlertLifecycleStoreStatus.Success,
                    Lifecycle: new(
                        AlertLifecycleContractVersions.Lifecycle,
                        alertId,
                        AlertLifecycleState.Open,
                        0,
                        null))
                : new(AlertLifecycleStoreStatus.NotFound);

        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) =>
            knownAlertIds.Contains(alertId)
                ? new(AlertLifecycleStoreStatus.Success, [])
                : new(AlertLifecycleStoreStatus.NotFound, []);

        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);

        public AlertLifecycleStoreResult ResolveFromReevaluation(
            AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);

        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);

        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
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
                        : $"/costs?session_id={Uri.EscapeDataString(item.SessionId)}"
                          + $"&estimate_id={Uri.EscapeDataString(item.EstimateId)}"))
                    .ToArray());
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

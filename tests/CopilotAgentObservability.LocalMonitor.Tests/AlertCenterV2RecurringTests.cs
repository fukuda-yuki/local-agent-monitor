using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterV2RecurringTests
{
    [Fact]
    public void Read_AggregatesRecurringGroupsAcrossAllOwnerPages()
    {
        var receipts = ReceiptItems(101);
        var owner = new PagedVersionedOwner(receipts);
        var projector = new RecurringProjector();
        var model = new SqliteAlertCenterReadModelV2(
            owner,
            new UnusedLifecycleStore(),
            projector,
            new UnavailableCostAlertPresentationResolverV1());

        var result = model.Read(Query());

        Assert.Equal(AlertCenterReadStatusV2.Success, result.Status);
        var snapshot = Assert.IsType<AlertCenterSnapshotV2>(result.Snapshot);
        Assert.Equal("complete", snapshot.AcquisitionState);
        Assert.Equal("complete", snapshot.RecurringState);
        var recurring = Assert.Single(snapshot.RecurringGroups);
        Assert.Equal(101, recurring.OccurrenceCount);
        Assert.Equal(101, recurring.AlertIds.Count);
        Assert.Equal(2, owner.ReceiptPageCount);
        Assert.Contains(projector.Calls, call => call.Count == 101 && !call.Incomplete);
    }

    [Fact]
    public void Read_WithholdsRecurringGroupsWhenOwnerAcquisitionIsIncomplete()
    {
        var receipts = ReceiptItems(2_001);
        var owner = new PagedVersionedOwner(receipts);
        var projector = new RecurringProjector();
        var model = new SqliteAlertCenterReadModelV2(
            owner,
            new UnusedLifecycleStore(),
            projector,
            new UnavailableCostAlertPresentationResolverV1());

        var result = model.Read(Query());

        Assert.Equal(AlertCenterReadStatusV2.Success, result.Status);
        var snapshot = Assert.IsType<AlertCenterSnapshotV2>(result.Snapshot);
        Assert.Equal("incomplete", snapshot.AcquisitionState);
        Assert.Equal("receipt_limit", snapshot.AcquisitionCapReason);
        Assert.Equal("incomplete_snapshot", snapshot.RecurringState);
        Assert.Empty(snapshot.RecurringGroups);
        Assert.Equal(20, owner.ReceiptPageCount);
        Assert.Contains(projector.Calls, call => call.Count == 2_000 && call.Incomplete);
    }

    private static AlertCenterQueryV2 Query() => new(
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
        new DateOnly(2026, 7, 22),
        new DateOnly(2026, 7, 23),
        "all",
        "all",
        "all",
        "all",
        null,
        100);

    private static IReadOnlyList<AlertVersionedReceiptQueryItem> ReceiptItems(int count)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var signals = Enumerable.Range(0, count).Select(index =>
        {
            var timestamp = observedAt.AddTicks(index);
            return new AlertSignal(
                $"recurring-signal-{index}",
                AlertSignalKind.SessionEvent,
                index,
                timestamp,
                null,
                AlertSignalStatus.Unknown,
                [],
                [],
                new AlertEvidenceReference(
                    AlertEvidenceKind.Span,
                    $"recurring-evidence-{index}",
                    "recurring-session",
                    "recurring-trace",
                    $"recurring-span-{index}",
                    null,
                    null,
                    null,
                    timestamp));
        }).ToArray();
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "fixture",
            "1",
            "recurring-session",
            "recurring-trace",
            AlertCompleteness.Partial,
            [],
            signals[0].ObservedAt,
            signals[^1].ObservedAt,
            [],
            signals);
        var evaluation = new AlertEvaluationEngine(
            new AlertRuleRegistry([new ManyMatchRule()]),
            new ExistingEvidenceResolver()).Evaluate(
                snapshot,
                new AlertEngineConfiguration(
                    AlertContractVersions.Configuration,
                    "recurring-fixture-v1",
                    []));
        Assert.Equal(count, evaluation.Receipts.Count);
        return evaluation.Receipts.Select(receipt =>
        {
            var bytes = AlertCanonicalJson.SerializeReceipt(receipt);
            return new AlertVersionedReceiptQueryItem(
                AlertContractKind.V1,
                bytes,
                AlertCenterReceiptConsumerV1.Validate(bytes),
                null);
        }).OrderBy(item => item.ReceiptV1!.AlertId, StringComparer.Ordinal).ToArray();
    }

    private static AlertCenterAlert Project(AlertCenterReceiptProjectionV1 receipt) => new(
        receipt.AlertId,
        "warning",
        "open",
        new("open", 0, null, ["acknowledge", "dismiss", "resolve"], []),
        new(
            receipt.RuleId,
            receipt.RuleVersion,
            "registered",
            "Recurring fixture",
            "Fixture projection",
            null,
            "trace",
            "trace",
            [],
            []),
        [new("count", "items", 1)],
        [],
        new(receipt.SourceSurface, receipt.SourceVersion, "supported_at_evaluation"),
        receipt.SessionId,
        receipt.TraceId,
        new("exact_agreement", "repo-safe", "workspace-safe", "repo-safe", "workspace-safe", "repo-safe", "workspace-safe"),
        new("partial", []),
        receipt.FirstObservedAt.ToString("O"),
        receipt.LastObservedAt.ToString("O"),
        receipt.Summary,
        [],
        0,
        new([], []),
        "fixture",
        receipt.EvaluationId);

    private sealed class ManyMatchRule : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = new(
            "recurring-fixture",
            "1",
            "Recurring fixture",
            "Produces one deterministic match per signal.",
            [],
            AlertRuleScope.Trace,
            [],
            "trace",
            [],
            ["missing_required_capability", "rule_disabled", "source_not_applicable"],
            ["fixture"]);

        public AlertRuleOutcome Evaluate(AlertRuleContext context) => new(
            context.Snapshot.Signals.Select(item => new AlertRuleMatch(
                AlertSeverity.Warning,
                [new AlertObservedValue("count", "items", 1)],
                [item.Evidence],
                item.ObservedAt,
                item.ObservedAt)).ToArray(),
            []);
    }

    private sealed class ExistingEvidenceResolver : IAlertEvidenceResolver
    {
        public bool Exists(AlertEvidenceReference reference) => true;
    }

    private sealed class PagedVersionedOwner(IReadOnlyList<AlertVersionedReceiptQueryItem> receipts)
        : IAlertEngineVersionedQueryStore
    {
        internal int ReceiptPageCount { get; private set; }

        public AlertVersionedReceiptQueryPage ListReceiptsVersioned(
            string? afterAlertId,
            int limit)
        {
            ReceiptPageCount++;
            var start = afterAlertId is null
                ? 0
                : receipts.Select((item, index) => (item, index))
                    .Single(pair => pair.item.ReceiptV1!.AlertId == afterAlertId).index + 1;
            var items = receipts.Skip(start).Take(limit).ToArray();
            var exhausted = start + items.Length == receipts.Count;
            return new(
                AlertEngineQueryStatus.Success,
                items,
                exhausted ? null : items[^1].ReceiptV1!.AlertId,
                exhausted,
                items.Sum(item => item.CanonicalBytes.Count));
        }

        public AlertVersionedEvaluationQueryPage ListEvaluationsVersioned(
            string? afterEvaluationId,
            int limit) =>
            new(AlertEngineQueryStatus.Success, [], Exhausted: true);

        public AlertVersionedSuppressionQueryPage ListSuppressionsVersioned(
            string evaluationId,
            long? afterSuppressionOrdinal,
            int limit) =>
            new(AlertEngineQueryStatus.Success, [], Exhausted: true);
    }

    private sealed class RecurringProjector
        : IAlertCenterReadModel, IAlertCenterOwnedReceiptProjectorV1
    {
        internal List<(int Count, bool Incomplete)> Calls { get; } = [];

        public AlertCenterOwnedProjectionResult ProjectOwned(
            IReadOnlyList<AlertCenterReceiptProjectionV1> receipts,
            AlertCenterQuery query,
            bool incomplete)
        {
            Calls.Add((receipts.Count, incomplete));
            var alerts = receipts.Select(Project).ToArray();
            var recurring = receipts.Count <= 1
                ? []
                : new[]
                {
                    new AlertCenterRecurringGroup(
                        incomplete ? "incomplete_snapshot" : "supported",
                        "recurring-fixture",
                        "1",
                        "repo-safe",
                        "workspace-safe",
                        "fixture",
                        "1",
                        "2026-07-22",
                        "2026-07-22",
                        "2026-07-23",
                        receipts.Count,
                        receipts.Count,
                        receipts.Min(item => item.FirstObservedAt).ToString("O"),
                        receipts.Max(item => item.LastObservedAt).ToString("O"),
                        new Dictionary<string, int> { ["partial"] = receipts.Count },
                        receipts.Select(item => item.AlertId).ToArray(),
                        receipts.Select(item => item.SessionId).ToArray(),
                        []),
                };
            return new(AlertCenterReadStatus.Success, alerts, alerts, recurring);
        }

        public AlertCenterReadResult Read(AlertCenterQuery query) =>
            new(AlertCenterReadStatus.Unavailable);
    }

    private sealed class UnusedLifecycleStore : IAlertLifecycleStore
    {
        public AlertLifecycleStoreResult Initialize() =>
            new(AlertLifecycleStoreStatus.Success);
        public AlertLifecycleStoreResult Get(string alertId) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) =>
            new(AlertLifecycleStoreStatus.Unavailable, []);
        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
    }
}

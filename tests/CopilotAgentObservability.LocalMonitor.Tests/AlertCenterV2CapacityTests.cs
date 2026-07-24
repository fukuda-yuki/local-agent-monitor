using System.Reflection;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection("AlertCenterV2CapacitySerial")]
public sealed class AlertCenterV2CapacityTests
{
    private const int MaximumResponseBytes = 16 * 1_024 * 1_024;
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Read_ReprojectsAfterFinalV1ProjectionCrossesRetainedByteLimit()
    {
        var receipts = ReceiptItems(5);
        var projector = new SizedProjector(
            singletonSummaryBytes: 1,
            bulkSummaryBytes: 14 * 1_024 * 1_024);
        var model = Model(new VersionedOwnerStore(receipts), projector);

        var result = model.Read(Query(limit: 5));

        Assert.Equal(AlertCenterReadStatusV2.Success, result.Status);
        var snapshot = Assert.IsType<AlertCenterSnapshotV2>(result.Snapshot);
        Assert.Equal("incomplete", snapshot.AcquisitionState);
        Assert.Equal("retained_bytes_limit", snapshot.AcquisitionCapReason);
        Assert.Equal(4, snapshot.AcquiredReceiptCount);
        Assert.Equal([1, 1, 1, 1, 1, 5, 4], projector.BatchSizes);
    }

    [Fact]
    public void AcquireReceipts_UsesRetainedBytesBeforeReceiptLimitBeforeOwnerMore()
    {
        var projections = ReceiptItems(2_001);
        var collisionItems = projections.Select(item => new AlertVersionedReceiptQueryItem(
            AlertContractKind.V1,
            new byte[33_560],
            item.ReceiptV1,
            null)).ToArray();
        var retainedBytes = Acquire(
            new VersionedOwnerStore(collisionItems, pageSize: 100, neverExhaust: true));

        Assert.Equal("retained_bytes_limit", retainedBytes.CapReason);
        Assert.True(retainedBytes.Incomplete);
        Assert.Equal(1_999, retainedBytes.ItemCount);

        var receiptLimit = Acquire(
            new VersionedOwnerStore(projections, pageSize: 100, neverExhaust: true));

        Assert.Equal("receipt_limit", receiptLimit.CapReason);
        Assert.True(receiptLimit.Incomplete);
        Assert.Equal(2_000, receiptLimit.ItemCount);

        var ownerMore = Acquire(
            new VersionedOwnerStore(projections.Take(20).ToArray(), pageSize: 1, neverExhaust: true));

        Assert.Equal("owner_more", ownerMore.CapReason);
        Assert.True(ownerMore.Incomplete);
        Assert.Equal(20, ownerMore.ItemCount);
    }

    [Fact]
    public void Read_ShortensPagesUnderResponseCapAndReplaysForwardAndBackCursors()
    {
        var receipts = ReceiptItems(4);
        var projector = new SizedProjector(
            singletonSummaryBytes: 9 * 1_024 * 1_024,
            bulkSummaryBytes: 9 * 1_024 * 1_024);
        var model = Model(new VersionedOwnerStore(receipts), projector);

        var first = Snapshot(model.Read(Query(limit: 4)));
        var second = Snapshot(model.Read(Query(limit: 4, cursor: AssertCursor(first.NextCursor))));
        var third = Snapshot(model.Read(Query(limit: 4, cursor: AssertCursor(second.NextCursor))));
        var replayedSecond = Snapshot(model.Read(
            Query(limit: 4, cursor: AssertCursor(third.PreviousCursor))));
        var replayedThird = Snapshot(model.Read(
            Query(limit: 4, cursor: AssertCursor(replayedSecond.NextCursor))));

        Assert.All(
            [first, second, third, replayedSecond, replayedThird],
            snapshot =>
            {
                Assert.Single(snapshot.Items);
                Assert.InRange(
                    JsonSerializer.SerializeToUtf8Bytes(snapshot).Length,
                    1,
                    MaximumResponseBytes);
            });
        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(second.SnapshotId, third.SnapshotId);
        Assert.Equal((1, 1), (first.VisibleStartOrdinal, first.VisibleEndOrdinal));
        Assert.Equal((2, 2), (second.VisibleStartOrdinal, second.VisibleEndOrdinal));
        Assert.Equal((3, 3), (third.VisibleStartOrdinal, third.VisibleEndOrdinal));
        Assert.Equal(
            (second.VisibleStartOrdinal, second.VisibleEndOrdinal, second.NextCursor),
            (replayedSecond.VisibleStartOrdinal, replayedSecond.VisibleEndOrdinal, replayedSecond.NextCursor));
        Assert.Equal(
            (third.VisibleStartOrdinal, third.VisibleEndOrdinal, third.PreviousCursor),
            (replayedThird.VisibleStartOrdinal, replayedThird.VisibleEndOrdinal, replayedThird.PreviousCursor));
    }

    private static SqliteAlertCenterReadModelV2 Model(
        IAlertEngineVersionedQueryStore owner,
        IAlertCenterReadModel v1) =>
        new(
            owner,
            new OpenLifecycleStore(),
            v1,
            new UnavailableCostAlertPresentationResolverV1());

    private static AcquisitionObservation Acquire(IAlertEngineVersionedQueryStore owner)
    {
        var model = Model(owner, new SizedProjector(1, 1));
        var method = typeof(SqliteAlertCenterReadModelV2).GetMethod(
            "AcquireReceipts",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var value = method.Invoke(model, null);
        Assert.NotNull(value);
        var type = value.GetType();
        return new(
            Assert.IsType<bool>(type.GetProperty("Incomplete")!.GetValue(value)),
            Assert.IsType<string>(type.GetProperty("CapReason")!.GetValue(value)),
            Assert.IsAssignableFrom<System.Collections.ICollection>(
                type.GetProperty("Items")!.GetValue(value)).Count);
    }

    private static AlertCenterSnapshotV2 Snapshot(AlertCenterReadResultV2 result)
    {
        Assert.Equal(AlertCenterReadStatusV2.Success, result.Status);
        return Assert.IsType<AlertCenterSnapshotV2>(result.Snapshot);
    }

    private static string AssertCursor(string? cursor) =>
        Assert.IsType<string>(cursor);

    private static AlertCenterQueryV2 Query(int limit, string? cursor = null) => new(
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
        cursor,
        limit);

    private static IReadOnlyList<AlertVersionedReceiptQueryItem> ReceiptItems(int count)
    {
        var signals = Enumerable.Range(0, count).Select(index =>
        {
            var timestamp = ObservedAt.AddTicks(index);
            return new AlertSignal(
                $"capacity-signal-{index}",
                AlertSignalKind.SessionEvent,
                index,
                timestamp,
                null,
                AlertSignalStatus.Unknown,
                [],
                [],
                new AlertEvidenceReference(
                    AlertEvidenceKind.Span,
                    $"capacity-evidence-{index}",
                    "capacity-session",
                    "capacity-trace",
                    $"capacity-span-{index}",
                    null,
                    null,
                    null,
                    timestamp));
        }).ToArray();
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "fixture",
            "1",
            "capacity-session",
            "capacity-trace",
            AlertCompleteness.Partial,
            [],
            signals[0].ObservedAt,
            signals[^1].ObservedAt,
            [],
            signals);
        var evaluation = new AlertEvaluationEngine(
                new AlertRuleRegistry([new ManyMatchRule()]),
                new ExistingEvidenceResolver())
            .Evaluate(
                snapshot,
                new AlertEngineConfiguration(
                    AlertContractVersions.Configuration,
                    "capacity-fixture-v1",
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
        }).OrderBy(
            item => item.ReceiptV1!.AlertId,
            StringComparer.Ordinal).ToArray();
    }

    private static AlertCenterAlert Alert(
        AlertCenterReceiptProjectionV1 receipt,
        string summary) =>
        new(
            receipt.AlertId,
            "warning",
            "open",
            new("open", 0, null, ["acknowledge", "dismiss", "resolve"], []),
            new(
                receipt.RuleId,
                receipt.RuleVersion,
                "registered",
                "Capacity fixture",
                "Capacity fixture",
                null,
                null,
                "trace",
                [],
                []),
            [],
            [],
            new(receipt.SourceSurface, receipt.SourceVersion, "supported_at_evaluation"),
            receipt.SessionId,
            receipt.TraceId,
            new("available", "repository", "workspace", "repository", "workspace", "repository", "workspace"),
            new("partial", []),
            receipt.FirstObservedAt.ToUniversalTime().ToString("O"),
            receipt.LastObservedAt.ToUniversalTime().ToString("O"),
            summary,
            [],
            0,
            new([], []),
            "partial",
            receipt.EvaluationId);

    private sealed record AcquisitionObservation(
        bool Incomplete,
        string CapReason,
        int ItemCount);

    private sealed class SizedProjector(int singletonSummaryBytes, int bulkSummaryBytes)
        : IAlertCenterReadModel, IAlertCenterOwnedReceiptProjectorV1
    {
        private readonly string singletonSummary = new('s', singletonSummaryBytes);
        private readonly string bulkSummary = new('b', bulkSummaryBytes);

        internal List<int> BatchSizes { get; } = [];

        public AlertCenterReadResult Read(AlertCenterQuery query) =>
            new(AlertCenterReadStatus.Unavailable);

        public AlertCenterOwnedProjectionResult ProjectOwned(
            IReadOnlyList<AlertCenterReceiptProjectionV1> receipts,
            AlertCenterQuery query,
            bool incomplete)
        {
            BatchSizes.Add(receipts.Count);
            var summary = receipts.Count == 1 ? singletonSummary : bulkSummary;
            var alerts = receipts.Select(item => Alert(item, summary)).ToArray();
            return new(AlertCenterReadStatus.Success, alerts, alerts, []);
        }
    }

    private sealed class VersionedOwnerStore(
        IReadOnlyList<AlertVersionedReceiptQueryItem> receipts,
        int pageSize = 100,
        bool neverExhaust = false)
        : IAlertEngineVersionedQueryStore
    {
        public AlertVersionedReceiptQueryPage ListReceiptsVersioned(
            string? afterAlertId,
            int limit)
        {
            var start = afterAlertId is null
                ? 0
                : receipts.Select((item, index) => (item, index))
                    .Single(pair => pair.item.ReceiptV1!.AlertId == afterAlertId).index + 1;
            var items = receipts.Skip(start).Take(Math.Min(limit, pageSize)).ToArray();
            var exhausted = !neverExhaust && start + items.Length == receipts.Count;
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

    private sealed class OpenLifecycleStore : IAlertLifecycleStore
    {
        public AlertLifecycleStoreResult Initialize() =>
            new(AlertLifecycleStoreStatus.Success);
        public AlertLifecycleStoreResult Get(string alertId) =>
            new(AlertLifecycleStoreStatus.NotFound);
        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) =>
            new(AlertLifecycleStoreStatus.NotFound, []);
        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) =>
            new(AlertLifecycleStoreStatus.Unavailable);
    }

    private sealed class ManyMatchRule : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = new(
            "capacity-fixture",
            "1",
            "Capacity fixture",
            "Produces deterministic capacity receipts.",
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
}

[CollectionDefinition("AlertCenterV2CapacitySerial", DisableParallelization = true)]
public sealed class AlertCenterV2CapacitySerialCollection;

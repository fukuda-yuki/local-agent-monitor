using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Alerts;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterReadModelTests
{
    [Fact]
    public void Read_BoundsTypedReceiptPagesAndMarksRecurringResultsIncomplete()
    {
        using var temp = new MonitorTempDirectory
        {
            TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
        };
        var ownerStore = new OwnerQueryStore(ReceiptItems(2_001));
        var lifecycleStore = new OpenLifecycleStore();
        var readModel = ReadModel(temp, ownerStore, ownerStore, lifecycleStore);

        var result = readModel.Read(Query());

        Assert.Equal(AlertCenterReadStatus.Success, result.Status);
        var snapshot = Assert.IsType<AlertCenterSnapshot>(result.Snapshot);
        Assert.Equal("incomplete", snapshot.SnapshotState);
        Assert.Null(snapshot.OmittedReceiptCount);
        Assert.Equal(2_000, snapshot.TotalCount);
        Assert.Equal(100, snapshot.Alerts.Count);
        Assert.NotEmpty(snapshot.RecurringGroups);
        Assert.All(snapshot.RecurringGroups, group => Assert.Equal("incomplete_snapshot", group.AggregationState));
        Assert.Equal(20, ownerStore.ReceiptQueryCount);
        Assert.Equal(1, ownerStore.EvaluationQueryCount);
        Assert.Equal(2_000, lifecycleStore.GetCount);
    }

    [Fact]
    public void Read_FailsClosedForStalledOwnerCursorAndOversizedOwnerPage()
    {
        using var temp = new MonitorTempDirectory
        {
            TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
        };
        var valid = Assert.Single(ReceiptItems(1));
        var lifecycleStore = new OpenLifecycleStore();
        var stalledStore = new OwnerQueryStore([valid])
        {
            ForcedNextCursor = new string('f', 64),
        };

        var stalled = ReadModel(temp, stalledStore, stalledStore, lifecycleStore).Read(Query());

        Assert.Equal(AlertCenterReadStatus.Unavailable, stalled.Status);
        Assert.Null(stalled.Snapshot);
        Assert.Equal(0, lifecycleStore.GetCount);

        var oversizedItem = new AlertReceiptQueryItem(
            new byte[AlertEngineQueryLimits.MaximumPageBytes + 1],
            valid.Receipt);
        var oversizedStore = new OwnerQueryStore([oversizedItem]);

        var oversized = ReadModel(temp, oversizedStore, oversizedStore, lifecycleStore).Read(Query());

        Assert.Equal(AlertCenterReadStatus.Unavailable, oversized.Status);
        Assert.Null(oversized.Snapshot);
        Assert.Equal(0, lifecycleStore.GetCount);
    }

    [Fact]
    public void Read_PagesSuppressionsInOrdinalOrderAndCapsCoverageAtOneHundredFacts()
    {
        using var temp = new MonitorTempDirectory
        {
            TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
        };
        var ownerStore = new CoverageQueryStore(101, maximumItemsPerPage: 30);
        var lifecycleStore = new OpenLifecycleStore();

        var result = ReadModel(temp, ownerStore, ownerStore, lifecycleStore).Read(Query());

        Assert.Equal(AlertCenterReadStatus.Success, result.Status);
        var snapshot = Assert.IsType<AlertCenterSnapshot>(result.Snapshot);
        Assert.Empty(snapshot.Alerts);
        Assert.Equal(100, snapshot.Coverage.Count);
        Assert.Equal("incomplete", snapshot.CoverageState);
        Assert.Null(snapshot.OmittedCoverageFactCount);
        Assert.Equal("coverage-rule-000", snapshot.Coverage[0].RuleId);
        Assert.Equal("coverage-rule-099", snapshot.Coverage[^1].RuleId);
        Assert.All(snapshot.Coverage, item => Assert.Equal("unknown", item.ContextState));
        Assert.Equal(4, ownerStore.SuppressionQueryCount);
        Assert.Equal(0, lifecycleStore.GetCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Read_BoundsZeroSuppressionEvaluationHistoryAndReportsUnknownOmission(int maximumItemsPerPage)
    {
        using var temp = new MonitorTempDirectory
        {
            TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
        };
        var ownerStore = new ZeroSuppressionCoverageQueryStore(2_001, maximumItemsPerPage);

        var result = ReadModel(temp, ownerStore, ownerStore, new OpenLifecycleStore()).Read(Query());

        Assert.Equal(AlertCenterReadStatus.Success, result.Status);
        var snapshot = Assert.IsType<AlertCenterSnapshot>(result.Snapshot);
        Assert.Empty(snapshot.Coverage);
        Assert.Equal("incomplete", snapshot.CoverageState);
        Assert.Null(snapshot.OmittedCoverageFactCount);
        Assert.Equal(20, ownerStore.EvaluationQueryCount);
    }

    [Fact]
    public void Read_FailsClosedWhenLifecycleHistoryIsNotTheBoundedContiguousChain()
    {
        using var temp = new MonitorTempDirectory
        {
            TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)),
        };
        var ownerStore = new OwnerQueryStore(ReceiptItems(1));

        var result = ReadModel(temp, ownerStore, ownerStore, new TruncatedLifecycleStore()).Read(Query());

        Assert.Equal(AlertCenterReadStatus.Unavailable, result.Status);
        Assert.Null(result.Snapshot);
    }

    private static SqliteAlertCenterReadModel ReadModel(
        MonitorTempDirectory temp,
        IAlertEngineStore engineStore,
        IAlertEngineQueryStore queryStore,
        IAlertLifecycleStore lifecycleStore)
    {
        var projectionStore = new EmptyProjectionStore();
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        var resolver = new AlertCenterEvidenceResolver(
            sessionStore,
            projectionStore,
            new SqliteSourceCompatibilityStore(temp.DatabasePath));
        return new(
            engineStore,
            queryStore,
            lifecycleStore,
            projectionStore,
            sessionStore,
            resolver,
            temp.TimeProvider);
    }

    private static AlertCenterQuery Query() => new(
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
        0,
        100);

    private static IReadOnlyList<AlertReceiptQueryItem> ReceiptItems(int count)
    {
        var observedAt = new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero);
        var signals = Enumerable.Range(0, count).Select(index =>
        {
            var timestamp = observedAt.AddTicks(index);
            return new AlertSignal(
                $"bounded-signal-{index}",
                AlertSignalKind.SessionEvent,
                index,
                timestamp,
                null,
                AlertSignalStatus.Unknown,
                [],
                [],
                new AlertEvidenceReference(
                    AlertEvidenceKind.Span,
                    $"bounded-evidence-{index}",
                    "bounded-session",
                    "bounded-trace",
                    $"bounded-span-{index}",
                    null,
                    null,
                    null,
                    timestamp));
        }).ToArray();
        var snapshot = new AlertNormalizedSnapshot(
            AlertContractVersions.Snapshot,
            "fixture",
            "1",
            "bounded-session",
            "bounded-trace",
            AlertCompleteness.Partial,
            [],
            signals[0].ObservedAt,
            signals[^1].ObservedAt,
            [],
            signals);
        var registry = new AlertRuleRegistry([new ManyMatchRule()]);
        var configuration = new AlertEngineConfiguration(
            AlertContractVersions.Configuration,
            "bounded-fixture-v1",
            []);
        var evaluation = new AlertEvaluationEngine(registry, new ExistingEvidenceResolver())
            .Evaluate(snapshot, configuration);
        Assert.Equal(count, evaluation.Receipts.Count);
        return evaluation.Receipts.Select(receipt =>
        {
            var bytes = AlertCanonicalJson.SerializeReceipt(receipt);
            return new AlertReceiptQueryItem(bytes, AlertCenterReceiptConsumerV1.Validate(bytes));
        }).OrderBy(item => item.Receipt.AlertId, StringComparer.Ordinal).ToArray();
    }

    private sealed class ManyMatchRule : IAlertRule
    {
        public AlertRuleDescriptor Descriptor { get; } = new(
            "bounded-fixture",
            "1",
            "Bounded fixture",
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

    private sealed class OwnerQueryStore(IReadOnlyList<AlertReceiptQueryItem> receipts)
        : IAlertEngineStore, IAlertEngineQueryStore
    {
        internal int ReceiptQueryCount { get; private set; }
        internal int EvaluationQueryCount { get; private set; }
        internal string? ForcedNextCursor { get; init; }

        public AlertStoreResult Initialize() => new(AlertStoreStatus.Success);

        public AlertReceiptQueryPage ListReceipts(string? afterAlertId, int limit)
        {
            ReceiptQueryCount++;
            var start = afterAlertId is null
                ? 0
                : receipts.Select((item, index) => (item, index))
                    .Single(pair => pair.item.Receipt.AlertId == afterAlertId).index + 1;
            var items = receipts.Skip(start).Take(limit).ToArray();
            var next = ForcedNextCursor
                ?? (start + items.Length < receipts.Count ? items[^1].Receipt.AlertId : null);
            return new(AlertEngineQueryStatus.Success, items, next);
        }

        public AlertEvaluationQueryPage ListEvaluations(string? afterEvaluationId, int limit)
        {
            EvaluationQueryCount++;
            return new(AlertEngineQueryStatus.Success, []);
        }

        public AlertSuppressionQueryPage ListSuppressions(string evaluationId, long? afterSuppressionOrdinal, int limit) =>
            new(AlertEngineQueryStatus.Success, []);

        public AlertStoreResult Append(AlertEvaluationResult evaluation) => throw new NotSupportedException();
        public AlertStoreReadResult GetEvaluation(string evaluationId) => throw new NotSupportedException();
        public AlertStoreReadResult GetReceipt(string alertId) => throw new NotSupportedException();
        public AlertStoreListResult ListSuppressions(string evaluationId) => throw new NotSupportedException();
    }

    private sealed class CoverageQueryStore : IAlertEngineStore, IAlertEngineQueryStore
    {
        private readonly string evaluationId = new('a', 64);
        private readonly int maximumItemsPerPage;
        private readonly IReadOnlyList<AlertSuppressionQueryItem> suppressions;

        internal CoverageQueryStore(int suppressionCount, int maximumItemsPerPage)
        {
            this.maximumItemsPerPage = maximumItemsPerPage;
            suppressions = Enumerable.Range(0, suppressionCount).Select(index =>
            {
                var suppression = new AlertSuppression(
                    evaluationId,
                    $"coverage-rule-{index:D3}",
                    "1",
                    "missing_required_capability",
                    [$"coverage-capability-{index:D3}"]);
                var bytes = AlertCanonicalJson.SerializeSuppression(suppression);
                return new AlertSuppressionQueryItem(
                    index,
                    bytes,
                    AlertSuppressionConsumerV1.Validate(bytes));
            }).ToArray();
        }

        internal int SuppressionQueryCount { get; private set; }

        public AlertStoreResult Initialize() => new(AlertStoreStatus.Success);

        public AlertReceiptQueryPage ListReceipts(string? afterAlertId, int limit) =>
            new(AlertEngineQueryStatus.Success, []);

        public AlertEvaluationQueryPage ListEvaluations(string? afterEvaluationId, int limit) =>
            afterEvaluationId is null
                ? new(
                    AlertEngineQueryStatus.Success,
                    [new AlertEvaluationProjectionV1(
                        evaluationId,
                        new string('b', 64),
                        "coverage-fixture-v1",
                        new string('c', 64),
                        0,
                        suppressions.Count)])
                : new(AlertEngineQueryStatus.Success, []);

        public AlertSuppressionQueryPage ListSuppressions(
            string requestedEvaluationId,
            long? afterSuppressionOrdinal,
            int limit)
        {
            SuppressionQueryCount++;
            Assert.Equal(evaluationId, requestedEvaluationId);
            var start = checked((int)(afterSuppressionOrdinal ?? -1) + 1);
            var items = suppressions.Skip(start).Take(Math.Min(limit, maximumItemsPerPage)).ToArray();
            long? next = start + items.Length < suppressions.Count ? items[^1].SuppressionOrdinal : null;
            return new(AlertEngineQueryStatus.Success, items, next);
        }

        public AlertStoreResult Append(AlertEvaluationResult evaluation) => throw new NotSupportedException();
        public AlertStoreReadResult GetEvaluation(string requestedEvaluationId) => throw new NotSupportedException();
        public AlertStoreReadResult GetReceipt(string alertId) => throw new NotSupportedException();
        public AlertStoreListResult ListSuppressions(string requestedEvaluationId) => throw new NotSupportedException();
    }

    private sealed class ZeroSuppressionCoverageQueryStore(int evaluationCount, int maximumItemsPerPage)
        : IAlertEngineStore, IAlertEngineQueryStore
    {
        private readonly IReadOnlyList<AlertEvaluationProjectionV1> evaluations = Enumerable.Range(0, evaluationCount)
            .Select(index => new AlertEvaluationProjectionV1(
                index.ToString("x64"),
                new string('b', 64),
                "zero-suppression-fixture-v1",
                new string('c', 64),
                0,
                0))
            .ToArray();

        internal int EvaluationQueryCount { get; private set; }

        public AlertStoreResult Initialize() => new(AlertStoreStatus.Success);

        public AlertReceiptQueryPage ListReceipts(string? afterAlertId, int limit) =>
            new(AlertEngineQueryStatus.Success, []);

        public AlertEvaluationQueryPage ListEvaluations(string? afterEvaluationId, int limit)
        {
            EvaluationQueryCount++;
            var start = afterEvaluationId is null
                ? 0
                : evaluations.Select((item, index) => (item, index))
                    .Single(pair => pair.item.EvaluationId == afterEvaluationId).index + 1;
            var items = evaluations.Skip(start).Take(Math.Min(limit, maximumItemsPerPage)).ToArray();
            var next = start + items.Length < evaluations.Count ? items[^1].EvaluationId : null;
            return new(AlertEngineQueryStatus.Success, items, next);
        }

        public AlertSuppressionQueryPage ListSuppressions(string evaluationId, long? afterSuppressionOrdinal, int limit) =>
            throw new InvalidOperationException("Zero-suppression evaluations must not query suppression rows.");

        public AlertStoreResult Append(AlertEvaluationResult evaluation) => throw new NotSupportedException();
        public AlertStoreReadResult GetEvaluation(string evaluationId) => throw new NotSupportedException();
        public AlertStoreReadResult GetReceipt(string alertId) => throw new NotSupportedException();
        public AlertStoreListResult ListSuppressions(string evaluationId) => throw new NotSupportedException();
    }

    private sealed class OpenLifecycleStore : IAlertLifecycleStore
    {
        internal int GetCount { get; private set; }

        public AlertLifecycleStoreResult Initialize() => new(AlertLifecycleStoreStatus.Success);

        public AlertLifecycleStoreResult Get(string alertId)
        {
            GetCount++;
            return new(
                AlertLifecycleStoreStatus.Success,
                Lifecycle: new AlertLifecycleView(
                    AlertLifecycleContractVersions.Lifecycle,
                    alertId,
                    AlertLifecycleState.Open,
                    0,
                    null));
        }

        public AlertLifecycleHistoryResult History(string alertId, int limit = 50) =>
            new(AlertLifecycleStoreStatus.Success, []);

        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) => throw new NotSupportedException();
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) => throw new NotSupportedException();
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) => throw new NotSupportedException();
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) => throw new NotSupportedException();
    }

    private sealed class TruncatedLifecycleStore : IAlertLifecycleStore
    {
        private string? alertId;
        private static readonly DateTimeOffset OccurredAt = new(2026, 7, 23, 11, 0, 0, TimeSpan.Zero);

        public AlertLifecycleStoreResult Initialize() => new(AlertLifecycleStoreStatus.Success);

        public AlertLifecycleStoreResult Get(string requestedAlertId)
        {
            alertId = requestedAlertId;
            return new(
                AlertLifecycleStoreStatus.Success,
                Lifecycle: new(
                    AlertLifecycleContractVersions.Lifecycle,
                    requestedAlertId,
                    AlertLifecycleState.Dismissed,
                    2,
                    OccurredAt));
        }

        public AlertLifecycleHistoryResult History(string requestedAlertId, int limit = 50) => new(
            AlertLifecycleStoreStatus.Success,
            [new(
                AlertLifecycleContractVersions.Lifecycle,
                new string('e', 64),
                alertId ?? requestedAlertId,
                2,
                1,
                AlertLifecycleAction.Dismiss,
                AlertLifecycleState.Acknowledged,
                AlertLifecycleState.Dismissed,
                OccurredAt,
                "local_user",
                "user_reviewed",
                "bounded-private-comment",
                "aid1_" + new string('a', 43),
                null,
                null,
                "alert_lifecycle_updated")]);

        public AlertLifecycleStoreResult Mutate(AlertLifecycleMutation mutation) => throw new NotSupportedException();
        public AlertLifecycleStoreResult ResolveFromReevaluation(AlertLifecycleMutation mutation) => throw new NotSupportedException();
        public AlertLifecycleStoreResult Supersede(AlertLifecycleMutation mutation) => throw new NotSupportedException();
        public AlertLifecycleStoreResult SourceDeleted(AlertLifecycleMutation mutation) => throw new NotSupportedException();
    }

    private sealed class EmptyProjectionStore : ProjectionStoreTestDouble
    {
    }
}

using System.Globalization;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal sealed class SqliteAlertCenterReadModel : IAlertCenterReadModel
{
    private const int MaximumReceiptSnapshot = 2_000;
    private const int MaximumCoverageEvaluationPages = 20;
    private const int MaximumCoverageEvaluations = 2_000;
    private const int MaximumCoverageFacts = 100;
    private readonly IAlertEngineStore engineStore;
    private readonly IAlertEngineQueryStore queryStore;
    private readonly IAlertLifecycleStore lifecycleStore;
    private readonly IMonitorProjectionStore projectionStore;
    private readonly ISessionStore sessionStore;
    private readonly AlertCenterEvidenceResolver evidenceResolver;
    private readonly TimeProvider timeProvider;
    private readonly IReadOnlyDictionary<(string RuleId, string RuleVersion), AlertRuleDescriptor> descriptors;

    internal SqliteAlertCenterReadModel(
        IAlertEngineStore engineStore,
        IAlertEngineQueryStore queryStore,
        IAlertLifecycleStore lifecycleStore,
        IMonitorProjectionStore projectionStore,
        ISessionStore sessionStore,
        AlertCenterEvidenceResolver evidenceResolver,
        TimeProvider timeProvider)
    {
        this.engineStore = engineStore;
        this.queryStore = queryStore;
        this.lifecycleStore = lifecycleStore;
        this.projectionStore = projectionStore;
        this.sessionStore = sessionStore;
        this.evidenceResolver = evidenceResolver;
        this.timeProvider = timeProvider;
        var registry = new AlertRuleRegistry([
            .. ToolAlertRulePack.CreateRules(),
            .. TokenContextCacheAlertRulePack.CreateRules(),
        ]);
        descriptors = registry.Rules.ToDictionary(
            rule => (rule.Descriptor.RuleId, rule.Descriptor.RuleVersion),
            rule => rule.Descriptor);
    }

    public AlertCenterReadResult Read(AlertCenterQuery query)
    {
        try
        {
            var engineInitialization = engineStore.Initialize();
            if (engineInitialization.Status != AlertStoreStatus.Success)
            {
                return Failure(engineInitialization.Status == AlertStoreStatus.Busy
                    ? AlertCenterReadStatus.Busy
                    : AlertCenterReadStatus.Unavailable);
            }

            var lifecycleInitialization = lifecycleStore.Initialize();
            if (lifecycleInitialization.Status != AlertLifecycleStoreStatus.Success)
            {
                return Failure(lifecycleInitialization.Status == AlertLifecycleStoreStatus.Busy
                    ? AlertCenterReadStatus.Busy
                    : AlertCenterReadStatus.Unavailable);
            }

            var acquired = AcquireReceipts();
            if (acquired.Status != AlertCenterReadStatus.Success)
            {
                return Failure(acquired.Status);
            }

            var lifecycles = new Dictionary<string, AlertLifecycleView>(StringComparer.Ordinal);
            var histories = new Dictionary<string, IReadOnlyList<AlertLifecycleEvent>>(StringComparer.Ordinal);
            foreach (var receipt in acquired.Receipts)
            {
                var lifecycle = lifecycleStore.Get(receipt.AlertId);
                if (lifecycle.Status != AlertLifecycleStoreStatus.Success
                    || lifecycle.Lifecycle is null
                    || lifecycle.Lifecycle.AlertId != receipt.AlertId)
                {
                    return Failure(lifecycle.Status == AlertLifecycleStoreStatus.Success
                        ? AlertCenterReadStatus.Unavailable
                        : Map(lifecycle.Status));
                }

                var history = lifecycleStore.History(receipt.AlertId, 100);
                if (history.Status != AlertLifecycleStoreStatus.Success
                    || !ValidHistory(lifecycle.Lifecycle, history.Events))
                {
                    return Failure(history.Status == AlertLifecycleStoreStatus.Success
                        ? AlertCenterReadStatus.Unavailable
                        : Map(history.Status));
                }

                lifecycles.Add(receipt.AlertId, lifecycle.Lifecycle);
                histories.Add(receipt.AlertId, history.Events);
            }

            var relationships = Relationships(histories.Values);
            var traceCache = new Dictionary<string, MonitorTraceRow?>(StringComparer.Ordinal);
            var sessionCache = new Dictionary<string, SessionDetail?>(StringComparer.Ordinal);
            var allAlerts = acquired.Receipts.Select(receipt => Project(
                receipt,
                lifecycles[receipt.AlertId],
                histories[receipt.AlertId],
                relationships,
                traceCache,
                sessionCache)).ToArray();
            var filtered = allAlerts
                .Where(item => Matches(item, query))
                .OrderBy(item => SeverityRank(item.Severity))
                .ThenByDescending(item => item.LastObservedAt, StringComparer.Ordinal)
                .ThenBy(item => item.AlertId, StringComparer.Ordinal)
                .ToArray();

            var coverage = AcquireCoverage(acquired.Receipts);
            if (coverage.Status != AlertCenterReadStatus.Success)
            {
                return Failure(coverage.Status);
            }

            var snapshot = new AlertCenterSnapshot(
                AlertCenterContractVersions.Center,
                Timestamp(timeProvider.GetUtcNow()),
                QueryDto(query),
                acquired.Incomplete ? "incomplete" : "complete",
                acquired.Incomplete ? null : 0,
                coverage.Incomplete ? "incomplete" : "complete",
                coverage.Incomplete ? null : 0,
                filtered.LongLength,
                filtered.Skip(query.Offset).Take(query.Limit).ToArray(),
                Aggregate(filtered, query, acquired.Incomplete),
                coverage.Facts);
            return new(AlertCenterReadStatus.Success, snapshot);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return Failure(AlertCenterReadStatus.Busy);
        }
        catch (PersistenceBusyException)
        {
            return Failure(AlertCenterReadStatus.Busy);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return Failure(AlertCenterReadStatus.Unavailable);
        }
    }

    private ReceiptAcquisition AcquireReceipts()
    {
        var receipts = new List<AlertCenterReceiptProjectionV1>(MaximumReceiptSnapshot);
        string? cursor = null;
        while (true)
        {
            var limit = Math.Min(AlertEngineQueryLimits.MaximumPageSize, MaximumReceiptSnapshot - receipts.Count);
            var page = queryStore.ListReceipts(cursor, limit);
            if (page.Status != AlertEngineQueryStatus.Success)
            {
                return new(Map(page.Status), [], false);
            }
            if (page.Items.Count > limit)
            {
                return new(AlertCenterReadStatus.Unavailable, [], false);
            }

            var pageBytes = 0;
            var last = cursor;
            foreach (var item in page.Items)
            {
                pageBytes = checked(pageBytes + item.CanonicalBytes.Count);
                if (pageBytes > AlertEngineQueryLimits.MaximumPageBytes)
                {
                    return new(AlertCenterReadStatus.Unavailable, [], false);
                }

                var receipt = item.Receipt;
                if (last is not null && string.CompareOrdinal(receipt.AlertId, last) <= 0)
                {
                    return new(AlertCenterReadStatus.Unavailable, [], false);
                }
                receipts.Add(receipt);
                last = receipt.AlertId;
            }

            if (page.NextCursor is null)
            {
                return new(AlertCenterReadStatus.Success, receipts, false);
            }
            if (page.Items.Count == 0 || !string.Equals(page.NextCursor, last, StringComparison.Ordinal))
            {
                return new(AlertCenterReadStatus.Unavailable, [], false);
            }
            if (receipts.Count == MaximumReceiptSnapshot)
            {
                return new(AlertCenterReadStatus.Success, receipts, true);
            }
            cursor = page.NextCursor;
        }
    }

    private CoverageAcquisition AcquireCoverage(IReadOnlyList<AlertCenterReceiptProjectionV1> receipts)
    {
        var receiptContext = receipts.GroupBy(item => item.EvaluationId, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.ToArray(), StringComparer.Ordinal);
        var facts = new List<AlertCenterCoverageFact>(MaximumCoverageFacts);
        var evaluationCount = 0;
        var pageCount = 0;
        string? cursor = null;
        while (facts.Count < MaximumCoverageFacts
            && evaluationCount < MaximumCoverageEvaluations
            && pageCount < MaximumCoverageEvaluationPages)
        {
            var limit = Math.Min(
                AlertEngineQueryLimits.MaximumPageSize,
                MaximumCoverageEvaluations - evaluationCount);
            var page = queryStore.ListEvaluations(cursor, limit);
            pageCount++;
            if (page.Status != AlertEngineQueryStatus.Success)
            {
                return new(Map(page.Status), [], false);
            }
            if (page.Items.Count > limit)
            {
                return new(AlertCenterReadStatus.Unavailable, [], false);
            }

            var last = cursor;
            foreach (var evaluation in page.Items)
            {
                if (!CanonicalHash(evaluation.EvaluationId)
                    || evaluation.ReceiptCount < 0
                    || evaluation.SuppressionCount < 0
                    || last is not null && string.CompareOrdinal(evaluation.EvaluationId, last) <= 0)
                {
                    return new(AlertCenterReadStatus.Unavailable, [], false);
                }
                last = evaluation.EvaluationId;
                evaluationCount++;

                var suppressions = AcquireSuppressions(evaluation, receiptContext.GetValueOrDefault(evaluation.EvaluationId), facts);
                if (suppressions != AlertCenterReadStatus.Success)
                {
                    return new(suppressions, [], false);
                }
                if (facts.Count == MaximumCoverageFacts)
                {
                    return new(AlertCenterReadStatus.Success, facts, true);
                }
            }

            if (page.NextCursor is null)
            {
                return new(AlertCenterReadStatus.Success, facts, false);
            }
            if (page.Items.Count == 0 || !string.Equals(page.NextCursor, last, StringComparison.Ordinal))
            {
                return new(AlertCenterReadStatus.Unavailable, [], false);
            }
            if (evaluationCount == MaximumCoverageEvaluations
                || pageCount == MaximumCoverageEvaluationPages)
            {
                return new(AlertCenterReadStatus.Success, facts, true);
            }
            cursor = page.NextCursor;
        }
        return new(AlertCenterReadStatus.Success, facts, true);
    }

    private AlertCenterReadStatus AcquireSuppressions(
        AlertEvaluationProjectionV1 evaluation,
        IReadOnlyList<AlertCenterReceiptProjectionV1>? context,
        List<AlertCenterCoverageFact> facts)
    {
        if (evaluation.SuppressionCount == 0)
        {
            return AlertCenterReadStatus.Success;
        }

        var exactContext = ExactCoverageContext(evaluation, context);
        long seen = 0;
        long? cursor = null;
        while (seen < evaluation.SuppressionCount && facts.Count < MaximumCoverageFacts)
        {
            var page = queryStore.ListSuppressions(
                evaluation.EvaluationId,
                cursor,
                AlertEngineQueryLimits.MaximumPageSize);
            if (page.Status != AlertEngineQueryStatus.Success)
            {
                return Map(page.Status);
            }
            if (page.Items.Count > AlertEngineQueryLimits.MaximumPageSize)
            {
                return AlertCenterReadStatus.Unavailable;
            }

            var pageBytes = 0;
            var last = cursor;
            foreach (var item in page.Items)
            {
                pageBytes = checked(pageBytes + item.CanonicalBytes.Count);
                if (pageBytes > AlertEngineQueryLimits.MaximumPageBytes
                    || last is not null && item.SuppressionOrdinal <= last.Value)
                {
                    return AlertCenterReadStatus.Unavailable;
                }

                var suppression = item.Suppression;
                if (suppression.EvaluationId != evaluation.EvaluationId)
                {
                    return AlertCenterReadStatus.Unavailable;
                }

                seen++;
                if (seen > evaluation.SuppressionCount)
                {
                    return AlertCenterReadStatus.Unavailable;
                }
                facts.Add(new AlertCenterCoverageFact(
                    suppression.EvaluationId,
                    suppression.RuleId,
                    suppression.RuleVersion,
                    suppression.Code,
                    suppression.MissingCapabilities.ToArray(),
                    exactContext is null ? "unknown" : "exact_evaluation",
                    exactContext?.SourceSurface,
                    exactContext?.SourceVersion,
                    exactContext?.SessionId,
                    exactContext?.TraceId,
                    exactContext?.ObservationDate));
                last = item.SuppressionOrdinal;
                if (facts.Count == MaximumCoverageFacts)
                {
                    return AlertCenterReadStatus.Success;
                }
            }

            if (page.NextCursor is null)
            {
                return seen == evaluation.SuppressionCount
                    ? AlertCenterReadStatus.Success
                    : AlertCenterReadStatus.Unavailable;
            }
            if (page.Items.Count == 0 || page.NextCursor != last)
            {
                return AlertCenterReadStatus.Unavailable;
            }
            cursor = page.NextCursor;
        }
        return seen == evaluation.SuppressionCount || facts.Count == MaximumCoverageFacts
            ? AlertCenterReadStatus.Success
            : AlertCenterReadStatus.Unavailable;
    }

    private static CoverageContext? ExactCoverageContext(
        AlertEvaluationProjectionV1 evaluation,
        IReadOnlyList<AlertCenterReceiptProjectionV1>? receipts)
    {
        if (receipts is null || receipts.Count == 0 || receipts.Count != evaluation.ReceiptCount)
        {
            return null;
        }

        var contexts = receipts.Select(item => new CoverageContext(
            item.SourceSurface,
            item.SourceVersion,
            item.SessionId,
            item.TraceId,
            item.LastObservedAt.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)))
            .Distinct()
            .ToArray();
        return contexts.Length == 1 ? contexts[0] : null;
    }

    private AlertCenterAlert Project(
        AlertCenterReceiptProjectionV1 receipt,
        AlertLifecycleView lifecycle,
        IReadOnlyList<AlertLifecycleEvent> history,
        RelationshipIndex relationships,
        Dictionary<string, MonitorTraceRow?> traceCache,
        Dictionary<string, SessionDetail?> sessionCache)
    {
        var trace = receipt.TraceId is null ? null : Trace(receipt.TraceId, traceCache);
        var session = Session(receipt.SessionId, sessionCache);
        var scope = Scope(receipt.TraceId, trace, session);
        var evidence = receipt.Evidence.Select(reference => Evidence(
            reference,
            receipt.SourceSurface,
            receipt.SourceVersion)).ToArray();
        descriptors.TryGetValue((receipt.RuleId, receipt.RuleVersion), out var descriptor);
        return new AlertCenterAlert(
            receipt.AlertId,
            Severity(receipt.Severity),
            InitialState(receipt.InitialState),
            new(
                LifecycleState(lifecycle.State),
                lifecycle.Revision,
                lifecycle.LastOccurredAt is null ? null : Timestamp(lifecycle.LastOccurredAt.Value),
                AllowedActions(lifecycle.State),
                history.Select(History).ToArray()),
            Rule(receipt, descriptor),
            receipt.ObservedValues.Select(Value).ToArray(),
            receipt.EffectiveThresholds.Select(Value).ToArray(),
            new(receipt.SourceSurface, receipt.SourceVersion, "supported_at_evaluation"),
            receipt.SessionId,
            receipt.TraceId,
            scope,
            new(Completeness(receipt.Completeness), receipt.CompletenessReasons.ToArray()),
            Timestamp(receipt.FirstObservedAt),
            Timestamp(receipt.LastObservedAt),
            receipt.Summary,
            evidence,
            evidence.Length,
            new(
                relationships.Predecessors.GetValueOrDefault(receipt.AlertId) ?? [],
                relationships.Successors.GetValueOrDefault(receipt.AlertId) ?? []),
            receipt.Completeness == AlertCompleteness.Full
                ? "complete_receipt"
                : $"{Completeness(receipt.Completeness)}:{string.Join(',', receipt.CompletenessReasons)}",
            receipt.EvaluationId);
    }

    private AlertCenterEvidence Evidence(
        AlertEvidenceReference reference,
        string sourceSurface,
        string sourceVersion)
    {
        var resolved = evidenceResolver.ResolveForReceipt(reference, sourceSurface, sourceVersion);
        return new(
            EvidenceKind(reference.Kind),
            reference.EvidenceId,
            reference.SessionId,
            reference.TraceId,
            reference.SpanId,
            reference.TurnId,
            reference.EventId,
            reference.ToolCallId,
            Timestamp(reference.ObservedAt),
            Availability(resolved.Availability),
            resolved.ContentState,
            resolved.Href);
    }

    private MonitorTraceRow? Trace(string traceId, Dictionary<string, MonitorTraceRow?> cache)
    {
        if (cache.TryGetValue(traceId, out var cached)) return cached;
        var trace = projectionStore.GetMonitorTrace(traceId);
        cache.Add(traceId, trace);
        return trace;
    }

    private SessionDetail? Session(string sessionId, Dictionary<string, SessionDetail?> cache)
    {
        if (cache.TryGetValue(sessionId, out var cached)) return cached;
        SessionDetail? session = null;
        if (Guid.TryParseExact(sessionId, "D", out var parsed)
            && parsed != Guid.Empty
            && sessionId == parsed.ToString("D"))
        {
            session = sessionStore.GetDetail(parsed);
        }
        cache.Add(sessionId, session);
        return session;
    }

    private static AlertCenterScope Scope(string? traceId, MonitorTraceRow? trace, SessionDetail? session)
    {
        if (traceId is not null && session is not null && !OwnsTrace(session, traceId))
        {
            session = null;
        }
        var traceRepository = trace?.RepositoryName;
        var traceWorkspace = trace?.WorkspaceLabel;
        var sessionRepository = session?.Session.Repository;
        var sessionWorkspace = session?.Session.Workspace;
        if (!SafeScopeValue(traceRepository)
            || !SafeScopeValue(traceWorkspace)
            || !SafeScopeValue(sessionRepository)
            || !SafeScopeValue(sessionWorkspace))
        {
            return UnknownScope();
        }
        var hasScopeValue = traceRepository is not null
            || traceWorkspace is not null
            || sessionRepository is not null
            || sessionWorkspace is not null;
        if (!hasScopeValue)
        {
            return UnknownScope();
        }
        if (trace is not null && session is not null)
        {
            if (Conflicts(traceRepository, sessionRepository) || Conflicts(traceWorkspace, sessionWorkspace))
            {
                return new("conflicting", null, null, traceRepository, traceWorkspace, sessionRepository, sessionWorkspace);
            }
            return new(
                "exact_agreement",
                traceRepository ?? sessionRepository,
                traceWorkspace ?? sessionWorkspace,
                traceRepository,
                traceWorkspace,
                sessionRepository,
                sessionWorkspace);
        }
        if (trace is not null)
        {
            return new("exact_trace", traceRepository, traceWorkspace, traceRepository, traceWorkspace, null, null);
        }
        if (session is not null)
        {
            return new("exact_session", sessionRepository, sessionWorkspace, null, null, sessionRepository, sessionWorkspace);
        }
        return UnknownScope();
    }

    private static bool SafeScopeValue(string? value) => value is null || AlertCenterLabelGuard.Accepts(value);
    private static AlertCenterScope UnknownScope() => new("unknown", null, null, null, null, null, null);

    private static RelationshipIndex Relationships(IEnumerable<IReadOnlyList<AlertLifecycleEvent>> histories)
    {
        var predecessors = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var successors = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var item in histories.SelectMany(item => item))
        {
            if (item.NewAlertId is null) continue;
            var oldAlertId = item.OldAlertId ?? item.AlertId;
            Add(predecessors, item.NewAlertId, oldAlertId);
            Add(successors, oldAlertId, item.NewAlertId);
        }
        return new(
            predecessors.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value.ToArray(), StringComparer.Ordinal),
            successors.ToDictionary(item => item.Key, item => (IReadOnlyList<string>)item.Value.ToArray(), StringComparer.Ordinal));

        static void Add(Dictionary<string, SortedSet<string>> index, string key, string value)
        {
            if (!index.TryGetValue(key, out var items))
            {
                items = new(StringComparer.Ordinal);
                index.Add(key, items);
            }
            items.Add(value);
        }
    }

    private static bool ValidHistory(
        AlertLifecycleView lifecycle,
        IReadOnlyList<AlertLifecycleEvent> history)
    {
        if (lifecycle.SchemaVersion != AlertLifecycleContractVersions.Lifecycle
            || !AlertLifecycleValidation.IsCanonicalAlertId(lifecycle.AlertId)
            || !Enum.IsDefined(lifecycle.State)
            || lifecycle.Revision < 0
            || lifecycle.LastOccurredAt is { } occurredAt && occurredAt.Offset != TimeSpan.Zero
            || history.Count > 100
            || history.Any(item => item.AlertId != lifecycle.AlertId || !AlertLifecycleValidation.IsValidEvent(item)))
        {
            return false;
        }
        if (lifecycle.Revision == 0)
        {
            return lifecycle.State == AlertLifecycleState.Open
                && lifecycle.LastOccurredAt is null
                && history.Count == 0;
        }
        if (history.Count == 0
            || history.Count != (int)Math.Min(lifecycle.Revision, 100)
            || history[0].Revision != lifecycle.Revision
            || history[0].State != lifecycle.State
            || history[0].OccurredAt != lifecycle.LastOccurredAt)
        {
            return false;
        }
        return history.Zip(
                history.Skip(1),
                (newer, older) => newer.Revision == older.Revision + 1
                    && newer.PreviousState == older.State)
            .All(item => item)
            && (history[^1].Revision != 1 || history[^1].PreviousState == AlertLifecycleState.Open);
    }

    private static bool Matches(AlertCenterAlert item, AlertCenterQuery query)
    {
        var observationDate = DateOnly.ParseExact(item.LastObservedAt[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return observationDate >= query.From
            && observationDate <= query.To
            && Match(query.AlertId, item.AlertId)
            && Match(query.SessionId, item.SessionId)
            && Match(query.TraceId, item.TraceId)
            && Match(query.Severity, item.Severity)
            && Match(query.State, item.Lifecycle.State)
            && Match(query.RuleId, item.Rule.RuleId)
            && Match(query.SourceSurface, item.Source.Surface)
            && Match(query.Repository, item.Scope.Repository)
            && Match(query.Workspace, item.Scope.Workspace)
            && Match(query.Completeness, item.Completeness.State);
    }

    private static IReadOnlyList<AlertCenterRecurringGroup> Aggregate(
        IReadOnlyList<AlertCenterAlert> alerts,
        AlertCenterQuery query,
        bool incomplete) => alerts.GroupBy(item => new RecurringKey(
            item.Rule.RuleId,
            item.Rule.RuleVersion,
            item.Scope.Repository,
            item.Scope.Workspace,
            item.Source.Surface,
            item.Source.Version,
            item.LastObservedAt[..10]))
        .Select(group =>
        {
            var ordered = group.OrderBy(item => item.FirstObservedAt, StringComparer.Ordinal)
                .ThenBy(item => item.AlertId, StringComparer.Ordinal).ToArray();
            var sessions = ordered.Select(item => item.SessionId).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).ToArray();
            var scopeSupported = ordered.All(item => item.Scope.State is "exact_trace" or "exact_session" or "exact_agreement"
                && (item.Scope.Repository is not null || item.Scope.Workspace is not null));
            var state = incomplete
                ? "incomplete_snapshot"
                : !scopeSupported
                    ? "unsupported_scope"
                    : sessions.Length < 2 ? "low_n" : "supported";
            var evidence = ordered.SelectMany(item => item.Evidence).Select(item => new AlertCenterEvidenceReference(
                    item.Kind, item.EvidenceId, item.SessionId, item.TraceId, item.SpanId, item.TurnId,
                    item.EventId, item.ToolCallId, item.ObservedAt))
                .Distinct()
                .OrderBy(item => item.Kind, StringComparer.Ordinal)
                .ThenBy(item => item.SessionId, StringComparer.Ordinal)
                .ThenBy(item => item.EvidenceId, StringComparer.Ordinal)
                .ToArray();
            var distribution = ordered.GroupBy(item => item.Completeness.State, StringComparer.Ordinal)
                .OrderBy(item => item.Key, StringComparer.Ordinal)
                .ToDictionary(item => item.Key, item => item.Count(), StringComparer.Ordinal);
            return new AlertCenterRecurringGroup(
                state,
                group.Key.RuleId,
                group.Key.RuleVersion,
                group.Key.Repository,
                group.Key.Workspace,
                group.Key.SourceSurface,
                group.Key.SourceVersion,
                group.Key.ObservationDate,
                query.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                query.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ordered.Length,
                sessions.Length,
                ordered[0].FirstObservedAt,
                ordered.MaxBy(item => item.LastObservedAt, StringComparer.Ordinal)!.LastObservedAt,
                distribution,
                ordered.Select(item => item.AlertId).Order(StringComparer.Ordinal).ToArray(),
                sessions,
                evidence);
        })
        .OrderBy(item => AggregationRank(item.AggregationState))
        .ThenByDescending(item => item.DistinctSessionCount)
        .ThenByDescending(item => item.OccurrenceCount)
        .ThenBy(item => item.RuleId, StringComparer.Ordinal)
        .ThenBy(item => item.Repository, StringComparer.Ordinal)
        .ThenBy(item => item.Workspace, StringComparer.Ordinal)
        .ThenBy(item => item.SourceSurface, StringComparer.Ordinal)
        .ThenBy(item => item.ObservationDate, StringComparer.Ordinal)
        .ToArray();

    private static AlertCenterRule Rule(AlertCenterReceiptProjectionV1 receipt, AlertRuleDescriptor? descriptor)
    {
        if (descriptor is null)
        {
            return new(
                receipt.RuleId,
                receipt.RuleVersion,
                "unknown_version",
                null,
                null,
                null,
                null,
                null,
                receipt.RequiredCapabilities.ToArray(),
                []);
        }
        return new(
            receipt.RuleId,
            receipt.RuleVersion,
            "registered",
            descriptor.Title,
            descriptor.Description,
            descriptor.Description,
            descriptor.EvaluationWindow,
            descriptor.Scope.ToString().ToLowerInvariant(),
            descriptor.RequiredCapabilities,
            descriptor.Thresholds.Select(item => new AlertCenterThresholdDefinition(
                item.Name,
                item.Unit,
                item.Direction == AlertThresholdDirection.HigherIsWorse ? "higher_is_worse" : "lower_is_worse",
                item.Minimum,
                item.Maximum,
                item.WarningDefault,
                item.CriticalDefault)).ToArray());
    }

    private static AlertCenterValue Value(AlertObservedValue value) => new(value.Name, value.Unit, value.Value);
    private static AlertCenterQueryDto QueryDto(AlertCenterQuery value) => new(
        value.AlertId, value.SessionId, value.TraceId, value.Severity, value.State, value.RuleId, value.SourceSurface,
        value.Repository, value.Workspace, value.Completeness,
        value.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        value.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), value.Offset, value.Limit);
    private static bool Match(string? expected, string? actual) =>
        expected is null || string.Equals(expected, actual, StringComparison.Ordinal);
    private static bool Conflicts(string? left, string? right) =>
        left is not null && right is not null && !string.Equals(left, right, StringComparison.Ordinal);
    private static bool OwnsTrace(SessionDetail session, string traceId) =>
        session.Runs.Any(item => item.TraceId == traceId) || session.Events.Any(item => item.TraceId == traceId);
    private static int SeverityRank(string value) => value switch { "critical" => 0, "warning" => 1, _ => 2 };
    private static int AggregationRank(string value) => value switch
    {
        "supported" => 0,
        "low_n" => 1,
        "unsupported_scope" => 2,
        _ => 3,
    };
    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string Severity(AlertSeverity value) => value switch
    {
        AlertSeverity.Critical => "critical",
        AlertSeverity.Warning => "warning",
        AlertSeverity.Info => "info",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string InitialState(AlertInitialState value) => value switch
    {
        AlertInitialState.Open => "open",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string Completeness(AlertCompleteness value) => value switch
    {
        AlertCompleteness.Unbound => "unbound",
        AlertCompleteness.Partial => "partial",
        AlertCompleteness.Rich => "rich",
        AlertCompleteness.Full => "full",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string EvidenceKind(AlertEvidenceKind value) => value switch
    {
        AlertEvidenceKind.Session => "session",
        AlertEvidenceKind.Trace => "trace",
        AlertEvidenceKind.Span => "span",
        AlertEvidenceKind.Turn => "turn",
        AlertEvidenceKind.Event => "event",
        AlertEvidenceKind.ToolCall => "tool_call",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string Availability(AlertCenterEvidenceAvailability value) => value switch
    {
        AlertCenterEvidenceAvailability.Available => "available",
        AlertCenterEvidenceAvailability.Missing => "missing",
        AlertCenterEvidenceAvailability.Expired => "expired",
        AlertCenterEvidenceAvailability.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static string LifecycleState(AlertLifecycleState value) => value.ToString().ToLowerInvariant();
    private static string LifecycleAction(AlertLifecycleAction value) => value switch
    {
        AlertLifecycleAction.Acknowledge => "acknowledge",
        AlertLifecycleAction.Dismiss => "dismiss",
        AlertLifecycleAction.Resolve => "resolve",
        AlertLifecycleAction.Reopen => "reopen",
        AlertLifecycleAction.Supersede => "supersede",
        AlertLifecycleAction.SourceDeleted => "source_deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
    private static AlertCenterLifecycleTransition History(AlertLifecycleEvent value) => new(
        value.Revision,
        LifecycleAction(value.Action),
        LifecycleState(value.PreviousState),
        LifecycleState(value.State),
        Timestamp(value.OccurredAt),
        value.Actor,
        value.ReasonCode,
        value.OldAlertId,
        value.NewAlertId,
        value.ResultCode);
    private static IReadOnlyList<string> AllowedActions(AlertLifecycleState value) => value switch
    {
        AlertLifecycleState.Open => ["acknowledge", "dismiss", "resolve"],
        AlertLifecycleState.Acknowledged => ["dismiss", "resolve"],
        AlertLifecycleState.Dismissed or AlertLifecycleState.Resolved => ["reopen"],
        _ => [],
    };
    private static AlertCenterReadStatus Map(AlertEngineQueryStatus status) => status == AlertEngineQueryStatus.Busy
        ? AlertCenterReadStatus.Busy
        : status == AlertEngineQueryStatus.Success
            ? AlertCenterReadStatus.Success
            : AlertCenterReadStatus.Unavailable;
    private static AlertCenterReadStatus Map(AlertLifecycleStoreStatus status) => status == AlertLifecycleStoreStatus.Busy
        ? AlertCenterReadStatus.Busy
        : status == AlertLifecycleStoreStatus.Success
            ? AlertCenterReadStatus.Success
            : AlertCenterReadStatus.Unavailable;
    private static AlertCenterReadResult Failure(AlertCenterReadStatus status) => new(status);
    private static bool CanonicalHash(string value) =>
        value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private sealed record ReceiptAcquisition(
        AlertCenterReadStatus Status,
        IReadOnlyList<AlertCenterReceiptProjectionV1> Receipts,
        bool Incomplete);
    private sealed record CoverageAcquisition(
        AlertCenterReadStatus Status,
        IReadOnlyList<AlertCenterCoverageFact> Facts,
        bool Incomplete);
    private sealed record CoverageContext(
        string SourceSurface,
        string SourceVersion,
        string SessionId,
        string? TraceId,
        string ObservationDate);
    private sealed record RelationshipIndex(
        IReadOnlyDictionary<string, IReadOnlyList<string>> Predecessors,
        IReadOnlyDictionary<string, IReadOnlyList<string>> Successors);
    private sealed record RecurringKey(
        string RuleId,
        string RuleVersion,
        string? Repository,
        string? Workspace,
        string SourceSurface,
        string SourceVersion,
        string ObservationDate);
}

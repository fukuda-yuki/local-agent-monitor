using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal sealed class SqliteAlertCenterReadModelV2(
    IAlertEngineVersionedQueryStore queryStore,
    IAlertLifecycleStore lifecycleStore,
    IAlertCenterReadModel v1ReadModel,
    ICostAlertPresentationResolverV1 presentationResolver)
    : IAlertCenterReadModelV2
{
    private const int MaximumOwnerPages = 20;
    private const int MaximumReceipts = 2_000;
    private const int MaximumReceiptBytes = 64 * 1_024 * 1_024;
    private const int MaximumCoverageFacts = 100;
    private const int MaximumResponseBytes = 16 * 1_024 * 1_024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private readonly IReadOnlyDictionary<(string RuleId, string RuleVersion), AlertRuleDescriptorV2>
        descriptors = new AlertRuleRegistryV2().Rules.ToDictionary(
            item => (item.Descriptor.RuleId, item.Descriptor.RuleVersion),
            item => item.Descriptor);

    public AlertCenterReadResultV2 Read(AlertCenterQueryV2 query)
    {
        try
        {
            var lifecycleInitialization = lifecycleStore.Initialize();
            if (lifecycleInitialization.Status != AlertLifecycleStoreStatus.Success)
            {
                return Failure(lifecycleInitialization.Status == AlertLifecycleStoreStatus.Busy);
            }

            var acquisition = AcquireReceipts();
            if (acquisition.Status != AlertCenterReadStatusV2.Success)
            {
                return new(acquisition.Status);
            }

            var projector = v1ReadModel as IAlertCenterOwnedReceiptProjectorV1;
            var retainedOwners = new List<AlertVersionedReceiptQueryItem>();
            var retainedProjected = new List<ProjectedItem>();
            var retainedBytes = 0;
            foreach (var ownerItem in acquisition.Items)
            {
                ProjectedItem? projected;
                AlertCenterReadStatusV2 projectionStatus;
                if (ownerItem.ReceiptV1 is { } receiptV1)
                {
                    var result = projector?.ProjectOwned(
                        [receiptV1],
                        V1Query(query),
                        incomplete: false);
                    projectionStatus = result?.Status switch
                    {
                        AlertCenterReadStatus.Success => AlertCenterReadStatusV2.Success,
                        AlertCenterReadStatus.Busy => AlertCenterReadStatusV2.Busy,
                        _ => AlertCenterReadStatusV2.Unavailable,
                    };
                    var alert = result?.AllAlerts.SingleOrDefault();
                    projected = alert is null
                        ? null
                        : new(
                            alert.AlertId,
                            alert.Severity,
                            alert.LastObservedAt,
                            new("receipt_v1", alert, null),
                            false,
                            []);
                }
                else
                {
                    var result = ProjectCost(ownerItem.ReceiptV2!);
                    projectionStatus = result.Status;
                    projected = result.Item;
                }
                if (projectionStatus != AlertCenterReadStatusV2.Success
                    || projected is null
                    || projected.AlertId != OwnerAlertId(ownerItem))
                {
                    return new(projectionStatus == AlertCenterReadStatusV2.Success
                        ? AlertCenterReadStatusV2.Unavailable
                        : projectionStatus);
                }
                var size = checked(
                    ownerItem.CanonicalBytes.Count
                    + JsonSerializer.SerializeToUtf8Bytes(projected.Dto, Json).Length);
                if (retainedBytes > MaximumReceiptBytes - size)
                {
                    acquisition = new(
                        AlertCenterReadStatusV2.Success,
                        retainedOwners.ToArray(),
                        true,
                        "retained_bytes_limit");
                    break;
                }
                retainedOwners.Add(ownerItem);
                retainedProjected.Add(projected);
                retainedBytes += size;
            }

            AlertCenterOwnedProjectionResult v1;
            while (true)
            {
                var retainedV1 = retainedOwners
                    .Where(item => item.ReceiptV1 is not null)
                    .Select(item => item.ReceiptV1!)
                    .ToArray();
                v1 = retainedV1.Length == 0
                    ? new AlertCenterOwnedProjectionResult(
                        AlertCenterReadStatus.Success, [], [], [])
                    : projector?.ProjectOwned(
                        retainedV1,
                        V1Query(query),
                        acquisition.Incomplete)
                      ?? new AlertCenterOwnedProjectionResult(
                          AlertCenterReadStatus.Unavailable, [], [], []);
                if (v1.Status != AlertCenterReadStatus.Success)
                {
                    return new(v1.Status == AlertCenterReadStatus.Busy
                        ? AlertCenterReadStatusV2.Busy
                        : AlertCenterReadStatusV2.Unavailable);
                }
                var finalV1 = v1.AllAlerts.ToDictionary(item => item.AlertId, StringComparer.Ordinal);
                for (var index = 0; index < retainedProjected.Count; index++)
                {
                    if (!retainedProjected[index].IsCost)
                    {
                        var alertId = retainedProjected[index].AlertId;
                        if (!finalV1.TryGetValue(alertId, out var alert))
                        {
                            return new(AlertCenterReadStatusV2.Unavailable);
                        }
                        retainedProjected[index] = new(
                            alert.AlertId,
                            alert.Severity,
                            alert.LastObservedAt,
                            new("receipt_v1", alert, null),
                            false,
                            []);
                    }
                }
                var finalBytes = retainedOwners.Select((owner, index) =>
                        (long)owner.CanonicalBytes.Count
                        + JsonSerializer.SerializeToUtf8Bytes(
                            retainedProjected[index].Dto,
                            Json).Length)
                    .Sum();
                if (finalBytes <= MaximumReceiptBytes) break;
                if (retainedOwners.Count == 0)
                {
                    return new(AlertCenterReadStatusV2.ResponseTooLarge);
                }
                retainedOwners.RemoveAt(retainedOwners.Count - 1);
                retainedProjected.RemoveAt(retainedProjected.Count - 1);
                acquisition = new(
                    AlertCenterReadStatusV2.Success,
                    retainedOwners.ToArray(),
                    true,
                    "retained_bytes_limit");
            }
            var v1Matches = v1.FilteredAlerts
                .Select(item => item.AlertId)
                .ToHashSet(StringComparer.Ordinal);
            var items = retainedProjected
                .Where(item => item.IsCost
                    ? Matches(item, query)
                    : query.ReceiptKind is "all" or "receipt_v1"
                      && query.ScopeKind == "all"
                      && query.Currency == "all"
                      && query.CoverageState == "all"
                      && v1Matches.Contains(item.AlertId))
                .ToList();
            items.Sort(ProjectedItemComparer.Instance);
            var coverage = AcquireCoverage(acquisition.Items);
            var coverageState = coverage.Status switch
            {
                AlertCenterReadStatusV2.Success when coverage.Incomplete => "incomplete",
                AlertCenterReadStatusV2.Success => "complete",
                _ => "unavailable",
            };
            var coverageItems = coverage.Status == AlertCenterReadStatusV2.Success
                ? coverage.Items
                : [];
            var queryDto = QueryDto(query);
            var snapshotId = SnapshotId(
                queryDto,
                acquisition,
                retainedProjected,
                coverageState,
                coverageItems);
            var recurringGroups = acquisition.Incomplete
                || query.ReceiptKind == "cost_receipt_v2"
                || query.ScopeKind != "all"
                || query.Currency != "all"
                || query.CoverageState != "all"
                ? []
                : v1.RecurringGroups;
            var plans = BuildPagePlans(
                snapshotId,
                query,
                queryDto,
                acquisition,
                items,
                recurringGroups,
                coverageState,
                coverageItems);
            if (plans.Count == 0)
            {
                return new(AlertCenterReadStatusV2.ResponseTooLarge);
            }
            if (!TryCursor(
                    query.Cursor,
                    snapshotId,
                    query,
                    items,
                    plans,
                    out var start,
                    out var cursorStatus))
            {
                return new(cursorStatus);
            }
            return new(
                AlertCenterReadStatusV2.Success,
                plans.Single(item => item.Start == start).Snapshot);
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return new(AlertCenterReadStatusV2.Unavailable);
        }
    }

    private ReceiptAcquisition AcquireReceipts()
    {
        var items = new List<AlertVersionedReceiptQueryItem>();
        var cursor = (string?)null;
        var retainedBytes = 0;
        for (var pageNumber = 0; pageNumber < MaximumOwnerPages; pageNumber++)
        {
            var page = queryStore.ListReceiptsVersioned(cursor, 100);
            if (page.Status != AlertEngineQueryStatus.Success)
            {
                return new(Map(page.Status), [], false, null);
            }
            if (page.Items.Count > 100
                || page.CanonicalByteCount < 0
                || page.CanonicalByteCount > 8_388_608
                || page.CanonicalByteCount
                   != page.Items.Sum(item => (long)item.CanonicalBytes.Count))
            {
                return new(AlertCenterReadStatusV2.Unavailable, [], false, null);
            }
            foreach (var item in page.Items)
            {
                var alertId = OwnerAlertId(item);
                if (items.Count > 0
                    && string.CompareOrdinal(
                        OwnerAlertId(items[^1]),
                        alertId) >= 0)
                {
                    return new(AlertCenterReadStatusV2.Unavailable, [], false, null);
                }
                var size = item.CanonicalBytes.Count;
                if (retainedBytes > MaximumReceiptBytes - size)
                {
                    return new(
                        AlertCenterReadStatusV2.Success,
                        items.ToArray(),
                        true,
                        "retained_bytes_limit");
                }
                if (items.Count == MaximumReceipts)
                {
                    return new(
                        AlertCenterReadStatusV2.Success,
                        items.ToArray(),
                        true,
                        "receipt_limit");
                }
                items.Add(item);
                retainedBytes += size;
            }
            if (page.Exhausted)
            {
                return new(AlertCenterReadStatusV2.Success, items.ToArray(), false, null);
            }
            if (items.Count == MaximumReceipts)
            {
                return new(
                    AlertCenterReadStatusV2.Success,
                    items.ToArray(),
                    true,
                    "receipt_limit");
            }
            if (string.IsNullOrEmpty(page.NextCursor)
                || page.NextCursor == cursor)
            {
                return new(AlertCenterReadStatusV2.Unavailable, [], false, null);
            }
            cursor = page.NextCursor;
        }
        return new(
            AlertCenterReadStatusV2.Success,
            items.ToArray(),
            true,
            "owner_more");
    }

    private ProjectCostResult ProjectCost(AlertCenterReceiptProjectionV2 receipt)
    {
        var lifecycle = lifecycleStore.Get(receipt.AlertId);
        if (lifecycle.Status != AlertLifecycleStoreStatus.Success
            || lifecycle.Lifecycle is null
            || lifecycle.Lifecycle.AlertId != receipt.AlertId)
        {
            return new(Map(lifecycle.Status), null);
        }
        var history = lifecycleStore.History(receipt.AlertId, 100);
        if (history.Status != AlertLifecycleStoreStatus.Success
            || !ValidHistory(lifecycle.Lifecycle, history.Events))
        {
            return new(Map(history.Status), null);
        }
        var resolution = presentationResolver.Resolve(receipt.Members, receipt.Evidence);
        if (resolution.State == "busy") return new(AlertCenterReadStatusV2.Busy, null);
        if (!ValidPresentation(receipt, resolution)
            || !resolution.Members.Select((item, index) =>
                    item.SessionId == receipt.Members[index].SessionId
                    && item.SessionEffectiveAtUtc == receipt.Members[index].SessionEffectiveAtUtc
                    && item.EstimateId == receipt.Members[index].EstimateId)
                .All(item => item))
        {
            return new(AlertCenterReadStatusV2.Unavailable, null);
        }

        descriptors.TryGetValue((receipt.RuleId, receipt.RuleVersion), out var descriptor);
        var members = receipt.Members.Select((member, index) =>
        {
            var presentation = resolution.Members[index];
            return new AlertCenterCostMemberV2(
                member.SessionId,
                Timestamp(member.SessionEffectiveAtUtc),
                Wire(member.State),
                member.AttemptRevision,
                member.AttemptResultKind is null ? null : Wire(member.AttemptResultKind.Value),
                member.AttemptResultCode,
                member.HeadRevision,
                member.EstimateId,
                member.CatalogSha256,
                member.RegistryVersion,
                member.BillingMode,
                presentation.SessionEvidenceState,
                presentation.Repository,
                presentation.Workspace,
                presentation.ScopeState,
                presentation.SessionHref,
                presentation.EstimateEvidenceState,
                presentation.EstimateHref);
        }).ToArray();
        var bySession = resolution.Members.ToDictionary(item => item.SessionId, StringComparer.Ordinal);
        var evidence = receipt.Evidence.Select(item =>
        {
            var presentation = bySession[item.SessionId];
            var estimate = item.Kind == AlertEvidenceKindV2.PricingEstimate;
            return new AlertCenterCostEvidenceV2(
                estimate ? "pricing_estimate" : "session",
                item.EvidenceId,
                item.SessionId,
                Timestamp(item.ObservedAtUtc),
                estimate ? presentation.EstimateEvidenceState! : presentation.SessionEvidenceState,
                estimate ? presentation.EstimateHref : presentation.SessionHref);
        }).ToArray();
        var lifecycleDto = new AlertCenterLifecycle(
            Wire(lifecycle.Lifecycle.State),
            lifecycle.Lifecycle.Revision,
            lifecycle.Lifecycle.LastOccurredAt is null
                ? null
                : Timestamp(lifecycle.Lifecycle.LastOccurredAt.Value),
            AllowedActions(lifecycle.Lifecycle.State),
            history.Events.Select(History).ToArray());
        var payload = new AlertCenterCostReceiptV2(
            receipt.AlertId,
            receipt.EvaluationId,
            receipt.RuleId,
            receipt.RuleVersion,
            Wire(receipt.Severity),
            receipt.InitialState == AlertInitialState.Open
                ? "open"
                : throw new InvalidOperationException(),
            lifecycleDto,
            Timestamp(receipt.FirstObservedAt),
            Timestamp(receipt.LastObservedAt),
            receipt.Summary,
            descriptor is null
                ? new(receipt.RuleId, receipt.RuleVersion, "unknown_version", null, null, null, null)
                : new(
                    receipt.RuleId,
                    receipt.RuleVersion,
                    "registered",
                    descriptor.Title,
                    descriptor.Description,
                    descriptor.EvaluationWindow,
                    Wire(descriptor.ScopeKind)),
            descriptor?.Formula,
            receipt.SourceSurface,
            receipt.SourceVersion,
            Wire(receipt.Completeness),
            receipt.CompletenessReasons.ToArray(),
            receipt.SourceCostConfigurationId,
            receipt.SourceConfigurationHeadRevision,
            receipt.SourceConfigurationCatalogSha256,
            receipt.ConfigurationVersion,
            receipt.ConfigurationHash,
            receipt.InputHash,
            new(
                receipt.Scope.ScopeId,
                Wire(receipt.Scope.Kind),
                NullableTimestamp(receipt.Scope.WindowStartUtc),
                NullableTimestamp(receipt.Scope.WindowEndUtc),
                receipt.Scope.SessionIds.ToArray()),
            receipt.EligibilityDigest!,
            evidence,
            receipt.Currency,
            Wire(receipt.AggregateState),
            receipt.ObservedAmount,
            receipt.WarningThreshold,
            receipt.CriticalThreshold,
            receipt.EligibleCount,
            receipt.EstimatedCount,
            receipt.PartialCount,
            receipt.NotEstimableCount,
            receipt.MissingCount,
            receipt.FailedCount,
            receipt.UnavailableCount,
            receipt.StaleCount,
            receipt.CoverageNumerator,
            receipt.CoverageDenominator,
            receipt.CoverageBasisPoints,
            members);
        return new(
            AlertCenterReadStatusV2.Success,
            new(
                receipt.AlertId,
                Wire(receipt.Severity),
                Timestamp(receipt.LastObservedAt),
                new("cost_receipt_v2", null, payload),
                true,
                members));
    }

    private CoverageAcquisition AcquireCoverage(
        IReadOnlyList<AlertVersionedReceiptQueryItem> receipts)
    {
        var items = new List<AlertCenterCoverageItemV2>();
        var contexts = receipts
            .Where(item => item.ReceiptV1 is not null)
            .Select(item => item.ReceiptV1!)
            .GroupBy(item => item.EvaluationId, StringComparer.Ordinal)
            .ToDictionary(item => item.Key, item => item.ToArray(), StringComparer.Ordinal);
        string? evaluationCursor = null;
        var evaluationCount = 0;
        for (var pageIndex = 0; pageIndex < MaximumOwnerPages; pageIndex++)
        {
            var page = queryStore.ListEvaluationsVersioned(evaluationCursor, 100);
            if (page.Status == AlertEngineQueryStatus.Busy)
            {
                return new(AlertCenterReadStatusV2.Busy, [], false);
            }
            if (page.Status != AlertEngineQueryStatus.Success)
            {
                return new(AlertCenterReadStatusV2.Unavailable, [], false);
            }
            foreach (var evaluation in page.Items)
            {
                if (++evaluationCount > MaximumReceipts)
                {
                    return new(AlertCenterReadStatusV2.Success, items.ToArray(), true);
                }
                long? suppressionCursor = null;
                while (true)
                {
                    var suppressionPage = queryStore.ListSuppressionsVersioned(
                        EvaluationId(evaluation),
                        suppressionCursor,
                        100);
                    if (suppressionPage.Status != AlertEngineQueryStatus.Success)
                    {
                        return new(Map(suppressionPage.Status), [], false);
                    }
                    foreach (var suppression in suppressionPage.Items)
                    {
                        if (items.Count == MaximumCoverageFacts)
                        {
                            return new(AlertCenterReadStatusV2.Success, items.ToArray(), true);
                        }
                        items.Add(Coverage(suppression, contexts));
                    }
                    if (suppressionPage.Exhausted) break;
                    if (suppressionPage.NextCursor is null
                        || suppressionPage.NextCursor == suppressionCursor)
                    {
                        return new(AlertCenterReadStatusV2.Unavailable, [], false);
                    }
                    suppressionCursor = suppressionPage.NextCursor;
                }
            }
            if (page.Exhausted)
            {
                return new(AlertCenterReadStatusV2.Success, items.ToArray(), false);
            }
            if (string.IsNullOrEmpty(page.NextCursor)
                || page.NextCursor == evaluationCursor)
            {
                return new(AlertCenterReadStatusV2.Unavailable, [], false);
            }
            evaluationCursor = page.NextCursor;
        }
        return new(AlertCenterReadStatusV2.Success, items.ToArray(), true);
    }

    private static AlertCenterCoverageItemV2 Coverage(
        AlertVersionedSuppressionQueryItem item,
        IReadOnlyDictionary<string, AlertCenterReceiptProjectionV1[]> contexts)
    {
        if (item.SuppressionV2 is { } cost)
        {
            return new(
                "cost_suppression_v2",
                null,
                new(
                    cost.EvaluationId,
                    item.SuppressionOrdinal,
                    cost.RuleId,
                    cost.RuleVersion,
                    cost.Code,
                    cost.SourceCostConfigurationId,
                    cost.SourceConfigurationHeadRevision,
                    cost.SourceConfigurationCatalogSha256,
                    cost.ConfigurationVersion,
                    cost.ConfigurationHash,
                    Wire(cost.ScopeKind),
                    cost.ScopeId,
                    NullableTimestamp(cost.ScopeStartUtc),
                    NullableTimestamp(cost.ScopeEndUtc),
                    cost.EligibilityDigest,
                    cost.Currency,
                    Wire(cost.AggregateState),
                    cost.EligibleCount,
                    cost.EstimatedCount,
                    cost.PartialCount,
                    cost.NotEstimableCount,
                    cost.MissingCount,
                    cost.FailedCount,
                    cost.UnavailableCount,
                    cost.StaleCount,
                    cost.CoverageBasisPoints,
                    NullableTimestamp(cost.FirstObservedAt),
                    NullableTimestamp(cost.LastObservedAt)));
        }
        var v1 = item.SuppressionV1!;
        var context = Context(v1.EvaluationId, contexts);
        return new(
            "suppression_v1",
            new(
                v1.EvaluationId,
                item.SuppressionOrdinal,
                v1.RuleId,
                v1.RuleVersion,
                v1.Code,
                v1.MissingCapabilities.ToArray(),
                context is null ? "unknown" : "exact_evaluation",
                context?.SourceSurface,
                context?.SourceVersion,
                context?.SessionId,
                context?.TraceId,
                context?.LastObservedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            null);
    }

    private static AlertCenterReceiptProjectionV1? Context(
        string evaluationId,
        IReadOnlyDictionary<string, AlertCenterReceiptProjectionV1[]> contexts)
    {
        if (!contexts.TryGetValue(evaluationId, out var candidates)
            || candidates.Length == 0)
        {
            return null;
        }
        var first = candidates[0];
        return candidates.All(item =>
            item.SourceSurface == first.SourceSurface
            && item.SourceVersion == first.SourceVersion
            && item.SessionId == first.SessionId
            && item.TraceId == first.TraceId)
            ? first
            : null;
    }

    private static bool Matches(ProjectedItem item, AlertCenterQueryV2 query)
    {
        if (!item.IsCost) return true;
        var cost = item.Dto.CostReceiptV2!;
        var dateStart = query.From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dateEnd = query.To.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var dateMatches = cost.Scope.Kind == "session"
            ? cost.Members.Any(member =>
                DateTimeOffset.Parse(member.SessionEffectiveAtUtc, CultureInfo.InvariantCulture) >= dateStart
                && DateTimeOffset.Parse(member.SessionEffectiveAtUtc, CultureInfo.InvariantCulture) < dateEnd)
            : DateTimeOffset.Parse(cost.Scope.WindowStartUtc!, CultureInfo.InvariantCulture) < dateEnd
              && DateTimeOffset.Parse(cost.Scope.WindowEndUtc!, CultureInfo.InvariantCulture) > dateStart;
        var sameMember = cost.Members.Any(member =>
            Match(query.SessionId, member.SessionId)
            && (query.Repository is null && query.Workspace is null
                || member.ScopeState == "available")
            && Match(query.Repository, member.Repository)
            && Match(query.Workspace, member.Workspace));
        return dateMatches
            && query.ReceiptKind is "all" or "cost_receipt_v2"
            && Match(query.AlertId, cost.AlertId)
            && Match(query.Severity, cost.Severity)
            && Match(query.State, cost.Lifecycle.State)
            && Match(query.RuleId, cost.RuleId)
            && Match(query.SourceSurface, cost.SourceSurface)
            && Match(query.Completeness, cost.Completeness)
            && query.TraceId is null
            && (query.SessionId is null && query.Repository is null && query.Workspace is null || sameMember)
            && (query.ScopeKind == "all" || query.ScopeKind == cost.Scope.Kind)
            && (query.Currency == "all" || query.Currency == cost.Currency)
            && (query.CoverageState == "all"
                || query.CoverageState == "full" && cost.CoverageBasisPoints == 10_000
                || query.CoverageState == "partial" && cost.CoverageBasisPoints is > 0 and < 10_000);
    }

    private static bool ValidPresentation(
        AlertCenterReceiptProjectionV2 receipt,
        CostAlertPresentationResolutionV1 resolution)
    {
        if (resolution.State != "success"
            || resolution.Members.Count != receipt.Members.Count
            || JsonSerializer.SerializeToUtf8Bytes(resolution, Json).Length > 8_388_608)
        {
            return false;
        }
        for (var index = 0; index < receipt.Members.Count; index++)
        {
            var source = receipt.Members[index];
            var item = resolution.Members[index];
            var sessionHref =
                $"/costs?session_id={Uri.EscapeDataString(source.SessionId)}";
            var estimateHref = source.EstimateId is null
                ? null
                : $"{sessionHref}&estimate_id={Uri.EscapeDataString(source.EstimateId)}";
            if (item.SessionId != source.SessionId
                || item.SessionEffectiveAtUtc != source.SessionEffectiveAtUtc
                || item.SessionEvidenceState is not ("available" or "missing" or "expired")
                || item.Repository is not null && !AlertCenterLabelGuard.Accepts(item.Repository)
                || item.Workspace is not null && !AlertCenterLabelGuard.Accepts(item.Workspace)
                || item.ScopeState is not ("available" or "unavailable")
                || item.SessionHref != sessionHref
                || item.EstimateId != source.EstimateId
                || source.EstimateId is null
                   && (item.EstimateEvidenceState is not null || item.EstimateHref is not null)
                || source.EstimateId is not null
                   && (item.EstimateEvidenceState is not ("available" or "missing" or "expired")
                       || item.EstimateEvidenceState == "available" && item.EstimateHref != estimateHref
                       || item.EstimateEvidenceState != "available" && item.EstimateHref is not null))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidHistory(
        AlertLifecycleView lifecycle,
        IReadOnlyList<AlertLifecycleEvent> history)
    {
        if (lifecycle.SchemaVersion != AlertLifecycleContractVersions.Lifecycle
            || !AlertLifecycleValidation.IsCanonicalAlertId(lifecycle.AlertId)
            || !Enum.IsDefined(lifecycle.State)
            || lifecycle.Revision < 0
            || lifecycle.LastOccurredAt is { Offset: var offset }
               && offset != TimeSpan.Zero
            || history.Count > 100
            || history.Any(item =>
                item.AlertId != lifecycle.AlertId
                || !AlertLifecycleValidation.IsValidEvent(item)))
        {
            return false;
        }
        if (lifecycle.Revision == 0)
        {
            return lifecycle.State == AlertLifecycleState.Open
                && lifecycle.LastOccurredAt is null
                && history.Count == 0;
        }
        return history.Count == Math.Min(lifecycle.Revision, 100)
            && history.Count > 0
            && history[0].Revision == lifecycle.Revision
            && history[0].State == lifecycle.State
            && history[0].OccurredAt == lifecycle.LastOccurredAt
            && history.Zip(history.Skip(1), (newer, older) =>
                    newer.Revision == older.Revision + 1
                    && newer.PreviousState == older.State)
                .All(item => item)
            && (history[^1].Revision != 1
                || history[^1].PreviousState == AlertLifecycleState.Open);
    }

    private static IReadOnlyList<PagePlan> BuildPagePlans(
        string snapshotId,
        AlertCenterQueryV2 query,
        AlertCenterQueryDtoV2 queryDto,
        ReceiptAcquisition acquisition,
        IReadOnlyList<ProjectedItem> items,
        IReadOnlyList<AlertCenterRecurringGroup> recurringGroups,
        string coverageState,
        IReadOnlyList<AlertCenterCoverageItemV2> coverage)
    {
        var plans = new List<PagePlan>();
        var start = 0;
        do
        {
            var count = Math.Min(query.Limit, items.Count - start);
            AlertCenterSnapshotV2? accepted = null;
            while (count >= 0)
            {
                var end = start + count;
                var cursorAfter = count == 0
                    ? null
                    : EncodeCursor(snapshotId, query, items[end - 1]);
                var nextCursor = end < items.Count ? cursorAfter : null;
                var previousCursor = plans.Count < 2
                    ? null
                    : plans[^2].CursorAfter;
                var candidate = new AlertCenterSnapshotV2(
                    AlertCenterContractVersions.CenterV2,
                    snapshotId,
                    acquisition.Incomplete ? "incomplete" : "complete",
                    acquisition.CapReason,
                    acquisition.Items.Count,
                    acquisition.Incomplete ? "acquired_only" : "exact",
                    items.Count,
                    queryDto,
                    items.Skip(start).Take(count).Select(item => item.Dto).ToArray(),
                    count == 0 ? 0 : start + 1,
                    count == 0 ? 0 : end,
                    start > 0,
                    previousCursor,
                    nextCursor,
                    acquisition.Incomplete ? "incomplete_snapshot" : "complete",
                    recurringGroups,
                    coverageState,
                    coverage,
                    coverageState == "complete" ? 0 : null);
                if (JsonSerializer.SerializeToUtf8Bytes(candidate, Json).Length
                    <= MaximumResponseBytes)
                {
                    accepted = candidate;
                    plans.Add(new(start, end, cursorAfter, candidate));
                    break;
                }
                count--;
            }
            if (accepted is null || count == 0 && start < items.Count)
            {
                return [];
            }
            start += count;
        }
        while (start < items.Count || items.Count == 0 && plans.Count == 0);
        return plans;
    }

    private static bool TryCursor(
        string? cursor,
        string snapshotId,
        AlertCenterQueryV2 query,
        IReadOnlyList<ProjectedItem> items,
        IReadOnlyList<PagePlan> plans,
        out int start,
        out AlertCenterReadStatusV2 status)
    {
        start = 0;
        status = AlertCenterReadStatusV2.Success;
        if (cursor is null) return true;
        try
        {
            var encoded = cursor["alert-center-cursor-v2.".Length..];
            var padding = new string('=', (4 - encoded.Length % 4) % 4);
            var bytes = Convert.FromBase64String(
                encoded.Replace('-', '+').Replace('_', '/') + padding);
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            var expectedProperties = new[]
            {
                "schema_version", "snapshot_id", "filter_digest", "limit",
                "severity_rank", "last_observed_at", "alert_id",
            };
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(item => item.Name)
                    .SequenceEqual(expectedProperties, StringComparer.Ordinal))
            {
                status = AlertCenterReadStatusV2.InvalidQuery;
                return false;
            }
            var cursorSnapshotId = root.GetProperty("snapshot_id").GetString();
            var filterDigest = root.GetProperty("filter_digest").GetString();
            var limit = root.GetProperty("limit").GetInt32();
            var severityRank = root.GetProperty("severity_rank").GetInt32();
            var lastObservedAt = root.GetProperty("last_observed_at").GetString();
            var alertId = root.GetProperty("alert_id").GetString();
            var canonicalBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema_version = root.GetProperty("schema_version").GetString(),
                snapshot_id = cursorSnapshotId,
                filter_digest = filterDigest,
                limit,
                severity_rank = severityRank,
                last_observed_at = lastObservedAt,
                alert_id = alertId,
            });
            if (!bytes.AsSpan().SequenceEqual(canonicalBytes)
                || cursor != CursorText(canonicalBytes)
                || root.GetProperty("schema_version").GetString() != "alert.center.cursor.v2"
                || filterDigest != FilterDigest(QueryDto(query))
                || limit != query.Limit)
            {
                status = AlertCenterReadStatusV2.InvalidQuery;
                return false;
            }
            if (cursorSnapshotId != snapshotId)
            {
                status = AlertCenterReadStatusV2.SnapshotChanged;
                return false;
            }
            var index = items.ToList().FindIndex(item => item.AlertId == alertId);
            if (index < 0
                || !plans.Any(item => item.End == index + 1 && item.End < items.Count)
                || severityRank != SeverityRank(items[index].Severity)
                || lastObservedAt != items[index].LastObservedAt)
            {
                status = AlertCenterReadStatusV2.InvalidQuery;
                return false;
            }
            start = index + 1;
            return true;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            status = AlertCenterReadStatusV2.InvalidQuery;
            return false;
        }
    }

    private static string EncodeCursor(
        string snapshotId,
        AlertCenterQueryV2 query,
        ProjectedItem item)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = "alert.center.cursor.v2",
            snapshot_id = snapshotId,
            filter_digest = FilterDigest(QueryDto(query)),
            limit = query.Limit,
            severity_rank = SeverityRank(item.Severity),
            last_observed_at = item.LastObservedAt,
            alert_id = item.AlertId,
        });
        return CursorText(bytes);
    }

    private static string CursorText(byte[] bytes) =>
        "alert-center-cursor-v2."
        + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string SnapshotId(
        AlertCenterQueryDtoV2 query,
        ReceiptAcquisition acquisition,
        IReadOnlyList<ProjectedItem> acquiredProjections,
        string coverageState,
        IReadOnlyList<AlertCenterCoverageItemV2> coverage)
    {
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            query,
            acquisition.Incomplete,
            acquisition.CapReason,
            acquired = acquisition.Items.Select((owner, index) => new
            {
                alert_id = OwnerAlertId(owner),
                receipt_kind = owner.ContractVersion == AlertContractKind.V1
                    ? "receipt_v1"
                    : "cost_receipt_v2",
                canonical_receipt_sha = Convert.ToHexString(
                    SHA256.HashData(owner.CanonicalBytes.ToArray())).ToLowerInvariant(),
                sanitized_projection_sha = Convert.ToHexString(SHA256.HashData(
                    JsonSerializer.SerializeToUtf8Bytes(
                        acquiredProjections[index].Dto,
                        Json))).ToLowerInvariant(),
            }),
            coverageState,
            coverage,
        }, Json);
        return "alert-center-snapshot-" + LengthFramedSha256(
            "alert-center-snapshot/v2",
            snapshotBytes);
    }

    private static string FilterDigest(AlertCenterQueryDtoV2 query)
    {
        var queryBytes = JsonSerializer.SerializeToUtf8Bytes(query, Json);
        return LengthFramedSha256("alert-center-filter/v2", queryBytes);
    }

    private static string LengthFramedSha256(string domain, byte[] payload)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in new[] { Encoding.UTF8.GetBytes(domain), payload })
        {
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            stream.Write(length);
            stream.Write(value);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static AlertCenterQueryDtoV2 QueryDto(AlertCenterQueryV2 value) => new(
        value.AlertId,
        value.SessionId,
        value.TraceId,
        value.Severity,
        value.State,
        value.RuleId,
        value.SourceSurface,
        value.Repository,
        value.Workspace,
        value.Completeness,
        value.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        value.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        value.ReceiptKind,
        value.ScopeKind,
        value.Currency,
        value.CoverageState,
        value.Limit);

    private static AlertCenterQuery V1Query(AlertCenterQueryV2 value) => new(
        value.AlertId,
        value.SessionId,
        value.TraceId,
        value.Severity,
        value.State,
        value.RuleId,
        value.SourceSurface,
        value.Repository,
        value.Workspace,
        value.Completeness,
        value.From,
        value.To,
        0,
        MaximumReceipts);

    private static string EvaluationId(AlertVersionedEvaluationQueryItem item) =>
        item.EvaluationV1?.EvaluationId ?? item.EvaluationV2!.EvaluationId;
    private static string OwnerAlertId(AlertVersionedReceiptQueryItem item) =>
        item.ReceiptV1?.AlertId ?? item.ReceiptV2!.AlertId;
    private static bool Match(string? expected, string? actual) =>
        expected is null || expected == actual;
    private static int SeverityRank(string value) => value switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2,
    };
    private static AlertCenterReadResultV2 Failure(bool busy) =>
        new(busy ? AlertCenterReadStatusV2.Busy : AlertCenterReadStatusV2.Unavailable);
    private static AlertCenterReadStatusV2 Map(AlertEngineQueryStatus status) =>
        status == AlertEngineQueryStatus.Busy
            ? AlertCenterReadStatusV2.Busy
            : status == AlertEngineQueryStatus.Success
                ? AlertCenterReadStatusV2.Success
                : AlertCenterReadStatusV2.Unavailable;
    private static AlertCenterReadStatusV2 Map(AlertLifecycleStoreStatus status) =>
        status == AlertLifecycleStoreStatus.Busy
            ? AlertCenterReadStatusV2.Busy
            : status == AlertLifecycleStoreStatus.Success
                ? AlertCenterReadStatusV2.Success
                : AlertCenterReadStatusV2.Unavailable;
    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? NullableTimestamp(DateTimeOffset? value) =>
        value is null ? null : Timestamp(value.Value);
    private static string Wire(AlertSeverity value) => value.ToString().ToLowerInvariant();
    private static string Wire(AlertLifecycleState value) => value.ToString().ToLowerInvariant();
    private static string Wire(AlertCostScopeKindV2 value) => value switch
    {
        AlertCostScopeKindV2.Session => "session",
        AlertCostScopeKindV2.UtcDay => "utc_day",
        _ => "rolling_period",
    };
    private static string Wire(AlertCostAggregateStateV2 value) => value switch
    {
        AlertCostAggregateStateV2.Available => "available",
        AlertCostAggregateStateV2.Unrepresentable => "unrepresentable",
        _ => "not_applicable",
    };
    private static string Wire(AlertCostCompletenessV2 value) =>
        value == AlertCostCompletenessV2.Full ? "full" : "partial";
    private static string Wire(AlertCostMemberStateV2 value) => value switch
    {
        AlertCostMemberStateV2.NotEstimable => "not_estimable",
        _ => value.ToString().ToLowerInvariant(),
    };
    private static string Wire(AlertCostAttemptResultKindV2 value) =>
        value.ToString().ToLowerInvariant();
    private static IReadOnlyList<string> AllowedActions(AlertLifecycleState value) => value switch
    {
        AlertLifecycleState.Open => ["acknowledge", "dismiss", "resolve"],
        AlertLifecycleState.Acknowledged => ["dismiss", "resolve"],
        AlertLifecycleState.Dismissed or AlertLifecycleState.Resolved => ["reopen"],
        _ => [],
    };
    private static AlertCenterLifecycleTransition History(AlertLifecycleEvent value) => new(
        value.Revision,
        value.Action.ToString().ToLowerInvariant(),
        Wire(value.PreviousState),
        Wire(value.State),
        Timestamp(value.OccurredAt),
        value.Actor,
        value.ReasonCode,
        value.OldAlertId,
        value.NewAlertId,
        value.ResultCode);
    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private sealed record ReceiptAcquisition(
        AlertCenterReadStatusV2 Status,
        IReadOnlyList<AlertVersionedReceiptQueryItem> Items,
        bool Incomplete,
        string? CapReason);
    private sealed record CoverageAcquisition(
        AlertCenterReadStatusV2 Status,
        IReadOnlyList<AlertCenterCoverageItemV2> Items,
        bool Incomplete);
    private sealed record ProjectCostResult(
        AlertCenterReadStatusV2 Status,
        ProjectedItem? Item);
    private sealed record ProjectedItem(
        string AlertId,
        string Severity,
        string LastObservedAt,
        AlertCenterItemV2 Dto,
        bool IsCost,
        IReadOnlyList<AlertCenterCostMemberV2> Members);
    private sealed record PagePlan(
        int Start,
        int End,
        string? CursorAfter,
        AlertCenterSnapshotV2 Snapshot);
    private sealed class ProjectedItemComparer : IComparer<ProjectedItem>
    {
        public static ProjectedItemComparer Instance { get; } = new();
        public int Compare(ProjectedItem? left, ProjectedItem? right)
        {
            if (ReferenceEquals(left, right)) return 0;
            if (left is null) return -1;
            if (right is null) return 1;
            var severity = SeverityRank(left.Severity).CompareTo(SeverityRank(right.Severity));
            if (severity != 0) return severity;
            var observed = string.CompareOrdinal(right.LastObservedAt, left.LastObservedAt);
            return observed != 0 ? observed : string.CompareOrdinal(left.AlertId, right.AlertId);
        }
    }
}

namespace CopilotAgentObservability.ConfigCli;

internal static class DashboardDatasetGenerator
{
    public const string SchemaVersion = "sprint4-m2-v1";

    private static readonly DashboardDatasetParameters DefaultParameters = new(
        LongRunningTraceThresholdMs: 600000,
        LongRunningTurnThresholdMs: 300000,
        LongRunningToolThresholdMs: 120000,
        StuckSessionThresholdMs: 900000);

    private static readonly IReadOnlyDictionary<string, (decimal Input, decimal Output)> UnitPrices = new Dictionary<string, (decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-4.1-mini"] = (0.0000004m, 0.0000016m),
        ["gpt-4.1"] = (0.000002m, 0.000008m),
    };

    public static DashboardDataset Generate(
        IReadOnlyList<MeasurementInputRow> measurements,
        IReadOnlyList<DashboardRawOperation> rawOperations,
        IReadOnlyList<DiagnosisCandidateRow> diagnosisCandidates,
        IReadOnlyList<ImprovementCandidateRow> improvementCandidates,
        IReadOnlyList<AutoDecisionRow> autoDecisions,
        string timeBucketGranularity,
        DateTimeOffset generatedAtUtc)
    {
        var contextByTrace = measurements
            .Where(measurement => !string.IsNullOrWhiteSpace(measurement.Row.TraceId))
            .GroupBy(measurement => measurement.Row.TraceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var operationsByTrace = rawOperations
            .Where(operation => !string.IsNullOrWhiteSpace(operation.TraceId))
            .GroupBy(operation => operation.TraceId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var sensitiveTraces = CreateSensitiveTraceSet(diagnosisCandidates, improvementCandidates, autoDecisions);

        var runRows = measurements
            .Select(measurement => CreateRunRow(measurement, OperationsFor(measurement.Row.TraceId, operationsByTrace), sensitiveTraces, timeBucketGranularity, generatedAtUtc))
            .ToArray();
        var operationRows = measurements
            .SelectMany(measurement => CreateOperationRows(measurement, OperationsFor(measurement.Row.TraceId, operationsByTrace), sensitiveTraces, timeBucketGranularity, generatedAtUtc))
            .ToArray();
        var candidateRows = CreateCandidateRows(
            diagnosisCandidates,
            improvementCandidates,
            autoDecisions,
            contextByTrace,
            operationsByTrace,
            timeBucketGranularity,
            generatedAtUtc);
        var healthRows = CreateCollectionHealthRows(
            measurements,
            diagnosisCandidates,
            improvementCandidates,
            autoDecisions,
            timeBucketGranularity,
            generatedAtUtc);

        return new DashboardDataset(
            SchemaVersion,
            generatedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            timeBucketGranularity,
            DefaultParameters,
            runRows,
            operationRows,
            candidateRows,
            healthRows);
    }

    private static DashboardRunSummaryRow CreateRunRow(
        MeasurementInputRow measurement,
        IReadOnlyList<DashboardRawOperation> operations,
        ISet<string> sensitiveTraces,
        string timeBucketGranularity,
        DateTimeOffset fallbackTimestampUtc)
    {
        var row = measurement.Row;
        var model = operations.Select(operation => operation.Model).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var ttft = operations
            .Where(operation => operation.OperationKind == "llm" && operation.TtftMs.HasValue)
            .OrderBy(operation => operation.TtftMs!.Value)
            .FirstOrDefault();
        var cost = EstimateCost(model, row.InputTokens, row.OutputTokens);

        return new DashboardRunSummaryRow(
            SchemaVersion,
            BucketStart(operations, timeBucketGranularity, fallbackTimestampUtc),
            timeBucketGranularity,
            row.TraceId,
            row.TraceId,
            measurement.SourceRecordRef,
            FirstUserId(operations),
            FirstUserEmail(operations),
            row.ClientKind,
            row.ExperimentId,
            row.ExperimentCondition,
            row.TaskId,
            row.TaskCategory,
            row.TaskRunIndex,
            row.PromptVersion,
            row.AgentVariant,
            null,
            null,
            row.RepoSnapshot,
            model,
            DeriveRunStatus(row),
            row.SuccessStatus,
            row.DurationMs,
            ttft?.TtftMs,
            ttft?.TtftSource ?? "unavailable",
            row.InputTokens,
            row.OutputTokens,
            row.TotalTokens,
            row.TurnCount,
            operations.Count(operation => operation.OperationKind == "llm"),
            row.ToolCallCount,
            row.ErrorCount,
            cost.Value,
            cost.Source,
            row.DurationMs >= DefaultParameters.LongRunningTraceThresholdMs,
            row.DurationMs >= DefaultParameters.StuckSessionThresholdMs,
            row.TraceId is not null && sensitiveTraces.Contains(row.TraceId),
            row.TraceId is null ? null : $"trace:{row.TraceId}");
    }

    private static IReadOnlyList<DashboardOperationSummaryRow> CreateOperationRows(
        MeasurementInputRow measurement,
        IReadOnlyList<DashboardRawOperation> operations,
        ISet<string> sensitiveTraces,
        string timeBucketGranularity,
        DateTimeOffset fallbackTimestampUtc)
    {
        if (operations.Count == 0)
        {
            return [];
        }

        var row = measurement.Row;
        return operations
            .GroupBy(operation => new
            {
                operation.OperationKind,
                operation.ToolName,
                operation.Model,
                operation.Status,
                operation.PermissionResult,
            })
            .Select(group =>
            {
                var durations = group.Select(operation => operation.DurationMs).Where(value => value.HasValue).Select(value => value!.Value).Order().ToArray();
                int? totalDuration = durations.Length == 0 ? null : durations.Sum();
                var approvalWait = SumNullable(group.Select(operation => operation.ApprovalWaitMs));

                return new DashboardOperationSummaryRow(
                    SchemaVersion,
                    BucketStart(operations, timeBucketGranularity, fallbackTimestampUtc),
                    timeBucketGranularity,
                    row.TraceId,
                    FirstUserId(operations),
                    FirstUserEmail(operations),
                    row.ClientKind,
                    row.ExperimentId,
                    row.ExperimentCondition,
                    row.TaskId,
                    row.RepoSnapshot,
                    group.Key.OperationKind,
                    group.Key.ToolName,
                    group.Key.Model,
                    group.Key.Status,
                    group.Count(),
                    group.Sum(operation => operation.ErrorCount),
                    group.Sum(operation => operation.TimeoutCount),
                    group.Sum(operation => operation.RetryCount),
                    totalDuration,
                    Percentile(durations, 0.50),
                    Percentile(durations, 0.95),
                    approvalWait,
                    group.Key.PermissionResult,
                    group.Sum(operation => operation.SubagentCallCount),
                    group.Sum(operation => operation.NestedAgentCallCount),
                    group.Key.OperationKind == "tool" && totalDuration >= DefaultParameters.LongRunningToolThresholdMs,
                    row.TraceId is not null && sensitiveTraces.Contains(row.TraceId),
                    row.TraceId is null ? null : $"trace:{row.TraceId}");
            })
            .ToArray();
    }

    private static IReadOnlyList<DashboardCandidateSummaryRow> CreateCandidateRows(
        IReadOnlyList<DiagnosisCandidateRow> diagnosisCandidates,
        IReadOnlyList<ImprovementCandidateRow> improvementCandidates,
        IReadOnlyList<AutoDecisionRow> autoDecisions,
        IReadOnlyDictionary<string, MeasurementInputRow> contextByTrace,
        IReadOnlyDictionary<string, DashboardRawOperation[]> operationsByTrace,
        string timeBucketGranularity,
        DateTimeOffset fallbackTimestampUtc)
    {
        var rows = new List<DashboardCandidateSummaryRow>();
        rows.AddRange(diagnosisCandidates.Select(candidate =>
        {
            var context = FindContext(candidate.TraceId, contextByTrace);
            return CreateCandidateRow(
                context,
                OperationsFor(candidate.TraceId, operationsByTrace),
                timeBucketGranularity,
                fallbackTimestampUtc,
                candidate.TraceId,
                candidateKind: "diagnosis",
                diagnosisCandidateId: candidate.DiagnosisCandidateId,
                improvementCandidateId: null,
                autoDecisionId: null,
                candidateRule: candidate.RuleId,
                failureCategoryId: candidate.FailureCategoryId,
                antiPatternId: candidate.AntiPatternId,
                severity: candidate.Severity,
                improvementTarget: candidate.RecommendedImprovementTarget,
                proposedChangeKind: null,
                candidateStatus: candidate.CandidateStatus,
                decisionStatus: null,
                evidenceRef: SanitizeEvidenceRef(candidate.EvidenceRef),
                sensitiveBundlePresent: !string.IsNullOrWhiteSpace(candidate.SensitiveBundlePath));
        }));
        rows.AddRange(improvementCandidates.Select(candidate =>
        {
            var context = FindContext(candidate.TraceId, contextByTrace);
            return CreateCandidateRow(
                context,
                OperationsFor(candidate.TraceId, operationsByTrace),
                timeBucketGranularity,
                fallbackTimestampUtc,
                candidate.TraceId,
                candidateKind: "improvement",
                diagnosisCandidateId: candidate.SourceDiagnosisCandidateId,
                improvementCandidateId: candidate.ImprovementCandidateId,
                autoDecisionId: null,
                candidateRule: null,
                failureCategoryId: candidate.FailureCategoryId,
                antiPatternId: candidate.AntiPatternId,
                severity: candidate.Severity,
                improvementTarget: candidate.ImprovementTarget,
                proposedChangeKind: candidate.ProposedChangeKind,
                candidateStatus: candidate.CandidateStatus,
                decisionStatus: null,
                evidenceRef: SanitizeEvidenceRef(candidate.EvidenceRef),
                sensitiveBundlePresent: !string.IsNullOrWhiteSpace(candidate.SensitiveBundlePath));
        }));
        rows.AddRange(autoDecisions.Select(decision =>
        {
            var context = FindContext(decision.TraceId, contextByTrace);
            return CreateCandidateRow(
                context,
                OperationsFor(decision.TraceId, operationsByTrace),
                timeBucketGranularity,
                fallbackTimestampUtc,
                decision.TraceId,
                candidateKind: "auto-decision",
                diagnosisCandidateId: decision.SourceDiagnosisCandidateId,
                improvementCandidateId: decision.SourceImprovementCandidateId,
                autoDecisionId: decision.AutoDecisionId,
                candidateRule: decision.DecisionRuleId,
                failureCategoryId: null,
                antiPatternId: null,
                severity: null,
                improvementTarget: decision.ImplementationTarget,
                proposedChangeKind: decision.ImplementationTarget,
                candidateStatus: null,
                decisionStatus: decision.DecisionStatus,
                evidenceRef: null,
                sensitiveBundlePresent: decision.SensitiveContentIncluded || !string.IsNullOrWhiteSpace(decision.SensitiveBundlePath));
        }));

        return rows;
    }

    private static DashboardCandidateSummaryRow CreateCandidateRow(
        MeasurementInputRow? context,
        IReadOnlyList<DashboardRawOperation> operations,
        string timeBucketGranularity,
        DateTimeOffset fallbackTimestampUtc,
        string? sourceTraceId,
        string candidateKind,
        string? diagnosisCandidateId,
        string? improvementCandidateId,
        string? autoDecisionId,
        string? candidateRule,
        string? failureCategoryId,
        string? antiPatternId,
        string? severity,
        string? improvementTarget,
        string? proposedChangeKind,
        string? candidateStatus,
        string? decisionStatus,
        string? evidenceRef,
        bool sensitiveBundlePresent)
    {
        var row = context?.Row;
        var traceId = row?.TraceId ?? sourceTraceId;
        return new DashboardCandidateSummaryRow(
            SchemaVersion,
            BucketStart(operations, timeBucketGranularity, fallbackTimestampUtc),
            timeBucketGranularity,
            traceId,
            FirstUserId(operations),
            FirstUserEmail(operations),
            row?.ClientKind,
            row?.ExperimentId,
            row?.ExperimentCondition,
            row?.TaskId,
            row?.RepoSnapshot,
            candidateKind,
            diagnosisCandidateId,
            improvementCandidateId,
            autoDecisionId,
            null,
            candidateRule,
            failureCategoryId,
            antiPatternId,
            severity,
            improvementTarget,
            proposedChangeKind,
            candidateStatus,
            decisionStatus,
            null,
            null,
            null,
            evidenceRef,
            sensitiveBundlePresent,
            autoDecisionId is not null
                ? $"auto-decision:{autoDecisionId}"
                : improvementCandidateId is not null
                    ? $"improvement-candidate:{improvementCandidateId}"
                    : diagnosisCandidateId is not null
                        ? $"diagnosis-candidate:{diagnosisCandidateId}"
                        : null);
    }

    private static IReadOnlyList<DashboardCollectionHealthRow> CreateCollectionHealthRows(
        IReadOnlyList<MeasurementInputRow> measurements,
        IReadOnlyList<DiagnosisCandidateRow> diagnosisCandidates,
        IReadOnlyList<ImprovementCandidateRow> improvementCandidates,
        IReadOnlyList<AutoDecisionRow> autoDecisions,
        string timeBucketGranularity,
        DateTimeOffset fallbackTimestampUtc)
    {
        var rows = new List<DashboardCollectionHealthRow>();
        var candidateTraceIds = diagnosisCandidates.Select(candidate => candidate.TraceId)
            .Concat(improvementCandidates.Select(candidate => candidate.TraceId))
            .Concat(autoDecisions.Select(decision => decision.TraceId))
            .Where(traceId => !string.IsNullOrWhiteSpace(traceId))
            .ToArray();
        var measurementTraceIds = measurements
            .Select(measurement => measurement.Row.TraceId)
            .Where(traceId => !string.IsNullOrWhiteSpace(traceId))
            .ToHashSet(StringComparer.Ordinal);
        var mappingFailures = candidateTraceIds.Count(traceId => !measurementTraceIds.Contains(traceId!));

        foreach (var measurement in measurements)
        {
            var row = measurement.Row;
            foreach (var missingAttribute in MissingRequiredAttributes(row))
            {
                rows.Add(CreateHealthRow(
                    measurement,
                    timeBucketGranularity,
                    fallbackTimestampUtc,
                    "missing-required-attribute",
                    "warning",
                    missingAttribute,
                    null,
                    null,
                    0,
                    0,
                    0,
                    1,
                    measurement.SourceRecordRef));
            }

            var unknownSpanCount = row.UnknownSpansJson?.Count;
            var unknownAttributeCount = CountUnknownAttributes(row.UnknownAttributesJson);
            if ((unknownSpanCount ?? 0) > 0 || (unknownAttributeCount ?? 0) > 0)
            {
                rows.Add(CreateHealthRow(
                    measurement,
                    timeBucketGranularity,
                    fallbackTimestampUtc,
                    "unknown-telemetry",
                    "warning",
                    null,
                    unknownSpanCount,
                    unknownAttributeCount,
                    0,
                    0,
                    0,
                    1,
                    measurement.SourceRecordRef));
            }
        }

        if (mappingFailures > 0)
        {
            rows.Add(new DashboardCollectionHealthRow(
                SchemaVersion,
                BucketStart([], timeBucketGranularity, fallbackTimestampUtc),
                timeBucketGranularity,
                "candidate-inputs",
                null,
                null,
                null,
                null,
                null,
                "candidate-measurement-mapping",
                "warning",
                null,
                null,
                null,
                0,
                mappingFailures,
                0,
                mappingFailures,
                "candidate-inputs"));
        }

        return rows;
    }

    private static DashboardCollectionHealthRow CreateHealthRow(
        MeasurementInputRow measurement,
        string timeBucketGranularity,
        DateTimeOffset fallbackTimestampUtc,
        string healthCheckKind,
        string healthStatus,
        string? missingAttributeName,
        int? unknownSpanCount,
        int? unknownAttributeCount,
        int? normalizationFailureCount,
        int? mappingFailureCount,
        int? candidateGenerationFailureCount,
        int affectedRecordCount,
        string? detailsRef)
    {
        var row = measurement.Row;
        return new DashboardCollectionHealthRow(
            SchemaVersion,
            BucketStart([], timeBucketGranularity, fallbackTimestampUtc),
            timeBucketGranularity,
            measurement.SourceRecordRef,
            row.TraceId,
            null,
            null,
            row.ClientKind,
            row.ExperimentId,
            healthCheckKind,
            healthStatus,
            missingAttributeName,
            unknownSpanCount,
            unknownAttributeCount,
            normalizationFailureCount,
            mappingFailureCount,
            candidateGenerationFailureCount,
            affectedRecordCount,
            detailsRef);
    }

    private static string DeriveRunStatus(MeasurementRow row)
    {
        if (string.Equals(row.SuccessStatus, "excluded", StringComparison.OrdinalIgnoreCase))
        {
            return "excluded";
        }

        return row.ErrorCount > 0
            ? "error"
            : "success";
    }

    private static (decimal? Value, string Source) EstimateCost(string? model, int? inputTokens, int? outputTokens)
    {
        if (model is null || !UnitPrices.TryGetValue(model, out var unitPrice))
        {
            return (null, "unavailable-unit-price");
        }

        if (!inputTokens.HasValue && !outputTokens.HasValue)
        {
            return (null, "not-calculated");
        }

        var cost = ((inputTokens ?? 0) * unitPrice.Input) + ((outputTokens ?? 0) * unitPrice.Output);
        return (decimal.Round(cost, 8, MidpointRounding.AwayFromZero), "unit-price-table");
    }

    private static string BucketStart(
        IReadOnlyList<DashboardRawOperation> operations,
        string timeBucketGranularity,
        DateTimeOffset fallbackTimestampUtc)
    {
        var timestamp = operations.Select(operation => operation.StartedAtUtc).FirstOrDefault(value => value.HasValue)
            ?? fallbackTimestampUtc;
        var utc = timestamp.ToUniversalTime();

        return timeBucketGranularity switch
        {
            "hour" => new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
            "week" => StartOfWeek(utc).ToString("O", CultureInfo.InvariantCulture),
            _ => new DateTimeOffset(utc.Year, utc.Month, utc.Day, 0, 0, 0, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture),
        };
    }

    private static DateTimeOffset StartOfWeek(DateTimeOffset value)
    {
        var daysSinceMonday = ((int)value.DayOfWeek + 6) % 7;
        return new DateTimeOffset(value.Year, value.Month, value.Day, 0, 0, 0, TimeSpan.Zero).AddDays(-daysSinceMonday);
    }

    private static IReadOnlyList<DashboardRawOperation> OperationsFor(
        string? traceId,
        IReadOnlyDictionary<string, DashboardRawOperation[]> operationsByTrace)
    {
        return traceId is not null && operationsByTrace.TryGetValue(traceId, out var operations)
            ? operations
            : [];
    }

    private static MeasurementInputRow? FindContext(string? traceId, IReadOnlyDictionary<string, MeasurementInputRow> contextByTrace)
    {
        return traceId is not null && contextByTrace.TryGetValue(traceId, out var context)
            ? context
            : null;
    }

    private static string? FirstUserId(IReadOnlyList<DashboardRawOperation> operations)
    {
        return operations.Select(operation => operation.UserId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? FirstUserEmail(IReadOnlyList<DashboardRawOperation> operations)
    {
        return operations.Select(operation => operation.UserEmail).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static ISet<string> CreateSensitiveTraceSet(
        IReadOnlyList<DiagnosisCandidateRow> diagnosisCandidates,
        IReadOnlyList<ImprovementCandidateRow> improvementCandidates,
        IReadOnlyList<AutoDecisionRow> autoDecisions)
    {
        return diagnosisCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.TraceId) && !string.IsNullOrWhiteSpace(candidate.SensitiveBundlePath))
            .Select(candidate => candidate.TraceId!)
            .Concat(improvementCandidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.TraceId) && !string.IsNullOrWhiteSpace(candidate.SensitiveBundlePath))
                .Select(candidate => candidate.TraceId!))
            .Concat(autoDecisions
                .Where(decision => !string.IsNullOrWhiteSpace(decision.TraceId) && (decision.SensitiveContentIncluded || !string.IsNullOrWhiteSpace(decision.SensitiveBundlePath)))
                .Select(decision => decision.TraceId!))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> MissingRequiredAttributes(MeasurementRow row)
    {
        if (string.IsNullOrWhiteSpace(row.TraceId))
        {
            yield return "trace_id";
        }

        if (string.IsNullOrWhiteSpace(row.ClientKind))
        {
            yield return "client_kind";
        }

        if (string.IsNullOrWhiteSpace(row.ExperimentId))
        {
            yield return "experiment_id";
        }
    }

    private static int? CountUnknownAttributes(JsonObject? unknownAttributesJson)
    {
        if (unknownAttributesJson is null)
        {
            return null;
        }

        if (unknownAttributesJson.TryGetPropertyValue("resourceAttributes", out var resourceAttributes)
            && resourceAttributes is JsonObject resourceAttributesObject)
        {
            return resourceAttributesObject.Count;
        }

        return unknownAttributesJson.Count;
    }

    private static int? SumNullable(IEnumerable<int?> values)
    {
        var sum = 0;
        var hasValue = false;
        foreach (var value in values)
        {
            if (value.HasValue)
            {
                sum += value.Value;
                hasValue = true;
            }
        }

        return hasValue ? sum : null;
    }

    private static int? Percentile(IReadOnlyList<int> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return null;
        }

        var index = (int)Math.Ceiling(sortedValues.Count * percentile) - 1;
        index = Math.Clamp(index, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }

    private static string? SanitizeEvidenceRef(string? evidenceRef)
    {
        if (string.IsNullOrWhiteSpace(evidenceRef))
        {
            return null;
        }

        var trimmed = evidenceRef.Trim();

        // Reject obvious local filesystem paths so a sensitive bundle path (or
        // any other local path) placed in evidence_ref cannot leak into the
        // repository-safe dashboard dataset. Scheme-bearing refs (measurement:,
        // raw:, bundle:) and relative row refs are preserved.
        if (IsLocalPath(trimmed))
        {
            return null;
        }

        return trimmed.Replace('\\', '/');
    }

    private static bool IsLocalPath(string value)
    {
        // file: URI scheme.
        if (value.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Windows drive-letter absolute path, e.g. C:\... or C:/...
        if (value.Length >= 3
            && char.IsLetter(value[0])
            && value[1] == ':'
            && (value[2] == '\\' || value[2] == '/'))
        {
            return true;
        }

        // UNC path, e.g. \\server\share ...
        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            return true;
        }

        // Unix absolute path, e.g. /home/... or /tmp/...
        if (value.StartsWith("/", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}

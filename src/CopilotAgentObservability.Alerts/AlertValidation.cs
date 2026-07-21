using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Alerts;

internal static partial class AlertValidation
{
    private static readonly string[] RequiredSuppressionCodes = ["missing_required_capability", "rule_disabled", "source_not_applicable"];
    private static readonly HashSet<string> CompletenessReasonCodes = new(
    [
        "missing_native_session_id", "missing_trace_context", "trace_signal_disabled", "content_capture_disabled",
        "unsupported_source_version", "ingest_gap", "hook_only", "historical_summary_only", "unknown_span_kind",
        "schema_drift_detected", "planned_source_not_enabled",
    ], StringComparer.Ordinal);

    public static AlertContractException InvalidRegistry(string message) => new("invalid_rule_registry", message);

    public static bool IsToken(string? value) => value is not null && TokenRegex().IsMatch(value);

    public static bool IsOpaqueId(string? value) => value is not null
        && value.Length is > 0 and <= 256
        && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\' or '?' or '#');

    public static void ValidateDescriptor(AlertRuleDescriptor descriptor)
    {
        if (!IsToken(descriptor.RuleId) || !IsToken(descriptor.RuleVersion)
            || string.IsNullOrWhiteSpace(descriptor.Title) || descriptor.Title.Length > 160 || descriptor.Title.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(descriptor.Description) || descriptor.Description.Length > 500 || descriptor.Description.Any(char.IsControl)
            || !IsToken(descriptor.EvaluationWindow)
            || !AllUniqueTokens(descriptor.RequiredCapabilities)
            || !AllUniqueTokens(descriptor.GroupingKeys)
            || !AllUniqueTokens(descriptor.ApplicableSourceSurfaces)
            || !AllUniqueTokens(descriptor.SuppressionCodes)
            || descriptor.ApplicableSourceSurfaces.Count == 0
            || RequiredSuppressionCodes.Except(descriptor.SuppressionCodes, StringComparer.Ordinal).Any()
            || descriptor.Thresholds.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            throw InvalidRegistry("Rule descriptor is invalid.");
        }

        foreach (var threshold in descriptor.Thresholds)
        {
            if (!IsToken(threshold.Name) || !IsToken(threshold.Unit)
                || threshold.Minimum > threshold.Maximum
                || !InRange(threshold.WarningDefault, threshold)
                || !InRange(threshold.CriticalDefault, threshold)
                || !ValidRelationship(threshold.WarningDefault, threshold.CriticalDefault, threshold.Direction))
            {
                throw InvalidRegistry("Rule threshold is invalid.");
            }
        }
    }

    public static void ValidateSnapshot(AlertNormalizedSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != AlertContractVersions.Snapshot
            || !IsToken(snapshot.SourceSurface) || !IsToken(snapshot.SourceVersion)
            || !IsOpaqueId(snapshot.SessionId) || snapshot.TraceId is not null && !IsOpaqueId(snapshot.TraceId)
            || snapshot.FirstObservedAt > snapshot.LastObservedAt
            || !AllUniqueTokens(snapshot.CompletenessReasons) || snapshot.CompletenessReasons.Except(CompletenessReasonCodes, StringComparer.Ordinal).Any()
            || snapshot.Capabilities.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || snapshot.Capabilities.Any(item => !IsToken(item.Name))
            || snapshot.Signals.GroupBy(item => item.SignalId, StringComparer.Ordinal).Any(group => group.Count() != 1)
            || snapshot.Signals.GroupBy(item => item.Sequence).Any(group => group.Count() != 1)
            || snapshot.Signals.GroupBy(item => AlertCanonicalJson.EvidenceIdentity([item.Evidence]), StringComparer.Ordinal).Any(group => group.Count() != 1))
        {
            throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid.");
        }

        var signalIds = snapshot.Signals.Select(item => item.SignalId).ToHashSet(StringComparer.Ordinal);
        foreach (var signal in snapshot.Signals)
        {
            if (!IsOpaqueId(signal.SignalId) || signal.Sequence < 0
                || signal.ParentSignalId is not null && !IsOpaqueId(signal.ParentSignalId)
                || signal.ParentSignalId is not null && !signalIds.Contains(signal.ParentSignalId)
                || signal.ObservedAt < snapshot.FirstObservedAt || signal.ObservedAt > snapshot.LastObservedAt
                || signal.Metrics.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
                || signal.Metrics.Any(metric => !IsToken(metric.Name) || !IsToken(metric.Unit))
                || signal.ComparableKeys.GroupBy(item => item.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
                || signal.ComparableKeys.Any(key => !IsToken(key.Name) || !ValidComparableKey(key))
                || !ValidEvidence(signal.Evidence, snapshot))
            {
                throw new AlertContractException("invalid_snapshot", "Normalized alert signal is invalid.");
            }
        }
    }

    public static Dictionary<(string RuleId, string RuleVersion), ResolvedAlertRuleConfiguration> ResolveConfiguration(
        AlertRuleRegistry registry,
        AlertEngineConfiguration configuration)
    {
        if (configuration.SchemaVersion != AlertContractVersions.Configuration || !IsToken(configuration.ConfigurationVersion)
            || configuration.Rules.GroupBy(item => (item.RuleId, item.RuleVersion)).Any(group => group.Count() != 1))
        {
            throw InvalidConfiguration();
        }

        var configured = configuration.Rules.ToDictionary(item => (item.RuleId, item.RuleVersion));
        if (configured.Keys.Except(registry.Rules.Select(rule => (rule.Descriptor.RuleId, rule.Descriptor.RuleVersion))).Any())
        {
            throw InvalidConfiguration();
        }

        var result = new Dictionary<(string, string), ResolvedAlertRuleConfiguration>();
        foreach (var rule in registry.Rules)
        {
            var descriptor = rule.Descriptor;
            configured.TryGetValue((descriptor.RuleId, descriptor.RuleVersion), out var entry);
            if (entry is not null && (entry.SourceSurfaceAllowlist is not null
                && (!AllUniqueTokens(entry.SourceSurfaceAllowlist)
                    || entry.SourceSurfaceAllowlist.Except(descriptor.ApplicableSourceSurfaces, StringComparer.Ordinal).Any())))
            {
                throw InvalidConfiguration();
            }

            var thresholds = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var units = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var definition in descriptor.Thresholds)
            {
                var warningName = definition.Name + ".warning";
                var criticalName = definition.Name + ".critical";
                var warning = entry?.ThresholdOverrides.GetValueOrDefault(warningName, definition.WarningDefault) ?? definition.WarningDefault;
                var critical = entry?.ThresholdOverrides.GetValueOrDefault(criticalName, definition.CriticalDefault) ?? definition.CriticalDefault;
                if (!InRange(warning, definition) || !InRange(critical, definition) || !ValidRelationship(warning, critical, definition.Direction))
                {
                    throw InvalidConfiguration();
                }

                thresholds.Add(warningName, warning);
                thresholds.Add(criticalName, critical);
                units.Add(definition.Name, definition.Unit);
            }

            if (entry is not null && entry.ThresholdOverrides.Keys.Except(thresholds.Keys, StringComparer.Ordinal).Any())
            {
                throw InvalidConfiguration();
            }

            result.Add((descriptor.RuleId, descriptor.RuleVersion), new(
                entry?.Enabled ?? true,
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, decimal>(thresholds),
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(units),
                entry?.SourceSurfaceAllowlist is null ? null : Array.AsReadOnly(entry.SourceSurfaceAllowlist.Order(StringComparer.Ordinal).ToArray())));
        }

        return result;
    }

    public static bool IsValidMatchShape(AlertRuleMatch? match, AlertNormalizedSnapshot snapshot)
    {
        if (match is null || match.Evidence.Count == 0 || match.FirstObservedAt > match.LastObservedAt
            || match.FirstObservedAt < snapshot.FirstObservedAt || match.LastObservedAt > snapshot.LastObservedAt
            || match.ObservedValues.Count == 0
            || match.ObservedValues.GroupBy(item => (item.Name, item.Unit)).Any(group => group.Count() != 1)
            || match.ObservedValues.Any(item => !IsToken(item.Name) || !IsToken(item.Unit))
            || !Enum.IsDefined(match.Severity))
        {
            return false;
        }

        return true;
    }

    public static bool HasExactSnapshotEvidence(AlertRuleMatch match, AlertNormalizedSnapshot snapshot)
    {
        var snapshotEvidence = snapshot.Signals.Select(signal => signal.Evidence).ToHashSet();
        return match.Evidence.All(reference => snapshotEvidence.Contains(reference) && ValidEvidence(reference, snapshot));
    }

    public static bool IsDeclaredRuleSuppression(AlertRuleDescriptor descriptor, AlertRuleSuppression? suppression) =>
        suppression is not null
        && IsToken(suppression.Code)
        && descriptor.SuppressionCodes.Contains(suppression.Code, StringComparer.Ordinal)
        && !RequiredSuppressionCodes.Contains(suppression.Code, StringComparer.Ordinal);

    private static bool ValidEvidence(AlertEvidenceReference reference, AlertNormalizedSnapshot snapshot) =>
        IsOpaqueId(reference.EvidenceId)
        && IsOpaqueId(reference.SessionId)
        && reference.SessionId == snapshot.SessionId
        && reference.TraceId == snapshot.TraceId
        && OptionalId(reference.TraceId) && OptionalId(reference.SpanId) && OptionalId(reference.TurnId)
        && OptionalId(reference.EventId) && OptionalId(reference.ToolCallId)
        && (reference.Kind switch
        {
            AlertEvidenceKind.Session => true,
            AlertEvidenceKind.Trace => reference.TraceId is not null,
            AlertEvidenceKind.Span => reference.TraceId is not null && reference.SpanId is not null,
            AlertEvidenceKind.Turn => reference.TurnId is not null,
            AlertEvidenceKind.Event => reference.EventId is not null,
            AlertEvidenceKind.ToolCall => reference.ToolCallId is not null,
            _ => false,
        })
        && reference.ObservedAt >= snapshot.FirstObservedAt && reference.ObservedAt <= snapshot.LastObservedAt;

    private static bool ValidComparableKey(AlertComparableKey key) => key.Kind switch
    {
        AlertComparableKeyKind.MetadataToken => IsToken(key.Value),
        AlertComparableKeyKind.SensitiveHmac => SensitiveHmacRegex().IsMatch(key.Value),
        _ => false,
    };

    private static bool OptionalId(string? value) => value is null || IsOpaqueId(value);
    private static bool AllUniqueTokens(IEnumerable<string> values) => values.All(IsToken) && values.Distinct(StringComparer.Ordinal).Count() == values.Count();
    private static bool InRange(decimal value, AlertThresholdDefinition definition) => value >= definition.Minimum && value <= definition.Maximum;
    private static bool ValidRelationship(decimal warning, decimal critical, AlertThresholdDirection direction) =>
        direction == AlertThresholdDirection.HigherIsWorse ? warning <= critical : warning >= critical;
    private static AlertContractException InvalidConfiguration() => new("invalid_configuration", "Alert engine configuration is invalid.");

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenRegex();

    [GeneratedRegex("^hmac-sha256-v1:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveHmacRegex();
}

internal sealed record ResolvedAlertRuleConfiguration(
    bool Enabled,
    IReadOnlyDictionary<string, decimal> Thresholds,
    IReadOnlyDictionary<string, string> Units,
    IReadOnlyList<string>? SourceSurfaceAllowlist);

internal static class AlertFreezer
{
    public static AlertRuleDescriptor Descriptor(AlertRuleDescriptor value) => value with
    {
        RequiredCapabilities = Array.AsReadOnly(value.RequiredCapabilities.ToArray()),
        GroupingKeys = Array.AsReadOnly(value.GroupingKeys.ToArray()),
        Thresholds = Array.AsReadOnly(value.Thresholds.Select(item => item with { }).ToArray()),
        SuppressionCodes = Array.AsReadOnly(value.SuppressionCodes.ToArray()),
        ApplicableSourceSurfaces = Array.AsReadOnly(value.ApplicableSourceSurfaces.ToArray()),
    };

    public static AlertNormalizedSnapshot Snapshot(AlertNormalizedSnapshot value) => value with
    {
        CompletenessReasons = Array.AsReadOnly(AlertCanonicalOrdering.CompletenessReasons(value.CompletenessReasons).ToArray()),
        Capabilities = Array.AsReadOnly(value.Capabilities.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => item with { }).ToArray()),
        Signals = Array.AsReadOnly(value.Signals.OrderBy(item => item.Sequence).ThenBy(item => item.ObservedAt).ThenBy(item => item.SignalId, StringComparer.Ordinal)
            .Select(signal => signal with
            {
                Metrics = Array.AsReadOnly(signal.Metrics.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => item with { }).ToArray()),
                ComparableKeys = Array.AsReadOnly(signal.ComparableKeys.OrderBy(item => item.Name, StringComparer.Ordinal).Select(item => item with { }).ToArray()),
                Evidence = signal.Evidence with { },
            }).ToArray()),
    };
}

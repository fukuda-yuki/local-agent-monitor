using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CopilotAgentObservability.Alerts;

public static class AlertCanonicalJsonV2
{
    public static byte[] SerializeSnapshot(AlertNormalizedSnapshotV2 snapshot) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", snapshot.SchemaVersion);
        writer.WriteString("context_kind", snapshot.ContextKind);
        writer.WriteString("source_surface", snapshot.SourceSurface);
        writer.WriteString("source_version", snapshot.SourceVersion);
        writer.WriteString("acquisition_state", AlertWireV2.Acquisition(snapshot.AcquisitionState));
        Strings(writer, "acquisition_reasons", snapshot.AcquisitionReasons);
        writer.WriteString("aggregate_state", AlertWireV2.Aggregate(snapshot.AggregateState));
        writer.WriteString("eligibility_digest", snapshot.EligibilityDigest);
        NullableNumber(writer, "eligible_count", snapshot.EligibleCount);
        NullableNumber(writer, "eligible_lower_bound", snapshot.EligibleLowerBound);
        writer.WritePropertyName("scope");
        WriteScope(writer, snapshot.Scope);
        NullableString(writer, "currency", snapshot.Currency);
        NullableDecimal(writer, "amount", snapshot.Amount);
        NullableNumber(writer, "estimated_count", snapshot.EstimatedCount);
        NullableNumber(writer, "partial_count", snapshot.PartialCount);
        NullableNumber(writer, "not_estimable_count", snapshot.NotEstimableCount);
        NullableNumber(writer, "missing_count", snapshot.MissingCount);
        NullableNumber(writer, "failed_count", snapshot.FailedCount);
        NullableNumber(writer, "unavailable_count", snapshot.UnavailableCount);
        NullableNumber(writer, "stale_count", snapshot.StaleCount);
        NullableNumber(writer, "coverage_numerator", snapshot.CoverageNumerator);
        NullableNumber(writer, "coverage_denominator", snapshot.CoverageDenominator);
        NullableNumber(writer, "coverage_basis_points", snapshot.CoverageBasisPoints);
        writer.WritePropertyName("members");
        writer.WriteStartArray();
        foreach (var member in snapshot.Members) WriteMember(writer, member);
        writer.WriteEndArray();
        writer.WritePropertyName("evidence");
        writer.WriteStartArray();
        foreach (var evidence in snapshot.Evidence) WriteEvidence(writer, evidence);
        writer.WriteEndArray();
        writer.WriteString("completeness", AlertWireV2.Completeness(snapshot.Completeness));
        Strings(writer, "completeness_reasons", snapshot.CompletenessReasons);
        NullableTimestamp(writer, "first_observed_at", snapshot.FirstObservedAt);
        NullableTimestamp(writer, "last_observed_at", snapshot.LastObservedAt);
        writer.WriteEndObject();
    });

    public static byte[] SerializeConfiguration(AlertEngineConfigurationV2 configuration) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", configuration.SchemaVersion);
        writer.WriteString("configuration_version", configuration.ConfigurationVersion);
        writer.WriteString("source_cost_configuration_id", configuration.SourceCostConfigurationId);
        writer.WriteNumber("source_configuration_head_revision", configuration.SourceConfigurationHeadRevision);
        writer.WriteString("source_configuration_catalog_sha256", configuration.SourceConfigurationCatalogSha256);
        writer.WritePropertyName("rules");
        writer.WriteStartArray();
        foreach (var rule in configuration.Rules)
        {
            writer.WriteStartObject();
            writer.WriteString("rule_id", rule.RuleId);
            writer.WriteString("rule_version", rule.RuleVersion);
            writer.WriteBoolean("enabled", rule.Enabled);
            writer.WriteString("currency", rule.Currency);
            Decimal(writer, "warning_threshold", rule.WarningThreshold);
            Decimal(writer, "critical_threshold", rule.CriticalThreshold);
            writer.WriteNumber("minimum_coverage_basis_points", rule.MinimumCoverageBasisPoints);
            writer.WriteString("scope_kind", AlertWireV2.ScopeKind(rule.ScopeKind));
            NullableNumber(writer, "window_days", rule.WindowDays);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static byte[] SerializeEvaluation(AlertEvaluationResultV2 evaluation) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", evaluation.SchemaVersion);
        writer.WriteString("evaluation_id", evaluation.EvaluationId);
        writer.WriteString("input_hash", evaluation.InputHash);
        writer.WriteString("configuration_version", evaluation.ConfigurationVersion);
        writer.WriteString("configuration_hash", evaluation.ConfigurationHash);
        writer.WriteString("selected_rule_id", evaluation.SelectedRuleId);
        writer.WriteString("selected_rule_version", evaluation.SelectedRuleVersion);
        writer.WriteString("source_cost_configuration_id", evaluation.SourceCostConfigurationId);
        writer.WriteNumber("source_configuration_head_revision", evaluation.SourceConfigurationHeadRevision);
        writer.WriteString("source_configuration_catalog_sha256", evaluation.SourceConfigurationCatalogSha256);
        writer.WriteString("scope_kind", AlertWireV2.ScopeKind(evaluation.ScopeKind));
        writer.WriteString("scope_id", evaluation.ScopeId);
        NullableTimestamp(writer, "scope_start_utc", evaluation.ScopeStartUtc);
        NullableTimestamp(writer, "scope_end_utc", evaluation.ScopeEndUtc);
        writer.WriteString("eligibility_digest", evaluation.EligibilityDigest);
        NullableString(writer, "currency", evaluation.Currency);
        writer.WriteString("aggregate_state", AlertWireV2.Aggregate(evaluation.AggregateState));
        NullableNumber(writer, "eligible_count", evaluation.EligibleCount);
        NullableNumber(writer, "estimated_count", evaluation.EstimatedCount);
        NullableNumber(writer, "partial_count", evaluation.PartialCount);
        NullableNumber(writer, "not_estimable_count", evaluation.NotEstimableCount);
        NullableNumber(writer, "missing_count", evaluation.MissingCount);
        NullableNumber(writer, "failed_count", evaluation.FailedCount);
        NullableNumber(writer, "unavailable_count", evaluation.UnavailableCount);
        NullableNumber(writer, "stale_count", evaluation.StaleCount);
        NullableNumber(writer, "coverage_basis_points", evaluation.CoverageBasisPoints);
        NullableTimestamp(writer, "first_observed_at", evaluation.FirstObservedAt);
        NullableTimestamp(writer, "last_observed_at", evaluation.LastObservedAt);
        writer.WritePropertyName("receipts");
        writer.WriteStartArray();
        foreach (var receipt in evaluation.Receipts) WriteReceipt(writer, receipt, includeAlertId: true);
        writer.WriteEndArray();
        writer.WritePropertyName("suppressions");
        writer.WriteStartArray();
        foreach (var suppression in evaluation.Suppressions) WriteSuppression(writer, suppression);
        writer.WriteEndArray();
        writer.WritePropertyName("rejected_matches");
        writer.WriteStartArray();
        foreach (var rejected in evaluation.RejectedMatches)
        {
            writer.WriteStartObject();
            writer.WriteString("rule_id", rejected.RuleId);
            writer.WriteString("rule_version", rejected.RuleVersion);
            writer.WriteString("code", rejected.Code);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static byte[] SerializeReceipt(AlertReceiptV2 receipt) =>
        Write(writer => WriteReceipt(writer, receipt, includeAlertId: true));

    public static byte[] SerializeSuppression(AlertSuppressionV2 suppression) =>
        Write(writer => WriteSuppression(writer, suppression));

    internal static byte[] SerializeReceiptIdentityProjection(AlertReceiptV2 receipt) =>
        Write(writer => WriteReceipt(writer, receipt, includeAlertId: false));

    internal static void WriteScope(Utf8JsonWriter writer, AlertCostScopeV2 scope)
    {
        writer.WriteStartObject();
        writer.WriteString("scope_id", scope.ScopeId);
        writer.WriteString("kind", AlertWireV2.ScopeKind(scope.Kind));
        NullableTimestamp(writer, "window_start_utc", scope.WindowStartUtc);
        NullableTimestamp(writer, "window_end_utc", scope.WindowEndUtc);
        Strings(writer, "session_ids", scope.SessionIds);
        writer.WriteEndObject();
    }

    internal static void WriteMember(Utf8JsonWriter writer, AlertCostMemberV2 member)
    {
        writer.WriteStartObject();
        writer.WriteString("session_id", member.SessionId);
        writer.WriteString("session_effective_at_utc", AlertWireV2.Timestamp(member.SessionEffectiveAtUtc));
        writer.WriteString("session_updated_at_utc", AlertWireV2.Timestamp(member.SessionUpdatedAtUtc));
        writer.WriteString("source_surface", member.SourceSurface);
        writer.WriteString("source_application_version", member.SourceApplicationVersion);
        writer.WriteString("state", AlertWireV2.MemberState(member.State));
        writer.WriteNumber("attempt_revision", member.AttemptRevision);
        NullableString(writer, "attempt_result_kind", member.AttemptResultKind is null ? null : AlertWireV2.AttemptResult(member.AttemptResultKind.Value));
        NullableString(writer, "attempt_result_code", member.AttemptResultCode);
        NullableNumber(writer, "head_revision", member.HeadRevision);
        NullableString(writer, "estimate_id", member.EstimateId);
        NullableTimestamp(writer, "estimate_calculation_time_utc", member.EstimateCalculationTimeUtc);
        NullableString(writer, "catalog_sha256", member.CatalogSha256);
        NullableString(writer, "registry_version", member.RegistryVersion);
        NullableString(writer, "provider", member.Provider);
        NullableString(writer, "model", member.Model);
        NullableString(writer, "billing_mode", member.BillingMode);
        NullableDecimal(writer, "amount", member.Amount);
        NullableString(writer, "currency", member.Currency);
        writer.WriteEndObject();
    }

    internal static void WriteEvidence(Utf8JsonWriter writer, AlertEvidenceReferenceV2 evidence)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", AlertWireV2.EvidenceKind(evidence.Kind));
        writer.WriteString("evidence_id", evidence.EvidenceId);
        writer.WriteString("session_id", evidence.SessionId);
        writer.WriteString("observed_at_utc", AlertWireV2.Timestamp(evidence.ObservedAtUtc));
        writer.WriteEndObject();
    }

    private static void WriteReceipt(Utf8JsonWriter writer, AlertReceiptV2 receipt, bool includeAlertId)
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", receipt.SchemaVersion);
        writer.WriteString("sanitized_export_profile", receipt.SanitizedExportProfile);
        if (includeAlertId) writer.WriteString("alert_id", receipt.AlertId);
        writer.WriteString("evaluation_id", receipt.EvaluationId);
        writer.WriteString("rule_id", receipt.RuleId);
        writer.WriteString("rule_version", receipt.RuleVersion);
        writer.WriteString("severity", AlertWireV2.Severity(receipt.Severity));
        writer.WriteString("initial_state", "open");
        writer.WriteString("source_surface", receipt.SourceSurface);
        writer.WriteString("source_version", receipt.SourceVersion);
        writer.WritePropertyName("scope");
        WriteScope(writer, receipt.Scope);
        writer.WritePropertyName("evidence");
        writer.WriteStartArray();
        foreach (var evidence in receipt.Evidence) WriteEvidence(writer, evidence);
        writer.WriteEndArray();
        writer.WriteString("currency", receipt.Currency);
        writer.WriteString("aggregate_state", AlertWireV2.Aggregate(receipt.AggregateState));
        Decimal(writer, "observed_amount", receipt.ObservedAmount);
        Decimal(writer, "warning_threshold", receipt.WarningThreshold);
        Decimal(writer, "critical_threshold", receipt.CriticalThreshold);
        writer.WriteNumber("eligible_count", receipt.EligibleCount);
        writer.WriteNumber("estimated_count", receipt.EstimatedCount);
        writer.WriteNumber("partial_count", receipt.PartialCount);
        writer.WriteNumber("not_estimable_count", receipt.NotEstimableCount);
        writer.WriteNumber("missing_count", receipt.MissingCount);
        writer.WriteNumber("failed_count", receipt.FailedCount);
        writer.WriteNumber("unavailable_count", receipt.UnavailableCount);
        writer.WriteNumber("stale_count", receipt.StaleCount);
        writer.WriteNumber("coverage_numerator", receipt.CoverageNumerator);
        writer.WriteNumber("coverage_denominator", receipt.CoverageDenominator);
        writer.WriteNumber("coverage_basis_points", receipt.CoverageBasisPoints);
        writer.WritePropertyName("members");
        writer.WriteStartArray();
        foreach (var member in receipt.Members) WriteMember(writer, member);
        writer.WriteEndArray();
        writer.WriteString("source_cost_configuration_id", receipt.SourceCostConfigurationId);
        writer.WriteNumber("source_configuration_head_revision", receipt.SourceConfigurationHeadRevision);
        writer.WriteString("source_configuration_catalog_sha256", receipt.SourceConfigurationCatalogSha256);
        writer.WriteString("configuration_version", receipt.ConfigurationVersion);
        writer.WriteString("configuration_hash", receipt.ConfigurationHash);
        writer.WriteString("completeness", AlertWireV2.Completeness(receipt.Completeness));
        Strings(writer, "completeness_reasons", receipt.CompletenessReasons);
        writer.WriteString("first_observed_at", AlertWireV2.Timestamp(receipt.FirstObservedAt));
        writer.WriteString("last_observed_at", AlertWireV2.Timestamp(receipt.LastObservedAt));
        writer.WriteString("input_hash", receipt.InputHash);
        writer.WriteString("summary", receipt.Summary);
        writer.WriteEndObject();
    }

    private static void WriteSuppression(Utf8JsonWriter writer, AlertSuppressionV2 suppression)
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", suppression.SchemaVersion);
        writer.WriteString("evaluation_id", suppression.EvaluationId);
        writer.WriteString("rule_id", suppression.RuleId);
        writer.WriteString("rule_version", suppression.RuleVersion);
        writer.WriteString("code", suppression.Code);
        writer.WriteString("source_cost_configuration_id", suppression.SourceCostConfigurationId);
        writer.WriteNumber("source_configuration_head_revision", suppression.SourceConfigurationHeadRevision);
        writer.WriteString("source_configuration_catalog_sha256", suppression.SourceConfigurationCatalogSha256);
        writer.WriteString("configuration_version", suppression.ConfigurationVersion);
        writer.WriteString("configuration_hash", suppression.ConfigurationHash);
        writer.WriteString("scope_kind", AlertWireV2.ScopeKind(suppression.ScopeKind));
        writer.WriteString("scope_id", suppression.ScopeId);
        NullableTimestamp(writer, "scope_start_utc", suppression.ScopeStartUtc);
        NullableTimestamp(writer, "scope_end_utc", suppression.ScopeEndUtc);
        writer.WriteString("eligibility_digest", suppression.EligibilityDigest);
        NullableString(writer, "currency", suppression.Currency);
        writer.WriteString("aggregate_state", AlertWireV2.Aggregate(suppression.AggregateState));
        NullableNumber(writer, "eligible_count", suppression.EligibleCount);
        NullableNumber(writer, "estimated_count", suppression.EstimatedCount);
        NullableNumber(writer, "partial_count", suppression.PartialCount);
        NullableNumber(writer, "not_estimable_count", suppression.NotEstimableCount);
        NullableNumber(writer, "missing_count", suppression.MissingCount);
        NullableNumber(writer, "failed_count", suppression.FailedCount);
        NullableNumber(writer, "unavailable_count", suppression.UnavailableCount);
        NullableNumber(writer, "stale_count", suppression.StaleCount);
        NullableNumber(writer, "coverage_basis_points", suppression.CoverageBasisPoints);
        NullableTimestamp(writer, "first_observed_at", suppression.FirstObservedAt);
        NullableTimestamp(writer, "last_observed_at", suppression.LastObservedAt);
        writer.WriteEndObject();
    }

    private static byte[] Write(Action<Utf8JsonWriter> action)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
            SkipValidation = false,
        }))
        {
            action(writer);
        }
        return stream.ToArray();
    }

    private static void Decimal(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(AlertWireV2.Decimal(value), skipInputValidation: false);
    }

    private static void NullableDecimal(Utf8JsonWriter writer, string name, decimal? value)
    {
        if (value is null) writer.WriteNull(name); else Decimal(writer, name, value.Value);
    }

    private static void NullableNumber(Utf8JsonWriter writer, string name, long? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value);
    }

    private static void NullableNumber(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteNumber(name, value.Value);
    }

    private static void NullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static void NullableTimestamp(Utf8JsonWriter writer, string name, DateTimeOffset? value) =>
        NullableString(writer, name, value is null ? null : AlertWireV2.Timestamp(value.Value));

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}

internal static class AlertWireV2
{
    public static string Acquisition(AlertCostAcquisitionStateV2 value) => value switch
    {
        AlertCostAcquisitionStateV2.Complete => "complete",
        AlertCostAcquisitionStateV2.Incomplete => "incomplete",
        _ => throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid."),
    };

    public static string Aggregate(AlertCostAggregateStateV2 value) => value switch
    {
        AlertCostAggregateStateV2.Available => "available",
        AlertCostAggregateStateV2.Unrepresentable => "unrepresentable",
        AlertCostAggregateStateV2.NotApplicable => "not_applicable",
        _ => throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid."),
    };

    public static string ScopeKind(AlertCostScopeKindV2 value) => value switch
    {
        AlertCostScopeKindV2.Session => "session",
        AlertCostScopeKindV2.UtcDay => "utc_day",
        AlertCostScopeKindV2.RollingPeriod => "rolling_period",
        _ => throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid."),
    };

    public static string MemberState(AlertCostMemberStateV2 value) => value switch
    {
        AlertCostMemberStateV2.Estimated => "estimated",
        AlertCostMemberStateV2.Partial => "partial",
        AlertCostMemberStateV2.NotEstimable => "not_estimable",
        AlertCostMemberStateV2.Missing => "missing",
        AlertCostMemberStateV2.Failed => "failed",
        AlertCostMemberStateV2.Unavailable => "unavailable",
        AlertCostMemberStateV2.Stale => "stale",
        _ => throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid."),
    };

    public static string AttemptResult(AlertCostAttemptResultKindV2 value) => value switch
    {
        AlertCostAttemptResultKindV2.Estimate => "estimate",
        AlertCostAttemptResultKindV2.Unavailable => "unavailable",
        AlertCostAttemptResultKindV2.Failed => "failed",
        _ => throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid."),
    };

    public static string EvidenceKind(AlertEvidenceKindV2 value) => value switch
    {
        AlertEvidenceKindV2.Session => "session",
        AlertEvidenceKindV2.PricingEstimate => "pricing_estimate",
        _ => throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid."),
    };

    public static int EvidenceRank(AlertEvidenceKindV2 value) => value switch
    {
        AlertEvidenceKindV2.Session => 0,
        AlertEvidenceKindV2.PricingEstimate => 1,
        _ => int.MaxValue,
    };

    public static string Completeness(AlertCostCompletenessV2 value) => value switch
    {
        AlertCostCompletenessV2.Full => "full",
        AlertCostCompletenessV2.Partial => "partial",
        _ => throw new AlertContractException("invalid_snapshot", "Normalized alert snapshot is invalid."),
    };

    public static string Severity(AlertSeverity value) => value switch
    {
        AlertSeverity.Critical => "critical",
        AlertSeverity.Warning => "warning",
        AlertSeverity.Info => "info",
        _ => throw new AlertContractException("invalid_rule_output", "Alert rule output is invalid."),
    };

    public static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static string NullableTimestamp(DateTimeOffset? value) =>
        value is null ? "\0" : Timestamp(value.Value);

    public static string Decimal(decimal value) =>
        value == decimal.Zero ? "0" : value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture);
}

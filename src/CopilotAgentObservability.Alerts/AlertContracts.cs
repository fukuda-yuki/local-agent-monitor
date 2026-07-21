namespace CopilotAgentObservability.Alerts;

public static class AlertContractVersions
{
    public const string Snapshot = "alert.snapshot.v1";
    public const string Configuration = "alert.config.v1";
    public const string Receipt = "alert.receipt.v1";
    public const string Evaluation = "alert.evaluation.v1";
    public const string SanitizedReceiptProfile = "sanitized-alert-receipt.v1";
    public const string CanonicalJson = "alert.canonical-json.v1";
    public const string SensitiveComparableHash = "alert.hmac-sha256.v1";
}

public enum AlertCapabilityAvailability { Available, Unavailable, Unknown }
public enum AlertSignalKind { LlmCall, ToolCall, Permission, FileAccess, SessionEvent }
public enum AlertSignalStatus { Success, Error, Cancelled, Unknown }
public enum AlertComparableKeyKind { MetadataToken, SensitiveHmac }
public enum AlertEvidenceKind { Session, Trace, Span, Turn, Event, ToolCall }
public enum AlertCompleteness { Unbound, Partial, Rich, Full }
public enum AlertRuleScope { Session, Trace, CrossSession }
public enum AlertThresholdDirection { HigherIsWorse, LowerIsWorse }
public enum AlertSeverity { Critical, Warning, Info }
public enum AlertInitialState { Open }

public sealed record AlertCapabilityFact(string Name, AlertCapabilityAvailability Availability);

public sealed record AlertMetric(string Name, string Unit, decimal Value);

public sealed record AlertComparableKey(string Name, AlertComparableKeyKind Kind, string Value);

public sealed record AlertEvidenceReference(
    AlertEvidenceKind Kind,
    string EvidenceId,
    string SessionId,
    string? TraceId,
    string? SpanId,
    string? TurnId,
    string? EventId,
    string? ToolCallId,
    DateTimeOffset ObservedAt);

public sealed record AlertSignal(
    string SignalId,
    AlertSignalKind Kind,
    long Sequence,
    DateTimeOffset ObservedAt,
    string? ParentSignalId,
    AlertSignalStatus Status,
    IReadOnlyList<AlertMetric> Metrics,
    IReadOnlyList<AlertComparableKey> ComparableKeys,
    AlertEvidenceReference Evidence);

public sealed record AlertNormalizedSnapshot(
    string SchemaVersion,
    string SourceSurface,
    string SourceVersion,
    string SessionId,
    string? TraceId,
    AlertCompleteness Completeness,
    IReadOnlyList<string> CompletenessReasons,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    IReadOnlyList<AlertCapabilityFact> Capabilities,
    IReadOnlyList<AlertSignal> Signals);

public sealed record AlertThresholdDefinition(
    string Name,
    string Unit,
    AlertThresholdDirection Direction,
    decimal Minimum,
    decimal Maximum,
    decimal WarningDefault,
    decimal CriticalDefault);

public sealed record AlertRuleDescriptor(
    string RuleId,
    string RuleVersion,
    string Title,
    string Description,
    IReadOnlyList<string> RequiredCapabilities,
    AlertRuleScope Scope,
    IReadOnlyList<string> GroupingKeys,
    string EvaluationWindow,
    IReadOnlyList<AlertThresholdDefinition> Thresholds,
    IReadOnlyList<string> SuppressionCodes,
    IReadOnlyList<string> ApplicableSourceSurfaces);

public sealed record AlertRuleConfiguration(
    string RuleId,
    string RuleVersion,
    bool Enabled,
    IReadOnlyDictionary<string, decimal> ThresholdOverrides,
    IReadOnlyList<string>? SourceSurfaceAllowlist);

public sealed record AlertEngineConfiguration(
    string SchemaVersion,
    string ConfigurationVersion,
    IReadOnlyList<AlertRuleConfiguration> Rules);

public sealed record AlertRuleContext(
    AlertNormalizedSnapshot Snapshot,
    IReadOnlyDictionary<string, decimal> EffectiveThresholds);

public sealed record AlertObservedValue(string Name, string Unit, decimal Value);

public sealed record AlertRuleMatch(
    AlertSeverity Severity,
    IReadOnlyList<AlertObservedValue> ObservedValues,
    IReadOnlyList<AlertEvidenceReference> Evidence,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt);

public sealed record AlertRuleSuppression(string Code);

public sealed record AlertRuleOutcome(
    IReadOnlyList<AlertRuleMatch> Matches,
    IReadOnlyList<AlertRuleSuppression> Suppressions);

public sealed record AlertReceipt(
    string SchemaVersion,
    string SanitizedExportProfile,
    string AlertId,
    string EvaluationId,
    string RuleId,
    string RuleVersion,
    AlertSeverity Severity,
    AlertInitialState InitialState,
    string SourceSurface,
    string SourceVersion,
    string SessionId,
    string? TraceId,
    IReadOnlyList<AlertEvidenceReference> Evidence,
    IReadOnlyList<AlertObservedValue> ObservedValues,
    IReadOnlyList<AlertObservedValue> EffectiveThresholds,
    string ConfigurationVersion,
    string ConfigurationHash,
    IReadOnlyList<string> RequiredCapabilities,
    AlertCompleteness Completeness,
    IReadOnlyList<string> CompletenessReasons,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string EvaluationInputHash,
    string Summary);

public sealed record AlertSuppression(
    string EvaluationId,
    string RuleId,
    string RuleVersion,
    string Code,
    IReadOnlyList<string> MissingCapabilities);

public sealed record AlertRejectedMatch(string RuleId, string RuleVersion, string Code);

public sealed record AlertEvaluationResult(
    string SchemaVersion,
    string EvaluationId,
    string InputHash,
    string ConfigurationVersion,
    string ConfigurationHash,
    IReadOnlyList<AlertReceipt> Receipts,
    IReadOnlyList<AlertSuppression> Suppressions,
    IReadOnlyList<AlertRejectedMatch> RejectedMatches);

public sealed class AlertContractException(string code, string message) : ArgumentException(message)
{
    public string Code { get; } = code;
}

public interface IAlertEvidenceResolver
{
    bool Exists(AlertEvidenceReference reference);
}

public interface IAlertRule
{
    AlertRuleDescriptor Descriptor { get; }

    AlertRuleOutcome Evaluate(AlertRuleContext context);
}

public enum AlertStoreStatus { Success, NotFound, Busy, Unavailable, Conflict }

public sealed record AlertStoreResult(AlertStoreStatus Status, string? Code = null);

public sealed record AlertStoreReadResult(AlertStoreStatus Status, string? CanonicalJson = null, string? Code = null);

public sealed record AlertStoreListResult(AlertStoreStatus Status, IReadOnlyList<string> CanonicalJsonItems, string? Code = null);

public interface IAlertEngineStore
{
    AlertStoreResult Initialize();

    AlertStoreResult Append(AlertEvaluationResult evaluation);

    AlertStoreReadResult GetEvaluation(string evaluationId);

    AlertStoreReadResult GetReceipt(string alertId);

    AlertStoreListResult ListSuppressions(string evaluationId);
}

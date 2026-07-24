using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.Alerts;

public static class AlertReceiptConsumerV2
{
    public static AlertReceiptConsumerEnvelopeV2 Validate(ReadOnlySpan<byte> canonicalReceipt)
    {
        try
        {
            var receipt = AlertConsumerV2.ParseReceipt(canonicalReceipt);
            return new(
                receipt.AlertId,
                receipt.Scope.ScopeId,
                receipt.SourceSurface,
                receipt.LastObservedAt);
        }
        catch (Exception exception) when (AlertConsumerV2.IsNonFatal(exception))
        {
            throw new AlertReceiptConsumerException();
        }
    }
}

public sealed class AlertReceiptConsumerEnvelopeV2
{
    internal AlertReceiptConsumerEnvelopeV2(
        string alertId,
        string scopeId,
        string sourceSurface,
        DateTimeOffset lastObservedAt)
    {
        AlertId = alertId;
        ScopeId = scopeId;
        SourceSurface = sourceSurface;
        LastObservedAt = lastObservedAt;
    }

    public string AlertId { get; }
    public string ScopeId { get; }
    public string SourceSurface { get; }
    public DateTimeOffset LastObservedAt { get; }
}

public static class AlertCenterReceiptConsumerV2
{
    public static AlertCenterReceiptProjectionV2 Validate(ReadOnlySpan<byte> canonicalReceipt)
    {
        try
        {
            return new(AlertConsumerV2.ParseReceipt(canonicalReceipt));
        }
        catch (Exception exception) when (AlertConsumerV2.IsNonFatal(exception))
        {
            throw new AlertReceiptConsumerException();
        }
    }
}

public sealed class AlertCenterReceiptProjectionV2
{
    internal AlertCenterReceiptProjectionV2(AlertReceiptV2 receipt)
    {
        AlertId = receipt.AlertId;
        EvaluationId = receipt.EvaluationId;
        RuleId = receipt.RuleId;
        RuleVersion = receipt.RuleVersion;
        Severity = receipt.Severity;
        InitialState = receipt.InitialState;
        SourceSurface = receipt.SourceSurface;
        SourceVersion = receipt.SourceVersion;
        Scope = receipt.Scope with { SessionIds = Array.AsReadOnly(receipt.Scope.SessionIds.ToArray()) };
        Evidence = Array.AsReadOnly(receipt.Evidence.Select(item => item with { }).ToArray());
        Currency = receipt.Currency;
        AggregateState = receipt.AggregateState;
        ObservedAmount = receipt.ObservedAmount;
        WarningThreshold = receipt.WarningThreshold;
        CriticalThreshold = receipt.CriticalThreshold;
        EligibleCount = receipt.EligibleCount;
        EstimatedCount = receipt.EstimatedCount;
        PartialCount = receipt.PartialCount;
        NotEstimableCount = receipt.NotEstimableCount;
        MissingCount = receipt.MissingCount;
        FailedCount = receipt.FailedCount;
        UnavailableCount = receipt.UnavailableCount;
        StaleCount = receipt.StaleCount;
        CoverageNumerator = receipt.CoverageNumerator;
        CoverageDenominator = receipt.CoverageDenominator;
        CoverageBasisPoints = receipt.CoverageBasisPoints;
        Members = Array.AsReadOnly(receipt.Members.Select(item => item with { }).ToArray());
        SourceCostConfigurationId = receipt.SourceCostConfigurationId;
        SourceConfigurationHeadRevision = receipt.SourceConfigurationHeadRevision;
        SourceConfigurationCatalogSha256 = receipt.SourceConfigurationCatalogSha256;
        ConfigurationVersion = receipt.ConfigurationVersion;
        ConfigurationHash = receipt.ConfigurationHash;
        Completeness = receipt.Completeness;
        CompletenessReasons = Array.AsReadOnly(receipt.CompletenessReasons.ToArray());
        FirstObservedAt = receipt.FirstObservedAt;
        LastObservedAt = receipt.LastObservedAt;
        InputHash = receipt.InputHash;
        Summary = receipt.Summary;
    }

    public string AlertId { get; }
    public string EvaluationId { get; }
    public string RuleId { get; }
    public string RuleVersion { get; }
    public AlertSeverity Severity { get; }
    public AlertInitialState InitialState { get; }
    public string SourceSurface { get; }
    public string SourceVersion { get; }
    public AlertCostScopeV2 Scope { get; }
    public IReadOnlyList<AlertEvidenceReferenceV2> Evidence { get; }
    public string Currency { get; }
    public AlertCostAggregateStateV2 AggregateState { get; }
    public decimal ObservedAmount { get; }
    public decimal WarningThreshold { get; }
    public decimal CriticalThreshold { get; }
    public long EligibleCount { get; }
    public long EstimatedCount { get; }
    public long PartialCount { get; }
    public long NotEstimableCount { get; }
    public long MissingCount { get; }
    public long FailedCount { get; }
    public long UnavailableCount { get; }
    public long StaleCount { get; }
    public long CoverageNumerator { get; }
    public long CoverageDenominator { get; }
    public int CoverageBasisPoints { get; }
    public IReadOnlyList<AlertCostMemberV2> Members { get; }
    public string SourceCostConfigurationId { get; }
    public long SourceConfigurationHeadRevision { get; }
    public string SourceConfigurationCatalogSha256 { get; }
    public string ConfigurationVersion { get; }
    public string ConfigurationHash { get; }
    public AlertCostCompletenessV2 Completeness { get; }
    public IReadOnlyList<string> CompletenessReasons { get; }
    public DateTimeOffset FirstObservedAt { get; }
    public DateTimeOffset LastObservedAt { get; }
    public string InputHash { get; }
    public string Summary { get; }
}

public static class AlertEvaluationConsumerV2
{
    public static AlertEvaluationConsumerProjectionV2 Validate(ReadOnlySpan<byte> canonicalEvaluation)
    {
        try
        {
            return new(AlertConsumerV2.ParseEvaluation(canonicalEvaluation));
        }
        catch (Exception exception) when (AlertConsumerV2.IsNonFatal(exception))
        {
            throw new AlertEvaluationConsumerException();
        }
    }

    public static AlertSuppressionProjectionV2 ValidateSuppression(ReadOnlySpan<byte> canonicalSuppression)
    {
        try
        {
            return new(AlertConsumerV2.ParseSuppression(canonicalSuppression));
        }
        catch (Exception exception) when (AlertConsumerV2.IsNonFatal(exception))
        {
            throw new AlertEvaluationConsumerException();
        }
    }
}

public sealed class AlertEvaluationConsumerProjectionV2
{
    internal AlertEvaluationConsumerProjectionV2(AlertEvaluationResultV2 evaluation)
    {
        EvaluationId = evaluation.EvaluationId;
        InputHash = evaluation.InputHash;
        ConfigurationVersion = evaluation.ConfigurationVersion;
        ConfigurationHash = evaluation.ConfigurationHash;
        SelectedRuleId = evaluation.SelectedRuleId;
        SelectedRuleVersion = evaluation.SelectedRuleVersion;
        SourceCostConfigurationId = evaluation.SourceCostConfigurationId;
        SourceConfigurationHeadRevision = evaluation.SourceConfigurationHeadRevision;
        SourceConfigurationCatalogSha256 = evaluation.SourceConfigurationCatalogSha256;
        ScopeKind = evaluation.ScopeKind;
        ScopeId = evaluation.ScopeId;
        ScopeStartUtc = evaluation.ScopeStartUtc;
        ScopeEndUtc = evaluation.ScopeEndUtc;
        EligibilityDigest = evaluation.EligibilityDigest;
        Currency = evaluation.Currency;
        AggregateState = evaluation.AggregateState;
        EligibleCount = evaluation.EligibleCount;
        EstimatedCount = evaluation.EstimatedCount;
        PartialCount = evaluation.PartialCount;
        NotEstimableCount = evaluation.NotEstimableCount;
        MissingCount = evaluation.MissingCount;
        FailedCount = evaluation.FailedCount;
        UnavailableCount = evaluation.UnavailableCount;
        StaleCount = evaluation.StaleCount;
        CoverageBasisPoints = evaluation.CoverageBasisPoints;
        FirstObservedAt = evaluation.FirstObservedAt;
        LastObservedAt = evaluation.LastObservedAt;
        ReceiptCount = evaluation.Receipts.Count;
        SuppressionCount = evaluation.Suppressions.Count;
    }

    public string EvaluationId { get; }
    public string InputHash { get; }
    public string ConfigurationVersion { get; }
    public string ConfigurationHash { get; }
    public string SelectedRuleId { get; }
    public string SelectedRuleVersion { get; }
    public string SourceCostConfigurationId { get; }
    public long SourceConfigurationHeadRevision { get; }
    public string SourceConfigurationCatalogSha256 { get; }
    public AlertCostScopeKindV2 ScopeKind { get; }
    public string ScopeId { get; }
    public DateTimeOffset? ScopeStartUtc { get; }
    public DateTimeOffset? ScopeEndUtc { get; }
    public string EligibilityDigest { get; }
    public string? Currency { get; }
    public AlertCostAggregateStateV2 AggregateState { get; }
    public long? EligibleCount { get; }
    public long? EstimatedCount { get; }
    public long? PartialCount { get; }
    public long? NotEstimableCount { get; }
    public long? MissingCount { get; }
    public long? FailedCount { get; }
    public long? UnavailableCount { get; }
    public long? StaleCount { get; }
    public int? CoverageBasisPoints { get; }
    public DateTimeOffset? FirstObservedAt { get; }
    public DateTimeOffset? LastObservedAt { get; }
    public long ReceiptCount { get; }
    public long SuppressionCount { get; }
}

public sealed class AlertSuppressionProjectionV2
{
    internal AlertSuppressionProjectionV2(AlertSuppressionV2 suppression)
    {
        EvaluationId = suppression.EvaluationId;
        RuleId = suppression.RuleId;
        RuleVersion = suppression.RuleVersion;
        Code = suppression.Code;
        SourceCostConfigurationId = suppression.SourceCostConfigurationId;
        SourceConfigurationHeadRevision = suppression.SourceConfigurationHeadRevision;
        SourceConfigurationCatalogSha256 = suppression.SourceConfigurationCatalogSha256;
        ConfigurationVersion = suppression.ConfigurationVersion;
        ConfigurationHash = suppression.ConfigurationHash;
        ScopeKind = suppression.ScopeKind;
        ScopeId = suppression.ScopeId;
        ScopeStartUtc = suppression.ScopeStartUtc;
        ScopeEndUtc = suppression.ScopeEndUtc;
        EligibilityDigest = suppression.EligibilityDigest;
        Currency = suppression.Currency;
        AggregateState = suppression.AggregateState;
        EligibleCount = suppression.EligibleCount;
        EstimatedCount = suppression.EstimatedCount;
        PartialCount = suppression.PartialCount;
        NotEstimableCount = suppression.NotEstimableCount;
        MissingCount = suppression.MissingCount;
        FailedCount = suppression.FailedCount;
        UnavailableCount = suppression.UnavailableCount;
        StaleCount = suppression.StaleCount;
        CoverageBasisPoints = suppression.CoverageBasisPoints;
        FirstObservedAt = suppression.FirstObservedAt;
        LastObservedAt = suppression.LastObservedAt;
    }

    public string EvaluationId { get; }
    public string RuleId { get; }
    public string RuleVersion { get; }
    public string Code { get; }
    public string SourceCostConfigurationId { get; }
    public long SourceConfigurationHeadRevision { get; }
    public string SourceConfigurationCatalogSha256 { get; }
    public string ConfigurationVersion { get; }
    public string ConfigurationHash { get; }
    public AlertCostScopeKindV2 ScopeKind { get; }
    public string ScopeId { get; }
    public DateTimeOffset? ScopeStartUtc { get; }
    public DateTimeOffset? ScopeEndUtc { get; }
    public string EligibilityDigest { get; }
    public string? Currency { get; }
    public AlertCostAggregateStateV2 AggregateState { get; }
    public long? EligibleCount { get; }
    public long? EstimatedCount { get; }
    public long? PartialCount { get; }
    public long? NotEstimableCount { get; }
    public long? MissingCount { get; }
    public long? FailedCount { get; }
    public long? UnavailableCount { get; }
    public long? StaleCount { get; }
    public int? CoverageBasisPoints { get; }
    public DateTimeOffset? FirstObservedAt { get; }
    public DateTimeOffset? LastObservedAt { get; }
}

internal static class AlertConsumerV2
{
    private const int MaximumBytes = 8_388_608;
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static AlertReceiptV2 ParseReceipt(ReadOnlySpan<byte> bytes)
    {
        EnsureSize(bytes);
        var value = JsonSerializer.Deserialize<AlertReceiptV2>(bytes, Options)
            ?? throw new JsonException();
        AlertValidationV2.ValidateReceipt(value);
        if (!bytes.SequenceEqual(AlertCanonicalJsonV2.SerializeReceipt(value))) throw new JsonException();
        return value;
    }

    public static AlertEvaluationResultV2 ParseEvaluation(ReadOnlySpan<byte> bytes)
    {
        EnsureSize(bytes);
        var value = JsonSerializer.Deserialize<AlertEvaluationResultV2>(bytes, Options)
            ?? throw new JsonException();
        AlertValidationV2.ValidateEvaluation(value);
        if (!bytes.SequenceEqual(AlertCanonicalJsonV2.SerializeEvaluation(value))) throw new JsonException();
        return value;
    }

    public static AlertSuppressionV2 ParseSuppression(ReadOnlySpan<byte> bytes)
    {
        EnsureSize(bytes);
        var value = JsonSerializer.Deserialize<AlertSuppressionV2>(bytes, Options)
            ?? throw new JsonException();
        AlertValidationV2.ValidateSuppression(value);
        if (!bytes.SequenceEqual(AlertCanonicalJsonV2.SerializeSuppression(value))) throw new JsonException();
        return value;
    }

    public static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;

    private static void EnsureSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumBytes) throw new JsonException();
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = false,
            MaxDepth = 16,
            PropertyNameCaseInsensitive = false,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
}

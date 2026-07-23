using System.Text.Json.Serialization;

namespace CopilotAgentObservability.Pricing;

internal static class PricingContractLimits
{
    internal const int MaximumRegistryBytes = 1_048_576;
    internal const int MaximumEstimateBytes = 1_048_576;
    internal const int MaximumCatalogSnapshotBytes = 4 * 1_048_576;
    internal const int MaximumCatalogDocuments = 64;
    internal const int MaximumSourceReferenceLength = 4096;
}

public static class PricingContractVersions
{
    public const string Registry = "pricing.registry.v1";
    public const string CatalogSnapshot = "pricing.catalog-snapshot.v1";
    public const string RegistrySchemaUri =
        "https://local-agent-monitor.invalid/contracts/pricing/v1/pricing-registry.schema.json";
    public const string EstimateRequest = "pricing.estimate-request.v1";
    public const string Estimate = "pricing.estimate.v1";
    public const string CanonicalJson = "pricing.canonical-json.v1";
    public const string DisplayRounding = "pricing.display-rounding.v1";
    public const string NoIntermediateRounding = "none";
}

public static class PricingRegistrySourceKinds
{
    public const string Bundled = "bundled";
    public const string LocalOverride = "local_override";
}

public static class PricingProviders
{
    public const string GitHubCopilot = "github_copilot";
    public const string ClaudeCode = "claude_code";
    public const string CodexApp = "codex_app";
    public const string Unknown = "unknown";
}

public static class PricingBillingModes
{
    public const string GitHubAiCredits = "github_ai_credits";
    public const string GitHubLegacyRequests = "github_legacy_requests";
    public const string PlanIncluded = "plan_included";
    public const string AnthropicApiTokens = "anthropic_api_tokens";
    public const string CloudProviderApiTokens = "cloud_provider_api_tokens";
    public const string Subscription = "subscription";
    public const string CustomEnterprise = "custom_enterprise";
    public const string Unknown = "unknown";
}

public static class PricingRoutes
{
    public const string CreditConsumingInteraction = "credit_consuming_interaction";
    public const string LegacyRequest = "legacy_request";
    public const string CodeCompletion = "code_completion";
    public const string NextEditSuggestion = "next_edit_suggestion";
    public const string StandardGlobal = "standard_global";
    public const string UsOnlyInference = "us_only_inference";
    public const string Batch = "batch";
    public const string CloudProviderConfigured = "cloud_provider_configured";
    public const string SubscriptionOrContract = "subscription_or_contract";
    public const string Unknown = "unknown";
}

public static class PricingSourceCompleteness
{
    public const string Unbound = "unbound";
    public const string Partial = "partial";
    public const string Rich = "rich";
    public const string Full = "full";
}

public static class PricingSourceCompletenessReasons
{
    public const string MissingNativeSessionId = "missing_native_session_id";
    public const string MissingTraceContext = "missing_trace_context";
    public const string TraceSignalDisabled = "trace_signal_disabled";
    public const string ContentCaptureDisabled = "content_capture_disabled";
    public const string UnsupportedSourceVersion = "unsupported_source_version";
    public const string IngestGap = "ingest_gap";
    public const string HookOnly = "hook_only";
    public const string HistoricalSummaryOnly = "historical_summary_only";
    public const string UnknownSpanKind = "unknown_span_kind";
    public const string SchemaDriftDetected = "schema_drift_detected";
    public const string PlannedSourceNotEnabled = "planned_source_not_enabled";

    public static IReadOnlyList<string> Ordered { get; } = Array.AsReadOnly(
    [
        MissingNativeSessionId,
        MissingTraceContext,
        TraceSignalDisabled,
        ContentCaptureDisabled,
        UnsupportedSourceVersion,
        IngestGap,
        HookOnly,
        HistoricalSummaryOnly,
        UnknownSpanKind,
        SchemaDriftDetected,
        PlannedSourceNotEnabled
    ]);
}

public static class PricingEstimateStatuses
{
    public const string Estimated = "estimated";
    public const string Partial = "partial";
    public const string NotEstimable = "not-estimable";
}

public static class PricingEstimateReasons
{
    public const string UnknownModel = "unknown_model";
    public const string UnknownBillingMode = "unknown_billing_mode";
    public const string SubscriptionAllocationUnknown = "subscription_allocation_unknown";
    public const string SubscriptionOrContractUnknown = "subscription_or_contract_unknown";
    public const string CustomContract = "custom_contract";
    public const string MissingTokenCategory = "missing_token_category";
    public const string UnsupportedProviderRoute = "unsupported_provider_route";
    public const string PartialSource = "partial_source";
    public const string RegistryOutOfDate = "registry_out_of_date";
    public const string OutsideEffectiveRange = "outside_effective_range";
}

public sealed record PricingRegistrySourceReference(
    string Reference,
    DateOnly ReviewedDate,
    string Note);

public sealed record PricingRates(
    decimal? InputPerMillionTokens,
    decimal? OutputPerMillionTokens,
    decimal? CacheReadPerMillionTokens,
    [property: JsonPropertyName("cache_write_5m_per_million_tokens")]
    decimal? CacheWrite5mPerMillionTokens,
    [property: JsonPropertyName("cache_write_1h_per_million_tokens")]
    decimal? CacheWrite1hPerMillionTokens,
    decimal? ReasoningPerMillionTokens,
    decimal? PerRequest,
    decimal? PerCredit,
    decimal? RequestCreditMultiplier)
{
    public IEnumerable<decimal> NonNullRates()
    {
        if (InputPerMillionTokens is { } input) yield return input;
        if (OutputPerMillionTokens is { } output) yield return output;
        if (CacheReadPerMillionTokens is { } cacheRead) yield return cacheRead;
        if (CacheWrite5mPerMillionTokens is { } cacheWrite5m) yield return cacheWrite5m;
        if (CacheWrite1hPerMillionTokens is { } cacheWrite1h) yield return cacheWrite1h;
        if (ReasoningPerMillionTokens is { } reasoning) yield return reasoning;
        if (PerRequest is { } request) yield return request;
        if (PerCredit is { } credit) yield return credit;
        if (RequestCreditMultiplier is { } multiplier) yield return multiplier;
    }
}

public sealed record PricingRegistryEntry(
    string EntryId,
    int Revision,
    string? SupersedesEntryKey,
    string Provider,
    string CanonicalModelId,
    IReadOnlyList<string> Aliases,
    string BillingMode,
    string PricingRoute,
    PricingRates Rates,
    string Currency,
    int CurrencyMinorUnits,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    string SourceReference,
    DateOnly LastReviewedDate,
    bool IncludedZeroIncrementalCost,
    IReadOnlyList<string> Limitations);

public sealed record PricingRegistryDocument(
    [property: JsonPropertyName("$schema")] string SchemaUri,
    string SchemaVersion,
    string RegistryVersion,
    string SourceKind,
    string SourceId,
    string SourceLabel,
    DateOnly LastReviewedDate,
    DateOnly StaleAfterDate,
    IReadOnlyList<PricingRegistrySourceReference> SourceReferences,
    IReadOnlyList<PricingRegistryEntry> Entries);

public sealed record PricingCatalogSnapshot(
    string SchemaVersion,
    IReadOnlyList<PricingRegistryDocument> Documents);

public sealed record PricingValueProvenance(
    string SourceAdapter,
    string SourceVersionOrSchemaFingerprint,
    string SourceEventOrTraceSpanId,
    string CaptureContentState,
    string NormalizationVersion);

public sealed record PricingQuantity(decimal Value, PricingValueProvenance Provenance);

public sealed record PricingUsage(
    PricingQuantity? InputTokens,
    PricingQuantity? OutputTokens,
    PricingQuantity? CacheReadTokens,
    [property: JsonPropertyName("cache_write_5m_tokens")]
    PricingQuantity? CacheWrite5mTokens,
    [property: JsonPropertyName("cache_write_1h_tokens")]
    PricingQuantity? CacheWrite1hTokens,
    PricingQuantity? ReasoningTokens,
    PricingQuantity? RequestCount,
    PricingQuantity? CreditCount)
{
    public static PricingUsage Empty { get; } =
        new(null, null, null, null, null, null, null, null);
}

public sealed record PricingEstimateSource(
    string SourceSurface,
    string SourceVersion,
    string SessionId,
    DateTimeOffset SessionObservedAtUtc,
    string Provider,
    string ModelId,
    string BillingMode,
    string PricingRoute,
    string Completeness,
    IReadOnlyList<string> CompletenessReasons,
    PricingValueProvenance SessionTimeProvenance,
    PricingValueProvenance ProviderProvenance,
    PricingValueProvenance ModelProvenance,
    PricingValueProvenance BillingModeProvenance,
    PricingValueProvenance PricingRouteProvenance);

public sealed record PricingEstimateRequest(
    string SchemaVersion,
    DateTimeOffset CalculationTimeUtc,
    string? SupersedesEstimateId,
    PricingEstimateSource Source,
    PricingUsage Usage);

public sealed record PricingEstimateComponent(
    string Category,
    decimal? Quantity,
    string Unit,
    decimal? Rate,
    decimal? Amount,
    PricingValueProvenance? SourceProvenance,
    string? MissingReason);

public sealed record PricingEstimateCoverage(
    IReadOnlyList<string> RequiredCategories,
    IReadOnlyList<string> EstimatedCategories,
    IReadOnlyList<string> MissingCategories);

public sealed record PricingRegistryProvenance(
    string SchemaVersion,
    string RegistryVersion,
    string SourceKind,
    string SourceId,
    string SourceLabel,
    string EntryKey,
    string SourceReference,
    DateOnly LastReviewedDate,
    string CanonicalModelId,
    string MatchedModelId,
    string BillingMode,
    string PricingRoute,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    string Currency);

public sealed record PricingRoundingPolicy(
    string IntermediatePolicy,
    string DisplayPolicyVersion,
    string DisplayMode,
    int? CurrencyMinorUnits);

public sealed record PricingEstimateRecord(
    string SchemaVersion,
    string CatalogSha256,
    string EstimateId,
    string? SupersedesEstimateId,
    DateTimeOffset CalculationTimeUtc,
    string Status,
    decimal? Amount,
    string? Currency,
    IReadOnlyList<PricingEstimateComponent> Components,
    PricingEstimateCoverage Coverage,
    IReadOnlyList<string> Reasons,
    PricingRegistryProvenance? Registry,
    PricingEstimateSource Source,
    PricingUsage Usage,
    PricingRoundingPolicy Rounding,
    string CanonicalVersion);

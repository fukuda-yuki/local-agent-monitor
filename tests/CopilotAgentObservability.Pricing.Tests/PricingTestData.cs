namespace CopilotAgentObservability.Pricing.Tests;

internal static class PricingTestData
{
    internal static readonly DateTimeOffset SessionTime =
        new(2026, 7, 25, 12, 0, 0, TimeSpan.Zero);

    internal static PricingRegistryDocument SyntheticRegistry()
    {
        return PricingRegistryLoader.Deserialize(SyntheticJson());
    }

    internal static string SyntheticJson()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "pricing-registry.synthetic.v1.json");
        return File.ReadAllText(path);
    }

    internal static PricingEstimateRequest Request(
        string provider = PricingProviders.GitHubCopilot,
        string model = "synthetic-model",
        string billingMode = PricingBillingModes.GitHubAiCredits,
        string pricingRoute = PricingRoutes.CreditConsumingInteraction,
        PricingUsage? usage = null,
        string completeness = PricingSourceCompleteness.Full,
        DateTimeOffset? sessionTime = null,
        DateTimeOffset? calculatedAt = null,
        string? supersedes = null)
    {
        var provenance = Provenance();
        return new PricingEstimateRequest(
            PricingContractVersions.EstimateRequest,
            calculatedAt ?? new DateTimeOffset(2026, 7, 25, 12, 5, 0, TimeSpan.Zero),
            supersedes,
            new PricingEstimateSource(
                "synthetic-surface",
                "synthetic-source-v1",
                "session-opaque-1",
                sessionTime ?? SessionTime,
                provider,
                model,
                billingMode,
                pricingRoute,
                completeness,
                completeness == PricingSourceCompleteness.Full
                    ? []
                    : [PricingSourceCompletenessReasons.HistoricalSummaryOnly],
                provenance,
                provenance,
                provenance,
                provenance,
                provenance),
            usage ?? new PricingUsage(
                Quantity(1_000),
                Quantity(2_000),
                null,
                null,
                null,
                null,
                null,
                null));
    }

    internal static PricingQuantity Quantity(decimal value) => new(value, Provenance());

    internal static PricingValueProvenance Provenance() => new(
        "synthetic-adapter",
        "synthetic-fingerprint-v1",
        "opaque-event-1",
        "not_captured",
        "pricing-normalization.v1");
}

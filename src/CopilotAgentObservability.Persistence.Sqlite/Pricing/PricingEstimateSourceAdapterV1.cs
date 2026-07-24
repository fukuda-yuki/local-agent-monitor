using CopilotAgentObservability.Pricing;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal enum PricingEstimateSourceAdapterStatusV1
{
    Available,
    Unavailable,
    Failed,
}

internal sealed record PricingEstimateSourceAdapterRequestV1(
    string SessionId,
    DateTimeOffset SessionEffectiveAtUtc,
    string SourceSurface,
    string SourceApplicationVersion);

internal sealed record PricingEstimateSourceAdapterFactsV1(
    string AdapterCapabilityVersion,
    PricingEstimateSource Source,
    PricingUsage Usage);

internal sealed record PricingEstimateSourceAdapterResultV1(
    PricingEstimateSourceAdapterStatusV1 Status,
    string? ReasonCode,
    PricingEstimateSourceAdapterFactsV1? Facts)
{
    internal static PricingEstimateSourceAdapterResultV1 Available(
        PricingEstimateSourceAdapterFactsV1 facts) =>
        new(PricingEstimateSourceAdapterStatusV1.Available, null, facts);

    internal static PricingEstimateSourceAdapterResultV1 Unavailable(string reasonCode) =>
        new(PricingEstimateSourceAdapterStatusV1.Unavailable, reasonCode, null);

    internal static PricingEstimateSourceAdapterResultV1 Failed() =>
        new(PricingEstimateSourceAdapterStatusV1.Failed, "source_adapter_failed", null);
}

internal interface IPricingEstimateSourceAdapterV1
{
    PricingEstimateSourceAdapterResultV1 Acquire(
        PricingEstimateSourceAdapterRequestV1 request);
}

internal sealed class DefaultPricingEstimateSourceAdapterV1
    : IPricingEstimateSourceAdapterV1
{
    internal static DefaultPricingEstimateSourceAdapterV1 Instance { get; } = new();

    private DefaultPricingEstimateSourceAdapterV1() { }

    public PricingEstimateSourceAdapterResultV1 Acquire(
        PricingEstimateSourceAdapterRequestV1 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return PricingEstimateSourceAdapterResultV1.Unavailable(
            request.SourceSurface == "codex-app"
                ? "codex_adapter_unavailable"
                : "source_adapter_unavailable");
    }
}

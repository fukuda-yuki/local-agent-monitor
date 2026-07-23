using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Pricing;

public static class PricingEstimateConsumer
{
    private static readonly Regex EstimateIdPattern =
        new(
            "^pricing-estimate-[0-9a-f]{64}$",
            RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Pattern =
        new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownStatuses =
        new(StringComparer.Ordinal)
        {
            PricingEstimateStatuses.Estimated,
            PricingEstimateStatuses.Partial,
            PricingEstimateStatuses.NotEstimable
        };
    private static readonly HashSet<string> KnownReasons =
        new(StringComparer.Ordinal)
        {
            PricingEstimateReasons.UnknownModel,
            PricingEstimateReasons.UnknownBillingMode,
            PricingEstimateReasons.SubscriptionAllocationUnknown,
            PricingEstimateReasons.SubscriptionOrContractUnknown,
            PricingEstimateReasons.CustomContract,
            PricingEstimateReasons.MissingTokenCategory,
            PricingEstimateReasons.UnsupportedProviderRoute,
            PricingEstimateReasons.PartialSource,
            PricingEstimateReasons.RegistryOutOfDate,
            PricingEstimateReasons.OutsideEffectiveRange
        };
    private static readonly IReadOnlyList<string> KnownReasonOrder =
    [
        PricingEstimateReasons.UnknownModel,
        PricingEstimateReasons.UnknownBillingMode,
        PricingEstimateReasons.SubscriptionAllocationUnknown,
        PricingEstimateReasons.SubscriptionOrContractUnknown,
        PricingEstimateReasons.CustomContract,
        PricingEstimateReasons.MissingTokenCategory,
        PricingEstimateReasons.UnsupportedProviderRoute,
        PricingEstimateReasons.PartialSource,
        PricingEstimateReasons.RegistryOutOfDate,
        PricingEstimateReasons.OutsideEffectiveRange
    ];
    private static readonly IReadOnlyList<string> KnownComponentOrder =
    [
        "included_zero_incremental_cost",
        "input_tokens",
        "output_tokens",
        "cache_read_tokens",
        "cache_write_5m_tokens",
        "cache_write_1h_tokens",
        "reasoning_tokens",
        "requests",
        "request_credits",
        "credits"
    ];
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 32
    };

    public static PricingEstimateRecord Deserialize(
        ReadOnlySpan<byte> canonicalJson,
        PricingCatalog exactCatalog)
    {
        ArgumentNullException.ThrowIfNull(exactCatalog);
        if (canonicalJson.Length is 0 or > PricingContractLimits.MaximumEstimateBytes)
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate bytes are empty or exceed the v1 bound.");
        }

        try
        {
            var bytes = canonicalJson.ToArray();
            using var json = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            RejectDuplicateProperties(json.RootElement);
            var record = JsonSerializer.Deserialize<PricingEstimateRecord>(
                bytes,
                Options)
                ?? throw new PricingEstimateValidationException(
                    "Pricing estimate JSON is null.");
            Validate(record);
            var frozen = Freeze(record);
            if (!bytes.AsSpan().SequenceEqual(PricingCanonicalJson.Serialize(frozen)))
            {
                throw new PricingEstimateValidationException(
                    "Pricing estimate JSON is not canonical v1 bytes.");
            }

            if (!PricingCanonicalJson.HasValidIdentity(frozen))
            {
                throw new PricingEstimateValidationException(
                    "Pricing estimate identity does not match its canonical content.");
            }
            if (!string.Equals(
                    frozen.CatalogSha256,
                    exactCatalog.CatalogSha256,
                    StringComparison.Ordinal))
            {
                throw new PricingEstimateValidationException(
                    "Pricing estimate catalog snapshot identity does not match.");
            }

            var recomputed = new PricingEstimationEngine(exactCatalog).Estimate(
                new PricingEstimateRequest(
                    PricingContractVersions.EstimateRequest,
                    frozen.CalculationTimeUtc,
                    frozen.SupersedesEstimateId,
                    frozen.Source,
                    frozen.Usage));
            if (!bytes.AsSpan().SequenceEqual(
                    PricingCanonicalJson.Serialize(recomputed)))
            {
                throw new PricingEstimateValidationException(
                    "Pricing estimate does not match the supplied exact catalog and calculation.");
            }

            return frozen;
        }
        catch (PricingEstimateValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or InvalidOperationException
                or OverflowException)
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate JSON is invalid.");
        }
    }

    private static void Validate(PricingEstimateRecord record)
    {
        if (record.SchemaVersion != PricingContractVersions.Estimate
            || record.CanonicalVersion != PricingContractVersions.CanonicalJson
            || !Sha256Pattern.IsMatch(record.CatalogSha256)
            || !EstimateIdPattern.IsMatch(record.EstimateId)
            || !KnownStatuses.Contains(record.Status)
            || record.CalculationTimeUtc.Offset != TimeSpan.Zero
            || record.Components is null
            || record.Coverage is null
            || record.Reasons is null
            || record.Rounding is null
            || record.Source is null
            || record.Usage is null
            || record.Coverage.RequiredCategories is null
            || record.Coverage.EstimatedCategories is null
            || record.Coverage.MissingCategories is null
            || record.Components.Any(component => component is null)
            || record.Reasons.Any(reason => !KnownReasons.Contains(reason))
            || record.Reasons.Distinct(StringComparer.Ordinal).Count() != record.Reasons.Count)
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate contract fields are invalid.");
        }
        var reasonSet = record.Reasons.ToHashSet(StringComparer.Ordinal);
        if (!record.Reasons.SequenceEqual(
                KnownReasonOrder.Where(reasonSet.Contains),
                StringComparer.Ordinal))
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate reasons are not in canonical order.");
        }

        PricingEstimationEngine.ValidateRequest(new PricingEstimateRequest(
            PricingContractVersions.EstimateRequest,
            record.CalculationTimeUtc,
            record.SupersedesEstimateId,
            record.Source,
            record.Usage));
        var completenessReasonSet = record.Source.CompletenessReasons
            .ToHashSet(StringComparer.Ordinal);
        if (!record.Source.CompletenessReasons.SequenceEqual(
                PricingSourceCompletenessReasons.Ordered
                    .Where(completenessReasonSet.Contains),
                StringComparer.Ordinal))
        {
            throw new PricingEstimateValidationException(
                "Pricing source completeness reasons are not in canonical order.");
        }

        if (record.Rounding.IntermediatePolicy != PricingContractVersions.NoIntermediateRounding
            || record.Rounding.DisplayPolicyVersion != PricingContractVersions.DisplayRounding
            || record.Rounding.DisplayMode != "half_even")
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate rounding metadata is invalid.");
        }

        var required = record.Components.Select(component => component.Category).ToArray();
        var estimated = record.Components
            .Where(component => component.Amount is not null)
            .Select(component => component.Category)
            .ToArray();
        var missing = record.Components
            .Where(component => component.Amount is null)
            .Select(component => component.Category)
            .ToArray();
        if (!required.SequenceEqual(record.Coverage.RequiredCategories, StringComparer.Ordinal)
            || !estimated.SequenceEqual(record.Coverage.EstimatedCategories, StringComparer.Ordinal)
            || !missing.SequenceEqual(record.Coverage.MissingCategories, StringComparer.Ordinal)
            || required.Distinct(StringComparer.Ordinal).Count() != required.Length)
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate coverage does not match its components.");
        }
        var componentSet = required.ToHashSet(StringComparer.Ordinal);
        if (!required.SequenceEqual(
                KnownComponentOrder.Where(componentSet.Contains),
                StringComparer.Ordinal))
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate components are not in canonical order.");
        }

        if (record.Components.Any(component =>
                string.IsNullOrWhiteSpace(component.Category)
                || string.IsNullOrWhiteSpace(component.Unit)
                || component.Quantity is < 0
                || component.Rate is < 0
                || component.Amount is < 0
                || (component.Amount is null) != (component.MissingReason is not null)
                || (component.Amount is not null) != (component.SourceProvenance is not null)))
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate components are invalid.");
        }
        foreach (var component in record.Components)
        {
            if (component.SourceProvenance is not null)
            {
                PricingEstimationEngine.ValidateProvenance(component.SourceProvenance);
            }

            if (component.Amount is { } amount
                && component.Category != "included_zero_incremental_cost")
            {
                var divisor = component.Unit == "tokens" ? 1_000_000m : 1m;
                var expected = PricingExactDecimal.Multiply(
                    component.Quantity!.Value,
                    component.Rate!.Value,
                    divisor == 1_000_000m ? 6 : 0);
                if (amount != expected)
                {
                    throw new PricingEstimateValidationException(
                        "Pricing estimate component arithmetic is invalid.");
                }
            }
        }

        var computedAmount = record.Components
            .Where(component => component.Amount is not null)
            .Select(component => component.Amount!.Value)
            .ToArray();
        if (record.Status == PricingEstimateStatuses.NotEstimable)
        {
            if (record.Amount is not null
                || record.Currency is not null
                || (record.Registry is null
                    ? record.Components.Count != 0
                        || record.Reasons.Count != 1
                        || record.Rounding.CurrencyMinorUnits is not null
                    : record.Components.Count == 0
                        || estimated.Length != 0
                        || missing.Length != record.Components.Count
                        || !record.Reasons.Contains(
                            PricingEstimateReasons.MissingTokenCategory,
                            StringComparer.Ordinal)
                        || record.Rounding.CurrencyMinorUnits != 2))
            {
                throw new PricingEstimateValidationException(
                    "Not-estimable pricing record shape is invalid.");
            }
        }
        else if (record.Amount is null
                 || record.Amount != PricingExactDecimal.Sum(computedAmount)
                 || record.Currency != "USD"
                 || record.Registry is null
                 || record.Rounding.CurrencyMinorUnits != 2
                 || record.Components.Count == 0
                 || (record.Status == PricingEstimateStatuses.Estimated
                     && (record.Reasons.Count != 0 || missing.Length != 0))
                 || (record.Status == PricingEstimateStatuses.Partial
                     && record.Reasons.Count == 0))
        {
            throw new PricingEstimateValidationException(
                "Priced estimate record shape is invalid.");
        }

        if (record.Registry is { } registry
            && (registry.SchemaVersion != PricingContractVersions.Registry
                || registry.Currency != (record.Currency ?? "USD")
                || registry.MatchedModelId != record.Source.ModelId
                || registry.BillingMode != record.Source.BillingMode
                || registry.PricingRoute != record.Source.PricingRoute
                || registry.LastReviewedDate == default
                || registry.EffectiveFromUtc == default
                || registry.EffectiveFromUtc.Offset != TimeSpan.Zero
                || registry.EffectiveToUtc is { Offset: not { Ticks: 0 } }
                || registry.EffectiveFromUtc > record.Source.SessionObservedAtUtc
                || registry.EffectiveToUtc is { } effectiveTo
                    && record.Source.SessionObservedAtUtc >= effectiveTo))
        {
            throw new PricingEstimateValidationException(
                "Pricing estimate registry provenance is invalid.");
        }
    }

    private static PricingEstimateRecord Freeze(PricingEstimateRecord record)
    {
        static PricingValueProvenance? FreezeProvenance(PricingValueProvenance? value) =>
            value is null ? null : value with { };
        static PricingQuantity? FreezeQuantity(PricingQuantity? value) =>
            value is null
                ? null
                : value with { Provenance = FreezeProvenance(value.Provenance)! };

        var source = record.Source with
        {
            CompletenessReasons = Array.AsReadOnly(
                record.Source.CompletenessReasons.ToArray()),
            SessionTimeProvenance = FreezeProvenance(record.Source.SessionTimeProvenance)!,
            ProviderProvenance = FreezeProvenance(record.Source.ProviderProvenance)!,
            ModelProvenance = FreezeProvenance(record.Source.ModelProvenance)!,
            BillingModeProvenance = FreezeProvenance(record.Source.BillingModeProvenance)!,
            PricingRouteProvenance = FreezeProvenance(record.Source.PricingRouteProvenance)!
        };
        var usage = record.Usage with
        {
            InputTokens = FreezeQuantity(record.Usage.InputTokens),
            OutputTokens = FreezeQuantity(record.Usage.OutputTokens),
            CacheReadTokens = FreezeQuantity(record.Usage.CacheReadTokens),
            CacheWrite5mTokens = FreezeQuantity(record.Usage.CacheWrite5mTokens),
            CacheWrite1hTokens = FreezeQuantity(record.Usage.CacheWrite1hTokens),
            ReasoningTokens = FreezeQuantity(record.Usage.ReasoningTokens),
            RequestCount = FreezeQuantity(record.Usage.RequestCount),
            CreditCount = FreezeQuantity(record.Usage.CreditCount)
        };
        return record with
        {
            Components = Array.AsReadOnly(record.Components
                .Select(component => component with
                {
                    SourceProvenance = FreezeProvenance(component.SourceProvenance)
                })
                .ToArray()),
            Coverage = record.Coverage with
            {
                RequiredCategories = Array.AsReadOnly(
                    record.Coverage.RequiredCategories.ToArray()),
                EstimatedCategories = Array.AsReadOnly(
                    record.Coverage.EstimatedCategories.ToArray()),
                MissingCategories = Array.AsReadOnly(
                    record.Coverage.MissingCategories.ToArray())
            },
            Reasons = Array.AsReadOnly(record.Reasons.ToArray()),
            Registry = record.Registry is null ? null : record.Registry with { },
            Source = source,
            Usage = usage,
            Rounding = record.Rounding with { }
        };
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new PricingEstimateValidationException(
                        "Pricing estimate JSON contains a duplicate property.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}

public sealed class PricingEstimateValidationException : Exception
{
    public PricingEstimateValidationException(string message)
        : base(message)
    {
    }
}

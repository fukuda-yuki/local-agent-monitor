namespace CopilotAgentObservability.Pricing;

public sealed class PricingEstimationEngine
{
    private const decimal MaximumUsageQuantity = 1_000_000_000_000_000_000m;
    private const decimal MaximumRequestCount = 1_000_000_000_000m;
    private const decimal MinimumFractionalCredit = 0.000001m;

    private static readonly IReadOnlyList<string> ReasonOrder =
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

    private readonly PricingCatalog _catalog;
    private static readonly System.Text.RegularExpressions.Regex SafeTokenPattern =
        new(
            "^[A-Za-z0-9][A-Za-z0-9._:-]{0,255}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex EstimateIdPattern =
        new(
            "^pricing-estimate-[0-9a-f]{64}$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly IReadOnlyDictionary<string, int> CompletenessRanks =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [PricingSourceCompleteness.Unbound] = 0,
            [PricingSourceCompleteness.Partial] = 1,
            [PricingSourceCompleteness.Rich] = 2,
            [PricingSourceCompleteness.Full] = 3
        };
    private static readonly IReadOnlyDictionary<string, int> CompletenessReasonCeilings =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [PricingSourceCompletenessReasons.MissingNativeSessionId] = 0,
            [PricingSourceCompletenessReasons.MissingTraceContext] = 2,
            [PricingSourceCompletenessReasons.TraceSignalDisabled] = 2,
            [PricingSourceCompletenessReasons.ContentCaptureDisabled] = 2,
            [PricingSourceCompletenessReasons.UnsupportedSourceVersion] = 2,
            [PricingSourceCompletenessReasons.IngestGap] = 2,
            [PricingSourceCompletenessReasons.HookOnly] = 2,
            [PricingSourceCompletenessReasons.HistoricalSummaryOnly] = 1,
            [PricingSourceCompletenessReasons.UnknownSpanKind] = 2,
            [PricingSourceCompletenessReasons.SchemaDriftDetected] = 1,
            [PricingSourceCompletenessReasons.PlannedSourceNotEnabled] = 0
        };

    public PricingEstimationEngine(PricingCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public PricingEstimateRecord Estimate(PricingEstimateRequest request)
    {
        request = SnapshotRequestCollections(request);
        ValidateRequest(request);
        request = FreezeRequest(request);

        if (!PricingCatalog.IsSupportedProviderMode(
                request.Source.Provider,
                request.Source.BillingMode))
        {
            return NotEstimable(
                request,
                PricingEstimateReasons.UnsupportedProviderRoute);
        }

        if (!PricingCatalog.IsSupportedProviderModeRoute(
                request.Source.Provider,
                request.Source.BillingMode,
                request.Source.PricingRoute))
        {
            return NotEstimable(
                request,
                PricingEstimateReasons.UnsupportedProviderRoute);
        }

        if (request.Source.Provider == PricingProviders.CodexApp)
        {
            return NotEstimable(
                request,
                PricingEstimateReasons.SubscriptionOrContractUnknown);
        }

        if (request.Source.BillingMode == PricingBillingModes.Unknown)
        {
            return NotEstimable(request, PricingEstimateReasons.UnknownBillingMode);
        }

        if (request.Source.BillingMode == PricingBillingModes.Subscription)
        {
            return NotEstimable(
                request,
                PricingEstimateReasons.SubscriptionAllocationUnknown);
        }

        if (request.Source.BillingMode == PricingBillingModes.CustomEnterprise)
        {
            return NotEstimable(request, PricingEstimateReasons.CustomContract);
        }

        var selection = _catalog.TrySelect(
            request.Source.Provider,
            request.Source.ModelId,
            request.Source.BillingMode,
            request.Source.PricingRoute,
            request.Source.SessionObservedAtUtc);

        if (selection is null)
        {
            if (_catalog.HasExactTuple(
                    request.Source.Provider,
                    request.Source.ModelId,
                    request.Source.BillingMode,
                    request.Source.PricingRoute))
            {
                return NotEstimable(
                    request,
                    PricingEstimateReasons.OutsideEffectiveRange);
            }

            if (_catalog.HasExactModel(request.Source.Provider, request.Source.ModelId))
            {
                return NotEstimable(
                    request,
                    PricingEstimateReasons.UnsupportedProviderRoute);
            }

            return NotEstimable(request, PricingEstimateReasons.UnknownModel);
        }

        return EstimateSelected(request, selection, _catalog.CatalogSha256);
    }

    private static PricingEstimateRecord EstimateSelected(
        PricingEstimateRequest request,
        PricingRegistrySelection selection,
        string catalogSha256)
    {
        var entry = selection.Entry;
        var components = new List<PricingEstimateComponent>();
        var required = new List<string>();
        var estimated = new List<string>();
        var missing = new List<string>();
        var reasons = new HashSet<string>(StringComparer.Ordinal);

        if (entry.IncludedZeroIncrementalCost)
        {
            const string includedCategory = "included_zero_incremental_cost";
            required.Add(includedCategory);
            estimated.Add(includedCategory);
            components.Add(new PricingEstimateComponent(
                includedCategory,
                1m,
                "included_rule",
                0m,
                0m,
                request.Source.BillingModeProvenance,
                null));
        }
        else
        {
            AddTokenComponent(
                "input_tokens",
                entry.Rates.InputPerMillionTokens,
                request.Usage.InputTokens,
                components,
                required,
                estimated,
                missing,
                reasons);
            AddTokenComponent(
                "output_tokens",
                entry.Rates.OutputPerMillionTokens,
                request.Usage.OutputTokens,
                components,
                required,
                estimated,
                missing,
                reasons);
            AddTokenComponent(
                "cache_read_tokens",
                entry.Rates.CacheReadPerMillionTokens,
                request.Usage.CacheReadTokens,
                components,
                required,
                estimated,
                missing,
                reasons);
            AddTokenComponent(
                "cache_write_5m_tokens",
                entry.Rates.CacheWrite5mPerMillionTokens,
                request.Usage.CacheWrite5mTokens,
                components,
                required,
                estimated,
                missing,
                reasons);
            AddTokenComponent(
                "cache_write_1h_tokens",
                entry.Rates.CacheWrite1hPerMillionTokens,
                request.Usage.CacheWrite1hTokens,
                components,
                required,
                estimated,
                missing,
                reasons);
            AddTokenComponent(
                "reasoning_tokens",
                entry.Rates.ReasoningPerMillionTokens,
                request.Usage.ReasoningTokens,
                components,
                required,
                estimated,
                missing,
                reasons);

            if (entry.Rates.PerRequest is { } requestRate)
            {
                AddUnitComponent(
                    "requests",
                    requestRate,
                    request.Usage.RequestCount,
                    "requests",
                    components,
                    required,
                    estimated,
                    missing,
                    reasons);
            }
            else if (entry.Rates.PerCredit is { } creditRate
                     && entry.Rates.RequestCreditMultiplier is { } multiplier)
            {
                AddRequestCreditComponent(
                    creditRate,
                    multiplier,
                    request.Usage.RequestCount,
                    components,
                    required,
                    estimated,
                    missing,
                    reasons);
            }
            else if (entry.Rates.PerCredit is { } directCreditRate)
            {
                AddUnitComponent(
                    "credits",
                    directCreditRate,
                    request.Usage.CreditCount,
                    "credits",
                    components,
                    required,
                    estimated,
                    missing,
                    reasons);
            }
        }

        if (request.Source.Completeness != PricingSourceCompleteness.Full)
        {
            reasons.Add(PricingEstimateReasons.PartialSource);
        }

        if (DateOnly.FromDateTime(request.CalculationTimeUtc.UtcDateTime)
            > selection.Document.StaleAfterDate)
        {
            reasons.Add(PricingEstimateReasons.RegistryOutOfDate);
        }

        var amountComponents = components
            .Where(component => component.Amount is not null)
            .Select(component => component.Amount!.Value)
            .ToArray();
        decimal? amount = amountComponents.Length == 0
            ? null
            : PricingExactDecimal.Sum(amountComponents);
        var status = amount is null
            ? PricingEstimateStatuses.NotEstimable
            : reasons.Count == 0
                ? PricingEstimateStatuses.Estimated
                : PricingEstimateStatuses.Partial;

        var record = new PricingEstimateRecord(
            PricingContractVersions.Estimate,
            catalogSha256,
            string.Empty,
            request.SupersedesEstimateId,
            request.CalculationTimeUtc,
            status,
            amount,
            amount is null ? null : entry.Currency,
            components.AsReadOnly(),
            new PricingEstimateCoverage(
                required.AsReadOnly(),
                estimated.AsReadOnly(),
                missing.AsReadOnly()),
            SortReasons(reasons),
            new PricingRegistryProvenance(
                selection.Document.SchemaVersion,
                selection.Document.RegistryVersion,
                selection.Document.SourceKind,
                selection.Document.SourceId,
                selection.Document.SourceLabel,
                selection.EntryKey,
                entry.SourceReference,
                entry.LastReviewedDate,
                entry.CanonicalModelId,
                request.Source.ModelId,
                entry.BillingMode,
                entry.PricingRoute,
                entry.EffectiveFromUtc,
                entry.EffectiveToUtc,
                entry.Currency),
            request.Source,
            request.Usage,
            new PricingRoundingPolicy(
                PricingContractVersions.NoIntermediateRounding,
                PricingContractVersions.DisplayRounding,
                "half_even",
                entry.CurrencyMinorUnits),
            PricingContractVersions.CanonicalJson);

        return PricingCanonicalJson.WithIdentity(record);
    }

    private PricingEstimateRecord NotEstimable(
        PricingEstimateRequest request,
        string reason)
    {
        var record = new PricingEstimateRecord(
            PricingContractVersions.Estimate,
            _catalog.CatalogSha256,
            string.Empty,
            request.SupersedesEstimateId,
            request.CalculationTimeUtc,
            PricingEstimateStatuses.NotEstimable,
            null,
            null,
            [],
            new PricingEstimateCoverage([], [], []),
            SortReasons([reason]),
            null,
            request.Source,
            request.Usage,
            new PricingRoundingPolicy(
                PricingContractVersions.NoIntermediateRounding,
                PricingContractVersions.DisplayRounding,
                "half_even",
                null),
            PricingContractVersions.CanonicalJson);

        return PricingCanonicalJson.WithIdentity(record);
    }

    private static void AddTokenComponent(
        string category,
        decimal? rate,
        PricingQuantity? quantity,
        ICollection<PricingEstimateComponent> components,
        ICollection<string> required,
        ICollection<string> estimated,
        ICollection<string> missing,
        ISet<string> reasons)
    {
        if (rate is null)
        {
            return;
        }

        AddComponent(
            category,
            rate.Value,
            quantity,
            "tokens",
            value => CheckedAmount(value, rate.Value, divideByMillion: true),
            components,
            required,
            estimated,
            missing,
            reasons);
    }

    private static void AddUnitComponent(
        string category,
        decimal rate,
        PricingQuantity? quantity,
        string unit,
        ICollection<PricingEstimateComponent> components,
        ICollection<string> required,
        ICollection<string> estimated,
        ICollection<string> missing,
        ISet<string> reasons) =>
        AddComponent(
            category,
            rate,
            quantity,
            unit,
            value => CheckedAmount(value, rate, divideByMillion: false),
            components,
            required,
            estimated,
            missing,
            reasons);

    private static void AddRequestCreditComponent(
        decimal creditRate,
        decimal multiplier,
        PricingQuantity? requestCount,
        ICollection<PricingEstimateComponent> components,
        ICollection<string> required,
        ICollection<string> estimated,
        ICollection<string> missing,
        ISet<string> reasons)
    {
        const string category = "request_credits";
        required.Add(category);
        if (requestCount is null)
        {
            missing.Add(category);
            reasons.Add(PricingEstimateReasons.MissingTokenCategory);
            components.Add(new PricingEstimateComponent(
                category,
                null,
                "credits",
                creditRate,
                null,
                null,
                PricingEstimateReasons.MissingTokenCategory));
            return;
        }

        var credits = CheckedAmount(
            requestCount.Value,
            multiplier,
            divideByMillion: false);
        estimated.Add(category);
        components.Add(new PricingEstimateComponent(
            category,
            credits,
            "credits",
            creditRate,
            CheckedAmount(credits, creditRate, divideByMillion: false),
            requestCount.Provenance,
            null));
    }

    private static void AddComponent(
        string category,
        decimal rate,
        PricingQuantity? quantity,
        string unit,
        Func<decimal, decimal> amount,
        ICollection<PricingEstimateComponent> components,
        ICollection<string> required,
        ICollection<string> estimated,
        ICollection<string> missing,
        ISet<string> reasons)
    {
        required.Add(category);
        if (quantity is null)
        {
            missing.Add(category);
            reasons.Add(PricingEstimateReasons.MissingTokenCategory);
            components.Add(new PricingEstimateComponent(
                category,
                null,
                unit,
                rate,
                null,
                null,
                PricingEstimateReasons.MissingTokenCategory));
            return;
        }

        estimated.Add(category);
        components.Add(new PricingEstimateComponent(
            category,
            quantity.Value,
            unit,
            rate,
            amount(quantity.Value),
            quantity.Provenance,
            null));
    }

    private static IReadOnlyList<string> SortReasons(IEnumerable<string> reasons)
    {
        var set = reasons.ToHashSet(StringComparer.Ordinal);
        return Array.AsReadOnly(ReasonOrder.Where(set.Contains).ToArray());
    }

    internal static void ValidateRequest(PricingEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Usage);

        if (request.SchemaVersion != PricingContractVersions.EstimateRequest)
        {
            throw new ArgumentException(
                "The pricing estimate request schema is unsupported.",
                nameof(request));
        }

        RequireUtc(request.CalculationTimeUtc, nameof(request.CalculationTimeUtc));
        RequireUtc(request.Source.SessionObservedAtUtc, nameof(request.Source.SessionObservedAtUtc));
        RequireSafeToken(request.Source.SourceSurface, nameof(request.Source.SourceSurface));
        RequireSafeToken(request.Source.SourceVersion, nameof(request.Source.SourceVersion));
        RequireSafeToken(request.Source.SessionId, nameof(request.Source.SessionId));
        if (!PricingCatalog.IsKnownProvider(request.Source.Provider))
        {
            throw new ArgumentException(
                "The pricing provider token is unknown.",
                nameof(request));
        }

        RequireSafeLabel(request.Source.ModelId, nameof(request.Source.ModelId), 256);
        if (!PricingCatalog.IsKnownBillingMode(request.Source.BillingMode))
        {
            throw new ArgumentException(
                "The pricing billing-mode token is unknown.",
                nameof(request));
        }

        if (!PricingCatalog.IsKnownPricingRoute(request.Source.PricingRoute))
        {
            throw new ArgumentException(
                "The pricing route token is unknown.",
                nameof(request));
        }

        if (request.SupersedesEstimateId is { } predecessor
            && !EstimateIdPattern.IsMatch(predecessor))
        {
            throw new ArgumentException(
                "Pricing predecessor ID must be pricing-estimate- plus 64 lowercase hex characters.",
                nameof(request));
        }

        if (request.Source.Completeness is not PricingSourceCompleteness.Unbound
            and not PricingSourceCompleteness.Partial
            and not PricingSourceCompleteness.Rich
            and not PricingSourceCompleteness.Full)
        {
            throw new ArgumentException(
                "The pricing source completeness token is unknown.",
                nameof(request));
        }

        if (request.Source.Completeness == PricingSourceCompleteness.Full
            && request.Source.CompletenessReasons is { Count: not 0 })
        {
            throw new ArgumentException(
                "Full pricing source completeness cannot include incomplete-source reasons.",
                nameof(request));
        }

        ValidateProvenance(request.Source.SessionTimeProvenance);
        ValidateProvenance(request.Source.ProviderProvenance);
        ValidateProvenance(request.Source.ModelProvenance);
        ValidateProvenance(request.Source.BillingModeProvenance);
        ValidateProvenance(request.Source.PricingRouteProvenance);

        if (request.Source.CompletenessReasons is null
            || request.Source.CompletenessReasons.Count
                > PricingSourceCompletenessReasons.Ordered.Count
            || request.Source.CompletenessReasons
                .Distinct(StringComparer.Ordinal)
                .Count() != request.Source.CompletenessReasons.Count
            || request.Source.CompletenessReasons.Any(reason =>
                reason is null || !CompletenessReasonCeilings.ContainsKey(reason)))
        {
            throw new ArgumentException(
                "Pricing source completeness reasons must use unique fixed codes within the v1 bound.",
                nameof(request));
        }
        var completenessRank = CompletenessRanks[request.Source.Completeness];
        if (request.Source.CompletenessReasons.Any(reason =>
                completenessRank > CompletenessReasonCeilings[reason]))
        {
            throw new ArgumentException(
                "Pricing source completeness exceeds a declared reason ceiling.",
                nameof(request));
        }

        foreach (var quantity in Quantities(request.Usage))
        {
            if (quantity.Value < 0 || quantity.Value > MaximumUsageQuantity)
            {
                throw new ArgumentException(
                    "Pricing usage quantities must be non-negative and within the v1 bound.",
                    nameof(request));
            }

            ValidateProvenance(quantity.Provenance);
        }

        ValidateIntegralQuantity(request.Usage.InputTokens, "input_tokens");
        ValidateIntegralQuantity(request.Usage.OutputTokens, "output_tokens");
        ValidateIntegralQuantity(request.Usage.CacheReadTokens, "cache_read_tokens");
        ValidateIntegralQuantity(request.Usage.CacheWrite5mTokens, "cache_write_5m_tokens");
        ValidateIntegralQuantity(request.Usage.CacheWrite1hTokens, "cache_write_1h_tokens");
        ValidateIntegralQuantity(request.Usage.ReasoningTokens, "reasoning_tokens");
        ValidateIntegralQuantity(request.Usage.RequestCount, "request_count");
        if (request.Usage.RequestCount is { Value: > MaximumRequestCount })
        {
            throw new ArgumentException(
                "Pricing request count exceeds the v1 bound.",
                nameof(request));
        }
        if (request.Usage.CreditCount is { Value: > 0 and < MinimumFractionalCredit })
        {
            throw new ArgumentException(
                "Positive pricing credit count is below the v1 precision bound.",
                nameof(request));
        }
        if (request.Usage.CreditCount is { Value: var creditCount }
            && PricingExactDecimal.Scale(creditCount) > 6)
        {
            throw new ArgumentException(
                "Pricing credit count exceeds the v1 decimal scale bound.",
                nameof(request));
        }
    }

    private static IEnumerable<PricingQuantity> Quantities(PricingUsage usage)
    {
        if (usage.InputTokens is { } input) yield return input;
        if (usage.OutputTokens is { } output) yield return output;
        if (usage.CacheReadTokens is { } cacheRead) yield return cacheRead;
        if (usage.CacheWrite5mTokens is { } cacheWrite5m) yield return cacheWrite5m;
        if (usage.CacheWrite1hTokens is { } cacheWrite1h) yield return cacheWrite1h;
        if (usage.ReasoningTokens is { } reasoning) yield return reasoning;
        if (usage.RequestCount is { } requests) yield return requests;
        if (usage.CreditCount is { } credits) yield return credits;
    }

    internal static void ValidateProvenance(PricingValueProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        RequireSafeToken(provenance.SourceAdapter, nameof(provenance.SourceAdapter));
        RequireSafeToken(
            provenance.SourceVersionOrSchemaFingerprint,
            nameof(provenance.SourceVersionOrSchemaFingerprint));
        RequireSafeToken(
            provenance.SourceEventOrTraceSpanId,
            nameof(provenance.SourceEventOrTraceSpanId));
        RequireSafeToken(provenance.CaptureContentState, nameof(provenance.CaptureContentState));
        RequireSafeToken(provenance.NormalizationVersion, nameof(provenance.NormalizationVersion));
    }

    private static void ValidateIntegralQuantity(
        PricingQuantity? quantity,
        string category)
    {
        if (quantity is { Value: var value } && decimal.Truncate(value) != value)
        {
            throw new ArgumentException(
                "Pricing token and request quantities must be integers.");
        }
    }

    private static void RequireSafeToken(string? value, string field)
    {
        if (value is null
            || value is "." or ".."
            || !PricingSafeText.IsWellFormedUtf16(value)
            || !SafeTokenPattern.IsMatch(value)
            || LooksLikeCredential(value))
        {
            throw new ArgumentException(
                $"Pricing estimate field '{field}' must be a bounded repository-safe token.");
        }
    }

    private static void RequireSafeLabel(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !PricingSafeText.IsWellFormedUtf16(value)
            || value is "." or ".."
            || value.Any(char.IsControl)
            || value.Contains("://", StringComparison.Ordinal)
            || value.Contains('/')
            || value.Contains('\\')
            || PricingSafeText.ContainsEmail(value)
            || LooksLikeCredential(value)
            || Path.IsPathRooted(value))
        {
            throw new ArgumentException(
                $"Pricing estimate field '{field}' must be bounded repository-safe text.");
        }
    }

    private static bool LooksLikeCredential(string value) =>
        PricingSafeText.ContainsCredentialMarker(value);

    private static void RequireUtc(DateTimeOffset value, string field)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"Pricing estimate field '{field}' must be UTC.");
        }
    }

    private static decimal CheckedAmount(
        decimal quantity,
        decimal rate,
        bool divideByMillion)
    {
        return PricingExactDecimal.Multiply(
            quantity,
            rate,
            divideByMillion ? 6 : 0);
    }

    private static PricingEstimateRequest FreezeRequest(PricingEstimateRequest request)
    {
        static PricingValueProvenance FreezeProvenance(PricingValueProvenance value) =>
            value with { };
        static PricingQuantity? FreezeQuantity(PricingQuantity? quantity) =>
            quantity is null
                ? null
                : quantity with { Provenance = FreezeProvenance(quantity.Provenance) };

        var source = request.Source with
        {
            CompletenessReasons = Array.AsReadOnly(
                PricingSourceCompletenessReasons.Ordered
                    .Where(request.Source.CompletenessReasons.Contains)
                    .ToArray()),
            SessionTimeProvenance = FreezeProvenance(request.Source.SessionTimeProvenance),
            ProviderProvenance = FreezeProvenance(request.Source.ProviderProvenance),
            ModelProvenance = FreezeProvenance(request.Source.ModelProvenance),
            BillingModeProvenance = FreezeProvenance(request.Source.BillingModeProvenance),
            PricingRouteProvenance = FreezeProvenance(request.Source.PricingRouteProvenance)
        };
        var usage = request.Usage with
        {
            InputTokens = FreezeQuantity(request.Usage.InputTokens),
            OutputTokens = FreezeQuantity(request.Usage.OutputTokens),
            CacheReadTokens = FreezeQuantity(request.Usage.CacheReadTokens),
            CacheWrite5mTokens = FreezeQuantity(request.Usage.CacheWrite5mTokens),
            CacheWrite1hTokens = FreezeQuantity(request.Usage.CacheWrite1hTokens),
            ReasoningTokens = FreezeQuantity(request.Usage.ReasoningTokens),
            RequestCount = FreezeQuantity(request.Usage.RequestCount),
            CreditCount = FreezeQuantity(request.Usage.CreditCount)
        };
        return request with { Source = source, Usage = usage };
    }

    private static PricingEstimateRequest SnapshotRequestCollections(
        PricingEstimateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Usage);
        var reasons = request.Source.CompletenessReasons is null
            ? null!
            : Array.AsReadOnly(request.Source.CompletenessReasons.ToArray());
        return request with
        {
            Source = request.Source with { CompletenessReasons = reasons }
        };
    }
}

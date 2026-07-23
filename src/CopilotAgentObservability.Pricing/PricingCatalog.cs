using System.Net;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Pricing;

public sealed record PricingCatalogEntry(
    string EntryKey,
    PricingRegistryDocument Document,
    PricingRegistryEntry Entry);

public sealed record PricingRegistrySelection(
    string EntryKey,
    PricingRegistryDocument Document,
    PricingRegistryEntry Entry);

public sealed class PricingCatalog
{
    public const decimal MinimumRate = 0.000001m;
    public const decimal MaximumRate = 1_000_000m;

    private static readonly Regex TokenPattern =
        new("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    private static readonly Regex EntryKeyPattern =
        new(
            "^[a-z0-9][a-z0-9._-]{0,127}:[a-z0-9][a-z0-9._-]{0,127}@[1-9][0-9]{0,9}$",
            RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SupportedProviders =
        new(StringComparer.Ordinal)
        {
            PricingProviders.GitHubCopilot,
            PricingProviders.ClaudeCode,
            PricingProviders.CodexApp
        };

    private static readonly HashSet<string> KnownRoutes =
        new(StringComparer.Ordinal)
        {
            PricingRoutes.CreditConsumingInteraction,
            PricingRoutes.LegacyRequest,
            PricingRoutes.CodeCompletion,
            PricingRoutes.NextEditSuggestion,
            PricingRoutes.StandardGlobal,
            PricingRoutes.UsOnlyInference,
            PricingRoutes.Batch,
            PricingRoutes.CloudProviderConfigured,
            PricingRoutes.SubscriptionOrContract,
            PricingRoutes.Unknown
        };

    private static readonly HashSet<string> KnownModes =
        new(StringComparer.Ordinal)
        {
            PricingBillingModes.GitHubAiCredits,
            PricingBillingModes.GitHubLegacyRequests,
            PricingBillingModes.PlanIncluded,
            PricingBillingModes.AnthropicApiTokens,
            PricingBillingModes.CloudProviderApiTokens,
            PricingBillingModes.Subscription,
            PricingBillingModes.CustomEnterprise,
            PricingBillingModes.Unknown
        };

    private readonly IReadOnlyDictionary<string, PricingCatalogEntry> _byKey;

    private PricingCatalog(
        IReadOnlyList<PricingRegistryDocument> documents,
        IReadOnlyList<PricingCatalogEntry> entries)
    {
        Documents = documents;
        Entries = entries;
        _byKey = entries.ToDictionary(entry => entry.EntryKey, StringComparer.Ordinal);
        CatalogSha256 = PricingCanonicalJson.ComputeCatalogSha256(this);
    }

    public IReadOnlyList<PricingRegistryDocument> Documents { get; }
    public IReadOnlyList<PricingCatalogEntry> Entries { get; }
    public string CatalogSha256 { get; }

    public static PricingCatalog Create(
        PricingRegistryDocument bundled,
        params PricingRegistryDocument[] localOverrides)
    {
        ArgumentNullException.ThrowIfNull(bundled);
        ArgumentNullException.ThrowIfNull(localOverrides);

        var documents = new List<PricingRegistryDocument> { FreezeDocument(bundled) };
        documents.AddRange(localOverrides.Select(document =>
            FreezeDocument(document ?? throw new PricingRegistryValidationException(
                "Pricing registry documents cannot be null."))));
        if (documents.Count > PricingContractLimits.MaximumCatalogDocuments)
        {
            throw new PricingRegistryValidationException(
                "Pricing catalog document count exceeds the v1 bound.");
        }

        if (documents[0].SourceKind != PricingRegistrySourceKinds.Bundled)
        {
            throw new PricingRegistryValidationException(
                "The first pricing registry document must have source_kind 'bundled'.");
        }

        if (documents.Skip(1).Any(document =>
                document.SourceKind != PricingRegistrySourceKinds.LocalOverride))
        {
            throw new PricingRegistryValidationException(
                "Additional pricing registry documents must have source_kind 'local_override'.");
        }

        var entries = new List<PricingCatalogEntry>();
        foreach (var document in documents)
        {
            ValidateDocument(document);
            entries.AddRange(document.Entries.Select(entry =>
                new PricingCatalogEntry(EntryKey(document, entry), document, entry)));
        }

        if (documents.Select(document => document.SourceId)
            .Distinct(StringComparer.Ordinal)
            .Count() != documents.Count)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry source IDs must be unique across a catalog.");
        }

        ValidateCatalog(entries);
        return new PricingCatalog(documents.AsReadOnly(), entries.AsReadOnly());
    }

    public PricingRegistrySelection Select(
        string provider,
        string modelId,
        string billingMode,
        string pricingRoute,
        DateTimeOffset sessionTimeUtc) =>
        TrySelect(provider, modelId, billingMode, pricingRoute, sessionTimeUtc)
        ?? throw new PricingRegistryValidationException(
            "No exact pricing registry entry matched the supplied tuple.");

    public PricingRegistrySelection? TrySelect(
        string provider,
        string modelId,
        string billingMode,
        string pricingRoute,
        DateTimeOffset sessionTimeUtc)
    {
        var active = Entries
            .Where(item =>
                item.Entry.Provider == provider
                && item.Entry.BillingMode == billingMode
                && item.Entry.PricingRoute == pricingRoute
                && MatchesModel(item.Entry, modelId)
                && IsEffective(item.Entry, sessionTimeUtc))
            .ToList();

        if (active.Count == 0)
        {
            return null;
        }

        var survivors = active
            .Where(candidate => !active.Any(other =>
                other.EntryKey != candidate.EntryKey
                && Supersedes(other, candidate.EntryKey)))
            .ToList();

        if (survivors.Count != 1)
        {
            throw new PricingRegistryValidationException(
                "Exact pricing selection did not produce one supersession winner.");
        }

        var selected = survivors[0];
        return new PricingRegistrySelection(
            selected.EntryKey,
            selected.Document,
            selected.Entry);
    }

    public bool HasExactModel(string provider, string modelId) =>
        Entries.Any(item =>
            item.Entry.Provider == provider
            && MatchesModel(item.Entry, modelId));

    public bool HasExactModelAndMode(
        string provider,
        string modelId,
        string billingMode) =>
        Entries.Any(item =>
            item.Entry.Provider == provider
            && item.Entry.BillingMode == billingMode
            && MatchesModel(item.Entry, modelId));

    public bool HasExactTuple(
        string provider,
        string modelId,
        string billingMode,
        string pricingRoute) =>
        Entries.Any(item =>
            item.Entry.Provider == provider
            && item.Entry.BillingMode == billingMode
            && item.Entry.PricingRoute == pricingRoute
            && MatchesModel(item.Entry, modelId));

    public static bool IsSupportedProviderMode(string provider, string billingMode) =>
        provider switch
        {
            PricingProviders.GitHubCopilot =>
                billingMode is PricingBillingModes.GitHubAiCredits
                    or PricingBillingModes.GitHubLegacyRequests
                    or PricingBillingModes.PlanIncluded
                    or PricingBillingModes.CustomEnterprise
                    or PricingBillingModes.Unknown,
            PricingProviders.ClaudeCode =>
                billingMode is PricingBillingModes.AnthropicApiTokens
                    or PricingBillingModes.CloudProviderApiTokens
                    or PricingBillingModes.Subscription
                    or PricingBillingModes.CustomEnterprise
                    or PricingBillingModes.Unknown,
            PricingProviders.CodexApp =>
                billingMode is PricingBillingModes.Subscription
                    or PricingBillingModes.CustomEnterprise
                    or PricingBillingModes.Unknown,
            _ => false
        };

    public static bool IsSupportedProviderModeRoute(
        string provider,
        string billingMode,
        string pricingRoute) =>
        (provider, billingMode) switch
        {
            (PricingProviders.GitHubCopilot, PricingBillingModes.GitHubAiCredits) =>
                pricingRoute == PricingRoutes.CreditConsumingInteraction,
            (PricingProviders.GitHubCopilot, PricingBillingModes.GitHubLegacyRequests) =>
                pricingRoute == PricingRoutes.LegacyRequest,
            (PricingProviders.GitHubCopilot, PricingBillingModes.PlanIncluded) =>
                pricingRoute is PricingRoutes.CreditConsumingInteraction
                    or PricingRoutes.CodeCompletion
                    or PricingRoutes.NextEditSuggestion,
            (PricingProviders.GitHubCopilot, PricingBillingModes.CustomEnterprise) =>
                pricingRoute == PricingRoutes.SubscriptionOrContract,
            (PricingProviders.GitHubCopilot, PricingBillingModes.Unknown) =>
                pricingRoute == PricingRoutes.Unknown,
            (PricingProviders.ClaudeCode, PricingBillingModes.AnthropicApiTokens) =>
                pricingRoute is PricingRoutes.StandardGlobal
                    or PricingRoutes.UsOnlyInference
                    or PricingRoutes.Batch,
            (PricingProviders.ClaudeCode, PricingBillingModes.CloudProviderApiTokens) =>
                pricingRoute == PricingRoutes.CloudProviderConfigured,
            (PricingProviders.ClaudeCode, PricingBillingModes.Subscription
                or PricingBillingModes.CustomEnterprise) =>
                pricingRoute == PricingRoutes.SubscriptionOrContract,
            (PricingProviders.ClaudeCode, PricingBillingModes.Unknown) =>
                pricingRoute == PricingRoutes.Unknown,
            (PricingProviders.CodexApp, PricingBillingModes.Subscription
                or PricingBillingModes.CustomEnterprise) =>
                pricingRoute == PricingRoutes.SubscriptionOrContract,
            (PricingProviders.CodexApp, PricingBillingModes.Unknown) =>
                pricingRoute == PricingRoutes.Unknown,
            _ => false
        };

    public static bool IsKnownProvider(string provider) =>
        SupportedProviders.Contains(provider) || provider == PricingProviders.Unknown;

    public static bool IsKnownBillingMode(string billingMode) =>
        KnownModes.Contains(billingMode);

    public static bool IsKnownPricingRoute(string pricingRoute) =>
        KnownRoutes.Contains(pricingRoute);

    private bool Supersedes(PricingCatalogEntry candidate, string targetKey)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var current = candidate.Entry.SupersedesEntryKey;
        while (current is not null)
        {
            if (!visited.Add(current))
            {
                return false;
            }

            if (current == targetKey)
            {
                return true;
            }

            current = _byKey[current].Entry.SupersedesEntryKey;
        }

        return false;
    }

    private static void ValidateDocument(PricingRegistryDocument document)
    {
        if (document.SchemaVersion != PricingContractVersions.Registry)
        {
            throw new PricingRegistryValidationException(
                "The pricing registry schema version is unsupported.");
        }

        if (document.SchemaUri != PricingContractVersions.RegistrySchemaUri)
        {
            throw new PricingRegistryValidationException(
                "The pricing registry schema URI is unsupported.");
        }
        RequireToken(document.RegistryVersion, "registry_version");
        RequireToken(document.SourceId, "source_id");
        RequireSafeLabel(document.SourceLabel, "source_label", 256);
        if (document.LastReviewedDate == default
            || document.StaleAfterDate == default)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry review and stale dates are required.");
        }

        if (document.SourceKind is not PricingRegistrySourceKinds.Bundled
            and not PricingRegistrySourceKinds.LocalOverride)
        {
            throw new PricingRegistryValidationException(
                "The pricing registry source kind is unknown.");
        }

        if (document.StaleAfterDate < document.LastReviewedDate)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry stale_after_date precedes last_reviewed_date.");
        }

        if (document.SourceReferences is null || document.SourceReferences.Count == 0)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry requires at least one reviewed source reference.");
        }
        if (document.SourceReferences
            .Select(reference => reference?.Reference)
            .Distinct(StringComparer.Ordinal)
            .Count() != document.SourceReferences.Count)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry source references must be unique.");
        }

        foreach (var sourceReference in document.SourceReferences)
        {
            if (sourceReference is null
                || sourceReference.ReviewedDate == default
                || sourceReference.ReviewedDate > document.LastReviewedDate)
            {
                throw new PricingRegistryValidationException(
                    "Pricing source reviewed dates must be present and cannot follow the document review date.");
            }
            RequireAbsoluteHttpsUri(sourceReference.Reference, "source reference");
            RequireSafeLabel(sourceReference.Note, "source reference note", 1024);
        }

        if (document.Entries is null || document.Entries.Count == 0)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry requires at least one entry.");
        }

        foreach (var entry in document.Entries)
        {
            if (entry is null)
            {
                throw new PricingRegistryValidationException(
                    "Pricing registry entries cannot be null.");
            }
            ValidateEntry(document, entry);
        }
    }

    private static void ValidateEntry(
        PricingRegistryDocument document,
        PricingRegistryEntry entry)
    {
        RequireToken(entry.EntryId, "entry_id");
        if (entry.Revision < 1)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry entry revision must be positive.");
        }
        if (entry.SupersedesEntryKey is { } predecessor
            && (!EntryKeyPattern.IsMatch(predecessor)
                || LooksLikeCredential(predecessor)))
        {
            throw new PricingRegistryValidationException(
                "Pricing supersession key is invalid.");
        }

        if (!SupportedProviders.Contains(entry.Provider))
        {
            throw new PricingRegistryValidationException(
                "The pricing provider token is unknown.");
        }

        if (!KnownModes.Contains(entry.BillingMode)
            || !IsSupportedProviderMode(entry.Provider, entry.BillingMode))
        {
            throw new PricingRegistryValidationException(
                "The pricing provider and billing mode are not a supported pair.");
        }

        if (entry.Provider == PricingProviders.CodexApp
            || entry.BillingMode is PricingBillingModes.Subscription
                or PricingBillingModes.CustomEnterprise
                or PricingBillingModes.Unknown)
        {
            throw new PricingRegistryValidationException(
                "A not-estimable provider/billing mode cannot define a priced registry entry.");
        }

        RequireSafeLabel(entry.CanonicalModelId, "canonical_model_id", 256);
        if (!KnownRoutes.Contains(entry.PricingRoute))
        {
            throw new PricingRegistryValidationException(
                "The pricing route token is unknown.");
        }
        if (!IsSupportedProviderModeRoute(
                entry.Provider,
                entry.BillingMode,
                entry.PricingRoute))
        {
            throw new PricingRegistryValidationException(
                "The pricing provider, billing mode, and route are not a supported tuple.");
        }
        if (entry.Aliases is null
            || entry.Aliases.Any(alias => !IsSafeLabel(alias, 256))
            || entry.Aliases.Distinct(StringComparer.Ordinal).Count() != entry.Aliases.Count
            || entry.Aliases.Contains(entry.CanonicalModelId, StringComparer.Ordinal))
        {
            throw new PricingRegistryValidationException(
                "Pricing model aliases must be non-empty, exact, unique, and distinct from the canonical ID.");
        }

        if (entry.Currency != "USD" || entry.CurrencyMinorUnits != 2)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry v1 supports only USD with two minor units.");
        }

        RequireUtc(entry.EffectiveFromUtc, "effective_from_utc");
        if (entry.EffectiveFromUtc == default)
        {
            throw new PricingRegistryValidationException(
                "Pricing entry effective_from_utc is required.");
        }
        if (entry.EffectiveToUtc is { } effectiveTo)
        {
            RequireUtc(effectiveTo, "effective_to_utc");
            if (effectiveTo <= entry.EffectiveFromUtc)
            {
                throw new PricingRegistryValidationException(
                    "Pricing entry effective_to_utc must be after effective_from_utc.");
            }
        }

        RequireAbsoluteHttpsUri(entry.SourceReference, "entry source reference");
        if (!document.SourceReferences.Any(reference =>
                reference.Reference == entry.SourceReference))
        {
            throw new PricingRegistryValidationException(
                "Every entry source_reference must be present in the document source_references.");
        }
        var matchingReference = document.SourceReferences.Single(reference =>
            reference.Reference == entry.SourceReference);
        if (entry.LastReviewedDate == default
            || entry.LastReviewedDate > document.LastReviewedDate
            || entry.LastReviewedDate != matchingReference.ReviewedDate)
        {
            throw new PricingRegistryValidationException(
                "Pricing entry reviewed date must match its source reference and cannot follow the document review date.");
        }

        if (entry.Rates is null)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry entry rates are required.");
        }

        if (entry.Rates.NonNullRates().Any(rate =>
                rate < MinimumRate
                || rate > MaximumRate
                || PricingExactDecimal.Scale(rate) > 6))
        {
            throw new PricingRegistryValidationException(
                "Pricing rates and multipliers must be positive and within the v1 magnitude and scale bounds.");
        }

        var monetaryRates = entry.Rates.NonNullRates().Count()
            - (entry.Rates.RequestCreditMultiplier is null ? 0 : 1);
        if (entry.IncludedZeroIncrementalCost)
        {
            if (entry.BillingMode != PricingBillingModes.PlanIncluded
                || monetaryRates != 0
                || entry.Rates.RequestCreditMultiplier is not null)
            {
                throw new PricingRegistryValidationException(
                    "Included zero-incremental entries must use plan_included and contain no rates.");
            }
        }
        else if (entry.BillingMode == PricingBillingModes.PlanIncluded
                 || monetaryRates == 0)
        {
            throw new PricingRegistryValidationException(
                "Non-included pricing entries require at least one monetary rate.");
        }

        if (entry.Rates.RequestCreditMultiplier is not null
            && (entry.Rates.PerCredit is null
                || entry.Rates.PerRequest is not null))
        {
            throw new PricingRegistryValidationException(
                "A request-credit multiplier requires one credit rate and no request rate.");
        }

        if (entry.Rates.PerRequest is not null && entry.Rates.PerCredit is not null)
        {
            throw new PricingRegistryValidationException(
                "Request and credit rates cannot both be active in one entry.");
        }

        ValidateRateShape(entry);

        if (entry.Limitations is null)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry entry limitations are required.");
        }

        if (entry.Limitations.Any(limitation => !IsSafeLabel(limitation, 512)))
        {
            throw new PricingRegistryValidationException(
                "Pricing registry entry limitations must be bounded repository-safe text.");
        }
    }

    private static void ValidateCatalog(IReadOnlyList<PricingCatalogEntry> entries)
    {
        if (entries.Select(entry => entry.EntryKey)
            .Distinct(StringComparer.Ordinal)
            .Count() != entries.Count)
        {
            throw new PricingRegistryValidationException(
                "Pricing entry keys must be unique.");
        }

        var byKey = entries.ToDictionary(entry => entry.EntryKey, StringComparer.Ordinal);
        var orderByKey = entries
            .Select((entry, index) => (entry.EntryKey, Index: index))
            .ToDictionary(item => item.EntryKey, item => item.Index, StringComparer.Ordinal);
        foreach (var item in entries)
        {
            if (item.Entry.SupersedesEntryKey is { } targetKey
                && !byKey.ContainsKey(targetKey))
            {
                throw new PricingRegistryValidationException(
                    "A pricing entry supersedes a missing key.");
            }
        }

        foreach (var item in entries)
        {
            if (item.Entry.SupersedesEntryKey is not { } targetKey)
            {
                continue;
            }

            var target = byKey[targetKey];
            if (orderByKey[target.EntryKey] >= orderByKey[item.EntryKey])
            {
                throw new PricingRegistryValidationException(
                    "Pricing supersession must point to an earlier catalog entry.");
            }
            if (target.Document.SourceId == item.Document.SourceId
                && target.Entry.EntryId == item.Entry.EntryId
                && item.Entry.Revision <= target.Entry.Revision)
            {
                throw new PricingRegistryValidationException(
                    "A same-entry pricing supersession must increase its revision.");
            }

            if (target.Entry.Provider != item.Entry.Provider
                || target.Entry.CanonicalModelId != item.Entry.CanonicalModelId
                || target.Entry.BillingMode != item.Entry.BillingMode
                || target.Entry.PricingRoute != item.Entry.PricingRoute
                || target.Entry.Currency != item.Entry.Currency
                || !target.Entry.Aliases.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(item.Entry.Aliases))
            {
                throw new PricingRegistryValidationException(
                    "A pricing supersession must preserve provider, canonical model, exact aliases, billing mode, route, and currency.");
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { item.EntryKey };
            var current = target;
            while (true)
            {
                if (!visited.Add(current.EntryKey))
                {
                    throw new PricingRegistryValidationException(
                        "Pricing supersession chains cannot contain a cycle.");
                }

                if (current.Entry.SupersedesEntryKey is not { } next)
                {
                    break;
                }

                current = byKey[next];
            }
        }

        for (var leftIndex = 0; leftIndex < entries.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < entries.Count; rightIndex++)
            {
                var left = entries[leftIndex];
                var right = entries[rightIndex];
                if (!SameLookupSpace(left.Entry, right.Entry)
                    || !EffectivePeriodsOverlap(left.Entry, right.Entry))
                {
                    continue;
                }

                if (!SupersedesStatic(left, right.EntryKey, byKey)
                    && !SupersedesStatic(right, left.EntryKey, byKey))
                {
                    throw new PricingRegistryValidationException(
                        "Overlapping pricing entries require explicit supersession.");
                }
            }
        }
    }

    private static bool SupersedesStatic(
        PricingCatalogEntry candidate,
        string targetKey,
        IReadOnlyDictionary<string, PricingCatalogEntry> byKey)
    {
        var current = candidate.Entry.SupersedesEntryKey;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (current is not null && visited.Add(current))
        {
            if (current == targetKey)
            {
                return true;
            }

            current = byKey[current].Entry.SupersedesEntryKey;
        }

        return false;
    }

    private static bool SameLookupSpace(
        PricingRegistryEntry left,
        PricingRegistryEntry right)
    {
        if (left.Provider != right.Provider
            || left.BillingMode != right.BillingMode
            || left.PricingRoute != right.PricingRoute)
        {
            return false;
        }

        var leftNames = left.Aliases.Append(left.CanonicalModelId);
        var rightNames = right.Aliases.Append(right.CanonicalModelId)
            .ToHashSet(StringComparer.Ordinal);
        return leftNames.Any(rightNames.Contains);
    }

    private static bool EffectivePeriodsOverlap(
        PricingRegistryEntry left,
        PricingRegistryEntry right)
    {
        var leftEnd = left.EffectiveToUtc ?? DateTimeOffset.MaxValue;
        var rightEnd = right.EffectiveToUtc ?? DateTimeOffset.MaxValue;
        return left.EffectiveFromUtc < rightEnd && right.EffectiveFromUtc < leftEnd;
    }

    private static bool MatchesModel(PricingRegistryEntry entry, string modelId) =>
        entry.CanonicalModelId == modelId
        || entry.Aliases.Contains(modelId, StringComparer.Ordinal);

    private static bool IsEffective(
        PricingRegistryEntry entry,
        DateTimeOffset sessionTimeUtc) =>
        entry.EffectiveFromUtc <= sessionTimeUtc
        && (entry.EffectiveToUtc is null || sessionTimeUtc < entry.EffectiveToUtc);

    private static string EntryKey(
        PricingRegistryDocument document,
        PricingRegistryEntry entry) =>
        $"{document.SourceId}:{entry.EntryId}@{entry.Revision}";

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PricingRegistryValidationException(
                $"Pricing registry field '{field}' must be non-empty.");
        }
    }

    private static void RequireToken(string? value, string field)
    {
        if (value is null
            || value is "." or ".."
            || !PricingSafeText.IsWellFormedUtf16(value)
            || !TokenPattern.IsMatch(value)
            || LooksLikeCredential(value))
        {
            throw new PricingRegistryValidationException(
                "A pricing registry token field is invalid.");
        }
    }

    private static void RequireSafeLabel(string? value, string field, int maximumLength)
    {
        if (!IsSafeLabel(value, maximumLength))
        {
            throw new PricingRegistryValidationException(
                $"Pricing registry field '{field}' must be bounded repository-safe text.");
        }
    }

    private static bool IsSafeLabel(string? value, int maximumLength)
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
            return false;
        }

        return true;
    }

    private static bool LooksLikeCredential(string value) =>
        PricingSafeText.ContainsCredentialMarker(value);

    private static void RequireAbsoluteHttpsUri(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PricingRegistryValidationException(
                "Pricing registry source references must be public-style HTTPS URIs without credentials, query, or fragment.");
        }

        if (value.Length > PricingContractLimits.MaximumSourceReferenceLength
            || !value.StartsWith("https://", StringComparison.Ordinal)
            || !PricingSafeText.IsWellFormedUtf16(value)
            || ContainsUnsafeRawUriCharacter(value)
            || value.Contains('\\')
            || HasExplicitUserInfoDelimiter(value))
        {
            throw new PricingRegistryValidationException(
                "Pricing registry source references must be public-style HTTPS URIs without credentials, query, or fragment.");
        }

        string? decodedPath = null;
        string? decodedValue = null;
        try
        {
            decodedValue = Uri.UnescapeDataString(value);
            if (Uri.TryCreate(value, UriKind.Absolute, out var candidate))
            {
                decodedPath = Uri.UnescapeDataString(candidate.AbsolutePath);
            }
        }
        catch (UriFormatException)
        {
            decodedPath = null;
        }

        if (Regex.IsMatch(
                value,
                "%(?![0-9A-Fa-f]{2})",
                RegexOptions.CultureInvariant)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || decodedValue is null
            || decodedValue.Contains('\\')
            || ContainsUnsafeDecodedUriCharacter(decodedValue)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || uri.IdnHost.EndsWith(".", StringComparison.Ordinal)
            || IPAddress.TryParse(uri.IdnHost, out _)
            || uri.IdnHost.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || !uri.IdnHost.Contains('.', StringComparison.Ordinal)
            || uri.IdnHost.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".home", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.Equals("home.arpa", StringComparison.OrdinalIgnoreCase)
            || uri.IdnHost.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)
            || PricingSafeText.ContainsCredentialMarker(uri.IdnHost)
            || PricingSafeText.ContainsEmail(uri.IdnHost)
            || decodedPath is null
            || decodedPath.Split('/').Any(segment => segment is "." or "..")
            || Regex.IsMatch(
                value,
                @"(?:/|%2f)(?:\.|%2e){1,2}(?=/|%2f|$|\?|#)",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)
            || PricingSafeText.ContainsCredentialMarker(decodedPath)
            || PricingSafeText.ContainsEmail(decodedPath))
        {
            throw new PricingRegistryValidationException(
                "Pricing registry source references must be public-style HTTPS URIs without credentials, query, or fragment.");
        }
    }

    private static bool HasExplicitUserInfoDelimiter(string value)
    {
        var authority = value.AsSpan("https://".Length);
        var slash = authority.IndexOf('/');
        if (slash >= 0)
        {
            authority = authority[..slash];
        }

        return authority.Contains('@');
    }

    private static bool ContainsUnsafeRawUriCharacter(string value) =>
        value.Any(character =>
            char.IsControl(character)
            || char.IsWhiteSpace(character)
            || char.GetUnicodeCategory(character)
                is System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator);

    private static bool ContainsUnsafeDecodedUriCharacter(string value) =>
        value.Any(character =>
            char.IsControl(character)
            || char.GetUnicodeCategory(character)
                is System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator);

    private static void ValidateRateShape(PricingRegistryEntry entry)
    {
        var rates = entry.Rates;
        var hasTokens = rates.InputPerMillionTokens is not null
            || rates.OutputPerMillionTokens is not null
            || rates.CacheReadPerMillionTokens is not null
            || rates.CacheWrite5mPerMillionTokens is not null
            || rates.CacheWrite1hPerMillionTokens is not null
            || rates.ReasoningPerMillionTokens is not null;
        var hasUnits = rates.PerRequest is not null
            || rates.PerCredit is not null
            || rates.RequestCreditMultiplier is not null;

        if (entry.BillingMode is PricingBillingModes.GitHubAiCredits
                or PricingBillingModes.AnthropicApiTokens
                or PricingBillingModes.CloudProviderApiTokens
            && (!hasTokens || hasUnits))
        {
            throw new PricingRegistryValidationException(
                "Token billing modes require token rates and cannot contain request or credit rates.");
        }

        if (entry.BillingMode == PricingBillingModes.GitHubLegacyRequests
            && (hasTokens
                || (rates.PerRequest is null && rates.PerCredit is null)))
        {
            throw new PricingRegistryValidationException(
                "Legacy request billing requires only a request or credit rate.");
        }

        if (entry.BillingMode == PricingBillingModes.PlanIncluded
            && (hasTokens || hasUnits || !entry.IncludedZeroIncrementalCost))
        {
            throw new PricingRegistryValidationException(
                "Plan-included entries must define only the explicit included rule.");
        }

        if (entry.Provider == PricingProviders.ClaudeCode
            && rates.ReasoningPerMillionTokens is not null)
        {
            throw new PricingRegistryValidationException(
                "Claude output-inclusive routes cannot define a separate reasoning rate.");
        }
    }

    private static PricingRegistryDocument FreezeDocument(PricingRegistryDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var sourceReferences = document.SourceReferences is null
            ? null!
            : Array.AsReadOnly(document.SourceReferences
                .Select(reference => reference is null ? null! : reference with { })
                .ToArray());
        var entries = document.Entries is null
            ? null!
            : Array.AsReadOnly(document.Entries
                .Select(entry => entry is null
                    ? null!
                    : entry with
                    {
                        Aliases = entry.Aliases is null
                            ? null!
                            : Array.AsReadOnly(entry.Aliases.ToArray()),
                        Rates = entry.Rates is null ? null! : entry.Rates with { },
                        Limitations = entry.Limitations is null
                            ? null!
                            : Array.AsReadOnly(entry.Limitations.ToArray())
                    })
                .ToArray());
        return document with
        {
            SourceReferences = sourceReferences,
            Entries = entries
        };
    }

    private static void RequireUtc(DateTimeOffset value, string field)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new PricingRegistryValidationException(
                $"Pricing registry field '{field}' must be UTC.");
        }
    }
}

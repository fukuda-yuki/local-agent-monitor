namespace CopilotAgentObservability.Telemetry;

internal enum RepositoryMetadataStatus
{
    MetadataPresent,
    UrlFallbackUsed,
    MetadataNotPresent,
    UnsupportedCandidatePresent,
    UnsafeValueRejected,
}

internal enum RepositoryMetadataAttributeScope
{
    Resource,
    Span,
    Event,
}

internal enum RepositoryMetadataAttributeClassification
{
    Repository,
    Workspace,
    Vcs,
    Other,
}

internal sealed record RepositoryMetadataAttributeInventoryRow(
    string Key,
    int Count,
    RepositoryMetadataAttributeScope Scope,
    RepositoryMetadataAttributeClassification Classification);

internal sealed record RepositoryMetadataDiagnostic(
    RepositoryMetadataStatus Status,
    bool RepositoryLabelPresent,
    bool UrlFallbackUsed,
    IReadOnlyList<RepositoryMetadataAttributeInventoryRow> Inventory);

internal static partial class RepositoryMetadataDiagnostics
{
    private const string RepositoryNameKey = "vcs.repository.name";
    private const string RepositoryUrlKey = "vcs.repository.url.full";
    private const int MaximumSafeKeyLength = 128;

    [GeneratedRegex(
        "^https://github\\.com/(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repository>[A-Za-z0-9._-]+?)(?:\\.git)?/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GithubRepositoryUrlPattern();

    [GeneratedRegex(
        "^(?:github_pat_[A-Za-z0-9_]{20,}|gh[pousr]_[A-Za-z0-9]{20,}|glpat-[A-Za-z0-9_-]{20,}|sk-[A-Za-z0-9_-]{20,})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    public static RepositoryMetadataDiagnostic Build(string payloadJson)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        var state = new AnalysisState();
        var root = document.RootElement;

        foreach (var resourceSpan in OtlpSpanReader.EnumerateArrayProperty(root, "resourceSpans"))
        {
            if (resourceSpan.TryGetProperty("resource", out var resource)
                && resource.TryGetProperty("attributes", out var resourceAttributes))
            {
                VisitAttributes(resourceAttributes, RepositoryMetadataAttributeScope.Resource, state);
                EvaluateResourceMetadata(resourceAttributes, state);
            }

            foreach (var scopeSpan in OtlpSpanReader.EnumerateArrayProperty(resourceSpan, "scopeSpans"))
            {
                foreach (var span in OtlpSpanReader.EnumerateArrayProperty(scopeSpan, "spans"))
                {
                    if (span.TryGetProperty("attributes", out var spanAttributes))
                    {
                        VisitAttributes(spanAttributes, RepositoryMetadataAttributeScope.Span, state);
                    }
                    foreach (var spanEvent in OtlpSpanReader.EnumerateArrayProperty(span, "events"))
                    {
                        if (spanEvent.TryGetProperty("attributes", out var eventAttributes))
                        {
                            VisitAttributes(eventAttributes, RepositoryMetadataAttributeScope.Event, state);
                        }
                    }
                }
            }
        }

        var status = DetermineStatus(state);
        var inventory = state.Inventory
            .OrderBy(item => item.Key.Key, StringComparer.Ordinal)
            .ThenBy(item => item.Key.Scope)
            .Select(item => new RepositoryMetadataAttributeInventoryRow(
                item.Key.Key,
                item.Value,
                item.Key.Scope,
                Classify(item.Key.Key)))
            .ToArray();
        return new(
            status,
            status is RepositoryMetadataStatus.MetadataPresent or RepositoryMetadataStatus.UrlFallbackUsed,
            status == RepositoryMetadataStatus.UrlFallbackUsed,
            inventory);
    }

    internal static string? ResolveRepositoryName(JsonObject resourceAttributes)
    {
        ArgumentNullException.ThrowIfNull(resourceAttributes);
        if (resourceAttributes.ContainsKey(RepositoryNameKey))
        {
            return SanitizeRepositoryName(
                OtlpSpanReader.ReadString(resourceAttributes, RepositoryNameKey));
        }

        if (OtlpSpanReader.ReadString(resourceAttributes, RepositoryUrlKey) is not { } url)
        {
            return null;
        }

        var resolution = ResolveUrl(url);
        return resolution.Disposition == UrlDisposition.Safe ? resolution.RepositoryName : null;
    }

    internal static string StatusWire(RepositoryMetadataStatus status) => status switch
    {
        RepositoryMetadataStatus.MetadataPresent => "metadata_present",
        RepositoryMetadataStatus.UrlFallbackUsed => "url_fallback_used",
        RepositoryMetadataStatus.MetadataNotPresent => "metadata_not_present",
        RepositoryMetadataStatus.UnsupportedCandidatePresent => "unsupported_candidate_present",
        RepositoryMetadataStatus.UnsafeValueRejected => "unsafe_value_rejected",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    internal static string ScopeWire(RepositoryMetadataAttributeScope scope) => scope switch
    {
        RepositoryMetadataAttributeScope.Resource => "resource",
        RepositoryMetadataAttributeScope.Span => "span",
        RepositoryMetadataAttributeScope.Event => "event",
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };

    internal static string ClassificationWire(RepositoryMetadataAttributeClassification classification) => classification switch
    {
        RepositoryMetadataAttributeClassification.Repository => "repository",
        RepositoryMetadataAttributeClassification.Workspace => "workspace",
        RepositoryMetadataAttributeClassification.Vcs => "vcs",
        RepositoryMetadataAttributeClassification.Other => "other",
        _ => throw new ArgumentOutOfRangeException(nameof(classification)),
    };

    private static void VisitAttributes(
        JsonElement attributes,
        RepositoryMetadataAttributeScope scope,
        AnalysisState state)
    {
        if (attributes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var attribute in attributes.EnumerateArray())
        {
            var key = OtlpSpanReader.ReadString(attribute, "key");
            if (key is null)
            {
                continue;
            }

            var candidate = IsCandidate(key);
            state.CandidateSeen |= candidate;
            if (IsSafeKey(key))
            {
                var inventoryKey = new InventoryKey(key, scope);
                state.Inventory[inventoryKey] = state.Inventory.TryGetValue(inventoryKey, out var count)
                    ? checked(count + 1)
                    : 1;
            }

        }
    }

    private static void EvaluateResourceMetadata(JsonElement attributes, AnalysisState state)
    {
        if (attributes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var effectiveAttributes = OtlpAttributeConverter.ConvertAttributesArray(attributes);
        if (effectiveAttributes.ContainsKey(RepositoryNameKey))
        {
            var value = OtlpSpanReader.ReadString(effectiveAttributes, RepositoryNameKey);
            if (value is null)
            {
                state.UnsupportedResourceNameSeen = true;
            }
            else if (SanitizeRepositoryName(value) is not null)
            {
                state.SafeResourceNameSeen = true;
            }
            else
            {
                state.UnsafeResourceNameSeen = true;
            }
            return;
        }

        if (!effectiveAttributes.ContainsKey(RepositoryUrlKey))
        {
            return;
        }

        var url = OtlpSpanReader.ReadString(effectiveAttributes, RepositoryUrlKey);
        if (url is null)
        {
            state.UnsupportedUrlSeen = true;
            return;
        }

        switch (ResolveUrl(url).Disposition)
        {
            case UrlDisposition.Safe:
                state.SafeUrlSeen = true;
                break;
            case UrlDisposition.Unsupported:
                state.UnsupportedUrlSeen = true;
                break;
            case UrlDisposition.Unsafe:
                state.UnsafeUrlSeen = true;
                break;
        }
    }

    private static RepositoryMetadataStatus DetermineStatus(AnalysisState state)
    {
        if (state.SafeResourceNameSeen)
        {
            return RepositoryMetadataStatus.MetadataPresent;
        }
        if (state.UnsafeResourceNameSeen)
        {
            return RepositoryMetadataStatus.UnsafeValueRejected;
        }
        if (state.UnsupportedResourceNameSeen)
        {
            return RepositoryMetadataStatus.UnsupportedCandidatePresent;
        }
        if (state.SafeUrlSeen)
        {
            return RepositoryMetadataStatus.UrlFallbackUsed;
        }
        if (state.UnsafeUrlSeen)
        {
            return RepositoryMetadataStatus.UnsafeValueRejected;
        }
        if (state.UnsupportedUrlSeen || state.CandidateSeen)
        {
            return RepositoryMetadataStatus.UnsupportedCandidatePresent;
        }
        return RepositoryMetadataStatus.MetadataNotPresent;
    }

    private static UrlResolution ResolveUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || MeasurementSanitizer.IsUnsafeStringValue(value)
            || value.Contains('%', StringComparison.Ordinal))
        {
            return UrlResolution.Unsafe;
        }

        var match = GithubRepositoryUrlPattern().Match(value);
        if (match.Success)
        {
            var owner = match.Groups["owner"].Value;
            var repository = match.Groups["repository"].Value;
            if (IsTokenLike(owner) || IsTokenLike(repository))
            {
                return UrlResolution.Unsafe;
            }
            var sanitized = SanitizeRepositoryName(repository);
            return sanitized is null ? UrlResolution.Unsafe : new(UrlDisposition.Safe, sanitized);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !uri.IsDefaultPort)
        {
            return UrlResolution.Unsafe;
        }

        return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            ? UrlResolution.Unsafe
            : UrlResolution.Unsupported;
    }

    private static bool IsSafeKey(string key) =>
        key.Length is > 0 and <= MaximumSafeKeyLength
        && !MeasurementSanitizer.IsUnsafeStringValue(key)
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '.' or '-');

    private static bool IsCandidate(string key) =>
        key.Contains("repository", StringComparison.OrdinalIgnoreCase)
        || key.Contains("workspace", StringComparison.OrdinalIgnoreCase)
        || key.Contains("vcs", StringComparison.OrdinalIgnoreCase);

    private static RepositoryMetadataAttributeClassification Classify(string key)
    {
        if (key.Contains("repository", StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryMetadataAttributeClassification.Repository;
        }
        if (key.Contains("workspace", StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryMetadataAttributeClassification.Workspace;
        }
        if (key.Contains("vcs", StringComparison.OrdinalIgnoreCase))
        {
            return RepositoryMetadataAttributeClassification.Vcs;
        }
        return RepositoryMetadataAttributeClassification.Other;
    }

    private static bool IsTokenLike(string value) => TokenPattern().IsMatch(value);

    private static string? SanitizeRepositoryName(string? value)
    {
        if (value is not null
            && (value is "." or ".."
                || value.Contains('/', StringComparison.Ordinal)
                || value.Contains('\\', StringComparison.Ordinal)
                || IsTokenLike(value)))
        {
            return null;
        }

        return MeasurementSanitizer.SanitizeFreeFormName(value);
    }

    private sealed class AnalysisState
    {
        public Dictionary<InventoryKey, int> Inventory { get; } = [];
        public bool CandidateSeen { get; set; }
        public bool SafeResourceNameSeen { get; set; }
        public bool UnsafeResourceNameSeen { get; set; }
        public bool UnsupportedResourceNameSeen { get; set; }
        public bool SafeUrlSeen { get; set; }
        public bool UnsupportedUrlSeen { get; set; }
        public bool UnsafeUrlSeen { get; set; }
    }

    private readonly record struct InventoryKey(string Key, RepositoryMetadataAttributeScope Scope);
    private enum UrlDisposition { Safe, Unsupported, Unsafe }
    private readonly record struct UrlResolution(UrlDisposition Disposition, string? RepositoryName)
    {
        public static UrlResolution Unsupported { get; } = new(UrlDisposition.Unsupported, null);
        public static UrlResolution Unsafe { get; } = new(UrlDisposition.Unsafe, null);
    }
}

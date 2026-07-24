using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Pricing;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

public sealed record CostCatalogSourceReadV1(
    string SourceKind,
    string SourceId,
    string SourceLabel,
    string RegistryVersion,
    DateOnly LastReviewedDate,
    DateOnly StaleAfterDate);

public sealed record CostCatalogEntryReadV1(
    string SourceKind,
    string SourceId,
    string SourceLabel,
    string RegistryVersion,
    string EntryKey,
    string? SupersedesEntryKey,
    string SelectionState,
    string? SupersededByEntryKey,
    string Provider,
    string Model,
    string BillingMode,
    string PricingRoute,
    DateTimeOffset EffectiveFromUtc,
    DateTimeOffset? EffectiveToUtc,
    DateOnly LastReviewedDate,
    DateOnly StaleAfterDate,
    string Currency,
    bool IncludedZeroIncrementalCost,
    string? SourceReference);

public sealed record CostCatalogPageReadV1(
    string CatalogSha256,
    IReadOnlyList<CostCatalogSourceReadV1> Sources,
    IReadOnlyList<CostCatalogEntryReadV1> Entries,
    string? NextAfter);

public static class PricingCatalogReadProjectorV1
{
    private const string Prefix = "cost-catalog-cursor-v1.";
    private const int MaximumResponseBytes = 8 * 1024 * 1024;

    public static PricingReadResult<CostCatalogPageReadV1> Read(
        PricingCatalog catalog,
        string? after,
        int limit = 50)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (limit is < 1 or > 100) return new(PricingReadStatus.InvalidCursor);
        string? entryKey = null;
        if (after is not null)
        {
            if (!TryReadCursor(after, out var cursor))
                return new(PricingReadStatus.InvalidCursor);
            if (cursor.CatalogSha256 != catalog.CatalogSha256)
                return new(PricingReadStatus.CatalogChanged);
            if (!catalog.Entries.Any(item => item.EntryKey == cursor.EntryKey))
                return new(PricingReadStatus.InvalidCursor);
            entryKey = cursor.EntryKey;
        }

        var start = entryKey is null
            ? 0
            : catalog.Entries
                .Select((item, index) => (item.EntryKey, index))
                .Single(item => item.EntryKey == entryKey).index + 1;
        var selected = catalog.Entries.Skip(start).Take(limit + 1).ToArray();
        var hasMore = selected.Length > limit;
        if (hasMore) selected = selected[..limit];
        var supersededBy = catalog.Entries
            .Where(item => item.Entry.SupersedesEntryKey is not null)
            .ToDictionary(
                item => item.Entry.SupersedesEntryKey!,
                item => item.EntryKey,
                StringComparer.Ordinal);
        var entries = selected.Select(item =>
        {
            supersededBy.TryGetValue(item.EntryKey, out var successor);
            return new CostCatalogEntryReadV1(
                item.Document.SourceKind,
                item.Document.SourceId,
                item.Document.SourceLabel,
                item.Document.RegistryVersion,
                item.EntryKey,
                item.Entry.SupersedesEntryKey,
                successor is null ? "active" : "superseded",
                successor,
                item.Entry.Provider,
                item.Entry.CanonicalModelId,
                item.Entry.BillingMode,
                item.Entry.PricingRoute,
                item.Entry.EffectiveFromUtc,
                item.Entry.EffectiveToUtc,
                item.Document.LastReviewedDate,
                item.Document.StaleAfterDate,
                item.Entry.Currency,
                item.Entry.IncludedZeroIncrementalCost,
                item.Document.SourceKind == PricingRegistrySourceKinds.Bundled
                    ? item.Entry.SourceReference
                    : null);
        }).ToArray();
        var next = hasMore && entries.Length != 0
            ? WriteCursor(catalog.CatalogSha256, entries[^1].EntryKey)
            : null;
        var value = new CostCatalogPageReadV1(
            catalog.CatalogSha256,
            Array.AsReadOnly(catalog.Documents.Select(document =>
                new CostCatalogSourceReadV1(
                    document.SourceKind,
                    document.SourceId,
                    document.SourceLabel,
                    document.RegistryVersion,
                    document.LastReviewedDate,
                    document.StaleAfterDate)).ToArray()),
            Array.AsReadOnly(entries),
            next);
        return JsonSerializer.SerializeToUtf8Bytes(value).Length <= MaximumResponseBytes
            ? new(PricingReadStatus.Success, value)
            : new(PricingReadStatus.ResponseTooLarge);
    }

    private static string WriteCursor(string catalogSha256, string entryKey)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new CatalogCursor(
            "cost.catalog.cursor.v1",
            catalogSha256,
            entryKey));
        return Prefix + Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool TryReadCursor(string value, out CatalogCursor cursor)
    {
        cursor = default!;
        if (value.Length is < 1 or > 512
            || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        try
        {
            var encoded = value[Prefix.Length..]
                .Replace('-', '+')
                .Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var bytes = Convert.FromBase64String(encoded);
            var parsed = JsonSerializer.Deserialize<CatalogCursor>(bytes);
            if (parsed is null
                || parsed.SchemaVersion != "cost.catalog.cursor.v1"
                || parsed.CatalogSha256.Length != 64
                || parsed.CatalogSha256.Any(character =>
                    character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f'))
                || string.IsNullOrEmpty(parsed.EntryKey)
                || !bytes.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(parsed)))
                return false;
            if (!string.Equals(
                    value,
                    WriteCursor(parsed.CatalogSha256, parsed.EntryKey),
                    StringComparison.Ordinal))
                return false;
            cursor = parsed;
            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed record CatalogCursor(
        [property: JsonPropertyName("schema_version")] string SchemaVersion,
        [property: JsonPropertyName("catalog_sha256")] string CatalogSha256,
        [property: JsonPropertyName("entry_key")] string EntryKey);
}

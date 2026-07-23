using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;

namespace CopilotAgentObservability.Pricing;

public static class PricingRegistryLoader
{
    internal static JsonSerializerOptions SerializerOptions { get; } = CreateOptions();

    public static PricingRegistryDocument Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new PricingRegistryValidationException(
                "Pricing registry JSON is empty.");
        }
        if (Encoding.UTF8.GetByteCount(json) > PricingContractLimits.MaximumRegistryBytes)
        {
            throw new PricingRegistryValidationException(
                $"Pricing registry JSON exceeds {PricingContractLimits.MaximumRegistryBytes} UTF-8 bytes.");
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            RejectDuplicateProperties(document.RootElement);
            ValidateRequiredShape(document.RootElement);

            return JsonSerializer.Deserialize<PricingRegistryDocument>(json, SerializerOptions)
                ?? throw new PricingRegistryValidationException("Pricing registry JSON is null.");
        }
        catch (JsonException)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry JSON is invalid.");
        }
        catch (InvalidOperationException)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry JSON member shape is invalid.");
        }
    }

    private static void ValidateRequiredShape(JsonElement root)
    {
        RequireObjectProperties(
            root,
            "$schema",
            "schema_version",
            "registry_version",
            "source_kind",
            "source_id",
            "source_label",
            "last_reviewed_date",
            "stale_after_date",
            "source_references",
            "entries");

        var sourceReferences = RequireArray(root.GetProperty("source_references"));
        foreach (var sourceReference in sourceReferences.EnumerateArray())
        {
            RequireObjectProperties(sourceReference, "reference", "reviewed_date", "note");
        }

        var entries = RequireArray(root.GetProperty("entries"));
        foreach (var entry in entries.EnumerateArray())
        {
            RequireObjectProperties(
                entry,
                "entry_id",
                "revision",
                "supersedes_entry_key",
                "provider",
                "canonical_model_id",
                "aliases",
                "billing_mode",
                "pricing_route",
                "rates",
                "currency",
                "currency_minor_units",
                "effective_from_utc",
                "effective_to_utc",
                "source_reference",
                "last_reviewed_date",
                "included_zero_incremental_cost",
                "limitations");
            RequireObjectProperties(
                entry.GetProperty("rates"),
                "input_per_million_tokens",
                "output_per_million_tokens",
                "cache_read_per_million_tokens",
                "cache_write_5m_per_million_tokens",
                "cache_write_1h_per_million_tokens",
                "reasoning_per_million_tokens",
                "per_request",
                "per_credit",
                "request_credit_multiplier");
            RequireUtcTimestampLexeme(entry.GetProperty("effective_from_utc"));
            if (entry.GetProperty("effective_to_utc").ValueKind != JsonValueKind.Null)
            {
                RequireUtcTimestampLexeme(entry.GetProperty("effective_to_utc"));
            }
        }
    }

    private static JsonElement RequireArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new PricingRegistryValidationException(
                "Pricing registry JSON collection member is invalid.");
        }

        return element;
    }

    private static void RequireUtcTimestampLexeme(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String
            || element.GetString() is not { } value
            || !value.EndsWith("Z", StringComparison.Ordinal))
        {
            throw new PricingRegistryValidationException(
                "Pricing registry timestamp must use a UTC Z suffix.");
        }
    }

    private static void RequireObjectProperties(
        JsonElement element,
        params string[] requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object
            || requiredProperties.Any(property => !element.TryGetProperty(property, out _)))
        {
            throw new PricingRegistryValidationException(
                "Pricing registry JSON is missing a required member.");
        }
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
                    throw new PricingRegistryValidationException(
                        "Pricing registry JSON contains a duplicate property.");
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

    private static JsonSerializerOptions CreateOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 16
    };
}

public static class BundledPricingRegistry
{
    private const string ResourceSuffix = "Registry.pricing-registry.bundled.json";

    public static PricingRegistryDocument Load()
    {
        var assembly = typeof(BundledPricingRegistry).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            ?? throw new PricingRegistryValidationException(
                $"Embedded pricing registry resource ending in '{ResourceSuffix}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new PricingRegistryValidationException(
                $"Embedded pricing registry resource '{resourceName}' could not be opened.");
        using var reader = new StreamReader(stream);
        return PricingRegistryLoader.Deserialize(reader.ReadToEnd());
    }
}

public sealed class PricingRegistryValidationException : Exception
{
    public PricingRegistryValidationException(string message)
        : base(message)
    {
    }
}

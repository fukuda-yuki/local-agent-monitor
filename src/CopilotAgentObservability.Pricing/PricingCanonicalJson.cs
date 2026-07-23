using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.Pricing;

public static class PricingCanonicalJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.Strict,
        Converters =
        {
            new CanonicalUtcDateTimeOffsetConverter(),
            new CanonicalDecimalConverter()
        }
    };

    public static byte[] Serialize(PricingEstimateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.SerializeToUtf8Bytes(record, Options);
    }

    public static byte[] SerializeCatalogSnapshot(PricingCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return JsonSerializer.SerializeToUtf8Bytes(
            new PricingCatalogSnapshot(
                PricingContractVersions.CatalogSnapshot,
                catalog.Documents),
            Options);
    }

    internal static string ComputeCatalogSha256(PricingCatalog catalog)
    {
        var bytes = SerializeCatalogSnapshot(catalog);
        if (bytes.Length > PricingContractLimits.MaximumCatalogSnapshotBytes)
        {
            throw new PricingRegistryValidationException(
                "Pricing catalog snapshot canonical bytes exceed the v1 bound.");
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    internal static PricingEstimateRecord WithIdentity(PricingEstimateRecord record)
    {
        var identityBytes = Serialize(record with { EstimateId = string.Empty });
        var digest = SHA256.HashData(identityBytes);
        var estimateId = $"pricing-estimate-{Convert.ToHexStringLower(digest)}";
        var identified = record with { EstimateId = estimateId };
        if (Serialize(identified).Length > PricingContractLimits.MaximumEstimateBytes)
        {
            throw new ArgumentException(
                "Pricing estimate canonical bytes exceed the v1 bound.");
        }

        return identified;
    }

    internal static bool HasValidIdentity(PricingEstimateRecord record)
    {
        var expected = WithIdentity(record with { EstimateId = string.Empty }).EstimateId;
        return string.Equals(expected, record.EstimateId, StringComparison.Ordinal);
    }

    private sealed class CanonicalUtcDateTimeOffsetConverter
        : JsonConverter<DateTimeOffset>
    {
        public override DateTimeOffset Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.GetDateTimeOffset();

        public override void Write(
            Utf8JsonWriter writer,
            DateTimeOffset value,
            JsonSerializerOptions options)
        {
            writer.WriteStringValue(
                value.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    private sealed class CanonicalDecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.GetDecimal();

        public override void Write(
            Utf8JsonWriter writer,
            decimal value,
            JsonSerializerOptions options) =>
            writer.WriteRawValue(
                value.ToString(
                    "0.#############################",
                    CultureInfo.InvariantCulture),
                skipInputValidation: true);
    }
}

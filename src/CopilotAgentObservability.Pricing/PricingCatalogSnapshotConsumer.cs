using System.Text.Json;

namespace CopilotAgentObservability.Pricing;

public static class PricingCatalogSnapshotConsumer
{
    public static PricingCatalog Deserialize(ReadOnlySpan<byte> canonicalJson)
    {
        if (canonicalJson.Length is 0
            or > PricingContractLimits.MaximumCatalogSnapshotBytes)
        {
            throw new PricingRegistryValidationException(
                "Pricing catalog snapshot bytes are empty or exceed the v1 bound.");
        }

        try
        {
            var bytes = canonicalJson.ToArray();
            using var parsed = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            RejectDuplicateProperties(parsed.RootElement);
            var snapshot = JsonSerializer.Deserialize<PricingCatalogSnapshot>(
                bytes,
                PricingRegistryLoader.SerializerOptions)
                ?? throw new PricingRegistryValidationException(
                    "Pricing catalog snapshot JSON is null.");
            if (snapshot.SchemaVersion != PricingContractVersions.CatalogSnapshot
                || snapshot.Documents is null
                || snapshot.Documents.Count is 0
                    or > PricingContractLimits.MaximumCatalogDocuments
                || snapshot.Documents.Any(document => document is null))
            {
                throw new PricingRegistryValidationException(
                    "Pricing catalog snapshot contract fields are invalid.");
            }

            var catalog = PricingCatalog.Create(
                snapshot.Documents[0],
                snapshot.Documents.Skip(1).ToArray());
            if (!bytes.AsSpan().SequenceEqual(
                    PricingCanonicalJson.SerializeCatalogSnapshot(catalog)))
            {
                throw new PricingRegistryValidationException(
                    "Pricing catalog snapshot JSON is not canonical v1 bytes.");
            }

            return catalog;
        }
        catch (PricingRegistryValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException
                or ArgumentException
                or InvalidOperationException
                or NullReferenceException)
        {
            throw new PricingRegistryValidationException(
                "Pricing catalog snapshot JSON is invalid.");
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
                        "Pricing catalog snapshot JSON contains a duplicate property.");
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

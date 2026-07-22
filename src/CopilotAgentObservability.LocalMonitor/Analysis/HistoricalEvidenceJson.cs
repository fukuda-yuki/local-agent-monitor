using System.Text.Json;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalEvidenceJsonV1
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Serialize(HistoricalEvidenceDatasetV1 dataset)
    {
        HistoricalEvidenceExtractorV1.ValidateDataset(dataset);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(dataset, Options);
        if (bytes.Length > HistoricalEvidenceContractsV1.MaximumPayloadBytes)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidSerialization);
        return bytes;
    }

    internal static byte[] SerializeSelection(HistoricalEvidenceSelectionV1 selection) =>
        JsonSerializer.SerializeToUtf8Bytes(selection, Options);

    internal static HistoricalEvidenceDatasetV1 Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > HistoricalEvidenceContractsV1.MaximumPayloadBytes)
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidSerialization);
        try
        {
            var dataset = JsonSerializer.Deserialize<HistoricalEvidenceDatasetV1>(bytes, Options)
                ?? throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidSerialization);
            HistoricalEvidenceExtractorV1.ValidateDataset(dataset);
            if (!bytes.SequenceEqual(Serialize(dataset)))
                throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidSerialization);
            return dataset;
        }
        catch (HistoricalEvidenceValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException
            or NullReferenceException or InvalidOperationException or OverflowException)
        {
            throw new HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1.InvalidSerialization, exception);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
        };
        options.Converters.Add(new SessionSourceSurfaceConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    private sealed class SessionSourceSurfaceConverter : JsonConverter<SessionSourceSurface>
    {
        public override SessionSourceSurface Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException();
            try { return SessionWire.ParseSourceSurface(reader.GetString()!); }
            catch (ArgumentException exception) { throw new JsonException("Invalid Session source surface.", exception); }
        }

        public override void Write(Utf8JsonWriter writer, SessionSourceSurface value, JsonSerializerOptions options) =>
            writer.WriteStringValue(SessionWire.ToWire(value));
    }
}

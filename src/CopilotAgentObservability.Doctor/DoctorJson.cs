using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.Doctor;

public static class DoctorJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static DoctorFactSnapshot DeserializeFactSnapshot(string json) =>
        JsonSerializer.Deserialize<DoctorFactSnapshot>(json, Options)
        ?? throw new JsonException("Doctor fact snapshot is required.");

    public static DoctorResult DeserializeResult(string json) =>
        JsonSerializer.Deserialize<DoctorResult>(json, Options)
        ?? throw new JsonException("Doctor result is required.");

    public static string SerializeResult(DoctorResult result) =>
        JsonSerializer.Serialize(result, Options);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false
        };
        options.Converters.Add(new CanonicalUtcDateTimeOffsetConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    private sealed class CanonicalUtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var value = reader.GetString();
            if (value is null
                || !DateTimeOffset.TryParseExact(
                    value,
                    Format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                throw new JsonException("Doctor timestamp must use canonical UTC round-trip form.");
            }

            return parsed;
        }

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToUniversalTime().ToString(Format, CultureInfo.InvariantCulture));
        }
    }
}

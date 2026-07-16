using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.Doctor;

public static class DoctorJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static readonly string[] RequiredFactSnapshotProperties =
    [
        "schema_version",
        "source_surface",
        "observed_at",
        "observations",
        "install_and_source_version",
        "process_receiver_and_port",
        "source_effective_configuration",
        "endpoint_reachability",
        "protocol_and_signal_compatibility",
        "source_version_and_schema_diagnostics",
        "last_ingest",
        "raw_persistence",
        "projection",
        "exact_session_binding",
        "completeness_and_content",
        "restart_or_new_process",
    ];
    private static readonly IReadOnlyDictionary<string, string[]> RequiredFactFamilyProperties =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["install_and_source_version"] = ["monitor_install", "source_version", "source_feature"],
            ["process_receiver_and_port"] = ["monitor_process", "receiver_bind", "port_owner"],
            ["source_effective_configuration"] = ["endpoint_alignment"],
            ["endpoint_reachability"] = ["reachability"],
            ["protocol_and_signal_compatibility"] = ["protocol", "trace_signal"],
            ["source_version_and_schema_diagnostics"] = ["compatibility", "schema"],
            ["last_ingest"] = ["outcome"],
            ["raw_persistence"] = ["outcome"],
            ["projection"] = ["outcome"],
            ["exact_session_binding"] = ["requirement", "outcome"],
            ["completeness_and_content"] = ["completeness", "content_capture", "raw_access"],
            ["restart_or_new_process"] = ["requirement"],
        };
    private static readonly string[] RequiredObservationProperties =
    [
        "source_surface",
        "evidence_class",
        "evidence_kind",
        "evidence_ref",
        "observed_at",
    ];

    public static DoctorFactSnapshot DeserializeFactSnapshot(string json) =>
        DeserializeStrict<DoctorFactSnapshot>(json, EnsureFactSnapshotShape);

    public static DoctorResult DeserializeResult(string json) =>
        DeserializeStrict<DoctorResult>(json);

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
        options.Converters.Add(new CanonicalEnumConverterFactory());
        return options;
    }

    private static T DeserializeStrict<T>(string json, Action<JsonElement>? validateShape = null)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            EnsureDistinctProperties(document.RootElement);
            validateShape?.Invoke(document.RootElement);
            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new JsonException();
        }
        catch (JsonException)
        {
            throw new JsonException("invalid_input");
        }
    }

    private static void EnsureFactSnapshotShape(JsonElement root)
    {
        EnsureRequiredProperties(root, RequiredFactSnapshotProperties);

        foreach (var family in RequiredFactFamilyProperties)
        {
            var familyElement = root.GetProperty(family.Key);
            if (familyElement.ValueKind == JsonValueKind.Object)
            {
                EnsureRequiredProperties(familyElement, family.Value);
            }
        }

        var observations = root.GetProperty("observations");
        if (observations.ValueKind == JsonValueKind.Array)
        {
            foreach (var observation in observations.EnumerateArray())
            {
                EnsureRequiredProperties(observation, RequiredObservationProperties);
            }
        }
    }

    private static void EnsureRequiredProperties(JsonElement element, IEnumerable<string> requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException();
        }

        foreach (var propertyName in requiredProperties)
        {
            if (!element.TryGetProperty(propertyName, out _))
            {
                throw new JsonException();
            }
        }
    }

    private static void EnsureDistinctProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new JsonException();
                }

                EnsureDistinctProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                EnsureDistinctProperties(item);
            }
        }
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

    private sealed class CanonicalEnumConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
            (JsonConverter)Activator.CreateInstance(
                typeof(CanonicalEnumConverter<>).MakeGenericType(typeToConvert))!;
    }

    private sealed class CanonicalEnumConverter<TEnum> : JsonConverter<TEnum>
        where TEnum : struct, Enum
    {
        private static readonly IReadOnlyDictionary<string, TEnum> FromWire =
            Enum.GetValues<TEnum>().ToDictionary(ToWire, value => value, StringComparer.Ordinal);

        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String
                || reader.GetString() is not { } value
                || !FromWire.TryGetValue(value, out var parsed))
            {
                throw new JsonException();
            }

            return parsed;
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
        {
            if (!Enum.IsDefined(value))
            {
                throw new JsonException();
            }

            writer.WriteStringValue(ToWire(value));
        }

        private static string ToWire(TEnum value) =>
            JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
    }
}

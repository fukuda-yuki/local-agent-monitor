using System.Security.Cryptography;

namespace CopilotAgentObservability.Telemetry;

internal static class OtlpJsonStructuralWalker
{
    private const string UnknownSampleDomain = "source-unknown-sample-v1\0";

    public static SourceStructuralInventory Build(string payloadJson) =>
        Build(payloadJson, DateTimeOffset.UnixEpoch);

    public static SourceStructuralInventory Build(string payloadJson, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(payloadJson);
        using var document = JsonDocument.Parse(payloadJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An OTLP trace request must be a JSON object.");
        }

        var walker = new Walker(observedAt);
        walker.WalkEnvelope(document.RootElement, SourceStructuralEnvelope.Request);
        return SourceStructuralInventory.Create(walker.Occurrences, walker.HasRequiredTraceSignal);
    }

    private sealed class Walker(DateTimeOffset observedAt)
    {
        public List<SourceStructuralOccurrence> Occurrences { get; } = [];
        public bool HasRequiredTraceSignal { get; private set; }

        public void WalkEnvelope(JsonElement element, SourceStructuralEnvelope envelope)
        {
            AddRecognized(envelope, SourceStructuralRole.Envelope, SourceStructuralVocabulary.EnvelopeWire(envelope), SourceStructuralType.Object);
            if (envelope == SourceStructuralEnvelope.Span)
            {
                HasRequiredTraceSignal = true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!OtlpTraceSchema.TryGetField(envelope, property.Name, out var field))
                {
                    AddUnknownProperty(envelope, property);
                    continue;
                }

                if (!HasAcceptedRepresentation(field, property.Value))
                {
                    AddKnownWrongType(field, property.Value.ValueKind);
                    continue;
                }

                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in property.Value.EnumerateArray())
                    {
                        if (HasAcceptedArrayElement(field, item.ValueKind))
                        {
                            EmitAcceptedField(field, item);
                        }
                        else
                        {
                            AddKnownWrongType(field, item.ValueKind);
                        }
                    }
                    continue;
                }

                EmitAcceptedField(field, property.Value);
            }
        }

        private void EmitAcceptedField(OtlpTraceField field, JsonElement value)
        {
            AddRecognized(field.Envelope, SourceStructuralRole.KnownField, field.FieldCode, field.SemanticType);
            switch (field.Disposition)
            {
                case OtlpTraceFieldDisposition.Value:
                case OtlpTraceFieldDisposition.TraceIgnored:
                    return;
                case OtlpTraceFieldDisposition.ProducerName:
                    AddProducerName(
                        field.Envelope,
                        field.ProducerRole ?? throw new InvalidOperationException("Producer fields require a producer role."),
                        value.GetString() ?? string.Empty);
                    return;
                case OtlpTraceFieldDisposition.ChildEnvelope:
                    WalkEnvelope(
                        value,
                        field.ChildEnvelope ?? throw new InvalidOperationException("Child fields require a child envelope."));
                    return;
                default:
                    throw new ArgumentOutOfRangeException(nameof(field.Disposition));
            }
        }

        private void AddProducerName(SourceStructuralEnvelope envelope, SourceStructuralRole role, string rawName)
        {
            Occurrences.Add(SourceStructuralOccurrence.Create(
                envelope,
                role,
                SourceStructuralNameToken.FromProducerName(role, rawName),
                SourceStructuralType.String,
                SourceOccurrenceCount.Create(1)));
        }

        private void AddUnknownProperty(SourceStructuralEnvelope envelope, JsonProperty property)
        {
            var type = ActualType(property.Value.ValueKind);
            var propertyToken = HashUnknownPropertyName(property.Name);
            AddUnknown(envelope, $"json:{SourceStructuralVocabulary.EnvelopeWire(envelope)}:property:{propertyToken}:type:{SourceStructuralVocabulary.TypeWire(type)}", type);
        }

        private void AddKnownWrongType(OtlpTraceField field, JsonValueKind actualKind)
        {
            var type = ActualType(actualKind);
            AddUnknown(
                field.Envelope,
                $"json:{SourceStructuralVocabulary.EnvelopeWire(field.Envelope)}:known:{field.FieldCode}:actual:{SourceStructuralVocabulary.TypeWire(type)}",
                type);
        }

        private void AddUnknown(SourceStructuralEnvelope envelope, string canonicalName, SourceStructuralType type)
        {
            var name = SourceStructuralNameToken.ParseCanonical(canonicalName);
            var count = SourceOccurrenceCount.Create(1);
            var unknown = SourceUnknownIdentity.Create(
                SourceUnknownKind.Attribute,
                name,
                count,
                observedAt,
                observedAt,
                SampleReference(canonicalName));
            Occurrences.Add(SourceStructuralOccurrence.Create(
                envelope,
                SourceStructuralRole.UnknownJsonProperty,
                name,
                type,
                count,
                unknown));
        }

        private void AddRecognized(
            SourceStructuralEnvelope envelope,
            SourceStructuralRole role,
            string canonicalName,
            SourceStructuralType type)
        {
            Occurrences.Add(SourceStructuralOccurrence.Create(
                envelope,
                role,
                SourceStructuralNameToken.ParseCanonical(canonicalName),
                type,
                SourceOccurrenceCount.Create(1)));
        }

    }

    private static bool HasAcceptedRepresentation(OtlpTraceField field, JsonElement value) => field.JsonRepresentation switch
    {
        OtlpJsonRepresentation.Object => value.ValueKind == JsonValueKind.Object,
        OtlpJsonRepresentation.Array => value.ValueKind == JsonValueKind.Array,
        OtlpJsonRepresentation.String => value.ValueKind == JsonValueKind.String,
        OtlpJsonRepresentation.DecimalString => IsCanonicalDecimalString(field, value),
        OtlpJsonRepresentation.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        OtlpJsonRepresentation.Number => value.ValueKind == JsonValueKind.Number,
        _ => throw new ArgumentOutOfRangeException(nameof(field.JsonRepresentation)),
    };

    private static bool HasAcceptedArrayElement(OtlpTraceField field, JsonValueKind kind) => field.Disposition switch
    {
        OtlpTraceFieldDisposition.ChildEnvelope => kind == JsonValueKind.Object,
        OtlpTraceFieldDisposition.ProducerName => kind == JsonValueKind.String,
        _ => true,
    };

    private static bool IsCanonicalDecimalString(OtlpTraceField field, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = value.GetString() ?? string.Empty;
        var signed = field.FieldCode == "any_value.int";
        var digitStart = 0;
        if (text.Length != 0 && text[0] == '-')
        {
            if (!signed)
            {
                return false;
            }
            digitStart = 1;
        }
        if (digitStart == text.Length || text.AsSpan(digitStart).IndexOfAnyExceptInRange('0', '9') >= 0)
        {
            return false;
        }

        return signed
            ? long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)
            : ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    private static SourceStructuralType ActualType(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => SourceStructuralType.Object,
        JsonValueKind.Array => SourceStructuralType.Array,
        JsonValueKind.String => SourceStructuralType.String,
        JsonValueKind.Number => SourceStructuralType.Double,
        JsonValueKind.True or JsonValueKind.False => SourceStructuralType.Bool,
        JsonValueKind.Null => SourceStructuralType.Null,
        _ => throw new JsonException("Undefined JSON values cannot be structurally inventoried."),
    };

    private static string HashUnknownPropertyName(string rawName)
    {
        var bytes = Encoding.UTF8.GetBytes($"source-structure-v1\0unknown_json_property\0{rawName}");
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    private static string SampleReference(string canonicalName)
    {
        var bytes = Encoding.UTF8.GetBytes($"{UnknownSampleDomain}{canonicalName}");
        return $"sample:v1:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }
}

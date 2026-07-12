using System.Buffers.Binary;
using System.Security.Cryptography;

namespace CopilotAgentObservability.Telemetry;

internal static class OtlpProtobufStructuralWalker
{
    private const string UnknownSampleDomain = "source-unknown-sample-v1\0";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly IReadOnlyDictionary<(SourceStructuralEnvelope Envelope, int Tag, OtlpProtobufWireType WireType), OtlpTraceField> Fields =
        OtlpTraceSchema.Fields.ToDictionary(field => (field.Envelope, field.ProtobufTag, field.ProtobufWireType));

    public static DecodedOtlpTracePayload Decode(ReadOnlySpan<byte> payload) => Decode(payload, DateTimeOffset.UnixEpoch);

    public static DecodedOtlpTracePayload Decode(ReadOnlySpan<byte> payload, DateTimeOffset observedAt)
    {
        var walker = new Walker(observedAt);
        var request = walker.WalkEnvelope(payload, SourceStructuralEnvelope.Request);
        var payloadJson = request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
        var inventory = SourceStructuralInventory.Create(walker.Occurrences, walker.HasRequiredTraceSignal);
        return new DecodedOtlpTracePayload(payloadJson, inventory);
    }

    private sealed class Walker(DateTimeOffset observedAt)
    {
        public List<SourceStructuralOccurrence> Occurrences { get; } = [];
        public bool HasRequiredTraceSignal { get; private set; }

        public JsonObject WalkEnvelope(ReadOnlySpan<byte> payload, SourceStructuralEnvelope envelope)
        {
            AddRecognized(envelope, SourceStructuralRole.Envelope, SourceStructuralVocabulary.EnvelopeWire(envelope), SourceStructuralType.Object);
            if (envelope == SourceStructuralEnvelope.Span)
            {
                HasRequiredTraceSignal = true;
            }

            var result = CreateEnvelopeObject(envelope);
            foreach (var wireField in ProtoReader.ReadFields(payload))
            {
                var wireType = ToDescriptorWireType(wireField.WireType);
                if (!Fields.TryGetValue((envelope, wireField.FieldNumber, wireType), out var field))
                {
                    AddUnknown(envelope, wireField.FieldNumber, wireType);
                    continue;
                }

                AddRecognized(field.Envelope, SourceStructuralRole.KnownField, field.FieldCode, field.SemanticType);
                switch (field.Disposition)
                {
                    case OtlpTraceFieldDisposition.TraceIgnored:
                        break;
                    case OtlpTraceFieldDisposition.ChildEnvelope:
                        AddJsonValue(result, field, WalkEnvelope(
                            wireField.Value,
                            field.ChildEnvelope ?? throw new InvalidOperationException("Child fields require a child envelope.")));
                        break;
                    case OtlpTraceFieldDisposition.ProducerName:
                        var producerName = DecodeString(wireField.Value);
                        AddProducerName(
                            field.Envelope,
                            field.ProducerRole ?? throw new InvalidOperationException("Producer fields require a producer role."),
                            producerName);
                        AddJsonValue(result, field, JsonValue.Create(producerName));
                        break;
                    case OtlpTraceFieldDisposition.Value:
                        AddJsonValue(result, field, ConvertValue(field, wireField));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(field.Disposition));
                }
            }
            return result;
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

        private void AddUnknown(SourceStructuralEnvelope envelope, int fieldNumber, OtlpProtobufWireType wireType)
        {
            var type = wireType switch
            {
                OtlpProtobufWireType.Varint => SourceStructuralType.Varint,
                OtlpProtobufWireType.Fixed32 => SourceStructuralType.Fixed32,
                OtlpProtobufWireType.Fixed64 => SourceStructuralType.Fixed64,
                OtlpProtobufWireType.LengthDelimited => SourceStructuralType.LengthDelimited,
                _ => throw new ArgumentOutOfRangeException(nameof(wireType)),
            };
            var canonicalName = $"protobuf:{SourceStructuralVocabulary.EnvelopeWire(envelope)}:field:{fieldNumber}:wire:{SourceStructuralVocabulary.TypeWire(type)}";
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
                SourceStructuralRole.UnknownProtobufField,
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

    private static JsonObject CreateEnvelopeObject(SourceStructuralEnvelope envelope)
    {
        var result = new JsonObject();
        switch (envelope)
        {
            case SourceStructuralEnvelope.Request:
                result["resourceSpans"] = new JsonArray();
                break;
            case SourceStructuralEnvelope.ResourceSpans:
                result["scopeSpans"] = new JsonArray();
                break;
            case SourceStructuralEnvelope.Resource:
                result["attributes"] = new JsonArray();
                break;
            case SourceStructuralEnvelope.ScopeSpans:
                result["spans"] = new JsonArray();
                break;
            case SourceStructuralEnvelope.ArrayValue:
            case SourceStructuralEnvelope.KeyValueList:
                result["values"] = new JsonArray();
                break;
        }
        return result;
    }

    private static void AddJsonValue(JsonObject parent, OtlpTraceField field, JsonNode? value)
    {
        if (field.JsonRepresentation == OtlpJsonRepresentation.Array)
        {
            GetOrCreateArray(parent, field.JsonName).Add(value);
            return;
        }
        parent[field.JsonName] = value;
    }

    private static JsonNode? ConvertValue(OtlpTraceField field, ProtoField wireField) => field.SemanticType switch
    {
        SourceStructuralType.String => JsonValue.Create(DecodeString(wireField.Value)),
        SourceStructuralType.Bytes when field.FieldCode == "any_value.bytes" => JsonValue.Create(Convert.ToBase64String(wireField.Value)),
        SourceStructuralType.Bytes => JsonValue.Create(Convert.ToHexStringLower(wireField.Value)),
        SourceStructuralType.Bool => JsonValue.Create(wireField.Varint != 0),
        SourceStructuralType.Double => JsonValue.Create(BitConverter.Int64BitsToDouble(unchecked((long)wireField.Fixed64))),
        SourceStructuralType.Int when field.ProtobufWireType == OtlpProtobufWireType.Fixed32 => JsonValue.Create(wireField.Fixed32),
        SourceStructuralType.Int when field.ProtobufWireType == OtlpProtobufWireType.Fixed64 =>
            JsonValue.Create(wireField.Fixed64.ToString(CultureInfo.InvariantCulture)),
        SourceStructuralType.Int when field.FieldCode == "any_value.int" =>
            JsonValue.Create(unchecked((long)wireField.Varint).ToString(CultureInfo.InvariantCulture)),
        SourceStructuralType.Int => JsonValue.Create(wireField.Varint),
        _ => throw new InvalidDataException($"Unsupported protobuf value conversion for '{field.FieldCode}'."),
    };

    private static string DecodeString(ReadOnlySpan<byte> value)
    {
        try
        {
            return StrictUtf8.GetString(value);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("protobuf string field is not valid UTF-8.");
        }
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string propertyName)
    {
        if (parent.TryGetPropertyValue(propertyName, out var node) && node is JsonArray existing)
        {
            return existing;
        }
        var array = new JsonArray();
        parent[propertyName] = array;
        return array;
    }

    private static OtlpProtobufWireType ToDescriptorWireType(ProtoWireType wireType) => wireType switch
    {
        ProtoWireType.Varint => OtlpProtobufWireType.Varint,
        ProtoWireType.Fixed64 => OtlpProtobufWireType.Fixed64,
        ProtoWireType.LengthDelimited => OtlpProtobufWireType.LengthDelimited,
        ProtoWireType.Fixed32 => OtlpProtobufWireType.Fixed32,
        _ => throw new ArgumentOutOfRangeException(nameof(wireType)),
    };

    private static string SampleReference(string canonicalName)
    {
        var bytes = Encoding.UTF8.GetBytes($"{UnknownSampleDomain}{canonicalName}");
        return $"sample:v1:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    private readonly ref struct ProtoField(
        int fieldNumber,
        ProtoWireType wireType,
        ReadOnlySpan<byte> value,
        ulong varint,
        uint fixed32,
        ulong fixed64)
    {
        public int FieldNumber { get; } = fieldNumber;
        public ProtoWireType WireType { get; } = wireType;
        public ReadOnlySpan<byte> Value { get; } = value;
        public ulong Varint { get; } = varint;
        public uint Fixed32 { get; } = fixed32;
        public ulong Fixed64 { get; } = fixed64;
    }

    private enum ProtoWireType
    {
        Varint = 0,
        Fixed64 = 1,
        LengthDelimited = 2,
        Fixed32 = 5,
    }

    private ref struct ProtoReader
    {
        private ReadOnlySpan<byte> remaining;

        private ProtoReader(ReadOnlySpan<byte> payload) => remaining = payload;

        public static ProtoReader ReadFields(ReadOnlySpan<byte> payload) => new(payload);
        public ProtoReader GetEnumerator() => this;
        public ProtoField Current { get; private set; }

        public bool MoveNext()
        {
            if (remaining.IsEmpty)
            {
                return false;
            }

            var tag = ReadVarint();
            var fieldNumber = tag >> 3;
            if (fieldNumber is 0 or > 536_870_911)
            {
                throw new InvalidDataException("protobuf field number is invalid.");
            }

            var wireType = (ProtoWireType)(tag & 0x7);
            Current = wireType switch
            {
                ProtoWireType.Varint => new ProtoField((int)fieldNumber, wireType, default, ReadVarint(), 0, 0),
                ProtoWireType.Fixed64 => new ProtoField((int)fieldNumber, wireType, default, 0, 0, ReadFixed64()),
                ProtoWireType.LengthDelimited => ReadLengthDelimited((int)fieldNumber, wireType),
                ProtoWireType.Fixed32 => new ProtoField((int)fieldNumber, wireType, default, 0, ReadFixed32(), 0),
                _ => throw new InvalidDataException($"unsupported protobuf wire type '{wireType}'."),
            };
            return true;
        }

        private ProtoField ReadLengthDelimited(int fieldNumber, ProtoWireType wireType)
        {
            var rawLength = ReadVarint();
            if (rawLength > int.MaxValue)
            {
                throw new InvalidDataException("protobuf length-delimited field length is too large.");
            }
            var length = (int)rawLength;
            if (length > remaining.Length)
            {
                throw new InvalidDataException("protobuf length-delimited field exceeds payload length.");
            }
            var value = remaining[..length];
            remaining = remaining[length..];
            return new ProtoField(fieldNumber, wireType, value, 0, 0, 0);
        }

        private uint ReadFixed32()
        {
            if (remaining.Length < 4)
            {
                throw new InvalidDataException("protobuf fixed32 field exceeds payload length.");
            }
            var value = BinaryPrimitives.ReadUInt32LittleEndian(remaining);
            remaining = remaining[4..];
            return value;
        }

        private ulong ReadFixed64()
        {
            if (remaining.Length < 8)
            {
                throw new InvalidDataException("protobuf fixed64 field exceeds payload length.");
            }
            var value = BinaryPrimitives.ReadUInt64LittleEndian(remaining);
            remaining = remaining[8..];
            return value;
        }

        private ulong ReadVarint()
        {
            ulong value = 0;
            for (var index = 0; index < 10; index++)
            {
                if (remaining.IsEmpty)
                {
                    throw new InvalidDataException("protobuf varint field exceeds payload length.");
                }
                var current = remaining[0];
                remaining = remaining[1..];
                if (index == 9 && current > 1)
                {
                    throw new InvalidDataException("protobuf varint field is too long.");
                }
                value |= (ulong)(current & 0x7F) << (index * 7);
                if ((current & 0x80) == 0)
                {
                    return value;
                }
            }
            throw new InvalidDataException("protobuf varint field is too long.");
        }
    }
}

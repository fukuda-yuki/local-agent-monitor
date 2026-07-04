namespace CopilotAgentObservability.Telemetry;

internal static class OtlpProtobufTraceConverter
{
    public static string ConvertTraceRequestToRawOtlpJson(ReadOnlySpan<byte> payload)
    {
        var request = ParseExportTraceServiceRequest(payload);
        return request.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
    }

    private static JsonObject ParseExportTraceServiceRequest(ReadOnlySpan<byte> payload)
    {
        var root = new JsonObject { ["resourceSpans"] = new JsonArray() };
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            if (field.FieldNumber == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                root["resourceSpans"]!.AsArray().Add(ParseResourceSpans(field.Value));
            }
        }

        return root;
    }

    private static JsonObject ParseResourceSpans(ReadOnlySpan<byte> payload)
    {
        var resourceSpans = new JsonObject { ["scopeSpans"] = new JsonArray() };
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            if (field.FieldNumber == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                resourceSpans["resource"] = ParseResource(field.Value);
            }
            else if (field.FieldNumber == 2 && field.WireType == ProtoWireType.LengthDelimited)
            {
                resourceSpans["scopeSpans"]!.AsArray().Add(ParseScopeSpans(field.Value));
            }
            else if (field.FieldNumber == 3 && field.WireType == ProtoWireType.LengthDelimited)
            {
                resourceSpans["schemaUrl"] = Encoding.UTF8.GetString(field.Value);
            }
        }

        return resourceSpans;
    }

    private static JsonObject ParseResource(ReadOnlySpan<byte> payload)
    {
        var resource = new JsonObject { ["attributes"] = new JsonArray() };
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            if (field.FieldNumber == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                resource["attributes"]!.AsArray().Add(ParseKeyValue(field.Value));
            }
        }

        return resource;
    }

    private static JsonObject ParseScopeSpans(ReadOnlySpan<byte> payload)
    {
        var scopeSpans = new JsonObject { ["spans"] = new JsonArray() };
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            if (field.FieldNumber == 2 && field.WireType == ProtoWireType.LengthDelimited)
            {
                scopeSpans["spans"]!.AsArray().Add(ParseSpan(field.Value));
            }
            else if (field.FieldNumber == 3 && field.WireType == ProtoWireType.LengthDelimited)
            {
                scopeSpans["schemaUrl"] = Encoding.UTF8.GetString(field.Value);
            }
        }

        return scopeSpans;
    }

    private static JsonObject ParseSpan(ReadOnlySpan<byte> payload)
    {
        var span = new JsonObject();
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            switch (field.FieldNumber, field.WireType)
            {
                case (1, ProtoWireType.LengthDelimited):
                    span["traceId"] = Convert.ToHexString(field.Value).ToLowerInvariant();
                    break;
                case (2, ProtoWireType.LengthDelimited):
                    span["spanId"] = Convert.ToHexString(field.Value).ToLowerInvariant();
                    break;
                case (4, ProtoWireType.LengthDelimited):
                    span["parentSpanId"] = Convert.ToHexString(field.Value).ToLowerInvariant();
                    break;
                case (5, ProtoWireType.LengthDelimited):
                    span["name"] = Encoding.UTF8.GetString(field.Value);
                    break;
                case (6, ProtoWireType.Varint):
                    span["kind"] = field.Varint;
                    break;
                case (7, ProtoWireType.Fixed64):
                    span["startTimeUnixNano"] = field.Fixed64.ToString(CultureInfo.InvariantCulture);
                    break;
                case (8, ProtoWireType.Fixed64):
                    span["endTimeUnixNano"] = field.Fixed64.ToString(CultureInfo.InvariantCulture);
                    break;
                case (9, ProtoWireType.LengthDelimited):
                    GetOrCreateArray(span, "attributes").Add(ParseKeyValue(field.Value));
                    break;
                case (11, ProtoWireType.LengthDelimited):
                    GetOrCreateArray(span, "events").Add(ParseEvent(field.Value));
                    break;
                case (15, ProtoWireType.LengthDelimited):
                    span["status"] = ParseStatus(field.Value);
                    break;
            }
        }

        return span;
    }

    private static JsonObject ParseEvent(ReadOnlySpan<byte> payload)
    {
        var spanEvent = new JsonObject();
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            switch (field.FieldNumber, field.WireType)
            {
                case (1, ProtoWireType.Fixed64):
                    spanEvent["timeUnixNano"] = field.Fixed64.ToString(CultureInfo.InvariantCulture);
                    break;
                case (2, ProtoWireType.LengthDelimited):
                    spanEvent["name"] = Encoding.UTF8.GetString(field.Value);
                    break;
                case (3, ProtoWireType.LengthDelimited):
                    GetOrCreateArray(spanEvent, "attributes").Add(ParseKeyValue(field.Value));
                    break;
            }
        }

        return spanEvent;
    }

    private static JsonObject ParseStatus(ReadOnlySpan<byte> payload)
    {
        var status = new JsonObject();
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            switch (field.FieldNumber, field.WireType)
            {
                case (2, ProtoWireType.LengthDelimited):
                    status["message"] = Encoding.UTF8.GetString(field.Value);
                    break;
                case (3, ProtoWireType.Varint):
                    status["code"] = field.Varint;
                    break;
            }
        }

        return status;
    }

    private static JsonObject ParseKeyValue(ReadOnlySpan<byte> payload)
    {
        var keyValue = new JsonObject();
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            if (field.FieldNumber == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                keyValue["key"] = Encoding.UTF8.GetString(field.Value);
            }
            else if (field.FieldNumber == 2 && field.WireType == ProtoWireType.LengthDelimited)
            {
                keyValue["value"] = ParseAnyValue(field.Value);
            }
        }

        return keyValue;
    }

    private static JsonObject ParseAnyValue(ReadOnlySpan<byte> payload)
    {
        var value = new JsonObject();
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            switch (field.FieldNumber, field.WireType)
            {
                case (1, ProtoWireType.LengthDelimited):
                    value["stringValue"] = Encoding.UTF8.GetString(field.Value);
                    break;
                case (2, ProtoWireType.Varint):
                    value["boolValue"] = field.Varint != 0;
                    break;
                case (3, ProtoWireType.Varint):
                    value["intValue"] = unchecked((long)field.Varint).ToString(CultureInfo.InvariantCulture);
                    break;
                case (4, ProtoWireType.Fixed64):
                    value["doubleValue"] = BitConverter.Int64BitsToDouble((long)field.Fixed64);
                    break;
                case (5, ProtoWireType.LengthDelimited):
                    value["arrayValue"] = ParseArrayValue(field.Value);
                    break;
                case (6, ProtoWireType.LengthDelimited):
                    value["kvlistValue"] = ParseKeyValueList(field.Value);
                    break;
                case (7, ProtoWireType.LengthDelimited):
                    value["bytesValue"] = Convert.ToBase64String(field.Value);
                    break;
            }
        }

        return value;
    }

    private static JsonObject ParseArrayValue(ReadOnlySpan<byte> payload)
    {
        var arrayValue = new JsonObject { ["values"] = new JsonArray() };
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            if (field.FieldNumber == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                arrayValue["values"]!.AsArray().Add(ParseAnyValue(field.Value));
            }
        }

        return arrayValue;
    }

    private static JsonObject ParseKeyValueList(ReadOnlySpan<byte> payload)
    {
        var keyValueList = new JsonObject { ["values"] = new JsonArray() };
        foreach (var field in ProtoReader.ReadFields(payload))
        {
            if (field.FieldNumber == 1 && field.WireType == ProtoWireType.LengthDelimited)
            {
                keyValueList["values"]!.AsArray().Add(ParseKeyValue(field.Value));
            }
        }

        return keyValueList;
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

    private readonly ref struct ProtoField
    {
        public ProtoField(int fieldNumber, ProtoWireType wireType, ReadOnlySpan<byte> value, ulong varint, ulong fixed64)
        {
            FieldNumber = fieldNumber;
            WireType = wireType;
            Value = value;
            Varint = varint;
            Fixed64 = fixed64;
        }

        public int FieldNumber { get; }

        public ProtoWireType WireType { get; }

        public ReadOnlySpan<byte> Value { get; }

        public ulong Varint { get; }

        public ulong Fixed64 { get; }
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

        private ProtoReader(ReadOnlySpan<byte> payload)
        {
            remaining = payload;
        }

        public static ProtoReader ReadFields(ReadOnlySpan<byte> payload)
        {
            return new ProtoReader(payload);
        }

        public ProtoReader GetEnumerator()
        {
            return this;
        }

        public ProtoField Current { get; private set; }

        public bool MoveNext()
        {
            if (remaining.IsEmpty)
            {
                return false;
            }

            var tag = ReadVarint();
            var fieldNumber = (int)(tag >> 3);
            var wireType = (ProtoWireType)(tag & 0x7);
            Current = wireType switch
            {
                ProtoWireType.Varint => new ProtoField(fieldNumber, wireType, default, ReadVarint(), 0),
                ProtoWireType.Fixed64 => new ProtoField(fieldNumber, wireType, default, 0, ReadFixed64()),
                ProtoWireType.LengthDelimited => ReadLengthDelimited(fieldNumber, wireType),
                ProtoWireType.Fixed32 => ReadFixed32(fieldNumber, wireType),
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
            return new ProtoField(fieldNumber, wireType, value, 0, 0);
        }

        private ProtoField ReadFixed32(int fieldNumber, ProtoWireType wireType)
        {
            if (remaining.Length < 4)
            {
                throw new InvalidDataException("protobuf fixed32 field exceeds payload length.");
            }

            remaining = remaining[4..];
            return new ProtoField(fieldNumber, wireType, default, 0, 0);
        }

        private ulong ReadFixed64()
        {
            if (remaining.Length < 8)
            {
                throw new InvalidDataException("protobuf fixed64 field exceeds payload length.");
            }

            var value = BitConverter.ToUInt64(remaining[..8]);
            remaining = remaining[8..];
            return value;
        }

        private ulong ReadVarint()
        {
            ulong value = 0;
            var shift = 0;
            for (var index = 0; index < 10; index++)
            {
                if (remaining.IsEmpty)
                {
                    throw new InvalidDataException("protobuf varint field exceeds payload length.");
                }

                var current = remaining[0];
                remaining = remaining[1..];
                value |= (ulong)(current & 0x7F) << shift;
                if ((current & 0x80) == 0)
                {
                    return value;
                }

                shift += 7;
            }

            throw new InvalidDataException("protobuf varint field is too long.");
        }
    }
}

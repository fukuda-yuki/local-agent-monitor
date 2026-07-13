using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class OtlpProtobufTraceConverterTests
{
    private const string KnownNestedJson = """
        {
          "resourceSpans": [{
            "schemaUrl": "https://resource.example/schema",
            "resource": {
              "attributes": [{
                "key": "outer.key",
                "value": {
                  "arrayValue": {
                    "values": [{
                      "kvlistValue": {
                        "values": [{
                          "key": "nested.key",
                          "keyStrindex": 7,
                          "value": {
                            "stringValue": "nested value",
                            "stringValueStrindex": 8
                          }
                        }]
                      }
                    }]
                  }
                }
              }],
              "droppedAttributesCount": 1,
              "entityRefs": [{
                "schemaUrl": "https://entity.example/schema",
                "type": "service",
                "idKeys": ["service.id"],
                "descriptionKeys": ["service.description"]
              }]
            },
            "scopeSpans": [{
              "schemaUrl": "https://scope.example/schema",
              "scope": { "name": "scope", "version": "1.0" },
              "spans": [{
                "name": "span",
                "flags": 257,
                "events": [{ "name": "event" }],
                "links": [{ "flags": 513 }],
                "status": { "code": 1 }
              }]
            }]
          }]
        }
        """;

    [Fact]
    public void ConvertTraceRequestToRawOtlpJson_ConvertsRequiredTraceFields()
    {
        var traceId = Convert.FromHexString("11111111111111111111111111111111");
        var spanId = Convert.FromHexString("2222222222222222");
        var resource = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(1, OtlpProtobufTestPayload.KeyValue("client.kind", OtlpProtobufTestPayload.StringValue("vscode-copilot-chat"))),
            OtlpProtobufTestPayload.LengthDelimited(1, OtlpProtobufTestPayload.KeyValue("experiment.id", OtlpProtobufTestPayload.StringValue("baseline"))));
        var spanMessage = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(1, traceId),
            OtlpProtobufTestPayload.LengthDelimited(2, spanId),
            OtlpProtobufTestPayload.StringField(5, "chat gpt-4o"),
            OtlpProtobufTestPayload.VarintField(6, 1),
            OtlpProtobufTestPayload.Fixed64Field(7, 1_000_000_000),
            OtlpProtobufTestPayload.Fixed64Field(8, 1_500_000_000),
            OtlpProtobufTestPayload.LengthDelimited(9, OtlpProtobufTestPayload.KeyValue("gen_ai.operation.name", OtlpProtobufTestPayload.StringValue("chat"))),
            OtlpProtobufTestPayload.LengthDelimited(9, OtlpProtobufTestPayload.KeyValue("gen_ai.usage.input_tokens", OtlpProtobufTestPayload.IntValue(10))),
            OtlpProtobufTestPayload.LengthDelimited(9, OtlpProtobufTestPayload.KeyValue("gen_ai.usage.output_tokens", OtlpProtobufTestPayload.IntValue(5))),
            OtlpProtobufTestPayload.LengthDelimited(9, OtlpProtobufTestPayload.KeyValue("synthetic.signed", OtlpProtobufTestPayload.IntValue(unchecked((ulong)-7L)))),
            OtlpProtobufTestPayload.LengthDelimited(11, OtlpProtobufTestPayload.Message(
                OtlpProtobufTestPayload.Fixed64Field(1, 1_100_000_000),
                OtlpProtobufTestPayload.StringField(2, "gen_ai.first_token"),
                OtlpProtobufTestPayload.LengthDelimited(3, OtlpProtobufTestPayload.KeyValue("event.kind", OtlpProtobufTestPayload.StringValue("first-token"))))),
            OtlpProtobufTestPayload.LengthDelimited(15, OtlpProtobufTestPayload.Message(
                OtlpProtobufTestPayload.StringField(2, "synthetic error"),
                OtlpProtobufTestPayload.VarintField(3, 2))));
        var scopeSpans = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(2, spanMessage));
        var resourceSpans = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(1, resource), OtlpProtobufTestPayload.LengthDelimited(2, scopeSpans));
        var request = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(1, resourceSpans));

        var json = OtlpProtobufTraceConverter.ConvertTraceRequestToRawOtlpJson(request);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var resourceSpan = root.GetProperty("resourceSpans")[0];
        var resourceAttributes = resourceSpan.GetProperty("resource").GetProperty("attributes");
        Assert.Equal("client.kind", resourceAttributes[0].GetProperty("key").GetString());
        Assert.Equal("vscode-copilot-chat", resourceAttributes[0].GetProperty("value").GetProperty("stringValue").GetString());
        Assert.Equal("experiment.id", resourceAttributes[1].GetProperty("key").GetString());
        Assert.Equal("baseline", resourceAttributes[1].GetProperty("value").GetProperty("stringValue").GetString());

        var jsonSpan = resourceSpan.GetProperty("scopeSpans")[0].GetProperty("spans")[0];
        Assert.Equal("11111111111111111111111111111111", jsonSpan.GetProperty("traceId").GetString());
        Assert.Equal("2222222222222222", jsonSpan.GetProperty("spanId").GetString());
        Assert.Equal("chat gpt-4o", jsonSpan.GetProperty("name").GetString());
        Assert.Equal("1", jsonSpan.GetProperty("kind").GetRawText());
        Assert.Equal("1000000000", jsonSpan.GetProperty("startTimeUnixNano").GetString());
        Assert.Equal("1500000000", jsonSpan.GetProperty("endTimeUnixNano").GetString());

        var attributes = jsonSpan.GetProperty("attributes");
        Assert.Equal("gen_ai.operation.name", attributes[0].GetProperty("key").GetString());
        Assert.Equal("chat", attributes[0].GetProperty("value").GetProperty("stringValue").GetString());
        Assert.Equal("gen_ai.usage.input_tokens", attributes[1].GetProperty("key").GetString());
        Assert.Equal("10", attributes[1].GetProperty("value").GetProperty("intValue").GetString());
        Assert.Equal("gen_ai.usage.output_tokens", attributes[2].GetProperty("key").GetString());
        Assert.Equal("5", attributes[2].GetProperty("value").GetProperty("intValue").GetString());
        Assert.Equal("synthetic.signed", attributes[3].GetProperty("key").GetString());
        Assert.Equal("-7", attributes[3].GetProperty("value").GetProperty("intValue").GetString());

        var spanEvent = jsonSpan.GetProperty("events")[0];
        Assert.Equal("1100000000", spanEvent.GetProperty("timeUnixNano").GetString());
        Assert.Equal("gen_ai.first_token", spanEvent.GetProperty("name").GetString());
        Assert.Equal("event.kind", spanEvent.GetProperty("attributes")[0].GetProperty("key").GetString());

        var status = jsonSpan.GetProperty("status");
        Assert.Equal("synthetic error", status.GetProperty("message").GetString());
        Assert.Equal("2", status.GetProperty("code").GetRawText());
    }

    [Fact]
    public void ConvertTraceRequestToRawOtlpJson_PreservesStructuredAnyValues()
    {
        var traceId = Convert.FromHexString("11111111111111111111111111111111");
        var spanId = Convert.FromHexString("2222222222222222");
        var arrayValue = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(1, OtlpProtobufTestPayload.StringValue("first")),
            OtlpProtobufTestPayload.LengthDelimited(1, OtlpProtobufTestPayload.IntValue(2)));
        var keyValueList = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(
                1,
                OtlpProtobufTestPayload.KeyValue("nested", OtlpProtobufTestPayload.StringValue("value"))));
        var spanMessage = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(1, traceId),
            OtlpProtobufTestPayload.LengthDelimited(2, spanId),
            OtlpProtobufTestPayload.StringField(5, "structured attributes"),
            OtlpProtobufTestPayload.Fixed64Field(7, 1_000_000_000),
            OtlpProtobufTestPayload.Fixed64Field(8, 1_500_000_000),
            OtlpProtobufTestPayload.LengthDelimited(
                9,
                OtlpProtobufTestPayload.KeyValue(
                    "synthetic.array",
                    OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(5, arrayValue)))),
            OtlpProtobufTestPayload.LengthDelimited(
                9,
                OtlpProtobufTestPayload.KeyValue(
                    "synthetic.kvlist",
                    OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(6, keyValueList)))));
        var scopeSpans = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(2, spanMessage));
        var resourceSpans = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(2, scopeSpans));
        var request = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(1, resourceSpans));

        var json = OtlpProtobufTraceConverter.ConvertTraceRequestToRawOtlpJson(request);

        using var document = JsonDocument.Parse(json);
        var attributes = document.RootElement
            .GetProperty("resourceSpans")[0]
            .GetProperty("scopeSpans")[0]
            .GetProperty("spans")[0]
            .GetProperty("attributes");
        var array = attributes[0].GetProperty("value").GetProperty("arrayValue").GetProperty("values");
        Assert.Equal("first", array[0].GetProperty("stringValue").GetString());
        Assert.Equal("2", array[1].GetProperty("intValue").GetString());
        var kvlist = attributes[1].GetProperty("value").GetProperty("kvlistValue").GetProperty("values");
        Assert.Equal("nested", kvlist[0].GetProperty("key").GetString());
        Assert.Equal("value", kvlist[0].GetProperty("value").GetProperty("stringValue").GetString());
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public void ConvertTraceRequestToRawOtlpJson_RejectsMalformedPayloads(byte[] payload)
    {
        Assert.Throws<InvalidDataException>(() => OtlpProtobufTraceConverter.ConvertTraceRequestToRawOtlpJson(payload));
    }

    [Fact]
    public void Build_KnownNestedEnvelope_JsonAndProtobufMatchGoldenFingerprints()
    {
        var jsonInventory = OtlpJsonStructuralWalker.Build(KnownNestedJson, DateTimeOffset.UnixEpoch);
        var protobuf = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/x-protobuf",
            BuildKnownNestedProtobuf(includeUnknownAtEveryEnvelope: false));

        Assert.Equal("0993807efa4940fb29337778155ae220ed22ede5441484aa887461d6e429aac1", jsonInventory.SchemaFingerprint);
        Assert.Equal("52e9abbd95c7b1fc3dbfef75ac16ad33c98e009095f7d0648bf6a8a4ddd80e9f", jsonInventory.InventoryHash);
        Assert.Equal("0993807efa4940fb29337778155ae220ed22ede5441484aa887461d6e429aac1", protobuf.StructuralInventory.SchemaFingerprint);
        Assert.Equal("52e9abbd95c7b1fc3dbfef75ac16ad33c98e009095f7d0648bf6a8a4ddd80e9f", protobuf.StructuralInventory.InventoryHash);
        Assert.False(jsonInventory.HasUnknownFields);
        Assert.False(protobuf.StructuralInventory.HasUnknownFields);
        using var document = JsonDocument.Parse(protobuf.PayloadJson);
        var entityRef = document.RootElement.GetProperty("resourceSpans")[0].GetProperty("resource").GetProperty("entityRefs")[0];
        Assert.Equal("https://entity.example/schema", entityRef.GetProperty("schemaUrl").GetString());
        Assert.Equal("service.id", entityRef.GetProperty("idKeys")[0].GetString());
        Assert.Equal("service.description", entityRef.GetProperty("descriptionKeys")[0].GetString());
        Assert.DoesNotContain("Strindex", protobuf.PayloadJson, StringComparison.Ordinal);
        Assert.Contains(protobuf.StructuralInventory.StructuralOccurrences, item => item.Name.Value == "any_value.string_strindex" && item.Unknown is null);
        Assert.Contains(protobuf.StructuralInventory.StructuralOccurrences, item => item.Name.Value == "key_value.key_strindex" && item.Unknown is null);
        var serializedInventory = JsonSerializer.Serialize(protobuf.StructuralInventory);
        Assert.DoesNotContain("nested value", serializedInventory, StringComparison.Ordinal);
        Assert.DoesNotContain("https://entity.example/schema", serializedInventory, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnknownProtobufFieldAtEachEnvelope_IsCapturedBeforeConversion()
    {
        var decoded = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/x-protobuf",
            BuildKnownNestedProtobuf(includeUnknownAtEveryEnvelope: true));

        var expected = new[]
        {
            "request", "resource_spans", "resource", "scope_spans", "scope", "span", "event", "link",
            "status", "key_value", "any_value", "array_value", "key_value_list", "entity_ref",
        }.SelectMany(envelope => new[] { "varint", "fixed32", "fixed64", "length_delimited" }
            .Select(wire => $"protobuf:{envelope}:field:99:wire:{wire}"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(56, decoded.StructuralInventory.UnknownAttributeCount);
        Assert.Equal(expected, decoded.StructuralInventory.RetainedUnknownIdentities.Select(item => item.Name.Value).Order(StringComparer.Ordinal));
        Assert.DoesNotContain("wire-marker-secret", decoded.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", JsonSerializer.Serialize(decoded.StructuralInventory), StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_OfficialSpanAndLinkFlagsFixed32_AreRecognizedAndConverted()
    {
        var decoded = OtlpTracePayloadDecoder.DecodeTracePayload(
            "application/x-protobuf",
            BuildKnownNestedProtobuf(includeUnknownAtEveryEnvelope: false));

        using var document = JsonDocument.Parse(decoded.PayloadJson);
        var span = document.RootElement.GetProperty("resourceSpans")[0].GetProperty("scopeSpans")[0].GetProperty("spans")[0];
        Assert.Equal(257u, span.GetProperty("flags").GetUInt32());
        Assert.Equal(513u, span.GetProperty("links")[0].GetProperty("flags").GetUInt32());
        Assert.Contains(decoded.StructuralInventory.StructuralOccurrences, item => item.Name.Value == "span.flags" && item.Unknown is null);
        Assert.Contains(decoded.StructuralInventory.StructuralOccurrences, item => item.Name.Value == "link.flags" && item.Unknown is null);
        Assert.DoesNotContain(decoded.StructuralInventory.RetainedUnknownIdentities, item => item.Name.Value.Contains("field:16", StringComparison.Ordinal) || item.Name.Value.Contains("field:6", StringComparison.Ordinal));
    }

    [Fact]
    public void Decode_VarintFlags_AreUnknownRatherThanRecognized()
    {
        var decoded = OtlpTracePayloadDecoder.DecodeTracePayload("application/x-protobuf", BuildVarintFlagsProtobuf());

        Assert.Equal([
            "protobuf:link:field:6:wire:varint",
            "protobuf:span:field:16:wire:varint",
        ], decoded.StructuralInventory.RetainedUnknownIdentities.Select(item => item.Name.Value).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(decoded.StructuralInventory.StructuralOccurrences, item => item.Name.Value is "span.flags" or "link.flags");
        Assert.DoesNotContain("flags", decoded.PayloadJson, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedPayloads))]
    public void DecodeTracePayload_MalformedProtobufIsAdapterParseFailure(byte[] payload)
    {
        Assert.Throws<InvalidDataException>(() => OtlpTracePayloadDecoder.DecodeTracePayload("application/x-protobuf", payload));
    }

    [Theory]
    [MemberData(nameof(InvalidUtf8Payloads))]
    public void DecodeTracePayload_InvalidUtf8StringIsAdapterParseFailure(string path, byte[] payload)
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            OtlpTracePayloadDecoder.DecodeTracePayload("application/x-protobuf", payload));

        Assert.Equal("protobuf string field is not valid UTF-8.", exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(path, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("C3", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("�", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RecognizedSemanticFactsMatchJsonWhileUnknownsRemainTransportScoped()
    {
        const string json = """
            {"resourceSpans":[{"scopeSpans":[{"spans":[{"name":"span","future.field":"secret"}]}]}]}
            """;
        var protobufSpan = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.StringField(5, "span"),
            OtlpProtobufTestPayload.VarintField(99, 987654321));
        var protobuf = OtlpTracePayloadDecoder.DecodeTracePayload("application/x-protobuf", WrapSpan(protobufSpan));
        var jsonInventory = OtlpJsonStructuralWalker.Build(json, DateTimeOffset.UnixEpoch);

        Assert.Equal(
            jsonInventory.StructuralOccurrences.Where(item => item.Unknown is null).Select(Identity).Order(StringComparer.Ordinal),
            protobuf.StructuralInventory.StructuralOccurrences.Where(item => item.Unknown is null).Select(Identity).Order(StringComparer.Ordinal));
        Assert.StartsWith("json:span:property:", Assert.Single(jsonInventory.RetainedUnknownIdentities).Name.Value, StringComparison.Ordinal);
        Assert.Equal("protobuf:span:field:99:wire:varint", Assert.Single(protobuf.StructuralInventory.RetainedUnknownIdentities).Name.Value);
    }

    public static IEnumerable<object[]> MalformedPayloads()
    {
        yield return [new byte[] { 0x0A, 0x05, 0x01 }];
        yield return [new byte[] { 0x09, 0x01 }];
        yield return [new byte[] { 0x08 }];
        yield return [new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }];
        yield return [new byte[] { 0x0F }];
        yield return [new byte[] { 0x00 }];
        yield return [new byte[] { 0x0D, 0x01 }];
        yield return [new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x02 }];
    }

    public static IEnumerable<object[]> InvalidUtf8Payloads()
    {
        var invalidUtf8 = new byte[] { 0xC3, 0x28 };

        yield return ["span.name", WrapSpan(OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(5, invalidUtf8)))];

        var invalidKey = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(1, invalidUtf8));
        yield return ["key_value.key", WrapSpan(OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(9, invalidKey)))];

        var invalidStringValue = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.LengthDelimited(1, invalidUtf8));
        var attribute = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.StringField(1, "safe.key"),
            OtlpProtobufTestPayload.LengthDelimited(2, invalidStringValue));
        yield return ["any_value.string", WrapSpan(OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(9, attribute)))];

        var scopeSpans = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(2, OtlpProtobufTestPayload.Message(
                OtlpProtobufTestPayload.LengthDelimited(2, []))));
        var resourceSpans = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(2, scopeSpans),
            OtlpProtobufTestPayload.LengthDelimited(3, invalidUtf8));
        yield return ["resource_spans.schema_url", OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(1, resourceSpans))];
    }

    private static byte[] BuildKnownNestedProtobuf(bool includeUnknownAtEveryEnvelope)
    {
        var inventoriedUnknownEnvelopes = new HashSet<string>(StringComparer.Ordinal);
        byte[] Envelope(string envelope, params byte[][] fields) => EnvelopeWithOptionalUnknown(
            includeUnknownAtEveryEnvelope && inventoriedUnknownEnvelopes.Add(envelope),
            fields);

        var nestedAnyValue = Envelope("any_value",
            OtlpProtobufTestPayload.StringField(1, "nested value"),
            OtlpProtobufTestPayload.VarintField(8, 8));
        var nestedKeyValue = Envelope("key_value",
            OtlpProtobufTestPayload.StringField(1, "nested.key"),
            OtlpProtobufTestPayload.LengthDelimited(2, nestedAnyValue),
            OtlpProtobufTestPayload.VarintField(3, 7));
        var keyValueList = Envelope("key_value_list", OtlpProtobufTestPayload.LengthDelimited(1, nestedKeyValue));
        var arrayItem = Envelope("any_value", OtlpProtobufTestPayload.LengthDelimited(6, keyValueList));
        var arrayValue = Envelope("array_value", OtlpProtobufTestPayload.LengthDelimited(1, arrayItem));
        var outerAnyValue = Envelope("any_value", OtlpProtobufTestPayload.LengthDelimited(5, arrayValue));
        var outerKeyValue = Envelope("key_value",
            OtlpProtobufTestPayload.StringField(1, "outer.key"),
            OtlpProtobufTestPayload.LengthDelimited(2, outerAnyValue));
        var entityRef = Envelope("entity_ref",
            OtlpProtobufTestPayload.StringField(1, "https://entity.example/schema"),
            OtlpProtobufTestPayload.StringField(2, "service"),
            OtlpProtobufTestPayload.StringField(3, "service.id"),
            OtlpProtobufTestPayload.StringField(4, "service.description"));
        var resource = Envelope("resource",
            OtlpProtobufTestPayload.LengthDelimited(1, outerKeyValue),
            OtlpProtobufTestPayload.VarintField(2, 1),
            OtlpProtobufTestPayload.LengthDelimited(3, entityRef));
        var scope = Envelope("scope",
            OtlpProtobufTestPayload.StringField(1, "scope"),
            OtlpProtobufTestPayload.StringField(2, "1.0"));
        var spanEvent = Envelope("event", OtlpProtobufTestPayload.StringField(2, "event"));
        var link = Envelope("link", Fixed32Field(6, 513));
        var status = Envelope("status", OtlpProtobufTestPayload.VarintField(3, 1));
        var span = Envelope("span",
            OtlpProtobufTestPayload.StringField(5, "span"),
            OtlpProtobufTestPayload.LengthDelimited(11, spanEvent),
            OtlpProtobufTestPayload.LengthDelimited(13, link),
            OtlpProtobufTestPayload.LengthDelimited(15, status),
            Fixed32Field(16, 257));
        var scopeSpans = Envelope("scope_spans",
            OtlpProtobufTestPayload.LengthDelimited(1, scope),
            OtlpProtobufTestPayload.LengthDelimited(2, span),
            OtlpProtobufTestPayload.StringField(3, "https://scope.example/schema"));
        var resourceSpans = Envelope("resource_spans",
            OtlpProtobufTestPayload.LengthDelimited(1, resource),
            OtlpProtobufTestPayload.LengthDelimited(2, scopeSpans),
            OtlpProtobufTestPayload.StringField(3, "https://resource.example/schema"));
        return Envelope("request", OtlpProtobufTestPayload.LengthDelimited(1, resourceSpans));
    }

    private static byte[] BuildVarintFlagsProtobuf()
    {
        var link = OtlpProtobufTestPayload.Message(OtlpProtobufTestPayload.VarintField(6, 1));
        var span = OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(13, link),
            OtlpProtobufTestPayload.VarintField(16, 1));
        return WrapSpan(span);
    }

    private static byte[] WrapSpan(byte[] span) => OtlpProtobufTestPayload.Message(
        OtlpProtobufTestPayload.LengthDelimited(1, OtlpProtobufTestPayload.Message(
            OtlpProtobufTestPayload.LengthDelimited(2, OtlpProtobufTestPayload.Message(
                OtlpProtobufTestPayload.LengthDelimited(2, span))))));

    private static byte[] EnvelopeWithOptionalUnknown(bool includeUnknown, params byte[][] fields) => includeUnknown
        ? OtlpProtobufTestPayload.Message([.. fields, UnknownWireFields()])
        : OtlpProtobufTestPayload.Message(fields);

    private static byte[] UnknownWireFields() => OtlpProtobufTestPayload.Message(
        OtlpProtobufTestPayload.VarintField(99, 987654321),
        Fixed32Field(99, 0x12345678),
        OtlpProtobufTestPayload.Fixed64Field(99, 0x123456789ABCDEF0),
        OtlpProtobufTestPayload.StringField(99, "wire-marker-secret"));

    private static byte[] Fixed32Field(int fieldNumber, uint value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, ((ulong)fieldNumber << 3) | 5);
        Span<byte> bytes = stackalloc byte[4];
        BitConverter.TryWriteBytes(bytes, value);
        stream.Write(bytes);
        return stream.ToArray();
    }

    private static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static string Identity(SourceStructuralOccurrence item) => string.Join('|',
        item.Envelope,
        item.Role,
        item.Name.Value,
        item.StructuralType,
        item.Count.Value);
}

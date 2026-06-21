using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class OtlpProtobufTraceConverterTests
{
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

    public static IEnumerable<object[]> MalformedPayloads()
    {
        yield return [new byte[] { 0x0A, 0x05, 0x01 }];
        yield return [new byte[] { 0x09, 0x01 }];
        yield return [new byte[] { 0x08 }];
        yield return [new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }];
        yield return [new byte[] { 0x0F }];
    }
}

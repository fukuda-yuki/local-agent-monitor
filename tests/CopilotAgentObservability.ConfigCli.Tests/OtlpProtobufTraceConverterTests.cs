using System.Text;
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
        var resource = Message(
            LengthDelimited(1, KeyValue("client.kind", StringValue("vscode-copilot-chat"))),
            LengthDelimited(1, KeyValue("experiment.id", StringValue("baseline"))));
        var spanMessage = Message(
            LengthDelimited(1, traceId),
            LengthDelimited(2, spanId),
            StringField(5, "chat gpt-4o"),
            VarintField(6, 1),
            Fixed64Field(7, 1_000_000_000),
            Fixed64Field(8, 1_500_000_000),
            LengthDelimited(9, KeyValue("gen_ai.operation.name", StringValue("chat"))),
            LengthDelimited(9, KeyValue("gen_ai.usage.input_tokens", IntValue(10))),
            LengthDelimited(9, KeyValue("gen_ai.usage.output_tokens", IntValue(5))),
            LengthDelimited(9, KeyValue("synthetic.signed", IntValue(unchecked((ulong)-7L)))),
            LengthDelimited(11, Message(
                Fixed64Field(1, 1_100_000_000),
                StringField(2, "gen_ai.first_token"),
                LengthDelimited(3, KeyValue("event.kind", StringValue("first-token"))))),
            LengthDelimited(15, Message(
                StringField(2, "synthetic error"),
                VarintField(3, 2))));
        var scopeSpans = Message(LengthDelimited(2, spanMessage));
        var resourceSpans = Message(LengthDelimited(1, resource), LengthDelimited(2, scopeSpans));
        var request = Message(LengthDelimited(1, resourceSpans));

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

    private static byte[] KeyValue(string key, byte[] value)
    {
        return Message(StringField(1, key), LengthDelimited(2, value));
    }

    private static byte[] StringValue(string value)
    {
        return Message(StringField(1, value));
    }

    private static byte[] IntValue(ulong value)
    {
        return Message(VarintField(3, value));
    }

    private static byte[] Message(params byte[][] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            stream.Write(field);
        }

        return stream.ToArray();
    }

    private static byte[] StringField(int fieldNumber, string value)
    {
        return LengthDelimited(fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    private static byte[] LengthDelimited(int fieldNumber, byte[] value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, ((ulong)fieldNumber << 3) | 2);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
        return stream.ToArray();
    }

    private static byte[] VarintField(int fieldNumber, ulong value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, (ulong)fieldNumber << 3);
        WriteVarint(stream, value);
        return stream.ToArray();
    }

    private static byte[] Fixed64Field(int fieldNumber, ulong value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, ((ulong)fieldNumber << 3) | 1);
        Span<byte> bytes = stackalloc byte[8];
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
}

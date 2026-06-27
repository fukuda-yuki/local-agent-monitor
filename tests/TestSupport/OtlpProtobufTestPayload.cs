using System.Text;

namespace CopilotAgentObservability.TestSupport;

internal static class OtlpProtobufTestPayload
{
    public static byte[] VscodeCopilotChatTraceRequest()
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
            Fixed64Field(7, 1_000_000_000),
            Fixed64Field(8, 1_500_000_000),
            LengthDelimited(9, KeyValue("gen_ai.usage.input_tokens", IntValue(10))),
            LengthDelimited(9, KeyValue("gen_ai.usage.output_tokens", IntValue(5))));
        var scopeSpans = Message(LengthDelimited(2, spanMessage));
        var resourceSpans = Message(
            LengthDelimited(1, resource),
            LengthDelimited(2, scopeSpans));
        return Message(LengthDelimited(1, resourceSpans));
    }

    public static byte[] KeyValue(string key, byte[] value)
    {
        return Message(StringField(1, key), LengthDelimited(2, value));
    }

    public static byte[] StringValue(string value)
    {
        return Message(StringField(1, value));
    }

    public static byte[] IntValue(ulong value)
    {
        return Message(VarintField(3, value));
    }

    public static byte[] Message(params byte[][] fields)
    {
        using var stream = new MemoryStream();
        foreach (var field in fields)
        {
            stream.Write(field);
        }

        return stream.ToArray();
    }

    public static byte[] StringField(int fieldNumber, string value)
    {
        return LengthDelimited(fieldNumber, Encoding.UTF8.GetBytes(value));
    }

    public static byte[] LengthDelimited(int fieldNumber, byte[] value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, ((ulong)fieldNumber << 3) | 2);
        WriteVarint(stream, (ulong)value.Length);
        stream.Write(value);
        return stream.ToArray();
    }

    public static byte[] VarintField(int fieldNumber, ulong value)
    {
        using var stream = new MemoryStream();
        WriteVarint(stream, (ulong)fieldNumber << 3);
        WriteVarint(stream, value);
        return stream.ToArray();
    }

    public static byte[] Fixed64Field(int fieldNumber, ulong value)
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

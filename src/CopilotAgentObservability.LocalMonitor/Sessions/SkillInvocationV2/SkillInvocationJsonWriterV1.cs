using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal static class SkillInvocationJsonWriterV1
{
    internal static readonly JsonWriterOptions Options = new()
    {
        Indented = false,
        SkipValidation = false,
        Encoder = JavaScriptEncoder.Default
    };

    internal static byte[] WriteErrorEntity(string token)
    {
        return Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("error", token);
            writer.WriteEndObject();
        });
    }

    internal static byte[] Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, Options))
        {
            write(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static void WriteUnsignedNumber(Utf8JsonWriter writer, string propertyName, ulong value)
    {
        writer.WriteNumber(propertyName, value);
    }

    // Utf8JsonWriter and Encoding.UTF8 both replace an unpaired surrogate with U+FFFD instead of
    // rejecting it, so producer strings must be scanned before they ever reach the writer.
    internal static bool ContainsUnpairedSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (char.IsHighSurrogate(current))
            {
                var hasFollowingLowSurrogate = index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]);
                if (!hasFollowingLowSurrogate)
                {
                    return true;
                }

                index++;
            }
            else if (char.IsLowSurrogate(current))
            {
                return true;
            }
        }

        return false;
    }
}

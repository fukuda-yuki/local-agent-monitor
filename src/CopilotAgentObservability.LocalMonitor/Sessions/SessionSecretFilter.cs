using System.Text.RegularExpressions;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal static class SessionSecretFilter
{
    private static readonly string[] SecretFragments =
    [
        "authorization", "credential", "password", "passwd", "secret", "token", "api_key", "apikey", "access_key", "private_key",
    ];

    public static string Filter(JsonElement payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteFiltered(writer, payload);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFiltered(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    if (SecretFragments.Any(fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }
                    writer.WritePropertyName(property.Name);
                    WriteFiltered(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteFiltered(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(SanitizeString(value.GetString()!));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static string SanitizeString(string value)
    {
        var sanitized = Regex.Replace(value, @"(?i)Bearer\s+[^\s,;]+", "[REDACTED]", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"github_pat_[A-Za-z0-9_]+", "[REDACTED]", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"gh[pousr]_[A-Za-z0-9]+", "[REDACTED]", RegexOptions.CultureInvariant);
        sanitized = Regex.Replace(sanitized, @"sk-[A-Za-z0-9_-]+", "[REDACTED]", RegexOptions.CultureInvariant);
        return Regex.Replace(
            sanitized,
            """(?i)(authorization|credential|password|passwd|secret|token|api[_-]?key|access[_-]?key|private[_-]?key)\s*[:=]\s*(?:"[^"]*"|'[^']*'|[^\s,;]+)""",
            "$1=[REDACTED]",
            RegexOptions.CultureInvariant);
    }
}

using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Telemetry.Sessions;

internal static class SessionSecretFilter
{
    private const int MaximumTotalTokens = 2_147_483_647;
    private static readonly string[] SecretFragments =
    [
        "authorization", "credential", "password", "passwd", "secret", "token", "api_key", "apikey", "access_key", "private_key",
    ];

    public static string Filter(string eventType, JsonElement payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteFiltered(
                writer,
                payload,
                string.Equals(eventType, "subagent.completed", StringComparison.Ordinal));
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static bool IsSensitiveCarrier(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (CopilotAgentObservability.Telemetry.RepositoryMetadataDiagnostics.IsTokenLike(value)) return true;
        if (!string.Equals(SanitizeString(value), value, StringComparison.Ordinal)) return true;
        var normalized = string.Concat(value.Where(char.IsAsciiLetterOrDigit));
        return SecretFragments.Any(fragment =>
            string.Equals(normalized, string.Concat(fragment.Where(char.IsAsciiLetterOrDigit)), StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteFiltered(
        Utf8JsonWriter writer,
        JsonElement value,
        bool allowTotalTokens = false)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var exactTotalTokensCount = allowTotalTokens
                    ? value.EnumerateObject().Count(property => property.NameEquals("totalTokens"))
                    : 0;
                foreach (var property in value.EnumerateObject())
                {
                    if (SecretFragments.Any(fragment => property.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (allowTotalTokens
                            && exactTotalTokensCount == 1
                            && property.NameEquals("totalTokens")
                            && TryReadTotalTokens(property.Value, out var totalTokens))
                        {
                            writer.WriteNumber("totalTokens", totalTokens);
                        }
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

    private static bool TryReadTotalTokens(JsonElement value, out int totalTokens)
    {
        totalTokens = 0;
        if (value.ValueKind != JsonValueKind.Number)
        {
            return false;
        }

        var raw = value.GetRawText();
        if (raw.Length == 0 || (raw[0] == '0' ? raw.Length != 1 : raw[0] is < '1' or > '9'))
        {
            return false;
        }
        for (var index = 1; index < raw.Length; index++)
        {
            if (raw[index] is < '0' or > '9')
            {
                return false;
            }
        }
        return value.TryGetInt32(out totalTokens)
            && totalTokens >= 0
            && totalTokens <= MaximumTotalTokens;
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

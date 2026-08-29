using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor;

internal sealed record LocalMonitorV1ComparisonRowsQuery(string Family, string? Search, string? After, int Limit);
internal sealed record LocalMonitorV1ComparisonEvidenceQuery(int ResultOrdinal, string? FieldKey, string? After, int Limit);
internal sealed class LocalMonitorV1ComparisonQueryException : Exception;
internal sealed class LocalMonitorV1ComparisonCursorException : Exception;

internal static class LocalMonitorV1ComparisonQueryParser
{
    private static readonly HashSet<string> EvidenceFields = new(StringComparer.Ordinal)
    {
        "value", "available_count", "median", "minimum", "maximum", "total",
        "absolute_difference", "relative_difference_percent", "condition", "count",
        "duration_ms", "input_tokens", "output_tokens", "total_tokens", "cache_read",
        "cache_creation", "new_input", "error_count", "retry_count",
    };

    internal static LocalMonitorV1ComparisonRowsQuery ParseRows(string query)
    {
        var values = Parse(query, ["family", "q", "after", "limit"]);
        if (!values.TryGetValue("family", out var family) || family is not ("skill" or "tool" or "subagent")) Reject();
        string? search = null;
        if (values.TryGetValue("q", out var q))
        {
            search = Normalize(q);
            if (search.Length == 0 || Encoding.UTF8.GetByteCount(search) > 800 || search.EnumerateRunes().Count() > 200) Reject();
        }
        var after = OptionalCursor(values);
        return new(family!, search, after, Limit(values, 50, 100));
    }

    internal static LocalMonitorV1ComparisonEvidenceQuery ParseEvidence(string query)
    {
        var values = Parse(query, ["result_ordinal", "field_key", "after", "limit"]);
        var ordinal = 0;
        if (!values.TryGetValue("result_ordinal", out var ordinalText)
            || !int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out ordinal)
            || ordinal <= 0) Reject();
        values.TryGetValue("field_key", out var field);
        if (field is not null && !EvidenceFields.Contains(field)) Reject();
        return new(ordinal, field, OptionalCursor(values), Limit(values, 100, 200));
    }

    private static Dictionary<string, string> Parse(string query, string[] order)
    {
        if (string.IsNullOrEmpty(query) || query[0] != '?') Reject();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var last = -1;
        foreach (var pair in query[1..].Split('&', StringSplitOptions.None))
        {
            var separator = pair.IndexOf('=');
            if (separator <= 0) Reject();
            var key = Decode(pair[..separator]);
            var index = Array.IndexOf(order, key);
            if (index <= last || !result.TryAdd(key, Decode(pair[(separator + 1)..]))) Reject();
            last = index;
        }
        return result;
    }

    private static string Decode(string value)
    {
        try
        {
            var decoded = Uri.UnescapeDataString(value.Replace('+', ' '));
            _ = new UTF8Encoding(false, true).GetBytes(decoded);
            return decoded;
        }
        catch (Exception exception) when (exception is UriFormatException or EncoderFallbackException) { Reject(); return ""; }
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string? OptionalCursor(Dictionary<string, string> values)
    {
        if (!values.TryGetValue("after", out var after)) return null;
        if (after.Length is < 1 or > 2048 || after.Any(static c => c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.'))) Reject();
        return after;
    }

    private static int Limit(Dictionary<string, string> values, int defaultValue, int maximum)
    {
        if (!values.TryGetValue("limit", out var text)) return defaultValue;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value is < 1 || value > maximum) Reject();
        return value;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject() => throw new LocalMonitorV1ComparisonQueryException();
}

internal sealed class LocalMonitorV1ComparisonCursorCodec
{
    private const string Domain = "copilot-agent-observability/local-monitor-comparison-cursor/v1";
    private readonly byte[] key;

    internal LocalMonitorV1ComparisonCursorCodec(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32) throw new ArgumentException("comparison_cursor_key_too_short", nameof(key));
        this.key = key.ToArray();
    }

    internal string Encode(string repositoryId, string comparisonId, string operation, string queryBinding, int lastOrdinal)
    {
        if (lastOrdinal <= 0) throw new ArgumentOutOfRangeException(nameof(lastOrdinal));
        var bindingDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(queryBinding)));
        var payload = Encoding.UTF8.GetBytes(string.Join('\0', Domain, repositoryId, comparisonId, operation, bindingDigest, lastOrdinal.ToString(CultureInfo.InvariantCulture)));
        var mac = HMACSHA256.HashData(key, payload);
        return Base64(payload) + "." + Base64(mac);
    }

    internal int Decode(string cursor, string repositoryId, string comparisonId, string operation, string queryBinding)
    {
        try
        {
            var parts = cursor.Split('.');
            if (parts.Length != 2) Reject();
            var payload = FromBase64(parts[0]);
            var supplied = FromBase64(parts[1]);
            var expected = HMACSHA256.HashData(key, payload);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected)) Reject();
            var fields = new UTF8Encoding(false, true).GetString(payload).Split('\0');
            var ordinal = 0;
            var bindingDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(queryBinding)));
            if (fields.Length != 6 || fields[0] != Domain || fields[1] != repositoryId || fields[2] != comparisonId || fields[3] != operation || fields[4] != bindingDigest
                || !int.TryParse(fields[5], NumberStyles.None, CultureInfo.InvariantCulture, out ordinal) || ordinal <= 0) Reject();
            return ordinal;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException) { Reject(); return 0; }
    }

    private static string Base64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64(string value)
    {
        var text = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(text.PadRight(text.Length + ((4 - text.Length % 4) % 4), '='));
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Reject() => throw new LocalMonitorV1ComparisonCursorException();
}

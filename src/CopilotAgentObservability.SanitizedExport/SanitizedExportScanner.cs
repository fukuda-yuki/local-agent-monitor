using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace CopilotAgentObservability.SanitizedExport;

internal static partial class SanitizedExportScanner
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly HashSet<string> ForbiddenFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "raw_otlp", "raw_payload", "payload_json", "content_json", "prompt", "user_prompt", "response",
        "assistant_response", "system_prompt", "system_message", "tool_arguments", "tool_argument", "tool_input",
        "tool_results", "tool_result", "tool_output", "source_code", "source_body", "file_body", "file_content",
        "authorization", "password", "secret", "token", "api_key", "email", "user_email", "user_id", "user_name",
        "username", "phone", "address", "absolute_path", "local_path", "analysis_markdown", "raw_analysis",
    };

    internal static string? Scan(SanitizedExportRecord record, IReadOnlyList<string> forbiddenMarkers)
    {
        if (!SanitizedExportEntryPolicy.IsAllowed(record)) return "unexpected_entry";
        if (record.CanonicalBytes.Length == 0) return "invalid_canonical_content";

        string text;
        try { text = StrictUtf8.GetString(record.CanonicalBytes); }
        catch (DecoderFallbackException) { return "invalid_canonical_content"; }
        if (record.EntryPath.EndsWith(".json", StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(record.CanonicalBytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
                if (ContainsForbiddenField(document.RootElement)) return "forbidden_field";
            }
            catch (JsonException)
            {
                return "invalid_canonical_content";
            }
        }

        if (record.EntryPath.EndsWith(".csv", StringComparison.Ordinal) && ContainsForbiddenCsvHeader(text)
            || record.EntryPath.EndsWith(".html", StringComparison.Ordinal) && RawFieldPattern().IsMatch(text)) return "forbidden_field";
        if (CredentialPattern().IsMatch(text)) return "credential_pattern";
        if (LocalPathPattern().IsMatch(text)) return "local_path";
        if (EmailPattern().IsMatch(text)) return "pii_pattern";
        if (forbiddenMarkers.Any(marker => MarkerVariants(marker).Any(variant => text.Contains(variant, StringComparison.Ordinal))))
            return "forbidden_marker";

        return null;
    }

    private static bool ContainsForbiddenCsvHeader(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? text : text[..end];
        return header.Split(',').Select(value => value.Trim().Trim('"')).Any(ForbiddenFields.Contains);
    }

    internal static string? ScanMetadata(IEnumerable<string?> values, IReadOnlyList<string> forbiddenMarkers)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { values = values.Where(value => value is not null).ToArray() });
        return Scan(new SanitizedExportRecord(
            "sessions/metadata.json", "session_projection", "metadata", null, null, null, null, null, null,
            DateTimeOffset.UnixEpoch, bytes, []), forbiddenMarkers);
    }

    private static IEnumerable<string> MarkerVariants(string marker)
    {
        if (string.IsNullOrEmpty(marker)) yield break;
        yield return marker;
        yield return JsonSerializer.Serialize(marker)[1..^1];
        yield return System.Net.WebUtility.HtmlEncode(marker);
        yield return Uri.EscapeDataString(marker);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(marker));
        yield return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant()[..12];
    }

    private static bool ContainsForbiddenField(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (ForbiddenFields.Contains(property.Name) || ContainsForbiddenField(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (ContainsForbiddenField(item)) return true;
            }
        }
        return false;
    }

    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(bearer|basic)|-----BEGIN (?:(?:RSA |EC |OPENSSH )?PRIVATE KEY|CERTIFICATE)-----|(?:api[_-]?key|password|secret|token)\\s*[:=]\\s*[^\\s,;}{]{4,}|(?:gh[pousr]|github_pat)_[a-z0-9_]{20,}|AKIA[0-9A-Z]{16})", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPattern();

    [GeneratedRegex("(?i)(?:[a-z]:[\\\\/][^\\s\\\"']+|\\\\\\\\(?:\\?\\\\)?[^\\s\\\\]+\\\\|file:(?:/{2,3})?|/mnt/[a-z]/[^\\s\\\"']+|/(?:users|home|etc|var)/(?:[^\\s\\\"']+))", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPathPattern();

    [GeneratedRegex("(?i)(?<![a-z0-9.!#$%&'*+/=?^_`{|}~-])[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+(?![a-z0-9-])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex("(?i)(?:data-)?(raw_otlp|raw_payload|payload_json|content_json|prompt|user_prompt|response|assistant_response|system_prompt|system_message|tool_arguments|tool_argument|tool_input|tool_results|tool_result|tool_output|source_code|source_body|file_body|file_content|authorization|password|secret|token|api_key|email|user_email|user_id|user_name|username|phone|address|absolute_path|local_path|analysis_markdown|raw_analysis)\\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex RawFieldPattern();
}

internal static class SanitizedExportEntryPolicy
{
    private static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["session_projection"] = "sessions/",
        ["measurement_dataset"] = "measurements/",
        ["instruction_finding_handoff"] = "receipts/",
        ["alert_receipt"] = "receipts/",
        ["candidate_receipt"] = "receipts/",
        ["driver_receipt"] = "receipts/",
        ["dashboard_dataset"] = "dashboard/",
    };

    internal static bool IsAllowed(SanitizedExportRecord record)
    {
        if (!Prefixes.TryGetValue(record.RecordType, out var prefix)
            || !record.EntryPath.StartsWith(prefix, StringComparison.Ordinal)
            || record.EntryPath.StartsWith("/", StringComparison.Ordinal)
            || record.EntryPath.Contains('\\')
            || record.EntryPath.Split('/').Any(segment => segment is "" or "." or "..")
            || !(record.EntryPath.EndsWith(".json", StringComparison.Ordinal)
                || record.EntryPath.EndsWith(".csv", StringComparison.Ordinal)
                || record.EntryPath.EndsWith(".html", StringComparison.Ordinal)))
            return false;
        return true;
    }
}

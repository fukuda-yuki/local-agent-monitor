using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

    internal static string? Scan(SanitizedExportRecord record)
    {
        if (!SanitizedExportEntryPolicy.IsAllowed(record)) return "unexpected_entry";
        return ScanBytes(record.CanonicalBytes);
    }

    private static string? ScanBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return "invalid_canonical_content";

        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return "invalid_canonical_content"; }
        if (text.AsSpan().TrimStart().StartsWith("{".AsSpan(), StringComparison.Ordinal)
            || text.AsSpan().TrimStart().StartsWith("[".AsSpan(), StringComparison.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
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

        if (ContainsForbiddenCsvHeader(text) || RawFieldPattern().IsMatch(text)) return "forbidden_field";
        if (CredentialPattern().IsMatch(text)) return "credential_pattern";
        if (LocalPathPattern().IsMatch(text)) return "local_path";
        if (EmailPattern().IsMatch(text) || SensitiveIdentityPattern().IsMatch(text)) return "pii_pattern";
        return null;
    }

    private static bool ContainsForbiddenCsvHeader(string text)
    {
        var end = text.IndexOfAny(['\r', '\n']);
        var header = end < 0 ? text : text[..end];
        return header.Split(',').Select(value => value.Trim().Trim('"')).Any(ForbiddenFields.Contains);
    }

    internal static string? ScanMetadata(IEnumerable<string?> values)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { values = values.Where(value => value is not null).ToArray() });
        return ScanBytes(bytes);
    }

    internal static string? ScanCanonicalJson(byte[] bytes) => ScanBytes(bytes);

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

    [GeneratedRegex("(?i)(?:[a-z]:[\\\\/][^\\s\\\"']+|(?:\\\\\\\\|//)(?:\\?[\\\\/])?[^\\s\\\\/]+[\\\\/]|file:(?:/{2,3})?|(?<![a-z0-9._~:/-])/[a-z0-9._-]+(?:/[^\\s\\\"']+)*)", RegexOptions.CultureInvariant)]
    private static partial Regex LocalPathPattern();

    [GeneratedRegex("(?i)(?<![a-z0-9.!#$%&'*+/=?^_`{|}~-])[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+(?![a-z0-9-])", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    [GeneratedRegex("(?ix)(?:\\b\\d{3}-\\d{2}-\\d{4}\\b|(?<![\\d])(?:\\+?1[ .-]?)?(?:\\(\\d{3}\\)|\\d{3})[ .-]\\d{3}[ .-]\\d{4}(?![\\d])|\\b\\d{1,6}[ ]+[a-z0-9.'-]+(?:[ ]+[a-z0-9.'-]+){0,4}[ ]+(?:street|st|road|rd|avenue|ave|boulevard|blvd|lane|ln|drive|dr|way)\\b)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveIdentityPattern();

    [GeneratedRegex("(?i)(?:data-)?(raw_otlp|raw_payload|payload_json|content_json|prompt|user_prompt|response|assistant_response|system_prompt|system_message|tool_arguments|tool_argument|tool_input|tool_results|tool_result|tool_output|source_code|source_body|file_body|file_content|authorization|password|secret|token|api_key|email|user_email|user_id|user_name|username|phone|address|absolute_path|local_path|analysis_markdown|raw_analysis)\\s*=", RegexOptions.CultureInvariant)]
    private static partial Regex RawFieldPattern();
}

internal static class SanitizedExportEntryPolicy
{
    private static readonly IReadOnlyDictionary<string, string> Prefixes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["repository_metadata_projection"] = "repository-metadata/",
        ["instruction_finding_handoff"] = "instruction-findings/",
        ["alert_receipt"] = "alert-receipts/",
    };

    internal static bool IsAllowed(SanitizedExportRecord record)
    {
        if (!Prefixes.TryGetValue(record.RecordType, out var prefix)
            || !record.EntryPath.StartsWith(prefix, StringComparison.Ordinal)
            || record.EntryPath.StartsWith("/", StringComparison.Ordinal)
            || record.EntryPath.Contains('\\')
            || record.EntryPath.Split('/').Any(segment => segment is "" or "." or "..")
            || !record.EntryPath.EndsWith($"/{record.RecordId}.json", StringComparison.Ordinal)
            || record.RecordId.Length is 0 or > SanitizedExportLimits.MaximumIdentifierLength)
            return false;
        return true;
    }
}

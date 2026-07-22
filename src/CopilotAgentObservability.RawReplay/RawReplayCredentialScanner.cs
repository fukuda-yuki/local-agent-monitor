using System.Text.RegularExpressions;

namespace CopilotAgentObservability.RawReplay;

internal static partial class RawReplayCredentialScanner
{
    [GeneratedRegex("(?i)(authorization\\s*[:=]\\s*(?:bearer|basic)|(?:bearer|basic)\\s+[a-z0-9._~+/-]{8,}|-----BEGIN [A-Z0-9 ]*(?:PRIVATE KEY|CERTIFICATE)[A-Z0-9 ]*-----|(?:api[_-]?key|access[_-]?token|client[_-]?secret|password)['\\\"]?\\s*[:=]\\s*['\\\"]?[^\\s,;}{\\\"]{8,}|(?:gh[pousr]|github_pat)_[a-z0-9_]{20,}|glpat-[a-z0-9_-]{20,}|AKIA[0-9A-Z]{16}|sk-(?:ant-)?[a-z0-9_-]{20,})", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex Pattern();

    internal static bool ContainsKnownCredential(RawReplayRecord record) =>
        Match(record.Source) || Match(record.TraceId) || Match(record.PayloadJson) || Match(record.ResourceAttributesJson)
        || Match(record.Provenance.SourceSurface) || Match(record.Provenance.SourceApplicationVersion)
        || Match(record.Provenance.SourceAdapter) || Match(record.Provenance.AdapterVersion)
        || Match(record.Provenance.SchemaFingerprint) || Match(record.Provenance.InventoryHash)
        || Match(record.Provenance.CompatibilityState) || Match(record.Provenance.CaptureContentState)
        || Match(record.Provenance.SecretFilterState) || Match(record.Provenance.SecretFilterVersion);

    internal static bool ContainsKnownCredential(RawReplaySessionContent content) => Match(content.EventId)
        || Match(content.SessionId) || Match(content.RunId) || Match(content.TraceId) || Match(content.ContentJson)
        || Match(content.SourceAdapter) || Match(content.SourceEventId) || Match(content.SourceApplicationVersion)
        || Match(content.AdapterVersion) || Match(content.SchemaFingerprint) || Match(content.NormalizationVersion)
        || Match(content.MatchKind) || Match(content.ContentKind) || Match(content.ContentState)
        || Match(content.SecretFilterState) || Match(content.SecretFilterVersion);

    internal static bool ContainsKnownCredential(string? value) => Match(value);

    private static bool Match(string? value)
    {
        if (value is null) return false;
        try { return Pattern().IsMatch(value); }
        catch (RegexMatchTimeoutException) { return true; }
    }
}

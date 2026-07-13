using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed record SessionIngestEnvelope(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("source_adapter")] string? SourceAdapter,
    [property: JsonPropertyName("source_surface")] string? SourceSurface,
    [property: JsonPropertyName("native_session_id")] string? NativeSessionId,
    [property: JsonPropertyName("events")] IReadOnlyList<SessionIngestEvent>? Events,
    [property: JsonPropertyName("explicit_link")] SessionExplicitLink? ExplicitLink = null,
    [property: JsonPropertyName("source_application_version")] string? SourceApplicationVersion = null,
    [property: JsonPropertyName("adapter_version")] string? AdapterVersion = null,
    [property: JsonPropertyName("schema_fingerprint")] string? SchemaFingerprint = null,
    [property: JsonPropertyName("normalization_version")] string? NormalizationVersion = null);

internal sealed record SessionExplicitLink(
    [property: JsonPropertyName("source_surface")] string? SourceSurface,
    [property: JsonPropertyName("native_session_id")] string? NativeSessionId,
    [property: JsonPropertyName("kind")] string? Kind);

internal sealed record SessionIngestEvent(
    [property: JsonPropertyName("source_event_id")] string? SourceEventId,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("occurred_at")] string? OccurredAtValue,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("parent_event_id")] string? ParentEventId = null,
    [property: JsonPropertyName("run_native_id")] string? RunNativeId = null,
    [property: JsonPropertyName("trace_id")] string? TraceId = null)
{
    [JsonIgnore]
    public DateTimeOffset OccurredAt => DateTimeOffset.Parse(OccurredAtValue!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

internal static class SessionIngestValidation
{
    private const string MetadataTokenPattern = "^[A-Za-z0-9][A-Za-z0-9._+-]{0,255}$";

    public static bool IsValid(SessionIngestEnvelope? envelope)
    {
        if (envelope is null
            || envelope.SchemaVersion != 1
            || envelope.SourceAdapter is not ("copilot-sdk-stream" or "copilot-compatible-hook" or "claude-code-hook")
            || envelope.SourceSurface is not ("copilot-sdk" or "copilot-cli" or "vscode" or "hook-unknown" or "claude-code")
            || !Bounded(envelope.NativeSessionId, 256)
            || !OptionalMetadataToken(envelope.SourceApplicationVersion)
            || !OptionalMetadataToken(envelope.AdapterVersion)
            || !OptionalSha256(envelope.SchemaFingerprint)
            || !OptionalMetadataToken(envelope.NormalizationVersion)
            || envelope.Events is null
            || envelope.Events.Count is < 1 or > 100)
        {
            return false;
        }

        var sourceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in envelope.Events)
        {
            if (!Bounded(item.SourceEventId, 256)
                || item.Type is null || !Regex.IsMatch(item.Type, "^[A-Za-z][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)
                || !HasExplicitOffset(item.OccurredAtValue)
                || item.Payload.ValueKind != JsonValueKind.Object
                || !OptionalBounded(item.ParentEventId, 256)
                || !OptionalBounded(item.RunNativeId, 256)
                || !OptionalBounded(item.TraceId, 128)
                || !sourceIds.Add(item.SourceEventId!))
            {
                return false;
            }
        }

        if (!IsValidAdapterSurface(envelope.SourceAdapter, envelope.SourceSurface)
            || envelope.SourceAdapter == "claude-code-hook" && !HasCompleteClaudeProvenance(envelope)) return false;
        if (envelope.ExplicitLink is { } link
            && (link.SourceSurface is not ("copilot-sdk" or "copilot-cli" or "vscode" or "hook-unknown" or "claude-code")
                || !Bounded(link.NativeSessionId, 256)
                || link.Kind is not ("resume" or "handoff"))) return false;
        return true;
    }

    private static bool IsValidAdapterSurface(string sourceAdapter, string sourceSurface) => sourceAdapter switch
    {
        "copilot-sdk-stream" => sourceSurface == "copilot-sdk",
        "copilot-compatible-hook" => sourceSurface is "copilot-cli" or "vscode" or "hook-unknown",
        "claude-code-hook" => sourceSurface == "claude-code",
        _ => false,
    };

    private static bool HasCompleteClaudeProvenance(SessionIngestEnvelope envelope) =>
        MetadataToken(envelope.AdapterVersion)
        && MetadataToken(envelope.NormalizationVersion)
        && (MetadataToken(envelope.SourceApplicationVersion) || envelope.SchemaFingerprint is not null);

    private static bool Bounded(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool OptionalBounded(string? value, int maximum) => value is null || Bounded(value, maximum);
    private static bool OptionalMetadataToken(string? value) => value is null || MetadataToken(value);
    private static bool MetadataToken(string? value)
    {
        if (value is null) return false;
        var match = Regex.Match(value, MetadataTokenPattern, RegexOptions.CultureInvariant);
        return match.Success && match.Length == value.Length;
    }
    private static bool OptionalSha256(string? value) =>
        value is null || (value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    private static bool HasExplicitOffset(string? value)
    {
        if (value is null)
        {
            return false;
        }

        var match = Regex.Match(
            value,
            "^(?<y>[0-9]{4})-(?<m>[0-9]{2})-(?<d>[0-9]{2})T(?<h>[0-9]{2}):(?<n>[0-9]{2}):(?<s>[0-9]{2})(?:\\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})$",
            RegexOptions.CultureInvariant);
        if (!match.Success
            || !int.TryParse(match.Groups["y"].Value, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(match.Groups["m"].Value, CultureInfo.InvariantCulture, out var month)
            || !int.TryParse(match.Groups["d"].Value, CultureInfo.InvariantCulture, out var day)
            || !int.TryParse(match.Groups["h"].Value, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(match.Groups["n"].Value, CultureInfo.InvariantCulture, out var minute)
            || !int.TryParse(match.Groups["s"].Value, CultureInfo.InvariantCulture, out var second)
            || year is < 1 or > 9999
            || month is < 1 or > 12
            || day < 1 || day > DateTime.DaysInMonth(year, month)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
    }
}

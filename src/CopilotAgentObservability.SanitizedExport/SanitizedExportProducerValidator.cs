using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.InstructionFindings;

namespace CopilotAgentObservability.SanitizedExport;

internal static class SanitizedExportProducerValidator
{
    internal static string? Validate(SanitizedExportRecord record, bool requireEnvelope = true)
    {
        if (!SanitizedExportEntryPolicy.IsAllowed(record))
            return AllowedType(record.RecordType) ? "unexpected_entry" : "unsupported_record_type";
        if (record.CanonicalBytes.Length is 0 or > SanitizedExportLimits.MaximumRecordBytes)
            return "producer_contract_invalid";
        try
        {
            return record.RecordType switch
            {
                "repository_metadata_projection" => ValidateRepository(record, requireEnvelope),
                "instruction_finding_handoff" => ValidateInstruction(record, requireEnvelope),
                "alert_receipt" => ValidateAlert(record, requireEnvelope),
                _ => "unsupported_record_type",
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException
            or OverflowException or ArgumentException or AlertReceiptConsumerException
            or InstructionFindingHandoffConsumerValidationException)
        {
            return "producer_contract_invalid";
        }
    }

    private static string? ValidateRepository(SanitizedExportRecord record, bool requireEnvelope)
    {
        using var document = JsonDocument.Parse(record.CanonicalBytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        string[] names = ["schema_version", "record_id", "session_id", "trace_id", "source_surface", "repository_name", "workspace_label", "repo_snapshot", "observed_at", "completeness", "content_state", "retention_state"];
        var observedAt = ParseTimestamp(root.GetProperty("observed_at"));
        if (!Exact(root, names) || !IsMinifiedJson(record.CanonicalBytes)
            || Text(root, "schema_version") != "repository-metadata-projection.v1" || !Safe(Text(root, "record_id"))
            || !NullableSafe(root, "session_id") || !NullableSafe(root, "trace_id") || !NullableSafe(root, "source_surface")
            || !NullableSafe(root, "repository_name") || !NullableSafe(root, "workspace_label") || !NullableSafe(root, "repo_snapshot")
            || observedAt is null || !Safe(Text(root, "completeness"))
            || !Safe(Text(root, "content_state")) || !Safe(Text(root, "retention_state"))) return "producer_contract_invalid";
        var canonical = RepositoryMetadataProjectionV1.Serialize(Text(root, "record_id"), NullableText(root, "session_id"),
            NullableText(root, "trace_id"), NullableText(root, "source_surface"), NullableText(root, "repository_name"),
            NullableText(root, "workspace_label"), NullableText(root, "repo_snapshot"), observedAt.Value,
            Text(root, "completeness"), Text(root, "content_state"), Text(root, "retention_state"));
        if (!record.CanonicalBytes.AsSpan().SequenceEqual(canonical)) return "producer_contract_invalid";
        if (!requireEnvelope) return Text(root, "record_id") == record.RecordId ? null : "producer_envelope_mismatch";
        return Text(root, "record_id") == record.RecordId && NullableText(root, "session_id") == record.SessionId
            && NullableText(root, "trace_id") == record.TraceId && NullableText(root, "source_surface") == record.SourceSurface
            && NullableText(root, "repository_name") == record.RepositoryName && NullableText(root, "workspace_label") == record.WorkspaceLabel
            && NullableText(root, "repo_snapshot") == record.RepoSnapshot && observedAt == record.ObservedAt
            && Text(root, "completeness") == record.Completeness && Text(root, "content_state") == record.ContentState
            && Text(root, "retention_state") == record.RetentionState ? null : "producer_envelope_mismatch";
    }

    private static string? ValidateInstruction(SanitizedExportRecord record, bool requireEnvelope)
    {
        var analysisRunId = InstructionFindingHandoffConsumerV1.Validate(record.CanonicalBytes);
        if (record.RecordId != analysisRunId.ToString(CultureInfo.InvariantCulture)) return "producer_envelope_mismatch";
        return !requireEnvelope || record.SessionId is null && record.TraceId is null && record.SourceSurface is null
            && record.RepositoryName is null && record.WorkspaceLabel is null && record.RepoSnapshot is null
            ? null : "producer_envelope_mismatch";
    }

    private static string? ValidateAlert(SanitizedExportRecord record, bool requireEnvelope)
    {
        var receipt = AlertReceiptConsumerV1.Validate(record.CanonicalBytes);
        if (record.RecordId != receipt.AlertId) return "producer_envelope_mismatch";
        return !requireEnvelope || record.SessionId == receipt.SessionId && record.TraceId == receipt.TraceId
            && record.SourceSurface == receipt.SourceSurface && record.ObservedAt == receipt.LastObservedAt
            && record.RepositoryName is null && record.WorkspaceLabel is null && record.RepoSnapshot is null
            ? null : "producer_envelope_mismatch";
    }

    internal static bool AllowedType(string value) => value is "repository_metadata_projection" or "instruction_finding_handoff" or "alert_receipt";
    private static bool Exact(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(property => property.Name).SequenceEqual(names, StringComparer.Ordinal);
    private static string Text(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.String ? value.GetProperty(name).GetString()! : string.Empty;
    private static string? NullableText(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name);
    private static bool NullableSafe(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null || Safe(Text(value, name));
    private static bool Safe(string? value) => value is { Length: > 0 and <= SanitizedExportLimits.MaximumIdentifierLength } && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#');
    private static DateTimeOffset? ParseTimestamp(JsonElement value) => value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParseExact(value.GetString(), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : null;
    private static bool IsMinifiedJson(ReadOnlySpan<byte> bytes)
    {
        var quoted = false;
        var escaped = false;
        foreach (var value in bytes)
        {
            if (quoted)
            {
                if (escaped) escaped = false;
                else if (value == (byte)'\\') escaped = true;
                else if (value == (byte)'\"') quoted = false;
            }
            else if (value == (byte)'\"') quoted = true;
            else if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') return false;
        }
        return !quoted && !escaped;
    }
}

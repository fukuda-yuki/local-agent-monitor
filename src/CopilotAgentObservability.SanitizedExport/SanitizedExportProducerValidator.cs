using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.SanitizedExport;

internal static partial class SanitizedExportProducerValidator
{
    private static readonly JsonSerializerOptions AlertReceiptOptions = CreateAlertReceiptOptions();

    internal static string? Validate(SanitizedExportRecord record, bool requireEnvelope = true)
    {
        if (!SanitizedExportEntryPolicy.IsAllowed(record))
            return AllowedType(record.RecordType) ? "unexpected_entry" : "unsupported_record_type";
        if (record.CanonicalBytes.Length is 0 or > SanitizedExportLimits.MaximumRecordBytes)
            return "producer_contract_invalid";
        try
        {
            using var document = JsonDocument.Parse(record.CanonicalBytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (!IsMinifiedJson(record.CanonicalBytes))
                return "producer_contract_invalid";
            return record.RecordType switch
            {
                "repository_metadata_projection" => ValidateRepository(record, root, requireEnvelope),
                "instruction_finding_handoff" => "producer_validator_unavailable",
                "alert_receipt" => ValidateAlert(record, root, requireEnvelope),
                _ => "unsupported_record_type",
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            return "producer_contract_invalid";
        }
    }

    private static string? ValidateRepository(SanitizedExportRecord record, JsonElement root, bool requireEnvelope)
    {
        string[] names = ["schema_version", "record_id", "session_id", "trace_id", "source_surface", "repository_name", "workspace_label", "repo_snapshot", "observed_at", "completeness", "content_state", "retention_state"];
        if (!Exact(root, names) || Text(root, "schema_version") != "repository-metadata-projection.v1"
            || !Safe(Text(root, "record_id")) || !Safe(Text(root, "session_id")) || !Safe(Text(root, "source_surface"))
            || !NullableSafe(root, "trace_id") || !NullableSafe(root, "repository_name") || !NullableSafe(root, "workspace_label") || !NullableSafe(root, "repo_snapshot")
            || ParseTimestamp(root.GetProperty("observed_at")) is null
            || !Safe(Text(root, "completeness")) || !Safe(Text(root, "content_state")) || !Safe(Text(root, "retention_state")))
            return "producer_contract_invalid";
        if (!requireEnvelope) return Text(root, "record_id") == record.RecordId ? null : "producer_envelope_mismatch";
        return Text(root, "record_id") == record.RecordId
            && Text(root, "session_id") == record.SessionId
            && NullableText(root, "trace_id") == record.TraceId
            && Text(root, "source_surface") == record.SourceSurface
            && NullableText(root, "repository_name") == record.RepositoryName
            && NullableText(root, "workspace_label") == record.WorkspaceLabel
            && NullableText(root, "repo_snapshot") == record.RepoSnapshot
            && ParseTimestamp(root.GetProperty("observed_at")) == record.ObservedAt
            && Text(root, "completeness") == record.Completeness
            && Text(root, "content_state") == record.ContentState
            && Text(root, "retention_state") == record.RetentionState
                ? null : "producer_envelope_mismatch";
    }

    private static string? ValidateAlert(SanitizedExportRecord record, JsonElement root, bool requireEnvelope)
    {
        if (!TryParseAlertReceipt(record.CanonicalBytes, out var receipt) || receipt is null)
            return "producer_contract_invalid";
        if (record.RecordId != receipt.AlertId) return "producer_envelope_mismatch";
        return !requireEnvelope || record.SessionId == receipt.SessionId && record.TraceId == receipt.TraceId
            && record.SourceSurface == receipt.SourceSurface && record.ObservedAt == receipt.LastObservedAt
            && record.RepositoryName is null && record.WorkspaceLabel is null && record.RepoSnapshot is null ? null : "producer_envelope_mismatch";
    }

    internal static bool AllowedType(string value) => value is "repository_metadata_projection" or "instruction_finding_handoff" or "alert_receipt";
    private static bool Exact(JsonElement value, params string[] names) => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Select(property => property.Name).SequenceEqual(names, StringComparer.Ordinal);
    private static string Text(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.String ? value.GetProperty(name).GetString()! : string.Empty;
    private static string? NullableText(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Text(value, name);
    private static bool NullableSafe(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.Null || Safe(Text(value, name));
    private static bool Safe(string? value) => value is { Length: > 0 and <= SanitizedExportLimits.MaximumIdentifierLength } && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#');
    private static bool IsAlertToken(string? value) => value is { Length: > 0 and <= 128 } && AlertToken().IsMatch(value);
    private static bool IsOpaqueId(string? value) => value is { Length: > 0 and <= 256 }
        && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\' or '?' or '#');

    private static bool TryParseAlertReceipt(ReadOnlySpan<byte> bytes, out AlertReceipt? receipt)
    {
        receipt = null;
        try
        {
            receipt = JsonSerializer.Deserialize<AlertReceipt>(bytes, AlertReceiptOptions);
            return receipt is not null && ValidAlertReceipt(receipt) && bytes.SequenceEqual(AlertCanonicalJson.SerializeReceipt(receipt));
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or AlertContractException) { return false; }
    }

    private static bool ValidAlertReceipt(AlertReceipt receipt) =>
        receipt.SchemaVersion == AlertContractVersions.Receipt
        && receipt.SanitizedExportProfile == AlertContractVersions.SanitizedReceiptProfile
        && receipt.InitialState == AlertInitialState.Open
        && IsOpaqueId(receipt.AlertId) && receipt.AlertId.Length == 64
        && IsOpaqueId(receipt.EvaluationId) && receipt.EvaluationId.Length == 64
        && IsAlertToken(receipt.RuleId) && IsAlertToken(receipt.RuleVersion)
        && IsAlertToken(receipt.SourceSurface) && IsAlertToken(receipt.SourceVersion)
        && IsOpaqueId(receipt.SessionId) && (receipt.TraceId is null || IsOpaqueId(receipt.TraceId))
        && Enum.IsDefined(receipt.Severity) && Enum.IsDefined(receipt.Completeness)
        && receipt.FirstObservedAt.Offset == TimeSpan.Zero && receipt.LastObservedAt.Offset == TimeSpan.Zero
        && receipt.FirstObservedAt <= receipt.LastObservedAt
        && receipt.Evidence.Count > 0
        && receipt.Evidence.All(reference => ValidEvidence(reference, receipt))
        && receipt.Evidence.Distinct().Count() == receipt.Evidence.Count
        && ValidObservedValues(receipt.ObservedValues) && ValidObservedValues(receipt.EffectiveThresholds)
        && receipt.RequiredCapabilities.All(IsAlertToken)
        && receipt.RequiredCapabilities.Distinct(StringComparer.Ordinal).Count() == receipt.RequiredCapabilities.Count
        && receipt.CompletenessReasons.All(IsAlertToken)
        && receipt.CompletenessReasons.Distinct(StringComparer.Ordinal).Count() == receipt.CompletenessReasons.Count
        && IsAlertToken(receipt.ConfigurationVersion)
        && LowerHash(receipt.ConfigurationHash) && LowerHash(receipt.EvaluationInputHash)
        && !string.IsNullOrWhiteSpace(receipt.Summary) && receipt.Summary.Length <= 160 && !receipt.Summary.Any(char.IsControl);

    private static bool ValidEvidence(AlertEvidenceReference reference, AlertReceipt receipt) =>
        Enum.IsDefined(reference.Kind) && IsOpaqueId(reference.EvidenceId) && IsOpaqueId(reference.SessionId)
        && reference.SessionId == receipt.SessionId && reference.TraceId == receipt.TraceId
        && (reference.TraceId is null || IsOpaqueId(reference.TraceId))
        && (reference.SpanId is null || IsOpaqueId(reference.SpanId))
        && (reference.TurnId is null || IsOpaqueId(reference.TurnId))
        && (reference.EventId is null || IsOpaqueId(reference.EventId))
        && (reference.ToolCallId is null || IsOpaqueId(reference.ToolCallId))
        && reference.ObservedAt.Offset == TimeSpan.Zero
        && reference.ObservedAt >= receipt.FirstObservedAt && reference.ObservedAt <= receipt.LastObservedAt
        && reference.Kind switch
        {
            AlertEvidenceKind.Session => true,
            AlertEvidenceKind.Trace => reference.TraceId is not null,
            AlertEvidenceKind.Span => reference.TraceId is not null && reference.SpanId is not null,
            AlertEvidenceKind.Turn => reference.TurnId is not null,
            AlertEvidenceKind.Event => reference.EventId is not null,
            AlertEvidenceKind.ToolCall => reference.ToolCallId is not null,
            _ => false,
        };

    private static bool ValidObservedValues(IReadOnlyList<AlertObservedValue> values) => values.Count > 0
        && values.All(value => IsAlertToken(value.Name) && IsAlertToken(value.Unit))
        && values.Select(value => (value.Name, value.Unit)).Distinct().Count() == values.Count;

    private static bool LowerHash(string value) => value.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static JsonSerializerOptions CreateAlertReceiptOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            RespectNullableAnnotations = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }
    private static DateTimeOffset? ParseTimestamp(JsonElement value) => DateTimeOffset.TryParseExact(value.GetString(), "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result) ? result : null;
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

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)] private static partial Regex AlertToken();
}

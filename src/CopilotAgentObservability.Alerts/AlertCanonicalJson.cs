using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.Alerts;

public static class AlertCanonicalJson
{
    public static byte[] SerializeEvaluation(AlertEvaluationResult evaluation) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", evaluation.SchemaVersion);
        writer.WriteString("evaluation_id", evaluation.EvaluationId);
        writer.WriteString("input_hash", evaluation.InputHash);
        writer.WriteString("configuration_version", evaluation.ConfigurationVersion);
        writer.WriteString("configuration_hash", evaluation.ConfigurationHash);
        writer.WritePropertyName("receipts");
        writer.WriteStartArray();
        foreach (var receipt in evaluation.Receipts.OrderBy(item => AlertWire.SeverityRank(item.Severity))
            .ThenBy(item => item.RuleId, StringComparer.Ordinal).ThenBy(item => item.RuleVersion, StringComparer.Ordinal)
            .ThenBy(item => item.FirstObservedAt).ThenBy(item => EvidenceIdentity(item.Evidence), StringComparer.Ordinal)
            .ThenBy(item => item.AlertId, StringComparer.Ordinal))
        {
            WriteReceipt(writer, receipt);
        }
        writer.WriteEndArray();
        writer.WritePropertyName("suppressions");
        writer.WriteStartArray();
        foreach (var suppression in evaluation.Suppressions
            .GroupBy(item => $"{item.RuleId}\n{item.RuleVersion}\n{item.Code}\n{string.Join('\n', item.MissingCapabilities.Order(StringComparer.Ordinal))}", StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.RuleId, StringComparer.Ordinal)
            .ThenBy(item => item.RuleVersion, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal))
        {
            WriteSuppression(writer, suppression);
        }
        writer.WriteEndArray();
        writer.WritePropertyName("rejected_matches");
        writer.WriteStartArray();
        foreach (var rejected in evaluation.RejectedMatches.Distinct().OrderBy(item => item.RuleId, StringComparer.Ordinal)
            .ThenBy(item => item.RuleVersion, StringComparer.Ordinal).ThenBy(item => item.Code, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("rule_id", rejected.RuleId);
            writer.WriteString("rule_version", rejected.RuleVersion);
            writer.WriteString("code", rejected.Code);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    public static byte[] SerializeReceipt(AlertReceipt receipt) => Write(writer => WriteReceipt(writer, receipt));

    public static byte[] SerializeSuppression(AlertSuppression suppression) => Write(writer => WriteSuppression(writer, suppression));

    internal static byte[] SerializeSnapshot(AlertNormalizedSnapshot snapshot) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", snapshot.SchemaVersion);
        writer.WriteString("source_surface", snapshot.SourceSurface);
        writer.WriteString("source_version", snapshot.SourceVersion);
        writer.WriteString("session_id", snapshot.SessionId);
        String(writer, "trace_id", snapshot.TraceId);
        writer.WriteString("completeness", AlertWire.Completeness(snapshot.Completeness));
        Strings(writer, "completeness_reasons", AlertCanonicalOrdering.CompletenessReasons(snapshot.CompletenessReasons));
        writer.WriteString("first_observed_at", AlertWire.Timestamp(snapshot.FirstObservedAt));
        writer.WriteString("last_observed_at", AlertWire.Timestamp(snapshot.LastObservedAt));
        writer.WritePropertyName("capabilities");
        writer.WriteStartArray();
        foreach (var capability in snapshot.Capabilities.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", capability.Name);
            writer.WriteString("availability", AlertWire.Capability(capability.Availability));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WritePropertyName("signals");
        writer.WriteStartArray();
        foreach (var signal in snapshot.Signals.OrderBy(item => item.Sequence).ThenBy(item => item.ObservedAt).ThenBy(item => item.SignalId, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("signal_id", signal.SignalId);
            writer.WriteString("kind", AlertWire.SignalKind(signal.Kind));
            writer.WriteNumber("sequence", signal.Sequence);
            writer.WriteString("observed_at", AlertWire.Timestamp(signal.ObservedAt));
            String(writer, "parent_signal_id", signal.ParentSignalId);
            writer.WriteString("status", AlertWire.SignalStatus(signal.Status));
            writer.WritePropertyName("metrics");
            writer.WriteStartArray();
            foreach (var metric in signal.Metrics.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", metric.Name);
                writer.WriteString("unit", metric.Unit);
                Number(writer, "value", metric.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("comparable_keys");
            writer.WriteStartArray();
            foreach (var key in signal.ComparableKeys.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                writer.WriteStartObject();
                writer.WriteString("name", key.Name);
                writer.WriteString("kind", AlertWire.ComparableKeyKind(key.Kind));
                writer.WriteString("value", key.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WritePropertyName("evidence");
            WriteEvidence(writer, signal.Evidence);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    internal static byte[] SerializeResolvedConfiguration(
        AlertEngineConfiguration configuration,
        IReadOnlyDictionary<(string RuleId, string RuleVersion), ResolvedAlertRuleConfiguration> resolved) => Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", configuration.SchemaVersion);
        writer.WriteString("configuration_version", configuration.ConfigurationVersion);
        writer.WritePropertyName("rules");
        writer.WriteStartArray();
        foreach (var item in resolved.OrderBy(item => item.Key.RuleId, StringComparer.Ordinal).ThenBy(item => item.Key.RuleVersion, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("rule_id", item.Key.RuleId);
            writer.WriteString("rule_version", item.Key.RuleVersion);
            writer.WriteBoolean("enabled", item.Value.Enabled);
            writer.WritePropertyName("thresholds");
            writer.WriteStartObject();
            foreach (var threshold in item.Value.Thresholds.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                Number(writer, threshold.Key, threshold.Value);
            }
            writer.WriteEndObject();
            writer.WritePropertyName("source_surface_allowlist");
            if (item.Value.SourceSurfaceAllowlist is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                writer.WriteStartArray();
                foreach (var source in item.Value.SourceSurfaceAllowlist)
                {
                    writer.WriteStringValue(source);
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    internal static string EvidenceIdentity(IEnumerable<AlertEvidenceReference> references) =>
        Encoding.UTF8.GetString(Write(writer =>
        {
            writer.WriteStartArray();
            foreach (var reference in AlertCanonicalOrdering.Evidence(references))
            {
                WriteEvidence(writer, reference);
            }
            writer.WriteEndArray();
        }));

    internal static string ObservedIdentity(IEnumerable<AlertObservedValue> values) =>
        Encoding.UTF8.GetString(Write(writer => WriteObservedValues(writer, values)));

    private static void WriteReceipt(Utf8JsonWriter writer, AlertReceipt receipt)
    {
        writer.WriteStartObject();
        writer.WriteString("schema_version", receipt.SchemaVersion);
        writer.WriteString("sanitized_export_profile", receipt.SanitizedExportProfile);
        writer.WriteString("alert_id", receipt.AlertId);
        writer.WriteString("evaluation_id", receipt.EvaluationId);
        writer.WriteString("rule_id", receipt.RuleId);
        writer.WriteString("rule_version", receipt.RuleVersion);
        writer.WriteString("severity", AlertWire.Severity(receipt.Severity));
        writer.WriteString("initial_state", "open");
        writer.WriteString("source_surface", receipt.SourceSurface);
        writer.WriteString("source_version", receipt.SourceVersion);
        writer.WriteString("session_id", receipt.SessionId);
        String(writer, "trace_id", receipt.TraceId);
        writer.WritePropertyName("evidence");
        writer.WriteStartArray();
        foreach (var reference in AlertCanonicalOrdering.Evidence(receipt.Evidence))
        {
            WriteEvidence(writer, reference);
        }
        writer.WriteEndArray();
        writer.WritePropertyName("observed_values");
        WriteObservedValues(writer, receipt.ObservedValues);
        writer.WritePropertyName("effective_thresholds");
        WriteObservedValues(writer, receipt.EffectiveThresholds);
        writer.WriteString("configuration_version", receipt.ConfigurationVersion);
        writer.WriteString("configuration_hash", receipt.ConfigurationHash);
        Strings(writer, "required_capabilities", receipt.RequiredCapabilities.Order(StringComparer.Ordinal));
        writer.WriteString("completeness", AlertWire.Completeness(receipt.Completeness));
        Strings(writer, "completeness_reasons", AlertCanonicalOrdering.CompletenessReasons(receipt.CompletenessReasons));
        writer.WriteString("first_observed_at", AlertWire.Timestamp(receipt.FirstObservedAt));
        writer.WriteString("last_observed_at", AlertWire.Timestamp(receipt.LastObservedAt));
        writer.WriteString("evaluation_input_hash", receipt.EvaluationInputHash);
        writer.WriteString("summary", receipt.Summary);
        writer.WriteEndObject();
    }

    private static void WriteSuppression(Utf8JsonWriter writer, AlertSuppression suppression)
    {
        writer.WriteStartObject();
        writer.WriteString("evaluation_id", suppression.EvaluationId);
        writer.WriteString("rule_id", suppression.RuleId);
        writer.WriteString("rule_version", suppression.RuleVersion);
        writer.WriteString("code", suppression.Code);
        Strings(writer, "missing_capabilities", suppression.MissingCapabilities.Order(StringComparer.Ordinal));
        writer.WriteEndObject();
    }

    private static void WriteEvidence(Utf8JsonWriter writer, AlertEvidenceReference reference)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", AlertWire.EvidenceKind(reference.Kind));
        writer.WriteString("evidence_id", reference.EvidenceId);
        writer.WriteString("session_id", reference.SessionId);
        String(writer, "trace_id", reference.TraceId);
        String(writer, "span_id", reference.SpanId);
        String(writer, "turn_id", reference.TurnId);
        String(writer, "event_id", reference.EventId);
        String(writer, "tool_call_id", reference.ToolCallId);
        writer.WriteString("observed_at", AlertWire.Timestamp(reference.ObservedAt));
        writer.WriteEndObject();
    }

    private static void WriteObservedValues(Utf8JsonWriter writer, IEnumerable<AlertObservedValue> values)
    {
        writer.WriteStartArray();
        foreach (var value in values.OrderBy(item => item.Name, StringComparer.Ordinal).ThenBy(item => item.Unit, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", value.Name);
            writer.WriteString("unit", value.Unit);
            Number(writer, "value", value.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            write(writer);
        }
        return stream.ToArray();
    }

    private static void String(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name); else writer.WriteString(name, value);
    }

    private static void Strings(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void Number(Utf8JsonWriter writer, string name, decimal value)
    {
        writer.WritePropertyName(name);
        writer.WriteRawValue(value.ToString("G29", System.Globalization.CultureInfo.InvariantCulture), skipInputValidation: true);
    }
}

internal static class AlertWire
{
    public static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
    public static string Capability(AlertCapabilityAvailability value) => value switch { AlertCapabilityAvailability.Available => "available", AlertCapabilityAvailability.Unavailable => "unavailable", AlertCapabilityAvailability.Unknown => "unknown", _ => throw InvalidEnum() };
    public static string SignalKind(AlertSignalKind value) => value switch { AlertSignalKind.LlmCall => "llm_call", AlertSignalKind.ToolCall => "tool_call", AlertSignalKind.Permission => "permission", AlertSignalKind.FileAccess => "file_access", AlertSignalKind.SessionEvent => "session_event", _ => throw InvalidEnum() };
    public static string SignalStatus(AlertSignalStatus value) => value switch { AlertSignalStatus.Success => "success", AlertSignalStatus.Error => "error", AlertSignalStatus.Cancelled => "cancelled", AlertSignalStatus.Unknown => "unknown", _ => throw InvalidEnum() };
    public static string ComparableKeyKind(AlertComparableKeyKind value) => value switch { AlertComparableKeyKind.MetadataToken => "metadata_token", AlertComparableKeyKind.SensitiveHmac => "sensitive_hmac", _ => throw InvalidEnum() };
    public static string EvidenceKind(AlertEvidenceKind value) => value switch { AlertEvidenceKind.Session => "session", AlertEvidenceKind.Trace => "trace", AlertEvidenceKind.Span => "span", AlertEvidenceKind.Turn => "turn", AlertEvidenceKind.Event => "event", AlertEvidenceKind.ToolCall => "tool_call", _ => throw InvalidEnum() };
    public static string Completeness(AlertCompleteness value) => value switch { AlertCompleteness.Unbound => "unbound", AlertCompleteness.Partial => "partial", AlertCompleteness.Rich => "rich", AlertCompleteness.Full => "full", _ => throw InvalidEnum() };
    public static string Severity(AlertSeverity value) => value switch { AlertSeverity.Critical => "critical", AlertSeverity.Warning => "warning", AlertSeverity.Info => "info", _ => throw InvalidEnum() };
    public static int SeverityRank(AlertSeverity value) => value switch { AlertSeverity.Critical => 0, AlertSeverity.Warning => 1, AlertSeverity.Info => 2, _ => throw InvalidEnum() };
    private static AlertContractException InvalidEnum() => new("invalid_contract_value", "Alert contract enum value is invalid.");
}

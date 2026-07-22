using System.Text.Json;

namespace CopilotAgentObservability.Alerts;

public static class AlertReceiptConsumerV1
{
    private const int MaximumCanonicalBytes = 8_388_608;
    private const int MaximumJsonDepth = 3;

    public static AlertReceiptConsumerEnvelopeV1 Validate(ReadOnlySpan<byte> canonicalReceipt)
    {
        var receipt = ValidateCanonicalReceipt(canonicalReceipt);
        return new AlertReceiptConsumerEnvelopeV1(
            receipt.AlertId,
            receipt.SessionId,
            receipt.TraceId,
            receipt.SourceSurface,
            receipt.LastObservedAt);
    }

    internal static AlertReceipt ValidateCanonicalReceipt(ReadOnlySpan<byte> canonicalReceipt)
    {
        if (canonicalReceipt.Length is 0 or > MaximumCanonicalBytes)
        {
            throw new AlertReceiptConsumerException();
        }

        try
        {
            using var document = JsonDocument.Parse(
                canonicalReceipt.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = MaximumJsonDepth,
                });
            var receipt = AlertReceiptContractV1.Parse(document.RootElement);
            if (!AlertReceiptContractV1.IsValid(receipt)
                || !canonicalReceipt.SequenceEqual(AlertCanonicalJson.SerializeReceipt(receipt)))
            {
                throw new AlertReceiptFormatException();
            }

            return receipt;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            throw new AlertReceiptConsumerException();
        }
    }
}

public sealed class AlertReceiptConsumerEnvelopeV1
{
    internal AlertReceiptConsumerEnvelopeV1(
        string alertId,
        string sessionId,
        string? traceId,
        string sourceSurface,
        DateTimeOffset lastObservedAt)
    {
        AlertId = alertId;
        SessionId = sessionId;
        TraceId = traceId;
        SourceSurface = sourceSurface;
        LastObservedAt = lastObservedAt;
    }

    public string AlertId { get; }
    public string SessionId { get; }
    public string? TraceId { get; }
    public string SourceSurface { get; }
    public DateTimeOffset LastObservedAt { get; }
}

public sealed class AlertReceiptConsumerException : Exception
{
    internal AlertReceiptConsumerException()
        : base("Alert receipt is invalid.")
    {
    }

    public string Code { get; } = "invalid_alert_receipt";
}

internal static class AlertReceiptContractV1
{
    private static readonly string[] RootProperties =
    [
        "schema_version", "sanitized_export_profile", "alert_id", "evaluation_id", "rule_id", "rule_version",
        "severity", "initial_state", "source_surface", "source_version", "session_id", "trace_id", "evidence",
        "observed_values", "effective_thresholds", "configuration_version", "configuration_hash",
        "required_capabilities", "completeness", "completeness_reasons", "first_observed_at", "last_observed_at",
        "evaluation_input_hash", "summary",
    ];

    private static readonly string[] EvidenceProperties =
    [
        "kind", "evidence_id", "session_id", "trace_id", "span_id", "turn_id", "event_id", "tool_call_id", "observed_at",
    ];

    private static readonly string[] ObservedValueProperties = ["name", "unit", "value"];

    private static readonly IReadOnlyDictionary<string, AlertCompleteness> CompletenessReasonCeilings =
        new Dictionary<string, AlertCompleteness>(StringComparer.Ordinal)
        {
            ["missing_native_session_id"] = AlertCompleteness.Unbound,
            ["missing_trace_context"] = AlertCompleteness.Rich,
            ["trace_signal_disabled"] = AlertCompleteness.Rich,
            ["content_capture_disabled"] = AlertCompleteness.Rich,
            ["unsupported_source_version"] = AlertCompleteness.Rich,
            ["ingest_gap"] = AlertCompleteness.Rich,
            ["hook_only"] = AlertCompleteness.Rich,
            ["historical_summary_only"] = AlertCompleteness.Partial,
            ["unknown_span_kind"] = AlertCompleteness.Rich,
            ["schema_drift_detected"] = AlertCompleteness.Partial,
            ["planned_source_not_enabled"] = AlertCompleteness.Unbound,
        };

    public static AlertReceipt Parse(JsonElement root)
    {
        RequireExactProperties(root, RootProperties);
        return new AlertReceipt(
            RequiredString(root, "schema_version"),
            RequiredString(root, "sanitized_export_profile"),
            RequiredString(root, "alert_id"),
            RequiredString(root, "evaluation_id"),
            RequiredString(root, "rule_id"),
            RequiredString(root, "rule_version"),
            ParseSeverity(RequiredString(root, "severity")),
            ParseInitialState(RequiredString(root, "initial_state")),
            RequiredString(root, "source_surface"),
            RequiredString(root, "source_version"),
            RequiredString(root, "session_id"),
            OptionalString(root, "trace_id"),
            ParseEvidence(root.GetProperty("evidence")),
            ParseObservedValues(root.GetProperty("observed_values")),
            ParseObservedValues(root.GetProperty("effective_thresholds")),
            RequiredString(root, "configuration_version"),
            RequiredString(root, "configuration_hash"),
            ParseStrings(root.GetProperty("required_capabilities")),
            ParseCompleteness(RequiredString(root, "completeness")),
            ParseStrings(root.GetProperty("completeness_reasons")),
            ParseTimestamp(root.GetProperty("first_observed_at")),
            ParseTimestamp(root.GetProperty("last_observed_at")),
            RequiredString(root, "evaluation_input_hash"),
            RequiredString(root, "summary"));
    }

    public static bool IsValid(AlertReceipt? receipt)
    {
        if (receipt is null
            || receipt.SchemaVersion != AlertContractVersions.Receipt
            || receipt.SanitizedExportProfile != AlertContractVersions.SanitizedReceiptProfile
            || !CanonicalHash(receipt.AlertId)
            || !CanonicalHash(receipt.EvaluationId)
            || !AlertValidation.IsToken(receipt.RuleId)
            || !AlertValidation.IsToken(receipt.RuleVersion)
            || !Enum.IsDefined(receipt.Severity)
            || receipt.InitialState != AlertInitialState.Open
            || !AlertValidation.IsToken(receipt.SourceSurface)
            || !AlertValidation.IsToken(receipt.SourceVersion)
            || !AlertValidation.IsOpaqueId(receipt.SessionId)
            || receipt.TraceId is not null && !AlertValidation.IsOpaqueId(receipt.TraceId)
            || !AlertValidation.IsToken(receipt.ConfigurationVersion)
            || !CanonicalHash(receipt.ConfigurationHash)
            || !CanonicalHash(receipt.EvaluationInputHash)
            || !BoundedSummary(receipt.Summary)
            || receipt.FirstObservedAt > receipt.LastObservedAt
            || receipt.Evidence is null or { Count: 0 }
            || receipt.ObservedValues is null or { Count: 0 }
            || receipt.EffectiveThresholds is null
            || receipt.RequiredCapabilities is null
            || receipt.CompletenessReasons is null
            || !Enum.IsDefined(receipt.Completeness))
        {
            return false;
        }

        return ValidEvidence(receipt)
            && ValidObservedValues(receipt.ObservedValues)
            && ValidObservedValues(receipt.EffectiveThresholds)
            && UniqueTokens(receipt.RequiredCapabilities)
            && ValidCompleteness(receipt.Completeness, receipt.CompletenessReasons)
            && string.Equals(receipt.AlertId, AlertReceiptIdentityV1.Create(receipt), StringComparison.Ordinal);
    }

    private static bool ValidEvidence(AlertReceipt receipt)
    {
        if (receipt.Evidence.Distinct().Count() != receipt.Evidence.Count)
        {
            return false;
        }

        foreach (var reference in receipt.Evidence)
        {
            if (!AlertValidation.IsOpaqueId(reference.EvidenceId)
                || !AlertValidation.IsOpaqueId(reference.SessionId)
                || reference.SessionId != receipt.SessionId
                || reference.TraceId != receipt.TraceId
                || !OptionalOpaqueId(reference.TraceId)
                || !OptionalOpaqueId(reference.SpanId)
                || !OptionalOpaqueId(reference.TurnId)
                || !OptionalOpaqueId(reference.EventId)
                || !OptionalOpaqueId(reference.ToolCallId)
                || reference.Kind switch
                {
                    AlertEvidenceKind.Session => false,
                    AlertEvidenceKind.Trace => reference.TraceId is null,
                    AlertEvidenceKind.Span => reference.TraceId is null || reference.SpanId is null,
                    AlertEvidenceKind.Turn => reference.TurnId is null,
                    AlertEvidenceKind.Event => reference.EventId is null,
                    AlertEvidenceKind.ToolCall => reference.ToolCallId is null,
                    _ => true,
                })
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidObservedValues(IReadOnlyList<AlertObservedValue> values) =>
        values.All(value => AlertValidation.IsToken(value.Name) && AlertValidation.IsToken(value.Unit))
        && values.Select(value => (value.Name, value.Unit)).Distinct().Count() == values.Count;

    private static bool ValidCompleteness(AlertCompleteness completeness, IReadOnlyList<string> reasons)
    {
        if (!AreKnownCompletenessReasons(reasons))
        {
            return false;
        }

        var completenessRank = CompletenessRank(completeness);
        return reasons.All(reason =>
            CompletenessReasonCeilings.TryGetValue(reason, out var ceiling)
            && completenessRank <= CompletenessRank(ceiling));
    }

    public static bool AreKnownCompletenessReasons(IReadOnlyList<string>? reasons) =>
        reasons is not null
        && reasons.Distinct(StringComparer.Ordinal).Count() == reasons.Count
        && reasons.All(CompletenessReasonCeilings.ContainsKey);

    private static int CompletenessRank(AlertCompleteness value) => value switch
    {
        AlertCompleteness.Unbound => 0,
        AlertCompleteness.Partial => 1,
        AlertCompleteness.Rich => 2,
        AlertCompleteness.Full => 3,
        _ => int.MaxValue,
    };

    private static bool UniqueTokens(IReadOnlyList<string> values) =>
        values.All(AlertValidation.IsToken)
        && values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool CanonicalHash(string? value) =>
        value is { Length: 64 }
        && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool BoundedSummary(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 160
        && !value.Any(char.IsControl);

    private static bool OptionalOpaqueId(string? value) => value is null || AlertValidation.IsOpaqueId(value);

    private static IReadOnlyList<AlertEvidenceReference> ParseEvidence(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array);
        var values = new List<AlertEvidenceReference>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            RequireExactProperties(item, EvidenceProperties);
            values.Add(new AlertEvidenceReference(
                ParseEvidenceKind(RequiredString(item, "kind")),
                RequiredString(item, "evidence_id"),
                RequiredString(item, "session_id"),
                OptionalString(item, "trace_id"),
                OptionalString(item, "span_id"),
                OptionalString(item, "turn_id"),
                OptionalString(item, "event_id"),
                OptionalString(item, "tool_call_id"),
                ParseTimestamp(item.GetProperty("observed_at"))));
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyList<AlertObservedValue> ParseObservedValues(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array);
        var values = new List<AlertObservedValue>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            RequireExactProperties(item, ObservedValueProperties);
            var value = item.GetProperty("value");
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var number))
            {
                throw new AlertReceiptFormatException();
            }

            values.Add(new AlertObservedValue(
                RequiredString(item, "name"),
                RequiredString(item, "unit"),
                number));
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyList<string> ParseStrings(JsonElement element)
    {
        RequireKind(element, JsonValueKind.Array);
        var values = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value)
            {
                throw new AlertReceiptFormatException();
            }

            values.Add(value);
        }

        return Array.AsReadOnly(values.ToArray());
    }

    private static DateTimeOffset ParseTimestamp(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String
            || !AlertWire.TryParseTimestamp(element.GetString(), out var value))
        {
            throw new AlertReceiptFormatException();
        }

        return value;
    }

    private static AlertSeverity ParseSeverity(string value) => value switch
    {
        "critical" => AlertSeverity.Critical,
        "warning" => AlertSeverity.Warning,
        "info" => AlertSeverity.Info,
        _ => throw new AlertReceiptFormatException(),
    };

    private static AlertInitialState ParseInitialState(string value) => value switch
    {
        "open" => AlertInitialState.Open,
        _ => throw new AlertReceiptFormatException(),
    };

    private static AlertCompleteness ParseCompleteness(string value) => value switch
    {
        "unbound" => AlertCompleteness.Unbound,
        "partial" => AlertCompleteness.Partial,
        "rich" => AlertCompleteness.Rich,
        "full" => AlertCompleteness.Full,
        _ => throw new AlertReceiptFormatException(),
    };

    private static AlertEvidenceKind ParseEvidenceKind(string value) => value switch
    {
        "session" => AlertEvidenceKind.Session,
        "trace" => AlertEvidenceKind.Trace,
        "span" => AlertEvidenceKind.Span,
        "turn" => AlertEvidenceKind.Turn,
        "event" => AlertEvidenceKind.Event,
        "tool_call" => AlertEvidenceKind.ToolCall,
        _ => throw new AlertReceiptFormatException(),
    };

    private static string RequiredString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw new AlertReceiptFormatException();
        }

        return text;
    }

    private static string? OptionalString(JsonElement element, string name)
    {
        var value = element.GetProperty(name);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw new AlertReceiptFormatException();
        }

        return text;
    }

    private static void RequireExactProperties(JsonElement element, IReadOnlyList<string> expected)
    {
        RequireKind(element, JsonValueKind.Object);
        uint seen = 0;
        foreach (var property in element.EnumerateObject())
        {
            var index = IndexOf(expected, property.Name);
            if (index < 0)
            {
                throw new AlertReceiptFormatException();
            }

            var flag = 1u << index;
            if ((seen & flag) != 0)
            {
                throw new AlertReceiptFormatException();
            }

            seen |= flag;
        }

        var required = (1u << expected.Count) - 1;
        if (seen != required)
        {
            throw new AlertReceiptFormatException();
        }
    }

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (string.Equals(values[index], value, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void RequireKind(JsonElement element, JsonValueKind kind)
    {
        if (element.ValueKind != kind)
        {
            throw new AlertReceiptFormatException();
        }
    }
}

internal sealed class AlertReceiptFormatException : Exception;

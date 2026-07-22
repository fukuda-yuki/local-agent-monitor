using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Alerts;

internal static class AlertHashing
{
    public static byte[] Frame(params byte[][] values)
    {
        using var stream = new MemoryStream();
        Span<byte> length = stackalloc byte[4];
        foreach (var value in values)
        {
            BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
            stream.Write(length);
            stream.Write(value);
        }

        return stream.ToArray();
    }

    public static string Identifier(string domain, params string[] values) =>
        Sha256(Frame([Encoding.UTF8.GetBytes(domain), .. values.Select(Encoding.UTF8.GetBytes)]));

    public static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}

internal static class AlertReceiptIdentityV1
{
    public static string Create(
        string evaluationId,
        string ruleId,
        string ruleVersion,
        AlertSeverity severity,
        IReadOnlyList<AlertEvidenceReference> evidence,
        IReadOnlyList<AlertObservedValue> observedValues,
        DateTimeOffset firstObservedAt,
        DateTimeOffset lastObservedAt) =>
        AlertHashing.Identifier(
            "alert-receipt/v1",
            AlertContractVersions.Receipt,
            evaluationId,
            ruleId,
            ruleVersion,
            AlertWire.Severity(severity),
            AlertCanonicalJson.EvidenceIdentity(evidence),
            AlertCanonicalJson.ObservedIdentity(observedValues),
            AlertWire.Timestamp(firstObservedAt),
            AlertWire.Timestamp(lastObservedAt));

    public static string Create(AlertReceipt receipt) => Create(
        receipt.EvaluationId,
        receipt.RuleId,
        receipt.RuleVersion,
        receipt.Severity,
        receipt.Evidence,
        receipt.ObservedValues,
        receipt.FirstObservedAt,
        receipt.LastObservedAt);
}

internal static class AlertCanonicalOrdering
{
    private static readonly string[] CompletenessReasonOrder =
    [
        "missing_native_session_id", "missing_trace_context", "trace_signal_disabled", "content_capture_disabled",
        "unsupported_source_version", "ingest_gap", "hook_only", "historical_summary_only", "unknown_span_kind",
        "schema_drift_detected", "planned_source_not_enabled",
    ];

    public static IEnumerable<string> CompletenessReasons(IEnumerable<string> reasons)
    {
        var order = CompletenessReasonOrder.Select((value, index) => (value, index)).ToDictionary(item => item.value, item => item.index, StringComparer.Ordinal);
        return reasons.OrderBy(reason => order.GetValueOrDefault(reason, int.MaxValue)).ThenBy(reason => reason, StringComparer.Ordinal);
    }

    public static IEnumerable<AlertEvidenceReference> Evidence(IEnumerable<AlertEvidenceReference> evidence) =>
        evidence.Distinct().OrderBy(item => AlertWire.EvidenceKind(item.Kind), StringComparer.Ordinal)
            .ThenBy(item => item.SessionId, StringComparer.Ordinal).ThenBy(item => item.TraceId, StringComparer.Ordinal)
            .ThenBy(item => item.SpanId, StringComparer.Ordinal).ThenBy(item => item.TurnId, StringComparer.Ordinal)
            .ThenBy(item => item.EventId, StringComparer.Ordinal).ThenBy(item => item.ToolCallId, StringComparer.Ordinal)
            .ThenBy(item => item.EvidenceId, StringComparer.Ordinal).ThenBy(item => item.ObservedAt);
}

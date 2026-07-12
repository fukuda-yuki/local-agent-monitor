using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.SourceCompatibility;

/// <summary>
/// Backend-owned, sanitized source diagnostic projection. The wire shape is
/// intentionally smaller than the source observation row and never carries
/// inventory values, unknown names, raw content, or identifiers beyond the
/// opaque source metadata needed by monitor views.
/// </summary>
internal sealed record SourceDiagnosticDto(
    string? SourceSurface,
    string? SourceApplicationVersion,
    string? SourceAdapter,
    string? AdapterVersion,
    string? SchemaFingerprint,
    string? CompatibilityState,
    IReadOnlyList<string> ReasonCodes,
    string? NextAction)
{
    public static SourceDiagnosticDto? FromRows(IEnumerable<SourceCompatibilityRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var observations = rows.ToArray();
        if (observations.Length == 0)
        {
            return null;
        }

        var selected = observations
            .OrderByDescending(row => Severity(row.CompatibilityState))
            .ThenByDescending(row => row.Id)
            .First();
        var reasons = SourceCompatibilityReasonCodes.CanonicalOrder
            .Where(reason => observations.Any(row => row.ReasonCodes.Contains(reason, StringComparer.Ordinal)))
            .ToArray();

        return new(
            selected.SourceSurface,
            selected.SourceApplicationVersion,
            selected.SourceAdapter,
            selected.AdapterVersion,
            selected.SchemaFingerprint,
            CompatibilityStateWire(selected.CompatibilityState),
            reasons,
            selected.NextAction);
    }

    public static string? AgreedContentState(IEnumerable<SourceCompatibilityRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var states = rows
            .Select(row => row.CaptureContentState is { } state ? CaptureContentStateWire(state) : null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return states.Length == 1 ? states[0] : null;
    }

    public object ToWire() => new
    {
        source_surface = SourceSurface,
        source_application_version = SourceApplicationVersion,
        source_adapter = SourceAdapter,
        adapter_version = AdapterVersion,
        schema_fingerprint = SchemaFingerprint,
        compatibility_state = CompatibilityState,
        reason_codes = ReasonCodes,
        next_action = NextAction,
    };

    private static int Severity(SourceCompatibilityState state) => state switch
    {
        SourceCompatibilityState.RecognizedRecordDropDetected => 6,
        SourceCompatibilityState.AdapterFailure => 5,
        SourceCompatibilityState.UnsupportedSourceVersion => 4,
        SourceCompatibilityState.SchemaDriftDetected => 3,
        SourceCompatibilityState.SupportedWithUnknownFields => 2,
        SourceCompatibilityState.Supported => 1,
        _ => 0,
    };

    private static string CompatibilityStateWire(SourceCompatibilityState state) => state switch
    {
        SourceCompatibilityState.Supported => "supported",
        SourceCompatibilityState.SupportedWithUnknownFields => "supported_with_unknown_fields",
        SourceCompatibilityState.SchemaDriftDetected => "schema_drift_detected",
        SourceCompatibilityState.UnsupportedSourceVersion => "unsupported_source_version",
        SourceCompatibilityState.RecognizedRecordDropDetected => "recognized_record_drop_detected",
        SourceCompatibilityState.AdapterFailure => "adapter_failure",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string CaptureContentStateWire(SourceCaptureContentState state) => state switch
    {
        SourceCaptureContentState.Available => "available",
        SourceCaptureContentState.NotCaptured => "not_captured",
        SourceCaptureContentState.Redacted => "redacted",
        SourceCaptureContentState.Unsupported => "unsupported",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };
}

internal sealed record SourceProjectionState(
    SourceDiagnosticDto? SourceDiagnostic,
    string BindingState,
    string Completeness,
    IReadOnlyList<string> CompletenessReasonCodes,
    string? ContentState)
{
    public static SourceProjectionState Unavailable { get; } = new(null, "otel_only", "unbound", ["missing_native_session_id"], null);
}

internal static class SourceProjectionStateBuilder
{
    public static SourceProjectionState Build(
        IReadOnlyList<SourceCompatibilityRow> observations,
        SessionDetail? session)
    {
        var diagnostic = SourceDiagnosticDto.FromRows(observations);
        // An observation proves only that an OTLP record exists. It is not
        // binding evidence: a Hook event and an unrelated OTLP record may
        // share a trace id. Exact linkage requires the persisted Session
        // enrichment event written by the OTel enricher for this Session.
        var hasExactOtelEvent = session?.Events.Any(IsExactOtelEvent) == true;
        var hasHook = session?.Events.Any(eventItem => eventItem.SourceAdapter == "claude-code-hook") == true;
        var exact = session is not null
            && session.NativeIds.Any(native => native.BindingKind is SessionBindingKind.Native or SessionBindingKind.ExplicitResume or SessionBindingKind.ExplicitHandoff)
            && hasExactOtelEvent;
        var binding = exact ? "exact_linked" : hasHook ? "hook_only" : "otel_only";
        var completeness = session is null
            ? "unbound"
            : SessionWire.ToWire(session.Session.Completeness);
        var reasons = CompletenessReasons(observations, session, binding);
        return new(diagnostic, binding, completeness, reasons, SourceDiagnosticDto.AgreedContentState(observations));
    }

    private static bool IsExactOtelEvent(ObservedSessionEvent eventItem) =>
        !string.IsNullOrWhiteSpace(eventItem.TraceId)
        && eventItem.SourceAdapter is "claude-code-otel" or "otel-exact";

    private static IReadOnlyList<string> CompletenessReasons(
        IReadOnlyList<SourceCompatibilityRow> observations,
        SessionDetail? session,
        string binding)
    {
        var reasons = new HashSet<string>(StringComparer.Ordinal);
        if (session is null || session.NativeIds.Count == 0)
        {
            reasons.Add("missing_native_session_id");
        }
        if (binding == "hook_only")
        {
            reasons.Add("hook_only");
        }
        foreach (var reason in observations.SelectMany(item => item.ReasonCodes))
        {
            if (reason is "unsupported_source_version" or "schema_drift_detected")
            {
                reasons.Add(reason);
            }
        }
        return new[]
        {
            "missing_native_session_id", "missing_trace_context", "trace_signal_disabled", "content_capture_disabled",
            "unsupported_source_version", "ingest_gap", "hook_only", "historical_summary_only", "unknown_span_kind",
            "schema_drift_detected", "planned_source_not_enabled",
        }.Where(reasons.Contains).ToArray();
    }
}

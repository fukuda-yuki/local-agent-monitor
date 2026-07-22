using System.Globalization;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal enum AlertCenterEvidenceAvailability
{
    Available,
    Missing,
    Expired,
    Unknown,
}

internal sealed record AlertCenterEvidenceResolution(
    AlertCenterEvidenceAvailability Availability,
    string? ContentState = null,
    string? Href = null);

internal sealed class AlertCenterEvidenceResolver(
    ISessionStore sessionStore,
    IMonitorProjectionStore projectionStore,
    ISourceCompatibilityStore compatibilityStore) : IAlertEvidenceResolver
{
    private const string MonitorSpanPrefix = "monitor-span-row-v1:";
    private const string SessionEventPrefix = "session-event-row-v1:";
    private const string SourceObservationPrefix = "source-observation-row-v1:";

    public bool Exists(AlertEvidenceReference reference)
    {
        try
        {
            return Resolve(reference).Availability is AlertCenterEvidenceAvailability.Available
                or AlertCenterEvidenceAvailability.Expired;
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return false;
        }
    }

    internal AlertCenterEvidenceResolution Resolve(AlertEvidenceReference reference)
        => Resolve(reference, null, null, requireReceiptPartition: false);

    internal AlertCenterEvidenceResolution ResolveForReceipt(
        AlertEvidenceReference reference,
        string sourceSurface,
        string sourceVersion)
        => Resolve(reference, sourceSurface, sourceVersion, requireReceiptPartition: true);

    private AlertCenterEvidenceResolution Resolve(
        AlertEvidenceReference reference,
        string? sourceSurface,
        string? sourceVersion,
        bool requireReceiptPartition)
    {
        if (!TryCanonicalSession(reference.SessionId, out var sessionId))
        {
            return Unknown();
        }
        var session = sessionStore.GetDetail(sessionId);
        if (session is null)
        {
            return Missing();
        }
        if (reference.TraceId is null || !OwnsTrace(session, reference.TraceId))
        {
            return Unknown();
        }
        if (requireReceiptPartition
            && !ExactReceiptPartition(session, reference.TraceId, sourceSurface!, sourceVersion!))
        {
            return Unknown();
        }

        if (TryPositiveRowId(reference.EvidenceId, MonitorSpanPrefix, out var monitorRowId))
        {
            return ResolveMonitorSpan(reference, session, monitorRowId);
        }
        if (TryGuidRowId(reference.EvidenceId, SessionEventPrefix, out var eventRowId))
        {
            return ResolveSessionEvent(reference, session, eventRowId);
        }
        if (TryPositiveRowId(reference.EvidenceId, SourceObservationPrefix, out var observationRowId))
        {
            return ResolveSourceObservation(reference, session, observationRowId);
        }
        return Unknown();
    }

    internal static DateTimeOffset? MonitorObservedAt(MonitorSpanRow row)
    {
        if (TryTimestamp(row.StartTime, out var start)) return start;
        return TryTimestamp(row.ProjectedAt, out var projected) ? projected : null;
    }

    internal static string MonitorEvidenceId(long rowId) => $"{MonitorSpanPrefix}{rowId.ToString(CultureInfo.InvariantCulture)}";
    internal static string SessionEventEvidenceId(Guid eventId) => $"{SessionEventPrefix}{eventId:D}";
    internal static string SourceObservationEvidenceId(long rowId) => $"{SourceObservationPrefix}{rowId.ToString(CultureInfo.InvariantCulture)}";

    private AlertCenterEvidenceResolution ResolveMonitorSpan(
        AlertEvidenceReference reference,
        SessionDetail session,
        long rowId)
    {
        var row = projectionStore.GetSpansForTrace(reference.TraceId!)
            .SingleOrDefault(item => item.Id == rowId);
        if (row is null)
        {
            return Missing();
        }
        var observedAt = MonitorObservedAt(row);
        var source = compatibilityStore.GetByRawRecordId(row.RawRecordId);
        if (observedAt is null || source is null || !ExactPartition(session, reference.TraceId!, source)
            || row.TraceId != reference.TraceId
            || reference.Kind != AlertEvidenceKind.Span
            || row.SpanId is null
            || row.SpanId != reference.SpanId
            || !SameInstant(reference.ObservedAt, observedAt.Value))
        {
            return Unknown();
        }
        return Available($"/traces/{Uri.EscapeDataString(row.TraceId)}?span={Uri.EscapeDataString(row.SpanId)}");
    }

    private static AlertCenterEvidenceResolution ResolveSessionEvent(
        AlertEvidenceReference reference,
        SessionDetail session,
        Guid rowId)
    {
        var item = session.Events.SingleOrDefault(value => value.EventId == rowId);
        if (item is null)
        {
            return Missing();
        }
        if (reference.Kind != AlertEvidenceKind.Event
            || reference.EventId != rowId.ToString("D")
            || item.TraceId != reference.TraceId
            || !SameInstant(reference.ObservedAt, item.OccurredAt))
        {
            return Unknown();
        }
        var contentState = ContentState(item.ContentState);
        var href = $"/diagnostics?session_id={Uri.EscapeDataString(reference.SessionId)}";
        return item.ContentState == SessionContentState.ExpiredPendingDeletion
            ? new(AlertCenterEvidenceAvailability.Expired, contentState, href)
            : new(AlertCenterEvidenceAvailability.Available, contentState, href);
    }

    private AlertCenterEvidenceResolution ResolveSourceObservation(
        AlertEvidenceReference reference,
        SessionDetail session,
        long rowId)
    {
        var row = compatibilityStore.List(rowId - 1, 1).SingleOrDefault(item => item.Id == rowId);
        if (row is null)
        {
            return Missing();
        }
        var rawRecordId = row.RawRecordId;
        if (reference.Kind != AlertEvidenceKind.Trace
            || reference.SpanId is not null
            || rawRecordId is null
            || !ExactPartition(session, reference.TraceId!, row)
            || !projectionStore.GetSpansForTrace(reference.TraceId!).Any(item => item.RawRecordId == rawRecordId.Value)
            || !SameInstant(reference.ObservedAt, row.ObservedAt))
        {
            return Unknown();
        }
        return Available($"/traces/{Uri.EscapeDataString(reference.TraceId!)}");
    }

    private static bool ExactPartition(SessionDetail session, string traceId, SourceCompatibilityRow source)
    {
        return TryExactSessionPartition(session, traceId, out var surface, out var version)
            && source.SourceSurface == surface
            && source.SourceApplicationVersion == version;
    }

    private static bool ExactReceiptPartition(
        SessionDetail session,
        string traceId,
        string sourceSurface,
        string sourceVersion) =>
        TryExactSessionPartition(session, traceId, out var sessionSurface, out var sessionVersion)
        && sourceSurface == sessionSurface
        && sourceVersion == sessionVersion;

    private static bool TryExactSessionPartition(
        SessionDetail session,
        string traceId,
        out string? surface,
        out string? version)
    {
        surface = null;
        version = null;
        var events = session.Events.Where(item => item.TraceId == traceId).ToArray();
        if (events.Length == 0) return false;
        var partitions = events
            .Select(item => (Surface: Surface(item.SourceSurface, item.SourceAdapter), Version: item.SourceApplicationVersion))
            .ToArray();
        if (partitions.Any(item => item.Surface is null || item.Version is null)) return false;
        var exact = partitions.Distinct().ToArray();
        if (exact.Length != 1) return false;
        var runSurfaces = session.Runs
            .Where(item => item.TraceId == traceId && item.SourceSurface is not null)
            .Select(item => Surface(item.SourceSurface, null))
            .ToArray();
        if (!runSurfaces.All(item => item == exact[0].Surface)) return false;
        surface = exact[0].Surface;
        version = exact[0].Version;
        return true;
    }

    internal static string? Surface(SessionSourceSurface? surface, string? adapter) => surface switch
    {
        SessionSourceSurface.VisualStudioCode => "github-copilot-vscode",
        SessionSourceSurface.CopilotCli => "github-copilot-cli",
        SessionSourceSurface.ClaudeCode => "claude-code",
        null when string.Equals(adapter, "raw-otlp", StringComparison.Ordinal) => "raw-otlp",
        _ => null,
    };

    private static bool OwnsTrace(SessionDetail session, string traceId) =>
        session.Runs.Any(item => item.TraceId == traceId)
        || session.Events.Any(item => item.TraceId == traceId);

    private static bool TryCanonicalSession(string value, out Guid id) =>
        Guid.TryParseExact(value, "D", out id)
        && id != Guid.Empty
        && string.Equals(value, id.ToString("D"), StringComparison.Ordinal);

    private static bool TryPositiveRowId(string value, string prefix, out long rowId)
    {
        rowId = 0;
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(value.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out rowId)
            && rowId > 0
            && string.Equals(
                value[prefix.Length..],
                rowId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
    }

    private static bool TryGuidRowId(string value, string prefix, out Guid rowId)
    {
        rowId = Guid.Empty;
        return value.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(value.AsSpan(prefix.Length), "D", out rowId)
            && rowId != Guid.Empty
            && string.Equals(value[prefix.Length..], rowId.ToString("D"), StringComparison.Ordinal);
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);

    private static bool SameInstant(DateTimeOffset left, DateTimeOffset right) =>
        left.ToUniversalTime() == right.ToUniversalTime();

    private static string ContentState(SessionContentState value) => value switch
    {
        SessionContentState.Available => "available",
        SessionContentState.NotCaptured => "not_captured",
        SessionContentState.Redacted => "redacted",
        SessionContentState.Unsupported => "unsupported",
        SessionContentState.ExpiredPendingDeletion => "expired",
        _ => "unknown",
    };

    private static AlertCenterEvidenceResolution Available(string href) =>
        new(AlertCenterEvidenceAvailability.Available, Href: href);
    private static AlertCenterEvidenceResolution Missing() => new(AlertCenterEvidenceAvailability.Missing);
    private static AlertCenterEvidenceResolution Unknown() => new(AlertCenterEvidenceAvailability.Unknown);
    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

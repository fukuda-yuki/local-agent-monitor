using System.Globalization;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal enum AlertCenterSnapshotCompositionStatus
{
    Success,
    SessionNotFound,
    TraceNotFound,
    TraceNotOwned,
    SourcePartitionMissing,
    SourcePartitionAmbiguous,
    TraceIncomplete,
    StoreBusy,
    StoreUnavailable,
}

internal sealed record AlertCenterSnapshotCompositionResult(
    AlertCenterSnapshotCompositionStatus Status,
    AlertNormalizedSnapshot? Snapshot = null);

internal sealed record AlertCenterEvaluationPolicy(
    AlertRuleRegistry Registry,
    AlertEngineConfiguration Configuration)
{
    internal static AlertCenterEvaluationPolicy Create()
    {
        var registry = new AlertRuleRegistry([
            .. ToolAlertRulePack.CreateRules(),
            .. TokenContextCacheAlertRulePack.CreateRules(),
        ]);
        return new(
            registry,
            new AlertEngineConfiguration(
                AlertContractVersions.Configuration,
                "alert-center-default-v1",
                []));
    }
}

internal sealed class AlertCenterEvaluationSnapshotComposer(
    ISessionStore sessionStore,
    IMonitorProjectionStore projectionStore,
    ISourceCompatibilityStore compatibilityStore)
{
    internal AlertCenterSnapshotCompositionResult Compose(Guid sessionId, string traceId)
    {
        try
        {
            var session = sessionStore.GetDetail(sessionId);
            if (session is null) return new(AlertCenterSnapshotCompositionStatus.SessionNotFound);

            var trace = projectionStore.GetMonitorTrace(traceId);
            if (trace is null) return new(AlertCenterSnapshotCompositionStatus.TraceNotFound);

            var runs = session.Runs.Where(item => string.Equals(item.TraceId, traceId, StringComparison.Ordinal)).ToArray();
            var events = session.Events.Where(item => string.Equals(item.TraceId, traceId, StringComparison.Ordinal)).ToArray();
            if (runs.Length == 0 && events.Length == 0)
            {
                return new(AlertCenterSnapshotCompositionStatus.TraceNotOwned);
            }

            var surfaces = new List<string>();
            var missingSurface = false;
            foreach (var run in runs)
            {
                if (run.SourceSurface is not null)
                {
                    AddSurface(AlertCenterEvidenceResolver.Surface(run.SourceSurface, null), surfaces, ref missingSurface);
                }
            }
            foreach (var item in events)
            {
                AddSurface(AlertCenterEvidenceResolver.Surface(item.SourceSurface, item.SourceAdapter), surfaces, ref missingSurface);
            }

            var distinctSurfaces = surfaces.Distinct(StringComparer.Ordinal).ToArray();
            if (distinctSurfaces.Length > 1)
            {
                return new(AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous);
            }
            if (missingSurface || distinctSurfaces.Length == 0)
            {
                return new(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing);
            }

            var missingVersion = events.Length == 0;
            var versions = new List<string>();
            foreach (var item in events)
            {
                if (!IsToken(item.SourceApplicationVersion))
                {
                    missingVersion = true;
                    continue;
                }
                versions.Add(item.SourceApplicationVersion!);
            }
            var distinctVersions = versions.Distinct(StringComparer.Ordinal).ToArray();
            if (distinctVersions.Length > 1)
            {
                return new(AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous);
            }
            if (missingVersion || distinctVersions.Length == 0)
            {
                return new(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing);
            }

            if (!TryTimestamp(trace.FirstSeenAt, out var firstObservedAt)
                || !TryTimestamp(trace.LastSeenAt, out var lastObservedAt)
                || firstObservedAt > lastObservedAt)
            {
                return new(AlertCenterSnapshotCompositionStatus.TraceIncomplete);
            }

            var spans = projectionStore.GetSpansForTrace(traceId)
                .OrderBy(item => item.RawRecordId)
                .ThenBy(item => item.SpanOrdinal)
                .ThenBy(item => item.Id)
                .ToArray();
            if (trace.SpanCount < 1
                || spans.Length != trace.SpanCount
                || spans.Any(item => item.Id < 1
                    || item.RawRecordId < 1
                    || item.TraceId != traceId
                    || !IsOpaqueId(item.SpanId)
                    || AlertCenterEvidenceResolver.MonitorObservedAt(item) is null))
            {
                return new(AlertCenterSnapshotCompositionStatus.TraceIncomplete);
            }

            var observations = new List<SourceCompatibilityRow>();
            foreach (var rawRecordId in spans.Select(item => item.RawRecordId).Distinct().Order())
            {
                var observation = compatibilityStore.GetByRawRecordId(rawRecordId);
                if (observation is null || observation.RawRecordId != rawRecordId
                    || !IsToken(observation.SourceSurface) || !IsToken(observation.SourceApplicationVersion))
                {
                    return new(AlertCenterSnapshotCompositionStatus.SourcePartitionMissing);
                }
                observations.Add(observation);
            }
            var observedPartitions = observations
                .Select(item => (item.SourceSurface!, item.SourceApplicationVersion!))
                .Distinct()
                .ToArray();
            if (observedPartitions.Length > 1
                || observedPartitions.Length == 1
                && (observedPartitions[0].Item1 != distinctSurfaces[0]
                    || observedPartitions[0].Item2 != distinctVersions[0]))
            {
                return new(AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous);
            }

            var policy = AlertCenterEvaluationPolicy.Create();
            var capabilities = policy.Registry.Rules
                .SelectMany(item => item.Descriptor.RequiredCapabilities)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .Select(item => new AlertCapabilityFact(item, AlertCapabilityAvailability.Unknown))
                .ToArray();
            var signalDrafts = new List<SignalDraft>();
            signalDrafts.AddRange(spans.Select(item => new SignalDraft(
                $"monitor-span-signal-v1:{item.Id.ToString(CultureInfo.InvariantCulture)}",
                AlertSignalKind.SessionEvent,
                AlertCenterEvidenceResolver.MonitorObservedAt(item)!.Value,
                new AlertEvidenceReference(
                    AlertEvidenceKind.Span,
                    AlertCenterEvidenceResolver.MonitorEvidenceId(item.Id),
                    sessionId.ToString("D"),
                    traceId,
                    item.SpanId,
                    null,
                    null,
                    null,
                    AlertCenterEvidenceResolver.MonitorObservedAt(item)!.Value))));
            signalDrafts.AddRange(events.Select(item => new SignalDraft(
                $"session-event-signal-v1:{item.EventId:D}",
                AlertSignalKind.SessionEvent,
                item.OccurredAt,
                new AlertEvidenceReference(
                    AlertEvidenceKind.Event,
                    AlertCenterEvidenceResolver.SessionEventEvidenceId(item.EventId),
                    sessionId.ToString("D"),
                    traceId,
                    null,
                    null,
                    item.EventId.ToString("D"),
                    null,
                    item.OccurredAt))));
            signalDrafts.AddRange(observations.Select(item => new SignalDraft(
                $"source-observation-signal-v1:{item.Id.ToString(CultureInfo.InvariantCulture)}",
                AlertSignalKind.SessionEvent,
                item.ObservedAt,
                new AlertEvidenceReference(
                    AlertEvidenceKind.Trace,
                    AlertCenterEvidenceResolver.SourceObservationEvidenceId(item.Id),
                    sessionId.ToString("D"),
                    traceId,
                    null,
                    null,
                    null,
                    null,
                    item.ObservedAt))));
            var orderedDrafts = signalDrafts
                .OrderBy(item => item.ObservedAt)
                .ThenBy(item => item.SignalId, StringComparer.Ordinal)
                .ToArray();
            if (orderedDrafts.Length > 0)
            {
                firstObservedAt = new[] { firstObservedAt, orderedDrafts[0].ObservedAt }.Min();
                lastObservedAt = new[] { lastObservedAt, orderedDrafts[^1].ObservedAt }.Max();
            }
            var signals = orderedDrafts.Select((item, index) => new AlertSignal(
                item.SignalId,
                item.Kind,
                index,
                item.ObservedAt,
                null,
                AlertSignalStatus.Unknown,
                [],
                [],
                item.Evidence)).ToArray();
            var snapshot = new AlertNormalizedSnapshot(
                AlertContractVersions.Snapshot,
                distinctSurfaces[0],
                distinctVersions[0],
                sessionId.ToString("D"),
                traceId,
                Completeness(session.Session.Completeness),
                [],
                firstObservedAt,
                lastObservedAt,
                capabilities,
                signals);
            return new(AlertCenterSnapshotCompositionStatus.Success, snapshot);
        }
        catch (PersistenceBusyException)
        {
            return new(AlertCenterSnapshotCompositionStatus.StoreBusy);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(AlertCenterSnapshotCompositionStatus.StoreBusy);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException)
        {
            return new(AlertCenterSnapshotCompositionStatus.StoreUnavailable);
        }
    }

    private static void AddSurface(string? value, ICollection<string> target, ref bool missing)
    {
        if (value is null) missing = true;
        else target.Add(value);
    }

    private static bool TryTimestamp(string? value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out timestamp);

    private static bool IsToken(string? value) => value is { Length: >= 1 and <= 128 }
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && value.All(character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9'
            or '.' or '_' or '-');

    private static bool IsOpaqueId(string? value) => value is { Length: >= 1 and <= 256 }
        && !value.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character is '/' or '\\' or '?' or '#');

    private static AlertCompleteness Completeness(SessionCompleteness value) => value switch
    {
        SessionCompleteness.Unbound => AlertCompleteness.Unbound,
        SessionCompleteness.Partial => AlertCompleteness.Partial,
        SessionCompleteness.Rich => AlertCompleteness.Rich,
        SessionCompleteness.Full => AlertCompleteness.Full,
        _ => AlertCompleteness.Unbound,
    };

    private sealed record SignalDraft(
        string SignalId,
        AlertSignalKind Kind,
        DateTimeOffset ObservedAt,
        AlertEvidenceReference Evidence);
}

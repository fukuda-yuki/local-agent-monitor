using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Sessions;

internal sealed class SessionEventNormalizer
{
    private readonly ISessionStore store;
    private readonly TimeProvider timeProvider;

    public SessionEventNormalizer(ISessionStore store, TimeProvider? timeProvider = null)
    {
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void NormalizeAndWrite(SessionIngestEnvelope envelope)
    {
        var inputEvents = envelope.Events!;
        var persistedInputEvents = inputEvents.Where(item => !IsReasoningOrDelta(item.Type!)).ToArray();
        var surface = SessionWire.ParseSourceSurface(envelope.SourceSurface!);
        var nativeResolution = store.Resolve(surface, envelope.NativeSessionId!);
        var explicitResolution = envelope.ExplicitLink is null
            ? null
            : store.Resolve(SessionWire.ParseSourceSurface(envelope.ExplicitLink.SourceSurface!), envelope.ExplicitLink.NativeSessionId!);
        var resolved = explicitResolution ?? nativeResolution;
        var sessionId = resolved?.SessionId ?? Guid.CreateVersion7();
        var existing = resolved is null ? null : store.GetDetail(sessionId);
        var now = timeProvider.GetUtcNow();
        var lastSeen = inputEvents.Max(item => item.OccurredAt);
        var allTypes = (existing?.Events.Select(item => item.Type) ?? []).Concat(persistedInputEvents.Select(item => item.Type!)).ToArray();
        var hasStart = allTypes.Any(IsLifecycleStart);
        var hasInstruction = allTypes.Any(IsUserInstruction);
        var hasTerminal = allTypes.Any(IsTerminal);
        var hasUnsupported = (existing?.Events.Any(item => item.ContentState == SessionContentState.Unsupported) ?? false)
            || persistedInputEvents.Any(item => !IsSupported(item.Type!));
        var hasExactOtel = existing?.Events.Any(item => item.SourceAdapter == "otel-exact") ?? false;
        var hasGap = (existing?.Events.Any(item => item.Status == "gap_before_capture") ?? false)
            || inputEvents.Any(HasGapBeforeCapture);
        var completeness = SessionCompletenessCalculator.Calculate(new(
            HasNativeId: true,
            HasLifecycleStart: hasStart,
            HasUserInstruction: hasInstruction,
            HasSdkHookOrOtelEvidence: true,
            HasTerminalEvidence: hasTerminal,
            HasExactLinkedOtelEnrichment: hasExactOtel,
            HasAllSurfaceRequiredEvidence: hasStart && hasInstruction && hasTerminal,
            HasUnsupportedVersion: hasUnsupported,
            HasIngestGap: !hasStart || hasGap));
        var session = new ObservedSession(
            sessionId,
            hasTerminal ? ObservedSessionStatus.Completed : ObservedSessionStatus.Active,
            completeness,
            resolved?.Repository,
            resolved?.Workspace,
            existing?.Session.StartedAt ?? persistedInputEvents.Where(item => IsLifecycleStart(item.Type!)).Select(item => (DateTimeOffset?)item.OccurredAt).Min(),
            hasTerminal ? persistedInputEvents.Where(item => IsTerminal(item.Type!)).Select(item => (DateTimeOffset?)item.OccurredAt).Max() ?? resolved?.EndedAt : resolved?.EndedAt,
            resolved is null || lastSeen > resolved.LastSeenAt ? lastSeen : resolved.LastSeenAt,
            SessionRawRetentionState.Expiring,
            resolved?.CreatedAt ?? now,
            now);

        var runsByNativeId = (existing?.Runs ?? [])
            .Where(run => run.NativeRunId is not null && run.SourceSurface == surface)
            .ToDictionary(run => run.NativeRunId!, StringComparer.Ordinal);
        foreach (var nativeRunId in persistedInputEvents.Select(item => item.RunNativeId).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
        {
            runsByNativeId.TryAdd(nativeRunId!, new ObservedSessionRun(
                Guid.CreateVersion7(), sessionId, surface, nativeRunId, null, null, null,
                ObservedSessionStatus.Unknown, null, null, null, null, null));
        }

        var knownEventIds = (existing?.Events ?? [])
            .Where(item => string.Equals(item.SourceAdapter, envelope.SourceAdapter, StringComparison.Ordinal))
            .ToDictionary(item => item.SourceEventId, item => item.EventId, StringComparer.Ordinal);
        var existingSourceIds = knownEventIds.Keys.ToHashSet(StringComparer.Ordinal);
        foreach (var item in persistedInputEvents)
        {
            knownEventIds.TryAdd(item.SourceEventId!, Guid.CreateVersion7());
        }

        var observedEvents = persistedInputEvents.Select(item => new ObservedSessionEvent(
            knownEventIds[item.SourceEventId!],
            sessionId,
            item.RunNativeId is not null && runsByNativeId.TryGetValue(item.RunNativeId, out var run) ? run.RunId : null,
            surface,
            item.ParentEventId is not null && knownEventIds.TryGetValue(item.ParentEventId, out var parentId) ? parentId : null,
            item.TraceId,
            HasGapBeforeCapture(item) ? "gap_before_capture" : null,
            envelope.SourceAdapter!,
            item.SourceEventId!,
            item.Type!,
            item.OccurredAt,
            !IsSupported(item.Type!) ? SessionContentState.Unsupported
                : IsUsage(item.Type!) ? SessionContentState.NotCaptured
                : SessionContentState.Available,
            envelope.SourceApplicationVersion,
            envelope.AdapterVersion,
            envelope.SchemaFingerprint,
            envelope.NormalizationVersion)).ToArray();
        var contents = persistedInputEvents.Where(item => !IsUsage(item.Type!) && IsSupported(item.Type!)).Select(item => new SessionEventContent(
            knownEventIds[item.SourceEventId!],
            "application/json",
            SessionSecretFilter.Filter(item.Payload),
            now,
            now.AddDays(90))).ToArray();

        store.Write(new SessionWriteBatch(
            new SessionDetail(
                session,
                [new SessionNativeId(
                    sessionId,
                    surface,
                    envelope.NativeSessionId!,
                    envelope.ExplicitLink?.Kind switch
                    {
                        "resume" => SessionBindingKind.ExplicitResume,
                        "handoff" => SessionBindingKind.ExplicitHandoff,
                        _ => SessionBindingKind.Native,
                    },
                    lastSeen)],
                runsByNativeId.Values.Where(run => existing?.Runs.All(old => old.RunId != run.RunId) ?? true).ToArray(),
                observedEvents),
            contents));

        var newUnsupportedCount = persistedInputEvents.Count(item => !IsSupported(item.Type!) && !existingSourceIds.Contains(item.SourceEventId!));
        if (newUnsupportedCount > 0)
        {
            var state = store.GetProjectionState("session-normalizer");
            store.UpsertProjectionState(new(
                "session-normalizer",
                state?.ProjectionCursor,
                checked((state?.UnsupportedEventVersionCount ?? 0) + newUnsupportedCount),
                now));
        }
    }

    private static bool IsLifecycleStart(string type) => type is "session.start" or "SessionStart";
    private static bool IsUserInstruction(string type) => type is "user.message" or "UserPromptSubmit" or "userPromptSubmitted";
    private static bool IsTerminal(string type) => type is "session.shutdown" or "session.task_complete" or "SessionEnd" or "Stop";
    private static bool IsUsage(string type) => type is "assistant.usage" or "session.usage_info";
    private static bool IsSupported(string type) => type is
        "capture.started" or "assistant.usage" or "session.usage_info"
        or "session.start" or "session.started" or "session.shutdown" or "session.task_complete"
        or "user.message" or "assistant.message" or "assistant.turn_end"
        or "tool.execution_start" or "tool.execution_complete"
        or "subagent.started" or "subagent.completed" or "skill.started" or "skill.completed"
        or "SessionStart" or "UserPromptSubmit" or "PreToolUse" or "PermissionRequest"
        or "PostToolUse" or "PostToolUseFailure" or "SubagentStart" or "SubagentStop"
        or "Stop" or "StopFailure" or "SessionEnd";
    private static bool IsReasoningOrDelta(string type) => type.Contains("reasoning", StringComparison.OrdinalIgnoreCase)
        || type.EndsWith("_delta", StringComparison.Ordinal)
        || type is "assistant.streaming_delta" or "tool.execution_partial_result" or "tool.execution_progress";

    private static bool HasGapBeforeCapture(SessionIngestEvent item) => item.Type == "capture.started"
        && item.Payload.ValueKind == JsonValueKind.Object
        && item.Payload.TryGetProperty("gap_before_capture", out var gap)
        && gap.ValueKind == JsonValueKind.True;
}

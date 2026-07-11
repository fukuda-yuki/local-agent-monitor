namespace CopilotAgentObservability.Telemetry.Sessions;

public enum SessionCompleteness { Unbound, Partial, Rich, Full }
public enum ObservedSessionStatus { Active, Completed, Failed, Unknown }
public enum SessionSourceSurface { CopilotSdk, CopilotCli, VisualStudioCode, HookUnknown }
public enum SessionBindingKind { Native, ExplicitResume, ExplicitHandoff, TraceContext }
public enum SessionContentState { Available, NotCaptured, Redacted, Unsupported, ExpiredPendingDeletion }
public enum SessionRawRetentionState { Expiring, ExpiredPendingDeletion, NotCaptured }

public static class SessionWire
{
    public static string ToWire(SessionCompleteness value) => value switch
    {
        SessionCompleteness.Unbound => "unbound",
        SessionCompleteness.Partial => "partial",
        SessionCompleteness.Rich => "rich",
        SessionCompleteness.Full => "full",
        _ => throw Invalid(value),
    };

    public static string ToWire(ObservedSessionStatus value) => value switch
    {
        ObservedSessionStatus.Active => "active",
        ObservedSessionStatus.Completed => "completed",
        ObservedSessionStatus.Failed => "failed",
        ObservedSessionStatus.Unknown => "unknown",
        _ => throw Invalid(value),
    };

    public static string ToWire(SessionSourceSurface value) => value switch
    {
        SessionSourceSurface.CopilotSdk => "copilot-sdk",
        SessionSourceSurface.CopilotCli => "copilot-cli",
        SessionSourceSurface.VisualStudioCode => "vscode",
        SessionSourceSurface.HookUnknown => "hook-unknown",
        _ => throw Invalid(value),
    };

    public static string ToWire(SessionBindingKind value) => value switch
    {
        SessionBindingKind.Native => "native",
        SessionBindingKind.ExplicitResume => "explicit_resume",
        SessionBindingKind.ExplicitHandoff => "explicit_handoff",
        SessionBindingKind.TraceContext => "trace_context",
        _ => throw Invalid(value),
    };

    public static string ToWire(SessionContentState value) => value switch
    {
        SessionContentState.Available => "available",
        SessionContentState.NotCaptured => "not_captured",
        SessionContentState.Redacted => "redacted",
        SessionContentState.Unsupported => "unsupported",
        SessionContentState.ExpiredPendingDeletion => "expired_pending_deletion",
        _ => throw Invalid(value),
    };

    public static string ToWire(SessionRawRetentionState value) => value switch
    {
        SessionRawRetentionState.Expiring => "expiring",
        SessionRawRetentionState.ExpiredPendingDeletion => "expired_pending_deletion",
        SessionRawRetentionState.NotCaptured => "not_captured",
        _ => throw Invalid(value),
    };

    public static SessionCompleteness ParseCompleteness(string value) => value switch
    {
        "unbound" => SessionCompleteness.Unbound,
        "partial" => SessionCompleteness.Partial,
        "rich" => SessionCompleteness.Rich,
        "full" => SessionCompleteness.Full,
        _ => throw Invalid(value),
    };

    public static ObservedSessionStatus ParseStatus(string value) => value switch
    {
        "active" => ObservedSessionStatus.Active,
        "completed" => ObservedSessionStatus.Completed,
        "failed" => ObservedSessionStatus.Failed,
        "unknown" => ObservedSessionStatus.Unknown,
        _ => throw Invalid(value),
    };

    public static SessionSourceSurface ParseSourceSurface(string value) => value switch
    {
        "copilot-sdk" => SessionSourceSurface.CopilotSdk,
        "copilot-cli" => SessionSourceSurface.CopilotCli,
        "vscode" => SessionSourceSurface.VisualStudioCode,
        "hook-unknown" => SessionSourceSurface.HookUnknown,
        _ => throw Invalid(value),
    };

    public static SessionBindingKind ParseBindingKind(string value) => value switch
    {
        "native" => SessionBindingKind.Native,
        "explicit_resume" => SessionBindingKind.ExplicitResume,
        "explicit_handoff" => SessionBindingKind.ExplicitHandoff,
        "trace_context" => SessionBindingKind.TraceContext,
        _ => throw Invalid(value),
    };

    public static SessionContentState ParseContentState(string value) => value switch
    {
        "available" => SessionContentState.Available,
        "not_captured" => SessionContentState.NotCaptured,
        "redacted" => SessionContentState.Redacted,
        "unsupported" => SessionContentState.Unsupported,
        "expired_pending_deletion" => SessionContentState.ExpiredPendingDeletion,
        _ => throw Invalid(value),
    };

    public static SessionRawRetentionState ParseRawRetentionState(string value) => value switch
    {
        "expiring" => SessionRawRetentionState.Expiring,
        "expired_pending_deletion" => SessionRawRetentionState.ExpiredPendingDeletion,
        "not_captured" => SessionRawRetentionState.NotCaptured,
        _ => throw Invalid(value),
    };

    private static ArgumentException Invalid<T>(T value) =>
        new($"Unsupported Session wire value: {value}.", nameof(value));
}

public sealed record SessionCompletenessEvidence(
    bool HasNativeId,
    bool HasLifecycleStart,
    bool HasUserInstruction,
    bool HasSdkHookOrOtelEvidence,
    bool HasTerminalEvidence,
    bool HasExactLinkedOtelEnrichment,
    bool HasAllSurfaceRequiredEvidence,
    bool HasUnsupportedVersion,
    bool HasIngestGap);

public static class SessionCompletenessCalculator
{
    public static SessionCompleteness Calculate(SessionCompletenessEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!evidence.HasNativeId)
        {
            return SessionCompleteness.Unbound;
        }

        if (!evidence.HasLifecycleStart || !evidence.HasUserInstruction)
        {
            return SessionCompleteness.Partial;
        }

        if (!evidence.HasSdkHookOrOtelEvidence)
        {
            return SessionCompleteness.Partial;
        }

        return evidence.HasTerminalEvidence
            && evidence.HasExactLinkedOtelEnrichment
            && evidence.HasAllSurfaceRequiredEvidence
            && !evidence.HasUnsupportedVersion
            && !evidence.HasIngestGap
                ? SessionCompleteness.Full
                : SessionCompleteness.Rich;
    }
}

public sealed record ObservedSession(
    Guid SessionId,
    ObservedSessionStatus Status,
    SessionCompleteness Completeness,
    string? Repository,
    string? Workspace,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    DateTimeOffset LastSeenAt,
    SessionRawRetentionState RawRetentionState,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public static ObservedSession Create(
        ObservedSessionStatus status,
        SessionCompleteness completeness,
        string? repository,
        string? workspace,
        DateTimeOffset? startedAt,
        DateTimeOffset? endedAt,
        DateTimeOffset lastSeenAt,
        SessionRawRetentionState rawRetentionState)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.CreateVersion7(), status, completeness, repository, workspace, startedAt, endedAt, lastSeenAt, rawRetentionState, now, now);
    }
}

public sealed record SessionNativeId(
    Guid SessionId,
    SessionSourceSurface SourceSurface,
    string NativeSessionId,
    SessionBindingKind BindingKind,
    DateTimeOffset ObservedAt);

public sealed record ObservedSessionRun(
    Guid RunId,
    Guid SessionId,
    SessionSourceSurface? SourceSurface,
    string? NativeRunId,
    string? TraceId,
    Guid? ParentRunId,
    string? Model,
    ObservedSessionStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens)
{
    public static ObservedSessionRun Create(Guid sessionId, ObservedSessionStatus status) =>
        new(Guid.CreateVersion7(), sessionId, null, null, null, null, null, status, null, null, null, null, null);
}

public sealed record ObservedSessionEvent(
    Guid EventId,
    Guid SessionId,
    Guid? RunId,
    SessionSourceSurface? SourceSurface,
    Guid? ParentEventId,
    string? TraceId,
    string? Status,
    string SourceAdapter,
    string SourceEventId,
    string Type,
    DateTimeOffset OccurredAt,
    SessionContentState ContentState)
{
    public static ObservedSessionEvent Create(
        Guid sessionId,
        Guid? runId,
        string sourceAdapter,
        string sourceEventId,
        string type,
        DateTimeOffset occurredAt,
        SessionContentState contentState) =>
        new(Guid.CreateVersion7(), sessionId, runId, null, null, null, null, sourceAdapter, sourceEventId, type, occurredAt, contentState);
}

public sealed record SessionEventContent(
    Guid EventId,
    string ContentKind,
    string ContentJson,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt);

public sealed record SessionProjectionState(
    string ProjectorKey,
    long? ProjectionCursor,
    long UnsupportedEventVersionCount,
    DateTimeOffset UpdatedAt);

public sealed record SessionDetail(
    ObservedSession Session,
    IReadOnlyList<SessionNativeId> NativeIds,
    IReadOnlyList<ObservedSessionRun> Runs,
    IReadOnlyList<ObservedSessionEvent> Events);

public sealed record SessionWriteBatch(SessionDetail Detail, IReadOnlyList<SessionEventContent> Content);

public sealed record SessionContentLookup(SessionContentState State, SessionEventContent? Content);

public sealed record SessionHumanEvaluation(Guid SessionId, string Verdict, DateTimeOffset RecordedAt);

public interface ISessionStore
{
    void CreateSchema();
    void Write(SessionWriteBatch batch);
    ObservedSession? Resolve(SessionSourceSurface sourceSurface, string nativeSessionId);
    IReadOnlyList<ObservedSession> ListMostRecent(int limit);
    SessionDetail? GetDetail(Guid sessionId);
    SessionHumanEvaluation? GetHumanEvaluation(Guid sessionId);
    void UpsertHumanEvaluation(SessionHumanEvaluation evaluation);
    void ClearHumanEvaluation(Guid sessionId);
    SessionContentLookup? GetContent(Guid sessionId, Guid eventId);
    SessionRawRetentionState GetRawRetentionState(Guid sessionId);
    SessionProjectionState? GetProjectionState(string projectorKey);
    void UpsertProjectionState(SessionProjectionState state);
}

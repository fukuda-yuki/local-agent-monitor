namespace CopilotAgentObservability.Telemetry.Sessions;

using System.Text.Json;

public enum SessionCompleteness { Unbound, Partial, Rich, Full }
public enum ObservedSessionStatus { Active, Completed, Failed, Unknown }
public enum SessionSourceSurface { CopilotSdk, CopilotCli, VisualStudioCode, HookUnknown, ClaudeCode }
public enum SessionSourceAdapter { CopilotSdkStream, CopilotCompatibleHook, ClaudeCodeOtel, ClaudeCodeHook }
public enum SessionBindingKind { Native, ExplicitResume, ExplicitHandoff, TraceContext }
public enum SessionMatchKind { ExactNative, ExplicitLink, TraceContinuity, ConversationId, None }
public enum SessionContentState { Available, NotCaptured, Redacted, Unsupported, ExpiredPendingDeletion }
public enum SessionRawRetentionState { Expiring, ExpiredPendingDeletion, NotCaptured }
public enum ImprovementProposalStatus { Candidate, Recommended, Verified }
public enum ProposalApplyState { Draft, Approved, Applied, RolledBack, Failed }

internal enum ImprovementProposalFailure
{
    InvalidShape,
    EvidenceNotFound,
    EvidenceNotExactBound,
    InsufficientRecommendationEvidence,
    RecommendationAlreadyExists,
    ProposalNotFound,
    VerificationOwnedByComparison,
    InvalidStatus,
}

internal sealed class ImprovementProposalStoreException(ImprovementProposalFailure failure) : InvalidOperationException("Improvement proposal mutation was rejected.")
{
    internal ImprovementProposalFailure Failure { get; } = failure;
}

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
        SessionSourceSurface.ClaudeCode => "claude-code",
        _ => throw Invalid(value),
    };

    public static string ToWire(SessionSourceAdapter value) => value switch
    {
        SessionSourceAdapter.CopilotSdkStream => "copilot-sdk-stream",
        SessionSourceAdapter.CopilotCompatibleHook => "copilot-compatible-hook",
        SessionSourceAdapter.ClaudeCodeOtel => "claude-code-otel",
        SessionSourceAdapter.ClaudeCodeHook => "claude-code-hook",
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
        "claude-code" => SessionSourceSurface.ClaudeCode,
        _ => throw Invalid(value),
    };

    public static SessionSourceAdapter ParseSourceAdapter(string value) => value switch
    {
        "copilot-sdk-stream" => SessionSourceAdapter.CopilotSdkStream,
        "copilot-compatible-hook" => SessionSourceAdapter.CopilotCompatibleHook,
        "claude-code-otel" => SessionSourceAdapter.ClaudeCodeOtel,
        "claude-code-hook" => SessionSourceAdapter.ClaudeCodeHook,
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
    SessionContentState ContentState,
    string? SourceApplicationVersion = null,
    string? AdapterVersion = null,
    string? SchemaFingerprint = null,
    string? NormalizationVersion = null,
    SessionMatchKind? MatchKind = null)
{
    public static ObservedSessionEvent Create(
        Guid sessionId,
        Guid? runId,
        string sourceAdapter,
        string sourceEventId,
        string type,
        DateTimeOffset occurredAt,
        SessionContentState contentState,
        string? sourceApplicationVersion = null,
        string? adapterVersion = null,
        string? schemaFingerprint = null,
        string? normalizationVersion = null) =>
        new(
            Guid.CreateVersion7(), sessionId, runId, null, null, null, null,
            sourceAdapter, sourceEventId, type, occurredAt, contentState,
            sourceApplicationVersion, adapterVersion, schemaFingerprint, normalizationVersion);
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

internal enum SessionTerminalOutcome { Clean, Failed, Neutral }

internal sealed record SessionTerminalFact(Guid EventId, SessionTerminalOutcome Outcome, int PolicyVersion = 1);
internal sealed record SessionReplayContentCandidate(Guid EventId, string ContentKind, string ContentJson);

internal interface IClassifiedSessionStore
{
    void WriteClassified(
        SessionWriteBatch batch,
        IReadOnlyList<SessionTerminalFact> terminalFacts,
        IReadOnlyList<SessionReplayContentCandidate>? replayContentCandidates = null);
}

internal interface ICurrentSessionEligibilityStore
{
    bool IsCurrentSessionEligible(Guid sessionId);
}

internal static class SessionTerminalPolicyV1
{
    internal static SessionTerminalFact? Classify(
        Guid eventId,
        string sourceAdapter,
        SessionSourceSurface? sourceSurface,
        string type,
        JsonElement payload)
    {
        if (sourceAdapter == "copilot-sdk-stream" && sourceSurface == SessionSourceSurface.CopilotSdk)
        {
            if (type == "session.task_complete") return new(eventId, SessionTerminalOutcome.Clean);
            if (type == "session.shutdown")
                return new(eventId, RootString(payload, "shutdownType") switch
                {
                    "routine" => SessionTerminalOutcome.Clean,
                    "error" => SessionTerminalOutcome.Failed,
                    _ => SessionTerminalOutcome.Neutral,
                });
        }
        if (sourceAdapter == "copilot-compatible-hook"
            && sourceSurface is SessionSourceSurface.CopilotCli or SessionSourceSurface.VisualStudioCode or SessionSourceSurface.HookUnknown
            && type == "SessionEnd")
            return new(eventId, RootString(payload, "reason") switch
            {
                "complete" or "user_exit" => SessionTerminalOutcome.Clean,
                "error" or "timeout" => SessionTerminalOutcome.Failed,
                _ => SessionTerminalOutcome.Neutral,
            });
        if (sourceAdapter == "claude-code-hook" && sourceSurface == SessionSourceSurface.ClaudeCode && type == "SessionEnd")
        {
            var reason = RootString(payload, "reason", required: true);
            return new(eventId, reason is "clear" or "resume" or "logout" or "prompt_input_exit"
                ? SessionTerminalOutcome.Clean
                : SessionTerminalOutcome.Neutral);
        }
        return null;
    }

    private static string? RootString(JsonElement payload, string name, bool required = false)
    {
        if (payload.ValueKind == JsonValueKind.Object)
        {
            JsonElement found = default;
            var count = 0;
            foreach (var property in payload.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.Ordinal)) continue;
                found = property.Value;
                count++;
            }
            if (count == 1 && found.ValueKind == JsonValueKind.String) return found.GetString();
        }
        if (required) throw new InvalidOperationException("Invalid Claude SessionEnd terminal discriminator.");
        return null;
    }
}

public enum SessionContentReadDisposition { Granted, NotFound, Denied, Busy }

public sealed class SessionContentReadLease : IAsyncDisposable
{
    private readonly Func<ValueTask> release;
    private readonly Func<SessionContentUseReference> acquire;
    private readonly Func<SessionContentTerminalResult> sealRawResponse;
    private readonly Func<SessionContentTerminalResult> completeWithoutRaw;
    private int released;

    internal SessionContentReadLease(
        Func<ValueTask> release,
        Func<SessionContentUseReference> acquire,
        Func<SessionContentTerminalResult> sealRawResponse,
        Func<SessionContentTerminalResult> completeWithoutRaw)
    {
        this.release = release ?? throw new ArgumentNullException(nameof(release));
        this.acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        this.sealRawResponse = sealRawResponse ?? throw new ArgumentNullException(nameof(sealRawResponse));
        this.completeWithoutRaw = completeWithoutRaw ?? throw new ArgumentNullException(nameof(completeWithoutRaw));
    }

    internal SessionContentUseReference AcquireContentReference() => acquire();

    internal SessionContentTerminalResult TrySealRawResponse() => sealRawResponse();

    internal SessionContentTerminalResult TryCompleteWithoutRaw() => completeWithoutRaw();

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref released, 1) == 0 ? release() : ValueTask.CompletedTask;
}

internal enum SessionContentTerminalResult { Sealed, CompletedWithoutRaw, Lost, Busy }

internal sealed class SessionContentUseReference : IDisposable
{
    private Func<SessionEventContent>? read;
    private Action? releaseReference;

    internal SessionContentUseReference(Func<SessionEventContent> read, Action release)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        releaseReference = release ?? throw new ArgumentNullException(nameof(release));
    }

    internal SessionEventContent Content =>
        (Volatile.Read(ref read) ?? throw new ObjectDisposedException(nameof(SessionContentUseReference)))();

    public void Dispose()
    {
        Interlocked.Exchange(ref read, null);
        Interlocked.Exchange(ref releaseReference, null)?.Invoke();
    }
}

public sealed record SessionContentReadResult(SessionContentReadDisposition Disposition, SessionContentReadLease? Lease);

public sealed record SessionHumanEvaluation(Guid SessionId, string Verdict, DateTimeOffset RecordedAt);

public sealed record ImprovementProposalEvidenceReference(
    string Kind,
    string ReferenceId);

public sealed record ImprovementProposal(
    Guid ProposalId,
    int Revision,
    ImprovementProposalStatus Status,
    string TargetKind,
    string TargetLabel,
    string Title,
    string Summary,
    string ExpectedEffect,
    string RiskNote,
    IReadOnlyList<Guid> SourceSessionIds,
    IReadOnlyList<ImprovementProposalEvidenceReference> EvidenceReferences,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? RecommendedAt,
    DateTimeOffset? VerifiedAt);

public sealed record ProposalApplyAudit(
    Guid ApplyId,
    Guid ProposalId,
    Guid DraftId,
    Guid RootId,
    ProposalApplyState State,
    int FileCount,
    DateTimeOffset RecordedAt);

public sealed record ProposalApplyDraftMetadata(
    Guid DraftId, Guid ProposalId, int ProposalRevision, Guid RootId, int SelectionRevision, string ApprovalDigest,
    ProposalApplyState State, int FileCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record ProposalApplyRevisionMetadata(Guid DraftId, int SelectionRevision, string ApprovalDigest, DateTimeOffset? ApprovedAt);
public sealed record ProposalApplyImmutableMetadata(
    ProposalApplyDraftMetadata Draft,
    ProposalApplyRevisionMetadata Revision,
    IReadOnlyList<(string BaseSha256, string ReplacementSha256)> Files,
    IReadOnlyList<(string HunkId, bool Selected, string ReplacementSha256)> Hunks);

public sealed record ProposalApplyOutcome(Guid ApplyId, Guid DraftId, ProposalApplyState State, DateTimeOffset RecordedAt);
public sealed record ProposalApplyPendingOperation(Guid ApplyId, Guid DraftId, Guid ProposalId, Guid RootId, int FileCount, string OperationKind, DateTimeOffset RecordedAt);
public sealed record ProposalApplyLinkage(Guid ApplyId, Guid DraftId, Guid ProposalId, int ProposalRevision, Guid RootId, int FileCount, int SelectionRevision, string ApprovalDigest);
public sealed record ProposalApplicationReceipt(Guid ApplyId, Guid DraftId, Guid ProposalId, int ProposalRevision, int SelectionRevision, DateTimeOffset AppliedAt, int FileCount, string State, string CurrentState);

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
    void CreateObjectiveEvaluation(ObjectiveEvaluationReceipt receipt);
    IReadOnlyList<ObjectiveEvaluationReceipt> ListObjectiveEvaluations(Guid sessionId);
    EffectReceipt RecordEffectComparison(EffectComparisonRequest request, DateTimeOffset recordedAt);
    IReadOnlyList<EffectReceipt> ListEffectReceipts(Guid proposalId);
    EffectComparisonDetail? GetEffectComparison(Guid comparisonId);
    ValueTask<SessionContentReadResult> ReadContentAsync(Guid sessionId, Guid eventId, CancellationToken cancellationToken);
    SessionRawRetentionState GetRawRetentionState(Guid sessionId);
    SessionProjectionState? GetProjectionState(string projectorKey);
    void UpsertProjectionState(SessionProjectionState state);
    IReadOnlyList<ImprovementProposal> ListImprovementProposals(Guid sessionId);
    ImprovementProposal? GetImprovementProposal(Guid proposalId);
    void CreateImprovementProposal(ImprovementProposal proposal);
    ImprovementProposal UpdateImprovementProposalStatus(Guid proposalId, ImprovementProposalStatus status, DateTimeOffset updatedAt);
    void SaveProposalApplyDraft(ProposalApplyDraftMetadata draft, IReadOnlyList<(string BaseSha256, string ReplacementSha256)> files, IReadOnlyList<(string HunkId, bool Selected, string ReplacementSha256)> hunks, ProposalApplyRevisionMetadata revision);
    void UpdateProposalApplyDraft(ProposalApplyDraftMetadata draft, IReadOnlyList<(string BaseSha256, string ReplacementSha256)> files, IReadOnlyList<(string HunkId, bool Selected, string ReplacementSha256)> hunks, ProposalApplyRevisionMetadata revision);
    ProposalApplyDraftMetadata? GetProposalApplyDraft(Guid draftId);
    ProposalApplyImmutableMetadata? GetProposalApplyImmutableMetadata(Guid draftId);
    bool TryMigrateProposalApplyDigest(Guid draftId, int proposalRevision, int selectionRevision, string expectedOldDigest, string newDigest);
    IReadOnlyList<ProposalApplyDraftMetadata> ListActiveProposalApplyDrafts();
    void SaveProposalApplyApproval(Guid draftId, ProposalApplyRevisionMetadata revision);
    void SaveProposalApplyOutcome(ProposalApplyOutcome outcome, Guid proposalId, Guid rootId, int fileCount, string? errorCode);
    void SaveProposalApplyPending(ProposalApplyPendingOperation pending);
    bool TryAuthorizeProposalApply(ProposalApplyPendingOperation pending, int proposalRevision);
    IReadOnlyList<ProposalApplyPendingOperation> ListProposalApplyPending();
    IReadOnlyList<ProposalApplyLinkage> ListAppliedProposalApplyLinkages();
    IReadOnlyList<ProposalApplyLinkage> ListProposalApplyLinkages(Guid proposalId);
    IReadOnlyList<ProposalApplicationReceipt> ListApplicationReceipts(Guid proposalId);
    bool TryStartProposalApplyRollback(ProposalApplyPendingOperation pending);
    void CompleteProposalApplyPending(ProposalApplyOutcome outcome, Guid proposalId, Guid rootId, int fileCount, string? errorCode);
}

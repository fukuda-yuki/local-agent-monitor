using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SessionDomainTests
{
    public static TheoryData<SessionCompleteness, string> CompletenessValues => new()
    {
        { SessionCompleteness.Unbound, "unbound" },
        { SessionCompleteness.Partial, "partial" },
        { SessionCompleteness.Rich, "rich" },
        { SessionCompleteness.Full, "full" },
    };

    public static TheoryData<ObservedSessionStatus, string> StatusValues => new()
    {
        { ObservedSessionStatus.Active, "active" },
        { ObservedSessionStatus.Completed, "completed" },
        { ObservedSessionStatus.Failed, "failed" },
        { ObservedSessionStatus.Unknown, "unknown" },
    };

    public static TheoryData<SessionSourceSurface, string> SourceSurfaceValues => new()
    {
        { SessionSourceSurface.CopilotSdk, "copilot-sdk" },
        { SessionSourceSurface.CopilotCli, "copilot-cli" },
        { SessionSourceSurface.VisualStudioCode, "vscode" },
        { SessionSourceSurface.HookUnknown, "hook-unknown" },
    };

    public static TheoryData<SessionBindingKind, string> BindingKindValues => new()
    {
        { SessionBindingKind.Native, "native" },
        { SessionBindingKind.ExplicitResume, "explicit_resume" },
        { SessionBindingKind.ExplicitHandoff, "explicit_handoff" },
        { SessionBindingKind.TraceContext, "trace_context" },
    };

    public static TheoryData<SessionContentState, string> ContentStateValues => new()
    {
        { SessionContentState.Available, "available" },
        { SessionContentState.NotCaptured, "not_captured" },
        { SessionContentState.Redacted, "redacted" },
        { SessionContentState.Unsupported, "unsupported" },
        { SessionContentState.ExpiredPendingDeletion, "expired_pending_deletion" },
    };

    public static TheoryData<SessionRawRetentionState, string> RawRetentionStateValues => new()
    {
        { SessionRawRetentionState.Expiring, "expiring" },
        { SessionRawRetentionState.ExpiredPendingDeletion, "expired_pending_deletion" },
        { SessionRawRetentionState.NotCaptured, "not_captured" },
    };

    [Theory]
    [MemberData(nameof(CompletenessValues))]
    public void CompletenessWireMapping_IsExplicitAndStrict(SessionCompleteness value, string wire)
    {
        Assert.Equal(wire, SessionWire.ToWire(value));
        Assert.Equal(value, SessionWire.ParseCompleteness(wire));
        Assert.Throws<ArgumentException>(() => SessionWire.ParseCompleteness(wire.ToUpperInvariant()));
    }

    [Theory]
    [MemberData(nameof(StatusValues))]
    public void StatusWireMapping_IsExplicitAndStrict(ObservedSessionStatus value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseStatus, value, wire);

    [Theory]
    [MemberData(nameof(SourceSurfaceValues))]
    public void SourceSurfaceWireMapping_IsExplicitAndStrict(SessionSourceSurface value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseSourceSurface, value, wire);

    [Theory]
    [MemberData(nameof(BindingKindValues))]
    public void BindingKindWireMapping_IsExplicitAndStrict(SessionBindingKind value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseBindingKind, value, wire);

    [Theory]
    [MemberData(nameof(ContentStateValues))]
    public void ContentStateWireMapping_IsExplicitAndStrict(SessionContentState value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseContentState, value, wire);

    [Theory]
    [MemberData(nameof(RawRetentionStateValues))]
    public void RawRetentionStateWireMapping_IsExplicitAndStrict(SessionRawRetentionState value, string wire) =>
        AssertStrict(SessionWire.ToWire, SessionWire.ParseRawRetentionState, value, wire);

    [Fact]
    public void WireFormatting_RejectsUndefinedEnumMembers()
    {
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionCompleteness)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((ObservedSessionStatus)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionSourceSurface)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionBindingKind)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionContentState)int.MaxValue));
        Assert.Throws<ArgumentException>(() => SessionWire.ToWire((SessionRawRetentionState)int.MaxValue));
    }

    [Theory]
    [InlineData(false, false, false, false, false, false, false, false, false, SessionCompleteness.Unbound)]
    [InlineData(true, false, true, true, true, true, true, false, false, SessionCompleteness.Partial)]
    [InlineData(true, true, false, true, true, true, true, false, false, SessionCompleteness.Partial)]
    [InlineData(true, true, true, false, true, true, true, false, false, SessionCompleteness.Partial)]
    [InlineData(true, true, true, true, false, true, true, false, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, false, true, false, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, false, false, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, true, true, false, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, true, false, true, SessionCompleteness.Rich)]
    [InlineData(true, true, true, true, true, true, true, false, false, SessionCompleteness.Full)]
    public void Completeness_IsRecomputedFromExplicitEvidence(
        bool hasNativeId,
        bool hasLifecycleStart,
        bool hasUserInstruction,
        bool hasEvidenceFamily,
        bool hasTerminalEvidence,
        bool hasExactLinkedOtelEnrichment,
        bool hasAllSurfaceRequiredEvidence,
        bool hasUnsupportedVersion,
        bool hasIngestGap,
        SessionCompleteness expected)
    {
        var evidence = new SessionCompletenessEvidence(
            hasNativeId,
            hasLifecycleStart,
            hasUserInstruction,
            hasEvidenceFamily,
            hasTerminalEvidence,
            hasExactLinkedOtelEnrichment,
            hasAllSurfaceRequiredEvidence,
            hasUnsupportedVersion,
            hasIngestGap);

        Assert.Equal(expected, SessionCompletenessCalculator.Calculate(evidence));
    }

    [Fact]
    public void LocalIds_AreUuidVersion7()
    {
        var session = ObservedSession.Create(
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            repository: null,
            workspace: null,
            startedAt: null,
            endedAt: null,
            lastSeenAt: DateTimeOffset.UnixEpoch,
            SessionRawRetentionState.NotCaptured);
        var run = ObservedSessionRun.Create(session.SessionId, status: ObservedSessionStatus.Active);
        var @event = ObservedSessionEvent.Create(
            session.SessionId,
            run.RunId,
            sourceAdapter: "copilot-sdk-stream",
            sourceEventId: "event-1",
            type: "session.started",
            occurredAt: DateTimeOffset.UnixEpoch,
            SessionContentState.NotCaptured);

        Assert.Equal(7, session.SessionId.Version);
        Assert.Equal(7, run.RunId.Version);
        Assert.Equal(7, @event.EventId.Version);
        Assert.Equal(session.SessionId.ToString("D"), session.SessionId.ToString());
    }

    private static void AssertStrict<T>(Func<T, string> format, Func<string, T> parse, T value, string wire)
        where T : struct, Enum
    {
        Assert.Equal(wire, format(value));
        Assert.Equal(value, parse(wire));
        Assert.Throws<ArgumentException>(() => parse(wire.ToUpperInvariant()));
        Assert.Throws<ArgumentException>(() => parse("unknown-value"));
    }
}

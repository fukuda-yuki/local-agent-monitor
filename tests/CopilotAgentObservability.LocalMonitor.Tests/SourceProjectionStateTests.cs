using CopilotAgentObservability.LocalMonitor.SourceCompatibility;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceProjectionStateTests
{
    [Fact]
    public void Binding_requires_persisted_exact_otel_event_and_does_not_promote_trace_only_observation()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            null,
            null,
            now,
            null,
            now,
            SessionRawRetentionState.NotCaptured,
            now,
            now);
        var native = new SessionNativeId(sessionId, SessionSourceSurface.HookUnknown, "native-1", SessionBindingKind.Native, now);
        var hook = new ObservedSessionEvent(
            Guid.CreateVersion7(),
            sessionId,
            null,
            SessionSourceSurface.HookUnknown,
            null,
            "trace-shared",
            null,
            "claude-code-hook",
            "hook-1",
            "SessionStart",
            now,
            SessionContentState.NotCaptured);
        var observation = new SourceCompatibilityRow(
            1,
            "observation-1",
            1,
            "batch-1",
            "claude-code",
            "1.0",
            "claude-code-otel",
            "1",
            "sha256:fingerprint",
            "sha256:inventory",
            SourceCompatibilityState.Supported,
            [],
            SourceCompatibilityNextActions.None,
            SourceCaptureContentState.NotCaptured,
            0,
            0,
            0,
            0,
            0,
            now,
            []);

        var hookOnly = SourceProjectionStateBuilder.Build(
            [observation],
            new SessionDetail(session, [native], [], [hook]));
        Assert.Equal("hook_only", hookOnly.BindingState);

        var exactOtel = hook with
        {
            SourceAdapter = "claude-code-otel",
            SourceEventId = "trace-shared/span-1",
            Type = "otel.enrichment",
            MatchKind = SessionMatchKind.ExactNative,
        };
        var exactLinked = SourceProjectionStateBuilder.Build(
            [observation],
            new SessionDetail(session, [native], [], [hook, exactOtel]));
        Assert.Equal("exact_linked", exactLinked.BindingState);

        var otelOnly = SourceProjectionStateBuilder.Build([observation], null);
        Assert.Equal("otel_only", otelOnly.BindingState);
    }

    [Fact]
    public void Shared_trace_continuity_never_projects_as_exact_linked()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            null,
            null,
            now,
            null,
            now,
            SessionRawRetentionState.NotCaptured,
            now,
            now);
        var native = new SessionNativeId(sessionId, SessionSourceSurface.HookUnknown, "native-1", SessionBindingKind.Native, now);
        var events = new[]
        {
            new ObservedSessionEvent(
                Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.HookUnknown, null,
                "trace-shared", null, "claude-code-hook", "hook-1", "SessionStart", now,
                SessionContentState.NotCaptured),
            new ObservedSessionEvent(
                Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null,
                "trace-shared", null, "otel-exact", "trace-shared/span-1", "otel.span", now,
                SessionContentState.NotCaptured, MatchKind: SessionMatchKind.TraceContinuity),
        };

        var projection = SourceProjectionStateBuilder.Build([], new SessionDetail(session, [native], [], events));

        Assert.Equal("hook_only", projection.BindingState);
    }

    [Fact]
    public void Legacy_otel_exact_label_without_match_kind_never_projects_as_exact_linked()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            null,
            null,
            now,
            null,
            now,
            SessionRawRetentionState.NotCaptured,
            now,
            now);
        var native = new SessionNativeId(sessionId, SessionSourceSurface.HookUnknown, "native-1", SessionBindingKind.Native, now);
        var legacyOtel = new ObservedSessionEvent(
            Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null,
            "trace-legacy", null, "otel-exact", "trace-legacy/span-1", "otel.span", now,
            SessionContentState.NotCaptured);

        var projection = SourceProjectionStateBuilder.Build([], new SessionDetail(session, [native], [], [legacyOtel]));

        Assert.Equal("otel_only", projection.BindingState);
    }

    [Theory]
    [InlineData(SessionBindingKind.ExplicitResume)]
    [InlineData(SessionBindingKind.ExplicitHandoff)]
    public void Explicit_resume_or_handoff_match_kind_still_projects_as_exact_linked(SessionBindingKind bindingKind)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.CreateVersion7();
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            null,
            null,
            now,
            null,
            now,
            SessionRawRetentionState.NotCaptured,
            now,
            now);
        var native = new SessionNativeId(sessionId, SessionSourceSurface.ClaudeCode, "native-1", bindingKind, now);
        var explicitOtel = new ObservedSessionEvent(
            Guid.CreateVersion7(), sessionId, null, SessionSourceSurface.ClaudeCode, null,
            "trace-explicit", null, "claude-code-otel", "trace-explicit/span-1", "otel.span", now,
            SessionContentState.NotCaptured, MatchKind: SessionMatchKind.ExplicitLink);

        var projection = SourceProjectionStateBuilder.Build([], new SessionDetail(session, [native], [], [explicitOtel]));

        Assert.Equal("exact_linked", projection.BindingState);
    }
}

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
        };
        var exactLinked = SourceProjectionStateBuilder.Build(
            [observation],
            new SessionDetail(session, [native], [], [hook, exactOtel]));
        Assert.Equal("exact_linked", exactLinked.BindingState);

        var otelOnly = SourceProjectionStateBuilder.Build([observation], null);
        Assert.Equal("otel_only", otelOnly.BindingState);
    }
}

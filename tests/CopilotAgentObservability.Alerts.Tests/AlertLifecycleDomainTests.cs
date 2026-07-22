using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Alerts.Tests;

public sealed class AlertLifecycleDomainTests
{
    public static TheoryData<AlertLifecycleState, AlertLifecycleAction, AlertLifecycleState> AllowedTransitions => new()
    {
        { AlertLifecycleState.Open, AlertLifecycleAction.Acknowledge, AlertLifecycleState.Acknowledged },
        { AlertLifecycleState.Open, AlertLifecycleAction.Dismiss, AlertLifecycleState.Dismissed },
        { AlertLifecycleState.Acknowledged, AlertLifecycleAction.Dismiss, AlertLifecycleState.Dismissed },
        { AlertLifecycleState.Open, AlertLifecycleAction.Resolve, AlertLifecycleState.Resolved },
        { AlertLifecycleState.Acknowledged, AlertLifecycleAction.Resolve, AlertLifecycleState.Resolved },
        { AlertLifecycleState.Dismissed, AlertLifecycleAction.Reopen, AlertLifecycleState.Open },
        { AlertLifecycleState.Resolved, AlertLifecycleAction.Reopen, AlertLifecycleState.Open },
        { AlertLifecycleState.Open, AlertLifecycleAction.Supersede, AlertLifecycleState.Superseded },
        { AlertLifecycleState.Acknowledged, AlertLifecycleAction.Supersede, AlertLifecycleState.Superseded },
        { AlertLifecycleState.Dismissed, AlertLifecycleAction.Supersede, AlertLifecycleState.Superseded },
        { AlertLifecycleState.Resolved, AlertLifecycleAction.Supersede, AlertLifecycleState.Superseded },
    };

    [Theory]
    [MemberData(nameof(AllowedTransitions))]
    public void TransitionTable_AcceptsOnlyFrozenStateChanges(
        AlertLifecycleState current,
        AlertLifecycleAction action,
        AlertLifecycleState expected)
    {
        Assert.True(AlertLifecycleTransition.TryApply(current, action, out var actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(AlertLifecycleState.Open, AlertLifecycleAction.Reopen)]
    [InlineData(AlertLifecycleState.Acknowledged, AlertLifecycleAction.Acknowledge)]
    [InlineData(AlertLifecycleState.Acknowledged, AlertLifecycleAction.Reopen)]
    [InlineData(AlertLifecycleState.Dismissed, AlertLifecycleAction.Acknowledge)]
    [InlineData(AlertLifecycleState.Dismissed, AlertLifecycleAction.Dismiss)]
    [InlineData(AlertLifecycleState.Dismissed, AlertLifecycleAction.Resolve)]
    [InlineData(AlertLifecycleState.Resolved, AlertLifecycleAction.Acknowledge)]
    [InlineData(AlertLifecycleState.Resolved, AlertLifecycleAction.Dismiss)]
    [InlineData(AlertLifecycleState.Resolved, AlertLifecycleAction.Resolve)]
    [InlineData(AlertLifecycleState.Superseded, AlertLifecycleAction.Acknowledge)]
    [InlineData(AlertLifecycleState.Superseded, AlertLifecycleAction.Dismiss)]
    [InlineData(AlertLifecycleState.Superseded, AlertLifecycleAction.Resolve)]
    [InlineData(AlertLifecycleState.Superseded, AlertLifecycleAction.Reopen)]
    [InlineData(AlertLifecycleState.Superseded, AlertLifecycleAction.Supersede)]
    public void TransitionTable_RejectsInvalidOrTerminalTransitions(AlertLifecycleState current, AlertLifecycleAction action)
    {
        Assert.False(AlertLifecycleTransition.TryApply(current, action, out var unchanged));
        Assert.Equal(current, unchanged);
    }

    [Theory]
    [InlineData(AlertLifecycleState.Open)]
    [InlineData(AlertLifecycleState.Acknowledged)]
    [InlineData(AlertLifecycleState.Dismissed)]
    [InlineData(AlertLifecycleState.Resolved)]
    [InlineData(AlertLifecycleState.Superseded)]
    public void SourceDeleted_IsAnAuditEventThatPreservesState(AlertLifecycleState current)
    {
        Assert.True(AlertLifecycleTransition.TryApply(current, AlertLifecycleAction.SourceDeleted, out var actual));
        Assert.Equal(current, actual);
    }

    [Theory]
    [InlineData("reviewed retry threshold", true)]
    [InlineData("C:\\Users\\person\\raw.json", false)]
    [InlineData("see(C:\\Users\\person\\raw.json)", false)]
    [InlineData("\\\\server\\share\\raw.json", false)]
    [InlineData("../private/raw.json", false)]
    [InlineData("src/private/raw.json", false)]
    [InlineData("https://example.test/raw", false)]
    [InlineData("ssh://example.test/private", false)]
    [InlineData("person@example.test", false)]
    [InlineData("Authorization: Bearer secret", false)]
    [InlineData("prompt: private input", false)]
    [InlineData("tool result: private output", false)]
    [InlineData("<script>alert(1)</script>", false)]
    [InlineData("`raw evidence`", false)]
    [InlineData("[raw evidence]", false)]
    [InlineData("{\"prompt\":\"private\"}", false)]
    public void AuditCommentValidation_RejectsKnownRawAndSensitiveShapes(string comment, bool expected)
    {
        Assert.Equal(expected, AlertLifecycleValidation.IsSanitizedComment(comment));
    }

    [Fact]
    public void FixedIdentifiers_UseBoundedCanonicalGrammars()
    {
        Assert.True(AlertLifecycleValidation.IsReasonCode("user_reviewed"));
        Assert.False(AlertLifecycleValidation.IsReasonCode("User reviewed"));
        Assert.True(AlertLifecycleValidation.IsIdempotencyKey("aid1_" + new string('a', 43)));
        Assert.False(AlertLifecycleValidation.IsIdempotencyKey("aid1_short"));
    }

    [Fact]
    public void AuditCommentValidation_CountsUnicodeScalars()
    {
        Assert.True(AlertLifecycleValidation.IsSanitizedComment(string.Concat(Enumerable.Repeat("😀", 256))));
        Assert.False(AlertLifecycleValidation.IsSanitizedComment(string.Concat(Enumerable.Repeat("😀", 257))));
    }
}

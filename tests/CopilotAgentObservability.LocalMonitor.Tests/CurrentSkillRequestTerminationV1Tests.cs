using CopilotAgentObservability.LocalMonitor.SkillNative;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CurrentSkillRequestTerminationV1Tests
{
    [Fact]
    public void NoObservableFactIsNone()
    {
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerAborted: false, retentionLostOrBusy: false, runtimeInvalidated: false);

        Assert.Equal(CurrentSkillTerminationCause.None, cause);
        Assert.False(CurrentSkillRequestTerminationV1.AbortsWithoutResponse(cause));
        Assert.False(CurrentSkillRequestTerminationV1.PermitsSubstituteRuntimeUnavailable(cause));
    }

    [Fact]
    public void CallerAbortBeatsRetentionLossAndRuntimeInvalidation()
    {
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerAborted: true, retentionLostOrBusy: true, runtimeInvalidated: true);

        Assert.Equal(CurrentSkillTerminationCause.CallerAbort, cause);
        Assert.True(CurrentSkillRequestTerminationV1.AbortsWithoutResponse(cause));
        Assert.False(CurrentSkillRequestTerminationV1.PermitsSubstituteRuntimeUnavailable(cause));
    }

    [Fact]
    public void CallerAbortAloneAborts()
    {
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerAborted: true, retentionLostOrBusy: false, runtimeInvalidated: false);

        Assert.Equal(CurrentSkillTerminationCause.CallerAbort, cause);
        Assert.True(CurrentSkillRequestTerminationV1.AbortsWithoutResponse(cause));
    }

    [Fact]
    public void RetentionLossBeatsRuntimeInvalidation()
    {
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerAborted: false, retentionLostOrBusy: true, runtimeInvalidated: true);

        Assert.Equal(CurrentSkillTerminationCause.RetentionLostOrBusy, cause);
        Assert.True(CurrentSkillRequestTerminationV1.AbortsWithoutResponse(cause));
        Assert.False(CurrentSkillRequestTerminationV1.PermitsSubstituteRuntimeUnavailable(cause));
    }

    [Fact]
    public void RuntimeInvalidationAlonePermitsSubstituteUnavailable()
    {
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerAborted: false, retentionLostOrBusy: false, runtimeInvalidated: true);

        Assert.Equal(CurrentSkillTerminationCause.RuntimeInvalidation, cause);
        Assert.False(CurrentSkillRequestTerminationV1.AbortsWithoutResponse(cause));
        Assert.True(CurrentSkillRequestTerminationV1.PermitsSubstituteRuntimeUnavailable(cause));
    }

    [Fact]
    public void RetentionLossAloneAborts()
    {
        var cause = CurrentSkillRequestTerminationV1.ResolveCause(
            callerAborted: false, retentionLostOrBusy: true, runtimeInvalidated: false);

        Assert.Equal(CurrentSkillTerminationCause.RetentionLostOrBusy, cause);
        Assert.True(CurrentSkillRequestTerminationV1.AbortsWithoutResponse(cause));
        Assert.False(CurrentSkillRequestTerminationV1.PermitsSubstituteRuntimeUnavailable(cause));
    }
}

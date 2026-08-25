using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Tests;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal static class TestCertifiedIdentityExtensions
{
    internal static CopilotRuntimeGenerationV1 PublishReadyTestCandidate(
        this CopilotRuntimeAdmissionV1 admission,
        ICopilotSkillRuntimeClient client,
        out CopilotRuntimeGenerationV1? replacedGeneration)
        => admission.PublishReadyTestCandidate(client, SkillInvocationV2TestIdentity.V1065, out replacedGeneration);

    internal static CopilotRuntimeGenerationV1 PublishReadyTestCandidate(
        this CopilotRuntimeAdmissionV1 admission,
        ICopilotSkillRuntimeClient client,
        CertifiedSkillProducerIdentityV1 identity,
        out CopilotRuntimeGenerationV1? replacedGeneration)
    {
        admission.TryGetCurrentAdmittedGeneration(out replacedGeneration);
        var candidate = admission.CreateUnpublishedCandidate(
            client,
            identity,
            new TestAnalysisScope());
        Assert.True(candidate.TryMarkReady());
        Assert.True(admission.PublishCandidateAsync(candidate).GetAwaiter().GetResult());
        return candidate;
    }

    internal static CopilotRuntimeGenerationV1? InvalidateCurrentTestGeneration(
        this CopilotRuntimeAdmissionV1 admission)
    {
        if (!admission.TryGetCurrentAdmittedGeneration(out var current)) return null;
        admission.InvalidateCandidate(current);
        return current;
    }

    private sealed class TestAnalysisScope : IAnalysisSdkDirectoryScope
    {
        public string ChildDirectory => "synthetic-test-scope";
        public CancellationToken LeaseLostToken => CancellationToken.None;
        public bool IsLeaseLost => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

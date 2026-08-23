using CopilotAgentObservability.LocalMonitor.Tests;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal static class TestCertifiedIdentityExtensions
{
    internal static CopilotRuntimeGenerationV1? PublishAdmittedGeneration(
        this CopilotRuntimeAdmissionV1 admission,
        ICopilotSkillRuntimeClient client,
        out CopilotRuntimeGenerationV1? replacedGeneration) =>
        admission.PublishAdmittedGeneration(client, SkillInvocationV2TestIdentity.V1065, out replacedGeneration);
}

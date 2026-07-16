using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal static class SetupEnvironmentPlanValue
{
    public static UserEnvironmentValue Desired(SetupPrivatePlanMember member) =>
        member.DesiredValue is null
            ? UserEnvironmentValue.Missing
            : UserEnvironmentValue.Present(member.DesiredValue);
}

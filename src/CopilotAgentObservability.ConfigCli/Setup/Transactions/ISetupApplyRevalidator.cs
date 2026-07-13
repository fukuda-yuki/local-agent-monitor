using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal interface ISetupApplyRevalidator
{
    SetupPlanResult<SetupRevalidation> Revalidate(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet);
}

internal sealed class SetupApplyException : Exception
{
    public SetupApplyException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

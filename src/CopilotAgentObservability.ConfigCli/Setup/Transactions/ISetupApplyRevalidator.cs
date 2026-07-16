using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal interface ISetupApplyRevalidator
{
    SetupPlanResult<SetupRevalidation> Revalidate(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet);
}

internal static class SetupDesiredStateHash
{
    public static string File(SetupPrivateDesiredState desiredState) => desiredState switch
    {
        SetupInlineDesiredState inline => SetupHash.File(true, Encoding.UTF8.GetBytes(inline.Value)),
        SetupJsoncOwnedValuesDesiredState tagged => tagged.ExpectedStateHash,
        SetupClaudeSettingsOwnedValuesDesiredState claude => claude.ExpectedStateHash,
        _ => throw new FormatException(),
    };
}

internal sealed class SetupApplyException : Exception
{
    public SetupApplyException(string code)
        : this(SetupPlanResult.Failure<SetupLedgerChangeSet>(code))
    {
    }

    public SetupApplyException(SetupPlanFailure<SetupLedgerChangeSet> failure)
        : base((failure ?? throw new ArgumentNullException(nameof(failure))).Code)
    {
        Failure = failure;
    }

    public SetupPlanFailure<SetupLedgerChangeSet> Failure { get; }

    public string Code => Failure.Code;
}

using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal static class SetupTransactionEvidence
{
    public static void RequireImmutableIdentity(SetupPrivatePlan plan, SetupLedgerChangeSet changeSet)
    {
        if (plan.ChangeSetId != changeSet.ChangeSetId ||
            !string.Equals(plan.Adapter, changeSet.Adapter, StringComparison.Ordinal) ||
            !string.Equals(plan.SelectedTarget, changeSet.SelectedTarget, StringComparison.Ordinal) ||
            plan.CreatedAt != changeSet.CreatedAt ||
            !string.Equals(plan.ToolVersion, changeSet.ToolVersion, StringComparison.Ordinal) ||
            plan.Targets.Count != changeSet.Targets.Count)
        {
            throw new FormatException();
        }

        for (var targetIndex = 0; targetIndex < plan.Targets.Count; targetIndex++)
        {
            var expected = plan.Targets[targetIndex];
            var actual = changeSet.Targets[targetIndex];
            if (expected.RecordId != actual.RecordId ||
                expected.TargetKind != actual.TargetKind ||
                !string.Equals(expected.BaseStateHash, actual.PreviousStateHash, StringComparison.Ordinal) ||
                expected.Members.Count != actual.Members.Count)
            {
                throw new FormatException();
            }

            for (var memberIndex = 0; memberIndex < expected.Members.Count; memberIndex++)
            {
                if (!string.Equals(expected.Members[memberIndex].SettingKey,
                        actual.Members[memberIndex].SettingKey, StringComparison.Ordinal) ||
                    expected.Members[memberIndex].Operation != actual.Members[memberIndex].Operation)
                {
                    throw new FormatException();
                }
            }
        }

        SetupStorageValidation.ValidateDesiredStateBindings(plan, changeSet);
    }
}

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal static class SetupFaultPoint
{
    public const string AfterPlanPersistedBeforeLedger = "after-plan-persisted-before-ledger";
    public const string AfterJournalPreparedBeforeLedger = "after-journal-prepared-before-ledger";
    public const string AfterLedgerTransitionBeforeMutationIntent = "after-ledger-transition-before-mutation-intent";
    public const string AfterMutationIntentBeforeMutation = "after-mutation-intent-before-mutation";
    public const string AfterMutationBeforeCompletion = "after-mutation-before-completion";
    public const string AfterRestoreIntentBeforeRestore = "after-restore-intent-before-restore";
    public const string AfterRestoreBeforeCompletion = "after-restore-before-completion";
    public const string AfterCompletionBeforeCommit = "after-completion-before-commit";
    public const string AfterCommitBeforeLedger = "after-commit-before-ledger";
}

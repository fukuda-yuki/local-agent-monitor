namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ISourceCompatibilityStore
{
    void CreateSchema();

    long RecordAdapterFailure(SourceAdapterFailureDraft failure);

    IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit);
}

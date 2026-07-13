namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ISourceCompatibilityStore
{
    void CreateSchema();

    long RecordAdapterFailure(SourceAdapterFailureDraft failure);

    SourceCompatibilityRow? GetByRawRecordId(long rawRecordId);

    IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit);
}

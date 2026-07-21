namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ISourceCompatibilityStore
{
    void CreateSchema();

    long RecordAdapterFailure(SourceAdapterFailureDraft failure);

    SourceCompatibilityRow? GetByRawRecordId(long rawRecordId);

    SourceCompatibilityRow? GetByObservationId(string observationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(observationId);
        long? after = null;
        while (true)
        {
            var page = List(after, 200);
            var exact = page.FirstOrDefault(row => string.Equals(row.ObservationId, observationId, StringComparison.Ordinal));
            if (exact is not null)
            {
                return exact;
            }
            if (page.Count == 0)
            {
                return null;
            }
            var next = page[^1].Id;
            if (after is not null && next <= after)
            {
                throw new InvalidOperationException("Source diagnostics cursor did not advance.");
            }
            after = next;
        }
    }

    IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit);
}

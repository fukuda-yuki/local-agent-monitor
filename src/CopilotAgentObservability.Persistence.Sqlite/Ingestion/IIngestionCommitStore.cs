namespace CopilotAgentObservability.Persistence.Sqlite.Ingestion;

internal interface IIngestionCommitStore
{
    CommittedIngestionIds Commit(ValidatedIngestionBatch batch);
}

internal sealed class ValidatedIngestionBatch
{
    private ValidatedIngestionBatch(RawTelemetryRecord rawRecord, SourceObservationBatchDraft observation)
    {
        RawRecord = rawRecord;
        Observation = observation;
    }

    public RawTelemetryRecord RawRecord { get; }
    public SourceObservationBatchDraft Observation { get; }
    public string IngestBatchId => Observation.IngestBatchId;

    public static ValidatedIngestionBatch Create(
        RawTelemetryRecord rawRecord,
        SourceObservationBatchDraft observation)
    {
        ArgumentNullException.ThrowIfNull(rawRecord);
        ArgumentNullException.ThrowIfNull(observation);
        if (rawRecord.Id is not null)
        {
            throw new ArgumentException("A validated ingestion batch cannot contain a preassigned raw-record ID.", nameof(rawRecord));
        }
        if (!RawTelemetrySources.IsAllowed(rawRecord.Source) ||
            rawRecord.SchemaVersion != RawStoreDefaults.SchemaVersion ||
            rawRecord.PayloadJson is null)
        {
            throw new ArgumentException("The raw record does not satisfy the raw-store ingestion contract.", nameof(rawRecord));
        }
        if (observation.CompatibilityState == SourceCompatibilityState.AdapterFailure)
        {
            throw new ArgumentException("A committed ingestion batch requires a successful-batch observation.", nameof(observation));
        }

        return new ValidatedIngestionBatch(rawRecord, observation);
    }
}

internal sealed record CommittedIngestionIds(long RawRecordId, long ObservationId);

internal sealed class IngestionCommitBusyException : Exception
{
}

internal sealed class IngestionCommitFailedException : Exception
{
}

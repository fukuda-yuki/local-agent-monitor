using CopilotAgentObservability.Persistence.Sqlite.Ingestion;

namespace CopilotAgentObservability.LocalMonitor.Ingestion;

internal enum IngestionCommitStatus
{
    Committed,
    Busy,
    Failed,
}

internal sealed record IngestionCommitResult
{
    private IngestionCommitResult(IngestionCommitStatus status, long rawRecordId, long observationId)
    {
        Status = status;
        RawRecordId = rawRecordId;
        ObservationId = observationId;
    }

    public IngestionCommitStatus Status { get; }

    public long RawRecordId { get; }

    public long ObservationId { get; }

    public static IngestionCommitResult Committed(long rawRecordId, long observationId) =>
        new(IngestionCommitStatus.Committed, rawRecordId, observationId);

    public static IngestionCommitResult AdapterFailureRecorded(long observationId) =>
        new(IngestionCommitStatus.Committed, 0, observationId);

    public static IngestionCommitResult Busy { get; } = new(IngestionCommitStatus.Busy, 0, 0);

    public static IngestionCommitResult Failed { get; } = new(IngestionCommitStatus.Failed, 0, 0);
}

internal sealed class IngestionWriteRequest
{
    private readonly TaskCompletionSource<IngestionCommitResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private IngestionWriteRequest(
        ValidatedIngestionBatch? batch,
        SourceAdapterFailureDraft? adapterFailure,
        DateTimeOffset enqueuedAt)
    {
        Batch = batch;
        AdapterFailure = adapterFailure;
        EnqueuedAt = enqueuedAt;
    }

    public ValidatedIngestionBatch? Batch { get; }

    public SourceAdapterFailureDraft? AdapterFailure { get; }

    public DateTimeOffset EnqueuedAt { get; }

    public Task<IngestionCommitResult> Completion => completion.Task;

    public void Complete(IngestionCommitResult result) => completion.TrySetResult(result);

    public static IngestionWriteRequest ForBatch(ValidatedIngestionBatch batch, DateTimeOffset enqueuedAt) =>
        new(batch ?? throw new ArgumentNullException(nameof(batch)), null, enqueuedAt);

    public static IngestionWriteRequest ForAdapterFailure(SourceAdapterFailureDraft failure, DateTimeOffset enqueuedAt) =>
        new(null, failure ?? throw new ArgumentNullException(nameof(failure)), enqueuedAt);
}

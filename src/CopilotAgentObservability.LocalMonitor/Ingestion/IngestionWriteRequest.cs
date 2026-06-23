namespace CopilotAgentObservability.LocalMonitor.Ingestion;

internal enum IngestionCommitStatus
{
    Committed,
    Busy,
    Failed,
}

internal sealed record IngestionCommitResult
{
    private IngestionCommitResult(IngestionCommitStatus status, long rawRecordId)
    {
        Status = status;
        RawRecordId = rawRecordId;
    }

    public IngestionCommitStatus Status { get; }

    public long RawRecordId { get; }

    public static IngestionCommitResult Committed(long rawRecordId) =>
        new(IngestionCommitStatus.Committed, rawRecordId);

    public static IngestionCommitResult Busy { get; } = new(IngestionCommitStatus.Busy, 0);

    public static IngestionCommitResult Failed { get; } = new(IngestionCommitStatus.Failed, 0);
}

internal sealed class IngestionWriteRequest
{
    private readonly TaskCompletionSource<IngestionCommitResult> completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IngestionWriteRequest(RawTelemetryRecord record, DateTimeOffset enqueuedAt)
    {
        Record = record;
        EnqueuedAt = enqueuedAt;
    }

    public RawTelemetryRecord Record { get; }

    public DateTimeOffset EnqueuedAt { get; }

    public Task<IngestionCommitResult> Completion => completion.Task;

    public void Complete(IngestionCommitResult result) => completion.TrySetResult(result);
}

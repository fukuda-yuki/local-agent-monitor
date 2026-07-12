using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class IngestionQueueTests
{
    private static ValidatedIngestionBatch CreateBatch(string traceId = "trace-1")
    {
        var record = new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: "{}");
        var inventory = OtlpJsonStructuralWalker.Build(
            """{"resourceSpans":[{"scopeSpans":[{"spans":[{}]}]}]}""",
            DateTimeOffset.UnixEpoch);
        var observation = SourceObservationBatchDraft.Create(
            $"batch-{traceId}",
            "raw-otlp",
            sourceApplicationVersion: null,
            "raw-otlp",
            "1",
            inventory,
            SourceCompatibilityEvaluator.Assess(
                "raw-otlp",
                sourceApplicationVersion: null,
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Unsupported,
            DateTimeOffset.UnixEpoch);
        return ValidatedIngestionBatch.Create(record, observation);
    }

    [Fact]
    public void TryEnqueue_SucceedsWhenCapacityAvailable()
    {
        var queue = new IngestionQueue(capacity: 1);

        var batch = CreateBatch();
        var enqueued = queue.TryEnqueue(batch, out var request);

        Assert.True(enqueued);
        Assert.NotNull(request);
        Assert.Same(batch, request.Batch);
    }

    [Fact]
    public void TryEnqueue_ReturnsFalseWhenBoundedCapacityIsFull()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateBatch(), out _));

        var second = queue.TryEnqueue(CreateBatch("trace-2"), out var request);

        Assert.False(second);
        Assert.Null(request);
    }

    [Fact]
    public async Task Complete_WithRawRecordId_ReleasesAwaitingHttpSide()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateBatch(), out var request));

        request.Complete(IngestionCommitResult.Committed(42, 84));
        var result = await request.Completion;

        Assert.Equal(IngestionCommitStatus.Committed, result.Status);
        Assert.Equal(42, result.RawRecordId);
        Assert.Equal(84, result.ObservationId);
    }

    [Fact]
    public async Task Complete_WithBusy_MapsToTypedBusyResult()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateBatch(), out var request));

        request.Complete(IngestionCommitResult.Busy);
        var result = await request.Completion;

        Assert.Equal(IngestionCommitStatus.Busy, result.Status);
    }

    [Fact]
    public async Task Complete_WithFailure_MapsToTypedFailureResult()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateBatch(), out var request));

        request.Complete(IngestionCommitResult.Failed);
        var result = await request.Completion;

        Assert.Equal(IngestionCommitStatus.Failed, result.Status);
    }

    [Fact]
    public void TryEnqueue_AfterCompleteAdding_ReturnsFalse()
    {
        var queue = new IngestionQueue(capacity: 1);

        queue.CompleteAdding();

        Assert.False(queue.TryEnqueue(CreateBatch(), out _));
    }

    [Fact]
    public async Task TryEnqueue_AdapterFailureCarriesOnlySanitizedFailureDraft()
    {
        var queue = new IngestionQueue(capacity: 1);
        var failure = SourceAdapterFailureDraft.CreateParseFailure(
            "failure-1", null, null, null, null, null, null, DateTimeOffset.UnixEpoch);

        Assert.True(queue.TryEnqueue(failure, out var request));
        Assert.Null(request.Batch);
        Assert.Same(failure, request.AdapterFailure);

        request.Complete(IngestionCommitResult.AdapterFailureRecorded(91));
        var result = await request.Completion;
        Assert.Equal(IngestionCommitStatus.Committed, result.Status);
        Assert.Equal(0, result.RawRecordId);
        Assert.Equal(91, result.ObservationId);
    }
}

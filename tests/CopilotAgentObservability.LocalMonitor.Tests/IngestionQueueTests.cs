using CopilotAgentObservability.LocalMonitor.Ingestion;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class IngestionQueueTests
{
    private static RawTelemetryRecord CreateRecord(string traceId = "trace-1")
    {
        return new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: "{}");
    }

    [Fact]
    public void TryEnqueue_SucceedsWhenCapacityAvailable()
    {
        var queue = new IngestionQueue(capacity: 1);

        var enqueued = queue.TryEnqueue(CreateRecord(), out var request);

        Assert.True(enqueued);
        Assert.NotNull(request);
    }

    [Fact]
    public void TryEnqueue_ReturnsFalseWhenBoundedCapacityIsFull()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateRecord(), out _));

        var second = queue.TryEnqueue(CreateRecord(), out var request);

        Assert.False(second);
        Assert.Null(request);
    }

    [Fact]
    public async Task Complete_WithRawRecordId_ReleasesAwaitingHttpSide()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateRecord(), out var request));

        request.Complete(IngestionCommitResult.Committed(42));
        var result = await request.Completion;

        Assert.Equal(IngestionCommitStatus.Committed, result.Status);
        Assert.Equal(42, result.RawRecordId);
    }

    [Fact]
    public async Task Complete_WithBusy_MapsToTypedBusyResult()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateRecord(), out var request));

        request.Complete(IngestionCommitResult.Busy);
        var result = await request.Completion;

        Assert.Equal(IngestionCommitStatus.Busy, result.Status);
    }

    [Fact]
    public async Task Complete_WithFailure_MapsToTypedFailureResult()
    {
        var queue = new IngestionQueue(capacity: 1);
        Assert.True(queue.TryEnqueue(CreateRecord(), out var request));

        request.Complete(IngestionCommitResult.Failed);
        var result = await request.Completion;

        Assert.Equal(IngestionCommitStatus.Failed, result.Status);
    }

    [Fact]
    public void TryEnqueue_AfterCompleteAdding_ReturnsFalse()
    {
        var queue = new IngestionQueue(capacity: 1);

        queue.CompleteAdding();

        Assert.False(queue.TryEnqueue(CreateRecord(), out _));
    }
}

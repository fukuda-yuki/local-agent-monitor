using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class IngestionWriterWorkerTests
{
    private static ValidatedIngestionBatch CreateBatch(string traceId = "trace")
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
            $"batch-{traceId}", "raw-otlp", null, "raw-otlp", "1", inventory,
            SourceCompatibilityEvaluator.Assess(
                "raw-otlp", null, inventory, 1, VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Unsupported, DateTimeOffset.UnixEpoch);
        return ValidatedIngestionBatch.Create(record, observation);
    }

    [Fact]
    public async Task Worker_CreatesSchemaAndInsertsQueuedRecordsInOrder()
    {
        using var temp = new MonitorTempDirectory();
        var queue = new IngestionQueue(capacity: 8);
        var health = new MonitorHealthState();
        var compatibilityStore = new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var worker = new IngestionWriterWorker(
            queue,
            new SqliteIngestionCommitStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter),
            compatibilityStore,
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateBatch("a"), out var first));
            Assert.True(queue.TryEnqueue(CreateBatch("b"), out var second));

            var firstResult = await first.Completion;
            var secondResult = await second.Completion;

            Assert.Equal(IngestionCommitStatus.Committed, firstResult.Status);
            Assert.Equal(IngestionCommitStatus.Committed, secondResult.Status);
            Assert.Equal(firstResult.RawRecordId + 1, secondResult.RawRecordId);
            Assert.Equal(firstResult.ObservationId + 1, secondResult.ObservationId);
            Assert.Equal(2, new RawTelemetryStore(temp.DatabasePath).ListRecords().Count);
            Assert.Equal(2, compatibilityStore.List(after: null, limit: 200).Count);

            var snapshot = health.Snapshot();
            Assert.True(snapshot.MigrationComplete);
            Assert.True(snapshot.WriterRunning);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_BusyPersistence_ReturnsTypedBusyResultAndRecordsBackpressure()
    {
        var queue = new IngestionQueue(capacity: 1);
        var health = new MonitorHealthState();
        var worker = new IngestionWriterWorker(
            queue,
            new FakeCommitStore(_ => throw new IngestionCommitBusyException()),
            new FakeCompatibilityStore(),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateBatch(), out var request));
            var result = await request.Completion;

            Assert.Equal(IngestionCommitStatus.Busy, result.Status);
            Assert.NotNull(health.Snapshot().UnableToCommitSince);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_NonBusyPersistenceFailure_ReturnsFailedResultWithoutFatal()
    {
        var queue = new IngestionQueue(capacity: 1);
        var health = new MonitorHealthState();
        var worker = new IngestionWriterWorker(
            queue,
            new FakeCommitStore(_ => throw new IngestionCommitFailedException()),
            new FakeCompatibilityStore(),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateBatch(), out var request));
            var result = await request.Completion;

            Assert.Equal(IngestionCommitStatus.Failed, result.Status);
            Assert.False(health.Snapshot().FatalError);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_MigrationFailure_IsSurfacedAsNotReady()
    {
        var queue = new IngestionQueue(capacity: 1);
        var health = new MonitorHealthState();
        var worker = new IngestionWriterWorker(
            queue,
            new FakeCommitStore(_ => new CommittedIngestionIds(1, 2)),
            new FakeCompatibilityStore(schemaError: new InvalidOperationException("migration boom")),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var snapshot = health.Snapshot();
            Assert.False(snapshot.MigrationComplete);
            Assert.False(snapshot.WriterRunning);

            Assert.True(queue.TryEnqueue(CreateBatch(), out var request));
            var result = await request.Completion;
            Assert.Equal(IngestionCommitStatus.Failed, result.Status);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_UnexpectedInsertException_CompletesRequestAndKeepsProcessing()
    {
        var queue = new IngestionQueue(capacity: 4);
        var health = new MonitorHealthState();
        var calls = 0;
        var worker = new IngestionWriterWorker(
            queue,
            new FakeCommitStore(_ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new InvalidOperationException("unexpected");
                }

                return new CommittedIngestionIds(calls, calls + 10);
            }),
            new FakeCompatibilityStore(),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateBatch("first"), out var first));
            var firstResult = await first.Completion;
            Assert.Equal(IngestionCommitStatus.Failed, firstResult.Status);

            // The worker must stay alive and keep processing later requests.
            Assert.True(queue.TryEnqueue(CreateBatch("second"), out var second));
            var secondResult = await second.Completion;
            Assert.Equal(IngestionCommitStatus.Committed, secondResult.Status);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_DrainsAlreadyAcceptedQueueItemsDuringShutdown()
    {
        using var temp = new MonitorTempDirectory();
        var queue = new IngestionQueue(capacity: 16);
        var health = new MonitorHealthState();
        var compatibilityStore = new SqliteSourceCompatibilityStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var worker = new IngestionWriterWorker(
            queue,
            new SqliteIngestionCommitStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter),
            compatibilityStore,
            health);

        await worker.StartAsync(CancellationToken.None);

        var requests = new List<IngestionWriteRequest>();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(queue.TryEnqueue(CreateBatch($"trace-{i}"), out var request));
            requests.Add(request);
        }

        await worker.StopAsync(CancellationToken.None);

        foreach (var result in await Task.WhenAll(requests.Select(static request => request.Completion.WaitAsync(TimeSpan.FromSeconds(5)))))
        {
            Assert.Equal(IngestionCommitStatus.Committed, result.Status);
        }

        Assert.Equal(5, new RawTelemetryStore(temp.DatabasePath).ListRecords().Count);
        Assert.Equal(5, compatibilityStore.List(after: null, limit: 200).Count);
    }

    [Fact]
    public async Task Worker_StopAsyncWaitsForAcceptedQueueItemToCommit()
    {
        var queue = new IngestionQueue(capacity: 4);
        var health = new MonitorHealthState();
        var writer = new GatedCommitStore();
        var worker = new IngestionWriterWorker(queue, writer, new FakeCompatibilityStore(), health);

        await worker.StartAsync(CancellationToken.None);
        Assert.True(queue.TryEnqueue(CreateBatch(), out var request));
        await writer.Entered;

        var stopTask = worker.StopAsync(CancellationToken.None);

        Assert.False(stopTask.IsCompleted);
        Assert.False(request.Completion.IsCompleted);

        writer.Release();
        await stopTask;

        var result = await request.Completion;
        Assert.Equal(IngestionCommitStatus.Committed, result.Status);
        Assert.Equal(1, result.RawRecordId);
        Assert.Equal(2, result.ObservationId);
    }

    [Fact]
    public async Task Worker_RecordsAdapterFailureThroughTheSameSingleWriter()
    {
        var queue = new IngestionQueue(capacity: 1);
        var health = new MonitorHealthState();
        var compatibilityStore = new RecordingCompatibilityStore();
        var commitStore = new FakeCommitStore(_ => throw new InvalidOperationException("raw commit must not run"));
        var worker = new IngestionWriterWorker(queue, commitStore, compatibilityStore, health);
        var failure = SourceAdapterFailureDraft.CreateParseFailure(
            "failure-1", null, null, null, null, null, null, DateTimeOffset.UnixEpoch);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(failure, out var request));
            var result = await request.Completion;

            Assert.Same(failure, Assert.Single(compatibilityStore.Failures));
            Assert.Equal(IngestionCommitStatus.Committed, result.Status);
            Assert.Equal(0, result.RawRecordId);
            Assert.Equal(73, result.ObservationId);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeCommitStore : IIngestionCommitStore
    {
        private readonly Func<ValidatedIngestionBatch, CommittedIngestionIds> commit;

        public FakeCommitStore(Func<ValidatedIngestionBatch, CommittedIngestionIds> commit)
        {
            this.commit = commit;
        }

        public CommittedIngestionIds Commit(ValidatedIngestionBatch batch) => commit(batch);
    }

    private sealed class FakeCompatibilityStore : ISourceCompatibilityStore
    {
        private readonly Exception? schemaError;

        public FakeCompatibilityStore(Exception? schemaError = null)
        {
            this.schemaError = schemaError;
        }

        public void CreateSchema()
        {
            if (schemaError is not null)
            {
                throw schemaError;
            }
        }

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure) => 1;

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) => null;

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) => [];
    }

    private sealed class RecordingCompatibilityStore : ISourceCompatibilityStore
    {
        public List<SourceAdapterFailureDraft> Failures { get; } = [];

        public void CreateSchema()
        {
        }

        public long RecordAdapterFailure(SourceAdapterFailureDraft failure)
        {
            Failures.Add(failure);
            return 73;
        }

        public SourceCompatibilityRow? GetByRawRecordId(long rawRecordId) => null;

        public IReadOnlyList<SourceCompatibilityRow> List(long? after, int limit) => [];
    }

    private sealed class GatedCommitStore : IIngestionCommitStore
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim gate = new(initialState: false);

        public Task Entered => entered.Task;

        public void Release() => gate.Set();

        public CommittedIngestionIds Commit(ValidatedIngestionBatch batch)
        {
            entered.TrySetResult();
            gate.Wait();
            return new CommittedIngestionIds(1, 2);
        }
    }
}

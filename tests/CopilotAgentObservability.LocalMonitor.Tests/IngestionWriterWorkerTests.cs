using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class IngestionWriterWorkerTests
{
    private static RawTelemetryRecord CreateRecord(string traceId = "trace") =>
        new(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: "{}");

    [Fact]
    public async Task Worker_CreatesSchemaAndInsertsQueuedRecordsInOrder()
    {
        using var temp = new MonitorTempDirectory();
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var queue = new IngestionQueue(capacity: 8);
        var health = new MonitorHealthState();
        var worker = new IngestionWriterWorker(queue, new RawTelemetryStoreWriter(store), health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateRecord("a"), out var first));
            Assert.True(queue.TryEnqueue(CreateRecord("b"), out var second));

            var firstResult = await first.Completion;
            var secondResult = await second.Completion;

            Assert.Equal(IngestionCommitStatus.Committed, firstResult.Status);
            Assert.Equal(IngestionCommitStatus.Committed, secondResult.Status);
            Assert.Equal(firstResult.RawRecordId + 1, secondResult.RawRecordId);
            Assert.Equal(2, store.ListRecords().Count);

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
            new FakeRawWriter(_ => throw new PersistenceBusyException()),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateRecord(), out var request));
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
            new FakeRawWriter(_ => throw new PersistenceFailedException()),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateRecord(), out var request));
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
            new FakeRawWriter(_ => 1, schemaError: new InvalidOperationException("migration boom")),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var snapshot = health.Snapshot();
            Assert.False(snapshot.MigrationComplete);
            Assert.False(snapshot.WriterRunning);

            Assert.True(queue.TryEnqueue(CreateRecord(), out var request));
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
            new FakeRawWriter(_ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new InvalidOperationException("unexpected");
                }

                return calls;
            }),
            health);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.True(queue.TryEnqueue(CreateRecord("first"), out var first));
            var firstResult = await first.Completion;
            Assert.Equal(IngestionCommitStatus.Failed, firstResult.Status);

            // The worker must stay alive and keep processing later requests.
            Assert.True(queue.TryEnqueue(CreateRecord("second"), out var second));
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
        var store = new RawTelemetryStore(temp.DatabasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var queue = new IngestionQueue(capacity: 16);
        var health = new MonitorHealthState();
        var worker = new IngestionWriterWorker(queue, new RawTelemetryStoreWriter(store), health);

        await worker.StartAsync(CancellationToken.None);

        var requests = new List<IngestionWriteRequest>();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(queue.TryEnqueue(CreateRecord($"trace-{i}"), out var request));
            requests.Add(request);
        }

        await worker.StopAsync(CancellationToken.None);

        foreach (var request in requests)
        {
            Assert.True(request.Completion.IsCompletedSuccessfully);
            var result = await request.Completion;
            Assert.Equal(IngestionCommitStatus.Committed, result.Status);
        }

        Assert.Equal(5, store.ListRecords().Count);
    }

    private sealed class FakeRawWriter : IRawTelemetryWriter
    {
        private readonly Func<RawTelemetryRecord, long> insert;
        private readonly Exception? schemaError;

        public FakeRawWriter(Func<RawTelemetryRecord, long> insert, Exception? schemaError = null)
        {
            this.insert = insert;
            this.schemaError = schemaError;
        }

        public void EnsureSchema()
        {
            if (schemaError is not null)
            {
                throw schemaError;
            }
        }

        public long Insert(RawTelemetryRecord record) => insert(record);
    }
}

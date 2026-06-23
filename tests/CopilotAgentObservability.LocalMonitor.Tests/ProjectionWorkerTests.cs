using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class ProjectionWorkerTests
{
    [Fact]
    public async Task Pass_ProjectsPreExistingUnprocessedRows()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        store.Seed(2, T(2));
        store.Seed(3, T(3));
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        Assert.True(store.IsProjected(1));
        Assert.True(store.IsProjected(2));
        Assert.True(store.IsProjected(3));
        var snapshot = health.Snapshot();
        Assert.Equal(0, snapshot.ProjectionBacklog);
        Assert.Null(snapshot.OldestUnprocessedReceivedAt);
    }

    [Fact]
    public async Task Pass_IsNoOpUntilMigrationComplete()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        var health = new MonitorHealthState(); // migration not complete
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        Assert.False(store.IsProjected(1));
    }

    [Fact]
    public async Task Pass_ProjectsNewRowsAndDoesNotReprocess()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();
        store.Seed(2, T(2));
        await worker.RunProjectionPassAsync();

        Assert.Equal(1, store.ApplyCalls[1]);
        Assert.Equal(1, store.ApplyCalls[2]);
    }

    [Fact]
    public async Task Pass_BusyResultIsRetriedAndRawNotLost()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        var busy = true;
        store.OnApply = _ => busy ? ApplyOutcome.Busy : ApplyOutcome.Success;
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();
        Assert.False(store.IsProjected(1));
        Assert.Contains(1L, store.AllIds);

        busy = false;
        await worker.RunProjectionPassAsync();
        Assert.True(store.IsProjected(1));
    }

    [Fact]
    public async Task Pass_NonBusyFailureIsolatesRowAndContinues()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        store.Seed(2, T(2));
        store.OnApply = id => id == 1 ? ApplyOutcome.Fail : ApplyOutcome.Success;
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        Assert.False(store.IsProjected(1));
        Assert.True(store.IsProjected(2));
        Assert.Contains(1L, store.AllIds);
        Assert.True(health.Snapshot().ProjectionFailureCount >= 1);
    }

    [Fact]
    public async Task Pass_UpdatesHealthBacklogAndOldest()
    {
        var store = new FakeProjectionStore();
        store.Seed(1, T(1));
        store.Seed(2, T(2));
        store.OnApply = id => id == 1 ? ApplyOutcome.Success : ApplyOutcome.Busy;
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health);

        await worker.RunProjectionPassAsync();

        var snapshot = health.Snapshot();
        Assert.Equal(1, snapshot.ProjectionBacklog);
        Assert.Equal(T(2), snapshot.OldestUnprocessedReceivedAt);
    }

    [Fact]
    public async Task StartStop_TogglesProjectionWorkerRunning()
    {
        var store = new FakeProjectionStore();
        var health = ReadyHealth();
        var worker = new ProjectionWorker(store, health, pollInterval: TimeSpan.FromMilliseconds(20));

        await worker.StartAsync(CancellationToken.None);
        Assert.True(health.Snapshot().ProjectionWorkerRunning);

        await worker.StopAsync(CancellationToken.None);
        Assert.False(health.Snapshot().ProjectionWorkerRunning);
    }

    private static MonitorHealthState ReadyHealth()
    {
        var health = new MonitorHealthState();
        health.MarkMigrationComplete();
        return health;
    }

    private static DateTimeOffset T(int minutes) => DateTimeOffset.UnixEpoch.AddMinutes(minutes);

    private enum ApplyOutcome
    {
        Success,
        Busy,
        Fail,
    }

    private sealed class FakeProjectionStore : IMonitorProjectionStore
    {
        private readonly List<RawTelemetryRecord> records = new();
        private readonly HashSet<long> projected = new();

        public Func<long, ApplyOutcome> OnApply { get; set; } = _ => ApplyOutcome.Success;

        public Dictionary<long, int> ApplyCalls { get; } = new();

        public IReadOnlyCollection<long> AllIds => records.Select(r => r.Id!.Value).ToList();

        public void Seed(long id, DateTimeOffset receivedAt) =>
            records.Add(new RawTelemetryRecord(
                Id: id,
                Source: "raw-otlp",
                TraceId: $"t{id}",
                ReceivedAt: receivedAt,
                ResourceAttributesJson: null,
                PayloadJson: """{"resourceSpans":[]}"""));

        public bool IsProjected(long id) => projected.Contains(id);

        public IReadOnlyList<RawTelemetryRecord> ListUnprocessedForProjection(int limit) =>
            records.Where(r => !projected.Contains(r.Id!.Value)).Take(limit).ToList();

        public bool ApplyProjection(
            long rawRecordId,
            string source,
            DateTimeOffset receivedAt,
            MonitorRecordProjection projection,
            DateTimeOffset projectedAt)
        {
            ApplyCalls[rawRecordId] = ApplyCalls.GetValueOrDefault(rawRecordId) + 1;
            switch (OnApply(rawRecordId))
            {
                case ApplyOutcome.Busy:
                    throw new PersistenceBusyException();
                case ApplyOutcome.Fail:
                    throw new InvalidOperationException("projection boom");
                default:
                    projected.Add(rawRecordId);
                    return true;
            }
        }

        public MonitorProjectionStatus GetProjectionStatus()
        {
            var unprocessed = records.Where(r => !projected.Contains(r.Id!.Value)).ToList();
            var oldest = unprocessed.Count == 0 ? (DateTimeOffset?)null : unprocessed.Min(r => r.ReceivedAt);
            return new MonitorProjectionStatus(unprocessed.Count, oldest);
        }

        public MonitorProjectionPage<MonitorIngestionRow> ListMonitorIngestions(long afterRawRecordId, int limit) =>
            throw new NotSupportedException();

        public MonitorProjectionPage<MonitorTraceRow> ListMonitorTraces(long afterId, int limit) =>
            throw new NotSupportedException();

        public RawTelemetryRecord? GetRawRecordById(long id) =>
            throw new NotSupportedException();
    }
}

using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class ProjectionRetentionFenceTests
{
    [Fact]
    public async Task ApplyProjection_GrantLostAfterRead_PublishesNothing()
    {
        using var temp = new MonitorTempDirectory();
        var store = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = Raw("lost-trace", "lost-span");
        var rawRecordId = store.Insert(record);
        var read = await store.ListUnprocessedForProjectionAsync(100, RetentionReadKind.Operation, CancellationToken.None);
        await using var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        RawTelemetryRecord retained;
        using (var reference = lease.AcquireValueReference())
        {
            retained = Assert.Single(reference.Value);
        }
        Assert.IsType<MutableTimeProvider>(temp.TimeProvider).Advance(RetentionV1Constants.LeaseDuration);

        var applied = store.ApplyProjection(
            rawRecordId,
            retained.Source,
            retained.ReceivedAt,
            MonitorProjectionBuilder.Build(retained),
            temp.TimeProvider.GetUtcNow(),
            lease);

        Assert.False(applied);
        Assert.Empty(store.ListMonitorIngestions(0, 100).Items);
        Assert.Empty(store.ListMonitorTraces(0, 100).Items);
        Assert.Empty(store.GetSpansForTrace("lost-trace"));
    }

    [Fact]
    public async Task ApplySpanProjection_GrantLostAfterRead_LeavesAllProjectionTablesUnchanged()
    {
        using var temp = new MonitorTempDirectory();
        var store = temp.CreateRawStore(RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        var record = Raw("span-trace", "span-id");
        var rawRecordId = store.Insert(record);
        await ProjectTraceAsync(store, rawRecordId);
        var beforeIngestions = store.ListMonitorIngestions(0, 100).Items.ToArray();
        var beforeTraces = store.ListMonitorTraces(0, 100).Items.ToArray();
        var read = await store.ListUnprocessedForSpanProjectionAsync(100, RetentionReadKind.Operation, CancellationToken.None);
        await using var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        RawTelemetryRecord retained;
        using (var reference = lease.AcquireValueReference())
        {
            retained = Assert.Single(reference.Value);
        }
        Assert.IsType<MutableTimeProvider>(temp.TimeProvider).Advance(RetentionV1Constants.LeaseDuration);

        var applied = store.ApplySpanProjection(
            rawRecordId,
            MonitorSpanProjectionBuilder.Build(retained),
            temp.TimeProvider.GetUtcNow(),
            lease);

        Assert.False(applied);
        Assert.Equal(beforeIngestions, store.ListMonitorIngestions(0, 100).Items);
        Assert.Equal(beforeTraces, store.ListMonitorTraces(0, 100).Items);
        Assert.Empty(store.GetSpansForTrace("span-trace"));
    }

    [Fact]
    public async Task ApplyProjection_FenceHoldsEveryCanonicalScopeAcrossOneClockSampleEveryProofAndCommit()
    {
        using var temp = new MonitorTempDirectory();
        var observed = new List<MonitorProjectionPublicationCheckpoint>();
        IReadOnlyList<RetentionReadGrant>? grants = null;
        var clock = new CountingTimeProvider(DateTimeOffset.UnixEpoch);
        temp.TimeProvider = clock;
        var store = new RawTelemetryStore(
            temp.DatabasePath,
            temp.RetentionContext,
            clock,
            RawTelemetryStoreConnectionOptions.MonitorWriter,
            projectionPublicationCheckpoint: checkpoint =>
            {
                observed.Add(checkpoint);
                if (checkpoint is MonitorProjectionPublicationCheckpoint.AfterPublicationScopesAcquiredBeforeClockSample
                    or MonitorProjectionPublicationCheckpoint.AfterClockSampleBeforeProof
                    or MonitorProjectionPublicationCheckpoint.AfterGrantProof
                    or MonitorProjectionPublicationCheckpoint.BeforeCommit)
                {
                    Assert.NotNull(grants);
                    Assert.All(grants!, AssertScopeHeldByAnotherThread);
                }
            });
        store.CreateMonitorSchema();
        var firstId = store.Insert(Raw("first", "span-1"));
        store.Insert(Raw("second", "span-2"));
        var read = await store.ListUnprocessedForProjectionAsync(100, RetentionReadKind.Operation, CancellationToken.None);
        await using var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        grants = lease.Grants;
        RawTelemetryRecord retained;
        using (var reference = lease.AcquireValueReference())
        {
            retained = reference.Value[0];
        }
        var projectedAt = clock.GetUtcNow();
        clock.ResetSampleCount();

        Assert.True(store.ApplyProjection(
            firstId,
            retained.Source,
            retained.ReceivedAt,
            MonitorProjectionBuilder.Build(retained),
            projectedAt,
            lease));

        Assert.Equal(2, observed.Count(item => item == MonitorProjectionPublicationCheckpoint.AfterGrantProof));
        Assert.Equal(
            [
                MonitorProjectionPublicationCheckpoint.AfterTransactionBeganBeforePublicationScopes,
                MonitorProjectionPublicationCheckpoint.AfterPublicationScopesAcquiredBeforeClockSample,
                MonitorProjectionPublicationCheckpoint.AfterClockSampleBeforeProof,
                MonitorProjectionPublicationCheckpoint.AfterGrantProof,
                MonitorProjectionPublicationCheckpoint.AfterGrantProof,
                MonitorProjectionPublicationCheckpoint.BeforeCommit,
                MonitorProjectionPublicationCheckpoint.AfterCommit,
            ],
            observed);
        Assert.Equal(1, clock.SampleCount);
    }

    private static async Task ProjectTraceAsync(RawTelemetryStore store, long rawRecordId)
    {
        var read = await store.ListUnprocessedForProjectionAsync(100, RetentionReadKind.Operation, CancellationToken.None);
        await using var lease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        using var reference = lease.AcquireValueReference();
        var retained = Assert.Single(reference.Value);
        Assert.True(store.ApplyProjection(
            rawRecordId,
            retained.Source,
            retained.ReceivedAt,
            MonitorProjectionBuilder.Build(retained),
            store.Clock.GetUtcNow(),
            lease));
    }

    private static void AssertScopeHeldByAnotherThread(RetentionReadGrant grant)
    {
        var entered = false;
        var thread = new Thread(() =>
        {
            if (!grant.TryEnterLeasePublication(out var publication))
                return;
            entered = true;
            publication.Dispose();
        });
        thread.Start();
        thread.Join();
        Assert.False(entered);
    }

    private static RawTelemetryRecord Raw(string traceId, string spanId) =>
        new(
            Id: null,
            Source: "raw-otlp",
            TraceId: traceId,
            ReceivedAt: DateTimeOffset.UnixEpoch,
            ResourceAttributesJson: null,
            PayloadJson: $$"""
                {"resourceSpans":[{"scopeSpans":[{"spans":[{"traceId":"{{traceId}}","spanId":"{{spanId}}","name":"chat"}]}]}]}
                """);

    private sealed class CountingTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public int SampleCount { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            SampleCount++;
            return value;
        }

        public void ResetSampleCount() => SampleCount = 0;
    }
}

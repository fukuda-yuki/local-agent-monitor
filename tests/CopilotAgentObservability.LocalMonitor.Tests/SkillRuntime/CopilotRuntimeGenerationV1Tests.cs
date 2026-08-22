using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class CopilotRuntimeGenerationV1Tests
{
    [Fact]
    public void AdmittedGeneration_AcquiresCapability_WithUncancelledWorkToken()
    {
        var generation = NewAdmittedGeneration();

        var acquired = generation.TryAcquireOperationCapability(CancellationToken.None, out var capability);

        Assert.True(acquired);
        Assert.NotNull(capability);
        Assert.False(capability!.WorkToken.IsCancellationRequested);
        Assert.Same(generation, capability.Owner);
        Assert.Equal(1, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public void AdmittedGeneration_FreezeCertifiedVersionAndProtocol()
    {
        var generation = NewAdmittedGeneration();

        Assert.Equal("1.0.65", generation.FrozenVersion);
        Assert.Equal(3, generation.FrozenProtocolVersion);
        Assert.True(generation.IsAdmitted);
        Assert.False(generation.IsInvalid);
    }

    [Fact]
    public void CallerTokenCancellation_PropagatesToWorkToken()
    {
        var generation = NewAdmittedGeneration();
        using var callerCancellation = new CancellationTokenSource();
        Assert.True(generation.TryAcquireOperationCapability(callerCancellation.Token, out var capability));

        callerCancellation.Cancel();

        Assert.True(capability!.WorkToken.IsCancellationRequested);
    }

    [Fact]
    public void Invalidation_CancelsOnlyUnsealedCapabilities_AndClosesAdmission()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var unsealed));
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var sealedCapability));
        Assert.True(sealedCapability!.TrySealResponse());

        generation.Invalidate();

        Assert.True(unsealed!.WorkToken.IsCancellationRequested);
        Assert.False(sealedCapability!.WorkToken.IsCancellationRequested);
        Assert.False(generation.IsAdmitted);
        Assert.True(generation.IsInvalid);
        Assert.False(generation.TryAcquireOperationCapability(CancellationToken.None, out _));
    }

    [Fact]
    public void DrainClose_StopsAdmission_WithoutCancellingOutstandingWork()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        generation.CloseAdmissionForDrain();

        Assert.False(generation.IsAdmitted);
        Assert.False(generation.IsInvalid);
        Assert.False(capability!.WorkToken.IsCancellationRequested);
        Assert.False(generation.TryAcquireOperationCapability(CancellationToken.None, out _));
    }

    [Fact]
    public void SharedShutdownGate_StopsDirectGenerationCapabilityAcquisition()
    {
        var gate = new SkillHostShutdownGateV1();
        var admission = new CopilotRuntimeAdmissionV1(gate);
        var generation = admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _)!;

        Assert.True(gate.TryStartNormalShutdown());

        Assert.False(generation.TryAcquireOperationCapability(CancellationToken.None, out _));
        Assert.Equal(0, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public void Seal_IsAtomicPerCapability_AndRecordsWonKind()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        Assert.True(capability!.TrySealCommit());
        Assert.Equal(SkillRuntimeTerminalSealV1.Commit, capability.WonSealKind);
        Assert.False(capability.TrySealResponse());
        Assert.False(capability.TrySealV2NonCommitResponse());
        Assert.False(capability.TrySealReplaySuccess());
        Assert.Equal(SkillRuntimeTerminalSealV1.Commit, capability.WonSealKind);
    }

    [Fact]
    public void Seal_FailsAfterInvalidation_AndForForeignOwner()
    {
        var generation = NewAdmittedGeneration();
        var otherGeneration = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        Assert.True(otherGeneration.TryAcquireOperationCapability(CancellationToken.None, out var foreignCapability));

        generation.Invalidate();

        Assert.False(capability!.TrySealResponse());
        Assert.False(generation.TrySealCapability(foreignCapability!, SkillRuntimeTerminalSealV1.Response));
    }

    [Fact]
    public void Abandon_RequiresWonSeal_AndStaysValidAfterInvalidation()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        Assert.False(capability!.TryAbandonWonSeal());

        Assert.True(capability.TrySealResponse());
        generation.Invalidate();

        Assert.True(capability.TryAbandonWonSeal());
        Assert.False(capability.TryAbandonWonSeal());
    }

    [Fact]
    public void Release_IsExactlyOnce_AndIdempotent()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        Assert.Equal(1, generation.OutstandingCapabilityCount);

        capability!.Release();
        capability.Release();
        capability.Release();

        Assert.Equal(0, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public void Release_FromManyThreads_RemovesCapabilityExactlyOnce()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        Parallel.For(0, 64, _ => capability!.Release());

        Assert.Equal(0, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public void CancelWork_AfterDisposal_IsNoOp()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        capability!.DisposeWorkCancellation();

        capability.CancelWork();
    }

    [Fact]
    public void DisposeWorkCancellation_AfterCancellation_IsSafe()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        capability!.CancelWork();

        capability.DisposeWorkCancellation();
    }

    [Fact]
    public void DisposeWorkCancellation_CalledTwice_IsSafe()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        capability!.DisposeWorkCancellation();
        capability.DisposeWorkCancellation();
    }

    [Fact]
    public void WorkToken_AfterDisposal_RemainsReadableWithCancellationState()
    {
        var generation = NewAdmittedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        capability!.CancelWork();
        var tokenBeforeDisposal = capability.WorkToken;

        capability.DisposeWorkCancellation();

        Assert.Equal(tokenBeforeDisposal, capability.WorkToken);
        Assert.True(capability.WorkToken.IsCancellationRequested);
    }

    [Fact]
    public void Invalidate_ConcurrentWithRelease_DoesNotThrow()
    {
        const int iterations = 1_000;
        using var barrier = new Barrier(3);
        var exceptions = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        CopilotRuntimeGenerationV1? generation = null;
        CopilotRuntimeOperationCapabilityV1? capability = null;

        var invalidateThread = new Thread(() =>
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                barrier.SignalAndWait();
                try
                {
                    generation!.Invalidate();
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }

                barrier.SignalAndWait();
            }
        });
        var releaseThread = new Thread(() =>
        {
            for (var iteration = 0; iteration < iterations; iteration++)
            {
                barrier.SignalAndWait();
                try
                {
                    capability!.Release();
                }
                catch (Exception exception)
                {
                    exceptions.Enqueue(exception);
                }

                barrier.SignalAndWait();
            }
        });

        invalidateThread.Start();
        releaseThread.Start();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            generation = NewAdmittedGeneration();
            Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out capability));
            barrier.SignalAndWait();
            barrier.SignalAndWait();
        }

        invalidateThread.Join();
        releaseThread.Join();

        Assert.Empty(exceptions);
    }

    private static CopilotRuntimeGenerationV1 NewAdmittedGeneration() => new(new FakeSkillRuntimeClient());
}

public sealed class CopilotRuntimeAdmissionV1Tests
{
    [Fact]
    public void FreshAdmission_HasNoCurrentGeneration()
    {
        var admission = new CopilotRuntimeAdmissionV1();

        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.False(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out _));
        Assert.False(admission.IsShutdownClosed);
    }

    [Fact]
    public void PublishAdmittedGeneration_MakesItCurrentAndAdmitted()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var client = new FakeSkillRuntimeClient();

        var generation = admission.PublishAdmittedGeneration(client, out var replaced);

        Assert.NotNull(generation);
        Assert.Null(replaced);
        Assert.True(admission.TryGetCurrentAdmittedGeneration(out var current));
        Assert.Same(generation, current);
        Assert.True(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var capability));
        Assert.Same(generation, capability!.Owner);
        capability.Release();
    }

    [Fact]
    public void PublishAdmittedGeneration_InvalidatesReplacedGeneration_AndNotifiesObservers()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var firstClient = new FakeSkillRuntimeClient();
        var secondClient = new FakeSkillRuntimeClient();
        var firstGeneration = admission.PublishAdmittedGeneration(firstClient, out _);
        Assert.True(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var outstanding));
        var observerNotifications = 0;
        admission.RegisterInvalidationObserver(() => observerNotifications++);

        var secondGeneration = admission.PublishAdmittedGeneration(secondClient, out var replaced);

        Assert.NotNull(secondGeneration);
        Assert.Same(firstGeneration, replaced);
        Assert.True(firstGeneration!.IsInvalid);
        Assert.False(firstGeneration.IsAdmitted);
        Assert.True(outstanding!.WorkToken.IsCancellationRequested);
        Assert.Same(secondGeneration, Assert.IsType<CopilotRuntimeGenerationV1>(GetRequiredCurrent(admission)));
        Assert.Equal(1, observerNotifications);
    }

    [Fact]
    public void PublishAdmittedGeneration_AfterShutdownClose_ReturnsNull()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        admission.CloseForShutdown();

        var generation = admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out var replaced);

        Assert.Null(generation);
        Assert.Null(replaced);
        Assert.True(admission.IsShutdownClosed);
    }

    [Fact]
    public void InvalidateCurrentGeneration_RemovesInvalidatesAndNotifies()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var generation = admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);
        var observerNotifications = 0;
        admission.RegisterInvalidationObserver(() => observerNotifications++);

        var removed = admission.InvalidateCurrentGeneration();

        Assert.Same(generation, removed);
        Assert.True(generation!.IsInvalid);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(1, observerNotifications);
        Assert.Null(admission.InvalidateCurrentGeneration());
        Assert.Equal(1, observerNotifications);
    }

    [Fact]
    public void InvalidateGenerationIfCurrent_StaleGeneration_KeepsNewerCurrent()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var staleGeneration = admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);
        var newerGeneration = admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);

        var removed = admission.InvalidateGenerationIfCurrent(staleGeneration!);

        Assert.Null(removed);
        // PublishAdmittedGeneration already invalidated the replaced generation; the stale
        // generation is no longer current, so InvalidateGenerationIfCurrent must not act again.
        Assert.True(staleGeneration!.IsInvalid);
        Assert.Same(newerGeneration, GetRequiredCurrent(admission));
        Assert.True(newerGeneration!.IsAdmitted);
    }

    [Fact]
    public void InvalidateGenerationIfCurrent_CurrentGeneration_RemovesAndInvalidates()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var generation = admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);
        var observerNotifications = 0;
        admission.RegisterInvalidationObserver(() => observerNotifications++);

        var removed = admission.InvalidateGenerationIfCurrent(generation!);

        Assert.Same(generation, removed);
        Assert.True(generation!.IsInvalid);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(1, observerNotifications);
    }

    [Fact]
    public void CloseForShutdown_DrainClosesCurrentGeneration_AndBlocksCapabilityAcquisition()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var generation = admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);

        admission.CloseForShutdown();

        Assert.True(admission.IsShutdownClosed);
        Assert.False(generation!.IsAdmitted);
        Assert.False(generation.IsInvalid);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.False(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out _));
    }

    private static CopilotRuntimeGenerationV1 GetRequiredCurrent(CopilotRuntimeAdmissionV1 admission)
    {
        Assert.True(admission.TryGetCurrentAdmittedGeneration(out var generation));
        return generation!;
    }
}

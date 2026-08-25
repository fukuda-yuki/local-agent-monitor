using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class CopilotRuntimeCandidateLifecycleV1Tests
{
    [Fact]
    public async Task CandidateCreation_CapturesLeaseTokenOnceBeforeExposure()
    {
        var order = new List<string>();
        var scope = new CountingScope(order, canceled: false);
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());

        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);

        Assert.Equal(1, scope.LeaseTokenReads);
        Assert.True(candidate.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(candidate));
    }

    [Fact]
    public async Task PreCanceledLeaseDuringCreation_NeverPublishesAndCleansOnce()
    {
        var order = new List<string>();
        var scope = new CountingScope(order, canceled: true);
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());

        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        await candidate.CleanupTask;

        Assert.Equal(1, scope.LeaseTokenReads);
        Assert.True(candidate.IsInvalid);
        Assert.False(candidate.TryMarkReady());
        Assert.False(await admission.PublishCandidateAsync(candidate));
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(["client", "scope"], order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShutdownOrLeaseLossAfterRegistrationBeforeExposure_NeverPublishesOrDeadlocks(
        bool loseLease)
    {
        var order = new List<string>();
        var gate = new SkillHostShutdownGateV1();
        var scope = new OrderedScope(order, "scope");
        var admission = new CopilotRuntimeAdmissionV1(
            gate,
            candidateRegistrationAttachedForTesting: () =>
            {
                if (loseLease) scope.LoseLease();
                else gate.TryStartNormalShutdown();
            });

        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        await candidate.CleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(candidate.IsInvalid);
        Assert.False(candidate.TryMarkReady());
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task Candidate_IsInvisibleUntilExactObjectPublication_ThenReplacementCleansClientBeforeScope()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var first = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client-1"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope-1"));
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.True(first.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(first));
        Assert.Same(first, AssertCurrent(admission));

        var second = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client-2"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope-2"));
        Assert.True(second.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(second));

        Assert.Same(second, AssertCurrent(admission));
        Assert.Equal(["client-1", "scope-1"], order);
    }

    [Fact]
    public async Task Replacement_IsCurrentAndUsableWhilePublicationAwaitsHeldOldCapabilityDrain()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var first = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client-1"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope-1"));
        Assert.True(first.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(first));
        Assert.True(first.TryAcquireOperationCapability(CancellationToken.None, out var held));
        var second = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client-2"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope-2"));
        Assert.True(second.TryMarkReady());

        var publishing = admission.PublishCandidateAsync(second);
        await Task.Yield();

        Assert.False(publishing.IsCompleted);
        Assert.Same(second, AssertCurrent(admission));
        Assert.True(second.TryAcquireOperationCapability(CancellationToken.None, out var secondCapability));
        Assert.Empty(order);

        held.Release();
        Assert.True(await publishing);
        Assert.Same(second, AssertCurrent(admission));
        Assert.Equal(["client-1", "scope-1"], order);
        secondCapability.Release();
    }

    [Fact]
    public async Task ConcurrentReadyPublications_AreSerializedInCallOrder()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var initial = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client-0"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope-0"));
        Assert.True(initial.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(initial));
        Assert.True(initial.TryAcquireOperationCapability(CancellationToken.None, out var held));
        var first = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client-1"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope-1"));
        var second = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client-2"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope-2"));
        Assert.True(first.TryMarkReady());
        Assert.True(second.TryMarkReady());

        var publishFirst = admission.PublishCandidateAsync(first);
        var publishSecond = admission.PublishCandidateAsync(second);
        await Task.Yield();
        Assert.Same(first, AssertCurrent(admission));
        Assert.False(publishFirst.IsCompleted);
        Assert.False(publishSecond.IsCompleted);
        held.Release();

        Assert.True(await publishFirst);
        Assert.True(await publishSecond);
        Assert.Same(second, AssertCurrent(admission));
        Assert.Equal(["client-0", "scope-0", "client-1", "scope-1"], order);
    }

    [Fact]
    public async Task CandidateInvalidatedAfterAtomicSwap_IsCleanedWithoutRestoringOld()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var current = admission.CreateUnpublishedCandidate(new OrderedClient(order, "current-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "current-scope"));
        Assert.True(current.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(current));
        Assert.True(current.TryAcquireOperationCapability(CancellationToken.None, out var held));
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "candidate-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "candidate-scope"));
        Assert.True(candidate.TryMarkReady());

        var publishing = admission.PublishCandidateAsync(candidate);
        await Task.Yield();
        admission.InvalidateCandidate(candidate);
        held.Release();

        Assert.True(await publishing);
        await candidate.CleanupTask;
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(["current-client", "current-scope", "candidate-client", "candidate-scope"], order);
    }

    [Fact]
    public async Task ShutdownAfterAtomicSwap_CleansNewAndOldExactlyOnceWithoutRestoration()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var current = admission.CreateUnpublishedCandidate(new OrderedClient(order, "current-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "current-scope"));
        Assert.True(current.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(current));
        Assert.True(current.TryAcquireOperationCapability(CancellationToken.None, out var held));
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "candidate-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "candidate-scope"));
        Assert.True(candidate.TryMarkReady());

        var publishing = admission.PublishCandidateAsync(candidate);
        await Task.Yield();
        var shutdown = admission.CloseForShutdownAndDrainAsync(CancellationToken.None);
        held.Release();

        Assert.True(await publishing);
        await shutdown;
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(1, order.Count(marker => marker == "current-client"));
        Assert.Equal(1, order.Count(marker => marker == "current-scope"));
        Assert.Equal(1, order.Count(marker => marker == "candidate-client"));
        Assert.Equal(1, order.Count(marker => marker == "candidate-scope"));
    }

    [Fact]
    public async Task FaultingInvalidationObserver_CannotInterruptReplacementCleanupOrPublication()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        admission.RegisterInvalidationObserver(_ => throw new InvalidOperationException("observer"));
        var current = admission.CreateUnpublishedCandidate(new OrderedClient(order, "current-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "current-scope"));
        Assert.True(current.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(current));
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "candidate-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "candidate-scope"));
        Assert.True(candidate.TryMarkReady());

        Assert.True(await admission.PublishCandidateAsync(candidate));

        Assert.Same(candidate, AssertCurrent(admission));
        Assert.Equal(["current-client", "current-scope"], order);
    }

    [Fact]
    public async Task RawCandidate_CannotPublish_AndRefusalCleansOnlyCandidate()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var current = admission.CreateUnpublishedCandidate(new OrderedClient(order, "current-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "current-scope"));
        Assert.True(current.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(current));
        var raw = admission.CreateUnpublishedCandidate(new OrderedClient(order, "raw-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "raw-scope"));

        Assert.False(await admission.PublishCandidateAsync(raw));

        Assert.Same(current, AssertCurrent(admission));
        Assert.Equal(["raw-client", "raw-scope"], order);
    }

    [Fact]
    public async Task ReadyCandidateInvalidatedBeforePublication_CannotReplaceCurrent()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var current = admission.CreateUnpublishedCandidate(new OrderedClient(order, "current-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "current-scope"));
        Assert.True(current.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(current));
        var invalid = admission.CreateUnpublishedCandidate(new OrderedClient(order, "invalid-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "invalid-scope"));
        Assert.True(invalid.TryMarkReady());
        admission.InvalidateCandidate(invalid);

        Assert.False(await admission.PublishCandidateAsync(invalid));

        Assert.Same(current, AssertCurrent(admission));
        Assert.Equal(["invalid-client", "invalid-scope"], order);
    }

    [Fact]
    public void Candidate_CannotBecomeReadyUntilPreparationCapabilitiesAreReleased()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient([], "client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope([], "scope"));
        Assert.True(candidate.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        Assert.False(candidate.TryMarkReady());
        capability.Release();
        Assert.True(candidate.TryMarkReady());
    }

    [Fact]
    public async Task LeaseLoss_InvalidatesAndCleansOnlyExactCurrentCandidate()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var scope = new OrderedScope(order, "scope");
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        Assert.True(candidate.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(candidate));

        scope.LoseLease();
        await candidate.CleanupTask;

        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task Discard_RacingLeaseLoss_AwaitsTheSameCleanupTask()
    {
        var order = new List<string>();
        var releaseClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var scope = new OrderedScope(order, "scope");
        var candidate = admission.CreateUnpublishedCandidate(new BlockingClient(order, releaseClient.Task),
            SkillInvocationV2TestIdentity.V1065, scope);

        scope.LoseLease();
        var discard = admission.DiscardCandidateAsync(candidate);
        await Task.Yield();
        Assert.False(discard.IsCompleted);
        releaseClient.SetResult();
        Assert.True(await discard);

        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task ShutdownObservedAfterReadyToReservedTransition_RefusesAndCleansOnce()
    {
        var gate = new SkillHostShutdownGateV1();
        var admission = new CopilotRuntimeAdmissionV1(gate, () => gate.TryStartNormalShutdown());
        var invalidations = 0;
        admission.RegisterInvalidationObserver(_ => invalidations++);
        var candidate = CreateReadyCandidate(admission, []);

        var reservation = await admission.TryReservePublicationAsync(candidate);

        Assert.Null(reservation);
        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, invalidations);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityCancellationObservedAfterReservedTransition_RefusesBeforeCallbackCanAcquireAdmissionLock(
        bool loseLease)
    {
        using var callerCancellation = new CancellationTokenSource();
        OrderedScope? scope = null;
        Thread? cancellationThread = null;
        Exception? cancellationException = null;
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1(), () =>
        {
            cancellationThread = new Thread(() =>
            {
                try
                {
                    if (loseLease) scope!.LoseLease();
                    else callerCancellation.Cancel();
                }
                catch (Exception exception) { cancellationException = exception; }
            });
            cancellationThread.IsBackground = true;
            cancellationThread.Start();
            var authorityToken = loseLease ? scope!.LeaseLostToken : callerCancellation.Token;
            Assert.True(authorityToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        });
        var order = new List<string>();
        scope = new OrderedScope(order, "scope");
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        Assert.True(candidate.TryMarkReady());

        CopilotRuntimeAdmissionV1.PublicationReservation? reservation;
        var cancellationJoined = false;
        try { reservation = await admission.TryReservePublicationAsync(candidate, callerCancellation.Token); }
        finally
        {
            cancellationJoined = cancellationThread?.Join(TimeSpan.FromSeconds(5)) ?? true;
        }

        Assert.True(cancellationJoined);
        Assert.Null(cancellationException);
        Assert.Null(reservation);
        Assert.True(candidate.IsInvalid);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task InvalidationAfterPublication_RemovesAndCleansExactCandidate()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope"));
        Assert.True(candidate.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(candidate));

        admission.InvalidateCandidate(candidate);
        await candidate.CleanupTask;

        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task ReservedCandidate_RemainsInvisibleAndRejectsCapabilities()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope"));
        Assert.True(candidate.TryMarkReady());

        await using (var reservation = await admission.TryReservePublicationAsync(candidate))
        {
            Assert.NotNull(reservation);
            Assert.False(candidate.TryAcquireOperationCapability(CancellationToken.None, out _));
            Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        }

        Assert.True(candidate.IsInvalid);
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task Invalidation_IsImmediateWithoutReservation_AndDeferredWithReservation()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var immediate = admission.CreateUnpublishedCandidate(new OrderedClient([], "immediate-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope([], "immediate-scope"));
        admission.InvalidateCandidate(immediate);
        Assert.True(immediate.IsInvalid);

        var reserved = admission.CreateUnpublishedCandidate(new OrderedClient([], "reserved-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope([], "reserved-scope"));
        Assert.True(reserved.TryMarkReady());
        await using (var reservation = await admission.TryReservePublicationAsync(reserved))
        {
            Assert.NotNull(reservation);
            admission.InvalidateCandidate(reserved);
            Assert.False(reserved.IsInvalid);
            await reservation!.CommitAsync();
            Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        }

        await reserved.CleanupTask;
        Assert.True(reserved.IsInvalid);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task ReservedExplicitInvalidation_ClosesAdmissionBeforeSerializedInvalidation()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var invalidations = 0;
        admission.RegisterInvalidationObserver(_ => invalidations++);
        var candidate = CreateReadyCandidate(admission, order);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate));

        admission.InvalidateCandidate(candidate);
        Assert.False(candidate.IsInvalid);
        await reservation.CommitAsync();

        Assert.False(candidate.TryAcquireOperationCapability(CancellationToken.None, out _));
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.False(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out _));
        Assert.Equal(0, invalidations);
        Assert.Empty(order);

        await reservation.DisposeAsync();
        await candidate.CleanupTask;
        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, invalidations);
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task ReservedCallerCancellation_ClosesAdmissionBeforeSerializedInvalidation()
    {
        using var callerCancellation = new CancellationTokenSource();
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var invalidations = 0;
        admission.RegisterInvalidationObserver(_ => invalidations++);
        var candidate = CreateReadyCandidate(admission, order);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate, callerCancellation.Token));

        callerCancellation.Cancel();
        Assert.False(candidate.IsInvalid);
        await reservation.CommitAsync();

        Assert.False(candidate.TryAcquireOperationCapability(CancellationToken.None, out _));
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.False(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out _));
        Assert.Equal(0, invalidations);
        Assert.Empty(order);

        await reservation.DisposeAsync();
        await candidate.CleanupTask;
        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, invalidations);
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task ReservedLeaseLoss_ClosesAdmissionBeforeSerializedInvalidation()
    {
        var order = new List<string>();
        var scope = new OrderedScope(order, "scope");
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var invalidations = 0;
        admission.RegisterInvalidationObserver(_ => invalidations++);
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        Assert.True(candidate.TryMarkReady());
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate));

        scope.LoseLease();
        Assert.False(candidate.IsInvalid);
        await reservation.CommitAsync();

        Assert.False(candidate.TryAcquireOperationCapability(CancellationToken.None, out _));
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.False(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out _));
        Assert.Equal(0, invalidations);
        Assert.Empty(order);

        await reservation.DisposeAsync();
        await candidate.CleanupTask;
        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, invalidations);
        Assert.Equal(["client", "scope"], order);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthorityLossImmediatelyBeforeBind_RefusesReservationAndPreservesPrior(
        bool loseLease)
    {
        var order = new List<string>();
        var scope = new OrderedScope(order, "scope");
        CopilotRuntimeGenerationV1? candidate = null;
        var admission = new CopilotRuntimeAdmissionV1(
            new SkillHostShutdownGateV1(),
            publicationAuthorityBindingForTesting: () =>
            {
                if (candidate is null) return;
                if (loseLease) scope.LoseLease();
                else candidate.CloseAdmissionForDrain();
            });
        var prior = admission.PublishReadyTestCandidate(new OrderedClient([], "prior"), out _);
        candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        Assert.True(candidate.TryMarkReady());

        var reservation = await admission.TryReservePublicationAsync(candidate);

        Assert.Null(reservation);
        Assert.Same(prior, AssertCurrent(admission));
        Assert.True(candidate.IsInvalid);
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task ReservedInvalidation_ClosesAdmissionWhileCommitDrainsReplacedGeneration()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var prior = admission.PublishReadyTestCandidate(new OrderedClient([], "prior"), out _);
        var priorInvalidated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        admission.RegisterInvalidationObserver(generation =>
        {
            if (ReferenceEquals(generation, prior)) priorInvalidated.TrySetResult();
        });
        Assert.True(prior.TryAcquireOperationCapability(CancellationToken.None, out var held));
        var candidate = CreateReadyCandidate(admission, []);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate));
        admission.InvalidateCandidate(candidate);

        var commit = reservation.CommitAsync();
        await priorInvalidated.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(commit.IsCompleted);
        Assert.False(candidate.IsInvalid);
        Assert.False(candidate.TryAcquireOperationCapability(CancellationToken.None, out _));
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.False(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out _));

        held!.Release();
        await commit;
        await reservation.DisposeAsync();
        await candidate.CleanupTask;
        Assert.True(candidate.IsInvalid);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task AuthorityCancellationDuringCommit_DirectAuthorityAlwaysDeniesAdmission(
        bool loseLease,
        bool blockBeforeAdmissionClose)
    {
        using var callerCancellation = new CancellationTokenSource();
        using var commitEnteredOwnerLock = new ManualResetEventSlim();
        using var allowCommit = new ManualResetEventSlim();
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCallback = new ManualResetEventSlim();
        using var commitCallReturned = new ManualResetEventSlim();
        var order = new List<string>();
        var scope = new OrderedScope(order, "scope");
        var admission = new CopilotRuntimeAdmissionV1(
            new SkillHostShutdownGateV1(),
            commitOwnerLockAcquiredForTesting: () =>
            {
                commitEnteredOwnerLock.Set();
                allowCommit.Wait();
            },
            authorityCancellationRequestedForTesting: blockBeforeAdmissionClose ? () =>
            {
                callbackEntered.Set();
                allowCallback.Wait();
            } : null,
            authorityCancellationObservedForTesting: () =>
            {
                if (blockBeforeAdmissionClose) return;
                callbackEntered.Set();
                allowCallback.Wait();
            });
        var invalidations = 0;
        admission.RegisterInvalidationObserver(_ => invalidations++);
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        Assert.True(candidate.TryMarkReady());
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate, callerCancellation.Token));
        Task? commit = null;
        Exception? commitException = null;
        Exception? cancellationException = null;
        var commitThread = new Thread(() =>
        {
            try { commit = reservation.CommitAsync(); }
            catch (Exception exception) { commitException = exception; }
            finally { commitCallReturned.Set(); }
        }) { IsBackground = true };
        var cancellationThread = new Thread(() =>
        {
            try
            {
                if (loseLease) scope.LoseLease();
                else callerCancellation.Cancel();
            }
            catch (Exception exception) { cancellationException = exception; }
        }) { IsBackground = true };
        var commitEntered = false;
        var authorityRequested = false;
        var callbackReachedRequestedPhase = false;
        var commitReturned = false;
        var candidateAdmitted = false;
        var directAcquired = false;
        var currentVisible = false;
        var currentFileAcquired = false;
        var commitJoined = false;
        var cancellationJoined = false;

        try
        {
            commitThread.Start();
            commitEntered = commitEnteredOwnerLock.Wait(TimeSpan.FromSeconds(5));
            cancellationThread.Start();
            var authorityToken = loseLease ? scope.LeaseLostToken : callerCancellation.Token;
            authorityRequested = authorityToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            callbackReachedRequestedPhase = callbackEntered.Wait(TimeSpan.FromSeconds(5));
            allowCommit.Set();
            commitReturned = commitCallReturned.Wait(TimeSpan.FromSeconds(5));
            candidateAdmitted = candidate.IsAdmitted;
            directAcquired = candidate.TryAcquireOperationCapability(CancellationToken.None, out var direct);
            direct?.Release();
            currentVisible = admission.TryGetCurrentAdmittedGeneration(out _);
            currentFileAcquired = admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var currentFile);
            currentFile?.Release();
        }
        finally
        {
            allowCommit.Set();
            allowCallback.Set();
            commitJoined = commitThread.Join(TimeSpan.FromSeconds(5));
            cancellationJoined = cancellationThread.Join(TimeSpan.FromSeconds(5));
        }

        Assert.True(commitEntered);
        Assert.True(authorityRequested);
        Assert.True(callbackReachedRequestedPhase);
        Assert.True(commitReturned);
        Assert.False(candidateAdmitted);
        Assert.False(directAcquired);
        Assert.False(currentVisible);
        Assert.False(currentFileAcquired);
        Assert.True(commitJoined);
        Assert.True(cancellationJoined);
        Assert.Null(commitException);
        Assert.Null(cancellationException);
        Assert.NotNull(commit);
        await commit;
        await reservation.DisposeAsync();
        await candidate.CleanupTask;
        Assert.Equal(1, invalidations);
        Assert.Equal(["client", "scope"], order);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task CallerCancellationAfterCommittedReservationDisposal_DoesNotGovernPublishedGeneration()
    {
        using var callerCancellation = new CancellationTokenSource();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var candidate = CreateReadyCandidate(admission, []);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate, callerCancellation.Token));
        await reservation.CommitAsync();
        await reservation.DisposeAsync();

        callerCancellation.Cancel();

        Assert.Same(candidate, AssertCurrent(admission));
        Assert.True(candidate.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        Assert.False(capability!.WorkToken.IsCancellationRequested);
        Assert.True(capability.TrySealCommit());
        capability.Release();
        await admission.CloseForShutdownAndDrainAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CallerCancellationAfterRegistrationDisposalBeforeUnbind_StaysClosedAndInvalidates()
    {
        using var callerCancellation = new CancellationTokenSource();
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(
            new SkillHostShutdownGateV1(),
            reservationRegistrationDisposedForTesting: callerCancellation.Cancel);
        var invalidations = 0;
        admission.RegisterInvalidationObserver(_ => invalidations++);
        var candidate = CreateReadyCandidate(admission, order);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate, callerCancellation.Token));
        await reservation.CommitAsync();

        await reservation.DisposeAsync();
        await candidate.CleanupTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, invalidations);
        Assert.Equal(["client", "scope"], order);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task CallerCancellationRacingReservationDisposal_InvalidatesAndCleansOnce()
    {
        using var callerCancellation = new CancellationTokenSource();
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCallback = new ManualResetEventSlim();
        using var registrationDisposalStarted = new ManualResetEventSlim();
        using var disposeCallReturned = new ManualResetEventSlim();
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(
            new SkillHostShutdownGateV1(),
            authorityCancellationObservedForTesting: () =>
            {
                callbackEntered.Set();
                allowCallback.Wait();
            },
            reservationDisposingRegistrationForTesting: registrationDisposalStarted.Set);
        var invalidations = 0;
        admission.RegisterInvalidationObserver(_ => invalidations++);
        var candidate = CreateReadyCandidate(admission, order);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate, callerCancellation.Token));
        await reservation.CommitAsync();
        Exception? cancellationException = null;
        Exception? disposeException = null;
        Task? dispose = null;
        var cancellationThread = new Thread(() =>
        {
            try { callerCancellation.Cancel(); }
            catch (Exception exception) { cancellationException = exception; }
        }) { IsBackground = true };
        var disposeThread = new Thread(() =>
        {
            try { dispose = reservation.DisposeAsync().AsTask(); }
            catch (Exception exception) { disposeException = exception; }
            finally { disposeCallReturned.Set(); }
        }) { IsBackground = true };
        var callbackWasEntered = false;
        var disposeWasBlocked = false;
        var cancellationJoined = false;
        var disposeJoined = false;

        try
        {
            cancellationThread.Start();
            callbackWasEntered = callbackEntered.Wait(TimeSpan.FromSeconds(5));
            disposeThread.Start();
            Assert.True(registrationDisposalStarted.Wait(TimeSpan.FromSeconds(5)));
            disposeWasBlocked = !disposeCallReturned.IsSet;
        }
        finally
        {
            allowCallback.Set();
            cancellationJoined = cancellationThread.Join(TimeSpan.FromSeconds(5));
            disposeJoined = disposeThread.Join(TimeSpan.FromSeconds(5));
        }

        Assert.True(callbackWasEntered);
        Assert.True(disposeWasBlocked);
        Assert.True(cancellationJoined);
        Assert.True(disposeJoined);
        Assert.Null(cancellationException);
        Assert.Null(disposeException);
        Assert.NotNull(dispose);
        await dispose;
        await candidate.CleanupTask;
        Assert.Equal(1, invalidations);
        Assert.Equal(["client", "scope"], order);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task LeaseLossDirectlyCancelsAdmittedCapabilityAndRejectsSealBeforeCallbackProgress()
    {
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCallback = new ManualResetEventSlim();
        var scope = new OrderedScope([], "scope");
        var admission = new CopilotRuntimeAdmissionV1(
            new SkillHostShutdownGateV1(),
            authorityCancellationRequestedForTesting: () =>
            {
                callbackEntered.Set();
                allowCallback.Wait();
            });
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient([], "client"),
            SkillInvocationV2TestIdentity.V1065, scope);
        Assert.True(candidate.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(candidate));
        Assert.True(candidate.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        var cancellationThread = new Thread(scope.LoseLease) { IsBackground = true };
        var cancellationJoined = false;

        try
        {
            cancellationThread.Start();
            Assert.True(callbackEntered.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(capability!.WorkToken.IsCancellationRequested);
            Assert.False(capability.TrySealCommit());
            Assert.False(candidate.IsAdmitted);
        }
        finally
        {
            capability!.Release();
            allowCallback.Set();
            cancellationJoined = cancellationThread.Join(TimeSpan.FromSeconds(5));
        }

        Assert.True(cancellationJoined);
        await candidate.CleanupTask;
    }

    [Fact]
    public async Task ConcurrentCommits_PublishReservedCandidateOnce()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var prior = admission.PublishReadyTestCandidate(new OrderedClient([], "prior"), out _);
        Assert.True(prior.TryAcquireOperationCapability(CancellationToken.None, out var held));
        var candidate = CreateReadyCandidate(admission, []);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate));

        var first = reservation.CommitAsync();
        Assert.True(SpinWait.SpinUntil(() => ReferenceEquals(candidate,
            admission.TryGetCurrentAdmittedGeneration(out var current) ? current : null), TimeSpan.FromSeconds(5)));
        var second = reservation.CommitAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        held!.Release();
        await Task.WhenAll(first, second);
        await reservation.DisposeAsync();

        Assert.Same(candidate, AssertCurrent(admission));
        Assert.False(candidate.IsInvalid);
    }

    [Fact]
    public async Task CommitWinningDisposeRace_PublishesOnce()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var prior = admission.PublishReadyTestCandidate(new OrderedClient([], "prior"), out _);
        Assert.True(prior.TryAcquireOperationCapability(CancellationToken.None, out var held));
        var candidate = CreateReadyCandidate(admission, []);
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate));

        var commit = reservation.CommitAsync();
        Assert.True(SpinWait.SpinUntil(() => ReferenceEquals(candidate,
            admission.TryGetCurrentAdmittedGeneration(out var current) ? current : null), TimeSpan.FromSeconds(5)));
        var dispose = reservation.DisposeAsync().AsTask();
        Assert.False(commit.IsCompleted);
        Assert.False(dispose.IsCompleted);
        held!.Release();
        await Task.WhenAll(commit, dispose);

        Assert.Same(candidate, AssertCurrent(admission));
        Assert.False(candidate.IsInvalid);
    }

    [Fact]
    public async Task DisposeWinningCommitRace_AbortsOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var candidate = admission.CreateUnpublishedCandidate(new BlockingClient([], release.Task),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope([], "scope"));
        Assert.True(candidate.TryMarkReady());
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate));

        var dispose = reservation.DisposeAsync().AsTask();
        await Task.Yield();
        var commit = reservation.CommitAsync();
        Assert.False(dispose.IsCompleted);
        Assert.False(commit.IsCompleted);
        release.SetResult();
        await Task.WhenAll(dispose, commit);

        Assert.True(candidate.IsInvalid);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }

    [Fact]
    public async Task ConcurrentDisposals_AbortAndReleasePublicationGateOnce()
    {
        var order = new List<string>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var candidate = admission.CreateUnpublishedCandidate(new BlockingClient(order, release.Task),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope"));
        Assert.True(candidate.TryMarkReady());
        var reservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(candidate));

        var first = reservation.DisposeAsync().AsTask();
        await Task.Yield();
        var second = reservation.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);
        release.SetResult();
        await Task.WhenAll(first, second);
        var next = CreateReadyCandidate(admission, []);

        Assert.True(await admission.PublishCandidateAsync(next));
        Assert.Equal(["client", "scope"], order);
    }

    [Fact]
    public async Task DuplicatePublication_DoesNotDestroyCurrentCandidate()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope"));

        Assert.True(candidate.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(candidate));
        Assert.False(await admission.PublishCandidateAsync(candidate));

        Assert.Same(candidate, AssertCurrent(admission));
        Assert.Empty(order);
    }

    [Fact]
    public async Task Shutdown_CleansCurrentAndEveryUnpublishedCandidate()
    {
        var order = new List<string>();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var current = admission.CreateUnpublishedCandidate(new OrderedClient(order, "current-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "current-scope"));
        Assert.True(current.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(current));
        _ = admission.CreateUnpublishedCandidate(new OrderedClient(order, "pending-client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "pending-scope"));

        await admission.CloseForShutdownAndDrainAsync(CancellationToken.None);

        Assert.Equal(["current-client", "current-scope", "pending-client", "pending-scope"], order);
    }

    private static CopilotRuntimeGenerationV1 AssertCurrent(CopilotRuntimeAdmissionV1 admission)
    {
        Assert.True(admission.TryGetCurrentAdmittedGeneration(out var current));
        return current;
    }

    private static CopilotRuntimeGenerationV1 CreateReadyCandidate(
        CopilotRuntimeAdmissionV1 admission,
        List<string> order)
    {
        var candidate = admission.CreateUnpublishedCandidate(new OrderedClient(order, "client"),
            SkillInvocationV2TestIdentity.V1065, new OrderedScope(order, "scope"));
        Assert.True(candidate.TryMarkReady());
        return candidate;
    }

    private sealed class OrderedClient(List<string> order, string marker) : ICopilotSkillRuntimeClient
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult<CopilotRuntimeStatusObservationV1?>(null);
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(IReadOnlyList<string> projectPaths, IReadOnlyList<string> skillDirectories, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);
        public Task<GitHub.Copilot.CopilotSession> CreateSessionAsync(GitHub.Copilot.SessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RecordSessionStartCopilotVersion(string? copilotVersion) { }
        public ValueTask DisposeAsync() { order.Add(marker); return ValueTask.CompletedTask; }
    }

    private sealed class BlockingClient(List<string> order, Task release) : ICopilotSkillRuntimeClient
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult<CopilotRuntimeStatusObservationV1?>(null);
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(IReadOnlyList<string> projectPaths, IReadOnlyList<string> skillDirectories, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);
        public Task<GitHub.Copilot.CopilotSession> CreateSessionAsync(GitHub.Copilot.SessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RecordSessionStartCopilotVersion(string? copilotVersion) { }
        public async ValueTask DisposeAsync() { await release; order.Add("client"); }
    }

    private sealed class OrderedScope(List<string> order, string marker) : IAnalysisSdkDirectoryScope
    {
        private readonly CancellationTokenSource lost = new();
        public string ChildDirectory => "synthetic";
        public CancellationToken LeaseLostToken => lost.Token;
        public bool IsLeaseLost => lost.IsCancellationRequested;
        public void LoseLease() => lost.Cancel();
        public ValueTask DisposeAsync() { order.Add(marker); lost.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class CountingScope : IAnalysisSdkDirectoryScope
    {
        private readonly List<string> order;
        private readonly CancellationTokenSource lost;

        internal CountingScope(List<string> order, bool canceled)
        {
            this.order = order;
            lost = new CancellationTokenSource();
            if (canceled) lost.Cancel();
        }

        internal int LeaseTokenReads { get; private set; }
        public string ChildDirectory => "synthetic";
        public CancellationToken LeaseLostToken { get { LeaseTokenReads++; return lost.Token; } }
        public bool IsLeaseLost => lost.IsCancellationRequested;
        public ValueTask DisposeAsync() { order.Add("scope"); lost.Dispose(); return ValueTask.CompletedTask; }
    }
}

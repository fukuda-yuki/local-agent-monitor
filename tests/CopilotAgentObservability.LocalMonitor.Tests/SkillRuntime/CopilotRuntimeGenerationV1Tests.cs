using System.Reflection;
using CopilotAgentObservability.LocalMonitor.Tests;
using CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

public sealed class CopilotRuntimeGenerationV1Tests
{
    [Fact]
    public void Generation_HasOneOwnedScopeConstructionSurface()
    {
        var constructor = Assert.Single(typeof(CopilotRuntimeGenerationV1)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic));
        var scope = Assert.Single(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IAsyncDisposable));
        Assert.False(scope.HasDefaultValue);
    }

    [Fact]
    public void PublishedGeneration_AcquiresCapabilityWithFrozenIdentity()
    {
        var generation = NewPublishedGeneration();

        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        Assert.Same(generation.CertifiedIdentity, capability!.CertifiedIdentity);
        capability.Release();
        Assert.Equal(0, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public void Invalidation_CancelsUnsealedCapabilityAndClosesAdmission()
    {
        var generation = NewPublishedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        generation.Invalidate();

        Assert.True(capability!.WorkToken.IsCancellationRequested);
        Assert.False(generation.TryAcquireOperationCapability(CancellationToken.None, out _));
        capability.Release();
    }

    [Fact]
    public void CapabilityRelease_IsExactlyOnce()
    {
        var generation = NewPublishedGeneration();
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        Parallel.For(0, 32, _ => capability!.Release());

        Assert.Equal(0, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task CleanupStartup_ReturnsTaskWithoutWaitingForActiveInvalidationCallback()
    {
        using var lease = new CancellationTokenSource();
        using var callbackEntered = new ManualResetEventSlim();
        using var allowCallback = new ManualResetEventSlim();
        using var cleanupCallReturned = new ManualResetEventSlim();
        var client = new FakeSkillRuntimeClient();
        var scope = new CountingAsyncDisposable();
        var generation = new CopilotRuntimeGenerationV1(
            client,
            new SkillHostShutdownGateV1(),
            SkillInvocationV2TestIdentity.V1065,
            scope,
            lease.Token);
        var callbackCalls = 0;
        generation.AttachInvalidationRegistration(lease.Token.Register(() =>
        {
            Interlocked.Increment(ref callbackCalls);
            callbackEntered.Set();
            allowCallback.Wait();
        }));
        Task? cleanup = null;
        var cancelThread = new Thread(lease.Cancel) { IsBackground = true };
        var cleanupThread = new Thread(() =>
        {
            cleanup = generation.InvalidateAndCleanupAsync();
            cleanupCallReturned.Set();
        }) { IsBackground = true };
        var callbackWasEntered = false;
        var cleanupReturnedWhileCallbackBlocked = false;
        var cancelJoined = false;
        var cleanupJoined = false;

        try
        {
            cancelThread.Start();
            callbackWasEntered = callbackEntered.Wait(TimeSpan.FromSeconds(5));
            if (callbackWasEntered)
            {
                cleanupThread.Start();
                cleanupReturnedWhileCallbackBlocked = cleanupCallReturned.Wait(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            allowCallback.Set();
            cancelJoined = cancelThread.Join(TimeSpan.FromSeconds(5));
            cleanupJoined = !cleanupThread.IsAlive || cleanupThread.Join(TimeSpan.FromSeconds(5));
        }

        Assert.True(callbackWasEntered);
        Assert.True(cleanupReturnedWhileCallbackBlocked);
        Assert.True(cancelJoined);
        Assert.True(cleanupJoined);
        await Assert.IsType<Task>(cleanup);
        Assert.Equal(1, callbackCalls);
        Assert.Equal(1, client.DisposeCalls);
        Assert.Equal(1, scope.DisposeCalls);
    }

    private static CopilotRuntimeGenerationV1 NewPublishedGeneration() =>
        new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1())
            .PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);

    private sealed class CountingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCalls { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class CopilotRuntimeAdmissionV1Tests
{
    [Fact]
    public void FreshAdmission_HasNoCurrentGeneration()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.False(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out _));
    }

    [Fact]
    public void CandidatePublication_ReplacesAndInvalidatesPreviousGeneration()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var first = admission.PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);

        var second = admission.PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out var replaced);

        Assert.Same(first, replaced);
        Assert.True(first.IsInvalid);
        Assert.True(admission.TryGetCurrentAdmittedGeneration(out var current));
        Assert.Same(second, current);
    }

    [Fact]
    public async Task Shutdown_DrainsAndDisposesOwnedGeneration()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var client = new FakeSkillRuntimeClient();
        var generation = admission.PublishReadyTestCandidate(client, out _);
        Assert.True(generation.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        var close = admission.CloseForShutdownAndDrainAsync(CancellationToken.None);
        Assert.False(close.IsCompleted);
        capability!.Release();
        await close;

        Assert.False(generation.IsInvalid);
        Assert.False(capability.WorkToken.IsCancellationRequested);
        Assert.Equal(1, client.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }
}

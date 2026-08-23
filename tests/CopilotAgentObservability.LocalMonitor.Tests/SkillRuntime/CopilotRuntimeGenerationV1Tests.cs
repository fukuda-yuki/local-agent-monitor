using System.Reflection;
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

    private static CopilotRuntimeGenerationV1 NewPublishedGeneration() =>
        new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1())
            .PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);
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

        Assert.True(generation.IsInvalid);
        Assert.Equal(1, client.DisposeCalls);
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
    }
}

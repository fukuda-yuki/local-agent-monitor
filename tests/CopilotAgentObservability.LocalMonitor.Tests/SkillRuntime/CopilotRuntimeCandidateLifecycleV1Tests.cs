using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class CopilotRuntimeCandidateLifecycleV1Tests
{
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
        Assert.False(admission.TryGetCurrentAdmittedGeneration(out _));
        await candidate.CleanupTask;
        Assert.Equal(["candidate-client", "candidate-scope", "current-client", "current-scope"], order);
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
}

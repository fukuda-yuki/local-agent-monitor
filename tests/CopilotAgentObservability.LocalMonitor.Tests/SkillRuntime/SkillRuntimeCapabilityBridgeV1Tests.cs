using System.Text;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Tests;
using CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

public sealed class SkillRuntimeCapabilityBridgeV1Tests
{
    [Fact]
    public async Task PreparedBody_UsesOwningGenerationAndTransfersCapabilityExactlyOnce()
    {
        var fixture = new Fixture();
        var body = Encoding.UTF8.GetBytes("{\"prepared\":true}");

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardPreparedBodyAsync(fixture.Generation, body, CancellationToken.None));
        Assert.True(fixture.Bridge.TryConsume(fixture.Transport.Token, out var transfer));
        Assert.Equal(body.Length, transfer!.ExpectedBodyLength);
        transfer.ReleaseTransferredCapability();
        transfer.ReleaseTransferredCapability();
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task ReplacedGeneration_CannotForwardPreparedBody()
    {
        var fixture = new Fixture();
        var stale = fixture.Generation;
        fixture.Admission.PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable,
            await fixture.Bridge.ForwardPreparedBodyAsync(stale, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(0, fixture.Transport.SendCalls);
    }

    [Fact]
    public async Task Invalidation_ClearsPendingTokenAndReleasesCapability()
    {
        var fixture = new Fixture();
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardPreparedBodyAsync(fixture.Generation, new byte[] { 1 }, CancellationToken.None));

        await fixture.Admission.DiscardCandidateAsync(fixture.Generation);

        Assert.False(fixture.Bridge.TryConsume(fixture.Transport.Token, out _));
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task Invalidation_ClearsOnlyExactCandidatePendingEntries()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var tokenNumber = 0;
        var transport = new CapturingTransport();
        var bridge = new SkillRuntimeCapabilityBridgeV1(admission, transport, () => 0, () =>
        {
            var bytes = new byte[32];
            BitConverter.TryWriteBytes(bytes, ++tokenNumber);
            return bytes;
        });
        var first = admission.CreateUnpublishedCandidate(new FakeSkillRuntimeClient(),
            SkillInvocationV2TestIdentity.V1065, new LocalScope());
        var second = admission.CreateUnpublishedCandidate(new FakeSkillRuntimeClient(),
            SkillInvocationV2TestIdentity.V1065, new LocalScope());
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await bridge.ForwardPreparedBodyAsync(first, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await bridge.ForwardPreparedBodyAsync(second, new byte[] { 2 }, CancellationToken.None));

        await admission.DiscardCandidateAsync(first);

        Assert.False(bridge.TryConsume(transport.Tokens[0], out _));
        Assert.True(bridge.TryConsume(transport.Tokens[1], out var retained));
        retained!.ReleaseTransferredCapability();
        Assert.Equal(0, first.OutstandingCapabilityCount);
        Assert.Equal(0, second.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task Replacement_ClearsOldBridgeEntryWithoutClearingNewEntryDuringOldCleanup()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var releaseOldClient = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenNumber = 0;
        var transport = new CapturingTransport();
        var bridge = new SkillRuntimeCapabilityBridgeV1(admission, transport, () => 0, () =>
        {
            var bytes = new byte[32];
            BitConverter.TryWriteBytes(bytes, ++tokenNumber);
            return bytes;
        });
        var old = admission.CreateUnpublishedCandidate(new BlockingDisposeClient(releaseOldClient.Task),
            SkillInvocationV2TestIdentity.V1065, new LocalScope());
        Assert.True(old.TryMarkReady());
        Assert.True(await admission.PublishCandidateAsync(old));
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await bridge.ForwardPreparedBodyAsync(old, new byte[] { 1 }, CancellationToken.None));
        var replacement = admission.CreateUnpublishedCandidate(new FakeSkillRuntimeClient(),
            SkillInvocationV2TestIdentity.V1065, new LocalScope());
        Assert.True(replacement.TryMarkReady());

        var publishing = admission.PublishCandidateAsync(replacement);
        await Task.Yield();
        Assert.False(publishing.IsCompleted);
        Assert.True(admission.TryGetCurrentAdmittedGeneration(out var current));
        Assert.Same(replacement, current);
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await bridge.ForwardPreparedBodyAsync(replacement, new byte[] { 2 }, CancellationToken.None));
        Assert.False(bridge.TryConsume(transport.Tokens[0], out _));
        Assert.True(bridge.TryConsume(transport.Tokens[1], out var retained));
        retained!.ReleaseTransferredCapability();

        releaseOldClient.SetResult();
        Assert.True(await publishing);
    }

    [Fact]
    public async Task TransportFailure_ReleasesCapabilityAndLeavesNoToken()
    {
        var fixture = new Fixture(sendResult: false);

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable,
            await fixture.Bridge.ForwardPreparedBodyAsync(fixture.Generation, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(0, fixture.Generation.OutstandingCapabilityCount);
        Assert.Equal(0, fixture.Bridge.PendingCount);
    }

    [Fact]
    public async Task PreparedBodyCapacity_Admits64AndRefuses65th()
    {
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var generation = admission.PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);
        var transport = new FakeTransport(true);
        var token = 0;
        var bridge = new SkillRuntimeCapabilityBridgeV1(admission, transport, () => 0, () =>
        {
            var bytes = new byte[32];
            BitConverter.TryWriteBytes(bytes, token++);
            return bytes;
        });

        for (var index = 0; index < SkillRuntimeCapabilityBridgeV1.MaxPendingEntries; index++)
            Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
                await bridge.ForwardPreparedBodyAsync(generation, new byte[] { 1 }, CancellationToken.None));

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable,
            await bridge.ForwardPreparedBodyAsync(generation, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(SkillRuntimeCapabilityBridgeV1.MaxPendingEntries, transport.SendCalls);
    }

    [Fact]
    public async Task ExpiredPreparedBodyTokenIsPurgedAndReleased()
    {
        long now = 0;
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var generation = admission.PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);
        var transport = new FakeTransport(true);
        var bridge = new SkillRuntimeCapabilityBridgeV1(admission, transport, () => now, () => new byte[32]);
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await bridge.ForwardPreparedBodyAsync(generation, new byte[] { 1 }, CancellationToken.None));

        now = SkillRuntimeCapabilityBridgeV1.EntryLifetimeTicks;
        bridge.PurgeExpired();

        Assert.Equal(0, bridge.PendingCount);
        Assert.Equal(0, generation.OutstandingCapabilityCount);
    }

    [Fact]
    public async Task DuplicateTokenKeepsFirstPreparedBodyEntry()
    {
        var fixture = new Fixture();
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Forwarded,
            await fixture.Bridge.ForwardPreparedBodyAsync(fixture.Generation, new byte[] { 1 }, CancellationToken.None));
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable,
            await fixture.Bridge.ForwardPreparedBodyAsync(fixture.Generation, new byte[] { 2 }, CancellationToken.None));
        Assert.True(fixture.Bridge.TryConsume(fixture.Transport.Token, out var transfer));
        transfer!.ReleaseTransferredCapability();
    }

    [Fact]
    public async Task InvalidPreparedBodyOrCancellationDoesNotSend()
    {
        var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable,
            await fixture.Bridge.ForwardPreparedBodyAsync(
                fixture.Generation,
                new byte[OwnedSessionPreparedBufferV1.MaxAggregateBodyBytes + 1],
                CancellationToken.None));
        Assert.Equal(SkillRuntimeBridgeForwardOutcome.Unavailable,
            await fixture.Bridge.ForwardPreparedBodyAsync(fixture.Generation, new byte[] { 1 }, canceled.Token));
        Assert.Equal(0, fixture.Transport.SendCalls);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", true)]
    public void TokenGrammar_IsStrict(string? token, bool expected) =>
        Assert.Equal(expected, SkillRuntimeCapabilityBridgeV1.IsValidTokenGrammar(token));

    private sealed class Fixture
    {
        internal Fixture(bool sendResult = true)
        {
            Admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
            Transport = new FakeTransport(sendResult);
            Bridge = new SkillRuntimeCapabilityBridgeV1(Admission, Transport, () => 0, () => new byte[32]);
            Generation = Admission.PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);
        }

        internal CopilotRuntimeAdmissionV1 Admission { get; }
        internal FakeTransport Transport { get; }
        internal SkillRuntimeCapabilityBridgeV1 Bridge { get; }
        internal CopilotRuntimeGenerationV1 Generation { get; }
    }

    private sealed class FakeTransport(bool sendResult) : ISkillRuntimeBridgeTransport
    {
        internal string? Token { get; private set; }
        internal int SendCalls { get; private set; }
        public Task<bool> SendAsync(string capabilityToken, ReadOnlyMemory<byte> bodyUtf8, CancellationToken cancellationToken)
        {
            Token = capabilityToken;
            SendCalls++;
            return Task.FromResult(sendResult);
        }
    }

    private sealed class CapturingTransport : ISkillRuntimeBridgeTransport
    {
        internal List<string> Tokens { get; } = [];
        public Task<bool> SendAsync(string capabilityToken, ReadOnlyMemory<byte> bodyUtf8, CancellationToken cancellationToken)
        {
            Tokens.Add(capabilityToken);
            return Task.FromResult(true);
        }
    }

    private sealed class LocalScope : IAnalysisSdkDirectoryScope
    {
        public string ChildDirectory => "synthetic";
        public CancellationToken LeaseLostToken => CancellationToken.None;
        public bool IsLeaseLost => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingDisposeClient(Task release) : ICopilotSkillRuntimeClient
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CopilotRuntimeStatusObservationV1?>(null);
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
            IReadOnlyList<string> projectPaths, IReadOnlyList<string> skillDirectories,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);
        public Task<GitHub.Copilot.CopilotSession> CreateSessionAsync(
            GitHub.Copilot.SessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RecordSessionStartCopilotVersion(string? copilotVersion) { }
        public async ValueTask DisposeAsync() => await release;
    }
}

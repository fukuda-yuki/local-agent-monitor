using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class SkillHostShutdownCoordinatorV1Tests : IDisposable
{
    private readonly TempHandleSource handleSource = new();

    public void Dispose() => handleSource.Dispose();

    [Fact]
    public async Task TryStartNormalShutdown_ConcurrentCallers_ReturnsTrueExactlyOnce()
    {
        const int callerCount = 8;
        var gate = new SkillHostShutdownGateV1();
        using var barrier = new Barrier(callerCount + 1);
        var results = new bool[callerCount];
        var callers = Enumerable.Range(0, callerCount)
            .Select(index => Task.Run(() =>
            {
                barrier.SignalAndWait();
                results[index] = gate.TryStartNormalShutdown();
            }))
            .ToArray();

        barrier.SignalAndWait();
        await Task.WhenAll(callers);

        Assert.Single(results, result => result);
        Assert.True(gate.IsNormalShutdownStarted);
    }

    [Fact]
    public void TryStartNormalShutdown_WithoutAuthorityClosure_RefusesBothAdmissions()
    {
        var gate = new SkillHostShutdownGateV1();
        var roots = CreateRootGeneration(out _, gate);
        var admission = new CopilotRuntimeAdmissionV1(gate);
        admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);

        Assert.True(gate.TryStartNormalShutdown());

        Assert.False(
            roots.TryAcquireLease(out _),
            "SkillDiscoveryRootGenerationV1 accepted a lease after the shared gate started.");
        Assert.True(
            admission.AcquireCurrentFileCapability(CancellationToken.None, out _)
                == CopilotRuntimeAcquisitionDispositionV1.NormalShutdownClosed,
            "CopilotRuntimeAdmissionV1 did not report normal shutdown after the shared gate started.");
        Assert.False(roots.IsAdmissionClosed);
        Assert.False(admission.IsShutdownClosed);
    }

    [Fact]
    public async Task StoppingAsync_ClosesBothAdmissionsBeforeEitherDrainCompletes()
    {
        var gate = new SkillHostShutdownGateV1();
        var roots = CreateRootGeneration(out var preflight, gate);
        var admission = new CopilotRuntimeAdmissionV1(gate);
        var client = new FakeSkillRuntimeClient();
        var generation = admission.PublishAdmittedGeneration(client, out _)!;
        Assert.True(roots.TryAcquireLease(out var rootLease));
        Assert.True(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var capability));
        var coordinator = new SkillHostShutdownCoordinatorV1(gate, roots, admission);

        var stopping = coordinator.StoppingAsync(CancellationToken.None);

        Assert.True(roots.IsAdmissionClosed);
        Assert.True(admission.IsShutdownClosed);
        Assert.False(roots.TryAcquireLease(out _));
        Assert.Equal(
            CopilotRuntimeAcquisitionDispositionV1.NormalShutdownClosed,
            admission.AcquireCurrentFileCapability(CancellationToken.None, out _));
        Assert.False(capability!.WorkToken.IsCancellationRequested);
        Assert.False(generation.IsInvalid);
        Assert.False(stopping.IsCompleted);
        Assert.Equal(0, client.DisposeCalls);
        Assert.All(preflight.RetainedRoots, root => Assert.False(root.IsDisposed));

        capability.Release();
        Assert.False(stopping.IsCompleted);
        rootLease!.Dispose();
        await stopping;

        Assert.Equal(1, client.DisposeCalls);
        Assert.All(preflight.RetainedRoots, root => Assert.True(root.IsDisposed));

        await coordinator.StopAsync(CancellationToken.None);

        Assert.All(preflight.RetainedRoots, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public async Task StoppingAsync_RootLeaseAlreadyHeld_RuntimeAcquisitionReportsShutdownClosed()
    {
        var gate = new SkillHostShutdownGateV1();
        var roots = CreateRootGeneration(out _, gate);
        var admission = new CopilotRuntimeAdmissionV1(gate);
        admission.PublishAdmittedGeneration(new FakeSkillRuntimeClient(), out _);
        Assert.True(roots.TryAcquireLease(out var rootLease));
        var coordinator = new SkillHostShutdownCoordinatorV1(gate, roots, admission);

        var stopping = coordinator.StoppingAsync(CancellationToken.None);

        Assert.Equal(
            CopilotRuntimeAcquisitionDispositionV1.NormalShutdownClosed,
            admission.AcquireCurrentFileCapability(CancellationToken.None, out _));

        rootLease!.Dispose();
        await stopping;
        await coordinator.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StoppingAsync_RepeatedCalls_ReturnSameInProgressDrain()
    {
        var gate = new SkillHostShutdownGateV1();
        var admission = new CopilotRuntimeAdmissionV1(gate);
        var client = new FakeSkillRuntimeClient();
        admission.PublishAdmittedGeneration(client, out _);
        Assert.True(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var capability));
        var coordinator = new SkillHostShutdownCoordinatorV1(gate, null, admission);

        var firstStopping = coordinator.StoppingAsync(CancellationToken.None);
        var secondStopping = coordinator.StoppingAsync(CancellationToken.None);
        admission.CloseForShutdown();

        Assert.Same(firstStopping, secondStopping);
        Assert.False(firstStopping.IsCompleted);
        Assert.False(secondStopping.IsCompleted);

        capability!.Release();
        capability.Release();
        await Task.WhenAll(firstStopping, secondStopping);
        await coordinator.StopAsync(CancellationToken.None);
        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task StoppingAsync_RacingRuntimeInvalidation_PreservesCancellationAndDrainSemantics()
    {
        var gate = new SkillHostShutdownGateV1();
        var admission = new CopilotRuntimeAdmissionV1(gate);
        var client = new FakeSkillRuntimeClient();
        admission.PublishAdmittedGeneration(client, out _);
        Assert.True(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var capability));
        var coordinator = new SkillHostShutdownCoordinatorV1(gate, null, admission);

        var stopping = coordinator.StoppingAsync(CancellationToken.None);

        Assert.False(capability!.WorkToken.IsCancellationRequested);
        var removed = admission.InvalidateCurrentGeneration();
        Assert.NotNull(removed);
        await removed!.Client.DisposeAsync();
        Assert.True(capability.WorkToken.IsCancellationRequested);
        Assert.False(stopping.IsCompleted);

        capability.Release();
        await stopping;

        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task StoppingAsync_PendingBridgeToken_WaitsForOrdinaryConsumptionAndRelease()
    {
        var gate = new SkillHostShutdownGateV1();
        var admission = new CopilotRuntimeAdmissionV1(gate);
        var client = new FakeSkillRuntimeClient();
        var generation = admission.PublishAdmittedGeneration(client, out _)!;
        var transport = new FakeBridgeTransport();
        var bridge = new SkillRuntimeCapabilityBridgeV1(
            admission,
            transport,
            static () => 0,
            static () => new byte[SkillRuntimeCapabilityBridgeV1.TokenByteLength]);
        var sourceEvent = new SkillInvokedEvent
        {
            Id = Guid.Parse("018f0f4e-7b2a-4c11-8a3b-123456789abc"),
            Timestamp = new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero),
            Data = new SkillInvokedData
            {
                Name = "skill-name",
                Path = "skills/SKILL.md",
                Content = "body",
            },
        };
        Assert.Equal(
            SkillRuntimeBridgeForwardOutcome.Forwarded,
            await bridge.ForwardCallbackAsync(generation, "native-session", sourceEvent, CancellationToken.None));
        var token = Assert.Single(transport.Sends).Token;
        var coordinator = new SkillHostShutdownCoordinatorV1(gate, null, admission);

        var stopping = coordinator.StoppingAsync(CancellationToken.None);

        Assert.False(stopping.IsCompleted);
        Assert.Equal(1, bridge.PendingCount);
        Assert.Equal(1, generation.OutstandingCapabilityCount);
        Assert.Equal(0, client.DisposeCalls);
        Assert.True(bridge.TryConsume(token, out var transfer));
        transfer!.ReleaseTransferredCapability();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, generation.OutstandingCapabilityCount);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task CloseForShutdownAndDrainAsync_CancellationDoesNotBoundInnerDrain()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var client = new FakeSkillRuntimeClient();
        var generation = admission.PublishAdmittedGeneration(client, out _)!;
        Assert.True(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var capability));
        using var cancellation = new CancellationTokenSource();

        var drain = admission.CloseForShutdownAndDrainAsync(cancellation.Token);
        cancellation.Cancel();

        Assert.False(drain.IsCompleted);
        Assert.False(capability!.WorkToken.IsCancellationRequested);
        Assert.False(generation.IsInvalid);
        Assert.Equal(0, client.DisposeCalls);

        capability.Release();
        await drain;

        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task StoppingAsync_CancelledShutdownToken_StopsWaitingWithoutCancellingRootLease()
    {
        var gate = new SkillHostShutdownGateV1();
        var roots = CreateRootGeneration(out var preflight, gate);
        Assert.True(roots.TryAcquireLease(out var rootLease));
        using var cancellation = new CancellationTokenSource();
        var coordinator = new SkillHostShutdownCoordinatorV1(gate, roots, null);

        var stopping = coordinator.StoppingAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stopping.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, roots.OutstandingLeaseCount);
        Assert.All(preflight.RetainedRoots, root => Assert.False(root.IsDisposed));

        rootLease!.Dispose();
    }

    [Fact]
    public async Task StoppingAsync_CancelledOuterWaitThenStopAsync_WaitsForLeaseBeforeDisposingRoots()
    {
        var gate = new SkillHostShutdownGateV1();
        var roots = CreateRootGeneration(out var preflight, gate);
        Assert.True(roots.TryAcquireLease(out var rootLease));
        using var cancellation = new CancellationTokenSource();
        var coordinator = new SkillHostShutdownCoordinatorV1(gate, roots, null);

        var stopping = coordinator.StoppingAsync(cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stopping.WaitAsync(TimeSpan.FromSeconds(5)));

        var stop = coordinator.StopAsync(CancellationToken.None);

        Assert.False(stop.IsCompleted);
        Assert.Equal(1, roots.OutstandingLeaseCount);
        Assert.All(preflight.RetainedRoots, root => Assert.False(root.IsDisposed));

        rootLease!.Dispose();
        await stop.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(CancellationToken.None);

        Assert.All(preflight.RetainedRoots, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public async Task CloseForShutdownAndDrainAsync_ConcurrentInvalidationDisposesGenerationClientOnce()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var client = new FakeSkillRuntimeClient();
        admission.PublishAdmittedGeneration(client, out _);
        Assert.True(admission.TryAcquireCurrentFileCapability(CancellationToken.None, out var capability));

        var shutdownDrain = admission.CloseForShutdownAndDrainAsync(CancellationToken.None);
        var removed = admission.InvalidateCurrentGeneration();
        Assert.NotNull(removed);
        await removed!.Client.DisposeAsync();
        capability!.Release();
        await shutdownDrain;

        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task CloseForShutdownAndDrainAsync_NoGeneration_ReturnsImmediately()
    {
        var admission = new CopilotRuntimeAdmissionV1();

        await admission.CloseForShutdownAndDrainAsync(CancellationToken.None);

        Assert.True(admission.IsShutdownClosed);
    }

    [Fact]
    public async Task CloseForShutdownAndDrainAsync_DisposalFailureDoesNotEscape()
    {
        var admission = new CopilotRuntimeAdmissionV1();
        var client = new FakeSkillRuntimeClient { DisposeThrows = true };
        admission.PublishAdmittedGeneration(client, out _);

        await admission.CloseForShutdownAndDrainAsync(CancellationToken.None);

        Assert.Equal(1, client.DisposeCalls);
    }

    private SkillDiscoveryRootGenerationV1 CreateRootGeneration(
        out SkillDiscoveryRootPreflightResultV1 preflight,
        SkillHostShutdownGateV1? shutdownGate = null)
    {
        preflight = SkillDiscoveryRootPreflightV1.Run(
            [@"C:\repo"],
            [@"C:\skills"],
            new CertifiedDiscoveryPlatformV1(
                SkillProducerPathKeyPlatform.Windows,
                new StubOpener(handleSource)));
        return shutdownGate is null
            ? new SkillDiscoveryRootGenerationV1(preflight)
            : new SkillDiscoveryRootGenerationV1(preflight, shutdownGate);
    }

    private sealed class StubOpener(TempHandleSource handleSource) : IDiscoveryRootOpenerV1
    {
        private int openCount;

        public DiscoveryRootOpenResultV1 TryOpenRetainedRoot(
            string configuredRootPath,
            DiscoveryRootKindV1 kind)
        {
            Assert.True(SkillProducerPathKeyV1.TryParse(
                configuredRootPath,
                SkillProducerPathKeyPlatform.Windows,
                out var pathKey,
                out _));
            var seed = (ulong)++openCount;
            var fileId = new byte[16];
            fileId[0] = (byte)seed;
            return DiscoveryRootOpenResultV1.Succeeded(new RetainedDiscoveryRootV1(
                kind,
                pathKey!,
                DiscoveryRootNativeIdentityV1.CreateWindows(seed, fileId),
                handleSource.OpenHandle()));
        }

        public bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root) => !root.IsDisposed;
    }

    private sealed class TempHandleSource : IDisposable
    {
        private readonly string directoryPath =
            Path.Combine(Path.GetTempPath(), $"cao-shutdown-{Guid.NewGuid():N}");
        private readonly string filePath;

        public TempHandleSource()
        {
            Directory.CreateDirectory(directoryPath);
            filePath = Path.Combine(directoryPath, "handle-source.bin");
            File.WriteAllBytes(filePath, [1]);
        }

        public SafeFileHandle OpenHandle() => File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

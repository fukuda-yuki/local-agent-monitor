using CopilotAgentObservability.LocalMonitor.Settings;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SettingsAiReadinessServiceTests
{
    [Theory]
    [InlineData(true, "configured_not_checked")]
    [InlineData(false, "unconfigured")]
    public void Snapshot_UsesAnalysisEnabledConfiguration(bool enabled, string expected)
    {
        var service = Service(() => new Client(Status(true)), enabled: enabled);
        Assert.Equal(expected, service.GetSnapshot().ReadinessState);
    }

    [Theory]
    [InlineData(true, "ready")]
    [InlineData(false, "authentication_required")]
    public async Task CheckAsync_RequiresCertifiedAndAuthenticatedProductionStatus(bool authenticated, string expected)
    {
        var service = Service(() => new Client(Status(authenticated)));
        Assert.Equal(expected, (await service.CheckAsync(CancellationToken.None)).ReadinessState);
    }

    [Fact]
    public async Task CheckAsync_MapsUnusableRuntimeToUnavailableAndNeverCreatesAClientWhenDisabled()
    {
        var calls = 0;
        var unavailable = Service(() => { calls++; return null; });
        Assert.Equal("unavailable", (await unavailable.CheckAsync(CancellationToken.None)).ReadinessState);
        Assert.Equal(1, calls);
        var disabled = Service(() => { calls++; return new Client(Status(true)); }, enabled: false);
        Assert.Equal("unconfigured", (await disabled.CheckAsync(CancellationToken.None)).ReadinessState);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CheckAsync_DeduplicatesAndCallerCancellationDoesNotCancelSharedCheck()
    {
        var release = new TaskCompletionSource<CopilotRuntimeStatusObservationV1?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(release.Task);
        var service = Service(() => client);
        using var canceled = new CancellationTokenSource();
        var first = service.CheckAsync(canceled.Token);
        var second = service.CheckAsync(CancellationToken.None);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult(Status(true));
        Assert.Equal("ready", (await second).ReadinessState);
        Assert.Equal(1, client.StatusCalls);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task CloseAdmission_CancelsInflightDrainsCleanupAndRejectsNewFactories()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(async cancellationToken =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }, () => disposed.SetResult());
        var factoryCalls = 0;
        var service = Service(() => { factoryCalls++; return client; });
        var check = service.CheckAsync(CancellationToken.None);
        await entered.Task;
        await service.CloseAdmissionAndDrainAsync(CancellationToken.None);
        await disposed.Task;
        Assert.Equal("unavailable", (await check).ReadinessState);
        Assert.Equal("unavailable", (await service.CheckAsync(CancellationToken.None)).ReadinessState);
        Assert.Equal(1, factoryCalls);
        Assert.Equal(1, client.DisposeCalls);
    }

    [Fact]
    public async Task CheckAsync_IsBoundedWhenProviderIgnoresCancellationAndDisposesExactlyOnce()
    {
        var never = new TaskCompletionSource<CopilotRuntimeStatusObservationV1?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(_ => never.Task);
        var service = Service(() => client, timeout: TimeSpan.FromMilliseconds(30));
        var result = await service.CheckAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("check_failed", result.ReadinessState);
        Assert.Equal(1, client.DisposeCalls);
    }

    private static CopilotRuntimeStatusObservationV1 Status(bool authenticated) => new("1.0.75", 3, null, authenticated);

    private static SettingsAiReadinessService Service(Func<IOwnedCopilotClientV1?> factory, bool enabled = true, TimeSpan? timeout = null) =>
        new("github_copilot", "gpt-5", "standard", enabled, factory, timeout ?? TimeSpan.FromSeconds(1));

    private sealed class Client : IOwnedCopilotClientV1
    {
        private readonly Func<CancellationToken, Task<CopilotRuntimeStatusObservationV1?>> status;
        private readonly Action? dispose;
        internal Client(CopilotRuntimeStatusObservationV1? value) : this(_ => Task.FromResult(value)) { }
        internal Client(Task<CopilotRuntimeStatusObservationV1?> value) : this(_ => value) { }
        internal Client(Func<CancellationToken, Task<CopilotRuntimeStatusObservationV1?>> status, Action? dispose = null) => (this.status, this.dispose) = (status, dispose);
        internal int StatusCalls { get; private set; }
        internal int DisposeCalls { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) { StatusCalls++; return await status(cancellationToken); }
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException("Readiness must not create an SDK session.");
        public ValueTask DisposeAsync() { DisposeCalls++; dispose?.Invoke(); return ValueTask.CompletedTask; }
    }
}

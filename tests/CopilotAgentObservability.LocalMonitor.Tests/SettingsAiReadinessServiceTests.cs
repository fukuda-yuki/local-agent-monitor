using CopilotAgentObservability.LocalMonitor.Settings;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SettingsAiReadinessServiceTests
{
    [Fact]
    public void Snapshot_BeforeCheckIsConfiguredNotChecked()
    {
        var service = Service(new Client(new CopilotRuntimeStatusObservationV1("1.0.75", 3, null)));

        var snapshot = service.GetSnapshot();

        Assert.Equal("configured_not_checked", snapshot.ReadinessState);
        Assert.Equal("not_checked", snapshot.LastCheckResult);
        Assert.Equal(("github_copilot", "gpt-5", "standard"),
            (snapshot.Provider, snapshot.SelectedModel, snapshot.SelectedConfiguration));
    }

    [Fact]
    public async Task CheckAsync_MapsCertifiedNullAndUncertifiedStatusToClosedStates()
    {
        var ready = Service(new Client(new CopilotRuntimeStatusObservationV1("1.0.75", 3, null)));
        var authentication = Service(new Client((CopilotRuntimeStatusObservationV1?)null));
        var unavailable = Service(new Client(new CopilotRuntimeStatusObservationV1("not-certified", 3, null)));

        Assert.Equal("ready", (await ready.CheckAsync(CancellationToken.None)).ReadinessState);
        Assert.Equal("authentication_required", (await authentication.CheckAsync(CancellationToken.None)).ReadinessState);
        Assert.Equal("unavailable", (await unavailable.CheckAsync(CancellationToken.None)).ReadinessState);
    }

    [Fact]
    public async Task CheckAsync_DeduplicatesConcurrentChecksAndCallerCancellationDoesNotCancelSharedCheck()
    {
        var release = new TaskCompletionSource<CopilotRuntimeStatusObservationV1?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client(release.Task);
        var service = Service(client);
        using var canceled = new CancellationTokenSource();

        var first = service.CheckAsync(canceled.Token);
        var second = service.CheckAsync(CancellationToken.None);
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult(new("1.0.75", 3, null));

        Assert.Equal("ready", (await second).ReadinessState);
        Assert.Equal(1, client.StatusCalls);
    }

    [Fact]
    public async Task CheckAsync_TimeoutFailureAndShutdownUseFixedClosedOutcomes()
    {
        var never = new TaskCompletionSource<CopilotRuntimeStatusObservationV1?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timedOut = Service(new Client(never.Task), timeout: TimeSpan.FromMilliseconds(20));
        Assert.Equal("check_failed", (await timedOut.CheckAsync(CancellationToken.None)).ReadinessState);

        var gate = new SkillHostShutdownGateV1();
        gate.TryStartNormalShutdown();
        var stopped = Service(new Client(new CopilotRuntimeStatusObservationV1("1.0.75", 3, null)), gate: gate);
        Assert.Equal("unavailable", (await stopped.CheckAsync(CancellationToken.None)).ReadinessState);
    }

    private static SettingsAiReadinessService Service(Client client, TimeSpan? timeout = null, SkillHostShutdownGateV1? gate = null) =>
        new("github_copilot", "gpt-5", "standard", () => client, gate ?? new(), timeout ?? TimeSpan.FromSeconds(1));

    private sealed class Client : IOwnedCopilotClientV1
    {
        private readonly Task<CopilotRuntimeStatusObservationV1?> status;
        internal Client(CopilotRuntimeStatusObservationV1? status) : this(Task.FromResult(status)) { }
        internal Client(Task<CopilotRuntimeStatusObservationV1?> status) => this.status = status;
        internal int StatusCalls { get; private set; }
        public ICopilotSkillRuntimeClient RuntimeClient => throw new NotSupportedException();
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public async Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken)
        {
            StatusCalls++;
            return await status.WaitAsync(cancellationToken);
        }
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("A readiness check must not create an SDK session.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

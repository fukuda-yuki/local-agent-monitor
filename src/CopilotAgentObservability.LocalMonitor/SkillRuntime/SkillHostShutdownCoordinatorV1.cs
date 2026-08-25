using CopilotAgentObservability.LocalMonitor.SkillNative;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class SkillHostShutdownCoordinatorV1(
    SkillHostShutdownGateV1 shutdownGate,
    SkillDiscoveryRootGenerationV1? rootGeneration,
    CopilotRuntimeAdmissionV1? runtimeAdmission) : IHostedLifecycleService, IAsyncDisposable
{
    private readonly object sync = new();
    private Task? shutdownTask;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken)
        => GetOrStartShutdownTask().WaitAsync(cancellationToken);

    public ValueTask DisposeAsync() => new(GetOrStartShutdownTask());

    private async Task ShutdownOnceAsync()
    {
        shutdownGate.TryStartNormalShutdown();
        // The shared gate, not statement order, makes admission closure atomic. Each authority's
        // own closed flag remains responsible for its drain bookkeeping.
        runtimeAdmission?.CloseForShutdown();
        rootGeneration?.CloseAdmission();

        // Closure and drain cannot move to StopAsync: it runs after the web host stops, when an
        // in-flight request can no longer reach its ordinary terminal completion.
        var runtimeDrain = runtimeAdmission?.CloseForShutdownAndDrainAsync(CancellationToken.None)
            ?? Task.CompletedTask;
        var rootDrainAndDisposal = rootGeneration?.DrainAndDisposeRootsAsync() ?? Task.CompletedTask;
        await Task.WhenAll(rootDrainAndDisposal, runtimeDrain).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Task? shutdown;
        lock (sync)
        {
            shutdown = shutdownTask;
        }

        return shutdown?.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private Task GetOrStartShutdownTask()
    {
        lock (sync)
        {
            return shutdownTask ??= ShutdownOnceAsync();
        }
    }
}

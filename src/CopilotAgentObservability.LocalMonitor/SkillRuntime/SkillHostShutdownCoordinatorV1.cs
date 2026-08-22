using CopilotAgentObservability.LocalMonitor.SkillNative;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class SkillHostShutdownCoordinatorV1(
    SkillDiscoveryRootGenerationV1? rootGeneration,
    CopilotRuntimeAdmissionV1? runtimeAdmission) : IHostedLifecycleService
{
    private readonly object sync = new();
    private Task? shutdownTask;

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            return shutdownTask ??= StopOnceAsync(cancellationToken);
        }
    }

    private async Task StopOnceAsync(CancellationToken cancellationToken)
    {
        runtimeAdmission?.CloseForShutdown();
        // These authorities have independent locks, so this order, rather than a shared gate,
        // ensures a root-admitted request observes runtime admission as shutdown-closed.
        rootGeneration?.CloseAdmission();

        // Closure and drain cannot move to StopAsync: it runs after the web host stops, when an
        // in-flight request can no longer reach its ordinary terminal completion.
        var runtimeDrain = runtimeAdmission?.CloseForShutdownAndDrainAsync(CancellationToken.None)
            ?? Task.CompletedTask;
        var rootDrain = rootGeneration?.DrainAsync() ?? Task.CompletedTask;
        await Task.WhenAll(rootDrain, runtimeDrain).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        rootGeneration?.DisposeRootsAsync() ?? Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

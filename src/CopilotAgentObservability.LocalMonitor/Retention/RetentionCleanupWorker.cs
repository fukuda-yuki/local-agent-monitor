using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal sealed class RetentionCleanupWorker : IHostedService
{
    private readonly RetentionCleanupCoordinator? coordinator;
    private readonly TimeProvider time;
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly object wakeGate = new();
    private bool wakePending;
    private CancellationTokenSource? stopScanning;
    private CancellationTokenSource? drain;
    private Task? running;

    internal RetentionCleanupWorker() { time = TimeProvider.System; }
    internal RetentionCleanupWorker(RetentionCleanupCoordinator coordinator, TimeProvider? timeProvider = null) { this.coordinator = coordinator; time = timeProvider ?? TimeProvider.System; }

    internal ValueTask RunOnceAsync(CancellationToken cancellationToken) => coordinator?.RunOnceAsync(cancellationToken) ?? ValueTask.CompletedTask;
    internal ValueTask StartAsync()
    {
        if (running is null) { stopScanning = new CancellationTokenSource(); running = RunAsync(stopScanning.Token); }
        return ValueTask.CompletedTask;
    }
    internal void Wake()
    {
        lock (wakeGate)
        {
            if (wakePending) return;
            wakePending = true;
            wake.Release();
        }
    }
    internal async Task StopAsync()
    {
        var scanner = stopScanning;
        var task = running;
        if (scanner is null || task is null) return;
        scanner.Cancel();
        var deadline = time.GetUtcNow() + RetentionV1Constants.ShutdownDrainBound;
        coordinator?.BeginDrain(deadline);
        var completed = await Task.WhenAny(task, Task.Delay(RetentionV1Constants.ShutdownDrainBound, time)).ConfigureAwait(false);
        if (completed == task) await task.ConfigureAwait(false);
        drain?.Cancel();
    }
    Task IHostedService.StartAsync(CancellationToken cancellationToken) => StartAsync().AsTask();
    Task IHostedService.StopAsync(CancellationToken cancellationToken) => StopAsync();
    private async Task RunAsync(CancellationToken stopToken)
    {
        using var drainSource = new CancellationTokenSource();
        drain = drainSource;
        CancellationTokenSource? dueWakeCancellation = null;
        Task? dueWake = null;
        DateTimeOffset? scheduledDue = null;
        try
        {
            var cycle = await coordinator!.RunOneCycleAsync(stopToken, drainSource.Token).ConfigureAwait(false);
            CompleteWakeCycle();
            ScheduleDueWake(cycle.NextEligibleAt, ref scheduledDue, ref dueWake, ref dueWakeCancellation);
            while (!stopToken.IsCancellationRequested)
            {
                if (!cycle.ContinueImmediately)
                {
                    using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
                    var policyWake = Task.Delay(RetentionV1Constants.WorkerWakeInterval, time, waitCancellation.Token);
                    var signalWake = wake.WaitAsync(waitCancellation.Token);
                    var dueOrNever = dueWake ?? Task.Delay(Timeout.InfiniteTimeSpan, time, waitCancellation.Token);
                    var completed = await Task.WhenAny(policyWake, signalWake, dueOrNever).ConfigureAwait(false);
                    if (signalWake.IsCompletedSuccessfully) completed = signalWake;
                    waitCancellation.Cancel();
                    if (stopToken.IsCancellationRequested) break;
                    if (completed == dueWake)
                    {
                        dueWakeCancellation?.Dispose();
                        dueWakeCancellation = null;
                        dueWake = null;
                        scheduledDue = null;
                    }
                }
                cycle = await coordinator.RunOneCycleAsync(stopToken, drainSource.Token).ConfigureAwait(false);
                CompleteWakeCycle();
                ScheduleDueWake(cycle.NextEligibleAt, ref scheduledDue, ref dueWake, ref dueWakeCancellation);
            }
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested) { }
        finally { dueWakeCancellation?.Cancel(); dueWakeCancellation?.Dispose(); drain = null; }
    }

    private void CompleteWakeCycle()
    {
        lock (wakeGate)
        {
            wake.Wait(0);
            wakePending = false;
        }
    }

    private void ScheduleDueWake(DateTimeOffset? candidate, ref DateTimeOffset? scheduledDue, ref Task? dueWake, ref CancellationTokenSource? cancellation)
    {
        if (candidate is null)
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; dueWake = null; scheduledDue = null; return;
        }
        if (scheduledDue is { } current && current <= candidate.Value && current > time.GetUtcNow()) return;
        cancellation?.Cancel(); cancellation?.Dispose();
        scheduledDue = candidate;
        cancellation = new CancellationTokenSource();
        var delay = candidate.Value - time.GetUtcNow();
        dueWake = Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, time, cancellation.Token);
    }
}

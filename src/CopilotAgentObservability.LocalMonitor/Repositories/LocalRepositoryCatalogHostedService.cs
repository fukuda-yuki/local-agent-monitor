using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Repositories;

internal enum LocalRepositoryCatalogHostedServiceCheckpoint { BeforeDiscover, BeforeRunOnce, BeforeDelay }

internal sealed class LocalRepositoryCatalogHostedService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly SqliteLocalRepositoryReconciliationStore queue;
    private readonly LocalRepositoryRawAvailabilityReader rawAvailability;
    private readonly LocalRepositoryReconciliationWorker worker;
    private readonly TimeProvider timeProvider;
    private readonly Action<LocalRepositoryCatalogHostedServiceCheckpoint>? checkpoint;

    internal LocalRepositoryCatalogHostedService(
        SqliteLocalRepositoryReconciliationStore queue,
        LocalRepositoryRawAvailabilityReader rawAvailability,
        LocalRepositoryReconciliationWorker worker,
        TimeProvider? timeProvider = null,
        Action<LocalRepositoryCatalogHostedServiceCheckpoint>? checkpoint = null)
    {
        this.queue = queue ?? throw new ArgumentNullException(nameof(queue));
        this.rawAvailability = rawAvailability ?? throw new ArgumentNullException(nameof(rawAvailability));
        this.worker = worker ?? throw new ArgumentNullException(nameof(worker));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
    }

    internal async Task RunOnePassAsync(CancellationToken cancellationToken)
    {
        checkpoint?.Invoke(LocalRepositoryCatalogHostedServiceCheckpoint.BeforeDiscover);
        await queue.DiscoverAsync(rawAvailability, cancellationToken).ConfigureAwait(false);
        checkpoint?.Invoke(LocalRepositoryCatalogHostedServiceCheckpoint.BeforeRunOnce);
        await worker.RunOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnePassAsync(stoppingToken).ConfigureAwait(false);
                checkpoint?.Invoke(LocalRepositoryCatalogHostedServiceCheckpoint.BeforeDelay);
                await Task.Delay(PollInterval, timeProvider, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}

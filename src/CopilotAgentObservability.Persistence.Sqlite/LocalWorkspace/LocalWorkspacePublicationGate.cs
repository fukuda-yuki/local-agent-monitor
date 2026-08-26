namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ILocalWorkspacePublicationGate
{
    ValueTask<IAsyncDisposable> AcquireReadAsync(CancellationToken cancellationToken);

    ValueTask<IAsyncDisposable> AcquireWriteAsync(CancellationToken cancellationToken);
}

internal sealed class LocalWorkspacePublicationGate : ILocalWorkspacePublicationGate, IDisposable
{
    private readonly SemaphoreSlim semaphore = new(1, 1);

    public ValueTask<IAsyncDisposable> AcquireReadAsync(CancellationToken cancellationToken) =>
        AcquireAsync(cancellationToken);

    public ValueTask<IAsyncDisposable> AcquireWriteAsync(CancellationToken cancellationToken) =>
        AcquireAsync(cancellationToken);

    public void Dispose() => semaphore.Dispose();

    private async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(semaphore);
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IAsyncDisposable
    {
        private int released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

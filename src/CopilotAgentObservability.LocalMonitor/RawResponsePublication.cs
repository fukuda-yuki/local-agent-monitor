using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor;

internal static class RawResponsePublication
{
    internal static bool IsSuccessful(RetentionRawTerminalResult result) =>
        result is RetentionRawTerminalResult.Sealed or RetentionRawTerminalResult.CompletedWithoutRaw;

    internal static void Abort(HttpContext context) => context.Abort();
}

internal sealed class RawRazorPageLeaseTracker
{
    private readonly List<IAsyncDisposable> leases = [];
    private int transferredOrDisposed;

    internal void Add(IAsyncDisposable lease) => leases.Add(lease);

    internal void TransferTo(HttpResponse response)
    {
        if (Interlocked.Exchange(ref transferredOrDisposed, 1) != 0) return;
        var owned = leases.ToArray();
        response.OnCompleted(async () =>
        {
            foreach (var lease in owned)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        });
    }

    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref transferredOrDisposed, 1) != 0) return;
        foreach (var lease in leases)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }
}

using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;

namespace CopilotAgentObservability.LocalMonitor;

internal sealed class RawResponseTerminalFailureException : Exception;

internal static class RawResponsePublication
{
    internal static bool AuthorizesRawDerivedPublication(RetentionRawTerminalResult result) =>
        result == RetentionRawTerminalResult.Sealed;

    internal static bool AuthorizesFixedSafePublication(RetentionRawTerminalResult result) =>
        result == RetentionRawTerminalResult.CompletedWithoutRaw;

    internal static void Abort(HttpContext context) => context.Abort();
}

internal sealed class RawRazorPageLeaseTracker
{
    private const int MaximumBufferedEntityBytes = 4 * 1024 * 1024;
    private static readonly object ContextItemKey = new();
    private readonly List<(IAsyncDisposable Lease, Func<bool> Authorize)> leases = [];
    private int transferredOrDisposed;

    internal bool HasLeases => leases.Count > 0;

    internal void Add(IAsyncDisposable lease, Func<RetentionRawTerminalResult> seal) =>
        leases.Add((lease, () => RawResponsePublication.AuthorizesRawDerivedPublication(seal())));

    internal void AddFixedSafe(IAsyncDisposable lease, Func<RetentionRawTerminalResult> complete) =>
        leases.Add((lease, () => RawResponsePublication.AuthorizesFixedSafePublication(complete())));

    internal void Attach(HttpContext context)
    {
        if (Interlocked.Exchange(ref transferredOrDisposed, 1) != 0)
            throw new InvalidOperationException("Raw Razor page leases were already transferred or disposed.");
        context.Items.Add(ContextItemKey, new Attached(leases.ToArray()));
    }

    internal static bool TryTake(HttpContext context, out Attached attached)
    {
        if (context.Items.Remove(ContextItemKey, out var value) && value is Attached found)
        {
            attached = found;
            return true;
        }
        attached = null!;
        return false;
    }

    internal async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref transferredOrDisposed, 1) != 0) return;
        foreach (var (lease, _) in leases)
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal sealed class Attached(
        IReadOnlyList<(IAsyncDisposable Lease, Func<bool> Authorize)> owned)
    {
        internal async Task ExecuteBufferedAsync(HttpContext context, Func<Task> render)
        {
            await using var buffer = new BoundedMemoryStream(MaximumBufferedEntityBytes);
            var features = context.Features;
            var originalResponse = features.GetRequiredFeature<IHttpResponseFeature>();
            var destination = context.Response.Body;
            var originalBody = features.Get<IHttpResponseBodyFeature>();
            var bufferedResponse = new HttpResponseFeature
            {
                StatusCode = StatusCodes.Status200OK,
                Headers = new HeaderDictionary { ["Cache-Control"] = "no-store" },
                Body = buffer,
            };
            features.Set<IHttpResponseFeature>(bufferedResponse);
            features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(buffer));

            try
            {
                try
                {
                    await render().ConfigureAwait(false);
                }
                catch
                {
                    RawResponsePublication.Abort(context);
                    return;
                }
                finally
                {
                    features.Set(originalResponse);
                    features.Set(originalBody);
                }

                var authorized = true;
                foreach (var (_, authorize) in owned)
                {
                    authorized &= authorize();
                }
                if (!authorized)
                {
                    originalResponse.StatusCode = StatusCodes.Status200OK;
                    originalResponse.ReasonPhrase = null;
                    originalResponse.Headers.Clear();
                    RawResponsePublication.Abort(context);
                    return;
                }

                originalResponse.StatusCode = bufferedResponse.StatusCode;
                originalResponse.ReasonPhrase = bufferedResponse.ReasonPhrase;
                originalResponse.Headers.Clear();
                foreach (var header in bufferedResponse.Headers)
                {
                    originalResponse.Headers[header.Key] = header.Value;
                }
                buffer.Position = 0;
                await buffer.CopyToAsync(destination, context.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                foreach (var (lease, _) in owned)
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private sealed class BoundedMemoryStream(int maximumBytes) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            EnsureCapacity(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            EnsureCapacity(buffer.Length);
            base.Write(buffer);
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureCapacity(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }

        private void EnsureCapacity(int count)
        {
            if (Position > maximumBytes - count)
                throw new InvalidOperationException("The raw Razor entity exceeded its bounded response buffer.");
        }
    }
}

internal sealed class RawRazorPageBufferingFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (!RawRazorPageLeaseTracker.TryTake(context.HttpContext, out var attached))
        {
            await next().ConfigureAwait(false);
            return;
        }

        await attached.ExecuteBufferedAsync(
            context.HttpContext,
            async () =>
            {
                var executed = await next().ConfigureAwait(false);
                if (executed.Exception is not null && !executed.ExceptionHandled)
                    throw executed.Exception;
            }).ConfigureAwait(false);
    }
}

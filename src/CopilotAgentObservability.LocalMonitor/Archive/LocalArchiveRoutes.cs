using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace CopilotAgentObservability.LocalMonitor.Archive;

internal static class LocalArchiveRoutes
{
    private const string DirectPath = "/api/local-monitor/v1/archive";
    private const string ActionPath = "/api/local-monitor/v1/archive-actions";
    private const string ListPath = "/api/local-monitor/v1/archived-items";
    private const int MaximumBodyBytes = 65_536;

    internal static async Task AdaptAsync(
        HttpContext context,
        RequestDelegate next,
        SqliteLocalArchiveStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(store);

        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var querySeparator = rawTarget?.IndexOf('?') ?? -1;
        var path = querySeparator < 0 ? rawTarget : rawTarget![..querySeparator];
        var owned = path switch
        {
            DirectPath => OwnedRoute.Direct,
            ActionPath => OwnedRoute.Action,
            ListPath => OwnedRoute.List,
            _ => OwnedRoute.None,
        };
        if (owned == OwnedRoute.None)
        {
            await next(context);
            return;
        }

        SetHeaders(context.Response);
        var expectedMethod = owned == OwnedRoute.Action ? HttpMethods.Post : HttpMethods.Get;
        if (!string.Equals(context.Request.Method, expectedMethod, StringComparison.Ordinal))
        {
            await WriteMethodNotAllowedAsync(
                context,
                expectedMethod,
                head: context.Request.Method == HttpMethods.Head);
            return;
        }

        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, LocalArchiveWireError.CsrfRejected);
            return;
        }

        switch (owned)
        {
            case OwnedRoute.Direct:
                await DirectAsync(context, store);
                return;
            case OwnedRoute.Action:
                await ActionAsync(context, store);
                return;
            case OwnedRoute.List:
                await ListAsync(context, store);
                return;
            default:
                throw new InvalidOperationException("local_archive_route_invalid");
        }
    }

    private static async Task DirectAsync(HttpContext context, SqliteLocalArchiveStore store)
    {
        if (!LocalArchiveWire.TryParseDirectQuery(
            context.Request.QueryString.Value, out var query, out var error))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, error!.Value);
            return;
        }

        var result = store.Read(query!.TargetKind, query.TargetId, context.RequestAborted);
        if (result.Error is { } storeError)
        {
            await WriteStoreErrorAsync(context, storeError);
            return;
        }

        ReadOnlyMemory<byte> entity;
        try
        {
            entity = LocalArchiveWire.WriteDirect(query.TargetKind, result.Success!);
        }
        catch (InvalidOperationException)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                LocalArchiveWireError.ArchiveStoreUnavailable);
            return;
        }
        await WriteAsync(context, StatusCodes.Status200OK, entity);
    }

    private static async Task ActionAsync(HttpContext context, SqliteLocalArchiveStore store)
    {
        if (!MonitorHost.HasMonitorCsrfHeader(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, LocalArchiveWireError.CsrfRejected);
            return;
        }
        if (!LocalArchiveWire.HasNoSemanticQuery(context.Request.QueryString.Value))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, LocalArchiveWireError.InvalidRequest);
            return;
        }
        if (!LocalArchiveWire.HasSupportedPostMedia(
            context.Request.Headers[HeaderNames.ContentType],
            context.Request.Headers[HeaderNames.ContentEncoding]))
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType,
                LocalArchiveWireError.UnsupportedMediaType);
            return;
        }
        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                LocalArchiveWireError.RequestTooLarge);
            return;
        }

        var body = await ReadBodyAsync(context);
        if (body is null)
            return;
        if (!LocalArchiveWire.TryParseActionBody(body.Value, out var request))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, LocalArchiveWireError.InvalidRequest);
            return;
        }

        var result = store.Mutate(
            request!.Action,
            request.TargetKind,
            request.Targets,
            LocalArchiveWire.WriteAction,
            context.RequestAborted);
        if (result.Error is { } storeError)
        {
            await WriteStoreErrorAsync(context, storeError);
            return;
        }
        await WriteAsync(context, StatusCodes.Status200OK, result.Success!.Entity);
    }

    private static async Task ListAsync(HttpContext context, SqliteLocalArchiveStore store)
    {
        if (!LocalArchiveWire.TryParseListQuery(
            context.Request.QueryString.Value, out var query, out var error))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, error!.Value);
            return;
        }

        var result = store.ListArchived(
            query!.TargetKind,
            query.After?.ArchivedAt,
            query.After?.TargetId,
            query.Limit,
            context.RequestAborted);
        if (result.Error is { } storeError)
        {
            await WriteStoreErrorAsync(context, storeError);
            return;
        }

        ReadOnlyMemory<byte> entity;
        try
        {
            var success = result.Success!;
            var nextCursor = success.HasMore
                ? LocalArchiveCursorCodec.Encode(
                    query.TargetKind,
                    new(success.Items[^1].ArchivedAt!, success.Items[^1].TargetId))
                : null;
            entity = LocalArchiveWire.WriteList(query.TargetKind, success.Items, nextCursor);
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or ArgumentException or IndexOutOfRangeException)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                LocalArchiveWireError.ArchiveStoreUnavailable);
            return;
        }
        await WriteAsync(context, StatusCodes.Status200OK, entity);
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext context)
    {
        try
        {
            using var stream = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                if (count == 0)
                    return stream.ToArray();
                if (stream.Length + count > MaximumBodyBytes)
                {
                    await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                        LocalArchiveWireError.RequestTooLarge);
                    return null;
                }
                stream.Write(buffer, 0, count);
            }
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge,
                LocalArchiveWireError.RequestTooLarge);
            return null;
        }
    }

    private static Task WriteStoreErrorAsync(HttpContext context, LocalArchiveStoreError error)
    {
        var (status, wireError) = error switch
        {
            LocalArchiveStoreError.TargetNotFound =>
                (StatusCodes.Status404NotFound, LocalArchiveWireError.TargetNotFound),
            LocalArchiveStoreError.RevisionConflict =>
                (StatusCodes.Status409Conflict, LocalArchiveWireError.RevisionConflict),
            LocalArchiveStoreError.PersistenceBusy =>
                (StatusCodes.Status503ServiceUnavailable, LocalArchiveWireError.PersistenceBusy),
            LocalArchiveStoreError.ArchiveStoreUnavailable =>
                (StatusCodes.Status503ServiceUnavailable, LocalArchiveWireError.ArchiveStoreUnavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(error)),
        };
        return WriteErrorAsync(context, status, wireError);
    }

    private static Task WriteErrorAsync(
        HttpContext context,
        int status,
        LocalArchiveWireError error)
    {
        var bytes = LocalArchiveWire.ErrorBytes(error);
        return WriteAsync(context, status, bytes);
    }

    private static async Task WriteMethodNotAllowedAsync(
        HttpContext context,
        string allow,
        bool head)
    {
        var bytes = LocalArchiveWire.ErrorBytes(LocalArchiveWireError.MethodNotAllowed);
        SetHeaders(context.Response);
        context.Response.Headers.Allow = allow;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        if (head)
        {
            context.Response.ContentLength = bytes.Length;
            return;
        }
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static async Task WriteAsync(
        HttpContext context,
        int status,
        ReadOnlyMemory<byte> bytes)
    {
        context.Response.StatusCode = status;
        SetHeaders(context.Response);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static void SetHeaders(HttpResponse response)
    {
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        response.Headers.Remove(HeaderNames.Location);
        response.Headers.Remove(HeaderNames.ETag);
        response.Headers.Remove(HeaderNames.SetCookie);
        response.Headers.Remove(HeaderNames.Allow);
        foreach (var header in response.Headers.Keys
            .Where(static name => name.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            response.Headers.Remove(header);
        }
    }

    private enum OwnedRoute
    {
        None,
        Direct,
        Action,
        List,
    }
}

using CopilotAgentObservability.LocalMonitor.SkillNative;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal enum SkillHistoricalContentRouteOutcomeV1
{
    Document,
    NotFound,
    Expired,
    ContentUnavailable,
    Unavailable,
    Busy,
    AbortWithoutResponse
}

internal sealed record SkillHistoricalContentRouteResultV1(
    SkillHistoricalContentRouteOutcomeV1 Outcome,
    byte[] BodyUtf8);

// Everything the three Skill routes need from composition. A null CurrentFile pair is the
// zero-root or uncertified-platform host: the POST is then simply not registered, which is route
// absence rather than a stage-1 unavailable response.
internal sealed record SkillInvocationSnapshotRouteServicesV1(
    Func<Guid, Guid, CancellationToken, Task<SkillInvocationMetadataDocumentV1Response>> ReadMetadataAsync,
    Func<Guid, Guid, CancellationToken, Task<SkillHistoricalContentRouteResultV1>> ReadHistoricalContentAsync,
    SkillDiscoveryRootGenerationV1? RootGeneration,
    SkillCurrentFileOrchestratorV1? CurrentFileOrchestrator);

// Gate 2's HTTP surface for the three raw Skill routes.
//
// The routes are mapped without a method constraint so this type owns the 405 completely: the
// framework must not synthesize a GET result for HEAD, and no other endpoint may answer a matching
// path with a different method. A nonmatching path never reaches here and keeps the outer 404.
internal static class SkillInvocationSnapshotRoutes
{
    internal const string MetadataPattern =
        "/api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}";

    internal const string HistoricalContentPattern = MetadataPattern + "/content";

    internal const string CurrentFilePattern = MetadataPattern + "/current-file-read";

    internal const string ContentType = "application/json; charset=utf-8";
    internal const string CacheControl = "no-store";

    internal const int CurrentFileRequestMaxBytes = SkillInvocationContentDocumentsV1.CurrentFileRequestMaxBytes;

    private const string MethodNotAllowedToken = "method_not_allowed";
    private const string CsrfRejectedToken = "csrf_rejected";
    private const string UnsupportedMediaTypeToken = "unsupported_media_type";
    private const string RequestTooLargeToken = "request_too_large";
    private const string InvalidRequestToken = "invalid_request";
    private const string SnapshotNotFoundToken = "skill_snapshot_not_found";
    private const string SnapshotExpiredToken = "skill_snapshot_expired";
    private const string SnapshotContentUnavailableToken = "skill_snapshot_content_unavailable";
    private const string PersistenceBusyToken = "persistence_busy";
    private const string UnavailableToken = "local_monitor_ui_unavailable";

    private const string MonitorCsrfHeaderName = "x-monitor-csrf";
    private const string MonitorCsrfHeaderValue = "local-monitor";

    internal static void Map(WebApplication app, SkillInvocationSnapshotRouteServicesV1 services)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(services);

        app.Map(MetadataPattern, (RequestDelegate)(context => HandleMetadataAsync(context, services)));
        app.Map(HistoricalContentPattern, (RequestDelegate)(context => HandleHistoricalContentAsync(context, services)));

        if (services.RootGeneration is { } rootGeneration && services.CurrentFileOrchestrator is { } orchestrator)
        {
            app.Map(CurrentFilePattern, (RequestDelegate)(context =>
                HandleCurrentFileAsync(context, rootGeneration, orchestrator)));
        }
    }

    private static async Task HandleMetadataAsync(HttpContext context, SkillInvocationSnapshotRouteServicesV1 services)
    {
        if (!await TryAdmitGetAsync(context).ConfigureAwait(false))
        {
            return;
        }

        if (!TryParseRouteIdentity(context, out var sessionId, out var snapshotId))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, SnapshotNotFoundToken).ConfigureAwait(false);
            return;
        }

        var response = await services.ReadMetadataAsync(sessionId, snapshotId, context.RequestAborted).ConfigureAwait(false);
        await WriteBodyAsync(context, response.StatusCode, response.BodyUtf8).ConfigureAwait(false);
    }

    private static async Task HandleHistoricalContentAsync(
        HttpContext context,
        SkillInvocationSnapshotRouteServicesV1 services)
    {
        if (!await TryAdmitGetAsync(context).ConfigureAwait(false))
        {
            return;
        }

        if (!TryParseRouteIdentity(context, out var sessionId, out var snapshotId))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound, SnapshotNotFoundToken).ConfigureAwait(false);
            return;
        }

        var result = await services.ReadHistoricalContentAsync(sessionId, snapshotId, context.RequestAborted)
            .ConfigureAwait(false);

        switch (result.Outcome)
        {
            case SkillHistoricalContentRouteOutcomeV1.Document:
                await WriteBodyAsync(context, StatusCodes.Status200OK, result.BodyUtf8).ConfigureAwait(false);
                return;
            case SkillHistoricalContentRouteOutcomeV1.NotFound:
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, SnapshotNotFoundToken).ConfigureAwait(false);
                return;
            case SkillHistoricalContentRouteOutcomeV1.Expired:
                await WriteErrorAsync(context, StatusCodes.Status410Gone, SnapshotExpiredToken).ConfigureAwait(false);
                return;
            case SkillHistoricalContentRouteOutcomeV1.ContentUnavailable:
                await WriteErrorAsync(context, 422, SnapshotContentUnavailableToken).ConfigureAwait(false);
                return;
            case SkillHistoricalContentRouteOutcomeV1.Busy:
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, PersistenceBusyToken)
                    .ConfigureAwait(false);
                return;
            case SkillHistoricalContentRouteOutcomeV1.Unavailable:
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, UnavailableToken)
                    .ConfigureAwait(false);
                return;
            default:
                RawResponsePublication.Abort(context);
                return;
        }
    }

    private static async Task HandleCurrentFileAsync(
        HttpContext context,
        SkillDiscoveryRootGenerationV1 rootGeneration,
        SkillCurrentFileOrchestratorV1 orchestrator)
    {
        if (!string.Equals(context.Request.Method, HttpMethods.Post, StringComparison.Ordinal))
        {
            await WriteMethodNotAllowedAsync(context, HttpMethods.Post).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.CacheControl = CacheControl;

        // Stage 1 finishes before any root attempt: a max-body feature fault is the fixed 503 even
        // for a cross-site request, and a shutdown racing it cannot replace a failure already
        // selected here. The other required services are captured at registration, so a route that
        // exists always has them.
        if (!TryAdmitMaxRequestBodyFeature(context, CurrentFileRequestMaxBytes))
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, UnavailableToken)
                .ConfigureAwait(false);
            return;
        }

        if (!rootGeneration.TryAcquireLease(out var rootLease))
        {
            // Lost to the atomic normal-shutdown closure: no status, header, or entity, and none of
            // the later authorities are acquired.
            RawResponsePublication.Abort(context);
            return;
        }

        using (rootLease)
        {
            if (MonitorHost.IsCrossSiteRequest(context) || !HasExactMonitorCsrfHeader(context))
            {
                await WriteErrorAsync(context, StatusCodes.Status403Forbidden, CsrfRejectedToken).ConfigureAwait(false);
                return;
            }

            if (!IsAcceptedRequestMedia(context))
            {
                await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, UnsupportedMediaTypeToken)
                    .ConfigureAwait(false);
                return;
            }

            var read = await ReadBoundedBodyAsync(context, CurrentFileRequestMaxBytes).ConfigureAwait(false);
            if (read is null)
            {
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, RequestTooLargeToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!SkillInvocationContentDocumentsV1.ParseCurrentFileRequest(read).IsAccepted)
            {
                await WriteErrorAsync(context, StatusCodes.Status400BadRequest, InvalidRequestToken)
                    .ConfigureAwait(false);
                return;
            }

            if (!TryParseRouteIdentity(context, out var sessionId, out var snapshotId))
            {
                await WriteErrorAsync(context, StatusCodes.Status404NotFound, SnapshotNotFoundToken)
                    .ConfigureAwait(false);
                return;
            }

            var result = await orchestrator
                .ExecuteAsync(sessionId, snapshotId, rootLease, context.RequestAborted)
                .ConfigureAwait(false);

            if (result.Disposition == SkillCurrentFileDispositionV1.AbortWithoutResponse)
            {
                RawResponsePublication.Abort(context);
                return;
            }

            await WriteBodyAsync(context, result.StatusCode, result.BodyUtf8).ConfigureAwait(false);
        }
    }

    // Returns false once the 405 or the CSRF 403 has been written.
    private static async Task<bool> TryAdmitGetAsync(HttpContext context)
    {
        if (!string.Equals(context.Request.Method, HttpMethods.Get, StringComparison.Ordinal))
        {
            await WriteMethodNotAllowedAsync(context, HttpMethods.Get).ConfigureAwait(false);
            return false;
        }

        context.Response.Headers.CacheControl = CacheControl;

        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, CsrfRejectedToken).ConfigureAwait(false);
            return false;
        }

        return true;
    }

    private static bool TryParseRouteIdentity(HttpContext context, out Guid sessionId, out Guid snapshotId)
    {
        sessionId = Guid.Empty;
        snapshotId = Guid.Empty;

        return context.Request.RouteValues["sessionId"] is string sessionValue
            && context.Request.RouteValues["snapshotId"] is string snapshotValue
            && Guid.TryParseExact(sessionValue, "D", out sessionId)
            && Guid.TryParseExact(snapshotValue, "D", out snapshotId);
    }

    private static bool TryAdmitMaxRequestBodyFeature(HttpContext context, long exactLimit)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly)
        {
            return false;
        }

        try
        {
            feature.MaxRequestBodySize = exactLimit;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return false;
        }

        return feature.MaxRequestBodySize == exactLimit;
    }

    private static bool HasExactMonitorCsrfHeader(HttpContext context)
    {
        var header = context.Request.Headers[MonitorCsrfHeaderName];
        return header.Count == 1
            && string.Equals(header[0], MonitorCsrfHeaderValue, StringComparison.Ordinal);
    }

    // The closed request-media parser. Nothing here concatenates, splits, trims, or normalizes the
    // field before the decision, so a second physical field or a comma list stays a 415 rather than
    // becoming a parseable single value.
    private static bool IsAcceptedRequestMedia(HttpContext context)
    {
        var contentType = context.Request.Headers.ContentType;
        if (contentType.Count != 1
            || !MediaTypeHeaderValue.TryParse(contentType[0], out var media)
            || !string.Equals(media.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (media.Parameters.Count == 0)
        {
            return true;
        }

        if (media.Parameters.Count != 1)
        {
            return false;
        }

        var parameter = media.Parameters[0];
        return string.Equals(parameter.Name.Value, "charset", StringComparison.OrdinalIgnoreCase)
            && string.Equals(parameter.Value.Value, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    // Null means the request exceeded the route-owned boundary. A declared length above it is
    // rejected before any read; otherwise the stream is read one byte past the boundary so the
    // route owns the 413 rather than inheriting a framework default.
    private static async Task<byte[]?> ReadBoundedBodyAsync(HttpContext context, int maxBytes)
    {
        if (context.Request.ContentLength is { } declared && declared > maxBytes)
        {
            return null;
        }

        var buffer = new byte[maxBytes + 1];
        var total = 0;

        try
        {
            while (total < buffer.Length)
            {
                var read = await context.Request.Body
                    .ReadAsync(buffer.AsMemory(total), context.RequestAborted)
                    .ConfigureAwait(false);

                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }
        catch (BadHttpRequestException exception)
            when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        return total > maxBytes ? null : buffer[..total];
    }

    private static Task WriteMethodNotAllowedAsync(HttpContext context, string allow)
    {
        var entity = SkillInvocationJsonWriterV1.WriteErrorEntity(MethodNotAllowedToken);
        context.Response.Headers.CacheControl = CacheControl;
        context.Response.Headers.Allow = allow;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.ContentType = ContentType;

        // HEAD carries the same owned status, headers, Allow, and Content-Length, but transmits no
        // entity bytes.
        if (string.Equals(context.Request.Method, HttpMethods.Head, StringComparison.Ordinal))
        {
            context.Response.ContentLength = entity.Length;
            return Task.CompletedTask;
        }

        return context.Response.Body.WriteAsync(entity, context.RequestAborted).AsTask();
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string token) =>
        WriteBodyAsync(context, statusCode, SkillInvocationJsonWriterV1.WriteErrorEntity(token));

    private static Task WriteBodyAsync(HttpContext context, int statusCode, byte[] bodyUtf8)
    {
        context.Response.Headers.CacheControl = CacheControl;
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ContentType;
        return context.Response.Body.WriteAsync(bodyUtf8, context.RequestAborted).AsTask();
    }
}

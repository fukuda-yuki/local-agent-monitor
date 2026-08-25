using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal sealed record SkillInvocationV2IngestRouteServicesV1(
    Func<string?, SkillRuntimeBridgeTransfer?> TryConsumeCapability,
    Func<SkillInvocationV2IngestRequestFactsV1, SkillRuntimeBridgeTransfer, CancellationToken,
        Task<SkillInvocationV2IngestResultV1>> ExecuteIngestAsync);

internal static class SkillInvocationV2IngestRoute
{
    internal const string Pattern = "/api/session-ingest/v2/events";
    internal const int MaxRequestBodyBytes = 8_388_608;

    private const string ContentType = "application/json; charset=utf-8";
    private const string CacheControl = "no-store";
    private const string CapabilityHeaderName = "X-CAO-Skill-Runtime-Capability";
    private const string VersionHeaderName = "X-CAO-Session-Event-Version";
    private const string MethodNotAllowedToken = "method_not_allowed";
    private const string UnsupportedMediaTypeToken = "unsupported_media_type";
    private const string RequestTooLargeToken = "request_too_large";
    private const string InvalidRequestToken = "invalid_request";
    private const string IdempotencyConflictToken = "idempotency_conflict";
    private const string PersistenceBusyToken = "persistence_busy";
    private const string UnavailableToken = "local_monitor_ui_unavailable";

    internal static void Map(WebApplication app, SkillInvocationV2IngestRouteServicesV1 services)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(services);

        app.Map(Pattern, (RequestDelegate)(context => HandleAsync(context, services)));
    }

    private static async Task HandleAsync(HttpContext context, SkillInvocationV2IngestRouteServicesV1 services)
    {
        if (!string.Equals(context.Request.Method, HttpMethods.Post, StringComparison.Ordinal))
        {
            await WriteMethodNotAllowedAsync(context).ConfigureAwait(false);
            return;
        }

        if (services.TryConsumeCapability is null
            || services.ExecuteIngestAsync is null
            || !TryAdmitMaxRequestBodyFeature(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, UnavailableToken)
                .ConfigureAwait(false);
            return;
        }

        var capabilityHeader = context.Request.Headers[CapabilityHeaderName];
        var transfer = capabilityHeader.Count == 1
            ? services.TryConsumeCapability(capabilityHeader[0])
            : null;
        if (transfer is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, UnavailableToken)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            if (!IsAcceptedRequestMedia(context))
            {
                await PublishNonCommitErrorAsync(
                    context,
                    transfer,
                    StatusCodes.Status415UnsupportedMediaType,
                    UnsupportedMediaTypeToken).ConfigureAwait(false);
                return;
            }

            var bodyRead = await ReadBoundedBodyAsync(context).ConfigureAwait(false);
            if (bodyRead.Outcome == BodyReadOutcome.RequestTooLarge)
            {
                await PublishNonCommitErrorAsync(
                    context,
                    transfer,
                    StatusCodes.Status413PayloadTooLarge,
                    RequestTooLargeToken).ConfigureAwait(false);
                return;
            }

            if (bodyRead.Outcome == BodyReadOutcome.InvalidRequest)
            {
                await PublishNonCommitErrorAsync(
                    context,
                    transfer,
                    StatusCodes.Status400BadRequest,
                    InvalidRequestToken).ConfigureAwait(false);
                return;
            }

            if (bodyRead.Outcome == BodyReadOutcome.CallerAbort)
            {
                RawResponsePublication.Abort(context);
                return;
            }

            var body = bodyRead.Body!;

            if (transfer.ExpectedBodyLength != body.Length
                || !CryptographicOperations.FixedTimeEquals(
                    transfer.ExpectedBodySha256,
                    SHA256.HashData(body)))
            {
                await PublishNonCommitErrorAsync(
                    context,
                    transfer,
                    StatusCodes.Status400BadRequest,
                    InvalidRequestToken).ConfigureAwait(false);
                return;
            }

            var version = context.Request.Headers[VersionHeaderName];
            if (version.Count != 1 || !string.Equals(version[0], "2", StringComparison.Ordinal))
            {
                await PublishNonCommitErrorAsync(
                    context,
                    transfer,
                    StatusCodes.Status400BadRequest,
                    InvalidRequestToken).ConfigureAwait(false);
                return;
            }

            ParsedSkillInvocationV2Batch parsed;
            try
            {
                parsed = SkillInvocationV2Parser.Parse(body, transfer.RuntimeCapability);
            }
            catch (JsonException)
            {
                await PublishNonCommitErrorAsync(
                    context,
                    transfer,
                    StatusCodes.Status400BadRequest,
                    InvalidRequestToken).ConfigureAwait(false);
                return;
            }

            var facts = SkillInvocationV2IngestRequestFactsV1.Derive(parsed);
            var result = await services.ExecuteIngestAsync(facts, transfer, context.RequestAborted)
                .ConfigureAwait(false);
            var candidate = MapResult(result.Outcome);

            if (!result.TerminalSealAttempted)
            {
                var publication = SelectNonCommitPublication(context, transfer);
                if (publication == NonCommitPublication.Abort)
                {
                    return;
                }

                if (publication == NonCommitPublication.SubstituteUnavailable)
                {
                    await WriteErrorAsync(
                        context,
                        StatusCodes.Status503ServiceUnavailable,
                        UnavailableToken).ConfigureAwait(false);
                    return;
                }
            }

            await WriteCandidateAsync(context, candidate).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            RawResponsePublication.Abort(context);
        }
        finally
        {
            transfer.ReleaseTransferredCapability();
        }
    }

    private static bool TryAdmitMaxRequestBodyFeature(HttpContext context)
    {
        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is null || feature.IsReadOnly)
        {
            return false;
        }

        try
        {
            feature.MaxRequestBodySize = MaxRequestBodyBytes;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
        {
            return false;
        }

        return feature.MaxRequestBodySize == MaxRequestBodyBytes;
    }

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

    private static async Task<BodyReadResult> ReadBoundedBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength is { } declared && declared > MaxRequestBodyBytes)
        {
            return new(BodyReadOutcome.RequestTooLarge, null);
        }

        var buffer = new byte[MaxRequestBodyBytes + 1];
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
            return new(BodyReadOutcome.RequestTooLarge, null);
        }
        // Do not let read failures propagate into framework-selected 400 bytes or connection handling;
        // this route must explicitly select its owned outer-fault response or caller-abort semantics.
        catch (BadHttpRequestException)
        {
            return new(BodyReadOutcome.InvalidRequest, null);
        }
        catch (IOException)
        {
            return new(BodyReadOutcome.CallerAbort, null);
        }
        catch (OperationCanceledException)
        {
            return new(BodyReadOutcome.CallerAbort, null);
        }

        return total > MaxRequestBodyBytes
            ? new(BodyReadOutcome.RequestTooLarge, null)
            : new(BodyReadOutcome.Success, buffer[..total]);
    }

    private static async Task PublishNonCommitErrorAsync(
        HttpContext context,
        SkillRuntimeBridgeTransfer transfer,
        int statusCode,
        string token)
    {
        var publication = SelectNonCommitPublication(context, transfer);
        if (publication == NonCommitPublication.Abort)
        {
            return;
        }

        await WriteErrorAsync(
            context,
            publication == NonCommitPublication.Original
                ? statusCode
                : StatusCodes.Status503ServiceUnavailable,
            publication == NonCommitPublication.Original ? token : UnavailableToken).ConfigureAwait(false);
    }

    private static NonCommitPublication SelectNonCommitPublication(
        HttpContext context,
        SkillRuntimeBridgeTransfer transfer)
    {
        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted)
        {
            RawResponsePublication.Abort(context);
            return NonCommitPublication.Abort;
        }

        if (transfer.TrySealV2NonCommitResponse())
        {
            return NonCommitPublication.Original;
        }

        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted)
        {
            RawResponsePublication.Abort(context);
            return NonCommitPublication.Abort;
        }

        return NonCommitPublication.SubstituteUnavailable;
    }

    private static ResponseCandidate MapResult(SkillInvocationV2IngestOutcomeV1 outcome) => outcome switch
    {
        SkillInvocationV2IngestOutcomeV1.Committed => new(StatusCodes.Status204NoContent, null),
        SkillInvocationV2IngestOutcomeV1.ReplaySucceeded => new(StatusCodes.Status204NoContent, null),
        SkillInvocationV2IngestOutcomeV1.IdempotencyConflict =>
            new(StatusCodes.Status409Conflict, IdempotencyConflictToken),
        SkillInvocationV2IngestOutcomeV1.PersistenceBusy =>
            new(StatusCodes.Status503ServiceUnavailable, PersistenceBusyToken),
        _ => new(StatusCodes.Status503ServiceUnavailable, UnavailableToken),
    };

    private static Task WriteCandidateAsync(HttpContext context, ResponseCandidate candidate)
    {
        if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted)
        {
            RawResponsePublication.Abort(context);
            return Task.CompletedTask;
        }

        if (candidate.ErrorToken is null)
        {
            context.Response.Headers.CacheControl = CacheControl;
            context.Response.Headers.Remove(HeaderNames.Allow);
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        }

        return WriteErrorAsync(context, candidate.StatusCode, candidate.ErrorToken);
    }

    private static Task WriteMethodNotAllowedAsync(HttpContext context)
    {
        var entity = SkillInvocationJsonWriterV1.WriteErrorEntity(MethodNotAllowedToken);
        context.Response.Headers.CacheControl = CacheControl;
        context.Response.Headers.Allow = HttpMethods.Post;
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.ContentType = ContentType;

        if (string.Equals(context.Request.Method, HttpMethods.Head, StringComparison.Ordinal))
        {
            context.Response.ContentLength = entity.Length;
            return Task.CompletedTask;
        }

        return context.Response.Body.WriteAsync(entity, context.RequestAborted).AsTask();
    }

    private static Task WriteErrorAsync(HttpContext context, int statusCode, string token)
    {
        var entity = SkillInvocationJsonWriterV1.WriteErrorEntity(token);
        WriteErrorHeaders(context, statusCode);
        return context.Response.Body.WriteAsync(entity, context.RequestAborted).AsTask();
    }

    private static void WriteErrorHeaders(HttpContext context, int statusCode)
    {
        context.Response.Headers.CacheControl = CacheControl;
        context.Response.Headers.Remove(HeaderNames.Allow);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = ContentType;
    }

    private sealed record ResponseCandidate(int StatusCode, string? ErrorToken);

    private sealed record BodyReadResult(BodyReadOutcome Outcome, byte[]? Body);

    private enum BodyReadOutcome
    {
        Success,
        RequestTooLarge,
        InvalidRequest,
        CallerAbort,
    }

    private enum NonCommitPublication
    {
        Original,
        SubstituteUnavailable,
        Abort,
    }
}

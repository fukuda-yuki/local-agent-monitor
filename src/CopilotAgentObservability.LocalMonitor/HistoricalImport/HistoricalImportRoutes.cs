using System.Net.Mime;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;

namespace CopilotAgentObservability.LocalMonitor.HistoricalImport;

internal static class HistoricalImportRoutes
{
    private const int MaximumBodyBytes = 1_048_576;
    private const string ApiPrefix = "/api/historical-import/v1";
    private const string JsonContentType = "application/json";

    internal static void Map(WebApplication app, IHistoricalImportApplication application)
    {
        app.MapPost($"{ApiPrefix}/previews", context => CreatePreviewAsync(context, application));
        app.MapGet($"{ApiPrefix}/previews/{{previewId}}", (string previewId, HttpContext context) => ReadPreviewAsync(context, application, previewId));
        app.MapPost($"{ApiPrefix}/confirmations", context => IssueConfirmationAsync(context, application));
        app.MapPost($"{ApiPrefix}/imports", context => CommitAsync(context, application));
        app.MapGet($"{ApiPrefix}/imports/{{operationId}}", (string operationId, HttpContext context) => ReadStatusAsync(context, application, operationId));
        app.MapGet($"{ApiPrefix}/imports/{{operationId}}/result", (string operationId, HttpContext context) => ReadResultAsync(context, application, operationId));
        app.MapGet($"{ApiPrefix}/history", context => ReadHistoryAsync(context, application));
        app.MapGet($"{ApiPrefix}/observations", context => ReadObservationsAsync(context, application));
        app.MapGet($"{ApiPrefix}/observations/{{observationId}}", (string observationId, HttpContext context) => ReadObservationAsync(context, application, observationId));
    }

    internal static bool IsPath(PathString path) => path.StartsWithSegments(ApiPrefix);

    internal static Task WriteErrorAsync(HttpContext context, int statusCode, string error)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = JsonContentType;
        context.Response.Headers.CacheControl = "no-store";
        var safeError = HistoricalImportErrorCodes.All.Contains(error) || error is "invalid_host" or "cross_origin_forbidden" or "csrf_required" or "unsupported_media_type" or "request_too_large"
            ? error
            : HistoricalImportErrorCodes.StoreUnavailable;
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{{\"error\":\"{safeError}\"}}"), context.RequestAborted).AsTask();
    }

    private static async Task CreatePreviewAsync(HttpContext context, IHistoricalImportApplication application)
    {
        if (!await AuthorizeWriteAsync(context)) return;
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserialize(body, out HistoricalImportPreviewRequest? request))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }

        await InvokeAsync(context, () => application.CreatePreview(request!));
    }

    private static async Task ReadPreviewAsync(HttpContext context, IHistoricalImportApplication application, string previewId)
    {
        if (await RejectUnexpectedQueryAsync(context)) return;
        if (!IsIdentifier(previewId, "hip_"))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }
        await InvokeAsync(context, () => application.ReadPreview(previewId));
    }

    private static async Task IssueConfirmationAsync(HttpContext context, IHistoricalImportApplication application)
    {
        if (!await AuthorizeWriteAsync(context)) return;
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserialize(body, out HistoricalImportConfirmationRequest? request))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }

        await InvokeAsync(context, () => application.IssueConfirmation(request!));
    }

    private static async Task CommitAsync(HttpContext context, IHistoricalImportApplication application)
    {
        if (!await AuthorizeWriteAsync(context)) return;
        var body = await ReadBodyAsync(context);
        if (body is null) return;
        if (!TryDeserialize(body, out HistoricalImportCommitRequest? request))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }

        await InvokeAsync(context, () => application.Commit(request!), result =>
        {
            context.Response.Headers.Location = $"{ApiPrefix}/imports/{Uri.EscapeDataString(result.OperationId)}";
        });
    }

    private static async Task ReadStatusAsync(HttpContext context, IHistoricalImportApplication application, string operationId)
    {
        if (await RejectUnexpectedQueryAsync(context)) return;
        if (!IsIdentifier(operationId, "hop_"))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }
        await InvokeAsync(context, () => application.ReadStatus(operationId));
    }

    private static async Task ReadResultAsync(HttpContext context, IHistoricalImportApplication application, string operationId)
    {
        if (await RejectUnexpectedQueryAsync(context)) return;
        if (!IsIdentifier(operationId, "hop_"))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }
        await InvokeAsync(context, () => application.ReadResult(operationId));
    }

    private static async Task ReadHistoryAsync(HttpContext context, IHistoricalImportApplication application)
    {
        if (!TryReadLimit(context, allowCursor: false, out var limit, out _))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }
        await InvokeAsync(context, () => application.ListHistory(limit));
    }

    private static async Task ReadObservationsAsync(HttpContext context, IHistoricalImportApplication application)
    {
        if (!TryReadLimit(context, allowCursor: true, out var limit, out var cursor))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }
        await InvokeAsync(context, () => application.ListObservations(limit, cursor));
    }

    private static async Task ReadObservationAsync(HttpContext context, IHistoricalImportApplication application, string observationId)
    {
        if (await RejectUnexpectedQueryAsync(context)) return;
        if (!IsIdentifier(observationId, "hob_"))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
            return;
        }
        await InvokeAsync(context, () => application.GetObservation(observationId));
    }

    private static async Task<bool> AuthorizeWriteAsync(HttpContext context)
    {
        PrepareResponse(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return false;
        }
        if (!MonitorHost.HasMonitorCsrfHeader(context))
        {
            await WriteErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_required");
            return false;
        }
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
        {
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return false;
        }
        return true;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context)
    {
        if (context.Request.ContentLength > MaximumBodyBytes)
        {
            await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
            return null;
        }

        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            total += read;
            if (total > MaximumBodyBytes)
            {
                await WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large");
                return null;
            }
            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }
        return buffer.ToArray();
    }

    private static bool TryDeserialize<T>(byte[] body, out T? value)
    {
        try
        {
            value = HistoricalImportJson.Deserialize<T>(body);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
        catch (NotSupportedException)
        {
            value = default;
            return false;
        }
    }

    private static async Task InvokeAsync<T>(HttpContext context, Func<T> action, Action<T>? beforeWrite = null)
    {
        PrepareResponse(context.Response);
        try
        {
            var value = action();
            beforeWrite?.Invoke(value);
            await context.Response.Body.WriteAsync(HistoricalImportJson.Serialize(value), context.RequestAborted);
        }
        catch (HistoricalImportException exception)
        {
            if (exception.OperationId is { } operationId
                && IsIdentifier(operationId, "hop_"))
            {
                context.Response.Headers.Location = $"{ApiPrefix}/imports/{Uri.EscapeDataString(operationId)}";
            }
            await WriteErrorAsync(context, StatusFor(exception.Code), exception.Code);
        }
    }

    private static void PrepareResponse(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.ContentType = JsonContentType;
        response.StatusCode = StatusCodes.Status200OK;
    }

    private static int StatusFor(string error) => error switch
    {
        HistoricalImportErrorCodes.PreviewNotFound or
        HistoricalImportErrorCodes.OperationNotFound or
        HistoricalImportErrorCodes.ObservationNotFound => StatusCodes.Status404NotFound,
        HistoricalImportErrorCodes.PreviewExpired or
        HistoricalImportErrorCodes.ConfirmationExpired => StatusCodes.Status410Gone,
        HistoricalImportErrorCodes.PreviewStale or
        HistoricalImportErrorCodes.SourceChanged or
        HistoricalImportErrorCodes.NoEligibleCandidates or
        HistoricalImportErrorCodes.ConfirmationInvalid or
        HistoricalImportErrorCodes.ConfirmationConsumed or
        HistoricalImportErrorCodes.IdempotencyConflict or
        HistoricalImportErrorCodes.ResultNotAvailable or
        HistoricalImportErrorCodes.ProfileNotAdmitted or
        HistoricalImportErrorCodes.CandidateInvalid or
        HistoricalImportErrorCodes.FixtureNotSourceSupportEvidence => StatusCodes.Status409Conflict,
        HistoricalImportErrorCodes.StoreBusy or
        HistoricalImportErrorCodes.StoreUnavailable or
        HistoricalImportErrorCodes.TransactionFailed => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest,
    };

    private static bool TryReadLimit(HttpContext context, bool allowCursor, out int limit, out string? cursor)
    {
        limit = 100;
        cursor = null;
        foreach (var key in context.Request.Query.Keys)
        {
            if (key != "limit" && (!allowCursor || key != "cursor")) return false;
        }
        if (context.Request.Query.ContainsKey("limit"))
        {
            var limitText = context.Request.Query["limit"].ToString();
            if (string.IsNullOrEmpty(limitText)
                || !int.TryParse(limitText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out limit)
                || limit is < 1 or > 100) return false;
        }
        if (allowCursor)
        {
            if (context.Request.Query.ContainsKey("cursor"))
            {
                cursor = context.Request.Query["cursor"].ToString();
                if (string.IsNullOrEmpty(cursor) || !IsIdentifier(cursor, "hoc_")) return false;
            }
        }
        return true;
    }

    private static async Task<bool> RejectUnexpectedQueryAsync(HttpContext context)
    {
        if (context.Request.Query.Count == 0) return false;
        await WriteErrorAsync(context, StatusCodes.Status400BadRequest, HistoricalImportErrorCodes.RequestInvalid);
        return true;
    }

    private static bool IsIdentifier(string value, string prefix) =>
        value.Length == prefix.Length + 32
        && value.StartsWith(prefix, StringComparison.Ordinal)
        && value.AsSpan(prefix.Length).ToString().All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

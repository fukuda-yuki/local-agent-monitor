using System.Globalization;
using System.Net.Mime;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.SanitizedImport;
using Microsoft.AspNetCore.Http.Features;

namespace CopilotAgentObservability.LocalMonitor;

internal static class SanitizedImportRoutes
{
    private const string PreviewDigestHeader = "X-Sanitized-Import-Preview-Digest";

    internal static bool IsPath(PathString path) =>
        path == "/sanitized-import" || path.StartsWithSegments("/api/sanitized-import/v1");

    internal static void Map(WebApplication app, SqliteSanitizedImportStore store)
    {
        app.MapPost("/api/sanitized-import/v1/previews", context => PreviewAsync(context, store));
        app.MapPost("/api/sanitized-import/v1/imports", context => CommitAsync(context, store));
        app.MapGet("/api/sanitized-import/v1/imports", context => ListAsync(context, store));
        app.MapGet("/api/sanitized-import/v1/imports/{importId}", (string importId, HttpContext context) => DetailAsync(context, store, importId));
    }

    internal static Task ErrorAsync(HttpContext context, int status, string error)
    {
        Prepare(context.Response);
        context.Response.StatusCode = status;
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}"), context.RequestAborted).AsTask();
    }

    internal static async Task PreviewAsync(HttpContext context, SqliteSanitizedImportStore store)
    {
        if (!await AuthorizeAsync(context)) return;
        var archive = await ReadAsync(context);
        if (archive is null) return;
        var result = store.Preview(archive);
        if (!result.Success)
        {
            await ErrorAsync(context, Status(result.ErrorCode!), result.ErrorCode!);
            return;
        }
        await JsonAsync(context, StatusCodes.Status200OK, result);
    }

    internal static async Task CommitAsync(HttpContext context, SqliteSanitizedImportStore store)
    {
        if (!await AuthorizeAsync(context)) return;
        if (!context.Request.Headers.TryGetValue(PreviewDigestHeader, out var digestValues)
            || digestValues.Count != 1 || string.IsNullOrWhiteSpace(digestValues[0]))
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "preview_digest_invalid");
            return;
        }
        var archive = await ReadAsync(context);
        if (archive is null) return;
        var result = store.Commit(archive, digestValues[0]!);
        if (!result.Success)
        {
            await ErrorAsync(context, Status(result.ErrorCode!), result.ErrorCode!);
            return;
        }
        context.Response.Headers.Location = $"/api/sanitized-import/v1/imports/{result.ImportId}";
        await JsonAsync(context, result.IdempotentReplay ? StatusCodes.Status200OK : StatusCodes.Status201Created, result);
    }

    private static async Task ListAsync(HttpContext context, SqliteSanitizedImportStore store)
    {
        Prepare(context.Response);
        if (!AuthorizeRead(context))
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return;
        }
        if (!TryLimit(context, out var limit))
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        await JsonAsync(context, StatusCodes.Status200OK, store.ListHistory(limit));
    }

    private static async Task DetailAsync(HttpContext context, SqliteSanitizedImportStore store, string importId)
    {
        Prepare(context.Response);
        if (!AuthorizeRead(context))
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return;
        }
        if (context.Request.Query.Count != 0)
        {
            await ErrorAsync(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        if (store.GetHistory(importId) is not { } result)
        {
            await ErrorAsync(context, StatusCodes.Status404NotFound, "import_not_found");
            return;
        }
        await JsonAsync(context, StatusCodes.Status200OK, result);
    }

    private static async Task<bool> AuthorizeAsync(HttpContext context)
    {
        Prepare(context.Response);
        if (!AuthorizeRead(context))
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden");
            return false;
        }
        if (!MonitorHost.HasMonitorCsrfHeader(context))
        {
            await ErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_required");
            return false;
        }
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], MediaTypeNames.Application.Zip, StringComparison.OrdinalIgnoreCase))
        {
            await ErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type");
            return false;
        }
        if (context.Request.ContentLength > SanitizedExportLimits.MaximumUncompressedBytes)
        {
            await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "bundle_too_large");
            return false;
        }
        var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (bodySize is { IsReadOnly: false })
            bodySize.MaxRequestBodySize = SanitizedExportLimits.MaximumUncompressedBytes + 1L;
        return true;
    }

    private static async Task<byte[]?> ReadAsync(HttpContext context)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[8192];
        long total = 0;
        int read;
        try
        {
            while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
            {
                total += read;
                if (total > SanitizedExportLimits.MaximumUncompressedBytes)
                {
                    await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "bundle_too_large");
                    return null;
                }
                await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
            }
        }
        catch (BadHttpRequestException exception)
        {
            var mapped = MapUnhandledException(exception);
            await ErrorAsync(context, mapped.Status, mapped.Error);
            return null;
        }
        return output.ToArray();
    }

    private static bool TryLimit(HttpContext context, out int limit)
    {
        limit = SanitizedImportLimits.DefaultHistoryItems;
        if (context.Request.Query.Any(item => item.Key != "limit")
            || !context.Request.Query.TryGetValue("limit", out var values)) return context.Request.Query.Count == 0;
        return values.Count == 1
            && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            && limit is >= 1 and <= SanitizedImportLimits.MaximumHistoryItems;
    }

    private static bool AuthorizeRead(HttpContext context) => !MonitorHost.IsCrossSiteRequest(context);

    private static int Status(string errorCode) => errorCode switch
    {
        "preview_digest_invalid" => StatusCodes.Status400BadRequest,
        "record_conflict" or "preview_changed" => StatusCodes.Status409Conflict,
        "bundle_too_large" or "entry_limit_exceeded" or "uncompressed_size_limit_exceeded" or "graph_limit_exceeded" => StatusCodes.Status413PayloadTooLarge,
        "import_store_busy" or "import_store_unavailable" or "import_transaction_failed" or "import_integrity_failed" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    internal static (int Status, string Error) MapUnhandledException(Exception? exception)
    {
        if (exception is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
            return (StatusCodes.Status413PayloadTooLarge, "bundle_too_large");
        if (exception is BadHttpRequestException)
            return (StatusCodes.Status400BadRequest, "invalid_request");
        return (StatusCodes.Status503ServiceUnavailable, "import_store_unavailable");
    }

    private static async Task JsonAsync<T>(HttpContext context, int status, T value)
    {
        Prepare(context.Response);
        context.Response.StatusCode = status;
        await context.Response.Body.WriteAsync(SanitizedImportJson.Serialize(value), context.RequestAborted);
    }

    private static void Prepare(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.ContentType = "application/json";
    }
}

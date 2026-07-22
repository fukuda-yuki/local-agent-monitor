using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor;

internal static class SanitizedExportRoutes
{
    private const int MaximumRequestBytes = SanitizedExportLimits.MaximumControlRequestBytes;

    internal static bool IsPath(PathString path) => path.StartsWithSegments("/api/sanitized-export/v1");

    internal static void Map(WebApplication app, string databasePath, ISanitizedExportSnapshotProvider? snapshotProvider = null)
    {
        var application = new Application(
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "sanitized-exports"),
            snapshotProvider ?? new SqliteSanitizedExportSnapshotProvider(databasePath));
        app.MapPost("/api/sanitized-export/v1/previews", context => PreviewAsync(context, application));
        app.MapPost("/api/sanitized-export/v1/exports", context => CreateAsync(context, application));
        app.MapGet("/api/sanitized-export/v1/exports/{exportId}", (string exportId, HttpContext context) => ResultAsync(context, application, exportId));
        app.MapGet("/api/sanitized-export/v1/exports/{exportId}/archive", (string exportId, HttpContext context) => DownloadAsync(context, application, exportId));
    }

    internal static Task ErrorAsync(HttpContext context, int status, string error)
    {
        Prepare(context.Response, "application/json");
        context.Response.StatusCode = status;
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}"), context.RequestAborted).AsTask();
    }

    private static async Task PreviewAsync(HttpContext context, Application application)
    {
        var request = await AuthorizeAndReadAsync(context);
        if (request is null) return;
        var result = application.Preview(request);
        if (!result.Success)
        {
            await ErrorAsync(context, Status(result.ErrorCode!), result.ErrorCode!);
            return;
        }
        await JsonAsync(context, StatusCodes.Status200OK, result);
    }

    private static async Task CreateAsync(HttpContext context, Application application)
    {
        var request = await AuthorizeAndReadAsync(context);
        if (request is null) return;
        var result = application.Create(request);
        if (result.ErrorCode is not null)
        {
            await ErrorAsync(context, Status(result.ErrorCode), result.ErrorCode);
            return;
        }
        context.Response.Headers.Location = $"/api/sanitized-export/v1/exports/{result.ExportId}";
        await JsonAsync(context, StatusCodes.Status201Created, result);
    }

    private static async Task ResultAsync(HttpContext context, Application application, string exportId)
    {
        if (!AuthorizeRead(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return; }
        if (!application.TryGet(exportId, out var result)) { await ErrorAsync(context, StatusCodes.Status404NotFound, "export_not_found"); return; }
        await JsonAsync(context, StatusCodes.Status200OK, result);
    }

    private static async Task DownloadAsync(HttpContext context, Application application, string exportId)
    {
        if (!AuthorizeRead(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return; }
        if (!application.TryReadArchive(exportId, out var bytes)) { await ErrorAsync(context, StatusCodes.Status404NotFound, "export_not_found"); return; }
        Prepare(context.Response, "application/zip");
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"sanitized-evidence-{exportId[..12]}.zip\"";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static async Task<SanitizedExportControlRequest?> AuthorizeAndReadAsync(HttpContext context)
    {
        Prepare(context.Response, "application/json");
        if (!AuthorizeRead(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return null; }
        if (!MonitorHost.HasMonitorCsrfHeader(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_required"); return null; }
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], MediaTypeNames.Application.Json, StringComparison.OrdinalIgnoreCase))
        { await ErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type"); return null; }
        if (context.Request.ContentLength > MaximumRequestBytes) { await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large"); return null; }
        await using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var total = 0;
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
        {
            total += read;
            if (total > MaximumRequestBytes) { await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "request_too_large"); return null; }
            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }
        try { return SanitizedExportJson.DeserializeControlRequest(buffer.ToArray()); }
        catch (JsonException) { await ErrorAsync(context, StatusCodes.Status400BadRequest, "request_invalid"); return null; }
    }

    private static bool AuthorizeRead(HttpContext context) => !MonitorHost.IsCrossSiteRequest(context);

    private static int Status(string errorCode) => errorCode switch
    {
        "request_invalid" => StatusCodes.Status400BadRequest,
        "publish_failed" or "output_exists" or "snapshot_provider_unavailable" or "snapshot_store_busy" or "snapshot_store_unavailable" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    private static async Task JsonAsync<T>(HttpContext context, int status, T value)
    {
        Prepare(context.Response, "application/json");
        context.Response.StatusCode = status;
        await context.Response.Body.WriteAsync(SanitizedExportJson.Serialize(value), context.RequestAborted);
    }

    private static void Prepare(HttpResponse response, string contentType)
    {
        response.Headers.CacheControl = "no-store";
        response.ContentType = contentType;
    }

    private sealed class Application(string outputDirectory, ISanitizedExportSnapshotProvider snapshotProvider)
    {
        private readonly SanitizedExportAuthorizedService authorizedService = new(snapshotProvider);
        private readonly SanitizedExportBundleInspector inspectionService = new();
        private readonly ConcurrentDictionary<string, ExportApiResult> results = new(StringComparer.Ordinal);

        internal SanitizedExportPreview Preview(SanitizedExportControlRequest request) => authorizedService.Preview(request);

        internal ExportApiResult Create(SanitizedExportControlRequest request)
        {
            try
            {
                var created = authorizedService.Create(request);
                if (!created.Success) return new(null, created.ErrorCode, null, null, null);
                var exportId = created.ArchiveSha256!;
                Directory.CreateDirectory(outputDirectory);
                var path = Path.Combine(outputDirectory, $"{exportId}.zip");
                if (File.Exists(path))
                {
                    if (!TryReadBounded(path, out var existing) || existing.LongLength != created.ArchiveBytes!.LongLength
                        || !existing.AsSpan().SequenceEqual(created.ArchiveBytes)
                        || !inspectionService.Inspect(existing).Success) return new(null, "publish_failed", null, null, null);
                }
                else
                {
                    if (!TryPublish(path, created.ArchiveBytes!)) return new(null, "publish_failed", null, null, null);
                }
                var result = new ExportApiResult(exportId, null, created.ArchiveSha256, created.ArchiveBytes!.LongLength, created.Preview, $"/api/sanitized-export/v1/exports/{exportId}/archive");
                results[exportId] = result;
                return result;
            }
            catch (IOException) { return new(null, "publish_failed", null, null, null); }
            catch (UnauthorizedAccessException) { return new(null, "publish_failed", null, null, null); }
            catch (System.Security.SecurityException) { return new(null, "publish_failed", null, null, null); }
        }

        internal bool TryGet(string exportId, out ExportApiResult result) => results.TryGetValue(exportId, out result!);
        internal bool TryReadArchive(string exportId, out byte[] bytes)
        {
            bytes = [];
            try
            {
                if (!results.TryGetValue(exportId, out var expected)) return false;
                var path = Path.Combine(outputDirectory, $"{exportId}.zip");
                if (!File.Exists(path)) return false;
                if (!TryReadBounded(path, out bytes) || bytes.LongLength != expected.ArchiveLength
                    || Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant() != expected.ArchiveSha256
                    || !inspectionService.Inspect(bytes).Success) { bytes = []; return false; }
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (System.Security.SecurityException) { return false; }
        }

        private static bool TryReadBounded(string path, out byte[] bytes)
        {
            bytes = [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > SanitizedExportLimits.MaximumUncompressedBytes) return false;
            using var output = new MemoryStream((int)stream.Length);
            var buffer = new byte[8192];
            long total = 0;
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                total += read;
                if (total > SanitizedExportLimits.MaximumUncompressedBytes) return false;
                output.Write(buffer, 0, read);
            }
            bytes = output.ToArray();
            return true;
        }

        private static bool TryPublish(string path, byte[] bytes)
        {
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.partial";
            try
            {
                if (File.Exists(path)) return false;
                File.WriteAllBytes(temporaryPath, bytes);
                File.Move(temporaryPath, path, overwrite: false);
                return true;
            }
            finally
            {
                try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private sealed record ExportApiResult(string? ExportId, string? ErrorCode, string? ArchiveSha256, [property: System.Text.Json.Serialization.JsonIgnore] long ArchiveLength, SanitizedExportPreview? Preview, string? DownloadPath)
    {
        internal ExportApiResult(string? exportId, string? errorCode, string? archiveSha256, SanitizedExportPreview? preview, string? downloadPath)
            : this(exportId, errorCode, archiveSha256, 0, preview, downloadPath) { }
    }
}

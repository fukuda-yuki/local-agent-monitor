using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.LocalMonitor;

internal static class RawReplayRoutes
{
    internal static bool IsPath(PathString path) => path.StartsWithSegments("/api/raw-replay/v1");

    internal static void Map(WebApplication app, string databasePath, RetentionCatalogStore catalog, bool sanitizedOnly,
        TimeProvider timeProvider, IRawReplaySnapshotProvider? snapshotProvider = null)
    {
        var application = new Application(databasePath, catalog, timeProvider,
            snapshotProvider ?? new SqliteRawReplaySnapshotProvider(databasePath));
        app.MapPost("/api/raw-replay/v1/export-previews", context => ExportPreviewAsync(context, application, sanitizedOnly))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/api/raw-replay/v1/exports", context => ExportAsync(context, application, sanitizedOnly))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapGet("/api/raw-replay/v1/exports/{exportId}", (string exportId, HttpContext context) => ExportResultAsync(context, application, sanitizedOnly, exportId));
        app.MapGet("/api/raw-replay/v1/exports/{exportId}/archive", (string exportId, HttpContext context) => ExportDownloadAsync(context, application, sanitizedOnly, exportId));
        app.MapPost("/api/raw-replay/v1/replay-previews", context => ReplayPreviewAsync(context, application, sanitizedOnly))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapPost("/api/raw-replay/v1/replays", context => ReplayAsync(context, application, sanitizedOnly))
            .WithMetadata(new Microsoft.AspNetCore.Mvc.DisableRequestSizeLimitAttribute());
        app.MapGet("/api/raw-replay/v1/replays/{replayId}", (string replayId, HttpContext context) => ReplayResultAsync(context, application, sanitizedOnly, replayId));
    }

    internal static Task ErrorAsync(HttpContext context, int status, string error)
    {
        Prepare(context.Response, MediaTypeNames.Application.Json);
        context.Response.StatusCode = status;
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{{\"error\":\"{error}\"}}"), context.RequestAborted).AsTask();
    }

    private static async Task ExportPreviewAsync(HttpContext context, Application application, bool sanitizedOnly)
    {
        var control = await ReadJsonAsync<RawReplayExportControl>(context, sanitizedOnly);
        if (control is null) return;
        var preview = await application.ExportPreviewAsync(control, context.RequestAborted);
        if (!preview.Success) { await ErrorAsync(context, Status(preview.ErrorCode!), preview.ErrorCode!); return; }
        await JsonAsync(context, StatusCodes.Status200OK, preview);
    }

    private static async Task ExportAsync(HttpContext context, Application application, bool sanitizedOnly)
    {
        var control = await ReadJsonAsync<RawReplayExportControl>(context, sanitizedOnly);
        if (control is null) return;
        var result = await application.ExportAsync(control, context.RequestAborted);
        if (result.ErrorCode is not null) { await ErrorAsync(context, Status(result.ErrorCode), result.ErrorCode); return; }
        context.Response.Headers.Location = $"/api/raw-replay/v1/exports/{result.ExportId}";
        await JsonAsync(context, StatusCodes.Status201Created, result);
    }

    private static async Task ExportResultAsync(HttpContext context, Application application, bool sanitizedOnly, string exportId)
    {
        if (!await AuthorizeReadAsync(context, sanitizedOnly)) return;
        if (!application.TryGetExport(exportId, out var result)) { await ErrorAsync(context, 404, "export_not_found"); return; }
        await JsonAsync(context, 200, result);
    }

    private static async Task ExportDownloadAsync(HttpContext context, Application application, bool sanitizedOnly, string exportId)
    {
        if (!await AuthorizeReadAsync(context, sanitizedOnly)) return;
        if (!application.TryGetArchive(exportId, out var bytes)) { await ErrorAsync(context, 404, "export_not_found"); return; }
        Prepare(context.Response, "application/zip"); context.Response.StatusCode = 200;
        context.Response.Headers.ContentDisposition = $"attachment; filename=\"raw-local-replay-{exportId[..12]}.zip\"";
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static async Task ReplayPreviewAsync(HttpContext context, Application application, bool sanitizedOnly)
    {
        if (!await AuthorizeWriteAsync(context, sanitizedOnly, "application/zip")) return;
        var bytes = await ReadBodyAsync(context, RawReplayLimits.MaximumArchiveBytes, "archive_too_large");
        if (bytes is null) return;
        var preview = application.ReplayPreview(bytes);
        if (preview.ErrorCode is not null) { await ErrorAsync(context, Status(preview.ErrorCode), preview.ErrorCode); return; }
        await JsonAsync(context, 200, preview);
    }

    private static async Task ReplayAsync(HttpContext context, Application application, bool sanitizedOnly)
    {
        var control = await ReadJsonAsync<RawReplayControl>(context, sanitizedOnly);
        if (control is null) return;
        var result = await application.ReplayAsync(control, context.RequestAborted);
        if (result.ErrorCode is not null) { await ErrorAsync(context, Status(result.ErrorCode), result.ErrorCode); return; }
        context.Response.Headers.Location = $"/api/raw-replay/v1/replays/{Uri.EscapeDataString(control.ReplayId)}";
        await JsonAsync(context, result.IdempotentReplay ? 200 : 201, result);
    }

    private static async Task ReplayResultAsync(HttpContext context, Application application, bool sanitizedOnly, string replayId)
    {
        if (!await AuthorizeReadAsync(context, sanitizedOnly)) return;
        var result = await application.ReadReplayAsync(replayId, context.RequestAborted);
        if (result.ErrorCode is not null) { await ErrorAsync(context, Status(result.ErrorCode), result.ErrorCode); return; }
        await JsonAsync(context, 200, result);
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext context, bool sanitizedOnly)
    {
        if (!await AuthorizeWriteAsync(context, sanitizedOnly, MediaTypeNames.Application.Json)) return default;
        var bytes = await ReadBodyAsync(context, RawReplayLimits.MaximumControlBytes, "request_too_large");
        if (bytes is null) return default;
        try { return RawReplayJson.DeserializeExact<T>(bytes); }
        catch (JsonException) { await ErrorAsync(context, 400, "request_invalid"); return default; }
    }

    private static async Task<bool> AuthorizeWriteAsync(HttpContext context, bool sanitizedOnly, string mediaType)
    {
        Prepare(context.Response, MediaTypeNames.Application.Json);
        if (MonitorHost.IsCrossSiteRequest(context)) { await ErrorAsync(context, 403, "cross_origin_forbidden"); return false; }
        if (!MonitorHost.HasMonitorCsrfHeader(context)) { await ErrorAsync(context, 403, "csrf_required"); return false; }
        if (sanitizedOnly) { await ErrorAsync(context, 403, "sanitized_only_denied"); return false; }
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], mediaType, StringComparison.OrdinalIgnoreCase))
        { await ErrorAsync(context, 415, "unsupported_media_type"); return false; }
        return true;
    }

    private static async Task<bool> AuthorizeReadAsync(HttpContext context, bool sanitizedOnly)
    {
        Prepare(context.Response, MediaTypeNames.Application.Json);
        if (MonitorHost.IsCrossSiteRequest(context)) { await ErrorAsync(context, 403, "cross_origin_forbidden"); return false; }
        if (sanitizedOnly) { await ErrorAsync(context, 403, "sanitized_only_denied"); return false; }
        return true;
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, int maximum, string error)
    {
        if (context.Request.ContentLength > maximum) { await ErrorAsync(context, 413, error); return null; }
        await using var output = new MemoryStream(); var buffer = new byte[8192]; var total = 0;
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (read == 0) break;
            total += read; if (total > maximum) { await ErrorAsync(context, 413, error); return null; }
            await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
        }
        return output.ToArray();
    }

    private static int Status(string code) => code switch
    {
        "request_invalid" or "profile_invalid" or "replay_id_invalid" => 400,
        "replay_not_found" or "export_not_found" => 404,
        "sanitized_only_denied" or "consent_required" => 403,
        "preview_changed" or "preview_expired" or "replay_id_conflict" or "source_id_conflict"
            or "normalization_version_mismatch" or "projection_version_mismatch" or "dashboard_version_mismatch" => 409,
        "request_too_large" or "archive_too_large" or "uncompressed_size_limit_exceeded" or "staging_size_limit_exceeded"
            or "entry_too_large" or "entry_limit_exceeded" or "manifest_too_large" or "selection_limit_exceeded" => 413,
        "snapshot_store_busy" or "snapshot_store_unavailable" or "snapshot_read_denied" or "snapshot_member_missing" or "replay_store_busy"
            or "replay_store_denied" or "replay_publish_failed" or "publish_failed" => 503,
        _ => 422,
    };

    private static async Task JsonAsync<T>(HttpContext context, int status, T value)
    {
        Prepare(context.Response, MediaTypeNames.Application.Json); context.Response.StatusCode = status;
        await context.Response.Body.WriteAsync(RawReplayJson.Serialize(value), context.RequestAborted);
    }

    private static void Prepare(HttpResponse response, string contentType)
    {
        response.Headers.CacheControl = "no-store"; response.ContentType = contentType;
    }

    private sealed class Application
    {
        private readonly RawReplayAuthorizedService exportService;
        private readonly RawReplayArchiveService archiveService = new();
        private readonly RetentionRawReplayStore replayStore;
        private readonly TimeProvider timeProvider;
        private readonly ConcurrentDictionary<string, ExportResult> exports = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, PendingReplay> previews = new(StringComparer.Ordinal);

        internal Application(string databasePath, RetentionCatalogStore catalog, TimeProvider timeProvider, IRawReplaySnapshotProvider provider)
        {
            exportService = new(provider); this.timeProvider = timeProvider;
            replayStore = new(catalog, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "raw-replays"), timeProvider);
        }

        internal ValueTask<RawReplayPreview> ExportPreviewAsync(RawReplayExportControl control, CancellationToken token) => exportService.PreviewAsync(control, token);

        internal async ValueTask<ExportResult> ExportAsync(RawReplayExportControl control, CancellationToken token)
        {
            var created = await exportService.CreateAsync(control, token);
            if (!created.Success) return new(null, created.ErrorCode, null, null, null);
            var id = created.ArchiveSha256!;
            var archive = created.ArchiveBytes!;
            exports[id] = new(id, null, id, created.Preview, $"/api/raw-replay/v1/exports/{id}/archive", archive);
            return exports[id] with { Archive = null };
        }

        internal bool TryGetExport(string id, out ExportResult result)
        {
            if (exports.TryGetValue(id, out var stored)) { result = stored with { Archive = null }; return true; }
            result = null!; return false;
        }

        internal bool TryGetArchive(string id, out byte[] bytes)
        {
            bytes = [];
            if (!exports.TryGetValue(id, out var stored) || stored.Archive is null
                || RawReplayHash.Sha256(stored.Archive) != stored.ArchiveSha256
                || !archiveService.Inspect(stored.Archive).Success) return false;
            bytes = stored.Archive.ToArray(); return true;
        }

        internal ReplayPreviewResult ReplayPreview(byte[] archive)
        {
            PurgeExpired();
            var inspected = archiveService.Inspect(archive);
            if (!inspected.Success || inspected.Bundle is null) return new(inspected.ErrorCode, null, null, null, null, null, null, 0, 0, [], null);
            var expires = timeProvider.GetUtcNow().AddMinutes(10);
            var digest = RawReplayHash.Framed("copilot-agent-observability/raw-local-replay-import-preview/v1",
                Encoding.UTF8.GetBytes(inspected.ArchiveSha256!), Encoding.UTF8.GetBytes(inspected.Bundle.Manifest.NormalizationVersion),
                Encoding.UTF8.GetBytes(inspected.Bundle.Manifest.ProjectionVersion), Encoding.UTF8.GetBytes(inspected.Bundle.Manifest.DashboardVersion),
                Encoding.UTF8.GetBytes(expires.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
            previews[digest] = new(archive.ToArray(), inspected.ArchiveSha256!, expires);
            return new(null, RawReplayWarnings.RawData, "raw", inspected.ArchiveSha256, inspected.Bundle.Manifest.NormalizationVersion,
                inspected.Bundle.Manifest.ProjectionVersion, inspected.Bundle.Manifest.DashboardVersion, inspected.RawRecordCount,
                inspected.SessionContentCount, inspected.Bundle.Manifest.SourceVersions, digest, expires);
        }

        internal async ValueTask<ReplayResultView> ReplayAsync(RawReplayControl control, CancellationToken token)
        {
            PurgeExpired();
            if (ControlError(control) is { } error) return new(false, error, false, null);
            if (control.PreviewDigest is null || !previews.TryRemove(control.PreviewDigest, out var pending))
                return new(false, "preview_expired", false, null);
            if (pending.ExpiresAt <= timeProvider.GetUtcNow()) return new(false, "preview_expired", false, null);
            if (pending.ArchiveSha256 != control.ArchiveSha256) return new(false, "preview_changed", false, null);
            var execution = await replayStore.ReplayAsync(control.ReplayId, pending.Archive, token);
            return new(execution.Success, execution.ErrorCode, execution.IdempotentReplay, execution.Result);
        }

        internal async ValueTask<ReplayResultView> ReadReplayAsync(string replayId, CancellationToken token)
        {
            if (!ValidReplayId(replayId)) return new(false, "replay_id_invalid", false, null);
            var retained = await replayStore.ReadAsync(replayId, token);
            if (retained.Lease is null)
                return new(false, retained.Disposition switch
                {
                    RetainedRawReplayReadDisposition.Busy => "replay_store_busy",
                    RetainedRawReplayReadDisposition.Denied => "replay_store_denied",
                    _ => "replay_not_found",
                }, false, null);
            await using var lease = retained.Lease;
            return new(true, null, true, lease.Receipt);
        }

        private void PurgeExpired()
        {
            var now = timeProvider.GetUtcNow();
            foreach (var item in previews.Where(item => item.Value.ExpiresAt <= now)) previews.TryRemove(item.Key, out _);
        }

        private static string? ControlError(RawReplayControl control)
        {
            if (control.SchemaVersion != RawReplayContractVersions.ReplayControl) return "request_invalid";
            if (control.Profile != RawReplayContractVersions.BundleProfile) return "profile_invalid";
            if (!ValidReplayId(control.ReplayId)) return "replay_id_invalid";
            if (control.SanitizedOnly) return "sanitized_only_denied";
            if (control.Consent is null || !control.Consent.IsValid) return "consent_required";
            if (control.NormalizationVersion != RawReplayContractVersions.Normalization) return "normalization_version_mismatch";
            if (control.ProjectionVersion != RawReplayContractVersions.Projection) return "projection_version_mismatch";
            if (control.DashboardVersion != RawReplayContractVersions.Dashboard) return "dashboard_version_mismatch";
            if (control.ArchiveSha256 is not { Length: 64 }
                || control.ArchiveSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                return "request_invalid";
            return null;
        }

        private static bool ValidReplayId(string? value) => value is { Length: >= 8 and <= 64 }
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
            && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
            && !RawReplayCredentialScanner.ContainsKnownCredential(value);
    }

    private sealed record ExportResult(string? ExportId, string? ErrorCode, string? ArchiveSha256, RawReplayPreview? Preview,
        string? DownloadPath, [property: System.Text.Json.Serialization.JsonIgnore] byte[]? Archive = null);
    private sealed record PendingReplay(byte[] Archive, string ArchiveSha256, DateTimeOffset ExpiresAt);
    private sealed record ReplayPreviewResult(string? ErrorCode, string? Warning, string? DataClassification, string? ArchiveSha256,
        string? NormalizationVersion, string? ProjectionVersion, string? DashboardVersion, int RawRecordCount, int SessionContentCount,
        IReadOnlyList<string> SourceVersions, string? PreviewDigest, DateTimeOffset? ExpiresAt = null);
    private sealed record ReplayResultView(bool Success, string? ErrorCode, bool IdempotentReplay, RawReplayReceipt? Result);
}

using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Archive;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal static class LocalMonitorV1CollectionRoutes
{
    internal const string RepositoriesPath = "/api/local-monitor/v1/repositories";
    internal const string SessionsPath = "/api/local-monitor/v1/sessions";

    internal static bool IsPath(PathString path) => path == RepositoriesPath || path == SessionsPath;

    internal static void Map(WebApplication app, ILocalRepositoryScopeSnapshotService service, LocalMonitorV1CollectionTestOverrides? testOverrides = null)
    {
        var cursorKey = testOverrides?.CursorKey.ToArray() ?? RandomNumberGenerator.GetBytes(32);
        app.MapGet(RepositoriesPath, async context =>
        {
            if (MonitorHost.IsCrossSiteRequest(context)) { await Error(context, 403, "csrf_rejected"); return; }
            if (!LocalMonitorV1RepositoryRequestParser.TryParse(context.Request.QueryString.Value ?? "", out var request)) { await Error(context, 400, "invalid_request"); return; }
            try
            {
                var snapshot = await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), context.RequestAborted);
                await Success(context, LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot, request!));
            }
            catch (LocalRepositoryScopeSnapshotException) { await Error(context, 503, "persistence_busy"); }
            catch (LocalMonitorV1CollectionException e) { await Error(context, e.Error == "workspace_too_large" ? 409 : 400, e.Error); }
        });
        app.Map(SessionsPath, async context =>
        {
            if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.Headers.Allow = "POST"; await Error(context, 405, "method_not_allowed"); return; }
            if (MonitorHost.IsCrossSiteRequest(context) || !MonitorHost.HasMonitorCsrfHeader(context)) { await Error(context, 403, "csrf_rejected"); return; }
            if (!LocalArchiveWire.HasNoSemanticQuery(context.Request.QueryString.Value)) { await Error(context, 400, "invalid_request"); return; }
            if (context.Request.ContentLength > 32_768) { await Error(context, 413, "request_too_large"); return; }
            var body = await ReadBody(context);
            if (body is null) return;
            if (!LocalArchiveWire.HasSupportedPostMedia(context.Request.Headers.ContentType, context.Request.Headers.ContentEncoding)) { await Error(context, 415, "unsupported_media_type"); return; }
            var status = LocalMonitorV1SessionSearchRequestParser.Parse(body.Value, out var request);
            if (status == LocalMonitorV1SessionSearchParseStatus.RequestTooLarge) { await Error(context, 413, "request_too_large"); return; }
            if (status != LocalMonitorV1SessionSearchParseStatus.Success) { await Error(context, 400, "invalid_request"); return; }
            if (request!.Cursor is not null && !LocalMonitorV1SessionCursorCodec.TryDecode(request.Cursor, cursorKey, request, out _)) { await Error(context, 400, "invalid_cursor"); return; }
            var scope = request.Scope switch { "repository" => LocalRepositoryScopeKind.Repository, "unassigned" => LocalRepositoryScopeKind.Unassigned, _ => LocalRepositoryScopeKind.All };
            try
            {
                var snapshot = await service.ReadAsync(new(scope, request.RepositoryId), context.RequestAborted);
                await Success(context, LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request, cursorKey, testOverrides?.SessionCollectionRevision, testOverrides?.SessionItemRevision));
            }
            catch (InvalidOperationException e) when (e.Message == "local_repository_scope_repository_not_found") { await Error(context, 404, "repository_not_found"); }
            catch (LocalRepositoryScopeSnapshotException) { await Error(context, 503, "persistence_busy"); }
            catch (LocalMonitorV1CollectionException e) { await Error(context, e.Error == "workspace_too_large" ? 409 : 400, e.Error); }
        });
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBody(HttpContext context)
    {
        try
        {
            using var stream = new MemoryStream(); var buffer = new byte[8192];
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted); if (count == 0) return stream.ToArray();
                if (stream.Length + count > 32_768) { await Error(context, 413, "request_too_large"); return null; }
                stream.Write(buffer, 0, count);
            }
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == 413)
        {
            await Error(context, 413, "request_too_large"); return null;
        }
    }

    private static async Task Success(HttpContext context, byte[] bytes)
    {
        context.Response.StatusCode = 200; context.Response.ContentType = "application/json; charset=utf-8"; context.Response.Headers.CacheControl = "no-store"; context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    internal static async Task Error(HttpContext context, int status, string code, bool json = true)
    {
        context.Response.StatusCode = status; context.Response.Headers.CacheControl = "no-store";
        if (!json) { context.Response.ContentLength = 0; return; }
        var bytes = Encoding.UTF8.GetBytes($"{{\"error\":\"{code}\"}}"); context.Response.ContentType = "application/json; charset=utf-8"; context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }
}

internal sealed record LocalMonitorV1CollectionTestOverrides(byte[] CursorKey, string? SessionCollectionRevision, string? SessionItemRevision);

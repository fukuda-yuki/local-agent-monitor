using System.Collections.Concurrent;
using System.Net.Mime;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

namespace CopilotAgentObservability.LocalMonitor;

internal static class RuntimeBackupRoutes
{
    private static readonly byte[] EmptyRequest = "{}"u8.ToArray();

    internal static bool IsPath(PathString path) => path.StartsWithSegments("/api/runtime-backup/v1") || path == "/backup-restore";

    internal static void Map(WebApplication app, string databasePath, TimeProvider timeProvider)
    {
        var application = new Application(databasePath, timeProvider);
        app.MapPost("/api/runtime-backup/v1/backups", context => CreateAsync(context, application));
        app.MapGet("/api/runtime-backup/v1/backups/{backupId}", (string backupId, HttpContext context) => ResultAsync(context, application, backupId));
        app.MapGet("/api/runtime-backup/v1/backups/{backupId}/archive", (string backupId, HttpContext context) => DownloadAsync(context, application, backupId));
        app.MapPost("/api/runtime-backup/v1/previews", context => PreviewAsync(context, application));
        app.MapGet("/backup-restore", UiAsync);
    }

    internal static Task ErrorAsync(HttpContext context, int status, string code)
    {
        Prepare(context.Response, MediaTypeNames.Application.Json);
        context.Response.StatusCode = status;
        return context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes($"{{\"error\":\"{code}\"}}"), context.RequestAborted).AsTask();
    }

    private static async Task CreateAsync(HttpContext context, Application application)
    {
        if (!await AuthorizePostAsync(context, MediaTypeNames.Application.Json)) return;
        var body = await ReadBoundedAsync(context, 2, "request_invalid");
        if (body is null || !body.AsSpan().SequenceEqual(EmptyRequest))
        {
            if (!context.Response.HasStarted) await ErrorAsync(context, StatusCodes.Status400BadRequest, "request_invalid");
            return;
        }
        var result = application.Create();
        if (result.ErrorCode is not null) { await ErrorAsync(context, Status(result.ErrorCode), result.ErrorCode); return; }
        context.Response.Headers.Location = $"/api/runtime-backup/v1/backups/{result.BackupId}";
        await JsonAsync(context, StatusCodes.Status201Created, result);
    }

    private static async Task ResultAsync(HttpContext context, Application application, string backupId)
    {
        if (!AuthorizeRead(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return; }
        if (!application.TryGet(backupId, out var result)) { await ErrorAsync(context, StatusCodes.Status404NotFound, "backup_not_found"); return; }
        await JsonAsync(context, StatusCodes.Status200OK, result);
    }

    private static async Task DownloadAsync(HttpContext context, Application application, string backupId)
    {
        if (!AuthorizeRead(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return; }
        if (!application.TryOpen(backupId, out var stream)) { await ErrorAsync(context, StatusCodes.Status404NotFound, "backup_not_found"); return; }
        await using (stream)
        {
            Prepare(context.Response, "application/zip");
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentLength = stream.Length;
            context.Response.Headers.ContentDisposition = $"attachment; filename=\"local-runtime-backup-{backupId[..12]}.zip\"";
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    private static async Task PreviewAsync(HttpContext context, Application application)
    {
        if (!await AuthorizePostAsync(context, "application/zip")) return;
        if (context.Request.ContentLength > RuntimeBackupLimits.MaximumArchiveBytes)
        {
            await ErrorAsync(context, StatusCodes.Status413PayloadTooLarge, RuntimeBackupErrorCodes.BundleTooLarge);
            return;
        }
        var result = await application.PreviewAsync(context.Request.Body, context.RequestAborted);
        if (!result.Success) { await ErrorAsync(context, Status(result.ErrorCode!), result.ErrorCode!); return; }
        await JsonAsync(context, StatusCodes.Status200OK, result);
    }

    private static async Task<bool> AuthorizePostAsync(HttpContext context, string contentType)
    {
        Prepare(context.Response, MediaTypeNames.Application.Json);
        if (!AuthorizeRead(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return false; }
        if (!MonitorHost.HasMonitorCsrfHeader(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "csrf_required"); return false; }
        if (!string.Equals(context.Request.ContentType?.Split(';', 2)[0], contentType, StringComparison.OrdinalIgnoreCase))
        { await ErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "unsupported_media_type"); return false; }
        return true;
    }

    private static bool AuthorizeRead(HttpContext context) => !MonitorHost.IsCrossSiteRequest(context);

    private static async Task<byte[]?> ReadBoundedAsync(HttpContext context, int maximum, string code)
    {
        if (context.Request.ContentLength > maximum) { await ErrorAsync(context, StatusCodes.Status400BadRequest, code); return null; }
        await using var output = new MemoryStream();
        var buffer = new byte[16];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
        {
            if (output.Length + read > maximum) { await ErrorAsync(context, StatusCodes.Status400BadRequest, code); return null; }
            await output.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
        }
        return output.ToArray();
    }

    private static int Status(string code) => code switch
    {
        RuntimeBackupErrorCodes.InvalidArguments or RuntimeBackupErrorCodes.ArchiveInvalid
            or RuntimeBackupErrorCodes.ArchiveAttributesInvalid or RuntimeBackupErrorCodes.ArchiveTimestampInvalid
            or RuntimeBackupErrorCodes.CompressionNotAllowed or RuntimeBackupErrorCodes.DuplicateEntry
            or RuntimeBackupErrorCodes.UnexpectedEntry or RuntimeBackupErrorCodes.ManifestInvalid
            or RuntimeBackupErrorCodes.ManifestNotCanonical or RuntimeBackupErrorCodes.ChecksumMismatch => StatusCodes.Status400BadRequest,
        RuntimeBackupErrorCodes.BundleTooLarge or RuntimeBackupErrorCodes.DatabaseTooLarge => StatusCodes.Status413PayloadTooLarge,
        RuntimeBackupErrorCodes.SnapshotStoreBusy or RuntimeBackupErrorCodes.SnapshotStoreUnavailable or RuntimeBackupErrorCodes.PublishFailed => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    private static async Task JsonAsync<T>(HttpContext context, int status, T value)
    {
        Prepare(context.Response, MediaTypeNames.Application.Json);
        context.Response.StatusCode = status;
        await context.Response.Body.WriteAsync(RuntimeBackupJson.SerializeResult(value), context.RequestAborted);
    }

    private static void Prepare(HttpResponse response, string contentType)
    {
        response.Headers.CacheControl = "no-store";
        response.ContentType = contentType;
    }

    private static async Task UiAsync(HttpContext context)
    {
        if (!AuthorizeRead(context)) { await ErrorAsync(context, StatusCodes.Status403Forbidden, "cross_origin_forbidden"); return; }
        Prepare(context.Response, MediaTypeNames.Text.Html);
        await context.Response.WriteAsync(Ui, context.RequestAborted);
    }

    private sealed class Application
    {
        private readonly string databasePath;
        private readonly string outputDirectory;
        private readonly SqliteRuntimeBackupService service;
        private readonly ConcurrentDictionary<string, BackupApiResult> results = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, string> files = new(StringComparer.Ordinal);

        internal Application(string databasePath, TimeProvider timeProvider)
        {
            this.databasePath = Path.GetFullPath(databasePath);
            outputDirectory = Path.Combine(Path.GetDirectoryName(this.databasePath)!, "runtime-backups");
            service = new(timeProvider);
        }

        internal BackupApiResult Create()
        {
            var published = Path.Combine(outputDirectory, $"runtime-backup-{Guid.NewGuid():N}.zip");
            var retained = false;
            try
            {
                if (File.Exists(outputDirectory))
                    return new(null, RuntimeBackupErrorCodes.SnapshotStoreUnavailable, null, null, null);
                var created = service.CreateAndPublish(databasePath, published);
                if (!created.Success)
                    return new(null, created.ErrorCode == RuntimeBackupErrorCodes.InvalidArguments
                        ? RuntimeBackupErrorCodes.SnapshotStoreUnavailable
                        : created.ErrorCode, null, null, null);
                var id = created.ArchiveSha256!;
                var result = new BackupApiResult(id, null, id, RuntimeBackupWarnings.All, $"/api/runtime-backup/v1/backups/{id}/archive");
                var retainedPath = files.GetOrAdd(id, published);
                retained = PathEquals(retainedPath, published);
                if (!retained)
                {
                    var existing = service.Inspect(retainedPath);
                    if (!existing.Success || existing.ArchiveSha256 != id) return new(null, RuntimeBackupErrorCodes.PublishFailed, null, null, null);
                    File.Delete(published);
                }
                results[id] = result;
                return result;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            { return new(null, RuntimeBackupErrorCodes.PublishFailed, null, null, null); }
            finally { try { if (!retained && File.Exists(published)) File.Delete(published); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
        }

        internal bool TryGet(string id, out BackupApiResult result)
        {
            if (IsDigest(id) && results.TryGetValue(id, out var found))
            {
                result = found;
                return true;
            }
            result = null!;
            return false;
        }

        internal bool TryOpen(string id, out FileStream stream)
        {
            stream = null!;
            if (!TryGet(id, out _)) return false;
            try
            {
                if (!files.TryGetValue(id, out var path)) return false;
                stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var inspection = service.Inspect(stream);
                if (!inspection.Success || inspection.ArchiveSha256 != id)
                {
                    stream.Dispose();
                    stream = null!;
                    return false;
                }
                stream.Position = 0;
                return true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                stream?.Dispose();
                stream = null!;
                return false;
            }
        }

        internal async Task<RuntimeRestorePreview> PreviewAsync(Stream body, CancellationToken cancellationToken)
            => await service.PreviewAsync(body, databasePath, cancellationToken);

        private static bool IsDigest(string value) => value.Length == 64 && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
        private static bool PathEquals(string left, string right) => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private sealed record BackupApiResult(string? BackupId, string? ErrorCode, string? ArchiveSha256, IReadOnlyList<string>? Warnings, string? DownloadPath);

    private const string Ui = """
        <!doctype html><html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>runtime backup と restore</title></head>
        <body><main><p><a href="/">← 概要へ戻る</a></p><h1>runtime backup と restore</h1><p><strong>raw content を含む backup です。</strong> repository-safe ではなく、Retention cleanup は operator-owned backup を削除しません。</p>
        <ul><li><code>raw_content_included</code></li><li><code>not_repository_safe</code></li><li><code>retention_backup_not_purged</code></li></ul>
        <button id="create" type="button">online backup を作成</button><a id="download" hidden>backup をダウンロード</a>
        <h2>offline restore 前の検査</h2><label for="bundle">検査する backup archive</label><input id="bundle" type="file" accept="application/zip" required aria-describedby="bundle-help"><span id="bundle-help">ZIP archive を1つ選択してください。</span><button id="preview" type="button">archive を検査</button>
        <h2 id="result-heading">操作結果</h2><pre id="result" role="status" aria-live="polite" aria-labelledby="result-heading" tabindex="-1"></pre><p>この Web UI から restore は実行できません。preview を確認し、Local Monitor を停止してから <code>config-cli runtime-backup restore</code> を使用してください。</p></main>
        <script>
        const out=document.querySelector('#result');
        const download=document.querySelector('#download');
        const bundle=document.querySelector('#bundle');
        let previewSequence=0;
        const show=value=>{out.textContent=typeof value==='string'?value:JSON.stringify(value,null,2);};
        bundle.onchange=()=>{previewSequence++;show('');};
        document.querySelector('#create').onclick=async()=>{
          download.hidden=true;download.removeAttribute('href');
          try{
            const r=await fetch('/api/runtime-backup/v1/backups',{method:'POST',headers:{'content-type':'application/json','x-monitor-csrf':'local-monitor'},body:'{}'});
            const j=await r.json();show(j);
            if(r.ok){download.href=j.download_path;download.hidden=false;}
          }catch{show({error:'request_failed'});}
        };
        document.querySelector('#preview').onclick=async()=>{
          const sequence=++previewSequence;
          const f=bundle.files[0];
          if(!f){show({error:'archive_required'});bundle.focus();return;}
          show('');
          try{
            const r=await fetch('/api/runtime-backup/v1/previews',{method:'POST',headers:{'content-type':'application/zip','x-monitor-csrf':'local-monitor'},body:f});
            const value=await r.json();if(sequence===previewSequence)show(value);
          }catch{if(sequence===previewSequence)show({error:'request_failed'});}
        };
        </script></body></html>
        """;
}

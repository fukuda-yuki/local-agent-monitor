using System.Text;

namespace CopilotAgentObservability.LocalMonitor;

internal static class LocalMonitorV1ComparisonRoutes
{
    private const int MaximumRequestBytes = 16_384;
    private const int MaximumResponseBytes = 8_388_608;

    internal static bool IsPath(PathString path) => path.Value?.StartsWith("/api/local-monitor/v1/repositories/", StringComparison.Ordinal) == true
        && path.Value.Contains("/comparisons", StringComparison.Ordinal);

    internal static void Map(WebApplication app, ILocalMonitorV1ComparisonApplication application)
    {
        app.Map("/api/local-monitor/v1/repositories/{repositoryId}/comparisons/preview", context => DispatchPost(context, application, LocalMonitorV1ComparisonOperation.Preview));
        app.Map("/api/local-monitor/v1/repositories/{repositoryId}/comparisons", context => DispatchPost(context, application, LocalMonitorV1ComparisonOperation.Create));
        app.Map("/api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}", context => DispatchRead(context, application, LocalMonitorV1ComparisonOperation.Read));
        app.Map("/api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}/rows", context => DispatchRead(context, application, LocalMonitorV1ComparisonOperation.Rows));
        app.Map("/api/local-monitor/v1/repositories/{repositoryId}/comparisons/{comparisonId}/evidence", context => DispatchRead(context, application, LocalMonitorV1ComparisonOperation.Evidence));
    }

    private static async Task DispatchPost(HttpContext context, ILocalMonitorV1ComparisonApplication application, LocalMonitorV1ComparisonOperation operation)
    {
        if (!HttpMethods.IsPost(context.Request.Method)) { context.Response.Headers.Allow = "POST"; await Error(context, 405, "method_not_allowed"); return; }
        if (!TryRepository(context, out var repositoryId) || context.Request.QueryString.HasValue) { await Error(context, 400, "invalid_request"); return; }
        if (!string.Equals(context.Request.ContentType, "application/json; charset=utf-8", StringComparison.Ordinal)
            || context.Request.Headers.ContentEncoding.Count != 0 || context.Request.ContentLength > MaximumRequestBytes) { await Error(context, 400, "invalid_request"); return; }
        var body = await ReadBody(context);
        if (body is null) { await Error(context, 400, "invalid_request"); return; }
        try
        {
            if (operation == LocalMonitorV1ComparisonOperation.Preview) LocalMonitorV1ComparisonParser.ParsePreview(body);
            else LocalMonitorV1ComparisonParser.ParseCreate(body);
        }
        catch (LocalMonitorV1ComparisonRequestException) { await Error(context, 400, "invalid_request"); return; }
        if (MonitorHost.IsCrossSiteRequest(context) || !MonitorHost.HasMonitorCsrfHeader(context)) { await Error(context, 403, "csrf_rejected"); return; }
        await Publish(context, await application.ExecuteAsync(operation, repositoryId!, null, body, "", context.RequestAborted));
    }

    private static async Task DispatchRead(HttpContext context, ILocalMonitorV1ComparisonApplication application, LocalMonitorV1ComparisonOperation operation)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { context.Response.Headers.Allow = "GET, HEAD"; await Error(context, 405, "method_not_allowed"); return; }
        if (!TryRepository(context, out var repositoryId) || !TryId(context, "comparisonId", out var comparisonId)) { await Error(context, 400, "invalid_request"); return; }
        if (MonitorHost.IsCrossSiteRequest(context)) { await Error(context, 403, "csrf_rejected"); return; }
        try
        {
            if (operation == LocalMonitorV1ComparisonOperation.Read)
            {
                if (context.Request.QueryString.HasValue) throw new LocalMonitorV1ComparisonQueryException();
            }
            else if (operation == LocalMonitorV1ComparisonOperation.Rows)
                _ = LocalMonitorV1ComparisonQueryParser.ParseRows(context.Request.QueryString.Value ?? "");
            else
                _ = LocalMonitorV1ComparisonQueryParser.ParseEvidence(context.Request.QueryString.Value ?? "");
        }
        catch (LocalMonitorV1ComparisonQueryException) { await Error(context, 400, "invalid_request"); return; }
        await Publish(context, await application.ExecuteAsync(operation, repositoryId!, comparisonId, ReadOnlyMemory<byte>.Empty, context.Request.QueryString.Value ?? "", context.RequestAborted));
    }

    private static bool TryRepository(HttpContext context, out string? value) => TryId(context, "repositoryId", out value);
    private static bool TryId(HttpContext context, string key, out string? value)
    {
        value = context.Request.RouteValues[key]?.ToString();
        return value is { Length: 36 } && value[14] == '7' && value[19] is '8' or '9' or 'a' or 'b'
            && value.Where((_, index) => index is not (8 or 13 or 18 or 23)).All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            && value[8] == '-' && value[13] == '-' && value[18] == '-' && value[23] == '-';
    }

    private static async Task<byte[]?> ReadBody(HttpContext context)
    {
        using var stream = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
            if (count == 0) return stream.ToArray();
            if (stream.Length + count > MaximumRequestBytes) return null;
            stream.Write(buffer, 0, count);
        }
    }

    private static async Task Publish(HttpContext context, LocalMonitorV1ComparisonResponse response)
    {
        if (response.Entity.Length > MaximumResponseBytes) { await Error(context, 409, "workspace_too_large"); return; }
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = response.Entity.Length;
        if (response.Location is not null) context.Response.Headers.Location = response.Location;
        if (!HttpMethods.IsHead(context.Request.Method)) await context.Response.Body.WriteAsync(response.Entity, context.RequestAborted);
    }

    internal static async Task Error(HttpContext context, int status, string code)
    {
        var bytes = Encoding.UTF8.GetBytes($"{{\"error\":\"{code}\"}}");
        await Publish(context, new(status, bytes));
    }
}

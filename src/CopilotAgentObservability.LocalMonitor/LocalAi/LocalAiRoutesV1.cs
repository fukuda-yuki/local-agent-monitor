using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal static class LocalAiRoutesV1
{
    private const int MaximumSessionBody = 16_384;
    private const int MaximumNodeBody = 262_144;
    internal static bool IsPath(PathString path) => path.Value?.StartsWith("/api/local-monitor/v1/ai", StringComparison.Ordinal) == true;

    internal static void Map(IEndpointRouteBuilder endpoints, ILocalAiAnalysisApplicationV1 application)
    {
        endpoints.Map("/api/local-monitor/v1/ai/session-runs", context => StartSession(context, application));
        endpoints.Map("/api/local-monitor/v1/ai/node-runs", context => StartNode(context, application));
        endpoints.Map("/api/local-monitor/v1/ai/session-runs/{runId}", context => ReadRun(context, application));
        endpoints.Map("/api/local-monitor/v1/ai/node-runs/{runId}", context => ReadRun(context, application));
        endpoints.Map("/api/local-monitor/v1/ai/runs/{runId}", context => ReadRun(context, application));
        endpoints.Map("/api/local-monitor/v1/ai/runs/{runId}/cancel", context => Cancel(context, application));
        endpoints.Map("/api/local-monitor/v1/ai/sessions/{sessionId}/reports", context => Reports(context, application));
    }

    private static async Task StartSession(HttpContext context, ILocalAiAnalysisApplicationV1 application)
    {
        if (!await PreparePost(context, MaximumSessionBody).ConfigureAwait(false)) return;
        var body = await Body(context, MaximumSessionBody).ConfigureAwait(false);
        if (!TrySessionRequest(body, out var request)) { await Error(context, 400, "invalid_request"); return; }
        var result = await application.StartSessionAsync(request!, context.RequestAborted).ConfigureAwait(false);
        await StartResponse(context, result).ConfigureAwait(false);
    }

    private static async Task StartNode(HttpContext context, ILocalAiAnalysisApplicationV1 application)
    {
        if (!await PreparePost(context, MaximumNodeBody).ConfigureAwait(false)) return;
        var body = await Body(context, MaximumNodeBody).ConfigureAwait(false);
        if (!TryNodeRequest(body, out var request)) { await Error(context, 400, "invalid_request"); return; }
        LocalAiStartResponseV1 result;
        try { result = await application.StartNodeAsync(request!, context.RequestAborted).ConfigureAwait(false); }
        catch (ArgumentException) { await Error(context, 400, "invalid_request"); return; }
        await StartResponse(context, result).ConfigureAwait(false);
    }

    private static async Task ReadRun(HttpContext context, ILocalAiAnalysisApplicationV1 application)
    {
        if (!ReadMethod(context)) return;
        if (MonitorHost.IsCrossSiteRequest(context)) { await Error(context, 403, "csrf_rejected"); return; }
        if (context.Request.QueryString.HasValue || !RouteUuid(context, "runId", out var runId)) { await Error(context, 400, "invalid_request"); return; }
        var status = await application.ReadRunAsync(runId!, context.RequestAborted).ConfigureAwait(false);
        if (status is null) { await Error(context, 404, "run_not_found"); return; }
        await Json(context, 200, new { run_id=status.RunId, state=status.State, scope_kind=status.ScopeKind,
            session_id=status.SessionId, node_id=status.NodeId, error=status.ErrorCode }).ConfigureAwait(false);
    }

    private static async Task Cancel(HttpContext context, ILocalAiAnalysisApplicationV1 application)
    {
        if (!await PreparePost(context, 2).ConfigureAwait(false)) return;
        var body = await Body(context, 2).ConfigureAwait(false);
        if (body is null || !body.AsSpan().SequenceEqual("{}"u8) || !RouteUuid(context, "runId", out var runId))
        { await Error(context, 400, "invalid_request"); return; }
        if (!await application.CancelAsync(runId!, context.RequestAborted).ConfigureAwait(false))
        { await Error(context, 409, "run_not_cancelable"); return; }
        await Json(context, 200, new { run_id=runId, state="canceled" }).ConfigureAwait(false);
    }

    private static async Task Reports(HttpContext context, ILocalAiAnalysisApplicationV1 application)
    {
        if (!ReadMethod(context)) return;
        if (MonitorHost.IsCrossSiteRequest(context)) { await Error(context, 403, "csrf_rejected"); return; }
        if (!RouteUuid(context, "sessionId", out var sessionId) || !TryReportsQuery(context, out var limit, out var cursor))
        { await Error(context, 400, "invalid_request"); return; }
        var page = await application.ReadReportsAsync(sessionId!, limit, cursor, context.RequestAborted).ConfigureAwait(false);
        await Json(context, 200, new { reports=page.Reports.Select(item => new { run_id=item.RunId,state=item.State,
            result=JsonDocument.Parse(item.ResultJson).RootElement.Clone(),snapshot_changed=item.SnapshotChanged }), next_cursor=page.NextCursor }).ConfigureAwait(false);
    }

    private static async Task<bool> PreparePost(HttpContext context, int maximum)
    {
        if (!HttpMethods.IsPost(context.Request.Method))
        { context.Response.Headers.Allow="POST"; await Error(context, 405, "method_not_allowed"); return false; }
        if (context.Request.QueryString.HasValue || !string.Equals(context.Request.ContentType, "application/json; charset=utf-8", StringComparison.Ordinal)
            || context.Request.Headers.ContentEncoding.Count != 0 || context.Request.ContentLength > maximum)
        { await Error(context, 400, "invalid_request"); return false; }
        if (MonitorHost.IsCrossSiteRequest(context) || !MonitorHost.HasMonitorCsrfHeader(context))
        { await Error(context, 403, "csrf_rejected"); return false; }
        return true;
    }

    private static bool ReadMethod(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) return true;
        context.Response.Headers.Allow="GET, HEAD"; Error(context, 405, "method_not_allowed").GetAwaiter().GetResult(); return false;
    }

    private static async Task<byte[]?> Body(HttpContext context, int maximum)
    {
        using var stream=new MemoryStream(); var buffer=new byte[4096];
        while (true) { var read=await context.Request.Body.ReadAsync(buffer,context.RequestAborted); if(read==0)return stream.ToArray();
            if(stream.Length+read>maximum)return null; stream.Write(buffer,0,read); }
    }

    private static bool TrySessionRequest(byte[]? bytes, out LocalAiSessionStartRequestV1? request)
    {
        request=null; if(!TryObject(bytes,["session_id","timeout_seconds"], ["session_id"],out var root))return false;
        if(!TryUuid(root.GetProperty("session_id"),out var session))return false;
        var timeout=root.TryGetProperty("timeout_seconds",out var value)&&value.TryGetInt32(out var parsed)?parsed:60;
        if(timeout is <1 or >600)return false; request=new(session!,timeout); return true;
    }

    private static bool TryNodeRequest(byte[]? bytes, out LocalAiNodeStartRequestV1? request)
    {
        request=null; if(!TryObject(bytes,["session_id","node_id","timeout_seconds","question","prior_turns"],["session_id","node_id"],out var root))return false;
        if(!TryUuid(root.GetProperty("session_id"),out var session)||root.GetProperty("node_id").ValueKind!=JsonValueKind.String)return false;
        var node=root.GetProperty("node_id").GetString(); if(string.IsNullOrWhiteSpace(node))return false;
        var timeout=root.TryGetProperty("timeout_seconds",out var timeoutValue)&&timeoutValue.TryGetInt32(out var parsed)?parsed:60;
        string? question=null; if(root.TryGetProperty("question",out var q)){if(q.ValueKind!=JsonValueKind.String)return false;question=q.GetString();}
        var turns=new List<LocalAiPriorTurnV1>(); if(root.TryGetProperty("prior_turns",out var prior))
        { if(prior.ValueKind!=JsonValueKind.Array)return false; foreach(var item in prior.EnumerateArray())
          { if(!Closed(item,["question","answer"],["question","answer"]))return false; turns.Add(new(item.GetProperty("question").GetString()!,item.GetProperty("answer").GetString()!)); } }
        request=new(session!,node!,timeout,question,turns); return timeout is >=1 and <=600;
    }

    private static bool TryObject(byte[]? bytes, string[] allowed, string[] required, out JsonElement root)
    {
        root=default; try { if(bytes is null)return false; using var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=16}); root=document.RootElement.Clone(); return Closed(root,allowed,required); }
        catch(JsonException){return false;}
    }
    private static bool Closed(JsonElement root,string[] allowed,string[] required)=>root.ValueKind==JsonValueKind.Object
        && root.EnumerateObject().All(property=>allowed.Contains(property.Name,StringComparer.Ordinal))
        && required.All(name => root.TryGetProperty(name, out _));
    private static bool TryUuid(JsonElement value,out string? id){id=value.ValueKind==JsonValueKind.String?value.GetString():null;return CanonicalUuid(id);}
    private static bool RouteUuid(HttpContext context,string key,out string? id){id=context.Request.RouteValues[key]?.ToString();return CanonicalUuid(id);}
    private static bool CanonicalUuid(string? id)=>id is {Length:36}&&Guid.TryParseExact(id,"D",out var parsed)&&parsed.Version==7&&id==id.ToLowerInvariant();
    private static bool TryReportsQuery(HttpContext context,out int? limit,out string? cursor)
    { limit=null;cursor=null;foreach(var pair in context.Request.Query){if(pair.Value.Count!=1)return false;if(pair.Key=="limit"){if(!int.TryParse(pair.Value[0],out var value)||value is <1 or >100)return false;limit=value;}else if(pair.Key=="cursor")cursor=pair.Value[0];else return false;}return true; }

    private static Task StartResponse(HttpContext context,LocalAiStartResponseV1 result)=>result.ErrorCode is null
        ? Json(context,201,new{run_id=result.RunId}) : Error(context,result.ErrorCode=="provider_unavailable"?503:409,result.ErrorCode);
    internal static Task Error(HttpContext context,int status,string code)=>Json(context,status,new{error=code});
    private static async Task Json(HttpContext context,int status,object entity)
    { var bytes=JsonSerializer.SerializeToUtf8Bytes(entity);context.Response.StatusCode=status;context.Response.ContentType="application/json; charset=utf-8";context.Response.Headers.CacheControl="no-store";context.Response.ContentLength=bytes.Length;if(!HttpMethods.IsHead(context.Request.Method))await context.Response.Body.WriteAsync(bytes,context.RequestAborted); }
}

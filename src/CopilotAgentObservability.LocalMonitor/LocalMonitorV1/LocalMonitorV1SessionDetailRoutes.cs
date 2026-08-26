using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal static class LocalMonitorV1SessionDetailRoutes
{
    private const string Prefix="/api/local-monitor/v1/sessions/";
    internal static bool IsPath(PathString path)=>path.Value?.StartsWith(Prefix,StringComparison.Ordinal)==true&&(path.Value.EndsWith("/summary",StringComparison.Ordinal)||path.Value.EndsWith("/timeline",StringComparison.Ordinal)||path.Value.Contains("/nodes/",StringComparison.Ordinal));

    internal static void Map(WebApplication app,ILocalRepositorySessionDetailSnapshotService service,byte[]? cursorKeyOverride=null)
    {
        var key=cursorKeyOverride?.ToArray()??RandomNumberGenerator.GetBytes(32);
        app.Map("/api/local-monitor/v1/sessions/{sessionId}/summary",context=>Handle(context,service,key,Kind.Summary));
        app.Map("/api/local-monitor/v1/sessions/{sessionId}/timeline",context=>Handle(context,service,key,Kind.Timeline));
        app.Map("/api/local-monitor/v1/sessions/{sessionId}/nodes/{nodeId}",context=>Handle(context,service,key,Kind.Node));
    }

    private static async Task Handle(HttpContext context,ILocalRepositorySessionDetailSnapshotService service,byte[] key,Kind kind)
    {
        if(!HttpMethods.IsGet(context.Request.Method)&&!HttpMethods.IsHead(context.Request.Method)){context.Response.Headers.Allow="GET, HEAD";await Error(context,405,"method_not_allowed");return;}
        var sessionId=context.Request.RouteValues["sessionId"] as string;
        var nodeId=context.Request.RouteValues["nodeId"] as string;
        if(!CanonicalUuid(sessionId)||(kind==Kind.Node&&!CanonicalNode(nodeId))){await Error(context,400,"invalid_request");return;}
        if(!TryParse(context.Request.QueryString.Value??"",kind,out var query)){await Error(context,400,"invalid_request");return;}
        try
        {
            LocalMonitorV1TimelinePosition decoded=default!; var cursorValid=true;
            if(kind==Kind.Timeline&&query.After is not null)
                cursorValid=LocalMonitorV1TimelineCursor.TryDecode(query.After,key,new(sessionId!,query.Revision!,query.ExecutionId,query.ParentNodeId,query.Limit),out decoded);
            var request=kind switch
            {
                Kind.Summary=>new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Summary,sessionId!),
                Kind.Timeline=>new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Timeline,sessionId!,query.ExecutionId,query.ParentNodeId,
                    cursorValid&&query.After is not null?new(decoded.TimeGroup,decoded.UtcTicks,decoded.SourceOrdinal,decoded.NodeId):null,query.Limit),
                _=>new LocalRepositorySessionDetailRequest(LocalRepositorySessionDetailRequestKind.Node,sessionId!,NodeId:nodeId!)
            };
            var snapshot=await service.ReadDetailAsync(request,context.RequestAborted);
            if(query.Revision is not null&&!StringComparer.Ordinal.Equals(query.Revision,snapshot.WorkspaceRevision)){await Error(context,409,"workspace_snapshot_stale");return;}
            if(!cursorValid){await Error(context,400,"invalid_cursor");return;}
            var bytes=kind switch{Kind.Summary=>LocalMonitorV1SessionDetailApplication.SerializeSummary(snapshot),Kind.Timeline=>LocalMonitorV1SessionDetailApplication.SerializeTimeline(snapshot,query.ExecutionId,query.ParentNodeId,query.Limit,query.After,key),_=>LocalMonitorV1SessionDetailApplication.SerializeNode(snapshot,nodeId!)};
            await Success(context,bytes);
        }
        catch(LocalWorkspaceSessionDetailException e){await Error(context,e.Error switch{"session_not_found"=>404,"local_monitor_ui_unavailable"=>503,_=>409},e.Error);}
        catch(LocalRepositoryScopeSnapshotException){await Error(context,503,"persistence_busy");}
        catch(InvalidOperationException){await Error(context,503,"local_monitor_ui_unavailable");}
        catch(LocalMonitorV1SessionDetailException e){var status=e.Error switch{"execution_not_found" or "node_not_found"=>404,"invalid_cursor"=>400,"local_monitor_ui_unavailable"=>503,_=>409};await Error(context,status,e.Error);}
    }

    private static bool TryParse(string raw,Kind kind,out Query query)
    {
        query=default!;if(kind==Kind.Summary){if(raw is not ("" or "?"))return false;query=new(null,null,null,null,100);return true;}
        if(raw.Length<2||raw[0]!='?'||raw.Contains('%')||raw.Contains('+')||raw.Contains(' ')||raw.EndsWith('&'))return false;
        var values=new Dictionary<string,string>(StringComparer.Ordinal);foreach(var pair in raw[1..].Split('&')){var split=pair.Split('=');if(split.Length!=2||split[0].Length==0||split[1].Length==0||!values.TryAdd(split[0],split[1]))return false;}
        var allowed=kind==Kind.Timeline?new[]{"workspace_revision","execution_id","parent_node_id","after","limit"}:new[]{"workspace_revision"};if(values.Keys.Any(k=>!allowed.Contains(k,StringComparer.Ordinal)))return false;
        if(!values.TryGetValue("workspace_revision",out var revision)||revision.Length!=64||revision.Any(c=>!IsLowerHex(c)))return false;
        values.TryGetValue("execution_id",out var execution);values.TryGetValue("parent_node_id",out var parent);values.TryGetValue("after",out var after);
        if(execution is not null&&!CanonicalUuid(execution)||parent is not null&&(!CanonicalNode(parent)||execution is null)||after is not null&&(after.Length!=159||after.Any(c=>!(char.IsAsciiLetterOrDigit(c)||c is '-' or '_'))))return false;
        var limit=100;if(values.TryGetValue("limit",out var limitText)&&(!int.TryParse(limitText,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out limit)||limit<1||limit>200||limitText!=limit.ToString(System.Globalization.CultureInfo.InvariantCulture)))return false;
        query=new(revision,execution,parent,after,limit);return true;
    }

    private static bool CanonicalUuid(string? value)=>value is not null&&Guid.TryParseExact(value,"D",out _)&&value.Length==36&&value[14]=='7'&&"89ab".Contains(value[19])&&value.All(c=>c=='-'||IsLowerHex(c));
    private static bool CanonicalNode(string? value)=>value is not null&&value.Length==37&&value.StartsWith("node-",StringComparison.Ordinal)&&value[5..].All(IsLowerHex);
    private static bool IsLowerHex(char c)=>c is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static async Task Success(HttpContext context,byte[] bytes){context.Response.StatusCode=200;context.Response.ContentType="application/json; charset=utf-8";context.Response.Headers.CacheControl="no-store";context.Response.ContentLength=bytes.Length;if(!HttpMethods.IsHead(context.Request.Method))await context.Response.Body.WriteAsync(bytes,context.RequestAborted);}
    internal static async Task Error(HttpContext context,int status,string code){var bytes=Encoding.UTF8.GetBytes($"{{\"error\":\"{code}\"}}");context.Response.StatusCode=status;context.Response.ContentType="application/json; charset=utf-8";context.Response.Headers.CacheControl="no-store";context.Response.ContentLength=bytes.Length;if(!HttpMethods.IsHead(context.Request.Method))await context.Response.Body.WriteAsync(bytes,context.RequestAborted);}
    private enum Kind{Summary,Timeline,Node}
    private sealed record Query(string? Revision,string? ExecutionId,string? ParentNodeId,string? After,int Limit);
}

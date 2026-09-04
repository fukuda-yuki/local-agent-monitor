using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.LocalMonitor.Pages;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal static class LocalMonitorV1HumanRoutes
{
    private const string HtmlContentType = "text/html; charset=utf-8";
    private const string SharedAsset = "/local-monitor-v1-shared.js";
    private static readonly HashSet<string> PrimaryExtensionAssets = new(StringComparer.Ordinal)
    {
        "/local-monitor-repositories.js",
        "/local-monitor-settings.js",
        "/local-monitor-explorer.js",
        "/local-monitor-session-workspace.js",
        "/local-monitor-compare.js",
    };
    private static readonly HashSet<string> PrimaryAssets = new(StringComparer.Ordinal)
    {
        SharedAsset,
        "/monitor.css",
        "/monitor.js",
        "/monitor-shell.js",
        "/local-monitor-repositories.js",
        "/local-monitor-settings.js",
        "/local-monitor-explorer.js",
        "/local-monitor-session-workspace.js",
        "/local-monitor-compare.js",
    };

    internal static bool IsPrimaryAsset(PathString path) =>
        path.Value is { } value && PrimaryAssets.Contains(value);

    internal static bool IsRetiredTraceList(HttpContext context)
    {
        var rawPath = RawPath(context);
        return string.Equals(rawPath, "/traces", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawPath, "/traces/", StringComparison.OrdinalIgnoreCase);
    }

    internal static Task RetireTraceListAsync(HttpContext context) =>
        Empty(context, StatusCodes.Status404NotFound);

    internal static bool IsRetiredHistoricalAnalysis(HttpContext context)
    {
        var rawPath = RawPath(context);
        return string.Equals(rawPath, "/historical-analysis", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawPath, "/historical-analysis/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(rawPath, "/monitor-historical-analysis.js", StringComparison.OrdinalIgnoreCase);
    }

    internal static Task RetireHistoricalAnalysisAsync(HttpContext context) =>
        Empty(context, StatusCodes.Status404NotFound);

    internal static Task<bool> TryDispatchUnavailableAssetAsync(HttpContext context, IFileProvider webRootFileProvider)
    {
        if (context.Request.Path == SharedAsset) return Task.FromResult(false);

        var rawPath = RawPath(context);
        if (!PrimaryExtensionAssets.Any(asset => IsRawAssetCandidate(rawPath, asset))) return Task.FromResult(false);

        if (PrimaryExtensionAssets.Contains(rawPath) && IsAvailablePrimaryAsset(webRootFileProvider, rawPath[1..]))
            return Task.FromResult(false);

        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = 0;
        return Task.FromResult(true);
    }

    private static bool IsAvailablePrimaryAsset(IFileProvider fileProvider, string leaf)
    {
        try
        {
            var file = fileProvider.GetFileInfo(leaf);
            if (file is not { Exists: true, IsDirectory: false }) return false;
            return file.PhysicalPath is not { } path
                || (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (Exception exception) when (IsNonFatalFileLookupFailure(exception))
        {
            return false;
        }
    }

    private static bool IsRawAssetCandidate(string rawPath, string asset)
    {
        var rawIndex = 0;
        var assetIndex = 0;
        while (rawIndex < rawPath.Length && assetIndex < asset.Length)
        {
            var value = rawPath[rawIndex++];
            if (value == '%')
            {
                if (rawIndex + 1 >= rawPath.Length
                    || !TryHex(rawPath[rawIndex], out var high)
                    || !TryHex(rawPath[rawIndex + 1], out var low)) return false;
                value = (char)((high << 4) | low);
                rawIndex += 2;
            }
            if (char.ToLowerInvariant(value) != asset[assetIndex++]) return false;
        }

        if (assetIndex != asset.Length) return false;
        if (rawIndex == rawPath.Length) return true;
        if (rawPath[rawIndex] is '/' or '\\') return true;
        return rawPath[rawIndex] == '%'
            && rawIndex + 2 < rawPath.Length
            && TryHex(rawPath[rawIndex + 1], out var separatorHigh)
            && TryHex(rawPath[rawIndex + 2], out var separatorLow)
            && ((separatorHigh << 4) | separatorLow) is '/' or '\\';
    }

    private static bool IsNonFatalFileLookupFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    internal static bool IsCandidate(HttpContext context)
    {
        if (SessionRoutes.IsRawContentPath(context.Request.Path)) return false;
        var rawPath = RawPath(context);
        if (string.Equals(rawPath, "/", StringComparison.Ordinal)) return true;
        if (string.IsNullOrEmpty(rawPath) || rawPath[0] != '/') return false;
        if (rawPath.StartsWith("//", StringComparison.Ordinal)) return true;
        if (rawPath.StartsWith("/sessions", StringComparison.OrdinalIgnoreCase)
            || rawPath.StartsWith("/repositories", StringComparison.OrdinalIgnoreCase)) return true;
        var separator = rawPath.IndexOf('/', 1);
        var firstSegment = separator < 0 ? rawPath.AsSpan(1) : rawPath.AsSpan(1, separator - 1);
        return IsRawLiteralSpelling(firstSegment, "sessions")
            || IsRawLiteralSpelling(firstSegment, "repositories");
    }

    internal static async Task<bool> TryDispatchAsync(
        HttpContext context,
        ILocalRepositoryScopeSnapshotService scopeService,
        ILocalRepositorySessionDetailSnapshotService detailService,
        TimeProvider timeProvider)
    {
        if (!IsCandidate(context)) return false;

        var rawPath = RawPath(context);
        var path = LocalMonitorV1PrimaryPathParser.Classify(rawPath);
        if (path.Classification == LocalMonitorV1PathClassification.NearPath)
        {
            await Empty(context, StatusCodes.Status404NotFound);
            return true;
        }

        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.Headers.Allow = "GET, HEAD";
            await Empty(context, StatusCodes.Status405MethodNotAllowed);
            return true;
        }

        if (path.Classification == LocalMonitorV1PathClassification.MatchedInvalid)
        {
            var recovery = rawPath.StartsWith("/repositories/", StringComparison.Ordinal)
                ? "open_repository_selection"
                : "open_all_sessions";
            await Page(context, LocalMonitorV1PageModel.Error("invalid_request", recovery), StatusCodes.Status400BadRequest);
            return true;
        }

        if (!LocalMonitorV1PageQueryParser.TryParse(path.RouteKind!.Value, RawQuery(context), out var query))
        {
            await Page(context, LocalMonitorV1PageModel.Error("invalid_request", RecoveryForInvalidQuery(path.RouteKind.Value)), StatusCodes.Status400BadRequest);
            return true;
        }

        if (MonitorHost.IsCrossSiteRequest(context))
        {
            await CrossOriginAsync(context);
            return true;
        }

        var options = context.RequestServices.GetRequiredService<MonitorOptions>();
        if (query!.AnalysisId is not null
            && (path.RouteKind == LocalMonitorV1PrimaryRouteKind.RepositorySessions && !options.RepositoryAiEnabled
                || path.RouteKind == LocalMonitorV1PrimaryRouteKind.ComparisonDetail && !options.CompareAiEnabled))
        {
            await Page(context, LocalMonitorV1PageModel.ResolvedError(
                path, query, "analysis_run_not_found", "open_repository_selection"), StatusCodes.Status404NotFound);
            return true;
        }

        var resolution = await ResolveAsync(
            path,
            query!,
            scopeService,
            detailService,
            context.RequestServices.GetRequiredService<ILocalMonitorV1ComparisonApplication>(),
            context.RequestServices.GetRequiredService<ILocalAiAnalysisApplicationV1>(),
            context.RequestServices.GetRequiredService<IRazorViewEngine>(),
            timeProvider,
            context.RequestAborted);
        await Page(context, resolution.Model, resolution.StatusCode);
        return true;
    }

    internal static Task InvalidHostAsync(HttpContext context)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"error\":\"invalid_host\"}");
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask();
    }

    internal static Task CrossOriginAsync(HttpContext context)
    {
        var bytes = Encoding.UTF8.GetBytes("{\"error\":\"cross_origin_forbidden\"}");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        return HttpMethods.IsHead(context.Request.Method)
            ? Task.CompletedTask
            : context.Response.Body.WriteAsync(bytes, context.RequestAborted).AsTask();
    }

    private static async ValueTask<(LocalMonitorV1PageModel Model, int StatusCode)> ResolveAsync(
        LocalMonitorV1PrimaryPathResult path,
        LocalMonitorV1PageQuery query,
        ILocalRepositoryScopeSnapshotService scopeService,
        ILocalRepositorySessionDetailSnapshotService detailService,
        ILocalMonitorV1ComparisonApplication comparisonApplication,
        ILocalAiAnalysisApplicationV1 localAiApplication,
        IRazorViewEngine viewEngine,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        LocalRepositoryScopeSnapshot? scopeSnapshot = null;
        try
        {
            switch (path.RouteKind)
            {
                case LocalMonitorV1PrimaryRouteKind.RepositorySelection:
                case LocalMonitorV1PrimaryRouteKind.AllSessions:
                    scopeSnapshot = await scopeService.ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellationToken);
                    break;
                case LocalMonitorV1PrimaryRouteKind.UnassignedSessions:
                    scopeSnapshot = await scopeService.ReadAsync(new(LocalRepositoryScopeKind.Unassigned, null), cancellationToken);
                    break;
                case LocalMonitorV1PrimaryRouteKind.RepositorySessions:
                    scopeSnapshot = await scopeService.ReadAsync(new(LocalRepositoryScopeKind.Repository, path.RepositoryId), cancellationToken);
                    if (query.AnalysisId is not null)
                    {
                        var run = await localAiApplication.ReadRunAsync(query.AnalysisId, cancellationToken);
                        if (run is null
                            || run.ScopeKind != "repository_selection"
                            || run.RepositoryId != path.RepositoryId
                            || run.ExpiresAt is null
                            || run.ExpiresAt <= timeProvider.GetUtcNow())
                        {
                            return (LocalMonitorV1PageModel.ResolvedError(
                                path, query, "analysis_run_not_found", "open_repository_selection"), 404);
                        }
                    }
                    break;
                case LocalMonitorV1PrimaryRouteKind.SessionDetail:
                    var snapshot = await detailService.ReadDetailAsync(
                        new(LocalRepositorySessionDetailRequestKind.Summary, path.SessionId!), cancellationToken);
                    if (query.AnalysisId is not null)
                    {
                        var run = await localAiApplication.ReadRunAsync(query.AnalysisId, cancellationToken);
                        if (run is null
                            || !string.Equals(run.SessionId, path.SessionId, StringComparison.Ordinal)
                            || run.ScopeKind is not ("session" or "node"))
                            return (LocalMonitorV1PageModel.ResolvedError(path, query, "analysis_run_not_found", "open_session_overview"), 404);
                        if (string.Equals(run.ScopeKind, "node", StringComparison.Ordinal))
                        {
                            if (run.NodeId is null)
                                return (LocalMonitorV1PageModel.ResolvedError(path, query, "analysis_run_not_found", "open_session_overview"), 404);
                            LocalRepositorySessionDetailSnapshot anchorSnapshot;
                            try
                            {
                                anchorSnapshot = await detailService.ReadDetailAsync(
                                    new(LocalRepositorySessionDetailRequestKind.Node, path.SessionId!, NodeId: run.NodeId,
                                        ExpectedWorkspaceRevision: snapshot.WorkspaceRevision), cancellationToken);
                            }
                            catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "node_not_found")
                            {
                                return (LocalMonitorV1PageModel.ResolvedError(path, query, "analysis_run_not_found", "open_session_overview"), 404);
                            }
                            var anchor = anchorSnapshot.Detail.Nodes.SingleOrDefault(item => item.NodeId == run.NodeId);
                            if (anchor is null
                                || query.NodeId is not null && query.NodeId != anchor.NodeId
                                || query.ExecutionId is not null && query.ExecutionId != anchor.ExecutionId)
                                return (LocalMonitorV1PageModel.ResolvedError(path, query, "analysis_run_not_found", "open_session_overview"), 404);
                            break;
                        }
                    }
                    if (query.ExecutionId is not null
                        && !snapshot.Detail.Executions.Any(item => item.ExecutionId == query.ExecutionId))
                    {
                        return (LocalMonitorV1PageModel.ResolvedError(path, query, "execution_not_found", "open_session_overview"), 404);
                    }
                    if (query.NodeId is not null)
                    {
                        var nodeSnapshot = await detailService.ReadDetailAsync(
                            new(LocalRepositorySessionDetailRequestKind.Node, path.SessionId!, NodeId: query.NodeId,
                                ExpectedWorkspaceRevision: snapshot.WorkspaceRevision), cancellationToken);
                        var node = nodeSnapshot.Detail.Nodes.SingleOrDefault(item => item.NodeId == query.NodeId);
                        if (node is null || query.ExecutionId is not null && node.ExecutionId != query.ExecutionId)
                            return (LocalMonitorV1PageModel.ResolvedError(path, query, "node_not_found", "open_session_overview"), 404);
                    }
                    break;
                case LocalMonitorV1PrimaryRouteKind.ComparisonDetail:
                    scopeSnapshot = await scopeService.ReadAsync(
                        new(LocalRepositoryScopeKind.Repository, path.RepositoryId), cancellationToken);
                    var comparison = await comparisonApplication.ExecuteAsync(
                        LocalMonitorV1ComparisonOperation.Read,
                        path.RepositoryId!,
                        path.ComparisonId,
                        ReadOnlyMemory<byte>.Empty,
                        string.Empty,
                        cancellationToken);
                    if (comparison.StatusCode == 404)
                        return (LocalMonitorV1PageModel.ResolvedError(path, query, "comparison_not_found", "open_repository_selection"), 404);
                    if (comparison.StatusCode == 410)
                        return (LocalMonitorV1PageModel.ResolvedError(path, query, "comparison_expired", "open_repository_selection"), 410);
                    if (comparison.StatusCode == 503)
                        return (LocalMonitorV1PageModel.ResolvedError(path, query, "persistence_busy", "retry"), 503);
                    if (comparison.StatusCode != 200)
                        return (LocalMonitorV1PageModel.ResolvedError(path, query, "local_monitor_ui_unavailable", "retry"), 503);
                    if (query.AnalysisId is not null)
                    {
                        var run = await localAiApplication.ReadRunAsync(query.AnalysisId, cancellationToken);
                        if (run is null
                            || run.ScopeKind != "comparison"
                            || run.RepositoryId != path.RepositoryId
                            || run.ComparisonId != path.ComparisonId
                            || run.ExpiresAt is null
                            || run.ExpiresAt <= timeProvider.GetUtcNow())
                        {
                            return (LocalMonitorV1PageModel.ResolvedError(
                                path, query, "analysis_run_not_found", "open_repository_selection"), 404);
                        }
                    }
                    break;
            }
        }
        catch (InvalidOperationException exception) when (exception.Message == "local_repository_scope_repository_not_found")
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "repository_not_found", "open_repository_selection"), 404);
        }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "session_not_found")
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "session_not_found", "open_all_sessions"), 404);
        }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "node_not_found")
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "node_not_found", "open_session_overview"), 404);
        }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "workspace_snapshot_stale")
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "workspace_snapshot_stale", "refresh_session_summary"), 409);
        }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "workspace_too_large")
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "workspace_too_large", "open_all_sessions"), 409);
        }
        catch (LocalWorkspaceSessionDetailException exception) when (exception.Error == "local_monitor_ui_unavailable")
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "local_monitor_ui_unavailable", "retry"), 503);
        }
        catch (LocalWorkspaceSessionDetailException)
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "local_monitor_ui_unavailable", "retry"), 503);
        }
        catch (LocalRepositoryScopeSnapshotException)
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "persistence_busy", "retry"), 503);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "persistence_busy", "retry"), 503);
        }
        catch (InvalidOperationException)
        {
            return (LocalMonitorV1PageModel.ResolvedError(path, query, "local_monitor_ui_unavailable", "retry"), 503);
        }

        var explorer = ResolveExplorerPresentation(path, scopeSnapshot);
        return HasExactRenderer(viewEngine, path.RouteKind!.Value)
            ? (LocalMonitorV1PageModel.Success(path, query, explorer.Scope, explorer.Heading), StatusCodes.Status200OK)
            : (LocalMonitorV1PageModel.ResolvedError(path, query, "local_monitor_ui_unavailable", "retry"),
                StatusCodes.Status503ServiceUnavailable);
    }

    private static (string? Scope, string? Heading) ResolveExplorerPresentation(
        LocalMonitorV1PrimaryPathResult path,
        LocalRepositoryScopeSnapshot? snapshot) => path.RouteKind switch
    {
        LocalMonitorV1PrimaryRouteKind.RepositorySessions =>
            ("repository", snapshot!.Repositories.Single(item => item.RepositoryId == path.RepositoryId).DisplayName),
        LocalMonitorV1PrimaryRouteKind.AllSessions => ("all", "すべてのセッション"),
        LocalMonitorV1PrimaryRouteKind.UnassignedSessions => ("unassigned", "リポジトリ未設定のセッション"),
        LocalMonitorV1PrimaryRouteKind.ComparisonDetail =>
            ("comparison", snapshot!.Repositories.Single(item => item.RepositoryId == path.RepositoryId).DisplayName),
        _ => (null, null),
    };

    private static bool HasExactRenderer(IRazorViewEngine viewEngine, LocalMonitorV1PrimaryRouteKind routeKind)
    {
        var viewPath = routeKind switch
        {
            LocalMonitorV1PrimaryRouteKind.RepositorySelection =>
                "/Pages/Shared/LocalMonitorV1/_RepositorySelection.cshtml",
            LocalMonitorV1PrimaryRouteKind.RepositorySessions or
            LocalMonitorV1PrimaryRouteKind.AllSessions or
            LocalMonitorV1PrimaryRouteKind.UnassignedSessions =>
                "/Pages/Shared/LocalMonitorV1/_SessionExplorer.cshtml",
            LocalMonitorV1PrimaryRouteKind.SessionDetail =>
                "/Pages/Shared/LocalMonitorV1/_SessionWorkspace.cshtml",
            LocalMonitorV1PrimaryRouteKind.ComparisonDetail =>
                "/Pages/Shared/LocalMonitorV1/_RepositoryCompare.cshtml",
            _ => throw new ArgumentOutOfRangeException(nameof(routeKind)),
        };

        try
        {
            var result = viewEngine.GetView(null, viewPath, false);
            return result.Success
                && result.View is not null
                && string.Equals(result.View.Path, viewPath, StringComparison.Ordinal);
        }
        catch (Exception exception) when (IsNonFatalRendererLookupFailure(exception))
        {
            return false;
        }
    }

    private static bool IsNonFatalRendererLookupFailure(Exception exception) =>
        exception is not OperationCanceledException
            and not OutOfMemoryException
            and not StackOverflowException
            and not AccessViolationException;

    private static string RecoveryForInvalidQuery(LocalMonitorV1PrimaryRouteKind kind) => kind switch
    {
        LocalMonitorV1PrimaryRouteKind.RepositorySelection or
        LocalMonitorV1PrimaryRouteKind.RepositorySessions or
        LocalMonitorV1PrimaryRouteKind.ComparisonDetail => "open_repository_selection",
        LocalMonitorV1PrimaryRouteKind.SessionDetail => "open_all_sessions",
        _ => "open_all_sessions",
    };

    private static string RawPath(HttpContext context)
    {
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? string.Empty;
        var query = target.IndexOf('?');
        return query < 0 ? target : target[..query];
    }

    private static string RawQuery(HttpContext context)
    {
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
        var query = target.IndexOf('?');
        return query < 0 ? string.Empty : target[query..];
    }

    private static bool IsRawLiteralSpelling(ReadOnlySpan<char> raw, ReadOnlySpan<char> literal)
    {
        var rawIndex = 0;
        var literalIndex = 0;
        while (rawIndex < raw.Length && literalIndex < literal.Length)
        {
            var value = raw[rawIndex++];
            if (value == '%')
            {
                if (rawIndex + 1 >= raw.Length
                    || !TryHex(raw[rawIndex], out var high)
                    || !TryHex(raw[rawIndex + 1], out var low)) return false;
                value = (char)((high << 4) | low);
                rawIndex += 2;
            }
            if (char.ToLowerInvariant(value) != literal[literalIndex++]) return false;
        }
        return rawIndex == raw.Length && literalIndex == literal.Length;
    }

    private static bool TryHex(char value, out int parsed)
    {
        parsed = value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1,
        };
        return parsed >= 0;
    }

    private static async Task Page(HttpContext context, LocalMonitorV1PageModel model, int statusCode)
    {
        var services = context.RequestServices;
        var viewEngine = services.GetRequiredService<IRazorViewEngine>();
        var result = viewEngine.GetView(null, "/Pages/LocalMonitorV1.cshtml", true);
        if (!result.Success) throw new InvalidOperationException("local_monitor_v1_view_unavailable");

        using var writer = new StringWriter();
        var actionContext = new ActionContext(context, new RouteData(), new ActionDescriptor());
        var viewData = new ViewDataDictionary<LocalMonitorV1PageModel>(
            services.GetRequiredService<IModelMetadataProvider>(), new ModelStateDictionary()) { Model = model };
        var tempData = new TempDataDictionary(context, services.GetRequiredService<ITempDataProvider>());
        var viewContext = new ViewContext(actionContext, result.View!, viewData, tempData, writer, new HtmlHelperOptions());
        await result.View.RenderAsync(viewContext);
        var bytes = Encoding.UTF8.GetBytes(writer.ToString());
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = HtmlContentType;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static Task Empty(HttpContext context, int statusCode)
    {
        context.Response.StatusCode = statusCode;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentLength = 0;
        return Task.CompletedTask;
    }
}

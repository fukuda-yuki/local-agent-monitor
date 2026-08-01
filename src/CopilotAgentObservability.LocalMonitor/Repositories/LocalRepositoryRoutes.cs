using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace CopilotAgentObservability.LocalMonitor.Repositories;

internal static class LocalRepositoryRoutes
{
    internal static object FallbackMarker { get; } = new LocalRepositoryFallbackMarker();

    private static readonly TemplateMatcher[] OwnedTemplates =
    [
        Matcher(LocalRepositoryContracts.CollectionRoute),
        Matcher(LocalRepositoryContracts.ItemRoute),
        Matcher(LocalRepositoryContracts.LocatorRoute),
        Matcher(LocalRepositoryContracts.SessionActionRoute),
        Matcher(LocalRepositoryContracts.AssignmentRoute),
    ];

    internal static void Map(IEndpointRouteBuilder endpoints, LocalRepositoryCatalogApplication application)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(application);
        endpoints.MapPost(LocalRepositoryContracts.CollectionRoute, context => CreateAsync(context, application));
        endpoints.MapPatch(LocalRepositoryContracts.ItemRoute, context => UpdateAsync(context, application));
        endpoints.MapGet(LocalRepositoryContracts.LocatorRoute, context => ReadLocatorsAsync(context, application));
        endpoints.MapPost(LocalRepositoryContracts.SessionActionRoute, context => SessionActionAsync(context, application));
        endpoints.MapGet(LocalRepositoryContracts.AssignmentRoute, context => ReadAssignmentAsync(context, application));
    }

    internal static async Task AdaptMethodNotAllowedAsync(
        HttpContext context,
        RequestDelegate next,
        IEnumerable<EndpointDataSource> endpointDataSources)
    {
        if (IsOwnedPath(context.Request.Path)
            && IsFallback(context.GetEndpoint()))
        {
            var methods = MatchingMethods(context.Request.Path, endpointDataSources);
            if (methods.Count == 0)
                throw new InvalidOperationException("local_repository_route_metadata_missing");
            context.Response.Headers.Allow = new StringValues(methods.ToArray());
            await WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, LocalRepositoryError.MethodNotAllowed);
            return;
        }
        await next(context);
        if (context.Response.StatusCode != StatusCodes.Status405MethodNotAllowed
            || context.Response.HasStarted
            || context.Response.ContentType is not null
            || context.Response.ContentLength is > 0
            || !IsOwnedPath(context.Request.Path))
        {
            return;
        }
        if (!context.Response.Headers.ContainsKey(HeaderNames.Allow))
        {
            var methods = MatchingMethods(context.Request.Path, endpointDataSources);
            if (methods.Count == 0)
                throw new InvalidOperationException("local_repository_route_metadata_missing");
            context.Response.Headers.Allow = new StringValues(methods.ToArray());
        }
        await WriteErrorAsync(context, StatusCodes.Status405MethodNotAllowed, LocalRepositoryError.MethodNotAllowed);
    }

    internal static bool IsNamespacePath(PathString path) =>
        path.StartsWithSegments("/api/local-monitor/v1", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOwnedPath(PathString path)
    {
        var text = path.Value ?? string.Empty;
        foreach (var matcher in OwnedTemplates)
        {
            var values = new RouteValueDictionary();
            if (matcher.TryMatch(text, values)) return true;
        }
        return false;
    }

    internal static async Task WriteErrorAsync(HttpContext context, int statusCode, LocalRepositoryError error)
    {
        context.Response.StatusCode = statusCode;
        SetHeaders(context.Response);
        var bytes = LocalRepositoryJson.ErrorBytes(error);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static async Task CreateAsync(HttpContext context, LocalRepositoryCatalogApplication application)
    {
        if (!await AdmitMutationAsync(context, LocalRepositoryContracts.RepositoryBodyLimit)) return;
        var bytes = await ReadBodyAsync(context, LocalRepositoryContracts.RepositoryBodyLimit);
        if (bytes is null) return;
        if (!LocalRepositoryJson.TryParseCreate(bytes.Value, out var request))
        {
            await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest);
            return;
        }
        var prepared = application.PrepareCreate(new(request!.DisplayName, request.GitHubLocator));
        if (prepared is LocalRepositoryPreparationRejected<LocalRepositoryCatalogApplication.PreparedCreate> rejected)
        {
            await WritePreparationErrorAsync(context, rejected.Failure);
            return;
        }
        if (!TryOperationKey(context.Request.Headers, out var key))
        {
            await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest);
            return;
        }
        var result = await application.ExecutePreparedAsync(
            ((LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>)prepared).Prepared,
            key!, value => LocalRepositoryJson.WriteRepository(201, value), context.RequestAborted);
        await WriteMutationResultAsync(context, result);
    }

    private static async Task UpdateAsync(HttpContext context, LocalRepositoryCatalogApplication application)
    {
        if (!await AdmitMutationAsync(context, LocalRepositoryContracts.RepositoryBodyLimit)) return;
        var bytes = await ReadBodyAsync(context, LocalRepositoryContracts.RepositoryBodyLimit);
        if (bytes is null) return;
        if (!LocalRepositoryJson.TryParseUpdate(bytes.Value, out var request))
        {
            await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest);
            return;
        }
        var repositoryId = Convert.ToString(context.Request.RouteValues["repositoryId"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (request!.Operation == "rename")
        {
            var prepared = application.PrepareRename(new(repositoryId, request.ExpectedRevision, request.DisplayName!));
            if (prepared is LocalRepositoryPreparationRejected<LocalRepositoryCatalogApplication.PreparedRename> rejected)
            {
                await WritePreparationErrorAsync(context, rejected.Failure); return;
            }
            if (!TryOperationKey(context.Request.Headers, out var key)) { await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest); return; }
            await WriteMutationResultAsync(context, await application.ExecutePreparedAsync(
                ((LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedRename>)prepared).Prepared,
                key!, value => LocalRepositoryJson.WriteRepository(200, value), context.RequestAborted));
            return;
        }
        var locatorPrepared = application.PrepareSetGitHubLocator(new(repositoryId, request.ExpectedRevision, request.GitHubLocator!));
        if (locatorPrepared is LocalRepositoryPreparationRejected<LocalRepositoryCatalogApplication.PreparedSetLocator> locatorRejected)
        {
            await WritePreparationErrorAsync(context, locatorRejected.Failure); return;
        }
        if (!TryOperationKey(context.Request.Headers, out var locatorKey)) { await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest); return; }
        await WriteMutationResultAsync(context, await application.ExecutePreparedAsync(
            ((LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSetLocator>)locatorPrepared).Prepared,
            locatorKey!, value => LocalRepositoryJson.WriteRepository(200, value), context.RequestAborted));
    }

    private static async Task SessionActionAsync(HttpContext context, LocalRepositoryCatalogApplication application)
    {
        if (!await AdmitMutationAsync(context, LocalRepositoryContracts.SessionActionBodyLimit)) return;
        var bytes = await ReadBodyAsync(context, LocalRepositoryContracts.SessionActionBodyLimit);
        if (bytes is null) return;
        if (!LocalRepositoryJson.TryParseSessionAction(bytes.Value, out var request))
        {
            await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest); return;
        }
        var prepared = application.PrepareSessionAction(new(
            request!.SessionId, request.ExpectedRevision, request.Action, request.RepositoryId));
        if (prepared is LocalRepositoryPreparationRejected<LocalRepositoryCatalogApplication.PreparedSessionAction> rejected)
        {
            await WritePreparationErrorAsync(context, rejected.Failure); return;
        }
        if (!TryOperationKey(context.Request.Headers, out var key)) { await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest); return; }
        await WriteMutationResultAsync(context, await application.ExecutePreparedAsync(
            ((LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>)prepared).Prepared,
            key!, LocalRepositoryJson.WriteAssignment, context.RequestAborted));
    }

    private static async Task ReadLocatorsAsync(HttpContext context, LocalRepositoryCatalogApplication application)
    {
        if (!await AdmitReadAsync(context)) return;
        var id = Convert.ToString(context.Request.RouteValues["repositoryId"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var result = await application.ReadLocatorsAsync(id, context.RequestAborted);
        switch (result)
        {
            case LocalRepositoryLocatorsFound found: await WriteBytesAsync(context, 200, LocalRepositoryJson.WriteLocators(found.Value)); break;
            case LocalRepositoryLocatorRepositoryNotFound: await WriteErrorAsync(context, 404, LocalRepositoryError.RepositoryNotFound); break;
            case LocalRepositoryLocatorReadBusy: await WriteErrorAsync(context, 503, LocalRepositoryError.PersistenceBusy); break;
            case LocalRepositoryLocatorReadCorrupt: throw new InvalidOperationException("local_repository_locator_read_corrupt");
            default: throw new InvalidOperationException("local_repository_locator_read_invalid");
        }
    }

    private static async Task ReadAssignmentAsync(HttpContext context, LocalRepositoryCatalogApplication application)
    {
        if (!await AdmitReadAsync(context)) return;
        var id = Convert.ToString(context.Request.RouteValues["sessionId"], System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        var result = await application.ReadAssignmentAsync(id, context.RequestAborted);
        switch (result)
        {
            case LocalRepositoryAssignmentFound found: await WriteBytesAsync(context, 200, LocalRepositoryJson.WriteAssignment(found.Value)); break;
            case LocalRepositoryAssignmentSessionNotFound: await WriteErrorAsync(context, 404, LocalRepositoryError.SessionNotFound); break;
            case LocalRepositoryAssignmentReadBusy: await WriteErrorAsync(context, 503, LocalRepositoryError.PersistenceBusy); break;
            case LocalRepositoryAssignmentReadCorrupt: throw new InvalidOperationException("local_repository_assignment_read_corrupt");
            default: throw new InvalidOperationException("local_repository_assignment_read_invalid");
        }
    }

    private static async Task<bool> AdmitReadAsync(HttpContext context)
    {
        SetHeaders(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context)) { await WriteErrorAsync(context, 403, LocalRepositoryError.CsrfRejected); return false; }
        if (context.Request.Query.Count != 0) { await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest); return false; }
        return true;
    }

    private static async Task<bool> AdmitMutationAsync(HttpContext context, int bodyLimit)
    {
        SetHeaders(context.Response);
        if (MonitorHost.IsCrossSiteRequest(context) || !MonitorHost.HasMonitorCsrfHeader(context))
        { await WriteErrorAsync(context, 403, LocalRepositoryError.CsrfRejected); return false; }
        if (context.Request.Query.Count != 0) { await WriteErrorAsync(context, 400, LocalRepositoryError.InvalidRequest); return false; }
        if (!HasJsonMediaType(context.Request.Headers)) { await WriteErrorAsync(context, 415, LocalRepositoryError.UnsupportedMediaType); return false; }
        if (context.Request.ContentLength is > 0 && context.Request.ContentLength > bodyLimit)
        { await WriteErrorAsync(context, 413, LocalRepositoryError.RequestTooLarge); return false; }
        return true;
    }

    private static bool HasJsonMediaType(IHeaderDictionary headers)
    {
        if (!headers.TryGetValue(HeaderNames.ContentType, out StringValues values) || values.Count != 1
            || !MediaTypeHeaderValue.TryParse(values[0], out var parsed)
            || parsed.Parameters.Any(static parameter => !parameter.Value.HasValue)) return false;
        return string.Equals(parsed.MediaType.Value, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBodyAsync(HttpContext context, int limit)
    {
        try
        {
            using var stream = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await context.Request.Body.ReadAsync(buffer, context.RequestAborted);
                if (count == 0) break;
                if (stream.Length + count > limit) { await WriteErrorAsync(context, 413, LocalRepositoryError.RequestTooLarge); return null; }
                stream.Write(buffer, 0, count);
            }
            return stream.ToArray();
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            await WriteErrorAsync(context, 413, LocalRepositoryError.RequestTooLarge);
            return null;
        }
    }

    private static bool TryOperationKey(IHeaderDictionary headers, out string? key)
    {
        key = null;
        if (!headers.TryGetValue("Idempotency-Key", out var values) || values.Count != 1) return false;
        key = values[0];
        return key is not null && LocalRepositoryCatalogValidation.IsOperationKey(key);
    }

    private static async Task WritePreparationErrorAsync(HttpContext context, LocalRepositoryPreparationFailure failure)
    {
        var (status, error) = failure switch
        {
            LocalRepositoryPreparationFailure.InvalidRequest => (400, LocalRepositoryError.InvalidRequest),
            LocalRepositoryPreparationFailure.InvalidLocator => (400, LocalRepositoryError.InvalidLocator),
            LocalRepositoryPreparationFailure.InvalidRepositoryTarget => (404, LocalRepositoryError.RepositoryNotFound),
            LocalRepositoryPreparationFailure.InvalidSessionTarget => (404, LocalRepositoryError.SessionNotFound),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        await WriteErrorAsync(context, status, error);
    }

    private static async Task WriteMutationResultAsync(HttpContext context, LocalRepositoryMutationResult result)
    {
        switch (result)
        {
            case LocalRepositoryMutationSucceeded succeeded:
                await WriteExactResponseAsync(context, succeeded.Response);
                return;
            case LocalRepositoryMutationBusy:
                await WriteErrorAsync(context, 503, LocalRepositoryError.PersistenceBusy); return;
            case LocalRepositoryMutationRejected rejected:
                var (status, error) = rejected.Failure switch
                {
                    LocalRepositoryMutationFailure.InvalidRequest => (400, LocalRepositoryError.InvalidRequest),
                    LocalRepositoryMutationFailure.InvalidLocator => (400, LocalRepositoryError.InvalidLocator),
                    LocalRepositoryMutationFailure.RepositoryNotFound => (404, LocalRepositoryError.RepositoryNotFound),
                    LocalRepositoryMutationFailure.SessionNotFound => (404, LocalRepositoryError.SessionNotFound),
                    LocalRepositoryMutationFailure.RevisionConflict => (409, LocalRepositoryError.RevisionConflict),
                    LocalRepositoryMutationFailure.LocatorConflict => (409, LocalRepositoryError.LocatorConflict),
                    LocalRepositoryMutationFailure.LocatorLimitReached => (409, LocalRepositoryError.LocatorLimitReached),
                    LocalRepositoryMutationFailure.IdempotencyConflict => (409, LocalRepositoryError.IdempotencyConflict),
                    _ => throw new ArgumentOutOfRangeException(nameof(rejected.Failure)),
                };
                await WriteErrorAsync(context, status, error);
                return;
            default: throw new InvalidOperationException("local_repository_mutation_result_invalid");
        }
    }

    private static async Task WriteBytesAsync(HttpContext context, int statusCode, ReadOnlyMemory<byte> bytes)
    {
        context.Response.StatusCode = statusCode;
        SetHeaders(context.Response);
        await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
    }

    private static async Task WriteExactResponseAsync(HttpContext context, LocalRepositoryExactResponse response)
    {
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.ContentType;
        context.Response.Headers.CacheControl = response.CacheControl;
        context.Response.Headers.Remove(HeaderNames.Location);
        context.Response.Headers.Remove(HeaderNames.ETag);
        await context.Response.Body.WriteAsync(response.CopyEntity(), context.RequestAborted);
    }

    private static void SetHeaders(HttpResponse response)
    {
        response.ContentType = LocalRepositoryExactResponse.SuccessContentType;
        response.Headers.CacheControl = LocalRepositoryExactResponse.SuccessCacheControl;
        response.Headers.Remove(HeaderNames.Location);
        response.Headers.Remove(HeaderNames.ETag);
    }

    private static TemplateMatcher Matcher(string template) =>
        new(TemplateParser.Parse(template), new RouteValueDictionary());

    private static IReadOnlyList<string> MatchingMethods(PathString path, IEnumerable<EndpointDataSource> dataSources)
    {
        var text = path.Value ?? string.Empty;
        return dataSources.SelectMany(static source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => !IsFallback(endpoint)
                && endpoint.RoutePattern.RawText is { } template
                && IsSharedTemplate(template)
                && Match(template, text))
            .SelectMany(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool Match(string template, string path) =>
        Matcher(template).TryMatch(path, new RouteValueDictionary());

    private static bool IsSharedTemplate(string template) => template is
        LocalRepositoryContracts.CollectionRoute
        or LocalRepositoryContracts.ItemRoute
        or LocalRepositoryContracts.LocatorRoute
        or LocalRepositoryContracts.SessionActionRoute
        or LocalRepositoryContracts.AssignmentRoute;

    private static bool IsFallback(Endpoint? endpoint) => endpoint?.Metadata.Any(metadata =>
        ReferenceEquals(metadata, FallbackMarker)) == true;

    private sealed class LocalRepositoryFallbackMarker;
}

using System.Text;
using CopilotAgentObservability.LocalMonitor.Archive;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveRouteTests
{
    private const string First = "01890f65-4c31-7f42-8a7d-111111111111";
    private const string Second = "01890f65-4c31-7f42-8a7d-222222222222";
    private const string Now = "2026-08-09T12:34:56.1234567+00:00";

    [Theory]
    [InlineData("/api/local-monitor/v1/Archive")]
    [InlineData("/api/local-monitor/v1/archive/")]
    [InlineData("/api/local-monitor/v1//archive")]
    [InlineData("/api/local-monitor/v1/archive/extra")]
    public async Task NearPathsFallThroughWithoutArchiveHeaders(string path)
    {
        using var database = new LocalArchiveMutationDatabase();
        var context = Context("GET", path);
        var nextCalls = 0;

        await LocalArchiveRoutes.AdaptAsync(
            context,
            _ => { nextCalls++; return Task.CompletedTask; },
            database.CreateStore());

        Assert.Equal(1, nextCalls);
        Assert.Null(context.Response.ContentType);
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.CacheControl));
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/%61rchive?target_kind=session&target_id=01890f65-4c31-7f42-8a7d-111111111111")]
    [InlineData("/api/local-monitor/v1/archive%2F?target_kind=session&target_id=01890f65-4c31-7f42-8a7d-111111111111")]
    [InlineData("/api/local-monitor/v1/archive%?target_kind=session&target_id=01890f65-4c31-7f42-8a7d-111111111111")]
    public async Task RawPercentEncodedAndMalformedPathsNeverAliasOwnedRoutes(string rawTarget)
    {
        using var database = new LocalArchiveMutationDatabase();
        var context = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={First}");
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget;
        var nextCalls = 0;

        await LocalArchiveRoutes.AdaptAsync(
            context,
            _ => { nextCalls++; return Task.CompletedTask; },
            database.CreateStore());

        Assert.Equal(1, nextCalls);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/archive", "POST", "GET")]
    [InlineData("/api/local-monitor/v1/archive", "OPTIONS", "GET")]
    [InlineData("/api/local-monitor/v1/archive-actions", "GET", "POST")]
    [InlineData("/api/local-monitor/v1/archived-items", "DELETE", "GET")]
    public async Task WrongMethodsWinBeforeOriginAndRequestAdmission(
        string path,
        string method,
        string allow)
    {
        using var database = new LocalArchiveMutationDatabase();
        var context = Context(method, path + "?bad=%25");
        context.Request.Headers.Origin = "https://evil.example";

        await InvokeAsync(context, database.CreateStore());

        await AssertResponseAsync(context, 405, "{\"error\":\"method_not_allowed\"}");
        Assert.Equal(allow, context.Response.Headers.Allow);
    }

    [Fact]
    public async Task WrongMethodSetsAllowBeforeRealServerStartsTheResponse()
    {
        using var database = new LocalArchiveMutationDatabase();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Use((context, next) => LocalArchiveRoutes.AdaptAsync(context, next, database.CreateStore()));
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/local-monitor/v1/archive");

        using var response = await client.SendAsync(request);

        Assert.Equal(405, (int)response.StatusCode);
        Assert.Equal(["GET"], response.Content.Headers.Allow);
        Assert.Equal("{\"error\":\"method_not_allowed\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/api/local-monitor/v1/archive", "GET")]
    [InlineData("/api/local-monitor/v1/archive-actions", "POST")]
    [InlineData("/api/local-monitor/v1/archived-items", "GET")]
    public async Task HeadHasRepresentationLengthAndNoEntity(string path, string allow)
    {
        using var database = new LocalArchiveMutationDatabase();
        var context = Context("HEAD", path);
        context.Request.Headers.Origin = "https://evil.example";

        await InvokeAsync(context, database.CreateStore());

        Assert.Equal(405, context.Response.StatusCode);
        Assert.Equal(allow, context.Response.Headers.Allow);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.Equal(30, context.Response.ContentLength);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task DirectGetEnforcesOriginThenStrictQueryAndWritesExactActiveBytes()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);

        var crossSite = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={First}");
        crossSite.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        await InvokeAsync(crossSite, database.CreateStore());
        await AssertResponseAsync(crossSite, 403, "{\"error\":\"csrf_rejected\"}");

        var invalid = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={First}&extra=1");
        await InvokeAsync(invalid, database.CreateStore());
        await AssertResponseAsync(invalid, 400, "{\"error\":\"invalid_request\"}");

        var success = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={First}");
        await InvokeAsync(success, database.CreateStore());
        await AssertResponseAsync(success, 200,
            $"{{\"schema_version\":\"local-archive.response.v1\",\"target_kind\":\"session\",\"target_id\":\"{First}\",\"state\":\"active\",\"revision\":0,\"archived_at\":null,\"updated_at\":null}}");
    }

    [Fact]
    public async Task PostEnforcesCsrfQueryMediaAndBothBodyLimitsBeforeStrictBody()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);

        var missingCsrf = Post("/api/local-monitor/v1/archive-actions", "application/json", "{}");
        await InvokeAsync(missingCsrf, database.CreateStore());
        await AssertResponseAsync(missingCsrf, 403, "{\"error\":\"csrf_rejected\"}");

        var queryBeforeMedia = Post("/api/local-monitor/v1/archive-actions?bad=1", "text/plain", "{}");
        queryBeforeMedia.Request.Headers["x-monitor-csrf"] = "local-monitor";
        await InvokeAsync(queryBeforeMedia, database.CreateStore());
        await AssertResponseAsync(queryBeforeMedia, 400, "{\"error\":\"invalid_request\"}");

        var media = Post("/api/local-monitor/v1/archive-actions", "application/json; charset=\"utf-8\"", "{}");
        media.Request.Headers["x-monitor-csrf"] = "local-monitor";
        await InvokeAsync(media, database.CreateStore());
        await AssertResponseAsync(media, 415, "{\"error\":\"unsupported_media_type\"}");

        var declared = Post("/api/local-monitor/v1/archive-actions", "application/json", "{}");
        declared.Request.Headers["x-monitor-csrf"] = "local-monitor";
        declared.Request.ContentLength = 65_537;
        await InvokeAsync(declared, database.CreateStore());
        await AssertResponseAsync(declared, 413, "{\"error\":\"request_too_large\"}");

        var streamed = Post("/api/local-monitor/v1/archive-actions", "application/json", new string(' ', 65_537));
        streamed.Request.Headers["x-monitor-csrf"] = "local-monitor";
        streamed.Request.ContentLength = null;
        await InvokeAsync(streamed, database.CreateStore());
        await AssertResponseAsync(streamed, 413, "{\"error\":\"request_too_large\"}");

        var invalidBody = Post("/api/local-monitor/v1/archive-actions", "application/json", "{}");
        invalidBody.Request.Headers["x-monitor-csrf"] = "local-monitor";
        await InvokeAsync(invalidBody, database.CreateStore());
        await AssertResponseAsync(invalidBody, 400, "{\"error\":\"invalid_request\"}");
    }

    [Fact]
    public async Task ExactBodyLimitIsAcceptedAndOneByteMoreIsRejectedForDeclaredAndStreamedBodies()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        var compact = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{First}\",\"expected_revision\":0}}]}}";
        var acceptedBody = compact + new string(' ', 65_536 - Encoding.UTF8.GetByteCount(compact));
        var accepted = Post("/api/local-monitor/v1/archive-actions?", "application/json", acceptedBody);
        accepted.Request.Headers["x-monitor-csrf"] = "local-monitor";
        await InvokeAsync(accepted, database.CreateStore());
        Assert.Equal(200, accepted.Response.StatusCode);

        var declared = Post("/api/local-monitor/v1/archive-actions", "application/json", acceptedBody);
        declared.Request.Headers["x-monitor-csrf"] = "local-monitor";
        declared.Request.ContentLength = 65_537;
        await InvokeAsync(declared, database.CreateStore());
        await AssertResponseAsync(declared, 413, "{\"error\":\"request_too_large\"}");

        var streamed = Post("/api/local-monitor/v1/archive-actions", "application/json", acceptedBody + " ");
        streamed.Request.Headers["x-monitor-csrf"] = "local-monitor";
        streamed.Request.ContentLength = null;
        await InvokeAsync(streamed, database.CreateStore());
        await AssertResponseAsync(streamed, 413, "{\"error\":\"request_too_large\"}");
    }

    [Fact]
    public async Task PostCommitsAndEmitsThePrecommitExactEntity()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        var body = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{First}\",\"expected_revision\":0}}]}}";
        var context = Post("/api/local-monitor/v1/archive-actions", "APPLICATION/JSON; CHARSET=utf-8", body);
        context.Request.Headers["x-monitor-csrf"] = "local-monitor";

        await InvokeAsync(context, database.CreateStore());

        await AssertResponseAsync(context, 200,
            $"{{\"schema_version\":\"local-archive-action.response.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{First}\",\"state\":\"archived\",\"revision\":1,\"archived_at\":\"{Now}\",\"updated_at\":\"{Now}\"}}]}}");
        Assert.Equal(("archived", 1L, Now), database.Current(First));
    }

    [Fact]
    public async Task ArchivedListUsesLastEmittedItemCursorAndMapsStoreFailures()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        database.InsertSession(Second);
        database.InsertHistory(LocalArchiveTargetKind.Session, First, 1, Now);
        database.InsertHistory(LocalArchiveTargetKind.Session, Second, 1, Now);
        var expectedCursor = LocalArchiveCursorCodec.Encode(
            LocalArchiveTargetKind.Session,
            new(Now, Second));

        var list = Context("GET", "/api/local-monitor/v1/archived-items?target_kind=session&limit=1");
        await InvokeAsync(list, database.CreateStore());
        await AssertResponseAsync(list, 200,
            $"{{\"schema_version\":\"local-archived-items.response.v1\",\"target_kind\":\"session\",\"items\":[{{\"target_id\":\"{Second}\",\"state\":\"archived\",\"revision\":1,\"archived_at\":\"{Now}\",\"updated_at\":\"{Now}\"}}],\"next_cursor\":\"{expectedCursor}\"}}");

        database.Execute("DROP TABLE local_archive_events;");
        var unavailable = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={First}");
        await InvokeAsync(unavailable, database.CreateStore());
        await AssertResponseAsync(unavailable, 503, "{\"error\":\"archive_store_unavailable\"}");
    }

    [Fact]
    public async Task StoreAbsenceAndRevisionConflictMapToTheirClosedErrors()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);

        var absent = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={Second}");
        await InvokeAsync(absent, database.CreateStore());
        await AssertResponseAsync(absent, 404, "{\"error\":\"target_not_found\"}");

        database.InsertHistory(LocalArchiveTargetKind.Session, First, 1, Now);
        var body = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"restore\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{First}\",\"expected_revision\":0}}]}}";
        var conflict = Post("/api/local-monitor/v1/archive-actions", "application/json", body);
        conflict.Request.Headers["x-monitor-csrf"] = "local-monitor";
        await InvokeAsync(conflict, database.CreateStore());
        await AssertResponseAsync(conflict, 409, "{\"error\":\"revision_conflict\"}");
    }

    [Fact]
    public async Task PersistenceBusyMapsToClosedErrorAndCancellationEscapesWithoutEntity()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        using (var blocker = database.Open())
        {
            using var command = blocker.CreateCommand();
            command.CommandText = "BEGIN EXCLUSIVE;";
            command.ExecuteNonQuery();
            var body = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{First}\",\"expected_revision\":0}}]}}";
            var busy = Post("/api/local-monitor/v1/archive-actions", "application/json", body);
            busy.Request.Headers["x-monitor-csrf"] = "local-monitor";

            await InvokeAsync(busy, database.CreateStore());

            await AssertResponseAsync(busy, 503, "{\"error\":\"persistence_busy\"}");
            using var rollback = blocker.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceled = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={First}");
        canceled.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(canceled, database.CreateStore()));

        Assert.Equal(0, canceled.Response.Body.Length);
    }

    [Fact]
    public async Task PostCancellationInsideMutateRollsBackAndEmitsNoSuccessEntity()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        using var cancellation = new CancellationTokenSource();
        var store = new SqliteLocalArchiveStore(
            database.Path,
            SqliteLocalRepositoryTargetExistenceAuthority.Instance,
            LocalArchiveSessionTargetExistenceAuthority.Instance,
            new FixedTimeProvider(DateTimeOffset.Parse(Now)),
            instant =>
            {
                cancellation.Cancel();
                return Guid.CreateVersion7(instant).ToString("D");
            });
        var body = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{First}\",\"expected_revision\":0}}]}}";
        var context = Post("/api/local-monitor/v1/archive-actions", "application/json", body);
        context.Request.Headers["x-monitor-csrf"] = "local-monitor";
        context.RequestAborted = cancellation.Token;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(context, store));

        Assert.Equal(0, database.EventCount());
        Assert.Null(database.Current(First));
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task PostResponseEmissionUsesRequestAbortedAfterDurableCommit()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        using var cancellation = new CancellationTokenSource();
        var body = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{First}\",\"expected_revision\":0}}]}}";
        var context = Post("/api/local-monitor/v1/archive-actions", "application/json", body);
        context.Request.Headers["x-monitor-csrf"] = "local-monitor";
        context.RequestAborted = cancellation.Token;
        var response = new CancelingResponseStream(cancellation);
        context.Response.Body = response;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeAsync(context, database.CreateStore()));

        Assert.Equal(cancellation.Token, response.ObservedToken);
        Assert.Equal(("archived", 1L, Now), database.Current(First));
        Assert.Equal(0, response.Length);
    }

    [Fact]
    public async Task OwnedNon405ResponsesRemoveEveryForbiddenAndAllowHeader()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        var context = Context("GET", $"/api/local-monitor/v1/archive?target_kind=session&target_id={First}");
        context.Response.Headers.Location = "/forbidden";
        context.Response.Headers.ETag = "\"forbidden\"";
        context.Response.Headers.SetCookie = "forbidden=1";
        context.Response.Headers.Allow = "DELETE";
        context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        context.Response.Headers["Access-Control-Allow-Headers"] = "*";

        await InvokeAsync(context, database.CreateStore());

        Assert.Equal(200, context.Response.StatusCode);
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.Location));
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ETag));
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.SetCookie));
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.Allow));
        Assert.DoesNotContain(context.Response.Headers.Keys,
            key => key.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase));
    }

    private static DefaultHttpContext Context(string method, string pathAndQuery)
    {
        var separator = pathAndQuery.IndexOf('?');
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("127.0.0.1:43199");
        context.Request.Path = separator < 0 ? pathAndQuery : pathAndQuery[..separator];
        context.Request.QueryString = separator < 0 ? QueryString.Empty : new QueryString(pathAndQuery[separator..]);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = pathAndQuery;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static DefaultHttpContext Post(string path, string contentType, string body)
    {
        var context = Context("POST", path);
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Headers.ContentType = contentType;
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        return context;
    }

    private static Task InvokeAsync(DefaultHttpContext context, SqliteLocalArchiveStore store) =>
        LocalArchiveRoutes.AdaptAsync(
            context,
            _ => throw new InvalidOperationException("owned path must not fall through"),
            store);

    private static async Task AssertResponseAsync(
        DefaultHttpContext context,
        int status,
        string entity)
    {
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", context.Response.ContentType);
        Assert.Equal("no-store", context.Response.Headers.CacheControl);
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.Location));
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.ETag));
        Assert.False(context.Response.Headers.ContainsKey(HeaderNames.SetCookie));
        Assert.DoesNotContain(context.Response.Headers.Keys,
            key => key.StartsWith("Access-Control-Allow-", StringComparison.OrdinalIgnoreCase));
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, false, leaveOpen: true);
        Assert.Equal(entity, await reader.ReadToEndAsync());
    }

    private sealed class CancelingResponseStream(CancellationTokenSource cancellation) : MemoryStream
    {
        internal CancellationToken ObservedToken { get; private set; }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObservedToken = cancellationToken;
            cancellation.Cancel();
            return ValueTask.FromCanceled(cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

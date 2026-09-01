using System.Net;
using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class LocalMonitorV1CollectionRouteTests
{
    private const string RequestJson = "{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}";

    [Fact]
    public async Task RawDefaultSessionPostPublishesExactEmptyBytesAndSuccessHeaders()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        using var request = Post(host, RequestJson);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.False(response.Headers.Contains("Location")); Assert.False(response.Headers.Contains("ETag")); Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.Equal("{\"schema_version\":\"local-monitor-sessions.response.v1\",\"workspace_revision\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"items\":[],\"next_cursor\":null}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RawDefaultRepositoryGetPublishesExactEmptyBytesAndSuccessHeaders()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());

        using var response = await host.Client.GetAsync("/api/local-monitor/v1/repositories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal("{\"schema_version\":\"local-monitor-repositories.response.v1\",\"workspace_revision\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"repositories\":[],\"all_session_count\":0,\"unassigned_active_session_count\":0,\"archived_repository_count\":0,\"next_cursor\":null}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("HEAD", true)]
    public async Task RepositoryCompositionUnavailablePublishesFrozenServiceUnavailableResponse(string method, bool suppressesBody)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new SessionDetailFailureSnapshotService("local_monitor_ui_unavailable")));
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/local-monitor/v1/repositories");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(40, response.Content.Headers.ContentLength);
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal(suppressesBody ? [] : Encoding.UTF8.GetBytes("{\"error\":\"local_monitor_ui_unavailable\"}"), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SessionCompositionUnavailablePublishesFrozenServiceUnavailableResponse()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new SessionDetailFailureSnapshotService("local_monitor_ui_unavailable")));
        using var request = Post(host, RequestJson);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(40, response.Content.Headers.ContentLength);
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal("{\"error\":\"local_monitor_ui_unavailable\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RepositoryNonmatchingSessionDetailFailureRemainsAnInternalError()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new SessionDetailFailureSnapshotService("workspace_snapshot_stale")));

        using var response = await host.Client.GetAsync("/api/local-monitor/v1/repositories");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("{\"accepted\":false,\"error\":\"internal_error\",\"message\":\"The request could not be processed.\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SessionNonmatchingSessionDetailFailureRemainsAnInternalError()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new SessionDetailFailureSnapshotService("workspace_snapshot_stale")));
        using var request = Post(host, RequestJson);

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("{\"accepted\":false,\"error\":\"internal_error\",\"message\":\"The request could not be processed.\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task RepositoryWrongMethodsWinBeforeOriginQueryAndCursorProcessing(string method)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new BusySnapshotService()));
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/local-monitor/v1/repositories?unknown=secret&after=not-a-cursor");
        request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal(["GET", "HEAD", "POST"], response.Content.Headers.Allow);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(30, response.Content.Headers.ContentLength);
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal("{\"error\":\"method_not_allowed\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("padded=")]
    [InlineData("illegal%2Fcharacter")]
    [InlineData("illegal+character")]
    [InlineData("018f0000-0000-7000-8000-000000000101")]
    public async Task RepositoryMalformedCursorValuesAreInvalidCursorWithoutReadingSnapshot(string cursor)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new BusySnapshotService()));

        using var response = await host.Client.GetAsync("/api/local-monitor/v1/repositories?after=" + cursor);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType!.ToString());
        Assert.Equal(26, response.Content.Headers.ContentLength);
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
        Assert.Equal("{\"error\":\"invalid_cursor\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("unknown=value")]
    [InlineData("after=first&after=second")]
    public async Task RepositoryUnknownOrDuplicateQueryComponentsRemainInvalidRequest(string query)
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new BusySnapshotService()));

        using var response = await host.Client.GetAsync("/api/local-monitor/v1/repositories?" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(27, response.Content.Headers.ContentLength);
        Assert.Equal("{\"error\":\"invalid_request\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RepositoryCursorDefectsUseInvalidCursorWithoutReadingSnapshot()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var firstRequest = new LocalMonitorV1RepositoryRequest("include_archived", null, 1);
        var cursor = LocalMonitorV1RepositoryCursorCodec.Encode(key, firstRequest, "018f0000-0000-7000-8000-000000000101");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new BusySnapshotService(), new(new byte[32], null, null, key)));

        using var malformed = await host.Client.GetAsync("/api/local-monitor/v1/repositories?after=" + cursor[..50] + (cursor[50] == 'A' ? "B" : "A") + cursor[51..] + "&archive_scope=include_archived&limit=1");
        using var mismatch = await host.Client.GetAsync("/api/local-monitor/v1/repositories?after=" + cursor + "&archive_scope=active_only&limit=1");

        Assert.Equal("{\"error\":\"invalid_cursor\"}", await malformed.Content.ReadAsStringAsync());
        Assert.Equal("{\"error\":\"invalid_cursor\"}", await mismatch.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RepositoryHeadErrorsKeepGetMetadataAndSuppressEntityBytes()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var cursor = LocalMonitorV1RepositoryCursorCodec.Encode(key, new("include_archived", null, 1), "018f0000-0000-7000-8000-000000000101");
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new BusySnapshotService(), new(new byte[32], null, null, key)));

        using var invalidRequest = await host.Client.SendAsync(new(HttpMethod.Head, "/api/local-monitor/v1/repositories?unknown=value"));
        using var invalidCursor = await host.Client.SendAsync(new(HttpMethod.Head, "/api/local-monitor/v1/repositories?archive_scope=active_only&after=" + cursor + "&limit=1"));

        Assert.Equal(HttpStatusCode.BadRequest, invalidRequest.StatusCode);
        Assert.Equal(27, invalidRequest.Content.Headers.ContentLength);
        Assert.Empty(await invalidRequest.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.BadRequest, invalidCursor.StatusCode);
        Assert.Equal(26, invalidCursor.Content.Headers.ContentLength);
        Assert.Empty(await invalidCursor.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json; charset=utf-8", invalidCursor.Content.Headers.ContentType!.ToString());
        Assert.Equal(["no-store"], invalidCursor.Headers.GetValues("Cache-Control"));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("OPTIONS")]
    public async Task SessionWrongMethodsWinBeforeOriginAndBody(string method)
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/local-monitor/v1/sessions"); request.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode); Assert.Equal(["POST"], response.Content.Headers.Allow);
        Assert.Equal(method == "HEAD" ? "" : "{\"error\":\"method_not_allowed\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SessionAdmissionUsesExactPrecedenceAndNeverReflectsInput()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        using var cross = Post(host, "secret-model"); cross.Headers.Remove("Origin"); cross.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        using var crossResponse = await host.Client.SendAsync(cross); Assert.Equal("{\"error\":\"csrf_rejected\"}", await crossResponse.Content.ReadAsStringAsync());
        using var media = Post(host, "secret-model"); media.Content!.Headers.ContentType = new("text/plain");
        using var mediaResponse = await host.Client.SendAsync(media); Assert.Equal("{\"error\":\"unsupported_media_type\"}", await mediaResponse.Content.ReadAsStringAsync());
        using var invalid = Post(host, "secret-model"); using var invalidResponse = await host.Client.SendAsync(invalid);
        Assert.Equal("{\"error\":\"invalid_request\"}", await invalidResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SessionAdmissionRejectsInvalidHostAndOversizedDeclaredBodyBeforeParsing()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options());
        using var invalidHost = Post(host, "secret-model"); invalidHost.Headers.Host = "evil.example";
        using var hostResponse = await host.Client.SendAsync(invalidHost);
        Assert.Equal(HttpStatusCode.BadRequest, hostResponse.StatusCode); Assert.Equal("{\"error\":\"invalid_host\"}", await hostResponse.Content.ReadAsStringAsync());

        using var oversized = Post(host, new string('x', 32_769)); oversized.Content!.Headers.ContentType = new("text/plain");
        using var oversizedResponse = await host.Client.SendAsync(oversized);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedResponse.StatusCode); Assert.Equal("{\"error\":\"request_too_large\"}", await oversizedResponse.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SanitizedOnlyDoesNotRegisterEitherCollectionRoute()
    {
        using var temp = new MonitorTempDirectory(); await using var host = await MonitorTestHost.StartAsync(temp, sanitizedOnly: true, testOptions: Options());
        Assert.DoesNotContain("/api/local-monitor/v1/sessions", host.RoutePatterns); Assert.DoesNotContain("/api/local-monitor/v1/repositories", host.RoutePatterns);
        foreach (var method in new[] { "GET", "POST", "HEAD", "PUT", "PATCH", "DELETE", "OPTIONS" })
        {
            using var response = await host.Client.SendAsync(new(new HttpMethod(method), "/api/local-monitor/v1/sessions")); Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task FullyBufferedOverflowAndPersistenceBusyUseExactErrors()
    {
        using var overflowTemp = new MonitorTempDirectory();
        var largeRows = Enumerable.Range(0, 200).Select(index => LargeSession(index)).ToArray();
        await using var overflowHost = await MonitorTestHost.StartAsync(overflowTemp, testOptions: Options(new FixedSnapshotService(new(new(LocalRepositoryScopeKind.All, null), [], largeRows))));
        using var overflowRequest = Post(overflowHost, RequestJson); using var overflow = await overflowHost.Client.SendAsync(overflowRequest);
        Assert.Equal(HttpStatusCode.Conflict, overflow.StatusCode); Assert.Equal("{\"error\":\"workspace_too_large\"}", await overflow.Content.ReadAsStringAsync());

        using var busyTemp = new MonitorTempDirectory(); await using var busyHost = await MonitorTestHost.StartAsync(busyTemp, testOptions: Options(new BusySnapshotService()));
        using var busyRequest = Post(busyHost, RequestJson); using var busy = await busyHost.Client.SendAsync(busyRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode); Assert.Equal("{\"error\":\"persistence_busy\"}", await busy.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("final-page.json", false)]
    [InlineData("more-page.json", true)]
    public async Task CanonicalNonemptyPagesPublishExactTaskOneGoldenBytes(string fixture, bool hasMore)
    {
        var key=Enumerable.Range(0,32).Select(value=>(byte)value).ToArray();
        var item=hasMore?MoreItem():FinalItem(); var rows=hasMore?new[] { item, LargeLookahead() }:new[] { item };
        var collectionRevision=hasMore?new string('3',64):new string('1',64); var itemRevision=hasMore?new string('4',64):new string('2',64);
        using var temp=new MonitorTempDirectory(); await using var host=await MonitorTestHost.StartAsync(temp,testOptions:Options(new FixedSnapshotService(new(new(LocalRepositoryScopeKind.All,null),[],rows)),new(key,collectionRevision,itemRevision)));
        var body=hasMore?RequestJson.Replace("\"archive_scope\":\"active_only\"","\"archive_scope\":\"active_only\"").Replace("\"limit\":null","\"limit\":1"):RequestJson.Replace("\"archive_scope\":\"active_only\"","\"archive_scope\":\"include_archived\"");
        using var request=Post(host,body); using var response=await host.Client.SendAsync(request);
        var expected=File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"TestData","LocalMonitorV1SessionCollection",fixture));
        Assert.Equal(HttpStatusCode.OK,response.StatusCode); Assert.Equal(expected,await response.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("empty.json")]
    [InlineData("final-page.json")]
    [InlineData("more-page.json")]
    public async Task RepositoryPagesPublishExactTaskOneGoldenBytes(string fixture)
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var (snapshot, query, collectionRevision, itemRevision) = RepositoryFixture(fixture);
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: Options(new FixedSnapshotService(snapshot), new(new byte[32], null, null, key, collectionRevision, itemRevision)));

        using var response = await host.Client.GetAsync("/api/local-monitor/v1/repositories" + query);

        var expected = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "LocalMonitorV1RepositoryCollection", fixture));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
    }

    private static (LocalRepositoryScopeSnapshot Snapshot, string Query, string? CollectionRevision, string? ItemRevision) RepositoryFixture(string fixture)
    {
        if (fixture == "empty.json") return (new(new(LocalRepositoryScopeKind.All, null), [], []), "", null, null);
        var activeId = "018f0000-0000-7000-8000-000000000101";
        var archivedId = "018f0000-0000-7000-8000-000000000102";
        if (fixture == "final-page.json")
        {
            var repository = new LocalRepositoryCatalogSnapshot(archivedId, "Synthetic Archived", 1, null, 1, LocalArchiveState.Archived, 2);
            return (new(new(LocalRepositoryScopeKind.All, null), [repository], [SessionForRepository(1, null), SessionForRepository(2, null, archived: true), SessionForRepository(3, archivedId, archived: true)]), "?archive_scope=include_archived", new string('1', 64), new string('2', 64));
        }
        var active = new LocalRepositoryCatalogSnapshot(activeId, "Synthetic Active", 1, null, 0, LocalArchiveState.Active, 0);
        var lookahead = new LocalRepositoryCatalogSnapshot(archivedId, "Synthetic Archived", 1, null, 0, LocalArchiveState.Archived, 0);
        return (new(new(LocalRepositoryScopeKind.All, null), [active, lookahead], [SessionForRepository(1, activeId, lastSeenAt: "2026-01-02T03:04:05.0000000+00:00"), SessionForRepository(2, activeId), SessionForRepository(3, null), SessionForRepository(4, null, archived: true)]), "?archive_scope=include_archived&limit=1", new string('3', 64), new string('4', 64));
    }

    private static LocalRepositoryScopeSessionSnapshot SessionForRepository(int index, string? repositoryId, bool archived = false, string? lastSeenAt = null)
    {
        var row = LargeSession(index);
        var projection = (LocalWorkspaceProjectionRow)row.Session;
        var epoch = DateTimeOffset.TryParse(lastSeenAt, out var parsed) ? parsed.ToUnixTimeMilliseconds() : (long?)null;
        return row with
        {
            Session = projection with { LastSeenAt = lastSeenAt, LastSeenEpochMilliseconds = epoch },
            AssignmentState = repositoryId is null ? LocalRepositoryScopeAssignmentState.Unassigned : LocalRepositoryScopeAssignmentState.Assigned,
            AssignmentAuthority = repositoryId is null ? LocalRepositoryScopeAssignmentAuthority.None : LocalRepositoryScopeAssignmentAuthority.Automatic,
            RepositoryId = repositoryId,
            IsUnassignedScopeMember = repositoryId is null,
            IsRequestedScopeMember = repositoryId is not null,
            ArchiveState = archived ? LocalArchiveState.Archived : LocalArchiveState.Active,
            ArchiveRevision = archived ? 1 : 0,
            IsEffectivelyEligible = !archived,
            ArchiveExclusionReason = archived ? "session_archived" : null
        };
    }

    private static HttpRequestMessage Post(RunningMonitorHost host, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/local-monitor/v1/sessions") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("Origin", host.Client.BaseAddress!.GetLeftPart(UriPartial.Authority)); request.Headers.Add("x-monitor-csrf", "local-monitor"); return request;
    }

    private static MonitorHostTestOptions Options(ILocalRepositoryScopeSnapshotService? service = null, LocalMonitorV1CollectionTestOverrides? overrides = null) => new() { StartWriter=false, StartProjectionWorker=false, StartSessionWriter=false, StartSessionOtelEnrichment=false, StartRetentionCleanupWorker=false, StartLocalRepositoryCatalogHostedService=false, UseUserSecrets=false, LocalRepositoryScopeSnapshotService=service, LocalMonitorV1CollectionOverrides=overrides };

    private static LocalRepositoryScopeSessionSnapshot FinalItem()
    {
        var id="018f0000-0000-7000-8000-000000000001"; var zero=new LocalWorkspaceFact<long>("recorded",0); var missing=new LocalWorkspaceFact<long>("not_observed",null);
        var tokens=new LocalWorkspaceTokenFacts("session_run","inconsistent",1,1,new("recorded",10),new("recorded",2),missing,missing,new("recorded",20),zero,new("inconsistent",null),new("inconsistent",null));
        var p=new LocalWorkspaceProjectionRow(id,0,1_767_225_601_000,"not_observed",null,"completed","partial",new("recorded",["copilot-cli"]),new("not_observed",[]),new(zero,missing,zero,zero,zero),tokens,"recorded","2026-01-01T00:00:00.0000000+00:00","2026-01-01T00:00:01.0000000+00:00","2026-01-01T00:00:02.0000000+00:00",1000,["cache_inconsistent","token_inconsistent"],"seed");
        return new(id,p,0,LocalRepositoryScopeAssignmentState.Unassigned,LocalRepositoryScopeAssignmentAuthority.None,null,[],true,true,true,LocalArchiveState.Archived,1,false,"session_archived");
    }

    private static LocalRepositoryScopeSessionSnapshot MoreItem()
    {
        var id="018f0000-0000-7000-8000-000000000002"; var zero=new LocalWorkspaceFact<long>("recorded",0); var missing=new LocalWorkspaceFact<long>("not_observed",null);
        var tokens=new LocalWorkspaceTokenFacts("mixed","recorded",2,2,new("recorded",100),new("recorded",25),new("recorded",125),new("recorded",5),new("recorded",40),new("recorded",10),new("recorded",60),new("recorded",4000));
        var p=new LocalWorkspaceProjectionRow(id,0,1_767_312_000_000,"recorded","Synthetic session","active","rich",new("recorded",["copilot-sdk","vscode"]),new("recorded",["model-a"]),new(new("recorded",1),zero,missing,zero,zero),tokens,"recorded","2026-01-02T00:00:00.0000000+00:00","2026-01-02T00:00:02.0000000+00:00","2026-01-02T00:00:03.0000000+00:00",2000,[],"seed");
        return new(id,p,2,LocalRepositoryScopeAssignmentState.Conflict,LocalRepositoryScopeAssignmentAuthority.Automatic,null,["018f0000-0000-7000-8000-000000000101","018f0000-0000-7000-8000-000000000102"],true,true,true,LocalArchiveState.Active,0,true,null);
    }

    private static LocalRepositoryScopeSessionSnapshot LargeLookahead()
    {
        var row=LargeSession(999); var p=(LocalWorkspaceProjectionRow)row.Session;
        return row with { Session=p with { SortEpochMilliseconds=1_767_311_999_000 } };
    }

    private static LocalRepositoryScopeSessionSnapshot LargeSession(int index)
    {
        var id=$"018f0000-0000-7000-8000-{index:x12}"; var missing=new LocalWorkspaceFact<long>("not_observed",null);
        var projection=new LocalWorkspaceProjectionRow(id,1,0,"recorded",new string('x',200_000),"active","rich",new("not_observed",[]),new("not_observed",[]),new(missing,missing,missing,missing,missing),new("none","not_observed",0,0,missing,missing,missing,missing,missing,missing,missing,missing),"not_observed",null,null,"2026-01-01T00:00:00.0000000+00:00",null,[],"seed");
        return new(id,projection,0,LocalRepositoryScopeAssignmentState.Unassigned,LocalRepositoryScopeAssignmentAuthority.None,null,[],true,true,true,LocalArchiveState.Active,0,true,null);
    }

    private sealed class FixedSnapshotService(LocalRepositoryScopeSnapshot snapshot) : ILocalRepositoryScopeSnapshotService
    { public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(LocalRepositoryScopeRequest request,CancellationToken cancellationToken)=>ValueTask.FromResult(snapshot with { Request=request }); }
    private sealed class BusySnapshotService : ILocalRepositoryScopeSnapshotService
    { public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(LocalRepositoryScopeRequest request,CancellationToken cancellationToken)=>throw new LocalRepositoryScopeSnapshotException(LocalRepositoryScopeSnapshotError.PersistenceBusy,"persistence_busy",new InvalidOperationException()); }
    private sealed class SessionDetailFailureSnapshotService(string error) : ILocalRepositoryScopeSnapshotService
    { public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(LocalRepositoryScopeRequest request,CancellationToken cancellationToken)=>throw new LocalWorkspaceSessionDetailException(error); }
}

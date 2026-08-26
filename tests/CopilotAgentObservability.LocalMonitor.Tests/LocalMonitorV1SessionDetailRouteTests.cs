using System.Net;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailRouteTests
{
    private const string SessionId="018f0000-0000-7000-8000-000000000001";

    [Fact]
    public async Task ProductionSummaryGetAndHeadMatchTheNonemptyGoldenExactly()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp);
        using var host = await StartProductionDetailRouteAsync(temp);
        var expected = File.ReadAllBytes(Path.Combine(
            FindRepositoryRoot(), "tests", "CopilotAgentObservability.LocalMonitor.Tests", "TestData",
            "LocalMonitorV1SessionDetail", "summary-full.json"));

        using var get = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var head = await host.Client.SendAsync(new(HttpMethod.Head,
            $"/api/local-monitor/v1/sessions/{SessionId}/summary"));

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var actual = await get.Content.ReadAsByteArrayAsync();
        Assert.True(expected.AsSpan().SequenceEqual(actual), System.Text.Encoding.UTF8.GetString(actual));
        Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], get.Headers.GetValues("Cache-Control"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task ProductionNonrecordedSummaryGetAndHeadMatchTheGoldenExactly()
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicNonrecordedSession(temp);
        using var host = await StartProductionDetailRouteAsync(temp);
        var path = $"/api/local-monitor/v1/sessions/{SessionId}/summary";
        using var get = await host.Client.GetAsync(path);
        var actual = await get.Content.ReadAsByteArrayAsync();
        var expected = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "LocalMonitorV1SessionDetail",
            "summary-nonrecorded-evidence.json"));

        Assert.True(expected.AsSpan().SequenceEqual(actual), System.Text.Encoding.UTF8.GetString(actual));
        Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("timeline-page.json", "timeline")]
    [InlineData("node-full.json", "root")]
    [InlineData("node-nested.json", "child")]
    public async Task ProductionDetailGetMatchesTheNonemptyGoldenExactly(string goldenName, string target)
    {
        using var temp = new MonitorTempDirectory();
        SeedDeterministicSession(temp);
        using var host = await StartProductionDetailRouteAsync(temp);
        using var summaryResponse = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var summary = JsonDocument.Parse(await summaryResponse.Content.ReadAsByteArrayAsync());
        var revision = summary.RootElement.GetProperty("workspace_revision").GetString()!;
        var execution = summary.RootElement.GetProperty("executions")[0];
        var executionId = execution.GetProperty("execution_id").GetString()!;
        var rootNodeId = execution.GetProperty("node_id").GetString()!;
        var timelinePath = $"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={revision}&execution_id={executionId}&limit=1";
        using var timelineResponse = await host.Client.GetAsync(timelinePath);
        var timelineBytes = await timelineResponse.Content.ReadAsByteArrayAsync();
        using var timeline = JsonDocument.Parse(timelineBytes);
        var childNodeId = timeline.RootElement.GetProperty("items")[0].GetProperty("node_id").GetString()!;
        var path = target switch
        {
            "timeline" => timelinePath,
            "root" => $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{rootNodeId}?workspace_revision={revision}",
            _ => $"/api/local-monitor/v1/sessions/{SessionId}/nodes/{childNodeId}?workspace_revision={revision}",
        };
        using var get = target == "timeline" ? timelineResponse : await host.Client.GetAsync(path);
        var actual = target == "timeline" ? timelineBytes : await get.Content.ReadAsByteArrayAsync();
        var expected = File.ReadAllBytes(Path.Combine(FindRepositoryRoot(), "tests",
            "CopilotAgentObservability.LocalMonitor.Tests", "TestData", "LocalMonitorV1SessionDetail", goldenName));

        Assert.True(expected.AsSpan().SequenceEqual(actual), System.Text.Encoding.UTF8.GetString(actual));
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(expected.Length, get.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", get.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], get.Headers.GetValues("Cache-Control"));
        using var head = await host.Client.SendAsync(new(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal(expected.Length, head.Content.Headers.ContentLength);
        Assert.Equal("application/json; charset=utf-8", head.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], head.Headers.GetValues("Cache-Control"));
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryHttpPipelineAcceptsExactlyEightMiBForGetAndHead(bool head)
    {
        var baseline = Snapshot(SessionId, new([], [], [], [], [], [], null, null, "canonical", "generation"), new string('1', 64), string.Empty);
        var baselineLength = LocalMonitorV1SessionDetailApplication.SerializeSummary(baseline).Length;
        var snapshot = Snapshot(SessionId, baseline.Detail, baseline.WorkspaceRevision, new string('a', 8_388_608 - baselineLength));
        using var running = await StartDetailRouteAsync(new FixedDetailService(snapshot));

        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get,
            $"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var response = await running.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(8_388_608, response.Content.Headers.ContentLength);
        Assert.Equal(head ? 0 : 8_388_608, (await response.Content.ReadAsByteArrayAsync()).Length);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SummaryHttpPipelineRejectsEightMiBPlusOneWithFixedBody(bool head)
    {
        var baseline = Snapshot(SessionId, new([], [], [], [], [], [], null, null, "canonical", "generation"), new string('1', 64), string.Empty);
        var baselineLength = LocalMonitorV1SessionDetailApplication.SerializeSummary(baseline).Length;
        var snapshot = Snapshot(SessionId, baseline.Detail, baseline.WorkspaceRevision, new string('a', 8_388_609 - baselineLength));
        using var running = await StartDetailRouteAsync(new FixedDetailService(snapshot));

        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get,
            $"/api/local-monitor/v1/sessions/{SessionId}/summary");
        using var response = await running.Client.SendAsync(request);

        var expected = System.Text.Encoding.UTF8.GetBytes("{\"error\":\"workspace_too_large\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(expected.Length, response.Content.Headers.ContentLength);
        Assert.Equal(head ? [] : expected, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
    }

    [Fact]
    public async Task SummaryReadsASeededSessionThroughTheProductionCoordinator()
    {
        using var temp=new MonitorTempDirectory();
        var session=AlertCenterRouteTests.SeedPersistedTraceAndSession(temp,"00000000000000000000000000000001",authoritativeToolStatus:true);
        await using var host=await MonitorTestHost.StartAsync(temp);

        _=await host.Services.GetRequiredService<ILocalRepositorySessionDetailSnapshotService>()
            .ReadDetailAsync(new(LocalRepositorySessionDetailRequestKind.Summary,session.ToString("D")),CancellationToken.None);

        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{session:D}/summary");

        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal("application/json; charset=utf-8",response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"],response.Headers.GetValues("Cache-Control"));
        var bytes=await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length,response.Content.Headers.ContentLength);
        Assert.Contains("\"schema_version\":\"local-monitor-session-summary.response.v1\"",System.Text.Encoding.UTF8.GetString(bytes),StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionSummaryDeduplicatesOneNativeSessionIdAcrossSourceSurfaces()
    {
        using var temp = new MonitorTempDirectory();
        var session = AlertCenterRouteTests.SeedPersistedTraceAndSession(temp, "00000000000000000000000000000001", authoritativeToolStatus: true);
        using (var connection = Open(temp))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at) VALUES($session,'claude-code','shared-native','native','2026-08-26T00:00:00.0000000+00:00'),($session,'copilot-sdk','shared-native','native','2026-08-26T00:00:00.0000000+00:00');";
            command.Parameters.AddWithValue("$session", session.ToString("D"));
            command.ExecuteNonQuery();
        }
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var response = await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{session:D}/summary");
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(json.RootElement.GetProperty("technical_references").GetProperty("native_session_ids")
            .EnumerateArray(), static value => value.GetString() == "shared-native");
    }

    [Fact]
    public async Task ProductionTimelinePagesThreeHundredChildrenWhenParentSortsAfterThem()
    {
        using var temp = new MonitorTempDirectory();
        var session = AlertCenterRouteTests.SeedPersistedTraceAndSession(temp, "00000000000000000000000000000001", authoritativeToolStatus: true);
        string executionId;
        string parentId;
        using (var connection = Open(temp))
        {
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
            executionId = Scalar(connection, "SELECT execution_id FROM local_workspace_execution_headers WHERE session_id=$session LIMIT 1;", session.ToString("D"));
            var rootId = Scalar(connection, "SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND execution_id=$execution AND source_kind='execution_root';", session.ToString("D"), executionId);
            parentId = InsertClonedNodes(connection, session.ToString("D"), executionId, rootId, 301);
        }
        var key = new byte[32];
        var revision = new string('1', 64);
        var seen = new List<string>();
        string? after = null;
        using (var connection = Open(temp))
        using (var transaction = connection.BeginTransaction())
        {
            using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = "CREATE TABLE IF NOT EXISTS skill_invocation_snapshots(snapshot_id TEXT,session_id TEXT); CREATE TABLE IF NOT EXISTS skill_invocation_snapshot_receipts(snapshot_id TEXT);";
                schema.ExecuteNonQuery();
            }
            var contributor = new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: FixedSkillRegistryGenerationAuthority.Load());
            var summaryDetail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Summary, session.ToString("D")), CancellationToken.None);
            using (var summary = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeSummary(Snapshot(session.ToString("D"), summaryDetail, revision))))
                Assert.True(summary.RootElement.GetProperty("executions")[0].GetProperty("child_count").GetInt32() > 0);
            var topLevelDetail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                new(LocalRepositorySessionDetailRequestKind.Timeline, session.ToString("D"), executionId, Limit: 100), CancellationToken.None);
            using (var topLevel = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(
                Snapshot(session.ToString("D"), topLevelDetail, revision), executionId, null, 100, null, key)))
                Assert.Equal(301, topLevel.RootElement.GetProperty("items").EnumerateArray()
                    .Single(item => item.GetProperty("node_id").GetString() == parentId).GetProperty("child_count").GetInt32());
            do
            {
                LocalRepositoryTimelinePosition? position = null;
                if (after is not null)
                {
                    Assert.True(LocalMonitorV1TimelineCursor.TryDecode(after, key,
                        new(session.ToString("D"), revision, executionId, parentId, 100), out var decoded));
                    position = new(decoded.TimeGroup, decoded.UtcTicks, decoded.SourceOrdinal, decoded.NodeId);
                }
                var detail = await contributor.ReadAsync(new DirectReadTransaction(connection, transaction),
                    new(LocalRepositorySessionDetailRequestKind.Timeline, session.ToString("D"), executionId, parentId, position, 100), CancellationToken.None);
                using var page = JsonDocument.Parse(LocalMonitorV1SessionDetailApplication.SerializeTimeline(
                    Snapshot(session.ToString("D"), detail, revision), executionId, parentId, 100, after, key));
                seen.AddRange(page.RootElement.GetProperty("items").EnumerateArray().Select(static item => item.GetProperty("node_id").GetString()!));
                after = page.RootElement.GetProperty("next_cursor").GetString();
            }
            while (after is not null);
        }

        Assert.Equal(301, seen.Count);
        Assert.Equal(301, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task WrongMethodWinsBeforeIdentifiersAndQuery(string method)
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var request=new HttpRequestMessage(new HttpMethod(method),"/api/local-monitor/v1/sessions/not-an-id/summary?unknown=secret");
        using var response=await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,response.StatusCode);Assert.Equal(["GET","HEAD"],response.Content.Headers.Allow);
        Assert.Equal("{\"error\":\"method_not_allowed\"}",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SummaryRejectsQueryBeforeSessionLookup()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary?unknown=secret");
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal("{\"error\":\"invalid_request\"}",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidAbsentSessionReturnsFixedNotFound()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);Assert.Equal("{\"error\":\"session_not_found\"}",await response.Content.ReadAsStringAsync());
        Assert.Equal(["no-store"],response.Headers.GetValues("Cache-Control"));
    }

    [Fact]
    public async Task HeadUsesGetErrorLengthWithoutEntity()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.SendAsync(new(HttpMethod.Head,$"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision=bad"));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(27,response.Content.Headers.ContentLength);Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task TimelinePassesTheClosedRequestShapeIntoTheSharedCoordinator()
    {
        var service = new CapturingDetailService();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, new byte[32]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={new string('1',64)}&execution_id=018f0000-0000-7000-8000-000000000003&parent_node_id=node-00000000000000000000000000000001&limit=17");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var request = Assert.IsType<LocalRepositorySessionDetailRequest>(service.Request);
        Assert.Equal(LocalRepositorySessionDetailRequestKind.Timeline, request.Kind);
        Assert.Equal(SessionId, request.SessionId);
        Assert.Equal("018f0000-0000-7000-8000-000000000003", request.ExecutionId);
        Assert.Equal("node-00000000000000000000000000000001", request.ParentNodeId);
        Assert.Equal(17, request.Limit);
        Assert.Null(request.After);
    }

    private sealed class CapturingDetailService : ILocalRepositorySessionDetailSnapshotService
    {
        internal LocalRepositorySessionDetailRequest? Request { get; private set; }
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(LocalRepositorySessionDetailRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            throw new LocalWorkspaceSessionDetailException("session_not_found");
        }
    }

    private sealed class FixedDetailService(LocalRepositorySessionDetailSnapshot snapshot) : ILocalRepositorySessionDetailSnapshotService
    {
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(LocalRepositorySessionDetailRequest request, CancellationToken cancellationToken) =>
            ValueTask.FromResult(snapshot);
    }

    private static async Task<RunningDetailRoute> StartDetailRouteAsync(ILocalRepositorySessionDetailSnapshotService service)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, new byte[32]);
        await app.StartAsync();
        return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private static async Task<RunningDetailRoute> StartProductionDetailRouteAsync(MonitorTempDirectory temp)
    {
        using (var connection = Open(temp))
        {
            using (var transaction = connection.BeginTransaction())
            {
                SkillProjectionSchemaV1.Ensure(connection, transaction);
                SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
                LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
                LocalArchiveSchemaV1.Ensure(connection, transaction);
                transaction.Commit();
            }
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch, FixedSkillRegistryGenerationAuthority.Load());
        }
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.Load());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray());
        await app.StartAsync();
        return new(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) });
    }

    private sealed class RunningDetailRoute(WebApplication app, HttpClient client) : IDisposable
    {
        internal HttpClient Client { get; } = client;
        public void Dispose()
        {
            Client.Dispose();
            app.StopAsync().GetAwaiter().GetResult();
            app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static string Scalar(SqliteConnection connection, string sql, string sessionId, string? executionId = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$execution", (object?)executionId ?? DBNull.Value);
        return (string)command.ExecuteScalar()!;
    }

    private static LocalRepositorySessionDetailSnapshot Snapshot(string sessionId, LocalWorkspaceSessionDetailContribution detail, string revision, string? label = null)
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null); var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(sessionId, 0, 0, label is null ? "not_observed" : "recorded", label, "completed", "full", new("not_observed", []), new("not_observed", []), activity, tokens, "not_observed", null, null, null, null, [], "revision");
        return new(new(sessionId, row, 0, LocalRepositoryScopeAssignmentState.Unassigned, LocalRepositoryScopeAssignmentAuthority.None, null, [], true, true, true, LocalArchiveState.Active, 0, true, null), detail, revision);
    }

    private static SqliteConnection Open(MonitorTempDirectory temp)
    {
        var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private static void SeedDeterministicSession(MonitorTempDirectory temp)
    {
        var observed = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var sessionId = Guid.Parse(SessionId);
        var runId = Guid.Parse("018f0000-0000-7000-8000-000000000003");
        var eventId = Guid.Parse("018f0000-0000-7000-8000-000000000004");
        var secondEventId = Guid.Parse("018f0000-0000-7000-8000-000000000005");
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        store.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Rich,
                    Repository: null, Workspace: null, StartedAt: observed,
                    EndedAt: observed.AddSeconds(1), LastSeenAt: observed.AddSeconds(1),
                    SessionRawRetentionState.NotCaptured, CreatedAt: observed, UpdatedAt: observed.AddSeconds(1)),
                [new SessionNativeId(sessionId, SessionSourceSurface.VisualStudioCode, "native-session-detail-golden", SessionBindingKind.Native, observed)],
                [new ObservedSessionRun(
                    runId, sessionId, SessionSourceSurface.VisualStudioCode, NativeRunId: "run-detail-golden",
                    TraceId: "00000000000000000000000000000001", ParentRunId: null, Model: "gpt-5.6-sol",
                    ObservedSessionStatus.Completed, StartedAt: observed, EndedAt: observed.AddSeconds(1),
                    InputTokens: 10, OutputTokens: 5, TotalTokens: 15)],
                [new ObservedSessionEvent(
                    eventId, sessionId, runId, SessionSourceSurface.VisualStudioCode, ParentEventId: null,
                    TraceId: "00000000000000000000000000000001", Status: "completed",
                    SourceAdapter: "github-copilot-vscode-otel", SourceEventId: "event-detail-golden",
                    Type: "user.message", OccurredAt: observed, SessionContentState.NotCaptured,
                    SourceApplicationVersion: "1.0", AdapterVersion: "monitor-projection-v1",
                    SchemaFingerprint: null, NormalizationVersion: "session-normalization-v1"),
                 new ObservedSessionEvent(
                    secondEventId, sessionId, runId, SessionSourceSurface.VisualStudioCode, ParentEventId: null,
                    TraceId: "00000000000000000000000000000001", Status: "completed",
                    SourceAdapter: "github-copilot-vscode-otel", SourceEventId: "event-detail-golden-2",
                    Type: "tool.completed", OccurredAt: observed.AddMilliseconds(500), SessionContentState.NotCaptured,
                    SourceApplicationVersion: "1.0", AdapterVersion: "monitor-projection-v1",
                    SchemaFingerprint: null, NormalizationVersion: "session-normalization-v1")]),
            []));
    }

    private static void SeedDeterministicNonrecordedSession(MonitorTempDirectory temp)
    {
        var observed = new DateTimeOffset(2026, 8, 26, 1, 2, 3, TimeSpan.Zero);
        var sessionId = Guid.Parse(SessionId);
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        store.Write(new SessionWriteBatch(
            new SessionDetail(
                new ObservedSession(
                    sessionId, ObservedSessionStatus.Active, SessionCompleteness.Unbound,
                    Repository: null, Workspace: null, StartedAt: null, EndedAt: null, LastSeenAt: observed,
                    SessionRawRetentionState.NotCaptured, CreatedAt: observed, UpdatedAt: observed),
                [new SessionNativeId(sessionId, SessionSourceSurface.VisualStudioCode,
                    "native-nonrecorded-evidence", SessionBindingKind.Native, observed)],
                [], []),
            []));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class DirectReadTransaction(SqliteConnection connection, SqliteTransaction transaction) : ILocalRepositoryReadTransaction
    {
        public ValueTask<T> ReadAsync<T>(Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken) =>
            read(connection, transaction, cancellationToken);
    }

    private static string InsertClonedNodes(SqliteConnection connection, string sessionId, string executionId, string rootId, int childCount)
    {
        var columns = new List<string>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA table_info(local_workspace_nodes);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        var parentId = LocalWorkspaceProjectionStore.StableNodeId("session_event", "late-parent");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var expressions = columns.Select(column => column switch
        {
            "node_id" => "$node_id", "session_id" => "$session", "execution_id" => "$execution",
            "source_kind" => "'session_event'", "source_identity" => "$identity", "source_ordinal" => "$ordinal",
            "parent_node_id" => "$parent", "relationship_authority" => "'exact'", "kind" => "'event'",
            "time_authority" => "$time", "start_utc_ticks" or "end_utc_ticks" or "duration_ms" => "NULL",
            _ => column,
        });
        command.CommandText = $"INSERT INTO local_workspace_nodes({string.Join(',', columns)}) SELECT {string.Join(',', expressions)} FROM local_workspace_nodes WHERE node_id=$root;";
        command.Parameters.AddWithValue("$session", sessionId); command.Parameters.AddWithValue("$execution", executionId); command.Parameters.AddWithValue("$root", rootId);
        var node = command.Parameters.Add("$node_id", SqliteType.Text); var identity = command.Parameters.Add("$identity", SqliteType.Text);
        var ordinal = command.Parameters.Add("$ordinal", SqliteType.Integer); var parent = command.Parameters.Add("$parent", SqliteType.Text); var time = command.Parameters.Add("$time", SqliteType.Text);
        node.Value = parentId; identity.Value = "late-parent"; ordinal.Value = childCount + 10; parent.Value = rootId; time.Value = "invalid"; command.ExecuteNonQuery();
        for (var index = 0; index < childCount; index++)
        {
            var value = $"child-{index:D3}"; node.Value = LocalWorkspaceProjectionStore.StableNodeId("session_event", value);
            identity.Value = value; ordinal.Value = index; parent.Value = parentId; time.Value = index < 200 ? "missing" : "invalid"; command.ExecuteNonQuery();
        }
        transaction.Commit();
        return parentId;
    }
}

using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.LocalMonitor.Repositories;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionDetailRevisionMatrixTests
{
    public static TheoryData<string, string, bool> PersistedRevisionInputs => new()
    {
        { "sessions.status", "UPDATE sessions SET status=CASE status WHEN 'active' THEN 'completed' ELSE 'active' END WHERE session_id=$session;", true },
        { "sessions.last_seen_at", "UPDATE sessions SET last_seen_at='2026-08-26T00:00:02.0000000+00:00' WHERE session_id=$session;", true },
        { "session_repository_assignment_revisions.revision", "UPDATE session_repository_assignment_revisions SET revision=revision+1,updated_at='2026-08-26T00:00:02.0000000+00:00' WHERE session_id=$session;", false },
        { "session_runs.status", "UPDATE session_runs SET status=CASE status WHEN 'completed' THEN 'failed' ELSE 'completed' END WHERE session_id=$session;", true },
        { "session_events.source_application_version", "UPDATE session_events SET source_application_version='matrix-event-v2' WHERE session_id=$session;", true },
        { "monitor_spans.status", "UPDATE monitor_spans SET status=CASE status WHEN 'ERROR' THEN 'OK' ELSE 'ERROR' END WHERE trace_id IN (SELECT trace_id FROM session_runs WHERE session_id=$session);", true },
        { "local_workspace_span_facts.retry_count", "UPDATE local_workspace_span_facts SET retry_count=COALESCE(retry_count,0)+1 WHERE raw_record_id IN (SELECT raw_record_id FROM monitor_spans WHERE trace_id IN (SELECT trace_id FROM session_runs WHERE session_id=$session));", false },
        { "local_workspace_execution_headers.status", "UPDATE local_workspace_execution_headers SET status=CASE status WHEN 'completed' THEN 'failed' ELSE 'completed' END WHERE session_id=$session;", false },
        { "local_workspace_nodes.status", "UPDATE local_workspace_nodes SET status=CASE status WHEN 'completed' THEN 'failed' ELSE 'completed' END WHERE session_id=$session;", false },
        { "local_workspace_node_edges.source_ordinal", "UPDATE local_workspace_node_edges SET source_ordinal=source_ordinal+1 WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id=$session);", false },
        { "local_workspace_node_content_refs.revision_input", "UPDATE local_workspace_node_content_refs SET revision_input=revision_input||':matrix' WHERE node_id IN (SELECT node_id FROM local_workspace_nodes WHERE session_id=$session);", false },
    };

    [Theory]
    [MemberData(nameof(PersistedRevisionInputs))]
    public async Task PersistedProductionSourceMutationChangesCoordinatorRevision(string source, string mutation, bool refresh)
    {
        using var fixture = await RealFixture.CreateAsync();
        var before = await fixture.ReadSummaryAsync();
        Assert.True(fixture.Execute(mutation, refresh) > 0, $"{source} did not mutate a real row");
        Assert.NotEqual(before.WorkspaceRevision, (await fixture.ReadSummaryAsync()).WorkspaceRevision);
    }

    [Fact]
    public async Task StableProductionRereadKeepsRevision()
    {
        using var fixture = await RealFixture.CreateAsync();
        Assert.Equal((await fixture.ReadSummaryAsync()).WorkspaceRevision, (await fixture.ReadSummaryAsync()).WorkspaceRevision);
    }

    [Fact]
    public async Task SessionArchiveAuthorityChangesSummaryRevisionAndStateWhileNoOpAndRestoreAreStableAndFenced()
    {
        using var fixture = await RealFixture.CreateAsync();
        var before = await fixture.ReadSummaryAsync();

        var archived = fixture.Archive(LocalArchiveTargetKind.Session, fixture.SessionId, 0, LocalArchiveAction.Archive);
        var afterArchive = await fixture.ReadSummaryAsync();
        Assert.NotEqual(before.WorkspaceRevision, afterArchive.WorkspaceRevision);
        Assert.Equal(LocalArchiveState.Archived, afterArchive.Session.ArchiveState);
        Assert.Equal(1, afterArchive.Session.ArchiveRevision);
        var noOp = fixture.Archive(LocalArchiveTargetKind.Session, fixture.SessionId, 1, LocalArchiveAction.Archive);
        Assert.Equal(archived, noOp);
        await fixture.AssertStaleRoutesAsync(before);

        fixture.Archive(LocalArchiveTargetKind.Session, fixture.SessionId, 1, LocalArchiveAction.Restore);
        var restored = await fixture.ReadSummaryAsync();
        Assert.NotEqual(afterArchive.WorkspaceRevision, restored.WorkspaceRevision);
        Assert.Equal(LocalArchiveState.Active, restored.Session.ArchiveState);
        Assert.Equal(2, restored.Session.ArchiveRevision);
        await fixture.AssertStaleRoutesAsync(afterArchive);
    }

    [Fact]
    public async Task AssignedRepositoryArchiveAuthorityChangesSummaryBytesEligibilityAndRevision()
    {
        using var fixture = await RealFixture.CreateAsync();
        var repositoryId = await fixture.CreateAndAssignRepositoryAsync();
        var before = await fixture.ReadSummaryAsync();
        var beforeBytes = LocalMonitorV1SessionDetailApplication.SerializeSummary(before);
        Assert.True(before.Session.IsEffectivelyEligible);

        fixture.Archive(LocalArchiveTargetKind.Repository, repositoryId, 0, LocalArchiveAction.Archive);
        var after = await fixture.ReadSummaryAsync();
        Assert.NotEqual(before.WorkspaceRevision, after.WorkspaceRevision);
        Assert.NotEqual(beforeBytes, LocalMonitorV1SessionDetailApplication.SerializeSummary(after));
        Assert.False(after.Session.IsEffectivelyEligible);
        Assert.Equal("repository_archived", after.Session.ArchiveExclusionReason);
        Assert.Equal(1, after.Session.AssignedRepositoryArchiveRevision);
        await fixture.AssertStaleRoutesAsync(before);
    }

    [Theory]
    [InlineData("raw_records.payload_json", "UPDATE raw_records SET payload_json=json_set(payload_json,'$.revision_matrix',1) WHERE id=(SELECT m.raw_record_id FROM monitor_spans m JOIN session_runs r ON r.trace_id=m.trace_id WHERE r.session_id=$session ORDER BY m.raw_record_id LIMIT 1);")]
    [InlineData("session_event_content.content_json", "UPDATE session_event_content SET content_json=json_set(content_json,'$.revision_matrix',1) WHERE event_id=(SELECT c.event_id FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id WHERE e.session_id=$session ORDER BY c.event_id LIMIT 1);")]
    public async Task LinkedRawCarrierMutationChangesSourceRevisionAndFencesOldSnapshot(string source,string sql)
    {
        using var fixture=await RealFixture.CreateAsync();
        var before=await fixture.ReadSummaryAsync();
        Assert.True(fixture.Execute(sql,false)==1,$"{source} did not mutate its linked carrier row");
        var after=await fixture.ReadSummaryAsync();
        Assert.NotEqual(before.WorkspaceRevision,after.WorkspaceRevision);
        await fixture.AssertStaleRoutesAsync(before);
    }

    [Fact]
    public async Task RealRegistryGenerationPublicationChangesCoordinatorRevisionAndFencesOldSnapshot()
    {
        using var fixture=await RealFixture.CreateAsync();
        var before=await fixture.ReadSummaryAsync();
        fixture.PublishRegistryGeneration();
        var after=await fixture.ReadSummaryAsync();
        Assert.NotEqual(before.Detail.SkillRegistryGenerationIdentity,after.Detail.SkillRegistryGenerationIdentity);
        Assert.NotEqual(before.WorkspaceRevision,after.WorkspaceRevision);
        await fixture.AssertStaleRoutesAsync(before);
    }

    [Theory]
    [InlineData("timeline")]
    [InlineData("node")]
    public async Task ProductionRoutesFenceStaleRevisionBeforeChildMembershipWithoutMixingValues(string route)
    {
        using var fixture = await RealFixture.CreateAsync();
        var before = await fixture.ReadSummaryAsync();
        var oldNode = before.Detail.Nodes[0].NodeId;
        fixture.Execute("UPDATE sessions SET status=CASE status WHEN 'active' THEN 'completed' ELSE 'active' END WHERE session_id=$session;", true);
        var absent = "ffffffffffffffffffffffffffffffff";
        var path = route == "timeline"
            ? $"/api/local-monitor/v1/sessions/{fixture.SessionId}/timeline?workspace_revision={before.WorkspaceRevision}&execution_id=018f0000-0000-7000-8000-{absent[..12]}"
            : $"/api/local-monitor/v1/sessions/{fixture.SessionId}/nodes/node-{absent}?workspace_revision={before.WorkspaceRevision}";
        using var response = await fixture.Client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("{\"error\":\"workspace_snapshot_stale\"}", body);
        Assert.DoesNotContain(oldNode, body, StringComparison.Ordinal);
        Assert.DoesNotContain(absent, body, StringComparison.Ordinal);
    }

    public static TheoryData<string> RevisionInputs => new()
    {
        "session_row", "assignment", "session_archive", "repository_archive",
        "run_event_span_projection", "skill_generation", "skill_current_fact",
        "retention_state", "raw_availability_binding"
    };

    [Theory]
    [MemberData(nameof(RevisionInputs))]
    public void EveryContractRevisionInputChangesTheProductionDigestInIsolation(string input)
    {
        var original = Snapshot();
        var changed = input switch
        {
            "session_row" => original with { Session = original.Session with { Session = ((LocalWorkspaceProjectionRow)original.Session.Session) with { Status = "active" } } },
            "assignment" => original with { Session = original.Session with { AssignmentRevision = 2 } },
            "session_archive" => original with { Session = original.Session with { ArchiveRevision = 3 } },
            "repository_archive" => original with { Session = original.Session with { AssignedRepositoryArchiveRevision = 4 } },
            "run_event_span_projection" => original with { Detail = original.Detail with { CanonicalRevisionInput = "run-event-span-node-edge:v2" } },
            "skill_generation" => original with { Detail = original.Detail with { SkillRegistryGenerationIdentity = "generation:v2" } },
            "skill_current_fact" => original with { Detail = original.Detail with { CanonicalRevisionInput = "canonical-current-skill:v2" } },
            "retention_state" => original with { Detail = original.Detail with { CanonicalRevisionInput = "retention:expired" } },
            "raw_availability_binding" => original with { Detail = original.Detail with { CanonicalRevisionInput = "raw:unavailable" } },
            _ => throw new ArgumentOutOfRangeException(nameof(input)),
        };

        Assert.NotEqual(Revision(original), Revision(changed));
    }

    [Fact]
    public void IdenticalCoherentSnapshotHasTheSameRevision()
    {
        var snapshot = Snapshot();
        Assert.Equal(Revision(snapshot), Revision(snapshot with { }));
    }

    [Fact]
    public void CompleteJsonEntityAcceptsExactlyEightMiBAndRejectsTheNextByte()
    {
        var baseline = Snapshot(label: string.Empty);
        var baselineLength = LocalMonitorV1SessionDetailApplication.SerializeSummary(baseline).Length;
        var accepted = Snapshot(label: new string('a', 8_388_608 - baselineLength));

        Assert.Equal(8_388_608, LocalMonitorV1SessionDetailApplication.SerializeSummary(accepted).Length);

        var rejected = accepted with
        {
            Session = accepted.Session with
            {
                Session = ((LocalWorkspaceProjectionRow)accepted.Session.Session) with
                {
                    LabelText = new string('a', 8_388_609 - baselineLength),
                },
            },
        };
        var error = Assert.Throws<LocalMonitorV1SessionDetailException>(() =>
            LocalMonitorV1SessionDetailApplication.SerializeSummary(rejected));
        Assert.Equal("workspace_too_large", error.Error);
    }

    private static string Revision(LocalRepositorySessionDetailSnapshot snapshot) =>
        SqliteLocalRepositoryScopeSnapshotService.ComputeRevisionForTest(snapshot.Session, snapshot.Detail);

    private static LocalRepositorySessionDetailSnapshot Snapshot(string? label = null)
    {
        var none = new LocalWorkspaceFact<long>("not_observed", null);
        var zero = new LocalWorkspaceFact<long>("recorded", 0);
        var activity = new LocalWorkspaceActivityFacts(zero, zero, zero, zero, zero);
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 0, none, none, none, none, none, none, none, none);
        var row = new LocalWorkspaceProjectionRow(SessionId, 1, 1, label is null ? "not_observed" : "recorded", label, "completed", "full",
            new("not_observed", []), new("not_observed", []), activity, tokens,
            "missing", null, null, null, null, [], "session:v1");
        var session = new LocalRepositoryScopeSessionSnapshot(SessionId, row, 1,
            LocalRepositoryScopeAssignmentState.Assigned, LocalRepositoryScopeAssignmentAuthority.Manual,
            RepositoryId, [RepositoryId], true, true, true, LocalArchiveState.Active, 1, true, null, 1);
        var detail = new LocalWorkspaceSessionDetailContribution([], [], [], [], [], [], null, null,
            "run-event-span-node-edge-skill-retention-raw:v1", "generation:v1");
        return new(session, detail, string.Empty);
    }

    private const string SessionId = "018f0000-0000-7000-8000-000000000001";
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000002";

    private sealed class RealFixture : IDisposable
    {
        private readonly MonitorTempDirectory temp;
        private readonly RunningMonitorHost host;
        private readonly ILocalRepositorySessionDetailSnapshotService service;
        private readonly SqliteLocalArchiveStore archive;
        private readonly LocalRepositoryCatalogApplication catalog;
        private readonly SkillInvocationV2RegistryProviderV1 registry;
        private RealFixture(MonitorTempDirectory temp, RunningMonitorHost host, string sessionId) { this.temp=temp;this.host=host;SessionId=sessionId;service=host.Services.GetRequiredService<ILocalRepositorySessionDetailSnapshotService>();archive=host.Services.GetRequiredService<SqliteLocalArchiveStore>();catalog=host.Services.GetRequiredService<LocalRepositoryCatalogApplication>();registry=host.Services.GetRequiredService<SkillInvocationV2RegistryProviderV1>(); }
        internal string SessionId { get; }
        internal HttpClient Client => host.Client;
        internal static async Task<RealFixture> CreateAsync()
        {
            var temp=new MonitorTempDirectory();var id=AlertCenterRouteTests.SeedPersistedTraceAndSession(temp,"00000000000000000000000000000001",true).ToString("D");var host=await MonitorTestHost.StartAsync(temp);
            using var c=Open(temp.DatabasePath);LocalWorkspaceProjectionSchemaV1.Ensure(c,DateTimeOffset.UnixEpoch);using var t=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT OR IGNORE INTO session_repository_assignment_revisions(session_id,revision,updated_at) VALUES($session,0,'2026-08-26T00:00:00.0000000+00:00'); INSERT OR IGNORE INTO session_event_content(event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token) SELECT event_id,'json','{}','2026-08-26T00:00:00.0000000+00:00','2030-08-27T00:00:00.0000000+00:00',randomblob(32) FROM session_events WHERE session_id=$session ORDER BY event_id LIMIT 1; INSERT OR IGNORE INTO local_workspace_node_edges(node_id,related_node_id,relation_kind,relationship_authority,source_ordinal) SELECT MIN(node_id),MAX(node_id),'retry','explicit',0 FROM local_workspace_nodes WHERE session_id=$session HAVING COUNT(*)>1;";q.Parameters.AddWithValue("$session",id);q.ExecuteNonQuery();t.Commit();return new(temp,host,id);
        }
        internal ValueTask<LocalRepositorySessionDetailSnapshot> ReadSummaryAsync()=>service.ReadDetailAsync(new(LocalRepositorySessionDetailRequestKind.Summary,SessionId),CancellationToken.None);
        internal string Archive(LocalArchiveTargetKind kind,string id,long revision,LocalArchiveAction action)
        {
            var result=archive.Mutate(action,kind,[new(id,revision)],success=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(success),CancellationToken.None);
            Assert.Null(result.Error);return System.Text.Encoding.UTF8.GetString(result.Success!.Entity.Span);
        }
        internal async Task<string> CreateAndAssignRepositoryAsync()
        {
            Execute("DELETE FROM session_repository_assignment_revisions WHERE session_id=$session;",false);
            var create=Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedCreate>>(catalog.PrepareCreate(new("Revision authority",null)));
            LocalRepositoryMutationRepository? repository=null;
            var created=await catalog.ExecutePreparedAsync(create.Prepared,OperationKey('a'),value=>{repository=value;return LocalRepositoryJson.WriteRepository(201,value);},CancellationToken.None);
            Assert.IsType<LocalRepositoryMutationSucceeded>(created);
            var assign=Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(catalog.PrepareSessionAction(new(SessionId,0,"assign",repository!.RepositoryId)));
            var assigned=await catalog.ExecutePreparedAsync(assign.Prepared,OperationKey('b'),LocalRepositoryJson.WriteAssignment,CancellationToken.None);
            Assert.IsType<LocalRepositoryMutationSucceeded>(assigned);
            return repository.RepositoryId;
        }
        internal async Task AssertStaleRoutesAsync(LocalRepositorySessionDetailSnapshot old)
        {
            var absent="ffffffffffffffffffffffffffffffff";
            foreach(var path in new[]{
                $"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={old.WorkspaceRevision}&execution_id=018f0000-0000-7000-8000-{absent[..12]}",
                $"/api/local-monitor/v1/sessions/{SessionId}/nodes/node-{absent}?workspace_revision={old.WorkspaceRevision}"})
            {
                using var response=await Client.GetAsync(path);var body=await response.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Equal("{\"error\":\"workspace_snapshot_stale\"}",body);
                Assert.DoesNotContain(absent,body,StringComparison.Ordinal);
                Assert.DoesNotContain(old.Detail.Nodes[0].NodeId,body,StringComparison.Ordinal);
            }
        }
        internal void PublishRegistryGeneration()=>registry.PublishGeneration(SkillInvocationV2ArtifactRegistry.Load());
        internal int Execute(string sql,bool refresh){using var c=Open(temp.DatabasePath);using var t=c.BeginTransaction();using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;q.Parameters.AddWithValue("$session",SessionId);var n=q.ExecuteNonQuery();if(refresh)LocalWorkspaceProjectionStore.Refresh(c,t,DateTimeOffset.UnixEpoch,FixedSkillRegistryGenerationAuthority.Load());t.Commit();return n;}
        private static SqliteConnection Open(string path){var c=new SqliteConnection($"Data Source={path};Pooling=False");c.Open();return c;}
        private static string OperationKey(char value)=>"lrc1_"+new string(value,42)+"A";
        public void Dispose(){host.DisposeAsync().AsTask().GetAwaiter().GetResult();temp.Dispose();}
    }
}

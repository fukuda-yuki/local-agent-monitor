using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Affected")]
public sealed class LocalAiProductionStackTests
{
    private const string SessionId="018f0000-0000-7000-8000-000000000001";
    private const string RunA="018f0000-0000-7000-8000-000000000010";
    private const string RunB="018f0000-0000-7000-8000-000000000020";

    [Fact]
    public void ResultEnvelope_ComposesExactComparisonScope()
    {
        const string repositoryId="0198f5c0-1b89-7d41-8c2f-4ecba0b54431",comparisonId="0198f5c0-1b89-7d41-8c2f-4ecba0b54432",snapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54433";
        var snapshot=new LocalAiSnapshotProjectionV1(snapshotId,"comparison",null,null,comparisonId,"revision","{}"u8.ToArray(),"{\"evidence_refs\":[]}"u8.ToArray(),new string('a',64),new HashSet<string>(),RepositoryId:repositoryId,ComparisonId:comparisonId,ExpiresAt:DateTimeOffset.Parse("2026-08-30T04:00:00Z"));
        var run=new LocalAiRunStatusV1("0198f5c0-1b89-7d41-8c2f-4ecba0b54434","running","comparison",null,null,null,RequestedAt:"2026-08-30T01:00:00.0000000+00:00",StartedAt:"2026-08-30T01:00:01.0000000+00:00",Model:"model",ConfigurationSha256:new string('a',64),PromptTemplateVersion:"template",RepositoryId:repositoryId,ComparisonId:comparisonId);

        using var document=JsonDocument.Parse(LocalAiResultEnvelopeV1.Compose("{\"summary\":\"s\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[]}"u8.ToArray(),snapshot,run,DateTimeOffset.Parse("2026-08-30T01:00:02Z")));

        Assert.Equal(new[]{"anchor_id","comparison_id","kind","repository_id"},document.RootElement.GetProperty("scope").EnumerateObject().Select(item=>item.Name).Order().ToArray());
        Assert.Null(snapshot.SessionId);
        Assert.Equal(comparisonId,document.RootElement.GetProperty("scope").GetProperty("comparison_id").GetString());
    }

    [Fact]
    public void AcceptedRepositorySnapshot_RestartsWithExactExpiryAndRejectsWrongScope()
    {
        using var temp=new MonitorTempDirectory{TimeProvider=new MutableTimeProvider(DateTimeOffset.Parse("2026-08-31T00:00:00Z"))};
        var repository=SqliteLocalAiRunRepositoryV1.Create(temp.DatabasePath,"model",new string('a',64),temp.TimeProvider);
        var snapshotId=Guid.CreateVersion7().ToString();var repositoryId=Guid.CreateVersion7().ToString();var expires=DateTimeOffset.Parse("2026-08-31T01:00:00Z");
        var payload="{\"members\":[]}"u8.ToArray();var index="{\"evidence_refs\":[]}"u8.ToArray();
        var accepted=new LocalAiSnapshotProjectionV1(snapshotId,"repository_selection",null,null,repositoryId,"revision",payload,index,
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload)),new HashSet<string>(),RepositoryId:repositoryId,ExpiresAt:expires);
        repository.StoreAccepted(accepted);

        var restarted=SqliteLocalAiRunRepositoryV1.Create(temp.DatabasePath,"model",new string('a',64),temp.TimeProvider).ReadAccepted(snapshotId);
        Assert.NotNull(restarted);Assert.Equal(expires,restarted!.ExpiresAt);Assert.Equal(accepted.PayloadSha256,restarted.PayloadSha256);
        var comparisonId=Guid.CreateVersion7().ToString();var comparisonSnapshotId=Guid.CreateVersion7().ToString();
        new LocalAiAnalysisStoreV1(temp.DatabasePath,timeProvider:temp.TimeProvider).InsertSnapshot(new(comparisonSnapshotId,"comparison",null,null,comparisonId,payload,index,repositoryId,comparisonId,expires));
        Assert.Null(repository.ReadAccepted(comparisonSnapshotId));
    }

    [Fact]
    public async Task ProductionHttpRepositoryPreviewPersistsAcrossRestartAndRunsFrozenSelectedEvidence()
    {
        using var temp=new MonitorTempDirectory();const string repositoryId="0198f5c0-1b89-7d41-8c2f-4ecba0b54431";
        var authority=FixedSkillRegistryGenerationAuthority.Load();
        var scope=new SqliteLocalRepositoryScopeSnapshotService(temp.DatabasePath,new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider,registryAuthority:authority),SqliteLocalArchiveFactSnapshotContributor.Instance,new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority:authority,timeProvider:temp.TimeProvider),skillRegistryAuthority:authority,timeProvider:temp.TimeProvider);
        var historical=new SqliteHistoricalEvidenceSnapshotSourceV1(temp.DatabasePath,new SqliteSessionStore(temp.DatabasePath));
        var repository=SqliteLocalAiRunRepositoryV1.Create(temp.DatabasePath,"model-test",new string('a',64),temp.TimeProvider);
        var firstApplication=new LocalAiAnalysisApplicationV1(_=>ValueTask.FromResult(true),scope,repository,new CapturingProvider(),timeProvider:temp.TimeProvider,repositories:new LocalAiRepositorySnapshotAdapterV1(scope,scope,historical,temp.TimeProvider));
        await using(var first=await MonitorTestHost.StartAsync(temp,testOptions:new MonitorHostTestOptions{LocalRepositoryScopeSnapshotService=scope,HistoricalEvidenceSource=historical,LocalAiAnalysisApplication=firstApplication,TimeProvider=temp.TimeProvider}))
        {
            LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(temp.DatabasePath,SessionId,RunA,RunB);
            using(var connection=Open(temp.DatabasePath))
            {
                using var command=connection.CreateCommand();command.CommandText="INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) VALUES($repository,'repository',1,$at,$at); INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at) VALUES($session,'assigned',$repository,1,$at); INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at) VALUES($session,1,$at);";
                command.Parameters.AddWithValue("$repository",repositoryId);command.Parameters.AddWithValue("$session",SessionId);command.Parameters.AddWithValue("$at",temp.TimeProvider.GetUtcNow().ToString("O"));command.ExecuteNonQuery();
            }
            var previewBody=JsonSerializer.Serialize(new{schema_version="local-ai-repository-preview.request.v1",repository_id=repositoryId,selection=new{kind="explicit",archive_scope="active_only",session_ids=new[]{SessionId}}});
            using var response=await first.Client.SendAsync(Post("/api/local-monitor/v1/ai/repository-preview",previewBody));
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());var snapshotId=json.RootElement.GetProperty("snapshot_id").GetString()!;var hash=json.RootElement.GetProperty("payload_sha256").GetString()!;
            var previewSession=json.RootElement.GetProperty("included")[0];
            Assert.Equal(new[]{"state","values"},previewSession.GetProperty("source").EnumerateObject().Select(item=>item.Name));
            Assert.Equal(new[]{"state","values"},previewSession.GetProperty("model").EnumerateObject().Select(item=>item.Name));
            Assert.Equal("not_captured",previewSession.GetProperty("content_state").GetString());
            using(var locked=Open(temp.DatabasePath)){using(var begin=locked.CreateCommand()){begin.CommandText="BEGIN EXCLUSIVE;";begin.ExecuteNonQuery();}var runBody=JsonSerializer.Serialize(new{schema_version="local-ai-repository-run.request.v1",snapshot_id=snapshotId,payload_sha256=hash,timeout_seconds=60});using var busy=await first.Client.SendAsync(Post("/api/local-monitor/v1/ai/repository-runs",runBody));Assert.Equal(HttpStatusCode.ServiceUnavailable,busy.StatusCode);Assert.Equal("{\"error\":\"persistence_busy\"}",await busy.Content.ReadAsStringAsync());using var rollback=locked.CreateCommand();rollback.CommandText="ROLLBACK;";rollback.ExecuteNonQuery();}
            var provider=new RepositoryRawProvider();var restartedRepository=SqliteLocalAiRunRepositoryV1.Create(temp.DatabasePath,"model-test",new string('a',64),temp.TimeProvider);
            var restartedApplication=new LocalAiAnalysisApplicationV1(_=>ValueTask.FromResult(true),scope,restartedRepository,provider,static (_,_,_)=>ValueTask.FromResult("raw-safe"u8.ToArray()),timeProvider:temp.TimeProvider,repositories:new LocalAiRepositorySnapshotAdapterV1(scope,scope,historical,temp.TimeProvider));
            var start=await restartedApplication.StartRepositoryAsync(new(snapshotId,hash,60),CancellationToken.None);Assert.Null(start.ErrorCode);Assert.NotNull(start.RunId);
            var status=await Poll(first.Client,$"/api/local-monitor/v1/ai/runs/{start.RunId}");Assert.Equal("succeeded",status.GetProperty("state").GetString());
            Assert.NotNull(provider.Request);var request=provider.Request!;var payload=Encoding.UTF8.GetString(request.Snapshot.PayloadCanonicalJson);
            Assert.Contains("repository_safe_evidence",payload,StringComparison.Ordinal);Assert.Contains($"/sessions/{SessionId}?node=",payload,StringComparison.Ordinal);Assert.DoesNotContain("\"citation_ref\":\"node-",payload,StringComparison.Ordinal);
            Assert.Equal(1,provider.Count);Assert.True(provider.PrefixedReadSucceeded);Assert.True(provider.UnprefixedReadRejected);Assert.Equal(provider.IndexedLocation,status.GetProperty("result").GetProperty("findings")[0].GetProperty("evidence_refs")[0].GetString());
            using(var connection=Open(temp.DatabasePath))
            {
                using var mutate=connection.CreateCommand();mutate.CommandText="UPDATE session_repository_manual_overrides SET revision=2 WHERE session_id=$session; UPDATE session_repository_assignment_revisions SET revision=2 WHERE session_id=$session;";mutate.Parameters.AddWithValue("$session",SessionId);mutate.ExecuteNonQuery();
            }
            Assert.Equal("stale_snapshot",(await restartedApplication.StartRepositoryAsync(new(snapshotId,hash,60),CancellationToken.None)).ErrorCode);Assert.Equal(1,provider.Count);
            using(var connection=Open(temp.DatabasePath)){using var restore=connection.CreateCommand();restore.CommandText="UPDATE session_repository_manual_overrides SET revision=1 WHERE session_id=$session; UPDATE session_repository_assignment_revisions SET revision=1 WHERE session_id=$session; UPDATE session_native_ids SET native_session_id=native_session_id||'-changed' WHERE session_id=$session;";restore.Parameters.AddWithValue("$session",SessionId);restore.ExecuteNonQuery();}
            Assert.Equal("stale_snapshot",(await restartedApplication.StartRepositoryAsync(new(snapshotId,hash,60),CancellationToken.None)).ErrorCode);Assert.Equal(1,provider.Count);
        }
    }

    [Fact]
    public async Task ProductionHttpRepositoryFilterOverTwoHundredReturnsScopeTooLargeWithoutSnapshot()
    {
        using var temp=new MonitorTempDirectory();var repositoryId=Guid.CreateVersion7().ToString();var scope=new OversizedFilterScope(repositoryId);
        var runs=SqliteLocalAiRunRepositoryV1.Create(temp.DatabasePath,"model-test",new string('a',64),temp.TimeProvider);var adapter=new LocalAiRepositorySnapshotAdapterV1(scope,new ThrowProjection(),new ThrowHistorical(),temp.TimeProvider);
        var application=new LocalAiAnalysisApplicationV1(_=>ValueTask.FromResult(true),new ThrowProjection(),runs,new CapturingProvider(),timeProvider:temp.TimeProvider,repositories:adapter);
        await using var host=await MonitorTestHost.StartAsync(temp,testOptions:new MonitorHostTestOptions{LocalAiAnalysisApplication=application,TimeProvider=temp.TimeProvider});
        var filter=new{schema_version="local-monitor-session-search.request.v1",scope="repository",repository_id=repositoryId,archive_scope="active_only",from=(string?)null,to=(string?)null,source=Array.Empty<string>(),model=Array.Empty<string>(),status=Array.Empty<string>(),has_skill=(bool?)null,has_subagent=(bool?)null,has_error=(bool?)null,has_retry=(bool?)null,q=(string?)null,cursor=(string?)null,limit=(int?)null};
        using var response=await host.Client.SendAsync(Post("/api/local-monitor/v1/ai/repository-preview",JsonSerializer.Serialize(new{schema_version="local-ai-repository-preview.request.v1",repository_id=repositoryId,selection=new{kind="filter",request=filter}})));
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);Assert.Equal("{\"error\":\"scope_too_large\"}",await response.Content.ReadAsStringAsync());using var connection=Open(temp.DatabasePath);using var count=connection.CreateCommand();count.CommandText="SELECT COUNT(*) FROM local_ai_snapshots;";Assert.Equal(0L,(long)count.ExecuteScalar()!);
    }


    [Fact]
    public async Task ProductionHostRealScopeAndStoreServeSessionNodeAndReportLifecycle()
    {
        using var temp=new MonitorTempDirectory();
        var authority=FixedSkillRegistryGenerationAuthority.Load();
        var scope=new SqliteLocalRepositoryScopeSnapshotService(temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(temp.TimeProvider,registryAuthority:authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority:authority,timeProvider:temp.TimeProvider),
            skillRegistryAuthority:authority,timeProvider:temp.TimeProvider);
        var retention=new RetentionCatalogStore(RetentionCatalogContext.InitializeNewOwnedDatabase(temp.DatabasePath,temp.TimeProvider),temp.TimeProvider);
        var repository=new SqliteLocalAiRunRepositoryV1(temp.DatabasePath,"model-test",new string('a',64),temp.TimeProvider,retention);
        var provider=new SequenceProvider("session-one","!zero","session-two",null,"node-one","!invalid","!block","!timeout");
        var application=new LocalAiAnalysisApplicationV1(_=>ValueTask.FromResult(true),scope,repository,provider,timeProvider:temp.TimeProvider);
        await using var host=await MonitorTestHost.StartAsync(temp,testOptions:new MonitorHostTestOptions{
            LocalRepositoryScopeSnapshotService=scope,LocalAiAnalysisApplication=application,TimeProvider=temp.TimeProvider});
        using(var schema=Open(temp.DatabasePath))LocalAiAnalysisSchemaV1.Ensure(schema);
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(temp.DatabasePath,SessionId,RunA,RunB);

        var first=await Start(host.Client,"/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{SessionId}}"}""");
        var firstStatus=await Poll(host.Client,$"/api/local-monitor/v1/ai/session-runs/{first}");
        Assert.Equal("succeeded",firstStatus.GetProperty("state").GetString());
        Assert.Equal("session-one",firstStatus.GetProperty("result").GetProperty("summary").GetString());
        var generic=await Get(host.Client,$"/api/local-monitor/v1/ai/runs/{first}");
        Assert.Equal("session-one",generic.GetProperty("result").GetProperty("summary").GetString());

        var zero=await Start(host.Client,"/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{SessionId}}"}""");
        Assert.Equal("zero_findings",(await Poll(host.Client,$"/api/local-monitor/v1/ai/session-runs/{zero}")).GetProperty("state").GetString());
        var second=await Start(host.Client,"/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{SessionId}}"}""");
        Assert.NotEqual(first,second);await Poll(host.Client,$"/api/local-monitor/v1/ai/session-runs/{second}");
        var failed=await Start(host.Client,"/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{SessionId}}"}""");
        Assert.Equal("provider_failed",(await Poll(host.Client,$"/api/local-monitor/v1/ai/session-runs/{failed}")).GetProperty("state").GetString());

        using(var connection=Open(temp.DatabasePath))
        {
            using var command=connection.CreateCommand();command.CommandText="UPDATE session_events SET type='event.changed' WHERE event_id='018f0000-0000-7000-8000-000000000025';";command.ExecuteNonQuery();
        }
        var reports=await Get(host.Client,$"/api/local-monitor/v1/ai/sessions/{SessionId}/reports");
        var items=reports.GetProperty("reports").EnumerateArray().ToArray();
        Assert.Equal(3,items.Length);Assert.DoesNotContain(items,item=>item.GetProperty("run_id").GetString()==failed);
        Assert.Contains(items,item=>item.GetProperty("run_id").GetString()==zero&&item.GetProperty("state").GetString()=="zero_findings");
        Assert.All(items,item=>Assert.Equal("retained",item.GetProperty("content_state").GetString()));
        Assert.Contains(items,item=>item.GetProperty("snapshot_changed").GetBoolean());

        string nodeId;using(var connection=Open(temp.DatabasePath))
        {
            using var command=connection.CreateCommand();command.CommandText="SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND kind='tool' ORDER BY node_id LIMIT 1;";command.Parameters.AddWithValue("$session",SessionId);nodeId=(string)command.ExecuteScalar()!;
            var sessionColumns=new List<string>();using var pragma=connection.CreateCommand();pragma.CommandText="PRAGMA table_info(sessions);";
            using(var reader=pragma.ExecuteReader())while(reader.Read())sessionColumns.Add(reader.GetString(1));
            command.CommandText=$"INSERT INTO sessions({string.Join(',',sessionColumns)}) SELECT $other,{string.Join(',',sessionColumns.Skip(1))} FROM sessions WHERE session_id=$session;";
            command.Parameters.AddWithValue("$other","018f0000-0000-7000-8000-000000000002");command.ExecuteNonQuery();
        }
        using(var malformed=Post("/api/local-monitor/v1/ai/node-runs",$$"""{"session_id":"{{SessionId}}","node_id":"node-anchor"}"""))
        using(var malformedResponse=await host.Client.SendAsync(malformed))Assert.Equal(HttpStatusCode.BadRequest,malformedResponse.StatusCode);
        foreach(var body in new[]{
            $$"""{"session_id":"{{SessionId}}","node_id":"node-00000000000000000000000000000000"}""",
            $$"""{"session_id":"018f0000-0000-7000-8000-000000000002","node_id":"{{nodeId}}"}"""})
        {
            using var missingNode=Post("/api/local-monitor/v1/ai/node-runs",body);using var missingNodeResponse=await host.Client.SendAsync(missingNode);
            Assert.Equal(HttpStatusCode.NotFound,missingNodeResponse.StatusCode);
            Assert.Equal("{\"error\":\"node_not_found\"}",await missingNodeResponse.Content.ReadAsStringAsync());
        }
        using(var noRejectedRows=Open(temp.DatabasePath)){using var countRejected=noRejectedRows.CreateCommand();countRejected.CommandText="SELECT COUNT(*) FROM local_ai_runs WHERE scope_kind='node';";Assert.Equal(0L,(long)countRejected.ExecuteScalar()!);}
        var nodeRun=await Start(host.Client,"/api/local-monitor/v1/ai/node-runs",$$"""{"session_id":"{{SessionId}}","node_id":"{{nodeId}}"}""");
        var nodeStatus=await Poll(host.Client,$"/api/local-monitor/v1/ai/node-runs/{nodeRun}");
        Assert.Equal("node-one",nodeStatus.GetProperty("result").GetProperty("summary").GetString());
        Assert.Equal("node-one",(await Get(host.Client,$"/api/local-monitor/v1/ai/runs/{nodeRun}")).GetProperty("result").GetProperty("summary").GetString());
        var afterNode=await Get(host.Client,$"/api/local-monitor/v1/ai/sessions/{SessionId}/reports");
        Assert.DoesNotContain(afterNode.GetProperty("reports").EnumerateArray(),item=>item.GetProperty("run_id").GetString()==nodeRun);
        var invalid=await Start(host.Client,"/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{SessionId}}"}""");
        Assert.Equal("invalid_result",(await Poll(host.Client,$"/api/local-monitor/v1/ai/runs/{invalid}")).GetProperty("state").GetString());

        using(var expired=Open(temp.DatabasePath)){using var command=expired.CreateCommand();command.CommandText="DELETE FROM local_ai_results WHERE run_id=$run;";command.Parameters.AddWithValue("$run",first);command.ExecuteNonQuery();}
        var expiredReports=await Get(host.Client,$"/api/local-monitor/v1/ai/sessions/{SessionId}/reports");
        var expiredItem=Assert.Single(expiredReports.GetProperty("reports").EnumerateArray(),item=>item.GetProperty("run_id").GetString()==first);
        Assert.Equal("expired",expiredItem.GetProperty("content_state").GetString());Assert.Equal(JsonValueKind.Null,expiredItem.GetProperty("result").ValueKind);

        var activeStart=Start(host.Client,"/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{SessionId}}"}""");
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));var active=await activeStart;
        using var activeCancel=Post($"/api/local-monitor/v1/ai/runs/{active}/cancel","{}");using var activeCancelResponse=await host.Client.SendAsync(activeCancel);
        Assert.Equal(HttpStatusCode.OK,activeCancelResponse.StatusCode);provider.Release.TrySetResult();
        Assert.Equal("canceled",(await Poll(host.Client,$"/api/local-monitor/v1/ai/runs/{active}")).GetProperty("state").GetString());
        var timedOut=await Start(host.Client,"/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{SessionId}}","timeout_seconds":1}""");
        Assert.Equal("timed_out",(await Poll(host.Client,$"/api/local-monitor/v1/ai/runs/{timedOut}")).GetProperty("state").GetString());

        using var cancel=Post($"/api/local-monitor/v1/ai/runs/{nodeRun}/cancel","{}");
        using var cancelResponse=await host.Client.SendAsync(cancel);Assert.Equal(HttpStatusCode.Conflict,cancelResponse.StatusCode);
        Assert.All(new[]{"/api/local-monitor/v1/ai/session-runs","/api/local-monitor/v1/ai/node-runs",
            "/api/local-monitor/v1/ai/session-runs/{runId}","/api/local-monitor/v1/ai/node-runs/{runId}",
            "/api/local-monitor/v1/ai/runs/{runId}","/api/local-monitor/v1/ai/runs/{runId}/cancel",
            "/api/local-monitor/v1/ai/sessions/{sessionId}/reports"},pattern=>Assert.Contains(pattern,host.RoutePatterns));
        Assert.Equal(HttpStatusCode.NotFound,(await host.Client.GetAsync($"/api/local-monitor/v1/ai/session-runs/{nodeRun}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await host.Client.GetAsync($"/api/local-monitor/v1/ai/node-runs/{first}")).StatusCode);
        using(var wrongMethod=new HttpRequestMessage(HttpMethod.Put,"/api/local-monitor/v1/ai/runs/not-a-uuid"))
        using(var wrongMethodResponse=await host.Client.SendAsync(wrongMethod))Assert.Equal(HttpStatusCode.MethodNotAllowed,wrongMethodResponse.StatusCode);
        const string missing="018f0000-0000-7000-8000-000000000099";
        using(var missingRequest=Post("/api/local-monitor/v1/ai/session-runs",$$"""{"session_id":"{{missing}}"}"""))
        {missingRequest.Content!.Headers.ContentType=System.Net.Http.Headers.MediaTypeHeaderValue.Parse("Application/Json");using var missingResponse=await host.Client.SendAsync(missingRequest);Assert.Equal(HttpStatusCode.NotFound,missingResponse.StatusCode);}
        using(var missingReports=await host.Client.GetAsync($"/api/local-monitor/v1/ai/sessions/{missing}/reports"))Assert.Equal(HttpStatusCode.NotFound,missingReports.StatusCode);
        using(var oversized=Post("/api/local-monitor/v1/ai/session-runs",new string('x',16_385)))
        {oversized.Content=new StringContent(new string('x',16_385),Encoding.UTF8,"text/plain");using var oversizedResponse=await host.Client.SendAsync(oversized);Assert.Equal(HttpStatusCode.RequestEntityTooLarge,oversizedResponse.StatusCode);}

        using var reopened=Open(temp.DatabasePath);using var count=reopened.CreateCommand();
        count.CommandText="SELECT COUNT(*) FROM local_ai_runs WHERE state='succeeded';";Assert.Equal(3L,(long)count.ExecuteScalar()!);
    }

    private static async Task<string> Start(HttpClient client,string path,string body)
    {using var response=await client.SendAsync(Post(path,body));Assert.Equal(HttpStatusCode.Created,response.StatusCode);using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());return json.RootElement.GetProperty("run_id").GetString()!;}
    private static async Task<JsonElement> Poll(HttpClient client,string path)
    {for(var i=0;i<50;i++){var value=await Get(client,path);if(value.GetProperty("state").GetString()!="running")return value;await Task.Delay(10);}throw new TimeoutException();}
    private static async Task<JsonElement> Get(HttpClient client,string path)
    {using var response=await client.GetAsync(path);Assert.Equal(HttpStatusCode.OK,response.StatusCode);using var json=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());return json.RootElement.Clone();}
    private static HttpRequestMessage Post(string path,string body)
    {var request=new HttpRequestMessage(HttpMethod.Post,path){Content=new StringContent(body,Encoding.UTF8,"application/json")};request.Headers.Add("x-monitor-csrf","local-monitor");request.Headers.Add("Sec-Fetch-Site","same-origin");return request;}
    private static SqliteConnection Open(string path){var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());connection.Open();return connection;}
    private sealed class SequenceProvider(params string?[] summaries):ILocalAiProviderAdapterV1
    {private int index;internal TaskCompletionSource Entered{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);internal TaskCompletionSource Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request,CancellationToken token)
        {var summary=summaries[Interlocked.Increment(ref index)-1];if(summary is null)return LocalAiProviderOutcomeV1.Failed();
            if(summary=="!block"){Entered.TrySetResult();await Release.Task;token.ThrowIfCancellationRequested();}
            if(summary=="!timeout"){await Task.Delay(Timeout.InfiniteTimeSpan,token);throw new InvalidOperationException();}
            if(summary=="!invalid")return LocalAiProviderOutcomeV1.Complete("{}"u8.ToArray());
            if(summary=="!zero")return LocalAiProviderOutcomeV1.Complete(Encoding.UTF8.GetBytes("{\"summary\":\"none\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[]}"));
            var evidence=request.Snapshot.EvidenceIdentifiers.First();var bytes=Encoding.UTF8.GetBytes($$"""{"summary":"{{summary}}","findings":[{"finding_id":"f-1","title":"title","explanation":"explanation","evidence_state":"supported","evidence_refs":["{{evidence}}"],"limitation":"none"}],"improvement_suggestions":[],"limitations":[]}""");
            return LocalAiProviderOutcomeV1.Complete(bytes);}}
    private sealed class CapturingProvider:ILocalAiProviderAdapterV1
    {public int Count{get;private set;}public LocalAiProviderRequestV1? Request{get;private set;}public ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request,CancellationToken token){Count++;Request=request;return ValueTask.FromResult(LocalAiProviderOutcomeV1.Complete("{\"summary\":\"none\",\"findings\":[],\"improvement_suggestions\":[],\"limitations\":[]}"u8.ToArray()));}}
    private sealed class RepositoryRawProvider:ILocalAiProviderAdapterV1
    {
        public int Count{get;private set;}public LocalAiProviderRequestV1? Request{get;private set;}public bool PrefixedReadSucceeded{get;private set;}public bool UnprefixedReadRejected{get;private set;}public string? IndexedLocation{get;private set;}
        public async ValueTask<LocalAiProviderOutcomeV1> ExecuteAsync(LocalAiProviderRequestV1 request,CancellationToken token)
        {
            Count++;Request=request;using var payload=JsonDocument.Parse(request.Snapshot.PayloadCanonicalJson);var raw=payload.RootElement.GetProperty("raw_content")[0];var handle=raw.GetProperty("evidence_id").GetString()!;IndexedLocation=raw.GetProperty("citation_ref").GetString()!;PrefixedReadSucceeded=Encoding.UTF8.GetString(await request.RawReads.ReadAsync(handle,token))=="raw-safe";
            try{await request.RawReads.ReadAsync(handle[(handle.IndexOf(':')+1)..],token);}catch(LocalAiRawReadException exception) when(exception.Message=="raw_scope_rejected"){UnprefixedReadRejected=true;}
            return LocalAiProviderOutcomeV1.Complete(JsonSerializer.SerializeToUtf8Bytes(new{summary="supported",findings=new[]{new{finding_id="f-1",title="title",explanation="explanation",evidence_state="supported",evidence_refs=new[]{IndexedLocation},limitation="none"}},improvement_suggestions=Array.Empty<object>(),limitations=Array.Empty<object>()}));
        }
    }
    private sealed class ThrowProjection:ILocalAiSnapshotProjectionServiceV1
    {public ValueTask<LocalAiSnapshotProjectionV1> ReadSessionAsync(string id,CancellationToken token)=>throw new InvalidOperationException();public ValueTask<LocalAiSnapshotProjectionV1> ReadNodeAsync(string id,string node,CancellationToken token)=>throw new InvalidOperationException();public ValueTask<bool> IsCurrentAsync(LocalAiSnapshotProjectionV1 value,CancellationToken token)=>throw new InvalidOperationException();}
    private sealed class ThrowHistorical:IHistoricalEvidenceSnapshotSourceV1{public ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(HistoricalEvidenceSelectionV1 selection,CancellationToken token)=>throw new InvalidOperationException();}
    private sealed class OversizedFilterScope(string repositoryId):ILocalRepositoryScopeSnapshotService
    {
        public ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(LocalRepositoryScopeRequest request,CancellationToken token)
        {
            var fact=new LocalWorkspaceFact<long>("not_observed",null);var rows=Enumerable.Range(0,201).Select(index=>{var id=Guid.CreateVersion7().ToString();var projection=new LocalWorkspaceProjectionRow(id,0,index,"unavailable",null,"completed","rich",new("recorded",[]),new("recorded",[]),new(fact,fact,fact,fact,fact),new("none","not_observed",0,0,fact,fact,fact,fact,fact,fact,fact,fact),"not_observed",null,null,null,null,[],"seed");return new LocalRepositoryScopeSessionSnapshot(id,projection,1,LocalRepositoryScopeAssignmentState.Assigned,LocalRepositoryScopeAssignmentAuthority.Manual,repositoryId,[],true,false,true,LocalArchiveState.Active,0,true,null,0);}).ToArray();
            return ValueTask.FromResult(new LocalRepositoryScopeSnapshot(request,[new(repositoryId,"repository",1,null,0,LocalArchiveState.Active,0)],rows));
        }
    }
}

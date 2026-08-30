using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Affected")]
public sealed class LocalAiProductionStackTests
{
    private const string SessionId="018f0000-0000-7000-8000-000000000001";
    private const string RunA="018f0000-0000-7000-8000-000000000010";
    private const string RunB="018f0000-0000-7000-8000-000000000020";

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
        {using var command=connection.CreateCommand();command.CommandText="SELECT node_id FROM local_workspace_nodes WHERE session_id=$session AND kind='tool' ORDER BY node_id LIMIT 1;";command.Parameters.AddWithValue("$session",SessionId);nodeId=(string)command.ExecuteScalar()!;}
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
}

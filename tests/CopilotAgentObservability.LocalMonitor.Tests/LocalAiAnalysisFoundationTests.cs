using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalAiAnalysisFoundationTests
{
    [Fact]
    public void Schema_MigratesExistingVersionTableAndRejectsMalformedCompleteInventory()
    {
        using var migration = new Database(); using var connection = migration.Open();
        Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('session',14);");
        LocalAiAnalysisSchemaV1.Ensure(connection); LocalAiAnalysisSchemaV1.Ensure(connection);
        Assert.Equal(1L, Scalar(connection, "SELECT version FROM schema_version WHERE component='local_ai_analysis';"));

        using var corrupt = new Database(); using var corruptConnection = corrupt.Open();
        Execute(corruptConnection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('local_ai_analysis',1); CREATE TABLE local_ai_snapshots(id TEXT); CREATE TABLE local_ai_runs(id TEXT); CREATE TABLE local_ai_results(id TEXT);");
        Assert.Throws<InvalidOperationException>(() => LocalAiAnalysisSchemaV1.Ensure(corruptConnection));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("newer")]
    public void Schema_RejectsPartialOrNewerAuthorityWithoutMutation(string shape)
    {
        using var database = new Database(); using var connection = database.Open();
        Execute(connection, shape == "partial" ? "CREATE TABLE local_ai_snapshots(id TEXT);" : "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('local_ai_analysis',2);");
        var before = Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema;");
        Assert.Throws<InvalidOperationException>(() => LocalAiAnalysisSchemaV1.Ensure(connection));
        Assert.Equal(before, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema;"));
    }

    [Fact]
    public void Snapshot_RequiresCanonicalUuidAndExactCanonicalEvidenceIndex()
    {
        using var database = new Database(); var store = database.Store(); var snapshot = Snapshot(); store.InsertSnapshot(snapshot); store.InsertSnapshot(snapshot);
        Assert.Throws<ArgumentException>(() => store.InsertSnapshot(snapshot with { SnapshotId = SnapshotId.ToUpperInvariant() }));
        Assert.Throws<ArgumentException>(() => store.InsertSnapshot(snapshot with { SnapshotId = "{" + SnapshotId + "}" }));
        Assert.Throws<InvalidOperationException>(() => store.InsertSnapshot(snapshot with { EvidenceIndexCanonicalJson = "{\"other\":[]}"u8.ToArray() }));
        Assert.Throws<InvalidOperationException>(() => store.InsertSnapshot(snapshot with { EvidenceIndexCanonicalJson = "{\"evidence_refs\":[\"ev-1\"],\"extra\":true}"u8.ToArray() }));
        Assert.Throws<InvalidOperationException>(() => store.InsertSnapshot(snapshot with { PayloadCanonicalJson = "{\"z\":1,\"a\":2}"u8.ToArray() }));
        using var connection = database.Open();
        Assert.Equal(PayloadHash, Text(connection, "SELECT payload_sha256 FROM local_ai_snapshots;"));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("{\"evidence_refs\":[\"ev-1\"]}"u8)), Text(connection, "SELECT evidence_index_sha256 FROM local_ai_snapshots;"));
        Assert.Throws<SqliteException>(() => Execute(connection, "UPDATE local_ai_snapshots SET anchor_id='changed';"));
    }

    [Fact]
    public void Run_PersistsProvenanceAndAtomicallyUpdatesLifecycleMetadata()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot()); var run = store.CreateRun(Request()); Assert.Equal(60, run.TimeoutSeconds);
        using (var connection = database.Open())
            Assert.Equal("github_copilot_sdk|synthetic-model|" + Hash64 + "|local-ai-analysis.prompt.v1|2026-08-30T01:00:00.0000000+00:00|||", Text(connection, "SELECT provider||'|'||model||'|'||configuration_sha256||'|'||prompt_template_version||'|'||requested_at||'|'||coalesce(started_at,'')||'|'||coalesce(completed_at,'')||'|'||coalesce(result_id,'') FROM local_ai_runs;"));
        store.TransitionRun(run.RunId, LocalAiRunStateV1.Running); Assert.Throws<ArgumentException>(() => store.TransitionRun(run.RunId, LocalAiRunStateV1.ProviderFailed, "arbitrary")); store.TransitionRun(run.RunId, LocalAiRunStateV1.ProviderFailed, "provider_failed");
        using var read = database.Open(); Assert.Equal(1L, Scalar(read, "SELECT COUNT(*) FROM local_ai_runs WHERE started_at IS NOT NULL AND completed_at IS NOT NULL AND error_code='provider_failed' AND result_id IS NULL;"));
        Assert.Throws<InvalidOperationException>(() => store.TransitionRun(run.RunId, LocalAiRunStateV1.Running));
    }

    [Fact]
    public void Validator_RequiresClosedNestedProvenanceAndSuggestionContract()
    {
        Assert.Equal(LocalAiResultValidationCodeV1.Valid, LocalAiResultValidatorV1.Validate(Result(), ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(Result(scope: "{}"), ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(Result(provenanceExtra: ",\"extra\":true"), ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(Result(targetKind: "repository"), ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(Result(configurationHash: "ABC"), ["ev-1"]).Code);
    }

    [Fact]
    public void Validator_AcceptsExactSizeBoundaryAndRejectsOverflowAndDepth17()
    {
        var baseResult = Result(); var exactSize = Result(summary: new string('x', "synthetic".Length + 1_048_576 - baseResult.Length));
        Assert.Equal(1_048_576, exactSize.Length); Assert.Equal(LocalAiResultValidationCodeV1.Valid, LocalAiResultValidatorV1.Validate(exactSize, ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.TooLarge, LocalAiResultValidatorV1.Validate(exactSize.Concat([(byte)' ']).ToArray(), ["ev-1"]).Code);
        var depth16 = Encoding.UTF8.GetBytes(new string('[', 16) + "0" + new string(']', 16));
        var depth17 = Encoding.UTF8.GetBytes("{\"scope\":" + new string('[', 17) + "0" + new string(']', 17) + "}");
        Assert.True(LocalAiCanonicalJsonV1.IsWithinDepthLimit(depth16));
        Assert.False(LocalAiCanonicalJsonV1.IsWithinDepthLimit(Encoding.UTF8.GetBytes(new string('[', 17) + "0" + new string(']', 17))));
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(depth17, ["ev-1"]).Code);
    }

    [Fact]
    public void Completion_ValidatesEvidenceAndStoresIndependentChecksumAndResultLink()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot());
        var invalid = store.CreateRun(Request()); store.TransitionRun(invalid.RunId, LocalAiRunStateV1.Running); Assert.Equal(LocalAiRunStateV1.InvalidEvidence, store.Complete(invalid.RunId, Result(evidenceRef: "missing")));
        var run = store.CreateRun(Request()); store.TransitionRun(run.RunId, LocalAiRunStateV1.Running); Assert.Equal(LocalAiRunStateV1.Succeeded, store.Complete(run.RunId, Result()));
        using var connection = database.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT r.result_id,x.result_id,x.result_json,x.result_sha256,r.completed_at FROM local_ai_runs r JOIN local_ai_results x ON x.run_id=r.run_id WHERE r.run_id=$id;"; command.Parameters.AddWithValue("$id", run.RunId);
        using var reader = command.ExecuteReader(); Assert.True(reader.Read()); Assert.Equal(reader.GetString(0), reader.GetString(1)); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData((byte[])reader[2])), reader.GetString(3)); Assert.False(reader.IsDBNull(4));
    }

    [Fact]
    public void Completion_RejectsResultProvenanceThatDoesNotMatchRunAndSnapshot()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot());
        var run = store.CreateRun(Request()); store.TransitionRun(run.RunId, LocalAiRunStateV1.Running);
        Assert.Equal(LocalAiRunStateV1.InvalidResult, store.Complete(run.RunId, Result(provider: "different_provider")));
    }

    [Fact]
    public void NodeSuccess_IsExcludedFromSessionHistory()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot(NodeSnapshotId, "node", "node-1"));
        var run = store.CreateRun(Request(NodeSnapshotId, "node", "node-1")); store.TransitionRun(run.RunId, LocalAiRunStateV1.Running);
        Assert.Equal(LocalAiRunStateV1.Succeeded, store.Complete(run.RunId, Result(scope: Scope("node", "node-1"), snapshotId: NodeSnapshotId)));
        Assert.Empty(store.GetSessionReports(SessionId, null, null).Items);
    }

    [Fact]
    public void Regeneration_UsesDistinctSnapshotRunAndResultIdentitiesAndPreservesReports()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot()); store.InsertSnapshot(Snapshot(RegeneratedSnapshotId));
        var first = Complete(store, Request(), Result()); var second = Complete(store, Request(RegeneratedSnapshotId), Result(snapshotId: RegeneratedSnapshotId), true);
        var reports = store.GetSessionReports(SessionId, null, null).Items; Assert.Equal(2, reports.Count); Assert.NotEqual(first.RunId, second.RunId); Assert.Equal(2, reports.Select(item => item.ResultId).Distinct().Count()); Assert.Equal(2L, database.Scalar("SELECT COUNT(DISTINCT snapshot_id) FROM local_ai_runs;"));
    }

    [Fact]
    public void History_UsesDefaultAndMaximumNewestFirstCursorPagingAndExcludesFailures()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot());
        var failed = store.CreateRun(Request()); store.TransitionRun(failed.RunId, LocalAiRunStateV1.Running); store.TransitionRun(failed.RunId, LocalAiRunStateV1.TimedOut, "timed_out");
        for (var index = 0; index < 104; index++) { Complete(store, Request(), Result()); Thread.Sleep(1); }
        var first = store.GetSessionReports(SessionId, null, null); Assert.Equal(20, first.Items.Count); Assert.NotNull(first.NextCursor); Assert.True(first.Items.Zip(first.Items.Skip(1), (left, right) => left.CreatedAt > right.CreatedAt).All(value => value));
        Assert.Equal(100, store.GetSessionReports(SessionId, 1000, null).Items.Count); Assert.Equal(84, store.GetSessionReports(SessionId, 100, first.NextCursor).Items.Count);
    }

    private static LocalAiRunV1 Complete(LocalAiAnalysisStoreV1 store, LocalAiRunRequestV1 request, byte[] result, bool zero = false) { var run=store.CreateRun(request); store.TransitionRun(run.RunId,LocalAiRunStateV1.Running); Assert.Equal(zero?LocalAiRunStateV1.ZeroFindings:LocalAiRunStateV1.Succeeded,store.Complete(run.RunId,zero?Result(snapshotId:request.SnapshotId,zero:true):result)); return run; }
    private static LocalAiSnapshotV1 Snapshot(string id=SnapshotId,string kind="session",string? node=null)=>new(id,kind,SessionId,node,node??SessionId,"{\"value\":1}"u8.ToArray(),"{\"evidence_refs\":[\"ev-1\"]}"u8.ToArray());
    private static LocalAiRunRequestV1 Request(string snapshotId=SnapshotId,string kind="session",string? node=null)=>new(snapshotId,kind,SessionId,node,"github_copilot_sdk","synthetic-model",Hash64,"local-ai-analysis.prompt.v1",DateTimeOffset.Parse("2026-08-30T01:00:00Z"),null);
    private static string Scope(string kind="session",string? node=null)=>$"{{\"anchor_id\":\"{node??SessionId}\",\"kind\":\"{kind}\",\"node_id\":{(node is null?"null":"\""+node+"\"")},\"session_id\":\"{SessionId}\"}}";
    private static byte[] Result(string evidenceRef="ev-1",bool zero=false,string? scope=null,string snapshotId=SnapshotId,string targetKind="skill",string configurationHash=Hash64,string summary="synthetic",string provenanceExtra="",string provider="github_copilot_sdk")=>Encoding.UTF8.GetBytes("{\"findings\":"+(zero?"[]":"[{\"evidence_refs\":[\""+evidenceRef+"\"],\"evidence_state\":\"supported\",\"explanation\":\"explanation\",\"finding_id\":\"f-1\",\"limitation\":\"none\",\"title\":\"title\"}]") + ",\"improvement_suggestions\":[{\"concrete_change\":\"change\",\"evidence_refs\":[\""+evidenceRef+"\"],\"expected_effect\":\"effect\",\"rationale\":\"reason\",\"risks_or_limitations\":\"risk\",\"suggestion_id\":\"s-1\",\"target_kind\":\""+targetKind+"\",\"target_label\":\"target\"}],\"limitations\":[],\"provenance\":{\"completed_at\":\"2026-08-30T01:00:02.0000000+00:00\",\"configuration_sha256\":\""+configurationHash+"\",\"coverage\":{\"content_available\":true,\"excluded\":0,\"included\":1},\"model\":\"synthetic-model\",\"prompt_template_version\":\"local-ai-analysis.prompt.v1\",\"provider\":\""+provider+"\",\"requested_at\":\"2026-08-30T01:00:00.0000000+00:00\",\"snapshot_id\":\""+snapshotId+"\",\"snapshot_sha256\":\""+PayloadHash+"\",\"started_at\":\"2026-08-30T01:00:01.0000000+00:00\""+provenanceExtra+"},\"scope\":"+(scope??Scope())+",\"snapshot\":{\"payload_sha256\":\""+PayloadHash+"\",\"snapshot_id\":\""+snapshotId+"\"},\"summary\":\""+summary+"\"}");
    private const string SnapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54410",RegeneratedSnapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54412",NodeSnapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54413",SessionId="0198f5c0-1b89-7d41-8c2f-4ecba0b54411",Hash64="1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly string PayloadHash=Convert.ToHexStringLower(SHA256.HashData("{\"value\":1}"u8));
    private static long Scalar(SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt64(q.ExecuteScalar());} private static string Text(SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return(string)q.ExecuteScalar()!;} private static void Execute(SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;q.ExecuteNonQuery();}
    private sealed class Database:IDisposable{private readonly string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"local-ai-"+Guid.NewGuid().ToString("N"));internal Database(){Directory.CreateDirectory(directory);Path=System.IO.Path.Combine(directory,"test.sqlite");}internal string Path{get;}internal SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path,Pooling=false}.ToString());c.Open();return c;}internal LocalAiAnalysisStoreV1 Store(){using var c=Open();LocalAiAnalysisSchemaV1.Ensure(c);return new(Path);}internal long Scalar(string sql){using var c=Open();return LocalAiAnalysisFoundationTests.Scalar(c,sql);}public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}}
}

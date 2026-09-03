using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalAiAnalysisFoundationTests
{
    [Theory]
    [InlineData("repository_selection", "0198f5c0-1b89-7d41-8c2f-4ecba0b54431", null)]
    [InlineData("comparison", "0198f5c0-1b89-7d41-8c2f-4ecba0b54431", "0198f5c0-1b89-7d41-8c2f-4ecba0b54432")]
    public void ResultScope_AdmitsExactRepositoryDiscriminants(string kind, string repositoryId, string? comparisonId)
    {
        var scope = comparisonId is null
            ? $"{{\"anchor_id\":\"{repositoryId}\",\"kind\":\"{kind}\",\"repository_id\":\"{repositoryId}\"}}"
            : $"{{\"anchor_id\":\"{comparisonId}\",\"comparison_id\":\"{comparisonId}\",\"kind\":\"{kind}\",\"repository_id\":\"{repositoryId}\"}}";

        Assert.Equal(LocalAiResultValidationCodeV1.Valid, LocalAiResultValidatorV1.Validate(Result(scope: scope), ["ev-1"]).Code);
    }

    [Theory]
    [InlineData("{\"anchor_id\":\"0198f5c0-1b89-7d41-8c2f-4ecba0b54431\",\"kind\":\"repository_selection\",\"repository_id\":\"not-canonical\"}")]
    [InlineData("{\"anchor_id\":\"0198f5c0-1b89-7d41-8c2f-4ecba0b54432\",\"comparison_id\":\"0198f5c0-1b89-7d41-8c2f-4ecba0b54432\",\"kind\":\"comparison\",\"repository_id\":\"0198f5c0-1b89-7d41-8c2f-4ecba0b54431\",\"session_id\":null}")]
    [InlineData("{\"anchor_id\":\"0198f5c0-1b89-7d41-8c2f-4ecba0b54432\",\"kind\":\"comparison\",\"repository_id\":\"0198f5c0-1b89-7d41-8c2f-4ecba0b54431\"}")]
    public void ResultScope_RejectsInvalidDiscriminatedShapes(string scope)
    {
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(Result(scope: scope), ["ev-1"]).Code);
    }

    [Fact]
    public void StoredResultWithoutSnapshotEvidence_SkipsOnlyMembershipResolution()
    {
        var unresolved = Result(evidenceRef: "ev-missing");
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidEvidence, LocalAiResultValidatorV1.Validate(unresolved, ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.Valid, LocalAiResultValidatorV1.Validate(unresolved, null).Code);

        foreach (var malformed in new[]
        {
            "[]",
            "[1]",
            "[\"\"]",
            "[\" \"]",
            "[\"\\t\"]",
            "[\"\\n\"]",
            "[" + string.Join(',', Enumerable.Range(1, 17).Select(index => $"\"ev-{index}\"")) + "]",
        })
        {
            Assert.Equal(LocalAiResultValidationCodeV1.InvalidEvidence, LocalAiResultValidatorV1.Validate(Result(findingEvidenceRefs: malformed), null).Code);
            Assert.Equal(LocalAiResultValidationCodeV1.InvalidEvidence, LocalAiResultValidatorV1.Validate(Result(findingEvidenceRefs: malformed), ["ev-1"]).Code);
        }

        var expected = new LocalAiStoredResultInvariantV1(null, PayloadHash, SnapshotId, "session", SessionId, null, SessionId,
            "github_copilot_sdk", "synthetic-model", Hash64, "local-ai-analysis.prompt.v1",
            "2026-08-30T01:00:00.0000000+00:00", "2026-08-30T01:00:01.0000000+00:00", "2026-08-30T01:00:02.0000000+00:00", "succeeded");
        static byte[] Canonical(byte[] value) => LocalAiResultValidatorV1.Validate(value, null).CanonicalBytes!;
        Assert.True(LocalAiAnalysisStoreV1.ValidateStoredResultWithoutEvidenceMembership(Canonical(Result()), expected));
        Assert.False(LocalAiAnalysisStoreV1.ValidateStoredResultWithoutEvidenceMembership(Canonical(Result(provider: "other")), expected));
        Assert.False(LocalAiAnalysisStoreV1.ValidateStoredResultWithoutEvidenceMembership(Canonical(Result(zero: true)), expected));
    }

    [Fact]
    public void SessionContent_IsCatalogOwnedAndReadableAfterRestart()
    {
        using var database = new Database();
        var now = new DateTimeOffset(2026, 8, 30, 1, 0, 0, TimeSpan.Zero);
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        var catalog = new RetentionCatalogStore(context, time);
        var store = database.Store(catalog, time);

        store.InsertSnapshot(Snapshot());
        var run = Complete(store, Request(), Result());

        using (var connection = database.Open())
        {
            Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_run_raw' AND source_item_id IN ('local_ai:snapshot:' || '" + SnapshotId + "','local_ai:result:' || (SELECT result_id FROM local_ai_runs WHERE run_id='" + run.RunId + "')) AND policy_id='raw-default-90d';"));
        }

        var materialized=0;var report = Assert.Single(new LocalAiAnalysisStoreV1(database.Path,catalog,time,()=>materialized++).GetSessionReports(SessionId, null, null).Items);
        Assert.Equal("retained", report.ContentState);
        Assert.NotNull(report.CanonicalResult);
        Assert.Equal(1,materialized);
        using var read = database.Open();
        Assert.Equal(1L, Scalar(read, "SELECT COUNT(*) FROM local_ai_runs WHERE run_id='" + run.RunId + "' AND state='succeeded' AND result_id IS NOT NULL AND completed_at IS NOT NULL;"));
    }

    [Fact]
    public void SessionResult_ReadDenialExpiresBeforePhysicalDeletion()
    {
        using var database=new Database();var now=new DateTimeOffset(2026,8,30,1,0,0,TimeSpan.Zero);var time=new MutableTimeProvider(now);var context=RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path,time);var catalog=new RetentionCatalogStore(context,time);var store=database.Store(catalog,time);store.InsertSnapshot(Snapshot());var run=Complete(store,Request(),Result());
        using(var connection=database.Open()){using var deny=connection.CreateCommand();deny.CommandText="UPDATE retention_items SET state='deletion_queued',read_denied_at=$now,queued_at=$now,revision=revision+1 WHERE source_item_id='local_ai:result:'||(SELECT result_id FROM local_ai_runs WHERE run_id=$run);";deny.Parameters.AddWithValue("$now",now.ToString("O"));deny.Parameters.AddWithValue("$run",run.RunId);Assert.Equal(1,deny.ExecuteNonQuery());Assert.Equal(1L,Scalar(connection,"SELECT COUNT(*) FROM local_ai_results WHERE result_json IS NOT NULL;"));}
        var materialized=0;var deniedStore=new LocalAiAnalysisStoreV1(database.Path,catalog,time,()=>materialized++);var report=Assert.Single(deniedStore.GetSessionReports(SessionId,null,null).Items);Assert.Equal("expired",report.ContentState);Assert.Null(report.CanonicalResult);Assert.Equal(0,materialized);
    }

    [Fact]
    public void SessionWrites_RequireRetentionBeforeAnyWrite()
    {
        using var database=new Database();var store=database.NodeStore();var error=Assert.Throws<InvalidOperationException>(()=>store.InsertSnapshot(Snapshot()));Assert.Equal("local_ai_retention_required",error.Message);Assert.Equal(0L,database.Scalar("SELECT COUNT(*) FROM local_ai_snapshots;"));
    }

    [Theory]
    [InlineData("DROP TRIGGER local_ai_terminal_run_update_rejected; UPDATE local_ai_runs SET result_id=NULL;")]
    [InlineData("DROP TRIGGER local_ai_results_update_rejected; UPDATE local_ai_results SET result_sha256='2222222222222222222222222222222222222222222222222222222222222222';")]
    [InlineData("DROP TRIGGER local_ai_terminal_run_update_rejected; UPDATE local_ai_runs SET completed_at='not-a-timestamp';")]
    [InlineData("DROP TRIGGER local_ai_snapshots_update_rejected; UPDATE local_ai_snapshots SET evidence_index_json=x'7B7D';")]
    public void BackupSemanticValidation_RejectsMalformedGraphLifecycleHashesAndEvidence(string mutation)
    {
        using var database=new Database();var store=database.Store();store.InsertSnapshot(Snapshot());Complete(store,Request(),Result());using var connection=database.Open();Execute(connection,mutation);using var transaction=connection.BeginTransaction();Assert.False(SqliteRuntimeBackupService.ValidateLocalAiRows(connection,transaction));transaction.Commit();
    }

    [Fact]
    public async Task SessionSnapshot_ExpiresThroughExistingAnalysisRunRawDeletionAuthority()
    {
        using var database=new Database(); var now=new DateTimeOffset(2026,8,30,1,0,0,TimeSpan.Zero); var time=new MutableTimeProvider(now); var context=RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path,time); var catalog=new RetentionCatalogStore(context,time); var store=database.Store(catalog,time); store.InsertSnapshot(Snapshot());
        string itemId; using(var connection=database.Open()){using(var coverage=connection.CreateCommand()){coverage.CommandText="INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);";coverage.ExecuteNonQuery();}using var item=connection.CreateCommand();item.CommandText="SELECT item_id FROM retention_items WHERE source_item_id='local_ai:snapshot:"+SnapshotId+"';";itemId=(string)item.ExecuteScalar()!;using var queue=connection.CreateCommand();queue.CommandText="UPDATE retention_items SET state='deletion_queued',revision=1,read_denied_at=$now,queued_at=$now WHERE item_id=$id;";queue.Parameters.AddWithValue("$now",now.ToString("O"));queue.Parameters.AddWithValue("$id",itemId);queue.ExecuteNonQuery();}
        var claim=(await catalog.TryClaimDeletionAsync(new(itemId,1,RetentionWorkKind.Queued),"local-ai-test",now,CancellationToken.None)).Claim!; var intent=await catalog.EnsureDeleteIntentAsync(claim.Fence,0,now,CancellationToken.None); var deleteContext=new RetentionDeleteContext(claim.Fence.ItemId,claim.StoreInstanceId,claim.StoreKind,claim.Fence.ExpectedRevision,claim.Fence.LeaseOwner,claim.Fence.LeaseGeneration,claim.SourceIdentity,null,intent.IntentCursor,CancellationToken.None);
        Assert.Same(RetentionAdapterResult.Deleted,await new MonitorAnalysisRetentionAdapter(catalog).DeleteAsync(deleteContext)); using var read=database.Open();Assert.Equal(1L,Scalar(read,"SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id='"+SnapshotId+"' AND payload_json IS NULL AND evidence_index_json IS NULL;"));Assert.Equal("deleted",Text(read,"SELECT state FROM retention_items WHERE item_id='"+itemId+"';"));
    }

    [Fact]
    public void NodeCleanup_DeletesAllStaleNodeStateAtBoundaryAndPreservesYoungerAndSessionRows()
    {
        var createdAt = DateTimeOffset.Parse("2026-08-30T01:00:00Z");
        var time = new MutableTimeProvider(createdAt);
        using var database = new Database();
        var store = database.NodeStore(time);
        store.InsertSnapshot(Snapshot(NodeSnapshotId, "node", "node-1"));
        store.InsertSnapshot(Snapshot(FreshOrphanSnapshotId, "node", "node-orphan"));
        var oldRunlessSnapshotId = Guid.CreateVersion7().ToString();
        store.InsertSnapshot(Snapshot(oldRunlessSnapshotId, "node", "node-old-runless"));
        var run = Complete(store, Request(NodeSnapshotId, "node", "node-1"), Result(scope: Scope("node", "node-1"), snapshotId: NodeSnapshotId));
        var queued = store.CreateRun(Request(FreshOrphanSnapshotId, "node", "node-orphan"));
        var runningSnapshotId = Guid.CreateVersion7().ToString();
        store.InsertSnapshot(Snapshot(runningSnapshotId, "node", "node-running"));
        var running = store.CreateRun(Request(runningSnapshotId, "node", "node-running"));
        store.TransitionRun(running.RunId, LocalAiRunStateV1.Running, null, createdAt.AddSeconds(1));
        time.Advance(TimeSpan.FromTicks(1));
        var youngSnapshotId = Guid.CreateVersion7().ToString();
        store.InsertSnapshot(Snapshot(youngSnapshotId, "node", "node-young"));

        var sessionStore = database.Store(time: time);
        var sessionSnapshotId = Guid.CreateVersion7().ToString();
        sessionStore.InsertSnapshot(Snapshot(sessionSnapshotId));
        var sessionRun = sessionStore.CreateRun(Request(sessionSnapshotId) with { RequestedAt = createdAt });

        Assert.Equal(0, store.DeleteExpiredTransientRuns(createdAt.AddHours(24).AddTicks(-1)));
        Assert.Equal(3, store.DeleteExpiredTransientRuns(createdAt.AddHours(24)));
        using var connection = database.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_runs WHERE run_id='" + run.RunId + "';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_runs WHERE run_id IN ('" + queued.RunId + "','" + running.RunId + "');"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id='" + NodeSnapshotId + "';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id IN ('" + FreshOrphanSnapshotId + "','" + runningSnapshotId + "');"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id='" + oldRunlessSnapshotId + "';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id='" + youngSnapshotId + "';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_runs WHERE run_id='" + sessionRun.RunId + "';"));
    }

    [Fact]
    public void TransientCleanup_UsesCanonicalExpiryForRepositorySelectionAndComparison()
    {
        var createdAt=DateTimeOffset.Parse("2026-08-30T01:00:00Z");var time=new MutableTimeProvider(createdAt);using var database=new Database();var store=database.NodeStore(time);
        var repositoryId="0198f5c0-1b89-7d41-8c2f-4ecba0b54431";var comparisonId="0198f5c0-1b89-7d41-8c2f-4ecba0b54432";var repositorySnapshot=Guid.CreateVersion7().ToString();var comparisonSnapshot=Guid.CreateVersion7().ToString();
        store.InsertSnapshot(new(repositorySnapshot,"repository_selection",null,null,repositoryId,"{\"value\":1}"u8.ToArray(),"{\"evidence_refs\":[\"ev-1\"]}"u8.ToArray(),repositoryId));
        store.InsertSnapshot(new(comparisonSnapshot,"comparison",null,null,comparisonId,"{\"value\":1}"u8.ToArray(),"{\"evidence_refs\":[\"ev-1\"]}"u8.ToArray(),repositoryId,comparisonId,createdAt.AddHours(3)));
        var repositoryRun=store.CreateRun(new(repositorySnapshot,"repository_selection",null,null,"github_copilot_sdk","synthetic-model",Hash64,"local-ai-analysis.prompt.v1",createdAt,null,repositoryId));
        var comparisonRun=store.CreateRun(new(comparisonSnapshot,"comparison",null,null,"github_copilot_sdk","synthetic-model",Hash64,"local-ai-analysis.prompt.v1",createdAt,null,repositoryId,comparisonId));
        store.TransitionRun(comparisonRun.RunId,LocalAiRunStateV1.Running,null,StartedAt);
        var comparisonScope=$"{{\"anchor_id\":\"{comparisonId}\",\"comparison_id\":\"{comparisonId}\",\"kind\":\"comparison\",\"repository_id\":\"{repositoryId}\"}}";
        Assert.Equal(LocalAiRunStateV1.Succeeded,store.Complete(comparisonRun.RunId,Result(scope:comparisonScope,snapshotId:comparisonSnapshot),CompletedAt));

        Assert.Equal(0,store.DeleteExpiredTransientRuns(createdAt.AddHours(3).AddTicks(-1)));
        Assert.Equal(1,store.DeleteExpiredTransientRuns(createdAt.AddHours(3)));
        Assert.Equal(1L,database.Scalar($"SELECT COUNT(*) FROM local_ai_runs WHERE run_id='{repositoryRun.RunId}';"));
        Assert.Equal(0L,database.Scalar($"SELECT COUNT(*) FROM local_ai_runs WHERE run_id='{comparisonRun.RunId}';"));
        Assert.Equal(1,store.DeleteExpiredTransientRuns(createdAt.AddHours(24)));
        Assert.Equal(0L,database.Scalar("SELECT COUNT(*) FROM local_ai_snapshots WHERE scope_kind<>'session';"));
    }

    [Fact]
    public void Snapshot_EnforcesOneMiBPayloadAndEvidenceAdmissionBoundary()
    {
        using var database = new Database(); var store = database.NodeStore();
        var exactPayload = Encoding.UTF8.GetBytes("{\"value\":\"" + new string('x', 1_048_564) + "\"}");
        var exactEvidence = Encoding.UTF8.GetBytes("{\"evidence_refs\":[\"" + new string('x', 1_048_554) + "\"]}");
        Assert.Equal(1_048_576, exactPayload.Length); Assert.Equal(1_048_576, exactEvidence.Length);
        store.InsertSnapshot(Snapshot() with { ScopeKind = "node", NodeId = "node-payload", AnchorId = "node-payload", PayloadCanonicalJson = exactPayload });
        store.InsertSnapshot(Snapshot(Guid.CreateVersion7().ToString(), "node", "node-evidence") with { EvidenceIndexCanonicalJson = exactEvidence });
        Assert.Throws<InvalidOperationException>(() => store.InsertSnapshot(Snapshot(Guid.CreateVersion7().ToString(), "node", "node-overflow") with { PayloadCanonicalJson = exactPayload.Concat([(byte)' ']).ToArray() }));
        Assert.Throws<InvalidOperationException>(() => store.InsertSnapshot(Snapshot(Guid.CreateVersion7().ToString(), "node", "node-overflow") with { EvidenceIndexCanonicalJson = exactEvidence.Concat([(byte)' ']).ToArray() }));
    }
    [Fact]
    public void Schema_MigratesExistingVersionTableAndRejectsMalformedCompleteInventory()
    {
        using var migration = new Database(); using var connection = migration.Open();
        Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('session',14);");
        LocalAiAnalysisSchemaV1.Ensure(connection); LocalAiAnalysisSchemaV1.Ensure(connection);
        Assert.Equal(2L, Scalar(connection, "SELECT version FROM schema_version WHERE component='local_ai_analysis';"));
        Assert.Equal(3L, Scalar(connection, "SELECT COUNT(*) FROM pragma_table_info('local_ai_snapshots') WHERE name IN ('repository_id','comparison_id','expires_at');"));

        using var corrupt = new Database(); using var corruptConnection = corrupt.Open();
        Execute(corruptConnection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version VALUES('local_ai_analysis',1); CREATE TABLE local_ai_snapshots(id TEXT); CREATE TABLE local_ai_runs(id TEXT); CREATE TABLE local_ai_results(id TEXT);");
        Assert.Throws<InvalidOperationException>(() => LocalAiAnalysisSchemaV1.Ensure(corruptConnection));
    }

    [Fact]
    public void Schema_MigratesExactVersionOneRowsWithoutChangingSessionOrNodeContent()
    {
        using var database = new Database();
        using var connection = database.Open();
        CreateVersionOneSchema(connection);
        var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var emptyPayloadHash=Convert.ToHexStringLower(SHA256.HashData("{}"u8));
        Execute(connection, $"""
            INSERT INTO local_ai_snapshots VALUES('{SnapshotId}','session','{SessionId}',NULL,'{SessionId}',x'7B7D','{emptyPayloadHash}',x'7B2265766964656E63655F72656673223A5B5D7D','{Convert.ToHexStringLower(SHA256.HashData("{\"evidence_refs\":[]}"u8))}',x'{owner}','2026-08-30T01:00:00.0000000+00:00');
            INSERT INTO local_ai_snapshots VALUES('{NodeSnapshotId}','node','{SessionId}','node-1','node-1',x'7B7D','{emptyPayloadHash}',x'7B2265766964656E63655F72656673223A5B5D7D','{Convert.ToHexStringLower(SHA256.HashData("{\"evidence_refs\":[]}"u8))}',x'{owner}','2026-08-30T01:00:00.0000000+00:00');
            INSERT INTO local_ai_runs VALUES('{RegeneratedSnapshotId}','{SnapshotId}','session','{SessionId}',NULL,'queued','github_copilot_sdk','model','{Hash64}','template','2026-08-30T01:00:00.0000000+00:00',NULL,NULL,60,NULL,NULL,'2026-08-30T01:00:00.0000000+00:00','2026-08-30T01:00:00.0000000+00:00');
            INSERT INTO local_ai_runs VALUES('{FreshOrphanSnapshotId}','{NodeSnapshotId}','node','{SessionId}','node-1','queued','github_copilot_sdk','model','{Hash64}','template','2026-08-30T01:00:00.0000000+00:00',NULL,NULL,60,NULL,NULL,'2026-08-30T01:00:00.0000000+00:00','2026-08-30T01:00:00.0000000+00:00');
            """);

        LocalAiAnalysisSchemaV1.Ensure(connection);

        Assert.Equal(2L, Scalar(connection, "SELECT version FROM schema_version WHERE component='local_ai_analysis';"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_snapshots;"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM local_ai_runs;"));
        Assert.Equal("2026-08-31T01:00:00.0000000+00:00", Text(connection, $"SELECT expires_at FROM local_ai_snapshots WHERE snapshot_id='{NodeSnapshotId}';"));
        Assert.Equal(1L, Scalar(connection, $"SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id='{SnapshotId}' AND expires_at IS NULL AND payload_json=x'7B7D';"));
    }

    [Fact]
    public void Schema_MigratesCompletedNodeResultWithRunExpiryAndPreservesSevenDigitTicks()
    {
        using var database=new Database();using var connection=database.Open();using(var transaction=connection.BeginTransaction()){MonitorSchemaMigrator.ApplyBaseSchema(connection,transaction);transaction.Commit();}_=RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);CreateVersionOneSchema(connection);
        const string created="2026-08-30T01:00:00.1234567+00:00",started="2026-08-30T01:00:01.1234567+00:00",completed="2026-08-30T01:00:02.1234567+00:00";var owner=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var payload="{\"value\":1}"u8.ToArray();var evidence="{\"evidence_refs\":[\"ev-1\"]}"u8.ToArray();var result=Result(scope:Scope("node","node-1"),snapshotId:NodeSnapshotId,requestedAt:created,startedAt:started,completedAt:completed);var canonical=LocalAiResultValidatorV1.Validate(result,["ev-1"]).CanonicalBytes!;var resultId=Guid.CreateVersion7().ToString();
        using(var command=connection.CreateCommand()){command.CommandText="INSERT INTO local_ai_snapshots VALUES($snapshot,'node',$session,'node-1','node-1',$payload,$payload_hash,$evidence,$evidence_hash,$owner,$created); INSERT INTO local_ai_runs VALUES($run,$snapshot,'node',$session,'node-1','succeeded','github_copilot_sdk','synthetic-model',$configuration,'local-ai-analysis.prompt.v1',$created,$started,$completed,60,NULL,$result,$created,$completed); INSERT INTO local_ai_results VALUES($result,$run,$json,$result_hash,$owner,$completed);";command.Parameters.AddWithValue("$snapshot",NodeSnapshotId);command.Parameters.AddWithValue("$session",SessionId);command.Parameters.AddWithValue("$payload",payload);command.Parameters.AddWithValue("$payload_hash",Convert.ToHexStringLower(SHA256.HashData(payload)));command.Parameters.AddWithValue("$evidence",evidence);command.Parameters.AddWithValue("$evidence_hash",Convert.ToHexStringLower(SHA256.HashData(evidence)));command.Parameters.AddWithValue("$owner",Convert.FromHexString(owner));command.Parameters.AddWithValue("$created",created);command.Parameters.AddWithValue("$run",RegeneratedSnapshotId);command.Parameters.AddWithValue("$configuration",Hash64);command.Parameters.AddWithValue("$started",started);command.Parameters.AddWithValue("$completed",completed);command.Parameters.AddWithValue("$result",resultId);command.Parameters.AddWithValue("$json",canonical);command.Parameters.AddWithValue("$result_hash",Convert.ToHexStringLower(SHA256.HashData(canonical)));command.ExecuteNonQuery();}

        LocalAiAnalysisSchemaV1.Ensure(connection);

        Assert.Equal("2026-08-31T01:00:00.1234567+00:00",Text(connection,$"SELECT expires_at FROM local_ai_runs WHERE run_id='{RegeneratedSnapshotId}';"));
        Assert.Equal(Text(connection,$"SELECT expires_at FROM local_ai_runs WHERE run_id='{RegeneratedSnapshotId}';"),Text(connection,$"SELECT expires_at FROM local_ai_results WHERE result_id='{resultId}';"));
        using var validation=connection.BeginTransaction();Assert.True(SqliteRuntimeBackupService.ValidateLocalAiRows(connection,validation));validation.Commit();
    }

    [Fact]
    public void TransientSnapshotRetry_AfterClockAdvanceRemainsIdempotent()
    {
        var time=new MutableTimeProvider(DateTimeOffset.Parse("2026-08-30T01:00:00Z"));using var database=new Database();var store=database.NodeStore(time);var snapshot=Snapshot(NodeSnapshotId,"node","node-1");store.InsertSnapshot(snapshot);time.Advance(TimeSpan.FromHours(1));

        store.InsertSnapshot(snapshot);

        Assert.Equal(1L,database.Scalar($"SELECT COUNT(*) FROM local_ai_snapshots WHERE snapshot_id='{NodeSnapshotId}';"));
        Assert.Equal("2026-08-31T01:00:00.0000000+00:00",database.Text($"SELECT expires_at FROM local_ai_snapshots WHERE snapshot_id='{NodeSnapshotId}';"));
    }

    [Fact]
    public void Schema_CorruptVersionOneGraphRollsBackWithoutPartialMigration()
    {
        using var database=new Database();using var connection=database.Open();CreateVersionOneSchema(connection);var owner=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));var payloadHash=Convert.ToHexStringLower(SHA256.HashData("{}"u8));var evidenceHash=Convert.ToHexStringLower(SHA256.HashData("{\"evidence_refs\":[]}"u8));
        Execute(connection,$"INSERT INTO local_ai_snapshots VALUES('{NodeSnapshotId}','node','{SessionId}','node-1','node-1',x'7B7D','{payloadHash}',x'7B2265766964656E63655F72656673223A5B5D7D','{evidenceHash}',x'{owner}','2026-08-30T01:00:00.0000000+00:00'); INSERT INTO local_ai_runs VALUES('{RegeneratedSnapshotId}','{NodeSnapshotId}','session','{SessionId}',NULL,'queued','github_copilot_sdk','model','{Hash64}','template','2026-08-30T01:00:00.0000000+00:00',NULL,NULL,60,NULL,NULL,'2026-08-30T01:00:00.0000000+00:00','2026-08-30T01:00:00.0000000+00:00');");

        Assert.Throws<InvalidOperationException>(()=>LocalAiAnalysisSchemaV1.Ensure(connection));

        Assert.Equal(1L,Scalar(connection,"SELECT version FROM schema_version WHERE component='local_ai_analysis';"));
        Assert.Equal(0L,Scalar(connection,"SELECT COUNT(*) FROM pragma_table_info('local_ai_snapshots') WHERE name='expires_at';"));
        Assert.Equal(1L,Scalar(connection,"SELECT COUNT(*) FROM local_ai_runs;"));
    }

    [Fact]
    public void Schema_RejectsOwnedDdlWhoseStringLiteralWasMutated()
    {
        using var database = new Database(); using var connection = database.Open(); LocalAiAnalysisSchemaV1.Ensure(connection);
        Execute(connection, "DROP TRIGGER local_ai_snapshots_update_rejected; CREATE TRIGGER local_ai_snapshots_update_rejected BEFORE UPDATE ON local_ai_snapshots BEGIN SELECT RAISE(ABORT,'local_ai_snapshot_ immutable'); END;");
        var before = Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema;");
        Assert.Throws<InvalidOperationException>(() => LocalAiAnalysisSchemaV1.Ensure(connection));
        Assert.Equal(before, Scalar(connection, "SELECT COUNT(*) FROM sqlite_schema;"));
    }

    [Theory]
    [InlineData("created_at", "created_ at")]
    [InlineData("CREATE TABLE", "CRE ATE TABLE")]
    public void Schema_RejectsSplitWordTokensWithoutMutation(string canonical, string split)
    {
        using var database = new Database(); using var connection = database.Open(); LocalAiAnalysisSchemaV1.Ensure(connection);
        Execute(connection, $"PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(sql,'{canonical}','{split}') WHERE name='local_ai_results'; PRAGMA writable_schema=OFF;");
        var before = Text(connection, "SELECT sql FROM sqlite_schema WHERE name='local_ai_results';");
        Assert.Throws<InvalidOperationException>(() => LocalAiAnalysisSchemaV1.Ensure(connection));
        Assert.Equal(before, Text(connection, "SELECT sql FROM sqlite_schema WHERE name='local_ai_results';"));
    }

    [Fact]
    public void Schema_AcceptsFormattingWhitespaceBetweenExistingTokens()
    {
        using var database = new Database(); using var connection = database.Open(); LocalAiAnalysisSchemaV1.Ensure(connection);
        Execute(connection, "PRAGMA writable_schema=ON; UPDATE sqlite_schema SET sql=replace(replace(sql,'CREATE TABLE','CREATE  '||char(10)||' TABLE'),'(', ' ( ') WHERE name='local_ai_results'; PRAGMA writable_schema=OFF;");
        LocalAiAnalysisSchemaV1.Ensure(connection);
        Assert.Equal(2L, Scalar(connection, "SELECT version FROM schema_version WHERE component='local_ai_analysis';"));
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
        store.TransitionRun(run.RunId, LocalAiRunStateV1.Running, null, StartedAt); Assert.Throws<ArgumentException>(() => store.TransitionRun(run.RunId, LocalAiRunStateV1.ProviderFailed, "arbitrary")); store.TransitionRun(run.RunId, LocalAiRunStateV1.ProviderFailed, "provider_failed", CompletedAt);
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

    [Theory]
    [InlineData("findings")]
    [InlineData("improvement_suggestions")]
    public void Validator_RejectsDuplicateEvidenceRefsWithinOneResultItem(string collection)
    {
        var result = JsonNode.Parse(Result())!.AsObject();
        result[collection]![0]!["evidence_refs"] = new JsonArray("ev-1", "ev-1");

        Assert.Equal(
            LocalAiResultValidationCodeV1.InvalidEvidence,
            LocalAiResultValidatorV1.Validate(Encoding.UTF8.GetBytes(result.ToJsonString()), ["ev-1"]).Code);
    }

    [Fact]
    public void Validator_AllowsTheSameEvidenceRefAcrossSeparateResultItems()
    {
        var result = JsonNode.Parse(Result())!.AsObject();
        var findings = result["findings"]!.AsArray();
        findings.Add(findings[0]!.DeepClone());
        var suggestions = result["improvement_suggestions"]!.AsArray();
        suggestions.Add(suggestions[0]!.DeepClone());

        Assert.Equal(
            LocalAiResultValidationCodeV1.Valid,
            LocalAiResultValidatorV1.Validate(Encoding.UTF8.GetBytes(result.ToJsonString()), ["ev-1"]).Code);
    }

    [Fact]
    public void Validator_AcceptsExactSizeBoundaryAndRejectsOverflowAndDepth17()
    {
        var baseResult = Result(); var exactSize = Result(summary: new string('x', "synthetic".Length + 1_048_576 - baseResult.Length));
        Assert.Equal(1_048_576, exactSize.Length); Assert.Equal(LocalAiResultValidationCodeV1.Valid, LocalAiResultValidatorV1.Validate(exactSize, ["ev-1"]).Code);
        Assert.Equal(LocalAiResultValidationCodeV1.TooLarge, LocalAiResultValidatorV1.Validate(exactSize.Concat([(byte)' ']).ToArray(), ["ev-1"]).Code);
        var depth17 = Encoding.UTF8.GetBytes("{\"scope\":" + new string('[', 17) + "0" + new string(']', 17) + "}");
        Assert.Equal(LocalAiResultValidationCodeV1.InvalidResult, LocalAiResultValidatorV1.Validate(depth17, ["ev-1"]).Code);
    }

    [Fact]
    public void Completion_ValidatesEvidenceAndStoresIndependentChecksumAndResultLink()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot());
        var invalid = store.CreateRun(Request()); store.TransitionRun(invalid.RunId, LocalAiRunStateV1.Running, null, StartedAt); Assert.Equal(LocalAiRunStateV1.InvalidEvidence, store.Complete(invalid.RunId, Result(evidenceRef: "missing"), CompletedAt));
        var run = store.CreateRun(Request()); store.TransitionRun(run.RunId, LocalAiRunStateV1.Running, null, StartedAt); Assert.Equal(LocalAiRunStateV1.Succeeded, store.Complete(run.RunId, Result(), CompletedAt));
        using var connection = database.Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT r.result_id,x.result_id,x.result_json,x.result_sha256,r.completed_at FROM local_ai_runs r JOIN local_ai_results x ON x.run_id=r.run_id WHERE r.run_id=$id;"; command.Parameters.AddWithValue("$id", run.RunId);
        using var reader = command.ExecuteReader(); Assert.True(reader.Read()); Assert.Equal(reader.GetString(0), reader.GetString(1)); Assert.Equal(Convert.ToHexStringLower(SHA256.HashData((byte[])reader[2])), reader.GetString(3)); Assert.False(reader.IsDBNull(4));
    }

    [Fact]
    public void Completion_RejectsResultProvenanceThatDoesNotMatchRunAndSnapshot()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot());
        var run = store.CreateRun(Request()); store.TransitionRun(run.RunId, LocalAiRunStateV1.Running, null, StartedAt);
        Assert.Equal(LocalAiRunStateV1.InvalidResult, store.Complete(run.RunId, Result(provider: "different_provider"), CompletedAt));
    }

    [Theory]
    [InlineData("started")]
    [InlineData("completed")]
    public void Completion_RejectsLifecycleTimestampMismatch(string field)
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot());
        var run = store.CreateRun(Request()); store.TransitionRun(run.RunId, LocalAiRunStateV1.Running, null, StartedAt);
        var result = field == "started" ? Result(startedAt: "2026-08-30T01:00:03.0000000+00:00") : Result(completedAt: "2026-08-30T01:00:03.0000000+00:00");
        Assert.Equal(LocalAiRunStateV1.InvalidResult, store.Complete(run.RunId, result, CompletedAt));
    }

    [Fact]
    public void Completion_DistinguishesDepth16SemanticEvidenceFromDepth17ParserRejection()
    {
        using var database = new Database(); var store = database.Store(); store.InsertSnapshot(Snapshot());
        var semantic = store.CreateRun(Request()); store.TransitionRun(semantic.RunId, LocalAiRunStateV1.Running, null, StartedAt);
        Assert.Equal(LocalAiRunStateV1.InvalidEvidence, store.Complete(semantic.RunId, Result(findingEvidenceRefs: NestedEvidenceRefs(13)), CompletedAt));
        var admission = store.CreateRun(Request()); store.TransitionRun(admission.RunId, LocalAiRunStateV1.Running, null, StartedAt);
        Assert.Equal(LocalAiRunStateV1.InvalidResult, store.Complete(admission.RunId, Result(findingEvidenceRefs: NestedEvidenceRefs(14)), CompletedAt));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM local_ai_results;"));
        Assert.Equal(PayloadHash, database.Text("SELECT payload_sha256 FROM local_ai_snapshots;"));
    }

    [Fact]
    public void NodeSuccess_IsExcludedFromSessionHistory()
    {
        using var database = new Database(); var store = database.NodeStore(); store.InsertSnapshot(Snapshot(NodeSnapshotId, "node", "node-1"));
        var run = store.CreateRun(Request(NodeSnapshotId, "node", "node-1")); store.TransitionRun(run.RunId, LocalAiRunStateV1.Running, null, StartedAt);
        Assert.Equal(LocalAiRunStateV1.Succeeded, store.Complete(run.RunId, Result(scope: Scope("node", "node-1"), snapshotId: NodeSnapshotId), CompletedAt));
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
        var failed = store.CreateRun(Request()); store.TransitionRun(failed.RunId, LocalAiRunStateV1.Running, null, StartedAt); store.TransitionRun(failed.RunId, LocalAiRunStateV1.TimedOut, "timed_out", CompletedAt);
        for (var index = 0; index < 104; index++) { Complete(store, Request(), Result()); Thread.Sleep(1); }
        var first = store.GetSessionReports(SessionId, null, null); Assert.Equal(20, first.Items.Count); Assert.NotNull(first.NextCursor); Assert.True(first.Items.Zip(first.Items.Skip(1), (left, right) => left.CreatedAt >= right.CreatedAt).All(value => value));
        Assert.Equal(100, store.GetSessionReports(SessionId, 1000, null).Items.Count); Assert.Equal(84, store.GetSessionReports(SessionId, 100, first.NextCursor).Items.Count);
    }

    private static LocalAiRunV1 Complete(LocalAiAnalysisStoreV1 store, LocalAiRunRequestV1 request, byte[] result, bool zero = false) { var run=store.CreateRun(request); store.TransitionRun(run.RunId,LocalAiRunStateV1.Running,null,StartedAt); Assert.Equal(zero?LocalAiRunStateV1.ZeroFindings:LocalAiRunStateV1.Succeeded,store.Complete(run.RunId,zero?Result(snapshotId:request.SnapshotId,zero:true):result,CompletedAt)); return run; }
    private static LocalAiSnapshotV1 Snapshot(string id=SnapshotId,string kind="session",string? node=null)=>new(id,kind,SessionId,node,node??SessionId,"{\"value\":1}"u8.ToArray(),"{\"evidence_refs\":[\"ev-1\"]}"u8.ToArray());
    private static LocalAiRunRequestV1 Request(string snapshotId=SnapshotId,string kind="session",string? node=null)=>new(snapshotId,kind,SessionId,node,"github_copilot_sdk","synthetic-model",Hash64,"local-ai-analysis.prompt.v1",DateTimeOffset.Parse("2026-08-30T01:00:00Z"),null);
    private static string Scope(string kind="session",string? node=null)=>$"{{\"anchor_id\":\"{node??SessionId}\",\"kind\":\"{kind}\",\"node_id\":{(node is null?"null":"\""+node+"\"")},\"session_id\":\"{SessionId}\"}}";
    private static byte[] Result(string evidenceRef="ev-1",bool zero=false,string? scope=null,string snapshotId=SnapshotId,string targetKind="skill",string configurationHash=Hash64,string summary="synthetic",string provenanceExtra="",string provider="github_copilot_sdk",string requestedAt="2026-08-30T01:00:00.0000000+00:00",string startedAt="2026-08-30T01:00:01.0000000+00:00",string completedAt="2026-08-30T01:00:02.0000000+00:00",string limitations="[]",string? findingEvidenceRefs=null)=>Encoding.UTF8.GetBytes("{\"findings\":"+(zero?"[]":"[{\"evidence_refs\":"+(findingEvidenceRefs??"[\""+evidenceRef+"\"]")+",\"evidence_state\":\"supported\",\"explanation\":\"explanation\",\"finding_id\":\"f-1\",\"limitation\":\"none\",\"title\":\"title\"}]") + ",\"improvement_suggestions\":[{\"concrete_change\":\"change\",\"evidence_refs\":[\""+evidenceRef+"\"],\"expected_effect\":\"effect\",\"rationale\":\"reason\",\"risks_or_limitations\":\"risk\",\"suggestion_id\":\"s-1\",\"target_kind\":\""+targetKind+"\",\"target_label\":\"target\"}],\"limitations\":"+limitations+",\"provenance\":{\"completed_at\":\""+completedAt+"\",\"configuration_sha256\":\""+configurationHash+"\",\"coverage\":{\"content_available\":true,\"excluded\":0,\"included\":1},\"model\":\"synthetic-model\",\"prompt_template_version\":\"local-ai-analysis.prompt.v1\",\"provider\":\""+provider+"\",\"requested_at\":\""+requestedAt+"\",\"snapshot_id\":\""+snapshotId+"\",\"snapshot_sha256\":\""+PayloadHash+"\",\"started_at\":\""+startedAt+"\""+provenanceExtra+"},\"scope\":"+(scope??Scope())+",\"snapshot\":{\"payload_sha256\":\""+PayloadHash+"\",\"snapshot_id\":\""+snapshotId+"\"},\"summary\":\""+summary+"\"}");
    private static string NestedEvidenceRefs(int depth)=>new string('[',depth)+"0"+new string(']',depth);
    private const string SnapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54410",RegeneratedSnapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54412",NodeSnapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54413",FreshOrphanSnapshotId="0198f5c0-1b89-7d41-8c2f-4ecba0b54414",SessionId="0198f5c0-1b89-7d41-8c2f-4ecba0b54411",Hash64="1111111111111111111111111111111111111111111111111111111111111111";
    private static readonly string PayloadHash=Convert.ToHexStringLower(SHA256.HashData("{\"value\":1}"u8));
    private static readonly DateTimeOffset StartedAt=DateTimeOffset.Parse("2026-08-30T01:00:01Z"),CompletedAt=DateTimeOffset.Parse("2026-08-30T01:00:02Z");
    private static long Scalar(SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt64(q.ExecuteScalar());} private static string Text(SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;return(string)q.ExecuteScalar()!;} private static void Execute(SqliteConnection c,string sql){using var q=c.CreateCommand();q.CommandText=sql;q.ExecuteNonQuery();}
    private static void CreateVersionOneSchema(SqliteConnection connection) => Execute(connection, """
        CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);
        INSERT INTO schema_version VALUES('local_ai_analysis',1);
        CREATE TABLE local_ai_snapshots(snapshot_id TEXT PRIMARY KEY,scope_kind TEXT NOT NULL CHECK(scope_kind IN ('session','node')),session_id TEXT NOT NULL,node_id TEXT,anchor_id TEXT NOT NULL,payload_json BLOB,payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256)=64 AND payload_sha256=lower(payload_sha256)),evidence_index_json BLOB,evidence_index_sha256 TEXT NOT NULL CHECK(length(evidence_index_sha256)=64 AND evidence_index_sha256=lower(evidence_index_sha256)),retention_owner_token BLOB NOT NULL CHECK(length(retention_owner_token)=32),created_at TEXT NOT NULL,CHECK((scope_kind='session' AND node_id IS NULL) OR (scope_kind='node' AND node_id IS NOT NULL)),CHECK((payload_json IS NULL)=(evidence_index_json IS NULL)));
        CREATE TABLE local_ai_runs(run_id TEXT PRIMARY KEY,snapshot_id TEXT NOT NULL REFERENCES local_ai_snapshots(snapshot_id),scope_kind TEXT NOT NULL,session_id TEXT NOT NULL,node_id TEXT,state TEXT NOT NULL CHECK(state IN ('queued','running','succeeded','zero_findings','provider_failed','provider_partial','invalid_result','invalid_evidence','stale_snapshot','scope_too_large','timed_out','canceled')),provider TEXT NOT NULL,model TEXT NOT NULL,configuration_sha256 TEXT NOT NULL CHECK(length(configuration_sha256)=64 AND configuration_sha256=lower(configuration_sha256)),prompt_template_version TEXT NOT NULL,requested_at TEXT NOT NULL,started_at TEXT,completed_at TEXT,timeout_seconds INTEGER NOT NULL CHECK(timeout_seconds BETWEEN 1 AND 600),error_code TEXT,result_id TEXT UNIQUE,created_at TEXT NOT NULL,updated_at TEXT NOT NULL);
        CREATE TABLE local_ai_results(result_id TEXT PRIMARY KEY,run_id TEXT NOT NULL UNIQUE REFERENCES local_ai_runs(run_id),result_json BLOB,result_sha256 TEXT NOT NULL CHECK(length(result_sha256)=64 AND result_sha256=lower(result_sha256)),retention_owner_token BLOB NOT NULL CHECK(length(retention_owner_token)=32),created_at TEXT NOT NULL);
        CREATE INDEX IX_local_ai_session_reports ON local_ai_runs(scope_kind,session_id,state,completed_at DESC,run_id DESC);
        CREATE TRIGGER local_ai_snapshots_update_rejected BEFORE UPDATE ON local_ai_snapshots WHEN NOT (local_ai_retention_delete_authorized('snapshot',OLD.snapshot_id)=1 AND OLD.scope_kind='session' AND OLD.payload_json IS NOT NULL AND OLD.evidence_index_json IS NOT NULL AND NEW.payload_json IS NULL AND NEW.evidence_index_json IS NULL AND NEW.snapshot_id=OLD.snapshot_id AND NEW.scope_kind=OLD.scope_kind AND NEW.session_id=OLD.session_id AND NEW.node_id IS OLD.node_id AND NEW.anchor_id=OLD.anchor_id AND NEW.payload_sha256=OLD.payload_sha256 AND NEW.evidence_index_sha256=OLD.evidence_index_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at) BEGIN SELECT RAISE(ABORT,'local_ai_snapshot_immutable'); END;
        CREATE TRIGGER local_ai_results_update_rejected BEFORE UPDATE ON local_ai_results WHEN NOT (local_ai_retention_delete_authorized('result',OLD.result_id)=1 AND OLD.result_json IS NOT NULL AND NEW.result_json IS NULL AND NEW.result_id=OLD.result_id AND NEW.run_id=OLD.run_id AND NEW.result_sha256=OLD.result_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at) BEGIN SELECT RAISE(ABORT,'local_ai_result_immutable'); END;
        CREATE TRIGGER local_ai_terminal_run_update_rejected BEFORE UPDATE ON local_ai_runs WHEN OLD.state NOT IN ('queued','running') BEGIN SELECT RAISE(ABORT,'local_ai_terminal_run_immutable'); END;
        """);
    private sealed class Database:IDisposable{private readonly string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"local-ai-"+Guid.NewGuid().ToString("N"));internal Database(){Directory.CreateDirectory(directory);Path=System.IO.Path.Combine(directory,"test.sqlite");}internal string Path{get;}internal SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path,Pooling=false}.ToString());c.Open();return c;}internal LocalAiAnalysisStoreV1 Store(RetentionCatalogStore? catalog=null,TimeProvider? time=null){using var c=Open();LocalAiAnalysisSchemaV1.Ensure(c);catalog??=new RetentionCatalogStore(RetentionCatalogContext.InitializeNewOwnedDatabase(Path,time),time);return new(Path,catalog,time);}internal LocalAiAnalysisStoreV1 NodeStore(TimeProvider? time=null){using var c=Open();LocalAiAnalysisSchemaV1.Ensure(c);return new(Path,timeProvider:time);}internal long Scalar(string sql){using var c=Open();return LocalAiAnalysisFoundationTests.Scalar(c,sql);}internal string Text(string sql){using var c=Open();return LocalAiAnalysisFoundationTests.Text(c,sql);}public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(directory,true);}}
}

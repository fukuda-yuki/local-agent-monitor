using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Reflection;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RuntimeBackupLocalWorkspaceProjectionTests
{
    private static readonly DateTimeOffset PublicationAt = DateTimeOffset.Parse("2026-08-26T00:00:00Z");

    [Fact]
    public void ConfiguredProductionServicePreservesCurrentSdkSnapshotAndWorkspaceFactThroughRestore()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("configured-production.zip");
        var restored = fixture.Path("configured-production-restored.db");
        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        var inspected = fixture.Service.Inspect(archive);
        var restore = fixture.Service.Restore(archive, restored, new RuntimeRestoreOptions());

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.Equal(1, inspected.RowCounts!["skill_invocation_snapshots"]);
        Assert.Equal(1, inspected.RowCounts["local_workspace_session_search_facts"]);
        Assert.True(restore.Success, restore.ErrorCode);
        Assert.Equal(["demo-skill"], Strings(restored, "SELECT normalized_text FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.Equal(1L, Scalar(restored, "SELECT COUNT(*) FROM skill_invocation_snapshots;"));
    }

    [Fact]
    public void StableR0002WriterArchiveRemainsInspectablePreviewableAndRestorableAcrossBuilds()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("stable-r0002-writer.zip");
        var target = fixture.Path("stable-r0002-target.db");

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        var manifest = ReadManifest(archive);
        var inspected = fixture.Service.Inspect(archive);
        var preview = fixture.Service.Preview(archive, target);
        var restored = fixture.Service.Restore(archive, target, new RuntimeRestoreOptions());

        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal("runtime-backup-writer-r0002", manifest.SourceApplicationVersion);
        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(["demo-skill"], Strings(target, "SELECT normalized_text FROM local_workspace_session_search_facts WHERE kind='skill';"));
    }

    [Fact]
    public void HistoricalR0001WriterArchiveInspectsStructurallyAndStagesWithCurrentR0002Authority()
    {
        using var fixture = new HistoricalBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("historical-r0001.zip");
        var target = fixture.Path("historical-r0001-target.db");

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        var sourceBeforeInspection = File.ReadAllBytes(fixture.DatabasePath);
        var archiveBeforeInspection = File.ReadAllBytes(archive);
        var manifest = ReadManifest(archive);
        var currentService = new SqliteRuntimeBackupService(fixture.Clock);
        var inspected = currentService.Inspect(archive);
        var preview = currentService.Preview(archive, target);
        var restored = currentService.Restore(archive, target, new RuntimeRestoreOptions());

        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal("1.0.0", manifest.SourceApplicationVersion);
        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.Equal(1, inspected.RowCounts!["skill_invocation_snapshots"]);
        Assert.Equal(1, inspected.RowCounts["local_workspace_session_search_facts"]);
        Assert.Equal(sourceBeforeInspection, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal(archiveBeforeInspection, File.ReadAllBytes(archive));
        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(0L, Scalar(target, "SELECT COUNT(*) FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.Equal(1L, Scalar(target, "SELECT COUNT(*) FROM skill_invocation_snapshots;"));
        Assert.Equal(1L, Scalar(target, "SELECT COUNT(*) FROM skill_invocation_snapshot_receipts;"));
        Assert.Equal(1L, Scalar(target, "SELECT COUNT(*) FROM session_events WHERE source_surface='copilot-sdk';"));
        Assert.Equal(sourceBeforeInspection, File.ReadAllBytes(fixture.DatabasePath));
        Assert.Equal(archiveBeforeInspection, File.ReadAllBytes(archive));
    }

    [Fact]
    public void WriterProvenanceInjectionRejectsAuthorityFromAnotherRevision()
    {
        var gate = new LocalWorkspacePublicationGate();
        var currentAuthority = FixedSkillRegistryGenerationAuthority.ForWriterVersion(
            SkillInvocationV2ArtifactRegistry.CurrentWriterVersion);

        Assert.Throws<ArgumentException>(() =>
            new SqliteRuntimeBackupService(new RecordingTimeProvider(PublicationAt), currentAuthority, gate, "1.0.0"));
    }

    [Fact]
    public void PublicationUsesOneCapturedInstantToExcludeFactAtExpiryEquality()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt);
        var archive = fixture.Path("expiry-equality.zip");

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal(0L, Scalar(fixture.DatabasePath, "SELECT COUNT(*) FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.Equal(PublicationAt, fixture.Clock.FirstObservedInstant);
    }

    [Fact]
    public void StructuralInspectionRejectsUnmappedManifestWriterVersion()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("mapped-writer.zip");
        var unmapped = fixture.Path("unmapped-writer.zip");
        Assert.True(fixture.Service.CreateAndPublish(fixture.DatabasePath, archive).Success);
        RewriteWriterVersion(archive, unmapped, "unmapped-writer");

        var result = fixture.Service.Inspect(unmapped);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Fact]
    public void RetentionPinAndUnpinRefreshSdkFactToExactEffectiveLifetimeInOwningTransaction()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var catalog = new RetentionCatalogStore(fixture.DatabasePath, fixture.Clock);
        var application = new RetentionMutationApplicationService(
            catalog,
            fixture.Clock,
            publicationGate: fixture.Gate,
            workspaceParticipant: new LocalWorkspaceProjectionTransactionParticipant(fixture.Authority));
        var itemId = Text(fixture.DatabasePath, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' ORDER BY item_id LIMIT 1;");
        var workflowKey = RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat((byte)61, 32).ToArray());
        var preview = Assert.IsType<RetentionMutationPreviewResponse>(application.CreatePreview(
            new(new(RetentionMutationTargetKind.Item, itemId), RetentionMutationOperation.Pin,
                RetentionMutationScope.SingleItem, RetentionMutationReasonCodes.ResearchNeeded, null), workflowKey).Preview);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), workflowKey).Confirmation);

        var result = application.ExecuteMutation(new(
            confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
            RetentionMutationTargetKind.Item, itemId), workflowKey);

        Assert.Null(result.ErrorCode);
        Assert.Equal(1L, Scalar(fixture.DatabasePath,
            "SELECT COUNT(*) FROM local_workspace_session_search_facts WHERE kind='skill' AND source_identity LIKE 'sdk:%' AND expires_at IS NULL;"));

        var unpinKey = RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat((byte)62, 32).ToArray());
        var unpinPreview = Assert.IsType<RetentionMutationPreviewResponse>(application.CreatePreview(
            new(new(RetentionMutationTargetKind.Item, itemId), RetentionMutationOperation.Unpin,
                RetentionMutationScope.SingleItem, RetentionMutationReasonCodes.ResearchNeeded, null), unpinKey).Preview);
        var unpinConfirmation = Assert.IsType<RetentionConfirmationIssueResponse>(application.IssueConfirmation(
            new(unpinPreview.PreviewId, unpinPreview.PreviewDigest), unpinKey).Confirmation);

        var unpin = application.ExecuteMutation(new(
            unpinConfirmation.ConfirmationToken, RetentionMutationOperation.Unpin, RetentionMutationScope.SingleItem,
            RetentionMutationTargetKind.Item, itemId), unpinKey);

        Assert.Null(unpin.ErrorCode);
        Assert.Equal(
            Text(fixture.DatabasePath, "SELECT expires_at FROM retention_items WHERE item_id='" + itemId + "';"),
            Text(fixture.DatabasePath, "SELECT expires_at FROM local_workspace_session_search_facts WHERE kind='skill' AND source_identity LIKE 'sdk:%';"));
    }

    [Fact]
    public void RetentionDeleteNowRemovesSdkFactBeforeCommitBecomesVisible()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var catalog = new RetentionCatalogStore(fixture.DatabasePath, fixture.Clock);
        var application = new RetentionMutationApplicationService(catalog, fixture.Clock,
            publicationGate: fixture.Gate,
            workspaceParticipant: new LocalWorkspaceProjectionTransactionParticipant(fixture.Authority));
        var itemId = Text(fixture.DatabasePath, "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' ORDER BY item_id LIMIT 1;");
        var workflowKey = RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat((byte)63, 32).ToArray());
        var preview = Assert.IsType<RetentionMutationPreviewResponse>(application.CreatePreview(
            new(new(RetentionMutationTargetKind.Item, itemId), RetentionMutationOperation.DeleteNow,
                RetentionMutationScope.SingleItem, RetentionMutationReasonCodes.ResearchNeeded, null), workflowKey).Preview);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), workflowKey).Confirmation);

        var result = application.ExecuteMutation(new(
            confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem,
            RetentionMutationTargetKind.Item, itemId), workflowKey);

        Assert.Null(result.ErrorCode);
        Assert.Equal(0L, Scalar(fixture.DatabasePath,
            "SELECT COUNT(*) FROM local_workspace_session_search_facts WHERE kind='skill' AND source_identity LIKE 'sdk:%';"));
    }

    [Theory]
    [InlineData("UPDATE local_workspace_session_search_facts SET normalized_text='fabricated' WHERE source_identity LIKE 'sdk:%';")]
    [InlineData("UPDATE local_workspace_session_search_facts SET source_identity='sdk:00000000-0000-0000-0000-000000000000' WHERE source_identity LIKE 'sdk:%';")]
    [InlineData("UPDATE local_workspace_session_search_facts SET expires_at=NULL WHERE source_identity LIKE 'sdk:%';")]
    [InlineData("UPDATE local_workspace_session_search_facts SET expires_at='2099-01-01T00:00:00.0000000+00:00' WHERE source_identity LIKE 'sdk:%';")]
    public void StructuralInspectionRejectsSdkFactWithoutExactReadableSourceGraph(string mutation)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        Execute(fixture.DatabasePath, mutation);
        using var connection = Open(fixture.DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(
                connection,
                transaction,
                skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                    SkillInvocationV2ArtifactRegistry.CurrentWriterVersion))).Message);
    }

    [Theory]
    [InlineData("DELETE FROM local_workspace_session_search_facts WHERE source_identity LIKE 'sdk:%';")]
    [InlineData("INSERT INTO local_workspace_session_search_facts(session_id,kind,source_identity,normalized_text,expires_at) SELECT session_id,'skill','otel:fabricated','fabricated',NULL FROM local_workspace_sessions LIMIT 1;")]
    [InlineData("DELETE FROM local_workspace_sessions;")]
    public void StructuralInspectionRejectsMissingOrFabricatedWorkspaceRows(string mutation)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        Execute(fixture.DatabasePath, mutation);
        using var connection = Open(fixture.DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(
                connection,
                transaction,
                skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                    SkillInvocationV2ArtifactRegistry.CurrentWriterVersion))).Message);
    }

    [Fact]
    public void PreRefreshBusyMapsToSnapshotStoreBusyWithoutEscaping()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        using var blocker = Open(fixture.DatabasePath);
        using var transaction = blocker.BeginTransaction(deferred: false);

        var result = fixture.Service.CreateAndPublish(fixture.DatabasePath, fixture.Path("busy.zip"));

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.SnapshotStoreBusy, result.ErrorCode);
    }

    [Fact]
    public void InvalidProjectionAtPreRefreshMapsToRestoreIncompatibleWithoutEscaping()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        Execute(fixture.DatabasePath, "ALTER TABLE local_workspace_projection_state ADD COLUMN injected TEXT;");

        var result = fixture.Service.CreateAndPublish(fixture.DatabasePath, fixture.Path("invalid-projection.zip"));

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
    }

    [Theory]
    [InlineData("UPDATE local_workspace_sessions SET label_text='tampered';")]
    [InlineData("UPDATE local_workspace_session_search_facts SET normalized_text='tampered' WHERE kind='label';")]
    [InlineData("UPDATE local_workspace_sessions SET revision_seed='tampered';")]
    [InlineData("UPDATE local_workspace_session_models SET model='tampered';")]
    [InlineData("UPDATE local_workspace_token_observations SET input_tokens=999;")]
    [InlineData("DELETE FROM local_workspace_session_activity WHERE kind='retry';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='unknown';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_expired,raw_content_expired';")]
    [InlineData("UPDATE local_workspace_sessions SET capture_notes='raw_content_not_captured,projection_invalid';")]
    [InlineData("UPDATE local_workspace_execution_headers SET source_identity='tampered-execution';")]
    [InlineData("UPDATE local_workspace_nodes SET source_identity='tampered-node' WHERE source_kind='execution_root';")]
    [InlineData("UPDATE local_workspace_node_edges SET related_node_id=node_id WHERE relation_kind='parent';")]
    [InlineData("UPDATE local_workspace_node_content_refs SET source_item_id='tampered-content-source';")]
    public void ValidationRejectsSemanticProjectionTampering(string mutation)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','expiring','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000003','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk',NULL,NULL,NULL,'gpt-5',NULL,NULL,10,3,13,'active');
            INSERT INTO session_events VALUES('0198f5b8-0c00-7000-8000-000000000002','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000003',NULL,NULL,NULL,NULL,'synthetic','prompt-1','user.message','2026-08-24T00:00:00.0000000+00:00','available',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
            INSERT INTO session_event_content VALUES('0198f5b8-0c00-7000-8000-000000000002','application/json','{"value":"hello"}','2026-08-24T00:00:00.0000000+00:00','2099-01-01T00:00:00.0000000+00:00',randomblob(32));
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    [Theory]
    [MemberData(nameof(DurableUnavailableSemanticTamperCases))]
    public void ValidationAuthenticatesDurableUnavailableSemanticReceiptGraph(string unavailableState, string mutation)
    {
        using var connection = LocalWorkspaceProjectionBackfillTests.OpenUnavailableSdkToolFixture(unavailableState);
        using (var control = connection.BeginTransaction(deferred: true))
        {
            LocalWorkspaceProjectionBackupValidation.Validate(connection, control);
            control.Rollback();
        }
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    public static TheoryData<string, string> DurableUnavailableSemanticTamperCases
    {
        get
        {
            var mutations = new[]
            {
                "UPDATE local_workspace_semantic_receipts SET authority_receipt=authority_receipt||'|tampered';",
                "UPDATE local_workspace_node_source_references SET revision_input=revision_input||'|tampered' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);",
                "UPDATE local_workspace_tool_metadata SET started_state='not_observed' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);",
                "UPDATE local_workspace_semantic_receipts SET source_family='claude_hook';",
                "UPDATE local_workspace_semantic_receipts SET scope_kind='native_session';",
                "UPDATE local_workspace_node_content_refs SET retention_revision=retention_revision+1 WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);",
                "UPDATE session_events SET source_surface=NULL WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';",
                "UPDATE session_events SET type='event' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'; UPDATE local_workspace_node_source_references SET revision_input='copilot-sdk-stream|sdk-source-start|event|event|1|2026-08-24T00:00:00.0000000+00:00|copilot-sdk-stream|exact_sdk_tool|v1' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);",
                "UPDATE local_workspace_node_source_references SET source_kind='skill_claim' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);",
            };
            var data = new TheoryData<string, string>();
            foreach (var state in new[] { "read_denied", "deleted" })
                foreach (var mutation in mutations)
                    data.Add(state, mutation);
            return data;
        }
    }

    [Theory]
    [InlineData("UPDATE local_workspace_node_source_references SET source_kind='session_event' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE local_workspace_node_source_references SET trace_id='ffffffffffffffffffffffffffffffff' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE local_workspace_node_source_references SET revision_input=revision_input||'|tampered' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE local_workspace_node_source_references SET revision_input=replace(revision_input,'otel.tool.started','otel.tool.fabricated') WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE local_workspace_semantic_receipts SET authority_receipt='otel-exact|tampered|v1';")]
    [InlineData("UPDATE session_events SET trace_id='ffffffffffffffffffffffffffffffff' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("UPDATE session_events SET source_adapter='otel-drift' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("UPDATE session_events SET run_id='0198f5b8-0c00-7000-8000-000000000020' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES('0198f5b8-0c00-7000-8000-000000000021','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-drift','drift/event','event','2026-08-24T00:00:02.0000000+00:00','not_captured'); UPDATE local_workspace_node_source_references SET source_identity='0198f5b8-0c00-7000-8000-000000000021',event_id='0198f5b8-0c00-7000-8000-000000000021' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    public void ValidationAuthenticatesDurableUnavailableOtelToolGraph(string mutation)
    {
        using var connection = OpenUnavailableOtelToolFixture();
        using (var control = connection.BeginTransaction(deferred: true))
        {
            LocalWorkspaceProjectionBackupValidation.Validate(connection, control);
            control.Rollback();
        }
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenUnavailableOtelToolFixture()
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE raw_records(id INTEGER PRIMARY KEY,source TEXT,trace_id TEXT,received_at TEXT,resource_attributes_json TEXT,payload_json TEXT,schema_version INTEGER,retention_owner_token BLOB);
            CREATE TABLE monitor_spans(raw_record_id INTEGER,trace_id TEXT,span_id TEXT,parent_span_id TEXT,span_ordinal INTEGER,operation TEXT,category TEXT,tool_name TEXT,tool_type TEXT,mcp_tool_name TEXT,mcp_server_hash TEXT,agent_name TEXT,request_model TEXT,response_model TEXT,input_tokens INTEGER,output_tokens INTEGER,total_tokens INTEGER,reasoning_tokens INTEGER,cache_read_tokens INTEGER,cache_creation_tokens INTEGER,status TEXT,error_type TEXT,finish_reasons TEXT,conversation_id TEXT,duration_ms REAL,start_time TEXT,end_time TEXT,projected_at TEXT);
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES
              ('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run-a','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active'),
              ('0198f5b8-0c00-7000-8000-000000000020','0198f5b8-0c00-7000-8000-000000000001','claude-code','native-run-b','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,trace_id,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','claude-code','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','otel-exact','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/bbbbbbbbbbbbbbbb','otel.span','2026-08-24T00:00:00.0000000+00:00','not_captured');
            INSERT INTO raw_records VALUES(1,'otlp','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','2026-08-24T00:00:00.0000000+00:00','{}','{}',1,NULL);
            INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,operation,category,tool_name,status,start_time,end_time)
              VALUES(1,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbb',0,'execute_tool','tool_call','Read','ok','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:01.0000000+00:00');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, PublicationAt);
        LocalWorkspaceProjectionSchemaTests.Execute(connection,
            "DELETE FROM raw_records; DELETE FROM local_workspace_span_facts;");
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(
                connection, transaction, PublicationAt, FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
        }
        return connection;
    }

    [Theory]
    [InlineData("UPDATE local_workspace_node_source_references SET source_kind='skill_claim' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE session_events SET source_surface=NULL WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("UPDATE session_events SET run_id='0198f5b8-0c00-7000-8000-000000000020' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011';")]
    [InlineData("UPDATE local_workspace_node_source_references SET source_identity='0198f5b8-0c00-7000-8000-000000000021',event_id='0198f5b8-0c00-7000-8000-000000000021',revision_input='copilot-sdk-stream|arbitrary-source|event|event|1|2026-08-24T00:00:02.0000000+00:00|copilot-sdk-stream|exact_sdk_tool|v1' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE session_events SET source_event_id='drifted-source' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'; UPDATE local_workspace_node_source_references SET revision_input='copilot-sdk-stream|drifted-source|tool.execution_start|tool.execution_start|1|2026-08-24T00:00:00.0000000+00:00|copilot-sdk-stream|exact_sdk_tool|v1' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE session_runs SET native_run_id='drifted-native-run' WHERE run_id='0198f5b8-0c00-7000-8000-000000000010';")]
    [InlineData("UPDATE session_events SET type='tool.execution_complete' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'; UPDATE local_workspace_node_source_references SET revision_input='copilot-sdk-stream|sdk-source-start|tool.execution_complete|tool.execution_complete|1|2026-08-24T00:00:00.0000000+00:00|copilot-sdk-stream|exact_sdk_tool|v1' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts); UPDATE local_workspace_tool_metadata SET started_state='not_observed',completed_state='recorded' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    public void DurableSdkToolValidatorBindsExactEventRunCarrierAndLifecycle(string mutation)
    {
        using var connection = LocalWorkspaceProjectionBackfillTests.OpenUnavailableSdkToolFixture("deleted");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000020','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-run-2',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000021','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','copilot-sdk-stream','arbitrary-source','event','2026-08-24T00:00:02.0000000+00:00','not_captured');
            """);
        ValidateDurableSemanticGraphForTest(connection);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        Assert.Throws<InvalidOperationException>(() => ValidateDurableSemanticGraphForTest(connection));
    }

    [Theory]
    [InlineData("DELETE FROM session_native_ids;")]
    [InlineData("UPDATE session_native_ids SET native_session_id='drifted-native-session';")]
    [InlineData("UPDATE local_workspace_nodes SET source_kind='session_event' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE session_events SET type='event' WHERE event_id='0198f5b8-0c00-7000-8000-000000000011'; UPDATE local_workspace_node_source_references SET revision_input='copilot-sdk-stream|subagent-source|event|event|1|2026-08-24T00:00:00.0000000+00:00|copilot-sdk-stream|native_run|v1' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    [InlineData("UPDATE local_workspace_node_source_references SET source_kind='otel_span',trace_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',span_id='bbbbbbbbbbbbbbbb' WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts);")]
    public void DurableSdkSubagentValidatorBindsNativeSessionRunAndLifecycle(string mutation)
    {
        using var connection = OpenSdkSubagentFixture();
        ValidateDurableSemanticGraphForTest(connection);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, mutation);

        Assert.Throws<InvalidOperationException>(() => ValidateDurableSemanticGraphForTest(connection));
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenSdkSubagentFixture()
    {
        var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_native_ids VALUES('0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-session','native','2026-08-24T00:00:00.0000000+00:00');
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000010','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-child-run',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state)
              VALUES('0198f5b8-0c00-7000-8000-000000000011','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','copilot-sdk-stream','subagent-source','subagent.selected','2026-08-24T00:00:00.0000000+00:00','not_captured');
            """);
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, PublicationAt);
        return connection;
    }

    private static void ValidateDurableSemanticGraphForTest(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: true);
        var method = typeof(LocalWorkspaceProjectionBackupValidation).GetMethod(
            "ValidateDurableSemanticGraph", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            method.Invoke(null, [connection, transaction]);
        }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
        finally
        {
            transaction.Rollback();
        }
    }

    [Fact]
    public void ValidationAcceptsExactComponentAndRejectsTamperedOwnedSchema()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction);
            transaction.Rollback();
        }

        LocalWorkspaceProjectionSchemaTests.Execute(connection, "ALTER TABLE local_workspace_projection_state ADD COLUMN injected TEXT;");
        using var tampered = connection.BeginTransaction(deferred: true);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, tampered));
        Assert.Equal("local_workspace_projection_backup_invalid", exception.Message);
    }

    [Theory]
    [MemberData(nameof(EveryOwnedV5Table))]
    public void ValidationRejectsSchemaTamperingForEveryOwnedV5Table(string table)
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, PublicationAt);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, $"ALTER TABLE {table} ADD COLUMN injected TEXT;");

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    public static TheoryData<string> EveryOwnedV5Table
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var table in LocalWorkspaceProjectionSchemaV1.TableNames) data.Add(table);
            return data;
        }
    }

    [Fact]
    public void CurrentBackupInventoryIncludesVersionAndAllV5RowCounts()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));

        Assert.Equal(18, LocalWorkspaceProjectionSchemaV1.TableNames.Length);
        Assert.All(LocalWorkspaceProjectionSchemaV1.TableNames, table =>
            Assert.Equal([table], LocalWorkspaceProjectionSchemaTests.Strings(connection, $"SELECT name FROM sqlite_schema WHERE type='table' AND name='{table}';")));
    }

    [Fact]
    public void MigrationOrderPlacesProjectionAfterBothSkillAuthorities()
    {
        var order = Assert.IsType<string[]>(typeof(SqliteRuntimeBackupService)
            .GetField("MigrationOrder", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null));
        var projection = Array.IndexOf(order, "local_workspace_projection");
        Assert.True(projection > Array.IndexOf(order, "skill_projection"));
        Assert.True(projection > Array.IndexOf(order, "skill_invocation_snapshot"));
        Assert.True(projection > Array.IndexOf(order, "retention"));
        Assert.True(projection > Array.IndexOf(order, "local_repository_catalog"));
        Assert.True(projection > Array.IndexOf(order, "local_archive"));
    }

    [Theory]
    [InlineData("retention")]
    [InlineData("skill_projection")]
    [InlineData("skill_invocation_snapshot")]
    [InlineData("local_repository_catalog")]
    [InlineData("local_archive")]
    public void WorkspaceComponentParentGateRejectsEachMissingCurrentDependency(string missing)
    {
        var versions = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["session"] = 14,
            ["retention"] = 1,
            ["skill_projection"] = 1,
            ["skill_invocation_snapshot"] = 1,
            ["local_repository_catalog"] = 1,
            ["local_archive"] = 1,
            ["local_workspace_projection"] = 5,
        };
        versions.Remove(missing);
        var method = typeof(SqliteRuntimeBackupService).GetMethod(
            "HasValidLocalWorkspaceProjectionParents", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.False(Assert.IsType<bool>(method.Invoke(null, [versions])));
    }

    [Fact]
    public void DurableOtelToolCannotAuthenticateAfterItsNormalizedMonitorSpanCarrierIsRemoved()
    {
        using var connection = OpenUnavailableOtelToolFixture();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "DELETE FROM monitor_spans;");
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    [Fact]
    public void ValidationRejectsRecordedLabelWithoutExactAuthorizedContent()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE local_workspace_sessions SET label_state='recorded',label_text='x',label_source_identity='0198f5b8-0c00-7000-8000-000000000099',label_expires_at='2099-01-01T00:00:00.0000000+00:00'; INSERT INTO local_workspace_session_search_facts VALUES((SELECT session_id FROM local_workspace_sessions),'label','0198f5b8-0c00-7000-8000-000000000099','x','2099-01-01T00:00:00.0000000+00:00');");

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
    }

    [Fact]
    public void ValidationRejectsFactExpiredAtBackupPublicationTime()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000001','active','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO local_workspace_session_search_facts VALUES((SELECT session_id FROM sessions),'label','stale','stale','2026-08-26T00:00:00.0000000+00:00');");

        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction, DateTimeOffset.Parse("2026-08-26T00:00:00Z"))).Message);
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string Text(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }

    private static string[] Strings(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static void RewriteWriterVersion(string source, string output, string writerVersion)
    {
        byte[] manifest;
        byte[] database;
        using (var archive = ZipFile.OpenRead(source))
        {
            manifest = Read(archive.GetEntry("manifest.json")!);
            database = Read(archive.GetEntry("database.sqlite")!);
        }
        var parsed = RuntimeBackupJson.ParseManifest(manifest);
        using var target = ZipFile.Open(output, ZipArchiveMode.Create);
        Write(target, "manifest.json", RuntimeBackupJson.WriteManifest(parsed with { SourceApplicationVersion = writerVersion }));
        Write(target, "database.sqlite", database);
    }

    private static RuntimeBackupManifestData ReadManifest(string source)
    {
        using var archive = ZipFile.OpenRead(source);
        return RuntimeBackupJson.ParseManifest(Read(archive.GetEntry("manifest.json")!));
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var memory = new MemoryStream();
        source.CopyTo(memory);
        return memory.ToArray();
    }

    private static void Write(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        entry.ExternalAttributes = 0;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    private sealed class ConfiguredBackupFixture : IDisposable
    {
        private readonly LocalRepositoryCatalogFixture fixture = new();

        internal ConfiguredBackupFixture(DateTimeOffset publicationAt, DateTimeOffset expiresAt)
        {
            Clock = new RecordingTimeProvider(publicationAt);
            Gate = new LocalWorkspacePublicationGate();
            Authority = new SkillInvocationV2RegistryProviderV1(SkillInvocationV2ArtifactRegistry.Load(), Gate);
            Service = new SqliteRuntimeBackupService(Clock, Authority, Gate);
            var initialized = Service.Initialize(DatabasePath);
            Assert.True(initialized.Success, initialized.ErrorCode);
            var participant = new LocalWorkspaceProjectionTransactionParticipant(Authority);
            var facts = SkillInvocationV2IngestRequestFactsV1.Derive(
                SkillInvocationV2Parser.Parse(Encoding.UTF8.GetBytes(ValidRequest), new ProductionRuntimeCapability()));
            var ingested = SkillInvocationV2IngestTransactionV1.Execute(
                DatabasePath, facts, Authority, new RecordingTimeProvider(expiresAt.AddDays(-90)),
                () => true, () => true, CancellationToken.None, Gate, participant);
            Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, ingested.Outcome);
        }

        internal string DatabasePath => fixture.DatabasePath;
        internal RecordingTimeProvider Clock { get; }
        internal SkillInvocationV2RegistryProviderV1 Authority { get; }
        internal LocalWorkspacePublicationGate Gate { get; }
        internal SqliteRuntimeBackupService Service { get; }
        internal string Path(string name) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath)!, name);
        public void Dispose() => fixture.Dispose();

        private const string ValidRequest = "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"backup-sdk-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":null,\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"demo-skill\",\"path\":\"skills/demo/SKILL.md\",\"content\":\"synthetic body\",\"source\":\"project\",\"trigger\":\"user-invoked\"}}]}";
    }

    private sealed class HistoricalBackupFixture : IDisposable
    {
        private readonly LocalRepositoryCatalogFixture fixture = new();

        internal HistoricalBackupFixture(DateTimeOffset publicationAt, DateTimeOffset expiresAt)
        {
            Clock = new RecordingTimeProvider(publicationAt);
            var gate = new LocalWorkspacePublicationGate();
            var authority = FixedSkillRegistryGenerationAuthority.ForWriterVersion("1.0.0");
            Service = new SqliteRuntimeBackupService(Clock, authority, gate, "1.0.0");
            Assert.True(Service.Initialize(DatabasePath).Success);
            var participant = new LocalWorkspaceProjectionTransactionParticipant(authority);
            var facts = SkillInvocationV2IngestRequestFactsV1.Derive(
                SkillInvocationV2Parser.Parse(Encoding.UTF8.GetBytes(HistoricalRequest), new HistoricalRuntimeCapability()));
            var ingested = SkillInvocationV2IngestTransactionV1.Execute(
                DatabasePath, facts, authority, new RecordingTimeProvider(expiresAt.AddDays(-90)),
                () => true, () => true, CancellationToken.None, gate, participant);
            Assert.Equal(SkillInvocationV2IngestOutcomeV1.Committed, ingested.Outcome);
        }

        internal string DatabasePath => fixture.DatabasePath;
        internal RecordingTimeProvider Clock { get; }
        internal SqliteRuntimeBackupService Service { get; }
        internal string Path(string name) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath)!, name);
        public void Dispose() => fixture.Dispose();

        private const string HistoricalRequest = "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"backup-sdk-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":null,\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"historical-skill\",\"path\":\"skills/historical/SKILL.md\",\"content\":\"synthetic historical body\",\"source\":\"project\",\"trigger\":\"user-invoked\"}}]}";
    }

    private sealed class HistoricalRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1 CertifiedIdentity { get; } =
            new("1.0.65", 3, "copilot-sdk-dotnet-1.0.4+cao-skill-v2.1",
                "github-copilot-sdk.skill-invoked.normalize.v1", "github-copilot-sdk.skill-invoked.v1",
                "8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c", 1);
    }

    private sealed class ProductionRuntimeCapability : ISkillInvocationV2RuntimeCapability
    {
        public CopilotAgentObservability.LocalMonitor.SkillRuntime.CertifiedSkillProducerIdentityV1 CertifiedIdentity { get; } =
            SkillInvocationV2TestIdentity.V1065;
    }

    private sealed class RecordingTimeProvider(DateTimeOffset instant) : TimeProvider
    {
        internal DateTimeOffset? FirstObservedInstant { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            FirstObservedInstant ??= instant;
            return instant;
        }
    }
}

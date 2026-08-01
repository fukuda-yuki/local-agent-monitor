using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryAutomaticAdmissionValidationTests
{
    [Fact]
    public async Task ValidAutomaticAdmissionGraphIsRestorable()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Widget.git")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        await fixture.RunPreparedAsync(prepared);
        fixture.Execute($"""
            INSERT INTO monitor_spans(
                raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,
                tool_name,tool_type,mcp_tool_name,mcp_server_hash,agent_name,request_model,
                response_model,input_tokens,output_tokens,total_tokens,reasoning_tokens,
                cache_read_tokens,cache_creation_tokens,status,error_type,finish_reasons,
                conversation_id,duration_ms,start_time,end_time,projected_at)
            VALUES(
                {prepared.RawRecordId},'{LocalRepositoryAdmissionFixture.Trace(1)}',NULL,NULL,0,
                NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                NULL,NULL,NULL,NULL,NULL,'1970-01-01T00:00:00.0000000+00:00');
            INSERT INTO local_repository_reconciliation_state(
                projector_key,last_discovered_span_id,updated_at)
            VALUES(
                'local-repository-catalog-v1',(SELECT MAX(id) FROM monitor_spans),
                '2026-08-01T00:00:00.0000000+00:00');
            """);
        using var connection = Open(fixture.DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: true);
        var reconciliationState = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(
            connection,
            transaction);

        SqliteLocalRepositoryCatalogStore.ValidateRestorableAutomaticAdmissionState(
            connection,
            transaction,
            reconciliationState);

        transaction.Rollback();
    }

    [Fact]
    public async Task AcceptedObservationContextAndOwnerVariantsAreRestorable()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var sharedSession = LocalRepositoryAdmissionFixture.Session(20);
        var manualOwner = fixture.SeedManualOwner("https://github.com/Manual/Owner");
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Observed")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1, sharedSession)]);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(2),
                LocalRepositoryAdmissionFixture.Span(2),
                "https://github.com/Example/Observed")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(2, sharedSession)]);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.ResourcePayload(
                "https://github.com/Example/Resource",
                (LocalRepositoryAdmissionFixture.Trace(3), LocalRepositoryAdmissionFixture.Span(3)),
                (LocalRepositoryAdmissionFixture.Trace(4), LocalRepositoryAdmissionFixture.Span(4))),
            [
                LocalRepositoryAdmissionFixture.MatchedEvent(3),
                LocalRepositoryAdmissionFixture.MatchedEvent(4),
            ]);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.ResourceAndSpanPayload(
                "https://github.com/Example/Shadowed",
                LocalRepositoryAdmissionFixture.Trace(5),
                LocalRepositoryAdmissionFixture.Span(5),
                "not-a-locator"),
            [LocalRepositoryAdmissionFixture.MatchedEvent(5)]);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.DuplicateAndInvalidTypePayload(
                LocalRepositoryAdmissionFixture.Trace(6),
                LocalRepositoryAdmissionFixture.Span(6)),
            [LocalRepositoryAdmissionFixture.MatchedEvent(6)]);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(7),
                LocalRepositoryAdmissionFixture.Span(7),
                "https://github.com/Manual/Owner")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(7)]);
        CompleteDiscovery(fixture);

        Validate(fixture);

        Assert.Equal(1, fixture.ScalarLong($"SELECT COUNT(*) FROM local_repository_history WHERE repository_id='{manualOwner.RepositoryId}';"));
        Assert.Equal(2, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_observation_contexts WHERE session_id='{sharedSession}' AND admission_state='admitted';"));
        Assert.Equal(1, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{sharedSession}' AND action='automatic_reconcile';"));
    }

    [Fact]
    public async Task CaseVariantManualOwnerReuseIsRestorable()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var owner = fixture.SeedManualOwner("https://github.com/Manual/OwnerCase");
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "git@github.com:manual/ownercase.git")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        CompleteDiscovery(fixture);

        Assert.Equal(owner.RepositoryId, fixture.ScalarText("SELECT repository_id FROM session_repository_observation_contexts;"));
        Assert.Equal("Manual", fixture.ScalarText("SELECT display_owner FROM local_repository_locators;"));
        Assert.Equal("manual", fixture.ScalarText("SELECT display_owner FROM session_repository_observations;"));

        Validate(fixture);
    }

    [Fact]
    public async Task CaseVariantObservedOwnerReuseIsRestorable()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/FirstCase")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(2),
                LocalRepositoryAdmissionFixture.Span(2),
                "https://github.com/example/FIRSTCASE")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(2)]);
        CompleteDiscovery(fixture);

        Assert.Equal(repositoryId, fixture.ScalarText("SELECT repository_id FROM session_repository_observation_contexts ORDER BY observed_at DESC LIMIT 1;"));
        Assert.Equal("Example", fixture.ScalarText("SELECT display_owner FROM local_repository_locators;"));
        Assert.Equal("example", fixture.ScalarText("SELECT display_owner FROM session_repository_observations ORDER BY raw_record_id DESC LIMIT 1;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history WHERE action='create_observed';"));

        Validate(fixture);
    }

    [Fact]
    public async Task NewEvidenceUnderManualOverridesAndResumedAutomaticStateIsRestorable()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var assignedSession = LocalRepositoryAdmissionFixture.Session(30);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(30),
                LocalRepositoryAdmissionFixture.Span(30),
                "https://github.com/Example/Assigned")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(30, assignedSession)]);
        var assignedRepository = fixture.ScalarText("SELECT repository_id FROM local_repositories ORDER BY repository_id LIMIT 1;");
        fixture.SeedManualAssignment(assignedSession, assignedRepository);
        var assignedRevision = fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{assignedSession}';");
        var assignedHistoryCount = fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{assignedSession}';");
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(31),
                LocalRepositoryAdmissionFixture.Span(31),
                "https://github.com/Example/AssignedAdditional")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(31, assignedSession)]);
        Assert.Equal(assignedRevision, fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{assignedSession}';"));
        Assert.Equal(assignedHistoryCount, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{assignedSession}';"));

        var explicitSession = LocalRepositoryAdmissionFixture.Session(40);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(40),
                LocalRepositoryAdmissionFixture.Span(40),
                "https://github.com/Example/Explicit")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(40, explicitSession)]);
        SeedExplicitUnassign(fixture, explicitSession);
        var explicitRevision = fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{explicitSession}';");
        var explicitHistoryCount = fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{explicitSession}';");
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(41),
                LocalRepositoryAdmissionFixture.Span(41),
                "https://github.com/Example/ExplicitAdditional")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(41, explicitSession)]);
        Assert.Equal(explicitRevision, fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{explicitSession}';"));
        Assert.Equal(explicitHistoryCount, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{explicitSession}';"));

        SeedResumeAutomatic(fixture, assignedSession);
        CompleteDiscovery(fixture);

        Validate(fixture);
    }

    [Theory]
    [MemberData(nameof(SemanticCorruptions))]
    public async Task SemanticContradictionsUseOneValueFreeFailure(string corruption)
    {
        using var fixture = await CreateValidGraphAsync();
        fixture.Execute(corruption);

        var error = Assert.Throws<InvalidOperationException>(() => Validate(fixture));

        Assert.Equal("local_repository_automatic_admission_state_invalid", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(LocalRepositoryAdmissionFixture.Session(1), error.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> SemanticCorruptions()
    {
        yield return ["DROP TRIGGER session_repository_observations_update_rejected; UPDATE session_repository_observations SET source_identity_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff';"];
        yield return ["DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET context_identity_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff';"];
        yield return ["DROP TRIGGER source_schema_observations_projection_input_update_rejected; UPDATE source_schema_observations SET raw_payload_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff';"];
        yield return ["UPDATE source_schema_observations SET source_surface='github-copilot-vscode';"];
        yield return ["UPDATE source_schema_observations SET source_application_version='bad/version';"];
        yield return ["UPDATE source_schema_observations SET observed_at='2026-08-01T01:02:04.1234567+00:00';"];
        yield return ["UPDATE session_events SET type='message';"];
        yield return ["UPDATE session_events SET trace_id='ffffffffffffffffffffffffffffffff';"];
        yield return ["UPDATE session_events SET source_surface='vscode';"];
        yield return ["PRAGMA foreign_keys=OFF; DELETE FROM session_events;"];
        yield return ["PRAGMA foreign_keys=OFF; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET session_event_id='01900000-0000-7000-9000-000000000099';"];
        yield return ["PRAGMA foreign_keys=OFF; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET session_id='01900000-0000-7000-8000-000000000099';"];
        yield return ["PRAGMA foreign_keys=OFF; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET repository_id='01900000-0000-7000-8000-000000000099',locator_id='01900000-0000-7000-8000-000000000098' WHERE admission_state='admitted';"];
        yield return ["DROP TRIGGER local_repository_locators_update_rejected; UPDATE local_repository_locators SET canonical_locator='github.com/example/different';"];
        yield return ["PRAGMA ignore_check_constraints=ON; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET admission_state='invalid_locator' WHERE admission_state='admitted';"];
        yield return ["PRAGMA ignore_check_constraints=ON; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET admission_state='invalid_locator',repository_id=NULL,locator_id=NULL WHERE admission_state='admitted';"];
        yield return ["PRAGMA ignore_check_constraints=ON; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET admission_state='shadowed',repository_id=NULL,locator_id=NULL;"];
        yield return ["PRAGMA foreign_keys=OFF; DROP TRIGGER session_repository_observation_contexts_delete_rejected; DELETE FROM session_repository_observation_contexts;"];
        yield return ["DROP TRIGGER local_repository_history_delete_rejected; DELETE FROM local_repository_history WHERE action='create_observed';"];
        yield return ["PRAGMA ignore_check_constraints=ON; INSERT INTO local_repository_history(history_id,repository_id,action,previous_revision,new_revision,locator_id,cause_kind,operation_key,context_identity_sha256,occurred_at) SELECT '01900000-0000-7000-8000-00000000ff01',repository_id,'create_observed',1,2,locator_id,cause_kind,NULL,context_identity_sha256,occurred_at FROM local_repository_history WHERE action='create_observed';"];
        yield return ["PRAGMA foreign_keys=OFF; DROP TRIGGER local_repository_history_update_rejected; UPDATE local_repository_history SET locator_id='01900000-0000-7000-8000-00000000ff02' WHERE action='create_observed';"];
        yield return ["PRAGMA foreign_keys=OFF; DROP TRIGGER session_repository_assignment_history_update_rejected; INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) VALUES('01900000-0000-7000-8000-00000000ff03','completed','full','2026-08-01T01:02:03.1234567+00:00','not_captured','2026-08-01T01:02:03.1234567+00:00','2026-08-01T01:02:03.1234567+00:00'); UPDATE session_repository_assignment_history SET session_id='01900000-0000-7000-8000-00000000ff03' WHERE action='automatic_reconcile';"];
        yield return ["PRAGMA ignore_check_constraints=ON; INSERT INTO session_repository_assignment_history(history_id,session_id,action,previous_revision,new_revision,previous_assignment_state_sha256,new_assignment_state_sha256,previous_state,new_state,previous_authority,new_authority,previous_repository_id,new_repository_id,cause_kind,operation_key,reconciliation_fingerprint,occurred_at) SELECT '01900000-0000-7000-8000-00000000ff04',session_id,action,1,2,previous_assignment_state_sha256,new_assignment_state_sha256,previous_state,new_state,previous_authority,new_authority,previous_repository_id,new_repository_id,cause_kind,NULL,reconciliation_fingerprint,occurred_at FROM session_repository_assignment_history WHERE action='automatic_reconcile';"];
    }

    [Fact]
    public async Task ResourcePrecedenceContradictionAndSpanShadowingAreRejected()
    {
        using var resourceFixture = new LocalRepositoryAdmissionFixture();
        await resourceFixture.RunAsync(
            LocalRepositoryAdmissionFixture.ResourcePayload(
                "https://github.com/Example/ResourceOnly",
                (LocalRepositoryAdmissionFixture.Trace(50), LocalRepositoryAdmissionFixture.Span(50))),
            [LocalRepositoryAdmissionFixture.MatchedEvent(50)]);
        CompleteDiscovery(resourceFixture);
        resourceFixture.Execute("PRAGMA ignore_check_constraints=ON; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET admission_state='shadowed',repository_id=NULL,locator_id=NULL;");
        Assert.Equal("local_repository_automatic_admission_state_invalid", Assert.Throws<InvalidOperationException>(() => Validate(resourceFixture)).Message);

        using var spanFixture = await CreateValidGraphAsync();
        spanFixture.Execute("PRAGMA ignore_check_constraints=ON; DROP TRIGGER session_repository_observation_contexts_update_rejected; UPDATE session_repository_observation_contexts SET admission_state='shadowed',repository_id=NULL,locator_id=NULL;");
        Assert.Equal("local_repository_automatic_admission_state_invalid", Assert.Throws<InvalidOperationException>(() => Validate(spanFixture)).Message);
    }

    [Fact]
    public async Task DuplicateExactSessionIdentityIsRejected()
    {
        using var fixture = await CreateValidGraphAsync();
        fixture.Execute("""
            PRAGMA foreign_keys=OFF;
            CREATE TABLE duplicate_session_events AS SELECT * FROM session_events;
            INSERT INTO duplicate_session_events
            SELECT '01900000-0000-7000-9000-00000000ff05',session_id,run_id,source_surface,
                   parent_event_id,trace_id,status,source_adapter,source_event_id,type,occurred_at,
                   content_state,source_application_version,adapter_version,schema_fingerprint,
                   normalization_version,match_kind
            FROM session_events LIMIT 1;
            DROP TABLE session_events;
            ALTER TABLE duplicate_session_events RENAME TO session_events;
            """);

        var error = Assert.Throws<InvalidOperationException>(() => Validate(fixture));

        Assert.Equal("local_repository_automatic_admission_state_invalid", error.Message);
    }

    [Fact]
    public async Task AutomaticTransitionTouchingManualAuthorityIsRejectedThroughResolverAuthority()
    {
        using var fixture = await CreateValidGraphAsync();
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories LIMIT 1;");
        var manualFingerprint = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState("assigned", "manual", repositoryId, []));
        fixture.Execute($"""
            PRAGMA ignore_check_constraints=ON;
            DROP TRIGGER session_repository_assignment_history_update_rejected;
            UPDATE session_repository_assignment_history
            SET previous_state='assigned',previous_authority='manual',
                previous_repository_id='{repositoryId}',
                previous_assignment_state_sha256='{manualFingerprint}'
            WHERE action='automatic_reconcile';
            """);

        var error = Assert.Throws<InvalidOperationException>(() => Validate(fixture));

        Assert.Equal("local_repository_automatic_admission_state_invalid", error.Message);
    }

    [Fact]
    public async Task AutomaticCreationCauseCannotTargetAManualLocator()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var manual = fixture.SeedManualOwner("https://github.com/Manual/Owner");
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(60),
                LocalRepositoryAdmissionFixture.Span(60),
                "https://github.com/manual/owner")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(60)]);
        CompleteDiscovery(fixture);
        var contextIdentity = fixture.ScalarText("SELECT context_identity_sha256 FROM session_repository_observation_contexts LIMIT 1;");
        fixture.Execute($"""
            PRAGMA ignore_check_constraints=ON;
            INSERT INTO local_repository_history(
                history_id,repository_id,action,previous_revision,new_revision,locator_id,
                cause_kind,operation_key,context_identity_sha256,occurred_at)
            VALUES(
                '01900000-0000-7000-8000-00000000ff06','{manual.RepositoryId}',
                'create_observed',1,2,'{manual.LocatorId}','source_context',NULL,
                '{contextIdentity}','2026-08-01T01:02:03.1234567+00:00');
            """);

        var error = Assert.Throws<InvalidOperationException>(() => Validate(fixture));

        Assert.Equal("local_repository_automatic_admission_state_invalid", error.Message);
    }

    [Fact]
    public async Task IndependentlyCanonicalCreationTimestampsMayDiffer()
    {
        using var fixture = await CreateValidGraphAsync();
        fixture.Execute("""
            DROP TRIGGER local_repository_locators_update_rejected;
            DROP TRIGGER local_repository_history_update_rejected;
            UPDATE local_repositories SET created_at='2026-08-01T01:02:04.1234567+00:00';
            UPDATE local_repository_locators SET created_at='2026-08-01T01:02:05.1234567+00:00';
            UPDATE local_repository_locator_heads SET updated_at='2026-08-01T01:02:06.1234567+00:00';
            UPDATE local_repository_history SET occurred_at='2026-08-01T01:02:07.1234567+00:00';
            """);

        Validate(fixture);
    }

    [Fact]
    public async Task LaterRenameMayDifferFromImmutableObservedDisplayName()
    {
        using var fixture = await CreateValidGraphAsync();
        fixture.Execute("UPDATE local_repositories SET display_name='Renamed Widget',revision=2,updated_at='2026-08-01T01:02:08.1234567+00:00';");

        Validate(fixture);
    }

    [Fact]
    public async Task ProofMismatchWinsBeforeSemanticMaterialization()
    {
        using var fixture = await CreateValidGraphAsync();
        fixture.Execute("DROP TRIGGER session_repository_observations_update_rejected; UPDATE session_repository_observations SET source_identity_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff';");
        using var proofConnection = Open(fixture.DatabasePath);
        using var proofTransaction = proofConnection.BeginTransaction(deferred: true);
        var lookupCounts = new List<int>();
        var proof = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(
            proofConnection,
            proofTransaction,
            null,
            lookupCounts.Add);
        using var otherConnection = Open(fixture.DatabasePath);
        using var otherTransaction = otherConnection.BeginTransaction(deferred: true);

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqliteLocalRepositoryCatalogStore.ValidateRestorableAutomaticAdmissionState(
                otherConnection,
                otherTransaction,
                proof));

        Assert.Equal("local_repository_reconciliation_state_transaction_mismatch", error.Message);
        Assert.Empty(lookupCounts);
        otherTransaction.Rollback();
        proofTransaction.Rollback();
    }

    [Fact]
    public async Task ValidationUsesOnlySelectAndPreservesEveryStoredValue()
    {
        using var fixture = await CreateValidGraphAsync();
        using var connection = Open(fixture.DatabasePath);
        var before = Snapshot(connection);
        using var transaction = connection.BeginTransaction(deferred: true);
        using (var queryOnly = connection.CreateCommand())
        {
            queryOnly.Transaction = transaction;
            queryOnly.CommandText = "PRAGMA query_only=ON;";
            queryOnly.ExecuteNonQuery();
        }
        var proof = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction);

        SqliteLocalRepositoryCatalogStore.ValidateRestorableAutomaticAdmissionState(connection, transaction, proof);

        Assert.Equal(0, ScalarLong(connection, "SELECT total_changes();"));
        transaction.Rollback();
        Assert.Equal(before, Snapshot(connection));
    }

    [Fact]
    public async Task MoreThanTwoPagesAreFullyTraversedAndLateCorruptionIsRejected()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        for (var index = 1; index <= 257; index++)
        {
            await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                    LocalRepositoryAdmissionFixture.Trace(index),
                    LocalRepositoryAdmissionFixture.Span(index),
                    $"https://github.com/Example/Repo{index}")),
                [LocalRepositoryAdmissionFixture.MatchedEvent(index)]);
        }
        CompleteDiscovery(fixture);

        Validate(fixture);
        Assert.Equal(257, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(257, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Equal(257, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history WHERE action='create_observed';"));
        Assert.Equal(257, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history WHERE action='automatic_reconcile';"));

        fixture.Execute("DROP TRIGGER session_repository_observations_update_rejected; UPDATE session_repository_observations SET source_identity_sha256='ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff' WHERE raw_record_id=(SELECT MAX(raw_record_id) FROM session_repository_observations);");
        var error = Assert.Throws<InvalidOperationException>(() => Validate(fixture));
        Assert.Equal("local_repository_automatic_admission_state_invalid", error.Message);
    }

    private static async Task<LocalRepositoryAdmissionFixture> CreateValidGraphAsync()
    {
        var fixture = new LocalRepositoryAdmissionFixture();
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Widget.git")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        CompleteDiscovery(fixture);
        return fixture;
    }

    private static void CompleteDiscovery(LocalRepositoryAdmissionFixture fixture) => fixture.Execute("""
        INSERT INTO monitor_spans(
            raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,
            tool_name,tool_type,mcp_tool_name,mcp_server_hash,agent_name,request_model,
            response_model,input_tokens,output_tokens,total_tokens,reasoning_tokens,
            cache_read_tokens,cache_creation_tokens,status,error_type,finish_reasons,
            conversation_id,duration_ms,start_time,end_time,projected_at)
        SELECT q.raw_record_id,printf('%032x',q.raw_record_id),NULL,NULL,0,
               NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
               NULL,NULL,NULL,NULL,NULL,'1970-01-01T00:00:00.0000000+00:00'
        FROM local_repository_reconciliation_queue q
        WHERE NOT EXISTS(SELECT 1 FROM monitor_spans s WHERE s.raw_record_id=q.raw_record_id);
        INSERT INTO local_repository_reconciliation_state(
            projector_key,last_discovered_span_id,updated_at)
        VALUES(
            'local-repository-catalog-v1',(SELECT MAX(id) FROM monitor_spans),
            '2026-08-01T00:00:00.0000000+00:00')
        ON CONFLICT(projector_key) DO UPDATE
        SET last_discovered_span_id=excluded.last_discovered_span_id,
            updated_at=excluded.updated_at;
        """);

    private static void Validate(LocalRepositoryAdmissionFixture fixture)
    {
        using var connection = Open(fixture.DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: true);
        var proof = SqliteLocalRepositoryReconciliationStore.ValidateRestorableState(connection, transaction);
        SqliteLocalRepositoryCatalogStore.ValidateRestorableAutomaticAdmissionState(connection, transaction, proof);
        transaction.Rollback();
    }

    private static void SeedExplicitUnassign(LocalRepositoryAdmissionFixture fixture, string sessionId)
    {
        var previousFingerprint = fixture.ScalarText($"SELECT new_assignment_state_sha256 FROM session_repository_assignment_history WHERE session_id='{sessionId}' ORDER BY new_revision DESC LIMIT 1;");
        var fingerprint = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState("explicitly_unassigned", "manual", null, []));
        var operationKey = OperationKey(10);
        fixture.Execute($"""
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at)
            VALUES('{operationKey}','{new string('d', 64)}',200,'application/json; charset=utf-8','no-store',X'7B7D','2026-08-01T01:02:03.1234567+00:00');
            UPDATE session_repository_assignment_revisions SET revision=2 WHERE session_id='{sessionId}';
            INSERT INTO session_repository_assignment_history(
                history_id,session_id,action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at)
            SELECT '01900000-0000-7000-8000-00000000ee01','{sessionId}','explicitly_unassign',1,2,
                   '{previousFingerprint}','{fingerprint}',new_state,'explicitly_unassigned',new_authority,'manual',
                   new_repository_id,NULL,'user_operation','{operationKey}',NULL,'2026-08-01T01:02:03.1234567+00:00'
            FROM session_repository_assignment_history WHERE session_id='{sessionId}' AND new_revision=1;
            INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at)
            VALUES('{sessionId}','explicitly_unassigned',NULL,2,'2026-08-01T01:02:03.1234567+00:00');
            """);
    }

    private static void SeedResumeAutomatic(LocalRepositoryAdmissionFixture fixture, string sessionId)
    {
        var previousFingerprint = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState(
                "assigned",
                "manual",
                fixture.ScalarText($"SELECT repository_id FROM session_repository_manual_overrides WHERE session_id='{sessionId}';"),
                []));
        var candidates = fixture.QueryStrings($"SELECT DISTINCT repository_id FROM session_repository_observation_contexts WHERE session_id='{sessionId}' AND admission_state='admitted' ORDER BY repository_id;");
        var state = candidates.Length == 1 ? "assigned" : "conflict";
        var repositoryId = candidates.Length == 1 ? candidates[0] : null;
        var nextFingerprint = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState(state, "automatic", repositoryId, candidates));
        var operationKey = OperationKey(11);
        fixture.Execute($"""
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at)
            VALUES('{operationKey}','{new string('e', 64)}',200,'application/json; charset=utf-8','no-store',X'7B7D','2026-08-01T01:02:03.1234567+00:00');
            DELETE FROM session_repository_manual_overrides WHERE session_id='{sessionId}';
            UPDATE session_repository_assignment_revisions SET revision=3 WHERE session_id='{sessionId}';
            INSERT INTO session_repository_assignment_history(
                history_id,session_id,action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at)
            VALUES(
                '01900000-0000-7000-8000-00000000ee02','{sessionId}','resume_automatic',2,3,
                '{previousFingerprint}','{nextFingerprint}','assigned','{state}','manual','automatic',
                (SELECT new_repository_id FROM session_repository_assignment_history WHERE session_id='{sessionId}' AND new_revision=2),
                {(repositoryId is null ? "NULL" : $"'{repositoryId}'")},'user_operation','{operationKey}',NULL,
                '2026-08-01T01:02:03.1234567+00:00');
            """);
    }

    private static string Snapshot(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT group_concat(value,'|') FROM (
                SELECT 'q:'||queue_id||':'||state||':'||attempt_count AS value FROM local_repository_reconciliation_queue
                UNION ALL SELECT 'o:'||observation_id||':'||source_identity_sha256 FROM session_repository_observations
                UNION ALL SELECT 'c:'||context_id||':'||context_identity_sha256 FROM session_repository_observation_contexts
                UNION ALL SELECT 'r:'||repository_id||':'||revision FROM local_repositories
                UNION ALL SELECT 'h:'||history_id||':'||action FROM local_repository_history
                UNION ALL SELECT 'a:'||history_id||':'||action FROM session_repository_assignment_history
                ORDER BY value COLLATE BINARY);
            """;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string OperationKey(byte fill) =>
        "lrc1_" + Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }
}

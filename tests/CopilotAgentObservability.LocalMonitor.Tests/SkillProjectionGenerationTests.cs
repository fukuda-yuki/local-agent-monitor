using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionGenerationTests
{
    private const string TraceId = "22222222222222222222222222222222";
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ResolvedOrdinaryIngestion_AdvancesExactFrontierWithoutBumpingCompatibilityRevision()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);

        ingestion.Commit(CreateBatch("ordinary-generation-1", ObservedAt));
        using (var connection = Open(database.Path))
        {
            Assert.Equal(0, ScalarLong(connection, "SELECT current_revision FROM source_trace_compatibility_revisions;"));
            Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_generations;"));
            Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_generation_inputs;"));
            Assert.Equal("pending", ScalarText(connection, "SELECT state FROM skill_projection_queue;"));
        }

        ingestion.Commit(CreateBatch("ordinary-generation-2", ObservedAt.AddSeconds(1)));

        using var final = Open(database.Path);
        Assert.Equal(0, ScalarLong(final, "SELECT current_revision FROM source_trace_compatibility_revisions;"));
        Assert.Equal(2, ScalarLong(final, "SELECT COUNT(*) FROM skill_projection_generations;"));
        Assert.Equal(3, ScalarLong(final, "SELECT COUNT(*) FROM skill_projection_generation_inputs;"));
        Assert.Equal(2, ScalarLong(final, "SELECT desired_generation_id FROM skill_projection_trace_heads;"));
        Assert.Equal("superseded", ScalarText(final, "SELECT lifecycle FROM skill_projection_generations WHERE generation_id=1;"));
        Assert.Equal("superseded", ScalarText(final, "SELECT state FROM skill_projection_queue WHERE generation_id=1;"));
        Assert.Equal("pending", ScalarText(final, "SELECT state FROM skill_projection_queue WHERE generation_id=2;"));
    }

    [Fact]
    public void FailedTerminalDesiredGeneration_IsCurrentSchemaValidAndRoundTripsThroughRuntimeBackup()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("failed-terminal-generation", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var lease = Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(ObservedAt.AddSeconds(1)));

        var outcome = store.RecordTerminal(
            lease,
            ObservedAt.AddSeconds(2),
            "skill_projection_input_digest_mismatch");

        Assert.Equal(SkillProjectionWorkOutcome.FailedTerminal, outcome);
        using (var connection = Open(database.Path))
        {
            Assert.Equal(
                "failed_terminal",
                ScalarText(connection, "SELECT lifecycle FROM skill_projection_generations;"));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM skill_projection_queue
                    WHERE state='failed_terminal'
                      AND attempt_count=1
                      AND lease_generation=1
                      AND lease_owner IS NULL
                      AND lease_expires_at IS NULL
                      AND next_attempt_at IS NULL
                      AND error_code='skill_projection_input_digest_mismatch';
                    """));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    """
                    SELECT COUNT(*)
                    FROM skill_projection_trace_heads
                    WHERE desired_generation_id=1
                      AND current_generation_id IS NULL;
                    """));
            Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
            Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_inventories;"));
            Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_inventory_names;"));
            SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
            SkillProjectionSchemaV1.Validate(connection, transaction: null);
        }

        using var target = new TestDatabase();
        new SqliteSourceCompatibilityStore(target.Path).CreateSchema();
        var bundle = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(database.Path)!,
            "failed-terminal.zip");
        var backup = new SqliteRuntimeBackupService();
        Assert.True(backup.CreateAndPublish(database.Path, bundle).Success);
        Assert.True(backup.Inspect(bundle).Success);
        Assert.True(backup.Preview(bundle, target.Path).Success);
        Assert.True(backup.Restore(bundle, target.Path, new RuntimeRestoreOptions()).Success);
        using var restored = Open(target.Path);
        SourceCompatibilitySchemaV11.Validate(restored, transaction: null);
        SkillProjectionSchemaV1.Validate(restored, transaction: null);
    }

    [Fact]
    public void FailedTerminalDesiredGeneration_IsSupersededWhenExactFrontierExtends()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("failed-terminal-frontier-1", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var lease = Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(ObservedAt.AddSeconds(1)));
        Assert.Equal(
            SkillProjectionWorkOutcome.FailedTerminal,
            store.RecordTerminal(
                lease,
                ObservedAt.AddSeconds(2),
                "skill_projection_input_digest_mismatch"));

        AppendExactObservation(
            database.Path,
            CreateBatch("failed-terminal-frontier-2", ObservedAt.AddSeconds(3), SkillPayload));

        using var connection = Open(database.Path);
        Assert.Equal(
            "superseded",
            ScalarText(
                connection,
                "SELECT lifecycle FROM skill_projection_generations WHERE generation_id=$generation_id;",
                ("$generation_id", lease.GenerationId)));
        Assert.Equal(
            "superseded",
            ScalarText(
                connection,
                "SELECT state FROM skill_projection_queue WHERE generation_id=$generation_id;",
                ("$generation_id", lease.GenerationId)));
        Assert.Equal(
            1,
            ScalarLong(
                connection,
                """
                SELECT COUNT(*)
                FROM skill_projection_trace_heads AS head
                JOIN skill_projection_generations AS generation
                  ON generation.generation_id=head.desired_generation_id
                JOIN skill_projection_queue AS queue
                  ON queue.generation_id=generation.generation_id
                WHERE head.trace_id=$trace_id
                  AND head.desired_generation_id<>$old_generation_id
                  AND head.current_generation_id IS NULL
                  AND generation.lifecycle='pending'
                  AND queue.state='pending';
                """,
                ("$trace_id", TraceId),
                ("$old_generation_id", lease.GenerationId)));
        SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
        SkillProjectionSchemaV1.Validate(connection, transaction: null);
    }

    [Fact]
    public async Task OTelWorker_PublishesOneCurrentExactSpanClaimAndRetryDoesNotDuplicate()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        ingestion.Commit(CreateBatch("worker-generation", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var rawStore = new RawTelemetryStore(database.Path, retention);
        var store = new SqliteSkillProjectionStore(database.Path, rawStore);
        var worker = new SkillProjectionWorker(store);

        var first = await worker.RunNextAsync(ObservedAt.AddSeconds(1));
        var replay = await worker.RunNextAsync(ObservedAt.AddSeconds(2));

        Assert.Equal(SkillProjectionWorkOutcome.Published, first);
        Assert.Equal(SkillProjectionWorkOutcome.NoWork, replay);
        var reader = new SkillProjectionReadService(database.Path);
        var claims = reader.ListCurrentInvocations(TraceId);
        var claim = Assert.Single(claims);
        Assert.Equal("3333333333333333", claim.SpanId);
        Assert.Equal("safe-skill", claim.SkillName);
        var inventory = Assert.Single(reader.ListCurrentInventories(TraceId));
        Assert.Equal(1, inventory.ObservedNameCount);
        Assert.Equal(["safe-skill"], inventory.RetainedNames);
        Assert.False(inventory.NamesTruncated);
        using var connection = Open(database.Path);
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal("completed", ScalarText(connection, "SELECT state FROM skill_projection_queue;"));
        Assert.Equal("current", ScalarText(connection, "SELECT lifecycle FROM skill_projection_generations;"));
    }

    [Fact]
    public async Task ReconciliationInvalidation_RemovesCurrentClaimBeforeReplacementWorkerRuns()
    {
        using var database = new TestDatabase();
        var compatibility = new SqliteSourceCompatibilityStore(database.Path);
        compatibility.CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("immediate-invalidation", ObservedAt, SkillPayloadWithoutVersion));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var worker = new SkillProjectionWorker(
            new SqliteSkillProjectionStore(
                database.Path,
                new RawTelemetryStore(database.Path, retention)));
        Assert.Equal(
            SkillProjectionWorkOutcome.Published,
            await worker.RunNextAsync(ObservedAt.AddSeconds(1)));
        var reader = new SkillProjectionReadService(database.Path);
        Assert.Single(reader.ListCurrentInvocations(TraceId));

        CreateReconciler(database.Path).Reconcile(
            SourceCompatibilityReconciliationRequest.Create(
                "invalidate-current-claim",
                committed.ObservationId,
                TraceId,
                0,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1",
                SkillProjectionGenerationParticipant.CurrentProjectorVersion));

        Assert.Empty(reader.ListCurrentInvocations(TraceId));
        using var connection = Open(database.Path);
        Assert.Equal(1, ScalarLong(connection, "SELECT current_revision FROM source_trace_compatibility_revisions;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_trace_heads WHERE current_generation_id IS NOT NULL;"));
        Assert.Equal("superseded", ScalarText(connection, "SELECT lifecycle FROM skill_projection_generations WHERE generation_id=1;"));
    }

    [Fact]
    public async Task CompatibilityRevisionChangeImmediatelyBeforePublish_CannotPublishOldGeneration()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("publish-fence", ObservedAt, SkillPayloadWithoutVersion));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var worker = new SkillProjectionWorker(
            store,
            _ =>
            {
                using var connection = Open(database.Path);
                using var update = connection.CreateCommand();
                update.CommandText =
                    """
                    UPDATE source_trace_compatibility_revisions
                    SET current_revision=1,updated_at=$updated_at
                    WHERE trace_id=$trace_id AND current_revision=0;
                    """;
                update.Parameters.AddWithValue(
                    "$updated_at",
                    ObservedAt.AddSeconds(1).ToString("O"));
                update.Parameters.AddWithValue("$trace_id", TraceId);
                Assert.Equal(1, update.ExecuteNonQuery());
            });

        var outcome = await worker.RunNextAsync(ObservedAt.AddSeconds(1));

        Assert.Equal(SkillProjectionWorkOutcome.Superseded, outcome);
        Assert.Empty(new SkillProjectionReadService(database.Path).ListCurrentInvocations(TraceId));
        using var connection = Open(database.Path);
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal("superseded", ScalarText(connection, "SELECT state FROM skill_projection_queue WHERE generation_id=1;"));
    }

    [Fact]
    public async Task QueueAndRetentionHeartbeat_ExtendTheSameOwnedWorkFences()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("heartbeat", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var claimedAt = DateTimeOffset.UtcNow;
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        var retentionLease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        await using (retentionLease)
        {
            for (var seconds = 10; seconds <= 70; seconds += 10)
            {
                queueLease = Assert.IsType<SkillProjectionQueueLease>(
                    store.Heartbeat(
                        queueLease,
                        retentionLease,
                        claimedAt.AddSeconds(seconds)));
            }

            using var connection = Open(database.Path);
            Assert.Equal(
                claimedAt.AddSeconds(100).ToUniversalTime().ToString("O"),
                ScalarText(connection, "SELECT lease_expires_at FROM skill_projection_queue;"));
            Assert.All(
                retentionLease.Grants,
                grant => Assert.True(grant.LeaseExpiresAt >= claimedAt.AddSeconds(180)));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation' AND expires_at>=$minimum_expiry;",
                    ("$minimum_expiry", claimedAt.AddSeconds(180).ToUniversalTime().ToString("O"))));
        }
    }

    [Fact]
    public void SdkClaim_RemainsNonCurrentWhilePayloadAuthorityIsUnpromoted()
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteSessionStore(database.Path).CreateSchema();
        const string sessionId = "77777777-7777-7777-7777-777777777777";
        const string eventId = "88888888-8888-8888-8888-888888888888";
        const string payload = """{"skill":"safe-skill"}""";
        var payloadSha256 = Sha256(payload);
        using (var connection = Open(database.Path))
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO sessions(
                    session_id,status,completeness,repository,workspace,started_at,ended_at,
                    last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES(
                    $session_id,'completed','full',NULL,NULL,NULL,NULL,$at,'expiring',$at,$at);
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                    source_adapter,source_event_id,type,occurred_at,content_state,
                    source_application_version,adapter_version,schema_fingerprint,
                    normalization_version,match_kind)
                VALUES(
                    $event_id,$session_id,NULL,'copilot-sdk',NULL,NULL,'completed',
                    'copilot-sdk-adapter','producer-event-1','skill_invocation',$at,'available',
                    '1.0.0','adapter-1',$fingerprint,'normalizer-1','exact_native');
                INSERT INTO session_event_content(
                    event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
                VALUES(
                    $event_id,'skill-invocation-v1',$payload,$at,
                    '9999-12-31T23:59:59.9999999+00:00',randomblob(32));
                INSERT INTO skill_projection_sdk_claims(
                    claim_id,session_id,event_id,source_event_id,source_adapter,source_surface,
                    source_application_version,adapter_version,normalization_version,
                    payload_schema,schema_fingerprint,payload_sha256,
                    producer_trace_id,producer_span_id,skill_name,skill_source,
                    invocation_trigger,created_at)
                VALUES(
                    '99999999-9999-7999-8999-999999999999',$session_id,$event_id,
                    'producer-event-1','copilot-sdk-adapter','copilot-sdk',
                    '1.0.0','adapter-1','normalizer-1','skill-invocation-v1',
                    $fingerprint,$payload_sha256,NULL,NULL,'safe-skill',NULL,NULL,$at);
                """;
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$event_id", eventId);
            command.Parameters.AddWithValue("$at", ObservedAt.ToString("O"));
            command.Parameters.AddWithValue("$fingerprint", new string('a', 64));
            command.Parameters.AddWithValue("$payload", payload);
            command.Parameters.AddWithValue("$payload_sha256", payloadSha256);
            command.ExecuteNonQuery();
        }

        Assert.Empty(
            new SkillProjectionReadService(database.Path)
                .ListCurrentSdkClaims(sessionId));
        using var validation = Open(database.Path);
        var error = Assert.Throws<InvalidOperationException>(
            () => SkillProjectionSchemaV1.Validate(validation, transaction: null));
        Assert.Equal("skill_projection_sdk_claim_authority_unpromoted", error.Message);
    }

    [Fact]
    public void Aggregate_WithNoAdmissibleAuthority_DoesNotAssertZero()
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteSessionStore(database.Path).CreateSchema();
        const string sessionId = "77777777-7777-7777-7777-777777777777";
        using var connection = Open(database.Path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions(
                session_id,status,completeness,last_seen_at,raw_retention_state,
                created_at,updated_at)
            VALUES($session_id,'completed','full',$at,'expiring',$at,$at);
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$at", ObservedAt.ToString("O"));
        command.ExecuteNonQuery();

        var aggregate = new SkillProjectionReadService(database.Path)
            .GetSessionInvocationAggregate(sessionId);

        Assert.Null(aggregate.InvocationCount);
        Assert.Null(aggregate.State);
    }

    [Fact]
    public void SdkClaimParticipant_RejectsUnpromotedPayloadAuthorityAndRollsBackCaller()
    {
        using var database = new TestDatabase();
        _ = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteSessionStore(database.Path).CreateSchema();
        const string sessionId = "77777777-7777-7777-7777-777777777777";
        const string eventId = "88888888-8888-8888-8888-888888888888";
        const string payload = """{"skill":"safe-skill"}""";
        var claim = new SkillProjectionSdkClaimWrite(
            "99999999-9999-7999-8999-999999999999",
            sessionId,
            eventId,
            "producer-event-1",
            "copilot-sdk-adapter",
            "copilot-sdk",
            "1.0.0",
            "adapter-1",
            "normalizer-1",
            "skill-invocation-v1",
            new string('a', 64),
            Sha256(payload),
            null,
            null,
            "safe-skill",
            null,
            null,
            ObservedAt);
        using (var connection = Open(database.Path))
        {
            using var seed = connection.CreateCommand();
            seed.CommandText =
                """
                INSERT INTO sessions(
                    session_id,status,completeness,last_seen_at,raw_retention_state,
                    created_at,updated_at)
                VALUES($session_id,'completed','full',$at,'expiring',$at,$at);
                INSERT INTO session_events(
                    event_id,session_id,source_surface,status,source_adapter,source_event_id,
                    type,occurred_at,content_state,source_application_version,adapter_version,
                    schema_fingerprint,normalization_version,match_kind)
                VALUES(
                    $event_id,$session_id,'copilot-sdk','completed','copilot-sdk-adapter',
                    'producer-event-1','skill_invocation',$at,'available','1.0.0',
                    'adapter-1',$fingerprint,'normalizer-1','exact_native');
                INSERT INTO session_event_content(
                    event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
                VALUES(
                    $event_id,'skill-invocation-v1',$payload,$at,
                    '9999-12-31T23:59:59.9999999+00:00',randomblob(32));
                """;
            seed.Parameters.AddWithValue("$session_id", sessionId);
            seed.Parameters.AddWithValue("$event_id", eventId);
            seed.Parameters.AddWithValue("$at", ObservedAt.ToString("O"));
            seed.Parameters.AddWithValue("$fingerprint", new string('a', 64));
            seed.Parameters.AddWithValue("$payload", payload);
            seed.ExecuteNonQuery();
        }
        using (var connection = Open(database.Path))
        using (var transaction = connection.BeginTransaction())
        {
            using var unrelated = connection.CreateCommand();
            unrelated.Transaction = transaction;
            unrelated.CommandText =
                "INSERT INTO schema_version(component,version) VALUES('collision-proof',1);";
            unrelated.ExecuteNonQuery();
            var error = Assert.Throws<InvalidOperationException>(
                () => SkillProjectionSdkClaimParticipant.InsertOrVerify(
                    connection,
                    transaction,
                    claim));
            Assert.Equal("skill_projection_sdk_claim_authority_unpromoted", error.Message);
            transaction.Rollback();
        }
        using var verification = Open(database.Path);
        Assert.Equal(0, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM skill_projection_sdk_claims;"));
        Assert.Equal(0, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM schema_version WHERE component='collision-proof';"));
    }

    [Fact]
    public async Task ReclaimedQueueLease_MakesTheStaleWorkerUnableToMutateWork()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("stale-owner", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var first = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(ObservedAt.AddSeconds(1)));
        var firstRead = await store.ReadFrontierAsync(first, CancellationToken.None);
        await using var firstRetention = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(firstRead.Lease);
        var reclaimed = Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(ObservedAt.AddSeconds(32)));

        var staleOutcome = store.RecordRetry(
            first,
            ObservedAt.AddSeconds(32),
            "retention_lease_lost");

        Assert.Equal(SkillProjectionWorkOutcome.StaleOwner, staleOutcome);
        using var connection = Open(database.Path);
        Assert.Equal("leased", ScalarText(connection, "SELECT state FROM skill_projection_queue;"));
        Assert.Equal(reclaimed.LeaseOwner, ScalarText(connection, "SELECT lease_owner FROM skill_projection_queue;"));
        Assert.Equal(reclaimed.LeaseGeneration, ScalarLong(connection, "SELECT lease_generation FROM skill_projection_queue;"));
        Assert.Equal("pending", ScalarText(connection, "SELECT lifecycle FROM skill_projection_generations;"));
    }

    [Fact]
    public async Task RetentionOnlyLeaseLoss_RequeuesTheSameGenerationForItsCurrentOwner()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("retention-loss", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var lease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(ObservedAt.AddSeconds(1)));
        var read = await store.ReadFrontierAsync(lease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        using (var connection = Open(database.Path))
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM retention_leases WHERE lease_kind='operation';";
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        Assert.Null(store.Heartbeat(lease, retentionLease, ObservedAt.AddSeconds(11)));
        var outcome = store.RecordRetry(
            lease,
            ObservedAt.AddSeconds(11),
            "retention_lease_lost");

        Assert.Equal(SkillProjectionWorkOutcome.Retrying, outcome);
        using var verification = Open(database.Path);
        Assert.Equal("retry_pending", ScalarText(verification, "SELECT lifecycle FROM skill_projection_generations;"));
        Assert.Equal("pending", ScalarText(verification, "SELECT state FROM skill_projection_queue;"));
        Assert.Equal(ObservedAt.AddSeconds(12).ToString("O"), ScalarText(verification, "SELECT next_attempt_at FROM skill_projection_queue;"));
    }

    [Fact]
    public async Task RawExpiryBeforeProjection_ProducesInputUnavailable()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("raw-expiry", ObservedAt, SkillPayload));
        using (var connection = Open(database.Path))
        {
            using var expire = connection.CreateCommand();
            expire.CommandText =
                """
                UPDATE retention_items
                SET state='expired_pending_deletion',
                    read_denied_at=$expired_at,
                    queued_at=$expired_at,
                    revision=revision+1
                WHERE store_kind='raw_record';
                """;
            expire.Parameters.AddWithValue("$expired_at", ObservedAt.AddSeconds(1).ToString("O"));
            Assert.Equal(1, expire.ExecuteNonQuery());
        }
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var worker = new SkillProjectionWorker(
            new SqliteSkillProjectionStore(
                database.Path,
                new RawTelemetryStore(database.Path, retention)));

        var outcome = await worker.RunNextAsync(ObservedAt.AddSeconds(2));

        Assert.Equal(SkillProjectionWorkOutcome.InputUnavailable, outcome);
        using var verification = Open(database.Path);
        Assert.Equal("input_unavailable", ScalarText(verification, "SELECT lifecycle FROM skill_projection_generations;"));
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                """
                SELECT COUNT(*)
                FROM skill_projection_queue
                WHERE state='input_unavailable'
                  AND attempt_count=1
                  AND lease_generation=1
                  AND lease_owner IS NULL
                  AND lease_expires_at IS NULL
                  AND next_attempt_at IS NULL
                  AND error_code='retention_input_unavailable';
                """));
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                """
                SELECT COUNT(*)
                FROM skill_projection_trace_heads
                WHERE desired_generation_id=1
                  AND current_generation_id IS NULL;
                """));
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                """
                SELECT COUNT(*)
                FROM skill_projection_generation_inputs
                WHERE input_evidence_kind='payload_sha256'
                  AND raw_payload_sha256 IS NOT NULL;
                """));
        Assert.Equal(0, ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(0, ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_inventories;"));
        Assert.Equal(0, ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_inventory_names;"));
        Assert.Equal(
            0,
            ScalarLong(
                verification,
                "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        SourceCompatibilitySchemaV11.Validate(verification, transaction: null);
        SkillProjectionSchemaV1.Validate(verification, transaction: null);
        Assert.Empty(new SkillProjectionReadService(database.Path).ListCurrentInvocations(TraceId));
    }

    [Fact]
    public async Task WorkerInputUnavailableDesiredGeneration_IsSupersededWhenExactFrontierExtends()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        var first = ingestion.Commit(
            CreateBatch("worker-input-unavailable-frontier-1", ObservedAt, SkillPayload));
        MakeRawUnavailable(database.Path, first.RawRecordId, "read_denied");
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var worker = new SkillProjectionWorker(
            new SqliteSkillProjectionStore(
                database.Path,
                new RawTelemetryStore(database.Path, retention)));
        Assert.Equal(
            SkillProjectionWorkOutcome.InputUnavailable,
            await worker.RunNextAsync(ObservedAt.AddSeconds(2)));
        long oldDesired;
        using (var before = Open(database.Path))
        {
            oldDesired = ScalarLong(
                before,
                "SELECT desired_generation_id FROM skill_projection_trace_heads WHERE trace_id=$trace_id;",
                ("$trace_id", TraceId));
        }

        ingestion.Commit(CreateBatch(
            "worker-input-unavailable-frontier-2",
            ObservedAt.AddSeconds(3),
            SkillPayload));

        using var verification = Open(database.Path);
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                """
                SELECT COUNT(*)
                FROM skill_projection_generations AS generation
                JOIN skill_projection_queue AS queue
                  ON queue.generation_id=generation.generation_id
                WHERE generation.generation_id=$old_generation_id
                  AND generation.lifecycle='superseded'
                  AND queue.state='superseded'
                  AND queue.attempt_count=1
                  AND queue.lease_generation=1
                  AND queue.lease_owner IS NULL
                  AND queue.lease_expires_at IS NULL
                  AND queue.next_attempt_at IS NULL
                  AND queue.error_code IS NULL;
                """,
                ("$old_generation_id", oldDesired)));
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                """
                SELECT COUNT(*)
                FROM skill_projection_trace_heads AS head
                JOIN skill_projection_generations AS generation
                  ON generation.generation_id=head.desired_generation_id
                JOIN skill_projection_queue AS queue
                  ON queue.generation_id=generation.generation_id
                WHERE head.trace_id=$trace_id
                  AND head.desired_generation_id<>$old_generation_id
                  AND head.current_generation_id IS NULL
                  AND generation.lifecycle='pending'
                  AND queue.state='pending';
                """,
                ("$trace_id", TraceId),
                ("$old_generation_id", oldDesired)));
        SourceCompatibilitySchemaV11.Validate(verification, transaction: null);
        SkillProjectionSchemaV1.Validate(verification, transaction: null);
    }

    [Theory]
    [InlineData("ordinary", "deleted")]
    [InlineData("ordinary", "read_denied")]
    [InlineData("reconciliation", "deleted")]
    [InlineData("reconciliation", "read_denied")]
    public async Task DurableFrontier_UnavailableEarlierInputCannotShrinkLaterGeneration(
        string transition,
        string unavailable)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var store = new SqliteIngestionCommitStore(database.Path);
        var first = store.Commit(CreateBatch("frontier-a", ObservedAt, SkillPayload));
        CommittedIngestionIds? second = null;
        if (transition == "reconciliation")
        {
            second = store.Commit(CreateBatchWithResolution(
                "frontier-b",
                ObservedAt.AddSeconds(1),
                SkillPayload,
                TraceSourceVersionResolutionState.Unrecognised,
                "1.0.74"));
        }
        else
        {
        }
        MakeRawUnavailable(database.Path, first.RawRecordId, unavailable);
        long desiredGeneration;
        if (transition == "ordinary")
        {
            second = store.Commit(CreateBatch(
                "frontier-b",
                ObservedAt.AddSeconds(1),
                SkillPayload));
            using var connection = Open(database.Path);
            desiredGeneration = ScalarLong(
                connection,
                $"SELECT desired_generation_id FROM skill_projection_trace_heads WHERE trace_id='{TraceId}';");
        }
        else
        {
            var registry = VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create(
                    "github-copilot-cli",
                    "1.0.74",
                    new string('a', 64)),
            ],
            [],
            []);
            var reconciliation = new SourceCompatibilityReconciler(
                    database.Path,
                    SourceCompatibilityReconciliationAuthority.Create(
                    [
                        new("resolver-1", "registry-2", registry),
                    ]),
                    new MutableTimeProvider(ObservedAt.AddSeconds(2)))
                .Reconcile(SourceCompatibilityReconciliationRequest.Create(
                    "frontier-registry-reconciliation",
                    second!.ObservationId,
                    TraceId,
                    0,
                    SourceCompatibilityReconciliationTrigger.RegistryRevision,
                    "resolver-1",
                    "registry-2",
                    SkillProjectionGenerationParticipant.CurrentProjectorVersion));
            desiredGeneration = reconciliation.GenerationId!.Value;
        }

        using (var connection = Open(database.Path))
        {
            Assert.Equal(
                2,
                ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM skill_projection_generation_inputs WHERE generation_id={desiredGeneration};"));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM skill_projection_generation_inputs WHERE generation_id={desiredGeneration} AND raw_record_id={first.RawRecordId};"));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM skill_projection_generation_inputs WHERE generation_id={desiredGeneration} AND raw_record_id={second!.RawRecordId};"));
        }
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var worker = new SkillProjectionWorker(
            new SqliteSkillProjectionStore(
                database.Path,
                new RawTelemetryStore(
                    database.Path,
                    retention,
                    new MutableTimeProvider(ObservedAt.AddSeconds(3)))));

        var outcome = await worker.RunNextAsync(ObservedAt.AddSeconds(3));

        Assert.Equal(SkillProjectionWorkOutcome.InputUnavailable, outcome);
        using var verification = Open(database.Path);
        Assert.Equal(
            "input_unavailable",
            ScalarText(
                verification,
                $"SELECT lifecycle FROM skill_projection_generations WHERE generation_id={desiredGeneration};"));
        Assert.Empty(new SkillProjectionReadService(database.Path).ListCurrentInvocations(TraceId));
    }

    [Fact]
    public async Task LostRetentionLease_CannotPublishConstructedRows()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("lost-retention-publish", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var lease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(ObservedAt.AddSeconds(1)));
        var read = await store.ReadFrontierAsync(lease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        var projected = retentionLease.Value
            .Select((record, index) => new SkillProjectionProjectedInput(
                record.Id!.Value,
                record,
                MonitorSkillProjectionBuilder.Build(
                    record,
                    lease.Inputs[index].SourceSurface,
                    traceId => traceId == lease.TraceId
                        ? (TraceSourceVersionResolutionState.Resolved, lease.ExactVersion)
                        : null)))
            .ToArray();
        using (var connection = Open(database.Path))
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM retention_leases WHERE lease_kind='operation';";
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        var outcome = store.Publish(
            lease,
            projected,
            retentionLease,
            ObservedAt.AddSeconds(2));

        Assert.Equal(SkillProjectionWorkOutcome.Retrying, outcome);
        using var verification = Open(database.Path);
        Assert.Equal(0, ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(0, ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_inventories;"));
        Assert.Equal("retry_pending", ScalarText(verification, "SELECT lifecycle FROM skill_projection_generations;"));
        Assert.Equal("pending", ScalarText(verification, "SELECT state FROM skill_projection_queue;"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("partial")]
    [InlineData("duplicate")]
    [InlineData("reversed")]
    [InlineData("extra")]
    public async Task PublishRequiresExactOneToOneFrontierGrantTuples(string shape)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        ingestion.Commit(CreateBatch("grant-frontier-1", ObservedAt, SkillPayload));
        ingestion.Commit(CreateBatch("grant-frontier-2", ObservedAt.AddMilliseconds(1), SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var lease = Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(ObservedAt.AddSeconds(1)));
        var read = await store.ReadFrontierAsync(lease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        Assert.Equal(2, retentionLease.Grants.Count);
        var projected = retentionLease.Value
            .Select((record, index) => new SkillProjectionProjectedInput(
                record.Id!.Value,
                record,
                MonitorSkillProjectionBuilder.Build(
                    record,
                    lease.Inputs[index].SourceSurface,
                    traceId => traceId == lease.TraceId
                        ? (TraceSourceVersionResolutionState.Resolved, lease.ExactVersion)
                        : null)))
            .ToArray();
        IReadOnlyList<RetentionReadGrant> grants = shape switch
        {
            "empty" => [],
            "partial" => [retentionLease.Grants[0]],
            "duplicate" =>
                [retentionLease.Grants[0], retentionLease.Grants[0]],
            "reversed" =>
                [retentionLease.Grants[1], retentionLease.Grants[0]],
            "extra" =>
                [
                    retentionLease.Grants[0],
                    retentionLease.Grants[1],
                    retentionLease.Grants[0],
                ],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        await using var forgedLease =
            new RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>(
                retentionLease.Value,
                RetentionRevisionFence.Create(),
                static () => ValueTask.CompletedTask,
                grants);

        var outcome = store.Publish(
            lease,
            projected,
            forgedLease,
            ObservedAt.AddSeconds(2));

        Assert.Equal(SkillProjectionWorkOutcome.Retrying, outcome);
        using var verification = Open(database.Path);
        Assert.Equal(0, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(0, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM skill_projection_inventories;"));
        Assert.Equal("retry_pending", ScalarText(
            verification,
            "SELECT lifecycle FROM skill_projection_generations WHERE generation_id=2;"));
        Assert.Equal("pending", ScalarText(
            verification,
            "SELECT state FROM skill_projection_queue WHERE generation_id=2;"));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("extra")]
    [InlineData("reversed")]
    [InlineData("altered-digest")]
    public async Task HeartbeatRebindsTheExactPersistedFrontier(string shape)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        ingestion.Commit(CreateBatch("heartbeat-frontier-1", ObservedAt, SkillPayload));
        ingestion.Commit(CreateBatch(
            "heartbeat-frontier-2",
            ObservedAt.AddMilliseconds(1),
            SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var claimedAt = ObservedAt.AddSeconds(1);
        var lease = Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(lease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        Assert.Equal(2, lease.Inputs.Count);
        IReadOnlyList<SkillProjectionQueuedInput> forgedInputs = shape switch
        {
            "partial" => [lease.Inputs[0]],
            "extra" => [lease.Inputs[0], lease.Inputs[1], lease.Inputs[0]],
            "reversed" => [lease.Inputs[1], lease.Inputs[0]],
            "altered-digest" =>
                [
                    lease.Inputs[0] with { RawPayloadSha256 = new string('f', 64) },
                    lease.Inputs[1],
                ],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };
        var forged = lease with { Inputs = forgedInputs };
        var grantExpiries = retentionLease.Grants
            .Select(static grant => grant.LeaseExpiresAt)
            .ToArray();

        var renewed = store.Heartbeat(
            forged,
            retentionLease,
            claimedAt.AddSeconds(11));

        Assert.Null(renewed);
        using var verification = Open(database.Path);
        Assert.Equal(lease.LeaseExpiresAt.ToString("O"), ScalarText(
            verification,
            "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=2;"));
        Assert.Equal(
            grantExpiries,
            retentionLease.Grants
                .Select(static grant => grant.LeaseExpiresAt)
                .ToArray());
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("denied")]
    public async Task DelayedUnavailableReadUsesFreshTimeAndCannotMutateExpiredQueueLease(
        string disposition)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("delayed-unavailable", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention));
        var claimedAt = ObservedAt.AddSeconds(1);
        var time = new MutableTimeProvider(claimedAt);
        var worker = new SkillProjectionWorker(
            store,
            timeProvider: time,
            readFrontier: (_, _) =>
            {
                time.Advance(TimeSpan.FromSeconds(31));
                return ValueTask.FromResult(
                    new RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>(
                        disposition == "busy"
                            ? RetentionReadDisposition.Busy
                            : RetentionReadDisposition.Denied,
                        Lease: null));
            });

        var outcome = await worker.RunNextAsync(claimedAt);

        Assert.Equal(SkillProjectionWorkOutcome.StaleOwner, outcome);
        using var verification = Open(database.Path);
        Assert.Equal("leased", ScalarText(
            verification,
            "SELECT state FROM skill_projection_queue;"));
        Assert.Equal("pending", ScalarText(
            verification,
            "SELECT lifecycle FROM skill_projection_generations;"));
        Assert.Equal(0, ScalarLong(
            verification,
            """
            SELECT COUNT(*)
            FROM skill_projection_queue
            WHERE error_code IS NOT NULL;
            """));
    }

    private const string SkillPayload =
        """
        {"resourceSpans":[{
          "resource":{"attributes":[
            {"key":"service.version","value":{"stringValue":"1.0.74"}},
            {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
          ]},
          "scopeSpans":[{"spans":[{
            "traceId":"22222222222222222222222222222222",
            "spanId":"3333333333333333",
            "attributes":[
              {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
              {"key":"gen_ai.tool.name","value":{"stringValue":"skill"}},
              {"key":"github.copilot.skill.name","value":{"stringValue":"safe-skill"}},
              {"key":"github.copilot.context.skills","value":{"arrayValue":{"values":[
                {"stringValue":"safe-skill"}
              ]}}}
            ]
          }]}]
        }]}
        """;

    private const string SkillPayloadWithoutVersion =
        """
        {"resourceSpans":[{
          "resource":{"attributes":[
            {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
          ]},
          "scopeSpans":[{"spans":[{
            "traceId":"22222222222222222222222222222222",
            "spanId":"3333333333333333",
            "attributes":[
              {"key":"gen_ai.operation.name","value":{"stringValue":"execute_tool"}},
              {"key":"gen_ai.tool.name","value":{"stringValue":"skill"}},
              {"key":"github.copilot.skill.name","value":{"stringValue":"safe-skill"}}
            ]
          }]}]
        }]}
        """;

    private static SourceCompatibilityReconciler CreateReconciler(string databasePath) =>
        new(
            databasePath,
            SourceCompatibilityReconciliationAuthority.Create(
            [
                new(
                    "resolver-2",
                    "registry-1",
                    VerifiedSourceFingerprintRegistry.Create([], [], [])),
            ]),
            new MutableTimeProvider(ObservedAt.AddSeconds(2)));

    private static ValidatedIngestionBatch CreateBatch(string batchId, DateTimeOffset at)
        => CreateBatch(batchId, at, "{}");

    private static ValidatedIngestionBatch CreateBatch(
        string batchId,
        DateTimeOffset at,
        string payload)
        => CreateBatchWithResolution(
            batchId,
            at,
            payload,
            TraceSourceVersionResolutionState.Resolved,
            "1.0.74");

    private static ValidatedIngestionBatch CreateBatchWithResolution(
        string batchId,
        DateTimeOffset at,
        string payload,
        TraceSourceVersionResolutionState state,
        string? version)
    {
        var inventory = OtlpJsonStructuralWalker.Build(payload, at);
        var decision = SourceCompatibilityEvaluator.Assess(
            "github-copilot-cli",
            version,
            inventory,
            observedRecognizedCount: 1,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        var observation = SourceObservationBatchDraft.Create(
            batchId,
            "github-copilot-cli",
            version,
            "github-copilot-otel",
            "adapter-1",
            inventory,
            decision,
            SourceCaptureContentState.Available,
            at,
            [TraceSourceVersionResolutionDraft.Create(
                TraceId,
                state,
                version)]);
        return ValidatedIngestionBatch.Create(
            new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                TraceId,
                at,
                ResourceAttributesJson: null,
                PayloadJson: payload),
            observation);
    }

    private static void AppendExactObservation(
        string databasePath,
        ValidatedIngestionBatch batch)
    {
        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        var resolution = Assert.Single(batch.Observation.TraceSourceVersionResolutions);
        var before = SourceCompatibilityReconciler.ReadEffectiveTrace(
            connection,
            transaction,
            resolution.TraceId);
        var ownerToken = RandomNumberGenerator.GetBytes(32);
        var rawRecordId = RawTelemetryRecordSql.Insert(
            connection,
            transaction,
            batch.RawRecord,
            ownerToken);
        new RetentionCatalogStore(databasePath).RegisterRawRecord(
            connection,
            transaction,
            rawRecordId,
            batch.RawRecord.ReceivedAt,
            batch.RawRecord.SchemaVersion,
            ownerToken);
        SqliteSourceCompatibilityStore.InsertBatch(
            connection,
            transaction,
            rawRecordId,
            batch.RawRecord.PayloadJson,
            batch.Observation);
        SkillProjectionGenerationParticipant.AdmitOrdinaryObservation(
            connection,
            transaction,
            resolution.TraceId,
            before,
            batch.Observation.ObservedAt);
        transaction.Commit();
    }

    private static void MakeRawUnavailable(string path, long rawRecordId, string unavailable)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = unavailable switch
        {
            "deleted" =>
                """
                UPDATE retention_items
                SET state='deleted',
                    read_denied_at=$at,
                    deleted_at=$at,
                    revision=revision+1
                WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);
                INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
                SELECT item_id,$at,$at
                FROM retention_items
                WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);
                DELETE FROM raw_records WHERE id=$raw_record_id;
                """,
            "read_denied" =>
                """
                UPDATE retention_items
                SET state='expired_pending_deletion',
                    read_denied_at=$at,
                    queued_at=$at,
                    revision=revision+1
                WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(unavailable)),
        };
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.Parameters.AddWithValue("$at", ObservedAt.AddSeconds(2).ToString("O"));
        Assert.True(command.ExecuteNonQuery() >= 1);
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static long ScalarLong(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string ScalarText(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"skill-generation-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

}

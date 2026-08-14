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
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(ObservedAt.AddSeconds(2))));
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
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(ObservedAt.AddSeconds(2))));
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
        var rawStore = new RawTelemetryStore(
            database.Path,
            retention,
            new MutableTimeProvider(ObservedAt.AddSeconds(1)));
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
                new RawTelemetryStore(
                    database.Path,
                    retention,
                    new MutableTimeProvider(ObservedAt.AddSeconds(1)))));
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
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(ObservedAt.AddSeconds(1))));
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
    public async Task Publish_BlockedUntilExactQueueExpiryIsStaleAndDoesNotPublish()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("publish-post-wait-clock", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var claimedAt = ObservedAt.AddSeconds(1);
        var time = new MutableTimeProvider(claimedAt);
        using var checkpoint = new BlockingSkillTransactionCheckpoint(
            SkillProjectionCheckpoint.AfterPublishTransactionBeganBeforeClockSample);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time),
            checkpoint);
        var lease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
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
        using (var snapshotConnection = Open(database.Path))
            checkpoint.ExpectedState = ReadSkillWorkState(snapshotConnection, lease.GenerationId);

        var callerAt = claimedAt;
        var publishTask = Task.Run(() => store.Publish(
            lease,
            projected,
            retentionLease,
            callerAt));
        await checkpoint.WasReached.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            time.Advance(lease.LeaseExpiresAt - time.GetUtcNow());
            Assert.False(publishTask.IsCompleted);
        }
        finally
        {
            checkpoint.Continue();
        }

        Assert.Equal(
            SkillProjectionWorkOutcome.StaleOwner,
            await publishTask.WaitAsync(TimeSpan.FromSeconds(5)));
        using var verification = Open(database.Path);
        Assert.Equal(
            checkpoint.ExpectedState,
            ReadSkillWorkState(verification, lease.GenerationId));
        Assert.Equal(
            0,
            ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(
            0,
            ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_inventories;"));
        Assert.Equal(
            0,
            ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_inventory_names;"));
        Assert.Equal(
            0,
            ScalarLong(
                verification,
                "SELECT COUNT(*) FROM skill_projection_trace_heads WHERE current_generation_id IS NOT NULL;"));
    }

    [Fact]
    public async Task Publish_FutureCallerTimeUsesTrustedClockForFencesAndPersistedTimes()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("publish-future-caller", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var claimedAt = ObservedAt.AddSeconds(1);
        var trustedAt = claimedAt.AddSeconds(2);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time));
        var lease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
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
        time.Advance(trustedAt - time.GetUtcNow());

        var outcome = store.Publish(
            lease,
            projected,
            retentionLease,
            lease.LeaseExpiresAt.AddDays(1));

        Assert.Equal(SkillProjectionWorkOutcome.Published, outcome);
        var expectedTime = trustedAt.ToString("O");
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
                JOIN skill_projection_trace_heads AS head
                  ON head.trace_id=generation.trace_id
                WHERE generation.generation_id=$generation_id
                  AND generation.lifecycle='current'
                  AND generation.updated_at=$trusted_at
                  AND queue.state='completed'
                  AND queue.lease_owner IS NULL
                  AND queue.lease_expires_at IS NULL
                  AND head.current_generation_id=generation.generation_id
                  AND head.updated_at=$trusted_at;
                """,
                ("$generation_id", lease.GenerationId),
                ("$trusted_at", expectedTime)));
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                "SELECT COUNT(*) FROM skill_projection_invocations WHERE projected_at=$trusted_at;",
                ("$trusted_at", expectedTime)));
        Assert.Equal(
            1,
            ScalarLong(
                verification,
                "SELECT COUNT(*) FROM skill_projection_inventories WHERE projected_at=$trusted_at;",
                ("$trusted_at", expectedTime)));
    }

    [Theory]
    [InlineData("retry")]
    [InlineData("input-unavailable")]
    [InlineData("terminal")]
    public async Task FinishOwned_BlockedUntilExactQueueExpiryIsStaleAndDoesNotMutate(
        string operation)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("retry-post-wait-clock", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var claimedAt = ObservedAt.AddSeconds(1);
        var time = new MutableTimeProvider(claimedAt);
        using var checkpoint = new BlockingSkillTransactionCheckpoint(
            SkillProjectionCheckpoint.AfterFinishOwnedTransactionBeganBeforeClockSample);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time),
            checkpoint);
        var lease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        using (var snapshotConnection = Open(database.Path))
            checkpoint.ExpectedState = ReadSkillWorkState(snapshotConnection, lease.GenerationId);

        var callerAt = claimedAt;
        var finishTask = Task.Run(() => operation switch
        {
            "retry" => store.RecordRetry(lease, callerAt, "retention_lease_lost"),
            "input-unavailable" => store.RecordInputUnavailable(lease, callerAt),
            "terminal" => store.RecordTerminal(
                lease,
                callerAt,
                "skill_projection_input_digest_mismatch"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        });
        await checkpoint.WasReached.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            time.Advance(lease.LeaseExpiresAt - time.GetUtcNow());
            Assert.False(finishTask.IsCompleted);
        }
        finally
        {
            checkpoint.Continue();
        }

        Assert.Equal(
            SkillProjectionWorkOutcome.StaleOwner,
            await finishTask.WaitAsync(TimeSpan.FromSeconds(5)));
        using var verification = Open(database.Path);
        Assert.Equal(
            checkpoint.ExpectedState,
            ReadSkillWorkState(verification, lease.GenerationId));
        Assert.Equal(
            0,
            ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(
            0,
            ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_inventories;"));
        Assert.Equal(
            0,
            ScalarLong(verification, "SELECT COUNT(*) FROM skill_projection_inventory_names;"));
    }

    [Theory]
    [InlineData("retry", "retrying", "retry_pending", "pending", "retention_lease_lost")]
    [InlineData("input-unavailable", "input-unavailable", "input_unavailable", "input_unavailable", "retention_input_unavailable")]
    [InlineData("terminal", "failed-terminal", "failed_terminal", "failed_terminal", "skill_projection_input_digest_mismatch")]
    public void FinishOwned_FutureCallerTimeUsesTrustedClockForFencesAndPersistedTimes(
        string operation,
        string expectedOutcome,
        string expectedLifecycle,
        string expectedQueueState,
        string expectedErrorCode)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch($"{operation}-future-caller", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var claimedAt = ObservedAt.AddSeconds(1);
        var trustedAt = claimedAt.AddSeconds(2);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time));
        var lease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        time.Advance(trustedAt - time.GetUtcNow());
        var callerAt = lease.LeaseExpiresAt.AddDays(1);

        var outcome = operation switch
        {
            "retry" => store.RecordRetry(lease, callerAt, expectedErrorCode),
            "input-unavailable" => store.RecordInputUnavailable(lease, callerAt),
            "terminal" => store.RecordTerminal(lease, callerAt, expectedErrorCode),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

        Assert.Equal(
            expectedOutcome switch
            {
                "retrying" => SkillProjectionWorkOutcome.Retrying,
                "input-unavailable" => SkillProjectionWorkOutcome.InputUnavailable,
                "failed-terminal" => SkillProjectionWorkOutcome.FailedTerminal,
                _ => throw new ArgumentOutOfRangeException(nameof(expectedOutcome)),
            },
            outcome);
        var expectedNextAttempt = operation == "retry"
            ? trustedAt.AddSeconds(1).ToString("O")
            : null;
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
                WHERE generation.generation_id=$generation_id
                  AND generation.lifecycle=$lifecycle
                  AND generation.updated_at=$trusted_at
                  AND queue.state=$queue_state
                  AND queue.lease_owner IS NULL
                  AND queue.lease_expires_at IS NULL
                  AND queue.next_attempt_at IS $next_attempt_at
                  AND queue.error_code=$error_code;
                """,
                ("$generation_id", lease.GenerationId),
                ("$lifecycle", expectedLifecycle),
                ("$trusted_at", trustedAt.ToString("O")),
                ("$queue_state", expectedQueueState),
                ("$next_attempt_at", expectedNextAttempt is null ? DBNull.Value : expectedNextAttempt),
                ("$error_code", expectedErrorCode)));
    }

    [Fact]
    public async Task QueueAndRetentionHeartbeat_ExtendTheSameOwnedWorkFences()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("heartbeat", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        using (var coverageConnection = Open(database.Path))
            SeedExactAdapterCoverage(coverageConnection);
        var claimedAt = ObservedAt.AddSeconds(1);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time));
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        var retentionLease = Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        await using (retentionLease)
        {
            for (var seconds = 10; seconds <= 70; seconds += 10)
            {
                time.Advance(TimeSpan.FromSeconds(10));
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
                grant => Assert.True(PublishedLeaseExpiry(grant) >= claimedAt.AddSeconds(180)));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation' AND expires_at>=$minimum_expiry;",
                    ("$minimum_expiry", claimedAt.AddSeconds(180).ToUniversalTime().ToString("O"))));
        }
    }

    [Fact]
    public async Task Heartbeat_BlockedPastLeaseExpiry_DoesNotResurrectQueueOrRetentionLease()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("heartbeat-post-wait-clock", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        using (var coverageConnection = Open(database.Path))
            SeedExactAdapterCoverage(coverageConnection);

        var claimedAt = ObservedAt.AddSeconds(1);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time));
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        var grant = Assert.Single(retentionLease.Grants);
        var retentionExpiry = claimedAt.AddSeconds(20);
        string queueExpiryBefore;
        string persistedRetentionExpiryBefore;
        DateTimeOffset publishedRetentionExpiryBefore;
        using (var connection = Open(database.Path))
        {
            SetRetentionLeaseExpiry(connection, grant, retentionExpiry);
            queueExpiryBefore = ScalarText(
                connection,
                "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=$generation_id;",
                ("$generation_id", queueLease.GenerationId));
            persistedRetentionExpiryBefore = ScalarText(
                connection,
                "SELECT expires_at FROM retention_leases WHERE item_id=$item_id AND lease_kind='operation';",
                ("$item_id", grant.ItemId));
            publishedRetentionExpiryBefore = PublishedLeaseExpiry(grant);
        }

        var callStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var blocker = Open(database.Path);
        using var blockerTransaction = blocker.BeginTransaction(deferred: false);
        var heartbeatTask = Task.Run(() =>
        {
            callStarted.SetResult();
            return store.Heartbeat(queueLease, retentionLease, claimedAt);
        });

        try
        {
            await callStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(heartbeatTask.IsCompleted);
            time.Advance(TimeSpan.FromSeconds(31));
        }
        finally
        {
            blockerTransaction.Rollback();
        }

        var heartbeat = await heartbeatTask.WaitAsync(TimeSpan.FromSeconds(5));
        using var verification = Open(database.Path);
        Assert.Null(heartbeat);
        Assert.Equal(
            queueExpiryBefore,
            ScalarText(
                verification,
                "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=$generation_id;",
                ("$generation_id", queueLease.GenerationId)));
        Assert.Equal(
            persistedRetentionExpiryBefore,
            ScalarText(
                verification,
                "SELECT expires_at FROM retention_leases WHERE item_id=$item_id AND lease_kind='operation';",
                ("$item_id", grant.ItemId)));
        Assert.Equal(publishedRetentionExpiryBefore, PublishedLeaseExpiry(grant));
    }

    [Fact]
    public async Task Heartbeat_BlockedOnFrontierPublicationPastExactExpiry_DoesNotRenewAnyAuthority()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        ingestion.Commit(CreateBatch("publication-clock-frontier-1", ObservedAt, SkillPayload));
        ingestion.Commit(CreateBatch(
            "publication-clock-frontier-2",
            ObservedAt.AddMilliseconds(1),
            SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        using (var coverageConnection = Open(database.Path))
            SeedExactAdapterCoverage(coverageConnection);

        var claimedAt = ObservedAt.AddSeconds(1);
        var heartbeatStartedAt = claimedAt.AddSeconds(11);
        var time = new MutableTimeProvider(heartbeatStartedAt);
        var checkpoint = new SignalingSkillProjectionCheckpoint(
            SkillProjectionCheckpoint.AfterHeartbeatTransactionBeganBeforePublicationScopes);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time),
            checkpoint);
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        Assert.Equal(2, retentionLease.Grants.Count);
        var exactExpiry = queueLease.LeaseExpiresAt;

        string[] skillStateBefore;
        string[] retentionAuthorityBefore;
        DateTimeOffset[] publishedExpiriesBefore;
        using (var connection = Open(database.Path))
        {
            var persistedFrontier = ReadPersistedSkillFrontier(
                connection,
                queueLease.GenerationId);
            Assert.Equal([0, 1], persistedFrontier.Select(static row => row.Ordinal));
            Assert.Equal(
                persistedFrontier.Select(static row => row.ItemId),
                retentionLease.Grants.Select(static grant => grant.ItemId));
            Assert.Equal(
                exactExpiry.ToUniversalTime().ToString("O"),
                ScalarText(
                    connection,
                    "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=$generation_id;",
                    ("$generation_id", queueLease.GenerationId)));
            foreach (var grant in retentionLease.Grants)
                SetRetentionLeaseExpiry(connection, grant, exactExpiry);
            skillStateBefore = ReadSkillProjectionState(connection);
            retentionAuthorityBefore =
            [
                FullRowsSnapshot(connection, "retention_items"),
                FullRowsSnapshot(connection, "raw_records"),
                FullRowsSnapshot(connection, "retention_leases"),
                FullRowsSnapshot(connection, "retention_adapter_coverage"),
            ];
            publishedExpiriesBefore = retentionLease.Grants
                .Select(PublishedLeaseExpiry)
                .ToArray();
        }
        Assert.All(publishedExpiriesBefore, expiry => Assert.Equal(exactExpiry, expiry));

        var publicationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releasePublication = new ManualResetEventSlim(initialState: false);
        var publicationHolder = Task.Run(() =>
        {
            using var publication = retentionLease.Grants[1].EnterLeasePublication();
            publicationEntered.TrySetResult();
            if (!releasePublication.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("frontier publication scope was not released");
        });
        await publicationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var heartbeatCompleted = new TaskCompletionSource<(
            SkillProjectionQueueLease? Lease,
            Exception? Exception)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeatThread = new Thread(() =>
        {
            try
            {
                heartbeatCompleted.TrySetResult((
                    store.Heartbeat(
                        queueLease,
                        retentionLease,
                        heartbeatStartedAt),
                    null));
            }
            catch (Exception exception)
            {
                heartbeatCompleted.TrySetResult((null, exception));
            }
        })
        {
            IsBackground = true,
        };
        heartbeatThread.Start();

        try
        {
            await checkpoint.WasReached.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(SpinWait.SpinUntil(
                () => heartbeatThread.ThreadState.HasFlag(ThreadState.WaitSleepJoin),
                TimeSpan.FromSeconds(5)));
            Assert.False(heartbeatCompleted.Task.IsCompleted);
            using var publicationProbeConnection = Open(database.Path);
            using var firstPublicationProbe = publicationProbeConnection.CreateCommand();
            firstPublicationProbe.CommandText =
                """
                SELECT
                    $retention_read_source_token,
                    $retention_read_item_id,
                    $retention_read_revision,
                    $retention_read_lease_kind,
                    $retention_read_lease_owner,
                    $retention_read_lease_generation,
                    $retention_read_lease_expires_at;
                """;
            Assert.False(
                retentionLease.Grants[0]
                    .TryBindAdmissionSelectorCapability(firstPublicationProbe));
            time.Advance(exactExpiry - time.GetUtcNow());
            Assert.Equal(exactExpiry, time.GetUtcNow());
        }
        finally
        {
            releasePublication.Set();
        }

        await publicationHolder.WaitAsync(TimeSpan.FromSeconds(5));
        var heartbeat = await heartbeatCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(heartbeat.Exception);
        Assert.Null(heartbeat.Lease);
        using var verification = Open(database.Path);
        Assert.Equal(skillStateBefore, ReadSkillProjectionState(verification));
        Assert.Equal(
            retentionAuthorityBefore,
            new[]
            {
                FullRowsSnapshot(verification, "retention_items"),
                FullRowsSnapshot(verification, "raw_records"),
                FullRowsSnapshot(verification, "retention_leases"),
                FullRowsSnapshot(verification, "retention_adapter_coverage"),
            });
        Assert.Equal(
            publishedExpiriesBefore,
            retentionLease.Grants.Select(PublishedLeaseExpiry).ToArray());
    }

    [Fact]
    public async Task Heartbeat_BeforePersistedExpiry_RenewsTheSameQueueAndRetentionLeases()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("heartbeat-before-expiry-control", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        using (var coverageConnection = Open(database.Path))
            SeedExactAdapterCoverage(coverageConnection);

        var claimedAt = ObservedAt.AddSeconds(1);
        var heartbeatAt = claimedAt.AddSeconds(11);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(heartbeatAt)));
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        var grant = Assert.Single(retentionLease.Grants);
        using (var connection = Open(database.Path))
            SetRetentionLeaseExpiry(connection, grant, claimedAt.AddSeconds(20));

        var renewed = Assert.IsType<SkillProjectionQueueLease>(
            store.Heartbeat(queueLease, retentionLease, heartbeatAt));

        Assert.Equal(heartbeatAt.AddSeconds(30), renewed.LeaseExpiresAt);
        using var verification = Open(database.Path);
        Assert.Equal(
            heartbeatAt.AddSeconds(30).ToString("O"),
            ScalarText(
                verification,
                "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=$generation_id;",
                ("$generation_id", queueLease.GenerationId)));
        Assert.Equal(
            heartbeatAt.Add(RetentionV1Constants.LeaseDuration).ToString("O"),
            ScalarText(
                verification,
                "SELECT expires_at FROM retention_leases WHERE item_id=$item_id AND lease_kind='operation';",
                ("$item_id", grant.ItemId)));
        Assert.Equal(
            heartbeatAt.Add(RetentionV1Constants.LeaseDuration),
            PublishedLeaseExpiry(grant));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("revision")]
    [InlineData("readability")]
    [InlineData("source_receipt")]
    [InlineData("coverage")]
    public async Task Heartbeat_BeforeQueueIntervalIgnoresCurrentRenewalProofDrift(
        string drift)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("early-heartbeat-authority", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        using (var coverageConnection = Open(database.Path))
            SeedExactAdapterCoverage(coverageConnection);
        var claimedAt = ObservedAt.AddSeconds(1);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                time));
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        var grant = Assert.Single(retentionLease.Grants);
        using var connection = Open(database.Path);
        var queueExpiryBefore = ScalarText(
            connection,
            "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=$generation_id;",
            ("$generation_id", queueLease.GenerationId));
        var persistedRetentionExpiryBefore = ScalarText(
            connection,
            "SELECT expires_at FROM retention_leases WHERE item_id=$item_id AND lease_kind='operation';",
            ("$item_id", grant.ItemId));
        var publishedRetentionExpiryBefore = PublishedLeaseExpiry(grant);
        switch (drift)
        {
            case "none":
                break;
            case "revision":
                Assert.Equal(
                    1,
                    Execute(
                        connection,
                        "UPDATE retention_items SET revision=revision+1 WHERE item_id=$item_id;",
                        ("$item_id", grant.ItemId)));
                break;
            case "readability":
                Assert.Equal(
                    1,
                    Execute(
                        connection,
                        "UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$at WHERE item_id=$item_id;",
                        ("$at", claimedAt.ToString("O")),
                        ("$item_id", grant.ItemId)));
                break;
            case "source_receipt":
                Assert.Equal(
                    1,
                    Execute(
                        connection,
                        "UPDATE raw_records SET received_at=$received_at WHERE id=$raw_record_id;",
                        ("$received_at", ObservedAt.AddSeconds(1).ToString("O")),
                        ("$raw_record_id", Assert.Single(queueLease.Inputs).RawRecordId)));
                break;
            case "coverage":
                Assert.Equal(
                    1,
                    Execute(
                        connection,
                        "DELETE FROM retention_adapter_coverage WHERE store_kind='raw_record';"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }
        var retentionItemsBefore = FullRowsSnapshot(connection, "retention_items");
        var sourcesBefore = FullRowsSnapshot(connection, "raw_records");
        var leasesBefore = FullRowsSnapshot(connection, "retention_leases");
        var coverageBefore = FullRowsSnapshot(connection, "retention_adapter_coverage");
        Assert.True(
            GrantIsUsable(
                connection,
                grant,
                Assert.Single(queueLease.Inputs).RawRecordId,
                claimedAt.AddSeconds(5)));

        time.Advance(TimeSpan.FromSeconds(5));
        var heartbeat = store.Heartbeat(
            queueLease,
            retentionLease,
            claimedAt.AddSeconds(5));

        var unchanged = Assert.IsType<SkillProjectionQueueLease>(heartbeat);
        Assert.Equal(queueLease.LeaseExpiresAt, unchanged.LeaseExpiresAt);
        Assert.Equal(
            queueExpiryBefore,
            ScalarText(
                connection,
                "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=$generation_id;",
                ("$generation_id", queueLease.GenerationId)));
        Assert.Equal(
            persistedRetentionExpiryBefore,
            ScalarText(
                connection,
                "SELECT expires_at FROM retention_leases WHERE item_id=$item_id AND lease_kind='operation';",
                ("$item_id", grant.ItemId)));
        Assert.Equal(publishedRetentionExpiryBefore, PublishedLeaseExpiry(grant));
        Assert.Equal(retentionItemsBefore, FullRowsSnapshot(connection, "retention_items"));
        Assert.Equal(sourcesBefore, FullRowsSnapshot(connection, "raw_records"));
        Assert.Equal(leasesBefore, FullRowsSnapshot(connection, "retention_leases"));
        Assert.Equal(coverageBefore, FullRowsSnapshot(connection, "retention_adapter_coverage"));
        Assert.True(
            GrantIsUsable(
                connection,
                grant,
                Assert.Single(queueLease.Inputs).RawRecordId,
                claimedAt.AddSeconds(5)));
    }

    [Fact]
    public async Task Heartbeat_SecondPersistedOrdinalRenewalCasFailureRollsBackEveryLeaseExpiry()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        ingestion.Commit(CreateBatch("composite-heartbeat-1", ObservedAt, SkillPayload));
        ingestion.Commit(CreateBatch(
            "composite-heartbeat-2",
            ObservedAt.AddMilliseconds(1),
            SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        using (var coverageConnection = Open(database.Path))
            SeedExactAdapterCoverage(coverageConnection);
        var claimedAt = ObservedAt.AddSeconds(1);
        var heartbeatAt = claimedAt.AddSeconds(11);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(heartbeatAt)));
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        Assert.Equal(2, retentionLease.Grants.Count);
        var dueExpiry = claimedAt.AddSeconds(20);
        var renewedExpiry = heartbeatAt.Add(RetentionV1Constants.LeaseDuration);

        using var connection = Open(database.Path);
        var persistedFrontier = ReadPersistedSkillFrontier(
            connection,
            queueLease.GenerationId);
        Assert.Equal([0, 1], persistedFrontier.Select(static row => row.Ordinal));
        Assert.Equal(
            persistedFrontier.Select(static row => row.RawRecordId),
            queueLease.Inputs.Select(static input => input.RawRecordId));
        Assert.Equal(
            persistedFrontier.Select(static row => row.ItemId),
            retentionLease.Grants.Select(static grant => grant.ItemId));
        Assert.Equal(
            persistedFrontier.Select(static row => row.RawRecordId.ToString(
                System.Globalization.CultureInfo.InvariantCulture)),
            retentionLease.Grants.Select(static grant => grant.OwnershipKey.SourceItemId));
        foreach (var grant in retentionLease.Grants)
            SetRetentionLeaseExpiry(connection, grant, dueExpiry);
        InstallSecondOrdinalRenewalFailureTrigger(
            connection,
            persistedFrontier[0].ItemId,
            persistedFrontier[1].ItemId,
            renewedExpiry);
        var queueBefore = FullRowsSnapshot(
            connection,
            "skill_projection_queue",
            "generation_id",
            queueLease.GenerationId);
        var persistedLeasesBefore = persistedFrontier
            .Select(row => FullRowsSnapshot(
                connection,
                "retention_leases",
                "item_id",
                row.ItemId))
            .ToArray();
        var publishedExpiriesBefore = retentionLease.Grants
            .Select(PublishedLeaseExpiry)
            .ToArray();

        var renewed = store.Heartbeat(
            queueLease,
            retentionLease,
            heartbeatAt);

        Assert.Null(renewed);
        Assert.Equal(
            queueBefore,
            FullRowsSnapshot(
                connection,
                "skill_projection_queue",
                "generation_id",
                queueLease.GenerationId));
        Assert.Equal(
            persistedLeasesBefore,
            persistedFrontier
                .Select(row => FullRowsSnapshot(
                    connection,
                    "retention_leases",
                    "item_id",
                    row.ItemId))
                .ToArray());
        Assert.Equal(
            publishedExpiriesBefore,
            retentionLease.Grants.Select(PublishedLeaseExpiry).ToArray());
        Assert.All(publishedExpiriesBefore, expiry => Assert.Equal(dueExpiry, expiry));

        Execute(connection, "DROP TRIGGER fail_second_skill_retention_renewal;");
        Assert.Equal(
            0,
            ScalarLong(
                connection,
                "SELECT COUNT(*) FROM sqlite_schema WHERE type='trigger' AND name='fail_second_skill_retention_renewal';"));
        var control = Assert.IsType<SkillProjectionQueueLease>(
            store.Heartbeat(queueLease, retentionLease, heartbeatAt));

        Assert.Equal(heartbeatAt.AddSeconds(30), control.LeaseExpiresAt);
        Assert.All(
            persistedFrontier,
            row => Assert.Equal(
                renewedExpiry.ToUniversalTime().ToString("O"),
                ScalarText(
                    connection,
                    "SELECT expires_at FROM retention_leases WHERE item_id=$item_id AND lease_kind='operation';",
                    ("$item_id", row.ItemId))));
        Assert.All(
            retentionLease.Grants,
            grant => Assert.Equal(renewedExpiry, PublishedLeaseExpiry(grant)));
    }

    [Fact]
    public async Task Heartbeat_PublishesRetentionRenewalBeforeConcurrentValidatorCanBindIt()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("heartbeat-publication", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        using (var coverageConnection = Open(database.Path))
            SeedExactAdapterCoverage(coverageConnection);
        var checkpoint = new SkillRenewalPublicationCheckpoint(database.Path);
        var claimedAt = ObservedAt.AddSeconds(1);
        var heartbeatAt = claimedAt.AddSeconds(11);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(heartbeatAt)),
            checkpoint);
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        var grant = Assert.Single(retentionLease.Grants);
        using (var connection = Open(database.Path))
            SetRetentionLeaseExpiry(connection, grant, claimedAt.AddSeconds(20));
        checkpoint.Configure(
            grant,
            queueLease.GenerationId,
            heartbeatAt.Add(RetentionV1Constants.LeaseDuration));

        var renewed = store.Heartbeat(queueLease, retentionLease, heartbeatAt);

        Assert.NotNull(renewed);
        await Assert.IsType<Task>(checkpoint.ConcurrentValidator)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(checkpoint.GrantBindingWasBlocked);
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
        var claimedAt = ObservedAt.AddSeconds(1);
        var heartbeatAt = claimedAt.AddSeconds(10);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(heartbeatAt)));
        var lease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));
        var read = await store.ReadFrontierAsync(lease, CancellationToken.None);
        await using var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        using (var connection = Open(database.Path))
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM retention_leases WHERE lease_kind='operation';";
            Assert.Equal(1, delete.ExecuteNonQuery());
        }

        Assert.Null(store.Heartbeat(lease, retentionLease, heartbeatAt));
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
                new RawTelemetryStore(
                    database.Path,
                    retention,
                    new MutableTimeProvider(ObservedAt.AddSeconds(2)))));

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
                new RawTelemetryStore(
                    database.Path,
                    retention,
                    new MutableTimeProvider(ObservedAt.AddSeconds(2)))));
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
    public async Task Publish_AdmitsPinnedMemberPastHistoricalExpiryWithoutCatalogMutation()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("pinned-publish", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var claimedAt = ObservedAt.AddSeconds(1);
        var admissionAt = ObservedAt.AddSeconds(2);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                time));
        var lease = Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(claimedAt));
        var historicalExpiry = ObservedAt.AddMilliseconds(1500).ToString("O");
        (string Items, string Sources, string Leases) authorityBefore;
        using (var connection = Open(database.Path))
        {
            Assert.Equal(
                1,
                Execute(
                    connection,
                    "UPDATE retention_items SET state='retained_by_policy',expires_at=$expires_at WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$expires_at", historicalExpiry),
                    ("$raw_record_id", committed.RawRecordId)));
            authorityBefore = (
                FullRowsSnapshot(connection, "retention_items"),
                FullRowsSnapshot(connection, "raw_records"),
                FullRowsSnapshot(connection, "retention_leases"));
        }
        time.Advance(admissionAt - time.GetUtcNow());

        var read = await store.ReadFrontierAsync(lease, CancellationToken.None);
        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        SkillProjectionWorkOutcome outcome;
        await using (var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease))
        {
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

            outcome = store.Publish(
                lease,
                projected,
                retentionLease,
                admissionAt);
        }

        Assert.Equal(SkillProjectionWorkOutcome.Published, outcome);
        using var verification = Open(database.Path);
        Assert.Equal(authorityBefore.Items, FullRowsSnapshot(verification, "retention_items"));
        Assert.Equal(authorityBefore.Sources, FullRowsSnapshot(verification, "raw_records"));
        Assert.Equal(authorityBefore.Leases, FullRowsSnapshot(verification, "retention_leases"));
    }

    [Fact]
    public async Task ReadFrontier_FailsClosedAtAdmissionBoundaryWithoutPoisoningPinnedSibling()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        var first = ingestion.Commit(CreateBatch("mixed-admission-1", ObservedAt, SkillPayload));
        var second = ingestion.Commit(CreateBatch(
            "mixed-admission-2",
            ObservedAt.AddMilliseconds(1),
            SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var claimedAt = ObservedAt.AddSeconds(1);
        var admissionAt = ObservedAt.AddSeconds(2);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                time));
        var lease = Assert.IsType<SkillProjectionQueueLease>(
            store.ClaimNext(claimedAt));
        var boundaryExpiry = admissionAt.ToString("O");
        var pinnedExpiry = ObservedAt.AddMilliseconds(1500).ToString("O");
        string pinnedItemBefore;
        string boundaryImmutableBefore;
        string sourcesBefore;
        string leasesBefore;
        string[] skillAuthorityBefore;
        long boundaryRevision;
        using (var connection = Open(database.Path))
        {
            Assert.Equal(
                1,
                Execute(
                    connection,
                    "UPDATE retention_items SET state='expiring',expires_at=$expires_at WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$expires_at", boundaryExpiry),
                    ("$raw_record_id", first.RawRecordId)));
            Assert.Equal(
                1,
                Execute(
                    connection,
                    "UPDATE retention_items SET state='retained_by_policy',expires_at=$expires_at WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$expires_at", pinnedExpiry),
                    ("$raw_record_id", second.RawRecordId)));
            boundaryRevision = ScalarLong(
                connection,
                "SELECT revision FROM retention_items WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                ("$raw_record_id", first.RawRecordId));
            boundaryImmutableBefore = FullRowsSnapshot(
                connection,
                "retention_items",
                "source_item_id",
                first.RawRecordId.ToString(),
                "state",
                "revision",
                "read_denied_at",
                "queued_at");
            pinnedItemBefore = FullRowsSnapshot(
                connection,
                "retention_items",
                "source_item_id",
                second.RawRecordId.ToString());
            sourcesBefore = FullRowsSnapshot(connection, "raw_records");
            leasesBefore = FullRowsSnapshot(connection, "retention_leases");
            skillAuthorityBefore = ReadSkillFrontierAuthority(connection);
        }
        time.Advance(admissionAt - time.GetUtcNow());

        var denied = await store.ReadFrontierAsync(lease, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.Denied, denied.Disposition);
        Assert.Null(denied.Lease);
        using (var connection = Open(database.Path))
        {
            Assert.Equal(
                "expired_pending_deletion",
                ScalarText(
                    connection,
                    "SELECT state FROM retention_items WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$raw_record_id", first.RawRecordId)));
            Assert.Equal(
                boundaryRevision + 1,
                ScalarLong(
                    connection,
                    "SELECT revision FROM retention_items WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$raw_record_id", first.RawRecordId)));
            Assert.Equal(
                admissionAt.ToString("O"),
                ScalarText(
                    connection,
                    "SELECT read_denied_at FROM retention_items WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$raw_record_id", first.RawRecordId)));
            Assert.Equal(
                admissionAt.ToString("O"),
                ScalarText(
                    connection,
                    "SELECT queued_at FROM retention_items WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$raw_record_id", first.RawRecordId)));
            Assert.Equal(
                boundaryImmutableBefore,
                FullRowsSnapshot(
                    connection,
                    "retention_items",
                    "source_item_id",
                    first.RawRecordId.ToString(),
                    "state",
                    "revision",
                    "read_denied_at",
                    "queued_at"));
            Assert.Equal(
                pinnedItemBefore,
                FullRowsSnapshot(
                    connection,
                    "retention_items",
                    "source_item_id",
                    second.RawRecordId.ToString()));
            Assert.Equal(sourcesBefore, FullRowsSnapshot(connection, "raw_records"));
            Assert.Equal(leasesBefore, FullRowsSnapshot(connection, "retention_leases"));
            Assert.Equal(skillAuthorityBefore, ReadSkillFrontierAuthority(connection));
            Assert.Equal(
                0,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
            Assert.Equal(
                1,
                Execute(
                    connection,
                    """
                    UPDATE retention_items
                    SET state='expiring',expires_at=$expires_at,read_denied_at=NULL,queued_at=NULL
                    WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);
                    """,
                    ("$expires_at", admissionAt.AddDays(30).ToString("O")),
                    ("$raw_record_id", first.RawRecordId)));
        }

        var repaired = await store.ReadFrontierAsync(lease, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.Granted, repaired.Disposition);
        await using var repairedLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(repaired.Lease);
        Assert.Equal(2, repairedLease.Grants.Count);
    }

    [Theory]
    [InlineData("pin")]
    [InlineData("pin-unpin")]
    [InlineData("cleanup-read-denied")]
    public async Task Publish_PostAdmissionCatalogLifecycleDriftUsesExactLiveGrant(
        string lifecycleDrift)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var primary = new SqliteIngestionCommitStore(database.Path)
            .Commit(CreateBatch("post-admission-publish", ObservedAt, SkillPayload));
        var retention = RetentionCatalogContext.AdoptExistingCatalogV1(database.Path);
        var naturalExpiry = RetentionUnpinExpiryCalculator.Recalculate(
            ObservedAt,
            "raw-default-90d",
            1);
        var claimedAt = naturalExpiry.AddSeconds(-1);
        var time = new MutableTimeProvider(claimedAt);
        var rawStore = new RawTelemetryStore(database.Path, retention, time);
        var sentinelRawRecordId = rawStore.Insert(
            new RawTelemetryRecord(
                null,
                RawTelemetrySources.RawOtlp,
                "44444444444444444444444444444444",
                ObservedAt.AddMinutes(1),
                ResourceAttributesJson: null,
                PayloadJson: "{}"));
        var store = new SqliteSkillProjectionStore(database.Path, rawStore);
        var queueLease = Assert.IsType<SkillProjectionQueueLease>(store.ClaimNext(claimedAt));

        var read = await store.ReadFrontierAsync(queueLease, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.Granted, read.Disposition);
        var retentionLease =
            Assert.IsType<RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>>>(read.Lease);
        string retentionItemsAfterDrift;
        string sourcesAfterDrift;
        string sameItemAccessBefore;
        string unrelatedOperationBefore;
        await using (retentionLease)
        {
            var record = Assert.Single(retentionLease.Value);
            var grant = Assert.Single(retentionLease.Grants);
            var queuedInput = Assert.Single(queueLease.Inputs);
            Assert.Equal(primary.RawRecordId, record.Id);
            Assert.Equal(primary.RawRecordId, queuedInput.RawRecordId);
            Assert.Equal(primary.RawRecordId.ToString(), grant.OwnershipKey.SourceItemId);
            using (var frontierConnection = Open(database.Path))
            {
                var persisted = Assert.Single(
                    ReadPersistedSkillFrontier(frontierConnection, queueLease.GenerationId));
                Assert.Equal(0, persisted.Ordinal);
                Assert.Equal(primary.RawRecordId, persisted.RawRecordId);
                Assert.Equal(grant.ItemId, persisted.ItemId);
            }

            var projected = new[]
            {
                new SkillProjectionProjectedInput(
                    record.Id!.Value,
                    record,
                    MonitorSkillProjectionBuilder.Build(
                        record,
                        queuedInput.SourceSurface,
                        traceId => traceId == queueLease.TraceId
                            ? (TraceSourceVersionResolutionState.Resolved, queueLease.ExactVersion)
                            : null)),
            };
            var mutationAt = lifecycleDrift == "cleanup-read-denied"
                ? naturalExpiry
                : claimedAt.AddMilliseconds(500);
            time.Advance(mutationAt - time.GetUtcNow());

            string leasesBeforePublish;
            using (var connection = Open(database.Path))
            {
                var initialRevision = ScalarLong(
                    connection,
                    "SELECT revision FROM retention_items WHERE item_id=$item_id;",
                    ("$item_id", grant.ItemId));
                var excludedColumns = lifecycleDrift switch
                {
                    "pin" => new[] { "state", "revision" },
                    "pin-unpin" => new[] { "state", "expires_at", "revision" },
                    "cleanup-read-denied" =>
                        new[] { "state", "revision", "read_denied_at", "queued_at" },
                    _ => throw new ArgumentOutOfRangeException(nameof(lifecycleDrift)),
                };
                var immutableTargetBefore = FullRowsSnapshot(
                    connection,
                    "retention_items",
                    "item_id",
                    grant.ItemId,
                    excludedColumns);
                var sentinelItemId = ScalarText(
                    connection,
                    "SELECT item_id FROM retention_items WHERE store_kind='raw_record' AND source_item_id=CAST($raw_record_id AS TEXT);",
                    ("$raw_record_id", sentinelRawRecordId));
                var sentinelItemBefore = FullRowsSnapshot(
                    connection,
                    "retention_items",
                    "item_id",
                    sentinelItemId);
                var sourcesBefore = FullRowsSnapshot(connection, "raw_records");
                var expectedState = lifecycleDrift switch
                {
                    "pin" => "retained_by_policy",
                    "pin-unpin" => "expiring",
                    "cleanup-read-denied" => "expired_pending_deletion",
                    _ => throw new ArgumentOutOfRangeException(nameof(lifecycleDrift)),
                };
                var expectedRevision = initialRevision +
                    (lifecycleDrift == "pin-unpin" ? 2 : 1);
                var expectedReadDeniedAt = lifecycleDrift == "cleanup-read-denied"
                    ? mutationAt.ToString("O")
                    : null;
                var expectedQueuedAt = expectedReadDeniedAt;
                var driftedRows = lifecycleDrift switch
                {
                    "pin" => Execute(
                        connection,
                        """
                        UPDATE retention_items
                        SET state='retained_by_policy',revision=revision+1
                        WHERE item_id=$item_id
                          AND state='expiring'
                          AND read_denied_at IS NULL
                          AND expires_at>$at;
                        """,
                        ("$item_id", grant.ItemId),
                        ("$at", mutationAt.ToString("O"))),
                    "pin-unpin" => Execute(
                        connection,
                        """
                        UPDATE retention_items
                        SET state='retained_by_policy',revision=revision+1
                        WHERE item_id=$item_id
                          AND state='expiring'
                          AND read_denied_at IS NULL
                          AND expires_at>$at;
                        UPDATE retention_items
                        SET state='expiring',expires_at=$expires_at,revision=revision+1
                        WHERE item_id=$item_id
                          AND state='retained_by_policy'
                          AND read_denied_at IS NULL;
                        """,
                        ("$item_id", grant.ItemId),
                        ("$at", mutationAt.ToString("O")),
                        ("$expires_at", naturalExpiry.ToString("O"))),
                    "cleanup-read-denied" => Execute(
                        connection,
                        """
                        UPDATE retention_items
                        SET state='expired_pending_deletion',
                            read_denied_at=$at,
                            queued_at=$at,
                            revision=revision+1
                        WHERE item_id=$item_id
                          AND state='expiring'
                          AND read_denied_at IS NULL
                          AND expires_at<=$at;
                        """,
                        ("$item_id", grant.ItemId),
                        ("$at", mutationAt.ToString("O"))),
                    _ => throw new ArgumentOutOfRangeException(nameof(lifecycleDrift)),
                };
                Assert.Equal(lifecycleDrift == "pin-unpin" ? 2 : 1, driftedRows);
                Assert.Equal(
                    1,
                    ScalarLong(
                        connection,
                        """
                        SELECT COUNT(*)
                        FROM retention_items
                        WHERE item_id=$item_id
                          AND state=$state
                          AND expires_at=$expires_at
                          AND revision=$revision
                          AND read_denied_at IS $read_denied_at
                          AND queued_at IS $queued_at;
                        """,
                        ("$item_id", grant.ItemId),
                        ("$state", expectedState),
                        ("$expires_at", naturalExpiry.ToString("O")),
                        ("$revision", expectedRevision),
                        ("$read_denied_at", expectedReadDeniedAt is null
                            ? DBNull.Value
                            : expectedReadDeniedAt),
                        ("$queued_at", expectedQueuedAt is null
                            ? DBNull.Value
                            : expectedQueuedAt)));
                Assert.Equal(
                    immutableTargetBefore,
                    FullRowsSnapshot(
                        connection,
                        "retention_items",
                        "item_id",
                        grant.ItemId,
                        excludedColumns));
                Assert.Equal(
                    sentinelItemBefore,
                    FullRowsSnapshot(
                        connection,
                        "retention_items",
                        "item_id",
                        sentinelItemId));
                Assert.Equal(sourcesBefore, FullRowsSnapshot(connection, "raw_records"));

                Assert.Equal(
                    2,
                    Execute(
                        connection,
                        """
                        INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
                        VALUES($item_id,'access','same-item-access-sentinel',$expires_at,73);
                        INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
                        VALUES($sentinel_item_id,'operation','unrelated-operation-sentinel',$expires_at,74);
                        """,
                        ("$item_id", grant.ItemId),
                        ("$sentinel_item_id", sentinelItemId),
                        ("$expires_at", mutationAt.AddMinutes(3).ToString("O"))));
                retentionItemsAfterDrift = FullRowsSnapshot(connection, "retention_items");
                sourcesAfterDrift = FullRowsSnapshot(connection, "raw_records");
                leasesBeforePublish = FullRowsSnapshot(connection, "retention_leases");
                sameItemAccessBefore = FullRowsSnapshot(
                    connection,
                    "retention_leases",
                    "lease_kind",
                    "access");
                unrelatedOperationBefore = FullRowsSnapshot(
                    connection,
                    "retention_leases",
                    "item_id",
                    sentinelItemId);
            }

            var outcome = store.Publish(
                queueLease,
                projected,
                retentionLease,
                mutationAt.AddDays(1));

            Assert.Equal(SkillProjectionWorkOutcome.Published, outcome);
            using var published = Open(database.Path);
            Assert.Equal(
                1,
                ScalarLong(
                    published,
                    """
                    SELECT COUNT(*)
                    FROM skill_projection_generations AS generation
                    JOIN skill_projection_queue AS queue
                      ON queue.generation_id=generation.generation_id
                    JOIN skill_projection_trace_heads AS head
                      ON head.trace_id=generation.trace_id
                    WHERE generation.generation_id=$generation_id
                      AND generation.lifecycle='current'
                      AND generation.updated_at=$published_at
                      AND queue.state='completed'
                      AND queue.lease_owner IS NULL
                      AND queue.lease_expires_at IS NULL
                      AND queue.next_attempt_at IS NULL
                      AND queue.error_code IS NULL
                      AND head.desired_generation_id=generation.generation_id
                      AND head.current_generation_id=generation.generation_id
                      AND head.updated_at=$published_at;
                    """,
                    ("$generation_id", queueLease.GenerationId),
                    ("$published_at", mutationAt.ToString("O"))));
            Assert.Equal(
                1,
                ScalarLong(
                    published,
                    "SELECT COUNT(*) FROM skill_projection_invocations WHERE generation_id=$generation_id AND raw_record_id=$raw_record_id;",
                    ("$generation_id", queueLease.GenerationId),
                    ("$raw_record_id", primary.RawRecordId)));
            Assert.Equal(
                1,
                ScalarLong(
                    published,
                    "SELECT COUNT(*) FROM skill_projection_inventories WHERE generation_id=$generation_id AND raw_record_id=$raw_record_id;",
                    ("$generation_id", queueLease.GenerationId),
                    ("$raw_record_id", primary.RawRecordId)));
            Assert.Equal(
                1,
                ScalarLong(
                    published,
                    """
                    SELECT COUNT(*)
                    FROM skill_projection_inventory_names AS name
                    JOIN skill_projection_inventories AS inventory
                      ON inventory.inventory_id=name.inventory_id
                    WHERE inventory.generation_id=$generation_id
                      AND inventory.raw_record_id=$raw_record_id;
                    """,
                    ("$generation_id", queueLease.GenerationId),
                    ("$raw_record_id", primary.RawRecordId)));
            Assert.Equal(retentionItemsAfterDrift, FullRowsSnapshot(published, "retention_items"));
            Assert.Equal(sourcesAfterDrift, FullRowsSnapshot(published, "raw_records"));
            Assert.Equal(leasesBeforePublish, FullRowsSnapshot(published, "retention_leases"));
            SourceCompatibilitySchemaV11.Validate(published, transaction: null);
            SkillProjectionSchemaV1.Validate(published, transaction: null);
        }

        using var released = Open(database.Path);
        Assert.Equal(
            0,
            ScalarLong(
                released,
                """
                SELECT COUNT(*)
                FROM retention_leases
                WHERE item_id=$item_id
                  AND lease_kind='operation'
                  AND owner=$owner
                  AND generation=$generation;
                """,
                ("$item_id", retentionLease.Grants[0].ItemId),
                ("$owner", retentionLease.Grants[0].LeaseOwner),
                ("$generation", retentionLease.Grants[0].LeaseGeneration)));
        Assert.Equal(2, ScalarLong(released, "SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(
            retentionItemsAfterDrift,
            FullRowsSnapshot(released, "retention_items"));
        Assert.Equal(sourcesAfterDrift, FullRowsSnapshot(released, "raw_records"));
        Assert.Equal(
            sameItemAccessBefore,
            FullRowsSnapshot(released, "retention_leases", "lease_kind", "access"));
        Assert.Equal(
            unrelatedOperationBefore,
            FullRowsSnapshot(
                released,
                "retention_leases",
                "owner",
                "unrelated-operation-sentinel"));
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
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(ObservedAt.AddSeconds(2))));
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
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(ObservedAt.AddSeconds(2))));
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
                grants,
                static _ => ValueTask.CompletedTask);

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
        var claimedAt = ObservedAt.AddSeconds(1);
        var heartbeatAt = claimedAt.AddSeconds(11);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                retention,
                new MutableTimeProvider(heartbeatAt)));
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
            .Select(PublishedLeaseExpiry)
            .ToArray();

        var renewed = store.Heartbeat(
            forged,
            retentionLease,
            heartbeatAt);

        Assert.Null(renewed);
        using var verification = Open(database.Path);
        Assert.Equal(lease.LeaseExpiresAt.ToString("O"), ScalarText(
            verification,
            "SELECT lease_expires_at FROM skill_projection_queue WHERE generation_id=2;"));
        Assert.Equal(
            grantExpiries,
            retentionLease.Grants
                .Select(PublishedLeaseExpiry)
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
        var claimedAt = ObservedAt.AddSeconds(1);
        var time = new MutableTimeProvider(claimedAt);
        var store = new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(database.Path, retention, time));
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

    private static int Execute(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return command.ExecuteNonQuery();
    }

    private static IReadOnlyList<PersistedSkillFrontierMember> ReadPersistedSkillFrontier(
        SqliteConnection connection,
        long generationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT input.input_ordinal,input.raw_record_id,item.item_id
            FROM skill_projection_generation_inputs AS input
            JOIN retention_items AS item
              ON item.store_kind='raw_record'
             AND item.source_item_id=CAST(input.raw_record_id AS TEXT)
            WHERE input.generation_id=$generation_id
            ORDER BY input.input_ordinal;
            """;
        command.Parameters.AddWithValue("$generation_id", generationId);
        using var reader = command.ExecuteReader();
        var rows = new List<PersistedSkillFrontierMember>();
        while (reader.Read())
            rows.Add(new(reader.GetInt32(0), reader.GetInt64(1), reader.GetString(2)));
        return rows;
    }

    private static void InstallSecondOrdinalRenewalFailureTrigger(
        SqliteConnection connection,
        string firstItemId,
        string secondItemId,
        DateTimeOffset renewedExpiry)
    {
        var firstItemLiteral = SqlLiteral(connection, firstItemId);
        var secondItemLiteral = SqlLiteral(connection, secondItemId);
        var renewedExpiryLiteral = SqlLiteral(
            connection,
            renewedExpiry.ToUniversalTime().ToString("O"));
        using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            CREATE TRIGGER fail_second_skill_retention_renewal
            BEFORE UPDATE OF expires_at ON retention_leases
            WHEN OLD.item_id={{secondItemLiteral}}
              AND NEW.expires_at={{renewedExpiryLiteral}}
              AND EXISTS(
                    SELECT 1
                    FROM retention_leases
                    WHERE item_id={{firstItemLiteral}}
                      AND lease_kind='operation'
                      AND expires_at={{renewedExpiryLiteral}}
              )
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static string SqlLiteral(SqliteConnection connection, object value) =>
        ScalarText(connection, "SELECT quote($value);", ("$value", value));

    private static string FullRowsSnapshot(
        SqliteConnection connection,
        string table,
        string? keyColumn = null,
        object? key = null,
        params string[] excludedColumns)
    {
        var columns = new List<(string Name, int PrimaryKeyOrdinal)>();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = $"PRAGMA table_info({QuoteIdentifier(table)});";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
                columns.Add((reader.GetString(1), reader.GetInt32(5)));
        }

        Assert.NotEmpty(columns);
        Assert.All(
            excludedColumns,
            excluded => Assert.Contains(columns, column => column.Name == excluded));
        if (keyColumn is not null)
            Assert.Contains(columns, column => column.Name == keyColumn);
        var selectedColumns = columns
            .Where(column => !excludedColumns.Contains(column.Name, StringComparer.Ordinal))
            .ToArray();
        Assert.NotEmpty(selectedColumns);
        var orderColumns = selectedColumns
            .Where(static column => column.PrimaryKeyOrdinal > 0)
            .OrderBy(static column => column.PrimaryKeyOrdinal)
            .ToArray();
        if (orderColumns.Length == 0)
            orderColumns = selectedColumns;

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT {string.Join(',', selectedColumns.Select(column => $"quote({QuoteIdentifier(column.Name)})"))} FROM {QuoteIdentifier(table)}";
        if (keyColumn is not null)
        {
            command.CommandText += $" WHERE {QuoteIdentifier(keyColumn)}=$snapshot_key";
            command.Parameters.AddWithValue("$snapshot_key", key ?? DBNull.Value);
        }
        command.CommandText +=
            $" ORDER BY {string.Join(',', orderColumns.Select(column => $"{QuoteIdentifier(column.Name)} COLLATE BINARY"))};";

        var rows = new List<string>
        {
            string.Join('|', selectedColumns.Select(static column => column.Name)),
        };
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(string.Join(
                    '|',
                    Enumerable.Range(0, reader.FieldCount).Select(reader.GetString)));
            }
        }
        if (keyColumn is not null)
            Assert.Equal(2, rows.Count);
        return string.Join('\n', rows);
    }

    private static string[] ReadSkillFrontierAuthority(SqliteConnection connection) =>
    [
        FullRowsSnapshot(connection, "skill_projection_generations"),
        FullRowsSnapshot(connection, "skill_projection_generation_inputs"),
        FullRowsSnapshot(connection, "skill_projection_queue"),
        FullRowsSnapshot(connection, "skill_projection_trace_heads"),
    ];

    private static string[] ReadSkillProjectionState(SqliteConnection connection) =>
    [
        .. ReadSkillFrontierAuthority(connection),
        FullRowsSnapshot(connection, "skill_projection_invocations"),
        FullRowsSnapshot(connection, "skill_projection_inventories"),
        FullRowsSnapshot(connection, "skill_projection_inventory_names"),
    ];

    private static string QuoteIdentifier(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record PersistedSkillFrontierMember(
        int Ordinal,
        long RawRecordId,
        string ItemId);

    private static string[] ReadSkillWorkState(
        SqliteConnection connection,
        long generationId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT generation.*,queue.*,head.*
            FROM skill_projection_generations AS generation
            JOIN skill_projection_queue AS queue
              ON queue.generation_id=generation.generation_id
            JOIN skill_projection_trace_heads AS head
              ON head.trace_id=generation.trace_id
            WHERE generation.generation_id=$generation_id;
            """;
        command.Parameters.AddWithValue("$generation_id", generationId);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var values = Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index)
                ? "<null>"
                : reader.GetValue(index) is byte[] bytes
                    ? Convert.ToHexString(bytes)
                    : Convert.ToString(reader.GetValue(index)) ?? string.Empty)
            .ToArray();
        Assert.False(reader.Read());
        return values;
    }

    private static DateTimeOffset PublishedLeaseExpiry(RetentionReadGrant grant)
    {
        using var publication = grant.EnterLeasePublication();
        return publication.LeaseExpiresAt;
    }

    private static bool GrantIsUsable(
        SqliteConnection connection,
        RetentionReadGrant grant,
        long rawRecordId,
        DateTimeOffset at)
    {
        using var transaction = connection.BeginTransaction();
        var usable = RetentionCatalogStore.IsGrantUsable(
            connection,
            transaction,
            grant,
            rawRecordId,
            at);
        transaction.Rollback();
        return usable;
    }

    private static void SetRetentionLeaseExpiry(
        SqliteConnection connection,
        RetentionReadGrant grant,
        DateTimeOffset expiry)
    {
        Assert.Equal(
            1,
            Execute(
                connection,
                """
                UPDATE retention_leases
                SET expires_at=$expiry
                WHERE item_id=$item_id
                  AND lease_kind='operation'
                  AND owner=$owner
                  AND generation=$generation;
                """,
                ("$expiry", expiry.ToUniversalTime().ToString("O")),
                ("$item_id", grant.ItemId),
                ("$owner", grant.LeaseOwner),
                ("$generation", grant.LeaseGeneration)));
        grant.AdvanceExpiry(expiry);
    }

    private static void SeedExactAdapterCoverage(SqliteConnection connection) =>
        Assert.Equal(
            5,
            Execute(
                connection,
                """
                INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
                VALUES
                    ('session_event_content',1),
                    ('raw_record',1),
                    ('analysis_run_raw',1),
                    ('sensitive_bundle',1),
                    ('analysis_sdk_directory',1);
                """));

    private sealed class SkillRenewalPublicationCheckpoint(string databasePath)
        : ISkillProjectionCheckpoint
    {
        private RetentionReadGrant? grant;
        private long generationId;
        private DateTimeOffset expectedExpiry;

        internal bool GrantBindingWasBlocked { get; private set; }
        internal Task? ConcurrentValidator { get; private set; }

        internal void Configure(
            RetentionReadGrant configuredGrant,
            long configuredGenerationId,
            DateTimeOffset configuredExpectedExpiry)
        {
            grant = configuredGrant;
            generationId = configuredGenerationId;
            expectedExpiry = configuredExpectedExpiry;
        }

        public void Reached(SkillProjectionCheckpoint checkpoint)
        {
            if (checkpoint != SkillProjectionCheckpoint.BeforeRetentionRenewalPublication)
                return;
            using (var connection = Open(databasePath))
            {
                Assert.Equal(
                    "leased",
                    ScalarText(
                        connection,
                        "SELECT state FROM skill_projection_queue WHERE generation_id=$generation_id;",
                        ("$generation_id", generationId)));
            }

            var configuredGrant = grant
                ?? throw new InvalidOperationException("heartbeat checkpoint was not configured");
            var transactionStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var grantBindingAttempted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var completed = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            ConcurrentValidator = completed.Task;
            var thread = new Thread(() =>
            {
                try
                {
                    using var connection = Open(databasePath);
                    using var transaction = connection.BeginTransaction(deferred: false);
                    transactionStarted.TrySetResult();
                    using var bindingProbe = connection.CreateCommand();
                    bindingProbe.Transaction = transaction;
                    bindingProbe.CommandText = """
                        SELECT r.payload_json
                        FROM retention_items i
                        JOIN raw_records r ON r.id=CAST(i.source_item_id AS INTEGER)
                          AND r.retention_owner_token=$retention_read_source_token
                        JOIN retention_leases l ON i.item_id=$retention_read_item_id
                          AND i.revision=$retention_read_revision
                          AND l.item_id=i.item_id
                          AND l.lease_kind=$retention_read_lease_kind
                          AND l.owner=$retention_read_lease_owner
                          AND l.generation=$retention_read_lease_generation
                          AND l.expires_at=$retention_read_lease_expires_at;
                        """;
                    var bound = configuredGrant.TryBindAdmissionSelectorCapability(bindingProbe);
                    grantBindingAttempted.TrySetResult();
                    if (bound)
                        throw new InvalidOperationException(
                            "retention renewal publication was not protected");

                    GrantBindingWasBlocked = true;
                    using var publication = configuredGrant.EnterLeasePublication();
                    Assert.Equal(expectedExpiry, publication.LeaseExpiresAt);
                    transaction.Rollback();
                    completed.TrySetResult();
                }
                catch (Exception exception)
                {
                    transactionStarted.TrySetResult();
                    grantBindingAttempted.TrySetResult();
                    completed.TrySetException(exception);
                }
            })
            {
                IsBackground = true,
            };
            thread.Start();

            Assert.True(transactionStarted.Task.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(grantBindingAttempted.Task.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(completed.Task.IsCompleted);
        }
    }

    private sealed class BlockingSkillTransactionCheckpoint(
        SkillProjectionCheckpoint target) : ISkillProjectionCheckpoint, IDisposable
    {
        private readonly ManualResetEventSlim continuation = new(initialState: false);
        private readonly TaskCompletionSource reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WasReached => reached.Task;
        internal string[] ExpectedState { get; set; } = [];

        public void Reached(SkillProjectionCheckpoint checkpoint)
        {
            if (checkpoint != target)
                return;
            reached.TrySetResult();
            if (!continuation.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("skill projection transaction checkpoint was not released");
        }

        internal void Continue() => continuation.Set();

        public void Dispose()
        {
            continuation.Set();
            continuation.Dispose();
        }
    }

    private sealed class SignalingSkillProjectionCheckpoint(
        SkillProjectionCheckpoint target) : ISkillProjectionCheckpoint
    {
        private readonly TaskCompletionSource reached =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task WasReached => reached.Task;

        public void Reached(SkillProjectionCheckpoint checkpoint)
        {
            if (checkpoint == target)
                reached.TrySetResult();
        }
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

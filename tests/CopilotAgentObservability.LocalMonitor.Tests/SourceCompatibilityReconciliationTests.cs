using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SourceCompatibilityReconciliationTests
{
    private const string TraceId = "11111111111111111111111111111111";
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DecoderRevision_DeletedBeforeDigestPersistsAndReplaysUnavailableReceiptOnly()
    {
        using var database = new TestDatabase();
        var committed = CreateMarkerObservation(
            database.Path,
            "marker-decoder",
            TraceSourceVersionResolutionState.Missing,
            sourceApplicationVersion: null);
        var before = SnapshotAtomicCounts(database.Path);
        var request = Request(
            "marker-decoder-operation",
            committed.ObservationId,
            SourceCompatibilityReconciliationTrigger.DecoderRevision,
            "resolver-2",
            "registry-1");

        var reconciler = CreateReconciler(database.Path);
        var first = reconciler.Reconcile(request);
        var replay = reconciler.Reconcile(request);

        Assert.Equal(SourceCompatibilityReconciliationOutcome.InputUnavailable, first.Outcome);
        Assert.Equal(first, replay);
        Assert.Null(first.SupersessionId);
        Assert.Null(first.CompatibilityRevision);
        Assert.Null(first.GenerationId);
        Assert.Equal(
            before with { SourceReceipts = 1, SkillReceipts = 1 },
            SnapshotAtomicCounts(database.Path));
        using var connection = Open(database.Path);
        Assert.Equal(
            "input_unavailable",
            ScalarText(
                connection,
                "SELECT outcome FROM source_compatibility_reconciliation_receipts;"));
        Assert.Equal(
            "input_unavailable",
            ScalarText(
                connection,
                "SELECT outcome FROM skill_projection_operation_receipts;"));
    }

    [Fact]
    public void RegistryRevision_DeletedBeforeDigestCreatesOnlyTerminalUnavailableGeneration()
    {
        using var database = new TestDatabase();
        var committed = CreateMarkerObservation(
            database.Path,
            "marker-registry",
            TraceSourceVersionResolutionState.Unrecognised,
            "1.0.74");

        var result = CreateReconciler(database.Path).Reconcile(
            Request(
                "marker-registry-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.RegistryRevision,
                "resolver-2",
                "registry-1"));

        Assert.Equal(SourceCompatibilityReconciliationOutcome.Changed, result.Outcome);
        Assert.NotNull(result.GenerationId);
        using var connection = Open(database.Path);
        Assert.Equal("input_unavailable", ScalarText(
            connection,
            "SELECT lifecycle FROM skill_projection_generations;"));
        Assert.Equal("input_unavailable", ScalarText(
            connection,
            "SELECT state FROM skill_projection_queue;"));
        Assert.Equal("skill_projection_input_unavailable", ScalarText(
            connection,
            "SELECT error_code FROM skill_projection_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT attempt_count FROM skill_projection_queue;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT lease_generation FROM skill_projection_queue;"));
        Assert.Equal(result.GenerationId, ScalarLong(
            connection,
            "SELECT desired_generation_id FROM skill_projection_trace_heads;"));
        Assert.Equal(1, ScalarLong(
            connection,
            "SELECT current_generation_id IS NULL FROM skill_projection_trace_heads;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(0, ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_inventories;"));
        Assert.Null(new SqliteSkillProjectionStore(
            database.Path,
            new RawTelemetryStore(
                database.Path,
                RetentionCatalogContext.AdoptExistingCatalogV1(database.Path)))
            .ClaimNext(ObservedAt.AddMinutes(1)));
        Assert.Empty(new SkillProjectionReadService(database.Path)
            .ListCurrentInvocations(TraceId));
        using (var forged = connection.CreateCommand())
        {
            forged.CommandText =
                """
                UPDATE skill_projection_generations SET lifecycle='current';
                UPDATE skill_projection_queue
                SET state='completed',error_code=NULL;
                UPDATE skill_projection_trace_heads
                SET current_generation_id=desired_generation_id;
                INSERT INTO skill_projection_invocations(
                    generation_id,source_arm,raw_record_id,trace_id,span_id,
                    span_ordinal,session_id,skill_name,skill_source,
                    invocation_trigger,source_application_version,projected_at)
                VALUES(
                    $generation_id,'otel_trace_span',$raw_record_id,$trace_id,
                    '2222222222222222',0,NULL,'forged-skill',NULL,NULL,
                    '1.0.74','2026-07-31T00:01:00.0000000+00:00');
                """;
            forged.Parameters.AddWithValue("$generation_id", result.GenerationId!.Value);
            forged.Parameters.AddWithValue("$raw_record_id", committed.RawRecordId);
            forged.Parameters.AddWithValue("$trace_id", TraceId);
            forged.ExecuteNonQuery();
        }
        Assert.Empty(new SkillProjectionReadService(database.Path)
            .ListCurrentInvocations(TraceId));
    }

    [Fact]
    public void RegistryRevision_MarkerFrontierIsUnavailableWhenAnotherObservationRemainsMissing()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var ingestion = new SqliteIngestionCommitStore(database.Path);
        var marker = ingestion.Commit(
            CreateBatch(
                "marker-with-missing-peer",
                TraceSourceVersionResolutionState.Unrecognised,
                "1.0.74",
                "{}"));
        ingestion.Commit(
            CreateBatch(
                "missing-peer",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                "{}"));
        ConvertObservationToMarker(database.Path, marker);

        var result = CreateReconciler(database.Path).Reconcile(
            Request(
                "marker-with-missing-peer-operation",
                marker.ObservationId,
                SourceCompatibilityReconciliationTrigger.RegistryRevision,
                "resolver-2",
                "registry-1"));

        var generationId = Assert.IsType<long>(result.GenerationId);
        using var verification = Open(database.Path);
        Assert.Equal("missing", ScalarText(
            verification,
            "SELECT current_effective_state FROM source_trace_compatibility_revisions;"));
        Assert.Equal(1, ScalarLong(
            verification,
            $"SELECT COUNT(*) FROM skill_projection_trace_heads WHERE desired_generation_id={generationId};"));
        Assert.Equal(1, ScalarLong(
            verification,
            "SELECT current_generation_id IS NULL FROM skill_projection_trace_heads;"));
        Assert.Equal("input_unavailable", ScalarText(
            verification,
            $"SELECT lifecycle FROM skill_projection_generations WHERE generation_id={generationId};"));
        Assert.Equal("input_unavailable", ScalarText(
            verification,
            $"SELECT state FROM skill_projection_queue WHERE generation_id={generationId};"));
        Assert.Equal(1, ScalarLong(
            verification,
            $"""
            SELECT COUNT(*)
            FROM skill_projection_queue
            WHERE generation_id={generationId}
              AND state='input_unavailable'
              AND attempt_count=0
              AND lease_generation=0
              AND lease_owner IS NULL
              AND lease_expires_at IS NULL
              AND next_attempt_at IS NULL
              AND error_code='skill_projection_input_unavailable';
            """));
        SourceCompatibilitySchemaV11.Validate(verification, transaction: null);
        SkillProjectionSchemaV1.Validate(verification, transaction: null);
    }

    [Fact]
    public void MarkerDesiredGeneration_IsSupersededWhenAnExactObservationExtendsTheFrontier()
    {
        using var database = new TestDatabase();
        var marker = CreateMarkerObservation(
            database.Path,
            "marker-before-frontier-extension",
            TraceSourceVersionResolutionState.Unrecognised,
            "1.0.74");
        var first = CreateReconciler(database.Path).Reconcile(
            Request(
                "marker-before-frontier-extension-operation",
                marker.ObservationId,
                SourceCompatibilityReconciliationTrigger.RegistryRevision,
                "resolver-2",
                "registry-1"));
        var oldGenerationId = Assert.IsType<long>(first.GenerationId);
        using (var initial = Open(database.Path))
        {
            Assert.Equal(1, ScalarLong(
                initial,
                $"SELECT COUNT(*) FROM skill_projection_trace_heads WHERE desired_generation_id={oldGenerationId};"));
            Assert.Equal("input_unavailable", ScalarText(
                initial,
                $"SELECT lifecycle FROM skill_projection_generations WHERE generation_id={oldGenerationId};"));
        }

        AppendExactObservation(
            database.Path,
            CreateBatch(
                "exact-peer-after-marker",
                TraceSourceVersionResolutionState.Resolved,
                "1.0.74",
                "{}"));

        using var verification = Open(database.Path);
        var newGenerationId = ScalarLong(
            verification,
            "SELECT desired_generation_id FROM skill_projection_trace_heads;");
        Assert.NotEqual(oldGenerationId, newGenerationId);
        Assert.Equal("superseded", ScalarText(
            verification,
            $"SELECT lifecycle FROM skill_projection_generations WHERE generation_id={oldGenerationId};"));
        Assert.Equal("superseded", ScalarText(
            verification,
            $"SELECT state FROM skill_projection_queue WHERE generation_id={oldGenerationId};"));
        Assert.Equal("input_unavailable", ScalarText(
            verification,
            $"SELECT lifecycle FROM skill_projection_generations WHERE generation_id={newGenerationId};"));
        Assert.Equal(1, ScalarLong(
            verification,
            $"""
            SELECT COUNT(*)
            FROM skill_projection_queue
            WHERE generation_id={newGenerationId}
              AND state='input_unavailable'
              AND attempt_count=0
              AND lease_generation=0
              AND lease_owner IS NULL
              AND lease_expires_at IS NULL
              AND next_attempt_at IS NULL
              AND error_code='skill_projection_input_unavailable';
            """));
        Assert.Equal(1, ScalarLong(
            verification,
            $"SELECT COUNT(*) FROM skill_projection_trace_heads WHERE desired_generation_id={newGenerationId};"));
        Assert.Equal(1, ScalarLong(
            verification,
            "SELECT current_generation_id IS NULL FROM skill_projection_trace_heads;"));
        Assert.Equal(0, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM skill_projection_invocations;"));
        Assert.Equal(0, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM skill_projection_inventories;"));
        SourceCompatibilitySchemaV11.Validate(verification, transaction: null);
        SkillProjectionSchemaV1.Validate(verification, transaction: null);
    }

    [Fact]
    public void DecoderRevision_DerivesResolvedVersionFromLeasedBytesWithoutMutatingBase()
    {
        using var database = new TestDatabase();
        var compatibility = new SqliteSourceCompatibilityStore(database.Path);
        compatibility.CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "decoder-resolution",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));

        var result = CreateReconciler(database.Path).Reconcile(
            Request(
                "decoder-operation-1",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));

        Assert.Equal(SourceCompatibilityReconciliationOutcome.Changed, result.Outcome);
        Assert.Equal(1, result.InterpretationRevision);
        Assert.Equal(
            new TraceSourceVersionResolutionRow(
                TraceId,
                TraceSourceVersionResolutionState.Resolved,
                "1.0.74"),
            compatibility.GetTraceSourceVersionResolution(TraceId));
        using var connection = Open(database.Path);
        Assert.Equal(
            "missing",
            ScalarText(
                connection,
                $"SELECT resolution_state FROM source_trace_version_observations WHERE source_observation_id={committed.ObservationId} AND trace_id='{TraceId}';"));
    }

    [Fact]
    public void EmptyPayload_CannotBeDeclaredResolvedAndBecomesSemanticNoOp()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "empty-payload-authority",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                "{}"));
        var before = SnapshotAtomicCounts(database.Path);
        var request = Request(
            "empty-payload-operation",
            committed.ObservationId,
            SourceCompatibilityReconciliationTrigger.DecoderRevision,
            "resolver-2",
            "registry-1");

        var result = CreateReconciler(database.Path).Reconcile(request);

        Assert.Equal(SourceCompatibilityReconciliationOutcome.NoChange, result.Outcome);
        var after = before with { SourceReceipts = 1, SkillReceipts = 1 };
        Assert.Equal(after, SnapshotAtomicCounts(database.Path));
        Assert.Equal(
            TraceSourceVersionResolutionState.Missing,
            new SqliteSourceCompatibilityStore(database.Path)
                .GetTraceSourceVersionResolution(TraceId)!.State);
        using (var validation = Open(database.Path))
        {
            SourceCompatibilitySchemaV11.Validate(validation, transaction: null);
            SkillProjectionSchemaV1.Validate(validation, transaction: null);
        }
        new RetentionCatalogStore(
                RetentionCatalogContext.AdoptExistingCatalogV1(database.Path))
            .CreateSchema();
        Assert.Equal(after, SnapshotAtomicCounts(database.Path));

        var bundle = Path.Combine(database.Root, "semantic-no-op.zip");
        var restoredPath = Path.Combine(database.Root, "semantic-no-op-restored.sqlite");
        var service = new SqliteRuntimeBackupService();
        Assert.True(service.CreateAndPublish(database.Path, bundle).Success);
        var restored = service.Restore(
            bundle,
            restoredPath,
            new RuntimeRestoreOptions());
        Assert.True(restored.Success, restored.ErrorCode);
        using (var restoredValidation = Open(restoredPath))
        {
            SourceCompatibilitySchemaV11.Validate(
                restoredValidation,
                transaction: null);
            SkillProjectionSchemaV1.Validate(
                restoredValidation,
                transaction: null);
        }

        var replay = CreateReconciler(restoredPath).Reconcile(request);

        Assert.Equal(result, replay);
        Assert.Equal(after, SnapshotAtomicCounts(restoredPath));
    }

    [Fact]
    public void RegistryRevision_UsesPersistedExactTokenAfterPhysicalRawDeletion()
    {
        using var database = new TestDatabase();
        var compatibility = new SqliteSourceCompatibilityStore(database.Path);
        compatibility.CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "deleted-raw-registry-authority",
                TraceSourceVersionResolutionState.Unrecognised,
                "1.0.80",
                "{}"));
        DeleteRaw(database.Path, committed.RawRecordId);

        var result = CreateReconciler(database.Path).Reconcile(
            Request(
                "deleted-raw-registry-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.RegistryRevision,
                "resolver-1",
                "registry-2"));

        Assert.Equal(SourceCompatibilityReconciliationOutcome.Changed, result.Outcome);
        Assert.Equal(
            new TraceSourceVersionResolutionRow(
                TraceId,
                TraceSourceVersionResolutionState.Resolved,
                "1.0.80"),
            compatibility.GetTraceSourceVersionResolution(TraceId));
        using var connection = Open(database.Path);
        Assert.Equal(
            1,
            ScalarLong(
                connection,
                $"SELECT COUNT(*) FROM skill_projection_generation_inputs WHERE generation_id={result.GenerationId} AND raw_record_id={committed.RawRecordId};"));
    }

    [Theory]
    [InlineData("unknown-resolver", "registry-1")]
    [InlineData("resolver-2", "unknown-registry")]
    public void UnknownAcceptedRevisionTuple_FailsClosed(
        string resolverRevision,
        string registryRevision)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "unknown-revision-authority",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        var before = SnapshotAtomicCounts(database.Path);

        var action = () => CreateReconciler(database.Path).Reconcile(
            Request(
                "unknown-revision-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                resolverRevision,
                registryRevision));

        Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(before, SnapshotAtomicCounts(database.Path));
    }

    [Fact]
    public void UnknownProjectorVersion_FailsClosed()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "unknown-projector-authority",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        var before = SnapshotAtomicCounts(database.Path);

        var action = () => CreateReconciler(database.Path).Reconcile(
            Request(
                "unknown-projector-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1") with
            {
                ProjectorVersion = "unknown-projector",
            });

        Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(before, SnapshotAtomicCounts(database.Path));
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("read_denied")]
    [InlineData("deleted")]
    public void DecoderInputWithoutRetentionAuthority_MakesNoSourceOrSkillMutation(string mode)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                $"decoder-{mode}",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        MutateRawAvailability(database.Path, committed.RawRecordId, mode);
        var before = SnapshotAtomicCounts(database.Path);

        var action = () => CreateReconciler(database.Path).Reconcile(
            Request(
                $"decoder-{mode}-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));

        Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(before, SnapshotAtomicCounts(database.Path));
    }

    [Fact]
    public void IdenticalReceipt_ReplaysAfterRawDeletionAndAuthorityCatalogChange()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "receipt-replay",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        var request = Request(
            "receipt-replay-operation",
            committed.ObservationId,
            SourceCompatibilityReconciliationTrigger.DecoderRevision,
            "resolver-2",
            "registry-1");
        var first = CreateReconciler(database.Path).Reconcile(request);
        DeleteRaw(database.Path, committed.RawRecordId);

        var replay = new SourceCompatibilityReconciler(
                database.Path,
                SourceCompatibilityReconciliationAuthority.Empty,
                new MutableTimeProvider(ObservedAt.AddMinutes(2)))
            .Reconcile(request);

        Assert.Equal(first, replay);
        using var connection = Open(database.Path);
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM source_compatibility_reconciliation_receipts;"));
    }

    [Fact]
    public void SameOperationKeyWithDifferentCanonicalFingerprint_IsHardConflict()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "receipt-conflict",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        var request = Request(
            "receipt-conflict-operation",
            committed.ObservationId,
            SourceCompatibilityReconciliationTrigger.DecoderRevision,
            "resolver-2",
            "registry-1");
        CreateReconciler(database.Path).Reconcile(request);
        var before = SnapshotAtomicCounts(database.Path);

        var action = () => new SourceCompatibilityReconciler(
                database.Path,
                SourceCompatibilityReconciliationAuthority.Empty,
                new MutableTimeProvider(ObservedAt.AddMinutes(2)))
            .Reconcile(request with { ResolverRevision = "resolver-3" });

        Assert.Throws<InvalidOperationException>(action);
        Assert.Equal(before, SnapshotAtomicCounts(database.Path));
    }

    [Fact]
    public void UnchangedAggregateCorrection_StillAdvancesRevisionAndAtomicGeneration()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var first = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "unchanged-aggregate-first",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "unchanged-aggregate-second",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                "{}"));
        using var beforeConnection = Open(database.Path);
        var revisionBefore = ScalarLong(
            beforeConnection,
            $"SELECT current_revision FROM source_trace_compatibility_revisions WHERE trace_id='{TraceId}';");
        var generationsBefore = ScalarLong(
            beforeConnection,
            $"SELECT COUNT(*) FROM skill_projection_generations WHERE trace_id='{TraceId}';");
        var queuesBefore = ScalarLong(
            beforeConnection,
            $"SELECT COUNT(*) FROM skill_projection_queue WHERE trace_id='{TraceId}';");
        beforeConnection.Close();
        long? queueObservedBeforeCommit = null;
        var reconciler = CreateReconciler(
            database.Path,
            checkpoint =>
            {
                if (checkpoint != SourceCompatibilityReconciliationCheckpoint.BeforeCommit)
                    return;
                using var observer = Open(database.Path);
                queueObservedBeforeCommit = ScalarLong(
                    observer,
                    $"SELECT COUNT(*) FROM skill_projection_queue WHERE trace_id='{TraceId}';");
            });

        var result = reconciler.Reconcile(
            Request(
                "unchanged-aggregate-operation",
                first.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));

        Assert.Equal(SourceCompatibilityReconciliationOutcome.Changed, result.Outcome);
        Assert.Equal(revisionBefore + 1, result.CompatibilityRevision);
        Assert.NotNull(result.GenerationId);
        Assert.Equal(queuesBefore, queueObservedBeforeCommit);
        using var connection = Open(database.Path);
        Assert.Equal(
            generationsBefore + 1,
            ScalarLong(
                connection,
                $"SELECT COUNT(*) FROM skill_projection_generations WHERE trace_id='{TraceId}';"));
        Assert.Equal(
            queuesBefore + 1,
            ScalarLong(
                connection,
                $"SELECT COUNT(*) FROM skill_projection_queue WHERE trace_id='{TraceId}';"));
        Assert.Equal(
            "missing",
            ScalarText(
                connection,
                $"SELECT current_effective_state FROM source_trace_compatibility_revisions WHERE trace_id='{TraceId}';"));
    }

    [Fact]
    public void FailureBeforeCommit_RollsBackLedgerRevisionGenerationQueueAndReceiptsTogether()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "rollback-observation",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        var before = SnapshotAtomicCounts(database.Path);
        var reconciler = CreateReconciler(
            database.Path,
            checkpoint =>
            {
                if (checkpoint == SourceCompatibilityReconciliationCheckpoint.BeforeCommit)
                    throw new InvalidOperationException("injected_before_commit");
            });

        Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(
            Request(
                "decoder-operation-rollback",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1")));

        Assert.Equal(before, SnapshotAtomicCounts(database.Path));
    }

    [Fact]
    public void SchemaValidation_RejectsLedgerRevisionBeyondTheCurrentHead()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "head-validation-observation",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        CreateReconciler(database.Path).Reconcile(
            Request(
                "head-validation-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));
        using var connection = Open(database.Path);
        using (var append = connection.CreateCommand())
        {
            append.CommandText =
                """
                INSERT INTO source_trace_version_interpretation_supersessions(
                    source_observation_id,trace_id,previous_interpretation_revision,
                    new_interpretation_revision,derived_state,exact_version,reason,
                    raw_record_id,input_evidence_kind,raw_payload_sha256,
                    resolver_revision,registry_revision,projector_version,created_at,
                    operation_fingerprint)
                VALUES(
                    $source_observation_id,$trace_id,1,2,'resolved','1.0.74',
                    'decoder_revision',$raw_record_id,'payload_sha256',$digest,
                    'resolver-3','registry-1','skill-projector-1',$created_at,$fingerprint);
                """;
            append.Parameters.AddWithValue("$source_observation_id", committed.ObservationId);
            append.Parameters.AddWithValue("$trace_id", TraceId);
            append.Parameters.AddWithValue("$raw_record_id", committed.RawRecordId);
            append.Parameters.AddWithValue("$digest", Sha256(VersionPayload("1.0.74")));
            append.Parameters.AddWithValue("$created_at", ObservedAt.AddMinutes(2).ToString("O"));
            append.Parameters.AddWithValue("$fingerprint", new string('a', 64));
            append.ExecuteNonQuery();
        }

        Assert.Throws<InvalidOperationException>(
            () => SourceCompatibilitySchemaV11.Validate(connection, transaction: null));
    }

    [Theory]
    [InlineData("resolver_revision", "resolver/path")]
    [InlineData("resolver_revision", "resolver\\path")]
    [InlineData("registry_revision", "registry/path")]
    [InlineData("registry_revision", "registry\\path")]
    [InlineData("projector_version", "projector/path")]
    [InlineData("projector_version", "projector\\path")]
    public void RuntimePreflightRejectsRestoredRevisionTokenPathSeparatorsWithoutMutation(
        string field,
        string invalidValue)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "revision-token-restore",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        CreateReconciler(database.Path).Reconcile(
            Request(
                "revision-token-restore-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));
        using (var connection = Open(database.Path))
        {
            if (field == "projector_version")
            {
                using var update = connection.CreateCommand();
                update.CommandText =
                    """
                    UPDATE skill_projection_generations
                    SET projector_version=$value;
                    UPDATE skill_projection_queue
                    SET projector_version=$value;
                    """;
                update.Parameters.AddWithValue("$value", invalidValue);
                update.ExecuteNonQuery();
            }
            else
            {
                var trigger = Assert.Single(
                    SourceCompatibilitySchemaV11.TriggerDefinitions,
                    static item =>
                        item.Name
                        == "source_trace_version_interpretation_supersessions_update_rejected");
                using var update = connection.CreateCommand();
                update.CommandText =
                    $"""
                    DROP TRIGGER {trigger.Name};
                    UPDATE source_trace_version_interpretation_supersessions
                    SET {field}=$value;
                    {trigger.Sql}
                    """;
                update.Parameters.AddWithValue("$value", invalidValue);
                update.ExecuteNonQuery();
            }
            using var checkpoint = connection.CreateCommand();
            checkpoint.CommandText =
                """
                PRAGMA wal_checkpoint(TRUNCATE);
                PRAGMA journal_mode=DELETE;
                """;
            checkpoint.ExecuteNonQuery();
        }
        var before = SHA256.HashData(File.ReadAllBytes(database.Path));

        var preflight = new SqliteRuntimeBackupService()
            .PreflightForMigration(database.Path);

        Assert.False(preflight.Success);
        Assert.Equal(
            RuntimeBackupErrorCodes.RestoreIncompatible,
            preflight.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(database.Path)));
    }

    private static SourceCompatibilityReconciler CreateReconciler(
        string databasePath,
        Action<SourceCompatibilityReconciliationCheckpoint>? checkpoint = null) =>
        new(
            databasePath,
            SourceCompatibilityReconciliationAuthority.Create(
            [
                new(
                    "resolver-2",
                    "registry-1",
                    Registry("1.0.74")),
                new(
                    "resolver-1",
                    "registry-2",
                    Registry("1.0.80")),
            ]),
            new MutableTimeProvider(ObservedAt.AddMinutes(1)),
            checkpoint);

    private static VerifiedSourceFingerprintRegistry Registry(string version) =>
        VerifiedSourceFingerprintRegistry.Create(
        [
            VerifiedSourceFingerprintEvidence.Create(
                "github-copilot-cli",
                version,
                new string('a', 64)),
        ],
        [],
        []);

    private static SourceCompatibilityReconciliationRequest Request(
        string operationKey,
        long sourceObservationId,
        SourceCompatibilityReconciliationTrigger trigger,
        string resolverRevision,
        string registryRevision,
        long expectedRevision = 0) =>
        SourceCompatibilityReconciliationRequest.Create(
            operationKey,
            sourceObservationId,
            TraceId,
            expectedRevision,
            trigger,
            resolverRevision,
            registryRevision,
            SkillProjectionGenerationParticipant.CurrentProjectorVersion);

    private static ValidatedIngestionBatch CreateBatch(
        string ingestBatchId,
        TraceSourceVersionResolutionState state,
        string? sourceApplicationVersion,
        string payloadJson)
    {
        var inventory = OtlpJsonStructuralWalker.Build(payloadJson, ObservedAt);
        var decision = SourceCompatibilityEvaluator.Assess(
            "github-copilot-cli",
            sourceApplicationVersion,
            inventory,
            observedRecognizedCount: 1,
            VerifiedSourceFingerprintRegistry.Create([], [], []));
        var observation = SourceObservationBatchDraft.Create(
            ingestBatchId,
            "github-copilot-cli",
            sourceApplicationVersion,
            "github-copilot-otel",
            "adapter-1",
            inventory,
            decision,
            SourceCaptureContentState.Available,
            ObservedAt,
            [TraceSourceVersionResolutionDraft.Create(TraceId, state, sourceApplicationVersion)]);
        return ValidatedIngestionBatch.Create(
            new RawTelemetryRecord(
                Id: null,
                Source: RawTelemetrySources.RawOtlp,
                TraceId,
                ReceivedAt: ObservedAt,
                ResourceAttributesJson: null,
                PayloadJson: payloadJson),
            observation);
    }

    private static string VersionPayload(string version) =>
        """
        {"resourceSpans":[{
          "resource":{"attributes":[
            {"key":"service.version","value":{"stringValue":"__VERSION__"}}
          ]},
          "scopeSpans":[{"spans":[{
            "traceId":"11111111111111111111111111111111",
            "spanId":"2222222222222222"
          }]}]
        }]}
        """.Replace("__VERSION__", version, StringComparison.Ordinal);

    private static void MutateRawAvailability(string path, long rawRecordId, string mode)
    {
        if (mode == "deleted")
        {
            DeleteRaw(path, rawRecordId);
            return;
        }
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = mode switch
        {
            "expired" =>
                """
                UPDATE retention_items
                SET expires_at=$at
                WHERE store_kind='raw_record' AND source_item_id=$raw_record_id;
                """,
            "read_denied" =>
                """
                UPDATE retention_items
                SET state='expired_pending_deletion',
                    read_denied_at=$at,
                    queued_at=$at,
                    revision=revision+1
                WHERE store_kind='raw_record' AND source_item_id=$raw_record_id;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        command.Parameters.AddWithValue("$at", ObservedAt.AddSeconds(30).ToString("O"));
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId.ToString());
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void DeleteRaw(string path, long rawRecordId)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM raw_records WHERE id=$raw_record_id;";
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static CommittedIngestionIds CreateMarkerObservation(
        string path,
        string observationId,
        TraceSourceVersionResolutionState state,
        string? sourceApplicationVersion)
    {
        new SqliteSourceCompatibilityStore(path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(path).Commit(
            CreateBatch(observationId, state, sourceApplicationVersion, "{}"));
        ConvertObservationToMarker(path, committed);
        return committed;
    }

    private static void ConvertObservationToMarker(
        string path,
        CommittedIngestionIds committed)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM skill_projection_queue;
            DELETE FROM skill_projection_trace_heads;
            DELETE FROM skill_projection_generation_inputs;
            DELETE FROM skill_projection_generations;
            DROP TRIGGER source_schema_observations_projection_input_update_rejected;
            UPDATE source_schema_observations
            SET input_evidence_kind='deleted_before_digest_v10',
                raw_payload_sha256=NULL
            WHERE id=$source_observation_id;
            DELETE FROM raw_records WHERE id=$raw_record_id;
            CREATE TRIGGER source_schema_observations_projection_input_update_rejected
            BEFORE UPDATE OF input_evidence_kind,raw_payload_sha256 ON source_schema_observations
            WHEN OLD.input_evidence_kind IS NOT NEW.input_evidence_kind
              OR OLD.raw_payload_sha256 IS NOT NEW.raw_payload_sha256
            BEGIN SELECT RAISE(ABORT,'source_projection_input_immutable'); END;
            """;
        command.Parameters.AddWithValue("$source_observation_id", committed.ObservationId);
        command.Parameters.AddWithValue("$raw_record_id", committed.RawRecordId);
        command.ExecuteNonQuery();
    }

    private static void AppendExactObservation(
        string path,
        ValidatedIngestionBatch batch)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        var before = SourceCompatibilityReconciler.ReadEffectiveTrace(
            connection,
            transaction,
            TraceId);
        var ownerToken = RandomNumberGenerator.GetBytes(32);
        var rawRecordId = RawTelemetryRecordSql.Insert(
            connection,
            transaction,
            batch.RawRecord,
            ownerToken);
        new RetentionCatalogStore(path).RegisterRawRecord(
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
            TraceId,
            before,
            ObservedAt.AddMinutes(2));
        transaction.Commit();
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

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static AtomicCounts SnapshotAtomicCounts(string path)
    {
        using var connection = Open(path);
        return new(
            ScalarLong(connection, "SELECT COUNT(*) FROM source_trace_version_interpretation_supersessions;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM source_trace_version_interpretation_heads;"),
            ScalarLong(connection, "SELECT COALESCE(SUM(current_revision),0) FROM source_trace_compatibility_revisions;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_generations;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_generation_inputs;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_trace_heads;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_queue;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM source_compatibility_reconciliation_receipts;"),
            ScalarLong(connection, "SELECT COUNT(*) FROM skill_projection_operation_receipts;"));
    }

    private sealed record AtomicCounts(
        long Ledger,
        long InterpretationHeads,
        long CompatibilityRevisionSum,
        long Generations,
        long Inputs,
        long GenerationHeads,
        long Queue,
        long SourceReceipts,
        long SkillReceipts);

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"source-reconciliation-{Guid.NewGuid():N}");

        public TestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
        }

        public string Path { get; }
        public string Root => directory;

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

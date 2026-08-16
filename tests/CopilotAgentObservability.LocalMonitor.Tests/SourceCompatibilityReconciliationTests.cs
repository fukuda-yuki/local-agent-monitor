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

        Func<RawTelemetryRecord>? retainedAccess = null;
        var observedPayload = string.Empty;
        var result = CreateReconciler(
            database.Path,
            lastRawAccessObserverForTesting: access =>
            {
                retainedAccess = access;
                observedPayload = access().PayloadJson;
            }).Reconcile(
            Request(
                "decoder-operation-1",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));

        Assert.Equal(SourceCompatibilityReconciliationOutcome.Changed, result.Outcome);
        Assert.Contains("1.0.74", observedPayload, StringComparison.Ordinal);
        Assert.Throws<ObjectDisposedException>(() => retainedAccess!());
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

    [Theory]
    [InlineData("pin", "retained_by_policy", 2)]
    [InlineData("unpin", "expiring", 3)]
    [InlineData("cleanup_read_denied", "deletion_queued", 3)]
    public void DecoderRevision_PostAdmissionLifecycleDriftCommitsFromLiveGrantAndReleasesExactOperationLease(
        string mutation,
        string expectedState,
        long expectedRevision)
    {
        using var database = new TestDatabase();
        var compatibility = new SqliteSourceCompatibilityStore(database.Path);
        compatibility.CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                $"post-admission-{mutation}",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        var unrelatedRawRecordId = new RawTelemetryStore(
                database.Path,
                RetentionCatalogContext.AdoptExistingCatalogV1(database.Path),
                new MutableTimeProvider(ObservedAt.AddMinutes(1)))
            .Insert(new RawTelemetryRecord(
                Id: null,
                RawTelemetrySources.RawOtlp,
                TraceId: null,
                ObservedAt,
                ResourceAttributesJson: null,
                PayloadJson: "{}"));
        using (var beforeConnection = Open(database.Path))
        {
            Assert.Equal(1, ScalarLong(
                beforeConnection,
                $"SELECT revision FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId}';"));
        }
        var rawBefore = SnapshotTable(database.Path, "raw_records");
        string? retentionAfterDrift = null;
        string? accessLeaseBefore = null;
        string? unrelatedOperationLeaseBefore = null;
        var admissionCheckpointCount = 0;
        var reconciler = CreateReconciler(
            database.Path,
            checkpoint =>
            {
                if (checkpoint != SourceCompatibilityReconciliationCheckpoint.AfterRetentionAdmission)
                    return;
                admissionCheckpointCount++;
                using var connection = Open(database.Path);
                Assert.Equal(1, ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM retention_leases l JOIN retention_items i ON i.item_id=l.item_id WHERE i.store_kind='raw_record' AND i.source_item_id='{committed.RawRecordId}' AND l.lease_kind='operation' AND l.expires_at>'{ObservedAt.AddMinutes(1):O}';"));
                ApplyPostAdmissionRetentionMutation(
                    connection,
                    committed.RawRecordId,
                    mutation);
                Assert.Equal(expectedState, ScalarText(
                    connection,
                    $"SELECT state FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId}';"));
                Assert.Equal(expectedRevision, ScalarLong(
                    connection,
                    $"SELECT revision FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId}';"));
                retentionAfterDrift = SnapshotTable(connection, "retention_items");
                InsertUnrelatedAccessLease(connection, committed.RawRecordId);
                accessLeaseBefore = SnapshotRawLease(connection, committed.RawRecordId, "access");
                InsertUnrelatedOperationLease(
                    connection,
                    committed.RawRecordId,
                    unrelatedRawRecordId);
                unrelatedOperationLeaseBefore = SnapshotRawLease(
                    connection,
                    unrelatedRawRecordId,
                    "operation");
            });

        var result = reconciler.Reconcile(
            Request(
                $"post-admission-{mutation}-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));

        Assert.Equal(1, admissionCheckpointCount);
        Assert.Equal(SourceCompatibilityReconciliationOutcome.Changed, result.Outcome);
        Assert.Equal(1, result.InterpretationRevision);
        Assert.Equal(
            new TraceSourceVersionResolutionRow(
                TraceId,
                TraceSourceVersionResolutionState.Resolved,
                "1.0.74"),
            compatibility.GetTraceSourceVersionResolution(TraceId));
        Assert.Equal(rawBefore, SnapshotTable(database.Path, "raw_records"));
        using var verification = Open(database.Path);
        Assert.Equal(expectedState, ScalarText(
            verification,
            $"SELECT state FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId}';"));
        Assert.Equal(expectedRevision, ScalarLong(
            verification,
            $"SELECT revision FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{committed.RawRecordId}';"));
        Assert.Equal(retentionAfterDrift, SnapshotTable(verification, "retention_items"));
        Assert.Equal(0, ScalarLong(
            verification,
            $"SELECT COUNT(*) FROM retention_leases l JOIN retention_items i ON i.item_id=l.item_id WHERE i.store_kind='raw_record' AND i.source_item_id='{committed.RawRecordId}' AND l.lease_kind='operation';"));
        Assert.Equal(
            accessLeaseBefore,
            SnapshotRawLease(verification, committed.RawRecordId, "access"));
        Assert.Equal(
            unrelatedOperationLeaseBefore,
            SnapshotRawLease(verification, unrelatedRawRecordId, "operation"));
        Assert.Equal(1, ScalarLong(
            verification,
            "SELECT COUNT(*) FROM source_compatibility_reconciliation_receipts;"));
        SourceCompatibilitySchemaV11.Validate(verification, transaction: null);
        SkillProjectionSchemaV1.Validate(verification, transaction: null);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("generation")]
    public void DecoderRevision_ReplacedOperationLeaseTupleAfterAdmissionFailsWithoutDeletingReplacement(
        string tupleMember)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "post-admission-replaced-lease",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        var rawBefore = SnapshotTable(database.Path, "raw_records");
        var retentionItemBefore = SnapshotTable(database.Path, "retention_items");
        var publicationBefore = SnapshotAtomicCounts(database.Path);
        string? replacementLeaseBefore = null;
        string? accessLeaseBefore = null;
        var reconciler = CreateReconciler(
            database.Path,
            checkpoint =>
            {
                if (checkpoint != SourceCompatibilityReconciliationCheckpoint.AfterRetentionAdmission)
                    return;
                using var connection = Open(database.Path);
                Assert.Equal(1, ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM retention_leases l JOIN retention_items i ON i.item_id=l.item_id WHERE i.store_kind='raw_record' AND i.source_item_id='{committed.RawRecordId}' AND l.lease_kind='operation' AND l.expires_at>'{ObservedAt.AddMinutes(1):O}';"));
                ReplaceOperationLeaseTuple(
                    connection,
                    committed.RawRecordId,
                    tupleMember);
                replacementLeaseBefore = SnapshotRawLease(
                    connection,
                    committed.RawRecordId,
                    "operation");
                InsertUnrelatedAccessLease(connection, committed.RawRecordId);
                accessLeaseBefore = SnapshotRawLease(
                    connection,
                    committed.RawRecordId,
                    "access");
            });

        var exception = Assert.Throws<InvalidOperationException>(() => reconciler.Reconcile(
            Request(
                $"post-admission-replaced-{tupleMember}-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1")));

        Assert.Equal("source_compatibility_retained_input_unavailable", exception.Message);
        Assert.Equal(rawBefore, SnapshotTable(database.Path, "raw_records"));
        Assert.Equal(retentionItemBefore, SnapshotTable(database.Path, "retention_items"));
        Assert.Equal(publicationBefore, SnapshotAtomicCounts(database.Path));
        using var verification = Open(database.Path);
        Assert.Equal(
            replacementLeaseBefore,
            SnapshotRawLease(verification, committed.RawRecordId, "operation"));
        Assert.Equal(
            accessLeaseBefore,
            SnapshotRawLease(verification, committed.RawRecordId, "access"));
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
        var created = service.CreateAndPublish(database.Path, bundle);
        Assert.True(created.Success, created.ErrorCode);
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
    public void SourceIdentityOwners_InsertOrReplaceCannotReplaceAnyUniqueIdentityWhenRecursiveTriggersAreOff()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "all-source-identities",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        CreateReconciler(database.Path).Reconcile(
            Request(
                "all-source-identities-operation",
                committed.ObservationId,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1"));
        using var connection = Open(database.Path);
        Execute(connection, "PRAGMA foreign_keys=OFF; PRAGMA recursive_triggers=OFF;");
        var sourceSnapshot = SnapshotTable(connection, "source_schema_observations");

        foreach (var identity in new[] { "id", "observation_id", "raw_record_id", "ingest_batch_id" })
        {
            var id = identity == "id" ? "id" : "NULL";
            var observationId = identity == "observation_id"
                ? "observation_id"
                : $"observation_id || '-{identity}'";
            var rawRecordId = identity == "raw_record_id" ? "raw_record_id" : "NULL";
            var evidenceKind = identity == "raw_record_id" ? "input_evidence_kind" : "NULL";
            var payloadDigest = identity == "raw_record_id" ? "raw_payload_sha256" : "NULL";
            var ingestBatchId = identity == "ingest_batch_id" ? "ingest_batch_id" : "NULL";
            var sql =
                $"""
                INSERT OR REPLACE INTO source_schema_observations(
                    id,observation_id,raw_record_id,raw_payload_sha256,input_evidence_kind,
                    ingest_batch_id,source_surface,source_application_version,source_adapter,
                    adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                    reason_code,next_action,capture_content_state,unknown_span_count,
                    unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                    overflow_occurrence_count,observed_at)
                SELECT
                    {id},{observationId},{rawRecordId},{payloadDigest},{evidenceKind},
                    {ingestBatchId},source_surface,source_application_version,source_adapter,
                    adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                    reason_code,next_action,capture_content_state,unknown_span_count,
                    unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                    overflow_occurrence_count,observed_at
                FROM source_schema_observations
                WHERE id={committed.ObservationId};
                """;

            Assert.Contains(
                "source_schema_observation_no_replace",
                Assert.Throws<SqliteException>(() => Execute(connection, sql)).Message,
                StringComparison.Ordinal);
            Assert.Equal(
                sourceSnapshot,
                SnapshotTable(connection, "source_schema_observations"));
        }

        var supersessionSnapshot = SnapshotTable(
            connection,
            "source_trace_version_interpretation_supersessions");
        Assert.Contains(
            "source_compatibility_supersession_no_replace",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                "INSERT OR REPLACE INTO source_trace_version_interpretation_supersessions SELECT * FROM source_trace_version_interpretation_supersessions;")).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            supersessionSnapshot,
            SnapshotTable(connection, "source_trace_version_interpretation_supersessions"));
        Assert.Contains(
            "source_compatibility_supersession_no_replace",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                """
                INSERT OR REPLACE INTO source_trace_version_interpretation_supersessions
                SELECT supersession_id+100,source_observation_id,trace_id,
                       previous_interpretation_revision,new_interpretation_revision,
                       derived_state,exact_version,reason,raw_record_id,input_evidence_kind,
                       raw_payload_sha256,resolver_revision,registry_revision,projector_version,
                       created_at,operation_fingerprint
                FROM source_trace_version_interpretation_supersessions;
                """)).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            supersessionSnapshot,
            SnapshotTable(connection, "source_trace_version_interpretation_supersessions"));

        var headSnapshot = SnapshotTable(
            connection,
            "source_trace_version_interpretation_heads");
        Assert.Contains(
            "source_compatibility_head_no_replace",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                "INSERT OR REPLACE INTO source_trace_version_interpretation_heads SELECT * FROM source_trace_version_interpretation_heads;")).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            headSnapshot,
            SnapshotTable(connection, "source_trace_version_interpretation_heads"));
        Assert.Contains(
            "source_compatibility_head_no_replace",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                """
                INSERT OR REPLACE INTO source_trace_version_interpretation_heads
                SELECT source_observation_id+100,trace_id,current_interpretation_revision,
                       current_supersession_id
                FROM source_trace_version_interpretation_heads;
                """)).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            headSnapshot,
            SnapshotTable(connection, "source_trace_version_interpretation_heads"));

        var receiptSnapshot = SnapshotTable(
            connection,
            "source_compatibility_reconciliation_receipts");
        Assert.Contains(
            "source_compatibility_receipt_no_replace",
            Assert.Throws<SqliteException>(() => Execute(
                connection,
                "INSERT OR REPLACE INTO source_compatibility_reconciliation_receipts SELECT * FROM source_compatibility_reconciliation_receipts;")).Message,
            StringComparison.Ordinal);
        Assert.Equal(
            receiptSnapshot,
            SnapshotTable(connection, "source_compatibility_reconciliation_receipts"));

        foreach (var suffix in new[] { "first-null", "second-null" })
        {
            Execute(
                connection,
                $"""
                INSERT INTO source_schema_observations(
                    observation_id,compatibility_state,reason_code,next_action,
                    capture_content_state,unknown_span_count,unknown_event_count,
                    unknown_attribute_count,overflow_distinct_count,overflow_occurrence_count,
                    observed_at)
                VALUES(
                    '{suffix}','supported',NULL,'none','available',0,0,0,0,0,
                    '2026-07-31T00:02:00.0000000+00:00');
                """);
        }
        Assert.Equal(
            2,
            ScalarLong(
                connection,
                "SELECT COUNT(*) FROM source_schema_observations WHERE raw_record_id IS NULL AND ingest_batch_id IS NULL;"));
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
    [InlineData("null")]
    [InlineData("blob")]
    public void CurrentSchemaValidation_RejectsPresentRawEvidenceThatIsNotText(
        string storageClass)
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        var committed = new SqliteIngestionCommitStore(database.Path).Commit(
            CreateBatch(
                "current-nontext-raw",
                TraceSourceVersionResolutionState.Missing,
                sourceApplicationVersion: null,
                VersionPayload("1.0.74")));
        if (storageClass == "blob")
        {
            using var connection = Open(database.Path);
            Execute(
                connection,
                $"UPDATE raw_records SET payload_json=zeroblob(1) WHERE id={committed.RawRecordId};");
        }
        else
        {
            SetRawPayloadToNullWithoutChangingStoredSchema(
                database.Path,
                committed.RawRecordId);
        }
        using var verification = Open(database.Path);

        var error = Assert.Throws<InvalidOperationException>(
            () => MonitorSchemaMigrator.ValidateBeforeInitialization(verification));

        Assert.Equal("source_projection_input_authority_invalid", error.Message);
    }

    [Fact]
    public void CurrentSchemaValidation_RejectsDeletedBeforeDigestMarkerWhenRawRowIsPresent()
    {
        using var database = new TestDatabase();
        var committed = CreateMarkerObservation(
            database.Path,
            "marker-with-present-raw",
            TraceSourceVersionResolutionState.Missing,
            sourceApplicationVersion: null);
        using var connection = Open(database.Path);
        Execute(
            connection,
            $"""
            INSERT INTO raw_records(
                id,source,trace_id,received_at,resource_attributes_json,payload_json,
                schema_version,retention_owner_token)
            VALUES(
                {committed.RawRecordId},'raw-otlp','{TraceId}',
                '2026-07-31T00:02:00.0000000+00:00',NULL,'[]',1,randomblob(32));
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => MonitorSchemaMigrator.ValidateBeforeInitialization(connection));

        Assert.Equal("source_projection_input_authority_invalid", error.Message);
    }

    [Fact]
    public void CurrentSchemaValidation_RejectsNegativeRawReferenceAtRevisionZero()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        Execute(
            connection,
            """
            PRAGMA ignore_check_constraints=ON;
            INSERT INTO source_schema_observations(
                id,observation_id,raw_record_id,compatibility_state,reason_code,next_action,
                capture_content_state,unknown_span_count,unknown_event_count,
                unknown_attribute_count,overflow_distinct_count,overflow_occurrence_count,
                observed_at)
            VALUES(
                1,'negative-raw-revision-zero',-1,'supported',NULL,'none','available',
                0,0,0,0,0,'2026-07-31T00:00:00.0000000+00:00');
            PRAGMA ignore_check_constraints=OFF;
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => SourceCompatibilitySchemaV11.Validate(connection, transaction: null));

        Assert.Equal("source_compatibility_identity_invalid", error.Message);
    }

    [Fact]
    public void CurrentSchemaValidation_RejectsCoherentNegativeSourceIdentityGraphWhenChecksAreDisabled()
    {
        using var database = new TestDatabase();
        new SqliteSourceCompatibilityStore(database.Path).CreateSchema();
        using var connection = Open(database.Path);
        Execute(
            connection,
            $"""
            PRAGMA ignore_check_constraints=ON;
            INSERT INTO raw_records(
                id,source,trace_id,received_at,resource_attributes_json,payload_json,
                schema_version,retention_owner_token)
            VALUES(1,'raw-otlp','{TraceId}','2026-07-31T00:00:00.0000000+00:00',
                   NULL,'[]',1,randomblob(32));
            INSERT INTO source_schema_observations(
                id,observation_id,raw_record_id,raw_payload_sha256,input_evidence_kind,
                ingest_batch_id,source_surface,source_application_version,source_adapter,
                adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                reason_code,next_action,capture_content_state,unknown_span_count,
                unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                overflow_occurrence_count,observed_at)
            VALUES(
                -1,'negative-source-graph',1,'{Sha256("[]")}',
                'payload_sha256','negative-source-graph-batch','github-copilot-cli',
                '1.0.74','github-copilot-otel','adapter-1',NULL,NULL,'supported',NULL,
                'none','available',0,0,0,0,0,'2026-07-31T00:00:00.0000000+00:00');
            INSERT INTO source_trace_version_observations
            VALUES(-1,'{TraceId}','missing',NULL);
            INSERT INTO source_trace_version_interpretation_supersessions(
                supersession_id,source_observation_id,trace_id,
                previous_interpretation_revision,new_interpretation_revision,
                derived_state,exact_version,reason,raw_record_id,input_evidence_kind,
                raw_payload_sha256,resolver_revision,registry_revision,projector_version,
                created_at,operation_fingerprint)
            VALUES(
                -2,-1,'{TraceId}',0,1,'missing',NULL,'decoder_revision',1,
                'payload_sha256','{Sha256("[]")}','resolver-2','registry-1',
                'skill-projector-1','2026-07-31T00:01:00.0000000+00:00',
                '{new string('a', 64)}');
            INSERT INTO source_trace_version_interpretation_heads
            VALUES(-1,'{TraceId}',1,-2);
            INSERT INTO source_trace_compatibility_revisions
            VALUES('{TraceId}',1,'missing',NULL,'2026-07-31T00:01:00.0000000+00:00');
            INSERT INTO source_compatibility_reconciliation_receipts(
                operation_key,request_fingerprint,source_observation_id,trace_id,
                expected_interpretation_revision,raw_record_id,input_evidence_kind,
                raw_payload_sha256,resolver_revision,registry_revision,projector_version,
                outcome,resulting_supersession_id,resulting_interpretation_revision,
                resulting_compatibility_revision,resulting_generation_id,created_at)
            VALUES(
                'negative-source-graph-operation','{new string('b', 64)}',-1,
                '{TraceId}',0,1,'payload_sha256','{Sha256("[]")}',
                'resolver-2','registry-1','skill-projector-1','changed',-2,1,1,NULL,
                '2026-07-31T00:01:00.0000000+00:00');
            PRAGMA ignore_check_constraints=OFF;
            """);

        var error = Assert.Throws<InvalidOperationException>(
            () => SourceCompatibilitySchemaV11.Validate(connection, transaction: null));

        Assert.Equal("source_compatibility_identity_invalid", error.Message);
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
        Action<SourceCompatibilityReconciliationCheckpoint>? checkpoint = null,
        Action<Func<RawTelemetryRecord>>? lastRawAccessObserverForTesting = null) =>
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
            checkpoint,
            lastRawAccessObserverForTesting);

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

    private static void ApplyPostAdmissionRetentionMutation(
        SqliteConnection connection,
        long rawRecordId,
        string mutation)
    {
        using var command = connection.CreateCommand();
        command.CommandText = mutation switch
        {
            "pin" =>
                "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='raw_record' AND source_item_id=$raw_record_id;",
            "unpin" =>
                "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='raw_record' AND source_item_id=$raw_record_id; " +
                "UPDATE retention_items SET state='expiring',revision=revision+1 WHERE store_kind='raw_record' AND source_item_id=$raw_record_id;",
            "cleanup_read_denied" =>
                "UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$at,revision=revision+1 WHERE store_kind='raw_record' AND source_item_id=$raw_record_id; " +
                "UPDATE retention_items SET state='deletion_queued',queued_at=$at,revision=revision+1 WHERE store_kind='raw_record' AND source_item_id=$raw_record_id;",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        command.Parameters.AddWithValue(
            "$raw_record_id",
            rawRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$at", ObservedAt.AddMinutes(1).ToString("O"));
        Assert.Equal(mutation == "pin" ? 1 : 2, command.ExecuteNonQuery());
    }

    private static void InsertUnrelatedAccessLease(
        SqliteConnection connection,
        long rawRecordId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
            SELECT item_id,'access','source-compatibility-access',$expires_at,41
            FROM retention_items
            WHERE store_kind='raw_record' AND source_item_id=$raw_record_id;
            """;
        command.Parameters.AddWithValue(
            "$raw_record_id",
            rawRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$expires_at", ObservedAt.AddMinutes(4).ToString("O"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void InsertUnrelatedOperationLease(
        SqliteConnection connection,
        long selectedRawRecordId,
        long unrelatedRawRecordId)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
            SELECT unrelated.item_id,'operation',selected_lease.owner,
                   selected_lease.expires_at,selected_lease.generation
            FROM retention_items AS selected
            JOIN retention_leases AS selected_lease
              ON selected_lease.item_id=selected.item_id
             AND selected_lease.lease_kind='operation'
            JOIN retention_items AS unrelated
              ON unrelated.store_kind='raw_record'
             AND unrelated.source_item_id=$unrelated_raw_record_id
            WHERE selected.store_kind='raw_record'
              AND selected.source_item_id=$selected_raw_record_id;
            """;
        command.Parameters.AddWithValue(
            "$selected_raw_record_id",
            selectedRawRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$unrelated_raw_record_id",
            unrelatedRawRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void ReplaceOperationLeaseTuple(
        SqliteConnection connection,
        long rawRecordId,
        string tupleMember)
    {
        using var command = connection.CreateCommand();
        var replacement = tupleMember switch
        {
            "owner" => "owner='replacement-source-compatibility-operation'",
            "generation" => "generation=generation+1",
            _ => throw new ArgumentOutOfRangeException(nameof(tupleMember)),
        };
        command.CommandText =
            $"""
            UPDATE retention_leases
            SET {replacement}
            WHERE item_id=(
                SELECT item_id
                FROM retention_items
                WHERE store_kind='raw_record' AND source_item_id=$raw_record_id)
              AND lease_kind='operation';
            """;
        command.Parameters.AddWithValue(
            "$raw_record_id",
            rawRecordId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static string SnapshotRawLease(
        SqliteConnection connection,
        long rawRecordId,
        string leaseKind) =>
        ScalarText(
            connection,
            $"""
            SELECT quote(l.item_id) || '|' || quote(l.lease_kind) || '|' || quote(l.owner) || '|' || quote(l.expires_at) || '|' || quote(l.generation)
            FROM retention_leases AS l
            JOIN retention_items AS i ON i.item_id=l.item_id
            WHERE i.store_kind='raw_record' AND i.source_item_id='{rawRecordId}' AND l.lease_kind='{leaseKind}';
            """);

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

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string SnapshotTable(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\" ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var snapshot = new StringBuilder();
        while (reader.Read())
        {
            snapshot.Append('[');
            for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
            {
                switch (reader.GetValue(ordinal))
                {
                    case DBNull:
                        snapshot.Append("null;");
                        break;
                    case long integer:
                        snapshot.Append('i').Append(integer).Append(';');
                        break;
                    case double number:
                        snapshot.Append('d').Append(
                            number.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(';');
                        break;
                    case string text:
                        snapshot.Append('s').Append(
                            Convert.ToBase64String(Encoding.UTF8.GetBytes(text))).Append(';');
                        break;
                    case byte[] bytes:
                        snapshot.Append('b').Append(Convert.ToBase64String(bytes)).Append(';');
                        break;
                    default:
                        throw new InvalidOperationException("unsupported_sqlite_snapshot_value");
                }
            }
            snapshot.Append(']');
        }
        return snapshot.ToString();
    }

    private static string SnapshotTable(string path, string table)
    {
        using var connection = Open(path);
        return SnapshotTable(connection, table);
    }

    private static void SetRawPayloadToNullWithoutChangingStoredSchema(
        string path,
        long rawRecordId)
    {
        RewriteRawPayloadNullability(path, nullable: true);
        using (var connection = Open(path))
            Execute(connection, $"UPDATE raw_records SET payload_json=NULL WHERE id={rawRecordId};");
        RewriteRawPayloadNullability(path, nullable: false);
    }

    private static void RewriteRawPayloadNullability(string path, bool nullable)
    {
        using var connection = Open(path);
        var schemaVersion = ScalarLong(connection, "PRAGMA schema_version;");
        var from = nullable ? "payload_json TEXT NOT NULL" : "payload_json TEXT";
        var to = nullable ? "payload_json TEXT" : "payload_json TEXT NOT NULL";
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            PRAGMA writable_schema=ON;
            UPDATE sqlite_schema
            SET sql=replace(sql,$from,$to)
            WHERE type='table' AND name='raw_records';
            PRAGMA schema_version={schemaVersion + 1};
            PRAGMA writable_schema=OFF;
            """;
        command.Parameters.AddWithValue("$from", from);
        command.Parameters.AddWithValue("$to", to);
        command.ExecuteNonQuery();
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

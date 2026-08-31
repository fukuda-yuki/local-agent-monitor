using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Telemetry.Repositories;
using CopilotAgentObservability.LocalMonitor.Tests.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class RuntimeBackupRestoreTests
{
    [Fact]
    public void LocalWorkspaceProjection_AllOwnedTablesRoundTripThroughProductionRestore()
    {
        using var temp = new RestoreTemp();
        using var fixture = new LocalRepositoryCatalogFixture();
        var source = fixture.DatabasePath;
        fixture.CreateSession(LocalRepositoryCatalogFixture.SessionId(3_702));
        using (var connection = temp.Open(source))
        {
            using (var install = connection.BeginTransaction())
            {
                LocalArchiveSchemaV1.Ensure(connection, install);
                SkillProjectionSchemaV1.Ensure(connection, install);
                SkillInvocationSnapshotSchemaV1.Ensure(connection, install);
                install.Commit();
            }
            using (var run = connection.CreateCommand())
            {
                run.CommandText = "INSERT INTO session_runs VALUES($run,$session,'copilot-sdk',NULL,NULL,NULL,'gpt-5',NULL,NULL,10,3,13,'completed');";
                run.Parameters.AddWithValue("$run", Guid.CreateVersion7(DateTimeOffset.Parse(LocalRepositoryCatalogFixture.At)).ToString("D"));
                run.Parameters.AddWithValue("$session", LocalRepositoryCatalogFixture.SessionId(3_702));
                run.ExecuteNonQuery();
            }
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse(LocalRepositoryCatalogFixture.At));
        }
        var raw = new RawTelemetryRecord(null, RawTelemetrySources.RawOtlp, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            DateTimeOffset.Parse(LocalRepositoryCatalogFixture.At), null,
            "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"spanId\":\"bbbbbbbbbbbbbbbb\",\"name\":\"synthetic\"},{\"traceId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"spanId\":\"cccccccccccccccc\",\"name\":\"synthetic-child\"}]}]}]}");
        var rawId = fixture.RawStore.Insert(raw);
        fixture.RawStore.ApplyProjection(rawId, raw.Source, raw.ReceivedAt, MonitorProjectionBuilder.Build(raw), raw.ReceivedAt);
        fixture.RawStore.ApplySpanProjection(rawId, MonitorSpanProjectionBuilder.Build(raw), raw.ReceivedAt);
        using (var connection = temp.Open(source))
        using (var transaction = connection.BeginTransaction(deferred: true))
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction);
        foreach (var mutation in new[]
        {
            $"UPDATE local_workspace_span_facts SET retry_count=1 WHERE raw_record_id={rawId};",
            $"UPDATE local_workspace_span_facts SET producer_total_tokens=1 WHERE raw_record_id={rawId};",
            $"DELETE FROM monitor_spans WHERE raw_record_id={rawId};",
            $"DELETE FROM local_workspace_span_facts WHERE raw_record_id={rawId} AND span_ordinal=0;",
            $"DELETE FROM local_workspace_span_facts WHERE raw_record_id={rawId};",
        })
        {
            using var connection = temp.Open(source);
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = mutation;
            command.ExecuteNonQuery();
            LocalWorkspaceProjectionStore.RefreshStructural(connection, transaction, DateTimeOffset.Parse(LocalRepositoryCatalogFixture.At));
            Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
                LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
            transaction.Rollback();
        }
        using (var readOnly = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = source, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        {
            readOnly.Open();
            using var transaction = readOnly.BeginTransaction(deferred: true);
            LocalWorkspaceProjectionBackupValidation.Validate(readOnly, transaction);
        }
        var bundle = Path.Combine(temp.Root, "projection-roundtrip.zip");
        var checkpoints = new List<string>();
        var service = new SqliteRuntimeBackupService(new FixedTimeProvider(DateTimeOffset.Parse(LocalRepositoryCatalogFixture.At)), checkpoints.Add);
        var backup = service.CreateAndPublish(source, bundle);
        Assert.True(backup.Success, backup.ErrorCode + ":" + string.Join(',', checkpoints));
        var expected = temp.SnapshotOwnedRows(source, "local_workspace_");
        var restored = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(restored.Success, restored.ErrorCode + ":" + string.Join(',', checkpoints));
        Assert.Equal(expected, temp.SnapshotOwnedRows(temp.Target, "local_workspace_"));
        using var verification = temp.Open(temp.Target);
        Assert.Equal(18, LocalWorkspaceProjectionSchemaV1.TableNames.Length);
        Assert.All(LocalWorkspaceProjectionSchemaV1.TableNames, table => Assert.Equal(1L,
            temp.Scalar<long>(verification, $"SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='{table}';")));
        Assert.Equal(5L, temp.Scalar<long>(verification, "SELECT version FROM schema_version WHERE component='local_workspace_projection';"));
        using var validation = verification.BeginTransaction(deferred: true);
        LocalWorkspaceProjectionBackupValidation.Validate(
            verification,
            validation,
            skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                SkillInvocationV2ArtifactRegistry.CurrentWriterVersion));
    }

    [Fact]
    public void Current_deleted_before_digest_authority_round_trips_exactly()
    {
        using var temp = new RestoreTemp();
        temp.CreateCurrentMarkerDatabase(temp.Source);
        var bundle = Path.Combine(temp.Root, "marker-round-trip.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);

        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        var restored = service.Restore(
            bundle,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.True(restored.Success, restored.ErrorCode);
        using var connection = temp.Open(temp.Target);
        Assert.Equal("deleted_before_digest_v10", temp.Scalar<string>(
            connection,
            "SELECT input_evidence_kind FROM source_schema_observations;"));
        Assert.Equal(1L, temp.Scalar<long>(
            connection,
            "SELECT raw_payload_sha256 IS NULL FROM source_schema_observations;"));
        Assert.Equal("input_unavailable", temp.Scalar<string>(
            connection,
            "SELECT lifecycle FROM skill_projection_generations;"));
        Assert.Equal("input_unavailable", temp.Scalar<string>(
            connection,
            "SELECT outcome FROM source_compatibility_reconciliation_receipts;"));
        Assert.Equal(
            SkillProjectionHashing.FrontierDigest(
                RestoreTemp.MarkerTraceId,
                [new SkillProjectionFrontierInput(
                    1,
                    1,
                    SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10,
                    null)]),
            temp.Scalar<string>(
                connection,
                "SELECT input_frontier_sha256 FROM skill_projection_generations;"));
        SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
        SkillProjectionSchemaV1.Validate(connection, transaction: null);
    }

    [Theory]
    [InlineData("receipt-fingerprint")]
    [InlineData("marker-with-digest")]
    [InlineData("marker-with-raw")]
    [InlineData("frontier-mismatch")]
    [InlineData("non-terminal")]
    [InlineData("projected-row")]
    [InlineData("current-pointer")]
    [InlineData("pointerless-head-without-source-revision")]
    [InlineData("pointerless-head-with-source-revision")]
    public void Marker_contradiction_is_restore_incompatible_without_target_mutation(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateCurrentMarkerDatabase(temp.Source);
        var valid = Path.Combine(temp.Root, "marker-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);
        var malicious = temp.CreateSkillProjectionContradictionArchive(valid, contradiction);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = temp.CanonicalDatabaseHash(temp.Target);

        var inspection = service.Inspect(malicious);
        var preview = service.Preview(malicious, temp.Target);
        var restore = service.Restore(
            malicious,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, temp.CanonicalDatabaseHash(temp.Target));
    }

    [Fact]
    public void Resolved_pointerless_superseded_head_is_restore_incompatible_without_target_mutation()
    {
        using var temp = new RestoreTemp();
        temp.CreateResolvedSkillProjectionDatabase(temp.Source);
        var valid = Path.Combine(temp.Root, "resolved-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);
        var malicious = temp.CreateSkillProjectionContradictionArchive(
            valid,
            "resolved-pointerless-superseded");
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = temp.CanonicalDatabaseHash(temp.Target);

        var inspection = service.Inspect(malicious);
        var preview = service.Preview(malicious, temp.Target);
        var restore = service.Restore(
            malicious,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, temp.CanonicalDatabaseHash(temp.Target));
    }

    [Theory]
    [InlineData("old-worker-input-unavailable")]
    [InlineData("worker-input-unavailable-projected-rows")]
    public void Payload_input_unavailable_contradiction_is_restore_incompatible_without_target_mutation(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreatePayloadInputUnavailableDatabase(
            temp.Source,
            withSuccessor: contradiction == "old-worker-input-unavailable");
        var valid = Path.Combine(temp.Root, "payload-input-unavailable-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);
        var malicious = temp.CreateSkillProjectionContradictionArchive(valid, contradiction);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = temp.CanonicalDatabaseHash(temp.Target);

        var inspection = service.Inspect(malicious);
        var preview = service.Preview(malicious, temp.Target);
        var restore = service.Restore(
            malicious,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, temp.CanonicalDatabaseHash(temp.Target));
    }

    [Theory]
    [InlineData("older-same-revision-current")]
    [InlineData("recomputed-desired-subset")]
    [InlineData("marker-omitted-current-projection")]
    public void Desired_frontier_contradiction_is_rejected_by_schema(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateCompleteDesiredFrontierDatabase(
            temp.Source,
            marker: contradiction == "marker-omitted-current-projection");

        temp.ApplySkillProjectionContradiction(temp.Source, contradiction);
        using var connection = temp.Open(temp.Source);
        SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
        Assert.Throws<InvalidOperationException>(
            () => SkillProjectionSchemaV1.Validate(connection, transaction: null));
    }

    [Theory]
    [InlineData("older-same-revision-current")]
    [InlineData("recomputed-desired-subset")]
    [InlineData("marker-omitted-current-projection")]
    public void Desired_frontier_contradiction_is_restore_incompatible_without_target_mutation(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateCompleteDesiredFrontierDatabase(
            temp.Source,
            marker: contradiction == "marker-omitted-current-projection");
        var valid = Path.Combine(temp.Root, $"frontier-{contradiction}-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);

        var malicious = temp.CreateSkillProjectionStateContradictionArchive(
            valid,
            contradiction);
        AssertRestoreIncompatibleWithoutTargetMutation(temp, service, malicious);
    }

    [Theory]
    [InlineData("pending-projected-rows")]
    [InlineData("superseded-unpublished-projected-rows")]
    public void Unpublished_projection_rows_are_rejected_by_schema(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateCompleteDesiredFrontierDatabase(
            temp.Source,
            marker: false,
            observationCount: contradiction == "pending-projected-rows" ? 1 : 2);

        temp.ApplySkillProjectionContradiction(temp.Source, contradiction);
        using var connection = temp.Open(temp.Source);
        SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
        Assert.Throws<InvalidOperationException>(
            () => SkillProjectionSchemaV1.Validate(connection, transaction: null));
    }

    [Theory]
    [InlineData("pending-projected-rows")]
    [InlineData("superseded-unpublished-projected-rows")]
    public void Unpublished_projection_rows_are_restore_incompatible_without_target_mutation(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateCompleteDesiredFrontierDatabase(
            temp.Source,
            marker: false,
            observationCount: contradiction == "pending-projected-rows" ? 1 : 2);
        var valid = Path.Combine(temp.Root, $"projection-state-{contradiction}-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);

        var malicious = temp.CreateSkillProjectionStateContradictionArchive(
            valid,
            contradiction);
        AssertRestoreIncompatibleWithoutTargetMutation(temp, service, malicious);
    }

    [Theory]
    [InlineData("unequal-queue-counters")]
    [InlineData("completed-zero-counters")]
    [InlineData("retry-pending-without-retry-fields")]
    public void Unreachable_queue_state_is_rejected_by_schema(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateCompleteDesiredFrontierDatabase(
            temp.Source,
            marker: false,
            observationCount: 1);

        temp.ApplySkillProjectionContradiction(temp.Source, contradiction);
        using var connection = temp.Open(temp.Source);
        SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
        Assert.Throws<InvalidOperationException>(
            () => SkillProjectionSchemaV1.Validate(connection, transaction: null));
    }

    [Theory]
    [InlineData("unequal-queue-counters")]
    [InlineData("completed-zero-counters")]
    [InlineData("retry-pending-without-retry-fields")]
    public void Unreachable_queue_state_is_restore_incompatible_without_target_mutation(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateCompleteDesiredFrontierDatabase(
            temp.Source,
            marker: false,
            observationCount: 1);
        var valid = Path.Combine(temp.Root, $"queue-state-{contradiction}-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);

        var malicious = temp.CreateSkillProjectionStateContradictionArchive(
            valid,
            contradiction);
        AssertRestoreIncompatibleWithoutTargetMutation(temp, service, malicious);
    }

    [Fact]
    public void Marker_registry_supersession_relabelled_as_decoder_is_rejected_by_source_schema()
    {
        using var temp = new RestoreTemp();
        temp.CreateMarkerRegistrySupersessionDatabase(temp.Source);

        temp.RelabelMarkerRegistrySupersessionAsDecoder(temp.Source);
        using var connection = temp.Open(temp.Source);
        Assert.Throws<InvalidOperationException>(
            () => SourceCompatibilitySchemaV11.Validate(connection, transaction: null));
    }

    [Fact]
    public void Marker_registry_supersession_relabelled_as_decoder_is_restore_incompatible_without_target_mutation()
    {
        using var temp = new RestoreTemp();
        temp.CreateMarkerRegistrySupersessionDatabase(temp.Source);
        var valid = Path.Combine(temp.Root, "marker-registry-supersession-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);

        var malicious = temp.CreateMarkerSupersessionContradictionArchive(valid);
        AssertRestoreIncompatibleWithoutTargetMutation(temp, service, malicious);
    }

    [Fact]
    public void Published_superseded_projection_remains_schema_valid_and_round_trips()
    {
        using var temp = new RestoreTemp();
        temp.CreatePublishedSupersededSkillProjectionDatabase(temp.Source);
        var archive = Path.Combine(temp.Root, "published-superseded-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);

        Assert.True(service.CreateAndPublish(temp.Source, archive).Success);
        var restore = service.Restore(
            archive,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.True(restore.Success, restore.ErrorCode);
        using var connection = temp.Open(temp.Target);
        Assert.Equal("superseded", temp.Scalar<string>(
            connection,
            "SELECT lifecycle FROM skill_projection_generations WHERE generation_id=1;"));
        Assert.Equal("completed", temp.Scalar<string>(
            connection,
            "SELECT state FROM skill_projection_queue WHERE generation_id=1;"));
        Assert.Equal(1L, temp.Scalar<long>(
            connection,
            "SELECT COUNT(*) FROM skill_projection_invocations WHERE generation_id=1;"));
        SourceCompatibilitySchemaV11.Validate(connection, transaction: null);
        SkillProjectionSchemaV1.Validate(connection, transaction: null);
    }

    [Theory]
    [InlineData("unsanitized-otel-skill-value")]
    [InlineData("coherent-negative-generated-identities")]
    public void Persisted_skill_boundary_contradiction_is_restore_incompatible_without_target_mutation(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreatePublishedSupersededSkillProjectionDatabase(temp.Source);
        var valid = Path.Combine(temp.Root, $"skill-boundary-{contradiction}-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, valid).Success);

        var malicious = temp.CreateSkillProjectionStateContradictionArchive(
            valid,
            contradiction);
        temp.AssertArchiveDatabaseChecksumMatchesManifest(malicious);
        AssertRestoreIncompatibleWithoutTargetMutation(temp, service, malicious);
    }

    private static void AssertRestoreIncompatibleWithoutTargetMutation(
        RestoreTemp temp,
        SqliteRuntimeBackupService service,
        string malicious)
    {
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = temp.CanonicalDatabaseHash(temp.Target);

        var inspection = service.Inspect(malicious);
        var preview = service.Preview(malicious, temp.Target);
        var restore = service.Restore(
            malicious,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, inspection.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, temp.CanonicalDatabaseHash(temp.Target));
    }

    [Fact]
    public void Runtime_backup_schema_can_be_added_to_the_current_database()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "schema-source", includeRaw: true);
        using var connection = temp.Open(temp.Source);
        using var transaction = connection.BeginTransaction(deferred: false);

        var exception = Record.Exception(() => RuntimeBackupSchemaV1.Ensure(connection, transaction));

        Assert.Null(exception);
    }

    [Fact]
    public void Monitor_startup_preparation_holds_the_lease_without_running_owner_migrations()
    {
        using var temp = new RestoreTemp();
        using (var database = temp.Open(temp.Target))
        {
            temp.Execute(database, "CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var service = new SqliteRuntimeBackupService(temp.Clock);

        var initialization = service.InitializeForMonitor(temp.Target);

        Assert.True(initialization.Result.Success, initialization.Result.ErrorCode);
        Assert.NotNull(initialization.Lease);
        using var lease = initialization.Lease!;
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        using (var database = temp.Open(temp.Target))
        {
            Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM schema_version WHERE component='runtime_backup';"));
            Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM schema_version WHERE component IN ('session','local_repository_catalog');"));
            Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM sqlite_schema WHERE name='local_repositories' OR name LIKE 'local_repository_%' OR name LIKE 'session_repository_%';"));
        }
        var competing = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);
        Assert.False(competing.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.MonitorMustBeStopped, competing.Result.ErrorCode);
        Assert.Null(competing.Lease);
        using (var database = temp.Open(temp.Target))
        using (var transaction = database.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(database, transaction);
            transaction.Commit();
        }
        new SqliteSessionStore(temp.Target).CreateSchema();

        var completed = service.CompleteMonitorInitialization(lease);

        Assert.True(completed.Success, completed.ErrorCode);
        using (var database = temp.Open(temp.Target))
        {
            Assert.Equal(1L, temp.Scalar<long>(database, "SELECT version FROM schema_version WHERE component='runtime_backup';"));
            Assert.Equal(1L, temp.Scalar<long>(database, "SELECT version FROM schema_version WHERE component='local_repository_catalog';"));
            foreach (var table in LocalRepositoryCatalogSchemaV1.TableNames)
                Assert.Equal(table == "local_repository_reconciliation_state" ? 1L : 0L, temp.Scalar<long>(database, $"SELECT COUNT(*) FROM \"{table}\";"));
            Assert.Equal(1L, temp.Scalar<long>(database, """
                SELECT COUNT(*) FROM local_repository_reconciliation_state
                WHERE projector_key='local-repository-catalog-v1'
                  AND last_discovered_span_id IS NULL
                  AND updated_at='1970-01-01T00:00:00.0000000+00:00';
                """));
        }
    }

    [Fact]
    public void Monitor_startup_removes_unlocked_empty_read_sidecars_before_recovery_guard()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "existing", includeRaw: false);
        File.WriteAllBytes(temp.Target + "-wal", []);
        File.WriteAllBytes(temp.Target + "-shm", new byte[32 * 1024]);

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);

        Assert.True(initialization.Result.Success, initialization.Result.ErrorCode);
        using var lease = Assert.IsType<RuntimeBackupMonitorLease>(initialization.Lease);
        Assert.False(File.Exists(temp.Target + "-wal"));
        Assert.False(File.Exists(temp.Target + "-shm"));
    }

    [Fact]
    public void Monitor_startup_preserves_empty_read_sidecars_when_another_monitor_is_live()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "existing", includeRaw: false);
        var wal = temp.Target + "-wal";
        var sharedMemory = temp.Target + "-shm";
        File.WriteAllBytes(wal, []);
        File.WriteAllBytes(sharedMemory, new byte[32 * 1024]);
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(temp.Target)!, "local-monitor.state.json"),
            JsonSerializer.Serialize(new { process_id = Environment.ProcessId }));

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.True(File.Exists(wal));
        Assert.True(File.Exists(sharedMemory));
    }

    [Fact]
    public void Monitor_startup_preserves_nonempty_wal_and_shared_memory()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "existing", includeRaw: false);
        var wal = temp.Target + "-wal";
        var sharedMemory = temp.Target + "-shm";
        File.WriteAllBytes(wal, [0x01]);
        File.WriteAllBytes(sharedMemory, new byte[32 * 1024]);

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.Equal(new byte[] { 0x01 }, File.ReadAllBytes(wal));
        Assert.True(File.Exists(sharedMemory));
    }

    [Theory]
    [InlineData("wal_only")]
    [InlineData("shm_only")]
    [InlineData("malformed_shm")]
    public void Monitor_startup_preserves_incomplete_or_malformed_read_sidecars(string kind)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "existing", includeRaw: false);
        var wal = temp.Target + "-wal";
        var sharedMemory = temp.Target + "-shm";
        if (kind is "wal_only" or "malformed_shm") File.WriteAllBytes(wal, []);
        if (kind is "shm_only") File.WriteAllBytes(sharedMemory, new byte[32 * 1024]);
        if (kind is "malformed_shm") File.WriteAllBytes(sharedMemory, [0x01]);

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.Equal(kind is "wal_only" or "malformed_shm", File.Exists(wal));
        Assert.Equal(kind is "shm_only" or "malformed_shm", File.Exists(sharedMemory));
    }

    [Theory]
    [InlineData("wal")]
    [InlineData("shm")]
    public void Monitor_startup_preserves_empty_read_sidecars_when_a_cleanup_handle_competes(string lockedSidecar)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "existing", includeRaw: false);
        var wal = temp.Target + "-wal";
        var sharedMemory = temp.Target + "-shm";
        File.WriteAllBytes(wal, []);
        File.WriteAllBytes(sharedMemory, new byte[32 * 1024]);
        using var competingHandle = new FileStream(
            lockedSidecar == "wal" ? wal : sharedMemory,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.True(File.Exists(wal));
        Assert.True(File.Exists(sharedMemory));
    }

    [Fact]
    public void Monitor_startup_preserves_paired_reparse_read_sidecars()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "existing", includeRaw: false);
        var wal = temp.Target + "-wal";
        var sharedMemory = temp.Target + "-shm";
        var walTarget = Path.Combine(temp.Root, "wal-target");
        var sharedMemoryTarget = Path.Combine(temp.Root, "shm-target");
        File.WriteAllBytes(walTarget, []);
        File.WriteAllBytes(sharedMemoryTarget, new byte[32 * 1024]);
        try
        {
            File.CreateSymbolicLink(wal, walTarget);
            File.CreateSymbolicLink(sharedMemory, sharedMemoryTarget);
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or UnauthorizedAccessException or IOException)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Cannot create paired reparse fixture: {exception.GetType().Name}");
        }

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.NotNull(new FileInfo(wal).LinkTarget);
        Assert.NotNull(new FileInfo(sharedMemory).LinkTarget);
    }

    [Fact]
    public void Monitor_startup_defers_exact_workspace_v2_migration_until_completion()
    {
        using var temp = new RestoreTemp();
        using (var database = temp.Open(temp.Source))
        using (var transaction = database.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(database, transaction);
            transaction.Commit();
        }
        new SqliteSessionStore(temp.Source).CreateSchema();
        using (var database = temp.Open(temp.Source))
        {
            using (var retention = database.BeginTransaction())
            {
                RetentionSchemaMigrator.Apply(database, retention);
                retention.Commit();
            }
            LocalWorkspaceProjectionSchemaV1.Ensure(database, temp.Clock.GetUtcNow());
            temp.Execute(database, "DELETE FROM schema_version WHERE component='local_workspace_projection'; DROP TABLE local_workspace_subagent_lifecycle; DROP TABLE local_workspace_skill_metadata; DROP TABLE local_workspace_tool_metadata; DROP TABLE local_workspace_node_source_references; DROP TABLE local_workspace_semantic_receipts; DROP TABLE local_workspace_node_content_refs; DROP TABLE local_workspace_content_tombstones; DROP TABLE local_workspace_node_edges; DROP TABLE local_workspace_nodes; DROP TABLE local_workspace_execution_headers; DROP TABLE local_workspace_token_observations; DROP TABLE local_workspace_span_facts; DROP TABLE local_workspace_session_activity; DROP TABLE local_workspace_session_models; DROP TABLE local_workspace_session_sources; DROP TABLE local_workspace_session_search_facts; DROP TABLE local_workspace_projection_state; DROP TABLE local_workspace_sessions;");
            foreach (var sql in LocalWorkspaceProjectionSchemaV1.ExactV2SchemaSql) temp.Execute(database, sql);
            temp.Execute(database, "INSERT INTO schema_version(component,version) VALUES('local_workspace_projection',2);");
        }
        var service = new SqliteRuntimeBackupService(temp.Clock);

        var initialization = service.InitializeForMonitor(temp.Source);

        Assert.True(initialization.Result.Success, initialization.Result.ErrorCode);
        using var lease = Assert.IsType<RuntimeBackupMonitorLease>(initialization.Lease);
        using (var database = temp.Open(temp.Source))
        {
            Assert.Equal(2L, temp.Scalar<long>(database, "SELECT version FROM schema_version WHERE component='local_workspace_projection';"));
            Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='local_workspace_session_search_facts';"));
        }

        var completed = service.CompleteMonitorInitialization(lease);

        Assert.True(completed.Success, completed.ErrorCode);
        using var verification = temp.Open(temp.Source);
        Assert.Equal(5L, temp.Scalar<long>(verification, "SELECT version FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.Equal(1L, temp.Scalar<long>(verification, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='local_workspace_session_search_facts';"));
    }

    [Theory]
    [InlineData("missing_stamp")]
    [InlineData("future_version")]
    [InlineData("v1_drift")]
    [InlineData("v2_drift")]
    public void Monitor_startup_preparation_rejects_invalid_workspace_ownership_without_mutation(string kind)
    {
        using var temp = new RestoreTemp();
        using (var database = temp.Open(temp.Source))
        using (var transaction = database.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(database, transaction);
            transaction.Commit();
        }
        new SqliteSessionStore(temp.Source).CreateSchema();
        using (var database = temp.Open(temp.Source))
        {
            LocalWorkspaceProjectionSchemaV1.Ensure(database, temp.Clock.GetUtcNow());
            temp.Execute(database, kind switch
            {
                "missing_stamp" => "DELETE FROM schema_version WHERE component='local_workspace_projection';",
                "future_version" => "UPDATE schema_version SET version=6 WHERE component='local_workspace_projection';",
                "v1_drift" => "DROP TABLE local_workspace_span_facts; UPDATE schema_version SET version=1 WHERE component='local_workspace_projection'; ALTER TABLE local_workspace_sessions ADD COLUMN drift TEXT;",
                "v2_drift" => "ALTER TABLE local_workspace_sessions ADD COLUMN drift TEXT;",
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            });
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Source);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Fact]
    public void Monitor_startup_preparation_allows_workspace_component_to_be_absent()
    {
        using var temp = new RestoreTemp();
        using (var database = temp.Open(temp.Source))
        using (var transaction = database.BeginTransaction())
        {
            MonitorSchemaMigrator.ApplyBaseSchema(database, transaction);
            transaction.Commit();
        }
        new SqliteSessionStore(temp.Source).CreateSchema();
        using (var database = temp.Open(temp.Source))
            temp.Execute(database, "CREATE TABLE localXworkspaceYextension(value INTEGER); CREATE INDEX localXworkspaceYextension_index ON localXworkspaceYextension(value);");
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Source);

        Assert.True(initialization.Result.Success, initialization.Result.ErrorCode);
        using var lease = Assert.IsType<RuntimeBackupMonitorLease>(initialization.Lease);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Theory]
    [InlineData("CREATE TABLE retention_component_versions(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO retention_component_versions(component,version) VALUES('retention',2);")]
    [InlineData("CREATE TABLE retention_component_versions(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO retention_component_versions(component,version) VALUES('forged_component',1);")]
    [InlineData("CREATE TABLE retention_component_versions(component TEXT PRIMARY KEY,version TEXT NOT NULL); INSERT INTO retention_component_versions(component,version) VALUES('retention','1');")]
    public void Monitor_startup_rejects_invalid_retention_only_vectors_without_mutating_the_database(string schema)
    {
        using var temp = new RestoreTemp();
        using (var database = temp.Open(temp.Target))
            temp.Execute(database, schema);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var initialization = new SqliteRuntimeBackupService(temp.Clock).InitializeForMonitor(temp.Target);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Restore_staging_migrates_version13_pinned_content_and_preserves_retention_and_skill_descendants()
    {
        using var temp = new RestoreTemp();
        var fixture = temp.CreatePinnedSessionVersion13Database(temp.Source, "restore-staging-v13");
        var expectedDescendants = temp.SnapshotOwnedRows(
            temp.Source,
            "session_event_content",
            "retention_",
            "skill_projection_");
        using (var source = temp.Open(temp.Source))
            temp.AssertSessionBoundSkillDescendants(source, fixture.SessionId);
        new SqliteSessionStore(temp.Source, temp.Clock).CreateSchema();
        var service = new SqliteRuntimeBackupService(temp.Clock);
        var currentBundle = Path.Combine(temp.Root, "session-v14-source.zip");
        Assert.True(service.CreateAndPublish(temp.Source, currentBundle).Success);
        var versionThirteenBundle = temp.CreateVersion13SessionArchive(
            currentBundle,
            "session-v13-restore.zip");

        var inspection = service.Inspect(versionThirteenBundle);
        var restored = service.Restore(
            versionThirteenBundle,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.True(inspection.Success, inspection.ErrorCode);
        Assert.Equal(13, inspection.ComponentVersions!["session"]);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.False(restored.PreRestoreBackupCreated);
        using var database = temp.Open(temp.Target);
        Assert.Equal(14L, temp.Scalar<long>(database, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal("failed", temp.Scalar<string>(database, $"SELECT terminal_outcome FROM session_events WHERE event_id='{fixture.EventId}';"));
        Assert.Equal("retained_by_policy", temp.Scalar<string>(database, $"SELECT state FROM retention_items WHERE item_id='{fixture.ItemId}';"));
        temp.AssertSessionBoundSkillDescendants(database, fixture.SessionId);
        Assert.Equal(
            expectedDescendants,
            temp.SnapshotOwnedRows(database, "session_event_content", "retention_", "skill_projection_"));
        Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public void Post_gate_private_safety_copy_migrates_version13_without_mutating_the_live_target()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "incoming", includeRaw: false);
        var incomingBundle = Path.Combine(temp.Root, "session-v14-incoming.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, incomingBundle).Success);
        var fixture = temp.CreatePinnedSessionVersion13Database(temp.Target, "safety-copy-v13");
        var expectedDescendants = temp.SnapshotOwnedRows(
            temp.Target,
            "session_event_content",
            "retention_",
            "skill_projection_");
        using (var source = temp.Open(temp.Target))
            temp.AssertSessionBoundSkillDescendants(source, fixture.SessionId);
        SqliteConnection.ClearAllPools();
        var liveTargetBefore = CaptureDatabaseFiles(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.BeforeSwap)
                throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() =>
            crashing.Restore(incomingBundle, temp.Target, new RuntimeRestoreOptions()));

        SqliteConnection.ClearAllPools();
        AssertDatabaseFilesEqual(liveTargetBefore, CaptureDatabaseFiles(temp.Target));
        using (var liveTarget = temp.Open(temp.Target))
        {
            Assert.Equal(13L, temp.Scalar<long>(liveTarget, "SELECT version FROM schema_version WHERE component='session';"));
            Assert.Equal(0L, temp.Scalar<long>(liveTarget, "SELECT COUNT(*) FROM pragma_table_info('session_events') WHERE name='terminal_outcome';"));
        }

        var safetyDirectory = Path.Combine(temp.Root, "runtime-backups");
        var safetyBundle = Assert.Single(Directory.EnumerateFiles(safetyDirectory, "*.zip", SearchOption.TopDirectoryOnly));
        var service = new SqliteRuntimeBackupService(temp.Clock);
        var safetyInspection = service.Inspect(safetyBundle);
        Assert.True(safetyInspection.Success, safetyInspection.ErrorCode);
        Assert.Equal(14, safetyInspection.ComponentVersions!["session"]);
        var safetyRestoreRoot = Path.Combine(temp.Root, "safety-restore");
        Directory.CreateDirectory(safetyRestoreRoot);
        var safetyTarget = Path.Combine(safetyRestoreRoot, "safety-restored.db");
        var safetyRestore = service.Restore(safetyBundle, safetyTarget, new RuntimeRestoreOptions());
        Assert.True(safetyRestore.Success, safetyRestore.ErrorCode);
        using var safetyDatabase = temp.Open(safetyTarget);
        Assert.Equal("failed", temp.Scalar<string>(safetyDatabase, $"SELECT terminal_outcome FROM session_events WHERE event_id='{fixture.EventId}';"));
        temp.AssertSessionBoundSkillDescendants(safetyDatabase, fixture.SessionId);
        Assert.Equal(
            expectedDescendants,
            temp.SnapshotOwnedRows(safetyDatabase, "session_event_content", "retention_", "skill_projection_"));
    }

    [Fact]
    public void Restore_reconciles_current_terminal_tombstone_and_never_restores_raw_bytes()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        var created = service.CreateAndPublish(temp.Source, bundle);
        Assert.True(created.Success, created.ErrorCode);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);
        var preview = service.Preview(bundle, temp.Target);
        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(1, preview.TerminalReconciliationCount);
        Assert.False(preview.RequiresConfirmation);
        Assert.True(result.Success, result.ErrorCode);
        Assert.True(result.PreRestoreBackupCreated);
        Assert.Equal(64, result.PreRestoreBackupSha256?.Length);
        Assert.True(File.Exists(Path.Combine(temp.Root, "runtime-backups", result.PreRestoreBackupFileName!)));
        using var restored = temp.Open(temp.Target);
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal("deleted", temp.Scalar<string>(restored, "SELECT state FROM retention_items;"));
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_tombstones;"));
        Assert.Equal("2026-04-02T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT deleted_at FROM retention_items;"));
    }

    [Fact]
    public async Task Restore_reconciles_terminal_session_content_through_canonical_workspace_tombstone_refresh()
    {
        using var temp = new RestoreTemp();
        using var fixture = await SessionEventContentRetentionAdapterTests.Fixture.CreateAsync(refreshAfterQueue: true);
        var bundle = Path.Combine(temp.Root, "session-content-before-deletion.zip");
        SqliteConnection.ClearAllPools();
        File.Copy(fixture.Path, temp.Source);
        var service = new SqliteRuntimeBackupService(fixture.Time);
        var created = service.CreateAndPublish(temp.Source, bundle);
        Assert.True(created.Success, created.ErrorCode);

        Assert.Same(RetentionAdapterResult.Deleted, await fixture.Adapter.DeleteAsync(fixture.Context));
        Assert.Equal(1L, fixture.Count("SELECT COUNT(*) FROM local_workspace_content_tombstones WHERE source_item_id=$target;"));
        SqliteConnection.ClearAllPools();
        File.Copy(fixture.Path, temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var restored = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(1, preview.TerminalReconciliationCount);
        Assert.True(restored.Success, restored.ErrorCode);
        using var database = temp.Open(temp.Target);
        Assert.Equal(0L, temp.Scalar<long>(database, $"SELECT COUNT(*) FROM session_event_content WHERE event_id='{fixture.TargetEventId}';"));
        Assert.Equal("deleted", temp.Scalar<string>(database, $"SELECT state FROM retention_items WHERE item_id='{fixture.Context.ItemId}';"));
        Assert.Equal("deleted", temp.Scalar<string>(database, $"SELECT availability_state FROM local_workspace_node_content_refs WHERE source_item_id='{fixture.TargetEventId}';"));
        Assert.Equal(1L, temp.Scalar<long>(database, $"SELECT COUNT(*) FROM local_workspace_content_tombstones WHERE source_item_id='{fixture.TargetEventId}';"));
        using var connection = new SqliteConnection($"Data Source={temp.Target};Pooling=False");
        connection.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        LocalWorkspaceProjectionBackupValidation.Validate(
            connection,
            transaction,
            skillRegistryAuthority: FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                SkillInvocationV2ArtifactRegistry.CurrentWriterVersion));
    }

    [Fact]
    public void Non_terminal_missing_source_requires_archive_bound_confirmation_before_reintroduction()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawWithoutTombstone(temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var blocked = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());
        var wrong = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(AllowResurrection: true, ConfirmationDigest: new string('0', 64)));
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "UPDATE retention_items SET revision=revision+1;");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var changedPreview = service.Preview(bundle, temp.Target);
        Assert.True(changedPreview.Success, changedPreview.ErrorCode);
        var stale = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(AllowResurrection: true, ConfirmationDigest: preview.ConfirmationDigest));
        var restored = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(AllowResurrection: true, ConfirmationDigest: changedPreview.ConfirmationDigest));

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(preview.RequiresConfirmation);
        Assert.Equal(1, preview.NonTerminalReintroductionCount);
        Assert.NotNull(preview.NonTerminalReintroductionDigest);
        Assert.NotNull(preview.ConfirmationDigest);
        Assert.NotEqual(preview.NonTerminalReintroductionDigest, changedPreview.NonTerminalReintroductionDigest);
        Assert.NotEqual(preview.ConfirmationDigest, changedPreview.ConfirmationDigest);
        var previewJson = System.Text.Encoding.UTF8.GetString(RuntimeBackupJson.SerializeResult(preview));
        Assert.DoesNotContain(new string('a', 32), previewJson, StringComparison.Ordinal);
        Assert.DoesNotContain(temp.Target, previewJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreResurrectionBlocked, blocked.ErrorCode);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreResurrectionBlocked, wrong.ErrorCode);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreResurrectionBlocked, stale.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(1, restored.NonTerminalReintroductionCount);
        Assert.True(restored.PreRestoreBackupCreated);
        var safetyBundle = Path.Combine(
            temp.Root,
            "runtime-backups",
            Assert.IsType<string>(restored.PreRestoreBackupFileName));
        var safetyInspection = service.Inspect(safetyBundle);
        Assert.True(safetyInspection.Success, safetyInspection.ErrorCode);
        Assert.Equal(14, safetyInspection.ComponentVersions!["session"]);
        var safetyTarget = Path.Combine(temp.Root, "restored-safety.db");
        var safetyRestore = service.Restore(
            safetyBundle,
            safetyTarget,
            new RuntimeRestoreOptions());
        Assert.True(safetyRestore.Success, safetyRestore.ErrorCode);
        using var safetyDatabase = temp.Open(safetyTarget);
        Assert.Equal(0L, temp.Scalar<long>(safetyDatabase, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal("expiring", temp.Scalar<string>(safetyDatabase, "SELECT state FROM retention_items;"));
        using var database = temp.Open(temp.Target);
        Assert.Equal(1L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM raw_records;"));
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("extra-source")]
    [InlineData("malformed-row")]
    [InlineData("foreign-key")]
    public void Exact_current_archive_cannot_reclassify_hostile_retention_state_as_missing(
        string contradiction)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "hostile-current", includeRaw: true);
        var validBundle = Path.Combine(temp.Root, "hostile-current-valid.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, validBundle).Success);
        var hostileBundle = temp.CreateRetentionCoverageContradictionArchive(
            validBundle,
            contradiction);
        var target = Path.Combine(temp.Root, $"hostile-{contradiction}.db");

        var inspection = service.Inspect(hostileBundle);
        var restored = service.Restore(
            hostileBundle,
            target,
            new RuntimeRestoreOptions());

        Assert.False(inspection.Success);
        var expectedError = contradiction == "foreign-key"
            ? RuntimeBackupErrorCodes.ArchiveInvalid
            : RuntimeBackupErrorCodes.RestoreIncompatible;
        Assert.Equal(expectedError, inspection.ErrorCode);
        Assert.False(restored.Success);
        Assert.Equal(expectedError, restored.ErrorCode);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public void Failed_pre_swap_cleanup_escalates_the_domain_result_and_keeps_recoverable_controls()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "cleanup-failure.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawWithoutTombstone(temp.Target);
        var cleanupAttempted = false;
        var service = new SqliteRuntimeBackupService(
            temp.Clock,
            checkpoint: null,
            installedDoctorCheck: null,
            restoreFailureCleanup: _ =>
            {
                cleanupAttempted = true;
                return false;
            });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, result.ErrorCode);
        Assert.True(cleanupAttempted);
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.Single(Directory.GetFiles(temp.Root, ".runtime-restore-stage-*.sqlite"));
        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);
        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, recovered.ErrorCode);
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Current_read_denial_is_reconciled_without_confirmation_and_staged_raw_is_removed()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.MarkReadDenied(temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(1, preview.TerminalReconciliationCount);
        Assert.False(preview.RequiresConfirmation);
        Assert.True(result.Success, result.ErrorCode);
        using var database = temp.Open(temp.Target);
        Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal("expired_pending_deletion", temp.Scalar<string>(database, "SELECT state FROM retention_items;"));
        Assert.Equal("2026-04-01T00:00:00.0000000+00:00", temp.Scalar<string>(database, "SELECT read_denied_at FROM retention_items;"));
        Assert.Equal(0L, temp.Scalar<long>(database, "SELECT COUNT(*) FROM retention_tombstones;"));
    }

    [Fact]
    public void Preview_and_restore_reject_a_current_non_deleted_item_with_a_tombstone()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        var created = service.CreateAndPublish(temp.Source, bundle);
        Assert.True(created.Success, created.ErrorCode);
        File.Copy(temp.Source, temp.Target);
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target,
                $"INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('{new string('a', 32)}','2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00');");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var preview = service.Preview(bundle, temp.Target);
        var restore = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.False(restore.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restore.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Terminal_reconciliation_preserves_a_failed_deletion_retry_cursor()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "failed-deletion-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "UPDATE retention_items SET state='deletion_failed',revision=5,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00',deletion_started_at='2026-04-01T01:00:00.0000000+00:00',attempt_count=1,next_retry_at='2026-04-01T02:00:00.0000000+00:00',error_code='retention_delete_io_failed';");
            temp.Execute(target, $"INSERT INTO retention_delete_journal(item_id,durable_cursor,intent_at,expected_revision) VALUES('{new string('a', 32)}','7','2026-04-01T01:01:00.0000000+00:00',4);");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal("deletion_failed", temp.Scalar<string>(restored, "SELECT state FROM retention_items;"));
        Assert.Equal(5L, temp.Scalar<long>(restored, "SELECT revision FROM retention_items;"));
        Assert.Equal("7", temp.Scalar<string>(restored, "SELECT durable_cursor FROM retention_delete_journal;"));
        Assert.Equal(4L, temp.Scalar<long>(restored, "SELECT expected_revision FROM retention_delete_journal;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
    }

    [Fact]
    public void Terminal_reconciliation_accepts_deleting_before_the_first_delete_intent()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "pre-intent-deleting-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "UPDATE retention_items SET state='deleting',revision=4,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00',deletion_started_at='2026-04-01T01:00:00.0000000+00:00';");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal("deleting", temp.Scalar<string>(restored, "SELECT state FROM retention_items;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_delete_journal;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_leases;"));
        Assert.Equal(0L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
    }

    [Fact]
    public void Terminal_reconciliation_copies_the_exact_item_audit_and_linked_receipt()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "audit-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);
        var operationId = temp.AddItemAudit(temp.Target);

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal(1L, temp.Scalar<long>(restored, $"SELECT COUNT(*) FROM retention_audit_events WHERE operation_id='{operationId}' AND target_kind='item' AND target_id='{new string('a', 32)}';"));
        Assert.Equal(1L, temp.Scalar<long>(restored, $"SELECT COUNT(*) FROM retention_operation_receipts WHERE operation_id='{operationId}' AND target_kind='item' AND target_id='{new string('a', 32)}';"));
        Assert.Equal("deleted", temp.Scalar<string>(restored, $"SELECT completion_code FROM retention_operation_receipts WHERE operation_id='{operationId}';"));
    }

    [Fact]
    public void Terminal_reconciliation_pages_more_than_256_audits_and_receipts()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "paged-audit-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);
        temp.AddItemAudits(temp.Target, 300);

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_operation_receipts WHERE operation IN ('pin','unpin','delete_now');"));
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("receipt")]
    public void Oversized_archive_reconciliation_cell_fails_before_target_swap(string location)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        temp.AddItemAudit(temp.Source);
        temp.MakeReconciliationCellOversized(temp.Source, location);
        var bundle = Path.Combine(temp.Root, $"oversized-{location}-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "current", includeRaw: true);
        temp.DeleteRawAndTombstone(temp.Target);
        temp.AddItemAudit(temp.Target);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Preview_and_restore_page_a_large_valid_terminal_catalog()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "many-terminal-items", includeRaw: false);
        temp.AddDeletedItems(temp.Source, 300);
        var bundle = Path.Combine(temp.Root, "many-terminal-items.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);

        var preview = service.Preview(bundle, temp.Target);
        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(300, preview.TerminalReconciliationCount);
        Assert.NotNull(preview.TerminalReconciliationDigest);
        Assert.True(result.Success, result.ErrorCode);
        Assert.Equal(300, result.TerminalReconciliationCount);
        using var restored = temp.Open(temp.Target);
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_items WHERE state='deleted';"));
        Assert.Equal(300L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_tombstones;"));
    }

    [Fact]
    public void Missing_current_terminal_audit_shape_fails_preflight_without_swapping_target()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        File.Copy(temp.Source, temp.Target);
        temp.DeleteRawAndTombstone(temp.Target);
        using (var target = temp.Open(temp.Target))
        {
            temp.Execute(target, "DROP TABLE retention_operation_receipts;");
            temp.Execute(target, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Colliding_staged_retention_item_primary_key_fails_without_orphaning_raw_content()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "from-backup", includeRaw: true);
        using (var source = temp.Open(temp.Source))
        {
            var receipt = RetentionOwnershipReceipt.CreateRawRecord(new(
                new string('2', 32), 2, "2026-01-01T00:00:00.0000000+00:00",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime.Ticks, 1, SHA256.HashData([7])));
            temp.Execute(source, "UPDATE raw_records SET id=2 WHERE id=1;");
            temp.Execute(source, $"UPDATE retention_items SET source_item_id='2',ownership_receipt=X'{Convert.ToHexString(receipt)}' WHERE item_id='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa';");
            temp.Execute(source, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "current", includeRaw: true);
        temp.DeleteRawAndTombstone(temp.Target);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Supported_pre_retention_archive_is_backfilled_before_installation()
    {
        using var temp = new RestoreTemp();
        temp.CreatePreRetentionDatabase(temp.Source, "older");
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        var archivedSource = service.PreflightForMigration(temp.Source);
        Assert.True(archivedSource.Success, Describe(archivedSource));
        Assert.DoesNotContain("historical_instruction_analysis", archivedSource.ComponentVersions!.Keys);
        Assert.DoesNotContain("historical_import", archivedSource.ComponentVersions.Keys);
        Assert.DoesNotContain("sanitized_import", archivedSource.ComponentVersions.Keys);
        Assert.DoesNotContain("session", archivedSource.ComponentVersions.Keys);
        Assert.DoesNotContain("local_repository_catalog", archivedSource.ComponentVersions.Keys);
        var archivedSteps = archivedSource.MigrationSteps!.ToArray();
        var session = Array.IndexOf(archivedSteps, "session:0->14");
        var catalog = Array.IndexOf(archivedSteps, "local_repository_catalog:0->1");
        var retention = Array.IndexOf(archivedSteps, "retention:0->1");
        var skill = Array.IndexOf(archivedSteps, "skill_projection:0->1");
        Assert.True(session >= 0 && session < catalog);
        Assert.True(catalog < retention);
        Assert.True(retention < skill);
        Assert.Equal(
            [
                "historical_instruction_analysis:0->1",
                "historical_import:0->1",
                "sanitized_import:0->1",
            ],
            archivedSource.MigrationSteps!.Where(step =>
                step.StartsWith("historical_", StringComparison.Ordinal)
                || step.StartsWith("sanitized_import:", StringComparison.Ordinal)));
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        var publishedSource = service.PreflightForMigration(temp.Source);
        Assert.True(publishedSource.Success, Describe(publishedSource));
        Assert.Empty(publishedSource.MigrationSteps!);
        var destination = Path.Combine(temp.Root, "fresh", "monitor.db");

        var result = service.Restore(bundle, destination, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        var restoredPreflight = service.PreflightForMigration(destination);
        Assert.True(restoredPreflight.Success, Describe(restoredPreflight));
        Assert.Equal(1, restoredPreflight.ComponentVersions!["historical_instruction_analysis"]);
        Assert.Equal(1, restoredPreflight.ComponentVersions["historical_import"]);
        Assert.Equal(1, restoredPreflight.ComponentVersions["sanitized_import"]);
        Assert.Equal(14, restoredPreflight.ComponentVersions["session"]);
        Assert.Equal(1, restoredPreflight.ComponentVersions["local_repository_catalog"]);
        Assert.Empty(restoredPreflight.MigrationSteps!);
        using var restored = temp.Open(destination);
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND source_item_id='1';"));
        foreach (var table in LocalRepositoryCatalogSchemaV1.TableNames)
            Assert.Equal(table == "local_repository_reconciliation_state" ? 1L : 0L, temp.Scalar<long>(restored, $"SELECT COUNT(*) FROM \"{table}\";"));
        Assert.Equal(1L, temp.Scalar<long>(restored, """
            SELECT COUNT(*) FROM local_repository_reconciliation_state
            WHERE projector_key='local-repository-catalog-v1'
              AND last_discovered_span_id IS NULL
              AND updated_at='1970-01-01T00:00:00.0000000+00:00';
            """));
    }

    [Fact]
    public void Monitor_v9_archive_preview_and_restore_migrate_retained_trace_source_attribution_once()
    {
        const string traceId = "11111111111111111111111111111111";
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "monitor-v9-source", includeRaw: true);
        temp.ConvertToMonitorV9WithRetainedTraceSourceEvidence(traceId);
        var bundle = Path.Combine(temp.Root, "monitor-v9-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);

        var preflight = service.PreflightForMigration(temp.Source);
        var created = service.CreateAndPublish(temp.Source, bundle);
        var preview = service.Preview(bundle, temp.Target);
        var restored = service.Restore(
            bundle,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.True(preflight.Success, Describe(preflight));
        Assert.Equal(9, preflight.ComponentVersions!["monitor"]);
        Assert.Contains("monitor:9->11", preflight.MigrationSteps!);
        Assert.True(created.Success, created.ErrorCode);
        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(11, preview.SourceComponentVersions["monitor"]);
        Assert.DoesNotContain(preview.MigrationSteps, step => step.StartsWith("monitor:", StringComparison.Ordinal));
        Assert.True(restored.Success, restored.ErrorCode);
        using (var verification = temp.Open(temp.Target))
        {
            Assert.Equal(11L, temp.Scalar<long>(
                verification,
                "SELECT version FROM schema_version WHERE component='monitor';"));
            Assert.Equal("copilot-cli", temp.Scalar<string>(
                verification,
                $"SELECT client_kind FROM monitor_traces WHERE trace_id='{traceId}';"));
            Assert.Equal("copilot-cli", temp.Scalar<string>(
                verification,
                "SELECT client_kind FROM monitor_ingestions WHERE raw_record_id=1;"));
            Assert.Equal(1L, temp.Scalar<long>(
                verification,
                $"SELECT COUNT(*) FROM source_trace_attribution_observations WHERE raw_record_id=1 AND trace_id='{traceId}' AND cli_candidate_observed=1 AND vscode_candidate_observed=0 AND unknown_candidate_observed=0 AND relevant_evidence_observed=1;"));
            Assert.Equal(0L, temp.Scalar<long>(
                verification,
                "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
            Assert.Equal(1L, temp.Scalar<long>(
                verification,
                "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record' AND source_item_id='1' AND state='expiring' AND read_denied_at IS NULL AND expires_at='9999-12-31T23:59:59.9999999+00:00';"));
        }
        var firstStartupHash = temp.CanonicalDatabaseHash(temp.Target);

        new SqliteSourceCompatibilityStore(temp.Target).CreateSchema();

        Assert.Equal(
            firstStartupHash,
            temp.CanonicalDatabaseHash(temp.Target));
        using var secondStartup = temp.Open(temp.Target);
        Assert.Equal(1L, temp.Scalar<long>(
            secondStartup,
            "SELECT COUNT(*) FROM source_trace_attribution_observations;"));
        Assert.Equal(0L, temp.Scalar<long>(
            secondStartup,
            "SELECT COUNT(*) FROM source_trace_attribution_reconciliation_queue;"));
    }

    [Fact]
    public void Monitor_v9_preflight_rejects_extra_obsolete_skill_authority_without_mutation()
    {
        const string traceId = "11111111111111111111111111111111";
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "monitor-v9-extra-skill", includeRaw: true);
        temp.ConvertToMonitorV9WithRetainedTraceSourceEvidence(traceId);
        using (var connection = temp.Open(temp.Source))
        {
            temp.Execute(
                connection,
                "CREATE TABLE MoNiToR_SkIlL_ShAdOw(id INTEGER PRIMARY KEY);");
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock)
            .PreflightForMigration(temp.Source);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Fact]
    public void Current_monitor_v10_backup_and_restore_preserve_pending_trace_source_reconciliation()
    {
        const string traceId = "11111111111111111111111111111111";
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "monitor-v10-pending-source", includeRaw: false);
        using (var source = temp.Open(temp.Source))
        {
            temp.Execute(
                source,
                $"INSERT INTO source_trace_attribution_reconciliation_queue(trace_id) VALUES('{traceId}');");
            temp.Execute(source, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var bundle = Path.Combine(temp.Root, "monitor-v10-pending-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);

        var created = service.CreateAndPublish(temp.Source, bundle);
        var inspection = service.Inspect(bundle);
        var preview = service.Preview(bundle, temp.Target);
        var restored = service.Restore(
            bundle,
            temp.Target,
            new RuntimeRestoreOptions());

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(inspection.Success, inspection.ErrorCode);
        Assert.Equal(
            1L,
            inspection.RowCounts["source_trace_attribution_reconciliation_queue"]);
        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(
            1L,
            preview.RowCounts!["source_trace_attribution_reconciliation_queue"]);
        Assert.True(restored.Success, restored.ErrorCode);
        using var verification = temp.Open(temp.Target);
        Assert.Equal(traceId, temp.Scalar<string>(
            verification,
            "SELECT trace_id FROM source_trace_attribution_reconciliation_queue;"));
    }

    [Fact]
    public void Pre_retention_archive_cannot_bypass_current_terminal_lineage()
    {
        using var temp = new RestoreTemp();
        temp.CreatePreRetentionDatabase(temp.Source, "older");
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "current", includeRaw: true);
        temp.DeleteRawAndTombstone(temp.Target);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Post_swap_io_or_permission_fault_atomically_restores_exact_old_database(bool permissionFailure)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var service = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterSwap)
            {
                if (permissionFailure) throw new UnauthorizedAccessException("injected-permission-fault");
                throw new IOException("injected-io-fault");
            }
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRolledBack, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        using var restored = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Doctor_check_failure_before_commit_rolls_back_the_exact_old_database()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "doctor-failure.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = File.ReadAllBytes(temp.Target);
        var service = new SqliteRuntimeBackupService(
            temp.Clock,
            checkpoint: null,
            _ => throw new IOException("injected-doctor-failure"));

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRolledBack, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var restored = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Post_swap_retention_invariant_drift_is_detected_and_rolls_back_old_database()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: true);
        var driftInjected = false;
        var service = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint != RuntimeBackupCheckpoints.AfterStageValidated) return;
            using var journal = JsonDocument.Parse(File.ReadAllBytes(temp.Target + ".runtime-restore-journal.json"));
            var stage = Path.Combine(temp.Root, journal.RootElement.GetProperty("stage_file_name").GetString()!);
            using (var staged = temp.Open(stage))
            {
                temp.Execute(staged, "UPDATE retention_items SET deleted_at='2026-04-02T00:00:00.0000000+00:00';");
                temp.Execute(staged, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            driftInjected = true;
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.True(driftInjected);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRolledBack, result.ErrorCode);
        using var restored = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT captured_at FROM retention_items;"));
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("history")]
    public async Task CatalogComposerRejectsCorruptionIntroducedAtArchiveSourcePreflight(string mutation)
    {
        using var temp = new RestoreTemp();
        using var fixture = await CreateRestoreCatalogFixtureAsync();
        var bundle = Path.Combine(temp.Root, $"catalog-p1-{mutation}.zip");
        var publisher = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(publisher.CreateAndPublish(fixture.DatabasePath, bundle).Success);
        CopyCurrentCatalog(fixture.DatabasePath, temp.Target, temp);
        var archiveBefore = File.ReadAllBytes(bundle);
        var targetBefore = File.ReadAllBytes(temp.Target);
        var injectedRows = -1;
        var service = new SqliteRuntimeBackupService(fixture.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterArchiveExtracted)
                injectedRows = CorruptCatalogSemantic(ReadOwnedStagePath(temp), mutation);
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.Equal(1, injectedRows);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.False(result.PreRestoreBackupCreated);
        Assert.Equal(archiveBefore, File.ReadAllBytes(bundle));
        Assert.Equal(targetBefore, File.ReadAllBytes(temp.Target));
        AssertNoPublishedPreRestore(temp.Root);
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("history")]
    public async Task CatalogComposerRejectsCorruptCurrentAtPreviewAndRestorePreflight(string mutation)
    {
        using var temp = new RestoreTemp();
        using var fixture = await CreateRestoreCatalogFixtureAsync();
        var bundle = Path.Combine(temp.Root, $"catalog-p2-{mutation}.zip");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, bundle).Success);
        _ = temp.CanonicalDatabaseHash(fixture.DatabasePath);
        var archiveBefore = File.ReadAllBytes(bundle);

        CopyCurrentCatalog(fixture.DatabasePath, temp.Target, temp);
        Assert.Equal(1, CorruptCatalogSemantic(temp.Target, mutation));
        var previewTargetBefore = File.ReadAllBytes(temp.Target);
        var preview = service.Preview(bundle, temp.Target);

        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.Equal(previewTargetBefore, File.ReadAllBytes(temp.Target));
        Assert.Equal(archiveBefore, File.ReadAllBytes(bundle));
        AssertNoPublishedPreRestore(temp.Root);

        CopyCurrentCatalog(fixture.DatabasePath, temp.Target, temp, overwrite: true);
        Assert.Equal(1, CorruptCatalogSemantic(temp.Target, mutation));
        var restoreTargetBefore = File.ReadAllBytes(temp.Target);
        var restored = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(restored.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restored.ErrorCode);
        Assert.False(restored.PreRestoreBackupCreated);
        Assert.Equal(restoreTargetBefore, File.ReadAllBytes(temp.Target));
        Assert.Equal(archiveBefore, File.ReadAllBytes(bundle));
        AssertNoPublishedPreRestore(temp.Root);
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("history")]
    public async Task CatalogComposerRejectsCorruptionIntroducedAfterStagedMigration(string mutation)
    {
        using var temp = new RestoreTemp();
        using var fixture = await CreateRestoreCatalogFixtureAsync();
        var bundle = Path.Combine(temp.Root, $"catalog-p3-{mutation}.zip");
        var publisher = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(publisher.CreateAndPublish(fixture.DatabasePath, bundle).Success);
        CopyCurrentCatalog(fixture.DatabasePath, temp.Target, temp);
        var archiveBefore = File.ReadAllBytes(bundle);
        var targetBefore = File.ReadAllBytes(temp.Target);
        var injectedRows = -1;
        var service = new SqliteRuntimeBackupService(fixture.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterMigration)
                injectedRows = CorruptCatalogSemantic(ReadOwnedStagePath(temp), mutation);
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.Equal(1, injectedRows);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(archiveBefore, File.ReadAllBytes(bundle));
        Assert.Equal(targetBefore, File.ReadAllBytes(temp.Target));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("history")]
    public async Task CatalogComposerRejectsCorruptionIntroducedAfterSwapAndRestoresOldTarget(string mutation)
    {
        using var temp = new RestoreTemp();
        using var fixture = await CreateRestoreCatalogFixtureAsync();
        var bundle = Path.Combine(temp.Root, $"catalog-p4-{mutation}.zip");
        var publisher = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(publisher.CreateAndPublish(fixture.DatabasePath, bundle).Success);
        CopyCurrentCatalog(fixture.DatabasePath, temp.Target, temp);
        var archiveBefore = File.ReadAllBytes(bundle);
        var targetBefore = File.ReadAllBytes(temp.Target);
        var injectedRows = -1;
        var installedBytesChanged = false;
        var reachedCheckpoints = new List<string>();
        string? beforeInstalledJournalPhase = null;
        var beforeInstalledTargetExists = false;
        var beforeInstalledJournalExists = false;
        var beforeInstalledRollbackExists = false;
        var beforeInstalledLockExists = false;
        var beforeInstalledSidecarExists = false;
        var service = new SqliteRuntimeBackupService(fixture.Clock, checkpoint =>
        {
            reachedCheckpoints.Add(checkpoint);
            if (checkpoint != SqliteRuntimeBackupService.CatalogBeforeInstalledValidationCheckpoint)
                return;
            using (var journal = JsonDocument.Parse(
                File.ReadAllBytes(temp.Target + ".runtime-restore-journal.json")))
            {
                beforeInstalledJournalPhase = journal.RootElement.GetProperty("phase").GetString();
            }
            beforeInstalledTargetExists = File.Exists(temp.Target);
            beforeInstalledJournalExists = File.Exists(temp.Target + ".runtime-restore-journal.json");
            beforeInstalledRollbackExists = File.Exists(temp.Target + ".runtime-restore-rollback");
            beforeInstalledLockExists = File.Exists(temp.Target + ".runtime-restore.lock");
            beforeInstalledSidecarExists = new[] { "-journal", "-wal", "-shm" }
                .Any(suffix => File.Exists(temp.Target + suffix));
            var installedBeforeMutation = File.ReadAllBytes(temp.Target);
            injectedRows = CorruptAndReplaceInstalledCatalog(temp.Target, mutation);
            installedBytesChanged = !File.ReadAllBytes(temp.Target).SequenceEqual(installedBeforeMutation);
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.Equal("prepared", beforeInstalledJournalPhase);
        Assert.True(beforeInstalledTargetExists);
        Assert.True(beforeInstalledJournalExists);
        Assert.True(beforeInstalledRollbackExists);
        Assert.True(beforeInstalledLockExists);
        Assert.False(beforeInstalledSidecarExists);
        Assert.True(
            reachedCheckpoints.IndexOf(RuntimeBackupCheckpoints.AfterSwap)
            < reachedCheckpoints.IndexOf(SqliteRuntimeBackupService.CatalogBeforeInstalledValidationCheckpoint));
        Assert.DoesNotContain(
            RuntimeBackupCheckpoints.AfterInstalledJournalCandidateFlushed,
            reachedCheckpoints);
        Assert.Equal(1, injectedRows);
        Assert.True(installedBytesChanged);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRolledBack, result.ErrorCode);
        Assert.Equal(archiveBefore, File.ReadAllBytes(bundle));
        Assert.Equal(targetBefore, File.ReadAllBytes(temp.Target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            temp.Root,
            ".catalog-installed-*",
            SearchOption.TopDirectoryOnly));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public async Task Post_swap_catalog_byte_drift_is_rejected_before_installed_validation()
    {
        using var temp = new RestoreTemp();
        using var fixture = await CreateRestoreCatalogFixtureAsync();
        var bundle = Path.Combine(temp.Root, "catalog-post-swap-hash.zip");
        var publisher = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(publisher.CreateAndPublish(fixture.DatabasePath, bundle).Success);
        CopyCurrentCatalog(fixture.DatabasePath, temp.Target, temp);
        var archiveBefore = File.ReadAllBytes(bundle);
        var targetBefore = File.ReadAllBytes(temp.Target);
        var injectedRows = -1;
        var installedValidationReached = false;
        var service = new SqliteRuntimeBackupService(fixture.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterSwap)
                injectedRows = CorruptAndReplaceInstalledCatalog(temp.Target, "queue");
            if (checkpoint == SqliteRuntimeBackupService.CatalogBeforeInstalledValidationCheckpoint)
                installedValidationReached = true;
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.Equal(1, injectedRows);
        Assert.False(installedValidationReached);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRolledBack, result.ErrorCode);
        Assert.Equal(archiveBefore, File.ReadAllBytes(bundle));
        Assert.Equal(targetBefore, File.ReadAllBytes(temp.Target));
        Assert.Empty(Directory.EnumerateFileSystemEntries(
            temp.Root,
            ".catalog-installed-*",
            SearchOption.TopDirectoryOnly));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Pre_restore_output_rejects_bundle_database_and_restore_control_file_collisions()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var reserved = new[]
        {
            bundle,
            temp.Target,
            temp.Target + "-journal",
            temp.Target + "-wal",
            temp.Target + "-shm",
            temp.Target + ".runtime-restore.lock",
            temp.Target + ".runtime-restore-stage",
            temp.Target + ".runtime-restore-rollback",
            temp.Target + ".runtime-restore-rollback-journal",
            temp.Target + ".runtime-restore-rollback-wal",
            temp.Target + ".runtime-restore-rollback-shm",
            temp.Target + ".runtime-restore-journal.json",
            temp.Target + ".runtime-restore-journal.json.commit",
        };

        foreach (var path in reserved)
        {
            var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(PreRestoreOutputPath: path));
            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        }

        using var database = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(database, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Theory]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData(".runtime-restore.lock")]
    [InlineData(".runtime-restore-stage")]
    [InlineData(".runtime-restore-rollback")]
    [InlineData(".runtime-restore-rollback-journal")]
    [InlineData(".runtime-restore-rollback-wal")]
    [InlineData(".runtime-restore-rollback-shm")]
    [InlineData(".runtime-restore-journal.json")]
    [InlineData(".runtime-restore-journal.json.commit")]
    public void Restore_bundle_rejects_database_sidecar_and_control_collisions_without_mutation(string suffix)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var validBundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, validBundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var collidingBundle = temp.Target + suffix;
        File.Copy(validBundle, collidingBundle);

        var result = service.Restore(collidingBundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.InvalidArguments, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        Assert.True(File.Exists(collidingBundle));
    }

    [Fact]
    public void Restore_uses_caller_selected_pre_restore_output_without_path_disclosure()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var preRestore = Path.Combine(temp.Root, "operator-pre-restore.zip");

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(PreRestoreOutputPath: preRestore));

        Assert.True(result.Success, result.ErrorCode);
        Assert.True(result.PreRestoreBackupCreated);
        Assert.Equal("operator-pre-restore.zip", result.PreRestoreBackupFileName);
        Assert.True(File.Exists(preRestore));
        Assert.True(service.Inspect(preRestore).Success);
        using (var json = JsonDocument.Parse(RuntimeBackupJson.SerializeResult(result)))
            Assert.DoesNotContain(JsonStrings(json.RootElement), value => value.Contains(temp.Root, StringComparison.OrdinalIgnoreCase));
        using var restored = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Restore_rejects_an_existing_caller_selected_pre_restore_output_without_mutation_or_path_disclosure()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var targetBefore = File.ReadAllBytes(temp.Target);
        var preRestore = Path.Combine(temp.Root, "operator-pre-restore.zip");
        var sentinel = Encoding.UTF8.GetBytes("caller-owned-sentinel");
        File.WriteAllBytes(preRestore, sentinel);

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions(PreRestoreOutputPath: preRestore));

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.OutputExists, result.ErrorCode);
        Assert.Equal(sentinel, File.ReadAllBytes(preRestore));
        Assert.Equal(targetBefore, File.ReadAllBytes(temp.Target));
        using (var json = JsonDocument.Parse(RuntimeBackupJson.SerializeResult(result)))
            Assert.DoesNotContain(JsonStrings(json.RootElement), value => value.Contains(temp.Root, StringComparison.OrdinalIgnoreCase));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Restore_receipt_io_or_permission_failure_before_swap_preserves_exact_old_database_and_cleans_owned_controls(bool permissionFailure)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var service = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.DuringStageReceipt)
            {
                if (permissionFailure) throw new UnauthorizedAccessException("injected-permission-fault");
                throw new IOException("injected-io-fault");
            }
        });

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreFailed, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        using var restored = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Windows_idle_sqlite_owner_is_rejected_before_restore_work()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        using var liveOwner = temp.Open(temp.Target);

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.MonitorMustBeStopped, result.ErrorCode);
        Assert.Equal("old", temp.Scalar<string>(liveOwner, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Active_delete_journal_writer_is_rejected_before_restore_work()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        using (var setup = temp.Open(temp.Target))
            temp.Execute(setup, "PRAGMA journal_mode=DELETE;");
        var before = File.ReadAllBytes(temp.Target);
        using var writer = temp.Open(temp.Target);
        temp.Execute(writer, "PRAGMA locking_mode=EXCLUSIVE;");
        temp.Execute(writer, "BEGIN EXCLUSIVE;");

        RuntimeRestoreResult result;
        try
        {
            result = new SqliteRuntimeBackupService(temp.Clock)
                .Restore(bundle, temp.Target, new RuntimeRestoreOptions());

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.MonitorMustBeStopped, result.ErrorCode);
        }
        finally
        {
            temp.Execute(writer, "ROLLBACK;");
        }
        writer.Dispose();
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
    }

    [Fact]
    public void Dangling_reparse_sidecar_is_not_treated_as_absent()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "dangling-sidecar.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var before = File.ReadAllBytes(temp.Target);
        var sidecar = temp.Target + "-wal";
        try { File.CreateSymbolicLink(sidecar, Path.Combine(temp.Root, "missing-sidecar-target")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        { throw Xunit.Sdk.SkipException.ForSkip($"Cannot create reparse fixture: {exception.GetType().Name}"); }

        var result = service.Restore(bundle, temp.Target, new RuntimeRestoreOptions());

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.MonitorMustBeStopped, result.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        Assert.True(new FileInfo(sidecar).LinkTarget is not null);
    }

    [Fact]
    public void Startup_recovery_uses_outside_database_journal_to_restore_exact_old_database_after_process_crash()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "crash-source.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterSwap) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-rollback"));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
        Assert.False(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.False(File.Exists(temp.Target + ".runtime-restore-rollback"));
        using var database = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(database, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterOwnerJournal, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterArchiveExtracted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterMigration, true)]
    [InlineData(RuntimeBackupCheckpoints.DuringStageReceipt, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterReceiptPersisted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterStageValidated, true)]
    [InlineData(RuntimeBackupCheckpoints.BeforeSwap, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterOwnerJournal, false)]
    [InlineData(RuntimeBackupCheckpoints.BeforeSwap, false)]
    public void Owner_journal_recovery_cleans_only_its_stage_and_preserves_the_pre_swap_destination(string faultPoint, bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "pre-swap.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        byte[]? before = null;
        if (targetExisted)
        {
            temp.CreateDatabase(temp.Target, "old", includeRaw: false);
            Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
            before = File.ReadAllBytes(temp.Target);
        }
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        if (targetExisted)
        {
            Assert.Equal(before, File.ReadAllBytes(temp.Target));
            using var target = temp.Open(temp.Target);
            Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        }
        else Assert.False(File.Exists(temp.Target));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledJournalCandidateFlushed, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledValidation, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap, false)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledValidation, false)]
    public void Pre_commit_crash_recovery_rolls_back_existing_or_fresh_destination(string faultPoint, bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "pre-commit.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        byte[]? before = null;
        if (targetExisted)
        {
            temp.CreateDatabase(temp.Target, "old", includeRaw: false);
            Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
            before = File.ReadAllBytes(temp.Target);
        }
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        if (targetExisted)
        {
            Assert.Equal(before, File.ReadAllBytes(temp.Target));
            using var target = temp.Open(temp.Target);
            Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        }
        else Assert.False(File.Exists(temp.Target));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalCommitted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterRollbackDeleted, true)]
    [InlineData(RuntimeBackupCheckpoints.BeforeJournalDeleted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalDeleted, true)]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed, false)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalDeleted, false)]
    public void Commit_decision_crash_recovery_keeps_validated_new_database_and_receipt(string faultPoint, bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "committed.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        if (targetExisted) temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        using var target = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal(1L, temp.Scalar<long>(target, "SELECT COUNT(*) FROM runtime_backup_receipts WHERE operation_kind='restore' AND result_code='restore_succeeded';"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Recovery_promotes_a_pending_commit_before_forward_cleanup(bool targetExisted)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "pending-commit-promotion.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        if (targetExisted) temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashingRestore = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashingRestore.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var crashingRecovery = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterRecoveryCommitPromoted) throw new SimulatedProcessCrashException();
        });

        Assert.Throws<SimulatedProcessCrashException>(() => crashingRecovery.Initialize(temp.Target));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.False(File.Exists(temp.Target + ".runtime-restore-journal.json.commit"));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        using var target = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal(1L, temp.Scalar<long>(target, "SELECT COUNT(*) FROM runtime_backup_receipts WHERE operation_kind='restore' AND result_code='restore_succeeded';"));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Invalid_committed_target_falls_back_to_exact_verified_rollback()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "committed-fallback.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        using (var installed = temp.Open(temp.Target))
        {
            temp.Execute(installed, "UPDATE runtime_probe SET value='tampered';");
            temp.Execute(installed, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap)]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed)]
    public void Missing_installed_target_is_recreated_from_the_exact_verified_rollback(string faultPoint)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "missing-installed.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        File.Delete(temp.Target);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterSwap)]
    [InlineData(RuntimeBackupCheckpoints.AfterInstalledValidation)]
    public void Recovery_recognizes_an_already_completed_pre_commit_rollback(string faultPoint)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "completed-pre-commit-rollback.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var rollback = temp.Target + ".runtime-restore-rollback";
        File.Replace(rollback, temp.Target, null, ignoreMetadataErrors: true);
        Assert.False(File.Exists(rollback));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Theory]
    [InlineData(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed)]
    [InlineData(RuntimeBackupCheckpoints.AfterJournalCommitted)]
    public void Recovery_recognizes_an_already_completed_committed_fallback_rollback(string faultPoint)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "completed-committed-fallback-rollback.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = File.ReadAllBytes(temp.Target);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == faultPoint) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var rollback = temp.Target + ".runtime-restore-rollback";
        File.Replace(rollback, temp.Target, null, ignoreMetadataErrors: true);
        Assert.False(File.Exists(rollback));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
        temp.AssertNoRestoreControls();
    }

    [Fact]
    public void Prepared_stage_hash_mismatch_is_retained_and_fails_closed()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "prepared-mismatch.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.BeforeSwap) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var journalPath = temp.Target + ".runtime-restore-journal.json";
        using var document = JsonDocument.Parse(File.ReadAllBytes(journalPath));
        var stage = Path.Combine(temp.Root, document.RootElement.GetProperty("stage_file_name").GetString()!);
        using (var stream = new FileStream(stage, FileMode.Open, FileAccess.Write, FileShare.None)) stream.WriteByte(0xff);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(stage));
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Recovery_refuses_active_target_sidecar_without_deleting_owned_rollback_or_journal()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "new", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "sidecar-recovery.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterSwap) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var sidecar = temp.Target + "-wal";
        File.WriteAllText(sidecar, "active-or-unknown");

        var blocked = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(blocked.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, blocked.ErrorCode);
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-rollback"));
        File.Delete(sidecar);
        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);
        Assert.True(recovered.Success, recovered.ErrorCode);
        using var target = temp.Open(temp.Target);
        Assert.Equal("old", temp.Scalar<string>(target, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Startup_rejects_unowned_legacy_stage_without_deleting_or_mutating()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));
        var stage = temp.Target + ".runtime-restore-stage";
        File.Copy(temp.Target, stage);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(stage));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Startup_rejects_unknown_v1_journal_and_preserves_all_artifacts()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "staged", includeRaw: false);
        File.Copy(temp.Source, temp.Target + ".runtime-restore-stage");
        File.WriteAllText(
            temp.Target + ".runtime-restore-journal.json",
            $$"""{"schema_version":"runtime-restore-journal.v1","archive_sha256":"{{new string('1', 64)}}","target_existed":false}""");

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.False(File.Exists(temp.Target));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-stage"));
        Assert.True(File.Exists(temp.Target + ".runtime-restore-journal.json"));
    }

    [Fact]
    public void Startup_rejects_invalid_nullable_hash_kind_and_preserves_nonce_bound_stage()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "staged", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "invalid-journal-kind.zip");
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).CreateAndPublish(temp.Source, bundle).Success);
        var crashing = new SqliteRuntimeBackupService(temp.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterOwnerJournal) throw new SimulatedProcessCrashException();
        });
        Assert.Throws<SimulatedProcessCrashException>(() => crashing.Restore(bundle, temp.Target, new RuntimeRestoreOptions()));
        var journalPath = temp.Target + ".runtime-restore-journal.json";
        var journal = File.ReadAllText(journalPath);
        using var document = JsonDocument.Parse(journal);
        var stage = Path.Combine(temp.Root, document.RootElement.GetProperty("stage_file_name").GetString()!);
        File.Copy(temp.Source, stage);
        File.WriteAllText(journalPath, journal.Replace("\"target_before_sha256\":null", "\"target_before_sha256\":7", StringComparison.Ordinal));

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(stage));
        Assert.False(File.Exists(temp.Target));
    }

    [Theory]
    [InlineData("legacy-stage")]
    [InlineData("legacy-stage-wal")]
    [InlineData("dynamic-stage")]
    [InlineData("dynamic-stage-shm")]
    [InlineData("rollback-journal")]
    [InlineData("rollback-wal")]
    [InlineData("rollback-shm")]
    [InlineData("journal-commit")]
    public void Startup_fails_closed_and_preserves_every_unowned_reserved_artifact(string kind)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Target, "old", includeRaw: false);
        Assert.True(new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target).Success);
        var artifact = kind switch
        {
            "legacy-stage" => temp.Target + ".runtime-restore-stage",
            "legacy-stage-wal" => temp.Target + ".runtime-restore-stage-wal",
            "dynamic-stage" => Path.Combine(temp.Root, ".runtime-restore-stage-unowned.sqlite"),
            "dynamic-stage-shm" => Path.Combine(temp.Root, ".runtime-restore-stage-unowned.sqlite-shm"),
            "rollback-journal" => temp.Target + ".runtime-restore-rollback-journal",
            "rollback-wal" => temp.Target + ".runtime-restore-rollback-wal",
            "rollback-shm" => temp.Target + ".runtime-restore-rollback-shm",
            "journal-commit" => temp.Target + ".runtime-restore-journal.json.commit",
            _ => throw new InvalidOperationException(),
        };
        File.WriteAllText(artifact, "unowned");
        var before = File.ReadAllBytes(temp.Target);

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(artifact));
        Assert.Equal(before, File.ReadAllBytes(temp.Target));
    }

    [Fact]
    public void Startup_rejects_unowned_orphan_rollback_and_preserves_new_target()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "old", includeRaw: false);
        temp.CreateDatabase(temp.Target, "new", includeRaw: false);
        File.Copy(temp.Source, temp.Target + ".runtime-restore-rollback");

        var recovered = new SqliteRuntimeBackupService(temp.Clock).Initialize(temp.Target);

        Assert.False(recovered.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreRollbackFailed, recovered.ErrorCode);
        Assert.True(File.Exists(temp.Target + ".runtime-restore-rollback"));
        using var database = temp.Open(temp.Target);
        Assert.Equal("new", temp.Scalar<string>(database, "SELECT value FROM runtime_probe WHERE id=1;"));
    }

    [Fact]
    public void Read_only_preflight_rejects_future_monitor_without_changing_candidate_bytes()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "future", includeRaw: false);
        using (var connection = temp.Open(temp.Source))
        {
            temp.Execute(connection, "UPDATE schema_version SET version=999 WHERE component='monitor';");
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
        Assert.Throws<InvalidOperationException>(() => MonitorHost.Build(
            new MonitorOptions(temp.Source, "http://127.0.0.1:0", false, 31_457_280),
            new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, StartSessionWriter = false, StartSessionOtelEnrichment = false, StartRetentionCleanupWorker = false, UseUserSecrets = false }));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Theory]
    [InlineData("source_trace_attribution_observations")]
    [InlineData("source_trace_attribution_reconciliation_queue")]
    public void Read_only_preflight_rejects_monitor_v10_without_trace_source_attribution_authority(
        string table)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "missing-attribution-authority", includeRaw: false);
        using (var connection = temp.Open(temp.Source))
        {
            temp.Execute(connection, $"DROP TABLE {table};");
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Theory]
    [InlineData("wrong-pk")]
    [InlineData("wrong-check")]
    [InlineData("missing-index")]
    [InlineData("wrong-index")]
    [InlineData("wrong-queue-pk")]
    public void Read_only_preflight_rejects_monitor_v10_with_malformed_trace_source_attribution_authority(
        string corruption)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "malformed-attribution-authority", includeRaw: false);
        using (var connection = temp.Open(temp.Source))
        {
            if (corruption is "wrong-pk" or "wrong-check")
            {
                temp.Execute(
                    connection,
                    """
                    DROP INDEX IX_source_trace_attribution_observations_trace_id;
                    DROP TABLE source_trace_attribution_observations;
                    """);
                var tableSql = corruption switch
                {
                    "wrong-pk" => SqliteSourceCompatibilityStore.TraceSourceAttributionTableSql.Replace(
                        "PRIMARY KEY (raw_record_id, trace_id)",
                        "PRIMARY KEY (raw_record_id)",
                        StringComparison.Ordinal),
                    "wrong-check" => SqliteSourceCompatibilityStore.TraceSourceAttributionTableSql.Replace(
                        "cli_candidate_observed IN (0, 1)",
                        "cli_candidate_observed IN (0, 1, 2)",
                        StringComparison.Ordinal),
                    _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
                };
                temp.Execute(connection, tableSql);
                temp.Execute(
                    connection,
                    SqliteSourceCompatibilityStore.TraceSourceAttributionIndexSql);
            }
            else if (corruption is "missing-index" or "wrong-index")
            {
                temp.Execute(
                    connection,
                    "DROP INDEX IX_source_trace_attribution_observations_trace_id;");
                if (corruption == "wrong-index")
                {
                    temp.Execute(
                        connection,
                        """
                        CREATE INDEX IX_source_trace_attribution_observations_trace_id
                        ON source_trace_attribution_observations(raw_record_id, trace_id);
                        """);
                }
            }
            else if (corruption == "wrong-queue-pk")
            {
                temp.Execute(
                    connection,
                    "DROP TABLE source_trace_attribution_reconciliation_queue;");
                temp.Execute(
                    connection,
                    SqliteSourceCompatibilityStore.TraceSourceReconciliationQueueTableSql.Replace(
                        "trace_id TEXT NOT NULL PRIMARY KEY",
                        "trace_id TEXT NOT NULL",
                        StringComparison.Ordinal));
            }
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock)
            .PreflightForMigration(temp.Source);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Fact]
    public void Preflight_and_preview_reject_monitor_v9_with_v10_owned_authority_without_mutation()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "current-source", includeRaw: false);
        var bundle = Path.Combine(temp.Root, "current-source.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        temp.CreateDatabase(temp.Target, "colliding-v9-target", includeRaw: false);
        using (var connection = temp.Open(temp.Target))
        {
            temp.Execute(
                connection,
                "UPDATE schema_version SET version=9 WHERE component='monitor';");
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Target));

        var preflight = service.PreflightForMigration(temp.Target);
        var preview = service.Preview(bundle, temp.Target);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preview.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Target)));
    }

    [Fact]
    public void Read_only_preflight_accepts_the_current_wave_3_component_vector()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "current-wave-3", includeRaw: false);
        new SqliteHistoricalInstructionAnalysisStoreV1(temp.Source).CreateSchema();
        new SqliteHistoricalImportStore(temp.Source).CreateSchema();
        new SqliteSanitizedImportStore(temp.Source, temp.Clock).CreateSchema();
        using (var connection = temp.Open(temp.Source))
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(1, result.ComponentVersions!["historical_instruction_analysis"]);
        Assert.Equal(1, result.ComponentVersions["historical_import"]);
        Assert.Equal(1, result.ComponentVersions["sanitized_import"]);
    }

    [Theory]
    [InlineData("historical_instruction_analysis")]
    [InlineData("historical_import")]
    [InlineData("sanitized_import")]
    public void Read_only_preflight_rejects_future_wave_3_component_versions_without_mutation(string component)
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "future-wave-3", includeRaw: false);
        new SqliteHistoricalInstructionAnalysisStoreV1(temp.Source).CreateSchema();
        new SqliteHistoricalImportStore(temp.Source).CreateSchema();
        new SqliteSanitizedImportStore(temp.Source, temp.Clock).CreateSchema();
        using (var connection = temp.Open(temp.Source))
        {
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE schema_version SET version=2 WHERE component=$component;";
            update.Parameters.AddWithValue("$component", component);
            Assert.Equal(1, update.ExecuteNonQuery());
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Fact]
    public void Read_only_preflight_rejects_sanitized_import_without_its_historical_import_dependency()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "missing-import-dependency", includeRaw: false);
        using (var connection = temp.Open(temp.Source))
        using (var transaction = connection.BeginTransaction())
        {
            SanitizedImportSchemaV1.Ensure(connection, transaction);
            transaction.Commit();
            temp.Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Fact]
    public void Read_only_preflight_does_not_modify_the_offline_database()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "offline-preflight", includeRaw: false);
        var before = SHA256.HashData(File.ReadAllBytes(temp.Source));

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.True(result.Success, Describe(result));
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(temp.Source)));
    }

    [Fact]
    public void Read_only_preflight_does_not_bypass_an_exclusive_delete_journal_writer()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "exclusive-preflight", includeRaw: false);
        using var writer = temp.Open(temp.Source);
        temp.Execute(writer, "PRAGMA journal_mode=DELETE;");
        temp.Execute(writer, "PRAGMA locking_mode=EXCLUSIVE;");
        temp.Execute(writer, "BEGIN EXCLUSIVE;");

        try
        {
            var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        }
        finally
        {
            temp.Execute(writer, "ROLLBACK;");
        }
    }

    [Fact]
    public void Preflight_orders_missing_wave_3_storage_migrations_before_runtime_backup()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "pre-wave-3", includeRaw: false);

        var result = new SqliteRuntimeBackupService(temp.Clock).PreflightForMigration(temp.Source);

        Assert.True(result.Success, Describe(result));
        var steps = result.MigrationSteps!.ToArray();
        var session = Array.IndexOf(steps, "session:0->14");
        var catalog = Array.IndexOf(steps, "local_repository_catalog:0->1");
        var skill = Array.IndexOf(steps, "skill_projection:0->1");
        var instruction = Array.IndexOf(steps, "historical_instruction_analysis:0->1");
        var historicalImport = Array.IndexOf(steps, "historical_import:0->1");
        var sanitizedImport = Array.IndexOf(steps, "sanitized_import:0->1");
        var runtimeBackup = Array.IndexOf(steps, "runtime_backup:0->1");
        var pricing = Array.IndexOf(steps, "pricing:0->1");
        Assert.True(session >= 0);
        Assert.True(session < catalog);
        Assert.True(catalog < skill);
        Assert.True(skill < instruction);
        Assert.True(instruction < historicalImport);
        Assert.True(historicalImport < sanitizedImport);
        Assert.True(sanitizedImport < runtimeBackup);
        Assert.True(runtimeBackup < pricing);
        Assert.Equal(
            [
                "historical_instruction_analysis:0->1",
                "historical_import:0->1",
                "sanitized_import:0->1",
                "runtime_backup:0->1",
                "pricing:0->1",
            ],
            steps[^5..]);
    }

    [Fact]
    public void Restore_to_fresh_destination_preserves_original_retention_clock_and_is_cross_directory_portable()
    {
        using var temp = new RestoreTemp();
        temp.CreateDatabase(temp.Source, "portable", includeRaw: true);
        var bundle = Path.Combine(temp.Root, "portable.zip");
        var service = new SqliteRuntimeBackupService(temp.Clock);
        Assert.True(service.CreateAndPublish(temp.Source, bundle).Success);
        var otherMachineDirectory = Directory.CreateDirectory(Path.Combine(temp.Root, "synthetic-other-machine"));
        var destination = Path.Combine(otherMachineDirectory.FullName, "monitor.db");

        var result = service.Restore(bundle, destination, new RuntimeRestoreOptions());

        Assert.True(result.Success, result.ErrorCode);
        using var restored = temp.Open(destination);
        Assert.Equal("2026-01-01T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT captured_at FROM retention_items;"));
        Assert.Equal("2026-04-01T00:00:00.0000000+00:00", temp.Scalar<string>(restored, "SELECT expires_at FROM retention_items;"));
        Assert.Equal("raw-default-90d", temp.Scalar<string>(restored, "SELECT policy_id FROM retention_items;"));
        Assert.Equal(1L, temp.Scalar<long>(restored, "SELECT policy_version FROM retention_items;"));
        Assert.Equal("portable", temp.Scalar<string>(restored, "SELECT value FROM runtime_probe WHERE id=1;"));
        Assert.Equal("database_ready", result.ReadinessCheck);
        Assert.Equal("doctor_store_ready", result.DoctorCheck);
        Assert.False(result.PreRestoreBackupCreated);
        var preflight = service.PreflightForMigration(destination);
        Assert.True(preflight.Success, preflight.ErrorCode);
        Assert.Equal(11, preflight.ComponentVersions!["monitor"]);
        Assert.Equal(14, preflight.ComponentVersions["session"]);
        Assert.Equal(1, preflight.ComponentVersions["local_repository_catalog"]);
        Assert.Equal(1, preflight.ComponentVersions["retention"]);
        Assert.Equal(1, preflight.ComponentVersions["doctor"]);
        Assert.Equal(2, preflight.ComponentVersions["alert_engine"]);
        Assert.Equal(1, preflight.ComponentVersions["alert_lifecycle"]);
        Assert.Equal(1, preflight.ComponentVersions["historical_instruction_analysis"]);
        Assert.Equal(1, preflight.ComponentVersions["historical_import"]);
        Assert.Equal(1, preflight.ComponentVersions["sanitized_import"]);
        Assert.Equal(1, preflight.ComponentVersions["runtime_backup"]);
        Assert.Equal(1, preflight.ComponentVersions["pricing"]);
        Assert.Equal(1, preflight.ComponentVersions["first_trace_navigation"]);
        Assert.Empty(preflight.MigrationSteps!);
    }

    private static async Task<LocalRepositoryAdmissionFixture> CreateRestoreCatalogFixtureAsync()
    {
        var fixture = new LocalRepositoryAdmissionFixture();
        try
        {
            var payload = LocalRepositoryAdmissionFixture.SpanPayload(
                new LocalRepositoryAdmissionFixture.SpanInput(
                    LocalRepositoryAdmissionFixture.Trace(1),
                    LocalRepositoryAdmissionFixture.Span(1),
                    "https://github.com/Synthetic/RuntimeBackupPhase.git"));
            await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
            fixture.Execute("""
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
                UPDATE local_repository_reconciliation_state
                SET last_discovered_span_id=(SELECT MAX(id) FROM monitor_spans),
                    updated_at='2026-08-01T00:00:00.0000000+00:00'
                WHERE projector_key='local-repository-catalog-v1';
                """);
            Assert.Equal("completed", fixture.ScalarText(
                "SELECT state FROM local_repository_reconciliation_queue;"));
            Assert.True(fixture.ScalarLong(
                "SELECT COUNT(*) FROM session_repository_assignment_history WHERE cause_kind='source_reconciliation';") > 0);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static void CopyCurrentCatalog(
        string source,
        string target,
        RestoreTemp temp,
        bool overwrite = false)
    {
        _ = temp.CanonicalDatabaseHash(source);
        File.Copy(source, target, overwrite);
        _ = temp.CanonicalDatabaseHash(target);
    }

    private static string ReadOwnedStagePath(RestoreTemp temp)
    {
        using var journal = JsonDocument.Parse(
            File.ReadAllBytes(temp.Target + ".runtime-restore-journal.json"));
        Assert.Equal(
            "runtime-restore-journal.v2",
            journal.RootElement.GetProperty("schema_version").GetString());
        var fileName = Assert.IsType<string>(
            journal.RootElement.GetProperty("stage_file_name").GetString());
        Assert.Equal(Path.GetFileName(fileName), fileName);
        Assert.StartsWith(".runtime-restore-stage-", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".sqlite", fileName, StringComparison.Ordinal);
        var path = Path.Combine(temp.Root, fileName);
        Assert.True(File.Exists(path));
        return path;
    }

    private static int CorruptCatalogSemantic(string path, string mutation)
    {
        var table = mutation switch
        {
            "queue" => "local_repository_reconciliation_queue",
            "history" => "session_repository_assignment_history",
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
        };
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=OFF;";
            foreignKeys.ExecuteNonQuery();
        }
        var triggers = new List<(string Name, string Sql)>();
        using (var triggerCommand = connection.CreateCommand())
        {
            triggerCommand.CommandText = """
                SELECT name,sql
                FROM sqlite_schema
                WHERE type='trigger' AND tbl_name=$table COLLATE BINARY
                ORDER BY name;
                """;
            triggerCommand.Parameters.AddWithValue("$table", table);
            using var reader = triggerCommand.ExecuteReader();
            while (reader.Read())
                triggers.Add((reader.GetString(0), reader.GetString(1)));
        }

        int affected;
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            using (var ignoreChecks = connection.CreateCommand())
            {
                ignoreChecks.Transaction = transaction;
                ignoreChecks.CommandText = "PRAGMA ignore_check_constraints=ON;";
                ignoreChecks.ExecuteNonQuery();
            }
            foreach (var trigger in triggers)
            {
                using var drop = connection.CreateCommand();
                drop.Transaction = transaction;
                drop.CommandText = $"DROP TRIGGER \"{trigger.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
                drop.ExecuteNonQuery();
            }
            using (var mutationCommand = connection.CreateCommand())
            {
                mutationCommand.Transaction = transaction;
                mutationCommand.CommandText = mutation switch
                {
                    "queue" => """
                        UPDATE local_repository_reconciliation_queue
                        SET attempt_count=0
                        WHERE state='completed';
                        """,
                    "history" => """
                        UPDATE session_repository_assignment_history
                        SET new_assignment_state_sha256=$invalid
                        WHERE cause_kind='source_reconciliation';
                        """,
                    _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null),
                };
                if (mutation == "history")
                    mutationCommand.Parameters.AddWithValue("$invalid", new string('f', 64));
                affected = mutationCommand.ExecuteNonQuery();
            }
            foreach (var trigger in triggers)
            {
                using var restore = connection.CreateCommand();
                restore.Transaction = transaction;
                restore.CommandText = trigger.Sql;
                restore.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        using (var journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=DELETE;";
            _ = journal.ExecuteScalar();
        }
        return affected;
    }

    private static int CorruptAndReplaceInstalledCatalog(string target, string mutation)
    {
        var scratch = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".catalog-installed-mutation-{Guid.NewGuid():N}.sqlite");
        var displaced = Path.Combine(
            Path.GetDirectoryName(target)!,
            $".catalog-installed-displaced-{Guid.NewGuid():N}.sqlite");
        try
        {
            File.Copy(target, scratch);
            var affected = CorruptCatalogSemantic(scratch, mutation);
            File.Replace(scratch, target, displaced, ignoreMetadataErrors: true);
            File.Delete(displaced);
            return affected;
        }
        finally
        {
            foreach (var path in new[]
            {
                scratch,
                scratch + "-journal",
                scratch + "-wal",
                scratch + "-shm",
                displaced,
                displaced + "-journal",
                displaced + "-wal",
                displaced + "-shm",
            })
                if (File.Exists(path))
                    File.Delete(path);
        }
    }

    private static void AssertNoPublishedPreRestore(string root)
    {
        var directory = Path.Combine(root, "runtime-backups");
        if (Directory.Exists(directory))
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
    }

    private static IReadOnlyDictionary<string, byte[]> CaptureDatabaseFiles(string databasePath) =>
        new[] { databasePath, databasePath + "-journal", databasePath + "-wal", databasePath + "-shm" }
            .Where(File.Exists)
            .ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);

    private static void AssertDatabaseFilesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), actual.Keys.Order(StringComparer.Ordinal));
        foreach (var name in expected.Keys)
            Assert.Equal(expected[name], actual[name]);
    }

    private sealed class RestoreTemp : IDisposable
    {
        internal const string MarkerTraceId = "11111111111111111111111111111111";
        internal RestoreTemp()
        {
            Root = Path.Combine(Path.GetTempPath(), $"runtime-restore-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
            Source = Path.Combine(Root, "source.db");
            Target = Path.Combine(Root, "target.db");
            Clock = new FixedTimeProvider(new DateTimeOffset(2026, 7, 23, 2, 3, 4, TimeSpan.Zero));
        }

        internal string Root { get; }
        internal string Source { get; }
        internal string Target { get; }
        internal TimeProvider Clock { get; }

        internal void CreateDatabase(string path, string value, bool includeRaw)
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using (var transaction = connection.BeginTransaction()) { MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction); transaction.Commit(); }
            using (var transaction = connection.BeginTransaction()) { RetentionSchemaMigrator.Apply(connection, transaction); transaction.Commit(); }
            Execute(connection, $"UPDATE retention_store_instances SET store_instance_id='{new string('2', 32)}' WHERE id=1;");
            Execute(connection, "CREATE TABLE runtime_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL);");
            using (var insert = connection.CreateCommand()) { insert.CommandText = "INSERT INTO runtime_probe(id,value) VALUES(1,$value);"; insert.Parameters.AddWithValue("$value", value); insert.ExecuteNonQuery(); }
            if (!includeRaw) return;
            var token = SHA256.HashData([7]);
            using (var raw = connection.CreateCommand()) { raw.CommandText = "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token) VALUES(1,'raw-otlp',NULL,'2026-01-01T00:00:00.0000000+00:00','{}','{\"secret\":\"private\"}',1,$token);"; raw.Parameters.AddWithValue("$token", token); raw.ExecuteNonQuery(); }
            var receipt = RetentionOwnershipReceipt.CreateRawRecord(new(
                new string('2', 32), 1, "2026-01-01T00:00:00.0000000+00:00",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime.Ticks, 1, token));
            using var item = connection.CreateCommand();
            item.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES($id,$store,'raw_record','1',1,$receipt,'2026-01-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','raw-default-90d',1,'expiring',1,1);";
            item.Parameters.AddWithValue("$id", new string('a', 32)); item.Parameters.AddWithValue("$store", new string('2', 32)); item.Parameters.AddWithValue("$receipt", receipt); item.ExecuteNonQuery();
        }

        internal void CreateCurrentMarkerDatabase(string path)
        {
            CreateDatabase(path, "marker", includeRaw: true);
            new RetentionCatalogStore(
                    RetentionCatalogContext.AdoptExistingCatalogV1(path),
                    Clock)
                .CreateSchema();
            var input = new SkillProjectionFrontierInput(
                1,
                1,
                SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10,
                null);
            var frontier = SkillProjectionHashing.FrontierDigest(MarkerTraceId, [input]);
            var request = SourceCompatibilityReconciliationRequest.Create(
                "marker-backup-operation",
                1,
                MarkerTraceId,
                0,
                SourceCompatibilityReconciliationTrigger.DecoderRevision,
                "resolver-2",
                "registry-1",
                SkillProjectionGenerationParticipant.CurrentProjectorVersion);
            var fingerprint = SkillProjectionHashing.ReconciliationFingerprint(request, input);
            using (var connection = Open(path))
            using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    INSERT INTO source_schema_observations(
                        id,observation_id,raw_record_id,raw_payload_sha256,
                        input_evidence_kind,ingest_batch_id,source_surface,
                        source_application_version,source_adapter,adapter_version,
                        schema_fingerprint,inventory_hash,compatibility_state,
                        reason_code,next_action,capture_content_state,unknown_span_count,
                        unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                        overflow_occurrence_count,observed_at)
                    VALUES(
                        1,'marker-observation',1,NULL,'deleted_before_digest_v10',
                        'marker-batch','github-copilot-cli','1.0.74','github-copilot-otel',
                        'adapter-1',NULL,NULL,'supported',NULL,'none','available',
                        0,0,0,0,0,'2026-07-31T00:00:00.0000000+00:00');
                    INSERT INTO source_trace_version_observations(
                        source_observation_id,trace_id,resolution_state,
                        source_application_version)
                    VALUES(1,$trace_id,'resolved','1.0.74');
                    INSERT INTO source_trace_compatibility_revisions(
                        trace_id,current_revision,current_effective_state,
                        current_exact_version,updated_at)
                    VALUES(
                        $trace_id,0,'resolved','1.0.74',
                        '2026-07-31T00:00:00.0000000+00:00');
                    INSERT INTO skill_projection_generations(
                        generation_id,trace_id,compatibility_revision,
                        input_frontier_sha256,projector_version,lifecycle,
                        created_at,updated_at)
                    VALUES(
                        1,$trace_id,0,$frontier,'skill-projector-1','input_unavailable',
                        '2026-07-31T00:00:00.0000000+00:00',
                        '2026-07-31T00:00:00.0000000+00:00');
                    INSERT INTO skill_projection_generation_inputs(
                        generation_id,input_ordinal,source_observation_id,raw_record_id,
                        input_evidence_kind,raw_payload_sha256)
                    VALUES(1,0,1,1,'deleted_before_digest_v10',NULL);
                    INSERT INTO skill_projection_trace_heads(
                        trace_id,desired_generation_id,current_generation_id,updated_at)
                    VALUES(
                        $trace_id,1,NULL,'2026-07-31T00:00:00.0000000+00:00');
                    INSERT INTO skill_projection_queue(
                        generation_id,trace_id,compatibility_revision,
                        input_frontier_sha256,projector_version,state,attempt_count,
                        lease_generation,error_code)
                    VALUES(
                        1,$trace_id,0,$frontier,'skill-projector-1','input_unavailable',
                        0,0,'skill_projection_input_unavailable');
                    INSERT INTO source_compatibility_reconciliation_receipts(
                        operation_key,request_fingerprint,source_observation_id,trace_id,
                        expected_interpretation_revision,raw_record_id,input_evidence_kind,
                        raw_payload_sha256,resolver_revision,registry_revision,
                        projector_version,outcome,resulting_supersession_id,
                        resulting_interpretation_revision,resulting_compatibility_revision,
                        resulting_generation_id,created_at)
                    VALUES(
                        'marker-backup-operation',$fingerprint,1,$trace_id,0,1,
                        'deleted_before_digest_v10',NULL,'resolver-2','registry-1',
                        'skill-projector-1','input_unavailable',NULL,0,NULL,NULL,
                        '2026-07-31T00:00:00.0000000+00:00');
                    INSERT INTO skill_projection_operation_receipts(
                        operation_key,semantic_fingerprint,outcome,generation_id,created_at)
                    VALUES(
                        'marker-backup-operation',$fingerprint,'input_unavailable',NULL,
                        '2026-07-31T00:00:00.0000000+00:00');
                    """;
                command.Parameters.AddWithValue("$trace_id", MarkerTraceId);
                command.Parameters.AddWithValue("$frontier", frontier);
                command.Parameters.AddWithValue("$fingerprint", fingerprint);
                command.ExecuteNonQuery();
            }
            DeleteRawAndTombstone(path);
        }

        internal void CreateResolvedSkillProjectionDatabase(string path)
        {
            CreateDatabase(path, "resolved-skill", includeRaw: false);
            new SqliteSourceCompatibilityStore(path).CreateSchema();
            new SqliteIngestionCommitStore(path).Commit(
                CreateResolvedSkillProjectionBatch(
                    "resolved-skill-batch",
                    Clock.GetUtcNow()));
        }

        internal void CreateCompleteDesiredFrontierDatabase(
            string path,
            bool marker,
            int observationCount = 2)
        {
            if (observationCount is < 1 or > 2)
                throw new ArgumentOutOfRangeException(nameof(observationCount));
            if (marker)
            {
                CreateCurrentMarkerDatabase(path);
                new SqliteSourceCompatibilityStore(path).CreateSchema();
            }
            else
            {
                CreateResolvedSkillProjectionDatabase(path);
            }
            if (observationCount == 2)
            {
                new SqliteIngestionCommitStore(path).Commit(
                    CreateResolvedSkillProjectionBatch(
                        marker ? "marker-successor" : "resolved-skill-successor",
                        Clock.GetUtcNow().AddSeconds(1)));
            }
            using var validation = Open(path);
            SourceCompatibilitySchemaV11.Validate(validation, transaction: null);
            SkillProjectionSchemaV1.Validate(validation, transaction: null);
        }

        internal void CreatePublishedSupersededSkillProjectionDatabase(string path)
        {
            CreateDatabase(path, "published-superseded", includeRaw: false);
            new SqliteSourceCompatibilityStore(path).CreateSchema();
            new SqliteIngestionCommitStore(path).Commit(
                CreateResolvedSkillProjectionBatch(
                    "published-superseded-first",
                    Clock.GetUtcNow(),
                    PublishedSkillPayload));
            var retention = RetentionCatalogContext.AdoptExistingCatalogV1(path);
            var operationAt = Clock.GetUtcNow().AddSeconds(1);
            var operationClock = new FixedTimeProvider(operationAt);
            var worker = new SkillProjectionWorker(
                new SqliteSkillProjectionStore(
                    path,
                    new RawTelemetryStore(path, retention, operationClock)),
                timeProvider: operationClock);
            Assert.Equal(
                SkillProjectionWorkOutcome.Published,
                worker.RunNextAsync(operationAt).GetAwaiter().GetResult());
            new SqliteIngestionCommitStore(path).Commit(
                CreateResolvedSkillProjectionBatch(
                    "published-superseded-successor",
                    Clock.GetUtcNow().AddSeconds(2)));
            using var validation = Open(path);
            SourceCompatibilitySchemaV11.Validate(validation, transaction: null);
            SkillProjectionSchemaV1.Validate(validation, transaction: null);
        }

        internal SessionVersion13TestFixture.Version13RetentionBackedDiscriminator CreatePinnedSessionVersion13Database(
            string path,
            string identity)
        {
            CreatePublishedSupersededSkillProjectionDatabase(path);
            return SessionVersion13TestFixture.CreateRetentionBackedDiscriminator(
                path,
                RetentionCatalogContext.AdoptExistingCatalogV1(path),
                Clock.GetUtcNow().AddDays(-91),
                retainedByPolicy: true,
                includeInstalledSkillDescendants: true,
                identity);
        }

        internal void AssertSessionBoundSkillDescendants(
            SqliteConnection connection,
            string sessionId)
        {
            Assert.Equal(1L, Scalar<long>(
                connection,
                $"SELECT COUNT(*) FROM skill_projection_invocations WHERE session_id='{sessionId}';"));
            Assert.Equal(
                "synthetic-skill|synthetic-source|synthetic-trigger|1.0.74",
                Scalar<string>(
                    connection,
                    $"""
                    SELECT skill_name || '|' || skill_source || '|' || invocation_trigger || '|' || source_application_version
                    FROM skill_projection_invocations
                    WHERE session_id='{sessionId}';
                    """));
            Assert.Equal(1L, Scalar<long>(
                connection,
                $"SELECT COUNT(*) FROM skill_projection_inventories WHERE session_id='{sessionId}';"));
            Assert.Equal(
                "1|1|0|synthetic-skill",
                Scalar<string>(
                    connection,
                    $"""
                    SELECT inventory.observed_name_count || '|' || inventory.retained_name_count || '|' ||
                           inventory.names_truncated || '|' || name.skill_name
                    FROM skill_projection_inventories AS inventory
                    JOIN skill_projection_inventory_names AS name
                      ON name.inventory_id=inventory.inventory_id
                    WHERE inventory.session_id='{sessionId}';
                    """));
            Assert.Equal(0L, Scalar<long>(
                connection,
                $"SELECT COUNT(*) FROM skill_projection_sdk_claims WHERE session_id='{sessionId}';"));
        }

        internal string CreateVersion13SessionArchive(string currentArchive, string outputFileName)
        {
            var output = Path.Combine(Root, outputFileName);
            byte[] manifest;
            byte[] database;
            using (var archive = ZipFile.OpenRead(currentArchive))
            {
                manifest = Read(archive.GetEntry("manifest.json")!);
                database = Read(archive.GetEntry("database.sqlite")!);
            }

            var mutatedPath = Path.Combine(Root, $".session-v13-{Guid.NewGuid():N}.sqlite");
            File.WriteAllBytes(mutatedPath, database);
            using (var connection = Open(mutatedPath))
            {
                Execute(connection, "DELETE FROM schema_version WHERE component='local_archive'; DROP TABLE local_archive_events; DROP TABLE local_archive_current;");
                // A Session 13 backup cannot legitimately declare skill_invocation_snapshot: the
                // component's conditional trigger registry is parented to Session 14 exactly, so a
                // downgraded archive that kept it would be incompatible rather than legacy.
                Execute(
                    connection,
                    "DELETE FROM schema_version WHERE component IN ('skill_invocation_snapshot','local_workspace_projection');"
                    + "DROP TRIGGER IF EXISTS skill_invocation_snapshot_session_event_update_rejected;"
                    + "DROP TRIGGER IF EXISTS skill_invocation_snapshot_session_event_delete_rejected;"
                    + "DROP TABLE IF EXISTS skill_invocation_snapshot_receipts;"
                    + "DROP TABLE IF EXISTS skill_invocation_snapshots;"
                    + "DROP TABLE IF EXISTS local_workspace_subagent_lifecycle;"
                    + "DROP TABLE IF EXISTS local_workspace_skill_metadata;"
                    + "DROP TABLE IF EXISTS local_workspace_tool_metadata;"
                    + "DROP TABLE IF EXISTS local_workspace_node_source_references;"
                    + "DROP TABLE IF EXISTS local_workspace_semantic_receipts;"
                    + "DROP TABLE IF EXISTS local_workspace_node_content_refs;"
                    + "DROP TABLE IF EXISTS local_workspace_content_tombstones;"
                    + "DROP TABLE IF EXISTS local_workspace_node_edges;"
                    + "DROP TABLE IF EXISTS local_workspace_nodes;"
                    + "DROP TABLE IF EXISTS local_workspace_execution_headers;"
                    + "DROP TABLE IF EXISTS local_workspace_session_sources;"
                    + "DROP TABLE IF EXISTS local_workspace_session_models;"
                    + "DROP TABLE IF EXISTS local_workspace_session_activity;"
                    + "DROP TABLE IF EXISTS local_workspace_token_observations;DROP TABLE IF EXISTS local_workspace_span_facts;"
                    + "DROP TABLE IF EXISTS local_workspace_session_search_facts;"
                    + "DROP TABLE IF EXISTS local_workspace_projection_state;"
                    + "DROP TABLE IF EXISTS local_workspace_sessions;");
                SessionVersion13TestFixture.DowngradeSessionEvents(connection);
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            var parsed = RuntimeBackupJson.ParseManifest(manifest);
            var rowCounts = new Dictionary<string, long>(StringComparer.Ordinal);
            using (var connection = Open(mutatedPath))
            {
                foreach (var table in parsed.RowCounts.Keys.Where(static table => table is not (
                    "local_archive_current"
                    or "local_archive_events"
                    or "skill_invocation_snapshots"
                    or "skill_invocation_snapshot_receipts") && !table.StartsWith("local_workspace_", StringComparison.Ordinal)))
                    rowCounts[table] = Scalar<long>(connection, $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";");
            }
            database = File.ReadAllBytes(mutatedPath);
            File.Delete(mutatedPath);
            File.Delete(mutatedPath + "-wal");
            File.Delete(mutatedPath + "-shm");

            var componentVersions = parsed.ComponentVersions.ToDictionary(
                static item => item.Key,
                static item => item.Value,
                StringComparer.Ordinal);
            componentVersions["session"] = 13;
            componentVersions.Remove("local_archive");
            componentVersions.Remove("skill_invocation_snapshot");
            componentVersions.Remove("local_workspace_projection");
            var databaseHash = Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant();
            manifest = RuntimeBackupJson.WriteManifest(parsed with
            {
                DatabaseSha256 = databaseHash,
                DatabaseSize = database.LongLength,
                ComponentVersions = componentVersions,
                RowCounts = rowCounts,
            });
            using var target = ZipFile.Open(output, ZipArchiveMode.Create);
            Write(target, "manifest.json", manifest);
            Write(target, "database.sqlite", database);
            return output;
        }

        internal string SnapshotOwnedRows(string path, params string[] tablePrefixes)
        {
            using var connection = Open(path);
            return SnapshotOwnedRows(connection, tablePrefixes);
        }

        internal string SnapshotOwnedRows(SqliteConnection connection, params string[] tablePrefixes)
        {
            var tables = new List<string>();
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM pragma_table_list WHERE schema='main' AND type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                using var reader = list.ExecuteReader();
                while (reader.Read())
                {
                    var table = reader.GetString(0);
                    if (tablePrefixes.Any(prefix => table.StartsWith(prefix, StringComparison.Ordinal)))
                        tables.Add(table);
                }
            }

            var lines = new List<string>();
            foreach (var table in tables)
            {
                var columns = new List<string>();
                using (var columnCommand = connection.CreateCommand())
                {
                    columnCommand.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden=0 ORDER BY cid;";
                    columnCommand.Parameters.AddWithValue("$table", table);
                    using var columnReader = columnCommand.ExecuteReader();
                    while (columnReader.Read()) columns.Add(columnReader.GetString(0));
                }
                var projection = string.Join(',', columns.Select(QuoteIdentifier));
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {projection} FROM {QuoteIdentifier(table)} ORDER BY {projection};";
                using var reader = command.ExecuteReader();
                lines.Add($"table|{table}|{string.Join('|', columns)}");
                while (reader.Read())
                {
                    lines.Add($"row|{table}|{string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index =>
                        reader.IsDBNull(index)
                            ? "<null>"
                            : reader.GetValue(index) is byte[] bytes
                                ? $"blob:{Convert.ToHexString(bytes)}"
                                : $"{reader.GetFieldType(index).Name}:{Convert.ToString(reader.GetValue(index), System.Globalization.CultureInfo.InvariantCulture)}"))}");
                }
            }
            return string.Join('\n', lines) + "\n";
        }

        private static string QuoteIdentifier(string identifier) =>
            $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

        internal void CreateMarkerRegistrySupersessionDatabase(string path)
        {
            CreateCurrentMarkerDatabase(path);
            using (var connection = Open(path))
            {
                Execute(
                    connection,
                    """
                    DROP TRIGGER source_trace_version_observations_update_rejected;
                    DROP TRIGGER source_compatibility_reconciliation_receipts_delete_rejected;
                    DROP TRIGGER skill_projection_operation_receipts_delete_rejected;
                    UPDATE source_trace_version_observations
                    SET resolution_state='unrecognised';
                    UPDATE source_trace_compatibility_revisions
                    SET current_effective_state='unrecognised';
                    DELETE FROM source_compatibility_reconciliation_receipts;
                    DELETE FROM skill_projection_operation_receipts;
                    DELETE FROM skill_projection_trace_heads;
                    DELETE FROM skill_projection_queue;
                    DELETE FROM skill_projection_generation_inputs;
                    DELETE FROM skill_projection_generations;
                    CREATE TRIGGER source_trace_version_observations_update_rejected
                    BEFORE UPDATE ON source_trace_version_observations
                    BEGIN SELECT RAISE(ABORT,'source_trace_version_observation_immutable'); END;
                    CREATE TRIGGER source_compatibility_reconciliation_receipts_delete_rejected
                    BEFORE DELETE ON source_compatibility_reconciliation_receipts
                    BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;
                    CREATE TRIGGER skill_projection_operation_receipts_delete_rejected
                    BEFORE DELETE ON skill_projection_operation_receipts
                    BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;
                    """);
            }
            var registry = VerifiedSourceFingerprintRegistry.Create(
            [
                VerifiedSourceFingerprintEvidence.Create(
                    "github-copilot-cli",
                    "1.0.74",
                    new string('a', 64)),
            ],
            [],
            []);
            var reconciler = new SourceCompatibilityReconciler(
                path,
                SourceCompatibilityReconciliationAuthority.Create(
                [
                    new SourceCompatibilityAcceptedRevision(
                        "resolver-2",
                        "registry-2",
                        registry),
                ]),
                Clock);
            var result = reconciler.Reconcile(
                SourceCompatibilityReconciliationRequest.Create(
                    "marker-registry-change",
                    1,
                    MarkerTraceId,
                    0,
                    SourceCompatibilityReconciliationTrigger.RegistryRevision,
                    "resolver-2",
                    "registry-2",
                    SkillProjectionGenerationParticipant.CurrentProjectorVersion));
            Assert.Equal(SourceCompatibilityReconciliationOutcome.Changed, result.Outcome);
            using var validation = Open(path);
            SourceCompatibilitySchemaV11.Validate(validation, transaction: null);
            SkillProjectionSchemaV1.Validate(validation, transaction: null);
        }

        internal void CreatePayloadInputUnavailableDatabase(
            string path,
            bool withSuccessor)
        {
            CreateResolvedSkillProjectionDatabase(path);
            var at = Clock.GetUtcNow();
            var operationAt = at.AddSeconds(2);
            var operationClock = new FixedTimeProvider(operationAt);
            using (var connection = Open(path))
            using (var deny = connection.CreateCommand())
            {
                deny.CommandText =
                    """
                    UPDATE retention_items
                    SET state='expired_pending_deletion',
                        read_denied_at=$at,
                        queued_at=$at,
                        revision=revision+1
                    WHERE store_kind='raw_record' AND source_item_id='1';
                    """;
                deny.Parameters.AddWithValue("$at", at.AddSeconds(1).ToString("O"));
                Assert.Equal(1, deny.ExecuteNonQuery());
            }
            var retention = RetentionCatalogContext.AdoptExistingCatalogV1(path);
            var worker = new SkillProjectionWorker(
                new SqliteSkillProjectionStore(
                    path,
                    new RawTelemetryStore(path, retention, operationClock)),
                timeProvider: operationClock);
            Assert.Equal(
                SkillProjectionWorkOutcome.InputUnavailable,
                worker.RunNextAsync(operationAt).GetAwaiter().GetResult());
            if (withSuccessor)
            {
                new SqliteIngestionCommitStore(path).Commit(
                    CreateResolvedSkillProjectionBatch(
                        "resolved-skill-successor",
                        at.AddSeconds(3)));
            }
            using var validation = Open(path);
            SourceCompatibilitySchemaV11.Validate(validation, transaction: null);
            SkillProjectionSchemaV1.Validate(validation, transaction: null);
        }

        private static ValidatedIngestionBatch CreateResolvedSkillProjectionBatch(
            string batchId,
            DateTimeOffset at,
            string payload = "{}")
        {
            var inventory = OtlpJsonStructuralWalker.Build(payload, at);
            var decision = SourceCompatibilityEvaluator.Assess(
                "github-copilot-cli",
                "1.0.74",
                inventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], []));
            var observation = SourceObservationBatchDraft.Create(
                batchId,
                "github-copilot-cli",
                "1.0.74",
                "github-copilot-otel",
                "adapter-1",
                inventory,
                decision,
                SourceCaptureContentState.Available,
                at,
                [TraceSourceVersionResolutionDraft.Create(
                    MarkerTraceId,
                    TraceSourceVersionResolutionState.Resolved,
                    "1.0.74")]);
            return ValidatedIngestionBatch.Create(
                new RawTelemetryRecord(
                    null,
                    RawTelemetrySources.RawOtlp,
                    MarkerTraceId,
                    at,
                    ResourceAttributesJson: null,
                    PayloadJson: payload),
                observation);
        }

        private static readonly string PublishedSkillPayload =
            """
            {"resourceSpans":[{
              "resource":{"attributes":[
                {"key":"service.version","value":{"stringValue":"1.0.74"}},
                {"key":"client.kind","value":{"stringValue":"copilot-cli"}}
              ]},
              "scopeSpans":[{"spans":[{
                "traceId":"TRACE_ID",
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
            """
            .Replace("TRACE_ID", MarkerTraceId, StringComparison.Ordinal);

        internal void ApplySkillProjectionContradiction(
            string path,
            string contradiction)
        {
            using var connection = Open(path);
            var desiredGenerationId = Scalar<long>(
                connection,
                "SELECT desired_generation_id FROM skill_projection_trace_heads;");
            if (contradiction == "older-same-revision-current")
            {
                var olderGenerationId = Scalar<long>(
                    connection,
                    "SELECT MIN(generation_id) FROM skill_projection_generations;");
                Execute(
                    connection,
                    $"""
                    UPDATE skill_projection_generations
                    SET lifecycle=CASE
                        WHEN generation_id={olderGenerationId} THEN 'current'
                        ELSE 'superseded'
                    END;
                    UPDATE skill_projection_queue
                    SET state=CASE
                            WHEN generation_id={olderGenerationId} THEN 'completed'
                            ELSE 'superseded'
                        END,
                        attempt_count=CASE
                            WHEN generation_id={olderGenerationId} THEN 1
                            ELSE attempt_count
                        END,
                        lease_generation=CASE
                            WHEN generation_id={olderGenerationId} THEN 1
                            ELSE lease_generation
                        END,
                        lease_owner=NULL,
                        lease_expires_at=NULL,
                        next_attempt_at=NULL,
                        error_code=NULL;
                    UPDATE skill_projection_trace_heads
                    SET desired_generation_id={olderGenerationId},
                        current_generation_id={olderGenerationId};
                    """);
            }
            else if (contradiction == "recomputed-desired-subset")
            {
                KeepOnlyNewestPayloadInput(connection, desiredGenerationId);
            }
            else if (contradiction == "marker-omitted-current-projection")
            {
                var payloadInput = KeepOnlyNewestPayloadInput(
                    connection,
                    desiredGenerationId);
                Execute(
                    connection,
                    $"""
                    DELETE FROM skill_projection_queue
                    WHERE generation_id<>{desiredGenerationId};
                    DELETE FROM skill_projection_generation_inputs
                    WHERE generation_id<>{desiredGenerationId};
                    DELETE FROM skill_projection_generations
                    WHERE generation_id<>{desiredGenerationId};
                    UPDATE skill_projection_generations
                    SET lifecycle='current'
                    WHERE generation_id={desiredGenerationId};
                    UPDATE skill_projection_queue
                    SET state='completed',attempt_count=1,lease_generation=1,
                        lease_owner=NULL,lease_expires_at=NULL,next_attempt_at=NULL,
                        error_code=NULL
                    WHERE generation_id={desiredGenerationId};
                    UPDATE skill_projection_trace_heads
                    SET current_generation_id={desiredGenerationId};
                    """);
                AddProjectedRows(
                    connection,
                    desiredGenerationId,
                    payloadInput.RawRecordId);
            }
            else if (contradiction == "pending-projected-rows")
            {
                AddProjectedRows(
                    connection,
                    desiredGenerationId,
                    Scalar<long>(
                        connection,
                        $"SELECT raw_record_id FROM skill_projection_generation_inputs WHERE generation_id={desiredGenerationId} LIMIT 1;"));
            }
            else if (contradiction == "superseded-unpublished-projected-rows")
            {
                var supersededGenerationId = Scalar<long>(
                    connection,
                    "SELECT MIN(generation_id) FROM skill_projection_generations;");
                AddProjectedRows(
                    connection,
                    supersededGenerationId,
                    Scalar<long>(
                        connection,
                        $"SELECT raw_record_id FROM skill_projection_generation_inputs WHERE generation_id={supersededGenerationId} LIMIT 1;"));
            }
            else if (contradiction == "unequal-queue-counters")
            {
                Execute(
                    connection,
                    "UPDATE skill_projection_queue SET attempt_count=1,lease_generation=2;");
            }
            else if (contradiction == "completed-zero-counters")
            {
                Execute(
                    connection,
                    $"""
                    UPDATE skill_projection_generations SET lifecycle='current';
                    UPDATE skill_projection_queue
                    SET state='completed',attempt_count=0,lease_generation=0,
                        lease_owner=NULL,lease_expires_at=NULL,next_attempt_at=NULL,
                        error_code=NULL;
                    UPDATE skill_projection_trace_heads
                    SET current_generation_id={desiredGenerationId};
                    """);
            }
            else if (contradiction == "retry-pending-without-retry-fields")
            {
                Execute(
                    connection,
                    """
                    UPDATE skill_projection_generations SET lifecycle='retry_pending';
                    UPDATE skill_projection_queue
                    SET state='pending',attempt_count=1,lease_generation=1,
                        lease_owner=NULL,lease_expires_at=NULL,next_attempt_at=NULL,
                        error_code=NULL;
                    """);
            }
            else if (contradiction == "unsanitized-otel-skill-value")
            {
                var updateTrigger = Assert.Single(
                    SkillProjectionSchemaV1.TriggerDefinitions,
                    trigger => trigger.Name == "skill_projection_invocations_update_rejected");
                Execute(connection, $"DROP TRIGGER {updateTrigger.Name};");
                using (var command = connection.CreateCommand())
                {
                    command.CommandText =
                        "UPDATE skill_projection_invocations SET skill_name=$unsafe;";
                    command.Parameters.AddWithValue("$unsafe", @"C:\synthetic\SKILL.md");
                    Assert.True(command.ExecuteNonQuery() >= 1);
                }
                Execute(connection, updateTrigger.Sql);
            }
            else if (contradiction == "coherent-negative-generated-identities")
            {
                var affectedTables = new HashSet<string>(StringComparer.Ordinal)
                {
                    "skill_projection_operation_receipts",
                    "skill_projection_invocations",
                    "skill_projection_inventories",
                    "skill_projection_inventory_names",
                };
                var updateTriggers = SkillProjectionSchemaV1.TriggerDefinitions
                    .Where(trigger => affectedTables.Contains(trigger.Table)
                        && trigger.Name.EndsWith("_update_rejected", StringComparison.Ordinal))
                    .ToArray();
                foreach (var trigger in updateTriggers)
                    Execute(connection, $"DROP TRIGGER {trigger.Name};");
                Execute(
                    connection,
                    """
                    PRAGMA foreign_keys=OFF;
                    PRAGMA ignore_check_constraints=ON;
                    UPDATE skill_projection_inventory_names
                    SET inventory_id=-inventory_id;
                    UPDATE skill_projection_inventories
                    SET inventory_id=-inventory_id,generation_id=-generation_id;
                    UPDATE skill_projection_invocations
                    SET invocation_id=-invocation_id,generation_id=-generation_id;
                    UPDATE skill_projection_generation_inputs
                    SET generation_id=-generation_id;
                    UPDATE skill_projection_trace_heads
                    SET desired_generation_id=CASE
                            WHEN desired_generation_id IS NULL THEN NULL
                            ELSE -desired_generation_id
                        END,
                        current_generation_id=CASE
                            WHEN current_generation_id IS NULL THEN NULL
                            ELSE -current_generation_id
                        END;
                    UPDATE skill_projection_queue
                    SET generation_id=-generation_id;
                    UPDATE skill_projection_operation_receipts
                    SET generation_id=CASE
                        WHEN generation_id IS NULL THEN NULL
                        ELSE -generation_id
                    END;
                    UPDATE skill_projection_generations
                    SET generation_id=-generation_id;
                    PRAGMA ignore_check_constraints=OFF;
                    PRAGMA foreign_keys=ON;
                    """);
                foreach (var trigger in updateTriggers)
                    Execute(connection, trigger.Sql);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(contradiction));
            }
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        private static SkillProjectionFrontierInput KeepOnlyNewestPayloadInput(
            SqliteConnection connection,
            long generationId)
        {
            using var select = connection.CreateCommand();
            select.CommandText =
                $"""
                SELECT source_observation_id,raw_record_id,raw_payload_sha256
                FROM skill_projection_generation_inputs
                WHERE generation_id={generationId}
                  AND input_evidence_kind='payload_sha256'
                ORDER BY raw_record_id DESC,source_observation_id DESC
                LIMIT 1;
                """;
            using var reader = select.ExecuteReader();
            Assert.True(reader.Read());
            var input = new SkillProjectionFrontierInput(
                reader.GetInt64(0),
                reader.GetInt64(1),
                SkillProjectionInputEvidenceKind.PayloadSha256,
                reader.GetString(2));
            reader.Close();
            var frontier = SkillProjectionHashing.FrontierDigest(
                MarkerTraceId,
                [input]);
            using var update = connection.CreateCommand();
            update.CommandText =
                """
                DELETE FROM skill_projection_generation_inputs
                WHERE generation_id=$generation_id;
                INSERT INTO skill_projection_generation_inputs(
                    generation_id,input_ordinal,source_observation_id,raw_record_id,
                    input_evidence_kind,raw_payload_sha256)
                VALUES(
                    $generation_id,0,$source_observation_id,$raw_record_id,
                    'payload_sha256',$raw_payload_sha256);
                UPDATE skill_projection_generations
                SET input_frontier_sha256=$frontier
                WHERE generation_id=$generation_id;
                UPDATE skill_projection_queue
                SET input_frontier_sha256=$frontier
                WHERE generation_id=$generation_id;
                """;
            update.Parameters.AddWithValue("$generation_id", generationId);
            update.Parameters.AddWithValue("$source_observation_id", input.SourceObservationId);
            update.Parameters.AddWithValue("$raw_record_id", input.RawRecordId);
            update.Parameters.AddWithValue("$raw_payload_sha256", input.RawPayloadSha256!);
            update.Parameters.AddWithValue("$frontier", frontier);
            update.ExecuteNonQuery();
            return input;
        }

        private void AddProjectedRows(
            SqliteConnection connection,
            long generationId,
            long rawRecordId) =>
            Execute(
                connection,
                $"""
                INSERT INTO skill_projection_invocations(
                    generation_id,source_arm,raw_record_id,trace_id,span_id,
                    span_ordinal,session_id,skill_name,skill_source,
                    invocation_trigger,source_application_version,projected_at)
                VALUES(
                    {generationId},'otel_trace_span',{rawRecordId},'{MarkerTraceId}',
                    '4444444444444444',7,NULL,'forged-skill',NULL,NULL,
                    '1.0.74','2026-07-23T02:04:00.0000000+00:00');
                INSERT INTO skill_projection_inventories(
                    generation_id,source_arm,raw_record_id,trace_id,session_id,
                    observed_name_count,retained_name_count,names_truncated,
                    source_application_version,projected_at)
                VALUES(
                    {generationId},'otel_trace_span',{rawRecordId},'{MarkerTraceId}',NULL,
                    1,1,0,'1.0.74','2026-07-23T02:04:00.0000000+00:00');
                INSERT INTO skill_projection_inventory_names(
                    inventory_id,name_ordinal,skill_name)
                VALUES(last_insert_rowid(),0,'forged-skill');
                """);

        internal void RelabelMarkerRegistrySupersessionAsDecoder(string path)
        {
            using var connection = Open(path);
            Execute(
                connection,
                """
                DROP TRIGGER source_trace_version_interpretation_supersessions_update_rejected;
                UPDATE source_trace_version_interpretation_supersessions
                SET reason='decoder_revision';
                CREATE TRIGGER source_trace_version_interpretation_supersessions_update_rejected
                BEFORE UPDATE ON source_trace_version_interpretation_supersessions
                BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;
                PRAGMA wal_checkpoint(TRUNCATE);
                """);
        }

        internal string CreateSkillProjectionStateContradictionArchive(
            string valid,
            string contradiction)
        {
            var output = Path.Combine(Root, $"skill-state-{contradiction}.zip");
            byte[] manifest;
            byte[] database;
            using (var archive = ZipFile.OpenRead(valid))
            {
                manifest = Read(archive.GetEntry("manifest.json")!);
                database = Read(archive.GetEntry("database.sqlite")!);
            }
            var mutatedPath = Path.Combine(Root, $".skill-state-{contradiction}.sqlite");
            File.WriteAllBytes(mutatedPath, database);
            ApplySkillProjectionContradiction(mutatedPath, contradiction);
            database = File.ReadAllBytes(mutatedPath);
            File.Delete(mutatedPath);
            manifest = ReplaceDatabaseHash(manifest, database);
            if (contradiction is
                "recomputed-desired-subset" or
                "marker-omitted-current-projection" or
                "pending-projected-rows" or
                "superseded-unpublished-projected-rows")
            {
                var parsed = RuntimeBackupJson.ParseManifest(manifest);
                var rows = parsed.RowCounts.ToDictionary(
                    static item => item.Key,
                    static item => item.Value,
                    StringComparer.Ordinal);
                if (contradiction == "recomputed-desired-subset")
                {
                    rows["skill_projection_generation_inputs"]--;
                }
                else if (contradiction == "marker-omitted-current-projection")
                {
                    rows["skill_projection_generations"]--;
                    rows["skill_projection_generation_inputs"] -= 2;
                    rows["skill_projection_queue"]--;
                    rows["skill_projection_invocations"]++;
                    rows["skill_projection_inventories"]++;
                    rows["skill_projection_inventory_names"]++;
                }
                else
                {
                    rows["skill_projection_invocations"]++;
                    rows["skill_projection_inventories"]++;
                    rows["skill_projection_inventory_names"]++;
                }
                manifest = RuntimeBackupJson.WriteManifest(
                    parsed with { RowCounts = rows });
            }
            using var target = ZipFile.Open(output, ZipArchiveMode.Create);
            Write(target, "manifest.json", manifest);
            Write(target, "database.sqlite", database);
            return output;
        }

        internal string CreateRetentionCoverageContradictionArchive(
            string valid,
            string contradiction)
        {
            var output = Path.Combine(Root, $"retention-{contradiction}.zip");
            byte[] manifest;
            byte[] database;
            using (var archive = ZipFile.OpenRead(valid))
            {
                manifest = Read(archive.GetEntry("manifest.json")!);
                database = Read(archive.GetEntry("database.sqlite")!);
            }
            var mutatedPath = Path.Combine(Root, $".retention-{contradiction}.sqlite");
            File.WriteAllBytes(mutatedPath, database);
            using (var connection = Open(mutatedPath))
            {
                Execute(connection, contradiction switch
                {
                    "wrong-owner" =>
                        "UPDATE retention_items SET ownership_receipt=randomblob(32);",
                    "extra-source" =>
                        "UPDATE retention_items SET source_item_id='2';",
                    "malformed-row" =>
                        "PRAGMA ignore_check_constraints=ON; UPDATE retention_items SET state='malformed'; PRAGMA ignore_check_constraints=OFF;",
                    "foreign-key" =>
                        $"PRAGMA foreign_keys=OFF; UPDATE retention_items SET store_instance_id='{new string('3', 32)}'; PRAGMA foreign_keys=ON;",
                    _ => throw new ArgumentOutOfRangeException(nameof(contradiction)),
                });
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            database = File.ReadAllBytes(mutatedPath);
            File.Delete(mutatedPath);
            manifest = ReplaceDatabaseHash(manifest, database);
            using var target = ZipFile.Open(output, ZipArchiveMode.Create);
            Write(target, "manifest.json", manifest);
            Write(target, "database.sqlite", database);
            return output;
        }

        internal string CreateMarkerSupersessionContradictionArchive(string valid)
        {
            const string contradiction = "marker-registry-labelled-decoder";
            var output = Path.Combine(Root, $"{contradiction}.zip");
            byte[] manifest;
            byte[] database;
            using (var archive = ZipFile.OpenRead(valid))
            {
                manifest = Read(archive.GetEntry("manifest.json")!);
                database = Read(archive.GetEntry("database.sqlite")!);
            }
            var mutatedPath = Path.Combine(Root, $".{contradiction}.sqlite");
            File.WriteAllBytes(mutatedPath, database);
            RelabelMarkerRegistrySupersessionAsDecoder(mutatedPath);
            database = File.ReadAllBytes(mutatedPath);
            File.Delete(mutatedPath);
            manifest = ReplaceDatabaseHash(manifest, database);
            using var target = ZipFile.Open(output, ZipArchiveMode.Create);
            Write(target, "manifest.json", manifest);
            Write(target, "database.sqlite", database);
            return output;
        }

        internal string CreateSkillProjectionContradictionArchive(
            string valid,
            string contradiction)
        {
            var output = Path.Combine(Root, $"marker-{contradiction}.zip");
            byte[] manifest;
            byte[] database;
            using (var archive = ZipFile.OpenRead(valid))
            {
                manifest = Read(archive.GetEntry("manifest.json")!);
                database = Read(archive.GetEntry("database.sqlite")!);
            }
            var mutatedPath = Path.Combine(Root, $".marker-{contradiction}.sqlite");
            File.WriteAllBytes(mutatedPath, database);
            using (var connection = Open(mutatedPath))
            {
                if (contradiction == "receipt-fingerprint")
                {
                    Execute(
                        connection,
                        $"""
                        DROP TRIGGER source_compatibility_reconciliation_receipts_update_rejected;
                        DROP TRIGGER skill_projection_operation_receipts_update_rejected;
                        UPDATE source_compatibility_reconciliation_receipts
                        SET request_fingerprint='{new string('f', 64)}';
                        UPDATE skill_projection_operation_receipts
                        SET semantic_fingerprint='{new string('f', 64)}';
                        CREATE TRIGGER source_compatibility_reconciliation_receipts_update_rejected
                        BEFORE UPDATE ON source_compatibility_reconciliation_receipts
                        BEGIN SELECT RAISE(ABORT,'source_compatibility_append_only'); END;
                        CREATE TRIGGER skill_projection_operation_receipts_update_rejected
                        BEFORE UPDATE ON skill_projection_operation_receipts
                        BEGIN SELECT RAISE(ABORT,'skill_projection_append_only'); END;
                        """);
                }
                else if (contradiction == "marker-with-digest")
                {
                    Execute(
                        connection,
                        $"""
                        DROP TRIGGER source_schema_observations_projection_input_update_rejected;
                        PRAGMA ignore_check_constraints=ON;
                        UPDATE source_schema_observations
                        SET raw_payload_sha256='{new string('a', 64)}';
                        PRAGMA ignore_check_constraints=OFF;
                        CREATE TRIGGER source_schema_observations_projection_input_update_rejected
                        BEFORE UPDATE OF input_evidence_kind,raw_payload_sha256 ON source_schema_observations
                        WHEN OLD.input_evidence_kind IS NOT NEW.input_evidence_kind
                          OR OLD.raw_payload_sha256 IS NOT NEW.raw_payload_sha256
                        BEGIN SELECT RAISE(ABORT,'source_projection_input_immutable'); END;
                        """);
                }
                else if (contradiction == "marker-with-raw")
                {
                    Execute(
                        connection,
                        $$"""
                        INSERT INTO raw_records(
                            id,source,trace_id,received_at,resource_attributes_json,
                            payload_json,schema_version,retention_owner_token)
                        VALUES(
                            1,'raw-otlp','{{MarkerTraceId}}',
                            '2026-07-31T00:00:00.0000000+00:00','{}','{}',1,
                            randomblob(32));
                        """);
                }
                else if (contradiction == "frontier-mismatch")
                {
                    Execute(
                        connection,
                        $"""
                        UPDATE skill_projection_generations
                        SET input_frontier_sha256='{new string('f', 64)}';
                        UPDATE skill_projection_queue
                        SET input_frontier_sha256='{new string('f', 64)}';
                        """);
                }
                else if (contradiction == "non-terminal")
                {
                    Execute(
                        connection,
                        """
                        UPDATE skill_projection_generations SET lifecycle='pending';
                        UPDATE skill_projection_queue
                        SET state='pending',error_code=NULL;
                        """);
                }
                else if (contradiction == "projected-row")
                {
                    Execute(
                        connection,
                        $"""
                        INSERT INTO skill_projection_invocations(
                            generation_id,source_arm,raw_record_id,trace_id,span_id,
                            span_ordinal,session_id,skill_name,skill_source,
                            invocation_trigger,source_application_version,projected_at)
                        VALUES(
                            1,'otel_trace_span',1,'{MarkerTraceId}',
                            '2222222222222222',0,NULL,'forged-skill',NULL,NULL,
                            '1.0.74','2026-07-31T00:01:00.0000000+00:00');
                        """);
                }
                else if (contradiction == "current-pointer")
                {
                    Execute(
                        connection,
                        """
                        UPDATE skill_projection_generations SET lifecycle='current';
                        UPDATE skill_projection_queue
                        SET state='completed',error_code=NULL;
                        UPDATE skill_projection_trace_heads
                        SET current_generation_id=desired_generation_id;
                        """);
                }
                else if (contradiction == "resolved-pointerless-superseded")
                {
                    Execute(
                        connection,
                        """
                        UPDATE skill_projection_generations
                        SET lifecycle='superseded';
                        UPDATE skill_projection_queue
                        SET state='superseded',
                            lease_owner=NULL,
                            lease_expires_at=NULL,
                            next_attempt_at=NULL,
                            error_code=NULL;
                        UPDATE skill_projection_trace_heads
                        SET desired_generation_id=NULL,
                            current_generation_id=NULL;
                        """);
                }
                else if (contradiction == "old-worker-input-unavailable")
                {
                    Execute(
                        connection,
                        """
                        UPDATE skill_projection_generations
                        SET lifecycle='input_unavailable'
                        WHERE generation_id=(
                            SELECT MIN(generation_id)
                            FROM skill_projection_generations);
                        UPDATE skill_projection_queue
                        SET state='input_unavailable',
                            error_code='retention_input_unavailable'
                        WHERE generation_id=(
                            SELECT MIN(generation_id)
                            FROM skill_projection_generations);
                        """);
                }
                else if (contradiction == "worker-input-unavailable-projected-rows")
                {
                    Execute(
                        connection,
                        $"""
                        INSERT INTO skill_projection_invocations(
                            generation_id,source_arm,raw_record_id,trace_id,span_id,
                            span_ordinal,session_id,skill_name,skill_source,
                            invocation_trigger,source_application_version,projected_at)
                        VALUES(
                            1,'otel_trace_span',1,'{MarkerTraceId}',
                            '2222222222222222',0,NULL,'forged-skill',NULL,NULL,
                            '1.0.74','2026-07-23T02:04:00.0000000+00:00');
                        INSERT INTO skill_projection_inventories(
                            generation_id,source_arm,raw_record_id,trace_id,session_id,
                            observed_name_count,retained_name_count,names_truncated,
                            source_application_version,projected_at)
                        VALUES(
                            1,'otel_trace_span',1,'{MarkerTraceId}',NULL,
                            1,1,0,'1.0.74','2026-07-23T02:04:00.0000000+00:00');
                        INSERT INTO skill_projection_inventory_names(
                            inventory_id,name_ordinal,skill_name)
                        VALUES(last_insert_rowid(),0,'forged-skill');
                        """);
                }
                else if (contradiction is
                    "pointerless-head-without-source-revision" or
                    "pointerless-head-with-source-revision")
                {
                    Execute(
                        connection,
                        """
                        UPDATE skill_projection_trace_heads
                        SET desired_generation_id=NULL,current_generation_id=NULL;
                        DELETE FROM skill_projection_queue;
                        DELETE FROM skill_projection_generation_inputs;
                        DELETE FROM skill_projection_generations;
                        """);
                    if (contradiction == "pointerless-head-without-source-revision")
                    {
                        Execute(
                            connection,
                            "DELETE FROM source_trace_compatibility_revisions;");
                    }
                }
                else
                {
                    throw new ArgumentOutOfRangeException(nameof(contradiction));
                }
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            database = File.ReadAllBytes(mutatedPath);
            File.Delete(mutatedPath);
            manifest = ReplaceDatabaseHash(manifest, database);
            if (contradiction is
                "marker-with-raw" or
                "projected-row" or
                "worker-input-unavailable-projected-rows" or
                "pointerless-head-without-source-revision" or
                "pointerless-head-with-source-revision")
            {
                var parsed = RuntimeBackupJson.ParseManifest(manifest);
                var rows = parsed.RowCounts.ToDictionary(
                    static item => item.Key,
                    static item => item.Value,
                    StringComparer.Ordinal);
                if (contradiction == "marker-with-raw")
                {
                    rows["raw_records"]++;
                }
                else if (contradiction == "projected-row")
                {
                    rows["skill_projection_invocations"]++;
                }
                else if (contradiction == "worker-input-unavailable-projected-rows")
                {
                    rows["skill_projection_invocations"]++;
                    rows["skill_projection_inventories"]++;
                    rows["skill_projection_inventory_names"]++;
                }
                else
                {
                    rows["skill_projection_queue"]--;
                    rows["skill_projection_generation_inputs"]--;
                    rows["skill_projection_generations"]--;
                    if (contradiction == "pointerless-head-without-source-revision")
                        rows["source_trace_compatibility_revisions"]--;
                }
                manifest = RuntimeBackupJson.WriteManifest(parsed with { RowCounts = rows });
            }
            using var target = ZipFile.Open(output, ZipArchiveMode.Create);
            Write(target, "manifest.json", manifest);
            Write(target, "database.sqlite", database);
            return output;
        }

        internal void AssertArchiveDatabaseChecksumMatchesManifest(string path)
        {
            using var archive = ZipFile.OpenRead(path);
            var manifest = RuntimeBackupJson.ParseManifest(
                Read(archive.GetEntry("manifest.json")!));
            var database = Read(archive.GetEntry("database.sqlite")!);
            Assert.Equal(manifest.DatabaseSize, database.LongLength);
            Assert.Equal(
                manifest.DatabaseSha256,
                Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant());
        }

        private static byte[] Read(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }

        private static byte[] ReplaceDatabaseHash(byte[] manifest, byte[] database)
        {
            using var document = JsonDocument.Parse(manifest);
            var oldHash = document.RootElement
                .GetProperty("snapshot")
                .GetProperty("snapshot_id")
                .GetString()!;
            var oldSize = document.RootElement
                .GetProperty("files")[0]
                .GetProperty("size")
                .GetInt64();
            var newHash = Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant();
            var json = Encoding.UTF8.GetString(manifest)
                .Replace(oldHash, newHash, StringComparison.Ordinal)
                .Replace(
                    $"\"size\":{oldSize}",
                    $"\"size\":{database.LongLength}",
                    StringComparison.Ordinal);
            return Encoding.UTF8.GetBytes(json);
        }

        private static void Write(ZipArchive archive, string name, byte[] bytes)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = 0;
            using var stream = entry.Open();
            stream.Write(bytes);
        }

        internal void CreatePreRetentionDatabase(string path, string value)
        {
            using var connection = Open(path);
            Execute(connection, "PRAGMA journal_mode=WAL;");
            using (var transaction = connection.BeginTransaction()) { MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction); transaction.Commit(); }
            Execute(connection, "CREATE TABLE runtime_probe(id INTEGER PRIMARY KEY,value TEXT NOT NULL);");
            using (var insert = connection.CreateCommand()) { insert.CommandText = "INSERT INTO runtime_probe(id,value) VALUES(1,$value);"; insert.Parameters.AddWithValue("$value", value); insert.ExecuteNonQuery(); }
            var token = SHA256.HashData([7]);
            using (var raw = connection.CreateCommand()) { raw.CommandText = "INSERT INTO raw_records(id,source,trace_id,received_at,resource_attributes_json,payload_json,schema_version,retention_owner_token) VALUES(1,'raw-otlp',NULL,'2026-01-01T00:00:00.0000000+00:00','{}','{\"secret\":\"private\"}',1,$token);"; raw.Parameters.AddWithValue("$token", token); raw.ExecuteNonQuery(); }
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void ConvertToMonitorV9WithRetainedTraceSourceEvidence(
            string traceId)
        {
            const string spanId = "2222222222222222";
            new SqliteSourceCompatibilityStore(Source).CreateSchema();
            using var connection = Open(Source);
            var payload =
                """
                {"resourceSpans":[{"resource":{"attributes":[
                  {"key":"service.name","value":{"stringValue":"github-copilot"}}
                ]},"scopeSpans":[{"spans":[
                  {"traceId":"TRACE_ID","spanId":"SPAN_ID","name":"chat gpt-4o"}
                ]}]}]}
                """
                .Replace("TRACE_ID", traceId, StringComparison.Ordinal)
                .Replace("SPAN_ID", spanId, StringComparison.Ordinal);
            using (var raw = connection.CreateCommand())
            {
                raw.CommandText =
                    """
                    UPDATE raw_records
                    SET trace_id=$trace_id,
                        payload_json=$payload
                    WHERE id=1;
                    """;
                raw.Parameters.AddWithValue("$trace_id", traceId);
                raw.Parameters.AddWithValue("$payload", payload);
                raw.ExecuteNonQuery();
            }
            Execute(
                connection,
                $"""
                DROP TABLE IF EXISTS skill_projection_sdk_claims;
                DROP TABLE IF EXISTS skill_projection_inventory_names;
                DROP TABLE IF EXISTS skill_projection_inventories;
                DROP TABLE IF EXISTS skill_projection_invocations;
                DROP TABLE IF EXISTS skill_projection_operation_receipts;
                DROP TABLE IF EXISTS skill_projection_queue;
                DROP TABLE IF EXISTS skill_projection_trace_heads;
                DROP TABLE IF EXISTS skill_projection_generation_inputs;
                DROP TABLE IF EXISTS skill_projection_generations;
                DELETE FROM schema_version WHERE component='skill_projection';
                DROP TABLE source_compatibility_reconciliation_receipts;
                DROP TABLE source_trace_version_interpretation_heads;
                DROP TABLE source_trace_version_interpretation_supersessions;
                DROP TABLE source_trace_compatibility_revisions;
                DROP TRIGGER IF EXISTS source_schema_observations_insert_no_replace;
                DROP TRIGGER IF EXISTS source_trace_version_observations_insert_no_replace;
                DROP TRIGGER source_trace_version_observations_update_rejected;
                DROP TRIGGER source_trace_version_observations_delete_rejected;
                DROP TRIGGER source_schema_observations_trace_version_child_delete_rejected;
                DROP TRIGGER source_schema_observations_projection_input_update_rejected;
                ALTER TABLE source_schema_observations
                DROP COLUMN input_evidence_kind;
                ALTER TABLE source_schema_observations
                DROP COLUMN raw_payload_sha256;
                CREATE TABLE monitor_skill_invocations (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    raw_record_id INTEGER NOT NULL,
                    trace_id TEXT NOT NULL,
                    span_id TEXT NULL,
                    span_ordinal INTEGER NOT NULL,
                    session_id TEXT NULL,
                    skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
                    skill_source TEXT NULL CHECK (skill_source IS NULL OR length(skill_source) BETWEEN 1 AND 256),
                    invocation_trigger TEXT NULL CHECK (invocation_trigger IS NULL OR length(invocation_trigger) BETWEEN 1 AND 256),
                    source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
                    projected_at TEXT NOT NULL,
                    UNIQUE(raw_record_id, span_ordinal),
                    UNIQUE(trace_id, span_id)
                );
                CREATE TABLE monitor_skill_inventories (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    raw_record_id INTEGER NOT NULL,
                    trace_id TEXT NOT NULL,
                    session_id TEXT NULL,
                    observed_name_count INTEGER NOT NULL CHECK (observed_name_count >= 0),
                    retained_name_count INTEGER NOT NULL CHECK (retained_name_count BETWEEN 0 AND 100),
                    names_truncated INTEGER NOT NULL CHECK (names_truncated IN (0, 1)),
                    source_application_version TEXT NOT NULL CHECK (length(source_application_version) BETWEEN 1 AND 256),
                    projected_at TEXT NOT NULL,
                    UNIQUE(raw_record_id, trace_id)
                );
                CREATE TABLE monitor_skill_inventory_names (
                    raw_record_id INTEGER NOT NULL,
                    trace_id TEXT NOT NULL,
                    name_ordinal INTEGER NOT NULL CHECK (name_ordinal BETWEEN 0 AND 99),
                    skill_name TEXT NOT NULL CHECK (length(skill_name) BETWEEN 1 AND 256),
                    PRIMARY KEY(raw_record_id, trace_id, name_ordinal),
                    FOREIGN KEY(raw_record_id, trace_id)
                        REFERENCES monitor_skill_inventories(raw_record_id, trace_id)
                        ON DELETE CASCADE
                );
                CREATE INDEX IX_monitor_skill_invocations_trace_id
                    ON monitor_skill_invocations(trace_id,id);
                CREATE INDEX IX_monitor_skill_invocations_session_id
                    ON monitor_skill_invocations(session_id,id);
                CREATE INDEX IX_monitor_skill_inventories_trace_id
                    ON monitor_skill_inventories(trace_id,id);
                CREATE INDEX IX_monitor_skill_inventories_session_id
                    ON monitor_skill_inventories(session_id,id);
                UPDATE retention_items
                SET expires_at='9999-12-31T23:59:59.9999999+00:00',
                    state='expiring',
                    read_denied_at=NULL,
                    queued_at=NULL
                WHERE store_kind='raw_record' AND source_item_id='1';
                INSERT INTO monitor_ingestions(
                    raw_record_id,received_at,source,trace_id,client_kind,span_count,
                    projected_at,span_projected_at)
                VALUES(
                    1,'2026-01-01T00:00:00.0000000+00:00','raw-otlp','{traceId}',
                    'legacy-family',1,'2026-01-01T00:00:01.0000000+00:00',
                    '2026-01-01T00:00:01.0000000+00:00');
                INSERT INTO monitor_traces(
                    trace_id,client_kind,span_count,projected_at)
                VALUES(
                    '{traceId}','legacy-family',1,
                    '2026-01-01T00:00:01.0000000+00:00');
                INSERT INTO monitor_spans(
                    raw_record_id,trace_id,span_id,span_ordinal,projected_at)
                VALUES(
                    1,'{traceId}','{spanId}',0,
                    '2026-01-01T00:00:01.0000000+00:00');
                INSERT INTO monitor_projection_dispositions(
                    raw_record_id,state,revision,updated_at)
                VALUES(
                    1,'completed',1,
                    '2026-01-01T00:00:01.0000000+00:00');
                DROP INDEX IX_source_trace_attribution_observations_trace_id;
                DROP TABLE source_trace_attribution_observations;
                DROP TABLE source_trace_attribution_reconciliation_queue;
                UPDATE schema_version
                SET version=9
                WHERE component='monitor';
                PRAGMA wal_checkpoint(TRUNCATE);
                """);
        }

        internal byte[] CanonicalDatabaseHash(string path)
        {
            using (var connection = Open(path))
            {
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
                Execute(connection, "PRAGMA journal_mode=DELETE;");
            }
            return SHA256.HashData(File.ReadAllBytes(path));
        }

        internal void DeleteRawAndTombstone(string path)
        {
            using var connection = Open(path);
            Execute(connection, "DELETE FROM raw_records;");
            Execute(connection, "UPDATE retention_items SET state='deleted',revision=9,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00',deleted_at='2026-04-02T00:00:00.0000000+00:00';");
            Execute(connection, $"INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES('{new string('a', 32)}','2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00');");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void DeleteRawWithoutTombstone(string path)
        {
            using var connection = Open(path);
            Execute(connection, "DELETE FROM raw_records;");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void MarkReadDenied(string path)
        {
            using var connection = Open(path);
            Execute(connection, "UPDATE retention_items SET state='expired_pending_deletion',revision=4,read_denied_at='2026-04-01T00:00:00.0000000+00:00',queued_at='2026-04-01T00:00:00.0000000+00:00';");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal string AddItemAudit(string path)
        {
            var operationId = "33333333-3333-3333-3333-333333333333";
            var itemId = new string('a', 32);
            var digest = new string('d', 64);
            using var connection = Open(path);
            Execute(connection, $"INSERT INTO retention_operation_receipts(operation_id,schema_version,result_code,target_kind,target_id,operation,scope,target_item_count,result_json,completion_code,expected_version,result_version,target_item_set_digest,created_at,completed_at,last_replayed_at) VALUES('{operationId}',1,'completed','item','{itemId}','delete_now','single_item',1,'{{}}','deleted','8','9','{digest}','2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',NULL);");
            Execute(connection, $"INSERT INTO retention_audit_events(event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,completion_code,error_code) VALUES('44444444-4444-4444-4444-444444444444','{operationId}','retention_mutation','item','{itemId}',NULL,'2026-04-02T00:00:00.0000000+00:00','local-user','delete_now','operator_request',NULL,'not_applicable','not_applicable','deleting','deleted','55555555-5555-5555-5555-555555555555','8','9','{digest}','deleted',NULL);");
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            return operationId;
        }

        internal void MakeReconciliationCellOversized(string path, string location)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = location switch
            {
                "audit" => "UPDATE retention_audit_events SET comment=$value;",
                "receipt" => "UPDATE retention_operation_receipts SET result_json=$value;",
                _ => throw new ArgumentOutOfRangeException(nameof(location)),
            };
            command.Parameters.AddWithValue("$value", new string('x', RuntimeBackupLimits.MaximumReconciliationTextBytes + 1));
            command.ExecuteNonQuery();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void AddItemAudits(string path, int count)
        {
            var itemId = new string('a', 32);
            var digest = new string('d', 64);
            using var connection = Open(path);
            using var transaction = connection.BeginTransaction();
            using var receipt = connection.CreateCommand();
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT INTO retention_operation_receipts(operation_id,schema_version,result_code,target_kind,target_id,operation,scope,target_item_count,result_json,completion_code,expected_version,result_version,target_item_set_digest,created_at,completed_at,last_replayed_at) VALUES($operation,1,'completed','item',$item,'delete_now','single_item',1,'{}','deleted','8','9',$digest,'2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',NULL);";
            receipt.Parameters.Add("$operation", SqliteType.Text);
            receipt.Parameters.AddWithValue("$item", itemId);
            receipt.Parameters.AddWithValue("$digest", digest);
            using var audit = connection.CreateCommand();
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO retention_audit_events(event_id,operation_id,event_type,target_kind,target_id,session_id,occurred_at,actor_label,operation,reason_code,comment,previous_pin_state,new_pin_state,previous_operation_state,new_operation_state,request_idempotency_key,expected_version,result_version,target_item_set_digest,completion_code,error_code) VALUES($event,$operation,'retention_mutation','item',$item,NULL,'2026-04-02T00:00:00.0000000+00:00','local-user','delete_now','operator_request',NULL,'not_applicable','not_applicable','deleting','deleted',$request,'8','9',$digest,'deleted',NULL);";
            audit.Parameters.Add("$event", SqliteType.Text);
            audit.Parameters.Add("$operation", SqliteType.Text);
            audit.Parameters.AddWithValue("$item", itemId);
            audit.Parameters.Add("$request", SqliteType.Text);
            audit.Parameters.AddWithValue("$digest", digest);
            for (var index = 0; index < count; index++)
            {
                var operationId = $"operation-{index:D4}";
                receipt.Parameters["$operation"].Value = operationId;
                receipt.ExecuteNonQuery();
                audit.Parameters["$event"].Value = $"event-{index:D4}";
                audit.Parameters["$operation"].Value = operationId;
                audit.Parameters["$request"].Value = $"request-{index:D4}";
                audit.ExecuteNonQuery();
            }
            transaction.Commit();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal void AddDeletedItems(string path, int count)
        {
            using var connection = Open(path);
            using var transaction = connection.BeginTransaction();
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,deleted_at,adapter_coverage_version) VALUES($item,$store,'raw_record',$source,1,zeroblob(32),'2026-01-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','raw-default-90d',1,'deleted',5,'2026-04-01T00:00:00.0000000+00:00','2026-04-01T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00',1);";
            item.Parameters.Add("$item", SqliteType.Text);
            item.Parameters.AddWithValue("$store", new string('2', 32));
            item.Parameters.Add("$source", SqliteType.Text);
            using var tombstone = connection.CreateCommand();
            tombstone.Transaction = transaction;
            tombstone.CommandText = "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES($item,'2026-04-02T00:00:00.0000000+00:00','2026-04-02T00:00:00.0000000+00:00');";
            tombstone.Parameters.Add("$item", SqliteType.Text);
            for (var index = 0; index < count; index++)
            {
                var itemId = (index + 1).ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
                item.Parameters["$item"].Value = itemId;
                item.Parameters["$source"].Value = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                item.ExecuteNonQuery();
                tombstone.Parameters["$item"].Value = itemId;
                tombstone.ExecuteNonQuery();
            }
            transaction.Commit();
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        internal SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        internal void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal T Scalar<T>(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture); }
        internal void AssertNoRestoreControls()
        {
            Assert.False(File.Exists(Target + ".runtime-restore-journal.json"));
            Assert.False(File.Exists(Target + ".runtime-restore-journal.json.commit"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback-journal"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback-wal"));
            Assert.False(File.Exists(Target + ".runtime-restore-rollback-shm"));
            Assert.False(File.Exists(Target + ".runtime-restore-stage"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Root, ".runtime-restore-stage-*", SearchOption.TopDirectoryOnly));
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static string Describe(RuntimeBackupPreflightResult result) =>
        $"{result.ErrorCode}; components={string.Join(',', result.ComponentVersions?.Select(item => $"{item.Key}:{item.Value}") ?? [])}; migrations={string.Join(',', result.MigrationSteps ?? [])}";

    private static IEnumerable<string> JsonStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) yield return element.GetString()!;
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray())
                foreach (var value in JsonStrings(item)) yield return value;
        else if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                foreach (var value in JsonStrings(property.Value)) yield return value;
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
    private sealed class SimulatedProcessCrashException : Exception;
}

using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
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
    public void DynamicR0002WriterArchiveIsPortableAcrossFreshMonitorAndCliAuthorities()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("fresh-process-r0002.zip");
        var target = fixture.Path("fresh-process-r0002-target.db");
        var cliTarget = fixture.Path("fresh-process-r0002-cli-target.db");
        var restartedGate = new LocalWorkspacePublicationGate();
        var restartedMonitor = new SqliteRuntimeBackupService(
            fixture.Clock,
            new SkillInvocationV2RegistryProviderV1(SkillInvocationV2ArtifactRegistry.Load(), restartedGate),
            restartedGate);
        var restartedCli = new SqliteRuntimeBackupService(fixture.Clock);

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        var monitorInspection = restartedMonitor.Inspect(archive);
        var cliInspection = restartedCli.Inspect(archive);
        var restored = restartedMonitor.Restore(archive, target, new RuntimeRestoreOptions());
        var cliOutput = new StringWriter();
        var cliError = new StringWriter();
        var cliExit = RuntimeBackupCli.Run(
            ["restore", "--bundle", archive, "--database", cliTarget],
            cliOutput,
            cliError);

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(monitorInspection.Success, monitorInspection.ErrorCode);
        Assert.True(cliInspection.Success, cliInspection.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(["demo-skill"], Strings(target, "SELECT normalized_text FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.Equal(0, cliExit);
        Assert.Equal(string.Empty, cliError.ToString());
        Assert.Equal(1L, Scalar(cliTarget, "SELECT COUNT(*) FROM skill_invocation_snapshots;"));
    }

    [Fact]
    public void DynamicRegistryGenerationIdentityRemainsOpaqueAcrossProvidersAndPublications()
    {
        var first = new SkillInvocationV2RegistryProviderV1();
        var second = new SkillInvocationV2RegistryProviderV1();
        var beforePublication = CanonicalIdentity(first);

        Assert.NotEqual(beforePublication, CanonicalIdentity(second));
        first.PublishGeneration(SkillInvocationV2ArtifactRegistry.Load());
        Assert.NotEqual(beforePublication, CanonicalIdentity(first));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("lease")]
    [InlineData("verify")]
    public void RegistryAuthorityFailureRejectsPublicationBeforeSourceMutation(string failure)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path($"authority-{failure}-publication.zip");
        NormalizeForByteComparison(fixture.DatabasePath);
        var before = File.ReadAllBytes(fixture.DatabasePath);
        var service = new SqliteRuntimeBackupService(
            fixture.Clock,
            new FailingRegistryGenerationAuthority(failure),
            fixture.Gate);

        var created = service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.False(created.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, created.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.False(File.Exists(archive));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void PublicationEnforcesExactRawPayloadCeilingBeforeWorkspaceSemanticReconstruction(
        int excessBytes,
        bool accepted)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var payload = BuildPaddedEmptyOtlpPayload(RawReplayLimits.MaximumRawRecordBytes + excessBytes);
        fixture.RawStore.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            null,
            PublicationAt,
            null,
            payload));
        var archive = fixture.Path($"raw-payload-boundary-{excessBytes}.zip");
        var receiptCount = Scalar(fixture.DatabasePath, "SELECT COUNT(*) FROM runtime_backup_receipts;");

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.Equal(accepted, created.Success);
        Assert.Equal(accepted ? null : RuntimeBackupErrorCodes.RestoreIncompatible, created.ErrorCode);
        if (accepted)
        {
            Assert.True(fixture.Service.Inspect(archive).Success);
        }
        else
        {
            Assert.Equal(receiptCount, Scalar(fixture.DatabasePath, "SELECT COUNT(*) FROM runtime_backup_receipts;"));
            Assert.False(File.Exists(archive));
        }
    }

    [Fact]
    public void RawSemanticReconstructionPreflightRejectsOversizedPayloadWithoutSourceMutation()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        fixture.RawStore.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            null,
            PublicationAt,
            null,
            BuildPaddedEmptyOtlpPayload(RawReplayLimits.MaximumRawRecordBytes + 1)));
        NormalizeForByteComparison(fixture.DatabasePath);
        var before = File.ReadAllBytes(fixture.DatabasePath);

        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            Assert.Throws<InvalidOperationException>(() =>
                LocalWorkspaceProjectionBackupValidation.ValidateRawSemanticReconstructionPreflight(
                    connection,
                    transaction));
            transaction.Rollback();
        }

        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
    }

    [Fact]
    public void RestoreRejectsOversizedWorkspaceRawPayloadBeforeTargetMutation()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        fixture.RawStore.Insert(new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            null,
            PublicationAt,
            null,
            BuildPaddedEmptyOtlpPayload(RawReplayLimits.MaximumRawRecordBytes)));
        var source = fixture.Path("raw-payload-restore-source.zip");
        Assert.True(fixture.Service.CreateAndPublish(fixture.DatabasePath, source).Success);
        var oversized = fixture.Path("raw-payload-restore-oversized.zip");
        RewriteArchiveDatabase(source, oversized, fixture.Path("raw-payload-restore-stage.db"), path =>
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE raw_records SET payload_json=$payload WHERE id=(SELECT MAX(id) FROM raw_records);";
            command.Parameters.AddWithValue("$payload", BuildPaddedEmptyOtlpPayload(RawReplayLimits.MaximumRawRecordBytes + 1));
            Assert.Equal(1, command.ExecuteNonQuery());
        });
        var target = fixture.Path("raw-payload-restore-target.db");
        File.Copy(fixture.DatabasePath, target);
        NormalizeForByteComparison(target);
        var before = File.ReadAllBytes(target);

        var restored = fixture.Service.Restore(oversized, target, new RuntimeRestoreOptions());

        Assert.False(restored.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restored.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void ValidationAcceptsRawFactCardinalityBeyondDetailNodeLimitAndFindsLateTamper()
    {
        const int spanCount = 4_097;
        using var fixture = new LocalRepositoryCatalogFixture();
        using (var connection = Open(fixture.DatabasePath))
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, PublicationAt);
        var raw = new RawTelemetryRecord(
            null,
            RawTelemetrySources.RawOtlp,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            PublicationAt,
            null,
            BuildSyntheticSpanPayload(spanCount));
        var rawId = fixture.RawStore.Insert(raw);
        fixture.RawStore.ApplyProjection(
            rawId,
            raw.Source,
            raw.ReceivedAt,
            MonitorProjectionBuilder.Build(raw),
            raw.ReceivedAt);
        fixture.RawStore.ApplySpanProjection(
            rawId,
            MonitorSpanProjectionBuilder.Build(raw),
            raw.ReceivedAt);

        using (var connection = Open(fixture.DatabasePath))
        using (var transaction = connection.BeginTransaction(deferred: true))
        {
            LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction);
            transaction.Rollback();
        }
        Assert.Equal(spanCount, Scalar(fixture.DatabasePath,
            $"SELECT COUNT(*) FROM local_workspace_span_facts WHERE raw_record_id={rawId};"));

        using (var connection = Open(fixture.DatabasePath))
        using (var transaction = connection.BeginTransaction())
        {
            using var tamper = connection.CreateCommand();
            tamper.Transaction = transaction;
            tamper.CommandText = "UPDATE local_workspace_span_facts SET retry_count=1 WHERE raw_record_id=$raw AND span_ordinal=$ordinal;";
            tamper.Parameters.AddWithValue("$raw", rawId);
            tamper.Parameters.AddWithValue("$ordinal", spanCount - 1);
            Assert.Equal(1, tamper.ExecuteNonQuery());
            Assert.Equal("local_workspace_projection_backup_invalid", Assert.Throws<InvalidOperationException>(() =>
                LocalWorkspaceProjectionBackupValidation.Validate(connection, transaction)).Message);
            transaction.Rollback();
        }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("lease")]
    [InlineData("verify")]
    public void RegistryAuthorityFailureDoesNotReplaceManifestInspectionAndCannotMutateRestoreTarget(string failure)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path($"authority-{failure}-restore.zip");
        var target = fixture.Path($"authority-{failure}-target.db");
        Assert.True(fixture.Service.CreateAndPublish(fixture.DatabasePath, archive).Success);
        File.Copy(fixture.DatabasePath, target);
        NormalizeForByteComparison(target);
        var before = File.ReadAllBytes(target);
        var gate = new LocalWorkspacePublicationGate();
        var service = new SqliteRuntimeBackupService(
            fixture.Clock,
            new FailingRegistryGenerationAuthority(failure),
            gate);

        var inspected = service.Inspect(archive);
        var restored = service.Restore(archive, target, new RuntimeRestoreOptions());

        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.False(restored.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, restored.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData("preview")]
    [InlineData("restore-new")]
    [InlineData("restore-existing")]
    public void RestoreOperationHoldsOneRegistryGenerationCaptureThroughValidationAndSafetyBackup(string operation)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path($"single-authority-{operation}.zip");
        var target = fixture.Path($"single-authority-{operation}.db");
        Assert.True(fixture.Service.CreateAndPublish(fixture.DatabasePath, archive).Success);
        if (operation == "restore-existing") File.Copy(fixture.DatabasePath, target);
        var authority = new SingleCaptureRegistryGenerationAuthority();
        var gate = new LocalWorkspacePublicationGate();
        var service = new SqliteRuntimeBackupService(fixture.Clock, authority, gate);

        var result = operation == "preview"
            ? service.Preview(archive, target).Success
            : service.Restore(archive, target, new RuntimeRestoreOptions()).Success;

        Assert.True(result);
        Assert.Equal(1, authority.CaptureCount);
        Assert.Equal(1, authority.LeaseDisposeCount);
    }

    [Fact]
    public void ExistingDynamicTargetUsesCapturedAuthorityForPreviewAndSafetyBackup()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("dynamic-existing-source.zip");
        var target = fixture.Path("dynamic-existing-target.db");
        var safetyArchive = fixture.Path("dynamic-existing-safety.zip");
        Assert.True(fixture.Service.CreateAndPublish(fixture.DatabasePath, archive).Success);
        File.Copy(fixture.DatabasePath, target);
        Assert.Equal(CanonicalIdentity(fixture.Authority), Text(target,
            "SELECT registry_generation_identity FROM local_workspace_skill_metadata;"));
        Assert.NotEqual(CanonicalIdentity(FixedSkillRegistryGenerationAuthority.Load()), Text(target,
            "SELECT registry_generation_identity FROM local_workspace_skill_metadata;"));

        var preview = fixture.Service.Preview(archive, target);
        var restored = fixture.Service.Restore(
            archive,
            target,
            new RuntimeRestoreOptions(PreRestoreOutputPath: safetyArchive));

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.True(restored.PreRestoreBackupCreated);
        Assert.True(File.Exists(safetyArchive));
        Assert.Equal(["demo-skill"], Strings(target,
            "SELECT normalized_text FROM local_workspace_session_search_facts WHERE kind='skill';"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CommittedCrashRecoveryReconstructsPersistedDynamicAuthority(bool targetExisted)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path($"dynamic-committed-{targetExisted}.zip");
        var target = fixture.Path($"dynamic-committed-{targetExisted}.db");
        Assert.True(fixture.Service.CreateAndPublish(fixture.DatabasePath, archive).Success);
        if (targetExisted)
            Assert.True(new SqliteRuntimeBackupService(fixture.Clock).Initialize(target).Success);
        var crashing = new SqliteRuntimeBackupService(fixture.Clock, checkpoint =>
        {
            if (checkpoint == RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed)
                throw new SimulatedProcessCrashException();
        });
        crashing.ConfigureSkillRegistryAuthority(fixture.Authority);

        Assert.Throws<SimulatedProcessCrashException>(() =>
            crashing.Restore(archive, target, new RuntimeRestoreOptions()));

        var recovering = new SqliteRuntimeBackupService(fixture.Clock);
        var structuralPreflight = recovering.PreflightForMigration(target);
        Assert.True(structuralPreflight.Success,
            $"{structuralPreflight.ErrorCode};{string.Join(',', structuralPreflight.ComponentVersions?.Select(item => $"{item.Key}:{item.Value}") ?? [])}");

        var recovered = recovering.Initialize(target);

        Assert.True(recovered.Success, recovered.ErrorCode);
        Assert.Equal(["demo-skill"], Strings(target,
            "SELECT normalized_text FROM local_workspace_session_search_facts WHERE kind='skill';"));
        Assert.False(File.Exists(target + ".runtime-restore-journal.json"));
        Assert.False(File.Exists(target + ".runtime-restore-journal.json.commit"));
        Assert.False(File.Exists(target + ".runtime-restore-rollback"));
    }

    [Theory]
    [InlineData("INSERT INTO schema_version(component,version) VALUES('future_component',1);")]
    [InlineData("UPDATE schema_version SET version=2 WHERE component='runtime_backup';")]
    [InlineData("ALTER TABLE runtime_backup_receipts ADD COLUMN injected TEXT;")]
    public void PublicationPreflightRejectsUnknownFutureOrMalformedComponentWithoutSourceMutation(string mutation)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("preflight-rejected.zip");
        Execute(fixture.DatabasePath, mutation);
        NormalizeForByteComparison(fixture.DatabasePath);
        var before = File.ReadAllBytes(fixture.DatabasePath);

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.False(created.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, created.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public void CreateAndPublishAdoptsMissingRetentionCatalogForExistingRawAndSessionContent()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        Execute(fixture.DatabasePath, """
            INSERT INTO raw_records(
                source,trace_id,received_at,resource_attributes_json,payload_json,
                schema_version,retention_owner_token)
            VALUES(
                'raw-otlp',NULL,'2026-08-26T00:00:00.0000000+00:00','{}','{}',
                1,randomblob(32));
            """);
        RemoveRetentionAndDependentComponents(fixture.DatabasePath);
        var archive = fixture.Path("missing-retention-adoption.zip");

        Assert.Equal(1L, Scalar(fixture.DatabasePath, "SELECT COUNT(*) FROM raw_records;"));
        Assert.Equal(1L, Scalar(fixture.DatabasePath, "SELECT COUNT(*) FROM session_event_content;"));
        var preflight = fixture.Service.PreflightForMigration(fixture.DatabasePath);
        Assert.True(preflight.Success,
            $"{preflight.ErrorCode};{string.Join(',', preflight.ComponentVersions?.Select(item => $"{item.Key}:{item.Value}") ?? [])}");

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        var inspected = fixture.Service.Inspect(archive);

        Assert.True(created.Success, created.ErrorCode);
        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.Equal(1L, Scalar(fixture.DatabasePath,
            "SELECT COUNT(*) FROM retention_items WHERE store_kind='raw_record';"));
        Assert.Equal(1L, Scalar(fixture.DatabasePath,
            "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
    }

    [Fact]
    public void IncompleteLiveTailPreservesSourceAbsentReadDeniedWorkspaceAuthority()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        SeedTerminalEventContent(fixture.DatabasePath, fixture.Authority);
        SetTerminalContentState(fixture.DatabasePath, fixture.Authority, "read_denied");
        Execute(fixture.DatabasePath,
            $"DELETE FROM session_event_content WHERE event_id='{TerminalEventId}';");
        RemovePricingComponent(fixture.DatabasePath);
        var archive = fixture.Path("incomplete-live-tail.zip");
        var preflight = fixture.Service.PreflightForMigration(fixture.DatabasePath);

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.True(preflight.Success,
            $"{preflight.ErrorCode};{string.Join(',', preflight.ComponentVersions?.Select(item => $"{item.Key}:{item.Value}") ?? [])}");
        Assert.Equal("read_denied", WorkspaceContentState(fixture.DatabasePath));
        Assert.Equal(1L, Scalar(fixture.DatabasePath,
            "SELECT COUNT(*) FROM schema_version WHERE component='pricing' AND version=1;"));
        Assert.True(created.Success, created.ErrorCode);
    }

    [Fact]
    public void IncompleteSafetySnapshotPreservesSourceAbsentReadDeniedWorkspaceAuthority()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var incoming = fixture.Path("incomplete-safety-incoming.zip");
        SeedTerminalEventContent(fixture.DatabasePath, fixture.Authority);
        var targetDirectory = fixture.Path("app");
        Directory.CreateDirectory(targetDirectory);
        var target = System.IO.Path.Combine(targetDirectory, "incomplete-safety-target.db");
        File.Copy(fixture.DatabasePath, target);
        SetArchivedContentState(fixture.DatabasePath, fixture.Authority, "not_captured");
        var incomingCreated = fixture.Service.CreateAndPublish(fixture.DatabasePath, incoming);
        Assert.True(incomingCreated.Success, incomingCreated.ErrorCode);
        SetTerminalContentState(target, fixture.Authority, "read_denied");
        Execute(target, $"DELETE FROM session_event_content WHERE event_id='{TerminalEventId}';");
        RemovePricingComponent(target);
        var safetyArchive = System.IO.Path.Combine(targetDirectory, "incomplete-safety.zip");
        var restoredSafety = System.IO.Path.Combine(targetDirectory, "incomplete-safety-restored.db");

        var restored = fixture.Service.Restore(
            incoming,
            target,
            new RuntimeRestoreOptions(PreRestoreOutputPath: safetyArchive));
        var safetyRestore = fixture.Service.Restore(
            safetyArchive,
            restoredSafety,
            new RuntimeRestoreOptions());

        Assert.True(restored.Success, restored.ErrorCode);
        Assert.True(safetyRestore.Success, safetyRestore.ErrorCode);
        Assert.Equal("read_denied", WorkspaceContentState(restoredSafety));
        Assert.Equal(0L, Scalar(restoredSafety,
            $"SELECT COUNT(*) FROM session_event_content WHERE event_id='{TerminalEventId}';"));
    }

    [Fact]
    public void AlertV1MigrationRunsInsideTheRegisteredAtomicTail()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        DowngradeAlertToV1(fixture.DatabasePath);
        RemovePricingComponent(fixture.DatabasePath);
        var archive = fixture.Path("alert-v1-tail.zip");
        var preflight = fixture.Service.PreflightForMigration(fixture.DatabasePath);

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.True(preflight.Success,
            $"{preflight.ErrorCode};{string.Join(',', preflight.ComponentVersions?.Select(item => $"{item.Key}:{item.Value}") ?? [])}");
        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal(2L, Scalar(fixture.DatabasePath,
            "SELECT version FROM schema_version WHERE component='alert_engine';"));
        Assert.True(fixture.Service.Inspect(archive).Success);
    }

    [Theory]
    [InlineData("not_captured", "read_denied")]
    [InlineData("not_captured", "deleted")]
    [InlineData("invalid", "read_denied")]
    [InlineData("invalid", "deleted")]
    [InlineData("deleted", "read_denied")]
    public void RestorePreservesCurrentTerminalWorkspaceAuthorityAcrossArchivedReferences(
        string archivedState,
        string terminalState)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        SeedTerminalEventContent(fixture.DatabasePath, fixture.Authority);
        NormalizeForByteComparison(fixture.DatabasePath);
        var targetDirectory = fixture.Path("app");
        Directory.CreateDirectory(targetDirectory);
        var target = System.IO.Path.Combine(targetDirectory, $"terminal-{archivedState}-{terminalState}.db");
        File.Copy(fixture.DatabasePath, target);
        if (archivedState == "deleted")
            SetTerminalContentState(fixture.DatabasePath, fixture.Authority, archivedState);
        else
            SetArchivedContentState(fixture.DatabasePath, fixture.Authority, archivedState);
        Assert.Equal(archivedState, WorkspaceContentState(fixture.DatabasePath));
        var archive = fixture.Path($"terminal-{archivedState}-{terminalState}.zip");
        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        Assert.True(created.Success, created.ErrorCode);
        SetTerminalContentState(target, fixture.Authority, terminalState);
        Assert.Equal(terminalState, WorkspaceContentState(target));
        if (terminalState == "read_denied")
        {
            Assert.Equal(1L, Scalar(target, """
                SELECT COUNT(*) FROM local_workspace_node_content_refs r
                JOIN session_events e ON e.event_id=r.source_item_id
                JOIN retention_items i ON i.item_id=r.retention_item_id
                WHERE r.availability_state='read_denied'
                  AND r.revision_input=e.content_state||'|'||i.captured_at||'|'||i.expires_at||'|'||
                    i.item_id||'|'||i.store_instance_id||'|'||CAST(i.revision AS TEXT)||'|'||i.state||'|';
                """));
        }

        var preview = fixture.Service.Preview(archive, target);
        var restored = fixture.Service.Restore(archive, target, new RuntimeRestoreOptions());

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.Equal(1, preview.TerminalReconciliationCount);
        Assert.False(preview.RequiresConfirmation);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(terminalState, WorkspaceContentState(target));
        Assert.Equal(0L, Scalar(target,
            $"SELECT COUNT(*) FROM session_event_content WHERE event_id='{TerminalEventId}';"));
        Assert.Equal(terminalState == "deleted" ? 1L : 0L, Scalar(target,
            $"SELECT COUNT(*) FROM local_workspace_content_tombstones WHERE store_kind='session_event_content' AND source_item_id='{TerminalEventId}';"));
        var roundTripArchive = System.IO.Path.Combine(
            targetDirectory,
            $"terminal-roundtrip-{archivedState}-{terminalState}.zip");
        var roundTripPreflight = fixture.Service.PreflightForMigration(target);
        Assert.True(roundTripPreflight.Success, roundTripPreflight.ErrorCode);
        var roundTrip = fixture.Service.CreateAndPublish(target, roundTripArchive);
        Assert.True(roundTrip.Success, roundTrip.ErrorCode);
        Assert.True(fixture.Service.Inspect(roundTripArchive).Success);
        Assert.Equal(terminalState, WorkspaceContentState(target));
    }

    [Fact]
    public void RestoreReplaysExactReadDeniedPointerWithoutFallbackContentReference()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        SeedTerminalEventContent(
            fixture.DatabasePath,
            fixture.Authority,
            eventType: "UserPromptSubmit",
            contentJson: "{\"prompt\":\"synthetic\"}",
            sourceAdapter: "claude-code-hook",
            schemaFingerprint: new string('0', 64));
        var targetDirectory = fixture.Path("app");
        Directory.CreateDirectory(targetDirectory);
        var target = System.IO.Path.Combine(targetDirectory, "terminal-pointer.db");
        File.Copy(fixture.DatabasePath, target);
        SetArchivedContentState(fixture.DatabasePath, fixture.Authority, "not_captured");
        var archive = fixture.Path("terminal-pointer.zip");
        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        Assert.True(created.Success, created.ErrorCode);
        SetTerminalContentState(target, fixture.Authority, "read_denied");
        Assert.Equal(["instruction|/prompt|read_denied"], Strings(target,
            $"SELECT part||'|'||json_pointer||'|'||availability_state FROM local_workspace_node_content_refs WHERE source_item_id='{TerminalEventId}';"));

        var restored = fixture.Service.Restore(archive, target, new RuntimeRestoreOptions());

        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(["instruction|/prompt|read_denied"], Strings(target,
            $"SELECT part||'|'||json_pointer||'|'||availability_state FROM local_workspace_node_content_refs WHERE source_item_id='{TerminalEventId}';"));
        Assert.Equal(0L, Scalar(target,
            $"SELECT COUNT(*) FROM local_workspace_node_content_refs WHERE source_item_id='{TerminalEventId}' AND part='event_content';"));
        Assert.Equal(0L, Scalar(target,
            $"SELECT COUNT(*) FROM session_event_content WHERE event_id='{TerminalEventId}';"));
    }

    [Fact]
    public void PublicationRejectsSourceAbsentReadDeniedWithUnauthenticatedRevisionWithoutMutation()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        SeedTerminalEventContent(fixture.DatabasePath, fixture.Authority);
        var targetDirectory = fixture.Path("app");
        Directory.CreateDirectory(targetDirectory);
        var target = System.IO.Path.Combine(targetDirectory, "terminal-revision.db");
        File.Copy(fixture.DatabasePath, target);
        SetArchivedContentState(fixture.DatabasePath, fixture.Authority, "not_captured");
        var archive = fixture.Path("terminal-revision-source.zip");
        Assert.True(fixture.Service.CreateAndPublish(fixture.DatabasePath, archive).Success);
        SetTerminalContentState(target, fixture.Authority, "read_denied");
        Assert.True(fixture.Service.Restore(archive, target, new RuntimeRestoreOptions()).Success);
        Assert.Equal(0L, Scalar(target,
            $"SELECT COUNT(*) FROM session_event_content WHERE event_id='{TerminalEventId}';"));
        Execute(target,
            $"UPDATE local_workspace_node_content_refs SET revision_input='fabricated' WHERE source_item_id='{TerminalEventId}';");
        NormalizeForByteComparison(target);
        var before = File.ReadAllBytes(target);
        var output = System.IO.Path.Combine(targetDirectory, "terminal-revision-tampered.zip");

        var created = fixture.Service.CreateAndPublish(target, output);

        Assert.False(created.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, created.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.False(File.Exists(output));
    }

    [Theory]
    [InlineData("run_id")]
    [InlineData("source_surface")]
    [InlineData("source_adapter")]
    [InlineData("source_event_id")]
    [InlineData("parent_event_id")]
    [InlineData("trace_id")]
    public void TerminalReconciliationRejectsSameSessionEventWithDifferentExactLineage(string mutation)
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        SeedTerminalEventContent(fixture.DatabasePath, fixture.Authority);
        var targetDirectory = fixture.Path("app");
        Directory.CreateDirectory(targetDirectory);
        var target = System.IO.Path.Combine(targetDirectory, $"terminal-lineage-{mutation}.db");
        File.Copy(fixture.DatabasePath, target);
        SetArchivedContentState(fixture.DatabasePath, fixture.Authority, "not_captured");
        var archive = fixture.Path($"terminal-lineage-{mutation}.zip");
        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);
        Assert.True(created.Success, created.ErrorCode);
        MutateTerminalEventLineage(target, mutation);
        SetTerminalContentState(target, fixture.Authority, "read_denied");
        NormalizeForByteComparison(target);
        var before = File.ReadAllBytes(target);

        var preview = fixture.Service.Preview(archive, target);
        var restored = fixture.Service.Restore(archive, target, new RuntimeRestoreOptions());

        Assert.False(preview.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, preview.ErrorCode);
        Assert.False(restored.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed, restored.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(target));
    }

    [Fact]
    public void LegacyV4WorkspaceTombstoneCaptureMapsToSessionEventContentInV5()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        SeedTerminalEventContent(fixture.DatabasePath, fixture.Authority);
        SetTerminalContentState(fixture.DatabasePath, fixture.Authority, "deleted");
        var destination = fixture.Path("v4-terminal-destination.db");
        File.Copy(fixture.DatabasePath, destination);
        ReplaceWorkspaceTombstonesWithV4Shape(fixture.DatabasePath);
        Execute(destination, "DELETE FROM local_workspace_content_tombstones;");

        using (var source = Open(fixture.DatabasePath))
        using (var sourceTransaction = source.BeginTransaction())
        using (var target = Open(destination))
        using (var targetTransaction = target.BeginTransaction())
        {
            var authority = LocalWorkspaceTerminalAuthority.Capture(source, sourceTransaction);
            authority.ApplyTombstones(target, targetTransaction);
            targetTransaction.Commit();
            sourceTransaction.Rollback();
        }

        Assert.Equal(1L, Scalar(destination,
            $"SELECT COUNT(*) FROM local_workspace_content_tombstones WHERE store_kind='session_event_content' AND source_item_id='{TerminalEventId}';"));
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
        File.Copy(fixture.DatabasePath, target);
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

    [Fact]
    public void WorkspaceFinalizationValidatesSemanticsBeforeWritingTheV5Stamp()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        using var connection = Open(fixture.DatabasePath);
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            CREATE TABLE test_workspace_stamp_audit(attempts INTEGER NOT NULL);
            INSERT INTO test_workspace_stamp_audit VALUES(0);
            CREATE TRIGGER test_workspace_stamp_insert BEFORE INSERT ON schema_version
            WHEN NEW.component='local_workspace_projection'
            BEGIN UPDATE test_workspace_stamp_audit SET attempts=attempts+1; END;
            CREATE TRIGGER test_workspace_stamp_update BEFORE UPDATE OF version ON schema_version
            WHEN NEW.component='local_workspace_projection'
            BEGIN UPDATE test_workspace_stamp_audit SET attempts=attempts+1; END;
            UPDATE local_workspace_execution_headers SET source_identity='tampered-execution';
            """);
        using var transaction = connection.BeginTransaction();
        var finalizer = typeof(LocalWorkspaceProjectionSchemaV1).GetMethod(
            "ValidateAndRestampCurrent",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(finalizer);
        var failure = Assert.Throws<TargetInvocationException>(() =>
            finalizer.Invoke(null, [connection, transaction]));
        Assert.Equal("local_workspace_projection_semantic_validation_failed", failure.InnerException!.Message);
        Assert.Equal(0L, Convert.ToInt64(new SqliteCommand(
            "SELECT attempts FROM test_workspace_stamp_audit;", connection, transaction).ExecuteScalar()));
        transaction.Rollback();
    }

    [Fact]
    public void PublicationRefreshRollsBackWhenTheFinalWorkspaceStampCannotBeWritten()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        Execute(fixture.DatabasePath, """
            UPDATE local_workspace_sessions SET revision_seed='tampered';
            CREATE TRIGGER test_workspace_stamp_insert_block BEFORE INSERT ON schema_version
            WHEN NEW.component='local_workspace_projection'
            BEGIN SELECT RAISE(ABORT,'blocked_workspace_stamp'); END;
            CREATE TRIGGER test_workspace_stamp_update_block BEFORE UPDATE OF version ON schema_version
            WHEN NEW.component='local_workspace_projection'
            BEGIN SELECT RAISE(ABORT,'blocked_workspace_stamp'); END;
            """);
        var before = Text(fixture.DatabasePath,
            "SELECT revision_seed FROM local_workspace_sessions LIMIT 1;");
        var refresh = typeof(SqliteRuntimeBackupService).GetMethod(
            "RefreshProjectionForPublication",
            BindingFlags.Static | BindingFlags.NonPublic);

        Assert.NotNull(refresh);
        Assert.Throws<TargetInvocationException>(() => refresh.Invoke(
            null, [fixture.DatabasePath, PublicationAt, fixture.Authority]));

        Assert.Equal(before, Text(fixture.DatabasePath,
            "SELECT revision_seed FROM local_workspace_sessions LIMIT 1;"));
        Assert.Equal(5L, Scalar(fixture.DatabasePath,
            "SELECT version FROM schema_version WHERE component='local_workspace_projection';"));
    }

    [Fact]
    public void CreateAndPublishRejectsOversizedRawProjectionPayloadBeforeSourceMutation()
    {
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archive = fixture.Path("oversized-raw-preflight.zip");
        using (var connection = Open(fixture.DatabasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO raw_records(
                    source,trace_id,received_at,resource_attributes_json,payload_json,
                    schema_version,retention_owner_token)
                VALUES('raw-otlp',NULL,'2026-08-26T00:00:00.0000000+00:00',NULL,$payload,1,randomblob(32));
                """;
            command.Parameters.AddWithValue("$payload",
                BuildPaddedEmptyOtlpPayload(RawReplayLimits.MaximumRawRecordBytes + 1));
            command.ExecuteNonQuery();
        }
        Execute(fixture.DatabasePath,
            "UPDATE local_workspace_sessions SET revision_seed='repairable-preflight-tamper';");
        NormalizeForByteComparison(fixture.DatabasePath);
        var before = File.ReadAllBytes(fixture.DatabasePath);

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archive);

        Assert.False(created.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, created.ErrorCode);
        Assert.Equal(before, File.ReadAllBytes(fixture.DatabasePath));
        Assert.False(File.Exists(archive));
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

    [Fact]
    public void DurableSdkToolValidatorAuthenticatesOverflowBeyondPersistedReferences()
    {
        using var connection = LocalWorkspaceProjectionBackfillTests.OpenUnavailableSdkToolFixture("deleted");
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            INSERT INTO session_runs VALUES('0198f5b8-0c00-7000-8000-000000000020','0198f5b8-0c00-7000-8000-000000000001','copilot-sdk','native-run-2',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'active');
            """);
        for (var index = 0; index < 15; index++)
        {
            LocalWorkspaceProjectionSchemaTests.Execute(connection, $$"""
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,parent_event_id,source_adapter,source_event_id,type,occurred_at,content_state)
                  VALUES('0198f5b8-0c00-7000-8000-{{index + 100:D12}}','0198f5b8-0c00-7000-8000-000000000001','0198f5b8-0c00-7000-8000-000000000010','copilot-sdk','0198f5b8-0c00-7000-8000-000000000011','copilot-sdk-stream','source-complete-{{index:D2}}','tool.execution_complete','2026-08-24T00:01:{{index:D2}}.0000000+00:00','not_captured');
                """);
        }
        using (var transaction = connection.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(
                connection, transaction, PublicationAt, FixedSkillRegistryGenerationAuthority.Load());
            transaction.Commit();
        }
        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE local_workspace_tool_metadata
               SET started_state='inconsistent'
             WHERE node_id IN (SELECT node_id FROM local_workspace_semantic_receipts WHERE semantic_kind='tool');
            """);

        Assert.Equal(["16:inconsistent:inconsistent:unknown:unknown"],
            LocalWorkspaceProjectionSchemaTests.Strings(connection, """
                SELECT COUNT(reference.source_ordinal)||':'||metadata.started_state||':'||metadata.completed_state||':'||node.lifecycle||':'||node.status
                  FROM local_workspace_tool_metadata metadata
                  JOIN local_workspace_nodes node ON node.node_id=metadata.node_id
                  JOIN local_workspace_node_source_references reference ON reference.node_id=metadata.node_id
                 GROUP BY metadata.node_id;
                """));
        Assert.Equal(["17:1"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT COUNT(*)||':'||SUM(NOT EXISTS(
              SELECT 1 FROM local_workspace_node_source_references reference
               WHERE reference.event_id=event.event_id
                 AND reference.node_id IN (SELECT node_id FROM local_workspace_semantic_receipts)))
              FROM session_events event
             WHERE event.type IN ('tool.execution_start','tool.execution_complete')
               AND event.run_id='0198f5b8-0c00-7000-8000-000000000010';
            """));
        ValidateDurableSemanticGraphForTest(connection);

        LocalWorkspaceProjectionSchemaTests.Execute(connection, """
            UPDATE session_events
               SET run_id='0198f5b8-0c00-7000-8000-000000000020'
             WHERE event_id=(
               SELECT event.event_id
                 FROM session_events event
                WHERE event.type='tool.execution_complete'
                  AND NOT EXISTS(
                    SELECT 1 FROM local_workspace_node_source_references reference
                     WHERE reference.event_id=event.event_id
                       AND reference.node_id IN (SELECT node_id FROM local_workspace_semantic_receipts))
                LIMIT 1);
            """);
        Assert.Equal(["16:0"], LocalWorkspaceProjectionSchemaTests.Strings(connection, """
            SELECT COUNT(*)||':'||SUM(NOT EXISTS(
              SELECT 1 FROM local_workspace_node_source_references reference
               WHERE reference.event_id=event.event_id
                 AND reference.node_id IN (SELECT node_id FROM local_workspace_semantic_receipts)))
              FROM session_events event
             WHERE event.type IN ('tool.execution_start','tool.execution_complete')
               AND event.run_id='0198f5b8-0c00-7000-8000-000000000010';
            """));

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
        using var fixture = new ConfiguredBackupFixture(PublicationAt, PublicationAt.AddDays(1));
        var archivePath = fixture.Path("v5-inventory.zip");
        var extractedDatabasePath = fixture.Path("v5-inventory-database.sqlite");

        var created = fixture.Service.CreateAndPublish(fixture.DatabasePath, archivePath);
        Assert.True(created.Success, created.ErrorCode);
        var inspected = fixture.Service.Inspect(archivePath);
        Assert.True(inspected.Success, inspected.ErrorCode);
        var manifest = ReadManifest(archivePath);
        using (var archive = ZipFile.OpenRead(archivePath))
        {
            Assert.Single(archive.Entries, static entry => entry.FullName == "database.sqlite")
                .ExtractToFile(extractedDatabasePath);
        }

        Assert.Equal(5, manifest.ComponentVersions["local_workspace_projection"]);
        Assert.Equal(5, inspected.ComponentVersions["local_workspace_projection"]);

        var extractedWorkspaceRowCounts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        using (var connection = Open(extractedDatabasePath))
        {
            using (var version = connection.CreateCommand())
            {
                version.CommandText = "SELECT version FROM schema_version WHERE component='local_workspace_projection';";
                Assert.Equal(5L, Convert.ToInt64(
                    version.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            foreach (var table in LocalWorkspaceProjectionSchemaV1.TableNames)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";";
                extractedWorkspaceRowCounts.Add(
                    table,
                    Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        Assert.Equal(18, extractedWorkspaceRowCounts.Count);
        Assert.Contains(extractedWorkspaceRowCounts, static item => item.Value == 0);
        Assert.Contains(extractedWorkspaceRowCounts, static item => item.Value > 0);
        Assert.Equal(
            extractedWorkspaceRowCounts.ToArray(),
            manifest.RowCounts
                .Where(static item => item.Key.StartsWith("local_workspace_", StringComparison.Ordinal))
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            extractedWorkspaceRowCounts.ToArray(),
            inspected.RowCounts
                .Where(static item => item.Key.StartsWith("local_workspace_", StringComparison.Ordinal))
                .OrderBy(static item => item.Key, StringComparer.Ordinal)
                .ToArray());
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
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "UPDATE local_workspace_sessions SET label_state='recorded',label_text='x',label_source_identity='0198f5b8-0c00-7000-8000-000000000099',label_expires_at='2099-01-01T00:00:00.0000000+00:00',label_owner_revision='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',instruction_count=1; INSERT INTO local_workspace_session_search_facts VALUES((SELECT session_id FROM local_workspace_sessions),'label','0198f5b8-0c00-7000-8000-000000000099','x','2099-01-01T00:00:00.0000000+00:00');");

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

    private static string BuildPaddedEmptyOtlpPayload(int utf8Bytes)
    {
        const string prefix = "{\"resourceSpans\":[],\"padding\":\"";
        const string suffix = "\"}";
        return prefix + new string('x', utf8Bytes - prefix.Length - suffix.Length) + suffix;
    }

    private static string BuildSyntheticSpanPayload(int count)
    {
        var builder = new StringBuilder("{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[");
        for (var index = 0; index < count; index++)
        {
            if (index != 0) builder.Append(',');
            builder.Append("{\"traceId\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"spanId\":\"")
                .Append(index.ToString("x16", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\",\"name\":\"synthetic\"}");
        }
        return builder.Append("]}]}]}").ToString();
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void NormalizeForByteComparison(string path) =>
        Execute(path, "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;");

    private static void RemoveRetentionAndDependentComponents(string path)
    {
        using var connection = Open(path);
        SetForeignKeys(connection, enabled: false);
        using var transaction = connection.BeginTransaction();
        foreach (var trigger in LocalWorkspaceProjectionSchemaV1.OwnedObjects.Where(item => item.Type == "trigger"))
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {trigger.Name};");
        foreach (var table in LocalWorkspaceProjectionSchemaV1.TableNames.Reverse())
            Execute(connection, transaction, $"DROP TABLE {table};");
        Execute(connection, transaction, "DELETE FROM schema_version WHERE component='local_workspace_projection';");
        foreach (var trigger in SkillInvocationSnapshotSchemaV1.TriggerDefinitions)
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {trigger.Name};");
        Execute(connection, transaction, "DROP TABLE skill_invocation_snapshot_receipts;");
        Execute(connection, transaction, "DROP TABLE skill_invocation_snapshots;");
        Execute(connection, transaction, "DELETE FROM schema_version WHERE component='skill_invocation_snapshot';");
        foreach (var trigger in SkillProjectionSchemaV1.TriggerDefinitions)
            Execute(connection, transaction, $"DROP TRIGGER IF EXISTS {trigger.Name};");
        foreach (var table in SkillProjectionSchemaV1.TableNames.Reverse())
            Execute(connection, transaction, $"DROP TABLE {table};");
        Execute(connection, transaction, "DELETE FROM schema_version WHERE component='skill_projection';");
        foreach (var table in RetentionTables)
            Execute(connection, transaction, $"DROP TABLE {table};");
        transaction.Commit();
        SetForeignKeys(connection, enabled: true);
    }

    private static void RemovePricingComponent(string path)
    {
        using var connection = Open(path);
        SetForeignKeys(connection, enabled: false);
        using var transaction = connection.BeginTransaction();
        foreach (var item in PricingSchemaV1.OwnedObjects
                     .Where(static item => item.Type is "trigger" or "index"))
            Execute(connection, transaction, $"DROP {item.Type.ToUpperInvariant()} IF EXISTS \"{item.Name}\";");
        foreach (var item in PricingSchemaV1.OwnedObjects
                     .Where(static item => item.Type == "table")
                     .Reverse())
            Execute(connection, transaction, $"DROP TABLE IF EXISTS \"{item.Name}\";");
        Execute(connection, transaction, "DELETE FROM schema_version WHERE component='pricing';");
        transaction.Commit();
        SetForeignKeys(connection, enabled: true);
    }

    private static void DowngradeAlertToV1(string path)
    {
        using var connection = Open(path);
        SetForeignKeys(connection, enabled: false);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, """
            DROP TABLE alert_suppressions;
            DROP TABLE alert_receipts;
            DROP TABLE alert_evaluations;
            DELETE FROM schema_version WHERE component='alert_engine';
            """);
        AlertSchemaV1.Create(connection, transaction);
        transaction.Commit();
        SetForeignKeys(connection, enabled: true);
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void SetForeignKeys(SqliteConnection connection, bool enabled)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys={(enabled ? "ON" : "OFF")};";
        command.ExecuteNonQuery();
    }

    private static void SeedTerminalEventContent(
        string path,
        ISkillRegistryGenerationAuthority authority,
        string eventType = "event",
        string contentJson = "{\"value\":\"synthetic\"}",
        string sourceAdapter = "terminal-fixture",
        string? schemaFingerprint = null)
    {
        const string captured = "2026-08-26T00:00:00.0000000+00:00";
        const string expires = "2026-11-24T00:00:00.0000000+00:00";
        const string kind = "application/json";
        const string sourceEvent = "terminal-event";
        var token = Enumerable.Repeat((byte)71, 32).ToArray();
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        var sessionId = TransactionText(connection, transaction,
            "SELECT session_id FROM sessions ORDER BY session_id LIMIT 1;");
        var storeId = TransactionText(connection, transaction,
            "SELECT store_instance_id FROM retention_store_instances WHERE id=1;");
        var receipt = RetentionOwnershipReceipt.CreateSession(new(
            storeId,
            TerminalEventId,
            kind,
            captured,
            DateTimeOffset.Parse(captured).UtcDateTime.Ticks,
            expires,
            DateTimeOffset.Parse(expires).UtcDateTime.Ticks,
            sessionId,
            TerminalRunId,
            sourceAdapter,
            sourceEvent,
            token));
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,status)
                VALUES($run,$session,'copilot-sdk','terminal-run','completed');
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                    type,occurred_at,content_state,schema_fingerprint)
                VALUES($event,$session,$run,'copilot-sdk',$adapter,$source,$type,$captured,'available',$fingerprint);
                INSERT INTO session_event_content(
                    event_id,content_kind,content_json,captured_at,expires_at,retention_owner_token)
                VALUES($event,$kind,$content,$captured,$expires,$token);
                INSERT INTO retention_items(
                    item_id,store_instance_id,store_kind,source_item_id,receipt_version,
                    ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,
                    revision,adapter_coverage_version)
                VALUES($item,$store,'session_event_content',$event,1,$receipt,$captured,$expires,
                    'raw-default-90d',1,'expiring',1,1);
                """;
            insert.Parameters.AddWithValue("$event", TerminalEventId);
            insert.Parameters.AddWithValue("$session", sessionId);
            insert.Parameters.AddWithValue("$run", TerminalRunId);
            insert.Parameters.AddWithValue("$adapter", sourceAdapter);
            insert.Parameters.AddWithValue("$source", sourceEvent);
            insert.Parameters.AddWithValue("$type", eventType);
            insert.Parameters.AddWithValue("$fingerprint", (object?)schemaFingerprint ?? DBNull.Value);
            insert.Parameters.AddWithValue("$content", contentJson);
            insert.Parameters.AddWithValue("$captured", captured);
            insert.Parameters.AddWithValue("$expires", expires);
            insert.Parameters.AddWithValue("$kind", kind);
            insert.Parameters.AddWithValue("$token", token);
            insert.Parameters.AddWithValue("$item", TerminalItemId);
            insert.Parameters.AddWithValue("$store", storeId);
            insert.Parameters.AddWithValue("$receipt", receipt);
            insert.ExecuteNonQuery();
        }
        LocalWorkspaceProjectionStore.Refresh(connection, transaction, PublicationAt, authority);
        transaction.Commit();
        Assert.Equal("available", WorkspaceContentState(path));
    }

    private static void SetArchivedContentState(
        string path,
        ISkillRegistryGenerationAuthority authority,
        string state)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, state == "not_captured"
            ? $"""
                DELETE FROM session_event_content WHERE event_id='{TerminalEventId}';
                DELETE FROM retention_items WHERE item_id='{TerminalItemId}';
                UPDATE session_events SET content_state='not_captured' WHERE event_id='{TerminalEventId}';
                """
            : $"UPDATE session_events SET content_state='unsupported' WHERE event_id='{TerminalEventId}';");
        LocalWorkspaceProjectionStore.Refresh(connection, transaction, PublicationAt, authority);
        transaction.Commit();
    }

    private static void MutateTerminalEventLineage(string path, string mutation)
    {
        const string otherRunId = "0199f5b8-0c00-7000-8000-000000000099";
        const string otherParentEventId = "0199f5b8-0c00-7000-8000-000000000098";
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        var sessionId = TransactionText(connection, transaction,
            $"SELECT session_id FROM session_events WHERE event_id='{TerminalEventId}';");
        var storeId = TransactionText(connection, transaction,
            "SELECT store_instance_id FROM retention_store_instances WHERE id=1;");
        var captured = TransactionText(connection, transaction,
            $"SELECT captured_at FROM retention_items WHERE item_id='{TerminalItemId}';");
        var expires = TransactionText(connection, transaction,
            $"SELECT expires_at FROM retention_items WHERE item_id='{TerminalItemId}';");
        var contentKind = TransactionText(connection, transaction,
            $"SELECT content_kind FROM session_event_content WHERE event_id='{TerminalEventId}';");
        var currentSourceAdapter = TransactionText(connection, transaction,
            $"SELECT source_adapter FROM session_events WHERE event_id='{TerminalEventId}';");
        var currentSourceEventId = TransactionText(connection, transaction,
            $"SELECT source_event_id FROM session_events WHERE event_id='{TerminalEventId}';");
        var sourceAdapter = mutation == "source_adapter" ? "other-adapter" : currentSourceAdapter;
        var sourceEventId = mutation == "source_event_id" ? "terminal-event-other" : currentSourceEventId;
        var runId = mutation == "run_id" ? otherRunId : TerminalRunId;
        if (mutation == "run_id")
        {
            Execute(connection, transaction, $"""
                INSERT INTO session_runs(run_id,session_id,source_surface,native_run_id,status)
                VALUES('{otherRunId}','{sessionId}','copilot-sdk','terminal-run-other','completed');
                """);
        }
        if (mutation == "parent_event_id")
        {
            Execute(connection, transaction, $"""
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,source_adapter,source_event_id,
                    type,occurred_at,content_state)
                VALUES('{otherParentEventId}','{sessionId}','{TerminalRunId}','copilot-sdk',
                    '{sourceAdapter}','terminal-parent-other','event',
                    '2026-08-25T23:59:59.0000000+00:00','not_captured');
                """);
        }
        Execute(connection, transaction, $"""
            UPDATE session_events
            SET run_id='{runId}',
                source_surface='{(mutation == "source_surface" ? "claude-code" : "copilot-sdk")}',
                source_adapter='{sourceAdapter}',
                source_event_id='{sourceEventId}',
                parent_event_id={(mutation == "parent_event_id" ? $"'{otherParentEventId}'" : "parent_event_id")},
                trace_id={(mutation == "trace_id" ? "'terminal-trace-other'" : "trace_id")}
            WHERE event_id='{TerminalEventId}';
            """);
        var token = Enumerable.Repeat((byte)71, 32).ToArray();
        var receipt = RetentionOwnershipReceipt.CreateSession(new(
            storeId,
            TerminalEventId,
            contentKind,
            captured,
            DateTimeOffset.Parse(captured).UtcDateTime.Ticks,
            expires,
            DateTimeOffset.Parse(expires).UtcDateTime.Ticks,
            sessionId,
            runId,
            sourceAdapter,
            sourceEventId,
            token));
        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = $"UPDATE retention_items SET ownership_receipt=$receipt WHERE item_id='{TerminalItemId}';";
            update.Parameters.AddWithValue("$receipt", receipt);
            Assert.Equal(1, update.ExecuteNonQuery());
        }
        transaction.Commit();
    }

    private static void ReplaceWorkspaceTombstonesWithV4Shape(string path)
    {
        Execute(path, """
            ALTER TABLE local_workspace_content_tombstones RENAME TO local_workspace_content_tombstones_v5;
            CREATE TABLE local_workspace_content_tombstones(
                source_item_id TEXT NOT NULL,
                part TEXT NOT NULL,
                locator_kind TEXT NOT NULL,
                json_pointer TEXT NULL,
                selected_utf8_bytes INTEGER NULL,
                deleted_at TEXT NOT NULL,
                retention_item_id TEXT NOT NULL,
                retention_revision INTEGER NOT NULL,
                PRIMARY KEY(source_item_id,part));
            INSERT INTO local_workspace_content_tombstones(
                source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,
                deleted_at,retention_item_id,retention_revision)
            SELECT source_item_id,part,locator_kind,json_pointer,selected_utf8_bytes,
                   deleted_at,retention_item_id,retention_revision
            FROM local_workspace_content_tombstones_v5;
            DROP TABLE local_workspace_content_tombstones_v5;
            """);
    }

    private static void SetTerminalContentState(
        string path,
        ISkillRegistryGenerationAuthority authority,
        string state)
    {
        const string deniedAt = "2026-08-27T00:00:00.0000000+00:00";
        const string deletedAt = "2026-08-27T00:00:01.0000000+00:00";
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction();
        if (state == "read_denied")
        {
            Execute(connection, transaction, $"""
                UPDATE retention_items
                SET state='expired_pending_deletion',revision=4,
                    read_denied_at='{deniedAt}',queued_at='{deniedAt}'
                WHERE item_id='{TerminalItemId}';
                """);
        }
        else
        {
            Execute(connection, transaction, $"""
                UPDATE retention_items
                SET state='deleted',revision=5,read_denied_at='{deniedAt}',
                    queued_at='{deniedAt}',deleted_at='{deletedAt}'
                WHERE item_id='{TerminalItemId}';
                INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
                VALUES('{TerminalItemId}','{deletedAt}','{deletedAt}');
                """);
            LocalWorkspaceProjectionStore.CompleteSessionEventContentDeletion(
                connection,
                transaction,
                TerminalEventId,
                DateTimeOffset.Parse(deletedAt));
            Execute(connection, transaction,
                $"DELETE FROM session_event_content WHERE event_id='{TerminalEventId}';");
        }
        LocalWorkspaceProjectionStore.Refresh(connection, transaction, PublicationAt, authority);
        transaction.Commit();
    }

    private static string WorkspaceContentState(string path) => Text(path,
        $"SELECT availability_state FROM local_workspace_node_content_refs WHERE store_kind='session_event_content' AND source_item_id='{TerminalEventId}';");

    private static string TransactionText(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        return (string)command.ExecuteScalar()!;
    }

    private const string TerminalRunId = "0199f5b8-0c00-7000-8000-000000000000";
    private const string TerminalEventId = "0199f5b8-0c00-7000-8000-000000000001";
    private const string TerminalItemId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static readonly string[] RetentionTables =
    [
        "retention_audit_events",
        "retention_operation_receipts",
        "retention_mutation_idempotency",
        "retention_confirmation_bindings",
        "retention_mutation_previews",
        "retention_worker_state",
        "retention_legacy_bundle_journal",
        "retention_legacy_bundle_blockers",
        "retention_analysis_sdk_directory_members",
        "retention_analysis_sdk_directory_reservations",
        "retention_adapter_coverage",
        "retention_delete_journal",
        "retention_leases",
        "retention_file_capture_members",
        "retention_file_capture_reservations",
        "retention_capture_journal",
        "retention_tombstones",
        "retention_items",
        "retention_store_instances",
        "retention_component_versions",
    ];

    private static string CanonicalIdentity(ISkillRegistryGenerationAuthority authority)
    {
        var capture = Assert.IsAssignableFrom<ISkillRegistryGenerationCapture>(authority.CaptureGeneration());
        Assert.True(authority.TryAcquireGenerationReadLease(capture, out var lease));
        using (lease)
        {
            Assert.True(authority.VerifyGenerationIdentity(capture, lease));
            return authority.GetCanonicalGenerationIdentity(capture, lease);
        }
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

    private static void RewriteArchiveDatabase(
        string source,
        string output,
        string databasePath,
        Action<string> mutation)
    {
        byte[] manifest;
        byte[] database;
        using (var archive = ZipFile.OpenRead(source))
        {
            manifest = Read(archive.GetEntry("manifest.json")!);
            database = Read(archive.GetEntry("database.sqlite")!);
        }
        File.WriteAllBytes(databasePath, database);
        mutation(databasePath);
        NormalizeForByteComparison(databasePath);
        database = File.ReadAllBytes(databasePath);
        var parsed = RuntimeBackupJson.ParseManifest(manifest) with
        {
            DatabaseSha256 = Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant(),
            DatabaseSize = database.LongLength,
        };
        using var target = ZipFile.Open(output, ZipArchiveMode.Create);
        Write(target, "manifest.json", RuntimeBackupJson.WriteManifest(parsed));
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
        internal RawTelemetryStore RawStore => fixture.RawStore;
        internal SqliteRuntimeBackupService Service { get; }
        internal string Path(string name) => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(DatabasePath)!, name);
        public void Dispose() => fixture.Dispose();

        private const string ValidRequest = "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"backup-sdk-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v2\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":\"backup-sdk-run\",\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"demo-skill\",\"path\":\"skills/demo/SKILL.md\",\"content\":\"synthetic body\",\"source\":\"project\",\"trigger\":\"user-invoked\"}}]}";
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

        private const string HistoricalRequest = "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"backup-sdk-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":\"backup-sdk-run\",\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"historical-skill\",\"path\":\"skills/historical/SKILL.md\",\"content\":\"synthetic historical body\",\"source\":\"project\",\"trigger\":\"user-invoked\"}}]}";
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

    private sealed class FailingRegistryGenerationAuthority(string failure) : ISkillRegistryGenerationAuthority
    {
        private readonly Generation generation = new();

        public ISkillRegistryGenerationCapture? CaptureGeneration() =>
            failure == "capture" ? null : generation;

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            lease = failure == "lease" || !ReferenceEquals(capture, generation) ? null : generation;
            return lease is not null;
        }

        public bool VerifyGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) =>
            failure != "verify" && ReferenceEquals(capture, generation) && ReferenceEquals(lease, generation);

        public string GetCanonicalGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease)
        {
            if (!VerifyGenerationIdentity(capture, lease))
                throw new InvalidOperationException("skill_registry_generation_not_current");
            var registry = SkillInvocationV2ArtifactRegistry.Load();
            var current = registry.History.Single(item => item.Revision == registry.CurrentRevision);
            return $"{current.Revision}:{current.ArtifactFingerprint}";
        }

        public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple) =>
            ReferenceEquals(lease, generation);

        private sealed class Generation : ISkillRegistryGenerationCapture, ISkillRegistryGenerationLease
        {
            public void Dispose() { }
        }
    }

    private sealed class SingleCaptureRegistryGenerationAuthority : ISkillRegistryGenerationAuthority
    {
        private readonly ISkillRegistryGenerationAuthority inner = FixedSkillRegistryGenerationAuthority.Load();

        internal int CaptureCount { get; private set; }
        internal int LeaseDisposeCount { get; private set; }

        public ISkillRegistryGenerationCapture? CaptureGeneration()
        {
            CaptureCount++;
            return CaptureCount == 1 ? inner.CaptureGeneration() : null;
        }

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture capture,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
        {
            if (!inner.TryAcquireGenerationReadLease(capture, out var innerLease))
            {
                lease = null;
                return false;
            }
            lease = new CountingLease(innerLease, () => LeaseDisposeCount++);
            return true;
        }

        public bool VerifyGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) =>
            lease is CountingLease counting
            && inner.VerifyGenerationIdentity(capture, counting.Inner);

        public string GetCanonicalGenerationIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) =>
            lease is CountingLease counting
                ? inner.GetCanonicalGenerationIdentity(capture, counting.Inner)
                : throw new InvalidOperationException();

        public string? GetCanonicalArtifactAuthorityIdentity(
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease) =>
            lease is CountingLease counting
                ? inner.GetCanonicalArtifactAuthorityIdentity(capture, counting.Inner)
                : null;

        public bool IsProducerTupleAccepted(
            ISkillRegistryGenerationLease lease,
            SkillRegistryProducerTuple tuple) =>
            lease is CountingLease counting
            && inner.IsProducerTupleAccepted(counting.Inner, tuple);

        private sealed class CountingLease(
            ISkillRegistryGenerationLease inner,
            Action dispose) : ISkillRegistryGenerationLease
        {
            private int disposed;

            internal ISkillRegistryGenerationLease Inner { get; } = inner;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                Inner.Dispose();
                dispose();
            }
        }
    }

    private sealed class SimulatedProcessCrashException : Exception;
}

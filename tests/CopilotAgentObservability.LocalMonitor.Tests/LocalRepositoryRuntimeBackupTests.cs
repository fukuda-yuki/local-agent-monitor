using System.Globalization;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.RawReplay;
using CopilotAgentObservability.SanitizedExport;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryRuntimeBackupTests
{
    private const string ColdComposerChild = "CAO_LOCAL_REPOSITORY_BACKUP_COLD_CHILD";
    private const string ColdComposerDatabase = "CAO_LOCAL_REPOSITORY_BACKUP_COLD_DATABASE";
    private const string AfterInitializeCatalogTail = SqliteRuntimeBackupService.CatalogAfterInitializeTailCheckpoint;
    private const string AfterMonitorCatalogTail = SqliteRuntimeBackupService.CatalogAfterMonitorTailCheckpoint;
    private const string AfterCreateCatalogTail = SqliteRuntimeBackupService.CatalogAfterCreateTailCheckpoint;

    [Fact]
    public async Task CurrentCatalogPreflightAndRoundTripPreserveEveryCatalogTable()
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        var expected = ReadCatalogSnapshot(fixture.DatabasePath);
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var bundle = Path.Combine(directory, "local-repository-runtime.backup.zip");
        var target = Path.Combine(directory, "local-repository-runtime.restored.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);

        ValidateCatalog(fixture.DatabasePath);
        var sourcePreflight = service.PreflightForMigration(fixture.DatabasePath);
        var created = service.CreateAndPublish(fixture.DatabasePath, bundle);
        var restored = service.Restore(bundle, target, new RuntimeRestoreOptions());
        var targetPreflight = service.PreflightForMigration(target);

        Assert.True(sourcePreflight.Success, sourcePreflight.ErrorCode);
        Assert.Equal(1, sourcePreflight.ComponentVersions!["local_repository_catalog"]);
        Assert.True(created.Success, created.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.True(targetPreflight.Success, targetPreflight.ErrorCode);
        Assert.Equal(1, targetPreflight.ComponentVersions!["local_repository_catalog"]);
        Assert.Equal(expected, ReadCatalogSnapshot(target));
    }

    [Fact]
    public void CurrentTerminalEventTypeWithoutPersistedFactIsRejectedBeforeBackupPublication()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        fixture.CreateSession(LocalRepositoryCatalogFixture.SessionId(3_701));
        fixture.Execute("UPDATE session_events SET terminal_outcome=NULL,terminal_policy_version=NULL WHERE type='session.task_complete';");
        var archive = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "missing-terminal-fact.zip");

        var result = new SqliteRuntimeBackupService(TimeProvider.System).CreateAndPublish(fixture.DatabasePath, archive);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.False(File.Exists(archive));
    }

    [Fact]
    public async Task ExactLegacySession13CatalogMigratesFirstAndPreservesEveryCatalogChild()
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        var expected = ReadCatalogSnapshot(fixture.DatabasePath);
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var currentBundle = Path.Combine(directory, "current-catalog.zip");
        var legacyBundle = Path.Combine(directory, "legacy-session13-catalog.zip");
        var target = Path.Combine(directory, "legacy-session13-catalog-restored.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, currentBundle).Success);
        var sourceHash = DatabaseHash(fixture.DatabasePath);
        RewriteArchiveDatabase(
            currentBundle,
            legacyBundle,
            path =>
            {
                using var connection = OpenWritable(path);
                using (var removeArchive = connection.CreateCommand())
                {
                    // skill_invocation_snapshot is parented to Session 14 exactly, so a downgraded
                    // Session 13 archive that kept it would be incompatible rather than legacy.
                    removeArchive.CommandText =
                        "DELETE FROM schema_version WHERE component IN ('local_archive','skill_invocation_snapshot','local_workspace_projection');"
                        + "DROP TABLE local_archive_events; DROP TABLE local_archive_current;"
                        + "DROP TRIGGER IF EXISTS skill_invocation_snapshot_session_event_update_rejected;"
                        + "DROP TRIGGER IF EXISTS skill_invocation_snapshot_session_event_delete_rejected;"
                        + "DROP TABLE IF EXISTS skill_invocation_snapshot_receipts;"
                        + "DROP TABLE IF EXISTS skill_invocation_snapshots;"
                        + "DROP TABLE IF EXISTS local_workspace_session_sources;"
                        + "DROP TABLE IF EXISTS local_workspace_session_models;"
                        + "DROP TABLE IF EXISTS local_workspace_session_activity;"
                        + "DROP TABLE IF EXISTS local_workspace_token_observations;"
                        + "DROP TABLE IF EXISTS local_workspace_span_facts;"
                        + "DROP TABLE IF EXISTS local_workspace_projection_state;"
                        + "DROP TABLE IF EXISTS local_workspace_sessions;";
                    removeArchive.ExecuteNonQuery();
                }
                SessionVersion13TestFixture.DowngradeSessionEvents(connection);
            },
            manifest => manifest with
            {
                ComponentVersions = new SortedDictionary<string, int>(
                    manifest.ComponentVersions
                        .Where(static item => item.Key is not ("local_archive" or "skill_invocation_snapshot" or "local_workspace_projection"))
                        .ToDictionary(static item => item.Key, static item => item.Value),
                    StringComparer.Ordinal)
                { ["session"] = 13 },
                RowCounts = new SortedDictionary<string, long>(
                    manifest.RowCounts
                        .Where(static item => item.Key is not (
                            "local_archive_current"
                            or "local_archive_events"
                            or "skill_invocation_snapshots"
                            or "skill_invocation_snapshot_receipts") && !item.Key.StartsWith("local_workspace_", StringComparison.Ordinal))
                        .ToDictionary(static item => item.Key, static item => item.Value),
                    StringComparer.Ordinal),
            });

        var inspected = service.Inspect(legacyBundle);
        var restored = service.Restore(legacyBundle, target, new RuntimeRestoreOptions());

        Assert.True(inspected.Success, inspected.ErrorCode);
        Assert.Equal(13, inspected.ComponentVersions!["session"]);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.Equal(sourceHash, DatabaseHash(fixture.DatabasePath));
        Assert.Equal(expected, ReadCatalogSnapshot(target));
        using var migrated = Open(target);
        Assert.Equal(14L, ScalarLong(target, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(2L, ScalarLong(target, "SELECT version FROM schema_version WHERE component='local_workspace_projection';"));
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(migrated, null));
        Assert.Equal(0L, ScalarLong(target, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal("session_events", StringJoin(target, "SELECT DISTINCT \"table\" FROM pragma_foreign_key_list('session_repository_observation_contexts') WHERE \"from\"='session_event_id';"));
    }

    [Fact]
    public async Task ComposerUsesTheApprovedPhaseOrderAndOneCallerOwnedReadTransaction()
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        var observer = new ValidationObserver();
        using var connection = Open(fixture.DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: true);

        LocalRepositoryCatalogBackupValidation.Validate(connection, transaction, observer);

        Assert.Equal(
            [
                LocalRepositoryCatalogBackupValidationPhase.Structure,
                LocalRepositoryCatalogBackupValidationPhase.Guards,
                LocalRepositoryCatalogBackupValidationPhase.RawReferences,
                LocalRepositoryCatalogBackupValidationPhase.Reconciliation,
                LocalRepositoryCatalogBackupValidationPhase.AutomaticAdmission,
                LocalRepositoryCatalogBackupValidationPhase.Mutation,
            ],
            observer.Phases);
        Assert.All(observer.RawIdPages, count => Assert.InRange(count, 1, 128));
        Assert.All(observer.RawPayloadResidentCounts, count => Assert.Equal(1, count));
        Assert.Equal(1, observer.RawPayloadHashCount);
        transaction.Rollback();
    }

    [Fact]
    public async Task ComposerFirstCallUsesOnlyTheCallerOwnedHandleAndReadTransaction()
    {
        if (Environment.GetEnvironmentVariable(ColdComposerChild) == "1")
        {
            AssertColdComposerChild(Environment.GetEnvironmentVariable(ColdComposerDatabase));
            return;
        }

        using var fixture = await CreatePopulatedCatalogAsync();
        var result = await RunColdComposerChildAsync(fixture.DatabasePath);

        Assert.True(
            result.ExitCode == 0,
            $"Cold composer child failed with exit code {result.ExitCode}.{Environment.NewLine}{result.Output}{Environment.NewLine}{result.Error}");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("older")]
    [InlineData("newer")]
    [InlineData("shape")]
    public async Task CatalogRequiresCurrentOrExactLegacySessionWithoutRepairOrMutation(string contradiction)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        ApplySessionContradiction(fixture.DatabasePath, contradiction);
        var before = DatabaseHash(fixture.DatabasePath);

        var result = new SqliteRuntimeBackupService(fixture.Clock).PreflightForMigration(fixture.DatabasePath);

        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("stamp-missing")]
    [InlineData("stamp-future")]
    [InlineData("table-missing")]
    [InlineData("table-extra-column")]
    [InlineData("index-missing")]
    [InlineData("index-definition")]
    [InlineData("trigger-missing")]
    [InlineData("trigger-definition")]
    [InlineData("reserved-extra")]
    [InlineData("reserved-index-extra")]
    [InlineData("reserved-uppercase")]
    public async Task PreflightRejectsPartialOrCounterfeitCatalogNamespaceWithoutMutation(string contradiction)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        ApplyNamespaceContradiction(fixture.DatabasePath, contradiction);
        var before = DatabaseHash(fixture.DatabasePath);
        var observer = new ValidationObserver();

        var direct = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));
        var result = new SqliteRuntimeBackupService(fixture.Clock).PreflightForMigration(fixture.DatabasePath);

        Assert.NotNull(direct);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.Structure, Assert.Single(observer.Phases));
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Theory]
    [MemberData(nameof(CatalogStorageGuardCases))]
    public async Task CatalogStorageGuardCasesRejectBeforeAnySemanticReader(StorageGuardCase guard)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        CorruptUpdate(fixture.DatabasePath, guard.Table, guard.Sql, guard.Value);
        var before = DatabaseHash(fixture.DatabasePath);
        var observer = new ValidationObserver();

        var direct = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));
        var result = new SqliteRuntimeBackupService(fixture.Clock).PreflightForMigration(fixture.DatabasePath);

        Assert.NotNull(direct);
        Assert.Equal(
            [LocalRepositoryCatalogBackupValidationPhase.Structure, LocalRepositoryCatalogBackupValidationPhase.Guards],
            observer.Phases);
        Assert.Empty(observer.RawIdPages);
        Assert.Empty(observer.RawPayloadResidentCounts);
        Assert.Equal(0, observer.RawPayloadHashCount);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Theory]
    [MemberData(nameof(ReachableOwnerStorageGuardCases))]
    public async Task ReachableOwnerStorageGuardCasesRejectBeforeAnySemanticReader(StorageGuardCase guard)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        CorruptUpdate(fixture.DatabasePath, guard.Table, guard.Sql, guard.Value);
        var before = DatabaseHash(fixture.DatabasePath);
        var observer = new ValidationObserver();

        var direct = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));
        var result = new SqliteRuntimeBackupService(fixture.Clock).PreflightForMigration(fixture.DatabasePath);

        Assert.NotNull(direct);
        Assert.Equal(
            [LocalRepositoryCatalogBackupValidationPhase.Structure, LocalRepositoryCatalogBackupValidationPhase.Guards],
            observer.Phases);
        Assert.Empty(observer.RawIdPages);
        Assert.Empty(observer.RawPayloadResidentCounts);
        Assert.Equal(0, observer.RawPayloadHashCount);
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Theory]
    [MemberData(nameof(NullableStorageControls))]
    public async Task NullableStorageControlsAdvanceBeyondTheGuardPhase(StorageGuardCase control)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        CorruptUpdate(fixture.DatabasePath, control.Table, control.Sql, control.Value);
        var before = DatabaseHash(fixture.DatabasePath);
        var observer = new ValidationObserver();

        _ = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));

        Assert.True(observer.Phases.Count >= 3, $"{control.Id}: {string.Join(',', observer.Phases)}");
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.Structure, observer.Phases[0]);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.Guards, observer.Phases[1]);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.RawReferences, observer.Phases[2]);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("direct-guard")]
    [InlineData("producer-guard")]
    [InlineData("valid-values-semantic")]
    public async Task SplitSessionEventCandidatesAreBothGuardedAndNeverHeuristicallyMerged(string splitCase)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        var (producerEventId, directEventId) = ArrangeSplitSessionEventCandidates(fixture.DatabasePath);
        if (splitCase == "direct-guard")
        {
            CorruptUpdate(
                fixture.DatabasePath,
                "session_events",
                "UPDATE session_events SET trace_id=zeroblob(32) WHERE event_id=$value;",
                directEventId);
        }
        else if (splitCase == "producer-guard")
        {
            CorruptUpdate(
                fixture.DatabasePath,
                "session_events",
                "UPDATE session_events SET type=zeroblob(1) WHERE event_id=$value;",
                producerEventId);
        }
        var before = DatabaseHash(fixture.DatabasePath);
        var observer = new ValidationObserver();

        var direct = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));
        var result = new SqliteRuntimeBackupService(fixture.Clock).PreflightForMigration(fixture.DatabasePath);

        Assert.NotNull(direct);
        if (splitCase == "valid-values-semantic")
        {
            Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.AutomaticAdmission, observer.Phases[^1]);
            Assert.Contains(LocalRepositoryCatalogBackupValidationPhase.Reconciliation, observer.Phases);
        }
        else
        {
            Assert.Equal(
                [LocalRepositoryCatalogBackupValidationPhase.Structure, LocalRepositoryCatalogBackupValidationPhase.Guards],
                observer.Phases);
            Assert.Empty(observer.RawIdPages);
            Assert.Equal(0, observer.RawPayloadHashCount);
        }
        Assert.False(result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Theory]
    [InlineData("reconciliation")]
    [InlineData("automatic-admission")]
    [InlineData("mutation")]
    public async Task SemanticContradictionsReachTheOrderedOwnerSeamAndMapToFixedIncompatibility(
        string contradiction)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        ApplySemanticContradiction(fixture.DatabasePath, contradiction);
        var before = DatabaseHash(fixture.DatabasePath);
        var observer = new ValidationObserver();

        var direct = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));
        var preflight = new SqliteRuntimeBackupService(fixture.Clock)
            .PreflightForMigration(fixture.DatabasePath);

        Assert.NotNull(direct);
        Assert.Equal(contradiction switch
        {
            "reconciliation" => LocalRepositoryCatalogBackupValidationPhase.Reconciliation,
            "automatic-admission" => LocalRepositoryCatalogBackupValidationPhase.AutomaticAdmission,
            "mutation" => LocalRepositoryCatalogBackupValidationPhase.Mutation,
            _ => throw new ArgumentOutOfRangeException(nameof(contradiction)),
        }, observer.Phases[^1]);
        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Fact]
    public void RawReferenceTraversalUsesBoundedKeysetsHashesEachPayloadOnceAndIsPerCall()
    {
        using var fixture = new BoundedRawCatalogFixture(257);
        var accepted = new ValidationObserver();

        ValidateObserved(fixture.DatabasePath, accepted);

        Assert.Equal([128, 128, 1], accepted.RawIdPages);
        Assert.Equal(257, accepted.RawPayloadResidentCounts.Count);
        Assert.All(accepted.RawPayloadResidentCounts, count => Assert.Equal(1, count));
        Assert.Equal(257, accepted.RawPayloadHashCount);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.Mutation, accepted.Phases[^1]);

        fixture.CorruptLastPayload();
        var before = DatabaseHash(fixture.DatabasePath);
        var rejected = new ValidationObserver();
        var error = Record.Exception(() => ValidateObserved(fixture.DatabasePath, rejected));
        var publicResult = new SqliteRuntimeBackupService().PreflightForMigration(fixture.DatabasePath);

        Assert.NotNull(error);
        Assert.Equal([128, 128, 1], rejected.RawIdPages);
        Assert.Equal(257, rejected.RawPayloadResidentCounts.Count);
        Assert.All(rejected.RawPayloadResidentCounts, count => Assert.Equal(1, count));
        Assert.Equal(257, rejected.RawPayloadHashCount);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.RawReferences, rejected.Phases[^1]);
        Assert.Equal(257, accepted.RawPayloadHashCount);
        Assert.NotSame(accepted, rejected);
        Assert.False(publicResult.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, publicResult.ErrorCode);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));

        using var oneRowFixture = new BoundedRawCatalogFixture(1);
        var oneRow = new ValidationObserver();
        ValidateObserved(oneRowFixture.DatabasePath, oneRow);
        Assert.Equal([1], oneRow.RawIdPages);
        Assert.Equal([1], oneRow.RawPayloadResidentCounts);
        Assert.Equal(1, oneRow.RawPayloadHashCount);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.Mutation, oneRow.Phases[^1]);
    }

    [Fact]
    public async Task RawDigestMismatchIsFixedIncompatibilityAndBackupDoesNotPublishOrMutate()
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        Execute(fixture.DatabasePath, "UPDATE raw_records SET payload_json='{}';");
        var before = DatabaseHash(fixture.DatabasePath);
        var output = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "must-not-publish.zip");
        var observer = new ValidationObserver();
        var service = new SqliteRuntimeBackupService(fixture.Clock);

        var direct = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));
        var preflight = service.PreflightForMigration(fixture.DatabasePath);
        var created = service.CreateAndPublish(fixture.DatabasePath, output);

        Assert.NotNull(direct);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.RawReferences, observer.Phases[^1]);
        Assert.Equal(1, observer.RawPayloadHashCount);
        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(created.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, created.ErrorCode);
        Assert.False(File.Exists(output));
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Fact]
    public void OwnerSemanticArchiveCoverageInventoryIsExact()
    {
        var scenarios = OwnerSemanticArchiveScenarios().ToArray();
        var expectedTags = AcceptedOwnerSemanticCoverageTags();
        var actualTags = scenarios
            .SelectMany(static scenario => scenario.CoverageTags)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var missing = expectedTags.Except(actualTags, StringComparer.Ordinal).ToArray();
        var unexpected = actualTags.Except(expectedTags, StringComparer.Ordinal).ToArray();

        Assert.True(missing.Length == 0, $"Missing coverage tags: {string.Join(',', missing)}");
        Assert.True(unexpected.Length == 0, $"Unexpected coverage tags: {string.Join(',', unexpected)}");
        Assert.Equal(expectedTags.Length, actualTags.Length);
        Assert.Equal(126, scenarios.Length);
        Assert.Equal(126, scenarios.Select(static scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, scenarios.Count(static scenario => scenario.Succeeds));
        var physicalIntegrityCases = scenarios
            .Where(static scenario => !scenario.Succeeds && !scenario.ComposerReachable)
            .ToArray();
        Assert.Single(physicalIntegrityCases);
        Assert.Equal("L4-head-owner", physicalIntegrityCases[0].Id);
        Assert.Equal(RuntimeBackupErrorCodes.ArchiveInvalid, physicalIntegrityCases[0].ExpectedErrorCode);
    }

    [Theory]
    [InlineData("L5-locator-129")]
    [InlineData("R9-late-page")]
    public async Task CardinalityAndLateReceiptScenariosReachTheirMutationAuthorities(string caseId)
    {
        var archiveCase = OwnerSemanticArchiveScenarios().Single(scenario => scenario.Id == caseId);
        using var fixture = await CreateOwnerSemanticArchiveFixtureAsync(archiveCase);
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var valid = Path.Combine(directory, $"authority-valid-{caseId}.zip");
        var corrupt = Path.Combine(directory, $"authority-corrupt-{caseId}.zip");
        var probe = Path.Combine(directory, $"authority-probe-{caseId}.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, valid).Success);
        RewriteArchiveDatabase(valid, corrupt, path => ApplyOwnerSemanticArchiveMutation(path, caseId));
        ExtractArchiveDatabase(corrupt, probe);
        AssertAcceptedMutationContradictionShape(caseId, probe);
        var before = DatabaseHash(probe);
        var observer = new ValidationObserver();

        var error = Record.Exception(() => ValidateObserved(probe, observer));

        Assert.NotNull(error);
        Assert.Equal(LocalRepositoryCatalogBackupValidationPhase.Mutation, observer.Phases[^1]);
        Assert.Equal(before, DatabaseHash(probe));
    }

    [Theory]
    [MemberData(nameof(OwnerSemanticArchiveCases))]
    public async Task OwnerSemanticArchivesHaveOneFixedClassificationAcrossPathSurfaces(
        OwnerSemanticArchiveCase archiveCase)
    {
        using var fixture = await CreateOwnerSemanticArchiveFixtureAsync(archiveCase);
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var valid = Path.Combine(directory, $"valid-{archiveCase.Id}.zip");
        var corrupt = Path.Combine(directory, $"corrupt-{archiveCase.Id}.zip");
        var previewTarget = Path.Combine(directory, $"preview-{archiveCase.Id}.db");
        var restoreTarget = Path.Combine(directory, $"restore-{archiveCase.Id}.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, valid).Success);
        RewriteArchiveDatabase(valid, corrupt, path => ApplyOwnerSemanticArchiveMutation(path, archiveCase.Id));
        ExtractArchiveDatabase(valid, restoreTarget);
        var archiveBefore = File.ReadAllBytes(corrupt);
        var targetBefore = File.ReadAllBytes(restoreTarget);

        var inspection = service.Inspect(corrupt);
        var preview = service.Preview(corrupt, previewTarget);
        var restored = service.Restore(corrupt, restoreTarget, new RuntimeRestoreOptions());

        AssertFixedOwnerSemanticRejection(inspection.Success, inspection.ErrorCode, archiveCase);
        AssertFixedOwnerSemanticRejection(preview.Success, preview.ErrorCode, archiveCase);
        AssertFixedOwnerSemanticRejection(restored.Success, restored.ErrorCode, archiveCase);
        Assert.Equal(archiveBefore, File.ReadAllBytes(corrupt));
        Assert.False(File.Exists(previewTarget));
        Assert.Equal(targetBefore, File.ReadAllBytes(restoreTarget));
        AssertNoRuntimeBackupArtifacts(directory, previewTarget, restoreTarget);
    }

    [Theory]
    [MemberData(nameof(OwnerSemanticArchiveSuccessCases))]
    public async Task OwnerSemanticSuccessControlsRoundTripAcrossPathSurfaces(
        OwnerSemanticArchiveCase archiveCase)
    {
        using var fixture = await CreateOwnerSemanticArchiveFixtureAsync(archiveCase);
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var archive = Path.Combine(directory, $"success-{archiveCase.Id}.zip");
        var previewTarget = Path.Combine(directory, $"success-preview-{archiveCase.Id}.db");
        var restoreTarget = Path.Combine(directory, $"success-restore-{archiveCase.Id}.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, archive).Success);
        var archiveBefore = File.ReadAllBytes(archive);

        var inspection = service.Inspect(archive);
        var preview = service.Preview(archive, previewTarget);
        var restored = service.Restore(archive, restoreTarget, new RuntimeRestoreOptions());

        Assert.True(inspection.Success, inspection.ErrorCode);
        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        Assert.False(File.Exists(previewTarget));
        Assert.True(File.Exists(restoreTarget));
        AssertOwnerSemanticSuccessFacts(archiveCase.Id, restoreTarget);
        Assert.Equal(archiveBefore, File.ReadAllBytes(archive));
        AssertNoRuntimeBackupArtifacts(directory, previewTarget, restoreTarget);
    }

    [Theory]
    [InlineData("L2-locator-digest")]
    [InlineData("O2-context-identity")]
    [InlineData("Q7-digest-disagreement")]
    [InlineData("H5-bad-before-endpoint")]
    [InlineData("R5-linked-action")]
    [InlineData("RAW5-digest-disagreement")]
    public async Task OwnerSemanticFamiliesReachEveryApplicableStreamPreviewSurface(string caseId)
    {
        var archiveCase = caseId == "RAW5-digest-disagreement"
            ? CorruptScenario(caseId)
            : OwnerSemanticArchiveScenarios().Single(scenario => scenario.Id == caseId);
        using var fixture = await CreateOwnerSemanticArchiveFixtureAsync(archiveCase);
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var valid = Path.Combine(directory, $"stream-valid-{caseId}.zip");
        var corrupt = Path.Combine(directory, $"stream-corrupt-{caseId}.zip");
        var fileTarget = Path.Combine(directory, $"stream-file-{caseId}.db");
        var asyncTarget = Path.Combine(directory, $"stream-async-{caseId}.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, valid).Success);
        RewriteArchiveDatabase(valid, corrupt, path => ApplyOwnerSemanticArchiveMutation(path, caseId));
        ExtractArchiveDatabase(valid, fileTarget);
        var archiveBytes = File.ReadAllBytes(corrupt);
        var targetBefore = File.ReadAllBytes(fileTarget);

        RuntimeBackupInspectionResult inspection;
        using (var stream = new FileStream(corrupt, FileMode.Open, FileAccess.Read, FileShare.Read))
            inspection = service.Inspect(stream);
        RuntimeRestorePreview preview;
        using (var stream = new FileStream(corrupt, FileMode.Open, FileAccess.Read, FileShare.Read))
            preview = service.Preview(stream, fileTarget);
        var asyncPreview = await service.PreviewAsync(
            new MemoryStream(archiveBytes, writable: false),
            asyncTarget,
            CancellationToken.None);

        AssertFixedIncompatibility(inspection.Success, inspection.ErrorCode, caseId);
        AssertFixedIncompatibility(preview.Success, preview.ErrorCode, caseId);
        AssertFixedIncompatibility(asyncPreview.Success, asyncPreview.ErrorCode, caseId);
        Assert.Equal(archiveBytes, File.ReadAllBytes(corrupt));
        Assert.Equal(targetBefore, File.ReadAllBytes(fileTarget));
        Assert.False(File.Exists(asyncTarget));
        AssertNoRuntimeBackupArtifacts(directory, fileTarget, asyncTarget);
    }

    [Theory]
    [InlineData("RAW1", "available", true)]
    [InlineData("RAW2", "unknown", true)]
    [InlineData("RAW3", "expired", true)]
    [InlineData("RAW4", null, false)]
    [InlineData("RAW5", null, false)]
    [InlineData("RAW6", "unknown", true)]
    public async Task OpaqueRawReferenceAndRetentionAvailabilityRoundTripMatrix(
        string rawCase,
        string? expectedAvailability,
        bool succeeds)
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var valid = Path.Combine(directory, $"{rawCase}-valid.zip");
        var transformed = Path.Combine(directory, $"{rawCase}-transformed.zip");
        var previewTarget = Path.Combine(directory, $"{rawCase}-preview.db");
        var restoreTarget = Path.Combine(directory, $"{rawCase}-restored.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, valid).Success);
        if (rawCase == "RAW1")
        {
            File.Copy(valid, transformed);
        }
        else if (succeeds)
        {
            RepublishArchiveDatabase(
                valid,
                transformed,
                fixture.Clock,
                path => ApplyRestorableRawMutation(path, rawCase, fixture.Clock));
        }
        else
        {
            RewriteArchiveDatabase(
                valid,
                transformed,
                path => ApplyOwnerSemanticArchiveMutation(path, rawCase + (rawCase == "RAW5" ? "-digest-disagreement" : "-payload-mismatch")));
        }
        var archiveBefore = File.ReadAllBytes(transformed);

        var inspection = service.Inspect(transformed);
        var preview = service.Preview(transformed, previewTarget);
        var restored = service.Restore(transformed, restoreTarget, new RuntimeRestoreOptions());

        if (!succeeds)
        {
            AssertFixedIncompatibility(inspection.Success, inspection.ErrorCode, rawCase);
            AssertFixedIncompatibility(preview.Success, preview.ErrorCode, rawCase);
            AssertFixedIncompatibility(restored.Success, restored.ErrorCode, rawCase);
            Assert.False(File.Exists(previewTarget));
            Assert.False(File.Exists(restoreTarget));
            Assert.Equal(archiveBefore, File.ReadAllBytes(transformed));
            AssertNoRuntimeBackupArtifacts(directory, previewTarget, restoreTarget);
            return;
        }

        Assert.True(inspection.Success, inspection.ErrorCode);
        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(restored.Success, restored.ErrorCode);
        var repositoryId = StringJoin(restoreTarget, "SELECT repository_id FROM local_repositories LIMIT 1;");
        var found = Assert.IsType<LocalRepositoryLocatorsFound>(
            await CreateReadApplication(restoreTarget).ReadLocatorsAsync(repositoryId, CancellationToken.None));
        var locator = Assert.Single(found.Value.Locators);
        Assert.NotNull(locator.Provenance);
        Assert.Equal(expectedAvailability, locator.Provenance.SourceContentAvailability);
        if (rawCase is "RAW2" or "RAW6")
            Assert.NotEqual("not_retained", locator.Provenance.SourceContentAvailability);
        Assert.Equal(archiveBefore, File.ReadAllBytes(transformed));
        AssertNoRuntimeBackupArtifacts(directory, previewTarget, restoreTarget);
    }

    [Fact]
    public async Task DeletedRawTombstoneWithStaleSpanFactIsRejectedAcrossBackupSurfaces()
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        var directory = Path.GetDirectoryName(fixture.DatabasePath)!;
        var valid = Path.Combine(directory, "deleted-fact-valid.zip");
        var deleted = Path.Combine(directory, "deleted-fact-canonical.zip");
        var corrupt = Path.Combine(directory, "deleted-fact-corrupt.zip");
        var previewTarget = Path.Combine(directory, "deleted-fact-preview.db");
        var restoreTarget = Path.Combine(directory, "deleted-fact-restore.db");
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        Assert.True(service.CreateAndPublish(fixture.DatabasePath, valid).Success);
        RepublishArchiveDatabase(valid, deleted, fixture.Clock, path => ApplyRestorableRawMutation(path, "RAW3", fixture.Clock));
        RewriteArchiveDatabase(deleted, corrupt, path => Execute(path, """
            INSERT INTO local_workspace_span_facts(raw_record_id,span_ordinal,retry_count,producer_total_tokens)
            SELECT CAST(i.source_item_id AS INTEGER),s.span_ordinal,NULL,NULL
            FROM retention_items i JOIN monitor_spans s ON s.raw_record_id=CAST(i.source_item_id AS INTEGER)
            WHERE i.store_kind='raw_record' AND i.state='deleted' LIMIT 1;
            """));

        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, service.Inspect(corrupt).ErrorCode);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, service.Preview(corrupt, previewTarget).ErrorCode);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, service.Restore(corrupt, restoreTarget, new RuntimeRestoreOptions()).ErrorCode);
        Assert.False(File.Exists(previewTarget));
        Assert.False(File.Exists(restoreTarget));
    }

    [Fact]
    public async Task MonitorStartupRejectsCatalogCorruptionBeforeReturningTheOwnerLease()
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        ApplyStorageContradiction(fixture.DatabasePath, "catalog-text");
        var before = DatabaseHash(fixture.DatabasePath);

        var initialization = new SqliteRuntimeBackupService(fixture.Clock)
            .InitializeForMonitor(fixture.DatabasePath);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Fact]
    public void MonitorStartupRejectsUndeclaredReservedCatalogObjectsBeforeReturningTheOwnerLease()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        Execute(fixture.DatabasePath, "DELETE FROM schema_version WHERE component='local_repository_catalog';");
        var before = DatabaseHash(fixture.DatabasePath);

        var initialization = new SqliteRuntimeBackupService(fixture.Clock)
            .InitializeForMonitor(fixture.DatabasePath);

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.Equal(before, DatabaseHash(fixture.DatabasePath));
    }

    [Fact]
    public void MonitorStartupRejectsUndeclaredReservedCatalogObjectsWithoutSharedComponentMetadata()
    {
        using var temp = new MonitorTempDirectory();
        Execute(temp.DatabasePath, "CREATE TABLE local_repository_task10_extra(id INTEGER PRIMARY KEY);");
        var before = DatabaseHash(temp.DatabasePath);

        var initialization = new SqliteRuntimeBackupService(temp.TimeProvider)
            .InitializeForMonitor(temp.DatabasePath);
        using var lease = initialization.Lease;

        Assert.False(initialization.Result.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, initialization.Result.ErrorCode);
        Assert.Null(initialization.Lease);
        Assert.Equal(before, DatabaseHash(temp.DatabasePath));
    }

    [Fact]
    public void LiveTailKeepsNoSessionLegacyBranchDistinctAndInstallsCatalogOnlyAfterSessionExists()
    {
        using var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var service = new SqliteRuntimeBackupService(temp.TimeProvider);

        var legacy = service.Initialize(temp.DatabasePath);

        Assert.True(legacy.Success, legacy.ErrorCode);
        Assert.Equal(1, ScalarLong(temp.DatabasePath, "SELECT version FROM schema_version WHERE component='runtime_backup';"));
        Assert.Equal(0, ScalarLong(temp.DatabasePath, "SELECT COUNT(*) FROM schema_version WHERE component IN ('session','local_repository_catalog');"));

        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        var current = service.Initialize(temp.DatabasePath);

        Assert.True(current.Success, current.ErrorCode);
        Assert.Equal(14, ScalarLong(temp.DatabasePath, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(1, ScalarLong(temp.DatabasePath, "SELECT version FROM schema_version WHERE component='local_repository_catalog';"));
        Assert.All(LocalRepositoryCatalogSchemaV1.TableNames, table =>
            Assert.Equal(table == "local_repository_reconciliation_state" ? 1 : 0, ScalarLong(temp.DatabasePath, $"SELECT COUNT(*) FROM \"{table}\";")));
        Assert.Equal(1, ScalarLong(temp.DatabasePath, """
            SELECT COUNT(*) FROM local_repository_reconciliation_state
            WHERE projector_key='local-repository-catalog-v1'
              AND last_discovered_span_id IS NULL
              AND updated_at='1970-01-01T00:00:00.0000000+00:00';
            """));
    }

    [Fact]
    public async Task InitializeValidatesCatalogBeforeAndAfterTail()
    {
        using (var corrupt = await CreatePopulatedCatalogAsync())
        {
            ApplyStorageContradiction(corrupt.DatabasePath, "catalog-text");
            var before = DatabaseHash(corrupt.DatabasePath);
            var checkpointCount = 0;
            var result = new SqliteRuntimeBackupService(corrupt.Clock, checkpoint =>
            {
                if (checkpoint == AfterInitializeCatalogTail) checkpointCount++;
            }).Initialize(corrupt.DatabasePath);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
            Assert.Equal(0, checkpointCount);
            Assert.Equal(before, DatabaseHash(corrupt.DatabasePath));
        }

        using (var valid = CreateCurrentSessionWithoutCatalog())
        {
            var checkpointCount = 0;
            var result = new SqliteRuntimeBackupService(valid.TimeProvider, checkpoint =>
            {
                if (checkpoint == AfterInitializeCatalogTail) checkpointCount++;
            }).Initialize(valid.DatabasePath);

            Assert.True(result.Success, result.ErrorCode);
            Assert.Equal(1, checkpointCount);
            AssertEmptyCatalogInstalled(valid.DatabasePath);
        }

        using (var corruptAfterTail = CreateCurrentSessionWithoutCatalog())
        {
            var checkpointCount = 0;
            byte[]? afterCorruption = null;
            var result = new SqliteRuntimeBackupService(corruptAfterTail.TimeProvider, checkpoint =>
            {
                if (checkpoint != AfterInitializeCatalogTail) return;
                checkpointCount++;
                CorruptNewlyInstalledCatalog(corruptAfterTail.DatabasePath);
                afterCorruption = DatabaseHash(corruptAfterTail.DatabasePath);
            }).Initialize(corruptAfterTail.DatabasePath);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
            Assert.Equal(1, checkpointCount);
            Assert.NotNull(afterCorruption);
            Assert.Equal(afterCorruption, DatabaseHash(corruptAfterTail.DatabasePath));
        }
    }

    [Fact]
    public async Task MonitorPreparationAndCompletionValidateCatalogBeforeAndAfterTail()
    {
        using (var corrupt = await CreatePopulatedCatalogAsync())
        {
            var checkpointCount = 0;
            var service = new SqliteRuntimeBackupService(corrupt.Clock, checkpoint =>
            {
                if (checkpoint == AfterMonitorCatalogTail) checkpointCount++;
            });
            var preparation = service.InitializeForMonitor(corrupt.DatabasePath);
            using var lease = preparation.Lease;
            Assert.True(preparation.Result.Success, preparation.Result.ErrorCode);
            Assert.NotNull(lease);
            ApplyStorageContradiction(corrupt.DatabasePath, "catalog-text");
            var before = DatabaseHash(corrupt.DatabasePath);

            var result = service.CompleteMonitorInitialization(lease!);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
            Assert.Equal(0, checkpointCount);
            Assert.Equal(before, DatabaseHash(corrupt.DatabasePath));
        }

        using (var valid = CreateCurrentSessionWithoutCatalog())
        {
            var checkpointCount = 0;
            var service = new SqliteRuntimeBackupService(valid.TimeProvider, checkpoint =>
            {
                if (checkpoint == AfterMonitorCatalogTail) checkpointCount++;
            });
            var preparation = service.InitializeForMonitor(valid.DatabasePath);
            using var lease = preparation.Lease;
            Assert.True(preparation.Result.Success, preparation.Result.ErrorCode);

            var result = service.CompleteMonitorInitialization(lease!);

            Assert.True(result.Success, result.ErrorCode);
            Assert.Equal(1, checkpointCount);
            AssertEmptyCatalogInstalled(valid.DatabasePath);
        }

        using (var corruptAfterTail = CreateCurrentSessionWithoutCatalog())
        {
            var checkpointCount = 0;
            byte[]? afterCorruption = null;
            var service = new SqliteRuntimeBackupService(corruptAfterTail.TimeProvider, checkpoint =>
            {
                if (checkpoint != AfterMonitorCatalogTail) return;
                checkpointCount++;
                CorruptNewlyInstalledCatalog(corruptAfterTail.DatabasePath);
                afterCorruption = DatabaseHash(corruptAfterTail.DatabasePath);
            });
            var preparation = service.InitializeForMonitor(corruptAfterTail.DatabasePath);
            using var lease = preparation.Lease;
            Assert.True(preparation.Result.Success, preparation.Result.ErrorCode);

            var result = service.CompleteMonitorInitialization(lease!);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
            Assert.Equal(1, checkpointCount);
            Assert.NotNull(afterCorruption);
            Assert.Equal(afterCorruption, DatabaseHash(corruptAfterTail.DatabasePath));
        }
    }

    [Fact]
    public async Task CreateAndPublishValidatesCatalogBeforeAndAfterTailWithoutPublication()
    {
        using (var corrupt = await CreatePopulatedCatalogAsync())
        {
            ApplyStorageContradiction(corrupt.DatabasePath, "catalog-text");
            var output = Path.Combine(Path.GetDirectoryName(corrupt.DatabasePath)!, "pre-tail-must-not-publish.zip");
            var before = DatabaseHash(corrupt.DatabasePath);
            var receiptsBefore = BackupReceiptCount(corrupt.DatabasePath);
            var checkpointCount = 0;
            var result = new SqliteRuntimeBackupService(corrupt.Clock, checkpoint =>
            {
                if (checkpoint == AfterCreateCatalogTail) checkpointCount++;
            }).CreateAndPublish(corrupt.DatabasePath, output);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
            Assert.Equal(0, checkpointCount);
            Assert.False(File.Exists(output));
            Assert.Equal(receiptsBefore, BackupReceiptCount(corrupt.DatabasePath));
            Assert.Equal(before, DatabaseHash(corrupt.DatabasePath));
        }

        using (var valid = CreateCurrentSessionWithoutCatalog())
        {
            var output = Path.Combine(Path.GetDirectoryName(valid.DatabasePath)!, "post-tail-valid.zip");
            var checkpointCount = 0;
            var service = new SqliteRuntimeBackupService(valid.TimeProvider, checkpoint =>
            {
                if (checkpoint == AfterCreateCatalogTail) checkpointCount++;
            });

            var result = service.CreateAndPublish(valid.DatabasePath, output);

            Assert.True(result.Success, result.ErrorCode);
            Assert.Equal(1, checkpointCount);
            Assert.True(File.Exists(output));
            Assert.True(service.Inspect(output).Success);
        }

        using (var corruptAfterTail = CreateCurrentSessionWithoutCatalog())
        {
            var output = Path.Combine(Path.GetDirectoryName(corruptAfterTail.DatabasePath)!, "post-tail-must-not-publish.zip");
            var checkpointCount = 0;
            byte[]? afterCorruption = null;
            var result = new SqliteRuntimeBackupService(corruptAfterTail.TimeProvider, checkpoint =>
            {
                if (checkpoint != AfterCreateCatalogTail) return;
                checkpointCount++;
                CorruptNewlyInstalledCatalog(corruptAfterTail.DatabasePath);
                afterCorruption = DatabaseHash(corruptAfterTail.DatabasePath);
            }).CreateAndPublish(corruptAfterTail.DatabasePath, output);

            Assert.False(result.Success);
            Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, result.ErrorCode);
            Assert.Equal(1, checkpointCount);
            Assert.NotNull(afterCorruption);
            Assert.False(File.Exists(output));
            Assert.Equal(0, BackupReceiptCount(corruptAfterTail.DatabasePath));
            Assert.Equal(afterCorruption, DatabaseHash(corruptAfterTail.DatabasePath));
        }
    }

    [Fact]
    public void StagedRestoreNormalizesOnlyCapturedCatalogLeasesAndPreservesOtherTypedBytes()
    {
        using var fixture = new LeaseCatalogFixture();
        var before = ReadQueueStoredValues(fixture.DatabasePath);
        fixture.ProveRollback(before);
        var service = new SqliteRuntimeBackupService(fixture.Clock);
        var sourceHash = DatabaseHash(fixture.DatabasePath);
        var observer = new ValidationObserver();
        var composer = Record.Exception(() => ValidateObserved(fixture.DatabasePath, observer));
        Assert.Null(composer);

        var preflight = service.PreflightForMigration(fixture.DatabasePath);
        var afterPreflight = ReadQueueStoredValues(fixture.DatabasePath);
        Assert.True(preflight.Success, preflight.ErrorCode);
        Assert.Equal(sourceHash, DatabaseHash(fixture.DatabasePath));
        Assert.Equal(before, afterPreflight);
        var created = service.CreateAndPublish(fixture.DatabasePath, fixture.BundlePath);
        var afterBackup = ReadQueueStoredValues(fixture.DatabasePath);
        Assert.True(created.Success, created.ErrorCode);
        Assert.Equal(before, afterBackup);
        var restored = service.Restore(fixture.BundlePath, fixture.TargetPath, new RuntimeRestoreOptions());
        Assert.True(restored.Success, restored.ErrorCode);
        var afterRestore = ReadQueueStoredValues(fixture.TargetPath);

        Assert.Equal(before.Count, afterRestore.Count);
        for (var row = 0; row < 2; row++)
        {
            foreach (var column in Enumerable.Range(0, 13).Except([6, 8, 9]))
                Assert.Equal(before[row][column], afterRestore[row][column]);
            Assert.Equal(StoredText("pending"), afterRestore[row][6]);
            Assert.Equal("null:", afterRestore[row][8]);
            Assert.Equal("null:", afterRestore[row][9]);
        }
        for (var row = 2; row < before.Count; row++)
            Assert.Equal(before[row], afterRestore[row]);
    }

    [Fact]
    public void MalformedCapturedLeaseFailsBeforeAnyLiveOrStagedNormalization()
    {
        using var fixture = new LeaseCatalogFixture();
        Execute(fixture.DatabasePath, "PRAGMA ignore_check_constraints=ON; UPDATE local_repository_reconciliation_queue SET lease_token=NULL WHERE raw_record_id=901;");
        var before = ReadQueueStoredValues(fixture.DatabasePath);
        var hash = DatabaseHash(fixture.DatabasePath);
        var service = new SqliteRuntimeBackupService(fixture.Clock);

        var preflight = service.PreflightForMigration(fixture.DatabasePath);
        var created = service.CreateAndPublish(fixture.DatabasePath, fixture.BundlePath);

        Assert.False(preflight.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, preflight.ErrorCode);
        Assert.False(created.Success);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, created.ErrorCode);
        Assert.Equal(before, ReadQueueStoredValues(fixture.DatabasePath));
        Assert.Equal(hash, DatabaseHash(fixture.DatabasePath));
        Assert.False(File.Exists(fixture.BundlePath));
    }

    [Fact]
    public async Task SanitizedExportAndImportContainNoCatalogNamespaceOrCarrier()
    {
        using var fixture = await CreatePopulatedCatalogAsync();
        const string safeProjection = "sanitized-safe-projection";
        const string safeWorkspace = "sanitized-safe-workspace";
        var traceId = LocalRepositoryAdmissionFixture.Trace(1);
        var sessionId = LocalRepositoryAdmissionFixture.Session(1);
        fixture.Execute($"""
            INSERT INTO monitor_traces(trace_id,client_kind,span_count,repository_name,workspace_label,projected_at)
            VALUES('{traceId}','github-copilot-cli',1,'{safeProjection}','{safeWorkspace}','2026-08-01T01:02:03.1234567Z');
            INSERT INTO session_runs(run_id,session_id,source_surface,trace_id,status)
            VALUES('sanitized-runtime-backup-run','{sessionId}','copilot-cli','{traceId}','completed');
            """);
        var snapshot = new SqliteSanitizedExportSnapshotProvider(fixture.DatabasePath)
            .Capture(new(TraceIds: [traceId]));
        Assert.True(snapshot.Success, snapshot.ErrorCode);
        var exported = new SanitizedExportService().Create(new(
            fixture.Clock.GetUtcNow(),
            snapshot.Snapshot!,
            new(TraceIds: [traceId])));
        Assert.True(exported.Success, exported.ErrorCode);

        var catalogMarkers = ReadCatalogMarkers(fixture.DatabasePath);
        using (var manifest = JsonDocument.Parse(exported.ManifestBytes!))
        {
            var root = manifest.RootElement;
            Assert.False(root.TryGetProperty("component_versions", out _));
            Assert.All(root.GetProperty("record_counts").EnumerateObject(), property =>
            {
                AssertCatalogAbsent(property.Name);
                Assert.True(property.Value.GetInt32() > 0);
            });
            Assert.All(root.GetProperty("processing_versions").EnumerateObject(), property =>
            {
                AssertCatalogAbsent(property.Name);
                AssertCatalogAbsent(property.Value.GetString()!);
            });
            AssertCatalogAbsent(root.GetProperty("capabilities").GetRawText());
        }
        using (var archive = new ZipArchive(new MemoryStream(exported.ArchiveBytes!), ZipArchiveMode.Read))
        {
            Assert.DoesNotContain(archive.Entries, entry =>
                entry.FullName.Contains("local_repository", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.Contains("session_repository", StringComparison.OrdinalIgnoreCase));
            foreach (var entry in archive.Entries)
            {
                var text = Encoding.UTF8.GetString(Read(entry));
                AssertCatalogAbsent(text);
                Assert.DoesNotContain("raw_record_id", text, StringComparison.Ordinal);
                foreach (var marker in catalogMarkers)
                    Assert.DoesNotContain(marker, text, StringComparison.Ordinal);
            }
        }

        using var destination = new MonitorTempDirectory();
        var import = new SqliteSanitizedImportStore(destination.DatabasePath, destination.TimeProvider);
        var preview = import.Preview(exported.ArchiveBytes!);
        var result = import.Commit(exported.ArchiveBytes!, preview.PreviewDigest!);

        Assert.True(preview.Success, preview.ErrorCode);
        Assert.True(result.Success, result.ErrorCode);
        AssertCatalogAbsent(JsonSerializer.Serialize(preview));
        AssertCatalogAbsent(JsonSerializer.Serialize(result));
        Assert.Equal(0, ScalarLong(destination.DatabasePath,
            "SELECT COUNT(*) FROM schema_version WHERE component='local_repository_catalog';"));
        Assert.Equal(0, ScalarLong(destination.DatabasePath, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE name='local_repositories' COLLATE NOCASE
               OR name LIKE 'local\_repository\_%' ESCAPE '\' COLLATE NOCASE
               OR name LIKE 'session\_repository\_%' ESCAPE '\' COLLATE NOCASE
               OR name LIKE 'IX\_local\_repository\_%' ESCAPE '\' COLLATE NOCASE
               OR name LIKE 'IX\_session\_repository\_%' ESCAPE '\' COLLATE NOCASE;
            """));
        var importedText = StringJoin(destination.DatabasePath,
            "SELECT canonical_json FROM sanitized_import_records ORDER BY local_record_id;");
        Assert.Contains(safeProjection, importedText, StringComparison.Ordinal);
        foreach (var marker in catalogMarkers)
            Assert.DoesNotContain(marker, importedText, StringComparison.Ordinal);
    }

    private static void AssertCatalogAbsent(string text)
    {
        Assert.DoesNotContain("local_repository", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session_repository", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("local-repository-catalog", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("repository_catalog", text, StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> CatalogStorageGuardCases()
    {
        var cases = new List<StorageGuardCase>();

        AddTextCases(cases, "local_repositories", "repository_id", 36);
        AddTextCases(cases, "local_repositories", "display_name", 800);
        AddIntegerCases(cases, "local_repositories", "revision", 0);
        AddTextCases(cases, "local_repositories", "created_at", 33);
        AddTextCases(cases, "local_repositories", "updated_at", 33);

        AddTextCases(cases, "local_repository_locators", "locator_id", 36);
        AddTextCases(cases, "local_repository_locators", "repository_id", 36);
        AddLiteralCases(cases, "local_repository_locators", "kind");
        AddAsciiCases(cases, "local_repository_locators", "canonical_locator", 151);
        AddTextCases(cases, "local_repository_locators", "locator_sha256", 64);
        AddLiteralCases(cases, "local_repository_locators", "source");
        AddAsciiCases(cases, "local_repository_locators", "display_owner", 39);
        AddAsciiCases(cases, "local_repository_locators", "display_repository", 100);
        AddTextCases(cases, "local_repository_locators", "created_at", 33);

        AddTextCases(cases, "local_repository_locator_heads", "repository_id", 36);
        AddLiteralCases(cases, "local_repository_locator_heads", "kind");
        AddTextCases(cases, "local_repository_locator_heads", "locator_id", 36);
        AddTextCases(cases, "local_repository_locator_heads", "updated_at", 33);

        AddTextCases(cases, "session_repository_observations", "observation_id", 36);
        AddTextCases(cases, "session_repository_observations", "source_identity_sha256", 64);
        AddIntegerCases(cases, "session_repository_observations", "raw_record_id", 0);
        AddTextCases(cases, "session_repository_observations", "raw_payload_sha256", 64);
        AddIntegerCases(cases, "session_repository_observations", "resource_span_ordinal", -1, 2_147_483_648L);
        AddNullableIntegerCases(cases, "session_repository_observations", "scope_span_ordinal", -1, 2_147_483_648L);
        AddNullableIntegerCases(cases, "session_repository_observations", "span_ordinal", -1, 2_147_483_648L);
        AddIntegerCases(cases, "session_repository_observations", "attribute_ordinal", -1, 2_147_483_648L);
        AddLiteralCases(cases, "session_repository_observations", "scope_kind");
        AddLiteralCases(cases, "session_repository_observations", "attribute_key");
        AddLiteralCases(cases, "session_repository_observations", "value_classification");
        AddNullableLiteralCases(cases, "session_repository_observations", "locator_kind");
        AddNullableAsciiCases(cases, "session_repository_observations", "canonical_locator", 151);
        AddNullableTextCases(cases, "session_repository_observations", "locator_sha256", 64);
        AddNullableAsciiCases(cases, "session_repository_observations", "display_owner", 39);
        AddNullableAsciiCases(cases, "session_repository_observations", "display_repository", 100);
        AddLiteralCases(cases, "session_repository_observations", "source_surface");
        AddNullableTextCases(cases, "session_repository_observations", "source_application_version", 64);
        AddTextCases(cases, "session_repository_observations", "observed_at", 33);

        AddTextCases(cases, "session_repository_observation_contexts", "context_id", 36);
        AddTextCases(cases, "session_repository_observation_contexts", "observation_id", 36);
        AddTextCases(cases, "session_repository_observation_contexts", "context_identity_sha256", 64);
        AddTextCases(cases, "session_repository_observation_contexts", "session_event_id", 36);
        AddTextCases(cases, "session_repository_observation_contexts", "session_id", 36);
        AddTextCases(cases, "session_repository_observation_contexts", "trace_id", 32);
        AddTextCases(cases, "session_repository_observation_contexts", "span_id", 16);
        AddLiteralCases(cases, "session_repository_observation_contexts", "admission_state");
        AddNullableTextCases(cases, "session_repository_observation_contexts", "repository_id", 36);
        AddNullableTextCases(cases, "session_repository_observation_contexts", "locator_id", 36);
        AddTextCases(cases, "session_repository_observation_contexts", "observed_at", 33);

        AddTextCases(cases, "session_repository_manual_overrides", "session_id", 36);
        AddLiteralCases(cases, "session_repository_manual_overrides", "state");
        AddNullableTextCases(cases, "session_repository_manual_overrides", "repository_id", 36);
        AddIntegerCases(cases, "session_repository_manual_overrides", "revision", 0);
        AddTextCases(cases, "session_repository_manual_overrides", "updated_at", 33);

        AddTextCases(cases, "session_repository_assignment_revisions", "session_id", 36);
        AddIntegerCases(cases, "session_repository_assignment_revisions", "revision", -1);
        AddTextCases(cases, "session_repository_assignment_revisions", "updated_at", 33);

        AddTextCases(cases, "session_repository_assignment_history", "history_id", 36);
        AddTextCases(cases, "session_repository_assignment_history", "session_id", 36);
        AddLiteralCases(cases, "session_repository_assignment_history", "action");
        AddIntegerCases(cases, "session_repository_assignment_history", "previous_revision", -1);
        AddIntegerCases(cases, "session_repository_assignment_history", "new_revision", 0);
        cases.Add(new("session_repository_assignment_history.new_revision.adjacency", "session_repository_assignment_history",
            GuardUpdate("session_repository_assignment_history", "new_revision", "previous_revision+100")));
        AddTextCases(cases, "session_repository_assignment_history", "previous_assignment_state_sha256", 64);
        AddTextCases(cases, "session_repository_assignment_history", "new_assignment_state_sha256", 64);
        AddLiteralCases(cases, "session_repository_assignment_history", "previous_state");
        AddLiteralCases(cases, "session_repository_assignment_history", "new_state");
        AddLiteralCases(cases, "session_repository_assignment_history", "previous_authority");
        AddLiteralCases(cases, "session_repository_assignment_history", "new_authority");
        AddNullableTextCases(cases, "session_repository_assignment_history", "previous_repository_id", 36);
        AddNullableTextCases(cases, "session_repository_assignment_history", "new_repository_id", 36);
        AddLiteralCases(cases, "session_repository_assignment_history", "cause_kind");
        AddNullableTextCases(cases, "session_repository_assignment_history", "operation_key", 48);
        AddNullableTextCases(cases, "session_repository_assignment_history", "reconciliation_fingerprint", 64);
        AddTextCases(cases, "session_repository_assignment_history", "occurred_at", 33);

        AddTextCases(cases, "local_repository_history", "history_id", 36);
        AddTextCases(cases, "local_repository_history", "repository_id", 36);
        AddLiteralCases(cases, "local_repository_history", "action");
        AddIntegerCases(cases, "local_repository_history", "previous_revision", -1);
        AddIntegerCases(cases, "local_repository_history", "new_revision", 0);
        AddNullableTextCases(cases, "local_repository_history", "locator_id", 36);
        AddLiteralCases(cases, "local_repository_history", "cause_kind");
        AddNullableTextCases(cases, "local_repository_history", "operation_key", 48);
        AddNullableTextCases(cases, "local_repository_history", "context_identity_sha256", 64);
        AddTextCases(cases, "local_repository_history", "occurred_at", 33);

        AddTextCases(cases, "local_repository_operation_receipts", "operation_key", 48);
        AddTextCases(cases, "local_repository_operation_receipts", "request_fingerprint", 64);
        AddIntegerCases(cases, "local_repository_operation_receipts", "status_code", 202);
        AddLiteralCases(cases, "local_repository_operation_receipts", "content_type");
        AddLiteralCases(cases, "local_repository_operation_receipts", "cache_control");
        cases.Add(new("local_repository_operation_receipts.response_entity.text", "local_repository_operation_receipts",
            GuardUpdate("local_repository_operation_receipts", "response_entity", "$value"), "not-a-blob"));
        cases.Add(new("local_repository_operation_receipts.response_entity.empty", "local_repository_operation_receipts",
            GuardUpdate("local_repository_operation_receipts", "response_entity", "zeroblob(0)")));
        cases.Add(new("local_repository_operation_receipts.response_entity.oversize", "local_repository_operation_receipts",
            GuardUpdate("local_repository_operation_receipts", "response_entity", $"zeroblob({LocalRepositoryExactResponse.MaximumEntityBytes + 1})")));
        AddTextCases(cases, "local_repository_operation_receipts", "created_at", 33);

        AddLiteralCases(cases, "local_repository_reconciliation_state", "projector_key");
        AddNullableIntegerCases(cases, "local_repository_reconciliation_state", "last_discovered_span_id", 0);
        AddTextCases(cases, "local_repository_reconciliation_state", "updated_at", 33);

        AddTextCases(cases, "local_repository_reconciliation_queue", "queue_id", 36);
        AddIntegerCases(cases, "local_repository_reconciliation_queue", "raw_record_id", 0);
        AddLiteralCases(cases, "local_repository_reconciliation_queue", "input_evidence_kind");
        AddNullableTextCases(cases, "local_repository_reconciliation_queue", "raw_payload_sha256", 64);
        AddLiteralCases(cases, "local_repository_reconciliation_queue", "projector_version");
        AddTextCases(cases, "local_repository_reconciliation_queue", "reconciliation_fingerprint", 64);
        AddLiteralCases(cases, "local_repository_reconciliation_queue", "state");
        AddIntegerCases(cases, "local_repository_reconciliation_queue", "attempt_count", -1);
        AddNullableTextCases(cases, "local_repository_reconciliation_queue", "lease_token", 64);
        AddNullableTextCases(cases, "local_repository_reconciliation_queue", "lease_expires_at", 33);
        AddNullableLiteralCases(cases, "local_repository_reconciliation_queue", "terminal_reason");
        AddTextCases(cases, "local_repository_reconciliation_queue", "created_at", 33);
        AddTextCases(cases, "local_repository_reconciliation_queue", "updated_at", 33);

        return cases.Select(static guard => new object[] { guard });
    }

    public static IEnumerable<object[]> OwnerSemanticArchiveCases()
    {
        return OwnerSemanticArchiveScenarios()
            .Where(static scenario => !scenario.Succeeds)
            .Select(static scenario => new object[] { scenario });
    }

    public static IEnumerable<object[]> OwnerSemanticArchiveSuccessCases() =>
        OwnerSemanticArchiveScenarios()
            .Where(static scenario => scenario.Succeeds)
            .Select(static scenario => new object[] { scenario });

    private static IEnumerable<OwnerSemanticArchiveCase> OwnerSemanticArchiveScenarios()
    {
        string[] ids =
        [
            "L1-canonical-locator", "L2-locator-digest", "L4-head-owner", "L6-binary-key",
            "O1-source-identity", "O2-context-identity", "O3-session-event", "O4-event-trace",
            "O4-event-source-event", "O5-source-surface", "O6-admitted-owner", "O8-observed-create",
            "O9-automatic-publication",
            "Q1-unavailable-pending", "Q1-waiting-zero", "Q1-completed-zero",
            "Q1-input-unavailable-zero", "Q1-failed-zero", "Q2-nonleased-token",
            "Q2-leased-without-token", "Q2-leased-without-expiry", "Q3-nonterminal-reason",
            "Q3-terminal-without-reason", "Q4-fingerprint", "Q5-no-cursor", "Q5-null-frontier",
            "Q5-missing-frontier-span", "Q5-raw-beyond-frontier", "Q6-noncompleted-publication",
            "Q7-digest-disagreement",
            "H1-missing-first", "H1-missing-latest", "H2-head-mismatch", "H3-missing-receipt",
            "H4-revision-zero", "H5-head-revision", "H5-assignment-fingerprint",
            "H5-invalid-endpoint", "H6-override-revision", "H6-missing-override",
            "H7-transition-authority", "H8-duplicate-operation",
            "R1-short-key", "R1-long-key", "R1-padding", "R1-invalid-alphabet",
            "R1-noncanonical-final", "R2-uppercase-fingerprint", "R3-status-envelope",
            "R3-malformed-entity", "R4-invalid-timestamp", "R4-unlinked-receipt",
            "R5-linked-action", "R6-assign-fingerprint", "R9-binary-key",
        ];
        foreach (var id in ids)
            yield return CurrentOwnerSemanticScenario(id);

        foreach (var scenario in new[]
        {
            SuccessScenario("L3-owner-reuse", "owner-reuse"),
            SuccessScenario("L5-locator-128", "locator-128"),
            CorruptScenario("L5-locator-129", "locator-128"),
            CorruptScenario("O4-event-source-adapter"),
            CorruptScenario("O4-event-source-surface"),
            CorruptScenario("O7-resource-span-precedence", "resource-span"),
            CorruptScenario("Q5-frontier-skips-raw"),
            CorruptScenario("Q6-pending-publication"),
            CorruptScenario("Q6-input-unavailable-publication"),
            CorruptScenario("Q6-failed-terminal-publication"),
            CorruptScenario("Q6-missing-queue-publication"),
            CorruptScenario("Q7-unavailable-evidence", "observation-only"),
            CorruptScenario("Q7-ambiguous-source-history"),
            SuccessScenario("Q8-candidate-128", "candidate-128"),
            CorruptScenario("Q8-candidate-129", "candidate-128"),
            CorruptScenario("Q9-binary-key", "queue-130"),
            CorruptScenario("Q9-late-page", "queue-130"),
            CorruptScenario("H1-missing-middle", "mutation-chain"),
            CorruptScenario("H1-noncontiguous", "mutation-chain"),
            CorruptScenario("H2-wrong-kind", "mutation-chain"),
            CorruptScenario("H2-orphan-locator", "mutation-chain"),
            CorruptScenario("H2-orphan-history", "mutation-chain"),
            CorruptScenario("H2-orphan-head", "mutation-chain"),
            CorruptScenario("H3-invalid-add", "mutation-chain"),
            CorruptScenario("H3-invalid-replace", "mutation-chain"),
            CorruptScenario("H3-wrong-locator-source", "mutation-chain"),
            CorruptScenario("H3-missing-cause", "mutation-chain"),
            CorruptScenario("H3-dual-cause", "mutation-chain"),
            CorruptScenario("H3-wrong-cause-kind", "mutation-chain"),
            CorruptScenario("H3-unrelated-cause", "mutation-chain"),
            SuccessScenario("H4-empty-session", "empty-session"),
            CorruptScenario("H5-missing-head", "assignment-chain"),
            CorruptScenario("H5-nonadjacent", "assignment-chain"),
            CorruptScenario("H5-equal-fingerprints", "assignment-chain"),
            CorruptScenario("H6-override-head", "assignment-chain"),
            CorruptScenario("H6-orphan-history", "assignment-chain"),
            CorruptScenario("H6-orphan-override", "assignment-chain"),
            CorruptScenario("H7-automatic-to-manual", "assignment-transition"),
            CorruptScenario("H7-automatic-from-manual", "assignment-transition"),
            CorruptScenario("H7-unassign-retains-repository", "assignment-transition"),
            CorruptScenario("H7-resume-from-nonmanual", "assignment-transition-automatic"),
            CorruptScenario("H7-automatic-equal", "assignment-transition-automatic"),
            CorruptScenario("H8-nonexact-reconciliation"),
            SuccessScenario("R1-canonical-key", "receipt-basic"),
            CorruptScenario("R2-fingerprint-storage-class", "receipt-basic"),
            CorruptScenario("R2-fingerprint-length", "receipt-basic"),
            CorruptScenario("R2-fingerprint-nonhex", "receipt-basic"),
            CorruptScenario("R3-content-type", "receipt-linked"),
            CorruptScenario("R3-cache-control", "receipt-linked"),
            CorruptScenario("R3-text-entity", "receipt-linked"),
            CorruptScenario("R3-oversized-entity", "receipt-linked"),
            CorruptScenario("R3-noncanonical-entity", "receipt-linked"),
            CorruptScenario("R3-opposite-entity-kind", "receipt-linked"),
            CorruptScenario("R4-duplicate-link", "receipt-duplicate-link"),
            CorruptScenario("R5-wrong-kind", "receipt-linked"),
            CorruptScenario("R5-wrong-target", "receipt-linked"),
            CorruptScenario("R5-wrong-revision", "receipt-linked"),
            CorruptScenario("R5-assignment-endpoint", "receipt-assignment"),
            CorruptScenario("R6-create-fingerprint", "receipt-fingerprint-create"),
            CorruptScenario("R6-rename-fingerprint", "receipt-fingerprint-rename"),
            CorruptScenario("R6-locator-add-fingerprint", "receipt-fingerprint-locator-add"),
            CorruptScenario("R6-locator-replace-fingerprint", "receipt-fingerprint-locator-replace"),
            CorruptScenario("R6-unassign-fingerprint", "receipt-fingerprint-unassign"),
            CorruptScenario("R6-resume-fingerprint", "receipt-fingerprint-resume"),
            SuccessScenario("R7-stale-no-op", "stale-no-op"),
            CorruptScenario("R8-missing-repository", "receipt-bound"),
            CorruptScenario("R8-future-repository-revision", "receipt-bound"),
            CorruptScenario("R8-missing-assignment", "receipt-bound"),
            CorruptScenario("R8-missing-positive-assignment-revision", "receipt-bound"),
            CorruptScenario("R8-wrong-assignment-state", "receipt-bound-assigned"),
            CorruptScenario("R9-late-page", "receipt-129"),
        })
        {
            yield return scenario;
        }
    }

    private static OwnerSemanticArchiveCase CurrentOwnerSemanticScenario(string id) => id switch
    {
        "L4-head-owner" => CorruptScenario(id, "mutation-chain", "L4-head-foreign-owner", "H2-wrong-owner"),
        "H1-missing-first" => CorruptScenario(id, "mutation-chain"),
        "H1-missing-latest" => CorruptScenario(id, "mutation-chain", "H1-missing-latest"),
        "H2-head-mismatch" => CorruptScenario(id, "mutation-chain"),
        "H3-missing-receipt" => CorruptScenario(id, "mutation-chain", "H3-missing-receipt", "R4-missing-linked"),
        "H4-revision-zero" => CorruptScenario(id, "assignment-chain"),
        "H5-head-revision" => CorruptScenario(id, "assignment-chain", "H5-missing-history"),
        "H5-assignment-fingerprint" => CorruptScenario("H5-bad-before-endpoint", "assignment-chain"),
        "H5-invalid-endpoint" => CorruptScenario(id, "assignment-chain"),
        "H6-override-revision" => CorruptScenario(id, "assignment-chain"),
        "H6-missing-override" => CorruptScenario(id, "assignment-chain", "H6-current-head"),
        "H7-transition-authority" => CorruptScenario(id, "assignment-chain", "H7-assign-nonmanual"),
        "Q6-noncompleted-publication" => CorruptScenario(id, "populated", "Q6-waiting-publication"),
        "R4-unlinked-receipt" => CorruptScenario(id, "receipt-basic"),
        "R1-short-key" or "R1-long-key" or "R1-padding" or "R1-invalid-alphabet" or
            "R1-noncanonical-final" or "R2-uppercase-fingerprint" or "R4-invalid-timestamp" or
            "R9-binary-key" => CorruptScenario(id, "receipt-basic"),
        "R3-status-envelope" or "R3-malformed-entity" => CorruptScenario(id, "receipt-linked"),
        "R5-linked-action" => CorruptScenario(id, "receipt-linked"),
        "R6-assign-fingerprint" => CorruptScenario(id, "receipt-assignment"),
        _ => CorruptScenario(id),
    };

    private static OwnerSemanticArchiveCase CorruptScenario(
        string id,
        string seedKind = "populated",
        params string[] coverageTags) =>
        new(id, id[..id.IndexOf('-', StringComparison.Ordinal)],
            coverageTags.Length == 0 ? [id] : coverageTags, seedKind, Succeeds: false,
            ExpectedErrorCode: id == "L4-head-owner"
                ? RuntimeBackupErrorCodes.ArchiveInvalid
                : RuntimeBackupErrorCodes.RestoreIncompatible,
            ComposerReachable: id != "L4-head-owner");

    private static OwnerSemanticArchiveCase SuccessScenario(string id, string seedKind) =>
        new(id, id[..id.IndexOf('-', StringComparison.Ordinal)], [id], seedKind, Succeeds: true,
            ExpectedErrorCode: null, ComposerReachable: true);

    private static string[] AcceptedOwnerSemanticCoverageTags() =>
    [
        "L1-canonical-locator", "L2-locator-digest", "L3-owner-reuse", "L4-head-foreign-owner",
        "L5-locator-128", "L5-locator-129", "L6-binary-key",
        "O1-source-identity", "O2-context-identity", "O3-session-event", "O4-event-trace",
        "O4-event-source-event", "O4-event-source-adapter", "O4-event-source-surface",
        "O5-source-surface", "O6-admitted-owner", "O7-resource-span-precedence", "O8-observed-create",
        "O9-automatic-publication",
        "Q1-unavailable-pending", "Q1-waiting-zero", "Q1-completed-zero",
        "Q1-input-unavailable-zero", "Q1-failed-zero", "Q2-nonleased-token",
        "Q2-leased-without-token", "Q2-leased-without-expiry", "Q3-nonterminal-reason",
        "Q3-terminal-without-reason", "Q4-fingerprint", "Q5-no-cursor", "Q5-null-frontier",
        "Q5-missing-frontier-span", "Q5-raw-beyond-frontier", "Q5-frontier-skips-raw",
        "Q6-pending-publication", "Q6-waiting-publication", "Q6-input-unavailable-publication",
        "Q6-failed-terminal-publication", "Q6-missing-queue-publication", "Q7-digest-disagreement",
        "Q7-unavailable-evidence", "Q7-ambiguous-source-history", "Q8-candidate-128",
        "Q8-candidate-129", "Q9-binary-key", "Q9-late-page",
        "H1-missing-first", "H1-missing-middle", "H1-noncontiguous", "H1-missing-latest",
        "H2-head-mismatch", "H2-wrong-kind", "H2-wrong-owner", "H2-orphan-locator",
        "H2-orphan-history", "H2-orphan-head", "H3-invalid-add", "H3-invalid-replace",
        "H3-wrong-locator-source", "H3-missing-cause", "H3-dual-cause", "H3-wrong-cause-kind",
        "H3-unrelated-cause", "H3-missing-receipt", "H4-empty-session", "H4-revision-zero",
        "H5-missing-head", "H5-missing-history", "H5-nonadjacent", "H5-bad-before-endpoint",
        "H5-equal-fingerprints", "H5-invalid-endpoint", "H6-override-revision", "H6-override-head",
        "H6-orphan-history", "H6-orphan-override", "H6-current-head", "H7-automatic-to-manual",
        "H7-automatic-from-manual", "H7-assign-nonmanual", "H7-unassign-retains-repository",
        "H7-resume-from-nonmanual", "H7-automatic-equal", "H8-duplicate-operation",
        "H8-nonexact-reconciliation",
        "R1-short-key", "R1-long-key", "R1-padding", "R1-invalid-alphabet", "R1-noncanonical-final",
        "R1-canonical-key", "R2-fingerprint-storage-class", "R2-fingerprint-length",
        "R2-uppercase-fingerprint", "R2-fingerprint-nonhex", "R3-status-envelope", "R3-content-type",
        "R3-cache-control", "R3-text-entity", "R3-oversized-entity", "R3-malformed-entity",
        "R3-noncanonical-entity", "R3-opposite-entity-kind", "R4-invalid-timestamp",
        "R4-unlinked-receipt", "R4-missing-linked", "R4-duplicate-link", "R5-wrong-kind",
        "R5-wrong-target", "R5-wrong-revision", "R5-linked-action", "R5-assignment-endpoint",
        "R6-create-fingerprint", "R6-rename-fingerprint", "R6-locator-add-fingerprint",
        "R6-locator-replace-fingerprint", "R6-assign-fingerprint", "R6-unassign-fingerprint",
        "R6-resume-fingerprint", "R7-stale-no-op", "R8-missing-repository",
        "R8-future-repository-revision", "R8-missing-assignment",
        "R8-missing-positive-assignment-revision", "R8-wrong-assignment-state", "R9-binary-key",
        "R9-late-page",
    ];

    public static IEnumerable<object[]> ReachableOwnerStorageGuardCases()
    {
        const string sourceReachable =
            "EXISTS(SELECT 1 FROM session_repository_observations o WHERE o.raw_record_id=source_schema_observations.raw_record_id)";
        const string sessionReachable =
            "EXISTS(SELECT 1 FROM session_repository_observation_contexts c WHERE (c.session_event_id=session_events.event_id AND c.session_id=session_events.session_id) OR (session_events.source_adapter='otel-exact' COLLATE BINARY AND session_events.source_event_id=c.trace_id || '/' || c.span_id))";
        const string rawReachable =
            "EXISTS(SELECT 1 FROM local_repository_reconciliation_queue q WHERE q.raw_record_id=raw_records.id) OR EXISTS(SELECT 1 FROM session_repository_observations o WHERE o.raw_record_id=raw_records.id)";
        var cases = new List<StorageGuardCase>();

        AddIntegerCases(cases, "source_schema_observations", "raw_record_id", sourceReachable, 0);
        AddLiteralCases(cases, "source_schema_observations", "input_evidence_kind", sourceReachable);
        AddTextCases(cases, "source_schema_observations", "raw_payload_sha256", 64, sourceReachable);
        AddLiteralCases(cases, "source_schema_observations", "source_surface", sourceReachable);
        AddNullableTextCases(cases, "source_schema_observations", "source_application_version", 64, sourceReachable);
        AddTextCases(cases, "source_schema_observations", "observed_at", 33, sourceReachable);

        AddTextCases(cases, "session_events", "event_id", 36, sessionReachable);
        AddTextCases(cases, "session_events", "session_id", 36, sessionReachable);
        AddLiteralCases(cases, "session_events", "type", sessionReachable);
        AddTextCases(cases, "session_events", "trace_id", 32, sessionReachable);
        AddLiteralCases(cases, "session_events", "source_surface", sessionReachable);
        AddLiteralCases(cases, "session_events", "source_adapter", sessionReachable);
        AddTextCases(cases, "session_events", "source_event_id", 49, sessionReachable);

        cases.Add(new("raw_records.payload_json.blob", "raw_records",
            GuardUpdate("raw_records", "payload_json", "zeroblob(1)", rawReachable)));
        cases.Add(new("raw_records.payload_json.oversize", "raw_records",
            GuardUpdate("raw_records", "payload_json", "$value", rawReachable),
            new string('x', RawReplayLimits.MaximumRawRecordBytes + 1)));

        return cases.Select(static guard => new object[] { guard });
    }

    public static IEnumerable<object[]> NullableStorageControls()
    {
        var controls = new[]
        {
            NullControl("session_repository_observations", "scope_span_ordinal"),
            NullControl("session_repository_observations", "span_ordinal"),
            NullControl("session_repository_observations", "locator_kind"),
            NullControl("session_repository_observations", "canonical_locator"),
            NullControl("session_repository_observations", "locator_sha256"),
            NullControl("session_repository_observations", "display_owner"),
            NullControl("session_repository_observations", "display_repository"),
            NullControl("session_repository_observations", "source_application_version"),
            NullControl("session_repository_observation_contexts", "repository_id"),
            NullControl("session_repository_observation_contexts", "locator_id"),
            NullControl("session_repository_manual_overrides", "repository_id"),
            NullControl("session_repository_assignment_history", "previous_repository_id"),
            NullControl("session_repository_assignment_history", "new_repository_id"),
            NullControl("session_repository_assignment_history", "operation_key"),
            NullControl("session_repository_assignment_history", "reconciliation_fingerprint"),
            NullControl("local_repository_history", "locator_id"),
            NullControl("local_repository_history", "operation_key"),
            NullControl("local_repository_history", "context_identity_sha256"),
            NullControl("local_repository_reconciliation_state", "last_discovered_span_id"),
            NullControl("local_repository_reconciliation_queue", "raw_payload_sha256"),
            NullControl("local_repository_reconciliation_queue", "lease_token"),
            NullControl("local_repository_reconciliation_queue", "lease_expires_at"),
            NullControl("local_repository_reconciliation_queue", "terminal_reason"),
            new StorageGuardCase(
                "local_repository_reconciliation_queue.attempt_count.int64_max",
                "local_repository_reconciliation_queue",
                GuardUpdate("local_repository_reconciliation_queue", "attempt_count", long.MaxValue.ToString(CultureInfo.InvariantCulture))),
        };
        return controls.Select(static control => new object[] { control });
    }

    private static StorageGuardCase NullControl(string table, string column) =>
        new($"{table}.{column}.null", table, GuardUpdate(table, column, "NULL"));

    private static void AddTextCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        int maximumBytes,
        string? where = null)
    {
        cases.Add(new($"{table}.{column}.blob", table, GuardUpdate(table, column, $"zeroblob({maximumBytes})", where)));
        cases.Add(new($"{table}.{column}.oversize", table, GuardUpdate(table, column, "$value", where), new string('x', maximumBytes + 1)));
    }

    private static void AddNullableTextCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        int maximumBytes,
        string? where = null) =>
        AddTextCases(cases, table, column, maximumBytes, where);

    private static void AddAsciiCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        int maximumBytes,
        string? where = null)
    {
        AddTextCases(cases, table, column, maximumBytes, where);
        cases.Add(new($"{table}.{column}.non_ascii", table, GuardUpdate(table, column, "$value", where), "é"));
    }

    private static void AddNullableAsciiCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        int maximumBytes,
        string? where = null) =>
        AddAsciiCases(cases, table, column, maximumBytes, where);

    private static void AddLiteralCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        string? where = null)
    {
        cases.Add(new($"{table}.{column}.blob", table, GuardUpdate(table, column, "zeroblob(1)", where)));
        cases.Add(new($"{table}.{column}.literal", table, GuardUpdate(table, column, "$value", where), "task10-invalid"));
    }

    private static void AddNullableLiteralCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        string? where = null) =>
        AddLiteralCases(cases, table, column, where);

    private static void AddIntegerCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        params long[] invalidValues) =>
        AddIntegerCases(cases, table, column, where: null, invalidValues);

    private static void AddIntegerCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        string? where,
        params long[] invalidValues)
    {
        cases.Add(new($"{table}.{column}.text", table, GuardUpdate(table, column, "$value", where), "one"));
        foreach (var value in invalidValues)
        {
            cases.Add(new(
                $"{table}.{column}.range.{value.ToString(CultureInfo.InvariantCulture)}",
                table,
                GuardUpdate(table, column, value.ToString(CultureInfo.InvariantCulture), where)));
        }
    }

    private static void AddNullableIntegerCases(
        ICollection<StorageGuardCase> cases,
        string table,
        string column,
        params long[] invalidValues)
    {
        cases.Add(new($"{table}.{column}.blob", table, GuardUpdate(table, column, "zeroblob(8)")));
        foreach (var value in invalidValues)
        {
            cases.Add(new(
                $"{table}.{column}.range.{value.ToString(CultureInfo.InvariantCulture)}",
                table,
                GuardUpdate(table, column, value.ToString(CultureInfo.InvariantCulture))));
        }
    }

    private static string GuardUpdate(string table, string column, string value, string? where = null)
    {
        var target = where is null
            ? $"rowid=(SELECT MIN(rowid) FROM \"{table}\")"
            : $"rowid=(SELECT MIN(rowid) FROM \"{table}\" WHERE {where})";
        return $"UPDATE \"{table}\" SET \"{column}\"={value} WHERE {target};";
    }

    private static void ApplySessionContradiction(string path, string contradiction) => Execute(path, contradiction switch
    {
        "missing" => "DELETE FROM schema_version WHERE component='session';",
        "older" => "UPDATE schema_version SET version=12 WHERE component='session';",
        "newer" => "UPDATE schema_version SET version=15 WHERE component='session';",
        "shape" => "ALTER TABLE sessions ADD COLUMN task10_unexpected TEXT NULL;",
        _ => throw new ArgumentOutOfRangeException(nameof(contradiction)),
    });

    private static void ApplyNamespaceContradiction(string path, string contradiction) => Execute(path, contradiction switch
    {
        "stamp-missing" => "DELETE FROM schema_version WHERE component='local_repository_catalog';",
        "stamp-future" => "UPDATE schema_version SET version=2 WHERE component='local_repository_catalog';",
        "table-missing" => "DROP TABLE session_repository_manual_overrides;",
        "table-extra-column" => "ALTER TABLE local_repositories ADD COLUMN task10_unexpected TEXT NULL;",
        "index-missing" => "DROP INDEX IX_local_repository_locators_repository_created;",
        "index-definition" => "DROP INDEX IX_local_repository_locators_repository_created; CREATE INDEX IX_local_repository_locators_repository_created ON local_repository_locators(repository_id,locator_id);",
        "trigger-missing" => "DROP TRIGGER local_repository_history_update_rejected;",
        "trigger-definition" => "DROP TRIGGER local_repository_history_update_rejected; CREATE TRIGGER local_repository_history_update_rejected BEFORE UPDATE ON local_repository_history BEGIN SELECT RAISE(ABORT,'task10_counterfeit'); END;",
        "reserved-extra" => "CREATE TABLE local_repository_task10_extra(id INTEGER PRIMARY KEY);",
        "reserved-index-extra" => "CREATE INDEX IX_local_repository_task10_extra ON raw_records(id);",
        "reserved-uppercase" => "CREATE TABLE SESSION_REPOSITORY_TASK10_EXTRA(id INTEGER PRIMARY KEY);",
        _ => throw new ArgumentOutOfRangeException(nameof(contradiction)),
    });

    private static void ApplyStorageContradiction(string path, string contradiction)
    {
        switch (contradiction)
        {
            case "catalog-text":
                CorruptUpdate(path, "local_repositories", "UPDATE local_repositories SET display_name=$value;", new string('x', 801));
                break;
            case "catalog-integer":
                CorruptUpdate(path, "local_repositories", "UPDATE local_repositories SET revision=$value;", "one");
                break;
            case "catalog-blob":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET response_entity=$value;", "not-a-blob");
                break;
            case "catalog-nullable":
                CorruptUpdate(path, "session_repository_observations", "UPDATE session_repository_observations SET source_application_version=zeroblob(1);");
                break;
            case "source-provenance":
                CorruptUpdate(path, "source_schema_observations", "UPDATE source_schema_observations SET source_application_version=$value;", new string('v', 65));
                break;
            case "session-event":
                CorruptUpdate(path, "session_events", "UPDATE session_events SET trace_id=zeroblob(32);");
                break;
            case "raw-payload":
                CorruptUpdate(path, "raw_records", "UPDATE raw_records SET payload_json=zeroblob(1);");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contradiction));
        }
    }

    private static void ApplySemanticContradiction(string path, string contradiction)
    {
        switch (contradiction)
        {
            case "reconciliation":
                CorruptUpdate(path, "local_repository_reconciliation_queue",
                    "UPDATE local_repository_reconciliation_queue SET attempt_count=0 WHERE state='completed';");
                break;
            case "automatic-admission":
                CorruptUpdate(path, "session_repository_observations",
                    "UPDATE session_repository_observations SET source_identity_sha256=$value;",
                    new string('0', 64));
                break;
            case "mutation":
                CorruptUpdate(path, "session_repository_assignment_history",
                    "UPDATE session_repository_assignment_history SET new_assignment_state_sha256=$value WHERE cause_kind='user_operation';",
                    new string('0', 64));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(contradiction));
        }
    }

    private static (string ProducerEventId, string DirectEventId) ArrangeSplitSessionEventCandidates(string path)
    {
        const string directEventId = "01900000-0000-7005-8000-000000000001";
        const string directTraceId = "33333333333333333333333333333333";
        const string directSpanId = "4444444444444444";
        using var connection = OpenWritable(path);
        string producerEventId;
        using (var current = connection.CreateCommand())
        {
            current.CommandText = "SELECT session_event_id FROM session_repository_observation_contexts LIMIT 1;";
            producerEventId = Assert.IsType<string>(current.ExecuteScalar());
        }
        var contextTriggers = ReadTableTriggers(connection, "session_repository_observation_contexts");
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var trigger in contextTriggers)
            Execute(connection, transaction, $"DROP TRIGGER \"{trigger.Name}\";");
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO session_events(
                    event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,
                    source_adapter,source_event_id,type,occurred_at,content_state,
                    source_application_version,adapter_version,schema_fingerprint,
                    normalization_version,match_kind)
                SELECT
                    $event,session_id,run_id,source_surface,parent_event_id,$trace,status,
                    source_adapter,$source_event,type,occurred_at,content_state,
                    source_application_version,adapter_version,schema_fingerprint,
                    normalization_version,match_kind
                FROM session_events
                WHERE event_id=$producer;
                UPDATE session_repository_observation_contexts
                SET session_event_id=$event;
                """;
            insert.Parameters.AddWithValue("$event", directEventId);
            insert.Parameters.AddWithValue("$trace", directTraceId);
            insert.Parameters.AddWithValue("$source_event", directTraceId + "/" + directSpanId);
            insert.Parameters.AddWithValue("$producer", producerEventId);
            insert.ExecuteNonQuery();
        }
        foreach (var trigger in contextTriggers)
            Execute(connection, transaction, trigger.Sql);
        transaction.Commit();
        return (producerEventId, directEventId);
    }

    private static void CorruptUpdate(string path, string table, string sql, object? value = null)
    {
        using var connection = OpenWritable(path);
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys=OFF;";
            foreignKeys.ExecuteNonQuery();
        }
        var triggers = ReadTableTriggers(connection, table);
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction, "PRAGMA ignore_check_constraints=ON;");
        foreach (var trigger in triggers)
            Execute(connection, transaction, $"DROP TRIGGER \"{trigger.Name}\";");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = sql;
            if (value is not null) command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
        foreach (var trigger in triggers)
            Execute(connection, transaction, trigger.Sql);
        transaction.Commit();
    }

    private static IReadOnlyList<(string Name, string Sql)> ReadTableTriggers(SqliteConnection connection, string table)
    {
        var triggers = new List<(string Name, string Sql)>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,sql FROM sqlite_schema WHERE type='trigger' AND tbl_name=$table COLLATE BINARY ORDER BY name;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1)));
        return triggers;
    }

    private static void ValidateObserved(string path, ValidationObserver observer)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            LocalRepositoryCatalogBackupValidation.Validate(connection, transaction, observer);
        }
        finally
        {
            transaction.Rollback();
        }
    }

    private static byte[] DatabaseHash(string path)
    {
        using (var connection = OpenWritable(path))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE); PRAGMA journal_mode=DELETE;";
            command.ExecuteNonQuery();
        }
        return SHA256.HashData(File.ReadAllBytes(path));
    }

    private static void RewriteArchiveDatabase(
        string source,
        string output,
        Action<string> mutateDatabase,
        Func<RuntimeBackupManifestData, RuntimeBackupManifestData>? mutateManifest = null)
    {
        byte[] manifest;
        byte[] database;
        using (var archive = ZipFile.OpenRead(source))
        {
            manifest = Read(archive.GetEntry("manifest.json")!);
            database = Read(archive.GetEntry("database.sqlite")!);
        }
        var mutated = Path.Combine(
            Path.GetDirectoryName(output)!,
            $".task10-rewrite-{Guid.NewGuid():N}.sqlite");
        File.WriteAllBytes(mutated, database);
        mutateDatabase(mutated);
        _ = DatabaseHash(mutated);
        database = File.ReadAllBytes(mutated);
        var parsed = RuntimeBackupJson.ParseManifest(manifest);
        parsed = mutateManifest?.Invoke(parsed) ?? parsed;
        var rowCounts = ReadManifestRowCounts(mutated, parsed.RowCounts.Keys);
        File.Delete(mutated);
        manifest = RuntimeBackupJson.WriteManifest(parsed with
        {
            DatabaseSha256 = Convert.ToHexStringLower(SHA256.HashData(database)),
            DatabaseSize = database.LongLength,
            RowCounts = rowCounts,
        });
        using var targetArchive = ZipFile.Open(output, ZipArchiveMode.Create);
        Write(targetArchive, "manifest.json", manifest);
        Write(targetArchive, "database.sqlite", database);
    }

    private static void ExtractArchiveDatabase(string source, string output)
    {
        using var archive = ZipFile.OpenRead(source);
        File.WriteAllBytes(output, Read(archive.GetEntry("database.sqlite")!));
    }

    private static IReadOnlyDictionary<string, long> ReadManifestRowCounts(
        string databasePath,
        IEnumerable<string> tables)
    {
        using var connection = Open(databasePath);
        var result = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
            result.Add(table, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static void ApplyOwnerSemanticArchiveMutation(string path, string caseId)
    {
        var f = new string('f', 64);
        var e32 = new string('e', 32);
        var e16 = new string('e', 16);
        switch (caseId)
        {
            case "L1-canonical-locator":
                CorruptUpdate(path, "local_repository_locators", "UPDATE local_repository_locators SET canonical_locator=canonical_locator || '/';");
                break;
            case "L2-locator-digest":
                CorruptUpdate(path, "local_repository_locators", "UPDATE local_repository_locators SET locator_sha256=$value;", f);
                break;
            case "L4-head-owner":
                PointHeadAtForeignOwner(path);
                break;
            case "L5-locator-129":
                InsertLocator129(path);
                break;
            case "L6-binary-key":
                CorruptUpdate(path, "local_repositories", "INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) SELECT zeroblob(36),'Binary',1,created_at,updated_at FROM local_repositories LIMIT 1;");
                break;
            case "O1-source-identity":
                CorruptUpdate(path, "session_repository_observations", "UPDATE session_repository_observations SET source_identity_sha256=$value;", f);
                break;
            case "O2-context-identity":
                CorruptUpdate(path, "session_repository_observation_contexts", "UPDATE session_repository_observation_contexts SET context_identity_sha256=$value;", f);
                break;
            case "O3-session-event":
                _ = ArrangeSplitSessionEventCandidates(path);
                break;
            case "O4-event-trace":
                CorruptUpdate(path, "session_events", "UPDATE session_events SET trace_id=$value WHERE event_id=(SELECT session_event_id FROM session_repository_observation_contexts LIMIT 1);", e32);
                break;
            case "O4-event-source-event":
                CorruptUpdate(path, "session_events", "UPDATE session_events SET source_event_id=$value WHERE event_id=(SELECT session_event_id FROM session_repository_observation_contexts LIMIT 1);", e32 + "/" + e16);
                break;
            case "O4-event-source-adapter":
                CorruptUpdate(path, "session_events", "UPDATE session_events SET source_adapter='raw-otlp' WHERE event_id=(SELECT session_event_id FROM session_repository_observation_contexts LIMIT 1);");
                break;
            case "O4-event-source-surface":
                CorruptUpdate(path, "session_events", "UPDATE session_events SET source_surface='vscode' WHERE event_id=(SELECT session_event_id FROM session_repository_observation_contexts LIMIT 1);");
                break;
            case "O5-source-surface":
                CorruptUpdate(path, "session_repository_observations", "UPDATE session_repository_observations SET source_surface='github-copilot-vscode';");
                CorruptUpdate(path, "source_schema_observations", "UPDATE source_schema_observations SET source_surface='github-copilot-vscode' WHERE raw_record_id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);");
                break;
            case "O6-admitted-owner":
                CorruptUpdate(path, "session_repository_observation_contexts", "UPDATE session_repository_observation_contexts SET repository_id=NULL,locator_id=NULL WHERE admission_state='admitted';");
                break;
            case "O7-resource-span-precedence":
                CorruptUpdate(path, "session_repository_observation_contexts", """
                    UPDATE session_repository_observation_contexts
                    SET admission_state='admitted',
                        repository_id=(SELECT c.repository_id FROM session_repository_observation_contexts c JOIN session_repository_observations o ON o.observation_id=c.observation_id WHERE o.scope_kind='span' LIMIT 1),
                        locator_id=(SELECT c.locator_id FROM session_repository_observation_contexts c JOIN session_repository_observations o ON o.observation_id=c.observation_id WHERE o.scope_kind='span' LIMIT 1)
                    WHERE observation_id=(SELECT observation_id FROM session_repository_observations WHERE scope_kind='resource' LIMIT 1);
                    """);
                break;
            case "O8-observed-create":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET context_identity_sha256=$value WHERE cause_kind='source_context';", f);
                break;
            case "O9-automatic-publication":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET new_repository_id=NULL WHERE cause_kind='source_reconciliation';");
                break;
            case "Q1-unavailable-pending":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET input_evidence_kind='input_unavailable',raw_payload_sha256=NULL,state='pending',attempt_count=0;");
                break;
            case "Q1-waiting-zero":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='waiting_session',attempt_count=0;");
                break;
            case "Q1-completed-zero":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='completed',attempt_count=0;");
                break;
            case "Q1-input-unavailable-zero":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='input_unavailable',attempt_count=0;");
                break;
            case "Q1-failed-zero":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='failed_terminal',attempt_count=0,terminal_reason='catalog_parse_failure';");
                break;
            case "Q2-nonleased-token":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET lease_token=$value,lease_expires_at='2026-08-01T00:00:30.0000000+00:00';", f);
                break;
            case "Q2-leased-without-token":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='leased',attempt_count=1,lease_token=NULL,lease_expires_at='2026-08-01T00:00:30.0000000+00:00';");
                break;
            case "Q2-leased-without-expiry":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='leased',attempt_count=1,lease_token=$value,lease_expires_at=NULL;", f);
                break;
            case "Q3-nonterminal-reason":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='pending',attempt_count=0,terminal_reason='catalog_parse_failure';");
                break;
            case "Q3-terminal-without-reason":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='failed_terminal',attempt_count=1,terminal_reason=NULL;");
                break;
            case "Q4-fingerprint":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET reconciliation_fingerprint=$value;", f);
                break;
            case "Q5-no-cursor":
                CorruptUpdate(path, "local_repository_reconciliation_state", "DELETE FROM local_repository_reconciliation_state;");
                break;
            case "Q5-null-frontier":
                CorruptUpdate(path, "local_repository_reconciliation_state", "UPDATE local_repository_reconciliation_state SET last_discovered_span_id=NULL;");
                break;
            case "Q5-missing-frontier-span":
                CorruptUpdate(path, "local_repository_reconciliation_state", "UPDATE local_repository_reconciliation_state SET last_discovered_span_id=(SELECT MAX(id)+1000 FROM monitor_spans);");
                break;
            case "Q5-raw-beyond-frontier":
                InsertQueueBeyondFrontier(path);
                break;
            case "Q5-frontier-skips-raw":
                InsertFrontierSkippedRaw(path);
                break;
            case "Q6-noncompleted-publication":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='waiting_session',attempt_count=1;");
                break;
            case "Q6-pending-publication":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='pending',attempt_count=1,lease_token=NULL,lease_expires_at=NULL,terminal_reason=NULL WHERE raw_record_id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);");
                break;
            case "Q6-input-unavailable-publication":
                SetObservationQueueUnavailable(path);
                break;
            case "Q6-failed-terminal-publication":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='failed_terminal',attempt_count=1,lease_token=NULL,lease_expires_at=NULL,terminal_reason='catalog_parse_failure' WHERE raw_record_id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);");
                break;
            case "Q6-missing-queue-publication":
                RemoveObservationQueueAndFrontierSpan(path);
                break;
            case "Q7-digest-disagreement":
            case "RAW5-digest-disagreement":
                CorruptUpdate(path, "raw_records", "DELETE FROM raw_records WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);");
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET raw_payload_sha256=$value WHERE raw_record_id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);", f);
                break;
            case "Q7-unavailable-evidence":
                SetObservationQueueUnavailable(path);
                break;
            case "Q7-ambiguous-source-history":
                InsertAmbiguousSourceHistory(path);
                break;
            case "Q8-candidate-129":
                InsertCandidate129(path);
                break;
            case "Q9-binary-key":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET queue_id=zeroblob(36) WHERE rowid=(SELECT rowid FROM local_repository_reconciliation_queue ORDER BY queue_id COLLATE BINARY LIMIT 1);");
                break;
            case "Q9-late-page":
                CorruptUpdate(path, "local_repository_reconciliation_queue", "UPDATE local_repository_reconciliation_queue SET state='completed',attempt_count=0 WHERE rowid=(SELECT rowid FROM local_repository_reconciliation_queue ORDER BY queue_id COLLATE BINARY DESC LIMIT 1);");
                break;
            case "RAW4-payload-mismatch":
                CorruptUpdate(path, "raw_records", "UPDATE raw_records SET payload_json='{}' WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);");
                break;
            case "H1-missing-first":
                CorruptUpdate(path, "local_repository_history", "DELETE FROM local_repository_history WHERE new_revision=(SELECT MIN(new_revision) FROM local_repository_history);");
                break;
            case "H1-missing-latest":
                CorruptUpdate(path, "local_repository_history", "DELETE FROM local_repository_history WHERE new_revision=(SELECT MAX(new_revision) FROM local_repository_history);");
                break;
            case "H1-missing-middle":
                CorruptUpdate(path, "local_repository_history", "DELETE FROM local_repository_history WHERE new_revision=2;");
                break;
            case "H1-noncontiguous":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET previous_revision=1 WHERE new_revision=3;");
                break;
            case "H2-head-mismatch":
                InsertHeadMismatchLocator(path, "01900000-0000-7006-8000-000000000002");
                break;
            case "H2-wrong-kind":
                SetLocatorAndHeadWrongKind(path);
                break;
            case "H2-orphan-locator":
                InsertOrphanLocator(path);
                break;
            case "H2-orphan-history":
                InsertOrphanRepositoryHistory(path);
                break;
            case "H2-orphan-head":
                InsertUnanchoredRepositoryHead(path);
                break;
            case "H3-missing-receipt":
                CorruptUpdate(path, "local_repository_operation_receipts", "DELETE FROM local_repository_operation_receipts WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE new_revision=4 LIMIT 1);");
                break;
            case "H3-invalid-add":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET action='add_locator' WHERE new_revision=3;");
                break;
            case "H3-invalid-replace":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET action='replace_locator' WHERE new_revision=2;");
                break;
            case "H3-wrong-locator-source":
                CorruptUpdate(path, "local_repository_locators", "UPDATE local_repository_locators SET source='observed' WHERE locator_id=(SELECT locator_id FROM local_repository_history WHERE new_revision=2 LIMIT 1);");
                break;
            case "H3-missing-cause":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET operation_key=NULL WHERE new_revision=4;");
                break;
            case "H3-dual-cause":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET context_identity_sha256=$value WHERE new_revision=4;", f);
                break;
            case "H3-wrong-cause-kind":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET cause_kind='source_context' WHERE new_revision=4;");
                break;
            case "H3-unrelated-cause":
                CorruptUpdate(path, "local_repository_history", $"UPDATE local_repository_history SET operation_key='{ArchiveOperationKey(15)}' WHERE new_revision=4;");
                break;
            case "H4-revision-zero":
                CorruptUpdate(path, "session_repository_assignment_revisions", "UPDATE session_repository_assignment_revisions SET revision=0;");
                break;
            case "H5-head-revision":
                CorruptUpdate(path, "session_repository_assignment_revisions", "UPDATE session_repository_assignment_revisions SET revision=revision+1;");
                break;
            case "H5-assignment-fingerprint":
            case "H5-bad-before-endpoint":
                SetBadAssignmentBeforeEndpoint(path);
                break;
            case "H5-missing-head":
                CorruptUpdate(path, "session_repository_assignment_revisions", "DELETE FROM session_repository_assignment_revisions;");
                break;
            case "H5-nonadjacent":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET previous_revision=1 WHERE new_revision=1;");
                break;
            case "H5-equal-fingerprints":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET new_assignment_state_sha256=previous_assignment_state_sha256;");
                break;
            case "H5-invalid-endpoint":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET new_state='unassigned' WHERE new_repository_id IS NOT NULL;");
                break;
            case "H6-override-revision":
                CorruptUpdate(path, "session_repository_manual_overrides", "UPDATE session_repository_manual_overrides SET revision=revision+1;");
                break;
            case "H6-missing-override":
                CorruptUpdate(path, "session_repository_manual_overrides", "DELETE FROM session_repository_manual_overrides;");
                break;
            case "H6-override-head":
                CorruptUpdate(path, "session_repository_manual_overrides", "UPDATE session_repository_manual_overrides SET repository_id=(SELECT MAX(repository_id COLLATE BINARY) FROM local_repositories);");
                break;
            case "H6-orphan-history":
                InsertOrphanAssignmentHistory(path);
                break;
            case "H6-orphan-override":
                InsertOrphanAssignmentOverride(path);
                break;
            case "H7-transition-authority":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET new_authority='automatic' WHERE action='assign';");
                break;
            case "H7-automatic-to-manual":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET new_authority='manual' WHERE action='automatic_reconcile';");
                break;
            case "H7-automatic-from-manual":
                ConvertLatestManualHistoryToAutomatic(path);
                break;
            case "H7-unassign-retains-repository":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET action='explicitly_unassign' WHERE action='assign';");
                break;
            case "H7-resume-from-nonmanual":
                CorruptUpdate(path, "session_repository_assignment_history", $"UPDATE session_repository_assignment_history SET action='resume_automatic',cause_kind='user_operation',operation_key='{ArchiveOperationKey(0x76)}',reconciliation_fingerprint=NULL WHERE action='automatic_reconcile';");
                break;
            case "H7-automatic-equal":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET new_assignment_state_sha256=previous_assignment_state_sha256 WHERE action='automatic_reconcile';");
                break;
            case "H8-duplicate-operation":
                CorruptUpdate(path, "local_repository_history", "UPDATE local_repository_history SET action='create',cause_kind='user_operation',operation_key=(SELECT operation_key FROM session_repository_assignment_history WHERE cause_kind='user_operation' LIMIT 1),context_identity_sha256=NULL WHERE new_revision=(SELECT MIN(new_revision) FROM local_repository_history);");
                break;
            case "H8-nonexact-reconciliation":
                CorruptUpdate(path, "session_repository_assignment_history", "UPDATE session_repository_assignment_history SET reconciliation_fingerprint=$value WHERE action='automatic_reconcile';", f);
                break;
            case "R1-short-key":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET operation_key=$value;", "lrc1_" + new string('a', 42));
                break;
            case "R1-long-key":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET operation_key=$value;", "lrc1_" + new string('a', 44));
                break;
            case "R1-padding":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET operation_key=substr(operation_key,1,47) || '=';");
                break;
            case "R1-invalid-alphabet":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET operation_key=substr(operation_key,1,47) || '!';");
                break;
            case "R1-noncanonical-final":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET operation_key=substr(operation_key,1,47) || 'B';");
                break;
            case "R2-uppercase-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=upper(request_fingerprint);");
                break;
            case "R2-fingerprint-storage-class":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=zeroblob(64);");
                break;
            case "R2-fingerprint-length":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value;", new string('a', 63));
                break;
            case "R2-fingerprint-nonhex":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value;", new string('g', 64));
                break;
            case "R3-status-envelope":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET status_code=201 WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='rename' LIMIT 1);");
                break;
            case "R3-malformed-entity":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET response_entity=X'7B7D' WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='rename' LIMIT 1);");
                break;
            case "R3-content-type":
                UpdateRenameReceipt(path, "content_type='application/json'");
                break;
            case "R3-cache-control":
                UpdateRenameReceipt(path, "cache_control='public'");
                break;
            case "R3-text-entity":
                UpdateRenameReceipt(path, "response_entity=CAST(response_entity AS TEXT)");
                break;
            case "R3-oversized-entity":
                UpdateRenameReceipt(path, "response_entity=zeroblob(16385)");
                break;
            case "R3-noncanonical-entity":
                PrefixRenameReceiptEntity(path);
                break;
            case "R3-opposite-entity-kind":
                SetRenameReceiptToAssignmentEntity(path);
                break;
            case "R4-invalid-timestamp":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET created_at='2026-02-30T00:00:00.0000000+00:00';");
                break;
            case "R4-unlinked-receipt":
                CorruptUpdate(path, "local_repository_operation_receipts", $"INSERT INTO local_repository_operation_receipts SELECT '{ArchiveOperationKey(0x7e)}',request_fingerprint,status_code,content_type,cache_control,response_entity,created_at FROM local_repository_operation_receipts LIMIT 1;");
                break;
            case "R4-duplicate-link":
                DuplicateRepositoryHistoryReceiptLink(path);
                break;
            case "R5-linked-action":
                UpdateRenameReceipt(path, "status_code=201");
                break;
            case "R5-wrong-kind":
                SetRenameReceiptToAssignmentEntity(path);
                break;
            case "R5-wrong-target":
                SetRenameReceiptRepositoryEntity(path, wrongTarget: true, wrongRevision: false);
                break;
            case "R5-wrong-revision":
                SetRenameReceiptRepositoryEntity(path, wrongTarget: false, wrongRevision: true);
                break;
            case "R5-assignment-endpoint":
                SetAssignmentReceiptWrongEndpoint(path);
                break;
            case "R6-assign-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value WHERE operation_key=(SELECT operation_key FROM session_repository_assignment_history WHERE action='assign' LIMIT 1);", f);
                break;
            case "R6-create-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='create' LIMIT 1);", f);
                break;
            case "R6-rename-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='rename' LIMIT 1);", f);
                break;
            case "R6-locator-add-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='add_locator' LIMIT 1);", f);
                break;
            case "R6-locator-replace-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='replace_locator' LIMIT 1);", f);
                break;
            case "R6-unassign-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value WHERE operation_key=(SELECT operation_key FROM session_repository_assignment_history WHERE action='explicitly_unassign' LIMIT 1);", f);
                break;
            case "R6-resume-fingerprint":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET request_fingerprint=$value WHERE operation_key=(SELECT operation_key FROM session_repository_assignment_history WHERE action='resume_automatic' LIMIT 1);", f);
                break;
            case "R8-missing-repository":
            case "R8-future-repository-revision":
            case "R8-missing-assignment":
            case "R8-missing-positive-assignment-revision":
            case "R8-wrong-assignment-state":
                InsertReceiptBoundContradiction(path, caseId);
                break;
            case "R9-binary-key":
                CorruptUpdate(path, "local_repository_operation_receipts", "UPDATE local_repository_operation_receipts SET operation_key=zeroblob(48);");
                break;
            case "R9-late-page":
                SetLatePageReceiptWrongRepositoryTarget(path);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseId), caseId, null);
        }
    }

    private static string ArchiveOperationKey(byte value) =>
        "lrc1_" + Convert.ToBase64String(Enumerable.Repeat(value, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static void RepublishArchiveDatabase(
        string source,
        string output,
        TimeProvider clock,
        Action<string> mutateDatabase)
    {
        var isolated = Path.Combine(
            Path.GetDirectoryName(output)!,
            $"task10-republish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(isolated);
        var transformed = Path.Combine(isolated, "source.sqlite");
        var isolatedOutput = Path.Combine(isolated, "transformed.zip");
        ExtractArchiveDatabase(source, transformed);
        mutateDatabase(transformed);
        _ = DatabaseHash(transformed);
        var result = new SqliteRuntimeBackupService(clock).CreateAndPublish(transformed, isolatedOutput);
        Assert.True(result.Success, result.ErrorCode);
        File.Move(isolatedOutput, output);
    }

    private static void ApplyRestorableRawMutation(string path, string rawCase, TimeProvider clock)
    {
        switch (rawCase)
        {
            case "RAW2":
                Execute(path, """
                    DELETE FROM retention_capture_journal
                    WHERE item_id=(SELECT item_id FROM retention_items
                      WHERE store_kind='raw_record'
                        AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1));
                    DELETE FROM retention_items
                    WHERE store_kind='raw_record'
                      AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1);
                    DELETE FROM raw_records
                    WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);
                    """);
                break;
            case "RAW3":
                Execute(path, """
                    INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
                    SELECT item_id,'2026-08-01T00:00:00.0000000+00:00','2026-08-01T00:00:00.0000000+00:00'
                    FROM retention_items
                    WHERE store_kind='raw_record'
                      AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1);
                    UPDATE retention_items
                    SET state='deleted',revision=revision+1,
                        read_denied_at='2026-08-01T00:00:00.0000000+00:00',
                        queued_at='2026-08-01T00:00:00.0000000+00:00',
                        deleted_at='2026-08-01T00:00:00.0000000+00:00'
                    WHERE store_kind='raw_record'
                      AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1);
                    DELETE FROM local_workspace_span_facts
                    WHERE raw_record_id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);
                    DELETE FROM raw_records
                    WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);
                    """);
                break;
            case "RAW6":
                InsertAlternateMatchingRaw(path, clock);
                Execute(path, """
                    DELETE FROM retention_capture_journal
                    WHERE item_id=(SELECT item_id FROM retention_items
                      WHERE store_kind='raw_record'
                        AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1));
                    DELETE FROM retention_items
                    WHERE store_kind='raw_record'
                      AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1);
                    DELETE FROM raw_records
                    WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);
                    """);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(rawCase), rawCase, null);
        }
    }

    private static void InsertAlternateMatchingRaw(string path, TimeProvider clock)
    {
        RawTelemetryRecord record;
        using (var connection = Open(path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT source,trace_id,received_at,resource_attributes_json,payload_json
                FROM raw_records
                WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            record = new(
                null,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                DateTimeOffset.ParseExact(reader.GetString(2), "O", CultureInfo.InvariantCulture),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4));
            Assert.False(reader.Read());
        }
        var alternateId = new RawTelemetryStore(
            path,
            RetentionCatalogContext.AdoptExistingCatalogV1(path),
            clock).Insert(record);
        Assert.NotEqual(
            ScalarLong(path, "SELECT raw_record_id FROM session_repository_observations LIMIT 1;"),
            alternateId);
        Assert.Equal(
            ScalarLong(path, "SELECT length(payload_json) FROM raw_records WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);"),
            ScalarLong(path, $"SELECT length(payload_json) FROM raw_records WHERE id={alternateId};"));
    }

    private static LocalRepositoryCatalogApplication CreateReadApplication(string databasePath)
    {
        var queue = new SqliteLocalRepositoryReconciliationStore(
            databasePath,
            TimeProvider.System,
            static () => new string('c', 64));
        return new(new SqliteLocalRepositoryCatalogStore(
            databasePath,
            queue,
            new LocalRepositoryAssignmentResolver(),
            TimeProvider.System));
    }

    private static void InsertHeadMismatchLocator(string path, string locatorId)
    {
        Assert.True(GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/Synthetic/HeadMismatch.git",
            out var locator));
        using (var connection = OpenWritable(path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO local_repository_locators(
                    locator_id,repository_id,kind,canonical_locator,locator_sha256,
                    source,display_owner,display_repository,created_at)
                SELECT $locator,repository_id,'github_repository',$canonical,$digest,
                       'manual',$owner,$repository,created_at
                FROM local_repositories
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$locator", locatorId);
            command.Parameters.AddWithValue("$canonical", locator!.CanonicalLocator);
            command.Parameters.AddWithValue("$digest", locator.LocatorSha256);
            command.Parameters.AddWithValue("$owner", locator.DisplayOwner);
            command.Parameters.AddWithValue("$repository", locator.DisplayRepository);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        CorruptUpdate(
            path,
            "local_repository_locator_heads",
            """
            UPDATE local_repository_locator_heads
            SET locator_id=$value
            WHERE repository_id=(
                SELECT repository_id
                FROM local_repository_locators
                WHERE locator_id=$value);
            """,
            locatorId);
    }

    private static void PointHeadAtForeignOwner(string path) => CorruptUpdate(
        path,
        "local_repository_locator_heads",
        """
        UPDATE local_repository_locator_heads
        SET locator_id=(
            SELECT l.locator_id
            FROM local_repository_locators l
            WHERE l.repository_id<>local_repository_locator_heads.repository_id
            ORDER BY l.repository_id COLLATE BINARY
            LIMIT 1)
        WHERE repository_id=(SELECT MIN(repository_id COLLATE BINARY) FROM local_repositories);
        """);

    private static void InsertLocator129(string path)
    {
        Assert.True(GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/example/overflow",
            out var locator));
        var repository = ReadRepositoryMutation(path);
        Assert.Equal(128, repository.Revision);
        var locatorId = LocalRepositoryCatalogFixture.RepositoryId(999_991);
        var historyId = LocalRepositoryCatalogFixture.RepositoryId(999_992);
        var operationKey = ArchiveOperationKey(0x7d);
        var at = repository.UpdatedAt.ToString("O", CultureInfo.InvariantCulture);
        var fingerprint = LocalRepositoryOperationFingerprint.SetGitHubLocator(
            repository.RepositoryId,
            repository.Revision,
            locator!.CanonicalLocator);
        var responseEntity = LocalRepositoryCatalogFixture.RepositoryEntity(
            repository with { Revision = 129 }).Span;
        CorruptUpdate(
            path,
            "local_repository_locators",
            $"""
            INSERT INTO local_repository_locators(
                locator_id,repository_id,kind,canonical_locator,locator_sha256,source,
                display_owner,display_repository,created_at)
            SELECT
                '{locatorId}',repository_id,'github_repository',
                '{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual',
                '{locator.DisplayOwner}','{locator.DisplayRepository}','{at}'
            FROM local_repositories
            LIMIT 1;
            UPDATE local_repository_locator_heads
            SET locator_id='{locatorId}',updated_at='{at}'
            WHERE repository_id='{repository.RepositoryId}' AND kind='github_repository';
            UPDATE local_repositories
            SET revision=129,updated_at='{at}'
            WHERE repository_id='{repository.RepositoryId}' AND revision=128;
            INSERT INTO local_repository_history(
                history_id,repository_id,action,previous_revision,new_revision,locator_id,
                cause_kind,operation_key,context_identity_sha256,occurred_at)
            VALUES(
                '{historyId}','{repository.RepositoryId}','replace_locator',128,129,'{locatorId}',
                'user_operation','{operationKey}',NULL,'{at}');
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,
                response_entity,created_at)
            VALUES(
                '{operationKey}','{fingerprint}',200,'application/json; charset=utf-8','no-store',
                X'{Convert.ToHexString(responseEntity)}','{at}');
            """);
    }

    private static void SetLatePageReceiptWrongRepositoryTarget(string path)
    {
        string operationKey;
        LocalRepositoryMutationRepository repository;
        using (var connection = Open(path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT history.operation_key,repository.repository_id,repository.display_name,
                       repository.revision,repository.created_at,repository.updated_at
                FROM local_repository_history history
                JOIN local_repositories repository ON repository.repository_id=history.repository_id
                WHERE history.operation_key=(
                    SELECT MAX(operation_key COLLATE BINARY)
                    FROM local_repository_operation_receipts);
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            operationKey = reader.GetString(0);
            repository = new(
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture),
                DateTimeOffset.ParseExact(reader.GetString(5), "O", CultureInfo.InvariantCulture));
            Assert.False(reader.Read());
        }

        var wrongTarget = repository with
        {
            RepositoryId = LocalRepositoryCatalogFixture.RepositoryId(930_000),
        };
        CorruptUpdate(
            path,
            "local_repository_operation_receipts",
            $"""
            UPDATE local_repository_operation_receipts
            SET response_entity=$value
            WHERE operation_key='{operationKey}';
            """,
            LocalRepositoryCatalogFixture.RepositoryEntity(wrongTarget).ToArray());
    }

    private static void InsertFrontierSkippedRaw(string path) => CorruptUpdate(
        path,
        "local_repository_reconciliation_state",
        $"""
        INSERT INTO monitor_spans(
            raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,
            tool_name,tool_type,mcp_tool_name,mcp_server_hash,agent_name,request_model,
            response_model,input_tokens,output_tokens,total_tokens,reasoning_tokens,
            cache_read_tokens,cache_creation_tokens,status,error_type,finish_reasons,
            conversation_id,duration_ms,start_time,end_time,projected_at)
        VALUES(
            999998,'{999998:x32}',NULL,NULL,0,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
            NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
            '{LocalRepositoryAdmissionFixture.ObservedAt}');
        UPDATE local_repository_reconciliation_state
        SET last_discovered_span_id=(SELECT MAX(id) FROM monitor_spans);
        """);

    private static void SetObservationQueueUnavailable(string path)
    {
        var rawRecordId = ScalarLong(path, "SELECT raw_record_id FROM session_repository_observations LIMIT 1;");
        var fingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.InputUnavailable(rawRecordId));
        CorruptUpdate(
            path,
            "local_repository_reconciliation_queue",
            $"""
            UPDATE local_repository_reconciliation_queue
            SET input_evidence_kind='input_unavailable',raw_payload_sha256=NULL,
                reconciliation_fingerprint='{fingerprint}',state='input_unavailable',attempt_count=0,
                lease_token=NULL,lease_expires_at=NULL,terminal_reason=NULL
            WHERE raw_record_id={rawRecordId};
            """);
    }

    private static void RemoveObservationQueueAndFrontierSpan(string path)
    {
        var rawRecordId = ScalarLong(path, "SELECT raw_record_id FROM session_repository_observations LIMIT 1;");
        CorruptUpdate(
            path,
            "local_repository_reconciliation_queue",
            $"""
            DELETE FROM local_repository_reconciliation_queue WHERE raw_record_id={rawRecordId};
            DELETE FROM monitor_spans WHERE raw_record_id={rawRecordId};
            UPDATE local_repository_reconciliation_state SET last_discovered_span_id=NULL;
            """);
    }

    private static void InsertAmbiguousSourceHistory(string path)
    {
        var sourceSession = StringJoin(path,
            "SELECT session_id FROM session_repository_assignment_history WHERE cause_kind='source_reconciliation' LIMIT 1;");
        const string otherSession = "01900000-0000-7009-8000-000000000001";
        const string otherHistory = "01900000-0000-7009-8000-000000000002";
        CorruptUpdate(
            path,
            "session_repository_assignment_history",
            $"""
            INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            SELECT '{otherSession}',status,completeness,last_seen_at,raw_retention_state,created_at,updated_at
            FROM sessions WHERE session_id='{sourceSession}';
            INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at)
            SELECT '{otherSession}',revision,updated_at
            FROM session_repository_assignment_revisions WHERE session_id='{sourceSession}';
            INSERT INTO session_repository_assignment_history(
                history_id,session_id,action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at)
            SELECT
                '{otherHistory}','{otherSession}',action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at
            FROM session_repository_assignment_history
            WHERE session_id='{sourceSession}' AND cause_kind='source_reconciliation'
            LIMIT 1;
            """);
    }

    private static void InsertCandidate129(string path)
    {
        const string observationId = "01900000-0000-7010-8000-000000000001";
        const string contextId = "01900000-0000-7010-8000-000000000002";
        const string repositoryId = "01900000-0000-7010-8000-000000000003";
        const string locatorId = "01900000-0000-7010-8000-000000000004";
        const string historyId = "01900000-0000-7010-8000-000000000005";
        const int spanOrdinal = int.MaxValue;
        Assert.True(GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/example/repository-129",
            out var locator));

        long rawRecordId;
        int resourceSpanOrdinal;
        int scopeSpanOrdinal;
        string attributeKey;
        string sessionEventId;
        string sessionId;
        string traceId;
        string spanId;
        using (var connection = Open(path))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT o.raw_record_id,o.resource_span_ordinal,o.scope_span_ordinal,o.attribute_key,
                       c.session_event_id,c.session_id,c.trace_id,c.span_id
                FROM session_repository_observations o
                JOIN session_repository_observation_contexts c
                  ON c.observation_id=o.observation_id
                WHERE o.scope_kind='span' AND c.admission_state='admitted'
                ORDER BY o.observation_id COLLATE BINARY
                LIMIT 1;
                """;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            rawRecordId = reader.GetInt64(0);
            resourceSpanOrdinal = reader.GetInt32(1);
            scopeSpanOrdinal = reader.GetInt32(2);
            attributeKey = reader.GetString(3);
            sessionEventId = reader.GetString(4);
            sessionId = reader.GetString(5);
            traceId = reader.GetString(6);
            spanId = reader.GetString(7);
            Assert.False(reader.Read());
        }

        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Span(
                rawRecordId,
                resourceSpanOrdinal,
                scopeSpanOrdinal,
                spanOrdinal,
                attributeOrdinal: 0,
                attributeKey: attributeKey));
        var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
            sourceIdentity,
            sessionId,
            sessionEventId,
            traceId,
            spanId));

        using var writable = OpenWritable(path);
        using var transaction = writable.BeginTransaction(deferred: false);
        Execute(
            writable,
            transaction,
            $"""
            INSERT INTO local_repositories(
                repository_id,display_name,revision,created_at,updated_at)
            VALUES(
                '{repositoryId}','repository-129',1,
                '{LocalRepositoryAdmissionFixture.ObservedAt}',
                '{LocalRepositoryAdmissionFixture.ObservedAt}');
            INSERT INTO local_repository_locators(
                locator_id,repository_id,kind,canonical_locator,locator_sha256,source,
                display_owner,display_repository,created_at)
            VALUES(
                '{locatorId}','{repositoryId}','github_repository',
                '{locator!.CanonicalLocator}','{locator.LocatorSha256}','observed',
                '{locator.DisplayOwner}','{locator.DisplayRepository}',
                '{LocalRepositoryAdmissionFixture.ObservedAt}');
            INSERT INTO session_repository_observations(
                observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,
                resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,
                scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,
                locator_sha256,display_owner,display_repository,source_surface,
                source_application_version,observed_at)
            SELECT
                '{observationId}','{sourceIdentity}',raw_record_id,raw_payload_sha256,
                {resourceSpanOrdinal},{scopeSpanOrdinal},{spanOrdinal},0,
                'span',attribute_key,'admitted','github_repository',
                '{locator.CanonicalLocator}','{locator.LocatorSha256}',
                '{locator.DisplayOwner}','{locator.DisplayRepository}',source_surface,
                source_application_version,'{LocalRepositoryAdmissionFixture.ObservedAt}'
            FROM session_repository_observations
            WHERE raw_record_id={rawRecordId}
            LIMIT 1;
            INSERT INTO session_repository_observation_contexts(
                context_id,observation_id,context_identity_sha256,session_event_id,session_id,
                trace_id,span_id,admission_state,repository_id,locator_id,observed_at)
            VALUES(
                '{contextId}','{observationId}','{contextIdentity}','{sessionEventId}','{sessionId}',
                '{traceId}','{spanId}','admitted','{repositoryId}','{locatorId}',
                '{LocalRepositoryAdmissionFixture.ObservedAt}');
            INSERT INTO local_repository_locator_heads(
                repository_id,kind,locator_id,updated_at)
            VALUES(
                '{repositoryId}','github_repository','{locatorId}',
                '{LocalRepositoryAdmissionFixture.ObservedAt}');
            INSERT INTO local_repository_history(
                history_id,repository_id,action,previous_revision,new_revision,locator_id,
                cause_kind,operation_key,context_identity_sha256,occurred_at)
            VALUES(
                '{historyId}','{repositoryId}','create_observed',0,1,'{locatorId}',
                'source_context',NULL,'{contextIdentity}',
                '{LocalRepositoryAdmissionFixture.ObservedAt}');
            """);
        transaction.Commit();
    }

    private static void InsertOrphanLocator(string path)
    {
        Assert.True(GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/example/orphan",
            out var locator));
        CorruptUpdate(
            path,
            "local_repository_locators",
            $"""
            INSERT INTO local_repository_locators(
                locator_id,repository_id,kind,canonical_locator,locator_sha256,source,
                display_owner,display_repository,created_at)
            VALUES(
                '01900000-0000-7011-8000-000000000002',
                (SELECT MIN(repository_id COLLATE BINARY) FROM local_repositories),'github_repository',
                '{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual',
                '{locator.DisplayOwner}','{locator.DisplayRepository}','{LocalRepositoryCatalogFixture.At}');
            """);
    }

    private static void InsertOrphanRepositoryHistory(string path)
    {
        const string repositoryId = "01900000-0000-7011-8000-000000000003";
        const string historyId = "01900000-0000-7011-8000-000000000004";
        var operationKey = ArchiveOperationKey(0x91);
        var at = DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", CultureInfo.InvariantCulture);
        var entity = LocalRepositoryCatalogFixture.RepositoryEntity(new(
            repositoryId, "Orphan", 1, at, at));
        var fingerprint = LocalRepositoryOperationFingerprint.Create("Orphan", null);
        CorruptUpdate(
            path,
            "local_repository_history",
            $"""
            INSERT INTO local_repositories(
                repository_id,display_name,revision,created_at,updated_at)
            VALUES(
                '{repositoryId}','Orphan',2,
                '{LocalRepositoryCatalogFixture.At}','{LocalRepositoryCatalogFixture.At}');
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,
                response_entity,created_at)
            VALUES(
                '{operationKey}','{fingerprint}',201,'application/json; charset=utf-8',
                'no-store',X'{Convert.ToHexString(entity.Span)}','{LocalRepositoryCatalogFixture.At}');
            INSERT INTO local_repository_history(
                history_id,repository_id,action,previous_revision,new_revision,locator_id,
                cause_kind,operation_key,context_identity_sha256,occurred_at)
            VALUES(
                '{historyId}','{repositoryId}','create',0,1,NULL,'user_operation',
                '{operationKey}',NULL,'{LocalRepositoryCatalogFixture.At}');
            """);
    }

    private static void SetLocatorAndHeadWrongKind(string path)
    {
        var repositoryId = StringJoin(path,
            "SELECT MIN(repository_id COLLATE BINARY) FROM local_repository_locator_heads;");
        CorruptUpdate(
            path,
            "local_repository_locators",
            $"""
            UPDATE local_repository_locators
            SET kind='other'
            WHERE repository_id='{repositoryId}'
              AND locator_id=(
                  SELECT locator_id
                  FROM local_repository_locator_heads
                  WHERE repository_id='{repositoryId}');
            """);
        CorruptUpdate(
            path,
            "local_repository_locator_heads",
            $"""
            UPDATE local_repository_locator_heads
            SET kind='other'
            WHERE repository_id='{repositoryId}';
            """);
    }

    private static void InsertUnanchoredRepositoryHead(string path)
    {
        const string repositoryId = "01900000-0000-7011-8000-000000000006";
        const string locatorId = "01900000-0000-7011-8000-000000000007";
        Assert.True(GitHubRepositoryLocatorParser.TryParse(
            "https://github.com/example/unanchored",
            out var locator));
        CorruptUpdate(
            path,
            "local_repository_locator_heads",
            $"""
            INSERT INTO local_repositories(
                repository_id,display_name,revision,created_at,updated_at)
            VALUES(
                '{repositoryId}','Unanchored',1,
                '{LocalRepositoryCatalogFixture.At}','{LocalRepositoryCatalogFixture.At}');
            INSERT INTO local_repository_locators(
                locator_id,repository_id,kind,canonical_locator,locator_sha256,source,
                display_owner,display_repository,created_at)
            VALUES(
                '{locatorId}','{repositoryId}','github_repository',
                '{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual',
                '{locator.DisplayOwner}','{locator.DisplayRepository}',
                '{LocalRepositoryCatalogFixture.At}');
            INSERT INTO local_repository_locator_heads(
                repository_id,kind,locator_id,updated_at)
            VALUES(
                '{repositoryId}','github_repository','{locatorId}',
                '{LocalRepositoryCatalogFixture.At}');
            """);
    }

    private static void SetBadAssignmentBeforeEndpoint(string path)
    {
        var repositoryId = StringJoin(path, "SELECT MIN(repository_id COLLATE BINARY) FROM local_repositories;");
        var fingerprint = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState("assigned", "manual", repositoryId, []));
        CorruptUpdate(
            path,
            "session_repository_assignment_history",
            $"""
            UPDATE session_repository_assignment_history
            SET previous_state='assigned',previous_authority='manual',
                previous_repository_id='{repositoryId}',
                previous_assignment_state_sha256='{fingerprint}'
            WHERE new_revision=1;
            """);
    }

    private static void InsertOrphanAssignmentHistory(string path)
    {
        const string sessionId = "01900000-0000-7012-8000-000000000002";
        const string historyId = "01900000-0000-7012-8000-000000000003";
        var repositoryId = StringJoin(path, "SELECT MIN(repository_id COLLATE BINARY) FROM local_repositories;");
        var operationKey = ArchiveOperationKey(0x92);
        var before = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState("unassigned", "none", null, []));
        var after = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState("assigned", "manual", repositoryId, []));
        var at = DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", CultureInfo.InvariantCulture);
        var entity = LocalRepositoryCatalogFixture.AssignmentEntity(new(
            sessionId, 1, "assigned", "manual", repositoryId, [], at));
        var fingerprint = LocalRepositoryOperationFingerprint.SessionAction(
            sessionId, 0, "assign", repositoryId);
        CorruptUpdate(
            path,
            "session_repository_assignment_history",
            $"""
            INSERT INTO sessions(
                session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            SELECT
                '{sessionId}',status,completeness,last_seen_at,raw_retention_state,created_at,updated_at
            FROM sessions
            ORDER BY session_id COLLATE BINARY
            LIMIT 1;
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,
                response_entity,created_at)
            VALUES(
                '{operationKey}','{fingerprint}',200,'application/json; charset=utf-8',
                'no-store',X'{Convert.ToHexString(entity.Span)}','{LocalRepositoryCatalogFixture.At}');
            INSERT INTO session_repository_assignment_history(
                history_id,session_id,action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at)
            VALUES(
                '{historyId}','{sessionId}','assign',0,1,'{before}','{after}',
                'unassigned','assigned','none','manual',NULL,'{repositoryId}',
                'user_operation','{operationKey}',NULL,'{LocalRepositoryCatalogFixture.At}');
            """);
    }

    private static void InsertOrphanAssignmentOverride(string path)
    {
        const string sessionId = "01900000-0000-7012-8000-000000000001";
        CorruptUpdate(
            path,
            "session_repository_manual_overrides",
            $"""
            INSERT INTO sessions(
                session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            SELECT
                '{sessionId}',status,completeness,last_seen_at,raw_retention_state,created_at,updated_at
            FROM sessions
            ORDER BY session_id COLLATE BINARY
            LIMIT 1;
            INSERT INTO session_repository_manual_overrides(
                session_id,state,repository_id,revision,updated_at)
            SELECT
                '{sessionId}','assigned',repository_id,1,
                '{LocalRepositoryCatalogFixture.At}'
            FROM local_repositories
            ORDER BY repository_id COLLATE BINARY
            LIMIT 1;
            """);
    }

    private static void ConvertLatestManualHistoryToAutomatic(string path)
    {
        var fingerprint = StringJoin(path,
            "SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue WHERE state='completed' LIMIT 1;");
        CorruptUpdate(
            path,
            "session_repository_assignment_history",
            $"""
            UPDATE session_repository_assignment_history
            SET action='automatic_reconcile',cause_kind='source_reconciliation',
                operation_key=NULL,reconciliation_fingerprint='{fingerprint}'
            WHERE new_revision=(SELECT MAX(new_revision) FROM session_repository_assignment_history);
            """);
    }

    private static void UpdateRenameReceipt(string path, string assignment) => CorruptUpdate(
        path,
        "local_repository_operation_receipts",
        $"""
        UPDATE local_repository_operation_receipts
        SET {assignment}
        WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='rename' LIMIT 1);
        """);

    private static void PrefixRenameReceiptEntity(string path)
    {
        var entity = ReadBlob(path, """
            SELECT response_entity FROM local_repository_operation_receipts
            WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='rename' LIMIT 1);
            """);
        UpdateRenameReceiptBlob(path, [0x20, .. entity]);
    }

    private static void SetRenameReceiptToAssignmentEntity(string path)
    {
        var entity = LocalRepositoryCatalogFixture.AssignmentEntity(new(
            LocalRepositoryCatalogFixture.SessionId(4_501),
            0,
            "unassigned",
            "none",
            null,
            [],
            null));
        UpdateRenameReceiptBlob(path, entity.ToArray());
    }

    private static void SetRenameReceiptRepositoryEntity(string path, bool wrongTarget, bool wrongRevision)
    {
        var repository = ReadRepositoryMutation(path);
        if (wrongTarget)
            repository = repository with { RepositoryId = LocalRepositoryCatalogFixture.RepositoryId(910_000) };
        if (wrongRevision)
            repository = repository with { Revision = repository.Revision + 1 };
        UpdateRenameReceiptBlob(path, LocalRepositoryCatalogFixture.RepositoryEntity(repository).ToArray());
    }

    private static void SetAssignmentReceiptWrongEndpoint(string path)
    {
        var sessionId = StringJoin(path, "SELECT session_id FROM session_repository_assignment_revisions LIMIT 1;");
        var at = DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", CultureInfo.InvariantCulture);
        var entity = LocalRepositoryCatalogFixture.AssignmentEntity(new(
            sessionId, 1, "explicitly_unassigned", "manual", null, [], at));
        CorruptUpdate(
            path,
            "local_repository_operation_receipts",
            "UPDATE local_repository_operation_receipts SET response_entity=$value WHERE operation_key=(SELECT operation_key FROM session_repository_assignment_history WHERE action='assign' LIMIT 1);",
            entity.ToArray());
    }

    private static void UpdateRenameReceiptBlob(string path, byte[] entity) => CorruptUpdate(
        path,
        "local_repository_operation_receipts",
        "UPDATE local_repository_operation_receipts SET response_entity=$value WHERE operation_key=(SELECT operation_key FROM local_repository_history WHERE action='rename' LIMIT 1);",
        entity);

    private static LocalRepositoryMutationRepository ReadRepositoryMutation(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT repository_id,display_name,revision,created_at,updated_at FROM local_repositories ORDER BY repository_id COLLATE BINARY LIMIT 1;";
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        return new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            DateTimeOffset.ParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture),
            DateTimeOffset.ParseExact(reader.GetString(4), "O", CultureInfo.InvariantCulture));
    }

    private static byte[] ReadBlob(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Assert.IsType<byte[]>(command.ExecuteScalar());
    }

    private static void DuplicateRepositoryHistoryReceiptLink(string path)
    {
        CorruptUpdate(
            path,
            "local_repository_history",
            """
            UPDATE local_repository_history
            SET operation_key=(SELECT operation_key FROM local_repository_history ORDER BY repository_id COLLATE BINARY LIMIT 1)
            WHERE repository_id=(SELECT MAX(repository_id COLLATE BINARY) FROM local_repository_history);
            """);
        CorruptUpdate(
            path,
            "local_repository_operation_receipts",
            """
            DELETE FROM local_repository_operation_receipts
            WHERE operation_key NOT IN (
                SELECT operation_key
                FROM local_repository_history
                WHERE operation_key IS NOT NULL);
            """);
    }

    private static void InsertReceiptBoundContradiction(string path, string caseId)
    {
        var repository = ReadRepositoryMutation(path);
        var existingSession = StringJoin(path, "SELECT session_id FROM sessions ORDER BY session_id COLLATE BINARY LIMIT 1;");
        var at = DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", CultureInfo.InvariantCulture);
        ReadOnlyMemory<byte> entity = caseId switch
        {
            "R8-missing-repository" => LocalRepositoryCatalogFixture.RepositoryEntity(
                repository with { RepositoryId = LocalRepositoryCatalogFixture.RepositoryId(920_000) }),
            "R8-future-repository-revision" => LocalRepositoryCatalogFixture.RepositoryEntity(
                repository with { Revision = repository.Revision + 1 }),
            "R8-missing-assignment" => LocalRepositoryCatalogFixture.AssignmentEntity(new(
                LocalRepositoryCatalogFixture.SessionId(920_001), 0, "unassigned", "none", null, [], null)),
            "R8-missing-positive-assignment-revision" => LocalRepositoryCatalogFixture.AssignmentEntity(new(
                existingSession, 1, "assigned", "manual", repository.RepositoryId, [], at)),
            "R8-wrong-assignment-state" => LocalRepositoryCatalogFixture.AssignmentEntity(new(
                existingSession, 1, "explicitly_unassigned", "manual", null, [], at)),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId), caseId, null),
        };
        CorruptUpdate(
            path,
            "local_repository_operation_receipts",
            $"""
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,
                response_entity,created_at)
            VALUES(
                '{MatrixOperationKey(8_000 + caseId.Length)}','{new string('d', 64)}',200,
                'application/json; charset=utf-8','no-store',X'{Convert.ToHexString(entity.Span)}',
                '{LocalRepositoryCatalogFixture.At}');
            """);
    }

    private static void InsertQueueBeyondFrontier(string path)
    {
        const long rawRecordId = 999_999;
        var digest = new string('d', 64);
        var fingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, digest));
        CorruptUpdate(
            path,
            "local_repository_reconciliation_queue",
            $"""
            INSERT INTO monitor_spans(
                raw_record_id,trace_id,span_id,parent_span_id,span_ordinal,operation,category,
                tool_name,tool_type,mcp_tool_name,mcp_server_hash,agent_name,request_model,
                response_model,input_tokens,output_tokens,total_tokens,reasoning_tokens,
                cache_read_tokens,cache_creation_tokens,status,error_type,finish_reasons,
                conversation_id,duration_ms,start_time,end_time,projected_at)
            VALUES(
                {rawRecordId},'{rawRecordId:x32}',NULL,NULL,0,
                NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,
                NULL,NULL,NULL,NULL,NULL,'{LocalRepositoryAdmissionFixture.ObservedAt}');
            INSERT INTO local_repository_reconciliation_queue(
                queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,
                reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,
                terminal_reason,created_at,updated_at)
            VALUES(
                '01900000-0000-7006-8000-000000000099',{rawRecordId},'payload_sha256','{digest}',
                'local-repository-catalog:1','{fingerprint}','pending',0,NULL,NULL,NULL,
                '{LocalRepositoryAdmissionFixture.ObservedAt}','{LocalRepositoryAdmissionFixture.ObservedAt}');
            """);
    }

    private static void AssertFixedIncompatibility(bool success, string? errorCode, string caseId)
    {
        Assert.False(success, caseId);
        Assert.Equal(RuntimeBackupErrorCodes.RestoreIncompatible, errorCode);
    }

    private static void AssertAcceptedMutationContradictionShape(string caseId, string path)
    {
        var operationKey = StringJoin(path, caseId == "L5-locator-129"
            ? "SELECT operation_key FROM local_repository_history WHERE new_revision=129;"
            : "SELECT MAX(operation_key COLLATE BINARY) FROM local_repository_operation_receipts;");
        var linkedRepositoryId = StringJoin(path, $"""
            SELECT repository_id
            FROM local_repository_history
            WHERE operation_key='{operationKey}';
            """);
        var receipt = LocalRepositoryExactResponse.ValidateMutationEntity(
            caseId == "L5-locator-129" ? 200 : 201,
            ReadBlob(path, $"""
                SELECT response_entity
                FROM local_repository_operation_receipts
                WHERE operation_key='{operationKey}';
                """));

        switch (caseId)
        {
            case "L5-locator-129":
                Assert.Equal(129, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_locators;"));
                Assert.Equal(129, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_history;"));
                Assert.Equal(129, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
                Assert.Equal(129, ScalarLong(path, "SELECT revision FROM local_repositories;"));
                Assert.Equal(
                    "replace_locator|128|129|user_operation",
                    StringJoin(path, "SELECT action||'|'||previous_revision||'|'||new_revision||'|'||cause_kind FROM local_repository_history WHERE new_revision=129;"));
                Assert.Equal(1, ScalarLong(path, """
                    SELECT COUNT(*)
                    FROM local_repository_locator_heads head
                    JOIN local_repository_history history
                      ON history.repository_id=head.repository_id
                     AND history.locator_id=head.locator_id
                    WHERE history.new_revision=129;
                    """));
                var canonicalLocator = StringJoin(path, """
                    SELECT locator.canonical_locator
                    FROM local_repository_history history
                    JOIN local_repository_locators locator ON locator.locator_id=history.locator_id
                    WHERE history.new_revision=129;
                    """);
                Assert.Equal(
                    LocalRepositoryOperationFingerprint.SetGitHubLocator(linkedRepositoryId, 128, canonicalLocator),
                    StringJoin(path, $"SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key='{operationKey}';"));
                Assert.Equal(LocalRepositoryMutationEntityKind.Repository, receipt.Kind);
                Assert.Equal(linkedRepositoryId, receipt.TargetId);
                Assert.Equal(129, receipt.Revision);
                break;
            case "R9-late-page":
                Assert.Equal(129, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
                Assert.Equal(129, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_history;"));
                Assert.Equal(
                    "201|application/json; charset=utf-8|no-store|create",
                    StringJoin(path, $"""
                        SELECT receipt.status_code||'|'||receipt.content_type||'|'||receipt.cache_control||'|'||history.action
                        FROM local_repository_operation_receipts receipt
                        JOIN local_repository_history history ON history.operation_key=receipt.operation_key
                        WHERE receipt.operation_key='{operationKey}';
                        """));
                Assert.Equal(LocalRepositoryMutationEntityKind.Repository, receipt.Kind);
                Assert.NotEqual(linkedRepositoryId, receipt.TargetId);
                Assert.Equal(1, receipt.Revision);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseId), caseId, null);
        }
    }

    private static void AssertFixedOwnerSemanticRejection(
        bool success,
        string? errorCode,
        OwnerSemanticArchiveCase archiveCase)
    {
        Assert.False(success, archiveCase.Id);
        Assert.Equal(archiveCase.ExpectedErrorCode, errorCode);
    }

    private static void AssertNoRuntimeBackupArtifacts(string directory, params string[] targets)
    {
        foreach (var target in targets)
        {
            Assert.False(File.Exists(target + "-journal"));
            Assert.False(File.Exists(target + "-wal"));
            Assert.False(File.Exists(target + "-shm"));
            Assert.False(File.Exists(target + ".runtime-restore-journal.json"));
            Assert.False(File.Exists(target + ".runtime-restore-journal.json.commit"));
            Assert.False(File.Exists(target + ".runtime-restore-rollback"));
            Assert.False(File.Exists(target + ".runtime-restore-stage"));
        }
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory, ".runtime-restore-stage-*", SearchOption.TopDirectoryOnly));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory, ".runtime-backup-inspect-*", SearchOption.TopDirectoryOnly));
        var runtimeBackups = Path.Combine(directory, "runtime-backups");
        if (Directory.Exists(runtimeBackups))
            Assert.Empty(Directory.EnumerateFileSystemEntries(runtimeBackups, ".runtime-backup-preview-*", SearchOption.TopDirectoryOnly));
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

    private static IReadOnlyList<string> ReadCatalogMarkers(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT repository_id FROM local_repositories
            UNION ALL SELECT display_name FROM local_repositories
            UNION ALL SELECT canonical_locator FROM local_repository_locators
            UNION ALL SELECT raw_payload_sha256 FROM local_repository_reconciliation_queue WHERE raw_payload_sha256 IS NOT NULL
            UNION ALL SELECT operation_key FROM local_repository_operation_receipts
            ORDER BY 1 COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var markers = new List<string>();
        while (reader.Read()) markers.Add(reader.GetString(0));
        return markers;
    }

    private static long ScalarLong(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static MonitorTempDirectory CreateCurrentSessionWithoutCatalog()
    {
        var temp = new MonitorTempDirectory();
        temp.CreateRawStore().CreateMonitorSchema();
        var legacy = new SqliteRuntimeBackupService(temp.TimeProvider).Initialize(temp.DatabasePath);
        Assert.True(legacy.Success, legacy.ErrorCode);
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        return temp;
    }

    private static void AssertEmptyCatalogInstalled(string path)
    {
        Assert.Equal(1, ScalarLong(path,
            "SELECT version FROM schema_version WHERE component='local_repository_catalog';"));
        Assert.All(LocalRepositoryCatalogSchemaV1.TableNames, table =>
            Assert.Equal(table == "local_repository_reconciliation_state" ? 1 : 0, ScalarLong(path, $"SELECT COUNT(*) FROM \"{table}\";")));
        Assert.Equal(1, ScalarLong(path, """
            SELECT COUNT(*) FROM local_repository_reconciliation_state
            WHERE projector_key='local-repository-catalog-v1'
              AND last_discovered_span_id IS NULL
              AND updated_at='1970-01-01T00:00:00.0000000+00:00';
            """));
    }

    private static void CorruptNewlyInstalledCatalog(string path) =>
        Execute(path, "ALTER TABLE local_repositories ADD COLUMN task10_post_tail_unexpected TEXT NULL;");

    private static long BackupReceiptCount(string path) =>
        ScalarLong(path, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='runtime_backup_receipts';") == 0
            ? 0
            : ScalarLong(path, "SELECT COUNT(*) FROM runtime_backup_receipts WHERE operation_kind='backup';");

    private static string StringJoin(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return string.Join('\n', values);
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadQueueStoredValues(string path)
    {
        string[] columns =
        [
            "queue_id", "raw_record_id", "input_evidence_kind", "raw_payload_sha256",
            "projector_version", "reconciliation_fingerprint", "state", "attempt_count",
            "lease_token", "lease_expires_at", "terminal_reason", "created_at", "updated_at",
        ];
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(',', columns.Select(column => $"typeof({column}),CAST({column} AS BLOB)"))} FROM local_repository_reconciliation_queue ORDER BY raw_record_id;";
        using var reader = command.ExecuteReader();
        var rows = new List<IReadOnlyList<string>>();
        while (reader.Read())
        {
            var cells = new List<string>(columns.Length);
            for (var ordinal = 0; ordinal < columns.Length * 2; ordinal += 2)
            {
                var bytes = reader.IsDBNull(ordinal + 1) ? [] : reader.GetFieldValue<byte[]>(ordinal + 1);
                cells.Add(reader.GetString(ordinal) + ":" + Convert.ToHexString(bytes));
            }
            rows.Add(cells);
        }
        return rows;
    }

    private static string StoredText(string value) => "text:" + Convert.ToHexString(Encoding.UTF8.GetBytes(value));

    private static void Execute(string path, string sql)
    {
        using var connection = OpenWritable(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task<ColdComposerChildResult> RunColdComposerChildAsync(string databasePath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = FindRepositoryRoot(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("test");
        process.StartInfo.ArgumentList.Add("tests/CopilotAgentObservability.LocalMonitor.Tests/CopilotAgentObservability.LocalMonitor.Tests.csproj");
        process.StartInfo.ArgumentList.Add("--no-build");
        process.StartInfo.ArgumentList.Add("--no-restore");
        process.StartInfo.ArgumentList.Add("--filter");
        process.StartInfo.ArgumentList.Add($"FullyQualifiedName={typeof(LocalRepositoryRuntimeBackupTests).FullName}.ComposerFirstCallUsesOnlyTheCallerOwnedHandleAndReadTransaction");
        process.StartInfo.Environment[ColdComposerChild] = "1";
        process.StartInfo.Environment[ColdComposerDatabase] = databasePath;

        Assert.True(process.Start());
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(10));
            return new(process.ExitCode, await outputTask, await errorTask);
        }
        catch
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
            {
            }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (Exception exception) when (exception is InvalidOperationException or TimeoutException) { }
            try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { }
            throw;
        }
    }

    private static void AssertColdComposerChild(string? databasePath)
    {
        Assert.False(string.IsNullOrWhiteSpace(databasePath));
        var composerType = typeof(LocalRepositoryCatalogBackupValidation);
        Assert.Null(composerType.GetMethod("BuildExpectedObjects", BindingFlags.Static | BindingFlags.NonPublic));
        Assert.DoesNotContain(
            composerType.GetFields(BindingFlags.Static | BindingFlags.NonPublic),
            field => field.FieldType.IsGenericType && field.FieldType.GetGenericTypeDefinition() == typeof(Lazy<>));

        using var connection = Open(databasePath!);
        using var transaction = connection.BeginTransaction(deferred: true);
        var statements = new List<string>();
        var deniedActions = new List<int>();
        strdelegate_trace trace = (_, statement) => statements.Add(statement);
        strdelegate_authorizer authorizer = (_, action, firstArgument, _, _, _) =>
        {
            if (action == raw.SQLITE_PRAGMA && IsReadOnlyComposerPragma(firstArgument)) return raw.SQLITE_OK;
            if (!IsForbiddenComposerAction(action)) return raw.SQLITE_OK;
            deniedActions.Add(action);
            return raw.SQLITE_DENY;
        };

        raw.sqlite3_trace(connection.Handle, trace, null);
        Assert.Equal(raw.SQLITE_OK, raw.sqlite3_set_authorizer(connection.Handle, authorizer, null));
        try
        {
            LocalRepositoryCatalogBackupValidation.Validate(connection, transaction);
            LocalRepositoryCatalogBackupValidation.Validate(connection, transaction);
            Assert.NotEmpty(statements);
            Assert.Empty(deniedActions);
            Assert.All(
                statements.Where(statement => !statement.TrimStart().StartsWith("-- PRAGMA ", StringComparison.OrdinalIgnoreCase)),
                statement => Assert.StartsWith("SELECT", statement.TrimStart(), StringComparison.OrdinalIgnoreCase));
            Assert.All(
                statements.Where(statement => statement.TrimStart().StartsWith("-- PRAGMA ", StringComparison.OrdinalIgnoreCase)),
                statement => Assert.True(
                    IsReadOnlyComposerPragmaTrace(statement),
                    $"Unexpected internal SQLite trace statement: {statement}"));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT 1;";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        finally
        {
            raw.sqlite3_set_authorizer(connection.Handle, (strdelegate_authorizer?)null, null);
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace?)null, null);
        }
        transaction.Rollback();
    }

    private static bool IsForbiddenComposerAction(int action) => action is
        raw.SQLITE_INSERT or raw.SQLITE_UPDATE or raw.SQLITE_DELETE
        or raw.SQLITE_CREATE_INDEX or raw.SQLITE_CREATE_TABLE or raw.SQLITE_CREATE_TEMP_INDEX or raw.SQLITE_CREATE_TEMP_TABLE
        or raw.SQLITE_CREATE_TEMP_TRIGGER or raw.SQLITE_CREATE_TEMP_VIEW or raw.SQLITE_CREATE_TRIGGER or raw.SQLITE_CREATE_VIEW or raw.SQLITE_CREATE_VTABLE
        or raw.SQLITE_DROP_INDEX or raw.SQLITE_DROP_TABLE or raw.SQLITE_DROP_TEMP_INDEX or raw.SQLITE_DROP_TEMP_TABLE
        or raw.SQLITE_DROP_TEMP_TRIGGER or raw.SQLITE_DROP_TEMP_VIEW or raw.SQLITE_DROP_TRIGGER or raw.SQLITE_DROP_VIEW or raw.SQLITE_DROP_VTABLE
        or raw.SQLITE_ALTER_TABLE or raw.SQLITE_ATTACH or raw.SQLITE_DETACH or raw.SQLITE_PRAGMA
        or raw.SQLITE_TRANSACTION or raw.SQLITE_SAVEPOINT or raw.SQLITE_REINDEX or raw.SQLITE_ANALYZE;

    private static bool IsReadOnlyComposerPragma(string? pragma) => pragma is
        "table_list" or "table_xinfo" or "index_list" or "index_xinfo" or "foreign_key_list";

    private static bool IsReadOnlyComposerPragmaTrace(string statement)
    {
        var trimmed = statement.TrimStart();
        return new[] { "table_list", "table_xinfo", "index_list", "index_xinfo", "foreign_key_list" }
            .Any(pragma => trimmed.Equals($"-- PRAGMA {pragma}", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith($"-- PRAGMA {pragma}=", StringComparison.OrdinalIgnoreCase));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Unable to locate repository root.");
    }

    private static async Task<LocalRepositoryAdmissionFixture> CreatePopulatedCatalogAsync()
    {
        var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
            LocalRepositoryAdmissionFixture.Trace(1),
            LocalRepositoryAdmissionFixture.Span(1),
            "https://github.com/Synthetic/RuntimeBackup.git"));
        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        CompleteDiscovery(fixture);

        var sessionId = LocalRepositoryAdmissionFixture.Session(1);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        long nextId = 1;
        string Id(DateTimeOffset _) => $"01900000-0000-7002-8000-{Interlocked.Increment(ref nextId):x12}";
        var queue = new SqliteLocalRepositoryReconciliationStore(
            fixture.DatabasePath,
            fixture.Clock,
            static () => new string('d', 64));
        var store = new SqliteLocalRepositoryCatalogStore(
            fixture.DatabasePath,
            queue,
            new LocalRepositoryAssignmentResolver(Id),
            fixture.Clock,
            Id);
        var application = new LocalRepositoryCatalogApplication(store);
        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
            application.PrepareSessionAction(new(sessionId, 1, "assign", repositoryId))).Prepared;
        var operationKey = "lrc1_" + Convert.ToBase64String(Enumerable.Repeat((byte)0x5a, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var result = await application.ExecutePreparedAsync(
            prepared,
            operationKey,
            LocalRepositoryCatalogFixture.AssignmentEntity,
            CancellationToken.None);

        Assert.IsType<LocalRepositoryMutationSucceeded>(result);
        foreach (var table in LocalRepositoryCatalogSchemaV1.TableNames)
            Assert.True(fixture.ScalarLong($"SELECT COUNT(*) FROM \"{table}\";") > 0, table);
        return fixture;
    }

    private static async Task<OwnerSemanticArchiveFixture> CreateOwnerSemanticArchiveFixtureAsync(
        OwnerSemanticArchiveCase archiveCase)
    {
        switch (archiveCase.SeedKind)
        {
            case "populated":
                {
                    var fixture = await CreatePopulatedCatalogAsync();
                    return new(fixture, fixture.DatabasePath, fixture.Clock);
                }
            case "owner-reuse":
                {
                    var fixture = new LocalRepositoryAdmissionFixture();
                    var sessionId = LocalRepositoryAdmissionFixture.Session(99);
                    await fixture.RunAsync(
                        LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                            LocalRepositoryAdmissionFixture.Trace(1),
                            LocalRepositoryAdmissionFixture.Span(1),
                            "https://github.com/Example/FirstCase")),
                        [LocalRepositoryAdmissionFixture.MatchedEvent(1, sessionId)]);
                    await fixture.RunAsync(
                        LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                            LocalRepositoryAdmissionFixture.Trace(2),
                            LocalRepositoryAdmissionFixture.Span(2),
                            "https://github.com/example/FIRSTCASE")),
                        [LocalRepositoryAdmissionFixture.MatchedEvent(2, sessionId)]);
                    CompleteDiscovery(fixture);
                    return new(fixture, fixture.DatabasePath, fixture.Clock);
                }
            case "locator-128":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    var repository = fixture.Repository(await fixture.CreateAsync(
                        "One", "https://github.com/example/one", fixture.Key(1)));
                    fixture.SeedHistoricalLocators(repository.RepositoryId, 127);
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "resource-span":
                {
                    var fixture = new LocalRepositoryAdmissionFixture();
                    const string locator = "https://github.com/Example/Shared";
                    await fixture.RunAsync(
                        LocalRepositoryAdmissionFixture.ResourceAndSpanPayload(
                            locator,
                            LocalRepositoryAdmissionFixture.Trace(1),
                            LocalRepositoryAdmissionFixture.Span(1),
                            locator),
                        [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
                    CompleteDiscovery(fixture);
                    return new(fixture, fixture.DatabasePath, fixture.Clock);
                }
            case "observation-only":
                {
                    var fixture = new LocalRepositoryAdmissionFixture();
                    await fixture.RunAsync(
                        LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                            LocalRepositoryAdmissionFixture.Trace(1),
                            LocalRepositoryAdmissionFixture.Span(1),
                            "not-a-github-locator")),
                        [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
                    CompleteDiscovery(fixture);
                    return new(fixture, fixture.DatabasePath, fixture.Clock);
                }
            case "candidate-128":
                {
                    var fixture = new LocalRepositoryAdmissionFixture();
                    var sessionId = LocalRepositoryAdmissionFixture.Session(999);
                    var spans = Enumerable.Range(1, 128)
                        .Select(index => new LocalRepositoryAdmissionFixture.SpanInput(
                            LocalRepositoryAdmissionFixture.Trace(index),
                            LocalRepositoryAdmissionFixture.Span(index),
                            $"https://github.com/example/repository-{index}"))
                        .ToArray();
                    var events = Enumerable.Range(1, 128)
                        .Select(index => LocalRepositoryAdmissionFixture.MatchedEvent(index, sessionId))
                        .ToArray();
                    await fixture.RunAsync(LocalRepositoryAdmissionFixture.SpanPayload(spans), events);
                    CompleteDiscovery(fixture);
                    return new(fixture, fixture.DatabasePath, fixture.Clock);
                }
            case "queue-130":
                {
                    var fixture = new BoundedRawCatalogFixture(130);
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "mutation-chain":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    var first = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(10)));
                    _ = await fixture.SetLocatorAsync(first.RepositoryId, 1, "https://github.com/example/one", fixture.Key(11));
                    _ = await fixture.SetLocatorAsync(first.RepositoryId, 2, "https://github.com/example/two", fixture.Key(12));
                    _ = await fixture.RenameAsync(first.RepositoryId, 3, "Four", fixture.Key(13));
                    _ = await fixture.RenameAsync(first.RepositoryId, 4, "Four", fixture.Key(15));
                    _ = fixture.Repository(await fixture.CreateAsync(
                        "Other", "https://github.com/example/other", fixture.Key(14)));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "empty-session":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    fixture.CreateSession(LocalRepositoryCatalogFixture.SessionId(3_700));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "assignment-chain":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    var first = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(20)));
                    _ = fixture.Repository(await fixture.CreateAsync("Two", null, fixture.Key(21)));
                    var sessionId = LocalRepositoryCatalogFixture.SessionId(4_000);
                    fixture.CreateSession(sessionId);
                    _ = await fixture.SessionActionAsync(sessionId, 0, "assign", first.RepositoryId, fixture.Key(22));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "assignment-transition":
                {
                    var fixture = await CreatePopulatedCatalogAsync();
                    return new(fixture, fixture.DatabasePath, fixture.Clock);
                }
            case "assignment-transition-automatic":
                {
                    var fixture = new LocalRepositoryAdmissionFixture();
                    await fixture.RunAsync(
                        LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                            LocalRepositoryAdmissionFixture.Trace(1),
                            LocalRepositoryAdmissionFixture.Span(1),
                            "https://github.com/example/one")),
                        [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
                    CompleteDiscovery(fixture);
                    if (archiveCase.Id == "H7-resume-from-nonmanual")
                    {
                        var sessionId = LocalRepositoryAdmissionFixture.Session(1);
                        var application = CreateReadApplication(fixture.DatabasePath);
                        var prepared = Assert.IsType<LocalRepositoryPreparationSucceeded<LocalRepositoryCatalogApplication.PreparedSessionAction>>(
                            application.PrepareSessionAction(new(sessionId, 1, "resume_automatic", null))).Prepared;
                        var result = await application.ExecutePreparedAsync(
                            prepared,
                            ArchiveOperationKey(0x76),
                            LocalRepositoryCatalogFixture.AssignmentEntity,
                            CancellationToken.None);
                        Assert.IsType<LocalRepositoryMutationSucceeded>(result);
                    }
                    return new(fixture, fixture.DatabasePath, fixture.Clock);
                }
            case "receipt-basic":
            case "receipt-bound":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    _ = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(30)));
                    if (archiveCase.SeedKind == "receipt-bound")
                        fixture.CreateSession(LocalRepositoryCatalogFixture.SessionId(4_800));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "receipt-linked":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(40)));
                    _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(41));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "receipt-duplicate-link":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    _ = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(42)));
                    _ = fixture.Repository(await fixture.CreateAsync("Two", null, fixture.Key(43)));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "receipt-assignment":
            case "receipt-bound-assigned":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(50)));
                    var sessionId = LocalRepositoryCatalogFixture.SessionId(4_800);
                    fixture.CreateSession(sessionId);
                    _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(51));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "receipt-fingerprint-create":
            case "receipt-fingerprint-rename":
            case "receipt-fingerprint-locator-add":
            case "receipt-fingerprint-locator-replace":
            case "receipt-fingerprint-unassign":
            case "receipt-fingerprint-resume":
                return await CreateReceiptFingerprintFixtureAsync(archiveCase.SeedKind);
            case "stale-no-op":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    var repository = fixture.Repository(await fixture.CreateAsync(
                        "One", "https://github.com/example/one", fixture.Key(60)));
                    _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(61));
                    _ = await fixture.SetLocatorAsync(repository.RepositoryId, 2, "https://github.com/example/two", fixture.Key(62));
                    _ = await fixture.SetLocatorAsync(repository.RepositoryId, 3, "https://github.com/example/two", fixture.Key(63));
                    _ = await fixture.RenameAsync(repository.RepositoryId, 3, "Three", fixture.Key(64));
                    var sessionId = LocalRepositoryCatalogFixture.SessionId(4_700);
                    fixture.CreateSession(sessionId);
                    _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(65));
                    _ = await fixture.SessionActionAsync(sessionId, 1, "assign", repository.RepositoryId, fixture.Key(66));
                    _ = await fixture.SessionActionAsync(sessionId, 1, "explicitly_unassign", null, fixture.Key(67));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            case "receipt-129":
                {
                    var fixture = new LocalRepositoryCatalogFixture();
                    for (var index = 0; index < 129; index++)
                        _ = await fixture.CreateAsync($"Repository {index}", null, MatrixOperationKey(index));
                    return new(fixture, fixture.DatabasePath, TimeProvider.System);
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(archiveCase), archiveCase.SeedKind, null);
        }
    }

    private static async Task<OwnerSemanticArchiveFixture> CreateReceiptFingerprintFixtureAsync(string seedKind)
    {
        var fixture = new LocalRepositoryCatalogFixture();
        var initialLocator = seedKind == "receipt-fingerprint-locator-replace"
            ? "https://github.com/example/one"
            : null;
        var repository = fixture.Repository(await fixture.CreateAsync("One", initialLocator, fixture.Key(70)));
        switch (seedKind)
        {
            case "receipt-fingerprint-create":
                break;
            case "receipt-fingerprint-rename":
                _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(71));
                break;
            case "receipt-fingerprint-locator-add":
            case "receipt-fingerprint-locator-replace":
                _ = await fixture.SetLocatorAsync(repository.RepositoryId, 1, "https://github.com/example/two", fixture.Key(72));
                break;
            case "receipt-fingerprint-unassign":
            case "receipt-fingerprint-resume":
                {
                    var sessionId = LocalRepositoryCatalogFixture.SessionId(4_600);
                    fixture.CreateSession(sessionId);
                    _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(73));
                    if (seedKind == "receipt-fingerprint-unassign")
                        _ = await fixture.SessionActionAsync(sessionId, 1, "explicitly_unassign", null, fixture.Key(74));
                    else
                        _ = await fixture.SessionActionAsync(sessionId, 1, "resume_automatic", null, fixture.Key(75));
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(seedKind));
        }
        return new(fixture, fixture.DatabasePath, TimeProvider.System);
    }

    private static void AssertOwnerSemanticSuccessFacts(string caseId, string path)
    {
        switch (caseId)
        {
            case "L3-owner-reuse":
                Assert.Equal(1, ScalarLong(path, "SELECT COUNT(*) FROM local_repositories;"));
                Assert.Equal(1, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_locators;"));
                Assert.Equal(1, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_history WHERE action='create_observed';"));
                Assert.Equal(2, ScalarLong(path, "SELECT COUNT(*) FROM session_repository_observation_contexts;"));
                break;
            case "L5-locator-128":
                Assert.Equal(128, ScalarLong(path, "SELECT revision FROM local_repositories;"));
                Assert.Equal(128, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_locators;"));
                break;
            case "Q8-candidate-128":
                Assert.Equal(128, ScalarLong(path, "SELECT COUNT(*) FROM local_repositories;"));
                Assert.Equal(128, ScalarLong(path, "SELECT COUNT(DISTINCT repository_id) FROM session_repository_observation_contexts WHERE admission_state='admitted';"));
                Assert.Equal("conflict", StringJoin(path, "SELECT new_state FROM session_repository_assignment_history ORDER BY new_revision DESC LIMIT 1;"));
                break;
            case "H4-empty-session":
                Assert.Equal(1, ScalarLong(path, "SELECT COUNT(*) FROM sessions;"));
                Assert.Equal(0, ScalarLong(path, "SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
                Assert.Equal(0, ScalarLong(path, "SELECT COUNT(*) FROM session_repository_assignment_history;"));
                break;
            case "R1-canonical-key":
                Assert.Equal(1, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_operation_receipts WHERE typeof(operation_key)='text' AND length(operation_key)=48;"));
                break;
            case "R7-stale-no-op":
                Assert.Equal(4, ScalarLong(path, "SELECT revision FROM local_repositories;"));
                Assert.Equal(4, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_history;"));
                Assert.Equal(2, ScalarLong(path, "SELECT revision FROM session_repository_assignment_revisions;"));
                Assert.Equal(2, ScalarLong(path, "SELECT COUNT(*) FROM session_repository_assignment_history;"));
                Assert.Equal(8, ScalarLong(path, "SELECT COUNT(*) FROM local_repository_operation_receipts;"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(caseId), caseId, null);
        }
    }

    private static string MatrixOperationKey(int value) => "lrc1_" + Convert.ToBase64String(
        SHA256.HashData(Encoding.UTF8.GetBytes($"task10-public-matrix\0{value}")))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

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
        UPDATE local_repository_reconciliation_state
        SET last_discovered_span_id=(SELECT MAX(id) FROM monitor_spans),
            updated_at='2026-08-01T00:00:00.0000000+00:00'
        WHERE projector_key='local-repository-catalog-v1';
        """);

    private static IReadOnlyDictionary<string, string> ReadCatalogSnapshot(string databasePath)
    {
        using var connection = Open(databasePath);
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in LocalRepositoryCatalogSchemaV1.TableNames)
        {
            using var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = $"PRAGMA table_info(\"{table}\");";
            using var columnsReader = columnsCommand.ExecuteReader();
            var columns = new List<string>();
            while (columnsReader.Read()) columns.Add(columnsReader.GetString(1));

            using var bytes = new MemoryStream();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT * FROM \"{table}\" ORDER BY {string.Join(',', columns.Select(column => $"\"{column}\""))};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                for (var ordinal = 0; ordinal < reader.FieldCount; ordinal++)
                {
                    var value = reader.GetValue(ordinal);
                    var payload = value switch
                    {
                        DBNull => Array.Empty<byte>(),
                        byte[] blob => blob,
                        long number => Encoding.UTF8.GetBytes(number.ToString(CultureInfo.InvariantCulture)),
                        string text => Encoding.UTF8.GetBytes(text),
                        _ => throw new InvalidOperationException(),
                    };
                    bytes.Write(BitConverter.GetBytes(payload.Length));
                    bytes.Write(payload);
                }
            }
            result.Add(table, Convert.ToHexString(SHA256.HashData(bytes.ToArray())).ToLowerInvariant());
        }
        return result;
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static SqliteConnection OpenWritable(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class ValidationObserver : ILocalRepositoryCatalogBackupValidationObserver
    {
        internal List<LocalRepositoryCatalogBackupValidationPhase> Phases { get; } = [];
        internal List<int> RawIdPages { get; } = [];
        internal List<int> RawPayloadResidentCounts { get; } = [];
        internal int RawPayloadHashCount { get; private set; }

        public void PhaseEntered(LocalRepositoryCatalogBackupValidationPhase phase) => Phases.Add(phase);
        public void RawIdPageMaterialized(int count) => RawIdPages.Add(count);
        public void RawPayloadMaterialized(int count) => RawPayloadResidentCounts.Add(count);
        public void RawPayloadHashed() => RawPayloadHashCount++;
    }

    private sealed record ColdComposerChildResult(int ExitCode, string Output, string Error);

    public sealed record StorageGuardCase(string Id, string Table, string Sql, object? Value = null)
    {
        public override string ToString() => Id;
    }

    public sealed record OwnerSemanticArchiveCase(
        string Id,
        string Family,
        IReadOnlyList<string> CoverageTags,
        string SeedKind,
        bool Succeeds,
        string? ExpectedErrorCode,
        bool ComposerReachable)
    {
        public override string ToString() => Id;
    }

    private sealed class OwnerSemanticArchiveFixture(
        IDisposable owner,
        string databasePath,
        TimeProvider clock) : IDisposable
    {
        internal string DatabasePath { get; } = databasePath;
        internal TimeProvider Clock { get; } = clock;

        public void Dispose() => owner.Dispose();
    }

    private sealed class BoundedRawCatalogFixture : IDisposable
    {
        private readonly MonitorTempDirectory temp = new();
        private readonly long lastRawRecordId;

        internal BoundedRawCatalogFixture(int count)
        {
            var rawStore = temp.CreateRawStore();
            rawStore.CreateMonitorSchema();
            new SqliteSourceCompatibilityStore(temp.DatabasePath).CreateSchema();
            new SqliteSessionStore(temp.DatabasePath).CreateSchema();
            using (var schema = OpenWritable(temp.DatabasePath))
                LocalRepositoryCatalogSchemaV1.Ensure(schema);

            var rows = new List<(long RawRecordId, string Digest)>(count);
            for (var index = 1; index <= count; index++)
            {
                var payload = $"{{\"task10\":{index.ToString(CultureInfo.InvariantCulture)}}}";
                var rawRecordId = rawStore.Insert(new(
                    null,
                    RawTelemetrySources.RawOtlp,
                    null,
                    DateTimeOffset.ParseExact(LocalRepositoryAdmissionFixture.ObservedAt, "O", CultureInfo.InvariantCulture),
                    null,
                    payload));
                rows.Add((rawRecordId, SkillProjectionHashing.InputDigest(payload)));
            }
            lastRawRecordId = rows[^1].RawRecordId;

            using var connection = OpenWritable(temp.DatabasePath);
            using var transaction = connection.BeginTransaction(deferred: false);
            foreach (var (rawRecordId, digest) in rows)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,projected_at)
                    VALUES($raw,$trace,NULL,0,$at);
                    INSERT INTO local_repository_reconciliation_queue(
                        queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,
                        reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,
                        terminal_reason,created_at,updated_at)
                    VALUES($queue,$raw,'payload_sha256',$digest,'local-repository-catalog:1',
                        $fingerprint,'pending',0,NULL,NULL,NULL,$at,$at);
                    """;
                command.Parameters.AddWithValue("$raw", rawRecordId);
                command.Parameters.AddWithValue("$trace", rawRecordId.ToString("x32", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$at", LocalRepositoryAdmissionFixture.ObservedAt);
                command.Parameters.AddWithValue("$queue", $"01900000-0000-7003-8000-{rawRecordId:x12}");
                command.Parameters.AddWithValue("$digest", digest);
                command.Parameters.AddWithValue("$fingerprint", LocalRepositoryIdentityHashing.ReconciliationFingerprint(
                    LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, digest)));
                command.ExecuteNonQuery();
            }
            Execute(connection, transaction, $"""
                UPDATE local_repository_reconciliation_state
                SET last_discovered_span_id=(SELECT MAX(id) FROM monitor_spans),
                    updated_at='{LocalRepositoryAdmissionFixture.ObservedAt}'
                WHERE projector_key='local-repository-catalog-v1';
                """);
            transaction.Commit();
        }

        internal string DatabasePath => temp.DatabasePath;

        internal void CorruptLastPayload() =>
            Execute(DatabasePath, $"UPDATE raw_records SET payload_json='{{}}' WHERE id={lastRawRecordId};");

        public void Dispose() => temp.Dispose();
    }

    private sealed class LeaseCatalogFixture : IDisposable
    {
        private readonly LocalRepositoryAdmissionFixture fixture = new();

        internal LeaseCatalogFixture()
        {
            var rows = new[]
            {
                new QueueSeed(901, "leased", 7, "payload_sha256", new string('1', 64), new string('a', 64), "9998-12-31T23:59:30.0000000+00:00", null, "9998-12-31T23:59:00.0000000+00:00"),
                new QueueSeed(902, "leased", 9, "payload_sha256", new string('2', 64), new string('b', 64), "2000-01-01T00:00:30.0000000+00:00", null, "2000-01-01T00:00:00.0000000+00:00"),
                new QueueSeed(903, "completed", 1, "payload_sha256", new string('3', 64), null, null, null, LocalRepositoryAdmissionFixture.ObservedAt),
                new QueueSeed(904, "pending", 0, "payload_sha256", new string('4', 64), null, null, null, LocalRepositoryAdmissionFixture.ObservedAt),
                new QueueSeed(905, "waiting_session", 1, "payload_sha256", new string('5', 64), null, null, null, LocalRepositoryAdmissionFixture.ObservedAt),
                new QueueSeed(906, "input_unavailable", 0, "input_unavailable", null, null, null, null, LocalRepositoryAdmissionFixture.ObservedAt),
                new QueueSeed(907, "failed_terminal", 1, "payload_sha256", new string('7', 64), null, null, "catalog_parse_failure", LocalRepositoryAdmissionFixture.ObservedAt),
            };
            using var connection = OpenWritable(DatabasePath);
            using var transaction = connection.BeginTransaction(deferred: false);
            foreach (var row in rows)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO monitor_spans(raw_record_id,trace_id,span_id,span_ordinal,projected_at)
                    VALUES($raw,$trace,NULL,0,$at);
                    INSERT INTO local_repository_reconciliation_queue(
                        queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,
                        reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,
                        terminal_reason,created_at,updated_at)
                    VALUES($queue,$raw,$evidence,$digest,'local-repository-catalog:1',$fingerprint,
                        $state,$attempts,$token,$expiry,$terminal,$at,$updated);
                    """;
                command.Parameters.AddWithValue("$raw", row.RawRecordId);
                command.Parameters.AddWithValue("$trace", row.RawRecordId.ToString("x32", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$at", LocalRepositoryAdmissionFixture.ObservedAt);
                command.Parameters.AddWithValue("$queue", $"01900000-0000-7004-8000-{row.RawRecordId:x12}");
                command.Parameters.AddWithValue("$evidence", row.EvidenceKind);
                command.Parameters.AddWithValue("$digest", (object?)row.Digest ?? DBNull.Value);
                command.Parameters.AddWithValue("$fingerprint", LocalRepositoryIdentityHashing.ReconciliationFingerprint(
                    row.EvidenceKind == "input_unavailable"
                        ? LocalRepositoryReconciliationEvidence.InputUnavailable(row.RawRecordId)
                        : LocalRepositoryReconciliationEvidence.PayloadSha256(row.RawRecordId, row.Digest!)));
                command.Parameters.AddWithValue("$state", row.State);
                command.Parameters.AddWithValue("$attempts", row.AttemptCount);
                command.Parameters.AddWithValue("$token", (object?)row.LeaseToken ?? DBNull.Value);
                command.Parameters.AddWithValue("$expiry", (object?)row.LeaseExpiry ?? DBNull.Value);
                command.Parameters.AddWithValue("$terminal", (object?)row.TerminalReason ?? DBNull.Value);
                command.Parameters.AddWithValue("$updated", row.UpdatedAt);
                command.ExecuteNonQuery();
            }
            Execute(connection, transaction, $"""
                UPDATE local_repository_reconciliation_state
                SET last_discovered_span_id=(SELECT MAX(id) FROM monitor_spans),
                    updated_at='{LocalRepositoryAdmissionFixture.ObservedAt}'
                WHERE projector_key='local-repository-catalog-v1';
                """);
            transaction.Commit();
            BundlePath = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "lease-catalog.zip");
            TargetPath = Path.Combine(Path.GetDirectoryName(DatabasePath)!, "lease-catalog-restored.db");
        }

        internal string DatabasePath => fixture.DatabasePath;
        internal TimeProvider Clock => fixture.Clock;
        internal string BundlePath { get; }
        internal string TargetPath { get; }

        internal void ProveRollback(IReadOnlyList<IReadOnlyList<string>> expected)
        {
            using var connection = OpenWritable(DatabasePath);
            using var transaction = connection.BeginTransaction(deferred: false);
            SqliteLocalRepositoryReconciliationStore.NormalizeRestoredLeases(connection, transaction);
            transaction.Rollback();
            Assert.Equal(expected, ReadQueueStoredValues(DatabasePath));
        }

        public void Dispose() => fixture.Dispose();

        private sealed record QueueSeed(
            long RawRecordId,
            string State,
            long AttemptCount,
            string EvidenceKind,
            string? Digest,
            string? LeaseToken,
            string? LeaseExpiry,
            string? TerminalReason,
            string UpdatedAt);
    }

    private static void ValidateCatalog(string path)
    {
        using var connection = Open(path);
        using var transaction = connection.BeginTransaction(deferred: true);
        var actual = ReadOwnedObjects(connection, transaction);
        using var expectedConnection = new SqliteConnection("Data Source=:memory:");
        expectedConnection.Open();
        using var expectedTransaction = expectedConnection.BeginTransaction();
        SqliteSessionStore.InitializeSchema(expectedConnection, expectedTransaction, DateTimeOffset.UnixEpoch);
        LocalRepositoryCatalogSchemaV1.Ensure(expectedConnection, expectedTransaction);
        var expected = ReadOwnedObjects(expectedConnection, expectedTransaction);
        Assert.True(SqliteOwnedSchemaAuthority.Equal(actual, expected),
            $"actual={string.Join(',', actual.Keys)}; expected={string.Join(',', expected.Keys)}");
        Assert.True(SqliteSessionStore.IsCurrentSchemaValid(connection, transaction), "session shape");
        using (var versions = connection.CreateCommand())
        {
            versions.Transaction = transaction;
            versions.CommandText = "SELECT group_concat(component || ':' || typeof(version) || ':' || version, ',') FROM schema_version ORDER BY component;";
            Assert.Contains("session:integer:14", Convert.ToString(versions.ExecuteScalar(), CultureInfo.InvariantCulture));
            Assert.Contains("local_repository_catalog:integer:1", Convert.ToString(versions.ExecuteScalar(), CultureInfo.InvariantCulture));
        }
        LocalRepositoryCatalogBackupValidation.Validate(connection, transaction);
        transaction.Rollback();
    }

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject> ReadOwnedObjects(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        SqliteOwnedSchemaAuthority.Read(connection, transaction, static (name, table) =>
            name.Equals("local_repositories", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("local_repository_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("session_repository_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("IX_local_repository_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("IX_session_repository_", StringComparison.OrdinalIgnoreCase)
            || table.Equals("local_repositories", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("local_repository_", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("session_repository_", StringComparison.OrdinalIgnoreCase));
}

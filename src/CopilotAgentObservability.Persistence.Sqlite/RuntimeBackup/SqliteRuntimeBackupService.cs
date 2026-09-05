using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalInstructionAnalysis;
using CopilotAgentObservability.Persistence.Sqlite.LocalAi;
using CopilotAgentObservability.Persistence.Sqlite.Pricing;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Persistence.Sqlite.SanitizedImport;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

public sealed class SqliteRuntimeBackupService
{
    private enum RetentionCoverageValidation
    {
        Operational,
        Restorable,
    }

    internal const string CatalogAfterInitializeTailCheckpoint = "catalog-after-initialize-tail";
    internal const string CatalogAfterMonitorTailCheckpoint = "catalog-after-monitor-tail";
    internal const string CatalogAfterCreateTailCheckpoint = "catalog-after-create-tail";
    internal const string CatalogBeforeInstalledValidationCheckpoint = "catalog-before-installed-validation";
    internal const string BeforeEmptyReadSidecarOwnershipCheckpoint = "before-empty-read-sidecar-ownership";
    internal const string BeforeEmptyReadSidecarDeleteCheckpoint = "before-empty-read-sidecar-delete";
    private static readonly DateTimeOffset ArchiveTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison FileNameComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly uint[] Crc32Table = BuildCrc32Table();
    private static readonly byte[] TransientOwnerMarkerBytes = "runtime-backup-transient-owner.v1\n"u8.ToArray();
    private const int ReconciliationPageSize = 16;
    private const uint WindowsSidecarDeleteAccess = 0xc0010000;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsNormalFileAttributes = 0x80;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const int WindowsFileDispositionInformationClass = 4;
    private static readonly IReadOnlyDictionary<string, int> SupportedComponents = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["alert_engine"] = 2,
        ["alert_lifecycle"] = 1,
        ["doctor"] = 1,
        ["first_trace_navigation"] = 1,
        ["historical_import"] = 1,
        ["historical_instruction_analysis"] = 1,
        ["local_archive"] = 1,
        ["local_ai_analysis"] = LocalAiAnalysisSchemaV1.Version,
        ["local_comparison"] = LocalComparisonSchemaV1.Version,
        ["local_repository_catalog"] = 1,
        ["local_workspace_projection"] = LocalWorkspaceProjectionSchemaV1.Version,
        ["monitor"] = 12,
        ["pricing"] = 1,
        ["retention"] = 1,
        ["runtime_backup"] = 1,
        ["sanitized_import"] = 1,
        ["session"] = 14,
        ["skill_invocation_snapshot"] = 1,
        ["skill_projection"] = 1,
    };
    private static readonly IReadOnlyDictionary<string, int> InstalledArchiveComponents = SupportedComponents
        .Where(component => component.Key is not ("local_comparison" or "local_ai_analysis"))
        .ToDictionary(component => component.Key, component => component.Value, StringComparer.Ordinal);
    private static readonly string[] RetentionStoreKinds = ["session_event_content", "raw_record", "analysis_run_raw", "sensitive_bundle", "analysis_sdk_directory"];
    private static readonly string[] RetentionStates = ["expiring", "retained_by_policy", "expired_pending_deletion", "deletion_queued", "deleting", "deleted", "deletion_failed"];
    private static readonly string[] MigrationOrder =
    [
        "monitor",
        "session",
        "local_repository_catalog",
        "local_archive",
        "retention",
        "skill_projection",
        "skill_invocation_snapshot",
        "local_workspace_projection",
        "doctor",
        "alert_engine",
        "alert_lifecycle",
        "first_trace_navigation",
        "historical_instruction_analysis",
        "historical_import",
        "sanitized_import",
        "runtime_backup",
        "pricing",
    ];
    private readonly TimeProvider timeProvider;
    private readonly Action<string>? checkpoint;
    private readonly Func<string, string> installedDoctorCheck;
    private readonly Func<string, bool> restoreFailureCleanup;
    private readonly string archiveWriterVersion = SkillInvocationV2ArtifactRegistry.CurrentWriterVersion;
    private readonly ILocalWorkspacePublicationGate? publicationGate;
    private ISkillRegistryGenerationAuthority? skillRegistryAuthority;

    public SqliteRuntimeBackupService(TimeProvider? timeProvider = null) :
        this(timeProvider, (Action<string>?)null, null, null)
    { }

    internal SqliteRuntimeBackupService(
        TimeProvider timeProvider,
        ISkillRegistryGenerationAuthority skillRegistryAuthority,
        ILocalWorkspacePublicationGate publicationGate)
        : this(timeProvider, (Action<string>?)null, null, null)
    {
        this.skillRegistryAuthority = skillRegistryAuthority ?? throw new ArgumentNullException(nameof(skillRegistryAuthority));
        this.publicationGate = publicationGate ?? throw new ArgumentNullException(nameof(publicationGate));
    }

    internal SqliteRuntimeBackupService(
        TimeProvider timeProvider,
        FixedSkillRegistryGenerationAuthority skillRegistryAuthority,
        ILocalWorkspacePublicationGate publicationGate,
        string archiveWriterVersion)
        : this(timeProvider, skillRegistryAuthority, publicationGate)
    {
        ArgumentException.ThrowIfNullOrEmpty(archiveWriterVersion);
        if (!skillRegistryAuthority.MatchesWriterVersion(archiveWriterVersion))
            throw new ArgumentException("Writer provenance does not match the fixed registry authority.", nameof(archiveWriterVersion));
        this.archiveWriterVersion = archiveWriterVersion;
    }

    internal SqliteRuntimeBackupService(TimeProvider? timeProvider, Action<string>? checkpoint) :
        this(timeProvider, checkpoint, null)
    {
    }

    internal SqliteRuntimeBackupService(
        TimeProvider? timeProvider,
        Action<string>? checkpoint,
        Func<string, string>? installedDoctorCheck,
        Func<string, bool>? restoreFailureCleanup = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
        this.installedDoctorCheck = installedDoctorCheck ?? DoctorCheck;
        this.restoreFailureCleanup = restoreFailureCleanup ?? RecoverInterruptedRestore;
        skillRegistryAuthority = FixedSkillRegistryGenerationAuthority.Load();
        publicationGate = new LocalWorkspacePublicationGate();
    }

    internal void ConfigureSkillRegistryAuthority(ISkillRegistryGenerationAuthority authority)
    {
        skillRegistryAuthority = authority ?? throw new ArgumentNullException(nameof(authority));
    }

    public RuntimeBackupPreflightResult Initialize(string databasePath)
    {
        if (!TryFullFile(databasePath, mustExist: false, out var database))
            return new(false, RuntimeBackupErrorCodes.InvalidArguments);
        using var lease = TryAcquireRestoreLease(database + ".runtime-restore.lock");
        if (lease is null) return new(false, RuntimeBackupErrorCodes.MonitorMustBeStopped);
        try
        {
            return InitializeWithLease(database);
        }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException or InvalidDataException or JsonException or InvalidCastException or FormatException or OverflowException || IsIo(exception))
        {
            return new(false, RuntimeBackupErrorCodes.RestoreIncompatible);
        }
    }

    public (RuntimeBackupPreflightResult Result, RuntimeBackupMonitorLease? Lease) InitializeForMonitor(string databasePath)
    {
        var publicationLease = publicationGate?.AcquireReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            if (!TryFullFile(databasePath, mustExist: false, out var database))
                return (new(false, RuntimeBackupErrorCodes.InvalidArguments), null);
            var stream = TryAcquireRestoreLease(database + ".runtime-restore.lock");
            if (stream is null) return (new(false, RuntimeBackupErrorCodes.MonitorMustBeStopped), null);
            var lease = new RuntimeBackupMonitorLease(database, stream);
            try
            {
                var result = PrepareMonitorInitializationWithLease(database, refreshLocalWorkspaceProjection: true);
                if (!result.Success)
                {
                    lease.Dispose();
                    return (result, null);
                }
                return (result, lease);
            }
            catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException || IsIo(exception))
            {
                lease.Dispose();
                return (new(false, RuntimeBackupErrorCodes.RestoreIncompatible), null);
            }
        }
        finally
        {
            publicationLease?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public RuntimeBackupPreflightResult CompleteMonitorInitialization(RuntimeBackupMonitorLease lease)
    {
        var publicationLease = publicationGate?.AcquireReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
        try
        {
            ArgumentNullException.ThrowIfNull(lease);
            if (!lease.IsActive) return new(false, RuntimeBackupErrorCodes.MonitorMustBeStopped);
            try { return CompleteMonitorInitializationWithLease(lease.DatabasePath); }
            catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException || IsIo(exception))
            { return new(false, RuntimeBackupErrorCodes.RestoreIncompatible); }
        }
        finally
        {
            publicationLease?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private RuntimeBackupPreflightResult PrepareMonitorInitializationWithLease(
        string database,
        bool refreshLocalWorkspaceProjection)
    {
        if (!RecoverTransientFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database))
            || !RecoverTransientFiles(Path.Combine(Path.GetDirectoryName(database)!, "runtime-backups"), Path.GetFileName(database)))
            return new(false, RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        var recoveryGuard = PathEntryExists(database) ? TryAcquireStartupRecoveryGuard(database) : null;
        if ((PathEntryExists(database) && recoveryGuard is null) || (!PathEntryExists(database) && (HasLiveMonitorState(database) || HasActiveSqliteSidecar(database))))
            return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
        if (!RecoverInterruptedRestore(database))
        {
            recoveryGuard?.Dispose();
            return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
        }
        if (!PathEntryExists(database))
        {
            recoveryGuard?.Dispose();
            return new(true, null, new Dictionary<string, int>(), []);
        }
        if (!TryRemoveEmptyReadSidecars(database))
        {
            recoveryGuard?.Dispose();
            return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
        }
        recoveryGuard?.Dispose();
        if (refreshLocalWorkspaceProjection)
            RefreshLiveLocalWorkspaceProjection(database);
        using var validationGuard = TryAcquireRecoveryGuard(database);
        if (validationGuard is null) return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
        RuntimeBackupPreflightResult result;
        using (var validationOwner = OpenValidationSidecarOwner(database))
            result = ValidateMonitorComponentVersions(database);
        return TryRemoveEmptyReadSidecars(database)
            ? result
            : new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
    }

    private void RefreshLiveLocalWorkspaceProjection(string database)
    {
        using var connection = Open(database, SqliteOpenMode.ReadWrite, immutableReadOnly: false);
        using var transaction = connection.BeginTransaction();
        using var installed = connection.CreateCommand();
        installed.Transaction = transaction;
        installed.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='schema_version');";
        bool hasOwnedObjects;
        var hasVersionTable = Convert.ToInt64(installed.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        installed.CommandText = "SELECT name,tbl_name FROM sqlite_schema;";
        using (var reader = installed.ExecuteReader())
        {
            hasOwnedObjects = false;
            while (reader.Read() && !hasOwnedObjects)
                hasOwnedObjects = reader.GetString(0).StartsWith("local_workspace_", StringComparison.OrdinalIgnoreCase)
                    || reader.GetString(1).StartsWith("local_workspace_", StringComparison.OrdinalIgnoreCase);
        }
        if (!hasVersionTable && !hasOwnedObjects)
        {
            transaction.Commit();
            return;
        }
        if (!hasVersionTable) throw new InvalidOperationException("local_workspace_projection_component_stamp_missing");
        installed.CommandText = "SELECT version FROM schema_version WHERE component='local_workspace_projection';";
        var version = installed.ExecuteScalar();
        if (version is null && !hasOwnedObjects)
        {
            transaction.Commit();
            return;
        }
        LocalWorkspaceProjectionSchemaV1.ValidateCurrentOrExactLegacy(connection, transaction);
        if (version is long current && current == LocalWorkspaceProjectionSchemaV1.Version)
        {
            var terminalAuthority = LocalWorkspaceTerminalAuthority.Capture(connection, transaction);
            terminalAuthority.ApplyTombstones(connection, transaction);
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, timeProvider.GetUtcNow(),
                skillRegistryAuthority ?? throw new InvalidOperationException("local_workspace_projection_authority_unavailable"));
            terminalAuthority.ApplyReadDenied(connection, transaction);
            LocalWorkspaceProjectionSchemaV1.ValidateAndRestampCurrent(connection, transaction);
        }
        transaction.Commit();
    }

    private RuntimeBackupPreflightResult CompleteMonitorInitializationWithLease(string database)
    {
        if (!PathEntryExists(database))
            return new(true, null, new Dictionary<string, int>(), []);
        var preparation = PreflightForMigration(database, immutableReadOnly: false);
        if (!preparation.Success) return preparation;
        EnsureCurrentBackupTail(database);
        checkpoint?.Invoke(CatalogAfterMonitorTailCheckpoint);
        return PreflightForMigration(database, immutableReadOnly: false);
    }

    private RuntimeBackupPreflightResult InitializeWithLease(string database)
    {
        var preparation = PrepareMonitorInitializationWithLease(database, refreshLocalWorkspaceProjection: false);
        if (!preparation.Success || !PathEntryExists(database)) return preparation;
        RuntimeBackupPreflightResult preflight;
        using (var recoveryGuard = TryAcquireRecoveryGuard(database))
        {
            if (recoveryGuard is null) return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
            using (var validationOwner = OpenValidationSidecarOwner(database))
                preflight = PreflightForMigration(database, immutableReadOnly: false);
            if (!TryRemoveEmptyReadSidecars(database))
                return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
        }
        if (!preflight.Success) return preflight;
        using (var validationOwner = OpenValidationSidecarOwner(database))
            EnsureCurrentBackupTail(database);
        if (!TryRemoveEmptyReadSidecars(database))
            return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
        checkpoint?.Invoke(CatalogAfterInitializeTailCheckpoint);
        using var finalGuard = TryAcquireRecoveryGuard(database);
        if (finalGuard is null) return new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
        RuntimeBackupPreflightResult final;
        using (var validationOwner = OpenValidationSidecarOwner(database))
            final = PreflightForMigration(database, immutableReadOnly: false);
        return TryRemoveEmptyReadSidecars(database)
            ? final
            : new(false, RuntimeBackupErrorCodes.RestoreRollbackFailed);
    }

    private RuntimeBackupPreflightResult ValidateMonitorComponentVersions(string database)
    {
        try
        {
            using var connection = Open(database, SqliteOpenMode.ReadOnly, immutableReadOnly: false);
            ValidateIntegrity(connection);
            if (!TableExists(connection, "schema_version")
                && !TableExists(connection, "retention_component_versions"))
            {
                var emptyVersions = new Dictionary<string, int>(StringComparer.Ordinal);
                return !ValidateOwnedComponentNamespaces(connection, emptyVersions)
                    ? new(false, RuntimeBackupErrorCodes.RestoreIncompatible, emptyVersions)
                    : new(true, null, emptyVersions, []);
            }
            var versions = ReadComponentVersions(connection, requireSharedSchemaVersion: false);
            foreach (var item in versions)
            {
                if (item.Value <= 0 || !SupportedComponents.TryGetValue(item.Key, out var supported) || item.Value > supported)
                    return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            }
            if (versions.ContainsKey("sanitized_import") && !versions.ContainsKey("historical_import"))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (!HasValidPricingParents(versions))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (versions.ContainsKey("skill_projection")
                && (!versions.TryGetValue("monitor", out var monitorVersion) || monitorVersion < 11
                    || !versions.TryGetValue("retention", out var retentionVersion)
                    || retentionVersion != SupportedComponents["retention"]))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (HasUndeclaredLocalRepositoryCatalogObjects(connection, versions))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (!HasValidLocalArchiveParents(versions)
                || !HasValidLocalWorkspaceProjectionParents(versions)
                || !HasValidSkillInvocationSnapshotParents(versions))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (versions.ContainsKey("local_repository_catalog")
                && (!versions.TryGetValue("session", out var sessionVersion)
                    || !IsCurrentOrLegacySessionParent(sessionVersion)
                    || !ValidateComponentShapes(
                        database,
                        versions,
                        immutableReadOnly: false,
                        skillRegistryAuthority: versions.TryGetValue("local_workspace_projection", out var workspaceVersion)
                            && workspaceVersion == SupportedComponents["local_workspace_projection"]
                            ? ReadCurrentWriterStructuralAuthority(database, immutableReadOnly: false)
                            : null)))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            return new(true, null, versions, []);
        }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException or InvalidCastException or FormatException or OverflowException)
        {
            return new(false, RuntimeBackupErrorCodes.RestoreIncompatible);
        }
    }

    public RuntimeBackupCreateResult CreateAndPublish(string databasePath, string outputPath)
    {
        if (!IsHostNativeAbsoluteLocalPath(databasePath) || !IsHostNativeAbsoluteLocalPath(outputPath))
            return CreateFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!TryFullFile(databasePath, mustExist: true, out var database) || !TryFullFile(outputPath, mustExist: false, out var output))
            return CreateFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (IsReservedDatabaseOutput(output, database) || IsReservedTransientArtifactName(output))
            return CreateFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!RecoverTransientFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database))
            || !RecoverTransientFiles(Path.GetDirectoryName(output)!, Path.GetFileName(database)))
            return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        if (PathEntryExists(output)) return CreateFailure(RuntimeBackupErrorCodes.OutputExists);
        IAsyncDisposable? publicationLease = null;
        try
        {
            publicationLease = publicationGate?.AcquireReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            var externalError = ValidateExternalState(database, scanRuntimeRoot: true, immutableDatabase: false);
            if (externalError is not null) return CreateFailure(externalError);
            var publicationTime = timeProvider.GetUtcNow();
            _ = ReadProjectionCursors(database, immutableReadOnly: false);
            var sourcePreflight = PreflightForMigration(
                database,
                immutableReadOnly: false,
                archiveWriterVersion: archiveWriterVersion,
                workspaceShapeOnly: true,
                reportBusy: true);
            if (!sourcePreflight.Success)
                return CreateFailure(sourcePreflight.ErrorCode!);
            using var pinnedAuthority = CaptureSkillRegistryAuthority();
            EnsureCurrentBackupTail(database, publicationTime, pinnedAuthority);
            RefreshProjectionForPublication(database, publicationTime, pinnedAuthority);
            checkpoint?.Invoke(CatalogAfterCreateTailCheckpoint);
            if (!PreflightForMigration(
                    database,
                    immutableReadOnly: false,
                    publicationTime,
                    publicationAuthority: pinnedAuthority,
                    allowDeniedLocalAiContent: true).Success)
                return CreateFailure(RuntimeBackupErrorCodes.RestoreIncompatible);
            var result = CreateArchive(
                database,
                output,
                scanRuntimeRoot: true,
                publicationTime: publicationTime,
                capturedAuthority: pinnedAuthority);
            if (!result.Success) return result;
            _ = TryAppendReceipt(database, "backup", result.ArchiveSha256!, "backup_succeeded", 0, false);
            return result;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreBusy); }
        catch (RuntimeBackupException exception) { return CreateFailure(exception.Code); }
        catch (SqliteException) { return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable); }
        catch (Exception exception) when (IsIo(exception)) { return CreateFailure(RuntimeBackupErrorCodes.PublishFailed); }
        catch (InvalidOperationException) { return CreateFailure(RuntimeBackupErrorCodes.RestoreIncompatible); }
        finally { publicationLease?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void RefreshProjectionForPublication(
        string databasePath,
        DateTimeOffset publicationTime,
        ISkillRegistryGenerationAuthority authority)
    {
        using var connection = Open(databasePath, SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction(deferred: false);
        var state = LocalWorkspaceProjectionSchemaV1.ReadInstallationState(connection, transaction);
        if (state == LocalWorkspaceProjectionInstallationState.Unsupported)
            throw new InvalidOperationException("local_workspace_projection_schema_unsupported");
        if (state == LocalWorkspaceProjectionInstallationState.Current)
        {
            var terminalAuthority = LocalWorkspaceTerminalAuthority.Capture(connection, transaction);
            terminalAuthority.ApplyTombstones(connection, transaction);
            LocalWorkspaceProjectionStore.Refresh(connection, transaction, publicationTime,
                authority);
            terminalAuthority.ApplyReadDenied(connection, transaction);
            LocalWorkspaceProjectionSchemaV1.ValidateAndRestampCurrent(connection, transaction);
        }
        transaction.Commit();
    }

    public RuntimeBackupInspectionResult Inspect(string bundlePath)
    {
        if (!TryFullFile(bundlePath, mustExist: true, out var bundle)) return InspectionFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!RecoverTransientFiles(Path.GetDirectoryName(bundle)!, databaseFileName: null))
            return InspectionFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        InspectionContext? context = null;
        try
        {
            context = InspectToStage(bundle);
            var result = context.Result;
            if (!context.Cleanup()) return InspectionFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            context = null;
            return result;
        }
        catch (RuntimeBackupException exception) { return InspectionFailure(exception.Code); }
        catch (InvalidDataException) { return InspectionFailure(RuntimeBackupErrorCodes.ArchiveInvalid); }
        catch (SqliteException) { return InspectionFailure(RuntimeBackupErrorCodes.ArchiveInvalid); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException) { return InspectionFailure(RuntimeBackupErrorCodes.RestoreIncompatible); }
        catch (Exception exception) when (IsIo(exception)) { return InspectionFailure(RuntimeBackupErrorCodes.ArchiveInvalid); }
        finally { _ = context?.Cleanup(); }
    }

    public RuntimeBackupInspectionResult Inspect(FileStream bundleStream)
    {
        ArgumentNullException.ThrowIfNull(bundleStream);
        if (!bundleStream.CanRead || !bundleStream.CanSeek
            || !TryFullFile(bundleStream.Name, mustExist: true, out var bundle)
            || RuntimeBackupNativePathClassifier.Read(bundleStream.SafeFileHandle) != RuntimeBackupNativePathKind.RegularFile)
            return InspectionFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!RecoverTransientFiles(Path.GetDirectoryName(bundle)!, databaseFileName: null))
            return InspectionFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        InspectionContext? context = null;
        try
        {
            context = InspectToStage(bundleStream, bundle);
            var result = context.Result;
            if (!context.Cleanup()) return InspectionFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            context = null;
            return result;
        }
        catch (RuntimeBackupException exception) { return InspectionFailure(exception.Code); }
        catch (InvalidDataException) { return InspectionFailure(RuntimeBackupErrorCodes.ArchiveInvalid); }
        catch (SqliteException) { return InspectionFailure(RuntimeBackupErrorCodes.ArchiveInvalid); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException) { return InspectionFailure(RuntimeBackupErrorCodes.RestoreIncompatible); }
        catch (Exception exception) when (IsIo(exception)) { return InspectionFailure(RuntimeBackupErrorCodes.ArchiveInvalid); }
        finally { _ = context?.Cleanup(); }
    }

    public RuntimeBackupPreflightResult PreflightForMigration(string databasePath)
    {
        if (!TryFullFile(databasePath, mustExist: true, out var database))
            return new(false, RuntimeBackupErrorCodes.InvalidArguments);
        using var guard = TryAcquireRecoveryGuard(database);
        RuntimeBackupPreflightResult result;
        try
        {
            using var validationOwner = guard is not null ? OpenValidationSidecarOwner(database) : null;
            result = PreflightForMigration(database, immutableReadOnly: false);
        }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException or InvalidCastException or FormatException or OverflowException)
        {
            result = new(false, RuntimeBackupErrorCodes.RestoreIncompatible);
        }
        return guard is null || TryRemoveEmptyReadSidecars(database)
            ? result
            : new(false, RuntimeBackupErrorCodes.RestoreIncompatible);
    }

    private RuntimeBackupPreflightResult PreflightForMigration(
        string databasePath,
        bool immutableReadOnly,
        DateTimeOffset? publicationTime = null,
        string? archiveWriterVersion = null,
        ISkillRegistryGenerationAuthority? publicationAuthority = null,
        bool workspaceShapeOnly = false,
        bool reportBusy = false,
        RetentionCoverageValidation retentionCoverageValidation = RetentionCoverageValidation.Operational,
        bool allowDeniedLocalAiContent = false,
        DateTimeOffset? localAiValidationTime = null)
    {
        if (!TryFullFile(databasePath, mustExist: true, out var database)) return new(false, RuntimeBackupErrorCodes.InvalidArguments);
        try
        {
            Dictionary<string, int> versions;
            using (var connection = Open(database, SqliteOpenMode.ReadOnly, immutableReadOnly))
            {
                ValidateIntegrity(connection);
                ValidateSchemaMetadataBounds(connection);
                ValidateRetentionPreflightBounds(connection);
                versions = ReadComponentVersions(connection);
                if (!ValidateExecutableObjects(connection, versions)) return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            }
            if (versions.Count is 0 or > RuntimeBackupLimits.MaximumInventoryItems) return new(false, RuntimeBackupErrorCodes.RestoreIncompatible);
            foreach (var item in versions)
            {
                if (item.Value <= 0 || !SupportedComponents.TryGetValue(item.Key, out var supported) || item.Value > supported)
                    return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            }
            if (versions.ContainsKey("sanitized_import") && !versions.ContainsKey("historical_import"))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (!HasValidPricingParents(versions))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (versions.ContainsKey("skill_projection")
                && (!versions.TryGetValue("monitor", out var monitorVersion) || monitorVersion < 11
                    || !versions.TryGetValue("retention", out var retentionVersion)
                    || retentionVersion != SupportedComponents["retention"]))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (versions.ContainsKey("local_repository_catalog")
                && (!versions.TryGetValue("session", out var sessionVersion)
                    || !IsCurrentOrLegacySessionParent(sessionVersion)))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (!HasValidLocalArchiveParents(versions)
                || !(HasValidLocalWorkspaceProjectionParents(versions)
                    || !workspaceShapeOnly && archiveWriterVersion is not null
                       && HasValidLocalWorkspaceProjectionParentsWithoutSnapshot(versions))
                || !HasValidSkillInvocationSnapshotParents(versions))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            if (!versions.ContainsKey("monitor")) return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            var validationAuthority = publicationAuthority ?? skillRegistryAuthority;
            if (publicationTime is null
                && archiveWriterVersion is null
                && publicationAuthority is null
                && !workspaceShapeOnly
                && versions.TryGetValue("local_workspace_projection", out var workspaceVersion)
                && workspaceVersion == SupportedComponents["local_workspace_projection"])
            {
                validationAuthority = ReadCurrentWriterStructuralAuthority(database, immutableReadOnly);
            }
            if (!ValidateComponentShapes(
                    database,
                    versions,
                    immutableReadOnly,
                    publicationTime,
                    validationAuthority,
                    archiveWriterVersion,
                    workspaceShapeOnly,
                    retentionCoverageValidation,
                    allowDeniedLocalAiContent,
                    localAiValidationTime))
                return new(false, RuntimeBackupErrorCodes.RestoreIncompatible, versions);
            var migrations = MigrationOrder.Where(component => SupportedComponents.TryGetValue(component, out var supported)
                    && (!versions.TryGetValue(component, out var current) || current < supported))
                .Select(component => $"{component}:{(versions.TryGetValue(component, out var value) ? value : 0)}->{SupportedComponents[component]}").ToArray();
            return new(true, null, versions, migrations);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        { return new(false, reportBusy ? RuntimeBackupErrorCodes.SnapshotStoreBusy : RuntimeBackupErrorCodes.RestoreIncompatible); }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException or InvalidCastException or FormatException or OverflowException)
        { return new(false, RuntimeBackupErrorCodes.RestoreIncompatible); }
    }

    private static bool HasValidPricingParents(IReadOnlyDictionary<string, int> versions) =>
        !versions.ContainsKey("pricing")
        || versions.TryGetValue("session", out var session) && IsCurrentOrLegacySessionParent(session)
            && versions.TryGetValue("alert_engine", out var alert) && alert == SupportedComponents["alert_engine"]
            && versions.TryGetValue("runtime_backup", out var backup) && backup == SupportedComponents["runtime_backup"];

    private static bool HasValidLocalArchiveParents(IReadOnlyDictionary<string, int> versions) =>
        !versions.ContainsKey("local_archive")
        || versions.TryGetValue("session", out var sessionVersion)
            && sessionVersion == SupportedComponents["session"]
            && versions.TryGetValue("local_repository_catalog", out var catalogVersion)
            && catalogVersion == SupportedComponents["local_repository_catalog"];

    private static bool HasValidLocalWorkspaceProjectionParents(IReadOnlyDictionary<string, int> versions)
    {
        if (!versions.TryGetValue("local_workspace_projection", out var workspaceVersion)) return true;
        if (workspaceVersion < SupportedComponents["local_workspace_projection"])
            return versions.TryGetValue("session", out var legacySessionVersion)
                && IsCurrentOrLegacySessionParent(legacySessionVersion)
                && versions.TryGetValue("retention", out var legacyRetentionVersion)
                && legacyRetentionVersion == SupportedComponents["retention"];
        return versions.TryGetValue("session", out var sessionVersion)
            && sessionVersion == SupportedComponents["session"]
            && versions.TryGetValue("local_repository_catalog", out var catalogVersion)
            && catalogVersion == SupportedComponents["local_repository_catalog"]
            && versions.TryGetValue("local_archive", out var archiveVersion)
            && archiveVersion == SupportedComponents["local_archive"]
            && versions.TryGetValue("retention", out var retentionVersion)
            && retentionVersion == SupportedComponents["retention"]
            && versions.TryGetValue("skill_projection", out var skillProjectionVersion)
            && skillProjectionVersion == SupportedComponents["skill_projection"]
            && versions.TryGetValue("skill_invocation_snapshot", out var skillSnapshotVersion)
            && skillSnapshotVersion == SupportedComponents["skill_invocation_snapshot"];
    }

    private static bool HasValidLocalWorkspaceProjectionParentsWithoutSnapshot(
        IReadOnlyDictionary<string, int> versions)
    {
        if (versions.ContainsKey("skill_invocation_snapshot")) return false;
        var migrated = versions.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        migrated["skill_invocation_snapshot"] = SupportedComponents["skill_invocation_snapshot"];
        return HasValidLocalWorkspaceProjectionParents(migrated);
    }

    // The child component's Session-14 conditional trigger registry only activates for the exact
    // parent version, so a legacy Session parent is never acceptable here.
    private static bool HasValidSkillInvocationSnapshotParents(IReadOnlyDictionary<string, int> versions) =>
        !versions.ContainsKey("skill_invocation_snapshot")
        || versions.TryGetValue("session", out var sessionVersion)
            && sessionVersion == SupportedComponents["session"]
            && versions.TryGetValue("retention", out var retentionVersion)
            && retentionVersion == SupportedComponents["retention"]
            && versions.TryGetValue("skill_projection", out var projectionVersion)
            && projectionVersion == SupportedComponents["skill_projection"];

    private static bool IsCurrentOrLegacySessionParent(int version) =>
        version is 13 || version == SupportedComponents["session"];

    public RuntimeRestorePreview Preview(string bundlePath, string databasePath)
    {
        if (!IsHostNativeAbsoluteLocalPath(bundlePath) || !IsHostNativeAbsoluteLocalPath(databasePath))
            return PreviewFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!TryFullFile(bundlePath, mustExist: true, out var bundle) || !TryFullFile(databasePath, mustExist: false, out var database))
            return PreviewFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!RecoverTransientFiles(Path.GetDirectoryName(bundle)!, Path.GetFileName(database))
            || !RecoverTransientFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database)))
            return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        return PreviewCore(() => InspectToStage(bundle), database);
    }

    public RuntimeRestorePreview Preview(FileStream bundleStream, string databasePath)
    {
        if (bundleStream is null || !bundleStream.CanRead || !bundleStream.CanSeek
            || !TryFullFile(databasePath, mustExist: false, out var database))
            return PreviewFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!RecoverTransientFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database)))
            return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        return PreviewCore(() => InspectToStage(bundleStream, database), database);
    }

    public async Task<RuntimeRestorePreview> PreviewAsync(Stream body, string databasePath, CancellationToken cancellationToken)
    {
        if (body is null || !body.CanRead || !TryFullFile(databasePath, mustExist: false, out var database))
            return PreviewFailure(RuntimeBackupErrorCodes.InvalidArguments);
        var stagingDirectory = Path.Combine(Path.GetDirectoryName(database)!, "runtime-backups");
        if (!RecoverTransientFiles(stagingDirectory, Path.GetFileName(database))
            || !TryPrepareOwnedDirectory(stagingDirectory))
            return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        var stagingPath = Path.Combine(stagingDirectory, $".runtime-backup-preview-{Guid.NewGuid():N}.zip");
        if (!TryFullFile(stagingPath, mustExist: false, out stagingPath))
            return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        FileStream file;
        try
        {
            file = new FileStream(stagingPath, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.DeleteOnClose | FileOptions.SequentialScan,
            });
        }
        catch (Exception exception) when (IsIo(exception))
        {
            return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        }

        await using (file)
        {
            if (RuntimeBackupNativePathClassifier.Read(file.SafeFileHandle) != RuntimeBackupNativePathKind.RegularFile
                || !TryPrepareOwnedDirectory(stagingDirectory))
                return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);

            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
            {
                total += read;
                if (total > RuntimeBackupLimits.MaximumArchiveBytes)
                    return PreviewFailure(RuntimeBackupErrorCodes.BundleTooLarge);
                try
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                catch (Exception exception) when (IsIo(exception))
                {
                    return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
                }
            }
            try
            {
                await file.FlushAsync(cancellationToken);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            }
            file.Position = 0;
            return Preview(file, database);
        }
    }

    private RuntimeRestorePreview PreviewCore(Func<InspectionContext> inspect, string database)
    {
        InspectionContext? context = null;
        FileStream? currentGuard = null;
        SqliteConnection? validationOwner = null;
        CapturedSkillRegistryAuthority? operationAuthority = null;
        var inspectionComplete = false;
        try
        {
            context = inspect();
            inspectionComplete = true;
            var source = PreflightForMigration(
                context.StagePath,
                immutableReadOnly: true,
                archiveWriterVersion: context.Manifest.SourceApplicationVersion,
                retentionCoverageValidation: RetentionCoverageValidation.Restorable);
            if (!source.Success) return PreviewFailure(RuntimeBackupErrorCodes.RestoreIncompatible, context.Result.ArchiveSha256);
            operationAuthority = CaptureSkillRegistryAuthority();
            var targetExists = PathEntryExists(database);
            if (targetExists) currentGuard = TryAcquireRecoveryGuard(database);
            if (currentGuard is not null) validationOwner = OpenValidationSidecarOwner(database);
            var current = targetExists
                ? PreflightForMigration(
                    database,
                    immutableReadOnly: false,
                    retentionCoverageValidation: RetentionCoverageValidation.Restorable)
                : new RuntimeBackupPreflightResult(true, null, new Dictionary<string, int>(), []);
            if (!current.Success) return PreviewFailure(RuntimeBackupErrorCodes.RestoreIncompatible, context.Result.ArchiveSha256);
            if (targetExists && current.ComponentVersions?.ContainsKey("retention") == true)
                ValidateRetentionInvariants(database, immutableReadOnly: false);
            var externalError = ValidateExternalState(database, scanRuntimeRoot: false, immutableDatabase: false);
            if (externalError is not null) return PreviewFailure(externalError, context.Result.ArchiveSha256);
            MigrateStaging(
                context.StagePath,
                source.ComponentVersions ?? new Dictionary<string, int>(),
                restorableSafetyCopy: false,
                context.Manifest.SourceApplicationVersion,
                operationAuthority);
            var migratedExternalError = ValidateDatabaseExternalRawState(context.StagePath, immutableReadOnly: true);
            if (migratedExternalError is not null) return PreviewFailure(migratedExternalError, context.Result.ArchiveSha256);
            _ = ValidateInstalledDatabase(context.StagePath, immutableReadOnly: true, skillRegistryAuthority: operationAuthority);
            var comparison = targetExists
                ? CompareRetention(database, context.StagePath, context.Result.ArchiveSha256!, currentImmutableReadOnly: false)
                : RetentionComparison.Empty;
            if (!comparison.Success) return PreviewFailure(comparison.ErrorCode!, context.Result.ArchiveSha256);
            var result = new RuntimeRestorePreview(true, null, true, targetExists, true, true,
                comparison.TerminalCount, comparison.TerminalDigest, comparison.NonTerminalCount, comparison.ConfirmationDigest,
                comparison.NonTerminalCount > 0, source.ComponentVersions ?? new Dictionary<string, int>(), current.ComponentVersions ?? new Dictionary<string, int>(),
                source.MigrationSteps ?? [], RuntimeBackupWarnings.All, context.Result.ArchiveSha256,
                context.Manifest.DatabaseSha256, context.Manifest.SourceJournalMode, context.Manifest.RowCounts,
                context.Manifest.ProjectionCursors, context.Manifest.Retention, context.Manifest.ExternalState)
            {
                CompatibilityReason = "compatible",
                NonTerminalReintroductionDigest = comparison.ComparisonDigest,
            };
            if (!context.Cleanup()) return PreviewFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable, context.Result.ArchiveSha256);
            context = null;
            return result;
        }
        catch (RuntimeBackupException exception) { return PreviewFailure(exception.Code); }
        catch (InvalidDataException) { return PreviewFailure(RuntimeBackupErrorCodes.ArchiveInvalid, context?.Result.ArchiveSha256); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException) { return PreviewFailure(RuntimeBackupErrorCodes.RestoreIncompatible, context?.Result.ArchiveSha256); }
        catch (SqliteException) { return PreviewFailure(inspectionComplete ? RuntimeBackupErrorCodes.RestoreIncompatible : RuntimeBackupErrorCodes.ArchiveInvalid, context?.Result.ArchiveSha256); }
        catch (Exception exception) when (IsIo(exception)) { return PreviewFailure(inspectionComplete ? RuntimeBackupErrorCodes.SnapshotStoreUnavailable : RuntimeBackupErrorCodes.ArchiveInvalid, context?.Result.ArchiveSha256); }
        finally
        {
            validationOwner?.Dispose();
            if (currentGuard is not null) _ = TryRemoveEmptyReadSidecars(database);
            currentGuard?.Dispose();
            operationAuthority?.Dispose();
            _ = context?.Cleanup();
        }
    }

    public RuntimeRestoreResult Restore(string bundlePath, string databasePath, RuntimeRestoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!IsHostNativeAbsoluteLocalPath(bundlePath)
            || !IsHostNativeAbsoluteLocalPath(databasePath)
            || options.PreRestoreOutputPath is not null && !IsHostNativeAbsoluteLocalPath(options.PreRestoreOutputPath))
            return RestoreFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!TryFullFile(bundlePath, mustExist: true, out var bundle) || !TryFullFile(databasePath, mustExist: false, out var database))
            return RestoreFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (IsReservedDatabaseOutput(bundle, database)) return RestoreFailure(RuntimeBackupErrorCodes.InvalidArguments);
        string? requestedPreRestoreOutput = null;
        if (options.PreRestoreOutputPath is not null
            && !TryFullFile(options.PreRestoreOutputPath, mustExist: false, out requestedPreRestoreOutput))
            return RestoreFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (requestedPreRestoreOutput is not null
            && (IsReservedRestoreOutput(requestedPreRestoreOutput, bundle, database)
                || IsReservedTransientArtifactName(requestedPreRestoreOutput)))
            return RestoreFailure(RuntimeBackupErrorCodes.InvalidArguments);
        if (!RecoverTransientFiles(Path.GetDirectoryName(bundle)!, Path.GetFileName(database))
            || !RecoverTransientFiles(Path.GetDirectoryName(database)!, Path.GetFileName(database)))
            return RestoreFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        InspectionContext? context = null;
        var rollbackPath = database + ".runtime-restore-rollback";
        var journalPath = database + ".runtime-restore-journal.json";
        var leasePath = database + ".runtime-restore.lock";
        var swapped = false;
        var targetExisted = false;
        FileStream? ownership = null;
        FileStream? recoveryGuard = null;
        FileStream? restoreLease = null;
        FileStream? bundleStream = null;
        SqliteConnection? validationOwner = null;
        CapturedSkillRegistryAuthority? operationAuthority = null;
        RuntimeBackupCreateResult? preRestore = null;
        RetentionComparison comparison = RetentionComparison.Empty;
        RestoreJournal? restoreJournal = null;
        var doctorCheck = "not_checked";
        var committed = false;
        var preserveRecoveryArtifacts = false;
        void InvokeCheckpoint(string value)
        {
            preserveRecoveryArtifacts = true;
            checkpoint?.Invoke(value);
            preserveRecoveryArtifacts = false;
        }
        try
        {
            restoreLease = TryAcquireRestoreLease(leasePath);
            if (restoreLease is null) return RestoreFailure(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            if (PathEntryExists(database))
            {
                recoveryGuard = TryAcquireRecoveryGuard(database);
                if (recoveryGuard is null) return RestoreFailure(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            }
            else if (HasLiveMonitorState(database) || HasActiveSqliteSidecar(database))
            {
                return RestoreFailure(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            }
            if (!RecoverInterruptedRestore(database)) return RestoreFailure(RuntimeBackupErrorCodes.RestoreRollbackFailed);
            recoveryGuard?.Dispose();
            recoveryGuard = null;
            operationAuthority = CaptureSkillRegistryAuthority();
            targetExisted = PathEntryExists(database);
            if (targetExisted)
            {
                ownership = TryAcquireOfflineOwnership(database);
                if (ownership is null) return RestoreFailure(RuntimeBackupErrorCodes.MonitorMustBeStopped);
                validationOwner = OpenValidationSidecarOwner(database);
                var current = PreflightForMigration(
                    database,
                    immutableReadOnly: false,
                    retentionCoverageValidation: RetentionCoverageValidation.Restorable);
                if (!current.Success) return RestoreFailure(RuntimeBackupErrorCodes.RestoreIncompatible);
                if (current.ComponentVersions?.ContainsKey("retention") == true)
                    ValidateRetentionInvariants(database, immutableReadOnly: false);
            }
            else if (HasLiveMonitorState(database) || HasActiveSqliteSidecar(database))
            {
                return RestoreFailure(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            }
            var externalError = ValidateExternalState(database, scanRuntimeRoot: false, immutableDatabase: false);
            if (externalError is not null) return RestoreFailure(externalError);

            bundleStream = new FileStream(bundle, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (bundleStream.Length <= 0)
                return RestoreFailure(RuntimeBackupErrorCodes.ArchiveInvalid);
            if (bundleStream.Length > RuntimeBackupLimits.MaximumArchiveBytes)
                return RestoreFailure(RuntimeBackupErrorCodes.BundleTooLarge);
            ValidateStoredArchive(bundleStream, expectedEntries: 2);
            bundleStream.Position = 0;
            var archiveHash = HashStream(bundleStream);
            bundleStream.Position = 0;
            var operationId = Guid.CreateVersion7(timeProvider.GetUtcNow()).ToString("D");
            var stageFileName = $".runtime-restore-stage-{Guid.Parse(operationId):N}.sqlite";
            var stagePath = Path.Combine(Path.GetDirectoryName(database)!, stageFileName);
            if (requestedPreRestoreOutput is not null && PathEquals(requestedPreRestoreOutput, stagePath))
                return RestoreFailure(RuntimeBackupErrorCodes.InvalidArguments, archiveHash);
            var targetBeforeHash = targetExisted ? HashFile(database) : null;
            restoreJournal = new("runtime-restore-journal.v2", operationId, "staging", archiveHash, stageFileName,
                targetExisted, targetBeforeHash, null, targetBeforeHash, null);
            WriteJournal(journalPath, restoreJournal);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterOwnerJournal);

            context = InspectToStage(bundleStream, bundle, stagePath);
            if (!FixedEquals(archiveHash, context.Result.ArchiveSha256))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterArchiveExtracted);
            var stagedExternalError = ValidateDatabaseExternalRawState(context.StagePath, immutableReadOnly: true);
            if (stagedExternalError is not null) throw new RuntimeBackupException(stagedExternalError);
            var preflight = PreflightForMigration(
                context.StagePath,
                immutableReadOnly: true,
                archiveWriterVersion: context.Manifest.SourceApplicationVersion,
                retentionCoverageValidation: RetentionCoverageValidation.Restorable);
            if (!preflight.Success) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
            MigrateStaging(
                context.StagePath,
                preflight.ComponentVersions ?? new Dictionary<string, int>(),
                restorableSafetyCopy: false,
                context.Manifest.SourceApplicationVersion,
                operationAuthority);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterMigration);
            stagedExternalError = ValidateDatabaseExternalRawState(context.StagePath, immutableReadOnly: true);
            if (stagedExternalError is not null) throw new RuntimeBackupException(stagedExternalError);
            if (targetExisted)
            {
                comparison = CompareRetention(database, context.StagePath, context.Result.ArchiveSha256!, currentImmutableReadOnly: false);
                if (!comparison.Success) throw new RuntimeBackupException(comparison.ErrorCode!);
                if (comparison.NonTerminalCount > 0 && (!options.AllowResurrection || !FixedEquals(options.ConfirmationDigest, comparison.ConfirmationDigest)))
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreResurrectionBlocked);
            }

            if (targetExisted && !ReconcileTerminal(database, context.StagePath, comparison))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed);
            if (targetExisted && !ReconcileNonTerminal(database, context.StagePath, comparison))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed);
            if (targetExisted && !RefreshWorkspaceAfterRetentionReconciliation(database, context.StagePath, operationAuthority))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed);

            if (targetExisted)
            {
                var prePath = requestedPreRestoreOutput;
                if (string.IsNullOrWhiteSpace(prePath))
                {
                    var directory = Path.Combine(Path.GetDirectoryName(database)!, "runtime-backups");
                    var candidate = Path.Combine(directory, $"pre-restore-{context.Result.ArchiveSha256![..16]}-{timeProvider.GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid():N}.zip");
                    if (!TryFullFile(candidate, mustExist: false, out prePath))
                        throw new RuntimeBackupException(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe);
                }
                preRestore = CreateArchive(
                    database,
                    prePath,
                    scanRuntimeRoot: false,
                    migrateSnapshotToCurrent: true,
                    capturedAuthority: operationAuthority);
                if (!preRestore.Success) throw new RuntimeBackupException(preRestore.ErrorCode!);
            }

            if (!TryAppendReceipt(context.StagePath, "restore", context.Result.ArchiveSha256!, "restore_succeeded",
                    comparison.NonTerminalCount, preRestore?.Success == true, operationId,
                    () => InvokeCheckpoint(RuntimeBackupCheckpoints.DuringStageReceipt)))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterReceiptPersisted);
            if (HasActiveSqliteSidecar(context.StagePath))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            var stagedFacts = ValidateInstalledDatabase(context.StagePath, immutableReadOnly: true, skillRegistryAuthority: operationAuthority);
            if (!HasMatchingRestoreReceipt(context.StagePath, operationId, context.Result.ArchiveSha256!, immutableReadOnly: true))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterStageValidated);
            FlushFile(context.StagePath);
            var stagedHash = HashFile(context.StagePath);
            restoreJournal = restoreJournal with { Phase = "prepared", StagedSha256 = stagedHash };
            ReplaceJournal(journalPath, restoreJournal);
            var preSwapExternalError = ValidateExternalState(database, scanRuntimeRoot: false, immutableDatabase: false);
            if (preSwapExternalError is not null)
                throw new RuntimeBackupException(preSwapExternalError);
            validationOwner?.Dispose();
            validationOwner = null;
            if (targetExisted && !TryRemoveEmptyReadSidecars(database))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            if (targetExisted && !HashEquals(database, targetBeforeHash!))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            InvokeCheckpoint(RuntimeBackupCheckpoints.BeforeSwap);
            if (HasActiveSqliteSidecar(database))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            if (targetExisted)
            {
                File.Replace(context.StagePath, database, rollbackPath, ignoreMetadataErrors: true);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(database)!);
                File.Move(context.StagePath, database, overwrite: false);
            }
            swapped = true;
            ownership?.Dispose();
            ownership = TryAcquireRecoveryGuard(database);
            if (ownership is null) throw new RuntimeBackupException(RuntimeBackupErrorCodes.MonitorMustBeStopped);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterSwap);
            if (HasActiveSqliteSidecar(database) || !HashEquals(database, stagedHash))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            InvokeCheckpoint(CatalogBeforeInstalledValidationCheckpoint);
            validationOwner = OpenValidationSidecarOwner(database);
            ValidateInstalledDatabase(database, immutableReadOnly: false, expected: stagedFacts, skillRegistryAuthority: operationAuthority);
            if (!HasMatchingRestoreReceipt(database, operationId, context.Result.ArchiveSha256!, immutableReadOnly: false))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            doctorCheck = installedDoctorCheck(database);
            restoreJournal = restoreJournal with { Phase = "installed" };
            WriteJournalCandidate(journalPath, restoreJournal);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterInstalledJournalCandidateFlushed);
            CommitJournalCandidate(journalPath);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterInstalledValidation);
            restoreJournal = restoreJournal with { Phase = "committed", InstalledSha256 = stagedHash };
            WriteJournalCandidate(journalPath, restoreJournal);
            committed = true;
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterCommittedJournalCandidateFlushed);
            CommitJournalCandidate(journalPath);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterJournalCommitted);
            if (!HashEquals(database, stagedHash) || !HasMatchingRestoreReceipt(database, operationId, context.Result.ArchiveSha256!, immutableReadOnly: false))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            validationOwner?.Dispose();
            validationOwner = null;
            if (!TryRemoveEmptyReadSidecars(database))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            if (targetExisted && (!HashEquals(rollbackPath, targetBeforeHash!) || !DeleteOwnedFile(rollbackPath)))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            if (!targetExisted && PathEntryExists(rollbackPath)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterRollbackDeleted);
            InvokeCheckpoint(RuntimeBackupCheckpoints.BeforeJournalDeleted);
            if (!DeleteOwnedFile(journalPath)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
            InvokeCheckpoint(RuntimeBackupCheckpoints.AfterJournalDeleted);
            return new(true, null, RuntimeBackupContractVersions.RestoreResult, context.Result.ArchiveSha256,
                preRestore?.Success == true, preRestore?.ArchiveSha256, preRestore?.PublishedFileName,
                comparison.TerminalCount, comparison.NonTerminalCount, "database_ready", doctorCheck, RuntimeBackupWarnings.All);
        }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException or InvalidDataException or JsonException or InvalidCastException or FormatException or OverflowException || IsIo(exception))
        {
            validationOwner?.Dispose();
            validationOwner = null;
            preserveRecoveryArtifacts = false;
            if (committed)
            {
                ownership?.Dispose();
                ownership = null;
                return RestoreFailure(RuntimeBackupErrorCodes.RestoreFailed, context?.Result.ArchiveSha256, comparison, preRestore);
            }
            FileStream? restoredTargetGuard = null;
            var readStateClean = TryRemoveEmptyReadSidecars(database);
            var rollbackSucceeded = swapped
                ? readStateClean
                    && ownership is not null
                    && !HasActiveSqliteSidecar(database)
                    && Rollback(database, rollbackPath, targetExisted, restoreJournal?.TargetBeforeSha256, restoreJournal?.StagedSha256, out restoredTargetGuard)
                : readStateClean && (restoreJournal is null || restoreFailureCleanup(database));
            ownership?.Dispose();
            ownership = restoredTargetGuard;
            if (rollbackSucceeded)
            {
                try
                {
                    rollbackSucceeded = DeleteOwnedFile(journalPath + ".commit") && DeleteOwnedFile(journalPath);
                }
                catch (Exception cleanupException) when (IsIo(cleanupException))
                {
                    rollbackSucceeded = false;
                }
            }
            if (!rollbackSucceeded) preserveRecoveryArtifacts = true;
            ownership?.Dispose();
            ownership = null;
            var preSwapCode = exception switch
            {
                RuntimeBackupException runtime => runtime.Code,
                RetentionMigrationBlockedException => RuntimeBackupErrorCodes.RestoreIncompatible,
                InvalidDataException or JsonException => RuntimeBackupErrorCodes.ArchiveInvalid,
                InvalidOperationException or FormatException or OverflowException or InvalidCastException => RuntimeBackupErrorCodes.RestoreIncompatible,
                _ => RuntimeBackupErrorCodes.RestoreFailed,
            };
            var failureCode = !rollbackSucceeded
                ? RuntimeBackupErrorCodes.RestoreRollbackFailed
                : swapped
                    ? RuntimeBackupErrorCodes.RestoreRolledBack
                    : preSwapCode;
            return RestoreFailure(failureCode,
                context?.Result.ArchiveSha256, comparison, preRestore);
        }
        finally
        {
            validationOwner?.Dispose();
            if (ownership is not null) _ = TryRemoveEmptyReadSidecars(database);
            ownership?.Dispose();
            recoveryGuard?.Dispose();
            bundleStream?.Dispose();
            operationAuthority?.Dispose();
            if (!preserveRecoveryArtifacts && restoreJournal is not null && !swapped && PathEntryExists(journalPath))
                _ = RecoverInterruptedRestore(database);
            if (restoreLease is not null)
            {
                restoreLease.Dispose();
            }
            if (context is not null && !preserveRecoveryArtifacts && !PathEntryExists(journalPath)) DeleteStageFiles(context.StagePath);
        }
    }

    private RuntimeBackupCreateResult CreateArchive(
        string databasePath,
        string outputPath,
        bool scanRuntimeRoot,
        bool migrateSnapshotToCurrent = false,
        DateTimeOffset? publicationTime = null,
        ISkillRegistryGenerationAuthority? capturedAuthority = null)
    {
        string? snapshot = null;
        string? partial = null;
        OwnedRuntimeBackupTransient? snapshotOwner = null;
        OwnedRuntimeBackupTransient? partialOwner = null;
        try
        {
            if (!RecoverTransientFiles(Path.GetDirectoryName(databasePath)!, Path.GetFileName(databasePath))
                || !RecoverTransientFiles(Path.GetDirectoryName(outputPath)!, Path.GetFileName(databasePath)))
                return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            if (PathEntryExists(outputPath)) return CreateFailure(RuntimeBackupErrorCodes.OutputExists);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory)) return CreateFailure(RuntimeBackupErrorCodes.InvalidArguments);
            if (!TryPrepareOwnedDirectory(outputDirectory)) return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            var startedAt = timeProvider.GetUtcNow();
            var cursorsAtStart = ReadProjectionCursors(databasePath, immutableReadOnly: false);
            var startExternalError = ValidateExternalState(databasePath, scanRuntimeRoot, immutableDatabase: false);
            if (startExternalError is not null) return CreateFailure(startExternalError);
            var externalState = ReadExternalState(databasePath);
            var externalFingerprint = ReadExternalStateFingerprint(databasePath);
            var sourceJournalMode = ReadJournalMode(databasePath);
            snapshot = Path.Combine(Path.GetDirectoryName(databasePath)!, $".{Path.GetFileName(databasePath)}.{Guid.NewGuid():N}.online-snapshot");
            snapshotOwner = CreateTransientOwner(snapshot, sqlite: true);
            checkpoint?.Invoke(RuntimeBackupCheckpoints.BeforeOnlineSnapshot);
            OnlineSnapshot(databasePath, snapshot);
            RemoveLocalComparisonsFromStaging(snapshot);
            var effectivePublicationTime = publicationTime ?? timeProvider.GetUtcNow();
            PrepareLocalAiStaging(snapshot, effectivePublicationTime);
            checkpoint?.Invoke(RuntimeBackupCheckpoints.AfterOnlineSnapshot);
            var snapshotPreflight = PreflightForMigration(
                snapshot,
                immutableReadOnly: true,
                publicationTime,
                publicationAuthority: migrateSnapshotToCurrent ? null : capturedAuthority,
                retentionCoverageValidation: migrateSnapshotToCurrent
                    ? RetentionCoverageValidation.Restorable
                    : RetentionCoverageValidation.Operational,
                localAiValidationTime: effectivePublicationTime);
            if (!snapshotPreflight.Success) return CreateFailure(RuntimeBackupErrorCodes.RestoreIncompatible);
            if (migrateSnapshotToCurrent)
            {
                MigrateStaging(
                    snapshot,
                    snapshotPreflight.ComponentVersions ?? new Dictionary<string, int>(),
                    restorableSafetyCopy: true,
                    archiveWriterVersion,
                    capturedAuthority,
                    effectivePublicationTime);
                _ = ValidateInstalledDatabase(
                    snapshot,
                    immutableReadOnly: true,
                    skillRegistryAuthority: capturedAuthority ?? skillRegistryAuthority,
                    localAiValidationTime: effectivePublicationTime);
            }
            var capturedExternalError = ValidateDatabaseExternalRawState(snapshot, immutableReadOnly: true);
            if (capturedExternalError is not null) return CreateFailure(capturedExternalError);
            var cursorsAtEnd = ReadProjectionCursors(databasePath, immutableReadOnly: false);
            var endExternalError = ValidateExternalState(databasePath, scanRuntimeRoot, immutableDatabase: false, [snapshot, snapshotOwner.MarkerPath]);
            if (endExternalError is not null) return CreateFailure(endExternalError);
            if (!ReadExternalState(databasePath).SequenceEqual(externalState)
                || !FixedEquals(ReadExternalStateFingerprint(databasePath), externalFingerprint))
                return CreateFailure(RuntimeBackupErrorCodes.ExternalRuntimeStateActive);
            var completedAt = timeProvider.GetUtcNow();
            var length = new FileInfo(snapshot).Length;
            if (length > RuntimeBackupLimits.MaximumDatabaseBytes) return CreateFailure(RuntimeBackupErrorCodes.DatabaseTooLarge);
            var databaseHash = HashFile(snapshot);
            var window = new RuntimeBackupBackupWindow(startedAt, completedAt, cursorsAtStart, cursorsAtEnd);
            var manifest = RuntimeBackupJson.WriteManifest(BuildManifest(snapshot, databaseHash, length, sourceJournalMode, window, externalState, completedAt));
            partial = Path.Combine(outputDirectory, $".runtime-backup-{Guid.NewGuid():N}.partial");
            partialOwner = CreateTransientOwner(partial, sqlite: false);
            using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteEntry(archive, "manifest.json", new MemoryStream(manifest, writable: false));
                using var database = new FileStream(snapshot, FileMode.Open, FileAccess.Read, FileShare.Read);
                WriteEntry(archive, "database.sqlite", database);
            }
            FlushFile(partial);
            if (new FileInfo(partial).Length > RuntimeBackupLimits.MaximumArchiveBytes) return CreateFailure(RuntimeBackupErrorCodes.BundleTooLarge);
            var inspectionContext = InspectToStage(partial, localAiValidationTime: effectivePublicationTime);
            var inspection = inspectionContext.Result;
            if (!inspectionContext.Cleanup()) return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            if (!inspection.Success) return CreateFailure(inspection.ErrorCode!);
            if (!snapshotOwner.Cleanup()) return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            snapshotOwner = null;
            snapshot = null;
            File.Move(partial, outputPath, overwrite: false);
            partial = outputPath;
            FlushFile(outputPath);
            if (!partialOwner.Cleanup()) return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            partialOwner = null;
            partial = null;
            return new(true, null, inspection.ArchiveSha256, RuntimeBackupContractVersions.BundleSchema, RuntimeBackupContractVersions.BundleProfile, Path.GetFileName(outputPath), RuntimeBackupWarnings.All);
        }
        catch (RuntimeBackupException exception) { return CreateFailure(exception.Code); }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreBusy); }
        catch (SqliteException) { return CreateFailure(RuntimeBackupErrorCodes.SnapshotStoreUnavailable); }
        catch (Exception exception) when (IsIo(exception)) { return CreateFailure(RuntimeBackupErrorCodes.PublishFailed); }
        catch (Exception exception) when (exception is InvalidOperationException or InvalidCastException or FormatException or OverflowException) { return CreateFailure(RuntimeBackupErrorCodes.RestoreIncompatible); }
        finally
        {
            _ = snapshotOwner?.Cleanup();
            _ = partialOwner?.Cleanup();
            DeleteKnownFile(snapshot);
            DeleteKnownFile(partial);
        }
    }

    private InspectionContext InspectToStage(
        string bundlePath,
        string? requestedStagePath = null,
        DateTimeOffset? localAiValidationTime = null)
    {
        var attributes = File.GetAttributes(bundlePath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        using var file = new FileStream(bundlePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return InspectToStage(file, bundlePath, requestedStagePath, localAiValidationTime);
    }

    private InspectionContext InspectToStage(
        FileStream file,
        string stageBasePath,
        string? requestedStagePath = null,
        DateTimeOffset? localAiValidationTime = null)
    {
        if (!file.CanRead || !file.CanSeek) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        var archiveLength = file.Length;
        if (archiveLength <= 0) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        if (archiveLength > RuntimeBackupLimits.MaximumArchiveBytes) throw new RuntimeBackupException(RuntimeBackupErrorCodes.BundleTooLarge);
        ValidateStoredArchive(file, expectedEntries: 2);
        var expectedCrcs = ReadStoredEntryCrcs(file, expectedEntries: 2);
        var stage = requestedStagePath ?? Path.Combine(Path.GetDirectoryName(stageBasePath)!, $".runtime-backup-inspect-{Guid.NewGuid():N}.sqlite");
        if (PathEntryExists(stage)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreRollbackFailed);
        OwnedRuntimeBackupTransient? stageOwner = requestedStagePath is null ? CreateTransientOwner(stage, sqlite: true) : null;
        try
        {
            byte[] manifestBytes;
            file.Position = 0;
            using (var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true))
            {
                if (archive.Entries.Count != 2) throw new RuntimeBackupException(RuntimeBackupErrorCodes.UnexpectedEntry);
                if (!archive.Entries.Select(entry => entry.FullName).SequenceEqual(new[] { "manifest.json", "database.sqlite" }, StringComparer.Ordinal)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.UnexpectedEntry);
                if (archive.Entries.GroupBy(entry => entry.FullName, StringComparer.Ordinal).Any(group => group.Count() != 1)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.DuplicateEntry);
                if (archive.Entries.Any(entry => entry.CompressedLength != entry.Length)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.CompressionNotAllowed);
                if (archive.Entries.Any(entry => entry.ExternalAttributes != 0)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveAttributesInvalid);
                if (archive.Entries.Any(entry => entry.LastWriteTime.DateTime != ArchiveTimestamp.DateTime)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveTimestampInvalid);
                manifestBytes = ReadBounded(archive.Entries[0], RuntimeBackupLimits.MaximumManifestBytes, RuntimeBackupErrorCodes.ManifestInvalid);
                if (ComputeCrc32(manifestBytes) != expectedCrcs[0]) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
                if (archive.Entries[1].Length > RuntimeBackupLimits.MaximumDatabaseBytes) throw new RuntimeBackupException(RuntimeBackupErrorCodes.DatabaseTooLarge);
                try
                {
                    using var source = archive.Entries[1].Open();
                    using var destination = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    CopyBounded(source, destination, RuntimeBackupLimits.MaximumDatabaseBytes, RuntimeBackupErrorCodes.DatabaseTooLarge, out var databaseCrc);
                    if (databaseCrc != expectedCrcs[1]) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
                    checkpoint?.Invoke(RuntimeBackupCheckpoints.BeforeInspectionStageFlush);
                    destination.Flush(true);
                }
                catch (RuntimeBackupException) { throw; }
                catch (Exception exception) when (IsIo(exception))
                {
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
                }
            }
            try
            {
                if (!HasSqliteHeader(stage)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            }
            catch (RuntimeBackupException) { throw; }
            catch (Exception exception) when (IsIo(exception))
            {
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            }
            var manifest = RuntimeBackupJson.ParseManifest(manifestBytes);
            string databaseHash;
            long databaseLength;
            try
            {
                databaseHash = HashFile(stage);
                databaseLength = new FileInfo(stage).Length;
            }
            catch (Exception exception) when (IsIo(exception))
            {
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            }
            if (manifest.DatabaseSize != databaseLength || !FixedEquals(databaseHash, manifest.DatabaseSha256)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ChecksumMismatch);
            var stagedExternalError = ValidateDatabaseExternalRawState(stage, immutableReadOnly: true);
            if (stagedExternalError is not null) throw new RuntimeBackupException(stagedExternalError);
            ValidateDatabaseMatchesManifest(stage, manifest, localAiValidationTime);
            file.Position = 0;
            var archiveHash = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            return new(stage, new(true, null, archiveHash, databaseHash, RuntimeBackupContractVersions.Manifest,
                RuntimeBackupContractVersions.BundleSchema, RuntimeBackupContractVersions.BundleProfile, manifest.ComponentVersions, manifest.RowCounts,
                RuntimeBackupWarnings.All, manifest.SourceJournalMode, manifest.ProjectionCursors, manifest.Retention, manifest.ExternalState), manifest, stageOwner);
        }
        catch
        {
            var cleaned = stageOwner is not null ? stageOwner.Cleanup() : TryDeleteStageFiles(stage);
            if (!cleaned) throw new RuntimeBackupException(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
            throw;
        }
    }

    private RuntimeBackupManifestData BuildManifest(
        string snapshotPath,
        string databaseHash,
        long length,
        string sourceJournalMode,
        RuntimeBackupBackupWindow backupWindow,
        IReadOnlyList<RuntimeBackupExternalState> external,
        DateTimeOffset createdAt)
    {
        var versions = ReadComponentVersions(snapshotPath, readOnly: true);
        var rows = ReadRowCounts(snapshotPath);
        var cursors = ReadProjectionCursors(snapshotPath, immutableReadOnly: true);
        var retention = ReadRetentionSummary(snapshotPath, immutableReadOnly: true);
        return new(createdAt, archiveWriterVersion,
            RuntimeInformation.RuntimeIdentifier, databaseHash, length, sourceJournalMode, backupWindow, versions, rows, cursors, retention, external);
    }

    private static string ReadJournalMode(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> header = stackalloc byte[20];
        stream.ReadExactly(header);
        if (!header[..16].SequenceEqual("SQLite format 3\0"u8))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        return (header[18], header[19]) switch
        {
            (2, 2) => "wal",
            (1, 1) => "delete",
            _ => throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible),
        };
    }

    internal static void OnlineSnapshot(
        string sourcePath,
        string destinationPath,
        bool immutableSource = false)
    {
        using var source = Open(sourcePath, SqliteOpenMode.ReadOnly, immutableSource);
        using var destination = Open(destinationPath, SqliteOpenMode.ReadWriteCreate);
        source.BackupDatabase(destination);
        using var journal = destination.CreateCommand(); journal.CommandText = "PRAGMA journal_mode=DELETE;"; journal.ExecuteNonQuery();
        ValidateIntegrity(destination);
    }

    private static void RemoveLocalComparisonsFromStaging(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        SetPragma(connection, "foreign_keys", false);
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var table in LocalComparisonSchemaV1.TableNames.Reverse())
        {
            using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DROP TABLE IF EXISTS \"{table}\";";
            drop.ExecuteNonQuery();
        }
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "DELETE FROM schema_version WHERE component='local_comparison';";
            _ = version.ExecuteNonQuery();
        }
        transaction.Commit();
        ValidateIntegrity(connection);
    }

    private static void PrepareLocalAiStaging(string path,DateTimeOffset at)
    {
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        if (!TableExists(connection, "local_ai_runs")) return;
        connection.CreateFunction<string,string,long>("local_ai_retention_delete_authorized",static (_,_)=>1L,isDeterministic:false);
        using var transaction = connection.BeginTransaction(deferred: false);
        foreach (var sql in new[]
        {
            "UPDATE local_ai_results SET result_json=NULL WHERE result_json IS NOT NULL AND EXISTS(SELECT 1 FROM retention_items i WHERE i.store_kind='analysis_run_raw' AND i.source_item_id='local_ai:result:'||local_ai_results.result_id AND (i.read_denied_at IS NOT NULL OR i.state NOT IN('retained_by_policy','expiring') OR (i.state='expiring' AND i.expires_at<=$now)));",
            "UPDATE local_ai_snapshots SET payload_json=NULL,evidence_index_json=NULL WHERE scope_kind='session' AND payload_json IS NOT NULL AND EXISTS(SELECT 1 FROM retention_items i WHERE i.store_kind='analysis_run_raw' AND i.source_item_id='local_ai:snapshot:'||local_ai_snapshots.snapshot_id AND (i.read_denied_at IS NOT NULL OR i.state NOT IN('retained_by_policy','expiring') OR (i.state='expiring' AND i.expires_at<=$now)));",
            "DELETE FROM local_ai_results WHERE run_id IN (SELECT run_id FROM local_ai_runs WHERE scope_kind<>'session');",
            "DELETE FROM local_ai_runs WHERE scope_kind<>'session';",
            "DELETE FROM local_ai_snapshots WHERE scope_kind<>'session';",
        })
        {
            using var command=connection.CreateCommand(); command.Transaction=transaction; command.CommandText=sql;command.Parameters.AddWithValue("$now",at.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture)); command.ExecuteNonQuery();
        }
        transaction.Commit();
        using (var checkpoint = connection.CreateCommand())
        {
            checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            checkpoint.ExecuteNonQuery();
        }
        ValidateIntegrity(connection);
    }

    private void ValidateDatabaseMatchesManifest(
        string path,
        RuntimeBackupManifestData manifest,
        DateTimeOffset? localAiValidationTime = null)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly: true);
        ValidateIntegrity(connection);
        ValidateSchemaMetadataBounds(connection);
        if (!ValidateExecutableObjects(connection, manifest.ComponentVersions)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        if (!ReadComponentVersions(connection).OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(manifest.ComponentVersions.OrderBy(item => item.Key, StringComparer.Ordinal)))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
        if (!ReadRowCounts(connection).OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(manifest.RowCounts.OrderBy(item => item.Key, StringComparer.Ordinal)))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
        if (!ValidateComponentShapes(
                path,
                manifest.ComponentVersions,
                immutableReadOnly: true,
                archiveWriterVersion: manifest.SourceApplicationVersion,
                retentionCoverageValidation: RetentionCoverageValidation.Restorable,
                localAiValidationTime: localAiValidationTime))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        ValidateRetentionPreflightBounds(connection);
        if (manifest.ComponentVersions.ContainsKey("retention"))
        {
            ValidateRetentionCoverage(path, immutableReadOnly: true);
            ValidateRetentionInvariants(path, immutableReadOnly: true);
        }
        if (!ReadProjectionCursors(path, immutableReadOnly: true).OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(manifest.ProjectionCursors.OrderBy(item => item.Key, StringComparer.Ordinal)))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
        var retention = ReadRetentionSummary(path, immutableReadOnly: true);
        if (!retention.StoreKindCounts.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(manifest.Retention.StoreKindCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
            || !retention.StateCounts.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(manifest.Retention.StateCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
            || retention.TombstoneCount != manifest.Retention.TombstoneCount
            || retention.EarliestCapturedAt != manifest.Retention.EarliestCapturedAt
            || retention.LatestCapturedAt != manifest.Retention.LatestCapturedAt
            || retention.EarliestExpiresAt != manifest.Retention.EarliestExpiresAt
            || retention.LatestExpiresAt != manifest.Retention.LatestExpiresAt
            || !retention.Policies.SequenceEqual(manifest.Retention.Policies, StringComparer.Ordinal))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
    }

    private static InstalledDatabaseFacts ValidateInstalledDatabase(
        string path,
        bool immutableReadOnly,
        InstalledDatabaseFacts? expected = null,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority = null,
        DateTimeOffset? localAiValidationTime = null)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly);
        ValidateIntegrity(connection);
        ValidateSchemaMetadataBounds(connection);
        var versions = ReadComponentVersions(connection);
        if (versions.Count != InstalledArchiveComponents.Count + (versions.ContainsKey("local_ai_analysis") ? 1 : 0)
            || InstalledArchiveComponents.Any(item => !versions.TryGetValue(item.Key, out var current) || current != item.Value))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        if (!ValidateExecutableObjects(connection, versions)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        if (!ValidateComponentShapes(
                path,
                versions,
                immutableReadOnly,
                publicationTime: ReadWorkspacePublicationTime(connection, versions),
                skillRegistryAuthority: skillRegistryAuthority ?? FixedSkillRegistryGenerationAuthority.Load(),
                retentionCoverageValidation: RetentionCoverageValidation.Restorable,
                localAiValidationTime: localAiValidationTime))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        ValidateRetentionPreflightBounds(connection);
        ValidateRetentionCoverage(path, immutableReadOnly);
        var cursors = ReadProjectionCursors(path, immutableReadOnly);
        var retention = ReadRetentionSummary(path, immutableReadOnly);
        ValidateRetentionInvariants(path, immutableReadOnly);
        var facts = new InstalledDatabaseFacts(cursors, retention);
        if (expected is not null
            && (!cursors.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(expected.ProjectionCursors.OrderBy(item => item.Key, StringComparer.Ordinal))
                || !RetentionSummaryEquals(retention, expected.Retention)))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        return facts;
    }

    private static DateTimeOffset? ReadWorkspacePublicationTime(
        SqliteConnection connection,
        IReadOnlyDictionary<string, int> versions)
    {
        if (!versions.TryGetValue("local_workspace_projection", out var version) || version != LocalWorkspaceProjectionSchemaV1.Version)
            return null;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT refreshed_at FROM local_workspace_projection_state WHERE projector_key='local-workspace-projection-v1';";
        var text = command.ExecuteScalar() as string;
        return text is not null
            && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value
            : throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
    }

    private static void ValidateRetentionInvariants(string path, bool immutableReadOnly)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly);
        if (!TableExists(connection, "retention_items") || !TableExists(connection, "retention_tombstones"))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        ValidateRetentionPreflightBounds(connection);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT captured_at,expires_at,read_denied_at,deleted_at FROM retention_items;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!TryTimestamp(reader.GetString(0), out var captured)
                    || !TryTimestamp(reader.GetString(1), out var expires)
                    || captured > expires
                    || !reader.IsDBNull(2) && !TryTimestamp(reader.GetString(2), out _)
                    || !reader.IsDBNull(3) && !TryTimestamp(reader.GetString(3), out _))
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
            }
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COUNT(*)
                FROM retention_items i
                LEFT JOIN retention_tombstones t ON t.item_id=i.item_id
                WHERE (i.state='deleted' AND (i.read_denied_at IS NULL OR i.deleted_at IS NULL OR t.item_id IS NULL OR t.deleted_at<>i.deleted_at))
                   OR (i.state<>'deleted' AND (i.deleted_at IS NOT NULL OR t.item_id IS NOT NULL));
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT receipt_at,deleted_at FROM retention_tombstones;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!TryTimestamp(reader.GetString(0), out _) || !TryTimestamp(reader.GetString(1), out _))
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        }
    }

    private static bool RetentionSummaryEquals(RuntimeBackupRetentionSummary left, RuntimeBackupRetentionSummary right) =>
        left.StoreKindCounts.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(right.StoreKindCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        && left.StateCounts.OrderBy(item => item.Key, StringComparer.Ordinal).SequenceEqual(right.StateCounts.OrderBy(item => item.Key, StringComparer.Ordinal))
        && left.TombstoneCount == right.TombstoneCount
        && left.EarliestCapturedAt == right.EarliestCapturedAt
        && left.LatestCapturedAt == right.LatestCapturedAt
        && left.EarliestExpiresAt == right.EarliestExpiresAt
        && left.LatestExpiresAt == right.LatestExpiresAt
        && left.Policies.SequenceEqual(right.Policies, StringComparer.Ordinal);

    private static bool TryTimestamp(string value, out DateTimeOffset timestamp) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out timestamp);

    private static void ValidateRetentionPreflightBounds(SqliteConnection connection)
    {
        var storeInstances = FindTableNameOrdinalIgnoreCase(connection, "retention_store_instances");
        if (storeInstances is not null
            && HasRows(connection, storeInstances, InvalidPreflightText("store_instance_id", nullable: false)))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        var items = FindTableNameOrdinalIgnoreCase(connection, "retention_items");
        if (items is not null)
        {
            var invalidItemText = new[]
            {
                InvalidPreflightText("item_id", nullable: false),
                InvalidPreflightText("store_instance_id", nullable: false),
                InvalidPreflightText("store_kind", nullable: false),
                InvalidPreflightText("source_item_id", nullable: false),
                InvalidPreflightText("state", nullable: false),
                InvalidPreflightText("policy_id", nullable: false),
                InvalidPreflightText("captured_at", nullable: false),
                InvalidPreflightText("expires_at", nullable: false),
                InvalidPreflightText("read_denied_at", nullable: true),
                InvalidPreflightText("deleted_at", nullable: true),
                "typeof(ownership_receipt)<>'blob' OR length(ownership_receipt)<>32",
                $"typeof(policy_version)<>'integer' OR length(CAST(policy_id || '@' || CAST(policy_version AS TEXT) AS BLOB))>{RuntimeBackupLimits.MaximumRetentionPreflightTextBytes}",
            };
            if (HasRows(connection, items, string.Join(" OR ", invalidItemText)))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        }
        var tombstones = FindTableNameOrdinalIgnoreCase(connection, "retention_tombstones");
        if (tombstones is not null
            && HasRows(connection, tombstones, $"{InvalidPreflightText("receipt_at", nullable: false)} OR {InvalidPreflightText("deleted_at", nullable: false)}"))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        ValidateCoverageSourceBounds(connection, "raw_records",
            [("received_at", false)], ["retention_owner_token"]);
        ValidateCoverageSourceBounds(connection, "monitor_analysis_runs",
            [("requested_at", false), ("span_id", true)], ["retention_owner_token"]);
        ValidateCoverageSourceBounds(connection, "session_event_content",
            [("event_id", false), ("content_kind", false), ("captured_at", false), ("expires_at", false)], ["retention_owner_token"]);
        ValidateCoverageSourceBounds(connection, "session_events",
            [("event_id", false), ("session_id", false), ("run_id", true), ("source_adapter", false), ("source_event_id", false)], []);
    }

    private static void ValidateCoverageSourceBounds(
        SqliteConnection connection,
        string table,
        IReadOnlyList<(string Column, bool Nullable)> textColumns,
        IReadOnlyList<string> tokenColumns)
    {
        var actualTable = FindTableNameOrdinalIgnoreCase(connection, table);
        if (actualTable is null) return;
        var columns = TableColumns(connection, actualTable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = textColumns.Where(item => columns.Contains(item.Column))
            .Select(item => InvalidPreflightText(item.Column, item.Nullable))
            .Concat(tokenColumns.Where(columns.Contains).Select(column => $"typeof({QuoteIdentifier(column)})<>'blob' OR length({QuoteIdentifier(column)})<>32"))
            .ToArray();
        if (invalid.Length > 0 && HasRows(connection, actualTable, string.Join(" OR ", invalid)))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
    }

    private static string InvalidPreflightText(string column, bool nullable)
    {
        var quoted = QuoteIdentifier(column);
        var wrongType = nullable
            ? $"typeof({quoted}) NOT IN ('text','null')"
            : $"typeof({quoted})<>'text'";
        return $"({wrongType} OR (typeof({quoted})='text' AND length(CAST({quoted} AS BLOB))>{RuntimeBackupLimits.MaximumRetentionPreflightTextBytes}))";
    }

    private static bool ValidateComponentShapes(
        string path,
        IReadOnlyDictionary<string, int> versions,
        bool immutableReadOnly,
        DateTimeOffset? publicationTime = null,
        ISkillRegistryGenerationAuthority? skillRegistryAuthority = null,
        string? archiveWriterVersion = null,
        bool workspaceShapeOnly = false,
        RetentionCoverageValidation retentionCoverageValidation = RetentionCoverageValidation.Operational,
        bool allowDeniedLocalAiContent = false,
        DateTimeOffset? localAiValidationTime = null)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly);
        if (!HasColumns(connection, "schema_version", "component", "version")) return false;
        if (!ValidateOwnedComponentNamespaces(connection, versions)) return false;
        if (versions.ContainsKey("retention")
            || versions.ContainsKey("local_repository_catalog")
            || versions.ContainsKey("local_archive")
            || versions.ContainsKey("skill_invocation_snapshot")
            || versions.ContainsKey("local_workspace_projection")
            || versions.ContainsKey("local_comparison")
            || versions.ContainsKey("local_ai_analysis")
            || workspaceShapeOnly)
        {
            if (versions.ContainsKey("local_repository_catalog")
                && (!versions.TryGetValue("session", out var localRepositorySessionVersion)
                    || !IsCurrentOrLegacySessionParent(localRepositorySessionVersion)))
                return false;
            using var canonicalReplica = versions.TryGetValue("local_workspace_projection", out var workspaceVersion)
                && workspaceVersion == SupportedComponents["local_workspace_projection"]
                && !workspaceShapeOnly
                ? new SqliteConnection("Data Source=:memory:")
                : null;
            if (canonicalReplica is not null)
            {
                canonicalReplica.Open();
                connection.BackupDatabase(canonicalReplica);
            }
            using var componentTransaction = connection.BeginTransaction(deferred: true);
            try
            {
                if (workspaceShapeOnly)
                    LocalWorkspaceProjectionBackupValidation.ValidateRawSemanticReconstructionPreflight(
                        connection,
                        componentTransaction);
                if (versions.ContainsKey("retention"))
                {
                    if (retentionCoverageValidation == RetentionCoverageValidation.Restorable)
                        RetentionCatalogStore.ValidateRestorableCurrentV1Authority(connection, componentTransaction);
                    else
                        RetentionCatalogStore.ValidateCurrentV1Authority(connection, componentTransaction);
                }
                if (versions.ContainsKey("local_repository_catalog"))
                {
                    if (versions["session"] == SupportedComponents["session"])
                        LocalRepositoryCatalogBackupValidation.Validate(connection, componentTransaction);
                    else
                        LocalRepositoryCatalogBackupValidation.ValidateLegacySession13(connection, componentTransaction);
                }
                if (versions.ContainsKey("local_archive"))
                    LocalArchiveBackupValidation.Validate(
                        connection,
                        componentTransaction,
                        LocalArchiveSessionTargetExistenceAuthority.Instance,
                        SqliteLocalRepositoryTargetExistenceAuthority.Instance);
                if (versions.ContainsKey("skill_invocation_snapshot"))
                    SkillInvocationSnapshotBackupValidation.Validate(connection, componentTransaction);
                if (versions.ContainsKey("local_ai_analysis") && (!LocalAiAnalysisSchemaV1.IsValid(connection, componentTransaction) || !ValidateLocalAiRows(connection,componentTransaction,localAiValidationTime??publicationTime??DateTimeOffset.UtcNow,allowDeniedContent:workspaceShapeOnly||allowDeniedLocalAiContent))) return false;
                if (versions.ContainsKey("local_comparison"))
                    LocalComparisonSchemaV1.Validate(connection, componentTransaction, allowLegacyRepositoryCatalog: true);
                if (versions.ContainsKey("local_workspace_projection"))
                {
                    if (workspaceShapeOnly && versions["local_workspace_projection"] == 4)
                        LocalWorkspaceProjectionSchemaV1.ValidateSemanticRows(connection, componentTransaction);
                    if (workspaceShapeOnly
                        || versions["local_workspace_projection"] is 1 or 2 or 3 or 4)
                        LocalWorkspaceProjectionSchemaV1.ValidateCurrentOrExactLegacy(connection, componentTransaction);
                    else
                    {
                        var validationAuthority = skillRegistryAuthority;
                        if (publicationTime is null && archiveWriterVersion is not null)
                        {
                            var archivedGenerationIdentity = ReadArchivedSkillRegistryGenerationIdentity(
                                connection,
                                componentTransaction);
                            validationAuthority = archivedGenerationIdentity is null
                                ? FixedSkillRegistryGenerationAuthority.ForWriterVersion(archiveWriterVersion)
                                : FixedSkillRegistryGenerationAuthority.ForWriterVersion(
                                    archiveWriterVersion,
                                    archivedGenerationIdentity);
                        }
                        LocalWorkspaceProjectionBackupValidation.Validate(
                            connection,
                            componentTransaction,
                            publicationTime,
                            validationAuthority,
                            canonicalReplica,
                            validateCanonicalProjection: archiveWriterVersion is null
                                || !LocalRepositoryCatalogSchemaV1.HasExactLegacyBackupSchema(
                                    connection,
                                    componentTransaction));
                    }
                }
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
            {
                throw;
            }
            catch (Exception exception) when (exception is SqliteException or InvalidOperationException or InvalidCastException or FormatException or OverflowException)
            {
                return false;
            }
            finally
            {
                componentTransaction.Rollback();
            }
        }
        if (versions.TryGetValue("monitor", out var monitor)
            && (!HasColumns(connection, "raw_records", "id", "source", "trace_id", "received_at", "payload_json", "schema_version")
                || monitor >= 7 && !HasColumns(connection, "raw_records", "retention_owner_token")
                || !HasColumns(connection, "monitor_ingestions", "id", "raw_record_id", "received_at", "source", "projected_at")
                || !HasColumns(connection, "monitor_traces", "id", "trace_id", "projected_at")
                || !HasColumns(connection, "monitor_spans", "id", "raw_record_id", "trace_id", "span_ordinal", "projected_at")
                || monitor is >= 9 and < 11 && !HasColumns(
                    connection,
                    "monitor_skill_invocations",
                    "id",
                    "raw_record_id",
                    "trace_id",
                    "span_id",
                    "span_ordinal",
                    "session_id",
                    "skill_name",
                    "skill_source",
                    "invocation_trigger",
                    "source_application_version",
                    "projected_at")
                || monitor is >= 9 and < 11 && !HasColumns(
                    connection,
                    "monitor_skill_inventories",
                    "id",
                    "raw_record_id",
                    "trace_id",
                    "session_id",
                    "observed_name_count",
                    "retained_name_count",
                    "names_truncated",
                    "source_application_version",
                    "projected_at")
                || monitor is >= 9 and < 11 && !HasColumns(
                    connection,
                    "monitor_skill_inventory_names",
                    "raw_record_id",
                    "trace_id",
                    "name_ordinal",
                    "skill_name")
                || monitor >= 10 && !HasColumns(
                    connection,
                    "source_trace_attribution_observations",
                    "raw_record_id",
                    "trace_id",
                    "cli_candidate_observed",
                    "vscode_candidate_observed",
                    "unknown_candidate_observed",
                    "relevant_evidence_observed")
                || monitor >= 10 && !HasColumns(
                    connection,
                    "source_trace_attribution_reconciliation_queue",
                    "trace_id"))) return false;
        if (versions.ContainsKey("monitor"))
        {
            try
            {
                MonitorSchemaMigrator.ValidateBeforeInitialization(connection);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        if (versions.ContainsKey("skill_projection"))
        {
            try
            {
                SkillProjectionSchemaV1.Validate(connection, transaction: null);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
        if (versions.ContainsKey("retention")
            && (!HasColumns(connection, "retention_component_versions", "component", "version")
                || !HasColumns(connection, "retention_store_instances", "id", "store_instance_id")
                || !HasColumns(connection, "retention_items", "item_id", "store_instance_id", "store_kind", "source_item_id", "receipt_version", "ownership_receipt", "state", "read_denied_at", "deleted_at")
                || !HasColumns(connection, "retention_tombstones", "item_id", "receipt_at", "deleted_at")
                || !HasColumns(connection, "retention_capture_journal", "item_id", "phase", "durable_cursor")
                || !HasColumns(connection, "retention_file_capture_reservations", "capture_id", "store_instance_id", "store_kind", "source_item_id", "reserved_at", "reserved_at_utc_ticks", "policy_id", "policy_version", "parent_locator", "staging_locator", "final_locator", "owner_token", "marker_sha256", "manifest_sha256", "phase", "durable_cursor", "planned_member_count", "planned_total_bytes", "error_code", "updated_at", "legacy_v1")
                || !HasColumns(connection, "retention_file_capture_members", "capture_id", "ordinal", "relative_path", "member_kind", "byte_length", "sha256", "deletion_order")
                || !HasColumns(connection, "retention_analysis_sdk_directory_reservations", "capture_id", "analysis_run_id", "store_instance_id", "requested_at", "requested_at_utc_ticks", "parent_locator", "child_locator", "analysis_owner_token_sha256", "owner_token", "marker_sha256", "phase", "error_code", "revision", "updated_at")
                || !HasColumns(connection, "retention_analysis_sdk_directory_members", "capture_id", "ordinal", "relative_path", "member_kind", "byte_length", "sha256", "deletion_order")
                || !HasColumns(connection, "retention_legacy_bundle_blockers", "root_locator", "classification", "recorded_at")
                || !HasColumns(connection, "retention_legacy_bundle_journal", "capture_id", "root_locator", "legacy_manifest_sha256", "legacy_staging_locator", "replacement_temp_locator", "subphase"))) return false;
        if (versions.ContainsKey("session"))
        {
            SqliteSessionStore.ValidateSchemaBeforeInitialization(connection);
            if (!HasColumns(connection, "session_native_ids", "session_id", "source_surface", "native_session_id")
                || !HasColumns(connection, "session_runs", "run_id", "session_id", "source_surface")
                || !HasColumns(connection, "session_events", "event_id", "session_id", "source_surface", "content_state")) return false;
        }
        if (versions.ContainsKey("doctor") && !DoctorSchemaV1.IsValid(connection, null)) return false;
        if (versions.TryGetValue("alert_engine", out var alertVersion)
            && (alertVersion == AlertSchemaV1.Version
                ? !AlertSchemaV1.IsValid(connection, null)
                : alertVersion != AlertSchemaV2.Version || !AlertSchemaV2.IsValid(connection, null))) return false;
        if (versions.ContainsKey("alert_lifecycle") && !AlertLifecycleSchemaV1.IsValid(connection, null)) return false;
        if (versions.ContainsKey("first_trace_navigation")
            && !HasColumns(connection, "first_trace_evidence_navigation", "verification_id", "evidence_ref", "target_kind", "target_id")) return false;
        using var transaction = connection.BeginTransaction(deferred: true);
        try
        {
            if (versions.ContainsKey("historical_instruction_analysis")
                && !HistoricalInstructionAnalysisSchemaV1.IsValid(connection, transaction)) return false;
            if (versions.ContainsKey("historical_import")
                && !SqliteHistoricalImportStore.IsSchemaValid(connection, transaction)) return false;
            if (versions.ContainsKey("sanitized_import"))
                SanitizedImportSchemaV1.ValidateStructure(connection, transaction);
            if (versions.ContainsKey("runtime_backup") && !RuntimeBackupSchemaV1.IsValid(connection, transaction)) return false;
            if (versions.ContainsKey("pricing")
                && (!PricingSchemaV1.IsValid(connection, transaction)
                    || !PricingSchemaV1.ValidateRows(connection, transaction))) return false;
            return true;
        }
        finally
        {
            transaction.Rollback();
        }
    }

    private static void ValidateSchemaMetadataBounds(SqliteConnection connection)
    {
        var tables = new List<string>();
        var objectCount = 0;
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"""
                SELECT CASE WHEN typeof(name)='text' AND length(CAST(name AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaIdentifierBytes}
                                  AND typeof(tbl_name)='text' AND length(CAST(tbl_name AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaIdentifierBytes}
                                  AND (sql IS NULL OR (typeof(sql)='text' AND length(CAST(sql AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaDefinitionBytes}))
                             THEN 1 ELSE 0 END,
                       type,name,sql
                FROM sqlite_schema
                WHERE type IN ('table','index','trigger')
                ORDER BY type,name;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (++objectCount > RuntimeBackupLimits.MaximumSchemaObjects || reader.GetInt64(0) != 1)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
                if (reader.GetString(1) != "table") continue;
                var table = reader.GetString(2);
                if (table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase))
                {
                    var sql = reader.IsDBNull(3) ? null : reader.GetString(3);
                    if (!IsAllowedSqliteTable(table, sql))
                        throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
                    continue;
                }
                tables.Add(table);
            }
        }
        if (tables.Count > RuntimeBackupLimits.MaximumInventoryItems)
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        foreach (var table in tables) _ = TableColumns(connection, table);
    }

    private static bool ValidateExecutableObjects(SqliteConnection connection, IReadOnlyDictionary<string, int> versions)
    {
        if (versions.ContainsKey("local_comparison"))
        {
            using var replica = new SqliteConnection("Data Source=:memory:");
            replica.Open(); connection.BackupDatabase(replica); SetPragma(replica, "foreign_keys", false);
            foreach (var table in LocalComparisonSchemaV1.TableNames.Reverse()) { using var drop = replica.CreateCommand(); drop.CommandText = $"DROP TABLE \"{table}\";"; drop.ExecuteNonQuery(); }
            var without = versions.Where(item => item.Key != "local_comparison").ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            return ValidateExecutableObjects(replica, without);
        }
        using (var forbidden = connection.CreateCommand())
        {
            forbidden.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type='view' OR (type='table' AND rootpage=0);";
            if (Convert.ToInt64(forbidden.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return false;
        }
        if (!ValidateIndexesAreNonExecutable(connection, versions)) return false;

        var allowed = new Dictionary<string, (string Table, string Definition)>(StringComparer.Ordinal);
        if (versions.TryGetValue("monitor", out var monitorVersion) && monitorVersion >= 7)
        {
            allowed["retention_raw_records_token_immutable"] = ("raw_records",
                "CREATE TRIGGER retention_raw_records_token_immutable BEFORE UPDATE OF retention_owner_token ON raw_records WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;");
            if (TableExists(connection, "monitor_analysis_runs"))
                allowed["retention_monitor_analysis_runs_token_immutable"] = ("monitor_analysis_runs",
                    "CREATE TRIGGER retention_monitor_analysis_runs_token_immutable BEFORE UPDATE OF retention_owner_token ON monitor_analysis_runs WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;");
        }
        if (versions.TryGetValue("session", out var sessionVersion) && sessionVersion >= 13)
            allowed["retention_session_event_content_token_immutable"] = ("session_event_content",
                "CREATE TRIGGER retention_session_event_content_token_immutable BEFORE UPDATE OF retention_owner_token ON session_event_content WHEN NEW.retention_owner_token IS NOT OLD.retention_owner_token BEGIN SELECT RAISE(ABORT,'retention_owner_token_immutable'); END;");
        if (versions.ContainsKey("alert_lifecycle"))
        {
            allowed["alert_lifecycle_events_no_update"] = ("alert_lifecycle_events", AlertLifecycleSchemaV1.UpdateTriggerSql);
            allowed["alert_lifecycle_events_no_delete"] = ("alert_lifecycle_events", AlertLifecycleSchemaV1.DeleteTriggerSql);
        }
        if (versions.ContainsKey("runtime_backup"))
        {
            allowed["runtime_backup_receipts_no_update"] = ("runtime_backup_receipts", RuntimeBackupSchemaV1.ReceiptNoUpdateTriggerSql);
            allowed["runtime_backup_receipts_no_delete"] = ("runtime_backup_receipts", RuntimeBackupSchemaV1.ReceiptNoDeleteTriggerSql);
            allowed["runtime_backup_receipts_no_replace"] = ("runtime_backup_receipts", RuntimeBackupSchemaV1.ReceiptNoReplaceTriggerSql);
        }
        if (versions.ContainsKey("pricing"))
        {
            foreach (var trigger in PricingSchemaV1.OwnedObjects.Where(item => item.Type == "trigger"))
                allowed[trigger.Name] = (trigger.TableName, trigger.Sql);
        }
        if (versions.TryGetValue("monitor", out var sourceMonitorVersion)
            && sourceMonitorVersion >= 11)
        {
            foreach (var trigger in SourceCompatibilitySchemaV11.TriggerDefinitions)
                allowed[trigger.Name] = (trigger.Table, trigger.Sql);
        }
        if (versions.ContainsKey("skill_projection"))
        {
            foreach (var trigger in SkillProjectionSchemaV1.TriggerDefinitions)
                allowed[trigger.Name] = (trigger.Table, trigger.Sql);
        }
        if (versions.ContainsKey("local_repository_catalog"))
        {
            foreach (var trigger in LocalRepositoryCatalogSchemaV1.TriggerDefinitions)
                allowed[trigger.Name] = (trigger.Table, trigger.Sql);
        }
        if (versions.ContainsKey("local_archive"))
        {
            foreach (var trigger in LocalArchiveSchemaV1.TriggerDefinitions)
                allowed[trigger.Name] = (trigger.Table, trigger.Sql);
        }
        if (versions.ContainsKey("skill_invocation_snapshot"))
        {
            foreach (var trigger in SkillInvocationSnapshotSchemaV1.TriggerDefinitions)
                allowed[trigger.Name] = (trigger.Table, trigger.Sql);
        }
        if (versions.ContainsKey("local_ai_analysis"))
        {
            allowed["local_ai_snapshots_update_rejected"] = ("local_ai_snapshots", "CREATE TRIGGER local_ai_snapshots_update_rejected BEFORE UPDATE ON local_ai_snapshots WHEN NOT (local_ai_retention_delete_authorized('snapshot',OLD.snapshot_id)=1 AND OLD.scope_kind='session' AND OLD.payload_json IS NOT NULL AND OLD.evidence_index_json IS NOT NULL AND NEW.payload_json IS NULL AND NEW.evidence_index_json IS NULL AND NEW.snapshot_id=OLD.snapshot_id AND NEW.scope_kind=OLD.scope_kind AND NEW.session_id=OLD.session_id AND NEW.node_id IS OLD.node_id AND NEW.repository_id IS OLD.repository_id AND NEW.comparison_id IS OLD.comparison_id AND NEW.anchor_id=OLD.anchor_id AND NEW.payload_sha256=OLD.payload_sha256 AND NEW.evidence_index_sha256=OLD.evidence_index_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at AND NEW.expires_at IS OLD.expires_at) BEGIN SELECT RAISE(ABORT,'local_ai_snapshot_immutable'); END");
            allowed["local_ai_results_update_rejected"] = ("local_ai_results", "CREATE TRIGGER local_ai_results_update_rejected BEFORE UPDATE ON local_ai_results WHEN NOT (local_ai_retention_delete_authorized('result',OLD.result_id)=1 AND OLD.result_json IS NOT NULL AND NEW.result_json IS NULL AND NEW.result_id=OLD.result_id AND NEW.run_id=OLD.run_id AND NEW.result_sha256=OLD.result_sha256 AND NEW.retention_owner_token=OLD.retention_owner_token AND NEW.created_at=OLD.created_at AND NEW.expires_at IS OLD.expires_at) BEGIN SELECT RAISE(ABORT,'local_ai_result_immutable'); END");
            allowed["local_ai_terminal_run_update_rejected"] = ("local_ai_runs", "CREATE TRIGGER local_ai_terminal_run_update_rejected BEFORE UPDATE ON local_ai_runs WHEN OLD.state NOT IN ('queued','running') BEGIN SELECT RAISE(ABORT,'local_ai_terminal_run_immutable'); END");
        }
        if (versions.ContainsKey("local_comparison"))
        {
            foreach (var trigger in LocalComparisonSchemaV1.OwnedObjects.Where(item => item.Type == "trigger"))
                allowed[trigger.Name] = (trigger.Table, trigger.Sql);
        }

        var actual = new HashSet<string>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT CASE WHEN typeof(name)='text' AND length(CAST(name AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaIdentifierBytes}
                              AND typeof(tbl_name)='text' AND length(CAST(tbl_name AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaIdentifierBytes}
                              AND typeof(sql)='text' AND length(CAST(sql AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaDefinitionBytes}
                         THEN 1 ELSE 0 END,
                   name,tbl_name,sql
            FROM sqlite_schema WHERE type='trigger' ORDER BY name;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.GetInt64(0) != 1) return false;
            var name = reader.GetString(1);
            if (!actual.Add(name) || !allowed.TryGetValue(name, out var expected)
                || reader.GetString(2) != expected.Table
                || NormalizeTriggerSql(reader.GetString(3)) != NormalizeTriggerSql(expected.Definition)) return false;
        }
        return allowed.Keys.All(actual.Contains);
    }

    private static bool ValidateIndexesAreNonExecutable(
        SqliteConnection connection,
        IReadOnlyDictionary<string, int> versions)
    {
        if (versions.ContainsKey("local_comparison"))
        {
            using var replica = new SqliteConnection("Data Source=:memory:");
            replica.Open();
            connection.BackupDatabase(replica);
            SetPragma(replica, "foreign_keys", false);
            foreach (var table in LocalComparisonSchemaV1.TableNames.Reverse())
            {
                using var drop = replica.CreateCommand(); drop.CommandText = $"DROP TABLE \"{table}\";"; drop.ExecuteNonQuery();
            }
            var without = versions.Where(item => item.Key != "local_comparison").ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            return ValidateIndexesAreNonExecutable(replica, without);
        }
        var indexes = new List<(string Name, string Table, string? Definition)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT name,tbl_name,sql FROM sqlite_schema WHERE type='index' ORDER BY name LIMIT {RuntimeBackupLimits.MaximumSchemaObjects + 1};";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (indexes.Count >= RuntimeBackupLimits.MaximumSchemaObjects
                    || reader.GetFieldType(0) != typeof(string)
                    || reader.GetFieldType(1) != typeof(string)) return false;
                var name = reader.GetString(0);
                var table = reader.GetString(1);
                var internalIndex = name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase);
                if (internalIndex && (!name.StartsWith($"sqlite_autoindex_{table}_", StringComparison.Ordinal) || !reader.IsDBNull(2))) return false;
                indexes.Add((name, table, reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var index in indexes)
        {
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT partial,origin FROM pragma_index_list($table) WHERE name=$name COLLATE BINARY;";
                list.Parameters.AddWithValue("$table", index.Table);
                list.Parameters.AddWithValue("$name", index.Name);
                using var reader = list.ExecuteReader();
                if (!reader.Read() || reader.GetFieldType(0) != typeof(long)
                    || reader.GetFieldType(1) != typeof(string)
                    || reader.GetInt64(0) is not (0 or 1)
                    || reader.GetInt64(0) == 1 && !IsAllowedPartialIndex(index.Name, index.Table, index.Definition, versions)
                    || index.Name.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) && reader.GetString(1) is not ("u" or "pk")
                    || reader.Read()) return false;
            }

            using var details = connection.CreateCommand();
            details.CommandText = "SELECT cid FROM pragma_index_xinfo($name) ORDER BY seqno;";
            details.Parameters.AddWithValue("$name", index.Name);
            using var detailReader = details.ExecuteReader();
            var count = 0;
            while (detailReader.Read())
            {
                if (++count > RuntimeBackupLimits.MaximumInventoryItems
                    || detailReader.GetFieldType(0) != typeof(long)
                    || detailReader.GetInt64(0) == -2) return false;
            }
            if (count == 0) return false;
        }
        return true;
    }

    private static string? ReadArchivedSkillRegistryGenerationIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        if (!TableExists(connection, "local_workspace_skill_metadata", transaction)
            || !TableColumns(connection, "local_workspace_skill_metadata", transaction)
                .Contains("registry_generation_identity", StringComparer.OrdinalIgnoreCase))
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT registry_generation_identity
              FROM local_workspace_skill_metadata
             GROUP BY registry_generation_identity
             ORDER BY registry_generation_identity COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetFieldType(0) != typeof(string)) throw new InvalidOperationException();
        var identity = reader.GetString(0);
        if (identity.Length is < 1 or > 512 || reader.Read()) throw new InvalidOperationException();
        return identity;
    }

    private ISkillRegistryGenerationAuthority ReadCurrentWriterStructuralAuthority(
        string path,
        bool immutableReadOnly)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly);
        using var transaction = connection.BeginTransaction(deferred: true);
        var persistedGenerationIdentity = ReadArchivedSkillRegistryGenerationIdentity(connection, transaction);
        transaction.Rollback();
        return persistedGenerationIdentity is null
            ? FixedSkillRegistryGenerationAuthority.ForWriterVersion(archiveWriterVersion)
            : FixedSkillRegistryGenerationAuthority.ForPersistedGenerationIdentity(
                archiveWriterVersion,
                persistedGenerationIdentity);
    }

    private static bool IsAllowedPartialIndex(
        string name,
        string table,
        string? definition,
        IReadOnlyDictionary<string, int> versions)
    {
        if (!versions.ContainsKey("retention") || definition is null) return false;
        var expected = (name, table) switch
        {
            ("IX_retention_legacy_bundle_journal_root_locator", "retention_legacy_bundle_journal") =>
                "CREATE UNIQUE INDEX IX_retention_legacy_bundle_journal_root_locator ON retention_legacy_bundle_journal(root_locator) WHERE root_locator IS NOT NULL",
            ("IX_retention_confirmation_bindings_one_active_preview", "retention_confirmation_bindings") =>
                "CREATE UNIQUE INDEX IX_retention_confirmation_bindings_one_active_preview ON retention_confirmation_bindings(preview_id) WHERE consumed_at IS NULL AND invalidated_at IS NULL",
            _ => null,
        };
        return expected is not null
            && string.Equals(NormalizeIndexSql(definition), expected, StringComparison.Ordinal);
    }

    private static string NormalizeIndexSql(string sql) =>
        NormalizeSchemaSql(sql)
            .Replace("CREATE UNIQUE INDEX IF NOT EXISTS ", "CREATE UNIQUE INDEX ", StringComparison.Ordinal)
            .Replace("CREATE INDEX IF NOT EXISTS ", "CREATE INDEX ", StringComparison.Ordinal);

    private static bool IsAllowedSqliteTable(string name, string? sql) => (name, NormalizeSchemaSql(sql)) switch
    {
        ("sqlite_sequence", "CREATE TABLE sqlite_sequence(name,seq)") => true,
        ("sqlite_stat1", "CREATE TABLE sqlite_stat1(tbl,idx,stat)") => true,
        ("sqlite_stat4", "CREATE TABLE sqlite_stat4(tbl,idx,neq,nlt,ndlt,sample)") => true,
        _ => false,
    };

    private static string NormalizeSchemaSql(string? sql) => sql is null
        ? string.Empty
        : string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static string NormalizeTriggerSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd(';')
            .Replace("CREATE TRIGGER IF NOT EXISTS ", "CREATE TRIGGER ", StringComparison.Ordinal);

    private static bool HasColumns(SqliteConnection connection, string table, params string[] required)
    {
        if (!TableExists(connection, table)) return false;
        var columns = TableColumns(connection, table).ToHashSet(StringComparer.Ordinal);
        return required.All(columns.Contains);
    }

    internal static bool ValidateOwnedComponentNamespaces(SqliteConnection connection, IReadOnlyDictionary<string, int> versions)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["doctor_verifications"] = "doctor",
            ["doctor_verification_evidence"] = "doctor",
            ["alert_evaluations"] = "alert_engine",
            ["alert_receipts"] = "alert_engine",
            ["alert_suppressions"] = "alert_engine",
            ["alert_lifecycle_events"] = "alert_lifecycle",
            ["alert_lifecycle_events_no_update"] = "alert_lifecycle",
            ["alert_lifecycle_events_no_delete"] = "alert_lifecycle",
            ["historical_instruction_analysis_runs"] = "historical_instruction_analysis",
            ["historical_import_previews"] = "historical_import",
            ["historical_import_confirmation_bindings"] = "historical_import",
            ["historical_import_operations"] = "historical_import",
            ["historical_import_observations"] = "historical_import",
            ["historical_import_observation_fields"] = "historical_import",
            ["historical_import_observation_provenance"] = "historical_import",
            ["historical_import_conflicts"] = "historical_import",
            ["sanitized_import_history"] = "sanitized_import",
            ["sanitized_import_records"] = "sanitized_import",
            ["sanitized_import_origins"] = "sanitized_import",
            ["sanitized_import_graph_nodes"] = "sanitized_import",
            ["sanitized_import_graph_declarations"] = "sanitized_import",
            ["sanitized_import_graph_edges"] = "sanitized_import",
            ["sanitized_import_history_order_idx"] = "sanitized_import",
            ["sanitized_import_origins_record_idx"] = "sanitized_import",
            ["sanitized_import_graph_edges_source_idx"] = "sanitized_import",
            ["sanitized_import_graph_edges_target_idx"] = "sanitized_import",
            ["runtime_backup_receipts"] = "runtime_backup",
            ["runtime_backup_receipts_no_update"] = "runtime_backup",
            ["runtime_backup_receipts_no_delete"] = "runtime_backup",
            ["runtime_backup_receipts_no_replace"] = "runtime_backup",
            ["first_trace_evidence_navigation"] = "first_trace_navigation",
            ["local_ai_snapshots"] = "local_ai_analysis",
            ["local_ai_runs"] = "local_ai_analysis",
            ["local_ai_results"] = "local_ai_analysis",
            ["IX_local_ai_session_reports"] = "local_ai_analysis",
            ["local_ai_snapshots_update_rejected"] = "local_ai_analysis",
            ["local_ai_results_update_rejected"] = "local_ai_analysis",
            ["local_ai_terminal_run_update_rejected"] = "local_ai_analysis",
        };
        foreach (var table in SemanticAttributeCaptureSchema.Tables)
            owners[table.Name] = "monitor";
        foreach (var table in SkillProjectionSchemaV1.TableNames)
            owners[table] = "skill_projection";
        foreach (var trigger in SkillProjectionSchemaV1.TriggerDefinitions)
            owners[trigger.Name] = "skill_projection";
        foreach (var table in LocalRepositoryCatalogSchemaV1.TableNames)
            owners[table] = "local_repository_catalog";
        foreach (var index in LocalRepositoryCatalogSchemaV1.IndexNames)
            owners[index] = "local_repository_catalog";
        foreach (var trigger in LocalRepositoryCatalogSchemaV1.TriggerDefinitions)
            owners[trigger.Name] = "local_repository_catalog";
        foreach (var item in LocalArchiveSchemaV1.OwnedObjects)
            owners[item.Name] = "local_archive";
        foreach (var item in SkillInvocationSnapshotSchemaV1.OwnedObjects)
            owners[item.Name] = "skill_invocation_snapshot";
        foreach (var item in LocalWorkspaceProjectionSchemaV1.OwnedObjects)
            owners[item.Name] = "local_workspace_projection";
        foreach (var item in LocalComparisonSchemaV1.OwnedObjects)
            owners[item.Name] = "local_comparison";
        if (versions.TryGetValue("monitor", out var monitorVersion)
            && monitorVersion is >= 9 and < 11
            && !versions.ContainsKey("skill_projection"))
        {
            foreach (var table in SkillProjectionSchemaV1.ObsoleteTableNames)
                owners[table] = "monitor";
            foreach (var index in SkillProjectionSchemaV1.ObsoleteIndexNames)
                owners[index] = "monitor";
        }
        foreach (var item in PricingSchemaV1.OwnedObjects)
            owners[item.Name] = "pricing";
        var prefixes = new[]
        {
            "doctor_",
            "alert_",
            "historical_instruction_analysis_",
            "historical_import_",
            "sanitized_import_",
            "runtime_backup_",
            "pricing_",
            "first_trace_",
            "skill_projection_",
            "monitor_skill_",
            "IX_monitor_skill_",
            "local_repository_",
            "session_repository_",
            "IX_local_repository_",
            "IX_session_repository_",
            "local_archive_",
            "IX_local_archive_",
            "local_comparison_",
            "IX_local_comparison_",
            "local_workspace_",
            "local_ai_",
            "IX_local_ai_",
        };
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name,tbl_name FROM sqlite_schema WHERE type IN ('table','index','trigger','view') ORDER BY name;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            var table = reader.GetString(1);
            if (!name.Equals("local_repositories", StringComparison.OrdinalIgnoreCase)
                && !prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || table.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
            if ((!owners.TryGetValue(name, out var owner)
                    && !owners.TryGetValue(table, out owner))
                || !versions.ContainsKey(owner)) return false;
        }
        return true;
    }

    internal static bool ValidateLocalAiRows(SqliteConnection connection,SqliteTransaction transaction,DateTimeOffset? validationTime=null,bool allowDeniedContent=false)
    {
        var at=(validationTime??DateTimeOffset.UtcNow).ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
        using(var invalid=connection.CreateCommand())
        {
            invalid.Transaction=transaction; invalid.CommandText=$"""
                SELECT EXISTS(
                  SELECT 1 FROM local_ai_snapshots s WHERE typeof(snapshot_id)<>'text' OR typeof(scope_kind)<>'text' OR typeof(anchor_id)<>'text'
                    OR typeof(session_id) NOT IN('null','text') OR typeof(node_id) NOT IN('null','text') OR typeof(repository_id) NOT IN('null','text') OR typeof(comparison_id) NOT IN('null','text') OR typeof(expires_at) NOT IN('null','text') OR typeof(payload_json) NOT IN('null','blob') OR typeof(evidence_index_json) NOT IN('null','blob')
                    OR typeof(created_at)<>'text' OR typeof(payload_sha256)<>'text' OR typeof(evidence_index_sha256)<>'text'
                    OR typeof(retention_owner_token)<>'blob' OR length(retention_owner_token)<>32
                    OR NOT ((scope_kind='session' AND session_id IS NOT NULL AND node_id IS NULL AND repository_id IS NULL AND comparison_id IS NULL AND expires_at IS NULL) OR (scope_kind='node' AND session_id IS NOT NULL AND node_id IS NOT NULL AND repository_id IS NULL AND comparison_id IS NULL AND expires_at IS NOT NULL) OR (scope_kind='repository_selection' AND session_id IS NULL AND node_id IS NULL AND repository_id IS NOT NULL AND comparison_id IS NULL AND expires_at IS NOT NULL) OR (scope_kind='comparison' AND session_id IS NULL AND node_id IS NULL AND repository_id IS NOT NULL AND comparison_id IS NOT NULL AND expires_at IS NOT NULL))
                    OR payload_sha256 GLOB '*[^0-9a-f]*' OR evidence_index_sha256 GLOB '*[^0-9a-f]*' OR (scope_kind<>'session' AND payload_json IS NULL)
                    OR (payload_json IS NOT NULL AND (length(payload_json)>{LocalAiAnalysisStoreV1.MaximumSnapshotDocumentBytes} OR length(evidence_index_json)>{LocalAiAnalysisStoreV1.MaximumSnapshotDocumentBytes}))
                    OR (scope_kind='session' AND (SELECT COUNT(*) FROM retention_items i WHERE i.store_kind='analysis_run_raw' AND i.source_item_id='local_ai:snapshot:'||s.snapshot_id)<>1)
                    OR (scope_kind='session' AND payload_json IS NULL AND EXISTS(SELECT 1 FROM retention_items i WHERE i.source_item_id='local_ai:snapshot:'||s.snapshot_id AND i.read_denied_at IS NULL AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now))))
                    OR ($allowDeniedContent=0 AND scope_kind='session' AND payload_json IS NOT NULL AND EXISTS(SELECT 1 FROM retention_items i WHERE i.source_item_id='local_ai:snapshot:'||s.snapshot_id AND (i.read_denied_at IS NOT NULL OR i.state NOT IN('retained_by_policy','expiring') OR (i.state='expiring' AND i.expires_at<=$now))))
                    OR (scope_kind<>'session' AND EXISTS(SELECT 1 FROM retention_items i WHERE i.source_item_id='local_ai:snapshot:'||s.snapshot_id))
                  UNION ALL SELECT 1 FROM local_ai_runs r JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id
                    WHERE r.scope_kind<>s.scope_kind OR r.session_id IS NOT s.session_id OR r.node_id IS NOT s.node_id OR r.repository_id IS NOT s.repository_id OR r.comparison_id IS NOT s.comparison_id OR typeof(r.requested_at)<>'text' OR (r.scope_kind='comparison' AND r.expires_at>s.expires_at)
                      OR r.provider<>'github_copilot_sdk' OR length(r.model) NOT BETWEEN 1 AND 200 OR length(r.prompt_template_version) NOT BETWEEN 1 AND 200 OR r.configuration_sha256 GLOB '*[^0-9a-f]*'
                      OR r.timeout_seconds NOT BETWEEN 1 AND 600 OR r.requested_at<s.created_at OR r.started_at<r.requested_at OR r.completed_at<r.started_at OR r.updated_at<r.created_at OR r.updated_at<coalesce(r.completed_at,r.started_at,r.requested_at)
                      OR (r.state='queued' AND (r.started_at IS NOT NULL OR r.completed_at IS NOT NULL OR r.error_code IS NOT NULL OR r.result_id IS NOT NULL))
                      OR (r.state='running' AND (r.started_at IS NULL OR r.completed_at IS NOT NULL OR r.error_code IS NOT NULL OR r.result_id IS NOT NULL))
                      OR (r.state IN('succeeded','zero_findings') AND (r.started_at IS NULL OR r.completed_at IS NULL OR r.error_code IS NOT NULL OR r.result_id IS NULL OR (SELECT COUNT(*) FROM local_ai_results x WHERE x.result_id=r.result_id AND x.run_id=r.run_id)<>1))
                      OR (r.state NOT IN('queued','running','succeeded','zero_findings') AND (r.started_at IS NULL OR r.completed_at IS NULL OR r.error_code<>r.state OR r.result_id IS NOT NULL))
                  UNION ALL SELECT 1 FROM local_ai_results x JOIN local_ai_runs r ON r.run_id=x.run_id
                    WHERE typeof(x.retention_owner_token)<>'blob' OR length(x.retention_owner_token)<>32 OR x.result_sha256 GLOB '*[^0-9a-f]*' OR length(x.result_json)>1048576 OR r.result_id<>x.result_id OR r.state NOT IN('succeeded','zero_findings') OR x.created_at<>r.completed_at OR (r.scope_kind<>'session' AND x.result_json IS NULL) OR x.expires_at IS NOT r.expires_at
                      OR (r.scope_kind='session' AND (SELECT COUNT(*) FROM retention_items i WHERE i.store_kind='analysis_run_raw' AND i.source_item_id='local_ai:result:'||x.result_id)<>1)
                      OR (r.scope_kind='session' AND x.result_json IS NULL AND EXISTS(SELECT 1 FROM retention_items i WHERE i.source_item_id='local_ai:result:'||x.result_id AND i.read_denied_at IS NULL AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now))))
                      OR ($allowDeniedContent=0 AND r.scope_kind='session' AND x.result_json IS NOT NULL AND EXISTS(SELECT 1 FROM retention_items i WHERE i.source_item_id='local_ai:result:'||x.result_id AND (i.read_denied_at IS NOT NULL OR i.state NOT IN('retained_by_policy','expiring') OR (i.state='expiring' AND i.expires_at<=$now))))
                      OR (r.scope_kind<>'session' AND EXISTS(SELECT 1 FROM retention_items i WHERE i.source_item_id='local_ai:result:'||x.result_id))
                  UNION ALL SELECT 1 FROM local_ai_results x LEFT JOIN local_ai_runs r ON r.run_id=x.run_id WHERE r.run_id IS NULL
                );
                """;invalid.Parameters.AddWithValue("$now",at);invalid.Parameters.AddWithValue("$allowDeniedContent",allowDeniedContent?1:0); if(Convert.ToInt64(invalid.ExecuteScalar(),CultureInfo.InvariantCulture)!=0)return false;
        }
        using(var ids=connection.CreateCommand()){ids.Transaction=transaction;ids.CommandText="SELECT snapshot_id FROM local_ai_snapshots UNION ALL SELECT run_id FROM local_ai_runs UNION ALL SELECT result_id FROM local_ai_results;";using var reader=ids.ExecuteReader();var count=0;while(reader.Read()){if(++count>1_000_000 || !LocalAiResultValidatorV1.CanonicalUuid7(reader.GetString(0)))return false;}}
        using(var facts=connection.CreateCommand()){facts.Transaction=transaction;facts.CommandText="SELECT scope_kind,session_id,node_id,repository_id,comparison_id,created_at,expires_at FROM local_ai_snapshots UNION ALL SELECT scope_kind,session_id,node_id,repository_id,comparison_id,requested_at,expires_at FROM local_ai_runs;";using var reader=facts.ExecuteReader();while(reader.Read()){LocalAiScopeIdentityV1 identity;try{identity=LocalAiScopeIdentityV1.Create(reader.GetString(0),reader.IsDBNull(1)?null:reader.GetString(1),reader.IsDBNull(2)?null:reader.GetString(2),reader.IsDBNull(3)?null:reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4));}catch(ArgumentException){return false;}if(!CanonicalTimestamp(reader.GetString(5))||!reader.IsDBNull(6)&&!CanonicalTimestamp(reader.GetString(6)))return false;var created=DateTimeOffset.ParseExact(reader.GetString(5),"O",CultureInfo.InvariantCulture,DateTimeStyles.None);if(identity.Kind==LocalAiScopeKindV1.Session){if(!reader.IsDBNull(6))return false;}else{var expires=DateTimeOffset.ParseExact(reader.GetString(6),"O",CultureInfo.InvariantCulture,DateTimeStyles.None);if(expires<=created||(identity.Kind==LocalAiScopeKindV1.Node?expires!=created.AddHours(24):expires>created.AddHours(24)))return false;}}}
        using(var times=connection.CreateCommand()){times.Transaction=transaction;times.CommandText="SELECT requested_at,started_at,completed_at,created_at,updated_at FROM local_ai_runs;";using var reader=times.ExecuteReader();while(reader.Read()){for(var i=0;i<5;i++)if(!reader.IsDBNull(i)&&!CanonicalTimestamp(reader.GetString(i)))return false;}}
        using(var snapshots=connection.CreateCommand())
        {
            snapshots.Transaction=transaction;snapshots.CommandText="SELECT snapshot_id,scope_kind,session_id,node_id,repository_id,comparison_id,anchor_id,payload_json,payload_sha256,evidence_index_json,evidence_index_sha256 FROM local_ai_snapshots;";using var reader=snapshots.ExecuteReader();
            while(reader.Read()){var session=reader.IsDBNull(2)?null:reader.GetString(2);var node=reader.IsDBNull(3)?null:reader.GetString(3);var repository=reader.IsDBNull(4)?null:reader.GetString(4);var comparison=reader.IsDBNull(5)?null:reader.GetString(5);if(!LocalAiAnalysisStoreV1.ValidateStoredSnapshotMetadata(reader.GetString(0),reader.GetString(1),session,node,reader.GetString(6),reader.GetString(8),reader.GetString(10),repository,comparison))return false;if(!reader.IsDBNull(7)&&!LocalAiAnalysisStoreV1.ValidateStoredSnapshot(new(reader.GetString(0),reader.GetString(1),session,node,reader.GetString(6),(byte[])reader[7],reader.GetString(8),(byte[])reader[9],reader.GetString(10),repository,comparison)))return false;}
        }
        using(var results=connection.CreateCommand())
        {
            results.Transaction=transaction;results.CommandText="SELECT x.result_json,x.result_sha256,s.evidence_index_json,s.payload_sha256,s.snapshot_id,s.scope_kind,s.session_id,s.node_id,s.anchor_id,r.provider,r.model,r.configuration_sha256,r.prompt_template_version,r.requested_at,r.started_at,r.completed_at,x.created_at,r.state,s.repository_id,s.comparison_id FROM local_ai_results x JOIN local_ai_runs r ON r.run_id=x.run_id JOIN local_ai_snapshots s ON s.snapshot_id=r.snapshot_id WHERE x.result_json IS NOT NULL;";using var reader=results.ExecuteReader();
            while(reader.Read()){var json=(byte[])reader[0];if(Convert.ToHexStringLower(SHA256.HashData(json))!=reader.GetString(1)||!CanonicalTimestamp(reader.GetString(16))||reader.GetString(16)!=reader.GetString(15))return false;var evidence=reader.IsDBNull(2)?null:(byte[])reader[2];var expected=new LocalAiStoredResultInvariantV1(evidence,reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),reader.GetString(8),reader.GetString(9),reader.GetString(10),reader.GetString(11),reader.GetString(12),reader.GetString(13),reader.GetString(14),reader.GetString(15),reader.GetString(17),reader.IsDBNull(18)?null:reader.GetString(18),reader.IsDBNull(19)?null:reader.GetString(19));if(evidence is null?!LocalAiAnalysisStoreV1.ValidateStoredResultWithoutEvidenceMembership(json,expected):!LocalAiAnalysisStoreV1.ValidateStoredResult(json,expected))return false;}
        }
        return true;
    }

    private static bool CanonicalTimestamp(string value)=>DateTimeOffset.TryParseExact(value,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed)&&parsed.ToString("O",CultureInfo.InvariantCulture)==value;

    private static bool HasUndeclaredLocalRepositoryCatalogObjects(
        SqliteConnection connection,
        IReadOnlyDictionary<string, int> versions)
    {
        if (versions.ContainsKey("local_repository_catalog")) return false;
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM sqlite_schema
                WHERE type IN ('table','index','trigger','view')
                  AND (name='local_repositories' COLLATE NOCASE
                       OR name LIKE 'local\_repository\_%' ESCAPE '\' COLLATE NOCASE
                       OR name LIKE 'session\_repository\_%' ESCAPE '\' COLLATE NOCASE
                       OR name LIKE 'IX\_local\_repository\_%' ESCAPE '\' COLLATE NOCASE
                       OR name LIKE 'IX\_session\_repository\_%' ESCAPE '\' COLLATE NOCASE));
            """;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void ValidateIntegrity(SqliteConnection connection)
    {
        using (var quick = connection.CreateCommand())
        {
            quick.CommandText = "SELECT COUNT(*),COALESCE(SUM(CASE WHEN typeof(quick_check)='text' AND length(CAST(quick_check AS BLOB))=2 AND quick_check='ok' THEN 1 ELSE 0 END),0) FROM pragma_quick_check;";
            using var reader = quick.ExecuteReader();
            if (!reader.Read() || reader.GetInt64(0) != 1 || reader.GetInt64(1) != 1 || reader.Read())
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        }
        using var foreign = connection.CreateCommand();
        foreign.CommandText = "SELECT EXISTS(SELECT 1 FROM pragma_foreign_key_check);";
        if (Convert.ToInt64(foreign.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
    }

    private static Dictionary<string, int> ReadComponentVersions(string path, bool readOnly)
    { using var connection = Open(path, readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, immutableReadOnly: readOnly); return ReadComponentVersions(connection); }

    private static Dictionary<string, int> ReadComponentVersions(
        SqliteConnection connection,
        bool requireSharedSchemaVersion = true)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!TableExists(connection, "schema_version"))
        {
            if (requireSharedSchemaVersion)
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
        }
        else
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT component,version,typeof(component),typeof(version),length(CAST(component AS BLOB)) FROM schema_version ORDER BY component;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
                if (reader.GetString(2) != "text" || reader.GetString(3) != "integer" || reader.GetInt64(4) is <= 0 or > 128)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
                var component = reader.GetString(0);
                if (component.Any(char.IsControl) || !result.TryAdd(component, reader.GetInt32(1)))
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
            }
        }
        if (TableExists(connection, "retention_component_versions"))
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT component,version,typeof(component),typeof(version),length(CAST(component AS BLOB)) FROM retention_component_versions ORDER BY component;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
                if (reader.GetString(2) != "text" || reader.GetString(3) != "integer" || reader.GetInt64(4) is <= 0 or > 128)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
                var component = reader.GetString(0);
                if (component.Any(char.IsControl) || !result.TryAdd(component, reader.GetInt32(1)))
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible);
            }
        }
        return result;
    }

    private static Dictionary<string, long> ReadRowCounts(string path) { using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly: true); return ReadRowCounts(connection); }
    private static Dictionary<string, long> ReadRowCounts(SqliteConnection connection)
    {
        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT CASE WHEN typeof(name)='text' AND length(CAST(name AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaIdentifierBytes} THEN 1 ELSE 0 END,name FROM sqlite_schema WHERE type='table' AND name NOT GLOB 'sqlite_*' ORDER BY name;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (tables.Count >= RuntimeBackupLimits.MaximumInventoryItems)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
                if (reader.GetInt64(0) != 1)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
                var table = reader.GetString(1);
                if (table.Any(char.IsControl)) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
                tables.Add(table);
            }
        }
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables) { using var command = connection.CreateCommand(); command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"")}\";"; result[table] = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
        return result;
    }

    private static Dictionary<string, long?> ReadProjectionCursors(string path, bool immutableReadOnly)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly); var result = new Dictionary<string, long?>(StringComparer.Ordinal);
        if (TableExists(connection, "session_projection_state"))
        {
            using var command = connection.CreateCommand(); command.CommandText = "SELECT projector_key,projection_cursor,typeof(projector_key),typeof(projection_cursor),length(CAST(projector_key AS BLOB)) FROM session_projection_state ORDER BY projector_key;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
                if (reader.GetString(2) != "text" || reader.GetInt64(4) is <= 0 or > 120
                    || reader.GetString(3) is not ("integer" or "null"))
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
                var key = $"session:{reader.GetString(0)}";
                var cursor = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                if (cursor is < 0 || key.Any(char.IsControl) || !result.TryAdd(key, cursor))
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid);
            }
        }
        return result;
    }

    private static RuntimeBackupRetentionSummary ReadRetentionSummary(string path, bool immutableReadOnly)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly);
        var kinds = RetentionStoreKinds.ToDictionary(kind => kind, _ => 0L, StringComparer.Ordinal);
        var states = RetentionStates.ToDictionary(state => state, _ => 0L, StringComparer.Ordinal);
        if (!TableExists(connection, "retention_items")) return new(kinds, states, 0, null, null, null, null, []);
        ValidateRetentionPreflightBounds(connection);
        using (var command = connection.CreateCommand()) { command.CommandText = "SELECT store_kind,COUNT(*) FROM retention_items GROUP BY store_kind ORDER BY store_kind;"; using var reader = command.ExecuteReader(); while (reader.Read()) { if (!kinds.ContainsKey(reader.GetString(0))) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible); kinds[reader.GetString(0)] = reader.GetInt64(1); } }
        using (var command = connection.CreateCommand()) { command.CommandText = "SELECT state,COUNT(*) FROM retention_items GROUP BY state ORDER BY state;"; using var reader = command.ExecuteReader(); while (reader.Read()) { if (!states.ContainsKey(reader.GetString(0))) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreIncompatible); states[reader.GetString(0)] = reader.GetInt64(1); } }
        string? ScalarText(string expression) { using var command = connection.CreateCommand(); command.CommandText = $"SELECT {expression} FROM retention_items;"; return command.ExecuteScalar() is string value ? value : null; }
        var policies = new List<string>(); using (var command = connection.CreateCommand()) { command.CommandText = "SELECT DISTINCT policy_id,policy_version FROM retention_items ORDER BY policy_id,policy_version;"; using var reader = command.ExecuteReader(); while (reader.Read()) { if (policies.Count >= RuntimeBackupLimits.MaximumInventoryItems) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ManifestInvalid); policies.Add($"{reader.GetString(0)}@{reader.GetInt32(1)}"); } }
        long tombstones = 0; if (TableExists(connection, "retention_tombstones")) { using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM retention_tombstones;"; tombstones = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
        return new(kinds, states, tombstones, ScalarText("MIN(captured_at)"), ScalarText("MAX(captured_at)"), ScalarText("MIN(expires_at)"), ScalarText("MAX(expires_at)"), policies);
    }

    private static IReadOnlyList<RuntimeBackupExternalState> ReadExternalState(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath)!;
        var proposalState = ReadProposalApplyState(Path.Combine(directory, "proposal-apply"));
        return
        [
            new("ephemeral_runtime", PathEntryExists(Path.Combine(directory, "local-monitor.state.json")) ? "present" : "absent", false, "ephemeral", "restart_rematerializes"),
            new("setup_storage", Directory.Exists(Path.Combine(directory, "setup")) ? "present" : "absent", false, "host_bound", "rerun_setup"),
            new("proposal_apply", proposalState, false, "configuration_only", "reconfigure_apply_roots"),
            new("operator_backups", "not_inventoried", false, "operator_owned", "retain_or_delete_separately"),
        ];
    }

    private static string ReadExternalStateFingerprint(string databasePath)
    {
        var directory = Path.Combine(Path.GetDirectoryName(databasePath)!, "proposal-apply");
        var state = ReadProposalApplyState(directory);
        var rootMap = Path.Combine(directory, "apply-root-map.json");
        return HashText(state == "configured" ? $"proposal_apply\nconfigured\n{HashFile(rootMap)}" : $"proposal_apply\n{state}");
    }

    private static string? ValidateDatabaseExternalRawState(string databasePath, bool immutableReadOnly)
    {
        try
        {
            if (!PathEntryExists(databasePath)) return null;
            if (!IsRegularControlFile(databasePath)) return RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe;
            using var connection = Open(databasePath, SqliteOpenMode.ReadOnly, immutableReadOnly);
            var itemsTable = FindTableNameOrdinalIgnoreCase(connection, "retention_items");
            var fileReservationsTable = FindTableNameOrdinalIgnoreCase(connection, "retention_file_capture_reservations");
            var sdkReservationsTable = FindTableNameOrdinalIgnoreCase(connection, "retention_analysis_sdk_directory_reservations");
            var captureJournalTable = FindTableNameOrdinalIgnoreCase(connection, "retention_capture_journal");
            var legacyJournalTable = FindTableNameOrdinalIgnoreCase(connection, "retention_legacy_bundle_journal");
            var blockerTable = FindTableNameOrdinalIgnoreCase(connection, "retention_legacy_bundle_blockers");
            if (itemsTable is not null && HasRows(connection, itemsTable, "candidate.store_kind IN ('sensitive_bundle','analysis_sdk_directory') AND candidate.state<>'deleted'"))
                return RuntimeBackupErrorCodes.ExternalRawStoreActive;
            if (fileReservationsTable is not null && HasRows(connection, fileReservationsTable, itemsTable is not null
                    ? $"candidate.phase IS NULL OR candidate.phase<>'complete' OR candidate.store_kind IS NULL OR candidate.store_kind<>'sensitive_bundle' OR candidate.capture_id IS NULL OR candidate.source_item_id IS NULL OR candidate.source_item_id<>candidate.capture_id OR NOT EXISTS(SELECT 1 FROM {QuoteIdentifier(itemsTable)} i WHERE i.store_instance_id=candidate.store_instance_id AND i.store_kind='sensitive_bundle' AND i.source_item_id=candidate.capture_id AND i.state='deleted')"
                    : "1=1"))
                return RuntimeBackupErrorCodes.ExternalRawStoreActive;
            if (sdkReservationsTable is not null
                && HasRows(connection, sdkReservationsTable, itemsTable is not null
                    ? $"candidate.phase IS NULL OR candidate.phase<>'sealed' OR NOT EXISTS(SELECT 1 FROM {QuoteIdentifier(itemsTable)} i WHERE i.store_instance_id=candidate.store_instance_id AND i.store_kind='analysis_sdk_directory' AND i.source_item_id=candidate.capture_id AND i.state='deleted')"
                    : "1=1"))
                return RuntimeBackupErrorCodes.ExternalRawStoreActive;
            if (captureJournalTable is not null
                && HasRows(connection, captureJournalTable, itemsTable is not null && fileReservationsTable is not null
                    ? $"candidate.phase IS NULL OR candidate.phase<>'complete' OR NOT EXISTS(SELECT 1 FROM {QuoteIdentifier(itemsTable)} i JOIN {QuoteIdentifier(fileReservationsTable)} r ON r.store_instance_id=i.store_instance_id AND r.capture_id=i.source_item_id WHERE i.item_id=candidate.item_id AND i.store_kind='sensitive_bundle' AND i.state='deleted' AND r.store_kind='sensitive_bundle' AND r.source_item_id=r.capture_id AND r.phase='complete')"
                    : "1=1"))
                return RuntimeBackupErrorCodes.ExternalRawStoreActive;
            if (legacyJournalTable is not null
                && HasRows(connection, legacyJournalTable, itemsTable is not null && fileReservationsTable is not null
                    ? $"candidate.subphase IS NULL OR candidate.subphase<>'catalog_completed' OR NOT EXISTS(SELECT 1 FROM {QuoteIdentifier(fileReservationsTable)} r WHERE r.capture_id=candidate.capture_id)"
                    : "1=1"))
                return RuntimeBackupErrorCodes.ExternalRawStoreActive;
            return blockerTable is not null
                && HasRows(connection, blockerTable, "1=1")
                    ? RuntimeBackupErrorCodes.ExternalRawStoreActive
                    : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or SqliteException or InvalidCastException or FormatException or OverflowException)
        {
            return RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe;
        }
    }

    private string? ValidateExternalState(
        string databasePath,
        bool scanRuntimeRoot,
        bool immutableDatabase,
        IReadOnlyCollection<string>? additionalAllowedFiles = null)
    {
        try
        {
            var rawError = ValidateDatabaseExternalRawState(databasePath, immutableDatabase);
            if (rawError is not null) return rawError;
            var proposal = Path.Combine(Path.GetDirectoryName(databasePath)!, "proposal-apply");
            _ = ReadProposalApplyState(proposal);
            if (!scanRuntimeRoot) return null;
            var databaseFullPath = Path.GetFullPath(databasePath);
            var allowedDirectories = new HashSet<string>(["app", "logs", "setup", "proposal-apply", "sanitized-exports", "runtime-backups"], PathComparer);
            var allowedFiles = new HashSet<string>(
                [Path.GetFileName(databaseFullPath), Path.GetFileName(databaseFullPath) + "-journal", Path.GetFileName(databaseFullPath) + "-wal", Path.GetFileName(databaseFullPath) + "-shm", Path.GetFileName(databaseFullPath) + ".runtime-restore.lock", "local-monitor.state.json", "local-monitor.pid"],
                PathComparer);
            var entries = Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(databaseFullPath)!, "*", SearchOption.TopDirectoryOnly)
                .Take(RuntimeBackupLimits.MaximumInventoryItems + 1).ToArray();
            if (entries.Length > RuntimeBackupLimits.MaximumInventoryItems)
                return RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe;
            foreach (var entry in entries)
            {
                var name = Path.GetFileName(entry);
                var kind = RuntimeBackupNativePathClassifier.Read(entry);
                if (PathComparer.Equals(name, "raw-replays"))
                {
                    if (kind != RuntimeBackupNativePathKind.Directory)
                        return RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe;
                    if (Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.TopDirectoryOnly).Take(1).Any())
                        return RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown;
                    continue;
                }
                if (kind == RuntimeBackupNativePathKind.Directory)
                {
                    if (!allowedDirectories.Contains(name)) return RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown;
                    continue;
                }
                if (allowedDirectories.Contains(name)) return RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe;
                if (kind != RuntimeBackupNativePathKind.RegularFile)
                    return RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe;
                if (!allowedFiles.Contains(name) && !(additionalAllowedFiles?.Any(path => PathEquals(path, entry)) ?? false)
                    && !IsRecognizedOperatorBackup(entry))
                    return RuntimeBackupErrorCodes.ExternalRuntimeStateUnknown;
            }
            return null;
        }
        catch (RuntimeBackupException exception) { return exception.Code; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or SqliteException) { return RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe; }
    }

    private bool IsRecognizedOperatorBackup(string path)
    {
        if (!IsRegularControlFile(path, RuntimeBackupLimits.MaximumArchiveBytes)) return false;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length <= 0 || file.Length > RuntimeBackupLimits.MaximumArchiveBytes) return false;
            ValidateStoredArchive(file, expectedEntries: 2);
            var expectedCrcs = ReadStoredEntryCrcs(file, expectedEntries: 2);
            file.Position = 0;
            using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            if (!archive.Entries.Select(entry => entry.FullName).SequenceEqual(["manifest.json", "database.sqlite"], StringComparer.Ordinal)
                || archive.Entries.Any(entry => entry.CompressedLength != entry.Length
                    || entry.ExternalAttributes != 0
                    || entry.LastWriteTime.DateTime != ArchiveTimestamp.DateTime)) return false;
            var manifestBytes = ReadBounded(archive.Entries[0], RuntimeBackupLimits.MaximumManifestBytes, RuntimeBackupErrorCodes.ManifestInvalid);
            if (ComputeCrc32(manifestBytes) != expectedCrcs[0]) return false;
            var manifest = RuntimeBackupJson.ParseManifest(manifestBytes);
            return archive.Entries[1].Length == manifest.DatabaseSize
                && archive.Entries[1].Length <= RuntimeBackupLimits.MaximumDatabaseBytes;
        }
        catch (Exception exception) when (exception is RuntimeBackupException or InvalidDataException or IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static string ReadProposalApplyState(string directory)
    {
        if (!PathEntryExists(directory)) return "absent";
        if (RuntimeBackupNativePathClassifier.Read(directory) != RuntimeBackupNativePathKind.Directory)
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe);
        var entries = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Take(RuntimeBackupLimits.MaximumInventoryItems + 1).ToArray();
        if (entries.Length > RuntimeBackupLimits.MaximumInventoryItems)
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe);
        byte[]? bytes = null;
        foreach (var entry in entries)
        {
            var name = Path.GetFileName(entry);
            var kind = RuntimeBackupNativePathClassifier.Read(entry);
            if (string.Equals(name, "drafts", StringComparison.Ordinal))
            {
                if (kind != RuntimeBackupNativePathKind.Directory)
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe);
                if (Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.TopDirectoryOnly).Take(1).Any())
                    throw new RuntimeBackupException(RuntimeBackupErrorCodes.ExternalRuntimeStateActive);
                continue;
            }
            if (kind != RuntimeBackupNativePathKind.RegularFile)
                throw new RuntimeBackupException(kind == RuntimeBackupNativePathKind.Directory
                    ? RuntimeBackupErrorCodes.ExternalRuntimeStateActive
                    : RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe);
            if (!string.Equals(name, "apply-root-map.json", StringComparison.Ordinal))
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ExternalRuntimeStateActive);
            if (bytes is not null || !ValidateProposalRootMap(entry)
                || ReadBoundedControlFile(entry, 256 * 1024) is not { } rootMapBytes)
                throw new RuntimeBackupException(RuntimeBackupErrorCodes.ExternalRuntimeStateUnsafe);
            bytes = rootMapBytes;
        }
        if (bytes is null) return "empty";
        return bytes.AsSpan().SequenceEqual("[]"u8) ? "empty" : "configured";
    }

    private static bool ValidateProposalRootMap(string path)
    {
        try
        {
            var bytes = ReadBoundedControlFile(path, 256 * 1024);
            if (bytes is null) return false;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = RuntimeBackupLimits.MaximumJsonDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            var ids = new HashSet<Guid>();
            var configuredRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            using var canonical = new MemoryStream();
            using (var writer = new Utf8JsonWriter(canonical))
            {
                writer.WriteStartArray();
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (++count > RuntimeBackupLimits.MaximumInventoryItems || item.ValueKind != JsonValueKind.Object
                        || !item.EnumerateObject().Select(property => property.Name).SequenceEqual(new[] { "RootId", "Kind", "CanonicalPath" }, StringComparer.Ordinal)) return false;
                    var idElement = item.GetProperty("RootId");
                    var kindElement = item.GetProperty("Kind");
                    var pathElement = item.GetProperty("CanonicalPath");
                    if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParseExact(idElement.GetString(), "D", out var id) || !ids.Add(id)
                        || kindElement.ValueKind != JsonValueKind.Number || !kindElement.TryGetInt32(out var kind) || kind is < 0 or > 2
                        || pathElement.ValueKind != JsonValueKind.String || pathElement.GetString() is not { Length: > 0 and <= 32767 } configuredPath
                        || configuredPath.Any(char.IsControl) || !configuredRoots.Add($"{kind}\n{configuredPath}")) return false;
                    writer.WriteStartObject();
                    writer.WriteString("RootId", id);
                    writer.WriteNumber("Kind", kind);
                    writer.WriteString("CanonicalPath", configuredPath);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return bytes.AsSpan().SequenceEqual(canonical.ToArray());
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static RetentionComparison CompareRetention(
        string currentPath,
        string stagedPath,
        string archiveHash,
        bool currentImmutableReadOnly)
    {
        try
        {
            using var current = Open(currentPath, SqliteOpenMode.ReadOnly, currentImmutableReadOnly);
            using var staged = Open(stagedPath, SqliteOpenMode.ReadOnly, immutableReadOnly: true);
            if (!TableExists(current, "retention_items") || !TableExists(staged, "retention_items")) return RetentionComparison.Empty;
            using var currentTransaction = current.BeginTransaction(deferred: true);
            using var stagedTransaction = staged.BeginTransaction(deferred: true);
            var currentStore = LoadReconciliationStoreId(current, currentTransaction);
            var stagedStore = LoadReconciliationStoreId(staged, stagedTransaction);
            var currentCount = ScalarLong(current, currentTransaction, "SELECT COUNT(*) FROM retention_items;");
            if (currentCount > 0 && currentStore != stagedStore) return RetentionComparison.Failure(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed);
            using var terminalDigest = new OrderedDigestAccumulator();
            var terminalCount = 0;
            foreach (var row in EnumerateRetentionRows(current, "state='deleted' OR read_denied_at IS NOT NULL", currentTransaction))
            {
                if (!TryResolveAuditScope(
                        current,
                        currentTransaction,
                        staged,
                        stagedTransaction,
                        row,
                        out _,
                        out _))
                    return RetentionComparison.Failure(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed);
                terminalDigest.Append(Identity(row));
                terminalCount = IncrementCount(terminalCount);
            }
            using var candidateDigest = new OrderedDigestAccumulator();
            var nonTerminalCount = 0;
            foreach (var row in EnumerateRetentionRows(current, "state<>'deleted' AND read_denied_at IS NULL", currentTransaction))
            {
                var stagedRow = FindRetentionRow(staged, row, stagedTransaction);
                if (stagedRow is null || !IsSqliteKind(row) || SourceExists(current, row, currentTransaction) || !SourceExists(staged, stagedRow, stagedTransaction)) continue;
                if (!SameReintroductionLineage(row, stagedRow)) return RetentionComparison.Failure(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed);
                var proof = BuildRetentionProof(current, currentTransaction, staged, stagedTransaction, row, stagedRow);
                if (proof is null) return RetentionComparison.Failure(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed);
                candidateDigest.Append($"{Identity(row)}\n{proof}");
                nonTerminalCount = IncrementCount(nonTerminalCount);
            }
            var terminalHash = terminalDigest.Complete();
            var comparisonDigest = candidateDigest.Complete();
            var confirmation = nonTerminalCount == 0 ? null : HashText($"local-runtime-restore-non-terminal-reintroduction-confirmation.v1\n{archiveHash}\n{comparisonDigest}\n{nonTerminalCount}");
            currentTransaction.Rollback();
            stagedTransaction.Rollback();
            return new(true, null, terminalCount, terminalHash, nonTerminalCount, comparisonDigest, confirmation);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or InvalidCastException or FormatException or OverflowException) { return RetentionComparison.Failure(RuntimeBackupErrorCodes.RestoreTombstoneReconcileFailed); }
    }

    private static bool ReconcileTerminal(string currentPath, string stagedPath, RetentionComparison expected)
    {
        try
        {
            using var current = Open(currentPath, SqliteOpenMode.ReadOnly, immutableReadOnly: false); using var staged = Open(stagedPath, SqliteOpenMode.ReadWrite);
            if (!TableExists(current, "retention_items")) return true;
            using var currentTransaction = current.BeginTransaction(deferred: true);
            using var transaction = staged.BeginTransaction(deferred: false);
            using var digest = new OrderedDigestAccumulator();
            var count = 0;
            foreach (var row in EnumerateRetentionRows(current, "state='deleted' OR read_denied_at IS NOT NULL", currentTransaction))
            {
                var identity = Identity(row);
                digest.Append(identity);
                count = IncrementCount(count);
                var stagedRow = FindRetentionRow(staged, row, transaction);
                var itemId = Convert.ToString(row["item_id"], CultureInfo.InvariantCulture)!;
                var stagedPrimaryRow = LoadGenericRow(staged, "retention_items", "item_id", itemId, transaction);
                if (stagedPrimaryRow is not null && !string.Equals(Identity(stagedPrimaryRow), identity, StringComparison.Ordinal)) return false;
                if (stagedRow is not null && (!BytesEqual(row["ownership_receipt"], stagedRow["ownership_receipt"]) || Convert.ToString(row["item_id"], CultureInfo.InvariantCulture) != Convert.ToString(stagedRow["item_id"], CultureInfo.InvariantCulture))) return false;
                UpsertGenericRow(current, currentTransaction, staged, transaction, "retention_items", "item_id", row);
                if (Convert.ToString(row["state"], CultureInfo.InvariantCulture) == "deleted")
                {
                    var tombstone = LoadGenericRow(current, "retention_tombstones", "item_id", itemId, currentTransaction);
                    if (tombstone is null) return false;
                    UpsertGenericRow(current, currentTransaction, staged, transaction, "retention_tombstones", "item_id", tombstone);
                    if (string.Equals(Convert.ToString(row["store_kind"], CultureInfo.InvariantCulture), "session_event_content", StringComparison.Ordinal)
                        && TableExists(staged, "local_workspace_content_tombstones", transaction))
                    {
                        var sourceItemId = Convert.ToString(row["source_item_id"], CultureInfo.InvariantCulture);
                        var deletedAt = Convert.ToString(row["deleted_at"], CultureInfo.InvariantCulture);
                        if (string.IsNullOrEmpty(sourceItemId) || !DateTimeOffset.TryParse(deletedAt, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var completedAt))
                            return false;
                        LocalWorkspaceProjectionStore.CompleteSessionEventContentDeletion(
                            staged,
                            transaction,
                            sourceItemId,
                            completedAt);
                    }
                }
                else
                {
                    DeleteIfTable(staged, transaction, "retention_tombstones", "item_id", itemId);
                    DeleteWorkspaceContentTombstones(staged, transaction, row);
                }
                if (stagedRow is not null && IsSqliteKind(stagedRow)) DeleteSource(staged, transaction, stagedRow);
                if (IsSqliteKind(row) && SourceExists(staged, row, transaction)) return false;
                DeleteIfTable(staged, transaction, "retention_leases", "item_id", itemId);
                var intent = LoadGenericRow(current, "retention_delete_journal", "item_id", itemId, currentTransaction);
                if (intent is not null) UpsertGenericRow(current, currentTransaction, staged, transaction, "retention_delete_journal", "item_id", intent);
                else DeleteIfTable(staged, transaction, "retention_delete_journal", "item_id", itemId);
                if (!CopyAudit(current, currentTransaction, staged, transaction, row)) return false;
            }
            if (count != expected.TerminalCount || !OptionalHashEquals(digest.Complete(), expected.TerminalDigest)) return false;
            transaction.Commit();
            currentTransaction.Rollback();
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or FormatException) { return false; }
    }

    private bool RefreshWorkspaceAfterRetentionReconciliation(
        string currentPath,
        string stagedPath,
        ISkillRegistryGenerationAuthority capturedAuthority)
    {
        try
        {
            using var current = Open(currentPath, SqliteOpenMode.ReadOnly, immutableReadOnly: false);
            using var currentTransaction = current.BeginTransaction(deferred: true);
            var terminalAuthority = LocalWorkspaceTerminalAuthority.Capture(
                current,
                currentTransaction,
                includeSourceBackedReadDenied: true);
            using var staged = Open(stagedPath, SqliteOpenMode.ReadWrite);
            using var transaction = staged.BeginTransaction(deferred: false);
            var state = LocalWorkspaceProjectionSchemaV1.ReadInstallationState(staged, transaction);
            if (state == LocalWorkspaceProjectionInstallationState.Absent)
            {
                transaction.Rollback();
                return true;
            }
            if (state != LocalWorkspaceProjectionInstallationState.Current)
                return false;
            terminalAuthority.ApplyTombstones(staged, transaction);
            LocalWorkspaceProjectionStore.Refresh(
                staged,
                transaction,
                timeProvider.GetUtcNow(),
                capturedAuthority);
            terminalAuthority.ApplyReadDenied(staged, transaction);
            LocalWorkspaceProjectionSchemaV1.ValidateAndRestampCurrent(staged, transaction);
            EnsureForeignKeysValid(staged, transaction);
            transaction.Commit();
            currentTransaction.Rollback();
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool ReconcileNonTerminal(string currentPath, string stagedPath, RetentionComparison expected)
    {
        try
        {
            using var current = Open(currentPath, SqliteOpenMode.ReadOnly, immutableReadOnly: false); using var staged = Open(stagedPath, SqliteOpenMode.ReadWrite);
            if (!TableExists(current, "retention_items")) return true;
            using var currentTransaction = current.BeginTransaction(deferred: true);
            using var transaction = staged.BeginTransaction(deferred: false);
            using var digest = new OrderedDigestAccumulator();
            var count = 0;
            foreach (var row in EnumerateRetentionRows(current, "state<>'deleted' AND read_denied_at IS NULL", currentTransaction))
            {
                var stagedRow = FindRetentionRow(staged, row, transaction);
                if (stagedRow is null || !IsSqliteKind(row) || SourceExists(current, row, currentTransaction) || !SourceExists(staged, stagedRow, transaction)) continue;
                if (!SameReintroductionLineage(row, stagedRow)) return false;
                var proof = BuildRetentionProof(current, currentTransaction, staged, transaction, row, stagedRow);
                if (proof is null) return false;
                digest.Append($"{Identity(row)}\n{proof}");
                count = IncrementCount(count);
                var itemId = Convert.ToString(row["item_id"], CultureInfo.InvariantCulture)!;
                UpsertGenericRow(current, currentTransaction, staged, transaction, "retention_items", "item_id", row);
                DeleteIfTable(staged, transaction, "retention_tombstones", "item_id", itemId);
                DeleteIfTable(staged, transaction, "retention_leases", "item_id", itemId);
                DeleteIfTable(staged, transaction, "retention_delete_journal", "item_id", itemId);
                if (!SourceExists(staged, row, transaction) || !CopyAudit(current, currentTransaction, staged, transaction, row)) return false;
            }
            if (count != expected.NonTerminalCount || !OptionalHashEquals(digest.Complete(), expected.ComparisonDigest)) return false;
            transaction.Commit();
            currentTransaction.Rollback();
            return true;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or InvalidCastException or FormatException or OverflowException) { return false; }
    }

    private static bool CopyAudit(SqliteConnection current, SqliteTransaction currentTransaction, SqliteConnection staged, SqliteTransaction transaction, Dictionary<string, object?> item)
    {
        if (!TableExists(current, "retention_audit_events", currentTransaction) || !TableExists(staged, "retention_audit_events", transaction)
            || !TableExists(current, "retention_operation_receipts", currentTransaction) || !TableExists(staged, "retention_operation_receipts", transaction)
            || !TableColumns(current, "retention_audit_events", currentTransaction).SequenceEqual(TableColumns(staged, "retention_audit_events", transaction), StringComparer.Ordinal)
            || !TableColumns(current, "retention_operation_receipts", currentTransaction).SequenceEqual(TableColumns(staged, "retention_operation_receipts", transaction), StringComparer.Ordinal)) return false;
        if (!TryResolveAuditScope(current, currentTransaction, staged, transaction, item, out var itemId, out var sessionId)) return false;
        foreach (var audit in EnumerateRelevantAudits(current, currentTransaction, itemId, sessionId))
        {
            var operation = Convert.ToString(audit["operation_id"], CultureInfo.InvariantCulture);
            if (operation is null) return false;
            var receipt = LoadGenericRow(current, "retention_operation_receipts", "operation_id", operation, currentTransaction);
            if (receipt is null || !AuditReceiptMatches(audit, receipt)) return false;
            if (HasNonEquivalentKeyCollision(staged, transaction, "retention_audit_events", "event_id", audit)) return false;
            UpsertGenericRow(current, currentTransaction, staged, transaction, "retention_audit_events", "event_id", audit);
        }
        foreach (var operation in EnumerateRelevantOperationIds(current, currentTransaction, itemId, sessionId))
        {
            var receipt = LoadGenericRow(current, "retention_operation_receipts", "operation_id", operation, currentTransaction);
            if (receipt is null) return false;
            var existing = LoadGenericRow(staged, "retention_operation_receipts", "operation_id", operation, transaction);
            if (existing is not null && !ReceiptRowsEquivalent(existing, receipt)) return false;
            UpsertGenericRow(current, currentTransaction, staged, transaction, "retention_operation_receipts", "operation_id", receipt);
        }
        return true;
    }

    private static bool SameReintroductionLineage(Dictionary<string, object?> current, Dictionary<string, object?> staged) =>
        Convert.ToString(current["item_id"], CultureInfo.InvariantCulture) == Convert.ToString(staged["item_id"], CultureInfo.InvariantCulture)
        && Convert.ToInt64(current["receipt_version"], CultureInfo.InvariantCulture) == Convert.ToInt64(staged["receipt_version"], CultureInfo.InvariantCulture)
        && BytesEqual(current["ownership_receipt"], staged["ownership_receipt"])
        && Convert.ToString(current["captured_at"], CultureInfo.InvariantCulture) == Convert.ToString(staged["captured_at"], CultureInfo.InvariantCulture);

    private static string? BuildRetentionProof(
        SqliteConnection current,
        SqliteTransaction currentTransaction,
        SqliteConnection staged,
        SqliteTransaction stagedTransaction,
        Dictionary<string, object?> row,
        Dictionary<string, object?> stagedRow)
    {
        if (!TableExists(current, "retention_audit_events", currentTransaction)
            || !TableExists(current, "retention_operation_receipts", currentTransaction)
            || !TryResolveAuditScope(current, currentTransaction, staged, stagedTransaction, row, out var itemId, out var sessionId)) return null;
        using var digest = new OrderedDigestAccumulator();
        digest.Append(RowFingerprint(row));
        digest.Append(RowFingerprint(stagedRow));
        foreach (var audit in EnumerateRelevantAudits(current, currentTransaction, itemId, sessionId))
        {
            var operation = Convert.ToString(audit["operation_id"], CultureInfo.InvariantCulture);
            if (operation is null) return null;
            var receipt = LoadGenericRow(current, "retention_operation_receipts", "operation_id", operation, currentTransaction);
            if (receipt is null || !AuditReceiptMatches(audit, receipt)) return null;
            digest.Append(RowFingerprint(audit));
        }
        foreach (var operation in EnumerateRelevantOperationIds(current, currentTransaction, itemId, sessionId))
        {
            var receipt = LoadGenericRow(current, "retention_operation_receipts", "operation_id", operation, currentTransaction);
            if (receipt is null) return null;
            digest.Append(RowFingerprint(receipt));
        }
        return digest.Complete();
    }

    private static bool TryResolveAuditScope(
        SqliteConnection current,
        SqliteTransaction currentTransaction,
        SqliteConnection staged,
        SqliteTransaction stagedTransaction,
        Dictionary<string, object?> item,
        out string itemId,
        out string? sessionId)
    {
        itemId = Convert.ToString(item["item_id"], CultureInfo.InvariantCulture) ?? string.Empty;
        sessionId = null;
        if (itemId.Length == 0) return false;
        if (Convert.ToString(item["store_kind"], CultureInfo.InvariantCulture) == "session_event_content")
        {
            var sourceId = Convert.ToString(item["source_item_id"], CultureInfo.InvariantCulture);
            if (sourceId is null) return false;
            var currentEvent = LoadGenericRow(current, "session_events", "event_id", sourceId, currentTransaction);
            var stagedEvent = LoadGenericRow(staged, "session_events", "event_id", sourceId, stagedTransaction);
            if (currentEvent is null || stagedEvent is null
                || !ExactValuesEqual(
                    currentEvent,
                    stagedEvent,
                    "event_id",
                    "session_id",
                    "run_id",
                    "source_surface",
                    "source_adapter",
                    "source_event_id",
                    "parent_event_id",
                    "trace_id"))
                return false;
            sessionId = TextValue(currentEvent, "session_id");
            if (currentEvent["run_id"] is string runId)
            {
                var currentRun = LoadGenericRow(current, "session_runs", "run_id", runId, currentTransaction);
                var stagedRun = LoadGenericRow(staged, "session_runs", "run_id", runId, stagedTransaction);
                if (currentRun is null || stagedRun is null
                    || !ExactValuesEqual(
                        currentRun,
                        stagedRun,
                        "run_id",
                        "session_id",
                        "source_surface",
                        "native_run_id",
                        "trace_id",
                        "parent_run_id"))
                    return false;
            }
        }
        return true;
    }

    private static void DeleteWorkspaceContentTombstones(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Dictionary<string, object?> item)
    {
        if (!string.Equals(TextValue(item, "store_kind"), "session_event_content", StringComparison.Ordinal)
            || !TableExists(connection, "local_workspace_content_tombstones", transaction))
            return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM local_workspace_content_tombstones
            WHERE store_kind='session_event_content'
              AND source_item_id=$source_item_id
              AND retention_item_id=$retention_item_id;
            """;
        command.Parameters.AddWithValue("$source_item_id", TextValue(item, "source_item_id"));
        command.Parameters.AddWithValue("$retention_item_id", TextValue(item, "item_id"));
        command.ExecuteNonQuery();
    }

    private static bool ExactValuesEqual(
        IReadOnlyDictionary<string, object?> current,
        IReadOnlyDictionary<string, object?> staged,
        params string[] columns)
    {
        foreach (var column in columns)
        {
            if (!current.TryGetValue(column, out var currentValue)
                || !staged.TryGetValue(column, out var stagedValue)
                || !ValuesEqual(currentValue, stagedValue))
                return false;
        }
        return true;
    }

    private static bool AuditReceiptMatches(Dictionary<string, object?> audit, Dictionary<string, object?> receipt)
    {
        static string Text(Dictionary<string, object?> row, string key) => Convert.ToString(row[key], CultureInfo.InvariantCulture) ?? string.Empty;
        var targetKind = Text(audit, "target_kind");
        return Text(audit, "operation_id") == Text(receipt, "operation_id")
            && targetKind == Text(receipt, "target_kind")
            && Text(audit, "target_id") == Text(receipt, "target_id")
            && Text(audit, "operation") == Text(receipt, "operation")
            && Text(audit, "expected_version") == Text(receipt, "expected_version")
            && Text(audit, "result_version") == Text(receipt, "result_version")
            && Text(audit, "target_item_set_digest") == Text(receipt, "target_item_set_digest")
            && Text(audit, "completion_code") == Text(receipt, "completion_code")
            && Text(audit, "occurred_at") == Text(receipt, "completed_at")
            && Text(receipt, "scope") == (targetKind == "session" ? "session_items" : "single_item");
    }

    private static bool ReceiptRowsEquivalent(Dictionary<string, object?> left, Dictionary<string, object?> right) =>
        left.Keys.SequenceEqual(right.Keys, StringComparer.Ordinal)
        && left.Where(item => item.Key != "last_replayed_at")
            .All(item => right.TryGetValue(item.Key, out var value) && ValuesEqual(item.Value, value));

    private static string RowFingerprint(Dictionary<string, object?> row)
    {
        var builder = new StringBuilder();
        foreach (var item in row)
        {
            builder.Append(item.Key.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(item.Key).Append('=');
            switch (item.Value)
            {
                case null: builder.Append("null"); break;
                case byte[] bytes: builder.Append("blob:").Append(Convert.ToBase64String(bytes)); break;
                case string value: builder.Append("text:").Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value); break;
                case IFormattable value: builder.Append("value:").Append(value.ToString(null, CultureInfo.InvariantCulture)); break;
                default: throw new InvalidCastException("Unsupported SQLite value type in retention proof.");
            }
            builder.Append('\n');
        }
        return HashText(builder.ToString());
    }

    private void MigrateStaging(
        string path,
        IReadOnlyDictionary<string, int> versions,
        bool restorableSafetyCopy,
        string sourceArchiveWriterVersion,
        ISkillRegistryGenerationAuthority? capturedAuthority = null,
        DateTimeOffset? localAiValidationTime = null)
    {
        if (!PreflightForMigration(
                path,
                immutableReadOnly: false,
                archiveWriterVersion: restorableSafetyCopy ? null : sourceArchiveWriterVersion,
                retentionCoverageValidation: RetentionCoverageValidation.Restorable,
                localAiValidationTime: localAiValidationTime).Success)
            throw new InvalidOperationException("Restore staging structural validation failed.");
        using var ownedAuthority = capturedAuthority is null ? CaptureSkillRegistryAuthority() : null;
        var authority = capturedAuthority ?? ownedAuthority!;
        if (HasCompleteCurrentVector(versions))
        {
            using (var connection = Open(path, SqliteOpenMode.ReadWrite))
            {
                try
                {
                    SetPragma(connection, "foreign_keys", false);
                    using var transaction = connection.BeginTransaction(deferred: false);
                    SqliteLocalRepositoryReconciliationStore.NormalizeRestoredLeases(
                        connection,
                        transaction);
                    SkillProjectionSchemaV1.NormalizeRestoredLeases(connection, transaction);
                    var terminalAuthority = LocalWorkspaceTerminalAuthority.Capture(connection, transaction);
                    terminalAuthority.ApplyTombstones(connection, transaction);
                    LocalWorkspaceProjectionStore.Refresh(
                        connection,
                        transaction,
                        timeProvider.GetUtcNow(),
                        authority);
                    terminalAuthority.ApplyReadDenied(connection, transaction);
                    LocalWorkspaceProjectionSchemaV1.ValidateAndRestampCurrent(connection, transaction);
                    EnsureForeignKeysValid(connection, transaction);
                    transaction.Commit();
                }
                finally
                {
                    SetPragma(connection, "foreign_keys", true);
                }
            }
            _ = ValidateInstalledDatabase(
                path,
                immutableReadOnly: false,
                skillRegistryAuthority: authority,
                localAiValidationTime: localAiValidationTime);
            return;
        }

        using (var connection = Open(path, SqliteOpenMode.ReadWrite))
        {
            try
            {
                SetPragma(connection, "foreign_keys", false);
                using var transaction = connection.BeginTransaction(deferred: false);
                var terminalAuthority = LocalWorkspaceTerminalAuthority.Capture(connection, transaction);
                MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
                MonitorSchemaMigrator.EnsureProjectionDispositionSchema(connection, transaction);
                MonitorSchemaMigrator.EnsureRuntimeStateSchema(connection, transaction);
                CompleteComponentMigration("monitor", versions);
                SqliteSessionStore.InitializeSchema(connection, transaction, timeProvider.GetUtcNow());
                CompleteComponentMigration("session", versions);
                LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
                if (versions.ContainsKey("local_repository_catalog"))
                    SqliteLocalRepositoryReconciliationStore.NormalizeRestoredLeases(connection, transaction);
                CompleteComponentMigration("local_repository_catalog", versions);
                LocalArchiveSchemaV1.Ensure(connection, transaction);
                CompleteComponentMigration("local_archive", versions);
                if (versions.TryGetValue("retention", out var retentionVersion)
                    && retentionVersion == SupportedComponents["retention"])
                {
                    RetentionCatalogStore.ValidateRestorableCurrentV1Authority(connection, transaction);
                }
                else
                {
                    var retention = new RetentionCatalogStore(path, timeProvider);
                    if (restorableSafetyCopy)
                        retention.InitializeForRestorableCopy(connection, transaction);
                    else
                        retention.InitializeForWrite(connection, transaction);
                }
                CompleteComponentMigration("retention", versions);
                EnsureSkillProjectionSchema(connection, transaction, versions);
                SkillProjectionSchemaV1.NormalizeRestoredLeases(connection, transaction);
                CompleteComponentMigration("skill_projection", versions);
                SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
                CompleteComponentMigration("skill_invocation_snapshot", versions);
                if (versions.TryGetValue("local_workspace_projection", out var workspaceVersion)
                    && workspaceVersion == LocalWorkspaceProjectionSchemaV1.Version)
                    terminalAuthority.ApplyTombstones(connection, transaction);
                LocalWorkspaceProjectionSchemaV1.Ensure(connection, transaction, timeProvider.GetUtcNow(), authority);
                terminalAuthority.ApplyTombstones(connection, transaction);
                terminalAuthority.ApplyReadDenied(connection, transaction);
                LocalWorkspaceProjectionSchemaV1.ValidateAndRestampCurrent(connection, transaction);
                CompleteComponentMigration("local_workspace_projection", versions);
                EnsureDoctorSchema(connection, transaction);
                CompleteComponentMigration("doctor", versions);
                EnsureAlertSchemaV2(connection, transaction);
                CompleteComponentMigration("alert_engine", versions);
                EnsureAlertLifecycleSchema(connection, transaction);
                CompleteComponentMigration("alert_lifecycle", versions);
                SqliteFirstTraceNavigationStore.EnsureSchema(connection, transaction);
                CompleteComponentMigration("first_trace_navigation", versions);
                HistoricalInstructionAnalysisSchemaV1.Ensure(connection, transaction);
                CompleteComponentMigration("historical_instruction_analysis", versions);
                SqliteHistoricalImportStore.EnsureSchema(connection, transaction);
                CompleteComponentMigration("historical_import", versions);
                SanitizedImportSchemaV1.Ensure(connection, transaction, validateForeignKeys: false);
                CompleteComponentMigration("sanitized_import", versions);
                RuntimeBackupSchemaV1.Ensure(connection, transaction);
                CompleteComponentMigration("runtime_backup", versions);
                PricingSchemaV1.Ensure(connection, transaction);
                CompleteComponentMigration("pricing", versions);
                EnsureForeignKeysValid(connection, transaction);
                transaction.Commit();
            }
            finally
            {
                SetPragma(connection, "legacy_alter_table", false);
                SetPragma(connection, "foreign_keys", true);
            }
        }
    }

    private static bool HasCompleteCurrentVector(IReadOnlyDictionary<string, int> versions) =>
        versions.Count == InstalledArchiveComponents.Count
        && InstalledArchiveComponents.All(component =>
            versions.TryGetValue(component.Key, out var version)
            && version == component.Value);

    private static void ValidateRetentionCoverage(string path, bool immutableReadOnly)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly);
        using var transaction = connection.BeginTransaction(deferred: true);
        RetentionCatalogStore.ValidateRestorableCoverage(connection, transaction);
        transaction.Rollback();
    }

    private static void EnsureDoctorSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        DoctorSchemaV1.EnsureSchemaVersionTable(connection, transaction);
        var version = DoctorSchemaV1.ReadVersion(connection, transaction);
        if (version is null)
        {
            if (DoctorSchemaV1.DoctorTablesExist(connection, transaction)) throw new InvalidOperationException();
            DoctorSchemaV1.Create(connection, transaction);
            DoctorSchemaV1.SetVersion(connection, transaction);
        }
        if (!DoctorSchemaV1.IsValid(connection, transaction)) throw new InvalidOperationException();
    }

    private static void EnsureAlertSchemaV2(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var version = AlertSchemaV1.ReadVersion(connection, transaction);
        if (version is null)
        {
            if (AlertSchemaV1.AnyEngineTableExists(connection, transaction)) throw new InvalidOperationException();
            AlertSchemaV2.Create(connection, transaction);
        }
        else if (version == AlertSchemaV1.Version)
        {
            try
            {
                SetPragma(connection, "legacy_alter_table", true);
                AlertSchemaV2.MigrateFromV1(connection, transaction);
            }
            finally
            {
                SetPragma(connection, "legacy_alter_table", false);
            }
        }
        if (!AlertSchemaV2.IsValid(connection, transaction)) throw new InvalidOperationException();
    }

    private static void EnsureAlertLifecycleSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        var version = AlertLifecycleSchemaV1.ReadVersion(connection, transaction);
        if (version is null)
        {
            if (AlertLifecycleSchemaV1.AnyObjectsExist(connection, transaction)) throw new InvalidOperationException();
            AlertLifecycleSchemaV1.Create(connection, transaction);
        }
        if (!AlertLifecycleSchemaV1.IsValid(connection, transaction)) throw new InvalidOperationException();
    }

    private static void SetPragma(SqliteConnection connection, string name, bool enabled)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA {name}={(enabled ? "ON" : "OFF")};";
        command.ExecuteNonQuery();
    }

    private static void EnsureForeignKeysValid(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidOperationException("Runtime backup migration produced invalid foreign keys.");
    }

    private CapturedSkillRegistryAuthority CaptureSkillRegistryAuthority()
    {
        var authority = skillRegistryAuthority
            ?? throw new InvalidOperationException("local_workspace_projection_authority_unavailable");
        var registry = SkillInvocationV2ArtifactRegistry.Load();
        if (!registry.TryResolveWriterVersion(archiveWriterVersion, out var expected))
            throw new InvalidOperationException("local_workspace_projection_authority_unavailable");
        return CapturedSkillRegistryAuthority.Create(
            authority,
            $"{expected.Revision}:{expected.ArtifactFingerprint}");
    }

    private void EnsureCurrentBackupTail(
        string path,
        DateTimeOffset? publicationTime = null,
        ISkillRegistryGenerationAuthority? capturedAuthority = null)
    {
        Dictionary<string, int> versions;
        using (var read = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly: false))
        {
            versions = ReadComponentVersions(read, requireSharedSchemaVersion: false);
            if (versions.TryGetValue("session", out var sessionVersion)
                && (sessionVersion != SupportedComponents["session"]
                    || !SqliteSessionStore.IsCurrentSchemaValid(read, null)))
                throw new InvalidOperationException();
        }
        if (HasCompleteCurrentVector(versions)) return;

        using var ownedAuthority = capturedAuthority is null ? CaptureSkillRegistryAuthority() : null;
        var authority = capturedAuthority ?? ownedAuthority!;
        using var connection = Open(path, SqliteOpenMode.ReadWrite);
        try
        {
            SetPragma(connection, "foreign_keys", false);
            using var transaction = connection.BeginTransaction(deferred: false);
            var terminalAuthority = LocalWorkspaceTerminalAuthority.Capture(connection, transaction);
            MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
            MonitorSchemaMigrator.EnsureProjectionDispositionSchema(connection, transaction);
            MonitorSchemaMigrator.EnsureRuntimeStateSchema(connection, transaction);
            CompleteComponentMigration("monitor", versions);
            SqliteSessionStore.InitializeSchema(connection, transaction, timeProvider.GetUtcNow());
            CompleteComponentMigration("session", versions);
            LocalRepositoryCatalogSchemaV1.Ensure(connection, transaction);
            CompleteComponentMigration("local_repository_catalog", versions);
            LocalArchiveSchemaV1.Ensure(connection, transaction);
            CompleteComponentMigration("local_archive", versions);
            if (versions.TryGetValue("retention", out var retentionVersion)
                && retentionVersion == SupportedComponents["retention"])
            {
                RetentionCatalogStore.ValidateCurrentV1Authority(connection, transaction);
            }
            else
            {
                new RetentionCatalogStore(path, timeProvider).InitializeForWrite(connection, transaction);
            }
            CompleteComponentMigration("retention", versions);
            EnsureSkillProjectionSchema(connection, transaction, versions);
            CompleteComponentMigration("skill_projection", versions);
            SkillInvocationSnapshotSchemaV1.Ensure(connection, transaction);
            CompleteComponentMigration("skill_invocation_snapshot", versions);
            if (versions.TryGetValue("local_workspace_projection", out var workspaceVersion)
                && workspaceVersion == LocalWorkspaceProjectionSchemaV1.Version)
                terminalAuthority.ApplyTombstones(connection, transaction);
            LocalWorkspaceProjectionSchemaV1.Ensure(
                connection,
                transaction,
                publicationTime ?? timeProvider.GetUtcNow(),
                authority);
            terminalAuthority.ApplyTombstones(connection, transaction);
            terminalAuthority.ApplyReadDenied(connection, transaction);
            LocalWorkspaceProjectionSchemaV1.ValidateAndRestampCurrent(connection, transaction);
            CompleteComponentMigration("local_workspace_projection", versions);
            EnsureDoctorSchema(connection, transaction);
            CompleteComponentMigration("doctor", versions);
            EnsureAlertSchemaV2(connection, transaction);
            CompleteComponentMigration("alert_engine", versions);
            EnsureAlertLifecycleSchema(connection, transaction);
            CompleteComponentMigration("alert_lifecycle", versions);
            SqliteFirstTraceNavigationStore.EnsureSchema(connection, transaction);
            CompleteComponentMigration("first_trace_navigation", versions);
            HistoricalInstructionAnalysisSchemaV1.Ensure(connection, transaction);
            CompleteComponentMigration("historical_instruction_analysis", versions);
            SqliteHistoricalImportStore.EnsureSchema(connection, transaction);
            CompleteComponentMigration("historical_import", versions);
            SanitizedImportSchemaV1.Ensure(connection, transaction, validateForeignKeys: false);
            CompleteComponentMigration("sanitized_import", versions);
            RuntimeBackupSchemaV1.Ensure(connection, transaction);
            CompleteComponentMigration("runtime_backup", versions);
            PricingSchemaV1.Ensure(connection, transaction);
            CompleteComponentMigration("pricing", versions);
            EnsureForeignKeysValid(connection, transaction);
            transaction.Commit();
        }
        finally
        {
            SetPragma(connection, "legacy_alter_table", false);
            SetPragma(connection, "foreign_keys", true);
        }
    }

    private void CompleteComponentMigration(
        string component,
        IReadOnlyDictionary<string, int> previousVersions)
    {
        if (SupportedComponents.TryGetValue(component, out var supported)
            && (!previousVersions.TryGetValue(component, out var previous) || previous < supported))
            checkpoint?.Invoke($"component-migration:{component}");
    }

    private static void EnsureSkillProjectionSchema(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyDictionary<string, int> previousVersions)
    {
        if (!previousVersions.ContainsKey("skill_projection")
            && SkillProjectionSchemaV1.HasObsoleteAuthority(connection, transaction))
        {
            SkillProjectionSchemaV1.TransitionFromObsolete(connection, transaction);
            return;
        }

        SkillProjectionSchemaV1.Ensure(connection, transaction);
    }

    private sealed class CapturedSkillRegistryAuthority :
        ISkillRegistryGenerationAuthority,
        ISkillRegistryGenerationCapture,
        IDisposable
    {
        private readonly ISkillRegistryGenerationAuthority inner;
        private readonly ISkillRegistryGenerationCapture capture;
        private readonly ISkillRegistryGenerationLease lease;
        private readonly string canonicalIdentity;
        private readonly string artifactAuthorityIdentity;
        private int disposed;

        private CapturedSkillRegistryAuthority(
            ISkillRegistryGenerationAuthority inner,
            ISkillRegistryGenerationCapture capture,
            ISkillRegistryGenerationLease lease,
            string canonicalIdentity,
            string artifactAuthorityIdentity) =>
            (this.inner, this.capture, this.lease, this.canonicalIdentity, this.artifactAuthorityIdentity) =
            (inner, capture, lease, canonicalIdentity, artifactAuthorityIdentity);

        internal static CapturedSkillRegistryAuthority Create(
            ISkillRegistryGenerationAuthority authority,
            string expectedCanonicalIdentity)
        {
            var capture = authority.CaptureGeneration()
                ?? throw new InvalidOperationException("skill_registry_generation_unavailable");
            if (!authority.TryAcquireGenerationReadLease(capture, out var lease))
                throw new InvalidOperationException("skill_registry_generation_unavailable");
            try
            {
                if (!authority.VerifyGenerationIdentity(capture, lease))
                    throw new InvalidOperationException("skill_registry_generation_not_current");
                var canonicalIdentity = authority.GetCanonicalGenerationIdentity(capture, lease);
                if (string.IsNullOrEmpty(canonicalIdentity))
                    throw new InvalidOperationException("skill_registry_generation_identity_mismatch");
                var artifactAuthorityIdentity = authority.GetCanonicalArtifactAuthorityIdentity(capture, lease);
                if (artifactAuthorityIdentity is null
                    || !string.Equals(artifactAuthorityIdentity, expectedCanonicalIdentity, StringComparison.Ordinal))
                    throw new InvalidOperationException("skill_registry_generation_identity_mismatch");
                return new(authority, capture, lease, canonicalIdentity, artifactAuthorityIdentity);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public ISkillRegistryGenerationCapture CaptureGeneration() => this;

        public bool TryAcquireGenerationReadLease(
            ISkillRegistryGenerationCapture candidate,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ISkillRegistryGenerationLease? generationLease)
        {
            if (!ReferenceEquals(candidate, this) || Volatile.Read(ref disposed) != 0)
            {
                generationLease = null;
                return false;
            }
            generationLease = new BorrowedGenerationLease(this);
            return true;
        }

        public bool VerifyGenerationIdentity(
            ISkillRegistryGenerationCapture candidate,
            ISkillRegistryGenerationLease generationLease) =>
            ReferenceEquals(candidate, this)
            && generationLease is BorrowedGenerationLease borrowed
            && ReferenceEquals(borrowed.Owner, this)
            && Volatile.Read(ref disposed) == 0;

        public string GetCanonicalGenerationIdentity(
            ISkillRegistryGenerationCapture candidate,
            ISkillRegistryGenerationLease generationLease)
        {
            if (!VerifyGenerationIdentity(candidate, generationLease))
                throw new InvalidOperationException("skill_registry_generation_not_current");
            return canonicalIdentity;
        }

        public string? GetCanonicalArtifactAuthorityIdentity(
            ISkillRegistryGenerationCapture candidate,
            ISkillRegistryGenerationLease generationLease) =>
            VerifyGenerationIdentity(candidate, generationLease) ? artifactAuthorityIdentity : null;

        public bool IsProducerTupleAccepted(
            ISkillRegistryGenerationLease generationLease,
            SkillRegistryProducerTuple tuple) =>
            generationLease is BorrowedGenerationLease borrowed
            && ReferenceEquals(borrowed.Owner, this)
            && Volatile.Read(ref disposed) == 0
            && inner.IsProducerTupleAccepted(lease, tuple);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
                lease.Dispose();
        }

        private sealed class BorrowedGenerationLease(CapturedSkillRegistryAuthority owner) : ISkillRegistryGenerationLease
        {
            internal CapturedSkillRegistryAuthority Owner { get; } = owner;
            public void Dispose() { }
        }
    }

    private bool TryAppendReceipt(
        string path,
        string kind,
        string digest,
        string code,
        int count,
        bool preRestore,
        string? operationId = null,
        Action? afterInsert = null)
    {
        try { using var connection = Open(path, SqliteOpenMode.ReadWrite); using var transaction = connection.BeginTransaction(deferred: false); RuntimeBackupSchemaV1.Ensure(connection, transaction); RuntimeBackupSchemaV1.AppendReceipt(connection, transaction, kind, digest, code, timeProvider.GetUtcNow(), count, preRestore, operationId); afterInsert?.Invoke(); transaction.Commit(); return true; }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException || IsIo(exception)) { return false; }
    }

    private FileStream? TryAcquireOfflineOwnership(string path)
    {
        try
        {
            if (!IsRegularControlFile(path) || HasLiveMonitorState(path) || PathEntryExists(path + "-journal")) return null;
            checkpoint?.Invoke(RuntimeBackupCheckpoints.BeforeOfflineCheckpoint);
            if (HasActiveSqliteSidecar(path))
            {
                using var connection = Open(path, SqliteOpenMode.ReadWrite);
                using (var wal = connection.CreateCommand())
                {
                    wal.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    using var reader = wal.ExecuteReader();
                    if (!reader.Read() || reader.GetInt32(0) != 0 || reader.Read()) return null;
                }
            }
            checkpoint?.Invoke(RuntimeBackupCheckpoints.AfterOfflineCheckpoint);
            if (HasActiveSqliteSidecar(path)) return null;
            using (var probe = Open(path, SqliteOpenMode.ReadWrite))
            using (var command = probe.CreateCommand())
            {
                command.CommandText = "PRAGMA query_only=ON; SELECT COUNT(*) FROM sqlite_schema;";
                _ = command.ExecuteScalar();
            }
            if (HasActiveSqliteSidecar(path)) return null;
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                if (RuntimeBackupNativePathClassifier.Read(exclusive.SafeFileHandle) != RuntimeBackupNativePathKind.RegularFile)
                    return null;
            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!IsRegularControlFile(path) || HasLiveMonitorState(path) || HasActiveSqliteSidecar(path))
            {
                guard.Dispose();
                return null;
            }
            return guard;
        }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static FileStream? TryAcquireRecoveryGuard(string path)
    {
        try
        {
            if (!IsRegularControlFile(path) || HasLiveMonitorState(path) || HasActiveSqliteSidecar(path)) return null;
            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!IsRegularControlFile(path) || HasLiveMonitorState(path) || HasActiveSqliteSidecar(path))
            {
                guard.Dispose();
                return null;
            }
            return guard;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static bool HasActiveSqliteSidecar(string path) => RestoreControlSidecars(path).Any(PathEntryExists);

    private FileStream? TryAcquireStartupRecoveryGuard(string path)
    {
        try
        {
            if (!IsRegularControlFile(path) || HasLiveMonitorState(path)) return null;
            var guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (!IsRegularControlFile(path) || HasLiveMonitorState(path) || !TryRemoveEmptyReadSidecars(path))
            {
                guard.Dispose();
                return null;
            }
            return guard;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private bool TryRemoveEmptyReadSidecars(string path)
    {
        try
        {
            var journal = path + "-journal";
            var wal = path + "-wal";
            var sharedMemory = path + "-shm";
            var hasWal = PathEntryExists(wal);
            var hasSharedMemory = PathEntryExists(sharedMemory);
            if (PathEntryExists(journal)) return false;
            if (hasWal != hasSharedMemory) return false;
            if (hasWal && (!IsRegularControlFile(wal) || new FileInfo(wal).Length != 0)) return false;
            if (hasSharedMemory
                && (!hasWal || !IsRegularControlFile(sharedMemory) || new FileInfo(sharedMemory).Length != 32 * 1024)) return false;
            if (!OperatingSystem.IsWindows() && hasWal) return false;
            checkpoint?.Invoke(BeforeEmptyReadSidecarOwnershipCheckpoint);
            using (var walOwnership = hasWal ? TryAcquireSidecarOwnership(wal, expectedLength: 0) : null)
            using (var sharedMemoryOwnership = hasSharedMemory ? TryAcquireSidecarOwnership(sharedMemory, expectedLength: 32 * 1024) : null)
            {
                if ((hasWal && walOwnership is null) || (hasSharedMemory && sharedMemoryOwnership is null)) return false;
                checkpoint?.Invoke(BeforeEmptyReadSidecarDeleteCheckpoint);
                if (sharedMemoryOwnership is not null && !TryMarkOwnedSidecarForDeletion(sharedMemoryOwnership)) return false;
                if (walOwnership is not null && !TryMarkOwnedSidecarForDeletion(walOwnership)) return false;
            }
            return !HasActiveSqliteSidecar(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static FileStream? TryAcquireSidecarOwnership(string path, long expectedLength)
    {
        try
        {
            var ownership = OpenWindowsSidecarOwnership(path);
            if (ownership is null) return null;
            if (RuntimeBackupNativePathClassifier.Read(ownership.SafeFileHandle) == RuntimeBackupNativePathKind.RegularFile
                && ownership.Length == expectedLength)
                return ownership;
            ownership.Dispose();
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static FileStream? OpenWindowsSidecarOwnership(string path)
    {
        var handle = CreateFileForSidecarDeletion(
            path,
            desiredAccess: WindowsSidecarDeleteAccess,
            shareMode: 0,
            securityAttributes: IntPtr.Zero,
            creationDisposition: WindowsOpenExisting,
            flagsAndAttributes: WindowsNormalFileAttributes | WindowsOpenReparsePoint,
            templateFile: IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }
        return new FileStream(handle, FileAccess.ReadWrite);
    }

    private static bool TryMarkOwnedSidecarForDeletion(FileStream ownership)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        return SetFileInformationByHandle(
            ownership.SafeFileHandle,
            fileInformationClass: WindowsFileDispositionInformationClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInformation>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.U1)]
        internal bool DeleteFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileForSidecarDeletion(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private static FileStream? TryAcquireRestoreLease(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (PathEntryExists(path) && !IsRegularControlFile(path)) return null;
            var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (RuntimeBackupNativePathClassifier.Read(lease.SafeFileHandle) != RuntimeBackupNativePathKind.RegularFile)
            {
                lease.Dispose();
                return null;
            }
            return lease;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static bool HasLiveMonitorState(string path)
    {
        var statePath = Path.Combine(Path.GetDirectoryName(path)!, "local-monitor.state.json");
        if (!PathEntryExists(statePath)) return false;
        try
        {
            var bytes = ReadBoundedControlFile(statePath, 64 * 1024);
            if (bytes is null) return true;
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = RuntimeBackupLimits.MaximumJsonDepth });
            if (!document.RootElement.TryGetProperty("process_id", out var id) || !id.TryGetInt32(out var processId)) return true;
            try { using var process = System.Diagnostics.Process.GetProcessById(processId); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }

    private static bool Rollback(
        string target,
        string? rollback,
        bool targetExisted,
        string? targetBeforeHash,
        string? stagedHash,
        out FileStream? restoredTargetGuard)
    {
        restoredTargetGuard = null;
        FileStream? candidateGuard = null;
        try
        {
            if (targetExisted)
            {
                if (rollback is null || targetBeforeHash is null || !HashEquals(rollback, targetBeforeHash)) return false;
                if (PathEntryExists(target))
                {
                    if (!IsRegularControlFile(target)) return false;
                    File.Replace(rollback, target, null, ignoreMetadataErrors: true);
                }
                else File.Move(rollback, target, overwrite: false);
                candidateGuard = TryAcquireRecoveryGuard(target);
                if (candidateGuard is null) return false;
                if (!HashEquals(target, targetBeforeHash)) return false;
                using (var validationOwner = OpenValidationSidecarOwner(target))
                    ValidateInstalledOrLegacy(target);
                restoredTargetGuard = candidateGuard;
                candidateGuard = null;
                return true;
            }
            if (PathEntryExists(target))
            {
                if (!IsRegularControlFile(target) || stagedHash is null || !HashEquals(target, stagedHash)) return false;
                File.Delete(target);
            }
            return !PathEntryExists(target);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException or RuntimeBackupException) { return false; }
        finally { candidateGuard?.Dispose(); }
    }

    private bool RecoverInterruptedRestore(string target)
    {
        var journalPath = target + ".runtime-restore-journal.json";
        var commitPath = journalPath + ".commit";
        var rollbackPath = target + ".runtime-restore-rollback";
        var reservedStages = FindReservedStageArtifacts(target);
        if (reservedStages is null) return false;
        if (RestoreControlSidecars(rollbackPath).Any(PathEntryExists)) return false;
        if (!PathEntryExists(journalPath))
            return !PathEntryExists(commitPath) && !PathEntryExists(rollbackPath) && reservedStages.Count == 0;
        if (!IsRegularControlFile(journalPath, 2048) || HasActiveSqliteSidecar(target)) return false;
        try
        {
            var journal = ReadJournal(journalPath);
            if (journal is null) return false;
            var stagePath = Path.Combine(Path.GetDirectoryName(target)!, journal.StageFileName);
            var ownedStagePaths = new[] { stagePath }.Concat(RestoreControlSidecars(stagePath)).ToHashSet(PathComparer);
            if (reservedStages.Any(path => !ownedStagePaths.Contains(path))
                || reservedStages.Any(path => !IsRegularControlFile(path))) return false;
            if (PathEntryExists(rollbackPath) && !IsRegularControlFile(rollbackPath)) return false;
            RestoreJournal? pending = null;
            if (PathEntryExists(commitPath))
            {
                if (!IsRegularControlFile(commitPath, 2048)) return false;
                pending = ReadJournal(commitPath);
                if (pending is null || !ValidJournalTransition(journal, pending)) return false;
            }

            var committedJournal = journal.Phase == "committed" ? journal : pending?.Phase == "committed" ? pending : null;
            if (committedJournal is not null)
            {
                if (journal.Phase != "committed")
                {
                    CommitJournalCandidate(journalPath);
                    checkpoint?.Invoke(RuntimeBackupCheckpoints.AfterRecoveryCommitPromoted);
                }
                if (PathEntryExists(stagePath)) return false;
                if (!DeleteOwnedStage(stagePath)) return false;
                if (ValidateCommittedTarget(target, stagePath, committedJournal))
                {
                    if (committedJournal.TargetExisted && PathEntryExists(rollbackPath))
                    {
                        if (!HashEquals(rollbackPath, committedJournal.RollbackSha256!)) return false;
                        if (!DeleteOwnedFile(rollbackPath)) return false;
                    }
                    else if (!committedJournal.TargetExisted && PathEntryExists(rollbackPath)) return false;
                    if (!DeleteOwnedFile(commitPath) || !DeleteOwnedFile(journalPath)) return false;
                    return true;
                }
                if (committedJournal.TargetExisted && HashEquals(rollbackPath, committedJournal.RollbackSha256!))
                {
                    if (!Rollback(target, rollbackPath, true, committedJournal.TargetBeforeSha256, committedJournal.StagedSha256, out var restoredTargetGuard)) return false;
                    using (restoredTargetGuard)
                        if (!DeleteOwnedStage(stagePath) || !DeleteOwnedFile(commitPath) || !DeleteOwnedFile(journalPath)) return false;
                    return true;
                }
                if (committedJournal.TargetExisted && !PathEntryExists(rollbackPath) && ValidateRestoredTarget(target, committedJournal))
                {
                    if (!DeleteOwnedStage(stagePath) || !DeleteOwnedFile(commitPath) || !DeleteOwnedFile(journalPath)) return false;
                    return true;
                }
                return false;
            }

            if (journal.Phase == "staging")
            {
                if (PathEntryExists(rollbackPath) || !TargetStillUnswapped(target, journal)) return false;
                if (!DeleteOwnedStage(stagePath) || !DeleteOwnedFile(commitPath) || !DeleteOwnedFile(journalPath)) return false;
                return true;
            }

            if (journal.StagedSha256 is null) return false;
            FileStream? restoredPreCommitGuard = null;
            if (journal.TargetExisted)
            {
                if (HashEquals(target, journal.TargetBeforeSha256!) && !PathEntryExists(rollbackPath))
                {
                    if (!ValidateRestoredTarget(target, journal)
                        || PathEntryExists(stagePath) && (!HashEquals(stagePath, journal.StagedSha256) || !DeleteOwnedStage(stagePath))) return false;
                }
                else
                {
                    if (PathEntryExists(stagePath) || (PathEntryExists(target) && !HashEquals(target, journal.StagedSha256))
                        || !HashEquals(rollbackPath, journal.RollbackSha256!)) return false;
                    if (!Rollback(target, rollbackPath, true, journal.TargetBeforeSha256, journal.StagedSha256, out restoredPreCommitGuard)) return false;
                }
            }
            else
            {
                if (PathEntryExists(rollbackPath)) return false;
                if (!PathEntryExists(target))
                {
                    if (PathEntryExists(stagePath) && (!HashEquals(stagePath, journal.StagedSha256) || !DeleteOwnedStage(stagePath))) return false;
                }
                else
                {
                    if (PathEntryExists(stagePath) || !HashEquals(target, journal.StagedSha256)) return false;
                    if (!Rollback(target, null, false, null, journal.StagedSha256, out restoredPreCommitGuard)) return false;
                }
            }
            using (restoredPreCommitGuard)
                if (!DeleteOwnedStage(stagePath) || !DeleteOwnedFile(commitPath) || !DeleteOwnedFile(journalPath)) return false;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or SqliteException or RuntimeBackupException)
        {
            return false;
        }
    }

    private static bool HasMatchingRestoreReceipt(
        string path,
        string operationId,
        string archiveHash,
        bool immutableReadOnly)
    {
        try
        {
            using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly);
            if (!RuntimeBackupSchemaV1.IsValid(connection, null)) return false;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM runtime_backup_receipts WHERE operation_id=$id AND operation_kind='restore' AND artifact_sha256=$hash AND result_code='restore_succeeded';";
            command.Parameters.AddWithValue("$id", operationId); command.Parameters.AddWithValue("$hash", archiveHash);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException) { return false; }
    }

    private static RestoreJournal? ReadJournal(string path)
    {
        var bytes = ReadBoundedControlFile(path, 2048);
        if (bytes is null or { Length: 0 }) return null;
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        var names = new[] { "schema_version", "operation_id", "phase", "archive_sha256", "stage_file_name", "target_existed", "target_before_sha256", "staged_sha256", "rollback_sha256", "installed_sha256" };
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(property => property.Name).SequenceEqual(names, StringComparer.Ordinal)) return null;
        bool TryNullableHash(string name, out string? value)
        {
            var property = root.GetProperty(name);
            if (property.ValueKind == JsonValueKind.Null)
            {
                value = null;
                return true;
            }
            if (property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
            value = null;
            return false;
        }
        var schema = root.GetProperty("schema_version").GetString(); var operation = root.GetProperty("operation_id").GetString(); var phase = root.GetProperty("phase").GetString();
        var archive = root.GetProperty("archive_sha256").GetString(); var stageFileName = root.GetProperty("stage_file_name").GetString();
        if (!TryNullableHash("staged_sha256", out var staged)
            || !TryNullableHash("target_before_sha256", out var before)
            || !TryNullableHash("rollback_sha256", out var rollback)
            || !TryNullableHash("installed_sha256", out var installed)) return null;
        if (schema != "runtime-restore-journal.v2" || operation is null || !Guid.TryParseExact(operation, "D", out var operationGuid) || operation != operationGuid.ToString("D")
            || stageFileName != $".runtime-restore-stage-{operationGuid:N}.sqlite"
            || phase is not ("staging" or "prepared" or "installed" or "committed") || !ValidHash(archive)
            || staged is not null && !ValidHash(staged)
            || before is not null && !ValidHash(before) || rollback is not null && !ValidHash(rollback) || installed is not null && !ValidHash(installed)
            || root.GetProperty("target_existed").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        var targetExisted = root.GetProperty("target_existed").GetBoolean();
        if (targetExisted && (before is null || rollback is null || !FixedEquals(before, rollback))
            || !targetExisted && (before is not null || rollback is not null)
            || phase == "staging" != (staged is null)
            || phase == "committed" != (installed is not null)
            || installed is not null && !FixedEquals(installed, staged)) return null;
        return new(schema, operation, phase, archive!, stageFileName, targetExisted, before, staged, rollback, installed);
    }

    private static bool SameJournalOperation(RestoreJournal left, RestoreJournal right) =>
        left.SchemaVersion == right.SchemaVersion && left.OperationId == right.OperationId && left.ArchiveSha256 == right.ArchiveSha256
        && left.StageFileName == right.StageFileName
        && left.TargetExisted == right.TargetExisted && left.TargetBeforeSha256 == right.TargetBeforeSha256
        && left.RollbackSha256 == right.RollbackSha256;

    private static bool ValidJournalTransition(RestoreJournal current, RestoreJournal pending) =>
        SameJournalOperation(current, pending)
        && (current.Phase, pending.Phase) is ("staging", "prepared") or ("prepared", "installed") or ("installed", "committed")
        && (current.Phase == "staging" || FixedEquals(current.StagedSha256, pending.StagedSha256));

    private bool ValidateCommittedTarget(string target, string stagePath, RestoreJournal journal)
    {
        try
        {
            if (journal.InstalledSha256 is null || HasActiveSqliteSidecar(target) || PathEntryExists(stagePath)
                || !HashEquals(target, journal.InstalledSha256)) return false;
            using var validationOwner = OpenValidationSidecarOwner(target);
            if (!HasMatchingRestoreReceipt(target, journal.OperationId, journal.ArchiveSha256, immutableReadOnly: false)) return false;
            var validationAuthority = ReadCurrentWriterStructuralAuthority(
                target,
                immutableReadOnly: false);
            ValidateInstalledDatabase(
                target,
                immutableReadOnly: false,
                skillRegistryAuthority: validationAuthority);
            return true;
        }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException || IsIo(exception)) { return false; }
    }

    private static bool ValidateRestoredTarget(string target, RestoreJournal journal)
    {
        try
        {
            if (!journal.TargetExisted || journal.TargetBeforeSha256 is null || HasActiveSqliteSidecar(target)
                || !HashEquals(target, journal.TargetBeforeSha256)) return false;
            using var validationOwner = OpenValidationSidecarOwner(target);
            ValidateInstalledOrLegacy(target);
            return true;
        }
        catch (Exception exception) when (exception is RuntimeBackupException or SqliteException or InvalidOperationException || IsIo(exception)) { return false; }
    }

    private static bool TargetStillUnswapped(string target, RestoreJournal journal) => journal.TargetExisted
        ? HashEquals(target, journal.TargetBeforeSha256!)
        : !PathEntryExists(target);

    private static bool HashEquals(string path, string expected) => IsRegularControlFile(path) && FixedEquals(HashFile(path), expected);
    private static bool DeleteOwnedFile(string path)
    {
        if (!PathEntryExists(path)) return true;
        if (!IsRegularControlFile(path)) return false;
        File.Delete(path);
        return !PathEntryExists(path);
    }

    private static bool DeleteOwnedStage(string path)
    {
        foreach (var sidecar in RestoreControlSidecars(path))
            if (!DeleteOwnedFile(sidecar)) return false;
        return DeleteOwnedFile(path);
    }

    private static OwnedRuntimeBackupTransient CreateTransientOwner(string rawPath, bool sqlite)
    {
        var markerPath = rawPath + ".owner.v1";
        if (!TryFullFile(markerPath, mustExist: false, out markerPath) || PathEntryExists(markerPath))
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        FileStream? marker = null;
        try
        {
            marker = new FileStream(markerPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            marker.Write(TransientOwnerMarkerBytes);
            marker.Flush(true);
            return new OwnedRuntimeBackupTransient(rawPath, markerPath, sqlite, marker);
        }
        catch (Exception exception) when (IsIo(exception))
        {
            marker?.Dispose();
            DeleteKnownFile(markerPath);
            throw new RuntimeBackupException(RuntimeBackupErrorCodes.SnapshotStoreUnavailable);
        }
    }

    private static bool RecoverTransientFiles(string directory, string? databaseFileName)
    {
        try
        {
            var directoryKind = RuntimeBackupNativePathClassifier.Read(directory);
            if (directoryKind == RuntimeBackupNativePathKind.Missing) return true;
            if (directoryKind != RuntimeBackupNativePathKind.Directory) return false;
            var markers = new HashSet<string>(PathComparer);
            foreach (var pattern in new[]
                     {
                         ".runtime-backup-*.partial.owner.v1",
                         ".runtime-backup-inspect-*.sqlite.owner.v1",
                         ".*.online-snapshot.owner.v1",
                     })
            {
                foreach (var marker in Directory.EnumerateFileSystemEntries(directory, pattern, SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(marker);
                    var partial = name.StartsWith(".runtime-backup-", FileNameComparison)
                        && name.EndsWith(".partial.owner.v1", FileNameComparison);
                    var inspection = name.StartsWith(".runtime-backup-inspect-", FileNameComparison)
                        && name.EndsWith(".sqlite.owner.v1", FileNameComparison);
                    var snapshot = databaseFileName is not null
                        && name.StartsWith($".{databaseFileName}.", FileNameComparison)
                        && name.EndsWith(".online-snapshot.owner.v1", FileNameComparison);
                    if (!partial && !inspection && !snapshot) continue;
                    if (markers.Add(marker) && markers.Count > RuntimeBackupLimits.MaximumInventoryItems) return false;
                }
            }

            foreach (var marker in markers.Order(PathComparer))
            {
                if (!TryResolveTransientMarker(marker, directory, databaseFileName, out var rawPath, out var sqlite)
                    || !RecoverTransientMarker(marker, rawPath, sqlite)) return false;
            }
            return true;
        }
        catch (Exception exception) when (IsIo(exception))
        {
            return false;
        }
    }

    private static bool TryResolveTransientMarker(
        string markerPath,
        string directory,
        string? databaseFileName,
        out string rawPath,
        out bool sqlite)
    {
        rawPath = string.Empty;
        sqlite = false;
        var name = Path.GetFileName(markerPath);
        string? token = null;
        if (name.StartsWith(".runtime-backup-", FileNameComparison)
            && name.EndsWith(".partial.owner.v1", FileNameComparison))
        {
            token = name[".runtime-backup-".Length..^".partial.owner.v1".Length];
        }
        else if (name.StartsWith(".runtime-backup-inspect-", FileNameComparison)
                 && name.EndsWith(".sqlite.owner.v1", FileNameComparison))
        {
            token = name[".runtime-backup-inspect-".Length..^".sqlite.owner.v1".Length];
            sqlite = true;
        }
        else if (databaseFileName is not null)
        {
            var prefix = $".{databaseFileName}.";
            const string suffix = ".online-snapshot.owner.v1";
            if (name.StartsWith(prefix, FileNameComparison) && name.EndsWith(suffix, FileNameComparison))
            {
                token = name[prefix.Length..^suffix.Length];
                sqlite = true;
            }
        }
        if (token is not { Length: 32 }
            || token.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f')
                && (!OperatingSystem.IsWindows() || character is not (>= 'A' and <= 'F'))))
            return false;
        rawPath = markerPath[..^".owner.v1".Length];
        return PathEquals(Path.GetDirectoryName(rawPath)!, directory);
    }

    private static bool RecoverTransientMarker(string markerPath, string rawPath, bool sqlite)
    {
        FileStream? marker = null;
        try
        {
            if (!IsRegularControlFile(markerPath, TransientOwnerMarkerBytes.Length)) return false;
            marker = new FileStream(markerPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            if (marker.Length != TransientOwnerMarkerBytes.Length) return false;
            var bytes = new byte[TransientOwnerMarkerBytes.Length];
            marker.ReadExactly(bytes);
            if (!bytes.AsSpan().SequenceEqual(TransientOwnerMarkerBytes)) return false;
            if (sqlite && !DeleteOwnedStage(rawPath) || !sqlite && !DeleteOwnedFile(rawPath)) return false;
        }
        catch (Exception exception) when (IsIo(exception))
        {
            return false;
        }
        finally
        {
            marker?.Dispose();
        }
        try
        {
            return DeleteOwnedFile(markerPath);
        }
        catch (Exception exception) when (IsIo(exception))
        {
            return false;
        }
    }

    private static IEnumerable<string> RestoreControlSidecars(string path) => [path + "-journal", path + "-wal", path + "-shm"];

    private static IReadOnlyList<string>? FindReservedStageArtifacts(string target)
    {
        try
        {
            var directory = Path.GetDirectoryName(target)!;
            if (!Directory.Exists(directory)) return [];
            var result = new List<string>();
            var legacyStage = target + ".runtime-restore-stage";
            foreach (var knownPath in new[] { legacyStage }.Concat(RestoreControlSidecars(legacyStage)))
            {
                if (!PathEntryExists(knownPath)) continue;
                if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems) return null;
                result.Add(knownPath);
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, ".runtime-restore-stage-*", SearchOption.TopDirectoryOnly))
            {
                if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems) return null;
                result.Add(entry);
            }
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    private static bool PathEntryExists(string path)
    {
        return RuntimeBackupNativePathClassifier.Read(path) != RuntimeBackupNativePathKind.Missing;
    }

    private static bool IsRegularControlFile(string path, long? maximumLength = null)
    {
        try
        {
            if (RuntimeBackupNativePathClassifier.Read(path) != RuntimeBackupNativePathKind.RegularFile) return false;
            return maximumLength is null || new FileInfo(path).Length <= maximumLength.Value;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static byte[]? ReadBoundedControlFile(string path, int maximumLength)
    {
        if (!IsRegularControlFile(path, maximumLength)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumLength) return null;
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return stream.Position == stream.Length && stream.Length == bytes.Length ? bytes : null;
    }

    private static void ValidateInstalledOrLegacy(string path) { using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly: false); ValidateIntegrity(connection); }
    private static string DoctorCheck(string path) { using var connection = Open(path, SqliteOpenMode.ReadOnly, immutableReadOnly: false); return TableExists(connection, "doctor_verifications") ? "doctor_store_ready" : "doctor_store_not_applicable"; }

    private static IEnumerable<Dictionary<string, object?>> EnumerateRetentionRows(SqliteConnection connection, string where, SqliteTransaction transaction)
    {
        var columns = TableColumns(connection, "retention_items", transaction);
        var projection = BoundedRowProjection(columns);
        var hasCursor = false;
        var lastStore = string.Empty;
        var lastKind = string.Empty;
        var lastSource = string.Empty;
        while (true)
        {
            var page = new List<Dictionary<string, object?>>(ReconciliationPageSize);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT {projection},* FROM retention_items
                    WHERE ({where})
                      AND ($has_cursor=0
                        OR (store_instance_id COLLATE BINARY)>$last_store
                        OR ((store_instance_id COLLATE BINARY)=$last_store AND (store_kind COLLATE BINARY)>$last_kind)
                        OR ((store_instance_id COLLATE BINARY)=$last_store AND (store_kind COLLATE BINARY)=$last_kind AND (source_item_id COLLATE BINARY)>$last_source))
                    ORDER BY store_instance_id COLLATE BINARY,store_kind COLLATE BINARY,source_item_id COLLATE BINARY
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$has_cursor", hasCursor ? 1 : 0);
                command.Parameters.AddWithValue("$last_store", lastStore);
                command.Parameters.AddWithValue("$last_kind", lastKind);
                command.Parameters.AddWithValue("$last_source", lastSource);
                command.Parameters.AddWithValue("$limit", ReconciliationPageSize);
                using var reader = command.ExecuteReader();
                while (reader.Read()) page.Add(ReadBoundedRow(reader, columns));
            }
            if (page.Count == 0) yield break;
            foreach (var row in page)
            {
                var store = TextValue(row, "store_instance_id");
                var kind = TextValue(row, "store_kind");
                var source = TextValue(row, "source_item_id");
                if (hasCursor && CompareIdentityParts(lastStore, lastKind, lastSource, store, kind, source) >= 0)
                    throw new InvalidOperationException();
                lastStore = store;
                lastKind = kind;
                lastSource = source;
                hasCursor = true;
                yield return row;
            }
            if (page.Count < ReconciliationPageSize) yield break;
        }
    }

    private static IEnumerable<Dictionary<string, object?>> EnumerateRelevantAudits(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string? sessionId)
    {
        var columns = TableColumns(connection, "retention_audit_events", transaction);
        var projection = BoundedRowProjection(columns);
        var hasCursor = false;
        var lastEvent = string.Empty;
        while (true)
        {
            var page = new List<Dictionary<string, object?>>(ReconciliationPageSize);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT {projection},* FROM retention_audit_events
                    WHERE {(sessionId is null ? "target_kind='item' AND target_id=$item" : "((target_kind='item' AND target_id=$item) OR (target_kind='session' AND target_id=$session AND session_id=$session))")}
                      AND ($has_cursor=0 OR (event_id COLLATE BINARY)>$last_event)
                    ORDER BY event_id COLLATE BINARY
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$item", itemId);
                if (sessionId is not null) command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$has_cursor", hasCursor ? 1 : 0);
                command.Parameters.AddWithValue("$last_event", lastEvent);
                command.Parameters.AddWithValue("$limit", ReconciliationPageSize);
                using var reader = command.ExecuteReader();
                while (reader.Read()) page.Add(ReadBoundedRow(reader, columns));
            }
            if (page.Count == 0) yield break;
            foreach (var row in page)
            {
                var eventId = TextValue(row, "event_id");
                if (hasCursor && StringComparer.Ordinal.Compare(lastEvent, eventId) >= 0) throw new InvalidOperationException();
                lastEvent = eventId;
                hasCursor = true;
                yield return row;
            }
            if (page.Count < ReconciliationPageSize) yield break;
        }
    }

    private static IEnumerable<string> EnumerateRelevantOperationIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string? sessionId)
    {
        var hasCursor = false;
        var lastOperation = string.Empty;
        while (true)
        {
            var page = new List<string>(ReconciliationPageSize);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"""
                    SELECT DISTINCT {BoundedTextProjection("operation_id")},operation_id FROM retention_audit_events
                    WHERE {(sessionId is null ? "target_kind='item' AND target_id=$item" : "((target_kind='item' AND target_id=$item) OR (target_kind='session' AND target_id=$session AND session_id=$session))")}
                      AND ($has_cursor=0 OR (operation_id COLLATE BINARY)>$last_operation)
                    ORDER BY operation_id COLLATE BINARY
                    LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$item", itemId);
                if (sessionId is not null) command.Parameters.AddWithValue("$session", sessionId);
                command.Parameters.AddWithValue("$has_cursor", hasCursor ? 1 : 0);
                command.Parameters.AddWithValue("$last_operation", lastOperation);
                command.Parameters.AddWithValue("$limit", ReconciliationPageSize);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.GetInt64(0) != 1) throw new InvalidOperationException();
                    page.Add(reader.GetString(1));
                }
            }
            if (page.Count == 0) yield break;
            foreach (var operation in page)
            {
                if (hasCursor && StringComparer.Ordinal.Compare(lastOperation, operation) >= 0) throw new InvalidOperationException();
                lastOperation = operation;
                hasCursor = true;
                yield return operation;
            }
            if (page.Count < ReconciliationPageSize) yield break;
        }
    }

    private static int CompareIdentityParts(string leftStore, string leftKind, string leftSource, string rightStore, string rightKind, string rightSource)
    {
        var compared = StringComparer.Ordinal.Compare(leftStore, rightStore);
        if (compared != 0) return compared;
        compared = StringComparer.Ordinal.Compare(leftKind, rightKind);
        return compared != 0 ? compared : StringComparer.Ordinal.Compare(leftSource, rightSource);
    }

    private static string TextValue(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) && value is string text ? text : throw new InvalidCastException();

    private static string LoadReconciliationStoreId(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {BoundedTextProjection("store_instance_id")},store_instance_id FROM retention_store_instances WHERE id=1 LIMIT 2;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return string.Empty;
        if (reader.GetInt64(0) != 1) throw new InvalidOperationException();
        var value = reader.GetString(1);
        if (reader.Read()) throw new InvalidOperationException();
        return value;
    }

    private static string BoundedTextProjection(string column)
    {
        var quoted = QuoteIdentifier(column);
        return $"CASE WHEN typeof({quoted})='text' AND length(CAST({quoted} AS BLOB))<={RuntimeBackupLimits.MaximumReconciliationTextBytes} THEN 1 ELSE 0 END";
    }

    private static string BoundedRowProjection(IReadOnlyList<string> columns)
    {
        if (columns.Count == 0) throw new InvalidOperationException();
        var oversizedCells = new List<string>(columns.Count * 2);
        var rowSize = new List<string>(columns.Count);
        foreach (var column in columns)
        {
            var quoted = QuoteIdentifier(column);
            oversizedCells.Add($"(typeof({quoted})='text' AND length(CAST({quoted} AS BLOB))>{RuntimeBackupLimits.MaximumReconciliationTextBytes})");
            oversizedCells.Add($"(typeof({quoted})='blob' AND length({quoted})>{RuntimeBackupLimits.MaximumReconciliationBlobBytes})");
            rowSize.Add($"CASE typeof({quoted}) WHEN 'text' THEN length(CAST({quoted} AS BLOB)) WHEN 'blob' THEN length({quoted}) WHEN 'integer' THEN 8 WHEN 'real' THEN 8 ELSE 0 END");
        }
        return $"CASE WHEN ({string.Join(" OR ", oversizedCells)}) OR ({string.Join(" + ", rowSize)})>{RuntimeBackupLimits.MaximumReconciliationRowBytes} THEN 0 ELSE 1 END";
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static Dictionary<string, object?> ReadBoundedRow(SqliteDataReader reader, IReadOnlyList<string> columns)
    {
        if (reader.FieldCount != columns.Count + 1 || reader.GetInt64(0) != 1) throw new InvalidOperationException();
        var result = new Dictionary<string, object?>(columns.Count, StringComparer.Ordinal);
        for (var index = 0; index < columns.Count; index++)
        {
            var readerIndex = index + 1;
            if (!string.Equals(reader.GetName(readerIndex), columns[index], StringComparison.Ordinal)) throw new InvalidOperationException();
            result[columns[index]] = reader.IsDBNull(readerIndex) ? null : reader.GetValue(readerIndex);
        }
        return result;
    }

    private static Dictionary<string, object?>? FindRetentionRow(SqliteConnection connection, Dictionary<string, object?> row, SqliteTransaction? transaction = null)
    {
        var columns = TableColumns(connection, "retention_items", transaction);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT {BoundedRowProjection(columns)},* FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind AND source_item_id=$source LIMIT 2;";
        command.Parameters.AddWithValue("$store", row["store_instance_id"]!); command.Parameters.AddWithValue("$kind", row["store_kind"]!); command.Parameters.AddWithValue("$source", row["source_item_id"]!);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var result = ReadBoundedRow(reader, columns);
        if (reader.Read()) throw new InvalidOperationException();
        return result;
    }
    private static string Identity(Dictionary<string, object?> row) => $"{TextValue(row, "store_instance_id")}\n{TextValue(row, "store_kind")}\n{TextValue(row, "source_item_id")}";
    private static bool IsSqliteKind(Dictionary<string, object?> row) => TextValue(row, "store_kind") is "session_event_content" or "raw_record" or "analysis_run_raw";

    private static bool SourceExists(SqliteConnection connection, Dictionary<string, object?> row, SqliteTransaction? transaction = null)
    {
        var kind = Convert.ToString(row["store_kind"], CultureInfo.InvariantCulture); var source = Convert.ToString(row["source_item_id"], CultureInfo.InvariantCulture)!;
        string? sql = kind switch { "session_event_content" => "SELECT EXISTS(SELECT 1 FROM session_event_content WHERE event_id=$id);", "raw_record" => "SELECT EXISTS(SELECT 1 FROM raw_records WHERE id=$id);", "analysis_run_raw" => "SELECT EXISTS(SELECT 1 FROM monitor_analysis_runs WHERE id=$id AND (result_markdown IS NOT NULL OR error_message IS NOT NULL OR EXISTS(SELECT 1 FROM monitor_analysis_events WHERE run_id=monitor_analysis_runs.id)));", _ => null };
        if (sql is null) return false; var table = kind switch { "session_event_content" => "session_event_content", "raw_record" => "raw_records", _ => "monitor_analysis_runs" }; if (!TableExists(connection, table, transaction)) return false;
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$id", kind == "session_event_content" ? source : long.Parse(source, CultureInfo.InvariantCulture)); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static void DeleteSource(SqliteConnection connection, SqliteTransaction transaction, Dictionary<string, object?> row)
    {
        var kind = Convert.ToString(row["store_kind"], CultureInfo.InvariantCulture); var source = Convert.ToString(row["source_item_id"], CultureInfo.InvariantCulture)!;
        if (kind == "session_event_content" && TableExists(connection, "session_event_content", transaction)) Execute(connection, transaction, "DELETE FROM session_event_content WHERE event_id=$id;", source);
        else if (kind == "raw_record" && TableExists(connection, "raw_records", transaction)) Execute(connection, transaction, "DELETE FROM raw_records WHERE id=$id;", long.Parse(source, CultureInfo.InvariantCulture));
        else if (kind == "analysis_run_raw" && TableExists(connection, "monitor_analysis_runs", transaction))
        { if (TableExists(connection, "monitor_analysis_events", transaction)) Execute(connection, transaction, "DELETE FROM monitor_analysis_events WHERE run_id=$id;", long.Parse(source, CultureInfo.InvariantCulture)); Execute(connection, transaction, "UPDATE monitor_analysis_runs SET result_markdown=NULL,error_message=NULL WHERE id=$id;", long.Parse(source, CultureInfo.InvariantCulture)); }
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, object value) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.Parameters.AddWithValue("$id", value); command.ExecuteNonQuery(); }

    private static Dictionary<string, object?>? LoadGenericRow(SqliteConnection connection, string table, string key, string value, SqliteTransaction? transaction = null)
    {
        if (!TableExists(connection, table, transaction)) return null;
        var columns = TableColumns(connection, table, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {BoundedRowProjection(columns)},* FROM {QuoteIdentifier(table)} WHERE {QuoteIdentifier(key)}=$value LIMIT 2;";
        command.Parameters.AddWithValue("$value", value);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = ReadBoundedRow(reader, columns);
        if (reader.Read()) throw new InvalidOperationException();
        return row;
    }

    private static bool HasNonEquivalentKeyCollision(SqliteConnection connection, SqliteTransaction transaction, string table, string key, Dictionary<string, object?> row)
    {
        var value = Convert.ToString(row[key], CultureInfo.InvariantCulture)!;
        var existing = LoadGenericRow(connection, table, key, value, transaction);
        return existing is not null && !RowsEqual(existing, row);
    }

    private static bool RowsEqual(Dictionary<string, object?> left, Dictionary<string, object?> right) =>
        left.Keys.SequenceEqual(right.Keys, StringComparer.Ordinal)
        && left.All(item => right.TryGetValue(item.Key, out var value) && ValuesEqual(item.Value, value));

    private static bool ValuesEqual(object? left, object? right) => left is byte[] leftBytes && right is byte[] rightBytes
        ? CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes)
        : Equals(left, right);

    private static void UpsertGenericRow(SqliteConnection source, SqliteTransaction sourceTransaction, SqliteConnection destination, SqliteTransaction transaction, string table, string key, Dictionary<string, object?> row)
    {
        var sourceColumns = TableColumns(source, table, sourceTransaction); var destinationColumns = TableColumns(destination, table, transaction); if (!sourceColumns.SequenceEqual(destinationColumns, StringComparer.Ordinal) || !row.Keys.SequenceEqual(sourceColumns, StringComparer.Ordinal)) throw new InvalidOperationException();
        using var command = destination.CreateCommand(); command.Transaction = transaction;
        var updates = sourceColumns.Where(column => !string.Equals(column, key, StringComparison.Ordinal)).Select(column => $"\"{column}\"=excluded.\"{column}\"");
        command.CommandText = $"INSERT INTO \"{table}\"({string.Join(',', sourceColumns.Select(column => $"\"{column}\""))}) VALUES({string.Join(',', sourceColumns.Select((_, index) => $"$p{index}"))}) ON CONFLICT(\"{key}\") DO UPDATE SET {string.Join(',', updates)};";
        for (var i = 0; i < sourceColumns.Count; i++) command.Parameters.AddWithValue($"$p{i}", row[sourceColumns[i]] ?? DBNull.Value); command.ExecuteNonQuery();
    }
    private static List<string> TableColumns(SqliteConnection connection, string table, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT CASE WHEN typeof(name)='text' AND length(CAST(name AS BLOB)) BETWEEN 1 AND {RuntimeBackupLimits.MaximumSchemaIdentifierBytes} AND typeof(hidden)='integer' AND hidden=0 THEN 1 ELSE 0 END,name FROM pragma_table_xinfo($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            if (result.Count >= RuntimeBackupLimits.MaximumInventoryItems || reader.GetInt64(0) != 1) throw new InvalidOperationException();
            result.Add(reader.GetString(1));
        }
        return result;
    }
    private static void DeleteIfTable(SqliteConnection connection, SqliteTransaction transaction, string table, string key, string value) { if (!TableExists(connection, table, transaction)) return; using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"DELETE FROM \"{table}\" WHERE \"{key}\"=$value;"; command.Parameters.AddWithValue("$value", value); command.ExecuteNonQuery(); }

    private static void WriteEntry(ZipArchive archive, string name, Stream source) { var entry = archive.CreateEntry(name, CompressionLevel.NoCompression); entry.LastWriteTime = ArchiveTimestamp; entry.ExternalAttributes = 0; using var destination = entry.Open(); source.CopyTo(destination); }
    private static byte[] ReadBounded(ZipArchiveEntry entry, long maximum, string code) { if (entry.Length < 0 || entry.Length > maximum || entry.Length > int.MaxValue) throw new RuntimeBackupException(code); using var source = entry.Open(); using var output = new MemoryStream((int)entry.Length); CopyBounded(source, output, maximum, code); return output.ToArray(); }
    private static void CopyBounded(Stream source, Stream destination, long maximum, string code) => CopyBounded(source, destination, maximum, code, out _);
    private static void CopyBounded(Stream source, Stream destination, long maximum, string code, out uint crc32)
    {
        var buffer = new byte[81920]; long total = 0; int read; var crc = uint.MaxValue;
        while ((read = source.Read(buffer, 0, buffer.Length)) != 0)
        {
            total += read; if (total > maximum) throw new RuntimeBackupException(code);
            destination.Write(buffer, 0, read);
            for (var index = 0; index < read; index++) crc = Crc32Table[(int)((crc ^ buffer[index]) & 0xff)] ^ (crc >> 8);
        }
        crc32 = ~crc;
    }
    private static uint ComputeCrc32(ReadOnlySpan<byte> bytes) { var crc = uint.MaxValue; foreach (var value in bytes) crc = Crc32Table[(int)((crc ^ value) & 0xff)] ^ (crc >> 8); return ~crc; }
    private static uint[] BuildCrc32Table()
    {
        var table = new uint[256];
        for (var index = 0; index < table.Length; index++)
        {
            var value = (uint)index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? 0xedb88320u ^ (value >> 1) : value >> 1;
            table[index] = value;
        }
        return table;
    }
    private static bool HasSqliteHeader(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length < 100) return false;
        Span<byte> header = stackalloc byte[16];
        stream.ReadExactly(header);
        return header.SequenceEqual("SQLite format 3\0"u8);
    }
    private static string HashFile(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return HashStream(stream); }
    private static string HashStream(Stream stream) { var original = stream.CanSeek ? stream.Position : 0; var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); if (stream.CanSeek) stream.Position = original; return hash; }
    private static string HashText(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static int IncrementCount(int value) => value == int.MaxValue ? throw new InvalidOperationException() : value + 1;
    private static bool OptionalHashEquals(string? left, string? right) => left is null ? right is null : right is not null && FixedEquals(left, right);
    private static bool FixedEquals(string? left, string? right) { if (left is null || right is null || left.Length != right.Length) return false; return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(left), Encoding.ASCII.GetBytes(right)); }
    private static bool BytesEqual(object? left, object? right) => left is byte[] a && right is byte[] b && CryptographicOperations.FixedTimeEquals(a, b);

    private static SqliteConnection? OpenValidationSidecarOwner(string path)
    {
        // Unix read-only WAL connections leave sidecars after their final close.
        // Windows retains its read-sharing guard and handle-owned cleanup.
        if (OperatingSystem.IsWindows()) return null;
        var connection = Open(path, SqliteOpenMode.ReadWrite);
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA query_only=ON; SELECT COUNT(*) FROM sqlite_schema;";
            _ = command.ExecuteScalar();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnection Open(
        string path,
        SqliteOpenMode mode,
        bool immutableReadOnly = false)
    {
        var dataSource = mode == SqliteOpenMode.ReadOnly
            && immutableReadOnly
            && !RestoreControlSidecars(path).Any(PathEntryExists)
                ? new Uri(path).AbsoluteUri + "?immutable=1"
                : path;
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = mode,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }
    private static bool TableExists(SqliteConnection connection, string table, SqliteTransaction? transaction = null) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name=$name);"; command.Parameters.AddWithValue("$name", table); return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1; }
    private static bool HasRows(SqliteConnection connection, string table, string predicate, SqliteTransaction? transaction = null) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = $"SELECT EXISTS(SELECT 1 FROM {QuoteIdentifier(table)} AS candidate WHERE {predicate});"; return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1; }
    private static string? FindTableNameOrdinalIgnoreCase(SqliteConnection connection, string expected)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN typeof(name)='text' AND length(CAST(name AS BLOB)) BETWEEN 1 AND 128 THEN 1 ELSE 0 END,name FROM sqlite_schema WHERE type='table' AND name=$name COLLATE NOCASE ORDER BY name COLLATE BINARY LIMIT 2;";
        command.Parameters.AddWithValue("$name", expected);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (reader.GetInt64(0) != 1) throw new InvalidOperationException();
        var actual = reader.GetString(1);
        if (reader.Read()) throw new InvalidOperationException();
        return actual;
    }
    private static string ScalarString(SqliteConnection connection, string sql) => ScalarString(connection, null, sql);
    private static string ScalarString(SqliteConnection connection, SqliteTransaction? transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty; }
    private static long ScalarLong(SqliteConnection connection, string sql) => ScalarLong(connection, null, sql);
    private static long ScalarLong(SqliteConnection connection, SqliteTransaction? transaction, string sql) { using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
    private static bool TryFullFile(string? path, bool mustExist, out string full)
    {
        full = string.Empty;
        try
        {
            if (!IsHostNativeAbsoluteLocalPath(path)) return false;
            full = Path.GetFullPath(path!);
            if (!string.Equals(full, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || HasReparseEntry(full)) return false;
            return RuntimeBackupNativePathClassifier.Read(full) switch
            {
                RuntimeBackupNativePathKind.Missing => !mustExist,
                RuntimeBackupNativePathKind.RegularFile => true,
                _ => false,
            };
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException) { return false; }
    }

    private static bool TryPrepareOwnedDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var parent = Directory.GetParent(full)?.FullName;
            if (parent is null || HasReparseEntry(parent)) return false;
            if (RuntimeBackupNativePathClassifier.Read(full) == RuntimeBackupNativePathKind.Missing) Directory.CreateDirectory(full);
            if (HasReparseEntry(full)) return false;
            return RuntimeBackupNativePathClassifier.Read(full) == RuntimeBackupNativePathKind.Directory;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    internal static bool IsHostNativeAbsoluteLocalPath(
        string? path,
        Func<string, DriveType>? windowsDriveType = null)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl)) return false;
        if (OperatingSystem.IsWindows())
        {
            if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
                || path.StartsWith('\\') || path.StartsWith('/') || path.Length < 3
                || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/')
                || path.AsSpan(2).Contains(':') || HasWindowsReservedSegment(path)) return false;
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return false;
            var driveType = (windowsDriveType ?? RuntimeBackupNativePathClassifier.ReadWindowsDriveType)(root);
            if (driveType is not (DriveType.Removable or DriveType.Fixed or DriveType.CDRom or DriveType.Ram)) return false;
        }
        else
        {
            if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
                || path.StartsWith("\\\\", StringComparison.Ordinal)
                || path.Contains('\\')
                || path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') return false;
        }
        return Path.IsPathFullyQualified(path);
    }

    private static bool HasReparseEntry(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return true;
        var relative = path[root.Length..];
        var current = root;
        var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length; index++)
        {
            current = Path.Combine(current, segments[index]);
            var kind = RuntimeBackupNativePathClassifier.Read(current);
            if (kind == RuntimeBackupNativePathKind.Missing) return false;
            if (kind is RuntimeBackupNativePathKind.Reparse or RuntimeBackupNativePathKind.OtherOrUnavailable) return true;
            if (index < segments.Length - 1 && kind != RuntimeBackupNativePathKind.Directory) return true;
        }
        return false;
    }

    private static bool HasWindowsReservedSegment(string path)
    {
        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)) return true;
        foreach (var segment in path[root.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.EndsWith(' ') || segment.EndsWith('.')) return true;
            var separator = segment.IndexOf('.');
            var basename = separator < 0 ? segment : segment[..separator];
            if (basename.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || basename.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || basename.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || basename.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || basename.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
                || basename.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)) return true;
            if (basename.Length == 4
                && (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                    || basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                && (basename[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3')) return true;
        }
        return false;
    }
    private static bool IsReservedRestoreOutput(string output, string bundle, string database) =>
        PathEquals(output, bundle) || IsReservedDatabaseOutput(output, database);
    private static bool IsReservedTransientArtifactName(string path)
    {
        var name = Path.GetFileName(path);
        return name.StartsWith(".runtime-backup-", FileNameComparison)
                   && name.EndsWith(".partial.owner.v1", FileNameComparison)
            || name.StartsWith(".runtime-backup-inspect-", FileNameComparison)
                   && name.EndsWith(".sqlite.owner.v1", FileNameComparison)
            || name.StartsWith(".", FileNameComparison)
                   && name.EndsWith(".online-snapshot.owner.v1", FileNameComparison);
    }
    private static bool IsReservedDatabaseOutput(string output, string database) =>
        IsReservedDynamicStagePath(output, database)
        || new[]
        {
            database, database + "-journal", database + "-wal", database + "-shm", database + ".runtime-restore.lock",
            database + ".runtime-restore-stage", database + ".runtime-restore-stage-journal", database + ".runtime-restore-stage-wal", database + ".runtime-restore-stage-shm",
            database + ".runtime-restore-rollback", database + ".runtime-restore-rollback-journal", database + ".runtime-restore-rollback-wal", database + ".runtime-restore-rollback-shm",
            database + ".runtime-restore-journal.json", database + ".runtime-restore-journal.json.commit"
        }
            .Any(path => PathEquals(output, path));
    private static bool IsReservedDynamicStagePath(string output, string database) =>
        PathEquals(Path.GetDirectoryName(output)!, Path.GetDirectoryName(database)!)
        && Path.GetFileName(output).StartsWith(".runtime-restore-stage-", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool PathEquals(string left, string right) => string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static bool IsIo(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
    private static void DeleteKnownFile(string? path) { if (string.IsNullOrWhiteSpace(path)) return; try { if (PathEntryExists(path) && IsRegularControlFile(path)) File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { } }
    private static void DeleteStageFiles(string path) { DeleteKnownFile(path); foreach (var sidecar in RestoreControlSidecars(path)) DeleteKnownFile(sidecar); }
    private static bool TryDeleteStageFiles(string path)
    {
        DeleteStageFiles(path);
        return !PathEntryExists(path) && !RestoreControlSidecars(path).Any(PathEntryExists);
    }
    private static void FlushFile(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); stream.Flush(true); }
    private static void WriteJournal(string path, RestoreJournal journal) => WriteJournalFile(path, journal, FileMode.CreateNew);
    private static void ReplaceJournal(string path, RestoreJournal journal) { WriteJournalCandidate(path, journal); CommitJournalCandidate(path); }
    private static void WriteJournalCandidate(string path, RestoreJournal journal) => WriteJournalFile(path + ".commit", journal, FileMode.CreateNew);
    private static void CommitJournalCandidate(string path) { File.Replace(path + ".commit", path, null, ignoreMetadataErrors: true); FlushFile(path); }
    private static void WriteJournalFile(string path, RestoreJournal journal, FileMode mode)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = journal.SchemaVersion,
            operation_id = journal.OperationId,
            phase = journal.Phase,
            archive_sha256 = journal.ArchiveSha256,
            stage_file_name = journal.StageFileName,
            target_existed = journal.TargetExisted,
            target_before_sha256 = journal.TargetBeforeSha256,
            staged_sha256 = journal.StagedSha256,
            rollback_sha256 = journal.RollbackSha256,
            installed_sha256 = journal.InstalledSha256,
        });
        if (bytes.Length > 2048) throw new RuntimeBackupException(RuntimeBackupErrorCodes.RestoreFailed);
        using var stream = new FileStream(path, mode, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true);
    }
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static RuntimeBackupCreateResult CreateFailure(string code) => new(false, code, null, RuntimeBackupContractVersions.BundleSchema, RuntimeBackupContractVersions.BundleProfile, null, RuntimeBackupWarnings.All);
    private static RuntimeBackupInspectionResult InspectionFailure(string code) => new(false, code);
    private static RuntimeRestorePreview PreviewFailure(string code, string? hash = null) => new(false, code, false, false, true, true, 0, null, 0, null, false, new Dictionary<string, int>(), new Dictionary<string, int>(), [], RuntimeBackupWarnings.All, hash) { CompatibilityReason = code };
    private static RuntimeRestoreResult RestoreFailure(string code, string? hash = null, RetentionComparison? comparison = null, RuntimeBackupCreateResult? preRestore = null) => new(false, code, RuntimeBackupContractVersions.RestoreResult, hash, preRestore?.Success == true, preRestore?.ArchiveSha256, preRestore?.PublishedFileName, comparison?.TerminalCount ?? 0, comparison?.NonTerminalCount ?? 0, "not_ready", "not_checked", RuntimeBackupWarnings.All);

    private static void ValidateStoredArchive(FileStream stream, int expectedEntries)
    {
        const uint endSignature = 0x06054b50, centralSignature = 0x02014b50, localSignature = 0x04034b50;
        if (stream.Length < 22) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        var endOffset = stream.Length - 22; var endBytes = ReadAt(stream, endOffset, 22); var end = endBytes.AsSpan();
        if (BinaryPrimitives.ReadUInt32LittleEndian(end) != endSignature || BinaryPrimitives.ReadUInt16LittleEndian(end[20..22]) != 0) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        var total = BinaryPrimitives.ReadUInt16LittleEndian(end[10..12]); var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(end[12..16]); var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(end[16..20]);
        if (BinaryPrimitives.ReadUInt16LittleEndian(end[4..6]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(end[6..8]) != 0
            || total != expectedEntries
            || BinaryPrimitives.ReadUInt16LittleEndian(end[8..10]) != expectedEntries
            || (long)centralOffset + centralSize != endOffset) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        long cursor = centralOffset; long expectedLocal = 0;
        for (var index = 0; index < expectedEntries; index++)
        {
            if (cursor > endOffset - 46) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            var centralBytes = ReadAt(stream, cursor, 46); var central = centralBytes.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(central) != centralSignature) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            long localOffset = BinaryPrimitives.ReadUInt32LittleEndian(central[42..46]);
            if (localOffset != expectedLocal || localOffset > stream.Length - 30) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            var localBytes = ReadAt(stream, localOffset, 30); var local = localBytes.AsSpan();
            if (BinaryPrimitives.ReadUInt32LittleEndian(local) != localSignature) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(central[8..10]); var method = BinaryPrimitives.ReadUInt16LittleEndian(central[10..12]);
            if (method != 0 || BinaryPrimitives.ReadUInt16LittleEndian(local[8..10]) != 0) throw new RuntimeBackupException(RuntimeBackupErrorCodes.CompressionNotAllowed);
            if ((flags & ~0x0800) != 0 || BinaryPrimitives.ReadUInt16LittleEndian(local[6..8]) != flags) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(central[28..30]); var extra = BinaryPrimitives.ReadUInt16LittleEndian(central[30..32]); var comment = BinaryPrimitives.ReadUInt16LittleEndian(central[32..34]);
            var localName = BinaryPrimitives.ReadUInt16LittleEndian(local[26..28]); var localExtra = BinaryPrimitives.ReadUInt16LittleEndian(local[28..30]);
            if (extra != 0 || comment != 0 || localExtra != 0 || localName != nameLength
                || BinaryPrimitives.ReadUInt16LittleEndian(central[34..36]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(central[38..42]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(central[12..14]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(central[14..16]) != 0x21
                || !central[12..28].SequenceEqual(local[10..26])) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            if (!ReadAt(stream, cursor + 46, nameLength).AsSpan().SequenceEqual(ReadAt(stream, localOffset + 30, nameLength))) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
            var compressed = BinaryPrimitives.ReadUInt32LittleEndian(central[20..24]); expectedLocal = checked(localOffset + 30 + nameLength + compressed); cursor = checked(cursor + 46 + nameLength);
        }
        if (cursor != endOffset || expectedLocal != centralOffset) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
    }

    private static uint[] ReadStoredEntryCrcs(FileStream stream, int expectedEntries)
    {
        var end = ReadAt(stream, stream.Length - 22, 22).AsSpan();
        long cursor = BinaryPrimitives.ReadUInt32LittleEndian(end[16..20]);
        var result = new uint[expectedEntries];
        for (var index = 0; index < expectedEntries; index++)
        {
            var central = ReadAt(stream, cursor, 46).AsSpan();
            result[index] = BinaryPrimitives.ReadUInt32LittleEndian(central[16..20]);
            cursor += 46
                + BinaryPrimitives.ReadUInt16LittleEndian(central[28..30])
                + BinaryPrimitives.ReadUInt16LittleEndian(central[30..32])
                + BinaryPrimitives.ReadUInt16LittleEndian(central[32..34]);
        }
        return result;
    }

    private static byte[] ReadAt(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > stream.Length - count) throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid);
        stream.Position = offset;
        var bytes = new byte[count];
        try { stream.ReadExactly(bytes); }
        catch (EndOfStreamException) { throw new RuntimeBackupException(RuntimeBackupErrorCodes.ArchiveInvalid); }
        return bytes;
    }

    private sealed class OwnedRuntimeBackupTransient
    {
        private readonly string rawPath;
        private readonly bool sqlite;
        private FileStream? markerStream;

        internal OwnedRuntimeBackupTransient(string rawPath, string markerPath, bool sqlite, FileStream marker)
        {
            this.rawPath = rawPath;
            this.sqlite = sqlite;
            markerStream = marker;
            MarkerPath = markerPath;
        }

        internal string MarkerPath { get; }

        internal bool Cleanup()
        {
            if (markerStream is null) return RecoverTransientMarker(MarkerPath, rawPath, sqlite);
            var cleaned = false;
            try
            {
                cleaned = sqlite ? DeleteOwnedStage(rawPath) : DeleteOwnedFile(rawPath);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                cleaned = false;
            }
            markerStream.Dispose();
            markerStream = null;
            if (!cleaned) return false;
            try
            {
                return DeleteOwnedFile(MarkerPath);
            }
            catch (Exception exception) when (IsIo(exception))
            {
                return false;
            }
        }
    }

    private sealed class InspectionContext(
        string stagePath,
        RuntimeBackupInspectionResult result,
        RuntimeBackupManifestData manifest,
        OwnedRuntimeBackupTransient? owner)
    {
        private bool cleaned;
        internal string StagePath { get; } = stagePath;
        internal RuntimeBackupInspectionResult Result { get; } = result;
        internal RuntimeBackupManifestData Manifest { get; } = manifest;
        internal bool Cleanup()
        {
            if (cleaned) return true;
            var success = owner?.Cleanup() ?? DeleteOwnedStage(StagePath);
            if (success) cleaned = true;
            return success;
        }
    }
    private sealed record RestoreJournal(
        string SchemaVersion,
        string OperationId,
        string Phase,
        string ArchiveSha256,
        string StageFileName,
        bool TargetExisted,
        string? TargetBeforeSha256,
        string? StagedSha256,
        string? RollbackSha256,
        string? InstalledSha256);
    private sealed record InstalledDatabaseFacts(
        IReadOnlyDictionary<string, long?> ProjectionCursors,
        RuntimeBackupRetentionSummary Retention);
    private sealed class OrderedDigestAccumulator : IDisposable
    {
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool hasValue;
        private bool completed;

        internal void Append(string value)
        {
            if (completed) throw new InvalidOperationException();
            if (hasValue) hash.AppendData("\n"u8);
            hash.AppendData(Encoding.UTF8.GetBytes(value));
            hasValue = true;
        }

        internal string? Complete()
        {
            if (completed) throw new InvalidOperationException();
            completed = true;
            return hasValue ? Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() : null;
        }

        public void Dispose() => hash.Dispose();
    }
    private sealed record RetentionComparison(
        bool Success,
        string? ErrorCode,
        int TerminalCount,
        string? TerminalDigest,
        int NonTerminalCount,
        string? ComparisonDigest,
        string? ConfirmationDigest)
    {
        internal static RetentionComparison Empty { get; } = new(true, null, 0, null, 0, null, null);
        internal static RetentionComparison Failure(string code) => new(false, code, 0, null, 0, null, null);
    }
}

using System.Text.Json.Serialization;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

public static class RuntimeBackupContractVersions
{
    public const string BundleSchema = "local-runtime-backup.v1";
    public const string BundleProfile = "local-runtime-backup";
    public const string Manifest = "local-runtime-backup-manifest.v1";
    public const string CanonicalJson = "local-runtime-backup-canonical-json.v1";
    public const string Archive = "local-runtime-backup-zip-store.v1";
    public const string Checksum = "sha256.v1";
    public const string RestorePreview = "local-runtime-restore-preview.v1";
    public const string RestoreResult = "local-runtime-restore-result.v1";
}

public static class RuntimeBackupLimits
{
    public const int MaximumManifestBytes = 1024 * 1024;
    public const long MaximumDatabaseBytes = 512L * 1024 * 1024;
    public const long MaximumArchiveBytes = MaximumDatabaseBytes + MaximumManifestBytes;
    public const int MaximumInventoryItems = 256;
    public const int MaximumJsonDepth = 32;
    public const int MaximumReconciliationTextBytes = 1024 * 1024;
    public const int MaximumReconciliationBlobBytes = 1024 * 1024;
    public const int MaximumReconciliationRowBytes = 2 * 1024 * 1024;
    public const int MaximumRetentionPreflightTextBytes = 1024;
    public const int MaximumSchemaIdentifierBytes = 512;
    public const int MaximumSchemaDefinitionBytes = 64 * 1024;
    public const int MaximumSchemaObjects = 1024;
}

public static class RuntimeBackupWarnings
{
    public const string RawContentIncluded = "raw_content_included";
    public const string NotRepositorySafe = "not_repository_safe";
    public const string RetentionBackupNotPurged = "retention_backup_not_purged";
    public static IReadOnlyList<string> All { get; } = [RawContentIncluded, NotRepositorySafe, RetentionBackupNotPurged];
}

public static class RuntimeBackupErrorCodes
{
    public const string InvalidArguments = "invalid_arguments";
    public const string ArchiveInvalid = "archive_invalid";
    public const string ArchiveAttributesInvalid = "archive_attributes_invalid";
    public const string ArchiveTimestampInvalid = "archive_timestamp_invalid";
    public const string CompressionNotAllowed = "compression_not_allowed";
    public const string DuplicateEntry = "duplicate_entry";
    public const string UnexpectedEntry = "unexpected_entry";
    public const string ManifestInvalid = "manifest_invalid";
    public const string ManifestNotCanonical = "manifest_not_canonical";
    public const string ChecksumMismatch = "checksum_mismatch";
    public const string BundleTooLarge = "bundle_too_large";
    public const string DatabaseTooLarge = "database_too_large";
    public const string SnapshotStoreBusy = "snapshot_store_busy";
    public const string SnapshotStoreUnavailable = "snapshot_store_unavailable";
    public const string OutputExists = "output_exists";
    public const string PublishFailed = "publish_failed";
    public const string ExternalRawStoreActive = "external_raw_store_active";
    public const string ExternalRuntimeStateActive = "external_runtime_state_active";
    public const string ExternalRuntimeStateUnsafe = "external_runtime_state_unsafe";
    public const string ExternalRuntimeStateUnknown = "external_runtime_state_unknown";
    public const string RestoreIncompatible = "restore_incompatible";
    public const string RestoreResurrectionBlocked = "restore_resurrection_blocked";
    public const string RestoreTombstoneReconcileFailed = "restore_tombstone_reconcile_failed";
    public const string MonitorMustBeStopped = "monitor_must_be_stopped";
    public const string RestoreRolledBack = "restore_rolled_back";
    public const string RestoreRollbackFailed = "restore_rollback_failed";
    public const string RestoreFailed = "restore_failed";
}

public static class RuntimeBackupCheckpoints
{
    public const string BeforeOnlineSnapshot = "before-online-snapshot";
    public const string AfterOnlineSnapshot = "after-online-snapshot";
    public const string BeforeOfflineCheckpoint = "before-offline-checkpoint";
    public const string AfterOfflineCheckpoint = "after-offline-checkpoint";
    public const string AfterOwnerJournal = "after-owner-journal";
    public const string AfterArchiveExtracted = "after-archive-extracted";
    public const string AfterMigration = "after-migration";
    public const string DuringStageReceipt = "during-stage-receipt";
    public const string AfterStageValidated = "after-stage-validated";
    public const string BeforeSwap = "before-swap";
    public const string AfterSwap = "after-swap";
    public const string AfterInstalledValidation = "after-installed-validation";
    public const string AfterReceiptPersisted = "after-receipt-persisted";
    public const string AfterInstalledJournalCandidateFlushed = "after-installed-journal-candidate-flushed";
    public const string AfterCommittedJournalCandidateFlushed = "after-committed-journal-candidate-flushed";
    public const string AfterJournalCommitted = "after-journal-committed";
    public const string AfterRecoveryCommitPromoted = "after-recovery-commit-promoted";
    public const string AfterRollbackDeleted = "after-rollback-deleted";
    public const string BeforeJournalDeleted = "before-journal-deleted";
    public const string AfterJournalDeleted = "after-journal-deleted";
}

public sealed record RuntimeBackupCreateResult(
    bool Success,
    string? ErrorCode,
    string? ArchiveSha256,
    string BundleSchemaVersion,
    string BundleProfile,
    string? PublishedFileName,
    IReadOnlyList<string> Warnings);

public sealed record RuntimeBackupInspectionResult(
    bool Success,
    string? ErrorCode,
    string? ArchiveSha256 = null,
    string? DatabaseSha256 = null,
    string? ManifestSchemaVersion = null,
    string? BundleSchemaVersion = null,
    string? BundleProfile = null,
    [property: JsonPropertyName("component_versions")] IReadOnlyDictionary<string, int>? ComponentVersionsValue = null,
    [property: JsonPropertyName("row_counts")] IReadOnlyDictionary<string, long>? RowCountsValue = null,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string>? WarningsValue = null,
    string? SourceJournalMode = null,
    IReadOnlyDictionary<string, long?>? ProjectionCursors = null,
    RuntimeBackupRetentionSummary? Retention = null,
    IReadOnlyList<RuntimeBackupExternalState>? ExternalState = null)
{
    [JsonIgnore] public IReadOnlyDictionary<string, int> ComponentVersions => ComponentVersionsValue ?? new Dictionary<string, int>();
    [JsonIgnore] public IReadOnlyDictionary<string, long> RowCounts => RowCountsValue ?? new Dictionary<string, long>();
    [JsonIgnore] public IReadOnlyList<string> Warnings => WarningsValue ?? [];
}

public sealed record RuntimeBackupPreflightResult(
    bool Success,
    string? ErrorCode,
    IReadOnlyDictionary<string, int>? ComponentVersions = null,
    IReadOnlyList<string>? MigrationSteps = null);

public sealed record RuntimeRestorePreview(
    bool Success,
    string? ErrorCode,
    bool Compatible,
    bool OverwritesExisting,
    bool MonitorStopRequired,
    bool RestartRequired,
    int TerminalReconciliationCount,
    string? TerminalReconciliationDigest,
    int NonTerminalReintroductionCount,
    string? ConfirmationDigest,
    bool RequiresConfirmation,
    IReadOnlyDictionary<string, int> SourceComponentVersions,
    IReadOnlyDictionary<string, int> CurrentComponentVersions,
    IReadOnlyList<string> MigrationSteps,
    IReadOnlyList<string> Warnings,
    string? ArchiveSha256,
    string? DatabaseSha256 = null,
    string? SourceJournalMode = null,
    IReadOnlyDictionary<string, long>? RowCounts = null,
    IReadOnlyDictionary<string, long?>? ProjectionCursors = null,
    RuntimeBackupRetentionSummary? Retention = null,
    IReadOnlyList<RuntimeBackupExternalState>? ExternalState = null)
{
    public string SchemaVersion { get; init; } = RuntimeBackupContractVersions.RestorePreview;
    public string? CompatibilityReason { get; init; }
    [JsonPropertyName("non_terminal_reintroduction_digest")]
    public string? NonTerminalReintroductionDigest { get; init; }
    public IReadOnlyList<string> DestinationPrerequisites { get; init; } =
        ["compatible_application_installed", "monitor_stopped", "proposal_apply_inactive_configuration_only", "setup_rerun_if_present", "restart_after_restore"];
}

public sealed record RuntimeRestoreOptions(
    string? PreRestoreOutputPath = null,
    bool AllowResurrection = false,
    string? ConfirmationDigest = null);

public sealed record RuntimeRestoreResult(
    bool Success,
    string? ErrorCode,
    string SchemaVersion,
    string? ArchiveSha256,
    bool PreRestoreBackupCreated,
    string? PreRestoreBackupSha256,
    string? PreRestoreBackupFileName,
    int TerminalReconciliationCount,
    int NonTerminalReintroductionCount,
    string ReadinessCheck,
    string DoctorCheck,
    IReadOnlyList<string> Warnings);

public sealed record RuntimeBackupExternalState(string Kind, string SourceState, bool Included, string Consistency, string RestoreAction);

internal sealed record RuntimeBackupManifestData(
    DateTimeOffset CreatedAt,
    string SourceApplicationVersion,
    string SourcePlatform,
    string DatabaseSha256,
    long DatabaseSize,
    string SourceJournalMode,
    RuntimeBackupBackupWindow BackupWindow,
    IReadOnlyDictionary<string, int> ComponentVersions,
    IReadOnlyDictionary<string, long> RowCounts,
    IReadOnlyDictionary<string, long?> ProjectionCursors,
    RuntimeBackupRetentionSummary Retention,
    IReadOnlyList<RuntimeBackupExternalState> ExternalState);

internal sealed record RuntimeBackupBackupWindow(
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    IReadOnlyDictionary<string, long?> ProjectionCursorsAtStart,
    IReadOnlyDictionary<string, long?> ProjectionCursorsAtEnd);

public sealed record RuntimeBackupRetentionSummary(
    IReadOnlyDictionary<string, long> StoreKindCounts,
    IReadOnlyDictionary<string, long> StateCounts,
    long TombstoneCount,
    string? EarliestCapturedAt,
    string? LatestCapturedAt,
    string? EarliestExpiresAt,
    string? LatestExpiresAt,
    IReadOnlyList<string> Policies);

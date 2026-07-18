namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public enum RetentionItemLifecycle
{
    Expiring,
    RetainedByPolicy,
    ExpiredPendingDeletion,
    DeletionQueued,
    Deleting,
    Deleted,
    DeletionFailed
}

public enum RetentionStoreKind
{
    SessionEventContent,
    RawRecord,
    AnalysisRunRaw,
    SensitiveBundle,
    AnalysisSdkDirectory
}

public enum RetentionPolicyKind
{
    RawDefault90Days,
    SensitiveBundle7Days
}

public enum RetentionErrorCode
{
    MigrationBlocked,
    MissingTimestamp,
    InvalidIdentity,
    OwnershipMismatch,
    CaptureIncomplete,
    LeaseConflict,
    LeaseLost,
    DeleteBusy,
    DeletePermissionDenied,
    DeleteIoFailed,
    UnexpectedSourceMissing,
    MaintenanceBusy,
    ItemLimitExceeded
}

public enum RetentionWorkerDiagnosticCode { RetryExhausted, AdapterCoverageMismatch }
public enum RetentionCapturePhase { Reserved, Staging, PublishedPendingCatalog, Complete }
public enum RetentionInventoryCategory { RequiredCleanup, RetainedByPolicy, NotApplicable, Blocked }
public enum RetentionSessionV1Condition { NeverCaptured, NeverCapturedNotCaptured, NeverCapturedRedacted, NeverCapturedUnsupported, ReadableExpiring, ReadableRetainedByPolicy, DeniedLifecycle, StaleMissingOrRepairBlocked, SelectedReadableWithDeniedSibling, SelectedDeniedWithReadableSibling, CapturedWithoutReadableSibling, UnknownSession, UnknownEvent, UnknownEventWithExpiringSession, SanitizedOnly }
public enum RetentionSessionRouteOutcome { Content, Expired, SessionNotFound, HostFallback }

public sealed record RetentionItemSummary(
    string ItemId,
    RetentionStoreKind StoreKind,
    RetentionInventoryCategory InventoryCategory,
    RetentionItemLifecycle State,
    string PolicyId,
    int PolicyVersion,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ReadDeniedAt,
    DateTimeOffset? QueuedAt,
    DateTimeOffset? DeletionStartedAt,
    DateTimeOffset? DeletedAt,
    int AttemptCount,
    bool RetryExhausted,
    RetentionErrorCode? ErrorCode,
    DateTimeOffset? RetryAt);

public sealed record RetentionSessionV1TableResult(string? EventContentState, string? SessionRawRetentionState, int StatusCode, string? ContentType, byte[]? ErrorUtf8, bool RouteAbsent)
{
    public bool HasSessionDto => SessionRawRetentionState is not null;
    public bool HasEventDto => EventContentState is not null;
    public RetentionSessionRouteOutcome RouteOutcome => RouteAbsent ? RetentionSessionRouteOutcome.HostFallback : StatusCode == 410 ? RetentionSessionRouteOutcome.Expired : StatusCode == 404 ? RetentionSessionRouteOutcome.SessionNotFound : RetentionSessionRouteOutcome.Content;
}

public static class RetentionSessionV1Projection
{
    private static readonly byte[] ExpiredContentResponse = "{\"error\":\"raw_content_expired\",\"content_state\":\"expired_pending_deletion\"}"u8.ToArray();

    public static byte[] ExpiredContentResponseUtf8 => ExpiredContentResponse.ToArray();

    public static string Project(RetentionItemLifecycle? lifecycle, bool wasCaptured) =>
        !wasCaptured
            ? "not_captured"
            : lifecycle is RetentionItemLifecycle.Expiring or RetentionItemLifecycle.RetainedByPolicy
                ? "expiring"
                : "expired_pending_deletion";

    public static string ProjectCondition(RetentionSessionV1Condition condition) => condition switch
    {
        RetentionSessionV1Condition.ReadableExpiring or RetentionSessionV1Condition.ReadableRetainedByPolicy or RetentionSessionV1Condition.SelectedReadableWithDeniedSibling or RetentionSessionV1Condition.SelectedDeniedWithReadableSibling => "expiring",
        RetentionSessionV1Condition.DeniedLifecycle or RetentionSessionV1Condition.StaleMissingOrRepairBlocked or RetentionSessionV1Condition.CapturedWithoutReadableSibling => "expired_pending_deletion",
        _ => "not_captured"
    };

    public static RetentionSessionV1TableResult Describe(RetentionSessionV1Condition condition) => condition switch
    {
        RetentionSessionV1Condition.NeverCaptured or RetentionSessionV1Condition.NeverCapturedNotCaptured => new("not_captured", "not_captured", 404, "application/json", "{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), false),
        RetentionSessionV1Condition.NeverCapturedRedacted => new("redacted", "not_captured", 404, "application/json", "{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), false),
        RetentionSessionV1Condition.NeverCapturedUnsupported => new("unsupported", "not_captured", 404, "application/json", "{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), false),
        RetentionSessionV1Condition.UnknownSession => new(null, null, 404, "application/json", "{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), false),
        RetentionSessionV1Condition.UnknownEvent => new(null, "not_captured", 404, "application/json", "{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), false),
        RetentionSessionV1Condition.UnknownEventWithExpiringSession => new(null, "expiring", 404, "application/json", "{\"error\":\"session_event_content_not_found\"}"u8.ToArray(), false),
        RetentionSessionV1Condition.SanitizedOnly => new(null, null, 404, "application/json", "{\"accepted\":false,\"error\":\"unsupported_endpoint\",\"message\":\"Only /v1/traces is supported.\"}"u8.ToArray(), true),
        RetentionSessionV1Condition.ReadableExpiring or RetentionSessionV1Condition.ReadableRetainedByPolicy or RetentionSessionV1Condition.SelectedReadableWithDeniedSibling => new("available", "expiring", 200, "application/json", null, false),
        RetentionSessionV1Condition.SelectedDeniedWithReadableSibling => new("expired_pending_deletion", "expiring", 410, "application/json", ExpiredContentResponseUtf8, false),
        _ => new("expired_pending_deletion", "expired_pending_deletion", 410, "application/json", ExpiredContentResponseUtf8, false)
    };
}

public static class RetentionV1Constants
{
    public static int CatalogSchemaVersion => 1; public static int AdapterCoverageVersion => 1; public static int ExpiryScanItemLimit => 100; public static int ClaimBatchLimit => 100; public static int MaximumActiveDeletionWorkers => 2; public static int MaximumDeleteAttempts => 5; public static int MaximumFileMembers => 256; public static int StatusItemSummaryLimit => 100;
    public static long MaximumFileBytes => 128L * 1024 * 1024;
    public static string RawDefaultPolicyId => "raw-default-90d"; public static string SensitiveBundlePolicyId => "sensitive-bundle-7d";
    public static TimeSpan RawDefaultTtl => TimeSpan.FromDays(90);
    public static TimeSpan SensitiveBundleTtl => TimeSpan.FromDays(7);
    public static TimeSpan ScanElapsedBudget => TimeSpan.FromSeconds(30);
    public static TimeSpan WorkerWakeInterval => TimeSpan.FromSeconds(15);
    public static TimeSpan LeaseDuration => TimeSpan.FromMinutes(2);
    public static TimeSpan LeaseRenewalDeadline => TimeSpan.FromMinutes(1);
    public static TimeSpan ActiveOperationQuiescenceBound => TimeSpan.FromMinutes(2);
    public static TimeSpan ShutdownDrainBound => TimeSpan.FromMinutes(2);
    public static TimeSpan WalMaintenanceRetryDelay => TimeSpan.FromMinutes(1);
    public static TimeSpan[] RetryDelays => [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2)];
}

public enum RetentionLeaseKind { Access, Operation }

public sealed record RetentionOwnershipKey(string StoreInstanceId, RetentionStoreKind StoreKind, string SourceItemId);

public sealed record RetentionCatalogItem(
    string ItemId,
    RetentionOwnershipKey OwnershipKey,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    RetentionItemLifecycle State,
    long Revision,
    DateTimeOffset? ReadDeniedAt);

public sealed class RetentionMigrationBlockedException : InvalidOperationException
{
    public RetentionMigrationBlockedException() : base("retention_migration_blocked") { }
}

public sealed class RetentionReadLeaseHandle : IDisposable
{
    private readonly Action release;
    internal RetentionReadLeaseHandle(string itemId, long revision, Action release)
    {
        ItemId = itemId;
        Revision = revision;
        this.release = release;
    }

    public string ItemId { get; }
    public long Revision { get; }
    public void Dispose() => release();
}

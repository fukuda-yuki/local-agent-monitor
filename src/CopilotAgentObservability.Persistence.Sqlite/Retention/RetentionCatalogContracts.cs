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
    RetryExhausted,
    MaintenanceBusy,
    AdapterCoverageMismatch,
    ItemLimitExceeded
}

public sealed record RetentionItemSummary(
    string ItemId,
    RetentionStoreKind StoreKind,
    string InventoryCategory,
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
}

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

public enum RetentionLeaseKind { Access, Operation, Deletion }

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

public sealed class RetentionCatalogUnavailableException : InvalidOperationException
{
    public RetentionCatalogUnavailableException() : base("retention_catalog_unavailable") { }
}

public sealed class RetentionReadLeaseHandle : IDisposable
{
    private readonly Action release;
    internal RetentionReadLeaseHandle(string itemId, long revision, long generation, Action release)
    {
        ItemId = itemId;
        Revision = revision;
        Generation = generation;
        this.release = release;
    }

    public string ItemId { get; }
    public long Revision { get; }
    public long Generation { get; }
    public void Dispose() => release();
}

internal enum RetentionReadDisposition { Granted, NotFound, Denied, Busy }
internal enum RetentionReadKind { Access, Operation }

internal sealed record RetentionReadRequest(
    RetentionOwnershipKey OwnershipKey,
    RetentionReadKind LeaseKind,
    DateTimeOffset Now,
    long? ExpectedRevision);

internal sealed record RetentionReadResult<T>(RetentionReadDisposition Disposition, RetentionReadLease<T>? Lease);

internal sealed record RetentionBatchReadResult<T>(RetentionReadDisposition Disposition, RetentionBatchReadLease<T>? Lease);

internal sealed class RetentionRevisionFence
{
    private static readonly byte[] SourceTokenBindingDomain = "copilot-agent-observability/retention-analysis-fence-source-binding/v1"u8.ToArray();
    private readonly string? itemId;
    private readonly string? storeInstanceId;
    private readonly string? sourceItemId;
    private readonly long revision;
    private readonly byte[]? sourceTokenBindingDigest;
    private readonly byte[]? operationToken;

    private RetentionRevisionFence() { }
    private RetentionRevisionFence(string itemId, string storeInstanceId, long runId, long revision, byte[] sourceToken, byte[] operationToken)
    {
        this.itemId = itemId;
        this.storeInstanceId = storeInstanceId;
        sourceItemId = runId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        this.revision = revision;
        sourceTokenBindingDigest = CreateSourceTokenBindingDigest(sourceToken);
        this.operationToken = operationToken.ToArray();
    }
    internal static RetentionRevisionFence Create() => new();

    internal static RetentionRevisionFence CreateAnalysisRunRaw(
        string itemId,
        string storeInstanceId,
        long runId,
        long revision,
        byte[] sourceToken,
        byte[] operationToken) =>
        new(itemId, storeInstanceId, runId, revision, sourceToken, operationToken);

    internal bool MatchesAnalysisRunRaw(
        string expectedItemId,
        string expectedStoreInstanceId,
        long expectedRunId,
        long expectedRevision,
        byte[] expectedSourceToken,
        byte[] expectedOperationToken) =>
        string.Equals(itemId, expectedItemId, StringComparison.Ordinal)
        && string.Equals(storeInstanceId, expectedStoreInstanceId, StringComparison.Ordinal)
        && string.Equals(sourceItemId, expectedRunId.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        && revision == expectedRevision
        && sourceTokenBindingDigest is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(sourceTokenBindingDigest, CreateSourceTokenBindingDigest(expectedSourceToken))
        && operationToken is not null && operationToken.AsSpan().SequenceEqual(expectedOperationToken);

    private static byte[] CreateSourceTokenBindingDigest(byte[] sourceToken)
    {
        if (sourceToken is not { Length: 32 }) throw new ArgumentException("Source token must be 32 bytes.", nameof(sourceToken));
        using var stream = new MemoryStream();
        WriteFrame(stream, SourceTokenBindingDomain);
        WriteFrame(stream, sourceToken);
        return System.Security.Cryptography.SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length));
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }
}

internal sealed class RetentionRevisionFenceRejectedException : InvalidOperationException
{
    internal RetentionRevisionFenceRejectedException() : base("The requested analysis raw write is no longer authorized.") { }
}

internal sealed class RetentionReadLease<T> : IAsyncDisposable
{
    private readonly Func<ValueTask> release;
    private int released;

    internal RetentionReadLease(T value, RetentionRevisionFence revisionFence, Func<ValueTask> release)
    {
        Value = value;
        RevisionFence = revisionFence;
        this.release = release;
    }

    internal T Value { get; }
    internal RetentionRevisionFence RevisionFence { get; }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref released, 1) == 0 ? release() : ValueTask.CompletedTask;
}

internal sealed class RetentionBatchReadLease<T> : IAsyncDisposable
{
    private readonly Func<ValueTask> release;
    private int released;

    internal RetentionBatchReadLease(T value, RetentionRevisionFence revisionFence, Func<ValueTask> release)
    {
        Value = value;
        RevisionFence = revisionFence;
        this.release = release;
    }

    internal T Value { get; }
    internal RetentionRevisionFence RevisionFence { get; }
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref released, 1) == 0 ? release() : ValueTask.CompletedTask;
}

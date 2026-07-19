namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public interface IRetentionDeletionAdapter
{
    RetentionStoreKind StoreKind { get; }
    ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context);
}

public sealed record RetentionDeleteContext(
    string ItemId,
    string StoreInstanceId,
    RetentionStoreKind StoreKind,
    long ExpectedRevision,
    string LeaseOwner,
    long LeaseGeneration,
    RetentionSourceIdentity SourceIdentity,
    RetentionPrivateLocatorHandle? PrivateLocator,
    int IntentCursor,
    CancellationToken CancellationToken);

public sealed record RetentionSourceIdentity(string SourceItemId, string OwnershipReceipt);

public sealed record RetentionPrivateLocatorHandle(string OpaqueHandle);

public enum RetentionAdapterDisposition
{
    Deleted,
    LeaseLost,
    TransientFailure,
    TerminalFailure
}

public sealed record RetentionAdapterResult
{
    private const string InvalidCodeMessage = "retention_adapter_result_invalid_code";

    private RetentionAdapterResult(RetentionAdapterDisposition disposition, RetentionErrorCode? errorCode)
    {
        Disposition = disposition;
        ErrorCode = errorCode;
    }

    public RetentionAdapterDisposition Disposition { get; }
    public RetentionErrorCode? ErrorCode { get; }

    public static RetentionAdapterResult Deleted { get; } = new(RetentionAdapterDisposition.Deleted, null);
    public static RetentionAdapterResult LeaseLost { get; } = new(RetentionAdapterDisposition.LeaseLost, RetentionErrorCode.LeaseLost);

    public static RetentionAdapterResult TransientFailure(RetentionErrorCode code) => code switch
    {
        RetentionErrorCode.DeleteBusy or RetentionErrorCode.DeletePermissionDenied or RetentionErrorCode.DeleteIoFailed => new(RetentionAdapterDisposition.TransientFailure, code),
        _ => throw new ArgumentOutOfRangeException(nameof(code), InvalidCodeMessage)
    };

    public static RetentionAdapterResult TerminalFailure(RetentionErrorCode code) => code switch
    {
        RetentionErrorCode.InvalidIdentity or RetentionErrorCode.OwnershipMismatch or RetentionErrorCode.UnexpectedSourceMissing or RetentionErrorCode.ItemLimitExceeded => new(RetentionAdapterDisposition.TerminalFailure, code),
        _ => throw new ArgumentOutOfRangeException(nameof(code), InvalidCodeMessage)
    };
}

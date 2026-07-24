namespace CopilotAgentObservability.Alerts;

public enum AlertEngineStoreStatusV2
{
    Success,
    NotFound,
    Busy,
    Unavailable,
    Conflict,
    ContractRejected,
}

public sealed record AlertEngineStoreResultV2(
    AlertEngineStoreStatusV2 Status,
    string? Code = null);

public sealed record AlertEngineStoreReadResultV2(
    AlertEngineQueryStatus Status,
    IReadOnlyList<byte> CanonicalBytes,
    string? Code = null);

public sealed record AlertEngineStoreListResultV2(
    AlertEngineQueryStatus Status,
    IReadOnlyList<IReadOnlyList<byte>> CanonicalItems,
    string? Code = null);

public interface IAlertEngineStoreV2 : IAlertEvidenceReadViewV2
{
    AlertEngineStoreResultV2 InitializeV2();

    AlertEngineStoreResultV2 Append(AlertEvaluationResultV2 evaluation);

    AlertEngineStoreReadResultV2 GetEvaluationV2(string evaluationId);

    AlertEngineStoreReadResultV2 GetReceiptV2(string alertId);

    AlertEngineStoreListResultV2 ListSuppressionsV2(string evaluationId);
}

public enum AlertContractKind { V1, V2 }

public sealed class AlertVersionedReceiptQueryItem
{
    public AlertVersionedReceiptQueryItem(
        AlertContractKind contractVersion,
        IEnumerable<byte> canonicalBytes,
        AlertCenterReceiptProjectionV1? receiptV1,
        AlertCenterReceiptProjectionV2? receiptV2)
    {
        if (receiptV1 is null == (receiptV2 is null))
        {
            throw new ArgumentException("Exactly one receipt projection is required.");
        }
        if (contractVersion != (receiptV1 is null ? AlertContractKind.V2 : AlertContractKind.V1))
        {
            throw new ArgumentException("Receipt projection does not match contract version.");
        }
        ContractVersion = contractVersion;
        CanonicalBytes = Array.AsReadOnly(canonicalBytes.ToArray());
        ReceiptV1 = receiptV1;
        ReceiptV2 = receiptV2;
    }

    public AlertContractKind ContractVersion { get; }
    public IReadOnlyList<byte> CanonicalBytes { get; }
    public AlertCenterReceiptProjectionV1? ReceiptV1 { get; }
    public AlertCenterReceiptProjectionV2? ReceiptV2 { get; }
}

public sealed class AlertVersionedEvaluationQueryItem
{
    public AlertVersionedEvaluationQueryItem(
        AlertContractKind contractVersion,
        IEnumerable<byte> canonicalBytes,
        AlertEvaluationProjectionV1? evaluationV1,
        AlertEvaluationConsumerProjectionV2? evaluationV2)
    {
        if (evaluationV1 is null == (evaluationV2 is null))
        {
            throw new ArgumentException("Exactly one evaluation projection is required.");
        }
        if (contractVersion != (evaluationV1 is null ? AlertContractKind.V2 : AlertContractKind.V1))
        {
            throw new ArgumentException("Evaluation projection does not match contract version.");
        }
        ContractVersion = contractVersion;
        CanonicalBytes = Array.AsReadOnly(canonicalBytes.ToArray());
        EvaluationV1 = evaluationV1;
        EvaluationV2 = evaluationV2;
    }

    public AlertContractKind ContractVersion { get; }
    public IReadOnlyList<byte> CanonicalBytes { get; }
    public AlertEvaluationProjectionV1? EvaluationV1 { get; }
    public AlertEvaluationConsumerProjectionV2? EvaluationV2 { get; }
}

public sealed class AlertVersionedSuppressionQueryItem
{
    public AlertVersionedSuppressionQueryItem(
        AlertContractKind contractVersion,
        long suppressionOrdinal,
        IEnumerable<byte> canonicalBytes,
        AlertSuppressionProjectionV1? suppressionV1,
        AlertSuppressionProjectionV2? suppressionV2)
    {
        if (suppressionV1 is null == (suppressionV2 is null))
        {
            throw new ArgumentException("Exactly one suppression projection is required.");
        }
        if (contractVersion != (suppressionV1 is null ? AlertContractKind.V2 : AlertContractKind.V1))
        {
            throw new ArgumentException("Suppression projection does not match contract version.");
        }
        ContractVersion = contractVersion;
        SuppressionOrdinal = suppressionOrdinal;
        CanonicalBytes = Array.AsReadOnly(canonicalBytes.ToArray());
        SuppressionV1 = suppressionV1;
        SuppressionV2 = suppressionV2;
    }

    public AlertContractKind ContractVersion { get; }
    public long SuppressionOrdinal { get; }
    public IReadOnlyList<byte> CanonicalBytes { get; }
    public AlertSuppressionProjectionV1? SuppressionV1 { get; }
    public AlertSuppressionProjectionV2? SuppressionV2 { get; }
}

public sealed record AlertVersionedReceiptQueryPage(
    AlertEngineQueryStatus Status,
    IReadOnlyList<AlertVersionedReceiptQueryItem> Items,
    string? NextCursor = null,
    bool Exhausted = false,
    int CanonicalByteCount = 0,
    string? Code = null);

public sealed record AlertVersionedEvaluationQueryPage(
    AlertEngineQueryStatus Status,
    IReadOnlyList<AlertVersionedEvaluationQueryItem> Items,
    string? NextCursor = null,
    bool Exhausted = false,
    int CanonicalByteCount = 0,
    string? Code = null);

public sealed record AlertVersionedSuppressionQueryPage(
    AlertEngineQueryStatus Status,
    IReadOnlyList<AlertVersionedSuppressionQueryItem> Items,
    long? NextCursor = null,
    bool Exhausted = false,
    int CanonicalByteCount = 0,
    string? Code = null);

public interface IAlertEngineVersionedQueryStore
{
    AlertVersionedReceiptQueryPage ListReceiptsVersioned(string? afterAlertId, int limit);

    AlertVersionedEvaluationQueryPage ListEvaluationsVersioned(string? afterEvaluationId, int limit);

    AlertVersionedSuppressionQueryPage ListSuppressionsVersioned(
        string evaluationId,
        long? afterSuppressionOrdinal,
        int limit);
}

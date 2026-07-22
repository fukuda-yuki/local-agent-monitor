namespace CopilotAgentObservability.Alerts;

public static class AlertEngineQueryLimits
{
    public const int MaximumPageSize = 100;
    public const int MaximumPageBytes = 8_388_608;
}

public enum AlertEngineQueryStatus
{
    Success,
    Invalid,
    NotFound,
    Busy,
    Unavailable,
}

public sealed class AlertReceiptQueryItem
{
    public AlertReceiptQueryItem(IEnumerable<byte> canonicalBytes, AlertCenterReceiptProjectionV1 receipt)
    {
        ArgumentNullException.ThrowIfNull(canonicalBytes);
        ArgumentNullException.ThrowIfNull(receipt);
        CanonicalBytes = Array.AsReadOnly(canonicalBytes.ToArray());
        Receipt = receipt;
    }

    public IReadOnlyList<byte> CanonicalBytes { get; }
    public AlertCenterReceiptProjectionV1 Receipt { get; }
}

public sealed class AlertEvaluationProjectionV1
{
    public AlertEvaluationProjectionV1(
        string evaluationId,
        string inputHash,
        string configurationVersion,
        string configurationHash,
        long receiptCount,
        long suppressionCount)
    {
        EvaluationId = evaluationId;
        InputHash = inputHash;
        ConfigurationVersion = configurationVersion;
        ConfigurationHash = configurationHash;
        ReceiptCount = receiptCount;
        SuppressionCount = suppressionCount;
    }

    public string EvaluationId { get; }
    public string InputHash { get; }
    public string ConfigurationVersion { get; }
    public string ConfigurationHash { get; }
    public long ReceiptCount { get; }
    public long SuppressionCount { get; }
}

public sealed class AlertSuppressionQueryItem
{
    public AlertSuppressionQueryItem(
        long suppressionOrdinal,
        IEnumerable<byte> canonicalBytes,
        AlertSuppressionProjectionV1 suppression)
    {
        ArgumentNullException.ThrowIfNull(canonicalBytes);
        ArgumentNullException.ThrowIfNull(suppression);
        SuppressionOrdinal = suppressionOrdinal;
        CanonicalBytes = Array.AsReadOnly(canonicalBytes.ToArray());
        Suppression = suppression;
    }

    public long SuppressionOrdinal { get; }
    public IReadOnlyList<byte> CanonicalBytes { get; }
    public AlertSuppressionProjectionV1 Suppression { get; }
}

public sealed record AlertReceiptQueryPage(
    AlertEngineQueryStatus Status,
    IReadOnlyList<AlertReceiptQueryItem> Items,
    string? NextCursor = null,
    string? Code = null);

public sealed record AlertEvaluationQueryPage(
    AlertEngineQueryStatus Status,
    IReadOnlyList<AlertEvaluationProjectionV1> Items,
    string? NextCursor = null,
    string? Code = null);

public sealed record AlertSuppressionQueryPage(
    AlertEngineQueryStatus Status,
    IReadOnlyList<AlertSuppressionQueryItem> Items,
    long? NextCursor = null,
    string? Code = null);

public interface IAlertEngineQueryStore
{
    AlertReceiptQueryPage ListReceipts(string? afterAlertId, int limit);

    AlertEvaluationQueryPage ListEvaluations(string? afterEvaluationId, int limit);

    AlertSuppressionQueryPage ListSuppressions(string evaluationId, long? afterSuppressionOrdinal, int limit);
}

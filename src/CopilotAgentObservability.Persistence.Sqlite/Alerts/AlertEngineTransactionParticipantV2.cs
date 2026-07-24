using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.Persistence.Sqlite;

public enum AlertEngineTransactionAppendStatusV2
{
    Success,
    Conflict,
    Busy,
    Unavailable,
    InvalidTransaction,
    ContractRejected,
}

public sealed record AlertEngineSuppressionIdentityV2(
    string EvaluationId,
    long SuppressionOrdinal);

public sealed class AlertEngineTransactionAppendResultV2
{
    internal AlertEngineTransactionAppendResultV2(
        AlertEngineTransactionAppendStatusV2 status,
        string? evaluationId = null,
        IReadOnlyList<string>? receiptIds = null,
        IReadOnlyList<AlertEngineSuppressionIdentityV2>? suppressionIdentities = null)
    {
        Status = status;
        EvaluationId = evaluationId;
        ReceiptIds = receiptIds ?? [];
        SuppressionIdentities = suppressionIdentities ?? [];
    }

    public AlertEngineTransactionAppendStatusV2 Status { get; }
    public string? EvaluationId { get; }
    public IReadOnlyList<string> ReceiptIds { get; }
    public IReadOnlyList<AlertEngineSuppressionIdentityV2> SuppressionIdentities { get; }
}

public interface ISqliteAlertEngineTransactionParticipantV2
{
    AlertEngineTransactionAppendResultV2 AppendEvaluation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AlertEvaluationResultV2 evaluation);
}

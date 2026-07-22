using CopilotAgentObservability.Alerts;

namespace CopilotAgentObservability.LocalMonitor.Alerts;

internal sealed class AlertCenterEvaluationCoordinator(
    AlertCenterEvaluationSnapshotComposer composer,
    AlertEvaluationApplication application) : IAlertCenterEvaluationCoordinator
{
    public AlertCenterEvaluationResult Evaluate(Guid sessionId, string traceId)
    {
        try
        {
            var composition = composer.Compose(sessionId, traceId);
            if (composition.Status != AlertCenterSnapshotCompositionStatus.Success
                || composition.Snapshot is null)
            {
                return new(Map(composition.Status));
            }

            var evaluated = application.EvaluateAndAppend(composition.Snapshot);
            if (evaluated.Status != AlertEvaluationApplicationStatus.Success
                || evaluated.Outcome is null)
            {
                return new(Map(evaluated.Status));
            }

            var outcome = evaluated.Outcome;
            return new(
                AlertCenterEvaluationStatus.Success,
                new AlertCenterEvaluationResponse(
                    AlertCenterContractVersions.EvaluationResult,
                    outcome.EvaluationId,
                    outcome.ReceiptIds.ToArray(),
                    outcome.Suppressions.Select(item => new AlertCenterEvaluationSuppression(
                        item.RuleId,
                        item.RuleVersion,
                        item.Code,
                        item.MissingCapabilities.ToArray())).ToArray(),
                    outcome.RejectedMatches.Select(item => new AlertCenterEvaluationRejectedMatch(
                        item.RuleId,
                        item.RuleVersion,
                        item.Code)).ToArray()));
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            return new(AlertCenterEvaluationStatus.StoreUnavailable);
        }
    }

    private static AlertCenterEvaluationStatus Map(AlertCenterSnapshotCompositionStatus status) => status switch
    {
        AlertCenterSnapshotCompositionStatus.SessionNotFound => AlertCenterEvaluationStatus.SessionNotFound,
        AlertCenterSnapshotCompositionStatus.TraceNotFound => AlertCenterEvaluationStatus.TraceNotFound,
        AlertCenterSnapshotCompositionStatus.TraceNotOwned => AlertCenterEvaluationStatus.TraceNotOwned,
        AlertCenterSnapshotCompositionStatus.SourcePartitionMissing => AlertCenterEvaluationStatus.SourcePartitionMissing,
        AlertCenterSnapshotCompositionStatus.SourcePartitionAmbiguous => AlertCenterEvaluationStatus.SourcePartitionAmbiguous,
        AlertCenterSnapshotCompositionStatus.TraceIncomplete => AlertCenterEvaluationStatus.TraceIncomplete,
        AlertCenterSnapshotCompositionStatus.StoreBusy => AlertCenterEvaluationStatus.StoreBusy,
        _ => AlertCenterEvaluationStatus.StoreUnavailable,
    };

    private static AlertCenterEvaluationStatus Map(AlertEvaluationApplicationStatus status) => status switch
    {
        AlertEvaluationApplicationStatus.InitializationBusy or AlertEvaluationApplicationStatus.AppendBusy =>
            AlertCenterEvaluationStatus.StoreBusy,
        AlertEvaluationApplicationStatus.AppendConflict => AlertCenterEvaluationStatus.StoreConflict,
        AlertEvaluationApplicationStatus.ContractRejected => AlertCenterEvaluationStatus.ContractRejected,
        _ => AlertCenterEvaluationStatus.StoreUnavailable,
    };

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and not StackOverflowException and not AccessViolationException;
}

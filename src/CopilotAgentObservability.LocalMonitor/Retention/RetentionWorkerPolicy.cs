using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal sealed class RetentionWorkerPolicy
{
    internal int PromotionLimit => RetentionV1Constants.ExpiryScanItemLimit;
    internal int ClaimLimit => RetentionV1Constants.ClaimBatchLimit;
    internal int MaximumWorkers => RetentionV1Constants.MaximumActiveDeletionWorkers;
    internal TimeSpan ScanBudget => RetentionV1Constants.ScanElapsedBudget;
    internal TimeSpan DrainBound => RetentionV1Constants.ShutdownDrainBound;
}

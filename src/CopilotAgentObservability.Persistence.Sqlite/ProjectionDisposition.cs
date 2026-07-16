namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum ProjectionDispositionState
{
    NotStarted,
    Pending,
    Completed,
    Failed,
}

internal sealed record ProjectionDisposition(
    long RawRecordId,
    ProjectionDispositionState State,
    int Revision,
    DateTimeOffset UpdatedAt);

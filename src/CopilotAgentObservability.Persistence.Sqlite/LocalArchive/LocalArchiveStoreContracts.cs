namespace CopilotAgentObservability.Persistence.Sqlite;

internal delegate ReadOnlyMemory<byte> LocalArchiveSuccessEntityWriter(
    LocalArchiveMutationSuccess success);

internal enum LocalArchiveAction
{
    Archive,
    Restore,
}

internal enum LocalArchiveTargetKind
{
    Session,
    Repository,
}

internal sealed record LocalArchiveMutationTargetSuccess(
    string TargetId,
    LocalArchiveState State,
    long Revision,
    string? ArchivedAt,
    string? UpdatedAt);

internal sealed record LocalArchiveMutationSuccess(
    LocalArchiveAction Action,
    LocalArchiveTargetKind TargetKind,
    IReadOnlyList<LocalArchiveMutationTargetSuccess> Targets);

internal sealed record LocalArchiveMutationSucceeded(
    ReadOnlyMemory<byte> Entity);

internal enum LocalArchiveStoreError
{
    TargetNotFound,
    RevisionConflict,
    PersistenceBusy,
    ArchiveStoreUnavailable,
}

internal sealed record LocalArchiveReadResult(
    LocalArchiveMutationTargetSuccess? Success,
    LocalArchiveStoreError? Error);

internal sealed record LocalArchiveListSuccess(
    IReadOnlyList<LocalArchiveMutationTargetSuccess> Items,
    bool HasMore);

internal sealed record LocalArchiveListResult(
    LocalArchiveListSuccess? Success,
    LocalArchiveStoreError? Error);

internal sealed record LocalArchiveMutationResult(
    LocalArchiveMutationSucceeded? Success,
    LocalArchiveStoreError? Error);

internal sealed record LocalArchiveStoredEvent(
    string EventId,
    LocalArchiveTargetKind TargetKind,
    string TargetId,
    LocalArchiveAction Action,
    long PreviousRevision,
    long NewRevision,
    string OccurredAt);

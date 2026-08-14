using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Archive;

internal sealed record LocalArchiveDirectQuery(
    LocalArchiveTargetKind TargetKind,
    string TargetId);

internal sealed record LocalArchiveCursor(
    string ArchivedAt,
    string TargetId);

internal sealed record LocalArchiveListQuery(
    LocalArchiveTargetKind TargetKind,
    LocalArchiveCursor? After,
    int Limit);

internal sealed record LocalArchiveActionRequest(
    LocalArchiveAction Action,
    LocalArchiveTargetKind TargetKind,
    IReadOnlyList<LocalArchiveMutationTarget> Targets);

internal enum LocalArchiveWireError
{
    InvalidHost,
    InvalidRequest,
    InvalidCursor,
    CsrfRejected,
    TargetNotFound,
    MethodNotAllowed,
    RevisionConflict,
    RequestTooLarge,
    UnsupportedMediaType,
    ArchiveStoreUnavailable,
    PersistenceBusy,
}

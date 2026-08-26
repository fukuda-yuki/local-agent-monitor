using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ILocalRepositoryReadTransaction
{
    ValueTask<T> ReadAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
        CancellationToken cancellationToken);
}

internal interface ILocalRepositoryTargetExistenceAuthority
{
    IReadOnlyList<string> ReadExisting(
        SqliteConnection openConnection,
        SqliteTransaction exactTransaction,
        IReadOnlyList<string> canonicalRepositoryIds,
        CancellationToken cancellationToken);
}

internal interface ILocalRepositorySessionSnapshotContributor
{
    ValueTask<LocalRepositorySessionContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken);
}

internal interface ILocalArchiveFactSnapshotContributor
{
    ValueTask<LocalArchiveFactContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryArchiveInput input,
        CancellationToken cancellationToken);
}

internal interface ILocalRepositoryScopeSnapshotService
{
    ValueTask<LocalRepositoryScopeSnapshot> ReadAsync(
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken);

}

internal interface ILocalRepositorySessionDetailSnapshotService
{
    ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
        string sessionId,
        CancellationToken cancellationToken);
}

internal interface ILocalWorkspaceSessionDetailSnapshotContributor
{
    ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        string sessionId,
        CancellationToken cancellationToken);
}

internal interface ILocalRepositorySessionSnapshotRow
{
    string SessionId { get; }
}

internal enum LocalRepositoryScopeKind
{
    All,
    Repository,
    Unassigned,
}

internal enum LocalRepositoryScopeAssignmentState
{
    Assigned,
    Unassigned,
    ExplicitlyUnassigned,
    Conflict,
}

internal enum LocalRepositoryScopeAssignmentAuthority
{
    Automatic,
    Manual,
    None,
}

internal enum LocalRepositoryScopeSnapshotError
{
    PersistenceBusy,
}

internal sealed record LocalRepositoryScopeRequest(
    LocalRepositoryScopeKind ScopeKind,
    string? RepositoryId,
    string? TargetSessionId = null);

internal sealed record LocalRepositorySessionContribution(
    IReadOnlyList<ILocalRepositorySessionSnapshotRow> Sessions);

internal sealed record LocalRepositoryArchiveInput(
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<string> RepositoryIds);

internal enum LocalArchiveState
{
    Active,
    Archived,
}

internal sealed record LocalArchiveSessionFact(
    string SessionId,
    LocalArchiveState State,
    long Revision);

internal sealed record LocalArchiveRepositoryFact(
    string RepositoryId,
    LocalArchiveState State,
    long Revision);

internal sealed record LocalArchiveFactContribution(
    IReadOnlyList<LocalArchiveSessionFact> Sessions,
    IReadOnlyList<LocalArchiveRepositoryFact> Repositories);

internal sealed record LocalRepositoryCatalogSnapshot(
    string RepositoryId,
    string DisplayName,
    long Revision,
    string? CurrentLocatorId,
    long AssignmentConflictCount,
    LocalArchiveState ArchiveState,
    long ArchiveRevision);

internal sealed record LocalRepositoryScopeSessionSnapshot(
    string SessionId,
    ILocalRepositorySessionSnapshotRow Session,
    long AssignmentRevision,
    LocalRepositoryScopeAssignmentState AssignmentState,
    LocalRepositoryScopeAssignmentAuthority AssignmentAuthority,
    string? RepositoryId,
    IReadOnlyList<string> CandidateRepositoryIds,
    bool IsAllScopeMember,
    bool IsUnassignedScopeMember,
    bool IsRequestedScopeMember,
    LocalArchiveState ArchiveState,
    long ArchiveRevision,
    bool IsEffectivelyEligible,
    string? ArchiveExclusionReason);

internal sealed record LocalRepositoryScopeSnapshot(
    LocalRepositoryScopeRequest Request,
    IReadOnlyList<LocalRepositoryCatalogSnapshot> Repositories,
    IReadOnlyList<LocalRepositoryScopeSessionSnapshot> Sessions);

internal sealed record LocalRepositorySessionDetailSnapshot(
    LocalRepositoryScopeSessionSnapshot Session,
    LocalWorkspaceSessionDetailContribution Detail,
    string WorkspaceRevision);

internal sealed class LocalRepositoryScopeSnapshotException : Exception
{
    internal LocalRepositoryScopeSnapshotException(
        LocalRepositoryScopeSnapshotError error,
        string errorCode,
        Exception innerException)
        : base(errorCode, innerException)
    {
        Error = error;
        ErrorCode = errorCode;
    }

    internal LocalRepositoryScopeSnapshotError Error { get; }
    internal string ErrorCode { get; }
}

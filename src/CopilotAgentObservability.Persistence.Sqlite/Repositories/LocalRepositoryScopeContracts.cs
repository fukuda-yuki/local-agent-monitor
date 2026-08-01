using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal interface ILocalRepositoryReadTransaction
{
    ValueTask<T> ReadAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
        CancellationToken cancellationToken);
}

internal interface ILocalRepositorySessionSnapshotContributor
{
    ValueTask<LocalRepositorySessionContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken);
}

internal interface ILocalArchiveEligibilitySnapshotContributor
{
    ValueTask<LocalArchiveEligibilityContribution> ReadAsync(
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
    string? RepositoryId);

internal sealed record LocalRepositorySessionContribution(
    IReadOnlyList<ILocalRepositorySessionSnapshotRow> Sessions);

internal sealed record LocalRepositoryArchiveInput(
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<string> RepositoryIds);

internal sealed record LocalArchiveSessionEligibility(
    string SessionId,
    bool IsEligible,
    string? ExclusionReason);

internal sealed record LocalArchiveEligibilityContribution(
    IReadOnlyList<LocalArchiveSessionEligibility> Sessions);

internal sealed record LocalRepositoryCatalogSnapshot(
    string RepositoryId,
    string DisplayName,
    long Revision,
    string? CurrentLocatorId,
    long AssignmentConflictCount);

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
    bool IsEffectivelyEligible,
    string? ArchiveExclusionReason);

internal sealed record LocalRepositoryScopeSnapshot(
    LocalRepositoryScopeRequest Request,
    IReadOnlyList<LocalRepositoryCatalogSnapshot> Repositories,
    IReadOnlyList<LocalRepositoryScopeSessionSnapshot> Sessions);

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

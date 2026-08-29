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

internal interface ILocalRepositoryComparisonInputSnapshotService
{
    ValueTask<LocalRepositoryComparisonInputSnapshot> ReadComparisonInputAsync(
        LocalRepositoryScopeRequest request,
        CancellationToken cancellationToken);
}

internal interface ILocalRepositorySessionDetailSnapshotService
{
    ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(
        LocalRepositorySessionDetailRequest request,
        CancellationToken cancellationToken);
}

internal interface ILocalWorkspaceSessionDetailSnapshotContributor
{
    ValueTask<LocalWorkspaceSessionDetailContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositorySessionDetailRequest request,
        CancellationToken cancellationToken);
}

internal enum LocalRepositorySessionDetailRequestKind
{
    Summary,
    Timeline,
    Node,
    Content,
}

internal sealed record LocalRepositoryTimelinePosition(
    byte TimeGroup,
    long UtcTicks,
    ulong SourceOrdinal,
    string NodeId);

internal sealed record LocalRepositorySessionDetailRequest(
    LocalRepositorySessionDetailRequestKind Kind,
    string SessionId,
    string? ExecutionId = null,
    string? ParentNodeId = null,
    LocalRepositoryTimelinePosition? After = null,
    int Limit = 100,
    string? NodeId = null,
    string? ContentPart = null,
    string? ExpectedWorkspaceRevision = null);

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
    string? TargetSessionId = null,
    IReadOnlyList<string>? ExactTargetSessionIds = null);

internal static class LocalRepositoryScopeRequestValidation
{
    internal static void Validate(LocalRepositoryScopeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.IsDefined(request.ScopeKind))
            throw new ArgumentException("invalid_local_repository_scope", nameof(request));
        if (request.ScopeKind == LocalRepositoryScopeKind.Repository)
        {
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(request.RepositoryId))
                throw new ArgumentException("invalid_local_repository_scope", nameof(request));
        }
        else if (request.RepositoryId is not null)
        {
            throw new ArgumentException("invalid_local_repository_scope", nameof(request));
        }
        if (request.TargetSessionId is not null
            && !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(request.TargetSessionId))
            throw new ArgumentException("invalid_local_repository_scope", nameof(request));
        if (request.TargetSessionId is not null && request.ExactTargetSessionIds is not null)
            throw new ArgumentException("invalid_local_repository_scope", nameof(request));
        if (request.ExactTargetSessionIds is { } targets
            && (targets.Count is < 1 or > 200
                || targets.Any(static id => !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(id))
                || targets.Distinct(StringComparer.Ordinal).Count() != targets.Count))
            throw new ArgumentException("invalid_local_repository_scope", nameof(request));
    }
}

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
    string? ArchiveExclusionReason,
    long? AssignedRepositoryArchiveRevision = null);

internal sealed record LocalRepositoryScopeSnapshot(
    LocalRepositoryScopeRequest Request,
    IReadOnlyList<LocalRepositoryCatalogSnapshot> Repositories,
    IReadOnlyList<LocalRepositoryScopeSessionSnapshot> Sessions);

internal sealed record LocalRepositorySessionDetailSnapshot(
    LocalRepositoryScopeSessionSnapshot Session,
    LocalWorkspaceSessionDetailContribution Detail,
    string WorkspaceRevision);

internal sealed record LocalRepositoryComparisonSessionInput(
    LocalRepositoryScopeSessionSnapshot Session,
    LocalWorkspaceSessionDetailContribution? Detail,
    string WorkspaceRevision,
    string? ProjectionError = null);

internal sealed record LocalRepositoryComparisonInputSnapshot(
    LocalRepositoryScopeSnapshot Scope,
    IReadOnlyList<LocalRepositoryComparisonSessionInput> Sessions);

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

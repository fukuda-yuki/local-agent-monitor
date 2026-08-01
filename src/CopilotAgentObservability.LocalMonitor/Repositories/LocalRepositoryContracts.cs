namespace CopilotAgentObservability.LocalMonitor.Repositories;

internal static class LocalRepositoryContracts
{
    internal const string CollectionRoute = "/api/local-monitor/v1/repositories";
    internal const string ItemRoute = "/api/local-monitor/v1/repositories/{repositoryId}";
    internal const string LocatorRoute = "/api/local-monitor/v1/repositories/{repositoryId}/locators";
    internal const string SessionActionRoute = "/api/local-monitor/v1/session-repository-actions";
    internal const string AssignmentRoute = "/api/local-monitor/v1/sessions/{sessionId}/repository-assignment";
    internal const int RepositoryBodyLimit = 16_384;
    internal const int SessionActionBodyLimit = 4_096;
}

internal sealed record LocalRepositoryCreateRequest(string SchemaVersion, string DisplayName, string? GitHubLocator);
internal sealed record LocalRepositoryUpdateRequest(
    string SchemaVersion,
    long ExpectedRevision,
    string Operation,
    string? DisplayName,
    string? GitHubLocator);
internal sealed record LocalRepositorySessionActionRequest(
    string SchemaVersion,
    string SessionId,
    long ExpectedRevision,
    string Action,
    string? RepositoryId);

internal enum LocalRepositoryError
{
    InvalidRequest,
    InvalidLocator,
    RepositoryNotFound,
    SessionNotFound,
    RevisionConflict,
    LocatorConflict,
    LocatorLimitReached,
    IdempotencyConflict,
    CsrfRejected,
    RequestTooLarge,
    UnsupportedMediaType,
    MethodNotAllowed,
    PersistenceBusy,
    LocalMonitorUiUnavailable,
}

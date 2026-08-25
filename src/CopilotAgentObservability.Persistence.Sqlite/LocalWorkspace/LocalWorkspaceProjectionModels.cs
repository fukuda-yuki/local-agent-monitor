namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalWorkspaceFact<T>(string State, T? Value) where T : struct;
internal sealed record LocalWorkspaceSetFact(string State, IReadOnlyList<string> Values);

internal sealed record LocalWorkspaceActivityFacts(
    LocalWorkspaceFact<long> Skill,
    LocalWorkspaceFact<long> Tool,
    LocalWorkspaceFact<long> Subagent,
    LocalWorkspaceFact<long> Error,
    LocalWorkspaceFact<long> Retry);

internal sealed record LocalWorkspaceTokenFacts(
    string Authority,
    string State,
    long AvailableExecutionCount,
    long TotalExecutionCount,
    LocalWorkspaceFact<long> Input,
    LocalWorkspaceFact<long> Output,
    LocalWorkspaceFact<long> Total,
    LocalWorkspaceFact<long> Reasoning,
    LocalWorkspaceFact<long> CacheRead,
    LocalWorkspaceFact<long> CacheCreation,
    LocalWorkspaceFact<long> NewInput,
    LocalWorkspaceFact<long> CacheReadRatioBasisPoints);

internal sealed record LocalWorkspaceProjectionRow(
    string SessionId,
    long SortGroup,
    long SortEpochMilliseconds,
    string LabelState,
    string? LabelText,
    string Status,
    string Completeness,
    LocalWorkspaceSetFact Sources,
    LocalWorkspaceSetFact Models,
    LocalWorkspaceActivityFacts Activity,
    LocalWorkspaceTokenFacts Tokens,
    string TimingState,
    string? StartedAt,
    string? EndedAt,
    string LastSeenAt,
    long? DurationMilliseconds,
    IReadOnlyList<string> CaptureNotes,
    string RevisionSeed) : ILocalRepositorySessionSnapshotRow;

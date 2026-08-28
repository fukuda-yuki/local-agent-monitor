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
    string? LastSeenAt,
    long? LastSeenEpochMilliseconds,
    long? DurationMilliseconds,
    IReadOnlyList<string> CaptureNotes,
    IReadOnlyList<string> SearchTexts,
    string RevisionSeed) : ILocalRepositorySessionSnapshotRow
{
    [System.Text.Json.Serialization.JsonIgnore]
    internal LocalWorkspaceFact<long>? CurrentSkillFilter { get; init; }

    internal LocalWorkspaceProjectionRow(
        string sessionId, long sortGroup, long sortEpochMilliseconds, string labelState, string? labelText,
        string status, string completeness, LocalWorkspaceSetFact sources, LocalWorkspaceSetFact models,
        LocalWorkspaceActivityFacts activity, LocalWorkspaceTokenFacts tokens, string timingState,
        string? startedAt, string? endedAt, string? lastSeenAt, long? durationMilliseconds,
        IReadOnlyList<string> captureNotes, string revisionSeed)
        : this(sessionId, sortGroup, sortEpochMilliseconds, labelState, labelText, status, completeness, sources, models,
            activity, tokens, timingState, startedAt, endedAt, lastSeenAt,
            DateTimeOffset.TryParse(lastSeenAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed.ToUnixTimeMilliseconds() : null,
            durationMilliseconds, captureNotes,
            labelText is null ? Array.Empty<string>() : [labelText.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant()], revisionSeed)
    { }
}

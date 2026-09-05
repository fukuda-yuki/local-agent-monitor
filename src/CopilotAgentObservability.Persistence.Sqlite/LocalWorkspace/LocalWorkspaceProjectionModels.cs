namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalWorkspaceFact<T>(string State, T? Value) where T : struct;
internal sealed record LocalWorkspaceSetFact(string State, IReadOnlyList<string> Values);
internal sealed record LocalWorkspaceTokenObservationFact(
    LocalWorkspaceFact<long> Subtotal, long ObservedCallCount, long ApplicableCallCount, long? PairedInput);
internal sealed record LocalWorkspaceObservedActivity(string StartedAt, string EndedAt, long DurationMilliseconds);

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
    LocalWorkspaceFact<long> CacheReadRatioBasisPoints)
{
    public IReadOnlyDictionary<string, LocalWorkspaceTokenObservationFact>? Observations { get; init; }
    internal bool HasValidObservations() => Observations is null || Observations.Count == 7
        && new[] { "input", "output", "total", "reasoning", "cache_read", "cache_creation", "cache_read_ratio_basis_points" }.All(key =>
            Observations.TryGetValue(key, out var value)
            && value.ObservedCallCount >= 0 && value.ObservedCallCount <= value.ApplicableCallCount
            && value.ApplicableCallCount == TotalExecutionCount
            && (value.Subtotal.State == "recorded" ? value.Subtotal.Value is >= 0 && value.ObservedCallCount > 0
                : value.Subtotal.State is "not_observed" or "inconsistent" or "oversized" && value.Subtotal.Value is null)
            && (key == "cache_read_ratio_basis_points"
                ? value.PairedInput is null or >= 0 && value.Subtotal.Value is null or <= 10_000
                    && (value.Subtotal.State != "recorded" || value.PairedInput > 0)
                : value.PairedInput is null));
}

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
    public LocalWorkspaceObservedActivity? ObservedActivity { get; init; }
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

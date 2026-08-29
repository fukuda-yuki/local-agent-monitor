namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalComparisonInputProjectionTests
{
    internal const string RepositoryId = "018f0000-0000-7000-8000-000000000003";
    [Fact]
    public void RepositoryScopeRequestCarriesExactOrderedBatchTargets()
    {
        string[] targets =
        [
            "018f0000-0000-7000-8000-000000000002",
            "018f0000-0000-7000-8000-000000000001",
        ];

        var request = new LocalRepositoryScopeRequest(
            LocalRepositoryScopeKind.Repository,
            "018f0000-0000-7000-8000-000000000003",
            ExactTargetSessionIds: targets);

        Assert.Same(targets, request.ExactTargetSessionIds);
        Assert.Null(request.TargetSessionId);
    }

    [Fact]
    public void PreviewPreservesRequestedOccurrencesAndCanonicalizesResolvedEntries()
    {
        var first = Candidate("018f0000-0000-7000-8000-000000000001", "revision-b");
        var second = Candidate("018f0000-0000-7000-8000-000000000002", "revision-a");

        var preview = LocalComparisonInputProjection.Project(
            RepositoryId,
            [second.SessionId, first.SessionId, second.SessionId],
            [first.SessionId],
            includeArchived: false,
            [first, second],
            repositoryRevision: "repository-1");

        Assert.Equal([second.SessionId, first.SessionId, second.SessionId, first.SessionId], preview.Requested.Select(item => item.SessionId));
        Assert.Equal([first.SessionId, second.SessionId], preview.Included.Select(item => item.SessionId));
        Assert.Equal(["duplicate", "cohort_overlap"], preview.Excluded.Select(item => item.Reason));
        Assert.False(preview.Valid);
    }

    [Fact]
    public void PreviewRevisionChangesWithRepositoryOrProjectionAuthorityButSelectionDoesNot()
    {
        var a = Candidate("018f0000-0000-7000-8000-000000000001", "projection-1");
        var b = Candidate("018f0000-0000-7000-8000-000000000002", "projection-2");
        var first = LocalComparisonInputProjection.Project(RepositoryId, [a.SessionId], [b.SessionId], false, [a, b], "repository-1");
        var changedRepository = LocalComparisonInputProjection.Project(RepositoryId, [a.SessionId], [b.SessionId], false, [a, b], "repository-2");
        var changedProjection = LocalComparisonInputProjection.Project(RepositoryId, [a.SessionId], [b.SessionId], false, [a with { ProjectionRevision = "projection-3" }, b], "repository-1");

        Assert.Equal(first.SelectionSha256, changedRepository.SelectionSha256);
        Assert.Equal(first.SelectionSha256, changedProjection.SelectionSha256);
        Assert.NotEqual(first.PreviewRevision, changedRepository.PreviewRevision);
        Assert.NotEqual(first.PreviewRevision, changedProjection.PreviewRevision);
    }

    [Fact]
    public void SelectionDigestUsesCanonicalIncludedCohortsNotExcludedOccurrences()
    {
        var a = Candidate("018f0000-0000-7000-8000-000000000001", "projection-1");
        var b = Candidate("018f0000-0000-7000-8000-000000000002", "projection-2");
        var clean = LocalComparisonInputProjection.Project(RepositoryId, [a.SessionId], [b.SessionId], false, [a, b], "repository-1");
        var withExcludedDuplicate = LocalComparisonInputProjection.Project(RepositoryId, [a.SessionId, a.SessionId], [b.SessionId], false, [a, b], "repository-1");

        Assert.Equal(clean.SelectionSha256, withExcludedDuplicate.SelectionSha256);
        Assert.NotEqual(clean.PreviewRevision, withExcludedDuplicate.PreviewRevision);
    }

    [Fact]
    public void PreviewUsesEveryFixedCandidateExclusionWithoutLeakingRawData()
    {
        var reasons = Enum.GetValues<LocalComparisonCandidateState>().Where(state => state != LocalComparisonCandidateState.Included).ToArray();
        var candidates = reasons.Select((state, index) => Candidate($"018f0000-0000-7000-8000-{index + 1:x12}", $"projection-{index}") with { State = state }).ToArray();

        var preview = LocalComparisonInputProjection.Project(RepositoryId, candidates.Select(item => item.SessionId).ToArray(), ["018f0000-0000-7000-8000-0000000000ff"], false, candidates, "repository-1");

        Assert.Equal(reasons.Select(LocalComparisonInputProjection.ExclusionToken), preview.Excluded.Take(reasons.Length).Select(item => item.Reason));
        Assert.DoesNotContain("path", System.Text.Json.JsonSerializer.Serialize(preview), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw", System.Text.Json.JsonSerializer.Serialize(preview), StringComparison.OrdinalIgnoreCase);
    }

    private static LocalComparisonProjectionCandidate Candidate(string sessionId, string projectionRevision) =>
        new(sessionId, RepositoryId, LocalComparisonCandidateState.Included, false, "active", ["synthetic"], "recorded", ["test-model"], "recorded", 5, "full", ["tokens"], 1, projectionRevision);

    [Fact]
    public void WorkspaceAdapterMapsRecordedFactsAndKeepsUnsupportedExplicit()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: true);

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, new string('a', 64), includeArchived: false);

        Assert.Equal(LocalComparisonFactState.Recorded, fact.Scalars["input_tokens"].Observation.State);
        Assert.Equal(10m, fact.Scalars["input_tokens"].Observation.Value);
        Assert.Equal(LocalComparisonFactState.SourceUnsupported, fact.Scalars["model_turn_count"].Observation.State);
        var subagent = Assert.Single(fact.NamedFamilies.Single(item => item.Family == "subagent").Items);
        Assert.Equal("識別名なし", subagent.DisplayName);
        Assert.Equal("018f0000-0000-7000-8000-000000000010", subagent.Reference.EventId);
        Assert.Equal("node-00000000000000000000000000000001", subagent.Reference.SourceIdentity);
        Assert.DoesNotContain("path", System.Text.Json.JsonSerializer.Serialize(fact), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkspaceAdapterMarksArchiveInclusionOnlyWhenExplicit()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: true);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);

        Assert.False(LocalComparisonInputProjection.MapSessionFact(session, detail, new string('a', 64), false).IsArchiveInclusionExplicit);
        Assert.True(LocalComparisonInputProjection.MapSessionFact(session, detail, new string('a', 64), true).IsArchiveInclusionExplicit);
    }

    internal static LocalRepositoryScopeSessionSnapshot ScopeSession(string sessionId, bool archived)
    {
        var activity = new LocalWorkspaceActivityFacts(Fact(0), Fact(1), Fact(1), new("source_unsupported", null), new("not_observed", null));
        var tokens = new LocalWorkspaceTokenFacts("session_run", "recorded", 1, 1, Fact(10), Fact(5), Fact(15), new("not_observed", null), Fact(0), new("source_unsupported", null), Fact(10), Fact(0));
        var row = new LocalWorkspaceProjectionRow(sessionId, 1, 1, "not_observed", null, "completed", "full", new("recorded", ["synthetic"]), new("recorded", ["test-model"]), activity, tokens, "recorded", "2026-08-29T00:00:00.0000000+00:00", "2026-08-29T00:00:01.0000000+00:00", "2026-08-29T00:00:01.0000000+00:00", 1000, [], "revision");
        return new(sessionId, row, 1, LocalRepositoryScopeAssignmentState.Assigned, LocalRepositoryScopeAssignmentAuthority.Manual, RepositoryId, [], true, false, true, archived ? LocalArchiveState.Archived : LocalArchiveState.Active, 1, true, null, 1);
    }

    internal static LocalWorkspaceSessionDetailContribution Detail(string sessionId, bool unidentifiedSubagent)
    {
        var activity = new LocalWorkspaceActivityFacts(Fact(0), Fact(0), Fact(0), Fact(0), Fact(0));
        var tokens = new LocalWorkspaceTokenFacts("none", "not_observed", 0, 1, new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null));
        var node = new LocalWorkspaceNodeDetail("node-00000000000000000000000000000001", sessionId, "018f0000-0000-7000-8000-000000000010", "semantic_subagent", "opaque", 1, null, "exact", "subagent", unidentifiedSubagent ? "not_observed" : "recorded", unidentifiedSubagent ? null : "helper", "completed", "completed", "missing", null, null, null, activity, tokens, null, null, null, 0, false, null, [], null, null, new("not_observed", "recorded", "recorded", "not_observed", "not_observed", "not_captured"));
        var skill = node with { NodeId = "node-00000000000000000000000000000002", Kind = "skill", NameState = "recorded", NameText = "skill-helper" };
        var tool = node with { NodeId = "node-00000000000000000000000000000003", Kind = "tool", NameState = "recorded", NameText = "tool-helper" };
        return new([], [node, skill, tool], [], [], Versions: ["1"], CanonicalRevisionInput: "revision", SkillRegistryGenerationIdentity: new string('b', 64));
    }

    private static LocalWorkspaceFact<long> Fact(long value) => new("recorded", value);

    [Fact]
    public void RepositoryScopeRequestKeepsSingleAndBatchTargetsMutuallyExclusive()
    {
        var request = new LocalRepositoryScopeRequest(
            LocalRepositoryScopeKind.Repository,
            "018f0000-0000-7000-8000-000000000003",
            TargetSessionId: "018f0000-0000-7000-8000-000000000001",
            ExactTargetSessionIds: ["018f0000-0000-7000-8000-000000000002"]);

        Assert.Throws<ArgumentException>(() => LocalRepositoryScopeRequestValidation.Validate(request));
    }

    [Fact]
    public void RepositoryScopeRequestRejectsNoncanonicalDuplicateAndOversizedExactTargets()
    {
        const string repositoryId = "018f0000-0000-7000-8000-000000000003";
        var invalid = new IReadOnlyList<string>[]
        {
            ["018F0000-0000-7000-8000-000000000001"],
            ["018f0000-0000-7000-8000-000000000001", "018f0000-0000-7000-8000-000000000001"],
            Enumerable.Range(0, 201).Select(index => $"018f0000-0000-7000-8000-{index:x12}").ToArray(),
        };

        Assert.All(invalid, targets => Assert.Throws<ArgumentException>(() =>
            LocalRepositoryScopeRequestValidation.Validate(new(
                LocalRepositoryScopeKind.Repository,
                repositoryId,
                ExactTargetSessionIds: targets))));
    }
}

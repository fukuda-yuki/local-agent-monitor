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
        Assert.All(preview.Excluded, item => Assert.NotNull(item.Metadata));
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
    public void PreviewRevisionFramesNullAndRecordedEmptyVersionDimensionsDistinctly()
    {
        var a = Candidate("018f0000-0000-7000-8000-000000000001", "projection-1");
        var b = Candidate("018f0000-0000-7000-8000-000000000002", "projection-2");
        var recordedEmpty = LocalComparisonInputProjection.Project(RepositoryId, [a.SessionId], [b.SessionId], false,
            [a with { SourceApplicationVersions = [] }, b], "repository-1");
        var unavailable = LocalComparisonInputProjection.Project(RepositoryId, [a.SessionId], [b.SessionId], false,
            [a with { SourceApplicationVersions = null }, b], "repository-1");

        Assert.NotEqual(recordedEmpty.PreviewRevision, unavailable.PreviewRevision);
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
        new(sessionId, RepositoryId, LocalComparisonCandidateState.Included, false, "active", 1, "active", 1, null,
            ["synthetic"], "recorded", ["test-model"], "recorded", 5, "full", ["tokens"], ["source-1"], ["adapter-1"], 1, projectionRevision);

    [Fact]
    public void WorkspaceAdapterMapsRecordedFactsAndKeepsUnsupportedExplicit()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: true);

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail(detail.Nodes), new string('a', 64), includeArchived: false);

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
    public void WorkspaceAdapterGroupsExactSemanticNamesAndPreservesEveryObservation()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var tool = detail.Nodes.Single(node => node.Kind == "tool");
        var repeated = tool with
        {
            NodeId = "node-00000000000000000000000000000004",
            ExecutionId = "018f0000-0000-7000-8000-000000000011",
            Activity = tool.Activity with { Retry = Fact(2) },
            SourceReferences = [new("source_event", "source-1", "trace-1", "span-1", "event-1", new string('c', 64), true)],
        };
        var comparisonDetail = ComparisonDetail([tool, repeated]);

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, comparisonDetail, new string('a', 64), includeArchived: false);

        var item = Assert.Single(fact.NamedFamilies.Single(family => family.Family == "tool").Items);
        Assert.Equal("tool-helper", item.DisplayName);
        Assert.Matches("^[0-9a-f]{64}$", item.IdentityKey);
        Assert.Equal("tool-helper", item.SortKey);
        Assert.Equal(2m, item.Values["call_count"].Observation.Value);
        Assert.Equal(2m, item.Values["retry_count"].Observation.Value);
        Assert.Equal(3, item.Values["call_count"].Evidence.Count);
        Assert.Contains(item.Values["call_count"].Evidence, evidence => evidence.Reference!.EventId == "event-1" && evidence.Reference.RevisionSha256 == new string('c', 64));
    }

    [Fact]
    public void WorkspaceAdapterAggregatesRepeatedSkillAndSubagentLifecycleAndTokens()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var skill = detail.Nodes.Single(node => node.Kind == "skill");
        var subagent = detail.Nodes.Single(node => node.Kind == "subagent") with
        {
            Tokens = detail.Nodes.Single(node => node.Kind == "subagent").Tokens with { Total = Fact(7) },
        };
        var nodes = new[]
        {
            skill,
            skill with { NodeId = "skill-repeat", ExecutionId = "018f0000-0000-7000-8000-000000000011" },
            subagent,
            subagent with
            {
                NodeId = "subagent-repeat",
                ExecutionId = "018f0000-0000-7000-8000-000000000012",
                Tokens = subagent.Tokens with { Total = new("capture_gap", null) },
                SubagentLifecycle = subagent.SubagentLifecycle! with { FailedState = "recorded" },
            },
        };

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail(nodes), new string('a', 64), false);

        var skillItem = Assert.Single(fact.NamedFamilies.Single(family => family.Family == "skill").Items);
        Assert.Equal(2m, skillItem.Values["invocation_count"].Observation.Value);
        var subagentItem = Assert.Single(fact.NamedFamilies.Single(family => family.Family == "subagent").Items);
        Assert.Equal(2m, subagentItem.Values["start_count"].Observation.Value);
        Assert.Equal(2m, subagentItem.Values["completed_count"].Observation.Value);
        Assert.Equal(LocalComparisonFactState.NotObserved, subagentItem.Values["failed_count"].Observation.State);
        Assert.Equal(LocalComparisonFactState.CaptureGap, subagentItem.Values["recorded_tokens"].Observation.State);
        Assert.Equal(2, subagentItem.Values["recorded_tokens"].Evidence.Count);
    }

    [Fact]
    public void WorkspaceAdapterUsesExactNamesAndOneFixedUnidentifiedSubagentIdentity()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var prototype = detail.Nodes.Single(node => node.Kind == "subagent");
        var nodes = new[]
        {
            prototype with { NodeId = "skill-missing", Kind = "skill", NameState = "not_observed", NameText = null },
            prototype with { NodeId = "tool-missing", Kind = "tool", NameState = "not_observed", NameText = null },
            prototype with { NodeId = "subagent-missing-1", NameState = "not_observed", NameText = null },
            prototype with { NodeId = "subagent-missing-2", ExecutionId = "018f0000-0000-7000-8000-000000000011", NameState = "source_unsupported", NameText = null },
            prototype with { NodeId = "subagent-named", NameState = "recorded", NameText = "  Helper\u3000Agent " },
        };

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail(nodes), new string('a', 64), false);

        var skillFamily = fact.NamedFamilies.Single(family => family.Family == "skill");
        var toolFamily = fact.NamedFamilies.Single(family => family.Family == "tool");
        Assert.Empty(skillFamily.Items);
        Assert.Empty(toolFamily.Items);
        Assert.Equal(LocalComparisonFactState.NotObserved, skillFamily.State);
        Assert.Equal(LocalComparisonFactState.NotObserved, toolFamily.State);
        var subagents = fact.NamedFamilies.Single(family => family.Family == "subagent").Items;
        Assert.Equal(2, subagents.Count);
        var unidentified = Assert.Single(subagents, item => item.DisplayName == "識別名なし");
        Assert.Equal(2m, unidentified.Values["start_count"].Observation.Value);
        var named = Assert.Single(subagents, item => item.DisplayName == "  Helper\u3000Agent ");
        Assert.Equal("helper agent", named.SortKey);
    }

    [Fact]
    public void WorkspaceAdapterAggregatesOnlyCompleteNamedFactsAndKeepsUnavailableEvidence()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var tool = detail.Nodes.Single(node => node.Kind == "tool") with { Lifecycle = "active", Status = "active", Activity = detail.Nodes.Single(node => node.Kind == "tool").Activity with { Retry = new("not_observed", null) } };
        var completed = tool with { NodeId = "tool-completed", Lifecycle = "completed", Status = "completed", Activity = tool.Activity with { Retry = Fact(1) } };

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail([tool, completed]), new string('a', 64), false);

        var item = Assert.Single(fact.NamedFamilies.Single(family => family.Family == "tool").Items);
        Assert.Equal(LocalComparisonFactState.SourceUnsupported, item.Values["failure_count"].Observation.State);
        Assert.Null(item.Values["failure_count"].Observation.Value);
        Assert.Equal(LocalComparisonFactState.NotObserved, item.Values["retry_count"].Observation.State);
        Assert.Equal(2, item.Values["failure_count"].Evidence.Count);
        Assert.Contains(item.Values["failure_count"].Evidence, evidence => evidence.State == LocalComparisonFactState.ExplicitZero && evidence.Reference!.SourceIdentity == completed.NodeId);
        Assert.Equal(2, item.Values["retry_count"].Evidence.Count);
    }

    [Theory]
    [InlineData("active")]
    [InlineData("unknown")]
    public void WorkspaceAdapterDoesNotPublishToolFailureZeroWithoutCompletedStatus(string status)
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var tool = detail.Nodes.Single(node => node.Kind == "tool") with { Lifecycle = "completed", Status = status };

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail([tool]), new string('a', 64), false);

        var failure = Assert.Single(fact.NamedFamilies.Single(family => family.Family == "tool").Items)
            .Values["failure_count"];
        Assert.Equal(LocalComparisonFactState.SourceUnsupported, failure.Observation.State);
        Assert.Null(failure.Observation.Value);
    }

    [Fact]
    public void WorkspaceAdapterRetainsKnownItemsButMarksFamilyPartialWhenAnyExactNameIsMissing()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var tool = detail.Nodes.Single(node => node.Kind == "tool");
        var missing = tool with { NodeId = "tool-missing", NameState = "not_observed", NameText = null };

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail([tool, missing]), new string('a', 64), false);

        var family = fact.NamedFamilies.Single(item => item.Family == "tool");
        Assert.Equal(LocalComparisonFactState.NotObserved, family.State);
        Assert.Single(family.Items);
        LocalComparisonApplicationValidation.ValidateSession(session.RepositoryId!, fact);
    }

    [Fact]
    public void WorkspaceAdapterDistinguishesProvedEmptyFamilyFromPositiveMissingDetail()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var zeroSession = session with { Session = ((LocalWorkspaceProjectionRow)session.Session!) with { Activity = ((LocalWorkspaceProjectionRow)session.Session!).Activity with { Tool = Fact(0) } } };

        var positive = LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail([]), new string('a', 64), false);
        var zero = LocalComparisonInputProjection.MapSessionFact(zeroSession, detail, ComparisonDetail([]), new string('a', 64), false);

        Assert.Equal(LocalComparisonFactState.CaptureGap, positive.NamedFamilies.Single(family => family.Family == "tool").State);
        Assert.Equal(LocalComparisonFactState.ExplicitZero, zero.NamedFamilies.Single(family => family.Family == "tool").State);
    }

    [Fact]
    public void WorkspaceAdapterMarksArchiveInclusionOnlyWhenExplicit()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: true);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);

        Assert.False(LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail(detail.Nodes), new string('a', 64), false).IsArchiveInclusionExplicit);
        Assert.True(LocalComparisonInputProjection.MapSessionFact(session, detail, ComparisonDetail(detail.Nodes), new string('a', 64), true).IsArchiveInclusionExplicit);
    }

    [Fact]
    public void WorkspaceAdapterSeparatesSourceAndAdapterVersions()
    {
        var session = ScopeSession("018f0000-0000-7000-8000-000000000001", archived: false);
        var detail = Detail(session.SessionId, unidentifiedSubagent: false);
        var comparison = new LocalWorkspaceComparisonDetailContribution(
            detail.Nodes, ["source-2", "source-1", "source-1"], ["adapter-1"],
            detail.CanonicalRevisionInput!, detail.SkillRegistryGenerationIdentity!);

        var fact = LocalComparisonInputProjection.MapSessionFact(session, detail, comparison, new string('a', 64), false);

        Assert.Equal(["source-1", "source-2"], fact.Conditions["source_versions"].Values);
        Assert.Equal(["adapter-1"], fact.Conditions["adapter_versions"].Values);
        Assert.DoesNotContain("1", fact.Conditions["source_versions"].Values);
    }

    internal static LocalRepositoryScopeSessionSnapshot ScopeSession(string sessionId, bool archived)
    {
        var activity = new LocalWorkspaceActivityFacts(Fact(0), Fact(1), Fact(1), new("source_unsupported", null), new("not_observed", null));
        var tokens = new LocalWorkspaceTokenFacts("session_run", "recorded", 1, 1, Fact(10), Fact(5), Fact(15), new("not_observed", null), Fact(0), new("source_unsupported", null), Fact(10), Fact(0));
        var row = new LocalWorkspaceProjectionRow(sessionId, 1, 1, "not_observed", null, "completed", "full", new("recorded", ["synthetic"]), new("recorded", ["test-model"]), activity, tokens, "recorded", "2026-08-29T00:00:00.0000000+00:00", "2026-08-29T00:00:01.0000000+00:00", "2026-08-29T00:00:01.0000000+00:00", 1000, [], "revision");
        return new(sessionId, row, 1, LocalRepositoryScopeAssignmentState.Assigned, LocalRepositoryScopeAssignmentAuthority.Manual, RepositoryId, [], true, false, true, archived ? LocalArchiveState.Archived : LocalArchiveState.Active, 1, !archived, archived ? "session_archived" : null, 1);
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

    private static LocalWorkspaceComparisonDetailContribution ComparisonDetail(IReadOnlyList<LocalWorkspaceNodeDetail> nodes) =>
        new(nodes, [], [], "revision", new string('b', 64));

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

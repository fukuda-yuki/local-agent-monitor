using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalComparisonCandidateState
{
    Included,
    RepositoryMismatch,
    RepositoryArchived,
    ProjectionUnavailable,
    UnsupportedSelection,
    WorkspaceTooLarge,
}

internal sealed record LocalComparisonProjectionCandidate(
    string SessionId,
    string RepositoryId,
    LocalComparisonCandidateState State,
    bool IsArchived,
    string ArchiveState,
    long? SessionArchiveRevision,
    string? AssignedRepositoryArchiveState,
    long? AssignedRepositoryArchiveRevision,
    string? ArchiveExclusionReason,
    IReadOnlyList<string>? Sources,
    string? SourcesState,
    IReadOnlyList<string>? Models,
    string? ModelsState,
    int? ProjectionVersion,
    string? Completeness,
    IReadOnlyList<string>? MetricCoverage,
    IReadOnlyList<string>? SourceApplicationVersions,
    IReadOnlyList<string>? AdapterVersions,
    long SessionRevision,
    string ProjectionRevision);

internal sealed record LocalComparisonRequestedOccurrence(string Cohort, int RequestOrdinal, string SessionId);
internal sealed record LocalComparisonProjectionExclusion(string Cohort, int RequestOrdinal, string SessionId, string Reason, LocalComparisonProjectionCandidate? Metadata = null);
internal sealed record LocalComparisonProjectionPreview(
    bool Valid,
    string SelectionSha256,
    string PreviewRevision,
    IReadOnlyList<LocalComparisonRequestedOccurrence> Requested,
    IReadOnlyList<LocalComparisonProjectionCandidate> Included,
    IReadOnlyList<LocalComparisonProjectionExclusion> Excluded);

internal static class LocalComparisonInputProjection
{
    private const string SelectionDomain = "copilot-agent-observability/local-comparison-selection/v1";
    private const string PreviewDomain = "copilot-agent-observability/local-comparison-preview/v1";
    private const string NamedIdentityDomain = "copilot-agent-observability/local-comparison-named-identity/v1";
    private const string UnidentifiedSubagentIdentity = "unidentified-subagent";
    private const string UnidentifiedDisplayName = "識別名なし";

    internal static LocalComparisonProjectionPreview Project(
        string repositoryId,
        IReadOnlyList<string> cohortA,
        IReadOnlyList<string> cohortB,
        bool includeArchived,
        IReadOnlyList<LocalComparisonProjectionCandidate> candidates,
        string repositoryRevision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryId);
        ArgumentNullException.ThrowIfNull(cohortA);
        ArgumentNullException.ThrowIfNull(cohortB);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRevision);
        if (cohortA.Count is < 1 or > 199 || cohortB.Count is < 1 or > 199 || cohortA.Count + cohortB.Count > 200)
            throw new ArgumentException("local_comparison_input_invalid");

        var requested = new List<LocalComparisonRequestedOccurrence>(cohortA.Count + cohortB.Count);
        AddRequested("a", cohortA);
        AddRequested("b", cohortB);
        var byId = candidates.ToDictionary(static item => item.SessionId, StringComparer.Ordinal);
        var aIds = cohortA.ToHashSet(StringComparer.Ordinal);
        var included = new List<(string Cohort, LocalComparisonProjectionCandidate Candidate)>();
        var excluded = new List<LocalComparisonProjectionExclusion>();
        Resolve("a", cohortA);
        Resolve("b", cohortB);
        var orderedIncludedEntries = included
            .OrderBy(static item => item.Cohort, StringComparer.Ordinal)
            .ThenBy(static item => item.Candidate.SessionId, StringComparer.Ordinal)
            .ToArray();
        var orderedIncluded = orderedIncludedEntries
            .Select(static item => item.Candidate)
            .ToArray();
        var selectionSha256 = Hash(SelectionDomain, orderedIncludedEntries.SelectMany(static item => new[] { item.Cohort, item.Candidate.SessionId }));
        var previewValues = new List<string> { repositoryId, repositoryRevision, includeArchived ? "1" : "0", selectionSha256 };
        foreach (var candidate in orderedIncluded)
        {
            AddMetadata(candidate);
        }
        foreach (var item in excluded)
        {
            previewValues.AddRange([item.Cohort, item.RequestOrdinal.ToString(System.Globalization.CultureInfo.InvariantCulture), item.SessionId, item.Reason]);
            if (item.Metadata is not null) AddMetadata(item.Metadata);
        }
        var valid = included.Any(static item => item.Cohort == "a")
            && included.Any(static item => item.Cohort == "b")
            && excluded.All(static item => item.Reason is "session_archived" or "repository_archived");
        return new(valid, selectionSha256, Hash(PreviewDomain, previewValues), Array.AsReadOnly(requested.ToArray()), Array.AsReadOnly(orderedIncluded), Array.AsReadOnly(excluded.ToArray()));

        void AddRequested(string cohort, IReadOnlyList<string> ids)
        {
            for (var index = 0; index < ids.Count; index++) requested.Add(new(cohort, index + 1, ids[index]));
        }

        void Resolve(string cohort, IReadOnlyList<string> ids)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < ids.Count; index++)
            {
                var id = ids[index];
                string? reason = null;
                byId.TryGetValue(id, out var candidate);
                if (!seen.Add(id)) reason = "duplicate";
                else if (cohort == "b" && aIds.Contains(id)) reason = "cohort_overlap";
                else if (candidate is null) reason = "session_not_found";
                else if (!string.Equals(candidate.RepositoryId, repositoryId, StringComparison.Ordinal)) reason = "repository_mismatch";
                else if (!includeArchived && candidate.ArchiveExclusionReason is not null) reason = candidate.ArchiveExclusionReason;
                else if (candidate.State != LocalComparisonCandidateState.Included) reason = ExclusionToken(candidate.State);
                else included.Add((cohort, candidate));
                if (reason is not null) excluded.Add(new(cohort, index + 1, id, reason,
                    candidate is not null && string.Equals(candidate.RepositoryId, repositoryId, StringComparison.Ordinal) ? candidate : null));
            }
        }

        void AddMetadata(LocalComparisonProjectionCandidate candidate)
        {
            previewValues.Add(candidate.SessionId); previewValues.Add(candidate.RepositoryId);
            previewValues.Add(candidate.ArchiveState); AddNullable(candidate.SessionArchiveRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddNullable(candidate.AssignedRepositoryArchiveState); AddNullable(candidate.AssignedRepositoryArchiveRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddNullable(candidate.ArchiveExclusionReason); AddNullable(candidate.ProjectionVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddNullable(candidate.Completeness); previewValues.Add(candidate.SessionRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddNullable(candidate.ProjectionRevision); AddNullable(candidate.SourcesState); AddList(candidate.Sources); AddNullable(candidate.ModelsState);
            AddList(candidate.Models); AddList(candidate.MetricCoverage); AddList(candidate.SourceApplicationVersions); AddList(candidate.AdapterVersions);

            void AddNullable(string? value) { previewValues.Add(value is null ? "0" : "1"); if (value is not null) previewValues.Add(value); }
            void AddList(IReadOnlyList<string>? values)
            {
                if (values is null) { previewValues.Add("0"); return; }
                previewValues.Add("1"); var canonical = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                previewValues.Add(canonical.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)); previewValues.AddRange(canonical);
            }
        }
    }

    internal static string ExclusionToken(LocalComparisonCandidateState state) => state switch
    {
        LocalComparisonCandidateState.RepositoryMismatch => "repository_mismatch",
        LocalComparisonCandidateState.RepositoryArchived => "repository_archived",
        LocalComparisonCandidateState.ProjectionUnavailable => "projection_unavailable",
        LocalComparisonCandidateState.UnsupportedSelection => "unsupported_selection",
        LocalComparisonCandidateState.WorkspaceTooLarge => "workspace_too_large",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    internal static LocalComparisonSessionFact MapSessionFact(
        LocalRepositoryScopeSessionSnapshot session,
        LocalWorkspaceSessionDetailContribution detail,
        LocalWorkspaceComparisonDetailContribution comparisonDetail,
        string workspaceRevision,
        bool includeArchived,
        bool assignedRepositoryArchived = false)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(comparisonDetail);
        if (session.Session is not LocalWorkspaceProjectionRow row || workspaceRevision.Length != 64)
            throw new LocalComparisonSelectionUnavailableException();
        var sessionReference = new LocalComparisonSourceReference("workspace_session", session.SessionId, null, null, null, workspaceRevision);
        var scalars = LocalComparisonRegistryV1.RequiredSessionScalarKeys.ToDictionary(
            static key => key,
            _ => Missing(LocalComparisonFactState.SourceUnsupported),
            StringComparer.Ordinal);
        scalars["input_tokens"] = Observed(row.Tokens.Input, sessionReference);
        scalars["output_tokens"] = Observed(row.Tokens.Output, sessionReference);
        scalars["total_tokens"] = Observed(row.Tokens.Total, sessionReference);
        scalars["cache_read_tokens"] = Observed(row.Tokens.CacheRead, sessionReference);
        scalars["cache_creation_tokens"] = Observed(row.Tokens.CacheCreation, sessionReference);
        foreach (var (metric, component) in new[] { ("input_tokens", "input"), ("output_tokens", "output"), ("total_tokens", "total"), ("cache_read_tokens", "cache_read"), ("cache_creation_tokens", "cache_creation") })
        {
            if (row.Tokens.Observations?.TryGetValue(component, out var observation) == true)
            {
                var original = scalars[metric];
                scalars[metric] = new(original.Observation, sessionReference)
                {
                    TokenObservation = observation,
                    CacheRatioObservation = component == "cache_read" ? row.Tokens.Observations["cache_read_ratio_basis_points"] : null,
                };
            }
        }
        scalars["session_duration"] = row is { Status: "active", TimingState: "recorded", EndedAt: null, DurationMilliseconds: null }
            ? Missing(LocalComparisonFactState.NotObserved)
            : Observed(row.TimingState, row.DurationMilliseconds, sessionReference);
        scalars["execution_count"] = Count(detail.Executions.Count, sessionReference);
        scalars["tool_call_count"] = Observed(row.Activity.Tool, sessionReference);
        scalars["skill_invocation_count"] = Observed(row.Activity.Skill, sessionReference);
        scalars["subagent_start_count"] = Observed(row.Activity.Subagent, sessionReference);
        scalars["error_count"] = Observed(row.Activity.Error, sessionReference);
        scalars["retry_count"] = Observed(row.Activity.Retry, sessionReference);
        scalars["error_session_count"] = Presence(scalars["error_count"], sessionReference);
        scalars["retry_session_count"] = Presence(scalars["retry_count"], sessionReference);

        var families = new[]
        {
            Family("skill", row.Activity.Skill),
            Family("tool", row.Activity.Tool),
            Family("subagent", row.Activity.Subagent),
        };
        var conditions = new Dictionary<string, LocalComparisonConditionFact>(StringComparer.Ordinal)
        {
            ["sources"] = Condition(row.Sources.State, row.Sources.Values, sessionReference),
            ["models"] = Condition(row.Models.State, row.Models.Values, sessionReference),
            ["source_versions"] = Condition(comparisonDetail.SourceApplicationVersions is null ? "source_unsupported" : comparisonDetail.SourceApplicationVersions.Count == 0 ? "not_observed" : "recorded", comparisonDetail.SourceApplicationVersions ?? [], sessionReference),
            ["adapter_versions"] = Condition(comparisonDetail.AdapterVersions is null ? "source_unsupported" : comparisonDetail.AdapterVersions.Count == 0 ? "not_observed" : "recorded", comparisonDetail.AdapterVersions ?? [], sessionReference),
            ["completeness"] = Condition("recorded", [row.Completeness], sessionReference),
        };
        var observedAt = DateTimeOffset.TryParse(row.LastSeenAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var parsed)
            ? parsed.ToUniversalTime() : (DateTimeOffset?)null;
        var targetState = observedAt is null ? State(row.TimingState) : LocalComparisonFactState.Recorded;
        return new(
            session.SessionId,
            session.RepositoryId ?? throw new LocalComparisonSelectionUnavailableException(),
            workspaceRevision,
            session.IsEffectivelyEligible || includeArchived && session.ArchiveExclusionReason is "session_archived" or "repository_archived",
            session.ArchiveState == LocalArchiveState.Archived,
            scalars,
            Array.AsReadOnly(families),
            conditions,
            new(LocalComparisonFactState.Recorded, sessionReference, targetState, observedAt, observedAt is null ? null : sessionReference),
            includeArchived && (session.ArchiveState == LocalArchiveState.Archived || assignedRepositoryArchived),
            assignedRepositoryArchived);

        LocalComparisonNamedFamilyFact Family(string family, LocalWorkspaceFact<long> aggregate)
        {
            var familyNodes = comparisonDetail.Nodes.Where(node => node.Kind == family).ToArray();
            var admitted = familyNodes
                .Select(node => (Node: node, Identity: SemanticIdentity(node, family)))
                .Where(static item => item.Identity is not null)
                .GroupBy(static item => item.Identity!, StringComparer.Ordinal);
            var items = admitted.Select(group =>
            {
                var observations = group.OrderBy(static item => CanonicalNodeReference(item.Node), StringComparer.Ordinal).ToArray();
                var display = observations[0].Node.NameState == "recorded" ? observations[0].Node.NameText! : UnidentifiedDisplayName;
                var identityKey = Hash(NamedIdentityDomain, [family, group.Key]);
                var reference = References(observations[0].Node, workspaceRevision)[0];
                var values = LocalComparisonRegistryV1.NamedFieldKeys[family].ToDictionary(
                    static key => key, _ => Missing(LocalComparisonFactState.SourceUnsupported), StringComparer.Ordinal);
                if (family == "skill") values["invocation_count"] = Aggregate(observations, _ => (LocalComparisonFactState.Recorded, 1m), workspaceRevision);
                if (family == "tool")
                {
                    values["call_count"] = Aggregate(observations, _ => (LocalComparisonFactState.Recorded, 1m), workspaceRevision);
                    values["failure_count"] = Aggregate(observations, item => ToolFailure(item.Node), workspaceRevision);
                    values["retry_count"] = Aggregate(observations, item => Observation(item.Node.Activity.Retry), workspaceRevision);
                }
                if (family == "subagent")
                {
                    values["start_count"] = Aggregate(observations, item => LifecycleObservation(item.Node.SubagentLifecycle?.StartedState), workspaceRevision);
                    values["completed_count"] = Aggregate(observations, item => LifecycleObservation(item.Node.SubagentLifecycle?.CompletedState), workspaceRevision);
                    values["failed_count"] = Aggregate(observations, item => LifecycleObservation(item.Node.SubagentLifecycle?.FailedState), workspaceRevision);
                    values["recorded_tokens"] = Aggregate(observations, item => Observation(item.Node.Tokens.Total), workspaceRevision);
                }
                return new LocalComparisonNamedItem(family, identityKey, Normalize(display), display, values, reference);
            }).OrderBy(static item => item.SortKey, StringComparer.Ordinal).ThenBy(static item => item.IdentityKey, StringComparer.Ordinal).ToArray();
            var missingNames = family is "skill" or "tool"
                ? familyNodes.Where(node => SemanticIdentity(node, family) is null).ToArray()
                : [];
            var state = missingNames.Length > 0
                ? MissingNameState(missingNames)
                : items.Length > 0 ? LocalComparisonFactState.Recorded : State(aggregate.State);
            if (items.Length == 0 && state == LocalComparisonFactState.Recorded)
                state = aggregate.Value == 0 ? LocalComparisonFactState.ExplicitZero : LocalComparisonFactState.CaptureGap;
            return new(family, state, Array.AsReadOnly(items), state is LocalComparisonFactState.Recorded or LocalComparisonFactState.ExplicitZero ? sessionReference : null);
        }
    }

    private static LocalComparisonFactState MissingNameState(IReadOnlyList<LocalWorkspaceNodeDetail> nodes)
    {
        var states = nodes.Select(node => State(node.NameState))
            .Where(static state => state is not LocalComparisonFactState.Recorded and not LocalComparisonFactState.ExplicitZero)
            .Order()
            .ToArray();
        return states.Length == 0 ? LocalComparisonFactState.CaptureGap : states[0];
    }

    private static string? SemanticIdentity(LocalWorkspaceNodeDetail node, string family) =>
        node.NameState == "recorded" && node.NameText is not null
            ? node.NameText
            : family == "subagent" ? UnidentifiedSubagentIdentity : null;

    private static string Normalize(string value) =>
        string.Join(' ', value.Normalize(NormalizationForm.FormKC)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static string CanonicalNodeReference(LocalWorkspaceNodeDetail node) =>
        string.Join('\n', node.SourceKind, node.SourceIdentity, node.TraceId, node.SpanId, node.EventId, node.ExecutionId, node.NodeId);

    private static IReadOnlyList<LocalComparisonSourceReference> References(LocalWorkspaceNodeDetail node, string workspaceRevision)
    {
        var references = new List<LocalComparisonSourceReference>
        {
            new("workspace_node", node.NodeId, null, null, null, workspaceRevision),
        };
        foreach (var source in (node.SourceReferences ?? []).Concat(node.ToolMetadata?.SourceReferences ?? []).Concat(node.SubagentLifecycle?.SourceReferences ?? []))
        {
            references.Add(new(
                source.SourceKind,
                source.SourceIdentity,
                source.TraceId,
                source.SpanId,
                source.EventId,
                source.RevisionInput is { Length: 64 } ? source.RevisionInput : workspaceRevision));
        }
        return Array.AsReadOnly(references
            .Distinct()
            .OrderBy(static item => item.SourceKind, StringComparer.Ordinal)
            .ThenBy(static item => item.SourceIdentity, StringComparer.Ordinal)
            .ThenBy(static item => item.TraceId, StringComparer.Ordinal)
            .ThenBy(static item => item.SpanId, StringComparer.Ordinal)
            .ThenBy(static item => item.EventId, StringComparer.Ordinal)
            .ThenBy(static item => item.RevisionSha256, StringComparer.Ordinal)
            .ToArray());
    }

    private static LocalComparisonObservedScalar Aggregate(
        IReadOnlyList<(LocalWorkspaceNodeDetail Node, string? Identity)> observations,
        Func<(LocalWorkspaceNodeDetail Node, string? Identity), (LocalComparisonFactState State, decimal? Value)> selector,
        string workspaceRevision)
    {
        var selected = observations.Select(item => (Item: item, Fact: selector(item))).ToArray();
        var unavailable = selected
            .Where(static item => item.Fact.State is not LocalComparisonFactState.Recorded and not LocalComparisonFactState.ExplicitZero)
            .OrderBy(static item => item.Fact.State)
            .FirstOrDefault();
        var state = unavailable.Item.Node is null ? LocalComparisonFactState.Recorded : unavailable.Fact.State;
        var value = unavailable.Item.Node is null ? selected.Sum(static item => item.Fact.Value!.Value) : (decimal?)null;
        if (state == LocalComparisonFactState.Recorded && value == 0) state = LocalComparisonFactState.ExplicitZero;
        var evidence = selected.SelectMany(item => References(item.Item.Node, workspaceRevision).Select(reference =>
            new LocalComparisonFactEvidence(
                item.Fact.State,
                reference,
                item.Fact.Value is null ? null : LocalComparisonScalarCalculator.CanonicalDecimal(item.Fact.Value.Value))))
            .ToArray();
        return new(new(state, value), Array.AsReadOnly(evidence));
    }

    private static (LocalComparisonFactState State, decimal? Value) ToolFailure(LocalWorkspaceNodeDetail node) => node.Status switch
    {
        "failed" => (LocalComparisonFactState.Recorded, 1m),
        "completed" => (LocalComparisonFactState.ExplicitZero, 0m),
        _ => (LocalComparisonFactState.SourceUnsupported, null),
    };

    private static (LocalComparisonFactState State, decimal? Value) LifecycleObservation(string? state) =>
        state == "recorded" ? (LocalComparisonFactState.Recorded, 1m) : (State(state ?? "source_unsupported"), null);

    private static (LocalComparisonFactState State, decimal? Value) Observation(LocalWorkspaceFact<long> fact)
    {
        var state = State(fact.State);
        if (state == LocalComparisonFactState.Recorded && fact.Value == 0) state = LocalComparisonFactState.ExplicitZero;
        return state is LocalComparisonFactState.Recorded or LocalComparisonFactState.ExplicitZero
            ? (state, fact.Value) : (state, null);
    }

    private static LocalComparisonObservedScalar Presence(LocalComparisonObservedScalar input, LocalComparisonSourceReference reference) =>
        input.Observation.Value is decimal value ? Count(value > 0 ? 1 : 0, reference) : Missing(input.Observation.State);

    private static LocalComparisonObservedScalar Count(long value, LocalComparisonSourceReference reference) =>
        new(new(value == 0 ? LocalComparisonFactState.ExplicitZero : LocalComparisonFactState.Recorded, value), reference);

    private static LocalComparisonObservedScalar Observed(LocalWorkspaceFact<long> fact, LocalComparisonSourceReference reference) =>
        Observed(fact.State, fact.Value, reference);

    private static LocalComparisonObservedScalar Observed(string state, long? value, LocalComparisonSourceReference reference)
    {
        var mapped = State(state);
        if (mapped == LocalComparisonFactState.Recorded && value == 0) mapped = LocalComparisonFactState.ExplicitZero;
        return mapped is LocalComparisonFactState.Recorded or LocalComparisonFactState.ExplicitZero
            ? new(new(mapped, value), reference) : Missing(mapped);
    }

    private static LocalComparisonObservedScalar Missing(LocalComparisonFactState state) =>
        new(new(state, null), Reference: null);

    private static LocalComparisonConditionFact Condition(string state, IReadOnlyList<string> values, LocalComparisonSourceReference reference)
    {
        var mapped = State(state);
        if (mapped == LocalComparisonFactState.Recorded && values.Count == 0) mapped = LocalComparisonFactState.ExplicitZero;
        return new(mapped, mapped is LocalComparisonFactState.Recorded ? Array.AsReadOnly(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()) : [], mapped is LocalComparisonFactState.Recorded or LocalComparisonFactState.ExplicitZero ? reference : null);
    }

    private static LocalComparisonFactState State(string state) => state switch
    {
        "recorded" => LocalComparisonFactState.Recorded,
        "not_observed" or "missing" => LocalComparisonFactState.NotObserved,
        "source_unsupported" => LocalComparisonFactState.SourceUnsupported,
        "capture_gap" => LocalComparisonFactState.CaptureGap,
        "certification_pending" => LocalComparisonFactState.CertificationPending,
        "not_captured" => LocalComparisonFactState.NotCaptured,
        "expired" => LocalComparisonFactState.Expired,
        "deleted" => LocalComparisonFactState.Deleted,
        "read_denied" => LocalComparisonFactState.ReadDenied,
        "too_large" => LocalComparisonFactState.TooLarge,
        "invalid" or "inconsistent" => LocalComparisonFactState.ProjectionInvalid,
        _ => LocalComparisonFactState.SourceUnsupported,
    };

    private static string Hash(string domain, IEnumerable<string> values)
    {
        using var stream = new MemoryStream();
        Write(domain);
        foreach (var value in values) Write(value);
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();

        void Write(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
        }
    }
}

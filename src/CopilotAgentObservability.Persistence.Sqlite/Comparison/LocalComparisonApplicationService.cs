using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalComparisonSourceReference(
    string SourceKind,
    string? SourceIdentity,
    string? TraceId,
    string? SpanId,
    string? EventId,
    string RevisionSha256);

internal sealed record LocalComparisonFactEvidence(
    LocalComparisonFactState State,
    LocalComparisonSourceReference? Reference,
    string? ConsumedValue = null);

internal sealed record LocalComparisonObservedScalar
{
    internal LocalComparisonObservedScalar(
        LocalComparisonScalarObservation observation,
        LocalComparisonSourceReference? Reference)
        : this(
            observation,
            Array.AsReadOnly(new[]
            {
                new LocalComparisonFactEvidence(
                    observation.State,
                    Reference,
                    observation.Value is null
                        ? null
                        : LocalComparisonScalarCalculator.CanonicalDecimal(observation.Value.Value)),
            }))
    {
    }

    internal LocalComparisonObservedScalar(
        LocalComparisonScalarObservation observation,
        IReadOnlyList<LocalComparisonFactEvidence> evidence)
    {
        Observation = observation;
        Evidence = evidence;
    }

    internal LocalComparisonScalarObservation Observation { get; }
    internal IReadOnlyList<LocalComparisonFactEvidence> Evidence { get; }
}

internal sealed record LocalComparisonSessionTargetFact(
    LocalComparisonFactState ValueAvailabilityState,
    LocalComparisonSourceReference? ValueAvailabilityReference,
    LocalComparisonFactState ObservedAtState,
    DateTimeOffset? ObservedAt,
    LocalComparisonSourceReference? ObservedAtReference);

internal sealed record LocalComparisonNamedItem(
    string Family,
    string IdentityKey,
    string SortKey,
    string DisplayName,
    IReadOnlyDictionary<string, LocalComparisonObservedScalar> Values,
    LocalComparisonSourceReference Reference);

internal sealed record LocalComparisonNamedFamilyFact(
    string Family,
    LocalComparisonFactState State,
    IReadOnlyList<LocalComparisonNamedItem> Items,
    LocalComparisonSourceReference? Reference);

internal sealed record LocalComparisonConditionFact(
    LocalComparisonFactState State,
    IReadOnlyList<string> Values,
    LocalComparisonSourceReference? Reference);

internal sealed record LocalComparisonSessionFact(
    string SessionId,
    string RepositoryId,
    string WorkspaceRevision,
    bool IsSelectable,
    bool IsArchived,
    IReadOnlyDictionary<string, LocalComparisonObservedScalar> Scalars,
    IReadOnlyList<LocalComparisonNamedFamilyFact> NamedFamilies,
    IReadOnlyDictionary<string, LocalComparisonConditionFact> Conditions,
    LocalComparisonSessionTargetFact Target,
    bool IsArchiveInclusionExplicit = false);

internal sealed record LocalComparisonCohortDraft(
    IReadOnlyList<LocalComparisonSessionFact> Members,
    int ExcludedSessionCount);

internal sealed record LocalComparisonDraft(
    string RepositoryId,
    LocalComparisonCohortDraft CohortA,
    LocalComparisonCohortDraft CohortB,
    byte[] ScopeConditionSha256);

internal enum LocalComparisonCreateStatus
{
    Accepted,
    SelectionInvalid,
    SelectionUnavailable,
    TooLarge,
    PersistenceBusy,
}

internal sealed record LocalComparisonCreateResult(
    LocalComparisonCreateStatus Status,
    LocalComparisonSnapshotWrite? Snapshot);

internal sealed class LocalComparisonApplicationService
{
    private const int MaximumReceiptBytes = 1_048_576;
    private const int MaximumEvidenceRows = 1_048_576 / 64;
    private readonly SqliteLocalComparisonStore? store;
    private readonly TimeProvider timeProvider;
    private readonly Func<DateTimeOffset, string> comparisonIdFactory;

    internal LocalComparisonApplicationService(
        SqliteLocalComparisonStore? store,
        TimeProvider? timeProvider = null,
        Func<DateTimeOffset, string>? comparisonIdFactory = null)
    {
        this.store = store;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.comparisonIdFactory = comparisonIdFactory
            ?? (static at => Guid.CreateVersion7(at).ToString("D", CultureInfo.InvariantCulture));
    }

    internal LocalComparisonCreateResult Prepare(LocalComparisonDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        try
        {
            var input = LocalComparisonApplicationValidation.Freeze(draft);
            var createdAt = timeProvider.GetUtcNow();
            if (createdAt.Offset != TimeSpan.Zero)
                createdAt = createdAt.ToUniversalTime();
            var comparisonId = comparisonIdFactory(createdAt);
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(comparisonId))
                return new(LocalComparisonCreateStatus.SelectionInvalid, Snapshot: null);
            var snapshot = BuildSnapshot(comparisonId, createdAt, input);
            return new(LocalComparisonCreateStatus.Accepted, snapshot);
        }
        catch (LocalComparisonSelectionUnavailableException)
        {
            return new(LocalComparisonCreateStatus.SelectionUnavailable, Snapshot: null);
        }
        catch (LocalComparisonTooLargeException)
        {
            return new(LocalComparisonCreateStatus.TooLarge, Snapshot: null);
        }
        catch (OverflowException)
        {
            return new(LocalComparisonCreateStatus.TooLarge, Snapshot: null);
        }
        catch (ArgumentException)
        {
            return new(LocalComparisonCreateStatus.SelectionInvalid, Snapshot: null);
        }
        catch (InvalidOperationException exception) when (
            exception.Message.StartsWith("local_comparison_input_", StringComparison.Ordinal))
        {
            return new(LocalComparisonCreateStatus.SelectionInvalid, Snapshot: null);
        }
    }

    internal LocalComparisonCreateResult Create(
        LocalComparisonDraft draft,
        CancellationToken cancellationToken)
    {
        if (store is null)
            throw new InvalidOperationException("local_comparison_store_missing");
        var prepared = Prepare(draft);
        if (prepared.Status != LocalComparisonCreateStatus.Accepted)
            return prepared;
        var status = store.Accept(prepared.Snapshot!, cancellationToken);
        return status == LocalComparisonAcceptStatus.PersistenceBusy
            ? new(LocalComparisonCreateStatus.PersistenceBusy, Snapshot: null)
            : prepared;
    }

    internal static LocalComparisonSnapshotWrite BuildSnapshot(
        string comparisonId,
        DateTimeOffset createdAt,
        FrozenInput input)
    {
        var expiresAt = createdAt.AddHours(24);
        var selection = LocalComparisonSelectionFrame.Create(
            input.CohortA.Select(static item => item.SessionId).ToArray(),
            input.CohortB.Select(static item => item.SessionId).ToArray());
        var memberships = BuildMemberships(comparisonId, input);
        var results = new ResultAccumulator();
        var evidence = new List<LocalComparisonStoredEvidence>();
        var nextOrdinal = 1;

        AddTargetRows(comparisonId, input, results, evidence, ref nextOrdinal);
        for (var section = 2; section <= 9; section++)
        {
            foreach (var metric in LocalComparisonRegistryV1.Metrics.Where(item =>
                item.SectionOrdinal == section))
            {
                if (metric.Key == "metric_availability")
                    AddMetricAvailabilityRow(comparisonId, input, results, evidence, ref nextOrdinal);
                else if (section == 9)
                    AddConditionRow(comparisonId, metric.Key, input, results, evidence, ref nextOrdinal);
                else
                    AddScalarRow(comparisonId, metric, input, results, evidence, ref nextOrdinal);
            }
            foreach (var family in LocalComparisonRegistryV1.NamedFamilies.Where(item =>
                item.SectionOrdinal == section))
            {
                AddNamedRows(comparisonId, family, input, results, evidence, ref nextOrdinal);
            }
        }
        if (evidence.Count > MaximumEvidenceRows)
            throw new LocalComparisonTooLargeException();

        var receipt = LocalComparisonReceiptFrame.CreateResult(
            comparisonId,
            input.RepositoryId,
            createdAt,
            expiresAt,
            selection.Bytes,
            selection.Sha256,
            input.ScopeConditionSha256,
            memberships,
            results.Rows,
            evidence);
        var allResults = new LocalComparisonStoredResult[results.Rows.Count + 1];
        allResults[0] = receipt;
        for (var index = 0; index < results.Rows.Count; index++)
            allResults[index + 1] = results.Rows[index];
        return new(
            comparisonId,
            input.RepositoryId,
            createdAt,
            expiresAt,
            selection.Bytes,
            selection.Sha256,
            input.ScopeConditionSha256.ToArray(),
            memberships,
            Array.AsReadOnly(allResults),
            Array.AsReadOnly(evidence.ToArray()));
    }

    private static IReadOnlyList<LocalComparisonStoredMembership> BuildMemberships(
        string comparisonId,
        FrozenInput input)
    {
        var result = new List<LocalComparisonStoredMembership>(
            input.CohortA.Count + input.CohortB.Count);
        var materializedFactBytes = 0;
        Add("a", input.CohortA);
        Add("b", input.CohortB);
        return Array.AsReadOnly(result.ToArray());

        void Add(string cohort, IReadOnlyList<LocalComparisonSessionFact> sessions)
        {
            for (var ordinal = 0; ordinal < sessions.Count; ordinal++)
            {
                var session = sessions[ordinal];
                var factFrame = LocalComparisonFactFrame.Create(session);
                if (factFrame.Length > MaximumReceiptBytes - materializedFactBytes)
                    throw new LocalComparisonTooLargeException();
                materializedFactBytes += factFrame.Length;
                result.Add(LocalComparisonStoredMembership.Create(
                    comparisonId,
                    cohort,
                    ordinal,
                    session.SessionId,
                    session.WorkspaceRevision,
                    factFrame));
            }
        }
    }

    private static void AddTargetRows(
        string comparisonId,
        FrozenInput input,
        ResultAccumulator results,
        List<LocalComparisonStoredEvidence> evidence,
        ref int nextOrdinal)
    {
        Add(nextOrdinal++, "included_session_count", input.CohortA.Count, input.CohortB.Count, includeEvidence: true);
        Add(nextOrdinal++, "excluded_session_count", input.ExcludedA, input.ExcludedB, includeEvidence: false);
        Add(nextOrdinal++, "available_session_count",
            input.CohortA.Count(static item => item.Target.ValueAvailabilityState == LocalComparisonFactState.Recorded),
            input.CohortB.Count(static item => item.Target.ValueAvailabilityState == LocalComparisonFactState.Recorded),
            includeEvidence: true,
            evidenceSelector: static item => new LocalComparisonFactEvidence(
                item.Target.ValueAvailabilityState,
                item.Target.ValueAvailabilityReference));
        AddPeriod(nextOrdinal++);
        AddArchiveInclusion(nextOrdinal++);

        void Add(
            int ordinal,
            string key,
            int a,
            int b,
            bool includeEvidence,
            Func<LocalComparisonSessionFact, LocalComparisonFactEvidence>? evidenceSelector = null)
        {
            var values = new List<KeyValuePair<string, string>>
            {
                Pair("a_count", a),
                Pair("b_count", b),
                Pair("absolute_difference", b - a),
            };
            if (evidenceSelector is not null)
            {
                values.Add(new("a_unavailable_states", UnavailableStates(
                    input.CohortA.Select(item => evidenceSelector(item).State))));
                values.Add(new("b_unavailable_states", UnavailableStates(
                    input.CohortB.Select(item => evidenceSelector(item).State))));
            }
            results.Add(LocalComparisonStoredResult.Create(
                comparisonId, ordinal, 1, "scalar", key, values));
            if (!includeEvidence)
                return;
            if (evidenceSelector is null)
            {
                AddSelectionEvidence(comparisonId, ordinal, "a", input.CohortA, evidence);
                AddSelectionEvidence(comparisonId, ordinal, "b", input.CohortB, evidence);
                return;
            }
            var evidenceOrdinal = 0;
            AddEvidence(comparisonId, ordinal, "value", "a", input.CohortA,
                input.CohortA.Select(evidenceSelector).Select(static item =>
                    (IReadOnlyList<LocalComparisonFactEvidence>)Array.AsReadOnly(new[] { item })).ToArray(),
                evidence, ref evidenceOrdinal);
            AddEvidence(comparisonId, ordinal, "value", "b", input.CohortB,
                input.CohortB.Select(evidenceSelector).Select(static item =>
                    (IReadOnlyList<LocalComparisonFactEvidence>)Array.AsReadOnly(new[] { item })).ToArray(),
                evidence, ref evidenceOrdinal);
        }

        void AddPeriod(int ordinal)
        {
            var values = new[]
            {
                Pair("a_session_count", input.CohortA.Count),
                Pair("a_available_count", input.CohortA.Count(static item => item.Target.ObservedAt is not null)),
                new KeyValuePair<string, string>("a_start", Instant(input.CohortA, minimum: true)),
                new KeyValuePair<string, string>("a_end", Instant(input.CohortA, minimum: false)),
                new KeyValuePair<string, string>("a_unavailable_states", UnavailableStates(
                    input.CohortA.Select(static item => item.Target.ObservedAtState))),
                Pair("b_session_count", input.CohortB.Count),
                Pair("b_available_count", input.CohortB.Count(static item => item.Target.ObservedAt is not null)),
                new KeyValuePair<string, string>("b_start", Instant(input.CohortB, minimum: true)),
                new KeyValuePair<string, string>("b_end", Instant(input.CohortB, minimum: false)),
                new KeyValuePair<string, string>("b_unavailable_states", UnavailableStates(
                    input.CohortB.Select(static item => item.Target.ObservedAtState))),
            };
            results.Add(LocalComparisonStoredResult.Create(
                comparisonId, ordinal, 1, "condition", "period", values));
            var evidenceOrdinal = 0;
            AddEvidence(comparisonId, ordinal, "observed_at", "a", input.CohortA,
                TargetTimeEvidence(input.CohortA), evidence, ref evidenceOrdinal);
            AddEvidence(comparisonId, ordinal, "observed_at", "b", input.CohortB,
                TargetTimeEvidence(input.CohortB), evidence, ref evidenceOrdinal);
        }

        void AddArchiveInclusion(int ordinal)
        {
            var a = input.CohortA.Count(static item => item.IsArchived);
            var b = input.CohortB.Count(static item => item.IsArchived);
            results.Add(LocalComparisonStoredResult.Create(
                comparisonId, ordinal, 1, "condition", "archived_inclusion",
                new[]
                {
                    Pair("a_included_count", a),
                    new KeyValuePair<string, string>("a_includes_archived", a == 0 ? "false" : "true"),
                    Pair("b_included_count", b),
                    new KeyValuePair<string, string>("b_includes_archived", b == 0 ? "false" : "true"),
                    Pair("absolute_difference", b - a),
                }));
            AddSelectionEvidence(comparisonId, ordinal, "a", input.CohortA, evidence);
            AddSelectionEvidence(comparisonId, ordinal, "b", input.CohortB, evidence);
        }
    }

    private static void AddScalarRow(
        string comparisonId,
        LocalComparisonMetricDefinition metric,
        FrozenInput input,
        ResultAccumulator results,
        List<LocalComparisonStoredEvidence> evidence,
        ref int nextOrdinal)
    {
        var ordinal = nextOrdinal++;
        var a = input.CohortA.Select(session => Metric(session, metric.Key)).ToArray();
        var b = input.CohortB.Select(session => Metric(session, metric.Key)).ToArray();
        var values = ScalarValues(a, b, metric.IncludeTotal);
        results.Add(LocalComparisonStoredResult.Create(
            comparisonId, ordinal, metric.SectionOrdinal, "scalar", metric.Key, values));
        AddMetricEvidence(comparisonId, ordinal, "value", "a", input.CohortA, a, evidence);
        AddMetricEvidence(comparisonId, ordinal, "value", "b", input.CohortB, b, evidence);
    }

    private static void AddNamedRows(
        string comparisonId,
        LocalComparisonMetricDefinition family,
        FrozenInput input,
        ResultAccumulator results,
        List<LocalComparisonStoredEvidence> evidence,
        ref int nextOrdinal)
    {
        var sessions = input.CohortA.Concat(input.CohortB).ToArray();
        var identities = new Dictionary<string, (string SortKey, string DisplayName)>(StringComparer.Ordinal);
        var minimumReceiptBytes = 0;
        foreach (var session in sessions)
        {
            foreach (var item in Family(session, family.Key).Items)
            {
                if (identities.TryGetValue(item.IdentityKey, out var known))
                {
                    if (known.SortKey != item.SortKey || known.DisplayName != item.DisplayName)
                        throw new InvalidOperationException("local_comparison_input_named_identity_inconsistent");
                    continue;
                }
                minimumReceiptBytes = checked(minimumReceiptBytes
                    + System.Text.Encoding.UTF8.GetByteCount(item.IdentityKey)
                    + System.Text.Encoding.UTF8.GetByteCount(item.SortKey)
                    + System.Text.Encoding.UTF8.GetByteCount(item.DisplayName)
                    + 32);
                if (minimumReceiptBytes > 1_048_576)
                    throw new LocalComparisonTooLargeException();
                identities.Add(item.IdentityKey, (item.SortKey, item.DisplayName));
            }
        }
        foreach (var identity in identities
            .OrderBy(static item => item.Value.SortKey, StringComparer.Ordinal)
            .ThenBy(static item => item.Key, StringComparer.Ordinal))
        {
            var key = identity.Key;
            var displayName = identity.Value.DisplayName;
            var values = new List<KeyValuePair<string, string>>
            {
                new("display_name", displayName),
                new("sort_key", identity.Value.SortKey),
            };
            var primaryField = LocalComparisonRegistryV1.NamedFieldKeys[family.Key][0];
            var aPresence = input.CohortA.Select(session => NamedIdentity(session, family.Key, key)).ToArray();
            var bPresence = input.CohortB.Select(session => NamedIdentity(session, family.Key, key)).ToArray();
            var aPrimary = input.CohortA.Select(session => NamedMetric(session, family.Key, key, primaryField)).ToArray();
            var bPrimary = input.CohortB.Select(session => NamedMetric(session, family.Key, key, primaryField)).ToArray();
            AddNamedSessionCounts(values, family.Key, aPrimary, bPrimary);
            foreach (var field in LocalComparisonRegistryV1.NamedFieldKeys[family.Key])
            {
                var a = input.CohortA.Select(session => NamedMetric(session, family.Key, key, field)).ToArray();
                var b = input.CohortB.Select(session => NamedMetric(session, family.Key, key, field)).ToArray();
                AppendNamedScalarValues(values, field, a, b);
            }
            var ordinal = nextOrdinal++;
            results.Add(LocalComparisonStoredResult.Create(
                comparisonId, ordinal, family.SectionOrdinal, family.Key, key, values));
            AddMetricEvidence(comparisonId, ordinal, "identity", "a", input.CohortA, aPresence, evidence);
            AddMetricEvidence(comparisonId, ordinal, "identity", "b", input.CohortB, bPresence, evidence);
            foreach (var field in LocalComparisonRegistryV1.NamedFieldKeys[family.Key])
            {
                var a = input.CohortA.Select(session => NamedMetric(session, family.Key, key, field)).ToArray();
                var b = input.CohortB.Select(session => NamedMetric(session, family.Key, key, field)).ToArray();
                AddMetricEvidence(comparisonId, ordinal, field, "a", input.CohortA, a, evidence);
                AddMetricEvidence(comparisonId, ordinal, field, "b", input.CohortB, b, evidence);
            }
        }
    }

    private static void AddConditionRow(
        string comparisonId,
        string key,
        FrozenInput input,
        ResultAccumulator results,
        List<LocalComparisonStoredEvidence> evidence,
        ref int nextOrdinal)
    {
        var ordinal = nextOrdinal++;
        var a = input.CohortA.Select(session => session.Conditions[key]).ToArray();
        var b = input.CohortB.Select(session => session.Conditions[key]).ToArray();
        var values = new[]
        {
            Pair("a_session_count", a.Length),
            Pair("a_available_count", a.Count(Available)),
            new KeyValuePair<string, string>("a_distribution", Distribution(a)),
            new KeyValuePair<string, string>("a_unavailable_states", UnavailableStates(a.Select(static item => item.State))),
            Pair("b_session_count", b.Length),
            Pair("b_available_count", b.Count(Available)),
            new KeyValuePair<string, string>("b_distribution", Distribution(b)),
            new KeyValuePair<string, string>("b_unavailable_states", UnavailableStates(b.Select(static item => item.State))),
        };
        results.Add(LocalComparisonStoredResult.Create(
            comparisonId, ordinal, 9, "condition", key, values));
        AddConditionEvidence(comparisonId, ordinal, "a", input.CohortA, a, evidence);
        AddConditionEvidence(comparisonId, ordinal, "b", input.CohortB, b, evidence);
    }

    private static void AddMetricAvailabilityRow(
        string comparisonId,
        FrozenInput input,
        ResultAccumulator results,
        List<LocalComparisonStoredEvidence> evidence,
        ref int nextOrdinal)
    {
        var ordinal = nextOrdinal++;
        var values = new List<KeyValuePair<string, string>>();
        var metrics = LocalComparisonRegistryV1.Metrics.Where(static item =>
            item.SectionOrdinal is 2 or 3 or 4 or 7 or 8).ToArray();
        foreach (var metric in metrics)
        {
            var field = "s" + metric.SectionOrdinal.ToString(CultureInfo.InvariantCulture)
                + "_" + metric.Key;
            var a = input.CohortA.Select(session => Metric(session, metric.Key)).ToArray();
            var b = input.CohortB.Select(session => Metric(session, metric.Key)).ToArray();
            values.Add(Pair("a_" + field, a.Count(static item => item.Observation.Value is not null)));
            values.Add(Pair("b_" + field, b.Count(static item => item.Observation.Value is not null)));
        }
        results.Add(LocalComparisonStoredResult.Create(
            comparisonId, ordinal, 9, "condition", "metric_availability", values));
        foreach (var metric in metrics)
        {
            var field = "s" + metric.SectionOrdinal.ToString(CultureInfo.InvariantCulture)
                + "_" + metric.Key;
            var a = input.CohortA.Select(session => Metric(session, metric.Key)).ToArray();
            var b = input.CohortB.Select(session => Metric(session, metric.Key)).ToArray();
            AddMetricEvidence(comparisonId, ordinal, field, "a", input.CohortA, a, evidence);
            AddMetricEvidence(comparisonId, ordinal, field, "b", input.CohortB, b, evidence);
        }
    }

    private static IReadOnlyList<KeyValuePair<string, string>> ScalarValues(
        IReadOnlyList<LocalComparisonObservedScalar> a,
        IReadOnlyList<LocalComparisonObservedScalar> b,
        bool includeTotal)
    {
        var values = new List<KeyValuePair<string, string>>();
        AppendScalarValues(values, prefix: "", a, b, includeTotal);
        return values;
    }

    private static void AppendScalarValues(
        List<KeyValuePair<string, string>> values,
        string prefix,
        IReadOnlyList<LocalComparisonObservedScalar> a,
        IReadOnlyList<LocalComparisonObservedScalar> b,
        bool includeTotal = true)
    {
        var aSummary = LocalComparisonScalarCalculator.Summarize(
            a.Select(static item => item.Observation).ToArray());
        var bSummary = LocalComparisonScalarCalculator.Summarize(
            b.Select(static item => item.Observation).ToArray());
        AddSummary(values, prefix + "a_", aSummary, a, includeTotal);
        AddSummary(values, prefix + "b_", bSummary, b, includeTotal);
        var difference = LocalComparisonScalarCalculator.Difference(aSummary.Median, bSummary.Median);
        values.Add(new(prefix + "absolute_difference", Decimal(difference.Absolute)));
        values.Add(new(prefix + "relative_difference", Decimal(difference.RelativePercent)));
    }

    private static void AppendNamedScalarValues(
        List<KeyValuePair<string, string>> values,
        string field,
        IReadOnlyList<LocalComparisonObservedScalar> a,
        IReadOnlyList<LocalComparisonObservedScalar> b)
    {
        var temporary = new List<KeyValuePair<string, string>>();
        AppendScalarValues(temporary, prefix: "", a, b);
        foreach (var pair in temporary)
        {
            var key = pair.Key.StartsWith("a_", StringComparison.Ordinal)
                ? "a_" + field + "_" + pair.Key[2..]
                : pair.Key.StartsWith("b_", StringComparison.Ordinal)
                    ? "b_" + field + "_" + pair.Key[2..]
                    : field + "_" + pair.Key;
            values.Add(new(key, pair.Value));
        }
    }

    private static void AddNamedSessionCounts(
        List<KeyValuePair<string, string>> values,
        string family,
        IReadOnlyList<LocalComparisonObservedScalar> a,
        IReadOnlyList<LocalComparisonObservedScalar> b)
    {
        var observedKey = family switch
        {
            "skill" => "invoked_session_count",
            "tool" => "called_session_count",
            "subagent" => "started_session_count",
            _ => throw new InvalidOperationException("local_comparison_named_family_invalid"),
        };
        var aAvailable = a.Count(static item => item.Observation.Value is not null);
        var bAvailable = b.Count(static item => item.Observation.Value is not null);
        var aObserved = a.Count(static item => item.Observation.Value > 0m);
        var bObserved = b.Count(static item => item.Observation.Value > 0m);
        values.Add(Pair("a_available_session_count", aAvailable));
        values.Add(Pair("b_available_session_count", bAvailable));
        values.Add(Pair("a_" + observedKey, aObserved));
        values.Add(Pair("b_" + observedKey, bObserved));
        values.Add(Pair(observedKey + "_absolute_difference", bObserved - aObserved));
    }

    private static void AddSummary(
        List<KeyValuePair<string, string>> values,
        string prefix,
        LocalComparisonScalarSummary summary,
        IReadOnlyList<LocalComparisonObservedScalar> observations,
        bool includeTotal)
    {
        values.Add(Pair(prefix + "session_count", summary.SessionCount));
        values.Add(Pair(prefix + "available_count", summary.AvailableCount));
        values.Add(new(prefix + "median", Decimal(summary.Median)));
        values.Add(new(prefix + "minimum", Decimal(summary.Minimum)));
        values.Add(new(prefix + "maximum", Decimal(summary.Maximum)));
        if (includeTotal)
            values.Add(new(prefix + "total", Decimal(summary.Total)));
        values.Add(new(prefix + "unavailable_states",
            UnavailableStates(observations.SelectMany(static item =>
                item.Observation.Value is null
                    ? item.Evidence.Select(static evidence => evidence.State).Distinct()
                    : Array.Empty<LocalComparisonFactState>()))));
    }

    private static LocalComparisonObservedScalar Metric(
        LocalComparisonSessionFact session,
        string key) => key switch
        {
            "new_input_tokens" => NewInput(session),
            "cache_read_ratio" => CacheRatio(session),
            "subagent_aggregate_start_count" => session.Scalars["subagent_start_count"],
            "subagent_aggregate_completed_count" => session.Scalars["subagent_completed_count"],
            "subagent_aggregate_failed_count" => session.Scalars["subagent_failed_count"],
            "subagent_aggregate_recorded_tokens" => session.Scalars["subagent_recorded_tokens"],
            _ => session.Scalars[key],
        };

    private static LocalComparisonObservedScalar NewInput(LocalComparisonSessionFact session)
    {
        var input = session.Scalars["input_tokens"];
        var cache = session.Scalars["cache_read_tokens"];
        if (input.Observation.Value is not decimal inputValue
            || cache.Observation.Value is not decimal cacheValue)
            return UnavailableDerived(input, cache);
        if (inputValue < 0m || cacheValue < 0m || cacheValue > inputValue)
            return new(
                new(LocalComparisonFactState.Inconsistent, null),
                RestateEvidence(LocalComparisonFactState.Inconsistent, input, cache));
        var value = inputValue - cacheValue;
        return new(
            new(value == 0m ? LocalComparisonFactState.ExplicitZero : LocalComparisonFactState.Recorded, value),
            CombineEvidence(input, cache));
    }

    private static LocalComparisonObservedScalar CacheRatio(LocalComparisonSessionFact session)
    {
        var input = session.Scalars["input_tokens"];
        var cache = session.Scalars["cache_read_tokens"];
        if (input.Observation.Value is not decimal inputValue
            || cache.Observation.Value is not decimal cacheValue)
            return UnavailableDerived(input, cache);
        if (inputValue == 0m && cacheValue == 0m)
            return new(
                new(LocalComparisonFactState.NotObserved, null),
                RestateEvidence(LocalComparisonFactState.NotObserved, input, cache));
        if (inputValue < 0m || cacheValue < 0m || cacheValue > inputValue)
            return new(
                new(LocalComparisonFactState.Inconsistent, null),
                RestateEvidence(LocalComparisonFactState.Inconsistent, input, cache));
        var value = cacheValue / inputValue;
        return new(
            new(value == 0m ? LocalComparisonFactState.ExplicitZero : LocalComparisonFactState.Recorded, value),
            CombineEvidence(input, cache));
    }

    private static LocalComparisonObservedScalar UnavailableDerived(
        LocalComparisonObservedScalar left,
        LocalComparisonObservedScalar right)
    {
        var unavailable = new[] { left, right }
            .Where(static item => item.Observation.Value is null)
            .ToArray();
        var state = unavailable.Select(static item => item.Observation.State).Distinct().ToArray();
        return new(
            new(state.Length == 1 ? state[0] : LocalComparisonFactState.Inconsistent, null),
            Array.AsReadOnly(unavailable.SelectMany(static item => item.Evidence).ToArray()));
    }

    private static IReadOnlyList<LocalComparisonFactEvidence> CombineEvidence(
        params LocalComparisonObservedScalar[] facts) =>
        Array.AsReadOnly(facts.SelectMany(static item => item.Evidence).ToArray());

    private static IReadOnlyList<LocalComparisonFactEvidence> RestateEvidence(
        LocalComparisonFactState state,
        params LocalComparisonObservedScalar[] facts) =>
        Array.AsReadOnly(facts
            .SelectMany(static item => item.Evidence)
            .Select(item => new LocalComparisonFactEvidence(state, item.Reference))
            .ToArray());

    private static LocalComparisonNamedFamilyFact Family(
        LocalComparisonSessionFact session,
        string family) =>
        session.NamedFamilies.Single(item => item.Family == family);

    private static LocalComparisonObservedScalar NamedMetric(
        LocalComparisonSessionFact session,
        string family,
        string key,
        string field)
    {
        var facts = Family(session, family);
        var item = facts.Items.SingleOrDefault(candidate => candidate.IdentityKey == key);
        if (item is not null)
            return item.Values[field];
        return facts.State is LocalComparisonFactState.Recorded or LocalComparisonFactState.ExplicitZero
            ? new(new(LocalComparisonFactState.ExplicitZero, 0m), facts.Reference)
            : new(new(facts.State, null), facts.Reference);
    }

    private static LocalComparisonObservedScalar NamedIdentity(
        LocalComparisonSessionFact session,
        string family,
        string key)
    {
        var facts = Family(session, family);
        var item = facts.Items.SingleOrDefault(candidate => candidate.IdentityKey == key);
        if (item is not null)
            return new(new(LocalComparisonFactState.Recorded, 1m), item.Reference);
        return facts.State is LocalComparisonFactState.Recorded or LocalComparisonFactState.ExplicitZero
            ? new(new(LocalComparisonFactState.ExplicitZero, 0m), facts.Reference)
            : new(new(facts.State, null), facts.Reference);
    }

    private static void AddSelectionEvidence(
        string comparisonId,
        int resultOrdinal,
        string cohort,
        IReadOnlyList<LocalComparisonSessionFact> sessions,
        List<LocalComparisonStoredEvidence> destination)
    {
        var evidenceOrdinal = destination.Count > 0
            && destination[^1].ResultOrdinal == resultOrdinal
            ? destination[^1].EvidenceOrdinal + 1
            : 0;
        var facts = sessions.Select(session =>
            (IReadOnlyList<LocalComparisonFactEvidence>)Array.AsReadOnly(new[]
            {
                new LocalComparisonFactEvidence(
                    LocalComparisonFactState.Recorded,
                    new LocalComparisonSourceReference(
                        "workspace_session", session.SessionId, null, null, null,
                        session.WorkspaceRevision),
                    session.SessionId),
            })).ToArray();
        AddEvidence(comparisonId, resultOrdinal, "selection", cohort, sessions,
            facts, destination, ref evidenceOrdinal);
    }

    private static void AddMetricEvidence(
        string comparisonId,
        int resultOrdinal,
        string fieldKey,
        string cohort,
        IReadOnlyList<LocalComparisonSessionFact> sessions,
        IReadOnlyList<LocalComparisonObservedScalar> facts,
        List<LocalComparisonStoredEvidence> destination)
    {
        var evidenceOrdinal = destination.Count > 0
            && destination[^1].ResultOrdinal == resultOrdinal
            ? destination[^1].EvidenceOrdinal + 1
            : 0;
        AddEvidence(comparisonId, resultOrdinal, fieldKey, cohort, sessions,
            facts.Select(static item =>
            {
                var value = item.Observation.Value is null
                    ? null
                    : LocalComparisonScalarCalculator.CanonicalDecimal(item.Observation.Value.Value);
                return (IReadOnlyList<LocalComparisonFactEvidence>)Array.AsReadOnly(
                    item.Evidence.Select(evidence => evidence with { ConsumedValue = value }).ToArray());
            }).ToArray(), destination, ref evidenceOrdinal);
    }

    private static void AddConditionEvidence(
        string comparisonId,
        int resultOrdinal,
        string cohort,
        IReadOnlyList<LocalComparisonSessionFact> sessions,
        IReadOnlyList<LocalComparisonConditionFact> facts,
        List<LocalComparisonStoredEvidence> destination)
    {
        var evidenceOrdinal = destination.Count > 0
            && destination[^1].ResultOrdinal == resultOrdinal
            ? destination[^1].EvidenceOrdinal + 1
            : 0;
        AddEvidence(comparisonId, resultOrdinal, "value", cohort, sessions,
            facts.Select(static item =>
                (IReadOnlyList<LocalComparisonFactEvidence>)Array.AsReadOnly(new[]
                {
                    new LocalComparisonFactEvidence(
                        item.State,
                        item.Reference,
                        Available(item) ? string.Join(';', item.Values) : null),
                })).ToArray(), destination, ref evidenceOrdinal);
    }

    private static void AddEvidence(
        string comparisonId,
        int resultOrdinal,
        string fieldKey,
        string cohort,
        IReadOnlyList<LocalComparisonSessionFact> sessions,
        IReadOnlyList<IReadOnlyList<LocalComparisonFactEvidence>> facts,
        List<LocalComparisonStoredEvidence> destination,
        ref int evidenceOrdinal)
    {
        if (sessions.Count != facts.Count)
            throw new InvalidOperationException("local_comparison_evidence_shape_invalid");
        var additional = facts.Sum(static item => Math.Max(1, item.Count));
        if (additional > MaximumEvidenceRows - destination.Count)
            throw new LocalComparisonTooLargeException();
        for (var index = 0; index < sessions.Count; index++)
        {
            var items = facts[index].Count == 0
                ? new[]
                {
                    new LocalComparisonFactEvidence(
                        LocalComparisonFactState.ProjectionInvalid,
                        Reference: null),
                }
                : facts[index]
                    .OrderBy(static item => StateToken(item.State), StringComparer.Ordinal)
                    .ThenBy(static item => item.Reference?.SourceKind, StringComparer.Ordinal)
                    .ThenBy(static item => item.Reference?.SourceIdentity, StringComparer.Ordinal)
                    .ThenBy(static item => item.Reference?.TraceId, StringComparer.Ordinal)
                    .ThenBy(static item => item.Reference?.SpanId, StringComparer.Ordinal)
                    .ThenBy(static item => item.Reference?.EventId, StringComparer.Ordinal)
                    .ThenBy(static item => item.Reference?.RevisionSha256, StringComparer.Ordinal)
                    .ToArray();
            foreach (var item in items)
            {
                if (item.ConsumedValue is { Length: > 200 })
                    throw new LocalComparisonTooLargeException();
                var reference = item.Reference;
                destination.Add(new(
                    comparisonId,
                    resultOrdinal,
                    evidenceOrdinal++,
                    fieldKey,
                    cohort,
                    sessions[index].SessionId,
                    StateToken(item.State),
                    item.ConsumedValue,
                    reference?.SourceKind,
                    reference?.SourceIdentity,
                    reference?.TraceId,
                    reference?.SpanId,
                    reference?.EventId,
                    reference?.RevisionSha256));
            }
        }
    }

    private static bool Available(LocalComparisonConditionFact fact) =>
        fact.State is LocalComparisonFactState.Recorded or LocalComparisonFactState.ExplicitZero;

    private static string Distribution(IReadOnlyList<LocalComparisonConditionFact> facts)
    {
        var value = string.Join(';', facts
            .Where(Available)
            .SelectMany(static item => item.Values)
            .GroupBy(static value => value, StringComparer.Ordinal)
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .Select(static group => group.Key + "=" + group.Count().ToString(CultureInfo.InvariantCulture)));
        return value.Length == 0 ? "none" : value;
    }

    private static string Instant(
        IReadOnlyList<LocalComparisonSessionFact> sessions,
        bool minimum)
    {
        var values = sessions
            .Where(static item => item.Target.ObservedAt is not null)
            .Select(static item => item.Target.ObservedAt!.Value)
            .Order()
            .ToArray();
        if (values.Length == 0)
            return "not_available";
        return (minimum ? values[0] : values[^1])
            .ToUniversalTime()
            .ToString("O", CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<IReadOnlyList<LocalComparisonFactEvidence>> TargetTimeEvidence(
        IReadOnlyList<LocalComparisonSessionFact> sessions) =>
        Array.AsReadOnly(sessions.Select(static session =>
            (IReadOnlyList<LocalComparisonFactEvidence>)Array.AsReadOnly(new[]
            {
                new LocalComparisonFactEvidence(
                    session.Target.ObservedAtState,
                    session.Target.ObservedAtReference,
                    session.Target.ObservedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
            })).ToArray());

    private static string UnavailableStates(IEnumerable<LocalComparisonFactState> states)
    {
        var value = string.Join(';', states
            .Where(static state => state is not LocalComparisonFactState.Recorded
                and not LocalComparisonFactState.ExplicitZero)
            .GroupBy(static state => state)
            .OrderBy(static group => (int)group.Key)
            .Select(group => StateToken(group.Key) + "="
                + group.Count().ToString(CultureInfo.InvariantCulture)));
        return value.Length == 0 ? "none" : value;
    }

    internal static string StateToken(LocalComparisonFactState state) => state switch
    {
        LocalComparisonFactState.Recorded => "recorded",
        LocalComparisonFactState.ExplicitZero => "explicit_zero",
        LocalComparisonFactState.NotObserved => "not_observed",
        LocalComparisonFactState.SourceUnsupported => "source_unsupported",
        LocalComparisonFactState.CaptureGap => "capture_gap",
        LocalComparisonFactState.CertificationPending => "certification_pending",
        LocalComparisonFactState.NotCaptured => "not_captured",
        LocalComparisonFactState.Expired => "expired",
        LocalComparisonFactState.Deleted => "deleted",
        LocalComparisonFactState.ReadDenied => "read_denied",
        LocalComparisonFactState.Inconsistent => "inconsistent",
        LocalComparisonFactState.ProjectionInvalid => "projection_invalid",
        LocalComparisonFactState.TooLarge => "too_large",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    private static string Decimal(decimal? value) => value is null
        ? "not_available"
        : LocalComparisonScalarCalculator.CanonicalDecimal(value.Value);

    private static KeyValuePair<string, string> Pair(string key, int value) =>
        new(key, value.ToString(CultureInfo.InvariantCulture));

    private sealed class ResultAccumulator
    {
        private readonly List<LocalComparisonStoredResult> rows = new();
        private int payloadBytes;

        internal IReadOnlyList<LocalComparisonStoredResult> Rows => rows;

        internal void Add(LocalComparisonStoredResult row)
        {
            if (row.Payload.Length > MaximumReceiptBytes - payloadBytes)
                throw new LocalComparisonTooLargeException();
            payloadBytes += row.Payload.Length;
            rows.Add(row);
        }
    }
}

internal sealed record FrozenInput(
    string RepositoryId,
    IReadOnlyList<LocalComparisonSessionFact> CohortA,
    IReadOnlyList<LocalComparisonSessionFact> CohortB,
    int ExcludedA,
    int ExcludedB,
    byte[] ScopeConditionSha256);

internal static class LocalComparisonApplicationValidation
{
    private const int MaximumMembershipFactBytes = 1_048_576;

    internal static FrozenInput Freeze(LocalComparisonDraft draft)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(draft.RepositoryId)
            || draft.CohortA is null
            || draft.CohortB is null
            || draft.CohortA.Members is null
            || draft.CohortB.Members is null
            || draft.CohortA.Members.Count is < 1 or > 199
            || draft.CohortB.Members.Count is < 1 or > 199
            || draft.CohortA.Members.Count > 200 - draft.CohortB.Members.Count
            || draft.ScopeConditionSha256 is null
            || draft.ScopeConditionSha256.Length != 32
            || draft.CohortA.ExcludedSessionCount is < 0 or > 1_000_000
            || draft.CohortB.ExcludedSessionCount is < 0 or > 1_000_000)
        {
            throw new ArgumentException("local_comparison_input_invalid");
        }
        var admittedFactBytes = 0;
        var a = FreezeCohort(
            draft.RepositoryId, draft.CohortA.Members, ref admittedFactBytes);
        var b = FreezeCohort(
            draft.RepositoryId, draft.CohortB.Members, ref admittedFactBytes);
        if (a.Count is < 1 or > 199 || b.Count is < 1 or > 199 || a.Count + b.Count > 200)
            throw new ArgumentException("local_comparison_input_cohort_invalid");
        if (a.Select(static item => item.SessionId)
            .Intersect(b.Select(static item => item.SessionId), StringComparer.Ordinal).Any())
        {
            throw new ArgumentException("local_comparison_input_duplicate_session");
        }
        return new(
            draft.RepositoryId,
            a,
            b,
            draft.CohortA.ExcludedSessionCount,
            draft.CohortB.ExcludedSessionCount,
            draft.ScopeConditionSha256.ToArray());
    }

    private static IReadOnlyList<LocalComparisonSessionFact> FreezeCohort(
        string repositoryId,
        IReadOnlyList<LocalComparisonSessionFact> source,
        ref int admittedFactBytes)
    {
        ArgumentNullException.ThrowIfNull(source);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new LocalComparisonSessionFact[source.Count];
        for (var index = 0; index < source.Count; index++)
        {
            var session = source[index]
                ?? throw new ArgumentException("local_comparison_input_session_invalid");
            ValidateSession(repositoryId, session);
            var factFrame = LocalComparisonFactFrame.Create(session);
            if (factFrame.Length > MaximumMembershipFactBytes - admittedFactBytes)
                throw new LocalComparisonTooLargeException();
            admittedFactBytes += factFrame.Length;
            if (!seen.Add(session.SessionId))
                throw new ArgumentException("local_comparison_input_duplicate_session");
            result[index] = FreezeSession(session);
        }
        Array.Sort(result, static (left, right) =>
            StringComparer.Ordinal.Compare(left.SessionId, right.SessionId));
        return Array.AsReadOnly(result);
    }

    internal static void ValidateSession(
        string repositoryId,
        LocalComparisonSessionFact session)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(session.SessionId)
            || session.RepositoryId != repositoryId
            || !IsHash(session.WorkspaceRevision))
        {
            throw new ArgumentException("local_comparison_input_session_invalid");
        }
        if (!session.IsSelectable
            || session.IsArchived && !session.IsArchiveInclusionExplicit)
            throw new LocalComparisonSelectionUnavailableException();
        if (!session.IsArchived && session.IsArchiveInclusionExplicit)
            throw new ArgumentException("local_comparison_input_archive_state_invalid");
        if (session.Scalars is null
            || !ExactKeys(session.Scalars.Keys, LocalComparisonRegistryV1.RequiredSessionScalarKeys)
            || session.NamedFamilies is null
            || session.NamedFamilies.Count != 3
            || session.Conditions is null
            || !ExactKeys(session.Conditions.Keys, LocalComparisonRegistryV1.ConditionKeys))
        {
            throw new ArgumentException("local_comparison_input_fact_shape_invalid");
        }
        ValidateTarget(session.Target, session.SessionId);
        foreach (var fact in session.Scalars)
        {
            ValidateObserved(fact.Value, session.SessionId);
            if (fact.Key != "session_duration")
                ValidateIntegralObserved(fact.Value);
            if (fact.Key is "error_session_count" or "retry_session_count")
                ValidateBinaryObserved(fact.Value);
        }
        foreach (var definition in LocalComparisonRegistryV1.NamedFamilies)
        {
            var matches = session.NamedFamilies.Where(item => item.Family == definition.Key).ToArray();
            if (matches.Length != 1)
                throw new ArgumentException("local_comparison_input_named_family_invalid");
            ValidateFamily(matches[0], session.SessionId);
        }
        foreach (var condition in session.Conditions.Values)
            ValidateCondition(condition, session.SessionId);
    }

    private static void ValidateTarget(
        LocalComparisonSessionTargetFact target,
        string sessionId)
    {
        if (target is null
            || target.ValueAvailabilityState == LocalComparisonFactState.ExplicitZero
            || target.ObservedAtState == LocalComparisonFactState.ExplicitZero
            || target.ValueAvailabilityState == LocalComparisonFactState.Recorded
                && target.ValueAvailabilityReference is null
            || (target.ObservedAtState == LocalComparisonFactState.Recorded)
                != (target.ObservedAt is not null)
            || target.ObservedAtState == LocalComparisonFactState.Recorded
                && target.ObservedAtReference is null
            || target.ObservedAt is not null
                && (target.ObservedAt.Value.Offset != TimeSpan.Zero
                    || target.ObservedAt.Value.ToString("O", CultureInfo.InvariantCulture)
                        != target.ObservedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)))
        {
            throw new ArgumentException("local_comparison_input_target_invalid");
        }
        if (target.ValueAvailabilityReference is not null)
            ValidateReference(target.ValueAvailabilityReference, sessionId);
        if (target.ObservedAtReference is not null)
            ValidateReference(target.ObservedAtReference, sessionId);
    }

    private static void ValidateObserved(
        LocalComparisonObservedScalar fact,
        string sessionId)
    {
        if (fact is null || fact.Observation is null
            || fact.Evidence is null || fact.Evidence.Count is < 1 or > 16
            || fact.Evidence.Any(item => item is null || item.State != fact.Observation.State)
            || fact.Evidence.Distinct().Count() != fact.Evidence.Count)
            throw new ArgumentException("local_comparison_input_scalar_invalid");
        if (fact.Observation.Value < 0m)
            throw new ArgumentException("local_comparison_input_scalar_invalid");
        if (fact.Observation.Value is not null
            && fact.Evidence.Any(static item => item.Reference is null))
            throw new ArgumentException("local_comparison_input_scalar_reference_missing");
        foreach (var item in fact.Evidence)
            if (item.Reference is not null)
                ValidateReference(item.Reference, sessionId);
    }

    private static void ValidateFamily(
        LocalComparisonNamedFamilyFact family,
        string sessionId)
    {
        if (!LocalComparisonRegistryV1.NamedFieldKeys.TryGetValue(family.Family, out var fields)
            || family.Items is null
            || family.Items.Count > 1_048_576 / 16)
        {
            throw new ArgumentException("local_comparison_input_named_family_invalid");
        }
        if (family.Reference is not null)
            ValidateReference(family.Reference, sessionId);
        if (family.State is LocalComparisonFactState.Recorded
                or LocalComparisonFactState.ExplicitZero
            && family.Reference is null)
        {
            throw new ArgumentException("local_comparison_input_named_family_reference_missing");
        }
        if (family.State is not LocalComparisonFactState.Recorded
                and not LocalComparisonFactState.ExplicitZero
            && family.Items.Count != 0)
        {
            throw new ArgumentException("local_comparison_input_named_family_invalid");
        }
        if (family.State == LocalComparisonFactState.ExplicitZero
            && family.Items.Count != 0)
        {
            throw new ArgumentException("local_comparison_input_named_family_invalid");
        }
        if (family.State == LocalComparisonFactState.Recorded
            && family.Items.Count == 0)
        {
            throw new ArgumentException("local_comparison_input_named_family_invalid");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in family.Items)
        {
            if (item.Family != family.Family
                || !IsOpaqueIdentity(item.IdentityKey)
                || !LocalComparisonBoundedText.IsToken(item.SortKey, 256)
                || !LocalComparisonBoundedText.IsText(item.DisplayName, 256)
                || !seen.Add(item.IdentityKey)
                || item.Values is null
                || !ExactKeys(item.Values.Keys, fields))
            {
                throw new ArgumentException("local_comparison_input_named_item_invalid");
            }
            ValidateReference(item.Reference, sessionId);
            foreach (var value in item.Values.Values)
            {
                ValidateObserved(value, sessionId);
                ValidateIntegralObserved(value);
            }
        }
    }

    private static void ValidateIntegralObserved(LocalComparisonObservedScalar fact)
    {
        if (fact.Observation.Value is decimal value && decimal.Truncate(value) != value)
            throw new ArgumentException("local_comparison_input_count_invalid");
    }

    private static void ValidateBinaryObserved(LocalComparisonObservedScalar fact)
    {
        if (fact.Observation.Value is decimal value && value > 1m)
            throw new ArgumentException("local_comparison_input_binary_count_invalid");
    }

    private static void ValidateCondition(
        LocalComparisonConditionFact condition,
        string sessionId)
    {
        if (condition is null || condition.Values is null || condition.Values.Count > 16)
            throw new ArgumentException("local_comparison_input_condition_invalid");
        if (condition.Reference is not null)
            ValidateReference(condition.Reference, sessionId);
        var available = condition.State is LocalComparisonFactState.Recorded
            or LocalComparisonFactState.ExplicitZero;
        if (available && condition.Reference is null
            || !available && condition.Values.Count != 0
            || condition.State == LocalComparisonFactState.Recorded && condition.Values.Count == 0
            || condition.State == LocalComparisonFactState.ExplicitZero && condition.Values.Count != 0
            || condition.Values.Any(static value => !LocalComparisonBoundedText.IsToken(value, 512))
            || condition.Values.Distinct(StringComparer.Ordinal).Count() != condition.Values.Count)
        {
            throw new ArgumentException("local_comparison_input_condition_invalid");
        }
    }

    private static void ValidateReference(
        LocalComparisonSourceReference reference,
        string sessionId)
    {
        if (!IsHash(reference.RevisionSha256)
            || reference.SourceIdentity is null
            || !IsOpaqueIdentity(reference.SourceIdentity)
            || (reference.TraceId is null) != (reference.SpanId is null)
            || reference.TraceId is not null
                && (!IsHex(reference.TraceId, 32) || !IsHex(reference.SpanId!, 16))
            || reference.EventId is not null
                && !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reference.EventId)
            || !KindMatches())
        {
            throw new ArgumentException("local_comparison_input_reference_invalid");
        }

        bool KindMatches() => reference.SourceKind switch
        {
            "workspace_session" => reference.SourceIdentity == sessionId
                && reference.TraceId is null && reference.EventId is null,
            "workspace_node" => reference.SourceIdentity.StartsWith("node-", StringComparison.Ordinal)
                && reference.SourceIdentity.Length == 37
                && reference.SourceIdentity[5..].All(static value => value is >= '0' and <= '9' or >= 'a' and <= 'f')
                && reference.TraceId is null && reference.EventId is null,
            "session_run" => LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reference.SourceIdentity)
                && reference.TraceId is null && reference.EventId is null,
            "session_event" => LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reference.SourceIdentity)
                && reference.EventId == reference.SourceIdentity,
            "otel_span" => LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reference.SourceIdentity)
                && reference.EventId == reference.SourceIdentity && reference.TraceId is not null,
            "skill_claim" when reference.SourceIdentity.StartsWith("otel:", StringComparison.Ordinal) =>
                reference.TraceId is not null && reference.EventId is not null,
            "skill_claim" when reference.SourceIdentity.StartsWith("sdk:", StringComparison.Ordinal) =>
                reference.TraceId is null && reference.EventId is not null,
            _ => false,
        };
    }

    private static bool IsOpaqueIdentity(string value) =>
        value.Length is >= 1 and <= 128
        && value.All(static character => character is >= 'a' and <= 'z'
            or >= '0' and <= '9' or ':' or '.' or '-');

    private static LocalComparisonSessionFact FreezeSession(LocalComparisonSessionFact source) =>
        new(
            source.SessionId,
            source.RepositoryId,
            source.WorkspaceRevision,
            source.IsSelectable,
            source.IsArchived,
            source.Scalars.ToDictionary(
                static item => item.Key,
                static item => FreezeObserved(item.Value),
                StringComparer.Ordinal),
            Array.AsReadOnly(source.NamedFamilies
                .OrderBy(static item => item.Family, StringComparer.Ordinal)
                .Select(static family => family with
                {
                    Items = Array.AsReadOnly(family.Items
                        .OrderBy(static item => item.SortKey, StringComparer.Ordinal)
                        .ThenBy(static item => item.IdentityKey, StringComparer.Ordinal)
                        .Select(static item => item with
                        {
                            Values = item.Values.ToDictionary(
                                static value => value.Key,
                                static value => FreezeObserved(value.Value),
                                StringComparer.Ordinal),
                        }).ToArray()),
                }).ToArray()),
            source.Conditions.ToDictionary(
                static item => item.Key,
                static item => item.Value with
                {
                    Values = Array.AsReadOnly(item.Value.Values
                        .Order(StringComparer.Ordinal).ToArray()),
                },
                StringComparer.Ordinal),
            source.Target,
            source.IsArchiveInclusionExplicit);

    private static LocalComparisonObservedScalar FreezeObserved(
        LocalComparisonObservedScalar source) =>
        new(
            source.Observation,
            Array.AsReadOnly(source.Evidence
                .OrderBy(static item => LocalComparisonApplicationService.StateToken(item.State), StringComparer.Ordinal)
                .ThenBy(static item => item.Reference?.SourceKind, StringComparer.Ordinal)
                .ThenBy(static item => item.Reference?.SourceIdentity, StringComparer.Ordinal)
                .ThenBy(static item => item.Reference?.TraceId, StringComparer.Ordinal)
                .ThenBy(static item => item.Reference?.SpanId, StringComparer.Ordinal)
                .ThenBy(static item => item.Reference?.EventId, StringComparer.Ordinal)
                .ThenBy(static item => item.Reference?.RevisionSha256, StringComparer.Ordinal)
                .ToArray()));

    private static bool ExactKeys(IEnumerable<string> actual, IEnumerable<string> expected) =>
        actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static bool IsHash(string value) => IsHex(value, 64);

    private static bool IsHex(string value, int length) =>
        value.Length == length
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class LocalComparisonFactFrame
{
    private const string Domain =
        "copilot-agent-observability/local-comparison-session-fact/v1";

    internal static byte[] Create(LocalComparisonSessionFact session)
    {
        using var stream = new MemoryStream();
        Write(Domain);
        Write(session.SessionId);
        Write(session.RepositoryId);
        Write(session.WorkspaceRevision);
        Write(session.IsArchived ? "1" : "0");
        Write(session.IsArchiveInclusionExplicit ? "1" : "0");
        Write(LocalComparisonApplicationService.StateToken(session.Target.ValueAvailabilityState));
        WriteReference(session.Target.ValueAvailabilityReference);
        Write(LocalComparisonApplicationService.StateToken(session.Target.ObservedAtState));
        Write(session.Target.ObservedAt is null
            ? "not_available"
            : session.Target.ObservedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        WriteReference(session.Target.ObservedAtReference);
        foreach (var key in LocalComparisonRegistryV1.RequiredSessionScalarKeys)
        {
            Write(key);
            WriteObserved(session.Scalars[key]);
        }
        foreach (var familyKey in LocalComparisonRegistryV1.NamedFamilies.Select(static item => item.Key))
        {
            var family = session.NamedFamilies.Single(item => item.Family == familyKey);
            Write(familyKey);
            Write(LocalComparisonApplicationService.StateToken(family.State));
            WriteReference(family.Reference);
            WriteCount(family.Items.Count);
            foreach (var item in family.Items)
            {
                Write(item.IdentityKey);
                Write(item.SortKey);
                Write(item.DisplayName);
                WriteReference(item.Reference);
                foreach (var field in LocalComparisonRegistryV1.NamedFieldKeys[familyKey])
                {
                    Write(field);
                    WriteObserved(item.Values[field]);
                }
            }
        }
        foreach (var key in LocalComparisonRegistryV1.ConditionKeys)
        {
            var condition = session.Conditions[key];
            Write(key);
            Write(LocalComparisonApplicationService.StateToken(condition.State));
            WriteReference(condition.Reference);
            WriteCount(condition.Values.Count);
            foreach (var value in condition.Values.Order(StringComparer.Ordinal))
                Write(value);
        }
        var result = stream.ToArray();
        if (result.Length is < 1 or > 1_048_576)
            throw new LocalComparisonTooLargeException();
        return result;

        void Write(string value)
        {
            LocalComparisonSelectionFrame.WriteFrame(stream, value);
            if (stream.Length > 1_048_576)
                throw new LocalComparisonTooLargeException();
        }
        void WriteCount(int value) => Write(value.ToString(CultureInfo.InvariantCulture));
        void WriteObserved(LocalComparisonObservedScalar fact)
        {
            Write(LocalComparisonApplicationService.StateToken(fact.Observation.State));
            Write(fact.Observation.Value is null
                ? "not_available"
                : LocalComparisonScalarCalculator.CanonicalDecimal(fact.Observation.Value.Value));
            WriteCount(fact.Evidence.Count);
            foreach (var item in fact.Evidence)
            {
                Write(LocalComparisonApplicationService.StateToken(item.State));
                WriteReference(item.Reference);
            }
        }
        void WriteReference(LocalComparisonSourceReference? reference)
        {
            Write(reference is null ? "0" : "1");
            if (reference is null)
                return;
            Write(reference.SourceKind);
            WriteNullable(reference.SourceIdentity);
            WriteNullable(reference.TraceId);
            WriteNullable(reference.SpanId);
            WriteNullable(reference.EventId);
            Write(reference.RevisionSha256);
        }
        void WriteNullable(string? value)
        {
            Write(value is null ? "0" : "1");
            if (value is not null)
                Write(value);
        }
    }

    internal static LocalComparisonSessionFact Decode(byte[] frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Length is < 1 or > 1_048_576)
            Reject();
        var reader = new LocalComparisonFrameReader(frame);
        if (reader.Read() != Domain)
            Reject();
        var sessionId = reader.Read();
        var repositoryId = reader.Read();
        var workspaceRevision = reader.Read();
        var isArchived = ReadBoolean(ref reader);
        var archiveExplicit = ReadBoolean(ref reader);
        var target = new LocalComparisonSessionTargetFact(
            ReadState(ref reader),
            ReadReference(ref reader),
            ReadState(ref reader),
            ReadInstant(ref reader),
            ReadReference(ref reader));
        var scalars = new Dictionary<string, LocalComparisonObservedScalar>(StringComparer.Ordinal);
        foreach (var expectedKey in LocalComparisonRegistryV1.RequiredSessionScalarKeys)
        {
            if (reader.Read() != expectedKey || !scalars.TryAdd(expectedKey, ReadObserved(ref reader)))
                Reject();
        }
        var families = new List<LocalComparisonNamedFamilyFact>();
        foreach (var definition in LocalComparisonRegistryV1.NamedFamilies)
        {
            if (reader.Read() != definition.Key)
                Reject();
            var state = ReadState(ref reader);
            var reference = ReadReference(ref reader);
            var count = ReadCount(ref reader, 1_048_576 / 16);
            var items = new LocalComparisonNamedItem[count];
            for (var itemIndex = 0; itemIndex < count; itemIndex++)
            {
                var identity = reader.Read();
                var sortKey = reader.Read();
                var displayName = reader.Read();
                var itemReference = ReadReference(ref reader) ?? throw Invalid();
                var values = new Dictionary<string, LocalComparisonObservedScalar>(StringComparer.Ordinal);
                foreach (var field in LocalComparisonRegistryV1.NamedFieldKeys[definition.Key])
                {
                    if (reader.Read() != field || !values.TryAdd(field, ReadObserved(ref reader)))
                        Reject();
                }
                items[itemIndex] = new(
                    definition.Key, identity, sortKey, displayName, values, itemReference);
            }
            families.Add(new(
                definition.Key, state, Array.AsReadOnly(items), reference));
        }
        var conditions = new Dictionary<string, LocalComparisonConditionFact>(StringComparer.Ordinal);
        foreach (var expectedKey in LocalComparisonRegistryV1.ConditionKeys)
        {
            if (reader.Read() != expectedKey)
                Reject();
            var state = ReadState(ref reader);
            var reference = ReadReference(ref reader);
            var count = ReadCount(ref reader, 16);
            var values = new string[count];
            for (var index = 0; index < count; index++)
                values[index] = reader.Read();
            if (!conditions.TryAdd(expectedKey,
                    new(state, Array.AsReadOnly(values), reference)))
                Reject();
        }
        if (!reader.AtEnd)
            Reject();
        var result = new LocalComparisonSessionFact(
            sessionId,
            repositoryId,
            workspaceRevision,
            true,
            isArchived,
            scalars,
            Array.AsReadOnly(families.ToArray()),
            conditions,
            target,
            archiveExplicit);
        try
        {
            LocalComparisonApplicationValidation.ValidateSession(repositoryId, result);
            if (!frame.SequenceEqual(Create(result)))
                Reject();
            return result;
        }
        catch (Exception exception) when (exception is ArgumentException
            or LocalComparisonSelectionUnavailableException
            or LocalComparisonTooLargeException)
        {
            throw new InvalidOperationException("local_comparison_fact_frame_invalid", exception);
        }
    }

    private static LocalComparisonObservedScalar ReadObserved(
        ref LocalComparisonFrameReader reader)
    {
        var state = ReadState(ref reader);
        var valueText = reader.Read();
        decimal? value = null;
        if (valueText != "not_available")
        {
            if (!decimal.TryParse(
                    valueText,
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsed)
                || LocalComparisonScalarCalculator.CanonicalDecimal(parsed) != valueText)
            {
                Reject();
            }
            value = parsed;
        }
        var count = ReadCount(ref reader, 16);
        if (count == 0)
            Reject();
        var evidence = new LocalComparisonFactEvidence[count];
        for (var index = 0; index < count; index++)
            evidence[index] = new(ReadState(ref reader), ReadReference(ref reader));
        try
        {
            return new(
                new LocalComparisonScalarObservation(state, value),
                Array.AsReadOnly(evidence));
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("local_comparison_fact_frame_invalid", exception);
        }
    }

    private static LocalComparisonSourceReference? ReadReference(
        ref LocalComparisonFrameReader reader)
    {
        var marker = reader.Read();
        if (marker == "0")
            return null;
        if (marker != "1")
            Reject();
        return new(
            reader.Read(),
            ReadNullable(ref reader),
            ReadNullable(ref reader),
            ReadNullable(ref reader),
            ReadNullable(ref reader),
            reader.Read());
    }

    private static string? ReadNullable(ref LocalComparisonFrameReader reader)
    {
        var marker = reader.Read();
        return marker switch
        {
            "0" => null,
            "1" => reader.Read(),
            _ => throw Invalid(),
        };
    }

    private static DateTimeOffset? ReadInstant(ref LocalComparisonFrameReader reader)
    {
        var value = reader.Read();
        if (value == "not_available")
            return null;
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result)
            || result.Offset != TimeSpan.Zero
            || result.ToString("O", CultureInfo.InvariantCulture) != value)
        {
            Reject();
        }
        return result;
    }

    private static LocalComparisonFactState ReadState(
        ref LocalComparisonFrameReader reader) => reader.Read() switch
        {
            "recorded" => LocalComparisonFactState.Recorded,
            "explicit_zero" => LocalComparisonFactState.ExplicitZero,
            "not_observed" => LocalComparisonFactState.NotObserved,
            "source_unsupported" => LocalComparisonFactState.SourceUnsupported,
            "capture_gap" => LocalComparisonFactState.CaptureGap,
            "certification_pending" => LocalComparisonFactState.CertificationPending,
            "not_captured" => LocalComparisonFactState.NotCaptured,
            "expired" => LocalComparisonFactState.Expired,
            "deleted" => LocalComparisonFactState.Deleted,
            "read_denied" => LocalComparisonFactState.ReadDenied,
            "inconsistent" => LocalComparisonFactState.Inconsistent,
            "projection_invalid" => LocalComparisonFactState.ProjectionInvalid,
            "too_large" => LocalComparisonFactState.TooLarge,
            _ => throw Invalid(),
        };

    private static bool ReadBoolean(ref LocalComparisonFrameReader reader) =>
        reader.Read() switch
        {
            "0" => false,
            "1" => true,
            _ => throw Invalid(),
        };

    private static int ReadCount(
        ref LocalComparisonFrameReader reader,
        int maximum)
    {
        var value = reader.Read();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
            || result < 0 || result > maximum
            || result.ToString(CultureInfo.InvariantCulture) != value)
        {
            Reject();
        }
        return result;
    }

    private static InvalidOperationException Invalid() =>
        new("local_comparison_fact_frame_invalid");

    private static void Reject() => throw Invalid();
}

internal sealed class LocalComparisonSelectionUnavailableException : Exception
{
    internal LocalComparisonSelectionUnavailableException()
        : base("local_comparison_selection_unavailable") { }
}

using System.Reflection;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalComparisonApplicationTests
{
    [Fact]
    public void SelectionFrame_CanonicalizesExplicitCohortsToThePinnedBytesAndHash()
    {
        var type = typeof(LocalArchiveSchemaV1).Assembly.GetType(
            "CopilotAgentObservability.Persistence.Sqlite.LocalComparisonSelectionFrame");
        Assert.NotNull(type);
        var create = type.GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [typeof(IReadOnlyList<string>), typeof(IReadOnlyList<string>)],
            modifiers: null);
        Assert.NotNull(create);

        var result = create.Invoke(null,
        [
            new[] { SessionC, SessionA },
            new[] { SessionB },
        ]);
        Assert.NotNull(result);
        var resultType = result.GetType();
        var bytes = Assert.IsType<byte[]>(resultType.GetProperty("Bytes")!.GetValue(result));
        var hash = Assert.IsType<string>(resultType.GetProperty("Sha256")!.GetValue(result));
        var cohortA = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            resultType.GetProperty("CohortA")!.GetValue(result));
        var cohortB = Assert.IsAssignableFrom<IReadOnlyList<string>>(
            resultType.GetProperty("CohortB")!.GetValue(result));

        Assert.Equal([SessionA, SessionC], cohortA);
        Assert.Equal([SessionB], cohortB);
        Assert.Equal(
            "00000039636f70696c6f742d6167656e742d6f62736572766162696c6974792f6c6f63616c2d636f6d70617269736f6e2d73656c656374696f6e2f7631000000016100000001320000002430313938663562382d306330302d373030302d383030302d3030303030303030303030310000002430313938663562382d306330302d373030302d383030302d303030303030303030303033000000016200000001310000002430313938663562382d306330302d373030302d383030302d303030303030303030303032",
            Convert.ToHexStringLower(bytes));
        Assert.Equal("1aabc5f3070d7295bf346c4a2bcf0ac7a3f3fd718b81d0ec42862de1b01fcf62", hash);
    }

    [Fact]
    public void ScalarCalculator_UsesDecimalMedianAndDoesNotTurnMissingIntoZero()
    {
        var assembly = typeof(LocalArchiveSchemaV1).Assembly;
        var factType = assembly.GetType(
            "CopilotAgentObservability.Persistence.Sqlite.LocalComparisonScalarObservation");
        var stateType = assembly.GetType(
            "CopilotAgentObservability.Persistence.Sqlite.LocalComparisonFactState");
        var calculator = assembly.GetType(
            "CopilotAgentObservability.Persistence.Sqlite.LocalComparisonScalarCalculator");
        Assert.NotNull(factType);
        Assert.NotNull(stateType);
        Assert.NotNull(calculator);

        var observations = Array.CreateInstance(factType, 4);
        observations.SetValue(CreateFact(factType, stateType, "Recorded", 10m), 0);
        observations.SetValue(CreateFact(factType, stateType, "NotObserved", null), 1);
        observations.SetValue(CreateFact(factType, stateType, "Recorded", 20m), 2);
        observations.SetValue(CreateFact(factType, stateType, "ExplicitZero", 0m), 3);
        var summarize = calculator.GetMethod(
            "Summarize",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(summarize);

        var result = summarize.Invoke(null, [observations]);
        Assert.NotNull(result);
        var type = result.GetType();
        Assert.Equal(4, type.GetProperty("SessionCount")!.GetValue(result));
        Assert.Equal(3, type.GetProperty("AvailableCount")!.GetValue(result));
        Assert.Equal(10m, type.GetProperty("Median")!.GetValue(result));
        Assert.Equal(0m, type.GetProperty("Minimum")!.GetValue(result));
        Assert.Equal(20m, type.GetProperty("Maximum")!.GetValue(result));
        Assert.Equal(30m, type.GetProperty("Total")!.GetValue(result));

        var even = LocalComparisonScalarCalculator.Summarize(
        [
            new(LocalComparisonFactState.Recorded, 1.1m),
            new(LocalComparisonFactState.Recorded, 2.2m),
        ]);
        Assert.Equal(1.65m, even.Median);
    }

    [Fact]
    public void ScalarObservation_RequiresExplicitZeroAndClosedMissingStateCoupling()
    {
        Assert.Throws<ArgumentException>(() =>
            new LocalComparisonScalarObservation(LocalComparisonFactState.Recorded, 0m));
        Assert.Throws<ArgumentException>(() =>
            new LocalComparisonScalarObservation(LocalComparisonFactState.ExplicitZero, null));
        Assert.Throws<ArgumentException>(() =>
            new LocalComparisonScalarObservation(LocalComparisonFactState.CaptureGap, 1m));
    }

    [Fact]
    public void ScalarDifference_UsesBAAndOnlyComputesRelativeForPositiveBaseline()
    {
        var positive = LocalComparisonScalarCalculator.Difference(40m, 30m);
        var zero = LocalComparisonScalarCalculator.Difference(0m, 30m);
        var missing = LocalComparisonScalarCalculator.Difference(null, 30m);

        Assert.Equal(-10m, positive.Absolute);
        Assert.Equal(-25.0m, positive.RelativePercent);
        Assert.Equal(30m, zero.Absolute);
        Assert.Null(zero.RelativePercent);
        Assert.Null(missing.Absolute);
        Assert.Null(missing.RelativePercent);
        Assert.Equal(12.4m,
            LocalComparisonScalarCalculator.Difference(40m, 44.94m).RelativePercent);
        Assert.Equal(-12.4m,
            LocalComparisonScalarCalculator.Difference(40m, 35.06m).RelativePercent);
    }

    [Fact]
    public void CanonicalDecimal_RemovesRepresentationOnlyFormatting()
    {
        Assert.Equal("0", LocalComparisonScalarCalculator.CanonicalDecimal(decimal.Negate(0m)));
        Assert.Equal("12", LocalComparisonScalarCalculator.CanonicalDecimal(12.000m));
        Assert.Equal("-0.125", LocalComparisonScalarCalculator.CanonicalDecimal(-0.1250m));
    }

    [Fact]
    public void Registry_HasTheFixedNineSectionsAndClosedMetricOrder()
    {
        Assert.Equal(
        [
            "target",
            "tokens",
            "input_token_breakdown",
            "time_and_execution",
            "skills",
            "tools",
            "subagents",
            "errors_and_retries",
            "conditions",
        ],
            LocalComparisonRegistryV1.Sections.Select(section => section.Token));
        Assert.Equal(
        [
            "included_session_count", "excluded_session_count", "available_session_count",
            "period", "archived_inclusion",
            "input_tokens", "output_tokens", "total_tokens",
            "cache_read_tokens", "new_input_tokens", "cache_creation_tokens", "cache_read_ratio",
            "session_duration", "execution_count", "model_turn_count", "tool_call_count",
            "skill_invocation_count", "subagent_start_count", "error_count", "retry_count",
            "subagent_aggregate_start_count", "subagent_aggregate_completed_count",
            "subagent_aggregate_failed_count", "subagent_aggregate_recorded_tokens",
            "error_session_count", "error_count", "retry_session_count", "retry_count",
            "recovery_relation_count",
            "sources", "models", "source_versions", "adapter_versions", "completeness",
            "metric_availability",
        ],
            LocalComparisonRegistryV1.Metrics.Select(metric => metric.Key));
    }

    [Fact]
    public void Prepare_ComputesDerivedScalarsAndCompleteNamedUnionWithoutMissingToZero()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, RevisionA);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var a = Session(
            SessionA,
            RevisionA,
            referenceA,
            input: Recorded(10m, referenceA),
            cacheRead: Recorded(2m, referenceA),
            tools:
            [
                Named("zeta", "Zeta", "tool", referenceA,
                    ("call_count", Recorded(1m, referenceA)),
                    ("failure_count", ExplicitZero(referenceA)),
                    ("retry_count", ExplicitZero(referenceA))),
            ]);
        a = a with
        {
            NamedFamilies = Array.AsReadOnly(a.NamedFamilies.Select(family =>
                family.Family == "skill"
                    ? new LocalComparisonNamedFamilyFact(
                        "skill", LocalComparisonFactState.SourceUnsupported,
                        Array.Empty<LocalComparisonNamedItem>(), Reference: null)
                    : family).ToArray()),
        };
        var b = Session(
            SessionB,
            RevisionB,
            referenceB,
            input: Recorded(20m, referenceB),
            cacheRead: Missing(LocalComparisonFactState.CaptureGap),
            tools:
            [
                Named("alpha", "Alpha", "tool", referenceB,
                    ("call_count", Recorded(2m, referenceB)),
                    ("failure_count", Recorded(1m, referenceB)),
                    ("retry_count", ExplicitZero(referenceB))),
            ]);
        b = b with
        {
            NamedFamilies = Array.AsReadOnly(b.NamedFamilies.Select(family =>
                family.Family == "skill"
                    ? new LocalComparisonNamedFamilyFact(
                        "skill", LocalComparisonFactState.Recorded,
                        Array.AsReadOnly(new[]
                        {
                            Named("digest-skill", "Digest Skill", "skill", referenceB,
                                ("invocation_count", Recorded(1m, referenceB))),
                        }), referenceB)
                    : family).ToArray()),
        };
        var draft = new LocalComparisonDraft(
            RepositoryId,
            new LocalComparisonCohortDraft([a], ExcludedSessionCount: 0),
            new LocalComparisonCohortDraft([b], ExcludedSessionCount: 0),
            ScopeConditionDigest());
        var service = new LocalComparisonApplicationService(
            store: null,
            new FixedTimeProvider(CreatedAt),
            _ => ComparisonId);

        var prepared = service.Prepare(draft);
        var repeated = service.Prepare(draft);

        Assert.Equal(LocalComparisonCreateStatus.Accepted, prepared.Status);
        var snapshot = Assert.IsType<LocalComparisonSnapshotWrite>(prepared.Snapshot);
        Assert.Equal(24, (snapshot.ExpiresAt - snapshot.CreatedAt).TotalHours);
        Assert.Equal(
            ["alpha", "zeta"],
            snapshot.Results
                .Where(row => row.RowKind == "tool")
                .Select(row => row.RowKey));
        var newInput = snapshot.Results.Single(row =>
            row.SectionOrdinal == 3 && row.RowKey == "new_input_tokens");
        Assert.Equal("8", Value(newInput, "a_median"));
        Assert.Equal("not_available", Value(newInput, "b_median"));
        Assert.Equal("0", Value(newInput, "b_available_count"));
        var alpha = snapshot.Results.Single(row => row.RowKind == "tool" && row.RowKey == "alpha");
        Assert.Equal("0", Value(alpha, "a_call_count_median"));
        Assert.Equal("2", Value(alpha, "b_call_count_median"));
        Assert.Equal("0", Value(alpha, "a_called_session_count"));
        Assert.Equal("1", Value(alpha, "b_called_session_count"));
        var skill = snapshot.Results.Single(row =>
            row.RowKind == "skill" && row.RowKey == "digest-skill");
        Assert.Equal("not_available", Value(skill, "a_invocation_count_median"));
        Assert.Equal("source_unsupported=1",
            Value(skill, "a_invocation_count_unavailable_states"));
        Assert.Equal(
            "capture_gap=1",
            Value(newInput, "b_unavailable_states"));
        var ratio = snapshot.Results.Single(row =>
            row.SectionOrdinal == 3 && row.RowKey == "cache_read_ratio");
        Assert.DoesNotContain(ratio.Values, item => item.Key is "a_total" or "b_total");
        Assert.Equal("1", Value(snapshot.Results.Single(row =>
            row.SectionOrdinal == 1 && row.RowKey == "available_session_count"), "a_count"));
        Assert.Contains(snapshot.Results, row =>
            row.SectionOrdinal == 1 && row.RowKey == "period");
        Assert.Contains(snapshot.Results, row =>
            row.SectionOrdinal == 1 && row.RowKey == "archived_inclusion");
        Assert.Contains(snapshot.Results, row =>
            row.SectionOrdinal == 9 && row.RowKey == "metric_availability");
        Assert.Equal(
            snapshot.Results.Select(row => row.SectionOrdinal).Order(),
            snapshot.Results.Select(row => row.SectionOrdinal));
        Assert.NotEmpty(snapshot.Results[0].Payload);
        Assert.Equal("receipt", snapshot.Results[0].RowKind);
        Assert.Equal(
            "ce16d163bb284453dfe90c7dad77988dd423a9b8812eefc984266cba9de3ea3d",
            snapshot.Results[0].PayloadSha256);
        Assert.Equal(snapshot.Results[0].Payload,
            Assert.IsType<LocalComparisonSnapshotWrite>(repeated.Snapshot).Results[0].Payload);
        Assert.DoesNotContain(snapshot.Results,
            row => row.Values.Any(value => value.Value.Contains("verdict", StringComparison.OrdinalIgnoreCase)));
        var readModel = LocalComparisonFrozenReadModel.Create(new(
            snapshot.ComparisonId,
            snapshot.RepositoryId,
            snapshot.CreatedAt,
            snapshot.ExpiresAt,
            snapshot.SelectionFrame,
            snapshot.SelectionSha256,
            snapshot.ScopeConditionSha256,
            snapshot.Memberships,
            snapshot.Results,
            snapshot.Evidence));
        Assert.Equal(9, readModel.Sections.Count);
        Assert.Equal([SessionA, SessionB],
            readModel.Members.Select(static item => item.SessionId));
        Assert.All(readModel.Members,
            item => Assert.Equal(LocalComparisonFactState.Recorded, item.ValueAvailabilityState));
        var toolUnion = readModel.NamedUnions.Single(item => item.Family == "tool");
        Assert.Equal(2, toolUnion.TotalCount);
        Assert.Equal(["alpha", "zeta"], toolUnion.Rows.Select(static item => item.RowKey));
        Assert.Contains(readModel.Evidence, item =>
            item.ResultOrdinal == alpha.ResultOrdinal && item.FieldKey == "failure_count");
    }

    [Fact]
    public void Prepare_RejectsDuplicateArchivedAndNonDigestSelections()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, RevisionA);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var a = Session(SessionA, RevisionA, referenceA,
            ExplicitZero(referenceA), ExplicitZero(referenceA), []);
        var b = Session(SessionB, RevisionB, referenceB,
            ExplicitZero(referenceB), ExplicitZero(referenceB), []);
        var service = new LocalComparisonApplicationService(
            store: null, new FixedTimeProvider(CreatedAt), _ => ComparisonId);

        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([a], 0),
                new([a], 0),
                ScopeConditionDigest())).Status);
        Assert.Equal(LocalComparisonCreateStatus.SelectionUnavailable,
            service.Prepare(new(
                RepositoryId,
                new([a with { IsArchived = true }], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);
        Assert.Equal(LocalComparisonCreateStatus.Accepted,
            service.Prepare(new(
                RepositoryId,
                new([a with
                {
                    IsArchived = true,
                    IsArchiveInclusionExplicit = true,
                }], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);
        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([a], 0),
                new([b], 0),
                new byte[1_048_576])).Status);
    }

    [Fact]
    public void Prepare_RejectsScopeConditionValuesThatAreNotAnExactSha256Digest()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, RevisionA);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var service = new LocalComparisonApplicationService(
            store: null, new FixedTimeProvider(CreatedAt), _ => ComparisonId);

        var result = service.Prepare(new(
            RepositoryId,
            new([Session(SessionA, RevisionA, referenceA,
                ExplicitZero(referenceA), ExplicitZero(referenceA), [])], 0),
            new([Session(SessionB, RevisionB, referenceB,
                ExplicitZero(referenceB), ExplicitZero(referenceB), [])], 0),
            new byte[31]));

        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Prepare_RejectsInvalidCountAndTokenDomains()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, RevisionA);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var a = Session(SessionA, RevisionA, referenceA,
            ExplicitZero(referenceA), ExplicitZero(referenceA), []);
        var b = Session(SessionB, RevisionB, referenceB,
            ExplicitZero(referenceB), ExplicitZero(referenceB), []);
        var service = new LocalComparisonApplicationService(
            store: null, new FixedTimeProvider(CreatedAt), _ => ComparisonId);
        var fractionalScalar = a with
        {
            Scalars = new Dictionary<string, LocalComparisonObservedScalar>(a.Scalars, StringComparer.Ordinal)
            {
                ["execution_count"] = Recorded(1.5m, referenceA),
            },
        };
        var fractionalNamed = a with
        {
            NamedFamilies = Array.AsReadOnly(a.NamedFamilies.Select(family =>
                family.Family == "tool"
                    ? new LocalComparisonNamedFamilyFact(
                        "tool",
                        LocalComparisonFactState.Recorded,
                        Array.AsReadOnly(new[]
                        {
                            Named("tool-a", "Tool A", "tool", referenceA,
                                ("call_count", Recorded(1.5m, referenceA)),
                                ("failure_count", ExplicitZero(referenceA)),
                                ("retry_count", ExplicitZero(referenceA))),
                        }),
                        referenceA)
                    : family).ToArray()),
        };
        var nonBinarySessionCount = a with
        {
            Scalars = new Dictionary<string, LocalComparisonObservedScalar>(a.Scalars, StringComparer.Ordinal)
            {
                ["error_session_count"] = Recorded(2m, referenceA),
            },
        };

        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([fractionalScalar], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);
        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([fractionalNamed], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);
        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([nonBinarySessionCount], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);
    }

    [Fact]
    public void Prepare_RejectsOversizedCohortsBeforeReadingAnyMemberFact()
    {
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var service = new LocalComparisonApplicationService(
            store: null, new FixedTimeProvider(CreatedAt), _ => ComparisonId);

        var result = service.Prepare(new(
            RepositoryId,
            new(new CountOnlySessionList(200), 0),
            new([Session(SessionB, RevisionB, referenceB,
                ExplicitZero(referenceB), ExplicitZero(referenceB), [])], 0),
            ScopeConditionDigest()));

        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Prepare_PreservesDerivedAndNamedFieldEvidenceWithoutRawPathReferences()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, RevisionA);
        var cacheReference = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var a = Session(
            SessionA,
            RevisionA,
            referenceA,
            Recorded(10m, referenceA),
            Recorded(2m, cacheReference),
            [
                Named("tool-a", "Tool A", "tool", referenceA,
                    ("call_count", Missing(LocalComparisonFactState.CaptureGap)),
                    ("failure_count", new(
                        new(LocalComparisonFactState.CaptureGap, null),
                        referenceA)),
                    ("retry_count", ExplicitZero(referenceA))),
            ]);
        var b = Session(
            SessionB,
            RevisionB,
            referenceB,
            ExplicitZero(referenceB),
            ExplicitZero(referenceB),
            []);
        var service = new LocalComparisonApplicationService(
            store: null, new FixedTimeProvider(CreatedAt), _ => ComparisonId);

        var prepared = service.Prepare(new(
            RepositoryId,
            new([a], 0),
            new([b], 0),
            ScopeConditionDigest()));

        Assert.Equal(LocalComparisonCreateStatus.Accepted, prepared.Status);
        var snapshot = Assert.IsType<LocalComparisonSnapshotWrite>(prepared.Snapshot);
        var newInput = snapshot.Results.Single(row => row.RowKey == "new_input_tokens");
        Assert.Equal(
            [RevisionA, cacheReference.RevisionSha256],
            snapshot.Evidence
                .Where(item => item.ResultOrdinal == newInput.ResultOrdinal
                    && item.FieldKey == "value" && item.SessionId == SessionA)
                .Select(item => item.RevisionSha256)
                .Order(StringComparer.Ordinal));
        var tool = snapshot.Results.Single(row => row.RowKind == "tool" && row.RowKey == "tool-a");
        Assert.Equal("0", Value(tool, "a_available_session_count"));
        Assert.Equal("0", Value(tool, "a_called_session_count"));
        var failure = Assert.Single(snapshot.Evidence, item =>
            item.ResultOrdinal == tool.ResultOrdinal
            && item.FieldKey == "failure_count"
            && item.SessionId == SessionA);
        Assert.Equal("capture_gap", failure.AvailabilityState);
        Assert.Equal(RevisionA, failure.RevisionSha256);

        var invalid = a with
        {
            Scalars = new Dictionary<string, LocalComparisonObservedScalar>(a.Scalars, StringComparer.Ordinal)
            {
                ["input_tokens"] = Recorded(1m, new(
                    "session_run", "c:\\users\\secret", null, null, null, RevisionA)),
            },
        };
        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([invalid], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);

        var duplicateEvidence = new LocalComparisonFactEvidence(
            LocalComparisonFactState.Recorded,
            referenceA);
        var duplicateEvidenceFact = a with
        {
            Scalars = new Dictionary<string, LocalComparisonObservedScalar>(a.Scalars, StringComparer.Ordinal)
            {
                ["input_tokens"] = new(
                    new(LocalComparisonFactState.Recorded, 1m),
                    Array.AsReadOnly(new[] { duplicateEvidence, duplicateEvidence })),
            },
        };
        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([duplicateEvidenceFact], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);

        var unresolvedAvailableEvidence = a with
        {
            Scalars = new Dictionary<string, LocalComparisonObservedScalar>(a.Scalars, StringComparer.Ordinal)
            {
                ["input_tokens"] = new(
                    new(LocalComparisonFactState.Recorded, 1m),
                    Array.AsReadOnly(new[]
                    {
                        new LocalComparisonFactEvidence(LocalComparisonFactState.Recorded, referenceA),
                        new LocalComparisonFactEvidence(LocalComparisonFactState.Recorded, Reference: null),
                    })),
            },
        };
        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([unresolvedAvailableEvidence], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);

        var ambiguousZeroCoverage = a with
        {
            NamedFamilies = Array.AsReadOnly(a.NamedFamilies.Select(family =>
                family.Family == "skill"
                    ? family with { State = LocalComparisonFactState.Recorded }
                    : family).ToArray()),
        };
        Assert.Equal(LocalComparisonCreateStatus.SelectionInvalid,
            service.Prepare(new(
                RepositoryId,
                new([ambiguousZeroCoverage], 0),
                new([b], 0),
                ScopeConditionDigest())).Status);
    }

    [Fact]
    public void Prepare_LeavesZeroInputCacheRatioUnavailableWithoutCohortTotal()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, RevisionA);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var service = new LocalComparisonApplicationService(
            store: null, new FixedTimeProvider(CreatedAt), _ => ComparisonId);

        var prepared = service.Prepare(new(
            RepositoryId,
            new([Session(SessionA, RevisionA, referenceA,
                ExplicitZero(referenceA), ExplicitZero(referenceA), [])], 0),
            new([Session(SessionB, RevisionB, referenceB,
                ExplicitZero(referenceB), ExplicitZero(referenceB), [])], 0),
            ScopeConditionDigest()));

        Assert.Equal(LocalComparisonCreateStatus.Accepted, prepared.Status);
        var ratio = Assert.IsType<LocalComparisonSnapshotWrite>(prepared.Snapshot).Results.Single(
            row => row.RowKey == "cache_read_ratio");
        Assert.Equal("not_observed=1", Value(ratio, "a_unavailable_states"));
        Assert.Equal("not_observed=1", Value(ratio, "b_unavailable_states"));
        Assert.DoesNotContain(ratio.Values, item => item.Key is "a_total" or "b_total");
    }

    [Fact]
    public void Prepare_RejectsOversizedNamedGraphsBeforeCloningThem()
    {
        var referenceA = new LocalComparisonSourceReference(
            "workspace_session", SessionA, null, null, null, RevisionA);
        var referenceB = new LocalComparisonSourceReference(
            "workspace_session", SessionB, null, null, null, RevisionB);
        var a = Session(SessionA, RevisionA, referenceA,
            ExplicitZero(referenceA), ExplicitZero(referenceA), []);
        a = a with
        {
            NamedFamilies = Array.AsReadOnly(a.NamedFamilies.Select(family =>
                family.Family == "tool"
                    ? new LocalComparisonNamedFamilyFact(
                        "tool",
                        LocalComparisonFactState.Recorded,
                        new OversizedNamedItemList(referenceA),
                        referenceA)
                    : family).ToArray()),
        };
        var b = Session(SessionB, RevisionB, referenceB,
            ExplicitZero(referenceB), ExplicitZero(referenceB), []);
        var service = new LocalComparisonApplicationService(
            store: null, new FixedTimeProvider(CreatedAt), _ => ComparisonId);

        var result = service.Prepare(new(
            RepositoryId,
            new([a], 0),
            new([b], 0),
            ScopeConditionDigest()));

        Assert.Equal(LocalComparisonCreateStatus.TooLarge, result.Status);
        Assert.Null(result.Snapshot);
    }

    [Fact]
    public void Create_PersistsTheCompleteApplicationReceiptForRestartOnlyReads()
    {
        var directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"local-comparison-app-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = System.IO.Path.Combine(directory, "comparison.sqlite");
        try
        {
            new SqliteSessionStore(path).CreateSchema();
            using (var connection = Open(path))
            {
                LocalRepositoryCatalogSchemaV1.Ensure(connection);
                LocalArchiveSchemaV1.Ensure(connection);
                LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
                LocalComparisonSchemaV1.Ensure(connection);
                Execute(connection, $"""
                    INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at)
                    VALUES('{RepositoryId}','Repository',1,'{Timestamp(CreatedAt)}','{Timestamp(CreatedAt)}');
                    INSERT INTO sessions(
                      session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
                    VALUES('{SessionA}','active','unbound','{Timestamp(CreatedAt)}','not_captured','{Timestamp(CreatedAt)}','{Timestamp(CreatedAt)}'),
                          ('{SessionB}','active','unbound','{Timestamp(CreatedAt)}','not_captured','{Timestamp(CreatedAt)}','{Timestamp(CreatedAt)}');
                    """);
            }
            var referenceA = new LocalComparisonSourceReference(
                "workspace_session", SessionA, null, null, null, RevisionA);
            var referenceB = new LocalComparisonSourceReference(
                "workspace_session", SessionB, null, null, null, RevisionB);
            var draft = new LocalComparisonDraft(
                RepositoryId,
                new([Session(SessionA, RevisionA, referenceA,
                    Recorded(10m, referenceA), Recorded(2m, referenceA), [])], 0),
                new([Session(SessionB, RevisionB, referenceB,
                    Recorded(20m, referenceB), Recorded(4m, referenceB), [])], 0),
                ScopeConditionDigest());
            var application = new LocalComparisonApplicationService(
                new SqliteLocalComparisonStore(path, new FixedTimeProvider(CreatedAt)),
                new FixedTimeProvider(CreatedAt),
                _ => ComparisonId);

            var created = application.Create(draft, default);

            Assert.Equal(LocalComparisonCreateStatus.Accepted, created.Status);
            var restarted = new SqliteLocalComparisonStore(path, new FixedTimeProvider(CreatedAt));
            var read = restarted.Read(RepositoryId, ComparisonId, default);
            Assert.Equal(LocalComparisonReadStatus.Found, read.Status);
            var frozen = Assert.IsType<LocalComparisonFrozenSnapshot>(read.Snapshot);
            Assert.Equal(
                Assert.IsType<LocalComparisonSnapshotWrite>(created.Snapshot).Results[0].Payload,
                frozen.Results[0].Payload);
            Assert.Equal(9, LocalComparisonRegistryV1.Sections.Count);
            Assert.DoesNotContain(frozen.Results,
                row => row.RowKey.Contains("score", StringComparison.OrdinalIgnoreCase)
                    || row.RowKey.Contains("rank", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string Value(LocalComparisonStoredResult row, string key) =>
        row.Values.Single(item => item.Key == key).Value;

    private static LocalComparisonSessionFact Session(
        string sessionId,
        string revision,
        LocalComparisonSourceReference reference,
        LocalComparisonObservedScalar input,
        LocalComparisonObservedScalar cacheRead,
        IReadOnlyList<LocalComparisonNamedItem> tools)
    {
        var scalars = LocalComparisonRegistryV1.RequiredSessionScalarKeys.ToDictionary(
            key => key,
            _ => ExplicitZero(reference),
            StringComparer.Ordinal);
        scalars["input_tokens"] = input;
        scalars["cache_read_tokens"] = cacheRead;
        var families = new[]
        {
            new LocalComparisonNamedFamilyFact("skill", LocalComparisonFactState.ExplicitZero,
                Array.Empty<LocalComparisonNamedItem>(), reference),
            new LocalComparisonNamedFamilyFact(
                "tool",
                tools.Count == 0
                    ? LocalComparisonFactState.ExplicitZero
                    : LocalComparisonFactState.Recorded,
                tools,
                reference),
            new LocalComparisonNamedFamilyFact("subagent", LocalComparisonFactState.ExplicitZero,
                Array.Empty<LocalComparisonNamedItem>(), reference),
        };
        var conditions = LocalComparisonRegistryV1.ConditionKeys.ToDictionary(
            key => key,
            key => new LocalComparisonConditionFact(
                LocalComparisonFactState.Recorded,
                Array.AsReadOnly(new[] { key + "-value" }),
                reference),
            StringComparer.Ordinal);
        return new LocalComparisonSessionFact(
            sessionId, RepositoryId, revision, IsSelectable: true, IsArchived: false,
            scalars, families, conditions,
            new LocalComparisonSessionTargetFact(
                LocalComparisonFactState.Recorded,
                reference,
                LocalComparisonFactState.Recorded,
                CreatedAt,
                reference));
    }

    private static LocalComparisonNamedItem Named(
        string key,
        string displayName,
        string family,
        LocalComparisonSourceReference reference,
        params (string Key, LocalComparisonObservedScalar Value)[] values) =>
        new(family, key, key, displayName,
            values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
            reference);

    private static LocalComparisonObservedScalar Recorded(
        decimal value,
        LocalComparisonSourceReference reference) =>
        new(new LocalComparisonScalarObservation(
            value == 0m ? LocalComparisonFactState.ExplicitZero : LocalComparisonFactState.Recorded,
            value), reference);

    private static LocalComparisonObservedScalar ExplicitZero(
        LocalComparisonSourceReference reference) => Recorded(0m, reference);

    private static LocalComparisonObservedScalar Missing(LocalComparisonFactState state) =>
        new(new LocalComparisonScalarObservation(state, null), Reference: null);

    private static byte[] ScopeConditionDigest() => Convert.FromHexString(
        "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc");

    private static object CreateFact(
        Type factType,
        Type stateType,
        string state,
        decimal? value) =>
        Activator.CreateInstance(factType, Enum.Parse(stateType, state), value)!;

    private const string SessionA = "0198f5b8-0c00-7000-8000-000000000001";
    private const string SessionB = "0198f5b8-0c00-7000-8000-000000000002";
    private const string SessionC = "0198f5b8-0c00-7000-8000-000000000003";
    private const string RepositoryId = "0198f5b8-0c00-7000-8000-000000000020";
    private const string ComparisonId = "0198f5b8-0c00-7000-8000-000000000010";
    private const string RevisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RevisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset CreatedAt =
        DateTimeOffset.Parse("2026-08-28T00:00:00.0000000+00:00", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    private sealed class CountOnlySessionList(int count) : IReadOnlyList<LocalComparisonSessionFact>
    {
        public int Count { get; } = count;
        public LocalComparisonSessionFact this[int index] =>
            throw new InvalidOperationException("must_not_enumerate_oversized_cohort");
        public IEnumerator<LocalComparisonSessionFact> GetEnumerator() =>
            throw new InvalidOperationException("must_not_enumerate_oversized_cohort");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class OversizedNamedItemList(
        LocalComparisonSourceReference reference) : IReadOnlyList<LocalComparisonNamedItem>
    {
        private int enumerationCount;

        public int Count => 2_500;
        public LocalComparisonNamedItem this[int index] => Create(index);

        public IEnumerator<LocalComparisonNamedItem> GetEnumerator()
        {
            var enumeration = ++enumerationCount;
            for (var index = 0; index < Count; index++)
            {
                if (enumeration >= 2 && index == 1_900)
                    throw new InvalidOperationException("must_not_clone_oversized_named_graph");
                yield return Create(index);
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private LocalComparisonNamedItem Create(int index)
        {
            var identity = "tool-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);
            return Named(
                identity,
                new string('x', 256),
                "tool",
                reference,
                ("call_count", Recorded(1m, reference)),
                ("failure_count", ExplicitZero(reference)),
                ("retry_count", ExplicitZero(reference)));
        }
    }
}

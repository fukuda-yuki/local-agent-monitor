using System.Collections;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillProjectionDiagnosticV1Tests
{
    [Fact]
    public void Diagnose_RealRegistryYieldsCurrentForTheAcceptedProductionTuple()
    {
        var registry = SkillInvocationV2ArtifactRegistry.Load();
        var tuple = Assert.Single(registry.Entries).Tuple;

        var outcome = SkillProjectionDiagnosticV1.Diagnose(isSnapshotAvailable: true, tuple);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Current, outcome);
    }

    [Fact]
    public void Diagnose_TupleAbsentFromGreatestRevisionIsInvalid()
    {
        var registry = SkillInvocationV2ArtifactRegistry.Load();
        var missingTuple = Assert.Single(registry.Entries).Tuple with { SourceApplicationVersion = "9.9.9" };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(isSnapshotAvailable: true, missingTuple);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Invalid, outcome);
    }

    [Fact]
    public void Diagnose_TupleRevokedInGreatestWithLowerContiguousAcceptanceIsStale()
    {
        var tuple = SampleTuple();
        var history = new[]
        {
            Revision(1, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted)),
            Revision(2, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Revoked))
        };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Stale, outcome);
    }

    [Fact]
    public void Diagnose_TupleRevokedInGreatestWithoutAnyPriorAcceptanceIsUnavailable()
    {
        var tuple = SampleTuple();
        var history = new[]
        {
            Revision(1, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Revoked))
        };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Unavailable, outcome);
    }

    [Fact]
    public void Diagnose_EmptyHistoryIsUnavailable()
    {
        var outcome = SkillProjectionDiagnosticV1.Diagnose(
            true, SampleTuple(), Array.Empty<SkillProjectionDiagnosticV1RegistryRevision>());

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Unavailable, outcome);
    }

    [Fact]
    public void Diagnose_HistoryStartingAtRevisionTwoIsUnavailable()
    {
        var tuple = SampleTuple();
        var history = new[] { Revision(2, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted)) };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Unavailable, outcome);
    }

    [Fact]
    public void Diagnose_HistoryWithAGapIsUnavailable()
    {
        var tuple = SampleTuple();
        var history = new[]
        {
            Revision(1, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted)),
            Revision(3, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted))
        };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Unavailable, outcome);
    }

    [Fact]
    public void Diagnose_HistoryWithADuplicatedRevisionIsUnavailable()
    {
        var tuple = SampleTuple();
        var history = new[]
        {
            Revision(1, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted)),
            Revision(1, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted))
        };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Unavailable, outcome);
    }

    [Fact]
    public void Diagnose_HistoryWhoseDeclaredRevisionDisagreesWithItsPositionIsUnavailable()
    {
        var tuple = SampleTuple();
        var history = new[]
        {
            Revision(2, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted)),
            Revision(1, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted))
        };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Unavailable, outcome);
    }

    [Fact]
    public void Diagnose_NonavailableSnapshotIsInvalidWithoutConsultingHistory()
    {
        var outcome = SkillProjectionDiagnosticV1.Diagnose(
            isSnapshotAvailable: false, SampleTuple(), new ThrowingHistory());

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Invalid, outcome);
    }

    [Theory]
    [MemberData(nameof(SingleMemberMutations))]
    public void Diagnose_TupleDifferingInExactlyOneMemberIsInvalid(SkillInvocationV2CompatibilityTuple mutatedTuple)
    {
        var accepted = SampleTuple();
        var history = new[] { Revision(1, Entry(accepted, SkillInvocationV2CompatibilityDisposition.Accepted)) };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, mutatedTuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Invalid, outcome);
    }

    public static IEnumerable<object[]> SingleMemberMutations()
    {
        var baseline = SampleTuple();
        yield return new object[] { baseline with { SourceApplicationVersion = baseline.SourceApplicationVersion + "-x" } };
        yield return new object[] { baseline with { AdapterVersion = baseline.AdapterVersion + "-x" } };
        yield return new object[] { baseline with { NormalizationVersion = baseline.NormalizationVersion + "-x" } };
        yield return new object[] { baseline with { PayloadSchema = baseline.PayloadSchema + "-x" } };
        yield return new object[] { baseline with { SchemaFingerprint = baseline.SchemaFingerprint + "-x" } };
    }

    [Fact]
    public void Diagnose_TupleMatchingIsCaseSensitiveAndOrdinal()
    {
        var accepted = SampleTuple();
        var caseChangedOnly = accepted with { AdapterVersion = accepted.AdapterVersion.ToUpperInvariant() };
        var history = new[] { Revision(1, Entry(accepted, SkillInvocationV2CompatibilityDisposition.Accepted)) };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, caseChangedOnly, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Invalid, outcome);
    }

    [Fact]
    public void Diagnose_AddingAnUnrelatedAcceptedTupleInALaterRevisionDoesNotChangeExistingTupleOutcome()
    {
        var tupleA = SampleTuple();
        var tupleB = tupleA with { AdapterVersion = tupleA.AdapterVersion + "-other" };
        var history = new[]
        {
            Revision(1, Entry(tupleA, SkillInvocationV2CompatibilityDisposition.Accepted)),
            Revision(
                2,
                Entry(tupleA, SkillInvocationV2CompatibilityDisposition.Accepted),
                Entry(tupleB, SkillInvocationV2CompatibilityDisposition.Accepted))
        };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tupleA, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Current, outcome);
    }

    [Fact]
    public void Diagnose_LaterRevisionDroppingAnAcceptedTupleIsInvalidNotStale()
    {
        var tuple = SampleTuple();
        var otherTuple = tuple with { AdapterVersion = tuple.AdapterVersion + "-other" };
        var history = new[]
        {
            Revision(1, Entry(tuple, SkillInvocationV2CompatibilityDisposition.Accepted)),
            Revision(2, Entry(otherTuple, SkillInvocationV2CompatibilityDisposition.Accepted))
        };

        var outcome = SkillProjectionDiagnosticV1.Diagnose(true, tuple, history);

        Assert.Equal(SkillProjectionDiagnosticV1Outcome.Invalid, outcome);
    }

    private static SkillInvocationV2CompatibilityTuple SampleTuple() => new(
        "1.0.1",
        "adapter-sample",
        "normalize-sample",
        "payload-schema-sample",
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

    private static SkillInvocationV2CompatibilityRegistryEntry Entry(
        SkillInvocationV2CompatibilityTuple tuple,
        SkillInvocationV2CompatibilityDisposition disposition) =>
        new(tuple, disposition);

    private static SkillProjectionDiagnosticV1RegistryRevision Revision(
        int revision, params SkillInvocationV2CompatibilityRegistryEntry[] entries) =>
        new(revision, entries);

    private sealed class ThrowingHistory : IReadOnlyList<SkillProjectionDiagnosticV1RegistryRevision>
    {
        public SkillProjectionDiagnosticV1RegistryRevision this[int index] =>
            throw new InvalidOperationException("History must not be touched for a nonavailable snapshot.");

        public int Count =>
            throw new InvalidOperationException("History must not be touched for a nonavailable snapshot.");

        public IEnumerator<SkillProjectionDiagnosticV1RegistryRevision> GetEnumerator() =>
            throw new InvalidOperationException("History must not be touched for a nonavailable snapshot.");

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

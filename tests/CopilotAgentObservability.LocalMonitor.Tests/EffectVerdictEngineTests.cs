using System.Globalization;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class EffectVerdictEngineTests
{
    [Fact]
    public void Evaluate_InvalidLinkage_TakesPrecedenceAndPreservesDeterministicReasons()
    {
        var facts = Facts(pre: Sessions("pre", true, 3), post: Sessions("post", true, 3),
            linkageValid: false, reasons: ["application_not_active", "proposal_revision_stale"]);

        var result = EffectVerdictEngine.Evaluate(facts);

        Assert.Equal(EffectVerdict.InsufficientEvidence, result.Verdict);
        Assert.Equal(["application_not_active", "proposal_revision_stale", "invalid_linkage"], result.Reasons);
        Assert.Equal(3, result.PreCount);
        Assert.Equal(3, result.PostCount);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(2, 3)]
    [InlineData(3, 2)]
    public void Evaluate_CohortsBelowThree_AreInsufficient(int preCount, int postCount)
    {
        var result = EffectVerdictEngine.Evaluate(Facts(Sessions("pre", true, preCount), Sessions("post", true, postCount)));

        Assert.Equal(EffectVerdict.InsufficientEvidence, result.Verdict);
        Assert.Contains("insufficient_cohort", result.Reasons);
    }

    [Fact]
    public void Evaluate_ExactThreeByThree_WithEqualQualityAndSubthresholdEfficiency_IsNoChange()
    {
        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3, duration: 100, tokens: 100),
            Sessions("post", true, 3, duration: 95, tokens: 95)));

        Assert.Equal(EffectVerdict.NoChange, result.Verdict);
        Assert.Equal(3, result.PrePass);
        Assert.Equal(3, result.PostPass);
    }

    [Fact]
    public void Evaluate_MissingQualityEvidence_IsInsufficientBeforeSevereFailure()
    {
        var post = Sessions("post", true, 3, duration: 50, tokens: 50).ToArray();
        post[0] = post[0] with { EvidenceIds = [] };
        post[1] = post[1] with { SevereFailure = true };

        var result = EffectVerdictEngine.Evaluate(Facts(Sessions("pre", true, 3, duration: 100, tokens: 100), post));

        Assert.Equal(EffectVerdict.InsufficientEvidence, result.Verdict);
        Assert.Contains("missing_quality_evidence", result.Reasons);
    }

    [Fact]
    public void Evaluate_PostSevereFailure_RegressesBeforeQualityOrEfficiency()
    {
        var post = Sessions("post", true, 3, duration: 1, tokens: 1).ToArray();
        post[0] = post[0] with { SevereFailure = true };

        var result = EffectVerdictEngine.Evaluate(Facts(Sessions("pre", false, 3, duration: 100, tokens: 100), post));

        Assert.Equal(EffectVerdict.Regressed, result.Verdict);
        Assert.Equal(["post_severe_failure"], result.Reasons);
    }

    [Fact]
    public void Evaluate_QualityPassRates_UseExactCrossMultiplicationForUnequalCohorts()
    {
        var pre = Sessions("pre", true, 3, duration: 100, tokens: 100).ToArray();
        pre[2] = pre[2] with { QualityPass = false };
        var post = Sessions("post", true, 4, duration: 1, tokens: 1).ToArray();
        post[3] = post[3] with { QualityPass = false };

        var result = EffectVerdictEngine.Evaluate(Facts(pre, post));

        Assert.Equal(EffectVerdict.Improved, result.Verdict);
        Assert.Equal(["quality_improved"], result.Reasons);
    }

    [Fact]
    public void Evaluate_LowerQuality_RegressesDespiteHugeEfficiencyGain()
    {
        var post = Sessions("post", true, 3, duration: 1, tokens: 1).ToArray();
        post[2] = post[2] with { QualityPass = false };

        var result = EffectVerdictEngine.Evaluate(Facts(Sessions("pre", true, 3, duration: 1000, tokens: 1000), post));

        Assert.Equal(EffectVerdict.Regressed, result.Verdict);
        Assert.Equal(["quality_regressed"], result.Reasons);
    }

    [Fact]
    public void Evaluate_HigherQuality_ImprovesDespiteHugeEfficiencyLoss()
    {
        var pre = Sessions("pre", true, 3, duration: 1, tokens: 1).ToArray();
        pre[2] = pre[2] with { QualityPass = false };

        var result = EffectVerdictEngine.Evaluate(Facts(pre, Sessions("post", true, 3, duration: 1000, tokens: 1000)));

        Assert.Equal(EffectVerdict.Improved, result.Verdict);
        Assert.Equal(["quality_improved"], result.Reasons);
    }

    [Fact]
    public void Evaluate_UsesSortedOddAndEvenDecimalMediansWithoutMutatingInput()
    {
        var pre = Sessions("pre", true, durations: [300L, 100L, 200L], tokens: [30L, 10L, 20L]);
        var post = Sessions("post", true, durations: [70L, 10L, 90L, 30L], tokens: [7L, 1L, 9L, 3L]);
        var originalPre = pre.ToArray();
        var originalPost = post.ToArray();

        var result = EffectVerdictEngine.Evaluate(Facts(pre, post));

        Assert.Equal(200m, result.PreDurationMedian);
        Assert.Equal(50m, result.PostDurationMedian);
        Assert.Equal(.75m, result.DurationDelta);
        Assert.Equal(20m, result.PreTokenMedian);
        Assert.Equal(5m, result.PostTokenMedian);
        Assert.Equal(.75m, result.TokenDelta);
        Assert.Equal(originalPre, pre);
        Assert.Equal(originalPost, post);
    }

    [Theory]
    [InlineData(100000L, 90001L, EffectVerdict.NoChange)]
    [InlineData(100L, 90L, EffectVerdict.Improved)]
    [InlineData(100L, 89L, EffectVerdict.Improved)]
    [InlineData(100L, 110L, EffectVerdict.NoChange)]
    [InlineData(100L, 111L, EffectVerdict.Regressed)]
    public void Evaluate_UsesExactTenPercentEfficiencyThresholds(long preDuration, long postDuration, EffectVerdict expected)
    {
        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3, duration: preDuration, tokens: 100),
            Sessions("post", true, 3, duration: postDuration, tokens: 100)));

        Assert.Equal(expected, result.Verdict);
    }

    [Fact]
    public void Evaluate_OneMaterialImprovementAndNeutralMetric_IsImproved()
    {
        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3, duration: 100, tokens: 100),
            Sessions("post", true, 3, duration: 90, tokens: 100)));

        Assert.Equal(EffectVerdict.Improved, result.Verdict);
        Assert.Equal(["duration_improved"], result.Reasons);
    }

    [Fact]
    public void Evaluate_OneMaterialRegressionAndNeutralMetric_IsRegressed()
    {
        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3, duration: 100, tokens: 100),
            Sessions("post", true, 3, duration: 111, tokens: 100)));

        Assert.Equal(EffectVerdict.Regressed, result.Verdict);
        Assert.Equal(["duration_regressed"], result.Reasons);
    }

    [Fact]
    public void Evaluate_MixedMaterialEfficiency_IsNoChangeWithStableMetricOrdering()
    {
        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3, duration: 100, tokens: 100),
            Sessions("post", true, 3, duration: 90, tokens: 111)));

        Assert.Equal(EffectVerdict.NoChange, result.Verdict);
        Assert.Equal(["duration_improved", "tokens_regressed"], result.Reasons);
    }

    [Theory]
    [InlineData(null, 100L)]
    [InlineData(0L, 100L)]
    [InlineData(-1L, 100L)]
    public void Evaluate_IncompleteOrNonpositiveMetric_DoesNotParticipate(long? invalidDuration, long tokens)
    {
        var pre = Sessions("pre", true, 3, duration: invalidDuration, tokens: tokens).ToArray();
        pre[1] = pre[1] with { DurationMs = invalidDuration };
        var post = Sessions("post", true, 3, duration: invalidDuration, tokens: tokens).ToArray();
        post[2] = post[2] with { DurationMs = invalidDuration };

        var result = EffectVerdictEngine.Evaluate(Facts(pre, post));

        Assert.Equal(EffectVerdict.NoChange, result.Verdict);
        Assert.Null(result.PreDurationMedian);
        Assert.Equal(100m, result.PreTokenMedian);
    }

    [Fact]
    public void Evaluate_NoCommonCompleteMetricAtEqualQuality_IsInsufficient()
    {
        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3, duration: null, tokens: 0),
            Sessions("post", true, 3, duration: -1, tokens: null)));

        Assert.Equal(EffectVerdict.InsufficientEvidence, result.Verdict);
        Assert.Equal(["missing_efficiency_evidence"], result.Reasons);
    }

    [Fact]
    public void Evaluate_SuppliedInsufficiencyReasons_AreSortedAndTakePrecedence()
    {
        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3),
            Sessions("post", true, 3),
            reasons: ["z_reason", "a_reason", "z_reason"]));

        Assert.Equal(EffectVerdict.InsufficientEvidence, result.Verdict);
        Assert.Equal(["a_reason", "z_reason"], result.Reasons);
    }

    [Fact]
    public void Evaluate_IsCultureInvariantAndKeepsUnroundedDecimalDelta()
    {
        using var culture = new CultureScope("fr-FR");

        var result = EffectVerdictEngine.Evaluate(Facts(
            Sessions("pre", true, 3, duration: 3, tokens: 3),
            Sessions("post", true, 3, duration: 2, tokens: 2)));

        Assert.Equal(1m / 3m, result.DurationDelta);
        Assert.Equal(1m / 3m, result.TokenDelta);
        Assert.Equal(EffectVerdict.Improved, result.Verdict);
    }

    private static EffectComparisonFacts Facts(
        IReadOnlyList<SessionEffectFacts> pre,
        IReadOnlyList<SessionEffectFacts> post,
        bool linkageValid = true,
        IReadOnlyList<string>? reasons = null) =>
        new(linkageValid, pre, post, reasons ?? []);

    private static IReadOnlyList<SessionEffectFacts> Sessions(
        string side,
        bool qualityPass,
        int count = 3,
        long? duration = 100,
        long? tokens = 100) =>
        Enumerable.Range(0, count).Select(index => Session(side, qualityPass, index, duration, tokens)).ToArray();

    private static IReadOnlyList<SessionEffectFacts> Sessions(
        string side,
        bool qualityPass,
        long[] durations,
        long[] tokens) =>
        durations.Select((duration, index) => Session(side, qualityPass, index, duration, tokens[index])).ToArray();

    private static SessionEffectFacts Session(string side, bool qualityPass, int index, long? duration, long? tokens) =>
        new(Guid.CreateVersion7(), side, $"case-{index}", qualityPass, false, duration, tokens, [$"evidence-{index}"]);

    private sealed class CultureScope : IDisposable
    {
        private readonly CultureInfo _original = CultureInfo.CurrentCulture;

        public CultureScope(string name) => CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);

        public void Dispose() => CultureInfo.CurrentCulture = _original;
    }
}

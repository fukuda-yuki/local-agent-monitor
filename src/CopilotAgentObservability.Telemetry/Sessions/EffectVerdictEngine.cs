namespace CopilotAgentObservability.Telemetry.Sessions;

public static class EffectVerdictEngine
{
    private const decimal MaterialChange = 0.10m;

    public static EffectVerdictResult Evaluate(EffectComparisonFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var pre = facts.Pre ?? [];
        var post = facts.Post ?? [];
        var prePass = pre.Count(session => session.QualityPass);
        var postPass = post.Count(session => session.QualityPass);
        var reasons = SortedReasons(facts.InsufficiencyReasons);

        if (!facts.LinkageValid)
        {
            reasons.Add("invalid_linkage");
            return Result(EffectVerdict.InsufficientEvidence, prePass, pre.Count, postPass, post.Count, null, null, reasons);
        }

        if (pre.Count < 3 || post.Count < 3)
        {
            reasons.Add("insufficient_cohort");
            return Result(EffectVerdict.InsufficientEvidence, prePass, pre.Count, postPass, post.Count, null, null, reasons);
        }

        var duration = Metric(pre, post, session => session.DurationMs);
        var tokens = Metric(pre, post, session => session.TotalTokens);

        if (reasons.Count > 0 || pre.Any(MissingQualityEvidence) || post.Any(MissingQualityEvidence))
        {
            if (pre.Any(MissingQualityEvidence) || post.Any(MissingQualityEvidence))
            {
                reasons.Add("missing_quality_evidence");
            }

            return Result(EffectVerdict.InsufficientEvidence, prePass, pre.Count, postPass, post.Count, duration, tokens, reasons);
        }

        if (post.Any(session => session.SevereFailure))
        {
            return Result(EffectVerdict.Regressed, prePass, pre.Count, postPass, post.Count, duration, tokens, ["post_severe_failure"]);
        }

        var qualityComparison = (long)postPass * pre.Count - (long)prePass * post.Count;
        if (qualityComparison > 0)
        {
            return Result(EffectVerdict.Improved, prePass, pre.Count, postPass, post.Count, duration, tokens, ["quality_improved"]);
        }

        if (qualityComparison < 0)
        {
            return Result(EffectVerdict.Regressed, prePass, pre.Count, postPass, post.Count, duration, tokens, ["quality_regressed"]);
        }

        if (duration is null && tokens is null)
        {
            return Result(EffectVerdict.InsufficientEvidence, prePass, pre.Count, postPass, post.Count, duration, tokens, ["missing_efficiency_evidence"]);
        }

        var changes = new[]
        {
            Classify("duration", duration),
            Classify("tokens", tokens),
        }.Where(change => change is not null).Cast<MetricChange>().ToArray();
        var hasImprovement = changes.Any(change => change.Improved);
        var hasRegression = changes.Any(change => change.Regressed);
        var metricReasons = changes.Where(change => change.Improved || change.Regressed)
            .Select(change => $"{change.Name}_{(change.Improved ? "improved" : "regressed")}")
            .ToArray();

        var verdict = hasImprovement && !hasRegression
            ? EffectVerdict.Improved
            : hasRegression && !hasImprovement
                ? EffectVerdict.Regressed
                : EffectVerdict.NoChange;

        return Result(verdict, prePass, pre.Count, postPass, post.Count, duration, tokens, metricReasons);
    }

    private static bool MissingQualityEvidence(SessionEffectFacts session) => session.EvidenceIds is not { Count: > 0 };

    private static List<string> SortedReasons(IReadOnlyList<string>? reasons) =>
        (reasons ?? []).Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    private static MetricSummary? Metric(
        IReadOnlyList<SessionEffectFacts> pre,
        IReadOnlyList<SessionEffectFacts> post,
        Func<SessionEffectFacts, long?> value)
    {
        if (pre.Any(session => value(session) is not > 0) || post.Any(session => value(session) is not > 0))
        {
            return null;
        }

        var preMedian = Median(pre.Select(session => (decimal)value(session)!.Value));
        var postMedian = Median(post.Select(session => (decimal)value(session)!.Value));
        return new MetricSummary(preMedian, postMedian, (preMedian - postMedian) / preMedian);
    }

    private static decimal Median(IEnumerable<decimal> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 1 ? ordered[middle] : (ordered[middle - 1] + ordered[middle]) / 2m;
    }

    private static MetricChange? Classify(string name, MetricSummary? metric) => metric is null
        ? null
        : new MetricChange(name, metric.Delta >= MaterialChange, metric.Delta < -MaterialChange);

    private static EffectVerdictResult Result(
        EffectVerdict verdict, int prePass, int preCount, int postPass, int postCount,
        MetricSummary? duration, MetricSummary? tokens, IReadOnlyList<string> reasons) =>
        new(verdict, prePass, preCount, postPass, postCount,
            duration?.PreMedian, duration?.PostMedian, duration?.Delta,
            tokens?.PreMedian, tokens?.PostMedian, tokens?.Delta, reasons);

    private sealed record MetricSummary(decimal PreMedian, decimal PostMedian, decimal Delta);
    private sealed record MetricChange(string Name, bool Improved, bool Regressed);
}

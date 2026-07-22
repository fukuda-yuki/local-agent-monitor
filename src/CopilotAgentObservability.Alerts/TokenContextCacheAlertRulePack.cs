namespace CopilotAgentObservability.Alerts;

public static class TokenContextCacheAlertRulePack
{
    public static IReadOnlyList<IAlertRule> CreateRules() =>
    [
        new HighInitialContextUtilizationRule(),
        new MonotonicContextGrowthRule(),
        new ContextGrowthWithOutputCollapseRule(),
        new LowCacheReadRatioRule(),
        new NearContextLimitTurnRule(),
    ];
}

internal static class TokenAlertContract
{
    public const string Version = "1";
    public const string InputTokens = "input-tokens";
    public const string OutputTokens = "output-tokens";
    public const string CacheReadTokens = "cache-read-tokens";
    public const string EffectiveContextLimit = "effective-context-limit";
    public const string Tokens = "tokens";
    public const string Ratio = "ratio";
    public const string Fraction = "fraction";
    public const string Turns = "turns";
    public const decimal MaximumTokenMetric = 1_000_000_000_000m;

    public static readonly string[] ApplicableSources =
    [
        "claude-code", "codex-app", "codex-cli", "github-copilot-cli", "github-copilot-vscode",
    ];

    public static readonly string[] Suppressions =
    [
        "missing_required_capability", "rule_disabled", "source_not_applicable",
        "minimum-sample-unmet", "incomplete-snapshot", "historical-input", "mixed-evaluation-dimension",
        "token-metric-out-of-domain", "token-arithmetic-overflow",
    ];

    public static AlertRuleOutcome? SuppressIncomplete(AlertNormalizedSnapshot snapshot)
    {
        if (snapshot.CompletenessReasons.Contains("historical_summary_only", StringComparer.Ordinal))
        {
            return Suppressed("historical-input");
        }

        return snapshot.Completeness is AlertCompleteness.Unbound or AlertCompleteness.Partial
            ? Suppressed("incomplete-snapshot")
            : null;
    }

    public static AlertRuleOutcome Suppressed(string code) => new([], [new(code)]);
    public static AlertRuleOutcome Matches(IEnumerable<AlertRuleMatch> matches) => new(matches.ToArray(), []);

    public static IReadOnlyList<AlertSignal> OrderedLlmSignals(AlertNormalizedSnapshot snapshot) => snapshot.Signals
        .Where(signal => signal.Kind == AlertSignalKind.LlmCall)
        .OrderBy(signal => signal.Sequence)
        .ThenBy(signal => signal.ObservedAt)
        .ThenBy(signal => signal.SignalId, StringComparer.Ordinal)
        .ToArray();

    public static decimal? Metric(AlertSignal signal, string name) => signal.Metrics
        .SingleOrDefault(metric => metric.Name == name && metric.Unit == Tokens)?.Value;

    public static string? Key(AlertSignal signal, string name) => signal.ComparableKeys
        .SingleOrDefault(key => key.Name == name && key.Kind == AlertComparableKeyKind.MetadataToken)?.Value;

    public static bool TryCommonGroup(AlertSignal signal, bool toolSchema, bool output, bool cache, out string group)
    {
        var parts = new List<string?>
        {
            Key(signal, "model-id"),
            Key(signal, "input-token-semantics-version"),
        };
        if (output) parts.Add(Key(signal, "output-token-semantics-version"));
        if (cache) parts.Add(Key(signal, "cache-read-token-semantics-version"));
        if (toolSchema) parts.Add(Key(signal, "tool-schema-generation"));
        if (parts.Any(part => part is null))
        {
            group = string.Empty;
            return false;
        }

        group = string.Join('\n', parts);
        return true;
    }

    public static bool TryLimitGroup(AlertSignal signal, out string group, out decimal limit)
    {
        limit = Metric(signal, EffectiveContextLimit) ?? 0;
        var model = Key(signal, "model-id");
        var semantics = Key(signal, "input-token-semantics-version");
        var authority = Key(signal, "effective-context-limit-authority");
        var version = Key(signal, "effective-context-limit-version");
        if (limit <= 0 || model is null || semantics is null || authority is null || version is null)
        {
            group = string.Empty;
            return false;
        }

        group = string.Join('\n', model, semantics, authority, version, limit.ToString("G29", System.Globalization.CultureInfo.InvariantCulture));
        return true;
    }

    public static bool HasMixedCommonDimension(IReadOnlyList<AlertSignal> signals, bool toolSchema, bool output, bool cache) => signals
        .Where(signal => signal.Status == AlertSignalStatus.Success)
        .Select(signal => TryCommonGroup(signal, toolSchema, output, cache, out var group) ? group : null)
        .Where(group => group is not null)
        .Distinct(StringComparer.Ordinal)
        .Take(2)
        .Count() > 1;

    public static bool HasMixedLimitDimension(IReadOnlyList<AlertSignal> signals) => signals
        .Where(signal => signal.Status == AlertSignalStatus.Success)
        .Select(signal => TryLimitGroup(signal, out var group, out _) ? group : null)
        .Where(group => group is not null)
        .Distinct(StringComparer.Ordinal)
        .Take(2)
        .Count() > 1;

    public static bool HasOutOfDomainTokenMetric(IReadOnlyList<AlertSignal> signals, params string[] metricNames) => signals
        .Where(signal => signal.Status == AlertSignalStatus.Success)
        .SelectMany(signal => metricNames.Select(name => (Name: name, Value: Metric(signal, name))))
        .Any(metric => metric.Value is { } value && !InTokenMetricDomain(metric.Name, value));

    public static bool TrySum(IEnumerable<decimal> values, out decimal sum)
    {
        sum = 0m;
        try
        {
            foreach (var value in values) sum += value;
            return true;
        }
        catch (OverflowException)
        {
            sum = 0m;
            return false;
        }
    }

    public static AlertSeverity? HigherSeverity(decimal value, decimal warning, decimal critical) =>
        value >= critical ? AlertSeverity.Critical : value >= warning ? AlertSeverity.Warning : null;

    public static AlertSeverity? LowerSeverity(decimal value, decimal warning, decimal critical) =>
        value < critical ? AlertSeverity.Critical : value < warning ? AlertSeverity.Warning : null;

    public static decimal Median(IEnumerable<decimal> values)
    {
        var ordered = values.Order().ToArray();
        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? ordered[middle - 1] + (ordered[middle] - ordered[middle - 1]) / 2m
            : ordered[middle];
    }

    public static AlertRuleMatch Match(
        AlertSeverity severity,
        IReadOnlyList<AlertSignal> included,
        int llmCount,
        int eligibleCount,
        params AlertObservedValue[] formulaValues)
    {
        var observed = new List<AlertObservedValue>(formulaValues)
        {
            new("eligible-turn-count", Turns, eligibleCount),
            new("included-turn-count", Turns, included.Count),
            new("excluded-turn-count", Turns, llmCount - included.Count),
        };
        return new(
            severity,
            observed,
            included.Select(signal => signal.Evidence).ToArray(),
            included.Min(signal => signal.ObservedAt),
            included.Max(signal => signal.ObservedAt));
    }

    public static AlertThresholdDefinition HigherThreshold(string name, string unit, decimal warning, decimal critical, decimal maximum = 1_000_000m) =>
        new(name, unit, AlertThresholdDirection.HigherIsWorse, 0m, maximum, warning, critical);

    public static AlertThresholdDefinition LowerThreshold(string name, string unit, decimal warning, decimal critical) =>
        new(name, unit, AlertThresholdDirection.LowerIsWorse, 0m, 1m, warning, critical);

    private static bool InTokenMetricDomain(string name, decimal value) =>
        value == decimal.Truncate(value)
        && value <= MaximumTokenMetric
        && (name == EffectiveContextLimit ? value >= 1m : value >= 0m);
}

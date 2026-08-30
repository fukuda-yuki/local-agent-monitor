using System.Text.RegularExpressions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1CompareSpecificationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CompareSpecificationFreezesSectionOrderScalarRulesAndNamedRowReachability()
    {
        var ia = Read("docs", "specifications", "interfaces", "local-monitor-v1-ia.md");
        var section = Slice(ia, "## 7. Repository Session Compare", "## 8. Session detail workspace");
        var normalizedSection = Regex.Replace(section, @"\s+", " ");

        Assert.Equal(
        [
            "1. 対象",
            "2. トークン",
            "3. 入力トークンの内訳",
            "4. 時間・実行量",
            "5. スキル",
            "6. ツール",
            "7. サブエージェント",
            "8. エラー・再試行",
            "9. 比較条件",
        ],
        Regex.Matches(section, @"(?m)^\d+\. .+$").Select(match => match.Value));
        Assert.Contains("available count, per-Session median, minimum, maximum and supplementary total", section, StringComparison.Ordinal);
        Assert.Contains("complete union through search/pagination, not top-N ranking", section, StringComparison.Ordinal);
        Assert.Contains("missing is not zero", section, StringComparison.Ordinal);
        Assert.Contains("every metric drills down to included and unavailable Sessions and exact evidence", section, StringComparison.Ordinal);
        Assert.Contains("Even cardinality uses the exact decimal arithmetic mean of the two central values", normalizedSection, StringComparison.Ordinal);
        Assert.Contains("absolute difference is `B median - A median`", normalizedSection, StringComparison.Ordinal);
        Assert.Contains("only when A median is greater than zero", normalizedSection, StringComparison.Ordinal);
        Assert.Contains("one fractional decimal digit with midpoint-away-from-zero rounding", normalizedSection, StringComparison.Ordinal);
        Assert.Contains("Canonical decimal text is `0`", normalizedSection, StringComparison.Ordinal);
        Assert.Contains("`copilot-agent-observability/local-comparison-selection/v1`", normalizedSection, StringComparison.Ordinal);
        Assert.Contains("four-byte unsigned big-endian UTF-8 byte length", normalizedSection, StringComparison.Ordinal);
        Assert.Contains("cohort `a` followed by cohort `b`", normalizedSection, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareSpecificationFreezesForbiddenLabelsAndDelegatesToTheOwnerWire()
    {
        var ia = Read("docs", "specifications", "interfaces", "local-monitor-v1-ia.md");
        var transport = Read("docs", "specifications", "interfaces", "local-monitor-v1-route-transport.md");
        var section = Slice(ia, "## 7. Repository Session Compare", "## 8. Session detail workspace");

        Assert.Contains("`主要な差`, `比較上の注意` or `品質証拠`", section, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"(?m)^#{1,6}\s+(主要な差|比較上の注意|品質証拠)\s*$"), section);
        Assert.DoesNotContain("/api/local-monitor/v1/comparisons", ia + transport, StringComparison.Ordinal);
        Assert.Contains("local-monitor-v1-comparison.md", transport, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareOperationalStateIsExcludedFor24HoursByNamedStagingCategories()
    {
        var backup = Read("docs", "specifications", "interfaces", "runtime-backup-restore.md");
        var transport = Read("docs", "specifications", "interfaces", "local-monitor-v1-route-transport.md");
        var normalizedBackup = Regex.Replace(backup, @"\s+", " ");
        var normalizedTransport = Regex.Replace(transport, @"\s+", " ");

        Assert.Contains("deterministic Compare snapshots are 24-hour non-backed-up state", backup, StringComparison.Ordinal);
        Assert.Contains(
            "comparison_snapshot\ncomparison_cohort_membership\ncomparison_result\ncomparison_evidence\ncomparison_expiry_tombstone",
            backup.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("foreign-key-safe staging drop order", backup, StringComparison.Ordinal);
        Assert.Contains("manifest/database member and restore omit every category", backup, StringComparison.Ordinal);
        Assert.Contains("every registered exact table across all five categories", normalizedBackup, StringComparison.Ordinal);
        Assert.Contains("reverse owner dependency order", normalizedBackup, StringComparison.Ordinal);
        Assert.Contains("zero unregistered `local_comparison_*` objects", normalizedBackup, StringComparison.Ordinal);
        Assert.DoesNotContain("`DROP TABLE \"local_comparison_expiry_tombstones\"`", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("grants no exclusion to future #166 snapshot/result/evidence tables", transport, StringComparison.Ordinal);
        Assert.Contains("all five registered Compare staging categories", normalizedTransport, StringComparison.Ordinal);
    }

    [Fact]
    public void HistoricalAnalysisHumanRouteIsRetiredWhileMachineApisRemainFrozen()
    {
        var routeTransport = Read("docs", "specifications", "interfaces", "local-monitor-v1-route-transport.md");
        var section = Slice(routeTransport, "### `/historical-analysis`", "## 14. Sanitized-only and logging");
        var normalized = section.ReplaceLineEndings(" ");

        Assert.Contains("The standalone human page is retired.", section, StringComparison.Ordinal);
        Assert.Contains("Versioned `/api/historical-analysis/v1/*` machine APIs remain unchanged.", normalized, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(Path.Combine([RepositoryRoot, .. path]));

    private static string Slice(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        var endIndex = text.IndexOf(end, startIndex + start.Length, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex);
        return text[startIndex..endIndex];
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx")))
            {
                return directory.FullName;
            }
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

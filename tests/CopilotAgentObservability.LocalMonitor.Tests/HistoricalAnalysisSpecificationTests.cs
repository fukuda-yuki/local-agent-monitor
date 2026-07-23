namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalAnalysisSpecificationTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void CanonicalInterfacePinsHistoricalAnalysisRoutesStatesAndSafetyBoundary()
    {
        var specification = File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "interfaces", "historical-analysis.md"));

        Assert.All(
        [
            "GET /historical-analysis",
            "POST /api/historical-analysis/v1/preview",
            "POST /api/historical-analysis/v1/instruction-runs",
            "GET /api/historical-analysis/v1/instruction-runs/{analysisRunId}",
            "POST /api/historical-analysis/v1/efficiency-runs",
            "GET /api/historical-analysis/v1/efficiency-runs/{analysisRunId}",
            "POST /api/historical-analysis/v1/evidence/resolve",
            "historical-instruction-analysis.read.v1",
            "supported`, `weak`, and `incomplete",
            "zero_findings",
            "provider_unavailable",
            "stale_extraction",
            "Cache-Control: no-store",
            "strict unknown-field rejection",
            "no CORS",
            "inert text",
            "no browser storage",
            "no combined analyze-all action",
            "no heuristic lookup",
            "future-surface-registry.json",
            "Issue #91",
        ], required => Assert.Contains(required, specification, StringComparison.Ordinal));
    }

    [Fact]
    public void CanonicalInterfaceIsIndexedAndPromotedToRequirementsSpecAndSecurityBoundary()
    {
        var index = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "specifications", "README.md"));
        var requirements = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "requirements.md"));
        var specification = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "spec.md"));
        var security = File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "security-data-boundaries.md"));

        Assert.Contains("interfaces/historical-analysis.md", index, StringComparison.Ordinal);
        Assert.Contains("Historical Analysis", requirements, StringComparison.Ordinal);
        Assert.Contains("/historical-analysis", specification, StringComparison.Ordinal);
        Assert.Contains("/api/historical-analysis/v1", security, StringComparison.Ordinal);
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

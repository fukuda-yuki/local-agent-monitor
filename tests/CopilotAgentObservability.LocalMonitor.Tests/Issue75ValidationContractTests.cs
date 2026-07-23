using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class Issue75ValidationContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ContractRoot = Path.Combine(
        RepositoryRoot, "docs", "specifications", "contracts", "historical-analysis", "v1");

    [Fact]
    public void ImplementedHistoricalAnalysisActivatesIssue91RowsAndLeavesFutureRegistry()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "contracts", "validation-matrix", "v1",
            "future-surface-registry.json")));
        Assert.DoesNotContain(
            registry.RootElement.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("surface_id").GetString() == "historical-analysis");

        using var handoff = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            ContractRoot, "issue-91-validation-handoff.json")));
        var root = handoff.RootElement;
        AssertObjectProperties(
            root,
            "schema_version", "surface_id", "owner_issue", "future_registry_state_at_kickoff",
            "production_surface_state", "active_row_ids", "required_profiles",
            "automated_test_filters", "expected_evidence_location", "canonical_transition");
        Assert.Equal("historical-analysis-validation-handoff.v1",
            root.GetProperty("schema_version").GetString());
        Assert.Equal("historical-analysis", root.GetProperty("surface_id").GetString());
        Assert.Equal(75, root.GetProperty("owner_issue").GetInt32());
        Assert.Equal("not_available",
            root.GetProperty("future_registry_state_at_kickoff").GetString());
        Assert.Equal("implemented_candidate",
            root.GetProperty("production_surface_state").GetString());
        Assert.Equal(
            ["91-H-075", "91-S-075", "91-L-075"],
            root.GetProperty("active_row_ids").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["raw-default", "sanitized-only", "content-available", "content-unavailable", "expired-evidence"],
            root.GetProperty("required_profiles").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            "docs/sprints/issue-75-historical-analysis/validation-matrix.json",
            root.GetProperty("expected_evidence_location").GetString());

        Assert.Equal(
            new[]
            {
                "FullyQualifiedName~HistoricalAnalysisSpecificationTests",
                "FullyQualifiedName~HistoricalAnalysisCoordinatorTests",
                "FullyQualifiedName~HistoricalAnalysisRouteTests",
                "FullyQualifiedName~HistoricalAnalysisPageTests",
                "FullyQualifiedName~HistoricalAnalysisPlaywrightTests",
                "FullyQualifiedName~HistoricalEvidenceExtractionTests",
                "FullyQualifiedName~HistoricalEvidenceProductionTests",
                "FullyQualifiedName~HistoricalEvidenceDatasetStoreTests",
                "FullyQualifiedName~HistoricalInstructionAnalysisTests",
                "FullyQualifiedName~HistoricalInstructionAnalysisPersistenceTests",
                "FullyQualifiedName~HistoricalEfficiencyAnalysisTests",
                "FullyQualifiedName~HistoricalEfficiencyAnalysisRegistryTests",
                "FullyQualifiedName~InstructionFindingHandoffConsumerV1Tests",
            },
            root.GetProperty("automated_test_filters").EnumerateArray()
                .Select(item => item.GetString()));
        Assert.Contains("placeholder was removed",
            root.GetProperty("canonical_transition").GetString(), StringComparison.Ordinal);
        Assert.Contains("no pass was inherited",
            root.GetProperty("canonical_transition").GetString(), StringComparison.Ordinal);
    }

    private static void AssertObjectProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

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

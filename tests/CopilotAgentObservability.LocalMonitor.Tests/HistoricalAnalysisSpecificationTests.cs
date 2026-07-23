using System.Text.Json;

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

    [Fact]
    public void VersionedHttpDtosPinClosedShapesAndPreviewToRunBindings()
    {
        var specification = ReadInterface();
        using var previewRequest = ReadJsonContract(specification, "### Preview request");
        using var previewResponse = ReadJsonContract(specification, "### Preview response");
        using var instructionStart = ReadJsonContract(specification, "### Instruction start request");
        using var efficiencyStart = ReadJsonContract(specification, "### Efficiency start request");

        AssertObjectProperties(previewRequest.RootElement, "schema_version", "selection");
        Assert.Equal("historical-analysis-preview.request.v1", previewRequest.RootElement.GetProperty("schema_version").GetString());
        AssertObjectProperties(previewRequest.RootElement.GetProperty("selection"),
            "repository", "workspace", "from", "to", "explicit_session_ids", "source_surfaces",
            "task_label", "experiment_label", "maximum_session_count", "sanitized_only");

        AssertObjectProperties(previewResponse.RootElement,
            "schema_version", "extraction_id", "raw_local_sha256", "repository_safe_sha256",
            "selection", "included", "excluded", "truncated_before", "truncated_session_count");
        Assert.Equal("historical-analysis-preview.response.v1", previewResponse.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("historical-extraction-[a-z0-9]{32}", previewResponse.RootElement.GetProperty("extraction_id").GetString());
        Assert.Equal("[a-f0-9]{64}", previewResponse.RootElement.GetProperty("raw_local_sha256").GetString());
        Assert.Equal("[a-f0-9]{64}", previewResponse.RootElement.GetProperty("repository_safe_sha256").GetString());

        AssertObjectProperties(instructionStart.RootElement,
            "schema_version", "extraction_id", "raw_local_sha256", "model", "provider",
            "configuration_sha256", "timeout_ms", "prompt_template_version");
        Assert.Equal("historical-analysis-instruction-start.request.v1", instructionStart.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("historical-instruction-analysis.prompt.v1", instructionStart.RootElement.GetProperty("prompt_template_version").GetString());

        AssertObjectProperties(efficiencyStart.RootElement,
            "schema_version", "extraction_id", "repository_safe_sha256");
        Assert.Equal("historical-analysis-efficiency-start.request.v1", efficiencyStart.RootElement.GetProperty("schema_version").GetString());
    }

    [Fact]
    public void EfficiencyStatusLifecycleAndReceiptInvariantsAreClosed()
    {
        var specification = ReadInterface();
        using var status = ReadJsonContract(specification, "### Efficiency status response");
        using var lifecycle = ReadJsonContract(specification, "### Efficiency lifecycle invariants");

        AssertObjectProperties(status.RootElement,
            "schema_version", "analysis_run_id", "extraction_id", "repository_safe_sha256", "state",
            "requested_at", "started_at", "completed_at", "receipt", "receipt_payload_sha256");
        Assert.Equal("historical-analysis-efficiency-status.v1", status.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("historical-efficiency-run-[a-z0-9]{32}", status.RootElement.GetProperty("analysis_run_id").GetString());
        Assert.Equal("queued", status.RootElement.GetProperty("state").GetString());
        Assert.Equal(
            ["queued", "running", "succeeded", "zero_drivers", "stale_extraction", "analysis_failed", "timed_out", "canceled"],
            lifecycle.RootElement.GetProperty("allowed_states").EnumerateArray().Select(value => value.GetString()));

        Assert.Contains("receipt is `null` for `queued`, `running`, `stale_extraction`, `analysis_failed`, `timed_out`, and `canceled`", specification, StringComparison.Ordinal);
        Assert.Contains("receipt is present only for `succeeded` and `zero_drivers`", specification, StringComparison.Ordinal);
        Assert.Contains("historical-efficiency-receipt.v1", specification, StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceResolutionCardinalityAndHttpErrorMappingAreClosed()
    {
        var specification = ReadInterface();
        using var resolveRequest = ReadJsonContract(specification, "### Evidence resolution request");
        using var errors = ReadJsonContract(specification, "### HTTP error mapping");

        AssertObjectProperties(resolveRequest.RootElement,
            "schema_version", "extraction_id", "repository_safe_sha256", "references");
        Assert.Equal("historical-analysis-evidence-resolve.request.v1", resolveRequest.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("1..16 distinct tokens in caller order", resolveRequest.RootElement.GetProperty("references").GetString());
        Assert.Contains("A singular `reference` field is rejected", specification, StringComparison.Ordinal);
        Assert.Contains("response array has the same cardinality and order", specification, StringComparison.Ordinal);

        var mapping = errors.RootElement.GetProperty("errors").EnumerateArray()
            .Select(item => (item.GetProperty("http_status").GetInt32(), item.GetProperty("error").GetString()))
            .ToArray();
        Assert.Equal(
        [
            (400, "invalid_historical_analysis_request"),
            (403, "cross_origin_forbidden"),
            (403, "csrf_required"),
            (404, "historical_analysis_run_not_found"),
            (404, "historical_extraction_not_found"),
            (409, "stale_extraction"),
            (409, "precondition_failed"),
            (409, "evidence_expired"),
            (413, "request_too_large"),
            (415, "unsupported_media_type"),
            (503, "provider_unavailable"),
            (503, "historical_analysis_store_unavailable"),
        ], mapping);
        Assert.Contains("provider_unavailable creates no run", specification, StringComparison.Ordinal);
    }

    private static string ReadInterface() => File.ReadAllText(Path.Combine(
        RepositoryRoot, "docs", "specifications", "interfaces", "historical-analysis.md"));

    private static JsonDocument ReadJsonContract(string specification, string heading)
    {
        var headingIndex = specification.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(headingIndex >= 0, $"Missing contract heading: {heading}");
        var blockStart = specification.IndexOf("```json", headingIndex, StringComparison.Ordinal);
        Assert.True(blockStart >= 0, $"Missing JSON block after: {heading}");
        var jsonStart = specification.IndexOf('\n', blockStart) + 1;
        var jsonEnd = specification.IndexOf("\n```", jsonStart, StringComparison.Ordinal);
        Assert.True(jsonEnd >= 0, $"Unclosed JSON block after: {heading}");
        return JsonDocument.Parse(specification[jsonStart..jsonEnd]);
    }

    private static void AssertObjectProperties(JsonElement value, params string[] expected) =>
        Assert.Equal(expected, value.EnumerateObject().Select(property => property.Name));

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

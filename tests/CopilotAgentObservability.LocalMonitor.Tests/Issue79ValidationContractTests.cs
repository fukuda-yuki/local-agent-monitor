using System.Diagnostics;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class Issue79ValidationContractTests
{
    private const string KickoffSha = "c02c10ab18553acef1619ce12ec630f4f6f5aa5f";
    private const string CandidateSha = "071b00e319de86cfa842371ee745025f3f2cfe96";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string MatrixPath = Path.Combine(
        RepositoryRoot, "docs", "sprints", "issue-79-historical-import", "validation-matrix.json");
    private static readonly string EvidencePath = Path.Combine(
        RepositoryRoot, "docs", "sprints", "issue-79-historical-import", "final-evidence.md");
    private static readonly string HandoffPath = Path.Combine(
        RepositoryRoot, "docs", "specifications", "contracts", "historical-import-workflow", "v1",
        "issue-91-validation-handoff.json");

    [Fact]
    public void HistoricalImportFunctionalCandidateActivatesIssue91RowsAndLeavesFutureRegistry()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "contracts", "validation-matrix", "v1",
            "future-surface-registry.json")));
        var entries = registry.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.DoesNotContain(entries,
            entry => entry.GetProperty("surface_id").GetString() == "historical-import");

        using var handoff = JsonDocument.Parse(File.ReadAllText(HandoffPath));
        AssertObjectProperties(
            handoff.RootElement,
            "schema_version", "owner_issue", "surface_id", "activation", "required_profiles",
            "active_rows", "matrix_ref", "evidence_ref", "inherits_issue_91_pass",
            "canonical_transition");
        Assert.Equal("historical-import-validation-handoff.v1",
            handoff.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal(79, handoff.RootElement.GetProperty("owner_issue").GetInt32());
        Assert.Equal("historical-import", handoff.RootElement.GetProperty("surface_id").GetString());
        Assert.Equal("implemented_candidate", handoff.RootElement.GetProperty("activation").GetString());
        Assert.False(handoff.RootElement.GetProperty("inherits_issue_91_pass").GetBoolean());
        Assert.Equal(
            ["raw-default", "sanitized-only", "supported", "new-version", "unsupported", "schema-drift"],
            handoff.RootElement.GetProperty("required_profiles").EnumerateArray().Select(value => value.GetString()));
        Assert.Contains("placeholder was removed", handoff.RootElement.GetProperty("canonical_transition").GetString(),
            StringComparison.Ordinal);
        Assert.Equal("docs/sprints/issue-79-historical-import/validation-matrix.json",
            handoff.RootElement.GetProperty("matrix_ref").GetString());
        Assert.Equal("docs/sprints/issue-79-historical-import/final-evidence.md",
            handoff.RootElement.GetProperty("evidence_ref").GetString());

        var rows = handoff.RootElement.GetProperty("active_rows").EnumerateArray().ToArray();
        Assert.Equal(["91-I-079", "91-S-079", "91-L-079"],
            rows.Select(row => row.GetProperty("row_id").GetString()));
        Assert.All(rows, row =>
        {
            AssertObjectProperties(row, "row_id", "scope", "matrix_location", "evidence_location");
            var rowId = row.GetProperty("row_id").GetString();
            Assert.Equal($"docs/sprints/issue-79-historical-import/validation-matrix.json#{rowId}",
                row.GetProperty("matrix_location").GetString());
            Assert.Equal($"docs/sprints/issue-79-historical-import/final-evidence.md#{rowId}",
                row.GetProperty("evidence_location").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("scope").GetString()));
        });

        var automatedScope = rows[0].GetProperty("scope").GetString()!;
        Assert.All(
            new[]
            {
                "HistoricalImportWorkflowContractTests",
                "HistoricalImportStoreTests",
                "HistoricalImportApplicationTests",
                "HistoricalImportGatewayTests",
                "HistoricalImportCliTests",
                "HistoricalImportRouteTests",
                "HistoricalImportUiPlaywrightTests",
            },
            testClass => Assert.Contains(testClass, automatedScope, StringComparison.Ordinal));
    }

    [Fact]
    public void FinalMatrixPinsRowsAndClassificationsWithoutInheritingPasses()
    {
        using var matrix = JsonDocument.Parse(File.ReadAllText(MatrixPath));
        var root = matrix.RootElement;
        Assert.Equal("validation-matrix.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal(KickoffSha, root.GetProperty("matrix_prep_sha").GetString());
        Assert.Equal(CandidateSha, root.GetProperty("final_validation_sha").GetString());
        Assert.Equal("docs/specifications/contracts/validation-matrix/v1/future-surface-registry.json",
            root.GetProperty("future_registry_ref").GetString());

        var rows = root.GetProperty("active_rows").EnumerateArray().ToArray();
        Assert.Equal(["91-I-079", "91-S-079", "91-L-079"],
            rows.Select(row => row.GetProperty("row_id").GetString()));
        Assert.Equal(["passed", "passed", "blocked_external"],
            rows.Select(row => row.GetProperty("classification").GetString()));
        Assert.All(rows, row => Assert.Equal(CandidateSha, row.GetProperty("validation_sha").GetString()));

        var decision = root.GetProperty("release_decision");
        Assert.Equal("release_ready_with_external_blockers", decision.GetProperty("decision").GetString());
        var blocker = Assert.Single(decision.GetProperty("external_blockers").EnumerateArray());
        Assert.Equal("91-L-079", blocker.GetProperty("row_id").GetString());

        var liveRow = Assert.Single(rows, row => row.GetProperty("row_id").GetString() == "91-L-079");
        Assert.Equal("blocked_external", liveRow.GetProperty("classification").GetString());
        Assert.Contains(liveRow.GetProperty("evidence").EnumerateArray(),
            evidence => evidence.GetProperty("kind").GetString() == "live");
    }

    [Fact]
    public async Task FinalMatrixPassesIssue91SemanticValidator()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "pwsh",
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(
            RepositoryRoot, "scripts", "validation", "issue-91", "validate-matrix.ps1"));
        startInfo.ArgumentList.Add("-MatrixPath");
        startInfo.ArgumentList.Add(MatrixPath);

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        Assert.True(process.ExitCode == 0, $"Issue #79 matrix validation failed: {stdout}{stderr}");
        Assert.Contains("matrix_validation=PASS rows=3 decision=release_ready_with_external_blockers", stdout,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EvidenceLedgerIsAnExplicitRepositorySafeFinalRecord()
    {
        var evidence = File.ReadAllText(EvidencePath);

        Assert.Contains("FINAL", evidence, StringComparison.Ordinal);
        Assert.Contains(CandidateSha, evidence, StringComparison.Ordinal);
        Assert.Contains("91-I-079", evidence, StringComparison.Ordinal);
        Assert.Contains("91-S-079", evidence, StringComparison.Ordinal);
        Assert.Contains("91-L-079", evidence, StringComparison.Ordinal);
        Assert.Contains("7809/7809", evidence, StringComparison.Ordinal);
        Assert.Contains("No prior Issue #91 pass was inherited", evidence, StringComparison.Ordinal);
        Assert.Contains("release_ready_with_external_blockers", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("PENDING", evidence, StringComparison.Ordinal);
    }

    private static void AssertObjectProperties(JsonElement element, params string[] expected)
    {
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
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

using System.Diagnostics;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class Issue91ValidationContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string ContractRoot = Path.Combine(
        RepositoryRoot, "docs", "specifications", "contracts", "validation-matrix", "v1");
    private static readonly string ValidationRoot = Path.Combine(
        RepositoryRoot, "scripts", "validation", "issue-91");

    [Fact]
    public void VersionedContractDeclaresClosedClassificationAndDecisionSets()
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(ContractRoot, "validation-matrix.schema.json")));
        var definitions = schema.RootElement.GetProperty("$defs");

        Assert.Equal(
            ["passed", "failed", "blocked_external", "not_applicable", "not_attempted"],
            definitions.GetProperty("classification").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(
            ["release_ready", "release_ready_with_external_blockers", "release_blocked"],
            definitions.GetProperty("release_decision").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
        Assert.DoesNotContain(
            "not_available",
            definitions.GetProperty("classification").GetProperty("enum")
                .EnumerateArray().Select(value => value.GetString()));
    }

    [Fact]
    public void FutureRegistryContainsOnlyIssue91FutureOwners()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(ContractRoot, "future-surface-registry.json")));
        Assert.True(File.Exists(Path.Combine(ContractRoot, "future-surface-registry.schema.json")));
        Assert.Equal("future-surface-registry.schema.json", registry.RootElement.GetProperty("$schema").GetString());
        Assert.Equal("validation-future-surfaces.v1", registry.RootElement.GetProperty("schema_version").GetString());

        var entries = registry.RootElement.GetProperty("entries").EnumerateArray().ToArray();
        Assert.Equal([75, 88, 93],
            entries.Select(entry => entry.GetProperty("owner_issue").GetInt32()));
        Assert.All(entries, entry =>
        {
            Assert.Equal("not_available", entry.GetProperty("state").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("entry_condition").GetString()));
            Assert.NotEmpty(entry.GetProperty("required_profiles").EnumerateArray());
            Assert.StartsWith("docs/sprints/", entry.GetProperty("expected_evidence_location").GetString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SanitizedImportMatrixPinsFunctionalCandidateAndExternalPortabilityGap()
    {
        using var matrix = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "sprints", "issue-86-sanitized-import", "validation-matrix.json")));

        const string functionalSha = "6156699a47733d59b4a83301dd02c0291dcfaec4";
        var root = matrix.RootElement;
        Assert.Equal("validation-matrix.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("c02c10ab18553acef1619ce12ec630f4f6f5aa5f", root.GetProperty("matrix_prep_sha").GetString());
        Assert.Equal(functionalSha, root.GetProperty("final_validation_sha").GetString());
        Assert.Equal(
            "docs/specifications/contracts/validation-matrix/v1/future-surface-registry.json",
            root.GetProperty("future_registry_ref").GetString());

        var rows = root.GetProperty("active_rows").EnumerateArray().ToArray();
        Assert.Equal(["91-I-086", "91-S-086", "91-L-086"],
            rows.Select(row => row.GetProperty("row_id").GetString()));
        Assert.Equal(["passed", "passed", "blocked_external"],
            rows.Select(row => row.GetProperty("classification").GetString()));
        Assert.All(rows, row => Assert.Equal(functionalSha, row.GetProperty("validation_sha").GetString()));

        var liveRow = rows[2];
        var decision = root.GetProperty("release_decision");
        Assert.Equal("release_ready_with_external_blockers", decision.GetProperty("decision").GetString());
        var blocker = Assert.Single(decision.GetProperty("external_blockers").EnumerateArray());
        Assert.Equal(liveRow.GetProperty("row_id").GetString(), blocker.GetProperty("row_id").GetString());
        Assert.Equal(liveRow.GetProperty("severity").GetString(), blocker.GetProperty("severity").GetString());
        Assert.Equal(liveRow.GetProperty("blocker").GetString(), blocker.GetProperty("blocker").GetString());
        Assert.Equal(liveRow.GetProperty("retry_condition").GetString(), blocker.GetProperty("retry_condition").GetString());
        Assert.Equal(liveRow.GetProperty("unverified_capability").GetString(), blocker.GetProperty("unverified_capability").GetString());
        Assert.Contains(liveRow.GetProperty("evidence").EnumerateArray(),
            evidence => evidence.GetProperty("kind").GetString() == "live");
    }

    [Fact]
    public void PreparationInventoryPinsShaWithoutFinalClassifications()
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "sprints", "issue-91-validation-matrix", "preparation-inventory.json")));
        Assert.Equal("5180a0424ff5488354a3e173c74b7e931d28679d",
            inventory.RootElement.GetProperty("matrix_prep_sha").GetString());
        Assert.Equal(JsonValueKind.Null, inventory.RootElement.GetProperty("final_validation_sha").ValueKind);
        Assert.Equal("candidate_ready_to_freeze", inventory.RootElement.GetProperty("state").GetString());

        var serialized = inventory.RootElement.GetRawText();
        Assert.DoesNotContain("\"classification\"", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("release_ready", serialized, StringComparison.Ordinal);
        Assert.All(inventory.RootElement.GetProperty("active_surfaces").EnumerateArray(), row =>
        {
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("owner").GetString()));
            Assert.Contains(row.GetProperty("requirement_level").GetString(), new[] { "required", "optional" });
            Assert.Contains(row.GetProperty("applicability").GetString(), new[] { "applicable", "not_applicable" });
        });
    }

    [Fact]
    public void SecretCorpusCoversTaxonomyAndBoundedTransformations()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            ValidationRoot, "fixtures", "secret-corpus.v1.json")));
        Assert.Equal("validation-secret-corpus.v1", corpus.RootElement.GetProperty("schema_version").GetString());

        var cases = corpus.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(cases.Length, cases.Select(item => item.GetProperty("case_id").GetString()).Distinct(StringComparer.Ordinal).Count());
        var taxonomies = cases.Select(item => item.GetProperty("taxonomy").GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Subset(
            new HashSet<string>(StringComparer.Ordinal)
            {
                "api_key_token", "authorization_header", "basic_authorization_header", "connection_string", "private_key_certificate", "certificate_block",
                "cloud_credential", "github_token", "environment_secret", "email_identity", "phone_identity",
                "government_identifier", "postal_address",
                "absolute_sensitive_path", "source_file_body", "prompt_response_tool_body", "derived_label",
                "nested_json_content", "nested_markdown_content", "nested_html_content",
            },
            taxonomies);

        var transformations = corpus.RootElement.GetProperty("supported_transformations")
            .EnumerateArray().Select(item => item.GetString()!).ToArray();
        Assert.Equal(["plain", "json_escape", "html_entity", "url_percent", "base64_utf8", "sha256_prefix_12"], transformations);
        Assert.NotEmpty(corpus.RootElement.GetProperty("negative_cases").EnumerateArray());
    }

    [Fact]
    public async Task ScannerSelfTestPasses()
    {
        var (exitCode, stdout, stderr) = await RunPowerShellAsync("test-scan-outputs.ps1");
        Assert.True(exitCode == 0, $"Scanner self-test failed: {stdout}{stderr}");
        Assert.Contains("self_test_result=PASS", stdout, StringComparison.Ordinal);
        Assert.Contains("transformation_cases=", stdout, StringComparison.Ordinal);
        Assert.Contains("negative_cases=5", stdout, StringComparison.Ordinal);
        Assert.Contains("case=zero-bytes exit=2 scanner_result=ERROR", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SemanticValidatorSelfTestPasses()
    {
        var (exitCode, stdout, stderr) = await RunPowerShellAsync("test-validation-contract.ps1");
        Assert.True(exitCode == 0, $"Matrix semantic validator self-test failed: {stdout}{stderr}");
        Assert.Contains("contract_self_test=PASS cases=10", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AutomatedMatrixManifestCoversEveryApplicableInventoryFilter()
    {
        using var inventory = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "sprints", "issue-91-validation-matrix", "preparation-inventory.json")));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(ValidationRoot, "automated-matrix.v1.json")));

        var rows = manifest.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(rows.Length, rows.Select(row => row.GetProperty("row_id").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(rows.Length, rows.Select(row => row.GetProperty("surface_id").GetString()).Distinct(StringComparer.Ordinal).Count());

        foreach (var surface in inventory.RootElement.GetProperty("active_surfaces").EnumerateArray())
        {
            var surfaceId = surface.GetProperty("surface_id").GetString();
            if (surface.GetProperty("applicability").GetString() == "not_applicable")
            {
                Assert.DoesNotContain(rows, row => row.GetProperty("surface_id").GetString() == surfaceId);
                continue;
            }

            var row = Assert.Single(rows, row => row.GetProperty("surface_id").GetString() == surfaceId);
            Assert.Contains(row.GetProperty("matrix_task").GetString(), new[] { "91-C", "91-D" });
            Assert.Equal("all_operations_x_applicable_profiles", row.GetProperty("coverage_mode").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("expected_invariant").GetString()));
            Assert.Equal(
                surface.GetProperty("operations").EnumerateArray().Select(value => value.GetString()),
                row.GetProperty("operations").EnumerateArray().Select(value => value.GetString()));
            Assert.Equal(
                surface.GetProperty("profiles").EnumerateArray().Select(value => value.GetString()),
                row.GetProperty("applicable_profiles").EnumerateArray().Select(value => value.GetString()));
            var expected = surface.GetProperty("automated_test_filters").EnumerateArray().Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
            var actual = row.GetProperty("test_groups").EnumerateArray()
                .SelectMany(group => group.GetProperty("filters").EnumerateArray())
                .Select(value => value.GetString()).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(expected, actual);
        }

        Assert.All(rows.Where(row => row.GetProperty("surface_id").GetString() is "source-compatibility" or "exact-binding"),
            row => Assert.Equal("91-D", row.GetProperty("matrix_task").GetString()));

        var runner = File.ReadAllText(Path.Combine(ValidationRoot, "run-automated-matrix.ps1"));
        Assert.Contains("--list-tests", runner, StringComparison.Ordinal);
        Assert.Contains("filter_discovered_zero", runner, StringComparison.Ordinal);
        Assert.Contains("incomplete_or_skipped_tests", runner, StringComparison.Ordinal);
        Assert.Contains("notExecuted", runner, StringComparison.Ordinal);
        Assert.Contains("$LASTEXITCODE", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("Start-Sleep", runner, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunPowerShellAsync(string scriptName)
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
        startInfo.ArgumentList.Add(Path.Combine(ValidationRoot, scriptName));

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        return (process.ExitCode, await stdoutTask, await stderrTask);
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

internal static class Issue91SecretCorpus
{
    private static readonly Lazy<string[]> LoadedMarkers = new(() =>
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "scripts", "validation", "issue-91", "fixtures", "secret-corpus.v1.json")));
        return corpus.RootElement.GetProperty("cases").EnumerateArray()
            .Select(item => item.GetProperty("marker").GetString()!)
            .ToArray();
    });

    internal static IReadOnlyList<string> Markers => LoadedMarkers.Value;

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

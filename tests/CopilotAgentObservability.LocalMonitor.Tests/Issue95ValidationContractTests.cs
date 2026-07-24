using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class Issue95ValidationContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private const string HandoffRelativePath = "docs/specifications/contracts/cost-analytics/v1/issue-91-validation-handoff.json";
    private const string RowContractRelativePath = "docs/specifications/contracts/cost-analytics/v1/issue-91-validation-row-contract.json";
    private const string ReadmeRelativePath = "docs/sprints/issue-95-cost-analytics/README.md";
    private const string MatrixRelativePath = "docs/sprints/issue-95-cost-analytics/validation-matrix.json";
    private const string ChecksumRelativePath = "docs/sprints/issue-95-cost-analytics/artifact-checksums.json";
    private const string AttestationRelativePath = "docs/sprints/issue-95-cost-analytics/evidence-attestation.json";

    [Fact]
    public void CandidateActivationIsExplicitWithoutClaimingFinalEvidence()
    {
        using var registry = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            RepositoryRoot, "docs", "specifications", "contracts", "validation-matrix", "v1",
            "future-surface-registry.json")));
        Assert.DoesNotContain(registry.RootElement.GetProperty("entries").EnumerateArray(),
            entry => entry.GetProperty("surface_id").GetString() == "cost-analytics");

        using var handoff = JsonDocument.Parse(File.ReadAllText(RepositoryPath(HandoffRelativePath)));
        var root = handoff.RootElement;
        AssertObjectProperties(root,
            "schema_version", "surface_id", "owner_issue", "future_registry_state_at_kickoff",
            "production_surface_state", "active_row_ids", "row_contract_path", "active_rows", "required_profiles",
            "automated_test_filters", "expected_evidence_location", "canonical_transition", "evidence_binding");
        Assert.Equal("cost-analytics-validation-handoff.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("cost-analytics", root.GetProperty("surface_id").GetString());
        Assert.Equal(95, root.GetProperty("owner_issue").GetInt32());
        Assert.Equal("not_registered", root.GetProperty("future_registry_state_at_kickoff").GetString());
        Assert.Equal("implemented_candidate", root.GetProperty("production_surface_state").GetString());
        Assert.Equal(["91-A-095", "91-S-095", "91-L-095"],
            root.GetProperty("active_row_ids").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(RowContractRelativePath, root.GetProperty("row_contract_path").GetString());
        using var rowContract = JsonDocument.Parse(File.ReadAllText(RepositoryPath(RowContractRelativePath)));
        Assert.Equal("cost-analytics-validation-row-contract.v1",
            rowContract.RootElement.GetProperty("schema_version").GetString());
        Assert.Equal("cost-analytics", rowContract.RootElement.GetProperty("surface_id").GetString());
        Assert.True(JsonElement.DeepEquals(
            rowContract.RootElement.GetProperty("active_rows"),
            root.GetProperty("active_rows")));
        var profileLedger = root.GetProperty("required_profiles");
        AssertObjectProperties(profileLedger,
            "collection", "content_access", "compatibility", "hook", "otel", "binding", "restart", "retention");
        Assert.Equal(
            ["sqlite-production-store", "loopback-api", "playwright-ui", "repository-artifacts", "genuine-github-copilot", "genuine-claude-code"],
            profileLedger.GetProperty("collection").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["raw-default", "sanitized-only", "separately-authorized-live-capture"],
            profileLedger.GetProperty("content_access").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["trusted-bundled-catalog", "source-mapping-unavailable", "private-local-override", "malformed", "tampered", "future-version", "reviewed-positive-source-mapping"],
            profileLedger.GetProperty("compatibility").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["unverified"], profileLedger.GetProperty("hook").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["unverified"], profileLedger.GetProperty("otel").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["exact-session-estimate", "exact-identity", "canonical-bytes"],
            profileLedger.GetProperty("binding").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["persisted-reload"], profileLedger.GetProperty("restart").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(["archive-safety"], profileLedger.GetProperty("retention").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            [
                "FullyQualifiedName~PricingPersistenceFoundationTests",
                "FullyQualifiedName~PricingCatalogProviderTests",
                "FullyQualifiedName~PricingQueryFoundationTests",
                "FullyQualifiedName~CostConfigurationApplicationServiceTests",
                "FullyQualifiedName~CostRecalculationCoordinatorTests",
                "FullyQualifiedName~CostAnalyticsReadModelTests",
                "FullyQualifiedName~CostRouteTests",
                "FullyQualifiedName~CostPageTests",
                "FullyQualifiedName~CostPagePlaywrightTests",
                "FullyQualifiedName~AlertEngineV2Tests",
                "FullyQualifiedName~CostAlertPresentationResolverTests",
                "FullyQualifiedName~GoldenAlertReceiptTests",
                "FullyQualifiedName~AlertEvaluationApplicationTests",
                "FullyQualifiedName~SqliteAlertEngineStoreV2Tests",
                "FullyQualifiedName~SqliteAlertEngineQueryStoreTests",
                "FullyQualifiedName~AlertCenter",
                "FullyQualifiedName~AlertLifecycle",
                "FullyQualifiedName~SanitizedExport",
                "FullyQualifiedName~RuntimeBackup",
                "FullyQualifiedName~LocalMonitorScriptTests",
            ],
            root.GetProperty("automated_test_filters").EnumerateArray().Select(item => item.GetString()));
        Assert.All(root.GetProperty("active_rows").EnumerateArray(), row =>
        {
            AssertObjectProperties(row,
                "row_id", "surface", "operation", "required_profiles", "versions",
                "evidence_references", "automated_test_filters", "blocked_external_contract");
            Assert.Equal(
                ["binding", "collection", "compatibility", "content_access", "hook", "otel", "restart", "retention"],
                row.GetProperty("required_profiles").EnumerateObject().Select(property => property.Name).Order());
            Assert.NotEmpty(row.GetProperty("versions").EnumerateObject());
            Assert.NotEmpty(row.GetProperty("evidence_references").EnumerateArray());
            Assert.NotEmpty(row.GetProperty("automated_test_filters").EnumerateArray());
        });
        var live = root.GetProperty("active_rows").EnumerateArray().Single(row =>
            row.GetProperty("row_id").GetString() == "91-L-095");
        var liveBlock = live.GetProperty("blocked_external_contract");
        Assert.Equal(["github-copilot", "claude-code"],
            liveBlock.GetProperty("required_providers").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["positive-estimate-persistence", "configured-budget-evaluation", "alert-center-readback"],
            liveBlock.GetProperty("unverified_capabilities").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains("reviewed positive source mappings", liveBlock.GetProperty("retry_condition").GetString(), StringComparison.Ordinal);
        Assert.Contains("separate live authorization", liveBlock.GetProperty("retry_condition").GetString(), StringComparison.Ordinal);
        Assert.Equal(MatrixRelativePath, root.GetProperty("expected_evidence_location").GetString());
        Assert.Contains("never a future-registry entry", root.GetProperty("canonical_transition").GetString(), StringComparison.Ordinal);
        Assert.Contains("no pass was inherited", root.GetProperty("canonical_transition").GetString(), StringComparison.Ordinal);

        var binding = root.GetProperty("evidence_binding");
        AssertObjectProperties(binding,
            "state", "matrix_path", "checksum_manifest_path", "attestation_path",
            "matrix_prep_sha", "final_validation_sha");
        Assert.Equal(MatrixRelativePath, binding.GetProperty("matrix_path").GetString());
        Assert.Equal(ChecksumRelativePath, binding.GetProperty("checksum_manifest_path").GetString());
        Assert.Equal(AttestationRelativePath, binding.GetProperty("attestation_path").GetString());
        var evidencePaths = new[]
        {
            ReadmeRelativePath, MatrixRelativePath, "docs/sprints/issue-95-cost-analytics/live-validation.md",
            ChecksumRelativePath, AttestationRelativePath,
        };
        var materialized = evidencePaths.Where(path => File.Exists(RepositoryPath(path))).ToArray();
        if (materialized.Length == 0)
        {
            Assert.Equal("pending_candidate_execution", binding.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, binding.GetProperty("matrix_prep_sha").ValueKind);
            Assert.Equal(JsonValueKind.Null, binding.GetProperty("final_validation_sha").ValueKind);
            return;
        }

        Assert.Equal(evidencePaths, materialized);
        Assert.Equal("finalized", binding.GetProperty("state").GetString());
        Assert.Matches("^[0-9a-f]{40}$", binding.GetProperty("matrix_prep_sha").GetString());
        Assert.Matches("^[0-9a-f]{40}$", binding.GetProperty("final_validation_sha").GetString());
    }

    [Fact]
    public void HandoffDirectlyBindsTheFrozenCostAnalyticsValidationRequirements()
    {
        var specification = File.ReadAllText(RepositoryPath("docs/specifications/interfaces/cost-analytics.md"));
        Assert.Contains("Issue #95 owns direct active rows", specification, StringComparison.Ordinal);
        Assert.Contains("`91-A-095`", specification, StringComparison.Ordinal);
        Assert.Contains("`91-S-095`", specification, StringComparison.Ordinal);
        Assert.Contains("`91-L-095`", specification, StringComparison.Ordinal);
        Assert.Contains("exact v1 alert golden compatibility", specification, StringComparison.Ordinal);
        Assert.Contains("canonical/store/read behavior", specification, StringComparison.Ordinal);
        Assert.Contains("Playwright/accessibility", specification, StringComparison.Ordinal);
        Assert.Contains("artifact checksums", specification, StringComparison.Ordinal);
    }

    [Fact]
    public void FutureEvidenceMustBindRowsHandoffAndRepositorySafeChecksums()
    {
        var evidencePaths = new[]
        {
            ReadmeRelativePath, MatrixRelativePath, "docs/sprints/issue-95-cost-analytics/live-validation.md",
            ChecksumRelativePath, AttestationRelativePath,
        };
        var materialized = evidencePaths.Where(path => File.Exists(RepositoryPath(path))).ToArray();
        if (materialized.Length == 0) return;
        Assert.Equal(evidencePaths, materialized);

        using var matrix = JsonDocument.Parse(File.ReadAllText(RepositoryPath(MatrixRelativePath)));
        var root = matrix.RootElement;
        var finalSha = root.GetProperty("final_validation_sha").GetString();
        var rows = root.GetProperty("active_rows").EnumerateArray().ToArray();
        Assert.Equal(["91-A-095", "91-S-095", "91-L-095"], rows.Select(row => row.GetProperty("row_id").GetString()));
        Assert.Equal(["passed", "passed", "blocked_external"], rows.Select(row => row.GetProperty("classification").GetString()));
        Assert.Equal("high", rows[2].GetProperty("severity").GetString());
        Assert.All(rows, row =>
        {
            Assert.Equal(finalSha, row.GetProperty("validation_sha").GetString());
            Assert.Equal(finalSha, row.GetProperty("versions").GetProperty("candidate").GetString());
        });

        using var handoff = JsonDocument.Parse(File.ReadAllText(RepositoryPath(HandoffRelativePath)));
        var binding = handoff.RootElement.GetProperty("evidence_binding");
        Assert.Equal("finalized", binding.GetProperty("state").GetString());
        Assert.Equal(root.GetProperty("matrix_prep_sha").GetString(), binding.GetProperty("matrix_prep_sha").GetString());
        Assert.Equal(finalSha, binding.GetProperty("final_validation_sha").GetString());

        using var checksums = JsonDocument.Parse(File.ReadAllText(RepositoryPath(ChecksumRelativePath)));
        var checksumRoot = checksums.RootElement;
        AssertObjectProperties(checksumRoot, "schema_version", "candidate_base", "algorithm", "verification_date", "artifacts");
        Assert.Equal("issue-95-artifact-checksums.v1", checksumRoot.GetProperty("schema_version").GetString());
        Assert.Equal(finalSha, checksumRoot.GetProperty("candidate_base").GetString());
        Assert.Equal("SHA-256", checksumRoot.GetProperty("algorithm").GetString());
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}$", checksumRoot.GetProperty("verification_date").GetString());
        var manifestArtifacts = checksumRoot.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Equal([HandoffRelativePath, ReadmeRelativePath, MatrixRelativePath, "docs/sprints/issue-95-cost-analytics/live-validation.md"],
            manifestArtifacts.Select(artifact => artifact.GetProperty("path").GetString()));
        foreach (var artifact in manifestArtifacts)
        {
            var relativePath = artifact.GetProperty("path").GetString()!;
            Assert.DoesNotContain("artifact-checksums.json", relativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("evidence-attestation.json", relativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("archive", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^[0-9a-f]{64}$", artifact.GetProperty("sha256").GetString());
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RepositoryPath(relativePath)))).ToLowerInvariant();
            Assert.Equal(artifact.GetProperty("sha256").GetString(), digest);
        }

        using var attestation = JsonDocument.Parse(File.ReadAllText(RepositoryPath(AttestationRelativePath)));
        var attestationRoot = attestation.RootElement;
        AssertObjectProperties(attestationRoot,
            "schema_version", "issue", "functional_candidate_sha", "evidence_materialization_sha",
            "evidence_materialization_parent_sha", "relationship", "checksum_algorithm",
            "artifacts_at_evidence_materialization", "verification", "publication");
        Assert.Equal("evidence-attestation.v1", attestationRoot.GetProperty("schema_version").GetString());
        Assert.Equal(95, attestationRoot.GetProperty("issue").GetInt32());
        Assert.Equal(finalSha, attestationRoot.GetProperty("functional_candidate_sha").GetString());
        Assert.Matches("^[0-9a-f]{40}$", attestationRoot.GetProperty("evidence_materialization_sha").GetString());
        Assert.Equal(finalSha, attestationRoot.GetProperty("evidence_materialization_parent_sha").GetString());
        Assert.Equal("SHA-256", attestationRoot.GetProperty("checksum_algorithm").GetString());
        Assert.False(string.IsNullOrWhiteSpace(attestationRoot.GetProperty("relationship").GetString()));
        Assert.NotEmpty(attestationRoot.GetProperty("verification").EnumerateObject());
        Assert.NotEmpty(attestationRoot.GetProperty("publication").EnumerateObject());
        var attestedArtifacts = attestationRoot.GetProperty("artifacts_at_evidence_materialization").EnumerateArray().ToArray();
        Assert.Equal([HandoffRelativePath, ReadmeRelativePath, MatrixRelativePath, "docs/sprints/issue-95-cost-analytics/live-validation.md", ChecksumRelativePath],
            attestedArtifacts.Select(artifact => artifact.GetProperty("path").GetString()));
    }

    [Fact]
    public void EvidenceChainVerifierIsReadOnlyAndFailsClosed()
    {
        var scriptPath = RepositoryPath("scripts/validation/issue-95/verify-evidence-chain.ps1");
        Assert.True(File.Exists(scriptPath));
        var script = File.ReadAllText(scriptPath);
        Assert.Contains("CandidateSha", script, StringComparison.Ordinal);
        Assert.Contains("EvidenceSha", script, StringComparison.Ordinal);
        Assert.Contains("AttestationSha", script, StringComparison.Ordinal);
        Assert.Contains("validate-matrix.ps1", script, StringComparison.Ordinal);
        Assert.Contains("git show", script, StringComparison.Ordinal);
        Assert.Contains("working_tree_substitution_detected", script, StringComparison.Ordinal);
        Assert.Contains("matrix_prep_not_exact_commit", script, StringComparison.Ordinal);
        Assert.Contains("not_candidate_ancestor", script, StringComparison.Ordinal);
        Assert.Contains("live_validation_contract_invalid", script, StringComparison.Ordinal);
        Assert.Contains("applicable_security_prerequisite_skips", script, StringComparison.Ordinal);
        Assert.Contains("not_applicable_os_security_tests", script, StringComparison.Ordinal);
        Assert.Contains("validation_os", script, StringComparison.Ordinal);
        Assert.Contains("StringComparer]::Ordinal", script, StringComparison.Ordinal);
        Assert.Contains("row_contract_mismatch", script, StringComparison.Ordinal);
        Assert.Contains("live_blocker_contract_mismatch", script, StringComparison.Ordinal);
        Assert.Contains("verifier_working_copy_mismatch", script, StringComparison.Ordinal);
        Assert.Contains("red_failure_fixture_not_authenticated", script, StringComparison.Ordinal);
        Assert.Contains("red_failure_correction_not_executable", script, StringComparison.Ordinal);
        Assert.Contains("live_evidence_anchor_", script, StringComparison.Ordinal);
        Assert.Contains("Read-AuthenticatedRedFixture", script, StringComparison.Ordinal);
        Assert.Contains("row_contract_profile_ledger_", script, StringComparison.Ordinal);
        Assert.DoesNotContain("git checkout", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git reset", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git clean", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DirectCatalogAndBrowserRetentionBoundariesRemainExecutable()
    {
        var routes = File.ReadAllText(RepositoryPath(
            "tests/CopilotAgentObservability.LocalMonitor.Tests/CostRouteTests.cs"));
        Assert.Contains(
            "CatalogRoutePinsDefaultFiftyMaximumOneHundredAndRejectsOneHundredOne",
            routes,
            StringComparison.Ordinal);
        Assert.Contains(
            "CatalogRouteRejectsCanonicalSameCatalogNonmemberCursor",
            routes,
            StringComparison.Ordinal);

        var browser = File.ReadAllText(RepositoryPath(
            "tests/CopilotAgentObservability.LocalMonitor.Tests/CostPagePlaywrightTests.cs"));
        Assert.Contains(
            "CostPage_RetainsAtMostSixtyFourSourcesAndOneHundredCatalogEntries",
            browser,
            StringComparison.Ordinal);
        Assert.Contains("ToHaveCountAsync(164)", browser, StringComparison.Ordinal);
        Assert.Contains("ToHaveCountAsync(101)", browser, StringComparison.Ordinal);
        Assert.Contains("Assert.InRange(polls, 1, 40)", browser, StringComparison.Ordinal);
        Assert.DoesNotContain("Task.Delay(300)", browser, StringComparison.Ordinal);

        var alertBrowser = File.ReadAllText(RepositoryPath(
            "tests/CopilotAgentObservability.LocalMonitor.Tests/AlertCenterPlaywrightTests.cs"));
        Assert.Contains(
            "AlertCenter_CostReceiptV2WarningAndCriticalPreserveExactLinksAndLifecycle",
            alertBrowser,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformSecurityTestsNeverReturnAsSilentSuccess()
    {
        var providerTests = File.ReadAllText(RepositoryPath(
            "tests/CopilotAgentObservability.LocalMonitor.Tests/PricingCatalogProviderTests.cs"));
        Assert.Contains("WindowsFact", providerTests, StringComparison.Ordinal);
        Assert.Contains("LinuxFact", providerTests, StringComparison.Ordinal);
        Assert.Contains("SkipException.ForSkip", providerTests, StringComparison.Ordinal);
        Assert.Contains(
            "security_prerequisite_unavailable:linux_fifo",
            providerTests,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (!OperatingSystem.IsWindows())\n        {\n            return;",
            providerTests,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "if (!OperatingSystem.IsLinux())\n        {\n            return;",
            providerTests,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvidenceChainVerifierSelfTestCoversAncestryDiffAndHashFailures()
    {
        var scriptPath = RepositoryPath("scripts/validation/issue-95/test-evidence-chain.ps1");
        var verifierPath = RepositoryPath("scripts/validation/issue-95/verify-evidence-chain.ps1");
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-VerifierPath");
        startInfo.ArgumentList.Add(verifierPath);
        startInfo.ArgumentList.Add("-MatrixValidatorPath");
        startInfo.ArgumentList.Add(RepositoryPath("scripts/validation/issue-91/validate-matrix.ps1"));
        startInfo.ArgumentList.Add("-MatrixFixturePath");
        startInfo.ArgumentList.Add(RepositoryPath(RowContractRelativePath));

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"{await stdoutTask}{await stderrTask}");
        Assert.Contains("evidence_chain_self_test=PASS cases=31", await stdoutTask, StringComparison.Ordinal);
        Assert.Equal(string.Empty, await stderrTask);
    }

    private static void AssertObjectProperties(JsonElement element, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));

    private static string RepositoryPath(string relativePath) =>
        Path.Combine([RepositoryRoot, .. relativePath.Split('/')]);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

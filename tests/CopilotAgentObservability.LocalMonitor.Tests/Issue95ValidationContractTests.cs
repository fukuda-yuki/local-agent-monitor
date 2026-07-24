using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class Issue95ValidationContractTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private const string HandoffRelativePath = "docs/specifications/contracts/cost-analytics/v1/issue-91-validation-handoff.json";
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
            "production_surface_state", "active_row_ids", "required_profiles",
            "automated_test_filters", "expected_evidence_location", "canonical_transition", "evidence_binding");
        Assert.Equal("cost-analytics-validation-handoff.v1", root.GetProperty("schema_version").GetString());
        Assert.Equal("cost-analytics", root.GetProperty("surface_id").GetString());
        Assert.Equal(95, root.GetProperty("owner_issue").GetInt32());
        Assert.Equal("not_registered", root.GetProperty("future_registry_state_at_kickoff").GetString());
        Assert.Equal("implemented_candidate", root.GetProperty("production_surface_state").GetString());
        Assert.Equal(["91-A-095", "91-S-095", "91-L-095"],
            root.GetProperty("active_row_ids").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            ["raw-default", "sanitized-only", "trusted-bundled-catalog", "private-local-override", "source-mapping-unavailable"],
            root.GetProperty("required_profiles").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(
            [
                "FullyQualifiedName~Pricing",
                "FullyQualifiedName~Cost",
                "FullyQualifiedName~AlertCenter",
                "FullyQualifiedName~AlertLifecycle",
                "FullyQualifiedName~SanitizedExport",
                "FullyQualifiedName~RuntimeBackup",
                "FullyQualifiedName~LocalMonitorScriptTests",
            ],
            root.GetProperty("automated_test_filters").EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(MatrixRelativePath, root.GetProperty("expected_evidence_location").GetString());
        Assert.Contains("never a future-registry entry", root.GetProperty("canonical_transition").GetString(), StringComparison.Ordinal);
        Assert.Contains("no pass was inherited", root.GetProperty("canonical_transition").GetString(), StringComparison.Ordinal);

        var binding = root.GetProperty("evidence_binding");
        AssertObjectProperties(binding,
            "state", "matrix_path", "checksum_manifest_path", "attestation_path",
            "matrix_prep_sha", "final_validation_sha");
        Assert.Equal("pending_candidate_execution", binding.GetProperty("state").GetString());
        Assert.Equal(MatrixRelativePath, binding.GetProperty("matrix_path").GetString());
        Assert.Equal(ChecksumRelativePath, binding.GetProperty("checksum_manifest_path").GetString());
        Assert.Equal(AttestationRelativePath, binding.GetProperty("attestation_path").GetString());
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("matrix_prep_sha").ValueKind);
        Assert.Equal(JsonValueKind.Null, binding.GetProperty("final_validation_sha").ValueKind);
    }

    [Fact]
    public void FutureEvidenceMustBindRowsHandoffAndRepositorySafeChecksums()
    {
        var matrixPath = RepositoryPath(MatrixRelativePath);
        if (!File.Exists(matrixPath)) return;

        using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
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

        var checksumPath = RepositoryPath(ChecksumRelativePath);
        Assert.True(File.Exists(checksumPath));
        using var checksums = JsonDocument.Parse(File.ReadAllText(checksumPath));
        foreach (var artifact in checksums.RootElement.GetProperty("artifacts").EnumerateArray())
        {
            var relativePath = artifact.GetProperty("path").GetString()!;
            Assert.DoesNotContain("artifact-checksums.json", relativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("evidence-attestation.json", relativePath, StringComparison.Ordinal);
            Assert.DoesNotContain("archive", relativePath, StringComparison.OrdinalIgnoreCase);
            Assert.Matches("^[0-9a-f]{64}$", artifact.GetProperty("sha256").GetString());
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RepositoryPath(relativePath)))).ToLowerInvariant();
            Assert.Equal(artifact.GetProperty("sha256").GetString(), digest);
        }
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
        Assert.DoesNotContain("git checkout", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git reset", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("git clean", script, StringComparison.OrdinalIgnoreCase);
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

        using var process = Process.Start(startInfo)!;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, $"{await stdoutTask}{await stderrTask}");
        Assert.Contains("evidence_chain_self_test=PASS cases=4", await stdoutTask, StringComparison.Ordinal);
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

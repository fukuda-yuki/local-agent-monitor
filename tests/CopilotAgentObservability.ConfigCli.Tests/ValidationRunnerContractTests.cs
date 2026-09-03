using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ValidationRunnerContractTests
{
    private const string RepositoryCompare =
        "CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1RepositoryComparePlaywrightTests.ImmutableCompareRendersNineSectionsRowsEvidenceAndResponsiveTableWithoutRecompute";
    private const string SessionExplorer =
        "CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1SessionExplorerPlaywrightTests.ComparePreviewCreatesFromTransientOrderedCohortsAndNavigatesOnlyByServerLocation";

    [Fact]
    public async Task NightlyEvidenceAcceptsExactlyOneTrxStorageIdentityPerExpectedProject()
    {
        using var directory = new TempDirectory();
        WriteProjectTrx(directory.Path, "config.trx", "bin/Config.Tests.dll", "Example.Config.Fact");
        WriteProjectTrx(directory.Path, "local.trx", "bin/Local.Tests.dll", "Example.Local.Fact");
        WriteNightlyReceipt(directory.Path, "tests/Config.Tests/Config.Tests.csproj", "success", 0, false, true);
        WriteNightlyReceipt(directory.Path, "tests/Local.Tests/Local.Tests.csproj", "success", 0, false, true);

        var result = await RunContractAsync(
            "-Mode", "NightlyEvidence",
            "-ResultsDirectory", directory.Path,
            "-ExpectedProjectsJson", JsonSerializer.Serialize(new[]
            {
                "tests/Config.Tests/Config.Tests.csproj",
                "tests/Local.Tests/Local.Tests.csproj",
            }));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("nightly_trx_evidence=passed", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bare")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public async Task NightlyEvidenceRejectsAmbiguousOrIncompleteProjectStorage(string defect)
    {
        using var directory = new TempDirectory();
        if (defect == "bare") WriteProjectTrx(directory.Path, "config.trx", "", "Example.Config.Fact");
        else WriteProjectTrx(directory.Path, "config.trx", "bin/Config.Tests.dll", "Example.Config.Fact");
        if (defect == "duplicate")
            WriteProjectTrx(directory.Path, "config-copy.trx", "other/Config.Tests.dll", "Example.Config.OtherFact");
        if (defect != "missing")
            WriteProjectTrx(directory.Path, "local.trx", "bin/Local.Tests.dll", "Example.Local.Fact");
        WriteNightlyReceipt(directory.Path, "tests/Config.Tests/Config.Tests.csproj", "success", 0, false, true);
        WriteNightlyReceipt(directory.Path, "tests/Local.Tests/Local.Tests.csproj", "success", 0, false, true);

        var result = await RunContractAsync(
            "-Mode", "NightlyEvidence",
            "-ResultsDirectory", directory.Path,
            "-ExpectedProjectsJson", JsonSerializer.Serialize(new[]
            {
                "tests/Config.Tests/Config.Tests.csproj",
                "tests/Local.Tests/Local.Tests.csproj",
            }));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("TRX", result.Output, StringComparison.OrdinalIgnoreCase);
        if (defect == "missing")
            Assert.Contains("tests/Local.Tests/Local.Tests.csproj", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NightlyEvidenceRejectsMissingReceiptByExactProjectPath()
    {
        using var directory = new TempDirectory();
        WriteProjectTrx(directory.Path, "config.trx", "bin/Config.Tests.dll", "Example.Config.Fact");
        WriteProjectTrx(directory.Path, "local.trx", "bin/Local.Tests.dll", "Example.Local.Fact");
        WriteNightlyReceipt(directory.Path, "tests/Config.Tests/Config.Tests.csproj", "success", 0, false, true);

        var result = await RunContractAsync(
            "-Mode", "NightlyEvidence",
            "-ResultsDirectory", directory.Path,
            "-ExpectedProjectsJson", JsonSerializer.Serialize(new[]
            {
                "tests/Config.Tests/Config.Tests.csproj",
                "tests/Local.Tests/Local.Tests.csproj",
            }));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tests/Local.Tests/Local.Tests.csproj", result.Output, StringComparison.Ordinal);
        Assert.Contains("receipt", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NightlyEvidenceRejectsTimedOutProjectByExactProjectPath()
    {
        using var directory = new TempDirectory();
        WriteProjectTrx(directory.Path, "local.trx", "bin/Local.Tests.dll", "Example.Local.Fact");
        WriteNightlyReceipt(directory.Path, "tests/Config.Tests/Config.Tests.csproj", "timeout", -1, true, true);
        WriteNightlyReceipt(directory.Path, "tests/Local.Tests/Local.Tests.csproj", "success", 0, false, true);

        var result = await RunContractAsync(
            "-Mode", "NightlyEvidence",
            "-ResultsDirectory", directory.Path,
            "-ExpectedProjectsJson", JsonSerializer.Serialize(new[]
            {
                "tests/Config.Tests/Config.Tests.csproj",
                "tests/Local.Tests/Local.Tests.csproj",
            }));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tests/Config.Tests/Config.Tests.csproj", result.Output, StringComparison.Ordinal);
        Assert.Contains("timeout", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NightlyEvidenceRejectsReceiptWhenRootExitedButCapturedDescendantDidNot()
    {
        using var directory = new TempDirectory();
        WriteProjectTrx(directory.Path, "config.trx", "bin/Config.Tests.dll", "Example.Config.Fact");
        WriteNightlyReceipt(
            directory.Path,
            "tests/Config.Tests/Config.Tests.csproj",
            "success",
            0,
            false,
            true,
            processTreeExited: false);

        var result = await RunContractAsync(
            "-Mode", "NightlyEvidence",
            "-ResultsDirectory", directory.Path,
            "-ExpectedProjectsJson", JsonSerializer.Serialize(new[]
            {
                "tests/Config.Tests/Config.Tests.csproj",
            }));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tests/Config.Tests/Config.Tests.csproj", result.Output, StringComparison.Ordinal);
        Assert.Contains("process_tree_exited=False", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void SolutionPassUsesCollisionResistantTrxPrefixAndRunnerOwnedNightlyProjectAuthority()
    {
        var source = File.ReadAllText(RunnerScript);

        Assert.DoesNotContain("'--logger', 'trx', '--results-directory', $ResultsDirectory", source, StringComparison.Ordinal);
        Assert.Contains("'--logger', ('trx;LogFilePrefix={0}' -f $LogFilePrefix)", source, StringComparison.Ordinal);
        Assert.Contains("$nightlyExpectedProjects = @($testProjects.Values) + @($localProject)", source, StringComparison.Ordinal);
        Assert.Contains("'-ExpectedProjectsJson', $serializedNightlyExpectedProjects", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CriticalSmokeAssertionAcceptsOnlyTheExactTwoPassedIdentities()
    {
        using var directory = new TempDirectory();
        WriteTrx(directory.Path, (RepositoryCompare, "Passed"), (SessionExplorer, "Passed"));

        var result = await RunContractAsync(
            "-Mode", "CriticalSmoke",
            "-ResultsDirectory", directory.Path,
            "-ExpectedFqns", $"{RepositoryCompare};{SessionExplorer}");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task CriticalSmokeAssertionRejectsWrongIdentityEvenWhenTwoRowsPass()
    {
        using var directory = new TempDirectory();
        WriteTrx(directory.Path, (RepositoryCompare, "Passed"), ("Example.Tests.WrongIdentity", "Passed"));

        var result = await RunContractAsync(
            "-Mode", "CriticalSmoke",
            "-ResultsDirectory", directory.Path,
            "-ExpectedFqns", $"{RepositoryCompare};{SessionExplorer}");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("exact critical smoke identities", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1800.0, 0)]
    [InlineData(1800.001, 1)]
    public async Task CompletionBudgetAcceptsThirtyMinutesAndRejectsAnyOverrun(
        double elapsedSeconds,
        int expectedExitCode)
    {
        var result = await RunContractAsync(
            "-Mode", "CompletionBudget",
            "-ElapsedSeconds", elapsedSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(expectedExitCode, result.ExitCode);
        if (expectedExitCode != 0)
            Assert.Contains("30 minute budget", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ManifestAssertionAcceptsAnExactPartitionWithTheoryMultiplicity()
    {
        using var directory = new TempDirectory();
        var manifest = CreateManifest(
            directory.Path,
            ("s-a", new[]
            {
                Row("Example.Tests.A.Fact"),
                Row("Example.Tests.A.Theory", "Example.Tests.A.Theory(value: 1)", 1),
                Row("Example.Tests.A.Theory", "Example.Tests.A.Theory(value: 2)", 2),
            }),
            ("s-b", new[] { Row("Example.Tests.B.Fact", projectPath: "tests/s-b/s-b.csproj") }));

        var result = await RunContractAsync(
            "-Mode", "Manifest",
            "-ManifestPath", manifest,
            "-ExpectedShardIds", "s-a;s-b");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("manifest_exact_partition=passed", result.Output);
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("gap")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("hash")]
    [InlineData("missing-prerequisites")]
    [InlineData("duplicate-prerequisite")]
    [InlineData("traversal-prerequisite")]
    [InlineData("invalid-prerequisite")]
    [InlineData("null-prerequisites")]
    [InlineData("scalar-prerequisite")]
    public async Task ManifestAssertionRejectsInvalidShardMembership(string defect)
    {
        using var directory = new TempDirectory();
        var first = new[] { Row("Example.Tests.A.Fact") };
        var second = new[] { Row("Example.Tests.B.Fact", projectPath: "tests/s-b/s-b.csproj") };
        var required = new[] { "s-a", "s-b" };
        var baseline = first.Concat(second).ToArray();
        var shards = new List<object>
        {
            Shard("s-a", first),
            Shard("s-b", second),
        };
        switch (defect)
        {
            case "overlap": shards[1] = Shard("s-b", first.Concat(second).ToArray()); break;
            case "gap": baseline = baseline.Append(Row("Example.Tests.C.Fact", projectPath: "tests/s-c/s-c.csproj")).ToArray(); break;
            case "empty": shards[1] = Shard("s-b", Array.Empty<object>()); break;
            case "unknown": shards.Add(Shard("s-c", new[] { Row("Example.Tests.C.Fact", projectPath: "tests/s-c/s-c.csproj") })); break;
        }

        var manifest = WriteManifest(directory.Path, required, baseline, shards);
        if (defect is "hash" or "missing-prerequisites" or "duplicate-prerequisite" or
            "traversal-prerequisite" or "invalid-prerequisite" or "null-prerequisites" or
            "scalar-prerequisite")
        {
            var document = JsonNode.Parse(File.ReadAllText(manifest))!.AsObject();
            var firstShard = document["shards"]![0]!.AsObject();
            switch (defect)
            {
                case "hash": document["baselineHash"] = "wrong"; break;
                case "missing-prerequisites": firstShard.Remove("prerequisiteProjects"); break;
                case "duplicate-prerequisite":
                    firstShard["prerequisiteProjects"] = new JsonArray("src/a/a.csproj", "src/a/a.csproj");
                    break;
                case "traversal-prerequisite":
                    firstShard["prerequisiteProjects"] = new JsonArray("../outside.csproj");
                    break;
                case "invalid-prerequisite":
                    firstShard["prerequisiteProjects"] = new JsonArray("src/a/not-a-project.txt");
                    break;
                case "null-prerequisites": firstShard["prerequisiteProjects"] = null; break;
                case "scalar-prerequisite": firstShard["prerequisiteProjects"] = "src/a/a.csproj"; break;
            }
            File.WriteAllText(manifest, document.ToJsonString());
        }
        var result = await RunContractAsync(
            "-Mode", "Manifest",
            "-ManifestPath", manifest,
            "-ExpectedShardIds", "s-a;s-b");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("manifest", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShardAssertionUsesProjectFqnOccurrenceAndTreatsDisplayAsDiagnostic()
    {
        using var directory = new TempDirectory();
        var allowedSkip = "Example.Tests.A.WindowsOnly";
        var expected = new[]
        {
            Row("Example.Tests.A.Theory", "Example.Tests.A.Theory(value: ···)", 1),
            Row("Example.Tests.A.Theory", "Example.Tests.A.Theory(value: ···)", 2),
            Row(allowedSkip),
        };
        var manifest = CreateManifest(directory.Path, ("s-a", expected));
        var results = Directory.CreateDirectory(Path.Combine(directory.Path, "results")).FullName;
        WriteTrx(
            results,
            ("Example.Tests.A.Theory", "Example.Tests.A.Theory(value: 1)", "Passed"),
            ("Example.Tests.A.Theory", "Example.Tests.A.Theory(value: 2)", "Passed"),
            (allowedSkip, allowedSkip, "NotExecuted"));
        var receipt = WriteReceipt(
            directory.Path,
            "s-a",
            "success",
            0,
            false,
            actualCount: expected.Length,
            actualHash: RowHash(expected));

        var result = await RunContractAsync(
            "-Mode", "Shard",
            "-ManifestPath", manifest,
            "-ShardId", "s-a",
            "-ReceiptPath", receipt,
            "-ResultsDirectory", results,
            "-ExpectedSkippedFqns", allowedSkip);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("shard_exact_rows=passed", result.Output);
    }

    [Fact]
    public async Task TheoryExpansionReplacesOnlyCollapsedTheoryPlaceholders()
    {
        using var directory = new TempDirectory();
        var discoveryRows = WriteDiscoveryRows(
            directory.Path,
            ("Example.Tests.Collapsed", "Example.Tests.Collapsed"),
            ("Example.Tests.PreEnumerated", "Example.Tests.PreEnumerated(value: ···)"),
            ("Example.Tests.Fact", "Example.Tests.Fact"));
        var expansion = Directory.CreateDirectory(Path.Combine(directory.Path, "expansion")).FullName;
        WriteTrx(
            expansion,
            ("Example.Tests.Collapsed", "Example.Tests.Collapsed(value: full-1)", "Passed"),
            ("Example.Tests.Collapsed", "Example.Tests.Collapsed(value: full-2)", "Passed"));

        var result = await RunContractAsync(
            "-Mode", "TheoryExpansion",
            "-DiscoveryRowsPath", discoveryRows,
            "-ExpansionResultsDirectory", expansion,
            "-ProjectPath", "tests/s-a/s-a.csproj",
            "-CollapsedFqns", "Example.Tests.Collapsed",
            "-ExpansionExitCode", "0");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("theory_expanded_rows=4", result.Output);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("unexpected")]
    [InlineData("failed")]
    [InlineData("skipped")]
    [InlineData("nonzero")]
    public async Task TheoryExpansionFailsClosedForInvalidBoundedEvidence(string defect)
    {
        using var directory = new TempDirectory();
        var discoveryRows = WriteDiscoveryRows(
            directory.Path,
            ("Example.Tests.Collapsed", "Example.Tests.Collapsed"),
            ("Example.Tests.Fact", "Example.Tests.Fact"));
        var expansion = Directory.CreateDirectory(Path.Combine(directory.Path, "expansion")).FullName;
        var fqn = defect == "unexpected" ? "Example.Tests.Other" : "Example.Tests.Collapsed";
        var outcome = defect switch
        {
            "failed" => "Failed",
            "skipped" => "NotExecuted",
            _ => "Passed",
        };
        if (defect == "zero") WriteTrx(expansion, Array.Empty<(string Fqn, string TestName, string Outcome)>());
        else WriteTrx(expansion, (fqn, fqn, outcome));

        var result = await RunContractAsync(
            "-Mode", "TheoryExpansion",
            "-DiscoveryRowsPath", discoveryRows,
            "-ExpansionResultsDirectory", expansion,
            "-ProjectPath", "tests/s-a/s-a.csproj",
            "-CollapsedFqns", "Example.Tests.Collapsed",
            "-ExpansionExitCode", defect == "nonzero" ? "1" : "0");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Theory]
    [InlineData("wrong-sha")]
    [InlineData("wrong-digest")]
    [InlineData("nonzero")]
    [InlineData("timeout")]
    [InlineData("unexpected-skip")]
    [InlineData("row-mismatch")]
    [InlineData("missing-occurrence")]
    [InlineData("extra-occurrence")]
    [InlineData("wrong-actual-count")]
    [InlineData("wrong-actual-hash")]
    public async Task ShardAssertionRejectsIncompleteOrMismatchedEvidence(string defect)
    {
        using var directory = new TempDirectory();
        var expected = new[] { Row("Example.Tests.A.Fact") };
        var manifest = CreateManifest(directory.Path, ("s-a", expected));
        var results = Directory.CreateDirectory(Path.Combine(directory.Path, "results")).FullName;
        var fqn = defect == "row-mismatch" ? "Example.Tests.A.Other" : "Example.Tests.A.Fact";
        var outcome = defect == "unexpected-skip" ? "NotExecuted" : "Passed";
        if (defect == "extra-occurrence")
            WriteTrx(results, (fqn, fqn, outcome), (fqn, fqn, outcome));
        else if (defect != "missing-occurrence")
            WriteTrx(results, (fqn, fqn, outcome));
        var receipt = WriteReceipt(
            directory.Path,
            "s-a",
            defect == "timeout" ? "timeout" : "success",
            defect == "nonzero" ? 1 : 0,
            defect == "timeout",
            candidateSha: defect == "wrong-sha" ? "other" : "candidate",
            manifestDigest: defect == "wrong-digest" ? "other" : "manifest",
            actualCount: defect == "wrong-actual-count" ? 2 : 1,
            actualHash: defect == "wrong-actual-hash" ? "other" : RowHash(expected));

        var result = await RunContractAsync(
            "-Mode", "Shard",
            "-ManifestPath", manifest,
            "-ShardId", "s-a",
            "-ReceiptPath", receipt,
            "-ResultsDirectory", results);

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task AggregateAssertionAcceptsEveryRequiredShardExactlyOnce()
    {
        using var directory = new TempDirectory();
        var manifest = CreateManifest(
            directory.Path,
            ("s-a", new[] { Row("Example.Tests.A.Fact") }),
            ("s-b", new[] { Row("Example.Tests.B.Fact", projectPath: "tests/s-b/s-b.csproj") }));
        var artifacts = Directory.CreateDirectory(Path.Combine(directory.Path, "artifacts")).FullName;
        WriteShardArtifact(artifacts, "s-a", "Example.Tests.A.Fact");
        WriteShardArtifact(artifacts, "s-b", "Example.Tests.B.Fact");
        var dependencies = WriteDependencies(directory.Path, ("s-a", "success"), ("s-b", "success"));

        var result = await RunContractAsync(
            "-Mode", "Aggregate",
            "-ManifestPath", manifest,
            "-ArtifactsDirectory", artifacts,
            "-DependencyResultsPath", dependencies,
            "-RunAttempt", "1",
            "-ExpectedShardIds", "s-a;s-b");

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("completion_aggregate=passed", result.Output);
    }

    [Theory]
    [InlineData("missing-artifact")]
    [InlineData("duplicate-artifact")]
    [InlineData("failed-dependency")]
    [InlineData("skipped-dependency")]
    [InlineData("aborted-dependency")]
    [InlineData("timeout-dependency")]
    [InlineData("unexpected-artifact")]
    [InlineData("wrong-artifact-name")]
    [InlineData("receipt-under-wrong-artifact")]
    public async Task AggregateAssertionFailsClosedWhenEvidenceIsIncomplete(string defect)
    {
        using var directory = new TempDirectory();
        var manifest = CreateManifest(
            directory.Path,
            ("s-a", new[] { Row("Example.Tests.A.Fact") }),
            ("s-b", new[] { Row("Example.Tests.B.Fact", projectPath: "tests/s-b/s-b.csproj") }));
        var artifacts = Directory.CreateDirectory(Path.Combine(directory.Path, "artifacts")).FullName;
        WriteShardArtifact(artifacts, "s-a", "Example.Tests.A.Fact");
        if (defect != "missing-artifact") WriteShardArtifact(artifacts, "s-b", "Example.Tests.B.Fact");
        if (defect == "duplicate-artifact")
            WriteShardArtifact(artifacts, "s-b", "Example.Tests.B.Fact", "copy");
        if (defect == "unexpected-artifact")
            WriteShardArtifact(artifacts, "s-c", "Example.Tests.C.Fact");
        if (defect == "wrong-artifact-name")
            Directory.Move(
                Path.Combine(artifacts, "completion-shard-candidate-1-s-b"),
                Path.Combine(artifacts, "completion-shard-candidate-1-wrong"));
        if (defect == "receipt-under-wrong-artifact")
        {
            var first = Path.Combine(artifacts, "completion-shard-candidate-1-s-a", "receipt-s-a.json");
            var second = Path.Combine(artifacts, "completion-shard-candidate-1-s-b", "receipt-s-b.json");
            var temporary = Path.Combine(artifacts, "receipt-swap.json");
            File.Move(first, temporary);
            File.Move(second, first);
            File.Move(temporary, second);
        }
        var dependencies = WriteDependencies(
            directory.Path,
            ("s-a", defect == "failed-dependency" ? "failure" : "success"),
            ("s-b", defect switch
            {
                "skipped-dependency" => "skipped",
                "aborted-dependency" => "cancelled",
                "timeout-dependency" => "timed_out",
                _ => "success",
            }));

        var result = await RunContractAsync(
            "-Mode", "Aggregate",
            "-ManifestPath", manifest,
            "-ArtifactsDirectory", artifacts,
            "-DependencyResultsPath", dependencies,
            "-RunAttempt", "1",
            "-ExpectedShardIds", "s-a;s-b");

        Assert.NotEqual(0, result.ExitCode);
    }

    [Fact]
    public async Task HostedWorkflowStructureKeepsValidationAuthorityInTheRunner()
    {
        var result = await RunContractAsync(
            "-Mode", "Workflow",
            "-WorkflowPath", Path.Combine(RepositoryRoot, ".github", "workflows", "validation.yml"),
            "-RunnerPath", Path.Combine(RepositoryRoot, "scripts", "test", "run-validation.ps1"),
            "-GuidePath", Path.Combine(RepositoryRoot, "docs", "agent-guides", "repository-workflow.md"));

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("workflow_completion_topology=passed", result.Output);
        var workflow = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot, ".github", "workflows", "validation.yml"));
        Assert.Contains("path: ~/.nuget/packages", workflow, StringComparison.Ordinal);

        using var successfulPreflight = new TempDirectory();
        var successfulResult = await RunDiscoveryPreflightAsync(successfulPreflight.Path, failPolicyTests: false);
        Assert.NotEqual(0, successfulResult.ExitCode);
        Assert.Equal(
            ["policy-tests", "policy-guard", "build"],
            await File.ReadAllLinesAsync(Path.Combine(successfulPreflight.Path, "preflight.log")));

        using var failedPolicyTests = new TempDirectory();
        var failedResult = await RunDiscoveryPreflightAsync(failedPolicyTests.Path, failPolicyTests: true);
        Assert.NotEqual(0, failedResult.ExitCode);
        Assert.Equal(
            ["policy-tests"],
            await File.ReadAllLinesAsync(Path.Combine(failedPolicyTests.Path, "preflight.log")));
    }

    [Fact]
    public void HostedNightlyHasOuterDeadlineAndAlwaysUploadsProjectEvidence()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot, ".github", "workflows", "validation.yml"));
        var windowsStart = workflow.IndexOf("  nightly-windows:", StringComparison.Ordinal);
        var linuxStart = workflow.IndexOf("  nightly-linux:", StringComparison.Ordinal);
        var windows = workflow[windowsStart..linuxStart];
        var linux = workflow[linuxStart..];

        foreach (var job in new[] { windows, linux })
        {
            Assert.Contains("timeout-minutes: 330", job, StringComparison.Ordinal);
            Assert.Contains("if: always()", job, StringComparison.Ordinal);
            Assert.Contains("path: artifacts/validation/**", job, StringComparison.Ordinal);
            Assert.Contains("if-no-files-found: error", job, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NightlyRunnerBoundsEveryOwnedProjectAndUsesUniqueProjectEvidenceNames()
    {
        var source = File.ReadAllText(RunnerScript);
        var nightlyStart = source.IndexOf("function Invoke-NightlyValidation", StringComparison.Ordinal);

        Assert.True(nightlyStart >= 0, "Nightly bounded runner function was not found.");
        var nightly = source[nightlyStart..];
        Assert.Contains("foreach ($projectPath in $nightlyExpectedProjects)", nightly, StringComparison.Ordinal);
        Assert.Contains("Invoke-BoundedCommand", nightly, StringComparison.Ordinal);
        Assert.Contains("$nightlyProjectTimeoutSeconds * 1000", nightly, StringComparison.Ordinal);
        Assert.Contains("receipt-$projectIdentity.json", nightly, StringComparison.Ordinal);
        Assert.Contains("nightly-$partitionToken-$projectIdentity", nightly, StringComparison.Ordinal);
        Assert.DoesNotContain("Invoke-TestPass -Target $solution -Filter $nightlyFilter", nightly, StringComparison.Ordinal);
        Assert.Contains(
            "Invoke-NightlyValidation -ResultsDirectory $nightlyResults -CommandFilePath 'dotnet' -CommandArgumentPrefix @()",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunnerClassifiesOnlyCollapsedTheoryAndBuildsExactBoundedFilter()
    {
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            foreach ($name in @('Get-CollapsedRuntimeTheoryFqns', 'Get-ExactFqnFilter', 'Merge-CollapsedTheoryRows')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Runner function was not found: $name" }
                Invoke-Expression $function.Extent.Text
            }
            $collapsed = 'CopilotAgentObservability.ConfigCli.Tests.ClaudeDoctorFactMapperTests.Map_RepresentativeInputMatrix_ProducesValidFactSnapshot'
            $preEnumerated = 'CopilotAgentObservability.ConfigCli.Tests.ValidationRunnerContractTests.CompletionBudgetAcceptsThirtyMinutesAndRejectsAnyOverrun'
            $fact = 'CopilotAgentObservability.ConfigCli.Tests.ValidationRunnerContractTests.ManifestAssertionAcceptsAnExactPartitionWithTheoryMultiplicity'
            $rows = @(
                [pscustomobject]@{ fqn = $collapsed; testName = $collapsed; source = 'list' },
                [pscustomobject]@{ fqn = $preEnumerated; testName = "$preEnumerated(elapsedSeconds: 1800)"; source = 'list' },
                [pscustomobject]@{ fqn = $fact; testName = $fact; source = 'list' })
            $classified = @(Get-CollapsedRuntimeTheoryFqns -Rows $rows -AssemblyPath $env:VALIDATION_TEST_ARG_1)
            if ($classified.Count -ne 1 -or $classified[0] -ne $collapsed) {
                throw "Classification reran a pre-enumerated Theory or Fact: $($classified -join ',')"
            }
            $filter = Get-ExactFqnFilter -Fqns $classified
            if ($filter -ne "FullyQualifiedName=$collapsed") { throw "Unexpected exact filter: $filter" }
            $expanded = @(
                [pscustomobject]@{ fqn = $collapsed; testName = "$collapsed(value: 1)"; outcome = 'Passed'; source = 'trx'; authorityIdentity = 'a' },
                [pscustomobject]@{ fqn = $collapsed; testName = "$collapsed(value: 2)"; outcome = 'Passed'; source = 'trx'; authorityIdentity = 'b' })
            $merged = @(Merge-CollapsedTheoryRows -Rows $rows -CollapsedFqns $classified -ExpandedRows $expanded)
            if ($merged.Count -ne 4 -or @($merged | Where-Object { $_.source -eq 'list' -and $_.fqn -eq $collapsed }).Count -ne 0) {
                throw 'Collapsed placeholder remained after merge.'
            }
            $duplicatePlaceholder = @($rows + [pscustomobject]@{ fqn = $collapsed; testName = $collapsed; source = 'list' })
            $rejected = $false
            try { $null = Merge-CollapsedTheoryRows -Rows $duplicatePlaceholder -CollapsedFqns $classified -ExpandedRows $expanded }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Residual collapsed placeholder was accepted.' }
            """;
        var assembly = Path.Combine(AppContext.BaseDirectory, "CopilotAgentObservability.ConfigCli.Tests.dll");

        var result = await RunPowerShellCommandAsync(command, RunnerScript, assembly);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task RunnerFinalizationReserveAndProcessTreeTimeoutAreEnforced()
    {
        using var directory = new TempDirectory();
        var marker = Path.Combine(directory.Path, "orphan.txt");
        var timeoutReceipt = Path.Combine(directory.Path, "receipt-timeout.json");
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            foreach ($name in @('Get-RemainingMilliseconds', 'Invoke-PhaseCommand', 'Assert-PhaseFinalizationReserve', 'Assert-PhaseCompletedWithinBudget', 'Write-ImmutableJson', 'Write-ShardFailureReceipt')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Runner function was not found: $name" }
                Invoke-Expression $function.Extent.Text
            }
            $phaseBudgetSeconds = 1800
            $phaseFinalizationReserveSeconds = 5
            Assert-PhaseFinalizationReserve -ElapsedSeconds 1795
            $rejected = $false
            try { Assert-PhaseFinalizationReserve -ElapsedSeconds 1795.001 }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Receipt reserve overrun was accepted.' }
            Assert-PhaseCompletedWithinBudget -ElapsedSeconds 1800
            $rejected = $false
            try { Assert-PhaseCompletedWithinBudget -ElapsedSeconds 1800.001 }
            catch { $rejected = $true }
            if (-not $rejected) { throw 'Post-finalization budget overrun was accepted.' }
            $repoRoot = (Get-Location).Path
            $phaseBudgetSeconds = 2
            $phaseFinalizationReserveSeconds = 1
            $phaseStopwatch = [Diagnostics.Stopwatch]::StartNew()
            $child = "Start-Sleep -Milliseconds 1500; [IO.File]::WriteAllText('$($env:VALIDATION_TEST_ARG_1.Replace("'", "''"))', 'orphan')"
            $parent = "Start-Process pwsh -ArgumentList @('-NoProfile','-Command',`"$child`"); Start-Sleep -Seconds 30"
            $result = Invoke-PhaseCommand -FilePath 'pwsh' -Arguments @('-NoProfile', '-Command', $parent)
            if (-not $result.TimedOut) { throw 'Hung process did not time out.' }
            Write-ShardFailureReceipt -Path $env:VALIDATION_TEST_ARG_2 -Value ([ordered]@{
                status = 'timeout'; timedOut = $true; exitCode = -1
            })
            Start-Sleep -Seconds 2
            if (Test-Path -LiteralPath $env:VALIDATION_TEST_ARG_1) { throw 'Timed-out descendant process survived.' }
            """;

        var result = await RunPowerShellCommandAsync(command, RunnerScript, marker, timeoutReceipt);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("\"status\": \"timeout\"", await File.ReadAllTextAsync(timeoutReceipt));
    }

    [Fact]
    public async Task NightlyProjectTimeoutKillsDescendantAndPublishesReceiptWithoutWaitingForPipeEof()
    {
        using var directory = new TempDirectory();
        var marker = Path.Combine(directory.Path, "descendant-survived.txt");
        var receipt = Path.Combine(directory.Path, "receipt-Example.Tests.json");
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            foreach ($name in @('Get-ProcessTreeSnapshot', 'Get-ProcessIdentityProbe', 'Wait-ProcessTreeExit', 'Invoke-BoundedCommand', 'Write-ImmutableJson', 'Write-NightlyProjectReceipt')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Runner function was not found: $name" }
                Invoke-Expression $function.Extent.Text
            }
            $repoRoot = (Get-Location).Path
            $child = "Start-Sleep -Seconds 5; [IO.File]::WriteAllText('$($env:VALIDATION_TEST_ARG_1.Replace("'", "''"))', 'survived')"
            $parent = "Start-Process pwsh -ArgumentList @('-NoProfile','-Command',`"$child`"); Start-Sleep -Seconds 30"
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            # Leave bounded startup margin for the real descendant under concurrent test load.
            $result = Invoke-BoundedCommand -FilePath 'pwsh' -Arguments @('-NoProfile', '-Command', $parent) -TimeoutMilliseconds 3000
            if (-not $result.TimedOut -or -not $result.ProcessTreeExited -or $result.CapturedProcessCount -lt 2) { throw 'Timed-out process tree did not terminalize.' }
            if ($stopwatch.Elapsed.TotalSeconds -gt 10) { throw "Timeout publication waited for pipe EOF; elapsed=$($stopwatch.Elapsed.TotalSeconds)." }
            Start-Sleep -Seconds 6
            if (Test-Path -LiteralPath $env:VALIDATION_TEST_ARG_1) { throw 'Timed-out descendant process survived.' }
            Write-NightlyProjectReceipt -Path $env:VALIDATION_TEST_ARG_2 -ProjectPath 'tests/Example.Tests/Example.Tests.csproj' -Result $result
            """;

        var result = await RunPowerShellCommandAsync(command, RunnerScript, marker, receipt);

        Assert.True(result.ExitCode == 0, result.Output);
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!.AsObject();
        Assert.Equal("tests/Example.Tests/Example.Tests.csproj", evidence["projectPath"]!.GetValue<string>());
        Assert.Equal("timeout", evidence["status"]!.GetValue<string>());
        Assert.True(evidence["timedOut"]!.GetValue<bool>());
        Assert.True(evidence["processTreeExited"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NightlyTimeoutStillKillsTreeWhenSnapshotProviderThrowsAndFailsClosed()
    {
        using var directory = new TempDirectory();
        var marker = Path.Combine(directory.Path, "descendant-survived.txt");
        var receipt = Path.Combine(directory.Path, "receipt-Example.Tests.json");
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            foreach ($name in @('Get-ProcessIdentityProbe', 'Wait-ProcessTreeExit', 'Invoke-BoundedCommand', 'Write-ImmutableJson', 'Write-NightlyProjectReceipt')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Runner function was not found: $name" }
                Invoke-Expression $function.Extent.Text
            }
            function Get-ProcessTreeSnapshot { throw 'synthetic snapshot provider failure' }
            $repoRoot = (Get-Location).Path
            $child = "Start-Sleep -Seconds 2; [IO.File]::WriteAllText('$($env:VALIDATION_TEST_ARG_1.Replace("'", "''"))', 'survived')"
            $encodedChild = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child))
            $parent = "Start-Process pwsh -ArgumentList @('-NoProfile','-EncodedCommand','$encodedChild'); Start-Sleep -Seconds 30"
            $result = Invoke-BoundedCommand -FilePath 'pwsh' -Arguments @('-NoProfile', '-Command', $parent) -TimeoutMilliseconds 500
            if (-not $result.TimedOut -or $result.SnapshotComplete -or $result.ProcessTreeExited) { throw 'Snapshot failure was not fail-closed.' }
            if ($result.SnapshotError -notlike '*synthetic snapshot provider failure*') { throw 'Snapshot error was not retained.' }
            Start-Sleep -Seconds 3
            if (Test-Path -LiteralPath $env:VALIDATION_TEST_ARG_1) { throw 'Snapshot failure prevented process-tree kill.' }
            Write-NightlyProjectReceipt -Path $env:VALIDATION_TEST_ARG_2 -ProjectPath 'tests/Example.Tests/Example.Tests.csproj' -Result $result
            """;

        var result = await RunPowerShellCommandAsync(command, RunnerScript, marker, receipt);

        Assert.True(result.ExitCode == 0, result.Output);
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!.AsObject();
        Assert.False(evidence["snapshotComplete"]!.GetValue<bool>());
        Assert.False(evidence["processTreeExited"]!.GetValue<bool>());
        Assert.Contains("synthetic snapshot provider failure", evidence["snapshotError"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessTreePollTreatsLiveUnreadableIdentityAsUnresolved()
    {
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-ProcessTreeExit'
            }, $true)
            if ($null -eq $function) { throw 'Wait-ProcessTreeExit was not found.' }
            Invoke-Expression $function.Extent.Text
            $snapshot = @([pscustomobject]@{ Id = 12345; StartTimeUtcTicks = 67890 })
            $probe = {
                param([int]$ProcessId)
                [pscustomobject]@{
                    Exists = $true
                    IdentityReadable = $false
                    StartTimeUtcTicks = $null
                    Error = 'synthetic live identity access denied'
                }
            }
            $result = Wait-ProcessTreeExit -Snapshot $snapshot -TimeoutMilliseconds 100 -ProcessProbe $probe
            if ($result.Exited -or $result.Complete) { throw 'A live process with unreadable identity was reported exited.' }
            if ($result.Error -notlike '*synthetic live identity access denied*') { throw 'Unreadable live identity error was not retained.' }
            """;

        var result = await RunPowerShellCommandAsync(command, RunnerScript);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task ProcessIdentityClassifierKeepsSuccessfulLookupLiveWhenIdentityReadThrows()
    {
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ProcessIdentityProbe'
            }, $true)
            if ($null -eq $function) { throw 'Get-ProcessIdentityProbe was not found.' }
            Invoke-Expression $function.Extent.Text
            Add-Type -TypeDefinition @'
            using System;
            public sealed class ValidationThrowingProcessIdentity : IDisposable
            {
                public DateTime StartTime => throw new InvalidOperationException("synthetic identity access denied");
                public void Dispose() { }
            }
            '@
            $global:validationFakeProcess = [ValidationThrowingProcessIdentity]::new()
            $lookup = { param([int]$ProcessId) $global:validationFakeProcess }
            $result = Get-ProcessIdentityProbe -ProcessId 12345 -ProcessLookup $lookup
            if (-not $result.Exists) { throw 'Successful lookup was misclassified as vanished after identity read failure.' }
            if ($result.IdentityReadable) { throw 'Throwing identity accessor was reported readable.' }
            if ($result.Error -notlike '*synthetic identity access denied*') { throw 'Identity read error was not retained.' }
            """;

        var result = await RunPowerShellCommandAsync(command, RunnerScript);

        Assert.True(result.ExitCode == 0, result.Output);
    }

    [Fact]
    public async Task NightlyProjectNormalCompletionPublishesProjectIdentifiableReceipt()
    {
        using var directory = new TempDirectory();
        var receipt = Path.Combine(directory.Path, "receipt-Example.Tests.json");
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            foreach ($name in @('Invoke-BoundedCommand', 'Write-ImmutableJson', 'Write-NightlyProjectReceipt')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Runner function was not found: $name" }
                Invoke-Expression $function.Extent.Text
            }
            $repoRoot = (Get-Location).Path
            $result = Invoke-BoundedCommand -FilePath 'pwsh' -Arguments @('-NoProfile', '-Command', 'Write-Output completed') -TimeoutMilliseconds 5000
            Write-NightlyProjectReceipt -Path $env:VALIDATION_TEST_ARG_1 -ProjectPath 'tests/Example.Tests/Example.Tests.csproj' -Result $result
            """;

        var result = await RunPowerShellCommandAsync(command, RunnerScript, receipt);

        Assert.True(result.ExitCode == 0, result.Output);
        var evidence = JsonNode.Parse(await File.ReadAllTextAsync(receipt))!.AsObject();
        Assert.Equal("success", evidence["status"]!.GetValue<string>());
        Assert.Equal(0, evidence["exitCode"]!.GetValue<int>());
        Assert.False(evidence["timedOut"]!.GetValue<bool>());
        Assert.True(evidence["processTreeExited"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NightlyRunnerTimesOutNamedProjectThenAttemptsAndFinalizesNextProject()
    {
        using var directory = new TempDirectory();
        var fakeDotnet = Path.Combine(directory.Path, "fake-dotnet.ps1");
        var descendantMarker = Path.Combine(directory.Path, "descendant-survived.txt");
        var secondProjectMarker = Path.Combine(directory.Path, "second-project-attempted.txt");
        await File.WriteAllTextAsync(fakeDotnet, """
            $projectPath = $args[1]
            if ($projectPath -like '*First.Tests.csproj') {
                $child = "Start-Sleep -Seconds 2; [IO.File]::WriteAllText('$($env:VALIDATION_DESCENDANT_MARKER.Replace("'", "''"))', 'survived')"
                Start-Process pwsh -ArgumentList @('-NoProfile', '-Command', $child)
                Start-Sleep -Seconds 30
            }
            [IO.File]::WriteAllText($env:VALIDATION_SECOND_PROJECT_MARKER, $projectPath)
            exit 0
            """);
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            foreach ($name in @(
                'Get-ProcessTreeSnapshot', 'Get-ProcessIdentityProbe', 'Wait-ProcessTreeExit', 'Invoke-BoundedCommand',
                'Write-ImmutableJson', 'Write-NightlyProjectReceipt', 'Invoke-NativeCommand',
                'Invoke-NightlyValidation')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Runner function was not found: $name" }
                Invoke-Expression $function.Extent.Text
            }
            $repoRoot = (Get-Location).Path
            $nightlyExpectedProjects = @('tests/First.Tests/First.Tests.csproj', 'tests/Second.Tests/Second.Tests.csproj')
            $serializedNightlyExpectedProjects = ConvertTo-Json -InputObject $nightlyExpectedProjects -Compress
            $nightlyFilter = 'Issue158Lane!=operator'
            $partitionToken = 'test'
            $nightlyProjectTimeoutSeconds = 1
            $validationContract = $env:VALIDATION_TEST_ARG_2
            $env:VALIDATION_DESCENDANT_MARKER = $env:VALIDATION_TEST_ARG_4
            $env:VALIDATION_SECOND_PROJECT_MARKER = $env:VALIDATION_TEST_ARG_5
            $contractFailed = $false
            try {
                Invoke-NightlyValidation -ResultsDirectory $env:VALIDATION_TEST_ARG_3 `
                    -CommandFilePath 'pwsh' `
                    -CommandArgumentPrefix @('-NoProfile', '-File', $env:VALIDATION_TEST_ARG_1)
            }
            catch {
                $contractFailed = $true
            }
            if (-not $contractFailed) { throw 'Timed-out project evidence was accepted.' }
            """;
        var results = Path.Combine(directory.Path, "results");

        var result = await RunPowerShellCommandAsync(
            command,
            RunnerScript,
            fakeDotnet,
            ContractScript,
            results,
            descendantMarker,
            secondProjectMarker);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.False(File.Exists(descendantMarker));
        Assert.Contains("Second.Tests.csproj", await File.ReadAllTextAsync(secondProjectMarker), StringComparison.Ordinal);
        var first = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(results, "receipt-First.Tests.json")))!.AsObject();
        var second = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(results, "receipt-Second.Tests.json")))!.AsObject();
        Assert.Contains("tests/First.Tests/First.Tests.csproj", result.Output, StringComparison.Ordinal);
        Assert.Equal("timeout", first["status"]!.GetValue<string>());
        Assert.Equal("tests/First.Tests/First.Tests.csproj", first["projectPath"]!.GetValue<string>());
        Assert.True(first["processTreeExited"]!.GetValue<bool>());
        Assert.Equal("success", second["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task NightlyRunnerFinalizesLaterProjectAfterCommandStartException()
    {
        using var directory = new TempDirectory();
        var secondProjectMarker = Path.Combine(directory.Path, "second-project-attempted.txt");
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            foreach ($name in @('Write-ImmutableJson', 'Write-NightlyProjectReceipt', 'Invoke-NativeCommand', 'Invoke-NightlyValidation')) {
                $function = $ast.Find({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true)
                if ($null -eq $function) { throw "Runner function was not found: $name" }
                Invoke-Expression $function.Extent.Text
            }
            function Invoke-BoundedCommand {
                param([string]$FilePath, [string[]]$Arguments, [int]$TimeoutMilliseconds)
                $projectPath = $Arguments | Where-Object { $_ -like '*.csproj' } | Select-Object -First 1
                if ($projectPath -like '*First.Tests.csproj') { throw 'synthetic command start failure' }
                [IO.File]::WriteAllText($env:VALIDATION_TEST_ARG_3, $projectPath)
                return [pscustomobject]@{
                    ExitCode = 0; TimedOut = $false; ProcessExited = $true; ProcessTreeExited = $true
                    CapturedProcessCount = 1; SnapshotComplete = $true; SnapshotError = $null
                    ElapsedSeconds = 0; Output = ''
                }
            }
            $repoRoot = (Get-Location).Path
            $nightlyExpectedProjects = @('tests/First.Tests/First.Tests.csproj', 'tests/Second.Tests/Second.Tests.csproj')
            $serializedNightlyExpectedProjects = ConvertTo-Json -InputObject $nightlyExpectedProjects -Compress
            $nightlyFilter = 'Issue158Lane!=operator'
            $partitionToken = 'test'
            $nightlyProjectTimeoutSeconds = 1
            $validationContract = $env:VALIDATION_TEST_ARG_1
            try {
                Invoke-NightlyValidation -ResultsDirectory $env:VALIDATION_TEST_ARG_2 -CommandFilePath 'synthetic-dotnet' -CommandArgumentPrefix @()
            } catch { }
            exit 0
            """;
        var results = Path.Combine(directory.Path, "results");

        var result = await RunPowerShellCommandAsync(command, RunnerScript, ContractScript, results, secondProjectMarker);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("Second.Tests.csproj", await File.ReadAllTextAsync(secondProjectMarker), StringComparison.Ordinal);
        var first = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(results, "receipt-First.Tests.json")))!.AsObject();
        var second = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(results, "receipt-Second.Tests.json")))!.AsObject();
        Assert.Equal("failure", first["status"]!.GetValue<string>());
        Assert.False(first["snapshotComplete"]!.GetValue<bool>());
        Assert.Contains("synthetic command start failure", first["snapshotError"]!.GetValue<string>());
        Assert.Equal("success", second["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task RunnerImmutableJsonWriterRejectsOverwriteAndPreservesOriginalBytes()
    {
        using var directory = new TempDirectory();
        var command = """
            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($env:VALIDATION_TEST_ARG_0, [ref]$tokens, [ref]$errors)
            $function = $ast.Find({
                param($node)
                $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -eq 'Write-ImmutableJson'
            }, $true)
            if ($null -eq $function) { throw 'Write-ImmutableJson was not found.' }
            Invoke-Expression $function.Extent.Text
            foreach ($name in @('manifest.json', 'receipt.json')) {
                $path = Join-Path $env:VALIDATION_TEST_ARG_1 $name
                Write-ImmutableJson -Path $path -Value ([ordered]@{ status = 'original' })
                $before = [System.IO.File]::ReadAllBytes($path)
                $rejected = $false
                try { Write-ImmutableJson -Path $path -Value ([ordered]@{ status = 'replacement' }) }
                catch { $rejected = $true }
                $after = [System.IO.File]::ReadAllBytes($path)
                if (-not $rejected -or -not [System.Linq.Enumerable]::SequenceEqual[byte]($before, $after)) {
                    throw 'Immutable JSON overwrite was not rejected without changing bytes.'
                }
            }
            """;

        var result = await RunPowerShellCommandAsync(command, RunnerScript, directory.Path);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Contains("original", await File.ReadAllTextAsync(Path.Combine(directory.Path, "manifest.json")));
        Assert.Contains("original", await File.ReadAllTextAsync(Path.Combine(directory.Path, "receipt.json")));
        foreach (var name in new[] { "manifest.json", "receipt.json" })
            Assert.False(File.ReadAllBytes(Path.Combine(directory.Path, name)).AsSpan().StartsWith(Encoding.UTF8.Preamble));
    }

    [Fact]
    public async Task ShardSuccessReceiptIsValidatedBeforeItsSingleImmutableWrite()
    {
        var source = File.ReadAllText(RunnerScript);
        var shardStart = source.IndexOf("function Invoke-ShardPhase", StringComparison.Ordinal);
        var aggregateStart = source.IndexOf("function Invoke-AggregatePhase", StringComparison.Ordinal);
        var shard = source[shardStart..aggregateStart];
        var validation = shard.IndexOf(
            "Assert-PhaseCommand -Result $contract -Description \"Shard evidence validation ($ShardId)\"",
            StringComparison.Ordinal);
        var successWrite = shard.IndexOf(
            "Write-ImmutableJson -Path $receiptPath -Value $successReceipt",
            StringComparison.Ordinal);

        Assert.True(validation >= 0, "Shard contract validation was not found.");
        Assert.True(successWrite > validation, "Success receipt must be written only after contract validation.");
        var shardReserve = shard.LastIndexOf(
            "Assert-PhaseFinalizationReserve -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds",
            successWrite,
            StringComparison.Ordinal);
        Assert.True(shardReserve > validation && shardReserve < successWrite,
            "Shard must reserve finalization time after validation and immediately before its immutable receipt.");
        var bindingTry = shard.IndexOf("try {", StringComparison.Ordinal);
        var manifestBinding = shard.IndexOf("ConvertFrom-Json -Depth 100", StringComparison.Ordinal);
        var candidateBinding = shard.IndexOf("Completion manifest candidate SHA does not match HEAD", StringComparison.Ordinal);
        var unknownBinding = shard.IndexOf("Unknown Completion shard ID", StringComparison.Ordinal);
        Assert.True(bindingTry >= 0 && manifestBinding > bindingTry && candidateBinding > bindingTry && unknownBinding > bindingTry,
            "Manifest parsing, candidate binding, and unknown shard rejection must be covered by failure-receipt handling.");
        Assert.Equal(1, CountOccurrences(shard, "Write-ImmutableJson -Path $receiptPath -Value $successReceipt"));
        Assert.Contains("Write-ShardFailureReceipt -Path $receiptPath -Value $failureReceipt", shard, StringComparison.Ordinal);
        Assert.Contains("status = if ($timedOut) { 'timeout' } else { 'failure' }", shard, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content -LiteralPath $receiptPath", shard, StringComparison.Ordinal);
        Assert.Contains("Write-ImmutableJson -Path $resolvedManifest -Value $manifest", source, StringComparison.Ordinal);
        Assert.Contains("'discovery-receipt.json'", source, StringComparison.Ordinal);
        Assert.Contains("'aggregate-receipt.json'", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Set-Content -LiteralPath $resolvedManifest", source, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(
            source,
            "Assert-PhaseFinalizationReserve -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds"));
        Assert.Contains(
            "$phaseBudgetSeconds - $phaseFinalizationReserveSeconds - $phaseStopwatch.Elapsed.TotalSeconds",
            source,
            StringComparison.Ordinal);
        Assert.Equal(5, CountOccurrences(
            source,
            "Assert-PhaseCompletedWithinBudget -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds"));
        AssertPhasePostCheckOrdering(
            source,
            "function Invoke-DiscoveryPhase",
            "function Invoke-ShardPhase",
            "'discovery-receipt.json'",
            "Write-Output \"validation_manifest=$resolvedManifest\"");
        AssertPhasePostCheckOrdering(
            source,
            "function Invoke-ShardPhase",
            "function Invoke-AggregatePhase",
            "Write-ImmutableJson -Path $receiptPath -Value $successReceipt",
            "Write-Output \"validation_results=$resultsRoot\"");
        AssertPhasePostCheckOrdering(
            source,
            "function Invoke-AggregatePhase",
            "if ($Lane -eq 'Completion' -and -not [string]::IsNullOrWhiteSpace($Phase))",
            "'aggregate-receipt.json'",
            "Write-Output \"validation_results=$resultsRoot\"");

        using var directory = new TempDirectory();
        var manifest = CreateManifest(directory.Path, ("s-a", new[] { Row("Example.Tests.A.Fact") }));
        var results = Directory.CreateDirectory(Path.Combine(directory.Path, "results")).FullName;
        WriteTrx(results, ("Example.Tests.A.Fact", "Passed"));
        var finalReceipt = Path.Combine(directory.Path, "receipt-s-a.json");
        var invalidProposedSuccess = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            shardId = "s-a",
            candidateSha = "candidate",
            authorityDigest = "authority",
            manifestDigest = "manifest",
            status = "success",
            exitCode = 0,
            timedOut = false,
            actualCount = 1,
            actualHash = "wrong",
        });
        var result = await RunContractAsync(
            "-Mode", "Shard",
            "-ManifestPath", manifest,
            "-ShardId", "s-a",
            "-ReceiptPath", finalReceipt,
            "-ReceiptJson", invalidProposedSuccess,
            "-ResultsDirectory", results);

        Assert.NotEqual(0, result.ExitCode);
        Assert.False(File.Exists(finalReceipt), "Contract failure must not materialize a false success receipt.");
    }

    [Fact]
    public async Task RunnerOwnsTheExactShardPrerequisitesAndBuildsThemBeforeTheTarget()
    {
        var source = File.ReadAllText(RunnerScript);
        var mapStart = source.IndexOf("$shardPrerequisiteProjects = [ordered]@{", StringComparison.Ordinal);
        Assert.True(mapStart >= 0, "Runner-owned prerequisite map was not found.");
        var mapEnd = source.IndexOf("}\n", mapStart, StringComparison.Ordinal);
        Assert.True(mapEnd > mapStart, "Runner-owned prerequisite map was not terminated.");
        var map = source[mapStart..mapEnd];

        Assert.Contains(
            "'s01' = @('src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj')",
            map,
            StringComparison.Ordinal);
        foreach (var id in Enumerable.Range(2, 9).Select(number => $"s{number:00}"))
            Assert.Contains($"'{id}' = @()", map, StringComparison.Ordinal);

        Assert.Contains("shardPrerequisiteProjects = $shardPrerequisiteProjects", source, StringComparison.Ordinal);
        Assert.Contains("prerequisiteProjects = @($shardPrerequisiteProjects[$id])", source, StringComparison.Ordinal);

        var shardStart = source.IndexOf("function Invoke-ShardPhase", StringComparison.Ordinal);
        var aggregateStart = source.IndexOf("function Invoke-AggregatePhase", StringComparison.Ordinal);
        var shard = source[shardStart..aggregateStart];
        var prerequisiteValidation = shard.IndexOf("Assert-ShardPrerequisiteProjects", StringComparison.Ordinal);
        var prerequisiteRestore = shard.IndexOf(
            "@('restore', $prerequisiteProjectPath)",
            StringComparison.Ordinal);
        var prerequisiteBuild = shard.IndexOf(
            "@('build', $prerequisiteProjectPath, '--no-restore')",
            StringComparison.Ordinal);
        var targetRestore = shard.IndexOf("@('restore', $projectPath)", StringComparison.Ordinal);

        Assert.True(prerequisiteValidation >= 0, "Shard prerequisite binding validation was not found.");
        Assert.True(prerequisiteRestore > prerequisiteValidation, "Prerequisite restore must follow exact binding validation.");
        Assert.True(prerequisiteBuild > prerequisiteRestore, "Prerequisite build must follow its restore.");
        Assert.True(targetRestore > prerequisiteBuild, "Target restore must follow every prerequisite build.");
        Assert.Contains("[IO.Path]::GetFullPath", source, StringComparison.Ordinal);
        Assert.Contains("[IO.Path]::IsPathRooted", source, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Exists", source, StringComparison.Ordinal);

        const string localMonitorProject =
            "src/CopilotAgentObservability.LocalMonitor/CopilotAgentObservability.LocalMonitor.csproj";
        var validator = await File.ReadAllTextAsync(ContractScript);
        Assert.DoesNotContain(localMonitorProject, validator, StringComparison.Ordinal);
        Assert.Contains("ExpectedPrerequisiteProjectsJson", validator, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(source, "'-ExpectedPrerequisiteProjectsJson'"));

        using var directory = new TempDirectory();
        var required = Enumerable.Range(1, 10).Select(number => $"s{number:00}").ToArray();
        var rows = required.ToDictionary(
            id => id,
            id => new[] { Row($"Example.Tests.{id}.Fact", projectPath: $"tests/{id}/{id}.csproj") });
        var expectedPrerequisites = required.ToDictionary(
            id => id,
            id => id == "s01" ? new[] { localMonitorProject } : Array.Empty<string>());
        var expectedJson = JsonSerializer.Serialize(expectedPrerequisites);
        var baselineRows = rows.Values.SelectMany(value => value).ToArray();
        var exactShards = required
            .Select(id => Shard(id, rows[id], expectedPrerequisites[id]))
            .ToList();
        var manifest = WriteManifest(directory.Path, required, baselineRows, exactShards);
        var accepted = await RunContractAsync(
            "-Mode", "Manifest",
            "-ManifestPath", manifest,
            "-ExpectedShardIds", string.Join(';', required),
            "-ExpectedPrerequisiteProjectsJson", expectedJson);
        Assert.True(accepted.ExitCode == 0, accepted.Output);

        var nullAuthority = await RunContractAsync(
            "-Mode", "Manifest",
            "-ManifestPath", manifest,
            "-ExpectedShardIds", string.Join(';', required),
            "-ExpectedPrerequisiteProjectsJson", "null");
        Assert.NotEqual(0, nullAuthority.ExitCode);

        foreach (var defect in required)
        {
            var mismatchedShards = required
                .Select(id => Shard(id, rows[id], expectedPrerequisites[id]))
                .ToList();
            var defectIndex = Array.IndexOf(required, defect);
            mismatchedShards[defectIndex] = defect == "s01"
                ? Shard(defect, rows[defect])
                : Shard(
                    defect,
                    rows[defect],
                    new[] { "src/CopilotAgentObservability.ConfigCli/CopilotAgentObservability.ConfigCli.csproj" });
            manifest = WriteManifest(directory.Path, required, baselineRows, mismatchedShards);
            var rejected = await RunContractAsync(
                "-Mode", "Manifest",
                "-ManifestPath", manifest,
                "-ExpectedShardIds", string.Join(';', required),
                "-ExpectedPrerequisiteProjectsJson", expectedJson);
            Assert.NotEqual(0, rejected.ExitCode);
            Assert.Contains("prerequisite", rejected.Output, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void WriteTrx(string directory, params (string Fqn, string Outcome)[] rows) =>
        WriteTrx(directory, rows.Select(row => (row.Fqn, row.Fqn, row.Outcome)).ToArray());

    private static void WriteTrx(
        string directory,
        params (string Fqn, string TestName, string Outcome)[] rows)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var results = new XElement(ns + "Results");
        var definitions = new XElement(ns + "TestDefinitions");
        foreach (var row in rows)
        {
            var testId = Guid.NewGuid().ToString();
            var executionId = Guid.NewGuid().ToString();
            var separator = row.Fqn.LastIndexOf('.');
            var className = row.Fqn[..separator];
            var methodName = row.Fqn[(separator + 1)..];
            results.Add(new XElement(
                ns + "UnitTestResult",
                new XAttribute("executionId", executionId),
                new XAttribute("testId", testId),
                new XAttribute("testName", row.TestName),
                new XAttribute("outcome", row.Outcome)));
            definitions.Add(new XElement(
                ns + "UnitTest",
                new XAttribute("name", methodName),
                new XAttribute("id", testId),
                new XElement(ns + "Execution", new XAttribute("id", executionId)),
                new XElement(
                    ns + "TestMethod",
                    new XAttribute("className", className),
                    new XAttribute("name", methodName))));
        }

        new XDocument(new XElement(ns + "TestRun", results, definitions))
            .Save(System.IO.Path.Combine(directory, "critical-smoke.trx"));
    }

    private static void WriteProjectTrx(string directory, string fileName, string storage, string fqn)
    {
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var testId = Guid.NewGuid().ToString();
        var executionId = Guid.NewGuid().ToString();
        var separator = fqn.LastIndexOf('.');
        new XDocument(new XElement(
            ns + "TestRun",
            new XElement(ns + "Results", new XElement(
                ns + "UnitTestResult",
                new XAttribute("executionId", executionId),
                new XAttribute("testId", testId),
                new XAttribute("testName", fqn),
                new XAttribute("outcome", "Passed"))),
            new XElement(ns + "TestDefinitions", new XElement(
                ns + "UnitTest",
                new XAttribute("name", fqn[(separator + 1)..]),
                new XAttribute("id", testId),
                new XAttribute("storage", storage),
                new XElement(ns + "Execution", new XAttribute("id", executionId)),
                new XElement(
                    ns + "TestMethod",
                    new XAttribute("className", fqn[..separator]),
                    new XAttribute("name", fqn[(separator + 1)..]))))))
            .Save(System.IO.Path.Combine(directory, fileName));
    }

    private static void WriteNightlyReceipt(
        string directory,
        string projectPath,
        string status,
        int exitCode,
        bool timedOut,
        bool processExited,
        bool? processTreeExited = null)
    {
        var identity = Path.GetFileNameWithoutExtension(projectPath);
        File.WriteAllText(
            Path.Combine(directory, $"receipt-{identity}.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                projectPath,
                projectIdentity = identity,
                status,
                exitCode,
                timedOut,
                processExited,
                processTreeExited = processTreeExited ?? processExited,
                capturedProcessCount = 1,
                snapshotComplete = true,
                snapshotError = (string?)null,
            }));
    }

    private static object Row(
        string fqn,
        string? testName = null,
        int occurrence = 1,
        string projectPath = "tests/s-a/s-a.csproj") => new
    {
        projectPath,
        fqn,
        testName = testName ?? fqn,
        occurrence,
        authorityIdentity = $"{projectPath}|{fqn}|{occurrence}",
    };

    private static object Shard(string id, object[] rows, string[]? prerequisiteProjects = null) => new
    {
        id,
        projectPath = $"tests/{id}/{id}.csproj",
        filter = "ValidationLane!=Nightly",
        prerequisiteProjects = prerequisiteProjects ?? Array.Empty<string>(),
        expectedCount = rows.Length,
        expectedHash = RowHash(rows),
        expectedRows = rows,
    };

    private static string CreateManifest(
        string directory,
        params (string Id, object[] Rows)[] shards) =>
        WriteManifest(
            directory,
            shards.Select(shard => shard.Id).ToArray(),
            shards.SelectMany(shard => shard.Rows).ToArray(),
            shards.Select(shard => Shard(shard.Id, shard.Rows)).ToList());

    private static string WriteManifest(
        string directory,
        string[] requiredShardIds,
        object[] baselineRows,
        List<object> shards)
    {
        var path = Path.Combine(directory, "manifest.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            candidateSha = "candidate",
            authorityDigest = "authority",
            manifestDigest = "manifest",
            requiredShardIds,
            baselineCount = baselineRows.Length,
            baselineHash = RowHash(baselineRows),
            baselineRows,
            shards,
        }));
        return path;
    }

    private static string RowHash(IEnumerable<object> rows)
    {
        var identities = rows
            .Select(row => JsonSerializer.SerializeToElement(row).GetProperty("authorityIdentity").GetString()!)
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', identities))))
            .ToLowerInvariant();
    }

    private static string WriteReceipt(
        string directory,
        string shardId,
        string status,
        int exitCode,
        bool timedOut,
        string candidateSha = "candidate",
        string manifestDigest = "manifest",
        int actualCount = 1,
        string? actualHash = null,
        string suffix = "")
    {
        var path = Path.Combine(directory, $"receipt-{shardId}{suffix}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            shardId,
            candidateSha,
            authorityDigest = "authority",
            manifestDigest,
            status,
            exitCode,
            timedOut,
            actualCount,
            actualHash = actualHash ?? RowHash(new[] { Row("Example.Tests.A.Fact") }),
        }));
        return path;
    }

    private static void WriteShardArtifact(string artifacts, string shardId, string fqn, string suffix = "")
    {
        var directory = Directory.CreateDirectory(Path.Combine(
            artifacts,
            $"completion-shard-candidate-1-{shardId}{suffix}"));
        var row = Row(fqn, projectPath: $"tests/{shardId}/{shardId}.csproj");
        WriteReceipt(
            directory.FullName,
            shardId,
            "success",
            0,
            false,
            actualHash: RowHash(new[] { row }),
            suffix: suffix);
        WriteTrx(directory.FullName, (fqn, fqn, "Passed"));
    }

    private static string WriteDependencies(string directory, params (string ShardId, string Result)[] rows)
    {
        var path = Path.Combine(directory, "dependencies.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rows.ToDictionary(row => row.ShardId, row => row.Result)));
        return path;
    }

    private static string WriteDiscoveryRows(
        string directory,
        params (string Fqn, string TestName)[] rows)
    {
        var path = Path.Combine(directory, "discovery-rows.json");
        File.WriteAllText(path, JsonSerializer.Serialize(rows.Select(row => new
        {
            fqn = row.Fqn,
            testName = row.TestName,
        })));
        return path;
    }

    private static async Task<ProcessResult> RunContractAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(ContractScript);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await standardOutput + await standardError);
    }

    private static async Task<ProcessResult> RunPowerShellCommandAsync(
        string command,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        for (var index = 0; index < arguments.Length; index++)
            startInfo.Environment[$"VALIDATION_TEST_ARG_{index}"] = arguments[index];
        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await standardOutput + await standardError);
    }

    private static async Task<ProcessResult> RunDiscoveryPreflightAsync(string root, bool failPolicyTests)
    {
        var scripts = Directory.CreateDirectory(Path.Combine(root, "scripts", "test")).FullName;
        var projectDirectory = Directory.CreateDirectory(Path.Combine(root, "src", "Probe")).FullName;
        File.Copy(RunnerScript, Path.Combine(scripts, "run-validation.ps1"));
        File.WriteAllText(
            Path.Combine(scripts, "test-repository-policy.ps1"),
            $"Add-Content -LiteralPath $env:VALIDATION_PREFLIGHT_LOG -Value 'policy-tests'\nexit {(failPolicyTests ? 23 : 0)}\n");
        File.WriteAllText(
            Path.Combine(scripts, "assert-repository-policy.ps1"),
            "Add-Content -LiteralPath $env:VALIDATION_PREFLIGHT_LOG -Value 'policy-guard'\nexit 0\n");
        File.WriteAllText(
            Path.Combine(scripts, "assert-validation-contract.ps1"),
            "throw 'The contract must not run after the synthetic solution inventory mismatch.'\n");
        File.WriteAllText(
            Path.Combine(root, "CopilotAgentObservability.slnx"),
            "<Solution><Project Path=\"src/Probe/Probe.csproj\" /></Solution>\n");
        File.WriteAllText(
            Path.Combine(projectDirectory, "Probe.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <Target Name="RecordBuild" BeforeTargets="Build">
                <WriteLinesToFile File="$(VALIDATION_PREFLIGHT_LOG)" Lines="build" Overwrite="false" />
              </Target>
            </Project>
            """);

        var startInfo = new ProcessStartInfo("pwsh")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(scripts, "run-validation.ps1"));
        startInfo.ArgumentList.Add("-Lane");
        startInfo.ArgumentList.Add("Completion");
        startInfo.ArgumentList.Add("-Phase");
        startInfo.ArgumentList.Add("Discovery");
        startInfo.ArgumentList.Add("-OutputDirectory");
        startInfo.ArgumentList.Add("artifacts/results");
        startInfo.Environment["VALIDATION_PREFLIGHT_LOG"] = Path.Combine(root, "preflight.log");
        using var process = Process.Start(startInfo)!;
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await standardOutput + await standardError);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        for (var index = 0; (index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0; index += search.Length)
            count++;
        return count;
    }

    private static void AssertPhasePostCheckOrdering(
        string source,
        string phaseStart,
        string phaseEnd,
        string receiptMarker,
        string finalOutputMarker)
    {
        var start = source.IndexOf(phaseStart, StringComparison.Ordinal);
        var end = source.IndexOf(phaseEnd, start + phaseStart.Length, StringComparison.Ordinal);
        var phase = source[start..end];
        var receipt = phase.IndexOf(receiptMarker, StringComparison.Ordinal);
        var output = phase.LastIndexOf(finalOutputMarker, StringComparison.Ordinal);
        var postCheck = phase.LastIndexOf(
            "Assert-PhaseCompletedWithinBudget -ElapsedSeconds $phaseStopwatch.Elapsed.TotalSeconds",
            StringComparison.Ordinal);
        Assert.True(receipt >= 0 && output > receipt && postCheck > output,
            $"Phase post-finalization budget check must follow receipt and required output: {phaseStart}");
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static string ContractScript => Path.Combine(
        RepositoryRoot,
        "scripts", "test", "assert-validation-contract.ps1");

    private static string RunnerScript => Path.Combine(
        RepositoryRoot,
        "scripts", "test", "run-validation.ps1");

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"validation-contract-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}

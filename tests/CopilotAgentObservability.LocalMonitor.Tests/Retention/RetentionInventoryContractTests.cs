using System.Diagnostics;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed partial class RetentionInventoryContractTests
{
    private const string BaseSha = "11d6c587903f6ea97026d815f608231efea08d65";
    private const string CandidateSha = "5c5540878a6731804084644d8a136be9ad748cf9";

    [Fact]
    public void FinalInventory_CoversEveryBaseToCandidateRawCallsite()
    {
        var root = FindRepositoryRoot();
        var baseline = File.ReadAllText(Path.Combine(root, "docs", "sprints", "issue-89-raw-read-callsite-inventory.md"));
        Assert.Contains(BaseSha, baseline, StringComparison.Ordinal);
        var baselinePaths = System.Text.RegularExpressions.Regex.Matches(baseline, @"src/[A-Za-z0-9_./-]+\.cs")
            .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);

        var manifestPath = Path.Combine(root, "docs", "sprints", "issue-89-final-retention-inventory.json");
        Assert.True(File.Exists(manifestPath), "The checked-in structured final retention inventory is required.");
        var manifest = JsonSerializer.Deserialize<InventoryManifest>(File.ReadAllText(manifestPath), JsonOptions);
        Assert.NotNull(manifest);
        Assert.Equal(BaseSha, manifest.BaseSha);
        Assert.Equal(CandidateSha, manifest.CandidateSha);

        var changedProductionPaths = GitDiff(root).Where(path => path.StartsWith("src/", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(changedProductionPaths.OrderBy(path => path, StringComparer.Ordinal),
            manifest.PathEntries.Select(entry => entry.Path).OrderBy(path => path, StringComparer.Ordinal));
        Assert.Equal(manifest.PathEntries.Count, manifest.PathEntries.Select(entry => entry.Path).Distinct(StringComparer.Ordinal).Count());
        Assert.All(baselinePaths, path => Assert.Contains(manifest.PathEntries, entry => entry.Path == path));

        var classifications = new[] { "required_cleanup", "retained_by_policy", "not_applicable" };
        var adapters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["session_event_content"] = "SessionEventContentRetentionAdapter", ["raw_record"] = "RawRecordRetentionAdapter",
            ["analysis_run_raw"] = "MonitorAnalysisRetentionAdapter", ["sensitive_bundle"] = "SensitiveBundleRetentionAdapter",
            ["analysis_sdk_directory"] = "AnalysisSdkDirectoryRetentionAdapter"
        };
        Assert.All(manifest.PathEntries.SelectMany(entry => entry.Contracts), contract =>
        {
            Assert.Contains(contract.Classification, classifications);
            Assert.NotEmpty(contract.Identity); Assert.NotEmpty(contract.TimestampAuthority); Assert.NotEmpty(contract.PolicyOrBoundary); Assert.NotEmpty(contract.TestEvidence);
            if (contract.Classification == "required_cleanup") Assert.Equal(adapters[contract.StoreKind], contract.Adapter);
            else
                Assert.True(string.IsNullOrWhiteSpace(contract.Adapter));
            foreach (var testName in contract.TestEvidence.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(root, "tests"), testName, SearchOption.AllDirectories));
            Assert.DoesNotContain("exact boundary identity", contract.Identity, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("support/control surface", contract.TimestampAuthority, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("support/control surface", contract.PolicyOrBoundary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RetentionContractTests.cs", contract.TestEvidence, StringComparison.Ordinal);
        });

        Assert.DoesNotContain(manifest.PathEntries.SelectMany(entry => entry.Contracts), contract => contract.Classification == "blocked" || contract.GateBypass || contract.UnregisteredCreator);
        Assert.Contains(manifest.PathEntries.Single(entry => entry.Path.EndsWith("AtomicDiagnosisOutputPublisher.cs", StringComparison.Ordinal)).Contracts, contract => contract.Classification == "not_applicable");
        Assert.Contains(manifest.PathEntries.Single(entry => entry.Path.EndsWith("SensitiveBundleWriter.cs", StringComparison.Ordinal)).Contracts, contract => contract.StoreKind == "sensitive_bundle" && contract.Classification == "required_cleanup");
        AssertStoreKinds(manifest, "SessionRoutes.cs", "session_event_content", "raw_record");
        AssertContainsStoreKinds(manifest, "SqliteSessionStore.cs", "session_event_content");
        AssertStoreKinds(manifest, "RawTelemetryStore.cs", "raw_record");
        AssertStoreKinds(manifest, "ClaudeDoctorFactCollector.cs", "raw_record");
        AssertStoreKinds(manifest, "ClaudeDoctorCandidateObserver.cs", "raw_record");
        AssertStoreKinds(manifest, "SqliteSessionOtelEnricher.cs", "raw_record");
        AssertStoreKinds(manifest, "DotNetCopilotRawAnalysisRunner.cs", "raw_record", "analysis_run_raw", "analysis_sdk_directory");
        AssertStoreKinds(manifest, "SqliteMonitorAnalysisStore.cs", "analysis_run_raw");
        AssertStoreKinds(manifest, "MonitorHost.cs", "raw_record", "analysis_run_raw");
        AssertStoreKinds(manifest, "ProjectionWorker.cs", "raw_record");
        AssertStoreKinds(manifest, "SqliteSessionOtelEnricher.cs", "raw_record");
        AssertStoreKinds(manifest, "RetentionAdapterRegistry.cs", "session_event_content", "raw_record", "analysis_run_raw", "sensitive_bundle", "analysis_sdk_directory");
        AssertStoreKinds(manifest, "RetentionCatalogStore.SqliteDeletion.cs", "session_event_content", "raw_record", "analysis_run_raw");
        AssertStoreKinds(manifest, "RetentionCatalogStore.FileCapture.cs", "sensitive_bundle", "analysis_sdk_directory");
        AssertStoreKinds(manifest, "RetentionCatalogStore.FileDeletion.cs", "sensitive_bundle", "analysis_sdk_directory");
        AssertStoreKinds(manifest, "MonitorSchemaMigrator.cs", "raw_record");
        AssertStoreKinds(manifest, "RawTelemetryRecordSql.cs", "raw_record");
        AssertStoreKinds(manifest, "RetentionCleanupWorker.cs", "session_event_content", "raw_record", "analysis_run_raw", "sensitive_bundle", "analysis_sdk_directory");
        AssertStoreKinds(manifest, "RetentionReadGate.cs", "session_event_content", "raw_record", "analysis_run_raw");
        AssertStoreKinds(manifest, "RetentionReadGrant.cs", "session_event_content", "raw_record", "analysis_run_raw");
        AssertStoreKinds(manifest, "RetentionSqliteMaintenance.cs", "session_event_content", "raw_record", "analysis_run_raw");
        AssertContainsStoreKinds(manifest, "RetentionOwnershipReceipt.cs", "catalog_metadata");
        AssertContainsStoreKinds(manifest, "RetentionSchemaMigrator.cs", "catalog_metadata");
    }

    private static IReadOnlySet<string> GitDiff(string root)
    {
        var start = new ProcessStartInfo("git", $"diff --name-only {BaseSha} {CandidateSha}") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start);
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git diff is required for the inventory contract: {error}");
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }
    private static void AssertStoreKinds(InventoryManifest manifest, string suffix, params string[] expected) =>
        Assert.Equal(expected.OrderBy(value => value, StringComparer.Ordinal), manifest.PathEntries.Single(entry => entry.Path.EndsWith(suffix, StringComparison.Ordinal)).Contracts.Select(contract => contract.StoreKind).OrderBy(value => value, StringComparer.Ordinal));

    private static void AssertContainsStoreKinds(InventoryManifest manifest, string suffix, params string[] expected)
    {
        var actual = manifest.PathEntries.Single(entry => entry.Path.EndsWith(suffix, StringComparison.Ordinal)).Contracts.Select(contract => contract.StoreKind).ToHashSet(StringComparer.Ordinal);
        Assert.All(expected, storeKind => Assert.Contains(storeKind, actual));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        throw new Xunit.Sdk.XunitException("Repository root could not be located.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record InventoryManifest(string BaseSha, string CandidateSha, List<PathEntry> PathEntries);
    private sealed record PathEntry(string Path, List<InventoryContract> Contracts);
    private sealed record InventoryContract(string StoreKind, string Classification, string Identity, string TimestampAuthority, string PolicyOrBoundary, string? Adapter, string TestEvidence, bool GateBypass, bool UnregisteredCreator);
}

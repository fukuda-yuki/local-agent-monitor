using System.Diagnostics;
using System.Xml.Linq;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ValidationRunnerContractTests
{
    private const string RepositoryCompare =
        "CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1RepositoryComparePlaywrightTests.ImmutableCompareRendersNineSectionsRowsEvidenceAndResponsiveTableWithoutRecompute";
    private const string SessionExplorer =
        "CopilotAgentObservability.LocalMonitor.Tests.LocalMonitorV1SessionExplorerPlaywrightTests.ComparePreviewCreatesFromTransientOrderedCohortsAndNavigatesOnlyByServerLocation";

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

    private static void WriteTrx(string directory, params (string Fqn, string Outcome)[] rows)
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
                new XAttribute("testName", methodName),
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

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static string ContractScript => Path.Combine(
        RepositoryRoot,
        "scripts", "test", "assert-validation-contract.ps1");

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

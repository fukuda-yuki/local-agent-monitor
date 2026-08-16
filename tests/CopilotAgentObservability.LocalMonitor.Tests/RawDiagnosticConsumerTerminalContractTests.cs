namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RawDiagnosticConsumerTerminalContractTests
{
    [Theory]
    [InlineData("CopilotAgentObservability.ConfigCli/RawTelemetry/RawStoreLeaseReader.cs")]
    [InlineData("CopilotAgentObservability.ConfigCli/FirstTrace/ClaudeCode/ClaudeDoctorFactCollector.cs")]
    [InlineData("CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/Doctor/GitHubCopilotDoctorEvidenceAdapter.cs")]
    [InlineData("CopilotAgentObservability.Persistence.Sqlite/Doctor/ClaudeCode/ClaudeDoctorCandidateObserver.cs")]
    [InlineData("CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionOtelEnricher.cs")]
    [InlineData("CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs")]
    public void RawConsumer_UsesScopedReferenceAndTerminalCompletionWithoutDirectLeaseValue(string relativePath)
    {
        var source = File.ReadAllText(SourcePath(relativePath));

        Assert.Contains("AcquireValueReference()", source, StringComparison.Ordinal);
        Assert.Contains("TryCompleteWithoutRaw()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lease.Value", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lease?.Value", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryMetadataDiagnosticsLoader_IsAlreadyTerminalCorrect()
    {
        var source = File.ReadAllText(SourcePath(
            "CopilotAgentObservability.LocalMonitor/Diagnostics/RepositoryMetadataDiagnosticsLoader.cs"));

        Assert.Contains("AcquireValueReference()", source, StringComparison.Ordinal);
        Assert.Contains("TryCompleteWithoutRaw()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lease.Value", source, StringComparison.OrdinalIgnoreCase);
    }

    private static string SourcePath(string relativePath)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return Path.Combine(root, "src", relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}

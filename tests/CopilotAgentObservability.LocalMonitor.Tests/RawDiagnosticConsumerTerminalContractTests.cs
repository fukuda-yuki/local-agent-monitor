using System.Reflection;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class RawDiagnosticConsumerTerminalContractTests
{
    [Theory]
    [InlineData("CopilotAgentObservability.ConfigCli/FirstTrace/ClaudeCode/ClaudeDoctorFactCollector.cs")]
    [InlineData("CopilotAgentObservability.ConfigCli/Setup/Adapters/GitHubCopilot/Doctor/GitHubCopilotDoctorEvidenceAdapter.cs")]
    [InlineData("CopilotAgentObservability.Persistence.Sqlite/Doctor/ClaudeCode/ClaudeDoctorCandidateObserver.cs")]
    [InlineData("CopilotAgentObservability.Persistence.Sqlite/Sessions/SqliteSessionOtelEnricher.cs")]
    public void RawConsumer_UsesScopedReferenceAndTerminalCompletionWithoutDirectLeaseValue(string relativePath)
    {
        var source = File.ReadAllText(SourcePath(relativePath));

        Assert.Contains("AcquireValueReference()", source, StringComparison.Ordinal);
        Assert.Contains("TryCompleteWithoutRaw()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lease.Value", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lease?.Value", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CopilotAgentObservability.ConfigCli/RawTelemetry/RawStoreLeaseReader.cs")]
    [InlineData("CopilotAgentObservability.LocalMonitor/Analysis/DotNetCopilotRawAnalysisRunner.cs")]
    public void CallerVisibleRawOwner_UsesRawDerivedSealWithoutCompletion(string relativePath)
    {
        var source = File.ReadAllText(SourcePath(relativePath));

        Assert.Contains("AcquireValueReference()", source, StringComparison.Ordinal);
        Assert.Contains("TrySealRawResponse()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryCompleteWithoutRaw()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("lease.Value", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Lease?.Value", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(SqliteSessionOtelEnricher), "PreparedProjectedSpan")]
    [InlineData(typeof(ClaudeDoctorCandidateObserver), "PreparedObservation")]
    [InlineData(typeof(GitHubCopilotDoctorEvidenceAdapter), "EvidencePreparation")]
    public void CompletionConsumer_MaterializesOnlyRawFreeShapeBeforeTerminal(
        Type consumerType,
        string nestedTypeName)
    {
        var preparedType = consumerType.GetNestedType(
            nestedTypeName,
            BindingFlags.Public | BindingFlags.NonPublic);

        Assert.NotNull(preparedType);
        Assert.DoesNotContain(
            preparedType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            property => property.PropertyType == typeof(RawTelemetryRecord)
                || property.Name is "Raw" or "PayloadJson");
        Assert.DoesNotContain(
            preparedType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            field => field.FieldType == typeof(RawTelemetryRecord)
                || field.Name.Contains("Raw", StringComparison.Ordinal)
                || field.Name.Contains("PayloadJson", StringComparison.Ordinal));
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

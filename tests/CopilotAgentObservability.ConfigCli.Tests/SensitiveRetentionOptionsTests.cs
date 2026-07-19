using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class SensitiveRetentionOptionsTests
{
    public static TheoryData<string[], string> InvalidVectors => new()
    {
        { ["generate-diagnosis-candidates", "M", "--include-sensitive-content", "--retention-database", "D", "--json", "O"], "--include-sensitive-content requires --raw <raw-store.db|raw-otlp.json>." },
        { ["generate-diagnosis-candidates", "M", "--retention-database", "D", "--json", "O"], "--retention-database is valid only with --include-sensitive-content." },
        { ["generate-diagnosis-candidates", "M", "--raw", "R", "--include-sensitive-content", "--json", "O"], "--include-sensitive-content requires --retention-database <local-monitor.db>." },
        { ["generate-diagnosis-candidates", "M", "--raw", "R", "--include-sensitive-content", "--retention-database", "D", "--retention-database", "D2", "--json", "O"], "--retention-database may be specified only once." },
        { ["generate-diagnosis-candidates", "M", "--raw", "R", "--include-sensitive-content", "--retention-database", "--json", "O"], "--retention-database requires a Local Monitor SQLite database path." },
        { ["generate-diagnosis-candidates", "M", "--raw", "R", "--include-sensitive-content", "--retention-database", "D"], "generate-diagnosis-candidates requires --csv, --json, or both." },
        { ["generate-diagnosis-candidates", "M", "--retention-database", "D", "--include-sensitive-content", "--raw"], "--raw requires a raw store database or raw OTLP JSON file path." },
    };

    [Theory]
    [MemberData(nameof(InvalidVectors))]
    public void SensitiveOptions_MatchCompleteRetentionDatabaseMatrix(string[] args, string expectedError)
    {
        var result = DiagnosisCandidateOptions.Parse(args);

        Assert.Null(result.Options);
        Assert.Equal(expectedError, result.Error);
    }

    [Fact]
    public void SensitiveOptions_ParsesRetentionDatabaseAndKeepsParentInertOutsideSensitiveMode()
    {
        var sensitive = DiagnosisCandidateOptions.Parse(
            ["generate-diagnosis-candidates", "M", "--raw", "R", "--include-sensitive-content", "--retention-database", "D", "--sensitive-output-dir", "P", "--json", "O"]);
        var nonSensitive = DiagnosisCandidateOptions.Parse(
            ["generate-diagnosis-candidates", "M", "--sensitive-output-dir", "P", "--json", "O"]);

        Assert.Null(sensitive.Error);
        var sensitiveOptions = Assert.IsType<DiagnosisCandidateOptions>(sensitive.Options);
        Assert.Equal("D", sensitiveOptions.RetentionDatabasePath);
        Assert.Equal("P", sensitiveOptions.SensitiveOutputDir);
        Assert.Null(nonSensitive.Error);
        var nonSensitiveOptions = Assert.IsType<DiagnosisCandidateOptions>(nonSensitive.Options);
        Assert.Null(nonSensitiveOptions.RetentionDatabasePath);
        Assert.Equal("P", nonSensitiveOptions.SensitiveOutputDir);
    }

    [Fact]
    public void HelpText_DescribesTheRetentionDatabaseSensitiveSyntax()
    {
        Assert.Contains(
            "generate-diagnosis-candidates <measurements.csv|measurements.json> [--raw <raw-store.db|raw-otlp.json>] [--include-sensitive-content --retention-database <local-monitor.db> [--sensitive-output-dir <dir>]] [--csv <output.csv>] [--json <output.json>]",
            CliHelpText.Text);
    }
}

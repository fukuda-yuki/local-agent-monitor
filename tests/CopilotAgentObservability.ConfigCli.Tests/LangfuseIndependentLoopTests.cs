using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class LangfuseIndependentLoopTests
{
    [Fact]
    public void EndToEnd_RawStoreThroughHumanDecision_UsesSyntheticFixturesOnly()
    {
        using var tempDirectory = new TempDirectory();
        var measurementsPath = Path.Combine(tempDirectory.Path, "measurements.json");
        var validatedDiagnosesPath = Path.Combine(tempDirectory.Path, "validated-diagnoses.json");
        var proposalsPath = Path.Combine(tempDirectory.Path, "proposals.json");
        var evaluationsPath = Path.Combine(tempDirectory.Path, "evaluations.json");
        var templatePath = Path.Combine(tempDirectory.Path, "decision-template.json");
        var decisionsPath = Path.Combine(tempDirectory.Path, "decisions.json");
        var finalDecisionsPath = Path.Combine(tempDirectory.Path, "final-decisions.json");

        AssertCliSuccess(["ingest-raw", RawFixturePath(), "--db", tempDirectory.DatabasePath]);
        AssertCliSuccess(["normalize-raw", tempDirectory.DatabasePath, "--json", measurementsPath]);

        using var measurementsDocument = JsonDocument.Parse(File.ReadAllText(measurementsPath));
        using var diagnosisFixtureDocument = JsonDocument.Parse(File.ReadAllText(DiagnosisFixturePath()));
        var measurement = Assert.Single(measurementsDocument.RootElement.EnumerateArray());
        var diagnosis = Assert.Single(diagnosisFixtureDocument.RootElement.GetProperty("diagnoses").EnumerateArray());
        AssertMatchesMeasurement(measurement, diagnosis);

        AssertCliSuccess(["validate-diagnoses", DiagnosisFixturePath(), "--json", validatedDiagnosesPath]);
        AssertCliSuccess(["generate-improvement-proposals", validatedDiagnosesPath, "--json", proposalsPath]);
        AssertCliSuccess(["evaluate-improvement-proposals", proposalsPath, "--json", evaluationsPath]);
        AssertCliSuccess(["generate-decision-template", evaluationsPath, "--json", templatePath]);

        WriteApprovedDecisionsFromTemplate(templatePath, decisionsPath);
        AssertCliSuccess(["record-human-decisions", evaluationsPath, decisionsPath, "--json", finalDecisionsPath]);

        using var finalDecisionsDocument = JsonDocument.Parse(File.ReadAllText(finalDecisionsPath));
        var finalDecision = Assert.Single(finalDecisionsDocument.RootElement.EnumerateArray());
        Assert.Equal("proposal-0001", finalDecision.GetProperty("proposal_id").GetString());
        Assert.Equal("approved", finalDecision.GetProperty("human_decision").GetString());

        AssertDoesNotContainUnsafeSyntheticMaterial(
            File.ReadAllText(measurementsPath),
            File.ReadAllText(validatedDiagnosesPath),
            File.ReadAllText(proposalsPath),
            File.ReadAllText(evaluationsPath),
            File.ReadAllText(templatePath),
            File.ReadAllText(finalDecisionsPath));
    }

    private static void AssertMatchesMeasurement(JsonElement measurement, JsonElement diagnosis)
    {
        Assert.Equal(measurement.GetProperty("trace_id").GetString(), diagnosis.GetProperty("trace_id").GetString());
        Assert.Equal(measurement.GetProperty("task_id").GetString(), diagnosis.GetProperty("task_id").GetString());
        Assert.Equal(measurement.GetProperty("client_kind").GetString(), diagnosis.GetProperty("client_kind").GetString());
        Assert.Equal(measurement.GetProperty("task_run_index").GetInt32(), diagnosis.GetProperty("task_run_index").GetInt32());
    }

    private static void WriteApprovedDecisionsFromTemplate(string templatePath, string decisionsPath)
    {
        using var templateDocument = JsonDocument.Parse(File.ReadAllText(templatePath));
        var decisions = templateDocument.RootElement.EnumerateArray()
            .Select(row => new Dictionary<string, object?>
            {
                ["proposal_id"] = row.GetProperty("proposal_id").GetString(),
                ["human_decision"] = "approved",
                ["decision_rationale"] = "Sanitized measurement link supports this human decision.",
                ["approver_id"] = "reviewer-m5",
                ["approved_at"] = "2026-06-08T00:00:00Z",
                ["conditions_or_notes"] = null,
            })
            .ToArray();

        Assert.NotEmpty(decisions);
        File.WriteAllText(decisionsPath, JsonSerializer.Serialize(decisions));
    }

    private static void AssertCliSuccess(string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(args, output, error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
    }

    private static void AssertDoesNotContainUnsafeSyntheticMaterial(params string[] outputs)
    {
        foreach (var output in outputs)
        {
            Assert.DoesNotContain("synthetic prompt resource attribute should not leak", output);
            Assert.DoesNotContain("synthetic auth token should not leak", output);
            Assert.DoesNotContain("synthetic nested auth token should not leak", output);
            Assert.DoesNotContain("synthetic unknown span prompt should not leak", output);
            Assert.DoesNotContain("synthetic unknown span name should not leak", output);
            Assert.DoesNotContain("user@example.com", output);
            Assert.DoesNotContain("nested@example.com", output);
            Assert.DoesNotContain("Synthetic User", output);
            Assert.DoesNotContain("synthetic-end-user", output);
        }
    }

    private static string RawFixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "raw-otlp.synthetic.json");
    }

    private static string DiagnosisFixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "m5-diagnoses.synthetic.json");
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m5-langfuse-independent-loop-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            DatabasePath = System.IO.Path.Combine(Path, "raw-store.db");
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

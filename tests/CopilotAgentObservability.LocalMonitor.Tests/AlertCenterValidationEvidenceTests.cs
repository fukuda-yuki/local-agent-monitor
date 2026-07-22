using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class AlertCenterValidationEvidenceTests
{
    [Fact]
    public void Issue84MatrixPinsThreeRowsAndHonestLiveBlocker()
    {
        var path = Path.Combine(
            FindRepositoryRoot(),
            "docs",
            "sprints",
            "issue-84-alert-center",
            "validation-matrix.json");

        using var matrix = JsonDocument.Parse(File.ReadAllText(path));
        var root = matrix.RootElement;
        Assert.Equal("validation-matrix.v1", root.GetProperty("schema_version").GetString());
        var finalSha = root.GetProperty("final_validation_sha").GetString();
        Assert.Matches("^[0-9a-f]{40}$", finalSha!);

        var rows = root.GetProperty("active_rows").EnumerateArray().ToArray();
        Assert.Equal(
            ["91-A-084", "91-L-084", "91-S-084"],
            rows.Select(row => row.GetProperty("row_id").GetString()).Order(StringComparer.Ordinal));
        Assert.All(rows, row => Assert.Equal(finalSha, row.GetProperty("validation_sha").GetString()));

        var automated = Assert.Single(rows, row => row.GetProperty("row_id").GetString() == "91-A-084");
        var security = Assert.Single(rows, row => row.GetProperty("row_id").GetString() == "91-S-084");
        var live = Assert.Single(rows, row => row.GetProperty("row_id").GetString() == "91-L-084");
        Assert.Equal("passed", automated.GetProperty("classification").GetString());
        Assert.Equal("passed", security.GetProperty("classification").GetString());
        Assert.Equal("blocked_external", live.GetProperty("classification").GetString());
        Assert.Equal("live", Assert.Single(live.GetProperty("evidence").EnumerateArray()).GetProperty("kind").GetString());
        Assert.False(string.IsNullOrWhiteSpace(live.GetProperty("blocker").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(live.GetProperty("retry_condition").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(live.GetProperty("unverified_capability").GetString()));

        Assert.Equal("release_ready_with_external_blockers", root.GetProperty("release_decision").GetProperty("decision").GetString());
        Assert.Equal("91-L-084", Assert.Single(root.GetProperty("release_decision").GetProperty("external_blockers").EnumerateArray()).GetProperty("row_id").GetString());
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CopilotAgentObservability.slnx"))) return directory.FullName;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}

using System.Text.Json;
using CopilotAgentObservability.ConfigCli;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class StaticDashboardGenerationTests
{
    [Fact]
    public void GenerateStaticDashboard_WritesHtmlAndSanitizedDataset()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteFile("dashboard.json", DashboardJson());
        var outputDirectory = Path.Combine(tempDirectory.Path, "site");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            [
                "generate-static-dashboard",
                inputPath,
                "--out-dir",
                outputDirectory,
                "--snapshot-date",
                "2026-06-19",
                "--title",
                "Sprint5 Static Dashboard",
            ],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());

        var html = File.ReadAllText(Path.Combine(outputDirectory, "index.html"));
        Assert.Contains("Sprint5 Static Dashboard", html);
        Assert.Contains("Run Overview", html);
        Assert.Contains("Agent / Tool Behavior", html);
        Assert.Contains("Prompt / Skill / Instructions", html);
        Assert.Contains("Baseline vs Variant", html);
        Assert.Contains("Diagnosis / Improvement Loop", html);
        Assert.Contains("Collection Health", html);
        Assert.Contains("Outcome Linkage Candidate", html);
        Assert.Contains("filter-user", html);
        Assert.Contains("dashboard-data.json", html);

        var dataset = File.ReadAllText(Path.Combine(outputDirectory, "dashboard-data.json"));
        Assert.Contains("user@example.com", dataset);
        Assert.Contains("example-user", dataset);
        Assert.DoesNotContain("synthetic raw prompt should be removed", dataset);
        Assert.DoesNotContain("synthetic dotted response should be removed", dataset);
        Assert.DoesNotContain("synthetic dotted tool args should be removed", dataset);
        Assert.DoesNotContain("Authorization=Basic", dataset);
        Assert.DoesNotContain("authorization.header", dataset);
        Assert.DoesNotContain("sensitive_bundle_path", dataset);
        Assert.DoesNotContain("C:/sensitive-bundle", dataset);
        Assert.DoesNotContain("secret_key", dataset);
        Assert.DoesNotContain("sensitive-bundle", dataset);

        using var document = JsonDocument.Parse(dataset);
        var run = document.RootElement.GetProperty("dashboard_run_summary").EnumerateArray().Single();
        Assert.Equal("user@example.com", run.GetProperty("user_email").GetString());
        Assert.False(run.TryGetProperty("prompt", out _));
        Assert.False(run.TryGetProperty("authorization", out _));
    }

    [Fact]
    public void GenerateStaticDashboard_SanitizesRealDataShapedSnapshot()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteFile("dashboard.json", RealDataShapedDashboardJson());
        var outputDirectory = Path.Combine(tempDirectory.Path, "site");

        var exitCode = CliApplication.Run(
            [
                "generate-static-dashboard",
                inputPath,
                "--out-dir",
                outputDirectory,
                "--snapshot-date",
                "2026-06-20",
            ],
            new StringWriter(),
            new StringWriter());

        Assert.Equal(0, exitCode);

        var html = File.ReadAllText(Path.Combine(outputDirectory, "index.html"));
        Assert.Contains("Snapshot: <span id=\"snapshot-date\">2026-06-20</span>", html);

        var dataset = File.ReadAllText(Path.Combine(outputDirectory, "dashboard-data.json"));
        Assert.Contains("real.user@example.com", dataset);
        Assert.Contains("real-user-001", dataset);
        Assert.Contains("repo-real-snapshot-2026-06-20", dataset);
        Assert.DoesNotContain("Real prompt content must not be published", dataset);
        Assert.DoesNotContain("Real response content must not be published", dataset);
        Assert.DoesNotContain("Real tool arguments must not be published", dataset);
        Assert.DoesNotContain("Real tool result must not be published", dataset);
        Assert.DoesNotContain("Authorization: Basic", dataset);
        Assert.DoesNotContain("secret_key", dataset);
        Assert.DoesNotContain("C:/Users/real-user/sensitive-bundle", dataset.Replace('\\', '/'));

        using var document = JsonDocument.Parse(dataset);
        var run = document.RootElement.GetProperty("dashboard_run_summary").EnumerateArray().Single();
        Assert.Equal("real.user@example.com", run.GetProperty("user_email").GetString());
        Assert.Equal("real-user-001", run.GetProperty("user_id").GetString());
        Assert.Equal("repo-real-snapshot-2026-06-20", run.GetProperty("repo_snapshot").GetString());
        Assert.False(run.TryGetProperty("messages", out _));
        Assert.False(run.TryGetProperty("content", out _));
        Assert.False(run.TryGetProperty("secret_key", out _));
    }

    [Fact]
    public void GenerateStaticDashboard_ReturnsNonZeroWithoutOutDir()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["generate-static-dashboard", "dashboard.json"], output, error);

        Assert.Equal(1, exitCode);
        Assert.Contains("requires --out-dir", error.ToString());
    }

    [Fact]
    public void GenerateStaticDashboard_ReturnsNonZeroForMissingRequiredTable()
    {
        using var tempDirectory = new TempDirectory();
        var inputPath = tempDirectory.WriteFile("dashboard.json", """{"dashboard_run_summary":[]}""");
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(
            ["generate-static-dashboard", inputPath, "--out-dir", Path.Combine(tempDirectory.Path, "site")],
            output,
            error);

        Assert.Equal(1, exitCode);
        Assert.Contains("dashboard_operation_summary", error.ToString());
    }

    private static string DashboardJson()
    {
        return """
            {
              "schema_version": "sprint4-m2-v1",
              "generated_at_utc": "2026-06-19T00:00:00.0000000+00:00",
              "time_bucket_granularity": "day",
              "parameters": {
                "long_running_trace_threshold_ms": 600000,
                "long_running_turn_threshold_ms": 300000,
                "long_running_tool_threshold_ms": 120000,
                "stuck_session_threshold_ms": 900000
              },
              "dashboard_run_summary": [
                {
                  "schema_version": "sprint4-m2-v1",
                  "time_bucket_start_utc": "2026-06-19T00:00:00.0000000+00:00",
                  "trace_id": "trace-1",
                  "user_id": "example-user",
                  "user_email": "user@example.com",
                  "client_kind": "copilot-cli",
                  "experiment_id": "baseline",
                  "agent_variant": "baseline",
                  "status": "success",
                  "success_status": "pass",
                  "duration_ms": 1234,
                  "ttft_ms": 100,
                  "total_tokens": 42,
                  "tool_call_count": 2,
                  "error_count": 0,
                  "prompt": "synthetic raw prompt should be removed",
                  "response.content": "synthetic dotted response should be removed",
                  "tool.arguments": "synthetic dotted tool args should be removed",
                  "authorization.header": "Authorization=Basic abc123",
                  "secret_key": "secret_key=synthetic-secret",
                  "evidence_ref": "C:/sensitive-bundle/manifest.json"
                }
              ],
              "dashboard_operation_summary": [],
              "dashboard_candidate_summary": [
                {
                  "candidate_kind": "diagnosis",
                  "trace_id": "trace-1",
                  "user_email": "user@example.com",
                  "sensitive_bundle_path": "C:/sensitive-bundle/manifest.json"
                }
              ],
              "dashboard_collection_health": []
            }
            """;
    }

    private static string RealDataShapedDashboardJson()
    {
        return """
            {
              "schema_version": "sprint4-m2-v1",
              "generated_at_utc": "2026-06-20T00:00:00.0000000+00:00",
              "time_bucket_granularity": "day",
              "dashboard_run_summary": [
                {
                  "schema_version": "sprint4-m2-v1",
                  "time_bucket_start_utc": "2026-06-20T00:00:00.0000000+00:00",
                  "trace_id": "real-trace-redacted-001",
                  "user_id": "real-user-001",
                  "user_email": "real.user@example.com",
                  "client_kind": "copilot-cli",
                  "experiment_id": "baseline",
                  "agent_variant": "baseline",
                  "repo_snapshot": "repo-real-snapshot-2026-06-20",
                  "status": "success",
                  "success_status": "pass",
                  "duration_ms": 4567,
                  "ttft_ms": 321,
                  "total_tokens": 1234,
                  "tool_call_count": 5,
                  "error_count": 0,
                  "messages": [
                    "Real prompt content must not be published"
                  ],
                  "content": "Real response content must not be published",
                  "tool_args": "Real tool arguments must not be published",
                  "tool_result": "Real tool result must not be published",
                  "headers": {
                    "authorization": "Authorization: Basic cHVibGljOnNlY3JldA==",
                    "secret_key": "secret_key=real-secret"
                  }
                }
              ],
              "dashboard_operation_summary": [
                {
                  "trace_id": "real-trace-redacted-001",
                  "user_email": "real.user@example.com",
                  "client_kind": "copilot-cli",
                  "operation_kind": "tool",
                  "tool_name": "shell",
                  "status": "success",
                  "call_count": 1,
                  "result": "Real tool result must not be published"
                }
              ],
              "dashboard_candidate_summary": [
                {
                  "candidate_kind": "diagnosis",
                  "trace_id": "real-trace-redacted-001",
                  "user_email": "real.user@example.com",
                  "evidence_ref": "C:/Users/real-user/sensitive-bundle/manifest.json"
                }
              ],
              "dashboard_collection_health": []
            }
            """;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m5-static-dashboard-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string WriteFile(string fileName, string content)
        {
            var path = System.IO.Path.Combine(Path, fileName);
            File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

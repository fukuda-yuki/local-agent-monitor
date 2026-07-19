using System.Text.Json;
using CopilotAgentObservability.ConfigCli;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SensitiveRetentionRuntimeTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"cao-sensitive-runtime-{Guid.NewGuid():N}");

    public SensitiveRetentionRuntimeTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void Capture_ValidatesCatalogBeforeAnyInputOrOutput()
    {
        var measurements = Path.Combine(directory, "measurements.json");
        var raw = Path.Combine(directory, "raw.json");
        var output = Path.Combine(directory, "private-output.json");
        File.WriteAllText(measurements, "{ invalid measurement containing private-secret }");
        File.WriteAllText(raw, "{ invalid raw containing private-secret }");

        using var error = new StringWriter();
        var exitCode = CliApplication.Run(["generate-diagnosis-candidates", measurements, "--raw", raw, "--include-sensitive-content", "--retention-database", Path.Combine(directory, "missing.db"), "--json", output], new StringWriter(), error);

        Assert.Equal(1, exitCode);
        Assert.Equal("error: retention_catalog_unavailable" + Environment.NewLine, error.ToString());
        Assert.False(File.Exists(output));
        Assert.DoesNotContain("private-secret", error.ToString());
    }

    [Fact]
    public void Capture_WithoutFragmentsDoesNotReserveCatalogOrCreateParent()
    {
        var catalog = CreateCatalog();
        var parent = Path.Combine(directory, "bundles");
        var measurements = WriteMeasurements("trace-safe");
        var raw = WriteRaw("trace-safe", "safe.detail", "safe value");
        var output = Path.Combine(directory, "out.json");

        var exitCode = CliApplication.Run(["generate-diagnosis-candidates", measurements, "--raw", raw, "--include-sensitive-content", "--retention-database", catalog, "--sensitive-output-dir", parent, "--json", output], new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.False(Directory.Exists(parent));
        Assert.Equal(0L, CaptureCount(catalog));
    }

    [Fact]
    public void Capture_PublishesBothOutputsAndManifestOmitsSourceAndDeletePaths()
    {
        var catalog = CreateCatalog();
        var parent = Path.Combine(directory, "bundles");
        var measurements = WriteMeasurements("trace-sensitive");
        var raw = WriteRaw("trace-sensitive", "authorization.token", "private-secret");
        var csv = Path.Combine(directory, "out.csv");
        var json = Path.Combine(directory, "out.json");

        var exitCode = CliApplication.Run(["generate-diagnosis-candidates", measurements, "--raw", raw, "--include-sensitive-content", "--retention-database", catalog, "--sensitive-output-dir", parent, "--csv", csv, "--json", json], new StringWriter(), new StringWriter());

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(csv));
        Assert.True(File.Exists(json));
        var bundle = Assert.Single(Directory.EnumerateDirectories(parent));
        var manifest = File.ReadAllText(Path.Combine(bundle, "manifest.json"));
        Assert.DoesNotContain(raw, manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("delete_target_paths", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_OutputFailurePreservesExistingTargetAndCompletedBundle()
    {
        var catalog = CreateCatalog();
        var parent = Path.Combine(directory, "bundles");
        var measurements = WriteMeasurements("trace-sensitive");
        var raw = WriteRaw("trace-sensitive", "authorization.token", "private-secret");
        var csv = Path.Combine(directory, "out.csv");
        var json = Path.Combine(directory, "out.json");
        File.WriteAllText(json, "existing-private-target");
        using var error = new StringWriter();

        var exitCode = CliApplication.Run(["generate-diagnosis-candidates", measurements, "--raw", raw, "--include-sensitive-content", "--retention-database", catalog, "--sensitive-output-dir", parent, "--csv", csv, "--json", json], new StringWriter(), error);

        Assert.Equal(1, exitCode);
        Assert.Equal("existing-private-target", File.ReadAllText(json));
        Assert.False(File.Exists(csv));
        Assert.Single(Directory.EnumerateDirectories(parent));
        Assert.DoesNotContain(directory, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-secret", error.ToString(), StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string CreateCatalog()
    {
        var path = Path.Combine(directory, $"catalog-{Guid.NewGuid():N}.db");
        RetentionCatalogContext.InitializeNewOwnedDatabase(path);
        return path;
    }

    private long CaptureCount(string catalog)
    {
        using var connection = new SqliteConnection($"Data Source={catalog}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM retention_file_capture_reservations";
        return (long)command.ExecuteScalar()!;
    }

    private string WriteMeasurements(string traceId)
    {
        var path = Path.Combine(directory, $"measurements-{traceId}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { new { trace_id = traceId, experiment_id = "experiment", client_kind = "copilot-cli", task_id = "task", task_category = "test", task_run_index = 1, experiment_condition = "baseline", prompt_version = "v1", agent_variant = "default", repo_snapshot = "repo", input_tokens = 1, output_tokens = 1, total_tokens = 2, turn_count = 1, tool_call_count = 0, duration_ms = 1, error_count = 0, success_status = "pass", aggregation_notes = "test" } }));
        return path;
    }

    private string WriteRaw(string traceId, string key, string value)
    {
        var path = Path.Combine(directory, $"raw-{traceId}.json");
        File.WriteAllText(path, "{\"resourceSpans\":[{\"scopeSpans\":[{\"spans\":[{\"traceId\":\"" + traceId + "\",\"spanId\":\"span\",\"name\":\"chat\",\"attributes\":[{\"key\":\"" + key + "\",\"value\":{\"stringValue\":\"" + value + "\"}}]}]}]}]}");
        return path;
    }
}

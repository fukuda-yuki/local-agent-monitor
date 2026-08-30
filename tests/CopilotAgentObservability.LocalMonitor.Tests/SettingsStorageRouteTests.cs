using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Settings;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SettingsStorageRouteTests
{
    private const string Path = "/api/local-monitor/v1/settings/storage";

    [Fact]
    public async Task Raw_default_returns_only_closed_storage_facts()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());

        using var response = await host.Client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(
            ["schema_version", "database_file_size_bytes", "retention", "backup", "historical_import", "restart_requirement"],
            json.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("settings-storage-summary.v1", json.RootElement.GetProperty("schema_version").GetString());
        Assert.True(json.RootElement.GetProperty("database_file_size_bytes").GetInt64() > 0);
        Assert.Equal("not_required", json.RootElement.GetProperty("restart_requirement").GetString());
        Assert.DoesNotContain(temp.Path, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Route_is_exact_closed_and_raw_default_only()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: DisabledWorkers());
        await AssertError(await host.Client.SendAsync(new(HttpMethod.Put, Path)), 405, "method_not_allowed");
        await AssertError(await host.Client.GetAsync(Path + "?extra=1"), 400, "invalid_request");
        await AssertError(await host.Client.SendAsync(new(HttpMethod.Get, Path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }), 400, "invalid_request");
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync(Path + "/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/API/local-monitor/v1/settings/storage")).StatusCode);

        using var sanitizedTemp = new MonitorTempDirectory();
        await using var sanitized = await MonitorTestHost.StartAsync(sanitizedTemp, sanitizedOnly: true, testOptions: DisabledWorkers());
        Assert.Equal(HttpStatusCode.NotFound, (await sanitized.Client.GetAsync(Path)).StatusCode);
    }

    [Fact]
    public void Backup_snapshot_retains_last_success_across_a_later_failure()
    {
        var snapshot = new RuntimeBackupStatusSnapshot(TimeProvider.System);
        snapshot.Running();
        snapshot.Succeeded();
        var succeeded = JsonSerializer.Serialize(snapshot.Read());
        snapshot.Running();
        snapshot.Failed();
        var failed = JsonSerializer.Serialize(snapshot.Read());

        Assert.Contains("\"state\":\"succeeded\"", succeeded, StringComparison.Ordinal);
        Assert.Contains("\"validation_state\":\"passed\"", succeeded, StringComparison.Ordinal);
        using var value = JsonDocument.Parse(failed);
        Assert.Equal("failed", value.RootElement.GetProperty("state").GetString());
        Assert.Equal("unknown", value.RootElement.GetProperty("validation_state").GetString());
        Assert.NotNull(value.RootElement.GetProperty("last_successful_at").GetString());
    }

    private static MonitorHostTestOptions DisabledWorkers() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
    };

    private static async Task AssertError(HttpResponseMessage response, int status, string code)
    {
        using (response)
        {
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            if (status == 405) Assert.Equal(["GET"], response.Content.Headers.Allow);
            Assert.Equal($"{{\"error\":\"{code}\"}}", await response.Content.ReadAsStringAsync());
        }
    }
}

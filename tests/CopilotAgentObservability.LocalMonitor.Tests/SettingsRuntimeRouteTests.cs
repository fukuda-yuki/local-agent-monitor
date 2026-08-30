using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SettingsRuntimeRouteTests
{
    private const string Path = "/api/local-monitor/v1/settings/runtime";
    private static readonly DateTimeOffset Start = new(2026, 8, 30, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task ReturnsClosedRuntimeFactsAndExactFiveMinuteActivity()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(Start);
        var health = MonitorTestHealth.Ready(time);
        health.SetProjectionStatus(7, Start.AddSeconds(-2));
        var store = new RawTelemetryStore(temp.DatabasePath, temp.RetentionContext, time, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateMonitorSchema();
        Insert(store, Start.AddSeconds(-301), "SECRET_OLD_PAYLOAD");
        Insert(store, Start.AddSeconds(-300), "SECRET_BOUNDARY_PAYLOAD");
        Insert(store, Start.AddSeconds(-1), "SECRET_LATEST_PAYLOAD");
        Insert(store, Start.AddSeconds(1), "SECRET_FUTURE_PAYLOAD");

        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            TimeProvider = time,
            Health = health,
            StartWriter = false,
            StartProjectionWorker = false,
        });

        using var response = await host.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SECRET_", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal([
            "application_started_at", "receiver_readiness", "endpoint", "activity_state", "latest_received_at",
            "recent_received_count", "projection_backlog", "capture_reasons", "projection_reasons", "restart_requirement",
        ], root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(Start.ToString("O"), root.GetProperty("application_started_at").GetString());
        Assert.Equal("degraded", root.GetProperty("receiver_readiness").GetString());
        Assert.Equal("available", root.GetProperty("activity_state").GetString());
        Assert.Equal(Start.AddSeconds(-1).ToString("O"), root.GetProperty("latest_received_at").GetString());
        Assert.Equal(2, root.GetProperty("recent_received_count").GetInt32());
        Assert.Equal(7, root.GetProperty("projection_backlog").GetInt32());
        Assert.Empty(root.GetProperty("capture_reasons").EnumerateArray());
        Assert.Equal(["projection_lag"], root.GetProperty("projection_reasons").EnumerateArray().Select(item => item.GetString()!).ToArray());
        Assert.Equal("unavailable", root.GetProperty("restart_requirement").GetString());
        var endpoint = root.GetProperty("endpoint");
        Assert.Equal(["transport", "scope", "port"], endpoint.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("http", endpoint.GetProperty("transport").GetString());
        Assert.Equal("loopback", endpoint.GetProperty("scope").GetString());
        Assert.Equal(new Uri(host.Url).Port, endpoint.GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task EmptyAndUnavailableActivityStayClosedAndProjectionUnknownIsNull()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(Start);
        var health = MonitorTestHealth.Ready(time);
        health.RecordProjectionStatusUnavailable();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new()
        {
            TimeProvider = time, Health = health, StartWriter = false, StartProjectionWorker = false,
        });

        using (var empty = await host.Client.GetAsync(Path))
        {
            using var json = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Equal("available", json.RootElement.GetProperty("activity_state").GetString());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("latest_received_at").ValueKind);
            Assert.Equal(0, json.RootElement.GetProperty("recent_received_count").GetInt32());
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("projection_backlog").ValueKind);
        }

        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE raw_records;";
            command.ExecuteNonQuery();
        }
        using var unavailable = await host.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.OK, unavailable.StatusCode);
        using var unavailableJson = JsonDocument.Parse(await unavailable.Content.ReadAsStringAsync());
        Assert.Equal("unavailable", unavailableJson.RootElement.GetProperty("activity_state").GetString());
        Assert.Equal(JsonValueKind.Null, unavailableJson.RootElement.GetProperty("latest_received_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, unavailableJson.RootElement.GetProperty("recent_received_count").ValueKind);
        Assert.Equal(Start.ToString("O"), unavailableJson.RootElement.GetProperty("application_started_at").GetString());
    }

    [Fact]
    public async Task ExactGetSecurityMatrixAndSanitizedOnlyAbsence()
    {
        using var temp = new MonitorTempDirectory();
        await using (var host = await MonitorTestHost.StartAsync(temp, testOptions: new() { StartWriter = false, StartProjectionWorker = false }))
        {
            await AssertError(await host.Client.SendAsync(new(HttpMethod.Put, Path)), 405, "method_not_allowed");
            await AssertError(await host.Client.SendAsync(new(new HttpMethod("PROPFIND"), Path)), 405, "method_not_allowed");
            using (var upper = await host.Client.GetAsync("/API/local-monitor/v1/settings/runtime"))
            {
                Assert.Equal(HttpStatusCode.NotFound, upper.StatusCode);
                Assert.True(upper.Headers.CacheControl?.NoStore);
                Assert.Equal(string.Empty, await upper.Content.ReadAsStringAsync());
            }
            using (var trailing = await host.Client.GetAsync(Path + "/"))
            {
                Assert.Equal(HttpStatusCode.NotFound, trailing.StatusCode);
                Assert.True(trailing.Headers.CacheControl?.NoStore);
                Assert.Equal(string.Empty, await trailing.Content.ReadAsStringAsync());
            }
            await AssertError(await host.Client.GetAsync(Path + "?extra=1"), 400, "invalid_request");
            await AssertError(await host.Client.SendAsync(new(HttpMethod.Get, Path) { Content = new StringContent("{}", Encoding.UTF8, "application/json") }), 400, "invalid_request");
            using var origin = new HttpRequestMessage(HttpMethod.Get, Path);
            origin.Headers.Add("Origin", "https://remote.example");
            await AssertError(await host.Client.SendAsync(origin), 403, "cross_origin_forbidden");
            using var invalidHost = new HttpRequestMessage(HttpMethod.Get, Path);
            invalidHost.Headers.Host = "remote.example";
            await AssertError(await host.Client.SendAsync(invalidHost), 400, "invalid_host");
        }

        using var sanitizedTemp = new MonitorTempDirectory();
        await using var sanitized = await MonitorTestHost.StartAsync(sanitizedTemp, sanitizedOnly: true, testOptions: new() { StartWriter = false, StartProjectionWorker = false });
        using var absent = await sanitized.Client.GetAsync(Path);
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
    }

    private static long Insert(RawTelemetryStore store, DateTimeOffset receivedAt, string payload) => store.Insert(new(
        null, RawTelemetrySources.RawOtlp, "trace", receivedAt, null, $"{{\"payload\":\"{payload}\"}}"));

    private static async Task AssertError(HttpResponseMessage response, int status, string code)
    {
        using (response)
        {
            Assert.Equal((HttpStatusCode)status, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            if (status == 405)
            {
                Assert.Equal(["GET"], response.Content.Headers.Allow);
            }
            Assert.Equal($"{{\"error\":\"{code}\"}}", await response.Content.ReadAsStringAsync());
        }
    }
}

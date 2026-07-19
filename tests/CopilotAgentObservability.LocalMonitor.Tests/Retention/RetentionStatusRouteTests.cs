using System.Net;
using System.Reflection;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using CopilotAgentObservability.LocalMonitor.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionStatusRouteTests
{
    [Fact]
    public async Task StatusRoute_MatchesExactV1Schema()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-full-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero))); catalog.CreateSchema();
            InsertItem(path, "ret-item-0001", "full-source", "deletion_queued", "2026-04-20T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", queuedAt: "2026-07-19T00:00:00.0000000+00:00", storeKind: "raw_record");
            Execute(path, "UPDATE retention_worker_state SET last_successful_run_at='2026-07-19T00:00:00.0000000+00:00' WHERE id=1;");
            await using var host = await StartRouteHostAsync(catalog, () => true);
            using var response = await host.Client.GetAsync("/api/retention/v1/status");
            await AssertHeaders(response); Assert.Equal(await FixtureAsync("status-full-item.json"), await response.Content.ReadAsByteArrayAsync());
        }
        finally { Delete(path); }
    }

    [Fact]
    public void RetentionStatusSurfaces_DoNotLeakInjectedMarkers()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero)));
            catalog.CreateSchema();
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,private_locator,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,lease_generation,attempt_count,retry_exhausted,adapter_coverage_version) SELECT 'ret-item-0001',store_instance_id,'raw_record','RAW_PAYLOAD_MARKER',1,zeroblob(32),'C:\\PRIVATE_PATH_MARKER','2026-04-20T00:00:00.0000000+00:00','2026-07-19T00:00:00.0000000+00:00','raw-default-90d',1,'deletion_queued',1,'2026-07-19T00:00:00.0000000+00:00',0,0,0,1 FROM retention_store_instances WHERE id=1;";
                command.ExecuteNonQuery();
            }

            Assert.True(catalog.TryReadStatusSnapshot(workerEnabled: true, out var snapshot));
            var json = JsonSerializer.Serialize(snapshot);
            Assert.DoesNotContain("RAW_PAYLOAD_MARKER", json, StringComparison.Ordinal);
            Assert.DoesNotContain("PRIVATE_PATH_MARKER", json, StringComparison.Ordinal);
            Assert.Equal("ret-item-0001", snapshot!.Items.Single().ItemId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task StatusRoute_FailsClosedWhenExposedCatalogFieldsAreNotDiagnosticSafe()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-unsafe-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path); catalog.CreateSchema();
            InsertItem(path, "ret-item-0001", "source", "deletion_failed", "2026-04-20T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", storeKind: "raw_record");
            await using var host = await StartRouteHostAsync(catalog, () => true);
            foreach (var update in new[] { "item_id='CREDENTIAL_MARKER'", "policy_id='PATH_MARKER'", "error_code='RAW_MARKER'" })
            {
                Execute(path, "UPDATE retention_items SET item_id='ret-item-0001',policy_id='raw-default-90d',error_code='retention_delete_busy';");
                Execute(path, $"UPDATE retention_items SET {update} WHERE item_id='ret-item-0001';");
                using var response = await host.Client.GetAsync("/api/retention/v1/status"); await AssertHeaders(response); var body = await response.Content.ReadAsByteArrayAsync();
                Assert.Equal(await FixtureAsync("status-unavailable.json"), body); Assert.DoesNotContain("MARKER", System.Text.Encoding.UTF8.GetString(body), StringComparison.Ordinal);
            }
        }
        finally { Delete(path); }
    }

    [Fact]
    public void StatusSnapshot_CapsItemsAtOneHundredInExpiryThenItemOrder()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-cap-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero)));
            catalog.CreateSchema();
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            for (var index = 0; index < 101; index++)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,lease_generation,attempt_count,retry_exhausted,adapter_coverage_version) SELECT $id,store_instance_id,'raw_record',$source,1,zeroblob(32),'2026-04-20T00:00:00.0000000+00:00',$expiry,'raw-default-90d',1,'deletion_queued',1,0,0,0,1 FROM retention_store_instances WHERE id=1;";
                command.Parameters.AddWithValue("$id", $"ret-item-{100 - index:D4}"); command.Parameters.AddWithValue("$source", index.ToString()); command.Parameters.AddWithValue("$expiry", $"2026-07-19T00:{index / 60:D2}:{index % 60:D2}.0000000+00:00"); command.ExecuteNonQuery();
            }
            Assert.True(catalog.TryReadStatusSnapshot(workerEnabled: true, out var snapshot));
            Assert.Equal(101, snapshot!.QueuedCount); Assert.Equal(100, snapshot.Items.Count);
            Assert.Equal("ret-item-0100", snapshot.Items[0].ItemId); Assert.Equal("ret-item-0001", snapshot.Items[^1].ItemId);
        }
        finally
        {
            SqliteConnection.ClearAllPools(); foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public async Task Diagnostics_PreserveUnknownValues()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-malformed-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path); catalog.CreateSchema();
            Execute(path, "UPDATE retention_worker_state SET last_successful_run_at='EXCEPTION_MARKER_CREDENTIAL_MARKER' WHERE id=1;");
            await using var host = await StartRouteHostAsync(catalog, () => true);
            using var response = await host.Client.GetAsync("/api/retention/v1/status");
            await AssertHeaders(response); var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.Equal(await FixtureAsync("status-unavailable.json"), bytes); Assert.DoesNotContain("EXCEPTION_MARKER_CREDENTIAL_MARKER", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
            Execute(path, "UPDATE retention_worker_state SET last_successful_run_at=NULL WHERE id=1;");
            InsertItem(path, "fedcba9876543210fedcba9876543210", "overflow-source", "deletion_queued", "2026-04-20T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", attemptCount: long.MaxValue, storeKind: "raw_record");
            using var overflow = await host.Client.GetAsync("/api/retention/v1/status"); await AssertHeaders(overflow); Assert.Equal(await FixtureAsync("status-unavailable.json"), await overflow.Content.ReadAsByteArrayAsync());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task StatusRoute_UsesExactFixtureBytesAndNoStoreHeaders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-route-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path); catalog.CreateSchema();
            await using var host = await StartRouteHostAsync(catalog, () => true);
            using var response = await host.Client.GetAsync("/api/retention/v1/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); Assert.False(response.Headers.Contains("ETag")); Assert.Null(response.Content.Headers.LastModified);
            Assert.Equal(await FixtureAsync("status-empty.json"), await response.Content.ReadAsByteArrayAsync());
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task StatusRoute_IsMappedByProductionMonitorHost()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions { StartRetentionCleanupWorker = false });
        using var response = await host.Client.GetAsync("/api/retention/v1/status");
        await AssertHeaders(response); using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync()); Assert.Equal("disabled", json.RootElement.GetProperty("worker_state").GetString());
    }

    [Fact]
    public async Task StatusRoute_PreservesUnavailableAsUnknown()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-unavailable-{Guid.NewGuid():N}.db");
        await using var host = await StartRouteHostAsync(new RetentionCatalogStore(path), () => true);
        using var response = await host.Client.GetAsync("/api/retention/v1/status");
        Assert.Equal(await FixtureAsync("status-unavailable.json"), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task SessionStatus_UsesExistingNotFoundBytesWithNoStoreHeaders()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-session-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path); catalog.CreateSchema();
            await using var host = await StartRouteHostAsync(catalog, () => true);
            using var response = await host.Client.GetAsync("/api/retention/v1/sessions/018f0000-0000-7000-8000-000000000001");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); Assert.False(response.Headers.Contains("ETag")); Assert.Null(response.Content.Headers.LastModified);
            Assert.Equal(await FixtureAsync("session-not-found.json"), await response.Content.ReadAsByteArrayAsync());
        }
        finally { SqliteConnection.ClearAllPools(); foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file); }
    }

    [Fact]
    public async Task SessionStatus_PreservesMixedAndAllLifecycleCounts()
    {
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-mixed-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero))); catalog.CreateSchema(); new SqliteSessionStore(path).CreateSchema(); InsertSession(path, sessionId);
            var states = new[] { "expiring", "retained_by_policy", "expired_pending_deletion", "deletion_queued", "deleting", "deleted", "deletion_failed" };
            for (var index = 0; index < states.Length; index++)
            {
                var eventId = $"018f0000-0000-7000-8000-{index + 2:D12}"; InsertEvent(path, eventId, sessionId);
                InsertItem(path, $"session-item-{index}", eventId, states[index], "2026-04-20T00:00:00.0000000+00:00", index == 0 ? "2026-07-19T00:02:00.0000000+00:00" : "2026-07-19T00:00:00.0000000+00:00", index < 2 ? null : "2026-07-19T00:00:00.0000000+00:00");
            }
            await using var host = await StartRouteHostAsync(catalog, () => true);
            using var mixed = await host.Client.GetAsync($"/api/retention/v1/sessions/{sessionId}"); await AssertHeaders(mixed); Assert.Equal(await FixtureAsync("session-mixed.json"), await mixed.Content.ReadAsByteArrayAsync());
            const string uncaptured = "018f0000-0000-7000-8000-000000000001"; var uncapturedPath = Path.Combine(Path.GetTempPath(), $"retention-status-empty-session-{Guid.NewGuid():N}.db");
            try
            {
                var emptyCatalog = new RetentionCatalogStore(uncapturedPath); emptyCatalog.CreateSchema(); new SqliteSessionStore(uncapturedPath).CreateSchema(); InsertSession(uncapturedPath, uncaptured);
                await using var emptyHost = await StartRouteHostAsync(emptyCatalog, () => true); using var empty = await emptyHost.Client.GetAsync($"/api/retention/v1/sessions/{uncaptured}"); await AssertHeaders(empty); Assert.Equal(await FixtureAsync("session-not-captured.json"), await empty.Content.ReadAsByteArrayAsync());
            }
            finally { Delete(uncapturedPath); }
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task StatusRoute_AppliesWorkerStatePrecedence()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-worker-{Guid.NewGuid():N}.db");
        try
        {
            var now = new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero)); var catalog = new RetentionCatalogStore(path, now); catalog.CreateSchema();
            await using var disabledHost = await StartRouteHostAsync(catalog, () => false); using var disabled = await disabledHost.Client.GetAsync("/api/retention/v1/status"); await AssertWorker(disabled, "disabled");
            Execute(path, "UPDATE retention_worker_state SET worker_error_code='retention_adapter_coverage_mismatch' WHERE id=1;");
            await using var degradedHost = await StartRouteHostAsync(catalog, () => true); using var degraded = await degradedHost.Client.GetAsync("/api/retention/v1/status"); await AssertWorker(degraded, "degraded");
            Execute(path, "UPDATE retention_worker_state SET worker_error_code=NULL WHERE id=1; INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,lease_generation,attempt_count,retry_exhausted,adapter_coverage_version) SELECT 'abcdefabcdefabcdefabcdefabcdefab',store_instance_id,'raw_record','lease-source',1,zeroblob(32),'2026-04-20T00:00:00.0000000+00:00','2026-07-19T00:00:00.0000000+00:00','raw-default-90d',1,'deleting',1,0,0,0,1 FROM retention_store_instances WHERE id=1; INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('abcdefabcdefabcdefabcdefabcdefab','deletion','owner','2026-07-19T00:02:00.0000000+00:00',1);");
            await using var runningHost = await StartRouteHostAsync(catalog, () => true); using var running = await runningHost.Client.GetAsync("/api/retention/v1/status"); await AssertWorker(running, "running");
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task StatusRoute_RejectsStaleDeletionLeasesAndAcceptsAValidClaim()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-lease-{Guid.NewGuid():N}.db");
        try
        {
            var time = new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero)); var catalog = new RetentionCatalogStore(path, time); catalog.CreateSchema();
            const string itemId = "0123456789abcdef0123456789abcdef";
            InsertItem(path, itemId, "source", "deletion_queued", "2026-04-20T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", storeKind: "raw_record");
            Execute(path, $"INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('{itemId}','deletion','owner','2026-07-19T00:02:00.0000000+00:00',1);");
            await using var host = await StartRouteHostAsync(catalog, () => true); using var queued = await host.Client.GetAsync("/api/retention/v1/status"); await AssertWorker(queued, "idle");
            Execute(path, $"UPDATE retention_items SET state='deleting',revision=2 WHERE item_id='{itemId}'; INSERT INTO retention_delete_journal(item_id,durable_cursor,intent_at,expected_revision) VALUES('{itemId}',0,'2026-07-19T00:01:00.0000000+00:00',1);");
            using var staleJournal = await host.Client.GetAsync("/api/retention/v1/status"); await AssertWorker(staleJournal, "idle");
            Execute(path, $"UPDATE retention_delete_journal SET expected_revision=2 WHERE item_id='{itemId}';");
            using var valid = await host.Client.GetAsync("/api/retention/v1/status"); await AssertWorker(valid, "running");
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task StatusRoute_FailsClosedForMalformedActiveDeletionLeaseTimestamp()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-malformed-lease-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero))); catalog.CreateSchema();
            const string itemId = "00112233445566778899aabbccddeeff";
            InsertItem(path, itemId, "source", "deleting", "2026-04-20T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", storeKind: "raw_record");
            Execute(path, $"INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES('{itemId}','deletion','owner','zzzz',1);");
            await using var host = await StartRouteHostAsync(catalog, () => true); using var response = await host.Client.GetAsync("/api/retention/v1/status"); await AssertHeaders(response);
            Assert.Equal(await FixtureAsync("status-unavailable.json"), await response.Content.ReadAsByteArrayAsync());
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task StatusRoute_FailsClosedForMalformedTimestampOutsideItemLimit()
    {
        var path = Path.Combine(Path.GetTempPath(), $"retention-status-malformed-item-{Guid.NewGuid():N}.db");
        try
        {
            var catalog = new RetentionCatalogStore(path, new MutableTimeProvider(new DateTimeOffset(2026, 7, 19, 0, 1, 0, TimeSpan.Zero))); catalog.CreateSchema();
            for (var index = 0; index < 101; index++) InsertItem(path, $"ret-item-{100 - index:D4}", $"source-{index}", "deletion_queued", "2026-04-20T00:00:00.0000000+00:00", $"2026-07-19T00:{index / 60:D2}:{index % 60:D2}.0000000+00:00", "2026-07-19T00:00:00.0000000+00:00", storeKind: "raw_record");
            Execute(path, "UPDATE retention_items SET captured_at='zzzz' WHERE item_id='ret-item-0000';");
            await using var host = await StartRouteHostAsync(catalog, () => true); using var response = await host.Client.GetAsync("/api/retention/v1/status"); await AssertHeaders(response);
            Assert.Equal(await FixtureAsync("status-unavailable.json"), await response.Content.ReadAsByteArrayAsync());
        }
        finally { Delete(path); }
    }

    private static async Task<RunningMonitorHost> StartRouteHostAsync(RetentionCatalogStore catalog, Func<bool> enabled)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); var app = builder.Build();
        RetentionStatusRoutes.Map(app, catalog, enabled); await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        return new RunningMonitorHost(app, new HttpClient { BaseAddress = new Uri(address) }, address);
    }

    private static Task<byte[]> FixtureAsync(string name) => File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "TestData", "Retention", name));

    private static void InsertSession(string path, string sessionId) => Execute(path, "INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) VALUES($id,'active','partial','2026-07-19T00:00:00.0000000+00:00','not_captured','2026-07-19T00:00:00.0000000+00:00','2026-07-19T00:00:00.0000000+00:00');", ("$id", sessionId));
    private static void InsertEvent(string path, string eventId, string sessionId) => Execute(path, "INSERT INTO session_events(event_id,session_id,source_adapter,source_event_id,type,occurred_at,content_state) VALUES($event,$session,'copilot-compatible-hook',$event,'UserPromptSubmit','2026-07-19T00:00:00.0000000+00:00','available');", ("$event", eventId), ("$session", sessionId));
    private static void InsertItem(string path, string itemId, string sourceItemId, string state, string capturedAt, string expiresAt, string? readDeniedAt, string? queuedAt = null, long attemptCount = 0, string storeKind = "session_event_content") => Execute(path, "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,lease_generation,attempt_count,retry_exhausted,adapter_coverage_version) SELECT $item,store_instance_id,$kind,$source,1,zeroblob(32),$captured,$expires,'raw-default-90d',1,$state,1,$denied,$queued,0,$attempt,0,1 FROM retention_store_instances WHERE id=1;", ("$item", itemId), ("$kind", storeKind), ("$source", sourceItemId), ("$captured", capturedAt), ("$expires", expiresAt), ("$state", state), ("$denied", (object?)readDeniedAt), ("$queued", (object?)queuedAt), ("$attempt", attemptCount));
    private static void Execute(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path}"); connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value); command.ExecuteNonQuery();
    }
    private static async Task AssertHeaders(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType); Assert.Equal("no-store", response.Headers.CacheControl?.ToString()); Assert.False(response.Headers.Contains("ETag")); Assert.Null(response.Content.Headers.LastModified);
        await Task.CompletedTask;
    }
    private static async Task AssertWorker(HttpResponseMessage response, string expected)
    {
        await AssertHeaders(response); using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync()); Assert.Equal(expected, json.RootElement.GetProperty("worker_state").GetString());
    }
    private static void Delete(string path)
    {
        SqliteConnection.ClearAllPools(); foreach (var file in new[] { path, path + "-wal", path + "-shm" }) if (File.Exists(file)) File.Delete(file);
    }
}

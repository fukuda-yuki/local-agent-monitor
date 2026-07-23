using System.Net;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

[Collection("Retention mutation API routes")]
public sealed class RetentionHistoryRouteTests
{
    [Fact]
    public async Task HistoryRoutes_ReturnExactTargetBoundDtosAndDefaultHundredEntryPages()
    {
        using var temp = NewTemp();
        var seeded = SeedSession(temp, 2);
        var catalog = new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider);
        AppendEvents(catalog, RetentionMutationTargetKind.Item, seeded.ItemIds[0], 101, temp.TimeProvider.GetUtcNow());
        catalog.AppendAuditEvent(Audit(250, RetentionMutationTargetKind.Session, seeded.SessionId, temp.TimeProvider.GetUtcNow().AddMinutes(5)));
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());

        using var first = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history");
        await AssertSuccessHeadersAsync(first);
        using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStreamAsync());
        AssertPropertyNames(firstJson.RootElement, "schema_version", "target_kind", "target_id", "events", "next_cursor");
        Assert.Equal("item", firstJson.RootElement.GetProperty("target_kind").GetString());
        Assert.Equal(seeded.ItemIds[0], firstJson.RootElement.GetProperty("target_id").GetString());
        var firstEvents = firstJson.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(100, firstEvents.Length);
        AssertPropertyNames(firstEvents[0],
            "event_id", "operation_id", "event_type", "target_kind", "target_id", "session_id", "occurred_at", "actor_label",
            "operation", "reason_code", "comment", "previous_pin_state", "new_pin_state", "previous_operation_state",
            "new_operation_state", "request_idempotency_key", "expected_version", "result_version", "target_item_set_digest",
            "completion_code", "error_code");
        var cursor = firstJson.RootElement.GetProperty("next_cursor").GetString();
        Assert.StartsWith(RetentionMutationIdentifierFormats.HistoryCursorPrefix, cursor, StringComparison.Ordinal);

        using var last = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history?cursor={cursor}");
        await AssertSuccessHeadersAsync(last);
        using var lastJson = JsonDocument.Parse(await last.Content.ReadAsStreamAsync());
        Assert.Single(lastJson.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, lastJson.RootElement.GetProperty("next_cursor").ValueKind);
        var ids = firstEvents.Select(static item => item.GetProperty("event_id").GetString())
            .Concat(lastJson.RootElement.GetProperty("events").EnumerateArray().Select(static item => item.GetProperty("event_id").GetString()));
        Assert.Equal(101, ids.Distinct(StringComparer.Ordinal).Count());

        using var session = await host.Client.GetAsync($"/api/retention/v1/sessions/{seeded.SessionId}/history?limit=1");
        await AssertSuccessHeadersAsync(session);
        using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStreamAsync());
        Assert.Equal("session", sessionJson.RootElement.GetProperty("target_kind").GetString());
        Assert.Equal(seeded.SessionId, sessionJson.RootElement.GetProperty("target_id").GetString());
        Assert.Single(sessionJson.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, sessionJson.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task HistoryRoutes_ValidateLimitThenTargetThenCursor()
    {
        using var temp = NewTemp();
        var seeded = SeedSession(temp, 2);
        var catalog = new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider);
        var auditEvent = Audit(1, RetentionMutationTargetKind.Item, seeded.ItemIds[0], temp.TimeProvider.GetUtcNow());
        catalog.AppendAuditEvent(auditEvent);
        Assert.True(RetentionMutationIdentifiers.TryParseAuditEventId(auditEvent.EventId, out var nonce));
        var foreignCursor = RetentionMutationIdentifiers.CreateHistoryCursor(nonce);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());

        foreach (var query in new[] { "limit=", "limit=0", "limit=101", "limit=-1", "limit=abc", "limit=1&limit=2" })
        {
            using var response = await host.Client.GetAsync($"/api/retention/v1/items/missing-item/history?{query}&cursor=not-a-cursor");
            await AssertErrorAsync(response, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.RequestInvalid);
        }

        using var missing = await host.Client.GetAsync("/api/retention/v1/items/missing-item/history?cursor=not-a-cursor");
        await AssertErrorAsync(missing, HttpStatusCode.NotFound, RetentionMutationErrorCodes.TargetNotFound);
        using var malformedSession = await host.Client.GetAsync("/api/retention/v1/sessions/NOT-A-CANONICAL-SESSION/history?cursor=not-a-cursor");
        await AssertErrorAsync(malformedSession, HttpStatusCode.NotFound, RetentionMutationErrorCodes.TargetNotFound);
        using var malformed = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history?cursor=not-a-cursor");
        await AssertErrorAsync(malformed, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.HistoryCursorInvalid);
        using var blank = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history?cursor=");
        await AssertErrorAsync(blank, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.HistoryCursorInvalid);
        using var repeated = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history?cursor={foreignCursor}&cursor={foreignCursor}");
        await AssertErrorAsync(repeated, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.HistoryCursorInvalid);
        using var foreign = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[1]}/history?cursor={foreignCursor}");
        await AssertErrorAsync(foreign, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.HistoryCursorInvalid);
    }

    [Fact]
    public async Task HistoryRoutes_ReturnEmptyHistoryForAnExistingTarget()
    {
        using var temp = NewTemp();
        var seeded = SeedSession(temp, 1);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());

        using var response = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history");

        await AssertSuccessHeadersAsync(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Empty(json.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("next_cursor").ValueKind);
    }

    [Fact]
    public async Task HistoryRoutes_AreSameOriginNoStoreAndFailClosedWithoutLeakingCatalogMarkers()
    {
        using var temp = NewTemp();
        var seeded = SeedSession(temp, 1);
        var catalog = new RetentionCatalogStore(temp.RetentionContext, temp.TimeProvider);
        var auditEvent = Audit(1, RetentionMutationTargetKind.Item, seeded.ItemIds[0], temp.TimeProvider.GetUtcNow());
        catalog.AppendAuditEvent(auditEvent);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());
        Execute(temp.DatabasePath,
            "UPDATE retention_items SET source_item_id='RAW_PAYLOAD_MARKER',private_locator='C:\\PRIVATE_PATH_MARKER' WHERE item_id=$item;",
            ("$item", seeded.ItemIds[0]));

        using var success = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history");
        await AssertSuccessHeadersAsync(success);
        var body = await success.Content.ReadAsStringAsync();
        Assert.False(body.Contains("RAW_PAYLOAD_MARKER", StringComparison.Ordinal), "Raw marker reached retention history.");
        Assert.False(body.Contains("PRIVATE_PATH_MARKER", StringComparison.Ordinal), "Path marker reached retention history.");
        Assert.False(
            body.Contains(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, StringComparison.Ordinal),
            "Plaintext confirmation material reached retention history.");

        using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/retention/v1/items/{seeded.ItemIds[0]}/history");
        crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossSite = await host.Client.SendAsync(crossSiteRequest);
        await AssertErrorAsync(crossSite, HttpStatusCode.Forbidden, "cross_origin_forbidden");

        Execute(temp.DatabasePath, "UPDATE retention_audit_events SET comment='CREDENTIAL_MARKER/C:\\PRIVATE_PATH_MARKER' WHERE event_id=$event;", ("$event", auditEvent.EventId));
        using var corrupt = await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemIds[0]}/history");
        await AssertErrorAsync(corrupt, HttpStatusCode.ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
        var corruptBody = await corrupt.Content.ReadAsStringAsync();
        Assert.False(corruptBody.Contains("CREDENTIAL_MARKER", StringComparison.Ordinal), "Credential marker reached an error response.");
        Assert.False(corruptBody.Contains("PRIVATE_PATH_MARKER", StringComparison.Ordinal), "Path marker reached an error response.");
    }

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero))
    };

    private static SeededTarget SeedSession(MonitorTempDirectory temp, int itemCount)
    {
        var time = temp.TimeProvider.GetUtcNow();
        var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
            time.AddMinutes(-1), time, time, SessionRawRetentionState.Expiring, time.AddMinutes(-1), time);
        var events = Enumerable.Range(0, itemCount).Select(index => new ObservedSessionEvent(
            Guid.Parse($"018f2b4e-7c1a-7f1a-8a2b-{index + 0x6072:X12}"), sessionId, null,
            SessionSourceSurface.CopilotSdk, null, $"trace-history-{index}", "received", "copilot-sdk-stream",
            $"history-event-{index}", "user.message", time.AddSeconds(index), SessionContentState.Available)).ToArray();
        var content = events.Select((item, index) => new SessionEventContent(
            item.EventId, "application/json", $"{{\"synthetic\":{index}}}", time.AddSeconds(index), time.AddDays(90).AddSeconds(index))).ToArray();
        store.Write(new(new(session, [], [], events), content));
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' ORDER BY item_id;";
        using var reader = command.ExecuteReader();
        var itemIds = new List<string>();
        while (reader.Read()) itemIds.Add(reader.GetString(0));
        return new(sessionId.ToString("D"), itemIds);
    }

    private static void AppendEvents(RetentionCatalogStore catalog, RetentionMutationTargetKind kind, string targetId, int count, DateTimeOffset start)
    {
        for (var index = 0; index < count; index++)
        {
            var nonce = BitConverter.GetBytes(index + 1).Concat(new byte[12]).ToArray();
            catalog.AppendAuditEvent(Audit(nonce, kind, targetId, start.AddSeconds(index)));
        }
    }

    private static RetentionAuditEvent Audit(byte nonce, RetentionMutationTargetKind kind, string targetId, DateTimeOffset occurredAt) =>
        Audit(Enumerable.Repeat(nonce, 16).ToArray(), kind, targetId, occurredAt);

    private static RetentionAuditEvent Audit(byte[] nonce, RetentionMutationTargetKind kind, string targetId, DateTimeOffset occurredAt) => new(
        RetentionMutationIdentifiers.CreateAuditEventId(nonce),
        $"operation-{Convert.ToHexString(nonce).ToLowerInvariant()}",
        RetentionMutationConstants.EventType,
        kind,
        targetId,
        kind == RetentionMutationTargetKind.Session ? targetId : null,
        occurredAt,
        RetentionMutationConstants.ActorLabel,
        RetentionMutationOperation.Pin,
        RetentionMutationReasonCodes.ResearchNeeded,
        null,
        RetentionPinState.Unpinned,
        RetentionPinState.Pinned,
        new(1, 0, 0, 0, 0, 0, 0),
        new(0, 1, 0, 0, 0, 0, 0),
        RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat((byte)(nonce[0] % 200 + 1), 32).ToArray()),
        "v1-" + new string('1', 64),
        "v1-" + new string('2', 64),
        "sha256-" + new string('3', 64),
        RetentionMutationCompletionCodes.PinApplied,
        null);

    private static MonitorHostTestOptions TestOptions() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
    };

    private static async Task AssertSuccessHeadersAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("ETag"));
        Assert.Null(response.Content.Headers.LastModified);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        await Task.CompletedTask;
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string error)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            string.Equals($"{{\"error\":\"{error}\"}}", body, StringComparison.Ordinal),
            "Response did not match the fixed retention error contract.");
    }

    private static void AssertPropertyNames(JsonElement value, params string[] expected) =>
        Assert.Equal(expected, value.EnumerateObject().Select(static property => property.Name).ToArray());

    private static void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    private sealed record SeededTarget(string SessionId, IReadOnlyList<string> ItemIds);
}

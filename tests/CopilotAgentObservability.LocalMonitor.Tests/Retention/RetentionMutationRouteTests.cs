using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

[CollectionDefinition("Retention mutation API routes", DisableParallelization = true)]
public sealed class RetentionMutationRouteCollection
{
}

[Collection("Retention mutation API routes")]
public sealed class RetentionMutationRouteTests
{
    [Fact]
    public async Task PreviewRoute_ReturnsThePinnedPreviewContract()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var sessionId = SeedSession(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
            UseUserSecrets = false,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/retention/v1/previews")
        {
            Content = new StringContent($"{{\"target\":{{\"kind\":\"session\",\"id\":\"{sessionId}\"}},\"operation\":\"pin\",\"scope\":\"session_items\",\"reason_code\":\"research_needed\",\"comment\":null}}", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", WorkflowKey(1));
        request.Headers.Add("x-monitor-csrf", "local-monitor");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertNoStore(response);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStreamAsync());
        Assert.Equal(1, json.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Equal("actionable", json.RootElement.GetProperty("result").GetString());
        Assert.Equal(sessionId, json.RootElement.GetProperty("target_id").GetString());
        Assert.Equal("pin", json.RootElement.GetProperty("operation").GetString());
        Assert.Equal("session_items", json.RootElement.GetProperty("scope").GetString());
        Assert.False(json.RootElement.TryGetProperty("ConfirmationToken", out _));
    }

    [Fact]
    public async Task WorkflowRoutes_ReturnExactSnakeCaseDtosAndExposeTokenOnlyAtConfirmationIssue()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var sessionId = SeedSession(temp);
        var itemId = ReadItemId(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartRetentionCleanupWorker = false,
            UseUserSecrets = false,
        });
        var workflowKey = WorkflowKey(2);

        using var preview = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/previews", $"{{\"target\":{{\"kind\":\"session\",\"id\":\"{sessionId}\"}},\"operation\":\"pin\",\"scope\":\"session_items\",\"reason_code\":\"research_needed\",\"comment\":null}}", workflowKey);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        AssertNoStore(preview);
        var previewBody = await preview.Content.ReadAsStringAsync();
        using var previewJson = JsonDocument.Parse(previewBody);
        AssertPropertyNames(previewJson.RootElement,
            "schema_version", "result", "empty_reason", "mutation_allowed", "preview_id", "target_kind", "target_id", "operation", "scope",
            "source_state", "session_completeness", "content_state", "current_state", "target_items", "target_item_count", "store_kind_summary",
            "excluded_item_count", "excluded_items_by_reason", "capture_expiry_policy_summary", "retained_metadata_impact",
            "active_cleanup_exclusion_conflicts", "backup_non_purge_warning_code", "expected_state_version", "target_item_set_digest",
            "preview_digest", "confirmation_expires_at", "rejection_code");
        Assert.Contains("empty_reason", previewJson.RootElement.EnumerateObject().Select(static property => property.Name));
        Assert.Equal(JsonValueKind.Null, previewJson.RootElement.GetProperty("empty_reason").ValueKind);
        AssertTokenAbsent(previewBody);

        using var storedPreview = await host.Client.GetAsync($"/api/retention/v1/previews/{previewJson.RootElement.GetProperty("preview_id").GetString()}");
        Assert.Equal(HttpStatusCode.OK, storedPreview.StatusCode);
        Assert.Equal("no-store", storedPreview.Headers.CacheControl?.ToString());
        Assert.True(
            string.Equals(previewBody, await storedPreview.Content.ReadAsStringAsync(), StringComparison.Ordinal),
            "Stored preview replay did not preserve the exact response.");

        var confirmationRequest = $"{{\"preview_id\":\"{previewJson.RootElement.GetProperty("preview_id").GetString()}\",\"preview_digest\":\"{previewJson.RootElement.GetProperty("preview_digest").GetString()}\"}}";
        using var confirmation = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/confirmations", confirmationRequest, workflowKey);
        Assert.True(confirmation.IsSuccessStatusCode, "Confirmation issue did not succeed.");
        AssertNoStore(confirmation);
        var confirmationBody = await confirmation.Content.ReadAsStringAsync();
        using var confirmationJson = JsonDocument.Parse(confirmationBody);
        var token = confirmationJson.RootElement.GetProperty("confirmation_token").GetString()!;
        Assert.True(token.StartsWith("rt90v1_", StringComparison.Ordinal), "Confirmation issue returned noncanonical material.");
        Assert.Equal(new[] { "schema_version", "confirmation_id", "confirmation_token", "confirmation_expires_at" }, confirmationJson.RootElement.EnumerateObject().Select(static property => property.Name).ToArray());

        using var mutation = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/mutations", $"{{\"confirmation_token\":\"{token}\",\"operation\":\"pin\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"{sessionId}\"}}", workflowKey);
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        AssertNoStore(mutation);
        var mutationBody = await mutation.Content.ReadAsStringAsync();
        AssertTokenAbsent(mutationBody, token);
        using var mutationJson = JsonDocument.Parse(mutationBody);
        AssertPropertyNames(mutationJson.RootElement,
            "schema_version", "operation_id", "result_code", "target_kind", "target_id", "operation", "scope", "target_item_count",
            "pin_state", "lifecycle_counts", "read_denied", "audit_event_id", "expected_version", "result_version",
            "backup_non_purge_warning_code", "idempotent_replay", "created_at", "completed_at");
        var operationId = mutationJson.RootElement.GetProperty("operation_id").GetString()!;
        Assert.Equal("retention_pin_applied", mutationJson.RootElement.GetProperty("result_code").GetString());
        Assert.Contains("audit_event_id", mutationJson.RootElement.EnumerateObject().Select(static property => property.Name));

        using var consumedRetry = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/confirmations", confirmationRequest, workflowKey);
        await AssertErrorAsync(consumedRetry, HttpStatusCode.Conflict, RetentionMutationErrorCodes.ConfirmationConsumed);
        Assert.True(
            string.Equals($"/api/retention/v1/mutations/{operationId}", consumedRetry.Headers.Location?.OriginalString, StringComparison.Ordinal),
            "Consumed confirmation returned an invalid operation-status location.");
        AssertTokenAbsent(await consumedRetry.Content.ReadAsStringAsync(), token);

        using var status = await host.Client.GetAsync($"/api/retention/v1/mutations/{operationId}");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        AssertNoStore(status);
        using var statusJson = JsonDocument.Parse(await status.Content.ReadAsStreamAsync());
        AssertPropertyNames(statusJson.RootElement,
            "schema_version", "operation_id", "operation", "target_kind", "target_id", "status", "result_code", "lifecycle_counts",
            "read_denied", "audit_event_id", "idempotent_replay", "created_at", "completed_at", "backup_non_purge_warning_code");
        Assert.Equal("committed", statusJson.RootElement.GetProperty("status").GetString());

        using var item = await host.Client.GetAsync($"/api/retention/v1/items/{itemId}");
        Assert.Equal(HttpStatusCode.OK, item.StatusCode);
        AssertNoStore(item);
        using var itemJson = JsonDocument.Parse(await item.Content.ReadAsStreamAsync());
        AssertPropertyNames(itemJson.RootElement,
            "schema_version", "item_id", "store_kind", "state", "pin_state", "delete_state", "policy_id", "policy_version",
            "captured_at", "expires_at", "read_denied_at", "queued_at", "deletion_started_at", "deleted_at", "attempt_count",
            "retry_exhausted", "error_code", "retry_at", "revision", "session_id");
        Assert.Equal("pinned", itemJson.RootElement.GetProperty("pin_state").GetString());
        Assert.Equal(sessionId, itemJson.RootElement.GetProperty("session_id").GetString());
        AssertTokenAbsent(await host.Client.GetStringAsync($"/api/retention/v1/items/{itemId}"), token);
    }

    [Fact]
    public async Task PostRoutes_RejectMissingIdempotencyKeyAndMissingCsrfWithFixedBodies()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var sessionId = SeedSession(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());
        var token = RetentionMutationToken.Create(Enumerable.Repeat((byte)3, 16).ToArray(), Enumerable.Repeat((byte)4, 32).ToArray());
        var requests = new[]
        {
            (Path: "/api/retention/v1/previews", Body: PreviewJson(sessionId)),
            (Path: "/api/retention/v1/confirmations", Body: "{\"preview_id\":\"rpv1_AAAAAAAAAAAAAAAAAAAAAA\",\"preview_digest\":\"sha256-0000000000000000000000000000000000000000000000000000000000000000\"}"),
            (Path: "/api/retention/v1/mutations", Body: $"{{\"confirmation_token\":\"{token}\",\"operation\":\"pin\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"{sessionId}\"}}"),
        };

        foreach (var requestData in requests)
        {
            using var missingKey = await SendRawAsync(host, HttpMethod.Post, requestData.Path, requestData.Body, csrf: true, workflowKey: null);
            await AssertErrorAsync(missingKey, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.IdempotencyKeyInvalid);

            using var invalidKey = await SendRawAsync(host, HttpMethod.Post, requestData.Path, requestData.Body, csrf: true, workflowKey: "rid1_invalid");
            await AssertErrorAsync(invalidKey, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.IdempotencyKeyInvalid);

            using var missingCsrf = await SendRawAsync(host, HttpMethod.Post, requestData.Path, requestData.Body, csrf: false, workflowKey: WorkflowKey(30));
            await AssertErrorAsync(missingCsrf, HttpStatusCode.Forbidden, "csrf_required");
        }
    }

    [Fact]
    public async Task PreviewAndConfirmationRoutes_MapRejectionAndDigestErrorsWithoutTokenLeak()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var sessionId = SeedSession(temp);
        var itemId = ReadItemId(temp);
        SetState(temp, itemId, "deletion_queued", denied: true);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());
        var workflowKey = WorkflowKey(31);

        using var preview = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/previews", PreviewJson(sessionId), workflowKey);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        AssertNoStore(preview);
        var previewBody = await preview.Content.ReadAsStringAsync();
        using var previewJson = JsonDocument.Parse(previewBody);
        Assert.False(previewJson.RootElement.GetProperty("mutation_allowed").GetBoolean());
        Assert.Equal(RetentionMutationErrorCodes.PinReadDenied, previewJson.RootElement.GetProperty("rejection_code").GetString());
        AssertTokenAbsent(previewBody);

        using var rejection = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/confirmations", $"{{\"preview_id\":\"{previewJson.RootElement.GetProperty("preview_id").GetString()}\",\"preview_digest\":\"{previewJson.RootElement.GetProperty("preview_digest").GetString()}\"}}", workflowKey);
        await AssertErrorAsync(rejection, HttpStatusCode.Conflict, RetentionMutationErrorCodes.PinReadDenied);
        AssertTokenAbsent(await rejection.Content.ReadAsStringAsync());

        using var digestMismatch = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/confirmations", $"{{\"preview_id\":\"{previewJson.RootElement.GetProperty("preview_id").GetString()}\",\"preview_digest\":\"sha256-{new string('0', 64)}\"}}", workflowKey);
        await AssertErrorAsync(digestMismatch, HttpStatusCode.Conflict, RetentionMutationErrorCodes.PreviewDigestMismatch);
        Assert.False(
            (await digestMismatch.Content.ReadAsStringAsync()).Contains(itemId, StringComparison.Ordinal),
            "Target material was reflected in a digest-mismatch response.");
    }

    [Fact]
    public async Task MutationRoute_Returns401ForInvalidTokenAnd200ForAlreadyQueuedDelete()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var sessionId = SeedSession(temp);
        var itemId = ReadItemId(temp);
        SetState(temp, itemId, "deletion_queued", denied: true);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());

        using var invalid = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/mutations", $"{{\"confirmation_token\":\"rt90v1_invalid\",\"operation\":\"delete_now\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"{sessionId}\"}}", WorkflowKey(40));
        await AssertErrorAsync(invalid, HttpStatusCode.Unauthorized, RetentionMutationErrorCodes.ConfirmationInvalid);
        Assert.False(
            (await invalid.Content.ReadAsStringAsync()).Contains("rt90v1_invalid", StringComparison.Ordinal),
            "Invalid confirmation input was reflected in an error response.");

        var workflowKey = WorkflowKey(41);
        using var preview = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/previews", PreviewJson(sessionId, "delete_now"), workflowKey);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        AssertNoStore(preview);
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStreamAsync());
        using var confirmation = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/confirmations", $"{{\"preview_id\":\"{previewJson.RootElement.GetProperty("preview_id").GetString()}\",\"preview_digest\":\"{previewJson.RootElement.GetProperty("preview_digest").GetString()}\"}}", workflowKey);
        Assert.True(confirmation.IsSuccessStatusCode, "Confirmation issue did not succeed.");
        AssertNoStore(confirmation);
        using var confirmationJson = JsonDocument.Parse(await confirmation.Content.ReadAsStreamAsync());
        var token = confirmationJson.RootElement.GetProperty("confirmation_token").GetString()!;
        using var mutation = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/mutations", $"{{\"confirmation_token\":\"{token}\",\"operation\":\"delete_now\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"{sessionId}\"}}", workflowKey);
        Assert.Equal(HttpStatusCode.OK, mutation.StatusCode);
        AssertNoStore(mutation);
        using var mutationJson = JsonDocument.Parse(await mutation.Content.ReadAsStreamAsync());
        Assert.Equal(RetentionMutationErrorCodes.DeleteAlreadyQueued, mutationJson.RootElement.GetProperty("result_code").GetString());
        AssertTokenAbsent(await mutation.Content.ReadAsStringAsync(), token);
    }

    [Fact]
    public async Task MutationRoute_MapsAuditAppendFailureToExactAuditWriteFailed503()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var sessionId = SeedSession(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());
        var workflowKey = WorkflowKey(43);
        var token = await IssueMutationTokenAsync(host, sessionId, workflowKey);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_retention_audit_append BEFORE INSERT ON retention_audit_events BEGIN SELECT RAISE(ABORT, 'synthetic audit append failure'); END;";
            command.ExecuteNonQuery();
        }

        using var mutation = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/mutations",
            $"{{\"confirmation_token\":\"{token}\",\"operation\":\"pin\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"{sessionId}\"}}", workflowKey);

        await AssertErrorAsync(mutation, HttpStatusCode.ServiceUnavailable, RetentionMutationErrorCodes.AuditWriteFailed);
    }

    [Fact]
    public async Task MutationRoute_MapsCatalogOpenFailureToExactCatalogUnavailable503()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var unavailablePath = Path.Combine(temp.Path, "unavailable-retention.db");
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions(
            (_, timeProvider) => new RetentionMutationApplicationService(new RetentionCatalogStore(unavailablePath, timeProvider), timeProvider)));
        var token = RetentionMutationToken.Create(
            Enumerable.Repeat((byte)0xff, RetentionMutationIdentifierFormats.NonceByteLength).ToArray(),
            Enumerable.Repeat((byte)0x22, RetentionMutationIdentifierFormats.SecretByteLength).ToArray());

        using var mutation = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/mutations",
            $"{{\"confirmation_token\":\"{token}\",\"operation\":\"pin\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071\"}}",
            WorkflowKey(44));

        await AssertErrorAsync(mutation, HttpStatusCode.ServiceUnavailable, RetentionMutationErrorCodes.CatalogUnavailable);
    }

    [Fact]
    public async Task ReadRoutes_ReturnFixedNotFoundErrorsAndCrossOriginIsRejected()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        SeedSession(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());

        using var preview = await host.Client.GetAsync("/api/retention/v1/previews/rpv1_AAAAAAAAAAAAAAAAAAAAAA");
        await AssertErrorAsync(preview, HttpStatusCode.NotFound, RetentionMutationErrorCodes.PreviewNotFound);
        using var operation = await host.Client.GetAsync("/api/retention/v1/mutations/missing-operation");
        await AssertErrorAsync(operation, HttpStatusCode.NotFound, RetentionMutationErrorCodes.OperationNotFound);
        using var item = await host.Client.GetAsync("/api/retention/v1/items/missing-item");
        await AssertErrorAsync(item, HttpStatusCode.NotFound, RetentionMutationErrorCodes.TargetNotFound);

        using var crossOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/retention/v1/status");
        crossOrigin.Headers.Add("Sec-Fetch-Site", "cross-site");
        using var crossOriginResponse = await host.Client.SendAsync(crossOrigin);
        await AssertErrorAsync(crossOriginResponse, HttpStatusCode.Forbidden, "cross_origin_forbidden");
    }

    [Fact]
    public async Task PreviewRoute_Returns413WithoutCreatingARecordWhenExactTargetSetExceedsLimit()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(new DateTimeOffset(2026, 7, 20, 12, 0, 0, TimeSpan.Zero)) };
        var sessionId = SeedSession(temp, itemCount: 101);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());

        using var response = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/previews", PreviewJson(sessionId), WorkflowKey(50));
        await AssertErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, RetentionMutationErrorCodes.TargetLimitExceeded);

        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM retention_mutation_previews;";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    private static string SeedSession(MonitorTempDirectory temp, int itemCount = 1)
    {
        var time = (MutableTimeProvider)temp.TimeProvider;
        var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
        var sessionStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, time);
        sessionStore.CreateSchema();
        var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
            time.GetUtcNow().AddMinutes(-1), time.GetUtcNow(), time.GetUtcNow(), SessionRawRetentionState.Expiring, time.GetUtcNow().AddMinutes(-1), time.GetUtcNow());
        var events = Enumerable.Range(0, itemCount).Select(index =>
        {
            var eventId = Guid.Parse($"018f2b4e-7c1a-7f1a-8a2b-{index + 0x6072:X12}");
            return new ObservedSessionEvent(eventId, sessionId, null, SessionSourceSurface.CopilotSdk, null,
                $"trace-route-{index}", "received", "copilot-sdk-stream", $"event-route-{index}", "user.message", time.GetUtcNow().AddSeconds(index), SessionContentState.Available);
        }).ToArray();
        var content = events.Select((item, index) => new SessionEventContent(item.EventId, "application/json", $"{{\"synthetic\":{index}}}", time.GetUtcNow().AddSeconds(index), time.GetUtcNow().AddDays(90).AddSeconds(index))).ToArray();
        sessionStore.Write(new(new(session, [], [], events), content));
        return sessionId.ToString("D");
    }

    private static string ReadItemId(MonitorTempDirectory temp)
    {
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id FROM retention_items WHERE store_kind='session_event_content';";
        return (string)command.ExecuteScalar()!;
    }

    private static void SetState(MonitorTempDirectory temp, string itemId, string state, bool denied)
    {
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE retention_items SET state=$state,read_denied_at=$denied,queued_at=$denied WHERE item_id=$item_id;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$denied", denied ? temp.TimeProvider.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value);
        command.Parameters.AddWithValue("$item_id", itemId);
        command.ExecuteNonQuery();
    }

    private static string PreviewJson(string sessionId, string operation = "pin") =>
        $"{{\"target\":{{\"kind\":\"session\",\"id\":\"{sessionId}\"}},\"operation\":\"{operation}\",\"scope\":\"session_items\",\"reason_code\":\"research_needed\",\"comment\":null}}";

    private static async Task<string> IssueMutationTokenAsync(RunningMonitorHost host, string sessionId, string workflowKey)
    {
        using var preview = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/previews", PreviewJson(sessionId), workflowKey);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        using var previewJson = JsonDocument.Parse(await preview.Content.ReadAsStreamAsync());
        using var confirmation = await SendJsonAsync(host, HttpMethod.Post, "/api/retention/v1/confirmations",
            $"{{\"preview_id\":\"{previewJson.RootElement.GetProperty("preview_id").GetString()}\",\"preview_digest\":\"{previewJson.RootElement.GetProperty("preview_digest").GetString()}\"}}", workflowKey);
        var confirmationBody = await confirmation.Content.ReadAsStringAsync();
        Assert.True(confirmation.StatusCode == HttpStatusCode.OK, "Confirmation issue did not succeed.");
        using var confirmationJson = JsonDocument.Parse(confirmationBody);
        return confirmationJson.RootElement.GetProperty("confirmation_token").GetString()!;
    }

    private static MonitorHostTestOptions TestOptions(
        Func<RetentionCatalogStore, TimeProvider, RetentionMutationApplicationService>? retentionMutationApplicationFactory = null) => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
        RetentionMutationApplicationFactory = retentionMutationApplicationFactory,
    };

    private static async Task<HttpResponseMessage> SendJsonAsync(RunningMonitorHost host, HttpMethod method, string path, string json, string workflowKey)
        => await SendRawAsync(host, method, path, json, csrf: true, workflowKey);

    private static async Task<HttpResponseMessage> SendRawAsync(RunningMonitorHost host, HttpMethod method, string path, string json, bool csrf, string? workflowKey)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        if (workflowKey is not null) request.Headers.Add("Idempotency-Key", workflowKey);
        if (csrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        using (request)
        {
            return await host.Client.SendAsync(request);
        }
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            string.Equals($"{{\"error\":\"{code}\"}}", body, StringComparison.Ordinal),
            "Response did not match the fixed retention error contract.");
    }

    private static void AssertTokenAbsent(string value, string? token = null)
    {
        var containsToken = token is null
            ? value.Contains(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, StringComparison.Ordinal)
            : value.Contains(token, StringComparison.Ordinal);
        Assert.False(containsToken, "Plaintext confirmation material reached a non-issue response.");
    }

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());

    private static void AssertPropertyNames(JsonElement value, params string[] expected) =>
        Assert.Equal(expected, value.EnumerateObject().Select(static property => property.Name).ToArray());

    private static string WorkflowKey(byte value) => RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat(value, 32).ToArray());
}

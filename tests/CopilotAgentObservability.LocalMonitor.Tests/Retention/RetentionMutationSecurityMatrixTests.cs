using System.Net;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

[Collection("Retention mutation API routes")]
public sealed class RetentionMutationSecurityMatrixTests
{
    // RetentionMutationHttpMatrixTests owns individual protocol/error branches,
    // RetentionMutationUiPlaywrightTests owns DOM/URL/browser-storage assertions,
    // and the Canvas helper suite owns its navigation-only boundary. This class
    // pins only the cross-surface rows not expressed by those focused suites.
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 6, 0, 0, TimeSpan.Zero);

    private static readonly string[] SensitiveMarkers =
    [
        "RAW_BODY_MATRIX_MARKER",
        "C:\\synthetic-private\\matrix-marker.json",
        "credential=matrix-marker",
        "rowid=matrix-marker",
        "PROMPT_LABEL_MATRIX_MARKER",
        "rt90v1_SYNTHETIC_TOKEN_MARKER",
    ];

    [Fact]
    public async Task RetentionRoutes_ApplyHostOriginCsrfNoStoreAndFixedErrorBoundaries()
    {
        using var temp = NewTemp();
        var seeded = SeedSession(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());
        Assert.True(IPAddress.TryParse(new Uri(host.Url).Host, out var address) && IPAddress.IsLoopback(address));

        var readPaths = new[]
        {
            "/api/retention/v1/status",
            $"/api/retention/v1/sessions/{seeded.SessionId}",
            $"/api/retention/v1/items/{seeded.ItemId}",
            $"/api/retention/v1/sessions/{seeded.SessionId}/history",
            $"/api/retention/v1/items/{seeded.ItemId}/history",
            "/api/retention/v1/mutations/missing-operation",
        };

        foreach (var path in readPaths)
        {
            using var crossSiteRequest = new HttpRequestMessage(HttpMethod.Get, path);
            crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
            using var crossSite = await host.Client.SendAsync(crossSiteRequest);
            await AssertFixedErrorAsync(crossSite, HttpStatusCode.Forbidden, "cross_origin_forbidden");
        }

        using (var invalidHostRequest = new HttpRequestMessage(HttpMethod.Get, "/api/retention/v1/status"))
        {
            invalidHostRequest.Headers.Host = "example.invalid";
            using var invalidHost = await host.Client.SendAsync(invalidHostRequest);
            await AssertFixedErrorAsync(invalidHost, HttpStatusCode.BadRequest, "invalid_host");
        }

        var workflowKey = WorkflowKey(1);
        var posts = new[]
        {
            (Path: "/api/retention/v1/previews", Body: PreviewJson(seeded.SessionId)),
            (Path: "/api/retention/v1/confirmations", Body: $$"""{"preview_id":"{{RetentionMutationIdentifiers.CreatePreviewId(new byte[16])}}","preview_digest":"sha256-{{new string('0', 64)}}"}"""),
            (Path: "/api/retention/v1/mutations", Body: $$"""{"confirmation_token":"rt90v1_invalid","operation":"pin","scope":"session_items","target_kind":"session","target_id":"{{seeded.SessionId}}"}"""),
        };
        foreach (var post in posts)
        {
            using var request = JsonRequest(HttpMethod.Post, post.Path, post.Body, workflowKey, includeCsrf: false);
            using var response = await host.Client.SendAsync(request);
            await AssertFixedErrorAsync(response, HttpStatusCode.Forbidden, "csrf_required");
        }
    }

    [Fact]
    public async Task SensitiveMarkersAndPlaintextConfirmationToken_StayOutOfEveryRetentionSurfaceAndDatabaseArtifact()
    {
        using var temp = NewTemp();
        var seeded = SeedSession(temp);
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: TestOptions());
        var workflowKey = WorkflowKey(2);
        var bodies = new List<string>();

        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync("/api/retention/v1/status")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/api/retention/v1/sessions/{seeded.SessionId}")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemId}")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/api/retention/v1/sessions/{seeded.SessionId}/history")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemId}/history")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/retention/session/{seeded.SessionId}")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/retention/item/{seeded.ItemId}")));

        using var previewResponse = await SendJsonAsync(host, "/api/retention/v1/previews", PreviewJson(seeded.SessionId), workflowKey);
        var previewBody = await ReadBodyAsync(previewResponse);
        bodies.Add(previewBody);
        using var previewJson = JsonDocument.Parse(previewBody);

        var confirmationBody = $$"""
            {"preview_id":"{{previewJson.RootElement.GetProperty("preview_id").GetString()}}","preview_digest":"{{previewJson.RootElement.GetProperty("preview_digest").GetString()}}"}
            """;
        using var confirmationResponse = await SendJsonAsync(host, "/api/retention/v1/confirmations", confirmationBody, workflowKey);
        var confirmationJsonText = await ReadBodyAsync(confirmationResponse);
        bodies.Add(confirmationJsonText);
        using var confirmationJson = JsonDocument.Parse(confirmationJsonText);
        var confirmationToken = confirmationJson.RootElement.GetProperty("confirmation_token").GetString()!;

        var mutationBody = $$"""
            {"confirmation_token":"{{confirmationToken}}","operation":"pin","scope":"session_items","target_kind":"session","target_id":"{{seeded.SessionId}}"}
            """;
        using var mutationResponse = await SendJsonAsync(host, "/api/retention/v1/mutations", mutationBody, workflowKey);
        var mutationJsonText = await ReadBodyAsync(mutationResponse);
        bodies.Add(mutationJsonText);
        using var mutationJson = JsonDocument.Parse(mutationJsonText);
        var operationId = mutationJson.RootElement.GetProperty("operation_id").GetString();

        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/api/retention/v1/mutations/{operationId}")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/api/retention/v1/sessions/{seeded.SessionId}/history")));
        bodies.Add(await ReadBodyAsync(await host.Client.GetAsync($"/api/retention/v1/items/{seeded.ItemId}/history")));

        using var invalid = JsonRequest(
            HttpMethod.Post,
            "/api/retention/v1/previews",
            PreviewJson(seeded.SessionId)[..^1] + ",\"unknown\":\"credential=matrix-marker\"}",
            WorkflowKey(3));
        using var invalidResponse = await host.Client.SendAsync(invalid);
        await AssertFixedErrorAsync(invalidResponse, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.RequestInvalid);
        bodies.Add(await invalidResponse.Content.ReadAsStringAsync());

        foreach (var body in bodies)
        {
            foreach (var marker in SensitiveMarkers)
            {
                Assert.False(
                    body.Contains(marker, StringComparison.Ordinal),
                    "Sensitive marker reached a retention response surface.");
            }
        }

        foreach (var body in bodies.Where(body => !string.Equals(body, confirmationJsonText, StringComparison.Ordinal)))
        {
            Assert.False(
                body.Contains(confirmationToken, StringComparison.Ordinal),
                "Plaintext confirmation material reached a non-issue response.");
        }

        AssertPlaintextAbsentFromDatabaseArtifacts(temp.DatabasePath, confirmationToken);
    }

    private static MonitorTempDirectory NewTemp() => new()
    {
        TimeProvider = new MutableTimeProvider(Now),
    };

    private static SeededTarget SeedSession(MonitorTempDirectory temp)
    {
        var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
        var eventId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6072");
        var store = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        store.CreateSchema();
        var session = new ObservedSession(
            sessionId,
            ObservedSessionStatus.Completed,
            SessionCompleteness.Full,
            null,
            null,
            Now.AddMinutes(-1),
            Now,
            Now,
            SessionRawRetentionState.Expiring,
            Now.AddMinutes(-1),
            Now);
        var observedEvent = new ObservedSessionEvent(
            eventId,
            sessionId,
            null,
            SessionSourceSurface.CopilotSdk,
            null,
            "trace-security-matrix",
            "received",
            "copilot-sdk-stream",
            "event-security-matrix",
            "user.message",
            Now,
            SessionContentState.Available);
        var content = new SessionEventContent(
            eventId,
            "application/json",
            JsonSerializer.Serialize(new { raw = SensitiveMarkers }),
            Now,
            Now.AddDays(90));
        store.Write(new(new(session, [], [], [observedEvent]), [content]));

        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT item_id FROM retention_items WHERE store_kind='session_event_content';";
        var itemId = (string)read.ExecuteScalar()!;
        using var update = connection.CreateCommand();
        update.CommandText = "UPDATE retention_items SET private_locator=$locator WHERE item_id=$item;";
        update.Parameters.AddWithValue("$locator", string.Join('|', SensitiveMarkers));
        update.Parameters.AddWithValue("$item", itemId);
        update.ExecuteNonQuery();
        return new(sessionId.ToString("D"), itemId);
    }

    private static MonitorHostTestOptions TestOptions() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartRetentionCleanupWorker = false,
        UseUserSecrets = false,
    };

    private static string PreviewJson(string sessionId) => $$"""
        {"target":{"kind":"session","id":"{{sessionId}}"},"operation":"pin","scope":"session_items","reason_code":"research_needed","comment":null}
        """;

    private static string WorkflowKey(byte value) =>
        RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat(value, 32).ToArray());

    private static HttpRequestMessage JsonRequest(
        HttpMethod method,
        string path,
        string body,
        string workflowKey,
        bool includeCsrf = true)
    {
        var request = new HttpRequestMessage(method, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Idempotency-Key", workflowKey);
        if (includeCsrf) request.Headers.Add("x-monitor-csrf", "local-monitor");
        return request;
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        RunningMonitorHost host,
        string path,
        string body,
        string workflowKey) =>
        await host.Client.SendAsync(JsonRequest(HttpMethod.Post, path, body, workflowKey));

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response)
    {
        using (response)
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
            Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
            return await response.Content.ReadAsStringAsync();
        }
    }

    private static async Task AssertFixedErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            string.Equals($"{{\"error\":\"{code}\"}}", body, StringComparison.Ordinal),
            "Response did not match the fixed retention error contract.");
    }

    private static void AssertPlaintextAbsentFromDatabaseArtifacts(string databasePath, string token)
    {
        var needle = Encoding.ASCII.GetBytes(token);
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            if (!File.Exists(path)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            Assert.False(ContainsSequence(copy.ToArray(), needle), "Plaintext confirmation material reached a SQLite artifact.");
        }
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var index = 0; index <= haystack.Length - needle.Length; index++)
        {
            if (haystack.AsSpan(index, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    private sealed record SeededTarget(string SessionId, string ItemId);
}

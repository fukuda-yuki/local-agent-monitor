using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

[Collection("Retention mutation API routes")]
public sealed class RetentionMutationHttpMatrixTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ConfirmationRoute_ReissuesUnconsumedTokenAndInvalidatesOldToken()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workflowKey = fixture.WorkflowKey(60);
        var preview = await fixture.CreatePreviewAsync(workflowKey);

        var firstToken = await fixture.IssueTokenAsync(preview, workflowKey);
        var secondToken = await fixture.IssueTokenAsync(preview, workflowKey);

        Assert.NotEqual(firstToken, secondToken);
        using var oldTokenMutation = await fixture.MutateAsync(firstToken, workflowKey);
        await AssertErrorAsync(oldTokenMutation, HttpStatusCode.Unauthorized, RetentionMutationErrorCodes.ConfirmationInvalid);
        Assert.DoesNotContain(firstToken, await oldTokenMutation.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmationRoute_MapsEmptyAndExpiredPreviewsWithoutTokenLeak()
    {
        await using (var emptyFixture = await Fixture.CreateAsync(itemCount: 0))
        {
            var workflowKey = emptyFixture.WorkflowKey(61);
            var preview = await emptyFixture.CreatePreviewAsync(workflowKey);

            using var response = await emptyFixture.IssueConfirmationAsync(preview, workflowKey);

            await AssertErrorAsync(response, HttpStatusCode.Conflict, RetentionMutationErrorCodes.TargetEmpty);
            Assert.DoesNotContain(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        await using (var expiredFixture = await Fixture.CreateAsync())
        {
            var workflowKey = expiredFixture.WorkflowKey(62);
            var preview = await expiredFixture.CreatePreviewAsync(workflowKey);
            expiredFixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);

            using var response = await expiredFixture.IssueConfirmationAsync(preview, workflowKey);

            await AssertErrorAsync(response, HttpStatusCode.Conflict, RetentionMutationErrorCodes.PreviewExpired);
            Assert.DoesNotContain(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task PreviewRoute_MapsIdempotencyConflictAndExpired()
    {
        await using (var conflictFixture = await Fixture.CreateAsync())
        {
            var workflowKey = conflictFixture.WorkflowKey(63);
            using var first = await conflictFixture.PostPreviewAsync(workflowKey, operation: "pin");
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);

            using var conflict = await conflictFixture.PostPreviewAsync(workflowKey, operation: "delete_now");

            await AssertErrorAsync(conflict, HttpStatusCode.Conflict, RetentionMutationErrorCodes.IdempotencyConflict);
        }

        await using (var expiredFixture = await Fixture.CreateAsync())
        {
            var workflowKey = expiredFixture.WorkflowKey(64);
            using var first = await expiredFixture.PostPreviewAsync(workflowKey);
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
            expiredFixture.Time.Advance(TimeSpan.FromDays(RetentionMutationConstants.IdempotencyLifetimeDays));

            using var expired = await expiredFixture.PostPreviewAsync(workflowKey);

            await AssertErrorAsync(expired, HttpStatusCode.Conflict, RetentionMutationErrorCodes.IdempotencyExpired);
        }
    }

    [Fact]
    public async Task ConfirmationRoute_MapsNonceCollisionToGenerationFailed503()
    {
        var token = RetentionMutationToken.Create(
            Enumerable.Repeat((byte)0xff, RetentionMutationIdentifierFormats.NonceByteLength).ToArray(),
            Enumerable.Repeat((byte)0x44, RetentionMutationIdentifierFormats.SecretByteLength).ToArray());
        await using var fixture = await Fixture.CreateAsync(applicationFactory: (catalog, timeProvider) =>
            new RetentionMutationApplicationService(
                catalog,
                timeProvider,
                confirmationIdGenerator: () => RetentionMutationIdentifiers.CreateConfirmationId(Enumerable.Repeat((byte)0x33, 16).ToArray()),
                tokenGenerator: () => token));
        var workflowKey = fixture.WorkflowKey(65);
        var preview = await fixture.CreatePreviewAsync(workflowKey);
        var firstToken = await fixture.IssueTokenAsync(preview, workflowKey);

        using var collision = await fixture.IssueConfirmationAsync(preview, workflowKey);

        Assert.Equal(token, firstToken);
        await AssertErrorAsync(collision, HttpStatusCode.ServiceUnavailable, RetentionMutationErrorCodes.ConfirmationGenerationFailed);
        Assert.DoesNotContain(token, await collision.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("consumed", RetentionMutationErrorCodes.ConfirmationConsumed)]
    [InlineData("expired", RetentionMutationErrorCodes.ConfirmationExpired)]
    [InlineData("binding", RetentionMutationErrorCodes.ConfirmationBindingMismatch)]
    [InlineData("target-first", RetentionMutationErrorCodes.ConfirmationTargetChanged)]
    [InlineData("pin", RetentionMutationErrorCodes.ConfirmationPinChanged)]
    [InlineData("retention", RetentionMutationErrorCodes.ConfirmationRetentionChanged)]
    [InlineData("conflict", RetentionMutationErrorCodes.ConfirmationConflictChanged)]
    [InlineData("version", RetentionMutationErrorCodes.ConfirmationVersionChanged)]
    [InlineData("pin-expired", RetentionMutationErrorCodes.PinExpired)]
    public async Task MutationRoute_MapsOrderedCommitStageFailures(string scenario, string expectedCode)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (scenario == "pin-expired")
        {
            fixture.Execute(
                "UPDATE retention_items SET expires_at=$expires WHERE item_id=$item;",
                ("$expires", fixture.Time.GetUtcNow().AddMinutes(2).ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                ("$item", fixture.ItemId));
        }
        var workflowKey = fixture.WorkflowKey(70);
        var preview = await fixture.CreatePreviewAsync(workflowKey);
        var token = await fixture.IssueTokenAsync(preview, workflowKey);
        var operation = "pin";

        switch (scenario)
        {
            case "consumed":
                fixture.Execute(
                    "UPDATE retention_confirmation_bindings SET consumed_at=$now WHERE consumed_at IS NULL;",
                    ("$now", fixture.Time.GetUtcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
                break;
            case "expired":
                fixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);
                break;
            case "binding":
                operation = "unpin";
                break;
            case "target-first":
                fixture.Execute(
                    "UPDATE retention_items SET item_id='replacement-item',state='retained_by_policy',policy_id='raw-default-90d',revision=revision+1 WHERE item_id=$item;",
                    ("$item", fixture.ItemId));
                break;
            case "pin":
                fixture.Execute("UPDATE retention_items SET state='retained_by_policy' WHERE item_id=$item;", ("$item", fixture.ItemId));
                break;
            case "retention":
                fixture.Execute(
                    "UPDATE retention_items SET captured_at=$captured,expires_at=$expires,policy_id='raw-default-90d' WHERE item_id=$item;",
                    ("$captured", fixture.Time.GetUtcNow().AddDays(-2).ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                    ("$expires", fixture.Time.GetUtcNow().AddDays(30).ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
                    ("$item", fixture.ItemId));
                break;
            case "conflict":
                fixture.Execute(
                    "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item,'access','matrix-owner',$expires,99);",
                    ("$item", fixture.ItemId),
                    ("$expires", fixture.Time.GetUtcNow().AddMinutes(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture)));
                break;
            case "version":
                fixture.Execute("UPDATE retention_items SET revision=revision+1 WHERE item_id=$item;", ("$item", fixture.ItemId));
                break;
            case "pin-expired":
                fixture.Time.Advance(TimeSpan.FromMinutes(2));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        using var response = await fixture.MutateAsync(token, workflowKey, operation);

        await AssertErrorAsync(response, HttpStatusCode.Conflict, expectedCode);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
    }

    [Fact]
    public async Task MutationRoute_MapsInjectedTransactionFailureToExact503()
    {
        await using var fixture = await Fixture.CreateAsync(applicationFactory: (catalog, timeProvider) =>
            new RetentionMutationApplicationService(catalog, timeProvider, mutationCheckpoint: point =>
            {
                if (point == "state_mutated") throw new InvalidOperationException("synthetic transaction failure");
            }));
        var workflowKey = fixture.WorkflowKey(80);
        var preview = await fixture.CreatePreviewAsync(workflowKey);
        var token = await fixture.IssueTokenAsync(preview, workflowKey);

        using var response = await fixture.MutateAsync(token, workflowKey);

        await AssertErrorAsync(response, HttpStatusCode.ServiceUnavailable, RetentionMutationErrorCodes.MutationTransactionFailed);
        Assert.DoesNotContain(token, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_items WHERE state='expiring' AND revision=1;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
    }

    [Fact]
    public async Task PostRoutes_RejectCrossSiteNonJsonUnknownFieldsAndBothOversizeForms()
    {
        await using var fixture = await Fixture.CreateAsync();
        var workflowKey = fixture.WorkflowKey(81);
        var token = RetentionMutationToken.Create(
            Enumerable.Repeat((byte)0x55, RetentionMutationIdentifierFormats.NonceByteLength).ToArray(),
            Enumerable.Repeat((byte)0x66, RetentionMutationIdentifierFormats.SecretByteLength).ToArray());
        var posts = new[]
        {
            (Path: "/api/retention/v1/previews", Body: Fixture.PreviewJson(fixture.SessionId)),
            (Path: "/api/retention/v1/confirmations", Body: $"{{\"preview_id\":\"{RetentionMutationIdentifiers.CreatePreviewId(new byte[16])}\",\"preview_digest\":\"sha256-{new string('0', 64)}\"}}"),
            (Path: "/api/retention/v1/mutations", Body: $"{{\"confirmation_token\":\"{token}\",\"operation\":\"pin\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"{fixture.SessionId}\"}}")
        };

        foreach (var post in posts)
        {
            using (var crossSiteRequest = fixture.Request(HttpMethod.Post, post.Path, post.Body, workflowKey))
            {
                crossSiteRequest.Headers.Add("Sec-Fetch-Site", "cross-site");
                using var crossSite = await fixture.Host.Client.SendAsync(crossSiteRequest);
                await AssertErrorAsync(crossSite, HttpStatusCode.Forbidden, "cross_origin_forbidden");
            }

            using (var nonJsonRequest = fixture.Request(HttpMethod.Post, post.Path, post.Body, workflowKey))
            {
                nonJsonRequest.Content!.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                using var nonJson = await fixture.Host.Client.SendAsync(nonJsonRequest);
                await AssertErrorAsync(nonJson, HttpStatusCode.UnsupportedMediaType, "unsupported_media_type");
            }

            using var unknownField = await fixture.PostAsync(
                post.Path,
                post.Body[..^1] + ",\"unknown_field\":\"synthetic-secret-marker\"}",
                workflowKey);
            await AssertErrorAsync(unknownField, HttpStatusCode.BadRequest, RetentionMutationErrorCodes.RequestInvalid);
            Assert.DoesNotContain("synthetic-secret-marker", await unknownField.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using (var declaredRequest = new HttpRequestMessage(HttpMethod.Post, "/api/retention/v1/previews"))
        {
            declaredRequest.Content = new StreamingContent(1_048_577, declareLength: true);
            declaredRequest.Headers.Add("Idempotency-Key", workflowKey);
            declaredRequest.Headers.Add("x-monitor-csrf", "local-monitor");
            using var declared = await fixture.Host.Client.SendAsync(declaredRequest);
            await AssertErrorAsync(declared, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        }

        using (var streamedRequest = new HttpRequestMessage(HttpMethod.Post, "/api/retention/v1/previews"))
        {
            streamedRequest.Content = new StreamingContent(1_048_577);
            streamedRequest.Headers.Add("Idempotency-Key", workflowKey);
            streamedRequest.Headers.Add("x-monitor-csrf", "local-monitor");
            using var streamed = await fixture.Host.Client.SendAsync(streamedRequest);
            await AssertErrorAsync(streamed, HttpStatusCode.RequestEntityTooLarge, "request_too_large");
        }
    }

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal($"{{\"error\":\"{code}\"}}", await response.Content.ReadAsStringAsync());
    }

    private sealed class StreamingContent : HttpContent
    {
        private readonly int length;
        private readonly bool declareLength;

        internal StreamingContent(int length, bool declareLength = false)
        {
            this.length = length;
            this.declareLength = declareLength;
            Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        protected override bool TryComputeLength(out long computedLength)
        {
            computedLength = declareLength ? length : 0;
            return declareLength;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var buffer = new byte[8192];
            var remaining = length;
            while (remaining > 0)
            {
                var count = Math.Min(buffer.Length, remaining);
                await stream.WriteAsync(buffer.AsMemory(0, count));
                remaining -= count;
            }
        }

    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(MonitorTempDirectory temp, RunningMonitorHost host, string sessionId, string itemId)
        {
            Temp = temp;
            Host = host;
            SessionId = sessionId;
            ItemId = itemId;
            Time = (MutableTimeProvider)temp.TimeProvider;
        }

        internal MonitorTempDirectory Temp { get; }
        internal RunningMonitorHost Host { get; }
        internal MutableTimeProvider Time { get; }
        internal string SessionId { get; }
        internal string ItemId { get; }

        internal static async Task<Fixture> CreateAsync(
            int itemCount = 1,
            Func<RetentionCatalogStore, TimeProvider, RetentionMutationApplicationService>? applicationFactory = null)
        {
            var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(Now) };
            try
            {
                var sessionId = SeedSession(temp, itemCount);
                var host = await MonitorTestHost.StartAsync(temp, testOptions: new MonitorHostTestOptions
                {
                    StartWriter = false,
                    StartProjectionWorker = false,
                    StartSessionWriter = false,
                    StartSessionOtelEnrichment = false,
                    StartRetentionCleanupWorker = false,
                    UseUserSecrets = false,
                    RetentionMutationApplicationFactory = applicationFactory,
                });
                return new(temp, host, sessionId, itemCount == 0 ? "missing-item" : ReadItemId(temp));
            }
            catch
            {
                temp.Dispose();
                throw;
            }
        }

        internal string WorkflowKey(byte value) =>
            RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat(value, RetentionMutationIdentifierFormats.SecretByteLength).ToArray());

        internal async Task<Preview> CreatePreviewAsync(string workflowKey, string operation = "pin")
        {
            using var response = await PostPreviewAsync(workflowKey, operation);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            return new(
                json.RootElement.GetProperty("preview_id").GetString()!,
                json.RootElement.GetProperty("preview_digest").GetString()!);
        }

        internal Task<HttpResponseMessage> PostPreviewAsync(string workflowKey, string operation = "pin") =>
            PostAsync("/api/retention/v1/previews", PreviewJson(SessionId, operation), workflowKey);

        internal async Task<string> IssueTokenAsync(Preview preview, string workflowKey)
        {
            using var response = await IssueConfirmationAsync(preview, workflowKey);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("confirmation_token").GetString()!;
        }

        internal Task<HttpResponseMessage> IssueConfirmationAsync(Preview preview, string workflowKey) =>
            PostAsync(
                "/api/retention/v1/confirmations",
                $"{{\"preview_id\":\"{preview.Id}\",\"preview_digest\":\"{preview.Digest}\"}}",
                workflowKey);

        internal Task<HttpResponseMessage> MutateAsync(string token, string workflowKey, string operation = "pin") =>
            PostAsync(
                "/api/retention/v1/mutations",
                $"{{\"confirmation_token\":\"{token}\",\"operation\":\"{operation}\",\"scope\":\"session_items\",\"target_kind\":\"session\",\"target_id\":\"{SessionId}\"}}",
                workflowKey);

        internal async Task<HttpResponseMessage> PostAsync(string path, string json, string workflowKey)
        {
            using var request = Request(HttpMethod.Post, path, json, workflowKey);
            return await Host.Client.SendAsync(request);
        }

        internal HttpRequestMessage Request(HttpMethod method, string path, string json, string workflowKey)
        {
            var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Idempotency-Key", workflowKey);
            request.Headers.Add("x-monitor-csrf", "local-monitor");
            return request;
        }

        internal void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        internal long Scalar(string sql)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }

        public async ValueTask DisposeAsync()
        {
            await Host.DisposeAsync();
            Temp.Dispose();
        }

        internal static string PreviewJson(string sessionId, string operation = "pin") =>
            $"{{\"target\":{{\"kind\":\"session\",\"id\":\"{sessionId}\"}},\"operation\":\"{operation}\",\"scope\":\"session_items\",\"reason_code\":\"research_needed\",\"comment\":null}}";

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection($"Data Source={Temp.DatabasePath}");
            connection.Open();
            return connection;
        }

        private static string SeedSession(MonitorTempDirectory temp, int itemCount)
        {
            var time = (MutableTimeProvider)temp.TimeProvider;
            var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
            var sessionStore = new SqliteSessionStore(temp.DatabasePath, temp.RetentionContext, time);
            sessionStore.CreateSchema();
            var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
                time.GetUtcNow().AddMinutes(-1), time.GetUtcNow(), time.GetUtcNow(), SessionRawRetentionState.Expiring, time.GetUtcNow().AddMinutes(-1), time.GetUtcNow());
            var events = Enumerable.Range(0, itemCount).Select(index => new ObservedSessionEvent(
                Guid.Parse($"018f2b4e-7c1a-7f1a-8a2b-{index + 0x6072:X12}"), sessionId, null,
                SessionSourceSurface.CopilotSdk, null, $"trace-matrix-{index}", "received", "copilot-sdk-stream",
                $"event-matrix-{index}", "user.message", time.GetUtcNow().AddSeconds(index), SessionContentState.Available)).ToArray();
            var content = events.Select((item, index) => new SessionEventContent(item.EventId, "application/json",
                $"{{\"synthetic\":{index}}}", time.GetUtcNow().AddSeconds(index), time.GetUtcNow().AddDays(90).AddSeconds(index))).ToArray();
            sessionStore.Write(new(new(session, [], [], events), content));
            return sessionId.ToString("D");
        }

        private static string ReadItemId(MonitorTempDirectory temp)
        {
            using var connection = new SqliteConnection($"Data Source={temp.DatabasePath}");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_id FROM retention_items WHERE store_kind='session_event_content';";
            return (string)command.ExecuteScalar()!;
        }
    }

    private sealed record Preview(string Id, string Digest);
}

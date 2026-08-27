using System.Globalization;
using System.Net;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.LocalMonitor.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

// The wire half of Group 6: the frozen generic raw content route's exact denial and failure bytes.
public sealed class GenericRouteContentDenialRouteTests
{
    private const string DefaultAdapter = "copilot-sdk-stream";
    private const string DefaultSurface = "copilot-sdk";
    private const string DefaultPayloadSchema = "github-copilot-sdk.skill-invoked.v1";
    private static readonly DateTimeOffset WriteAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASkillInvokedEventReturnsTheExact43ByteNotFound()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var write = SeedSkillInvocation(temp.DatabasePath, host.Services, "native-route-denied");

        using var response = await host.Client.GetAsync(
            $"/sessions/{write.NewSessionId:D}/events/{write.EventId:D}/content");

        await AssertExactEntityAsync(response, HttpStatusCode.NotFound, "{\"error\":\"session_event_content_not_found\"}");
    }

    [Fact]
    public async Task AMissingEventReturnsTheSameExact43ByteNotFound()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);

        using var response = await host.Client.GetAsync(
            $"/sessions/{Guid.CreateVersion7():D}/events/{Guid.CreateVersion7():D}/content");

        await AssertExactEntityAsync(response, HttpStatusCode.NotFound, "{\"error\":\"session_event_content_not_found\"}");
    }

    [Fact]
    public async Task AMalformedStorageReturnsTheExact37ByteUnavailable()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await MonitorTestHost.StartAsync(temp);
        var write = SeedSkillInvocation(temp.DatabasePath, host.Services, "native-route-unavailable");
        Execute(temp.DatabasePath, "ALTER TABLE session_events RENAME TO session_events_moved;");

        using var response = await host.Client.GetAsync(
            $"/sessions/{write.NewSessionId:D}/events/{write.EventId:D}/content");

        await AssertExactEntityAsync(
            response, HttpStatusCode.ServiceUnavailable, "{\"error\":\"session_store_unavailable\"}");
    }

    [Fact]
    public async Task ANonSkillEventReturnsItsExactFrozen200Bytes()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new GenericRouteContentClock(WriteAt);
        temp.TimeProvider = clock;
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        var identity = GenericRouteContentDenialTests.InsertNonSkillEvent(
            temp.DatabasePath, "native-route-allowed");

        using var response = await host.Client.GetAsync(
            $"/sessions/{identity.SessionId:D}/events/{identity.EventId:D}/content");

        await AssertExactEntityAsync(
            response,
            HttpStatusCode.OK,
            $$"""{"event_id":"{{identity.EventId:D}}","content_kind":"user_prompt","content":"{\u0022message\u0022:\u0022synthetic\u0022}","captured_at":"2026-01-01T00:00:00+00:00","expires_at":"2026-04-01T00:00:00+00:00"}""");
    }

    [Fact]
    public async Task ARealCommittedLeaseExpiredAfterBufferingReturnsExact409WithoutRaw()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new GenericRouteContentClock(WriteAt);
        temp.TimeProvider = clock;
        var options = new MonitorHostTestOptions
        {
            StartWriter = false,
            StartProjectionWorker = false,
            StartSessionWriter = false,
            StartSessionOtelEnrichment = false,
            StartLocalRepositoryCatalogHostedService = false,
            UseUserSecrets = false,
            SessionRawContentRouteCheckpoint = phase =>
            {
                if (phase != SessionRawContentRoutePhase.BeforeSeal) return;
                clock.UtcNow = WriteAt + RetentionV1Constants.LeaseDuration;
            },
        };
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: options);
        var identity = GenericRouteContentDenialTests.InsertNonSkillEvent(
            temp.DatabasePath, "native-route-expired-before-seal");

        using var response = await host.Client.GetAsync(
            $"/sessions/{identity.SessionId:D}/events/{identity.EventId:D}/content");

        await AssertExactEntityAsync(
            response,
            HttpStatusCode.Conflict,
            "{\"error\":\"raw_content_lease_lost\"}");
        Assert.DoesNotContain("synthetic", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadUsesTheSameAuthorizedEntityLengthWithoutPublishingRawBytes()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new GenericRouteContentClock(WriteAt);
        temp.TimeProvider = clock;
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        var identity = GenericRouteContentDenialTests.InsertNonSkillEvent(
            temp.DatabasePath, "native-route-head");
        var path = $"/sessions/{identity.SessionId:D}/events/{identity.EventId:D}/content";

        using var get = await host.Client.GetAsync(path);
        using var head = await host.Client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));

        Assert.Equal(get.StatusCode, head.StatusCode);
        Assert.Equal(get.Content.Headers.ContentLength, head.Content.Headers.ContentLength);
        Assert.Equal("application/json", head.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", head.Headers.CacheControl?.ToString());
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    // The store hands the route a lost grant when the expiry notification comes due while the
    // committed handle is still hidden, and the route's frozen v1 410 bytes must be exact there too.
    [Fact]
    public async Task AnExpiryNotificationDueWhileTheHandleIsHiddenReturnsTheExact410()
    {
        using var temp = new MonitorTempDirectory();
        var clock = new GenericRouteContentClock(WriteAt);
        temp.TimeProvider = clock;
        await using var host = await MonitorTestHost.StartAsync(temp, testOptions: QuietHost());
        var identity = GenericRouteContentDenialTests.InsertNonSkillEvent(
            temp.DatabasePath, "native-route-hidden-handle");
        clock.FireOnceWhenTheLeaseExpiryNotificationArms();

        using var response = await host.Client.GetAsync(
            $"/sessions/{identity.SessionId:D}/events/{identity.EventId:D}/content");

        await AssertExactEntityAsync(
            response,
            HttpStatusCode.Gone,
            """{"error":"raw_content_expired","content_state":"expired_pending_deletion"}""");
    }

    [Fact]
    public void TheThreeOwnedEntitiesHaveTheirExactByteLengths()
    {
        Assert.Equal(43, "{\"error\":\"session_event_content_not_found\"}"u8.Length);
        Assert.Equal(30, "{\"error\":\"session_store_busy\"}"u8.Length);
        Assert.Equal(37, "{\"error\":\"session_store_unavailable\"}"u8.Length);
    }

    // The deterministic clock cannot advance a background worker, and a worker's own timer must not
    // be mistaken for the access lease's arming, so none of them run for these two cases.
    private static MonitorHostTestOptions QuietHost() => new()
    {
        StartWriter = false,
        StartProjectionWorker = false,
        StartSessionWriter = false,
        StartSessionOtelEnrichment = false,
        StartLocalRepositoryCatalogHostedService = false,
        UseUserSecrets = false,
    };

    private static async Task AssertExactEntityAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string entity)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.ToString());
        Assert.Null(response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(entity), response.Content.Headers.ContentLength);
        Assert.Empty(response.Content.Headers.Allow);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(System.Text.Encoding.UTF8.GetBytes(entity), bytes);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.DoesNotContain((byte)'\n', bytes);
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
    }

    private static SessionSkillInvocationWrite SeedSkillInvocation(
        string databasePath,
        IServiceProvider services,
        string nativeSessionId)
    {
        var write = new SessionSkillInvocationWrite(
            SourceAdapter: DefaultAdapter,
            SourceSurface: DefaultSurface,
            SourceEventId: Guid.NewGuid().ToString("D"),
            SourceParentEventId: null,
            NativeSessionId: nativeSessionId,
            RunNativeId: null,
            SourceEphemeral: false,
            OccurredAt: WriteAt,
            SourceApplicationVersion: "1.0.65",
            AdapterVersion: "adapter-version-1",
            NormalizationVersion: "normalization-1",
            PayloadSchema: DefaultPayloadSchema,
            SchemaFingerprint: new string('a', 64),
            PayloadTokenUtf8: "{\"skill\":\"demo\"}"u8.ToArray(),
            State: "available",
            Reason: "none",
            Name: "demo-skill",
            Source: "project",
            Trigger: "user-invoked",
            BodySha256: new string('b', 64),
            BodyUtf8Bytes: 7L,
            DefinitionPathSha256: new string('c', 64),
            DefinitionPathUtf8Bytes: 12L,
            EventId: Guid.CreateVersion7(),
            SnapshotId: Guid.CreateVersion7(),
            ClaimId: Guid.CreateVersion7(),
            NewSessionId: Guid.CreateVersion7(),
            NewRunId: Guid.CreateVersion7(),
            WriteAt: WriteAt,
            ExpiresAt: WriteAt.AddDays(90));

        using var connection = Open(databasePath);
        using var transaction = connection.BeginTransaction();
        var outcome = SessionSkillInvocationParticipant.InsertOrVerify(
            connection,
            transaction,
            write,
            services.GetRequiredService<ILocalWorkspaceProjectionTransactionParticipant>(),
            out _);
        Assert.Equal(SessionSkillInvocationWriteOutcome.Inserted, outcome);
        transaction.Commit();
        return write;
    }

    private static void Execute(string databasePath, string sql)
    {
        using var connection = Open(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }
}

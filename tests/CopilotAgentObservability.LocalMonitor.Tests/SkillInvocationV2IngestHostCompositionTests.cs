using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using GitHub.Copilot;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationV2IngestHostCompositionTests
{
    private const string V2Path = "/api/session-ingest/v2/events";
    private const string CapabilityHeaderName = "X-CAO-Skill-Runtime-Capability";
    private const string VersionHeaderName = "X-CAO-Session-Event-Version";
    private const string ValidCapabilityToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string UnavailableEntity = "{\"error\":\"local_monitor_ui_unavailable\"}";
    private const string UnsupportedEndpointEntity =
        "{\"accepted\":false,\"error\":\"unsupported_endpoint\",\"message\":\"Only /v1/traces is supported.\"}";
    private const string MethodNotAllowedEntity = "{\"error\":\"method_not_allowed\"}";
    private const string ValidV2Request =
        "{\"schema_version\":2,\"source_adapter\":\"copilot-sdk-stream\",\"source_surface\":\"copilot-sdk\",\"native_session_id\":\"native-session\",\"source_application_version\":\"1.0.65\",\"adapter_version\":\"copilot-sdk-dotnet-1.0.4+cao-skill-v2.1\",\"normalization_version\":\"github-copilot-sdk.skill-invoked.normalize.v1\",\"payload_schema\":\"github-copilot-sdk.skill-invoked.v1\",\"schema_fingerprint\":\"8fac48d8a878cbc9a4ebf59aae78e242b3375f4b82abed7c7a0e45d7a6ff7a5c\",\"events\":[{\"source_event_id\":\"aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa\",\"source_parent_event_id\":null,\"type\":\"skill.invoked\",\"occurred_at\":\"2026-08-09T00:00:00.0000000+00:00\",\"run_native_id\":null,\"source_ephemeral\":false,\"trace_id\":null,\"span_id\":null,\"payload\":{\"name\":\"skill-name\",\"path\":\"skills/SKILL.md\",\"content\":\"body\"}}]}";
    private const string ValidV1Request =
        "{\"schema_version\":1,\"source_adapter\":\"claude-code-hook\",\"source_surface\":\"claude-code\",\"native_session_id\":\"host-composition-native\",\"source_application_version\":\"fixture-v1\",\"adapter_version\":\"claude-hook-v1\",\"normalization_version\":\"session-normalization-v1\",\"events\":[{\"source_event_id\":\"host-composition-event\",\"type\":\"UserPromptSubmit\",\"occurred_at\":\"2026-08-22T00:00:00Z\",\"payload\":{\"prompt\":\"synthetic\"}}]}";

    private static string CurrentV2Request() => ValidV2Request.Replace(
        "github-copilot-sdk.skill-invoked.normalize.v1",
        "github-copilot-sdk.skill-invoked.normalize.v2",
        StringComparison.Ordinal);

    [Fact]
    public async Task RawDefaultHost_AfterStart_PublishesSkillRuntimeBridge()
    {
        using var temp = new MonitorTempDirectory();
        var options = new MonitorOptions(
            temp.DatabasePath,
            Url: "http://127.0.0.1:0",
            SanitizedOnly: false,
            MaxRequestBodyBytes: MonitorOptions.DefaultMaxRequestBodyBytes,
            SkillDiscoveryDirectories: []);
        await using var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            TimeProvider = temp.TimeProvider,
            UseUserSecrets = false,
        });

        await app.StartAsync();

        Assert.True(app.Services.GetRequiredService<SkillRuntimeBridgeHolderV1>().HasBridge);
    }

    [Fact]
    public async Task RawDefaultHost_StopAsync_RunsCoordinatorStoppingBeforeHostedServiceStop()
    {
        using var temp = new MonitorTempDirectory();
        CopilotRuntimeAdmissionV1? admission = null;
        var probe = new ShutdownOrderProbe(() => admission!.IsShutdownClosed);
        var options = new MonitorOptions(
            temp.DatabasePath,
            Url: "http://127.0.0.1:0",
            SanitizedOnly: false,
            MaxRequestBodyBytes: MonitorOptions.DefaultMaxRequestBodyBytes,
            SkillDiscoveryDirectories: []);
        await using var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            TimeProvider = temp.TimeProvider,
            UseUserSecrets = false,
            AdditionalServices = services => services.AddHostedService(_ => probe),
        });
        admission = app.Services.GetRequiredService<CopilotRuntimeAdmissionV1>();
        await app.StartAsync();

        await app.StopAsync();

        Assert.True(probe.SawShutdownClosedAtStop);
    }

    [Fact]
    public async Task RawDefaultHost_CurrentFileRuntimeAcquisitionAfterShutdownClosure_AbortsWithoutResponseAndCompletesRetention()
    {
        using var temp = new MonitorTempDirectory();
        using var root = new TempRootDirectory();
        var grant = new ObservedRetentionGrant();
        var historicalGate = new BlockingHistoricalGate(grant);
        var options = new MonitorOptions(
            temp.DatabasePath,
            Url: "http://127.0.0.1:0",
            SanitizedOnly: false,
            MaxRequestBodyBytes: MonitorOptions.DefaultMaxRequestBodyBytes,
            SkillDiscoveryDirectories: [root.Path]);
        await using var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            TimeProvider = temp.TimeProvider,
            UseUserSecrets = false,
            SkillCurrentFileHistoricalGate = historicalGate,
            SkillCurrentAuthorizationGate = new AcquiredAuthorizationGate(),
        });
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var admission = app.Services.GetRequiredService<CopilotRuntimeAdmissionV1>();
        admission.PublishReadyTestCandidate(new FakeSkillRuntimeClient(), out _);
        var coordinator = Assert.Single(app.Services.GetServices<IHostedService>()
            .OfType<SkillHostShutdownCoordinatorV1>());
        const string path = "/api/local-monitor/v1/sessions/11111111-1111-4111-8111-111111111111/skill-invocations/22222222-2222-4222-8222-222222222222/current-file-read";
        using var request = JsonPost(path, "{\"schema_version\":\"local-skill-current-file-read.request.v1\"}");
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");

        var response = client.SendAsync(request);
        await historicalGate.Entered;
        var stopping = coordinator.StoppingAsync(CancellationToken.None);

        Assert.True(admission.IsShutdownClosed);
        Assert.False(stopping.IsCompleted);
        historicalGate.Release();

        await Assert.ThrowsAsync<HttpRequestException>(() => response);
        await stopping;

        Assert.Equal(1, grant.CompleteWithoutRawCalls);
        Assert.Equal(0, grant.SealRawCalls);
        Assert.Equal(1, grant.DisposeCalls);
    }

    [Fact]
    public async Task RawDefaultHost_PostWithoutCapability_ReturnsOwnedStageOneUnavailable()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        using var request = JsonPost(V2Path, CurrentV2Request());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(response.Content.Headers.Allow);
        Assert.Equal(Encoding.UTF8.GetBytes(UnavailableEntity), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RawDefaultHost_GetOnV2Path_ReturnsOwnedMethodNotAllowed()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);

        using var response = await host.Client.GetAsync(V2Path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("application/json; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(["POST"], response.Content.Headers.Allow);
        var entity = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(30, entity.Length);
        Assert.Equal(Encoding.UTF8.GetBytes(MethodNotAllowedEntity), entity);
    }

    [Fact]
    public async Task SanitizedOnlyHost_PostOnV2Path_ReturnsUnsupportedEndpointFallbackInsteadOfOwnedStageOneUnavailable()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp, sanitizedOnly: true);
        using var request = JsonPost(V2Path, CurrentV2Request());

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var entity = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetBytes(UnsupportedEndpointEntity), entity);
        Assert.NotEqual(Encoding.UTF8.GetBytes(UnavailableEntity), entity);
    }

    [Fact]
    public async Task RawDefaultHost_WellFormedPostWithoutAdmittedGeneration_ReturnsStageOneUnavailableWithoutWrites()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        using var request = JsonPost(V2Path, CurrentV2Request());
        request.Headers.TryAddWithoutValidation(CapabilityHeaderName, ValidCapabilityToken);
        request.Headers.TryAddWithoutValidation(VersionHeaderName, "2");

        using var response = await host.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(Encoding.UTF8.GetBytes(UnavailableEntity), await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0L, CountRows(temp.DatabasePath, "skill_invocation_snapshots"));
        Assert.Equal(0L, CountRows(temp.DatabasePath, "skill_invocation_snapshot_receipts"));
        Assert.Equal(0L, CountRows(temp.DatabasePath, "session_events"));
    }

    [Fact]
    public async Task RawDefaultHost_V2RegistrationPreservesSkillRawRoutesAndV1Ingest()
    {
        using var temp = new MonitorTempDirectory();
        using var root = new TempRootDirectory();
        await using var host = await StartAsync(temp, skillDirectories: [root.Path]);
        const string sessionId = "11111111-1111-4111-8111-111111111111";
        const string snapshotId = "22222222-2222-4222-8222-222222222222";
        var metadataPath = $"/api/local-monitor/v1/sessions/{sessionId}/skill-invocations/{snapshotId}";

        using var metadata = await host.Client.GetAsync(metadataPath);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await metadata.Content.ReadAsStringAsync());

        using var content = await host.Client.GetAsync(metadataPath + "/content");
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await content.Content.ReadAsStringAsync());

        using var currentFileRequest = JsonPost(
            metadataPath + "/current-file-read",
            "{\"schema_version\":\"local-skill-current-file-read.request.v1\"}");
        currentFileRequest.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        using var currentFile = await host.Client.SendAsync(currentFileRequest);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await currentFile.Content.ReadAsStringAsync());

        using var v1Request = JsonPost("/api/session-ingest/v1/events", ValidV1Request);
        v1Request.Headers.TryAddWithoutValidation(VersionHeaderName, "1");
        using var v1Response = await host.Client.SendAsync(v1Request);
        Assert.Equal(HttpStatusCode.NoContent, v1Response.StatusCode);
        Assert.Empty(await v1Response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task RawDefaultHost_DistinctOwnedSourceIds_CommitsV2PrefixAndV1Completion()
    {
        const string nativeSessionId = "synthetic-owned-session-5e";
        var firstInvocationId = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var secondInvocationId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        var startId = Guid.Parse("33333333-3333-4333-8333-333333333333");
        var terminalId = Guid.Parse("44444444-4444-4444-8444-444444444444");
        var startedAt = DateTimeOffset.Parse("2026-08-25T01:00:00Z");
        var completedAt = DateTimeOffset.Parse("2026-08-25T01:00:03Z");
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        var admission = host.Services.GetRequiredService<CopilotRuntimeAdmissionV1>();
        var candidate = admission.PublishReadyTestCandidate(
            new FakeSkillRuntimeClient(), SkillInvocationV2TestIdentity.V1075, out _);
        var bridge = Assert.IsType<SkillRuntimeCapabilityBridgeV1>(
            host.Services.GetRequiredService<SkillRuntimeBridgeHolderV1>().CurrentBridge);
        var bodies = new[]
        {
            PrepareInvocation(candidate, nativeSessionId, firstInvocationId, startedAt.AddSeconds(1), "alpha", "alpha body", 0),
            PrepareInvocation(candidate, nativeSessionId, secondInvocationId, startedAt.AddSeconds(2), "beta", "beta body", 1),
        };
        var start = Assert.IsType<SessionIngestEnvelope>(OwnedSessionEnvelopeMapperV1.TryMap(
            nativeSessionId,
            SkillInvocationV2TestIdentity.V1075,
            new SessionStartEvent
            {
                Id = startId,
                Timestamp = startedAt,
                Data = new SessionStartData
                {
                    SessionId = nativeSessionId,
                    CopilotVersion = "1.0.75",
                    Producer = "copilot",
                    StartTime = startedAt,
                    Version = 1,
                },
            }));
        var terminal = Assert.IsType<SessionIngestEnvelope>(OwnedSessionEnvelopeMapperV1.TryMap(
            nativeSessionId,
            SkillInvocationV2TestIdentity.V1075,
            new SessionTaskCompleteEvent
            {
                Id = terminalId,
                Timestamp = completedAt,
                Data = new SessionTaskCompleteData { Success = true },
            }));
        var prepared = new OwnedSessionPreparedImportV1(
            nativeSessionId,
            "1.0.75",
            SerializeEnvelope(start),
            bodies,
            SerializeEnvelope(terminal));
        var importer = new OwnedSessionPostCompletionImporterV1(
            bridge,
            host.Services.GetRequiredService<SessionEventQueue>(),
            TimeSpan.FromSeconds(5));

        var outcome = await importer.ImportAsync(candidate, prepared, CancellationToken.None);

        Assert.Equal(OwnedSessionPostFreezeOutcomeV1.Success, outcome);
        Assert.Equal(2L, CountRows(temp.DatabasePath, "skill_invocation_snapshots"));
        Assert.Equal(2L, CountRows(temp.DatabasePath, "skill_invocation_snapshot_receipts"));
        Assert.Equal(2L, CountRows(temp.DatabasePath, "skill_projection_sdk_claims"));
        Assert.Equal(4L, CountRows(temp.DatabasePath, "session_event_content"));
        Assert.Equal(4L, ScalarLong(temp.DatabasePath,
            "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND state='expiring';"));
        Assert.Equal(2L, ScalarLong(temp.DatabasePath,
            "SELECT COUNT(DISTINCT snapshot_id) FROM skill_invocation_snapshots;"));
        Assert.Equal(1L, ScalarLong(temp.DatabasePath,
            "SELECT COUNT(DISTINCT session_id) FROM skill_invocation_snapshots;"));
        Assert.Equal(1L, CountRows(temp.DatabasePath, "sessions"));
        Assert.Equal(1L, ScalarLong(temp.DatabasePath,
            $"SELECT COUNT(*) FROM session_native_ids WHERE source_surface='copilot-sdk' AND native_session_id='{nativeSessionId}' AND binding_kind='native';"));
        Assert.Equal(2L, ScalarLong(temp.DatabasePath,
            "SELECT COUNT(*) FROM session_events WHERE type='skill.invoked';"));
        Assert.Equal(1L, ScalarLong(temp.DatabasePath,
            $"SELECT COUNT(*) FROM session_events WHERE type='session.start' AND source_event_id='{startId:D}';"));
        Assert.Equal(1L, ScalarLong(temp.DatabasePath,
            $"SELECT COUNT(*) FROM session_events WHERE type='session.task_complete' AND source_event_id='{terminalId:D}' AND terminal_outcome='clean';"));
        Assert.Equal("completed", ScalarText(temp.DatabasePath, "SELECT status FROM sessions;"));
        Assert.Equal("session.start,session.task_complete", ScalarText(temp.DatabasePath,
            "SELECT group_concat(type, ',') FROM (SELECT type FROM session_events WHERE type IN ('session.start','session.task_complete') ORDER BY occurred_at);"));
        Assert.Equal(0, host.Services.GetRequiredService<SessionEventQueue>().Count);
        Assert.Equal(0, bridge.PendingCount);
        Assert.Equal(0, candidate.OutstandingCapabilityCount);
    }

    private static HttpRequestMessage JsonPost(string path, string body) =>
        new(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static long CountRows(string databasePath, string table)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return (long)command.ExecuteScalar()!;
    }

    private static long ScalarLong(string databasePath, string sql) =>
        Convert.ToInt64(Scalar(databasePath, sql), System.Globalization.CultureInfo.InvariantCulture);

    private static string ScalarText(string databasePath, string sql) =>
        Convert.ToString(Scalar(databasePath, sql), System.Globalization.CultureInfo.InvariantCulture)!;

    private static object Scalar(string databasePath, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private static OwnedSessionPreparedBodyV1 PrepareInvocation(
        CopilotRuntimeGenerationV1 candidate,
        string nativeSessionId,
        Guid eventId,
        DateTimeOffset timestamp,
        string name,
        string certifiedContent,
        int ordinal)
    {
        Assert.True(candidate.TryAcquireOperationCapability(CancellationToken.None, out var capability));
        try
        {
            var sourceEvent = new SkillInvokedEvent
            {
                Id = eventId,
                Timestamp = timestamp,
                Data = new SkillInvokedData
                {
                    Name = name,
                    Source = "custom",
                    Path = $"skills/{name}/SKILL.md",
                    Content = certifiedContent,
                    Description = $"synthetic {name}",
                },
            };
            Assert.True(SkillInvocationNormalizedJsonV1.TryWriteCancellable(
                nativeSessionId,
                sourceEvent,
                certifiedContent,
                capability,
                capability.WorkToken,
                out var body));
            return new OwnedSessionPreparedBodyV1(ordinal, body!, body!.Length, SHA256.HashData(body));
        }
        finally
        {
            capability.Release();
        }
    }

    private static byte[] SerializeEnvelope(SessionIngestEnvelope envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<RunningMonitorHost> StartAsync(
        MonitorTempDirectory temp,
        bool sanitizedOnly = false,
        IReadOnlyList<string>? skillDirectories = null)
    {
        var options = new MonitorOptions(
            temp.DatabasePath,
            Url: "http://127.0.0.1:0",
            SanitizedOnly: sanitizedOnly,
            MaxRequestBodyBytes: MonitorOptions.DefaultMaxRequestBodyBytes,
            SkillDiscoveryDirectories: skillDirectories ?? []);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions
        {
            TimeProvider = temp.TimeProvider,
            UseUserSecrets = false,
        });
        await app.StartAsync();

        var address = Assert.Single(app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses);
        return new RunningMonitorHost(app, new HttpClient { BaseAddress = new Uri(address) }, address);
    }

    private sealed class TempRootDirectory : IDisposable
    {
        public TempRootDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cao-skillroot-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class ShutdownOrderProbe(Func<bool> isShutdownClosed) : IHostedService
    {
        internal bool SawShutdownClosedAtStop { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            SawShutdownClosedAtStop = isShutdownClosed();
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingHistoricalGate(ObservedRetentionGrant grant)
        : ISkillCurrentFileHistoricalGateV1
    {
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => entered.Task;

        internal void Release() => release.TrySetResult();

        public async Task<SkillCurrentFileHistoricalAdmissionV1> AdmitAsync(
            Guid sessionId,
            Guid snapshotId,
            CancellationToken cancellationToken)
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return new SkillCurrentFileHistoricalAdmissionV1(
                SkillInvocationSnapshotContentOutcome.Granted,
                grant);
        }
    }

    private sealed class ObservedRetentionGrant : ISkillCurrentFileRetentionGrantV1
    {
        internal int CompleteWithoutRawCalls { get; private set; }

        internal int SealRawCalls { get; private set; }

        internal int DisposeCalls { get; private set; }

        public SkillInvocationSnapshotContentFacts Facts { get; } = new(
            Guid.Parse("22222222-2222-4222-8222-222222222222"),
            "synthetic body",
            "skills/SKILL.md",
            new string('0', 64),
            new string('1', 64),
            14,
            15,
            DateTimeOffset.Parse("2026-08-22T00:00:00Z"));

        public SkillInvocationSnapshotContentTerminalResult TrySealRawResponse()
        {
            SealRawCalls++;
            return SkillInvocationSnapshotContentTerminalResult.Sealed;
        }

        public SkillInvocationSnapshotContentTerminalResult TryCompleteWithoutRaw()
        {
            CompleteWithoutRawCalls++;
            return SkillInvocationSnapshotContentTerminalResult.CompletedWithoutRaw;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AcquiredAuthorizationGate : ISkillCurrentAuthorizationGateV1
    {
        public SkillProjectionCurrentSdkClaimAuthorizationResult TryAcquire(Guid sessionId, Guid snapshotId) =>
            SkillProjectionCurrentSdkClaimAuthorizationResult.ForAcquired(
                new SkillProjectionCurrentSdkClaimAuthorization(
                    "skill-name",
                    "skillDirectories",
                    new GenerationLease()));
    }

    private sealed class GenerationLease : ISkillRegistryGenerationLease
    {
        public void Dispose()
        {
        }
    }
}

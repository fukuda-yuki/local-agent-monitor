using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task RawDefaultHost_PostWithoutCapability_ReturnsOwnedStageOneUnavailable()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp);
        using var request = JsonPost(V2Path, ValidV2Request);

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
        using var request = JsonPost(V2Path, ValidV2Request);

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
        using var request = JsonPost(V2Path, ValidV2Request);
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
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillInvocationSnapshotHostCompositionTests
{
    private const string SessionId = "11111111-1111-4111-8111-111111111111";
    private const string SnapshotId = "22222222-2222-4222-8222-222222222222";
    private const string ValidRequest = """{"schema_version":"local-skill-current-file-read.request.v1"}""";

    private static string MetadataPath => $"/api/local-monitor/v1/sessions/{SessionId}/skill-invocations/{SnapshotId}";

    private static string ContentPath => MetadataPath + "/content";

    private static string CurrentFilePath => MetadataPath + "/current-file-read";

    [Fact]
    public async Task ARawDefaultHostWithAConfiguredRootRegistersAllThreeSkillRoutes()
    {
        using var temp = new MonitorTempDirectory();
        using var root = new TempRootDirectory();
        await using var host = await StartAsync(temp, skillDirectories: [root.Path]);

        using var metadata = await host.Client.GetAsync(MetadataPath);
        Assert.Equal(HttpStatusCode.NotFound, metadata.StatusCode);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await metadata.Content.ReadAsStringAsync());

        using var content = await host.Client.GetAsync(ContentPath);
        Assert.Equal(HttpStatusCode.NotFound, content.StatusCode);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await content.Content.ReadAsStringAsync());

        // The POST is registered and ran its stages: an absent snapshot is the pre-grant owned
        // 404, not the framework's empty route-absence 404.
        using var currentFile = await PostCurrentFileAsync(host);
        Assert.Equal(HttpStatusCode.NotFound, currentFile.StatusCode);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await currentFile.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task AZeroRootHostOmitsOnlyTheCurrentFilePost()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp, skillDirectories: []);

        using var metadata = await host.Client.GetAsync(MetadataPath);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await metadata.Content.ReadAsStringAsync());

        using var content = await host.Client.GetAsync(ContentPath);
        Assert.Equal("{\"error\":\"skill_snapshot_not_found\"}", await content.Content.ReadAsStringAsync());

        // Route absence, not a stage-1 unavailable response: the POST falls through to the host's
        // own unsupported-endpoint 404 and forms no owned Skill result.
        using var currentFile = await PostCurrentFileAsync(host);
        Assert.Equal(HttpStatusCode.NotFound, currentFile.StatusCode);
        Assert.Contains("unsupported_endpoint", await currentFile.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain("skill_", await currentFile.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReceiverOnlyComposesNoneOfTheThreeSkillRoutes()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(temp, skillDirectories: [], sanitizedOnly: true);

        foreach (var path in new[] { MetadataPath, ContentPath })
        {
            using var response = await host.Client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }

        using var currentFile = await PostCurrentFileAsync(host);
        Assert.Equal(HttpStatusCode.NotFound, currentFile.StatusCode);
        Assert.Empty(await currentFile.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public void AnInvalidConfiguredRootAbortsStartupWithTheSanitizedReason()
    {
        using var temp = new MonitorTempDirectory();
        var options = BuildOptions(temp, skillDirectories: ["not-an-absolute-path"], sanitizedOnly: false);

        var exception = Assert.Throws<SkillDiscoveryStartupAbortException>(
            () => MonitorHost.Build(options, new MonitorHostTestOptions { TimeProvider = temp.TimeProvider }));

        Assert.Equal("skill_discovery_root_configuration_invalid", exception.Reason);
        Assert.DoesNotContain("not-an-absolute-path", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingConfiguredRootDirectoryAbortsStartupWithTheSameSanitizedReason()
    {
        using var temp = new MonitorTempDirectory();
        var absent = Path.Combine(Path.GetTempPath(), $"cao-absent-{Guid.NewGuid():N}");
        var options = BuildOptions(temp, skillDirectories: [absent], sanitizedOnly: false);

        var exception = Assert.Throws<SkillDiscoveryStartupAbortException>(
            () => MonitorHost.Build(options, new MonitorHostTestOptions { TimeProvider = temp.TimeProvider }));

        Assert.Equal("skill_discovery_root_configuration_invalid", exception.Reason);
    }

    [Fact]
    public async Task ReceiverOnlyIgnoresConfiguredRootsInsteadOfAbortingStartup()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartAsync(
            temp, skillDirectories: ["not-an-absolute-path"], sanitizedOnly: true);

        using var currentFile = await PostCurrentFileAsync(host);
        Assert.Equal(HttpStatusCode.NotFound, currentFile.StatusCode);
        Assert.Empty(await currentFile.Content.ReadAsByteArrayAsync());
    }

    private static Task<HttpResponseMessage> PostCurrentFileAsync(RunningMonitorHost host)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, CurrentFilePath);
        request.Headers.TryAddWithoutValidation("x-monitor-csrf", "local-monitor");
        request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(ValidRequest));
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return host.Client.SendAsync(request);
    }

    private static MonitorOptions BuildOptions(
        MonitorTempDirectory temp,
        IReadOnlyList<string> skillDirectories,
        bool sanitizedOnly) =>
        new(
            temp.DatabasePath,
            Url: "http://127.0.0.1:0",
            SanitizedOnly: sanitizedOnly,
            MaxRequestBodyBytes: MonitorOptions.DefaultMaxRequestBodyBytes,
            SkillDiscoveryDirectories: skillDirectories);

    private static async Task<RunningMonitorHost> StartAsync(
        MonitorTempDirectory temp,
        IReadOnlyList<string> skillDirectories,
        bool sanitizedOnly = false)
    {
        var app = MonitorHost.Build(
            BuildOptions(temp, skillDirectories, sanitizedOnly),
            new MonitorHostTestOptions { TimeProvider = temp.TimeProvider });
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

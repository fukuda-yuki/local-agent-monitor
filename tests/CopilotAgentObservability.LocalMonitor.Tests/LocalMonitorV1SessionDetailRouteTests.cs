using System.Net;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionDetailRouteTests
{
    private const string SessionId="018f0000-0000-7000-8000-000000000001";

    [Fact]
    public async Task SummaryReadsASeededSessionThroughTheProductionCoordinator()
    {
        using var temp=new MonitorTempDirectory();
        var session=AlertCenterRouteTests.SeedPersistedTraceAndSession(temp,"00000000000000000000000000000001",authoritativeToolStatus:true);
        await using var host=await MonitorTestHost.StartAsync(temp);

        _=await host.Services.GetRequiredService<ILocalRepositorySessionDetailSnapshotService>()
            .ReadDetailAsync(new(LocalRepositorySessionDetailRequestKind.Summary,session.ToString("D")),CancellationToken.None);

        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{session:D}/summary");

        Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Equal("application/json; charset=utf-8",response.Content.Headers.ContentType?.ToString());
        Assert.Equal(["no-store"],response.Headers.GetValues("Cache-Control"));
        var bytes=await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(bytes.Length,response.Content.Headers.ContentLength);
        Assert.Contains("\"schema_version\":\"local-monitor-session-summary.response.v1\"",System.Text.Encoding.UTF8.GetString(bytes),StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task WrongMethodWinsBeforeIdentifiersAndQuery(string method)
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var request=new HttpRequestMessage(new HttpMethod(method),"/api/local-monitor/v1/sessions/not-an-id/summary?unknown=secret");
        using var response=await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,response.StatusCode);Assert.Equal(["GET","HEAD"],response.Content.Headers.Allow);
        Assert.Equal("{\"error\":\"method_not_allowed\"}",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SummaryRejectsQueryBeforeSessionLookup()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary?unknown=secret");
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal("{\"error\":\"invalid_request\"}",await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidAbsentSessionReturnsFixedNotFound()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/summary");
        Assert.Equal(HttpStatusCode.NotFound,response.StatusCode);Assert.Equal("{\"error\":\"session_not_found\"}",await response.Content.ReadAsStringAsync());
        Assert.Equal(["no-store"],response.Headers.GetValues("Cache-Control"));
    }

    [Fact]
    public async Task HeadUsesGetErrorLengthWithoutEntity()
    {
        using var temp=new MonitorTempDirectory();await using var host=await MonitorTestHost.StartAsync(temp);
        using var response=await host.Client.SendAsync(new(HttpMethod.Head,$"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision=bad"));
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);Assert.Equal(27,response.Content.Headers.ContentLength);Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task TimelinePassesTheClosedRequestShapeIntoTheSharedCoordinator()
    {
        var service = new CapturingDetailService();
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        LocalMonitorV1SessionDetailRoutes.Map(app, service, new byte[32]);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };

        using var response = await client.GetAsync($"/api/local-monitor/v1/sessions/{SessionId}/timeline?workspace_revision={new string('1',64)}&execution_id=018f0000-0000-7000-8000-000000000003&parent_node_id=node-00000000000000000000000000000001&limit=17");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var request = Assert.IsType<LocalRepositorySessionDetailRequest>(service.Request);
        Assert.Equal(LocalRepositorySessionDetailRequestKind.Timeline, request.Kind);
        Assert.Equal(SessionId, request.SessionId);
        Assert.Equal("018f0000-0000-7000-8000-000000000003", request.ExecutionId);
        Assert.Equal("node-00000000000000000000000000000001", request.ParentNodeId);
        Assert.Equal(17, request.Limit);
        Assert.Null(request.After);
    }

    private sealed class CapturingDetailService : ILocalRepositorySessionDetailSnapshotService
    {
        internal LocalRepositorySessionDetailRequest? Request { get; private set; }
        public ValueTask<LocalRepositorySessionDetailSnapshot> ReadDetailAsync(LocalRepositorySessionDetailRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            throw new LocalWorkspaceSessionDetailException("session_not_found");
        }
    }
}

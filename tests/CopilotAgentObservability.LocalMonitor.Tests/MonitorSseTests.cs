using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public class MonitorSseTests
{
    [Fact]
    public async Task Events_ResponseIsEventStreamContentType()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp, TimeSpan.FromMilliseconds(50));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/events");
        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Events_NotifyAfterProjectionAndDoNotCarryRawOrPii()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp, TimeSpan.FromMilliseconds(50));

        // The subscription is registered synchronously before the connect comment is
        // flushed, so awaiting the stream guarantees we are subscribed before posting.
        using var stream = await host.Client.GetStreamAsync("/events");
        var post = await host.Client.PostAsync("/v1/traces", JsonContent(SensitiveTraceJson));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);

        var eventText = await ReadUntilAsync(stream, "event: projection", TimeSpan.FromSeconds(10));

        Assert.Contains("event: projection", eventText);
        Assert.Contains("data: {}", eventText);
        Assert.DoesNotContain("SECRET_PROMPT_TEXT_MARKER", eventText);
        Assert.DoesNotContain("SECRET_TOOL_ARGS_MARKER", eventText);
        Assert.DoesNotContain("leak-marker@example.com", eventText);
        Assert.DoesNotContain("33333333333333333333333333333333", eventText);
    }

    [Fact]
    public async Task Events_OnlyGetIsAllowed()
    {
        using var temp = new MonitorTempDirectory();
        await using var host = await StartHostAsync(temp, TimeSpan.FromMilliseconds(50));

        var response = await host.Client.PostAsync("/events", JsonContent("{}"));

        Assert.True(
            response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound,
            $"Expected 405 or 404 for POST /events but got {(int)response.StatusCode}.");
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string marker, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[1024];
        var builder = new StringBuilder();
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cts.Token);
                if (read == 0)
                {
                    break;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                if (builder.ToString().Contains(marker, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Timed out; return whatever was read so the assertions can report the gap.
        }

        return builder.ToString();
    }

    private static async Task<RunningHost> StartHostAsync(MonitorTempDirectory temp, TimeSpan projectionPollInterval)
    {
        var url = $"http://127.0.0.1:{GetFreePort()}";
        var options = new MonitorOptions(temp.DatabasePath, url, SanitizedOnly: false, MaxRequestBodyBytes: 31_457_280);
        var app = MonitorHost.Build(options, new MonitorHostTestOptions { ProjectionPollInterval = projectionPollInterval });
        await app.StartAsync();
        return new RunningHost(app, new HttpClient { BaseAddress = new Uri(url) });
    }

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private const string SensitiveTraceJson = """
        {"resourceSpans":[{"resource":{"attributes":[
          {"key":"client.kind","value":{"stringValue":"vscode-copilot-chat"}},
          {"key":"user.email","value":{"stringValue":"leak-marker@example.com"}}
        ]},"scopeSpans":[{"spans":[
          {"traceId":"33333333333333333333333333333333","spanId":"4444444444444444","name":"chat gpt-4o","attributes":[
            {"key":"gen_ai.prompt","value":{"stringValue":"SECRET_PROMPT_TEXT_MARKER"}},
            {"key":"gen_ai.tool.arguments","value":{"stringValue":"SECRET_TOOL_ARGS_MARKER"}}
          ]}
        ]}]}]}
        """;

    private sealed class RunningHost(Microsoft.AspNetCore.Builder.WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public HttpClient Client { get; } = client;

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            try
            {
                await app.StopAsync();
            }
            catch
            {
                // Ignore stop faults during teardown.
            }

            await app.DisposeAsync();
        }
    }
}

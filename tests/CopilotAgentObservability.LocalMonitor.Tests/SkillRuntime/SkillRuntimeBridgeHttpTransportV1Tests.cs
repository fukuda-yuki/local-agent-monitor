using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class SkillRuntimeBridgeHttpTransportV1Tests
{
    private const string ValidToken = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Constructor_RejectsNullOrNonLoopbackOrScopedAddresses()
    {
        Assert.Throws<ArgumentNullException>(() => new SkillRuntimeBridgeHttpTransportV1(null!, 8080));
        Assert.Throws<ArgumentException>(() => new SkillRuntimeBridgeHttpTransportV1(IPAddress.Parse("8.8.8.8"), 8080));
        Assert.Throws<ArgumentException>(() => new SkillRuntimeBridgeHttpTransportV1(IPAddress.Parse("9.9.9.9"), 8080));
        var scopedLoopbackV6 = new IPAddress(IPAddress.IPv6Loopback.GetAddressBytes(), 5);
        Assert.Throws<ArgumentException>(() => new SkillRuntimeBridgeHttpTransportV1(scopedLoopbackV6, 8080));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void Constructor_RejectsPortsOutsideOneTo65535(int port)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, port));
    }

    [Fact]
    public void Constructor_FormatsNumericLoopbackEndpointWithIngestRoute()
    {
        using var ipv4 = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, 1234);
        Assert.Equal("http://127.0.0.1:1234/api/session-ingest/v2/events", ipv4.Endpoint.ToString());

        using var ipv6 = new SkillRuntimeBridgeHttpTransportV1(IPAddress.IPv6Loopback, 4321);
        Assert.Equal("http://[::1]:4321/api/session-ingest/v2/events", ipv6.Endpoint.ToString());
    }

    [Fact]
    public async Task SendAsync_PostsExactBodyAndHeaders_Once_OverHttp11()
    {
        using var server = CapturingHttpServer.Start();
        using var transport = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, server.Port);
        var body = "cao-bridge-body"u8.ToArray();

        var sent = await transport.SendAsync(ValidToken, body, CancellationToken.None);

        Assert.True(sent);
        var request = Assert.Single(server.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/api/session-ingest/v2/events", request.Path);
        Assert.Equal(new Version(1, 1), request.ProtocolVersion);
        Assert.Equal("application/json; charset=utf-8", request.ContentType);
        Assert.Equal(body, request.Body);
        var headerValues = Assert.Single(request.Headers, pair => pair.Key.Equals(SkillRuntimeBridgeHttpTransportV1.CapabilityHeaderName, StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Equal([ValidToken], headerValues);
    }

    [Fact]
    public async Task SendAsync_LargeBody_ArrivesByteIdentical()
    {
        using var server = CapturingHttpServer.Start();
        using var transport = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, server.Port);
        var body = new byte[1_000_000];
        Random.Shared.NextBytes(body);

        var sent = await transport.SendAsync(ValidToken, body, CancellationToken.None);

        Assert.True(sent);
        Assert.Equal(body, Assert.Single(server.Requests).Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Created)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    [InlineData(HttpStatusCode.Found)]
    [InlineData(HttpStatusCode.SeeOther)]
    [InlineData(HttpStatusCode.TemporaryRedirect)]
    [InlineData(HttpStatusCode.PermanentRedirect)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SendAsync_Non204Responses_AreSanitizedUnavailability(HttpStatusCode statusCode)
    {
        using var server = CapturingHttpServer.Start(_ => (int)statusCode);
        using var transport = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, server.Port);

        var sent = await transport.SendAsync(ValidToken, "body"u8.ToArray(), CancellationToken.None);

        Assert.False(sent);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task SendAsync_RedirectResponse_IsNotFollowed()
    {
        using var redirectTarget = CapturingHttpServer.Start();
        using var origin = CapturingHttpServer.Start(_ => 302);
        origin.RedirectTargetPort = redirectTarget.Port;
        using var transport = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, origin.Port);

        var sent = await transport.SendAsync(ValidToken, "body"u8.ToArray(), CancellationToken.None);

        Assert.False(sent);
        Assert.Single(origin.Requests);
        Assert.Empty(redirectTarget.Requests);
    }

    [Fact]
    public async Task SendAsync_IgnoresHttpProxyEnvironmentCarrier()
    {
        using var server = CapturingHttpServer.Start();
        var deadProxyPort = GetFreePort();
        var previousUpper = Environment.GetEnvironmentVariable("HTTP_PROXY");
        var previousLower = Environment.GetEnvironmentVariable("http_proxy");
        try
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", $"http://127.0.0.1:{deadProxyPort}");
            Environment.SetEnvironmentVariable("http_proxy", $"http://127.0.0.1:{deadProxyPort}");
            using var transport = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, server.Port);

            var sent = await transport.SendAsync(ValidToken, "body"u8.ToArray(), CancellationToken.None);

            Assert.True(sent);
            Assert.Single(server.Requests);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HTTP_PROXY", previousUpper);
            Environment.SetEnvironmentVariable("http_proxy", previousLower);
        }
    }

    [Fact]
    public async Task SendAsync_AmbientW3CActivity_InjectsNoTracePropagationHeaders()
    {
        using var server = CapturingHttpServer.Start();
        using var transport = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, server.Port);
        using var activity = new Activity("cao.transport.probe");
        activity.ActivityTraceFlags |= ActivityTraceFlags.Recorded;
        activity.Start();

        var sent = await transport.SendAsync(ValidToken, "body"u8.ToArray(), CancellationToken.None);

        Assert.True(sent);
        var request = Assert.Single(server.Requests);
        Assert.DoesNotContain(request.Headers, pair => pair.Key.Equals("traceparent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(request.Headers, pair => pair.Key.Equals("tracestate", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(request.Headers, pair => pair.Key.Equals("Request-Id", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SendAsync_CancelledToken_ReturnsFalse()
    {
        using var server = CapturingHttpServer.Start();
        using var transport = new SkillRuntimeBridgeHttpTransportV1(IPAddress.Loopback, server.Port);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var sent = await transport.SendAsync(ValidToken, "body"u8.ToArray(), cancellation.Token);

        Assert.False(sent);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class CapturingHttpServer : IDisposable
    {
        private readonly HttpListener listener;
        private readonly Task acceptLoop;
        private readonly CancellationTokenSource stop = new();
        private readonly Func<HttpListenerRequest, int> statusSelector;

        private CapturingHttpServer(HttpListener listener, Func<HttpListenerRequest, int> statusSelector)
        {
            this.listener = listener;
            this.statusSelector = statusSelector;
            acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public int Port { get; private set; }
        public int? RedirectTargetPort { get; set; }
        public ConcurrentQueue<CapturedRequest> Requests { get; } = new();

        public static CapturingHttpServer Start(Func<HttpListenerRequest, int>? statusSelector = null)
        {
            var candidate = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            candidate.Start();
            var port = ((IPEndPoint)candidate.LocalEndpoint).Port;
            candidate.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();

            return new CapturingHttpServer(listener, statusSelector ?? (_ => 204)) { Port = port };
        }

        private async Task AcceptLoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    using var bodyBuffer = new MemoryStream();
                    await context.Request.InputStream.CopyToAsync(bodyBuffer);
                    var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                    foreach (var name in context.Request.Headers.AllKeys)
                    {
                        if (name is null)
                        {
                            continue;
                        }

                        headers[name] = context.Request.Headers.GetValues(name) ?? [];
                    }

                    Requests.Enqueue(new CapturedRequest(
                        context.Request.HttpMethod,
                        context.Request.Url?.AbsolutePath ?? string.Empty,
                        context.Request.ProtocolVersion,
                        context.Request.ContentType,
                        headers,
                        bodyBuffer.ToArray()));

                    var statusCode = statusSelector(context.Request);
                    context.Response.StatusCode = statusCode;
                    if (statusCode is 301 or 302 or 303 or 307 or 308)
                    {
                        context.Response.RedirectLocation = $"http://127.0.0.1:{RedirectTargetPort ?? Port}/elsewhere";
                    }

                    context.Response.Close();
                }
                catch (Exception)
                {
                    // A capture failure is surfaced by the test's assertions on Requests.
                }
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            try
            {
                listener.Stop();
                listener.Close();
            }
            catch (Exception)
            {
            }

            try
            {
                acceptLoop.Wait(TimeSpan.FromSeconds(5));
            }
            catch (Exception)
            {
            }

            stop.Dispose();
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        Version ProtocolVersion,
        string? ContentType,
        IReadOnlyDictionary<string, string[]> Headers,
        byte[] Body);
}

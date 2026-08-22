using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal interface ISkillRuntimeBridgeTransport
{
    Task<bool> SendAsync(string capabilityToken, ReadOnlyMemory<byte> bodyUtf8, CancellationToken cancellationToken);
}

internal sealed class SkillRuntimeBridgeHttpTransportV1 : ISkillRuntimeBridgeTransport, IDisposable
{
    public const string CapabilityHeaderName = "X-CAO-Skill-Runtime-Capability";
    private const string VersionHeaderName = "X-CAO-Session-Event-Version";
    internal const string IngestRoutePath = "/api/session-ingest/v2/events";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient client;

    public SkillRuntimeBridgeHttpTransportV1(IPAddress loopbackAddress, int port)
    {
        ArgumentNullException.ThrowIfNull(loopbackAddress);
        if (!IPAddress.IsLoopback(loopbackAddress))
        {
            throw new ArgumentException("The v2 transport accepts only an already-bound numeric loopback address.", nameof(loopbackAddress));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be within 1..65535.");
        }

        Endpoint = new Uri($"http://{FormatNumericLiteral(loopbackAddress)}:{port}{IngestRoutePath}");

        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            ActivityHeadersPropagator = null,
            AutomaticDecompression = DecompressionMethods.None
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
    }

    public Uri Endpoint { get; }

    public async Task<bool> SendAsync(string capabilityToken, ReadOnlyMemory<byte> bodyUtf8, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new ByteArrayContent(bodyUtf8.ToArray())
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" } }
                }
            };
            request.Headers.TryAddWithoutValidation(VersionHeaderName, "2");
            request.Headers.TryAddWithoutValidation(CapabilityHeaderName, capabilityToken);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => client.Dispose();

    private static string FormatNumericLiteral(IPAddress address)
    {
        var literal = address.ToString();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (literal.Contains('%'))
            {
                throw new ArgumentException("Scoped IPv6 literals are not accepted.", nameof(address));
            }

            return $"[{literal}]";
        }

        foreach (var c in literal)
        {
            if (c != '.' && !char.IsAsciiDigit(c))
            {
                throw new ArgumentException("Only numeric IPv4 literals are accepted.", nameof(address));
            }
        }

        return literal;
    }
}

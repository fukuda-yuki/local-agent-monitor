using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Hosting;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class SkillRuntimeBridgeHolderV1
{
    private SkillRuntimeCapabilityBridgeV1? current;

    internal bool HasBridge => Volatile.Read(ref current) is not null;

    internal SkillRuntimeCapabilityBridgeV1? CurrentBridge => Volatile.Read(ref current);

    public SkillRuntimeBridgeTransfer? TryConsume(string? token)
    {
        var bridge = Volatile.Read(ref current);
        return bridge is not null && bridge.TryConsume(token, out var transfer)
            ? transfer
            : null;
    }

    public void Publish(SkillRuntimeCapabilityBridgeV1 bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        Volatile.Write(ref current, bridge);
    }

    public void Clear(SkillRuntimeCapabilityBridgeV1 bridge)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        Interlocked.CompareExchange(ref current, null, bridge);
    }
}

internal sealed class SkillRuntimeBridgeLifetimeV1(
    IServer server,
    SkillRuntimeBridgeHolderV1 holder,
    CopilotRuntimeAdmissionV1 admission,
    IHostApplicationLifetime applicationLifetime) : IHostedService
{
    private IDisposable? applicationStartedRegistration;
    private SkillRuntimeCapabilityBridgeV1? bridge;
    private SkillRuntimeBridgeHttpTransportV1? transport;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The sole sender may target only the already-bound numeric loopback endpoint, so the
        // bridge cannot exist before that address does. User-registered hosted services start
        // before the web host, which is why the address is resolved on ApplicationStarted rather
        // than here; an unresolvable address leaves the holder empty rather than guessing one.
        applicationStartedRegistration = applicationLifetime.ApplicationStarted.Register(PublishBridge);
        return Task.CompletedTask;
    }

    private void PublishBridge()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null
            || addresses.Count != 1
            || !TryResolveLoopbackOrigin(addresses.Single(), out var loopbackAddress, out var port))
        {
            return;
        }

        var createdTransport = new SkillRuntimeBridgeHttpTransportV1(loopbackAddress, port);
        var createdBridge = new SkillRuntimeCapabilityBridgeV1(
            admission,
            createdTransport,
            SkillRuntimeCapabilityBridgeV1.CreateMonotonicClock(),
            SkillRuntimeCapabilityBridgeV1.CreateCryptographicToken);
        transport = createdTransport;
        bridge = createdBridge;
        holder.Publish(createdBridge);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref applicationStartedRegistration, null)?.Dispose();
        var stoppedBridge = Interlocked.Exchange(ref bridge, null);
        if (stoppedBridge is not null)
        {
            holder.Clear(stoppedBridge);
            stoppedBridge.ClearPendingEntriesAndReleaseCapabilities();
        }

        Interlocked.Exchange(ref transport, null)?.Dispose();
        return Task.CompletedTask;
    }

    private static bool TryResolveLoopbackOrigin(
        string address,
        out IPAddress loopbackAddress,
        out int port)
    {
        loopbackAddress = IPAddress.None;
        port = 0;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var origin)
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || origin.UserInfo.Length != 0
            || origin.AbsolutePath != "/"
            || origin.Query.Length != 0
            || origin.Fragment.Length != 0
            || !IPAddress.TryParse(origin.Host, out var parsedAddress)
            || !IPAddress.IsLoopback(parsedAddress)
            || parsedAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                && parsedAddress.ScopeId != 0
            || origin.Port is < 1 or > 65535)
        {
            return false;
        }

        loopbackAddress = parsedAddress;
        port = origin.Port;
        return true;
    }
}

using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;

namespace CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

public sealed class SkillRuntimeBridgeLifetimeV1Tests
{
    private const string UnknownValidToken = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    public static TheoryData<string[]> RejectedAddressSets => new()
    {
        Array.Empty<string>(),
        new[] { "http://127.0.0.1:5000", "http://[::1]:5001" },
        new[] { "http://localhost:5000" },
        new[] { "http://10.0.0.5:5000" },
        new[] { "http://8.8.8.8:5000" },
        new[] { "https://127.0.0.1:5000" },
        new[] { "http://127.0.0.1:5000/monitor" },
        new[] { "http://user@127.0.0.1:5000" },
    };

    [Fact]
    public async Task StartAsync_WithoutAddressesFeature_CompletesAndLeavesHolderEmpty()
    {
        var holder = new SkillRuntimeBridgeHolderV1();
        var lifetime = new SkillRuntimeBridgeLifetimeV1(
            new FakeServer(new FeatureCollection()),
            holder,
            new CopilotRuntimeAdmissionV1());

        await lifetime.StartAsync(CancellationToken.None);

        Assert.Null(holder.TryConsume(UnknownValidToken));
    }

    [Theory]
    [MemberData(nameof(RejectedAddressSets))]
    public async Task StartAsync_WithUnusableAddresses_CompletesAndLeavesHolderEmpty(string[] addresses)
    {
        var holder = new SkillRuntimeBridgeHolderV1();
        var lifetime = CreateLifetime(addresses, holder);

        await lifetime.StartAsync(CancellationToken.None);

        Assert.Null(holder.TryConsume(UnknownValidToken));
    }

    [Theory]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    public async Task StartAsync_WithSingleNumericLoopbackHttpOrigin_PublishesBridgeAndCanRestart(string address)
    {
        var holder = new SkillRuntimeBridgeHolderV1();
        var lifetime = CreateLifetime([address], holder);

        await lifetime.StartAsync(CancellationToken.None);

        Assert.True(holder.HasBridge);
        await lifetime.StopAsync(CancellationToken.None);
        Assert.False(holder.HasBridge);

        var secondHolder = new SkillRuntimeBridgeHolderV1();
        var secondLifetime = CreateLifetime([address], secondHolder);
        await secondLifetime.StartAsync(CancellationToken.None);

        Assert.True(secondHolder.HasBridge);
        await secondLifetime.StopAsync(CancellationToken.None);
    }

    private static SkillRuntimeBridgeLifetimeV1 CreateLifetime(
        IReadOnlyCollection<string> addresses,
        SkillRuntimeBridgeHolderV1 holder)
    {
        var addressFeature = new ServerAddressesFeature();
        foreach (var address in addresses)
        {
            addressFeature.Addresses.Add(address);
        }

        var features = new FeatureCollection();
        features.Set<IServerAddressesFeature>(addressFeature);
        return new SkillRuntimeBridgeLifetimeV1(
            new FakeServer(features),
            holder,
            new CopilotRuntimeAdmissionV1());
    }

    private sealed class FakeServer(IFeatureCollection features) : IServer
    {
        public IFeatureCollection Features { get; } = features;

        public void Dispose()
        {
        }

        public Task StartAsync<TContext>(
            IHttpApplication<TContext> application,
            CancellationToken cancellationToken) where TContext : notnull =>
            throw new NotSupportedException();

        public Task StopAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}

using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.Tests.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

public sealed class CopilotSdkSkillDiscoveryGatewayTests
{
    [Fact]
    public async Task Discover_UsesExactCapabilityOwnerClient()
    {
        var ownerClient = new FakeSkillRuntimeClient
        {
            DiscoverResult = [new("owned", "skill-directory", "skill.md", null, null, null, true, true)]
        };
        var otherClient = new FakeSkillRuntimeClient();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var owner = admission.PublishReadyTestCandidate(ownerClient, out _);
        _ = otherClient;
        Assert.True(owner.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        var outcome = await new CopilotSdkSkillDiscoveryGateway().DiscoverAsync(
            capability!, DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, []), CancellationToken.None);

        var discovered = Assert.IsType<CopilotSkillDiscoveryOutcome.Discovered>(outcome);
        Assert.Equal("owned", Assert.Single(discovered.Facts).Name);
        Assert.Equal(1, ownerClient.DiscoverCalls);
        capability!.Release();
    }

    [Fact]
    public async Task Discover_FailureIsUnavailable()
    {
        var client = new FakeSkillRuntimeClient { DiscoverThrows = true };
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var owner = admission.PublishReadyTestCandidate(client, out _);
        Assert.True(owner.TryAcquireOperationCapability(CancellationToken.None, out var capability));

        Assert.IsType<CopilotSkillDiscoveryOutcome.Unavailable>(await new CopilotSdkSkillDiscoveryGateway().DiscoverAsync(
            capability!, DiscoveryRootSetV1.Create(SkillProducerPathKeyPlatform.Windows, []), CancellationToken.None));
        capability!.Release();
    }

    [Theory]
    [InlineData("1.0.65", 3, "1.0.65", true)]
    [InlineData("1.0.75", 3, null, true)]
    [InlineData("1.0.76", 3, null, false)]
    [InlineData("1.0.75", 4, null, false)]
    [InlineData("1.0.75", 3, "1.0.65", false)]
    public void IdentityCertifier_UsesExactRegistryTuple(
        string version, int protocol, string? sessionStartVersion, bool expected) =>
        Assert.Equal(expected, CopilotRuntimeIdentityCertifierV1.Certifies(
            new(version, protocol, sessionStartVersion)));
}

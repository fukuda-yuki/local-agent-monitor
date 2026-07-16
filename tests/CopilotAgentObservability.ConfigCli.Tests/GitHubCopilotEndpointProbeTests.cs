using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotEndpointProbeTests
{
    private const string CanonicalOrigin = "http://127.0.0.1:4320";

    [Fact]
    public void Classify_LiveStatusResponse_ReturnsLocalMonitorLive()
    {
        var result = Classify(Response(200, "{\"status\":\"live\"}"));

        Assert.Equal(GitHubCopilotEndpointClassification.LocalMonitorLive, result);
    }

    [Fact]
    public void Classify_LiveStatusResponseWithWhitespace_ReturnsLocalMonitorLive()
    {
        var result = Classify(Response(200, "{ \"status\" : \"live\" }"));

        Assert.Equal(GitHubCopilotEndpointClassification.LocalMonitorLive, result);
    }

    [Fact]
    public void Classify_ResponseWithAdditionalProperty_ReturnsForeignOwner()
    {
        var result = Classify(Response(200, "{\"status\":\"live\",\"extra\":1}"));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_ResponseWithDuplicateStatusProperty_ReturnsForeignOwner()
    {
        var result = Classify(Response(200, "{\"status\":\"live\",\"status\":\"live\"}"));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_ResponseWithNonExactStatusValue_ReturnsForeignOwner()
    {
        var result = Classify(Response(200, "{\"status\":\"Live\"}"));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Theory]
    [InlineData("\"live\"")]
    [InlineData("[]")]
    public void Classify_ResponseWithNonObjectJson_ReturnsForeignOwner(string body)
    {
        var result = Classify(Response(200, body));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_ResponseWithMalformedJson_ReturnsForeignOwner()
    {
        var result = Classify(Response(200, "{\"status\":\"live\""));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_ResponseWithSentinelByteOrIncompleteBody_ReturnsForeignOwner()
    {
        var result = Classify(new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            null,
            Enumerable.Repeat((byte)'x', 4097).ToArray(),
            false));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_ResponseWithOversizeTrustworthyContentLength_ReturnsForeignOwnerWithoutSecondProbe()
    {
        var result = Classify(new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            4097,
            [],
            true));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Theory]
    [InlineData(204)]
    [InlineData(404)]
    [InlineData(500)]
    public void Classify_NonSuccessResponse_ReturnsForeignOwner(int statusCode)
    {
        var result = Classify(Response(statusCode, "{\"status\":\"live\"}"));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_RedirectBlocked_ReturnsForeignOwner()
    {
        var result = Classify(new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.RedirectBlocked,
            302,
            null,
            [],
            true));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_ConnectionRefused_ReturnsMonitorNotRunning()
    {
        var result = Classify(SetupHttpProbeObservation.Refused);

        Assert.Equal(GitHubCopilotEndpointClassification.MonitorNotRunning, result);
    }

    [Theory]
    [InlineData(SetupHttpProbeOutcome.TimedOut)]
    [InlineData(SetupHttpProbeOutcome.TransportFailure)]
    public void Classify_TimeoutOrTransportFailure_ReturnsForeignOwner(SetupHttpProbeOutcome outcome)
    {
        var result = Classify(new SetupHttpProbeObservation(outcome, null, null, [], true));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    [Fact]
    public void Classify_UnknownObservationOutcome_ReturnsForeignOwner()
    {
        var result = Classify(new SetupHttpProbeObservation((SetupHttpProbeOutcome)999, null, null, [], true));

        Assert.Equal(GitHubCopilotEndpointClassification.ForeignOwner, result);
    }

    private static GitHubCopilotEndpointClassification Classify(SetupHttpProbeObservation observation)
    {
        var platform = new SetupTestPlatform(DateTimeOffset.UnixEpoch);
        platform.ScriptHttpProbe(observation);

        var result = GitHubCopilotEndpointProbe.Classify(platform, CanonicalOrigin);

        Assert.Equal(["http.get:http://127.0.0.1:4320:/health/live:500:4096"], platform.Operations);
        return result;
    }

    private static SetupHttpProbeObservation Response(int statusCode, string body) =>
        new(SetupHttpProbeOutcome.Response, statusCode, null, Encoding.UTF8.GetBytes(body), true);
}

using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal enum GitHubCopilotEndpointClassification
{
    LocalMonitorLive,
    MonitorNotRunning,
    ForeignOwner,
}

internal static class GitHubCopilotEndpointProbe
{
    private const string HealthLivePath = "/health/live";
    private const int TotalBudgetMilliseconds = 500;
    private const int MaxBodyBytes = 4096;

    public static GitHubCopilotEndpointClassification Classify(
        ISetupPlatform platform,
        string canonicalOrigin)
    {
        var observation = platform.HttpProbe.Get(
            canonicalOrigin,
            HealthLivePath,
            TotalBudgetMilliseconds,
            MaxBodyBytes);

        if (observation.Outcome == SetupHttpProbeOutcome.Refused)
        {
            return GitHubCopilotEndpointClassification.MonitorNotRunning;
        }

        if (observation.Outcome != SetupHttpProbeOutcome.Response ||
            observation.StatusCode != 200 ||
            observation.TrustworthyContentLength is < 0 or > MaxBodyBytes ||
            observation.Body.Length > MaxBodyBytes ||
            !observation.IsComplete)
        {
            return GitHubCopilotEndpointClassification.ForeignOwner;
        }

        return IsLiveStatusResponse(observation.Body)
            ? GitHubCopilotEndpointClassification.LocalMonitorLive
            : GitHubCopilotEndpointClassification.ForeignOwner;
    }

    private static bool IsLiveStatusResponse(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject ||
                !reader.Read() || reader.TokenType != JsonTokenType.PropertyName ||
                !reader.ValueTextEquals("status"u8) ||
                !reader.Read() || reader.TokenType != JsonTokenType.String ||
                !reader.ValueTextEquals("live"u8) ||
                !reader.Read() || reader.TokenType != JsonTokenType.EndObject)
            {
                return false;
            }

            return !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

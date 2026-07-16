using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal interface IGitHubCopilotTargetPartition
{
    string TargetToken { get; }

    GitHubCopilotPartitionPlan Plan(GitHubCopilotPartitionContext context);

    SetupPlanResult<SetupRevalidation> Revalidate(
        GitHubCopilotPartitionContext context,
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet);
}

internal sealed class GitHubCopilotPartitionContext
{
    private readonly Lazy<GitHubCopilotObservations> observations;
    private readonly Lazy<GitHubCopilotEndpointClassification> endpoint;

    internal GitHubCopilotPartitionContext(ISetupPlatform platform, SetupPlanRequest request)
    {
        Platform = platform ?? throw new ArgumentNullException(nameof(platform));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        observations = new Lazy<GitHubCopilotObservations>(() => GitHubCopilotDetection.Observe(Platform));
        endpoint = new Lazy<GitHubCopilotEndpointClassification>(() => GitHubCopilotEndpointProbe.Classify(Platform, Request.Endpoint));
    }

    internal ISetupPlatform Platform { get; }

    internal SetupPlanRequest Request { get; }

    internal GitHubCopilotObservations Observations => observations.Value;

    internal GitHubCopilotEndpointClassification Endpoint => endpoint.Value;
}

internal sealed record GitHubCopilotPartitionPlan(
    string? FailureCode,
    IReadOnlyList<SetupChangeRecord> Records,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> NextActions);

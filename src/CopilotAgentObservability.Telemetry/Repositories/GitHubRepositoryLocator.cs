namespace CopilotAgentObservability.Telemetry.Repositories;

public sealed class GitHubRepositoryLocator
{
    internal GitHubRepositoryLocator(
        string canonicalLocator,
        string locatorSha256,
        string displayOwner,
        string displayRepository)
    {
        CanonicalLocator = canonicalLocator;
        LocatorSha256 = locatorSha256;
        DisplayOwner = displayOwner;
        DisplayRepository = displayRepository;
    }

    public string CanonicalLocator { get; }
    public string LocatorSha256 { get; }
    public string DisplayOwner { get; }
    public string DisplayRepository { get; }
}

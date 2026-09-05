namespace CopilotAgentObservability.Telemetry.Repositories;

internal interface ILocalRepositoryLocator
{
    string Kind { get; }
    string CanonicalLocator { get; }
    string LocatorSha256 { get; }
    string DisplayOwner { get; }
    string DisplayRepository { get; }
}

public sealed class GitHubRepositoryLocator : ILocalRepositoryLocator
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
    public string Kind => "github_repository";
    public string LocatorSha256 { get; }
    public string DisplayOwner { get; }
    public string DisplayRepository { get; }
}

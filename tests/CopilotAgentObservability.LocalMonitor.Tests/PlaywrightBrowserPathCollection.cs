namespace CopilotAgentObservability.LocalMonitor.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlaywrightBrowserPathCollection
{
    public const string Name = "Playwright browser path environment";
}

using System.Reflection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Collection(PlaywrightBrowserPathCollection.Name)]
public class PlaywrightBrowserPathTests
{
    [Fact]
    public void Collection_DisablesParallelizationForSharedBrowserPathEnvironment()
    {
        var definition = Assert.IsType<CollectionDefinitionAttribute>(
            typeof(PlaywrightBrowserPathCollection).GetCustomAttribute(typeof(CollectionDefinitionAttribute)));

        Assert.True(definition.DisableParallelization);
    }

    [Fact]
    public void ConfigureDefault_PreservesExistingBrowserPath()
    {
        var previous = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", "C:\\custom-playwright-cache");

            PlaywrightBrowserPath.ConfigureDefault();

            Assert.Equal("C:\\custom-playwright-cache", Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", previous);
        }
    }

    [Fact]
    public void ConfigureDefault_SetsRepositoryLocalBrowserPathWhenUnset()
    {
        var previous = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
        try
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", null);

            PlaywrightBrowserPath.ConfigureDefault();

            var browserPath = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");
            Assert.NotNull(browserPath);
            Assert.EndsWith(Path.Combine("artifacts", "playwright-browsers"), browserPath, StringComparison.Ordinal);
            Assert.True(Path.IsPathFullyQualified(browserPath));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", previous);
        }
    }
}

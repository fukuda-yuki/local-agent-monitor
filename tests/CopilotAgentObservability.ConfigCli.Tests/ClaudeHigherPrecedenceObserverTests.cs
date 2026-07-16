using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeHigherPrecedenceObserverTests
{
    private const string Root = "C:\\repo";
    private const string ManagedPath = "C:\\Program Files\\ClaudeCode\\managed-settings.json";
    private static readonly IReadOnlyList<KeyValuePair<string, string>> Desired =
    [
        new("OTEL_TRACES_EXPORTER", "otlp"),
        new("OTEL_LOG_USER_PROMPTS", "1"),
    ];

    [Fact]
    public void Observe_ProcessEqualOmitsOwnershipAndProcessMismatchReturnsEnvironmentConflict()
    {
        var equal = Platform();
        equal.SeedProcessEnvironment("OTEL_TRACES_EXPORTER", "otlp");

        var equalResult = Observer(equal).Observe(Desired, includeContentCapture: true);

        Assert.Null(equalResult.FailureCode);
        Assert.False(equalResult.OwnedValues.Single(value => value.Key == "OTEL_TRACES_EXPORTER").IsUserOwned);
        Assert.Equal(SetupEffectiveSource.Environment, equalResult.OwnedValues.Single(value => value.Key == "OTEL_TRACES_EXPORTER").EffectiveSource);
        Assert.True(equalResult.OwnedValues.Single(value => value.Key == "OTEL_LOG_USER_PROMPTS").IsUserOwned);

        var mismatch = Platform();
        mismatch.SeedProcessEnvironment("OTEL_TRACES_EXPORTER", "console");

        var mismatchResult = Observer(mismatch).Observe(Desired, includeContentCapture: true);

        Assert.Equal("environment_override_conflict", mismatchResult.FailureCode);
        Assert.Empty(mismatchResult.OwnedValues);
    }

    [Fact]
    public void Observe_SelectedMachinePolicyDoesNotMergeLowerManagedFile()
    {
        var platform = Platform();
        platform.SeedManagedObservation(
            SetupManagedLocation.ClaudeCodeWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Present, "{\"env\":{}}"u8.ToArray(), true));
        platform.SeedFile(
            ManagedPath,
            Encoding.UTF8.GetBytes("{\"env\":{\"OTEL_TRACES_EXPORTER\":\"console\"}}"));

        var result = Observer(platform).Observe(Desired.Take(1).ToArray(), includeContentCapture: false);

        Assert.Null(result.FailureCode);
        Assert.Equal(["OTEL_TRACES_EXPORTER"], result.OwnedValues.Select(value => value.Key));
        Assert.Null(result.OwnedValues[0].EffectiveSource);
        Assert.DoesNotContain($"file.exists:{ManagedPath}", platform.Operations);
    }

    [Fact]
    public void Observe_LocalContentMismatchUsesContentPolicyConflictAndMalformedProjectFailsClosed()
    {
        var local = Platform();
        local.SeedFile(
            Path.Combine(Root, ".claude", "settings.local.json"),
            "{\"env\":{\"OTEL_LOG_USER_PROMPTS\":\"0\"}}"u8.ToArray());

        var localResult = Observer(local).Observe(Desired, includeContentCapture: true);

        Assert.Equal("content_policy_conflict", localResult.FailureCode);

        var malformed = Platform();
        malformed.SeedFile(
            Path.Combine(Root, ".claude", "settings.json"),
            "{\"env\":{"u8.ToArray());

        var malformedResult = Observer(malformed).Observe(Desired.Take(1).ToArray(), includeContentCapture: false);

        Assert.Equal("malformed_settings", malformedResult.FailureCode);
    }

    [Fact]
    public void Observe_AllProcessValuesEqual_DoesNotReadMalformedLowerSources()
    {
        var platform = Platform();
        foreach (var pair in Desired)
        {
            platform.SeedProcessEnvironment(pair.Key, pair.Value);
        }
        platform.SeedFile(ManagedPath, "{"u8.ToArray());
        platform.SeedFile(Path.Combine(Root, ".claude", "settings.local.json"), "{"u8.ToArray());
        platform.SeedFile(Path.Combine(Root, ".claude", "settings.json"), "{"u8.ToArray());

        var result = Observer(platform).Observe(Desired, includeContentCapture: true);

        Assert.Null(result.FailureCode);
        Assert.All(result.OwnedValues, value => Assert.False(value.IsUserOwned));
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("managed.read:", StringComparison.Ordinal));
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.exists:", StringComparison.Ordinal));
    }

    private static SetupTestPlatform Platform() => new(DateTimeOffset.UnixEpoch);

    private static ClaudeHigherPrecedenceObserver Observer(SetupTestPlatform platform) =>
        new(platform, Root, ManagedPath);
}

using Microsoft.Win32;
using System.Runtime.Versioning;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

[SupportedOSPlatform("windows")]
public sealed class ClaudeManagedSettingsRegistryTests
{
    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    public void ReadExactSettingsString_PreservesJsonWithoutExpandingEnvironment(RegistryValueKind kind)
    {
        string? observedName = null;
        RegistryValueOptions? observedOptions = null;

        var result = SystemSetupPlatform.ReadBoundedClaudeRegistrySettings(
            name =>
            {
                Assert.Equal("Settings", name);
                return kind;
            },
            (name, options) =>
            {
                observedName = name;
                observedOptions = options;
                return "{\"env\":{\"VALUE\":\"%UNEXPANDED%\"}}";
            });

        Assert.Equal(SetupManagedOutcome.Present, result.Outcome);
        Assert.True(result.IsComplete);
        Assert.Equal("Settings", observedName);
        Assert.Equal(RegistryValueOptions.DoNotExpandEnvironmentNames, observedOptions);
        Assert.Equal("{\"env\":{\"VALUE\":\"%UNEXPANDED%\"}}", System.Text.Encoding.UTF8.GetString(result.Bytes));
    }

    [Fact]
    public void ReadExactSettingsString_MissingValueIsAbsentWithoutReadingKind()
    {
        var kindRead = false;

        var result = SystemSetupPlatform.ReadBoundedClaudeRegistrySettings(
            _ =>
            {
                kindRead = true;
                return RegistryValueKind.String;
            },
            (_, _) => null);

        Assert.Equal(SetupManagedOutcome.Absent, result.Outcome);
        Assert.False(kindRead);
    }

    [Theory]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.Binary)]
    public void ReadExactSettingsString_WrongTypeFailsClosed(RegistryValueKind kind)
    {
        var result = SystemSetupPlatform.ReadBoundedClaudeRegistrySettings(
            _ => kind,
            (_, _) => kind == RegistryValueKind.DWord ? 1 : new byte[] { 1 });

        Assert.Equal(SetupManagedOutcome.Failed, result.Outcome);
    }

    [Fact]
    public void ReadExactSettingsString_OverOneMiBUtf8IsIncomplete()
    {
        var result = SystemSetupPlatform.ReadBoundedClaudeRegistrySettings(
            _ => RegistryValueKind.String,
            (_, _) => new string('a', 1024 * 1024 + 1));

        Assert.Equal(SetupManagedOutcome.Present, result.Outcome);
        Assert.False(result.IsComplete);
    }
}

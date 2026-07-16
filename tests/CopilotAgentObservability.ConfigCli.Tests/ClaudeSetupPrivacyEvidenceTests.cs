using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed partial class ClaudeCodeSetupAdapterTests
{
    [Fact]
    public void ProductionDispatch_ClaudeApplyKeepsPrivateSettingsPathHookAndTokenOutOfRepositorySafeEvidence()
    {
        const string RawSettingsMarker = "ISSUE68_RAW_SETTINGS_MARKER";
        const string AuthTokenMarker = "ISSUE68_AUTH_TOKEN_MARKER";
        const string HookCommand = "C:\\issue68-private-release\\Issue68HookMarker.exe";
        var platform = new SetupTestPlatform(AdapterTimestamp);
        SeedDirectoryChain(platform, Path.GetDirectoryName(ClaudeSettingsPath)!);
        platform.SeedFile(
            ClaudeSettingsPath,
            Encoding.UTF8.GetBytes($$"""
                {
                  "unrelated": "{{RawSettingsMarker}}"
                }
                """ + "\n"));
        ScriptVersionAndReadiness(platform);
        ScriptVersionAndReadiness(platform);
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(new ClaudeHookCommand(
                HookCommand,
                ["--authorization-token", AuthTokenMarker],
                ClaudeHookCommandMode.Release)));
        var planOptions = Assert.IsType<SetupOptions>(SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", "claude-code", "--target", "cli",
            "--endpoint", CanonicalOrigin,
        ]).Options);

        var plan = dispatch(planOptions);

        Assert.True(plan.Success, plan.Code);
        var changeSetId = Guid.Parse(plan.ChangeSetId!);
        var paths = new SetupRuntimePaths(platform);
        var privatePlan = Encoding.UTF8.GetString(platform.ReadSeededFile(paths.GetPlan(changeSetId)));
        AssertContainsJsonString(privatePlan, HookCommand);
        Assert.Contains(AuthTokenMarker, privatePlan, StringComparison.Ordinal);
        Assert.DoesNotContain(RawSettingsMarker, privatePlan, StringComparison.Ordinal);

        var applyOptions = Assert.IsType<SetupOptions>(SetupOptions.Parse(
            ["setup", "apply", "--change-set", plan.ChangeSetId!]).Options);
        var apply = dispatch(applyOptions);
        using var statusOutput = new StringWriter();
        using var statusError = new StringWriter();
        var statusExitCode = CliApplication.Run(
            ["setup", "status", "--adapter", "claude-code"],
            statusOutput,
            statusError,
            dispatch);
        using var repeatedApplyOutput = new StringWriter();
        using var repeatedApplyError = new StringWriter();
        var repeatedApplyExitCode = CliApplication.Run(
            ["setup", "apply", "--change-set", plan.ChangeSetId!],
            repeatedApplyOutput,
            repeatedApplyError,
            dispatch);

        Assert.True(apply.Success, apply.Code);
        Assert.Equal(0, statusExitCode);
        Assert.Equal(string.Empty, statusError.ToString());
        Assert.Equal(2, repeatedApplyExitCode);
        Assert.Equal(SetupCodes.InvalidArguments + "\n", repeatedApplyError.ToString());
        var recordId = Guid.Parse(Assert.Single(plan.Targets).RecordId);
        var backup = Encoding.UTF8.GetString(platform.ReadSeededFile(paths.GetBackup(changeSetId, recordId)));
        var appliedSettings = Encoding.UTF8.GetString(platform.ReadSeededFile(ClaudeSettingsPath));
        Assert.Contains(RawSettingsMarker, backup, StringComparison.Ordinal);
        Assert.Contains(RawSettingsMarker, appliedSettings, StringComparison.Ordinal);
        AssertContainsJsonString(appliedSettings, HookCommand);
        Assert.Contains(AuthTokenMarker, appliedSettings, StringComparison.Ordinal);

        var repositorySafeEvidence = new Dictionary<string, string>
        {
            ["plan stdout"] = SetupJson.Serialize(plan),
            ["apply stdout"] = SetupJson.Serialize(apply),
            ["status cli stdout"] = statusOutput.ToString(),
            ["repeated apply cli stdout"] = repeatedApplyOutput.ToString(),
            ["repeated apply cli stderr"] = repeatedApplyError.ToString(),
            ["ownership ledger"] = Encoding.UTF8.GetString(platform.ReadSeededFile(paths.OwnershipLedger)),
            ["transaction journal"] = Encoding.UTF8.GetString(platform.ReadSeededFile(paths.GetTransactionJournal(changeSetId))),
        };
        Assert.All(repositorySafeEvidence, evidence =>
        {
            Assert.False(string.IsNullOrWhiteSpace(evidence.Value), $"{evidence.Key} evidence must be non-empty.");
            Assert.DoesNotContain(RawSettingsMarker, evidence.Value, StringComparison.Ordinal);
            Assert.DoesNotContain(AuthTokenMarker, evidence.Value, StringComparison.Ordinal);
            AssertJsonStringAbsent(evidence.Value, ClaudeSettingsPath);
            AssertJsonStringAbsent(evidence.Value, HookCommand);
        });
    }

    private static void AssertContainsJsonString(string evidence, string value)
    {
        Assert.True(
            evidence.Contains(value, StringComparison.Ordinal) ||
            evidence.Contains(value.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal),
            "Expected private JSON evidence was absent.");
    }

    private static void AssertJsonStringAbsent(string evidence, string value)
    {
        Assert.DoesNotContain(value, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain(
            value.Replace("\\", "\\\\", StringComparison.Ordinal),
            evidence,
            StringComparison.Ordinal);
    }

    private static void SeedDirectoryChain(SetupTestPlatform platform, string directory)
    {
        var current = Path.GetPathRoot(directory)!;
        platform.SeedDirectory(current);
        foreach (var segment in directory[current.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            platform.SeedDirectory(current);
        }
    }
}

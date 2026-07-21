using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.ConfigCli.FirstTrace.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class FirstTraceSourceRegistryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Entries_AreTheFixedOrdinalCrossSurfaceRegistry()
    {
        Assert.Collection(
            FirstTraceSourceRegistry.Entries,
            entry => Assert.Equal(("github-copilot-vscode", "GitHub Copilot in VS Code", FirstTraceSetupOwnership.Managed),
                (entry.SourceId, entry.DisplayLabel, entry.SetupOwnership)),
            entry => Assert.Equal(("github-copilot-cli", "GitHub Copilot CLI", FirstTraceSetupOwnership.ManagedOnWindows),
                (entry.SourceId, entry.DisplayLabel, entry.SetupOwnership)),
            entry => Assert.Equal(("github-copilot-app-sdk", "GitHub Copilot App/SDK", FirstTraceSetupOwnership.CallerManaged),
                (entry.SourceId, entry.DisplayLabel, entry.SetupOwnership)),
            entry => Assert.Equal(("claude-code", "Claude Code", FirstTraceSetupOwnership.ManagedCliAndCallerManagedAgentSdk),
                (entry.SourceId, entry.DisplayLabel, entry.SetupOwnership)));

        Assert.Equal(
            FirstTraceSourceRegistry.Entries.Count,
            FirstTraceSourceRegistry.Entries.Select(entry => entry.SourceId).Distinct(StringComparer.Ordinal).Count());
        Assert.All(FirstTraceSourceRegistry.Entries, entry =>
        {
            Assert.NotEmpty(entry.ExpectedDoctorAdapter);
            Assert.NotEmpty(entry.AllowedInteractions);
        });

        var platform = new SetupTestPlatform(Now);
        var adapters = FirstTraceSourceRegistry.CreateAdapters(
            platform,
            _ => throw new InvalidOperationException());
        Assert.Equal(
            FirstTraceSourceRegistry.Entries.Select(entry => entry.SourceId),
            adapters.Select(adapter => adapter.AdapterId));
    }

    [Fact]
    public void DetectSources_UsesExplicitPresenceAndAbsenceWithoutTreatingFailuresAsAbsence()
    {
        var platform = new SetupTestPlatform(Now);
        platform.ScriptProcess("code", ["--list-extensions", "--show-versions"],
            new(SetupProcessOutcome.Completed, 0, "github.copilot-chat@1.2.3\n"));
        platform.ScriptProcess("code-insiders", ["--list-extensions", "--show-versions"],
            new(SetupProcessOutcome.NotFound, null, ""));
        platform.ScriptProcess("copilot", ["version"],
            new(SetupProcessOutcome.NotFound, null, ""));
        platform.ScriptProcess("claude", ["--version"],
            new(SetupProcessOutcome.TimedOut, null, ""));

        var states = FirstTraceSourceRegistry.DetectSources(platform);

        Assert.Equal(FirstTraceSourceDetectionState.Detected, states["github-copilot-vscode"]);
        Assert.Equal(FirstTraceSourceDetectionState.NotDetected, states["github-copilot-cli"]);
        Assert.Equal(FirstTraceSourceDetectionState.Unavailable, states["github-copilot-app-sdk"]);
        Assert.Equal(FirstTraceSourceDetectionState.Unavailable, states["claude-code"]);
    }

    [Fact]
    public void DetectSources_ReturnsUnavailableWhenVsCodeAbsenceIsAmbiguous()
    {
        var platform = new SetupTestPlatform(Now);
        platform.ScriptProcess("code", ["--list-extensions", "--show-versions"],
            new(SetupProcessOutcome.Failed, 1, ""));
        platform.ScriptProcess("code-insiders", ["--list-extensions", "--show-versions"],
            new(SetupProcessOutcome.Completed, 0, "publisher.unrelated@1.0.0\n"));
        platform.ScriptProcess("copilot", ["version"],
            new(SetupProcessOutcome.Completed, 1, "unsupported output"));
        platform.ScriptProcess("claude", ["--version"],
            new(SetupProcessOutcome.Completed, 1, "unsupported output"));

        var states = FirstTraceSourceRegistry.DetectSources(platform);

        Assert.Equal(FirstTraceSourceDetectionState.Unavailable, states["github-copilot-vscode"]);
        Assert.Equal(FirstTraceSourceDetectionState.Detected, states["github-copilot-cli"]);
        Assert.Equal(FirstTraceSourceDetectionState.Detected, states["claude-code"]);
    }

    [Fact]
    public void DetectSources_DistinguishesExplicitVsCodeAbsenceFromRunnerException()
    {
        var platform = new SetupTestPlatform(Now);
        platform.InjectFault("process.run:copilot:version", new InvalidOperationException("synthetic failure"));

        var states = FirstTraceSourceRegistry.DetectSources(platform);

        Assert.Equal(FirstTraceSourceDetectionState.NotDetected, states["github-copilot-vscode"]);
        Assert.Equal(FirstTraceSourceDetectionState.Unavailable, states["github-copilot-cli"]);
    }

    [Theory]
    [InlineData("github-copilot-vscode", "vscode")]
    [InlineData("github-copilot-cli", "cli")]
    [InlineData("github-copilot-app-sdk", "app-sdk")]
    public void GitHubAdapter_UsesExactSourceAndLeavesUnavailableSetupFactsUnknown(
        string sourceId,
        string expectedTarget)
    {
        SetupOptions? dispatched = null;
        var adapter = new GitHubCopilotFirstTraceAdapter(
            sourceId,
            options =>
            {
                dispatched = options;
                return new SetupCommandResult(
                    SetupCommand.Status,
                    false,
                    SetupCodes.SetupBusy,
                    null,
                    null,
                    null,
                    "github-copilot",
                    [],
                    [],
                    [],
                    [],
                    false);
            },
            () => Now);

        var facts = adapter.CollectFacts(
            "synthetic-doctor.db",
            "http://127.0.0.1:4320",
            verification: null);

        Assert.Equal(sourceId, adapter.AdapterId);
        Assert.Equal(sourceId, adapter.SourceSurface);
        Assert.Equal("github-copilot-doctor", adapter.ExpectedSourceAdapter);
        Assert.Equal(SetupCommand.Status, dispatched!.Command);
        Assert.Equal("github-copilot", dispatched.Adapter);
        Assert.Equal(sourceId, facts.SourceSurface);
        Assert.Equal("github-copilot-doctor", facts.ExpectedSourceAdapter);
        Assert.Null(facts.InstallAndSourceVersion);
        Assert.Contains(
            $"setup plan --adapter github-copilot --target {expectedTarget}",
            adapter.GetGuidance(null, includeSetupPlan: true).Select(item => item.Command));
        var expectedInteraction = sourceId switch
        {
            "github-copilot-vscode" => "vscode-chat",
            "github-copilot-cli" => "cli",
            _ => "app-sdk",
        };
        Assert.True(adapter.IsValidInteraction(null));
        Assert.True(adapter.IsValidInteraction(expectedInteraction));
        Assert.False(adapter.IsValidInteraction("wrong-interaction"));
        Assert.Contains(expectedInteraction, adapter.GetGuidance(null, false).Select(item => item.Interaction));
    }

    [Fact]
    public void GitHubEvidenceSelection_RequiresExplicitOpaqueReferences()
    {
        var adapter = new GitHubCopilotFirstTraceAdapter(
            "github-copilot-cli",
            _ => throw new InvalidOperationException(),
            () => Now);
        var candidate = new DoctorEvidenceCandidate(
            Guid.CreateVersion7().ToString("D"),
            Guid.CreateVersion7().ToString("D"),
            "github-copilot-cli",
            "github-copilot-doctor",
            DoctorEvidenceClass.RealSource,
            DoctorEvidenceKind.Ingest,
            "gc_doctor_00000000_00000000_00000000_00000000_00000000",
            Now,
            Now.AddMinutes(5));

        var selection = adapter.SelectEvidence([candidate], Now);

        Assert.True(selection.RequiresExplicitSelection);
        Assert.True(selection.HasEligibleCandidates);
        Assert.Empty(selection.EvidenceRefs);
    }

    [Fact]
    public void Help_ListsTheCompleteFirstTraceLifecycleWithoutAdapterOnStoredVerificationCommands()
    {
        Assert.Contains("first-trace begin --database <file> --adapter <github-copilot-vscode|github-copilot-cli|github-copilot-app-sdk|claude-code>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("first-trace status --database <file> --verification-id <uuid-v7>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("first-trace complete --database <file> --verification-id <uuid-v7>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.Contains("first-trace cancel --database <file> --verification-id <uuid-v7>", CliHelpText.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("first-trace status --database <file> --adapter", CliHelpText.Text, StringComparison.Ordinal);
    }
}

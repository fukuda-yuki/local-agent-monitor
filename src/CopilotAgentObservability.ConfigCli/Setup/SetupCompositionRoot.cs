using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.AppSdk;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.CopilotCli;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.VsCode;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli;

internal static class SetupCompositionRoot
{
    public static Func<SetupOptions, SetupCommandResult> CreateSetupDispatch() =>
        CreateSetupDispatch(new SystemSetupPlatform());

    internal static Func<SetupOptions, SetupCommandResult> CreateSetupDispatch(ISetupPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);

        var paths = new SetupRuntimePaths(platform);
        var planStore = new SetupPlanStore(platform, paths);
        var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
        var journalStore = new SetupTransactionJournalStore(platform, paths);
        var adapter = new GitHubCopilotSetupAdapter(
            platform,
            [
                new VsCodeTargetPartition(),
                new CopilotCliTargetPartition(),
                new AppSdkTargetPartition(),
            ]);
        var registry = new SetupAdapterRegistry([adapter]);
        var toolVersion = typeof(SetupCompositionRoot).Assembly.GetName().Version!.ToString();
        var dispatcher = new SetupCommandDispatcher(
            platform,
            paths,
            planStore,
            ledgerStore,
            journalStore,
            registry,
            toolVersion);

        return dispatcher.Dispatch;
    }
}

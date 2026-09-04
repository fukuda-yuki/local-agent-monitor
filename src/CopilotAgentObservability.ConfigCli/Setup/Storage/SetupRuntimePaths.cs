using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

public sealed class SetupRuntimePaths
{
    public SetupRuntimePaths(ISetupPlatform platform)
    {
        Root = SetupPathPolicy.Combine(platform.PathStyle, platform.LocalApplicationData, "CopilotAgentObservability", "LocalMonitor", "setup");
        OwnershipLedger = SetupPathPolicy.Combine(platform.PathStyle, Root, "ownership-ledger.v1.json");
        Lock = SetupPathPolicy.Combine(platform.PathStyle, Root, "setup.lock");
        Plans = SetupPathPolicy.Combine(platform.PathStyle, Root, "plans");
        Backups = SetupPathPolicy.Combine(platform.PathStyle, Root, "backups");
        Transactions = SetupPathPolicy.Combine(platform.PathStyle, Root, "transactions");
        this.platform = platform;
    }

    private readonly ISetupPlatform platform;

    public string Root { get; }

    public string OwnershipLedger { get; }

    public string Lock { get; }

    public string Plans { get; }

    public string Backups { get; }

    public string Transactions { get; }

    public void EnsureRoot() => platform.FileSystem.CreateDirectory(Root);

    public string GetPlan(Guid changeSetId) => SetupPathPolicy.Combine(platform.PathStyle, Plans, $"{changeSetId:D}.json");

    public string GetBackup(Guid changeSetId, Guid recordId) => SetupPathPolicy.Combine(platform.PathStyle, Backups, $"{changeSetId:D}", $"{recordId:D}.backup");

    public string GetTransactionJournal(Guid changeSetId) => SetupPathPolicy.Combine(platform.PathStyle, Transactions, $"{changeSetId:D}.journal.json");
}

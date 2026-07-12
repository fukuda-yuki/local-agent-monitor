using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

public sealed class SetupRuntimePaths
{
    public SetupRuntimePaths(ISetupPlatform platform)
    {
        Root = Path.Combine(platform.LocalApplicationData, "CopilotAgentObservability", "LocalMonitor", "setup");
        OwnershipLedger = Path.Combine(Root, "ownership-ledger.v1.json");
        Lock = Path.Combine(Root, "setup.lock");
        Plans = Path.Combine(Root, "plans");
        Backups = Path.Combine(Root, "backups");
        Transactions = Path.Combine(Root, "transactions");
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

    public string GetPlan(Guid changeSetId) => Path.Combine(Plans, $"{changeSetId:D}.json");

    public string GetBackup(Guid changeSetId, Guid recordId) => Path.Combine(Backups, $"{changeSetId:D}", $"{recordId:D}.backup");

    public string GetTransactionJournal(Guid changeSetId) => Path.Combine(Transactions, $"{changeSetId:D}.journal.json");
}

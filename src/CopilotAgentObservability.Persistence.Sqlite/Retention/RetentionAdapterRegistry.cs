using System.Collections.Frozen;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed class RetentionAdapterRegistry
{
    private const string ValidationMessage = "retention_adapter_registry_invalid";
    private static readonly FrozenSet<RetentionStoreKind> RetentionV1StoreKinds =
    [
        RetentionStoreKind.SessionEventContent,
        RetentionStoreKind.RawRecord,
        RetentionStoreKind.AnalysisRunRaw,
        RetentionStoreKind.SensitiveBundle,
        RetentionStoreKind.AnalysisSdkDirectory
    ];
    private readonly IReadOnlyDictionary<RetentionStoreKind, IRetentionDeletionAdapter> adapters;

    public RetentionAdapterRegistry(IEnumerable<IRetentionDeletionAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var supplied = adapters.ToArray();
        if (supplied.Length != RetentionV1StoreKinds.Count || supplied.Any(adapter => adapter is null)) throw new ArgumentException(ValidationMessage);

        var byKind = new Dictionary<RetentionStoreKind, IRetentionDeletionAdapter>();
        foreach (var adapter in supplied)
        {
            if (!RetentionV1StoreKinds.Contains(adapter.StoreKind) || !byKind.TryAdd(adapter.StoreKind, adapter)) throw new ArgumentException(ValidationMessage);
        }

        if (RetentionV1StoreKinds.Any(kind => !byKind.ContainsKey(kind))) throw new ArgumentException(ValidationMessage);

        this.adapters = new System.Collections.ObjectModel.ReadOnlyDictionary<RetentionStoreKind, IRetentionDeletionAdapter>(byKind);
    }

    public int CoverageVersion => RetentionV1Constants.AdapterCoverageVersion;

    public IRetentionDeletionAdapter Get(RetentionStoreKind storeKind)
    {
        if (!RetentionV1StoreKinds.Contains(storeKind)) throw new ArgumentOutOfRangeException(nameof(storeKind), ValidationMessage);
        return adapters[storeKind];
    }
}

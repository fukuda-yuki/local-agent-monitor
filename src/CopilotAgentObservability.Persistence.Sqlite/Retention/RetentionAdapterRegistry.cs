namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed class RetentionAdapterRegistry
{
    private const string ValidationMessage = "retention_adapter_registry_invalid";
    private readonly IReadOnlyDictionary<RetentionStoreKind, IRetentionDeletionAdapter> adapters;

    public RetentionAdapterRegistry(IEnumerable<IRetentionDeletionAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        var supplied = adapters.ToArray();
        var expectedKinds = Enum.GetValues<RetentionStoreKind>();
        if (supplied.Length != expectedKinds.Length || supplied.Any(adapter => adapter is null)) throw new ArgumentException(ValidationMessage);

        var byKind = new Dictionary<RetentionStoreKind, IRetentionDeletionAdapter>();
        foreach (var adapter in supplied)
        {
            if (!Enum.IsDefined(adapter.StoreKind) || !byKind.TryAdd(adapter.StoreKind, adapter)) throw new ArgumentException(ValidationMessage);
        }

        if (expectedKinds.Any(kind => !byKind.ContainsKey(kind))) throw new ArgumentException(ValidationMessage);

        this.adapters = new System.Collections.ObjectModel.ReadOnlyDictionary<RetentionStoreKind, IRetentionDeletionAdapter>(byKind);
    }

    public int CoverageVersion => RetentionV1Constants.AdapterCoverageVersion;

    public IRetentionDeletionAdapter Get(RetentionStoreKind storeKind)
    {
        if (!Enum.IsDefined(storeKind)) throw new ArgumentOutOfRangeException(nameof(storeKind), ValidationMessage);
        return adapters[storeKind];
    }
}

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed class RetentionReadGate
{
    private readonly RetentionCatalogStore catalog;

    public RetentionReadGate(RetentionCatalogStore catalog) => this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public ValueTask<RetentionReadLeaseHandle?> TryAcquireAsync(
        RetentionOwnershipKey ownershipKey,
        long expectedRevision,
        RetentionLeaseKind leaseKind,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        catalog.TryAcquireAsync(ownershipKey, expectedRevision, leaseKind, now, cancellationToken);
}

using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RetentionReadGrant
{
    private readonly object leasePublicationGate = new();
    private readonly byte[] sourceToken;
    private DateTimeOffset leaseExpiresAt;

    internal RetentionReadGrant(string itemId, long revision, string leaseOwner, long leaseGeneration, DateTimeOffset leaseExpiresAt, byte[] sourceToken)
    {
        ItemId = itemId;
        Revision = revision;
        LeaseOwner = leaseOwner;
        LeaseGeneration = leaseGeneration;
        this.leaseExpiresAt = leaseExpiresAt;
        this.sourceToken = sourceToken;
    }

    internal string ItemId { get; }
    internal long Revision { get; }
    internal string LeaseOwner { get; }
    internal long LeaseGeneration { get; }
    internal DateTimeOffset LeaseExpiresAt
    {
        get
        {
            using var publication = EnterLeasePublication();
            return publication.LeaseExpiresAt;
        }
    }

    internal void AdvanceExpiry(DateTimeOffset expiry)
    {
        using var publication = EnterLeasePublication();
        publication.AdvanceExpiry(expiry);
    }

    // Database participants acquire their immediate transaction before retaining this scope.
    internal LeasePublication EnterLeasePublication() => new(this);

    internal bool TryBindSelectorCapability(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Monitor.TryEnter(leasePublicationGate))
            return false;

        using var publication = LeasePublication.FromEnteredGate(this);
        publication.BindSelectorCapability(command);
        return true;
    }

    internal void BindSelectorCapability(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        using var publication = EnterLeasePublication();
        publication.BindSelectorCapability(command);
    }

    internal ref struct LeasePublication
    {
        private RetentionReadGrant? owner;

        internal LeasePublication(RetentionReadGrant owner)
        {
            Monitor.Enter(owner.leasePublicationGate);
            this.owner = owner;
        }

        internal static LeasePublication FromEnteredGate(RetentionReadGrant owner)
        {
            var publication = default(LeasePublication);
            publication.owner = owner;
            return publication;
        }

        internal DateTimeOffset LeaseExpiresAt => Owner.leaseExpiresAt;

        internal void AdvanceExpiry(DateTimeOffset expiry) => Owner.leaseExpiresAt = expiry;

        internal void BindSelectorCapability(SqliteCommand command)
        {
            var grant = Owner;
            command.Parameters.AddWithValue("$retention_read_source_token", grant.sourceToken);
            command.Parameters.AddWithValue("$retention_read_item_id", grant.ItemId);
            command.Parameters.AddWithValue("$retention_read_revision", grant.Revision);
            command.Parameters.AddWithValue("$retention_read_lease_owner", grant.LeaseOwner);
            command.Parameters.AddWithValue("$retention_read_lease_generation", grant.LeaseGeneration);
            command.Parameters.AddWithValue("$retention_read_lease_expires_at", grant.leaseExpiresAt.ToUniversalTime().ToString("O"));
        }

        internal void Dispose()
        {
            var grant = owner;
            owner = null;
            if (grant is not null) Monitor.Exit(grant.leasePublicationGate);
        }

        private RetentionReadGrant Owner => owner ?? throw new ObjectDisposedException(nameof(LeasePublication));
    }
}

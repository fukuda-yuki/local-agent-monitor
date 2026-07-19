using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RetentionReadGrant
{
    private readonly byte[] sourceToken;

    internal RetentionReadGrant(string itemId, long revision, string leaseOwner, long leaseGeneration, DateTimeOffset leaseExpiresAt, byte[] sourceToken)
    {
        ItemId = itemId;
        Revision = revision;
        LeaseOwner = leaseOwner;
        LeaseGeneration = leaseGeneration;
        LeaseExpiresAt = leaseExpiresAt;
        this.sourceToken = sourceToken;
    }

    internal string ItemId { get; }
    internal long Revision { get; }
    internal string LeaseOwner { get; }
    internal long LeaseGeneration { get; }
    internal DateTimeOffset LeaseExpiresAt { get; }

    internal void BindSelectorCapability(SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        BindPrivateSourceToken(command);
        command.Parameters.AddWithValue("$retention_read_item_id", ItemId);
        command.Parameters.AddWithValue("$retention_read_revision", Revision);
        command.Parameters.AddWithValue("$retention_read_lease_owner", LeaseOwner);
        command.Parameters.AddWithValue("$retention_read_lease_generation", LeaseGeneration);
        command.Parameters.AddWithValue("$retention_read_lease_expires_at", LeaseExpiresAt.ToUniversalTime().ToString("O"));
    }

    private void BindPrivateSourceToken(SqliteCommand command) => command.Parameters.AddWithValue("$retention_read_source_token", sourceToken);
}

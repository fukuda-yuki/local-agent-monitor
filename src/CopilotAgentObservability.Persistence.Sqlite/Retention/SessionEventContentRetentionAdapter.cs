namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class SessionEventContentRetentionAdapter : IRetentionDeletionAdapter
{
    private readonly RetentionCatalogStore catalog;
    private readonly TimeProvider timeProvider;
    private readonly ILocalWorkspaceProjectionTransactionParticipant participant;
    private readonly ILocalWorkspacePublicationGate? publicationGate;

    internal SessionEventContentRetentionAdapter(
        RetentionCatalogStore catalog,
        TimeProvider? timeProvider = null,
        ILocalWorkspaceProjectionTransactionParticipant? participant = null,
        ILocalWorkspacePublicationGate? publicationGate = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.participant = participant ?? UnconfiguredLocalWorkspaceProjectionTransactionParticipant.Instance;
        this.publicationGate = publicationGate;
    }

    public RetentionStoreKind StoreKind => RetentionStoreKind.SessionEventContent;

    public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
    {
        await using var publicationLease = publicationGate is null
            ? null
            : await publicationGate.AcquireReadAsync(context.CancellationToken);
        return await catalog.ExecuteSqliteDeletionAsync(context, (connection, transaction, grant) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM session_event_content WHERE event_id=$event_id AND retention_owner_token=$retention_owner_token;";
            command.Parameters.AddWithValue("$event_id", grant.OwnershipKey.SourceItemId);
            grant.BindSourceToken(command);
            var deleted = command.ExecuteNonQuery() == 1;
            return ValueTask.FromResult(deleted ? 1 : -1);
        }, (connection, transaction, grant, completedAt) =>
            participant.CompleteSessionEventContentDeletion(
                connection, transaction, grant.OwnershipKey.SourceItemId, completedAt));
    }
}

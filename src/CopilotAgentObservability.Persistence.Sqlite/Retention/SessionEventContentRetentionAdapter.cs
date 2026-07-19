namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class SessionEventContentRetentionAdapter : IRetentionDeletionAdapter
{
    private readonly RetentionCatalogStore catalog;

    internal SessionEventContentRetentionAdapter(RetentionCatalogStore catalog) =>
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public RetentionStoreKind StoreKind => RetentionStoreKind.SessionEventContent;

    public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) =>
        catalog.ExecuteSqliteDeletionAsync(context, (connection, transaction, grant) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM session_event_content WHERE event_id=$event_id AND retention_owner_token=$retention_owner_token;";
            command.Parameters.AddWithValue("$event_id", grant.OwnershipKey.SourceItemId);
            grant.BindSourceToken(command);
            return ValueTask.FromResult(command.ExecuteNonQuery() == 1 ? 1 : -1);
        });
}

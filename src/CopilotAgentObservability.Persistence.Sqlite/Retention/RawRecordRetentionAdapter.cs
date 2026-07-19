using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RawRecordRetentionAdapter : IRetentionDeletionAdapter
{
    private readonly RetentionCatalogStore catalog;

    internal RawRecordRetentionAdapter(RetentionCatalogStore catalog) =>
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public RetentionStoreKind StoreKind => RetentionStoreKind.RawRecord;

    public ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context) =>
        catalog.ExecuteSqliteDeletionAsync(
            context,
            (connection, transaction, grant) => DeleteRawRecordAsync(connection, transaction, grant));

    private static ValueTask<int> DeleteRawRecordAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionSqliteDeletionGrant grant)
    {
        if (!long.TryParse(grant.OwnershipKey.SourceItemId, CultureInfo.InvariantCulture, out var rawRecordId)
            || rawRecordId <= 0)
            throw new ArgumentException("Raw record identity is invalid.");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM raw_records WHERE id=$id AND retention_owner_token=$retention_owner_token;";
        command.Parameters.AddWithValue("$id", rawRecordId);
        grant.BindSourceToken(command);
        return ValueTask.FromResult(command.ExecuteNonQuery() == 1 ? 1 : -1);
    }
}

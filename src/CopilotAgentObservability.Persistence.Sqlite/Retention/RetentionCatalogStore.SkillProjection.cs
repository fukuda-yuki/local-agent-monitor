namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    internal static bool ValidateSourceCompatibilityOperationLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        long rawRecordId,
        DateTimeOffset at) =>
        IsGrantUsable(connection, transaction, grant, rawRecordId, at);

    internal static bool ValidateSkillProjectionOperationLeases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RetentionReadGrant> grants,
        IReadOnlyList<long> rawRecordIds,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(rawRecordIds);
        if (grants.Count != rawRecordIds.Count)
            return false;
        for (var index = 0; index < grants.Count; index++)
        {
            if (!ValidateSourceCompatibilityOperationLease(
                    connection,
                    transaction,
                    grants[index],
                    rawRecordIds[index],
                    at))
                return false;
        }
        return true;
    }
}

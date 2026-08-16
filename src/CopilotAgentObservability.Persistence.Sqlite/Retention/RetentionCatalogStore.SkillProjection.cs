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

    internal static bool ValidateSourceCompatibilityOperationLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        long rawRecordId,
        RetentionReadGrant.LeasePublication publication,
        DateTimeOffset at) =>
        GrantMatchesRawRecord(grant, rawRecordId)
        && IsGrantUsable(connection, transaction, grant, publication, at);

    internal static bool SkillProjectionOperationLeaseFrontierMatches(
        IReadOnlyList<RetentionReadGrant> grants,
        IReadOnlyList<long> rawRecordIds)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(rawRecordIds);
        if (grants.Count != rawRecordIds.Count)
            return false;
        for (var index = 0; index < grants.Count; index++)
            if (!GrantMatchesRawRecord(grants[index], rawRecordIds[index]))
                return false;
        return true;
    }

    internal static bool ValidateSkillProjectionOperationLeases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RetentionReadGrant> grants,
        IReadOnlyList<long> rawRecordIds,
        RetentionGrantPublicationSet publications,
        DateTimeOffset at,
        Action? grantProved = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(rawRecordIds);
        ArgumentNullException.ThrowIfNull(publications);
        if (grants.Count != rawRecordIds.Count || grants.Count != publications.Count)
            return false;
        for (var index = 0; index < grants.Count; index++)
        {
            if (!ValidateSourceCompatibilityOperationLease(
                    connection,
                    transaction,
                    grants[index],
                    rawRecordIds[index],
                    publications.ScopeFor(index, grants[index]),
                    at))
                return false;
            grantProved?.Invoke();
        }
        return true;
    }

    internal static bool ValidateMonitorProjectionOperationLeases(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RetentionReadGrant> grants,
        RetentionGrantPublicationSet publications,
        long projectedRawRecordId,
        DateTimeOffset at,
        Action? grantProved = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(publications);
        if (grants.Count == 0 || grants.Count != publications.Count)
            return false;
        var targetIncluded = false;
        for (var index = 0; index < grants.Count; index++)
        {
            var grant = grants[index];
            if (!long.TryParse(grant.OwnershipKey.SourceItemId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rawRecordId)
                || !ValidateSourceCompatibilityOperationLease(
                    connection,
                    transaction,
                    grant,
                    rawRecordId,
                    publications.ScopeFor(index, grant),
                    at))
                return false;
            targetIncluded |= rawRecordId == projectedRawRecordId;
            grantProved?.Invoke();
        }
        return targetIncluded;
    }
}

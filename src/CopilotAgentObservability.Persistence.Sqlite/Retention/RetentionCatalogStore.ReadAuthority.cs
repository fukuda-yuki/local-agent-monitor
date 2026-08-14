using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionRowReadability
{
    Readable,
    AlreadyDenied,
    LifecycleDenied,
    ExpiredExpiring,
}

public sealed partial class RetentionCatalogStore
{
    internal static RetentionRowReadability ClassifyRowReadability(RetentionCatalogItem item, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ClassifyRowReadability(item.State, item.ExpiresAt, item.ReadDeniedAt is not null, at);
    }

    internal static RetentionRowReadability ClassifyRowReadability(
        RetentionItemLifecycle state,
        DateTimeOffset expiresAt,
        bool readDenied,
        DateTimeOffset at)
    {
        if (readDenied) return RetentionRowReadability.AlreadyDenied;
        if (state == RetentionItemLifecycle.RetainedByPolicy) return RetentionRowReadability.Readable;
        if (state == RetentionItemLifecycle.Expiring)
            return at < expiresAt ? RetentionRowReadability.Readable : RetentionRowReadability.ExpiredExpiring;
        return RetentionRowReadability.LifecycleDenied;
    }

    internal static string ProjectSessionRawRetentionState(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string storeInstanceId,
        string sessionId,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(storeInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM retention_items AS i
                    JOIN session_events AS e ON e.event_id=i.source_item_id
                    WHERE e.session_id=$session_id
                      AND i.store_instance_id=$store_instance_id
                      AND i.store_kind='session_event_content'
                      AND i.read_denied_at IS NULL
                      AND (i.state='retained_by_policy' OR (i.state='expiring' AND i.expires_at>$now))
                ),
                EXISTS (
                    SELECT 1
                    FROM retention_items AS i
                    JOIN session_events AS e ON e.event_id=i.source_item_id
                    WHERE e.session_id=$session_id
                      AND i.store_instance_id=$store_instance_id
                      AND i.store_kind='session_event_content'
                );
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$store_instance_id", storeInstanceId);
        command.Parameters.AddWithValue("$now", Timestamp(at));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("retention_session_projection_unavailable");
        var readable = reader.GetInt64(0) != 0;
        var represented = reader.GetInt64(1) != 0;
        return RetentionSessionV1Projection.ProjectCondition(
            readable
                ? RetentionSessionV1Condition.ReadableExpiring
                : represented
                    ? RetentionSessionV1Condition.CapturedWithoutReadableSibling
                    : RetentionSessionV1Condition.NeverCaptured);
    }

    internal static bool IsGrantUsable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grant);
        var key = grant.OwnershipKey;
        if (grant.LeaseKind is not (RetentionLeaseKind.Access or RetentionLeaseKind.Operation)) return false;
        if (!string.Equals(key.StoreInstanceId, StoreId(connection, transaction), StringComparison.Ordinal)) return false;

        string sourceSql;
        object sourceId;
        switch (key.StoreKind)
        {
            case RetentionStoreKind.SessionEventContent when !string.IsNullOrWhiteSpace(key.SourceItemId):
                sourceSql =
                    "SELECT 1 FROM session_event_content WHERE event_id=$source_id AND retention_owner_token=$retention_read_source_token";
                sourceId = key.SourceItemId;
                break;
            case RetentionStoreKind.RawRecord when TryExactNumericSourceId(key.SourceItemId, out var rawRecordId):
                sourceSql =
                    "SELECT 1 FROM raw_records WHERE id=$source_id AND retention_owner_token=$retention_read_source_token";
                sourceId = rawRecordId;
                break;
            case RetentionStoreKind.AnalysisRunRaw when TryExactNumericSourceId(key.SourceItemId, out var analysisRunId):
                sourceSql =
                    "SELECT 1 FROM monitor_analysis_runs WHERE id=$source_id AND retention_owner_token=$retention_read_source_token";
                sourceId = analysisRunId;
                break;
            case RetentionStoreKind.SensitiveBundle when CanonicalId(key.SourceItemId):
                sourceSql =
                    "SELECT 1 FROM retention_file_capture_reservations WHERE capture_id=$source_id AND store_instance_id=$store_instance_id AND store_kind='sensitive_bundle' AND source_item_id=$source_id AND phase='complete' AND owner_token=$retention_read_source_token";
                sourceId = key.SourceItemId;
                break;
            case RetentionStoreKind.AnalysisSdkDirectory when CanonicalId(key.SourceItemId):
                sourceSql =
                    "SELECT 1 FROM retention_analysis_sdk_directory_reservations WHERE capture_id=$source_id AND store_instance_id=$store_instance_id AND phase='active' AND owner_token=$retention_read_source_token";
                sourceId = key.SourceItemId;
                break;
            default:
                return false;
        }

        using var publication = grant.EnterLeasePublication();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            $$"""
            SELECT EXISTS(
                SELECT 1
                FROM retention_leases AS lease
                WHERE lease.item_id=$retention_read_item_id
                  AND lease.lease_kind=$retention_read_lease_kind
                  AND lease.owner=$retention_read_lease_owner
                  AND lease.generation=$retention_read_lease_generation
                  AND lease.expires_at=$retention_read_lease_expires_at
                  AND lease.expires_at>$at
            )
            AND EXISTS(
                {{sourceSql}}
            );
            """;
        command.Parameters.AddWithValue("$source_id", sourceId);
        command.Parameters.AddWithValue("$store_instance_id", key.StoreInstanceId);
        command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        publication.BindPostCommitGrantUsabilityCapability(command);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    internal static bool IsGrantUsable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        long rawRecordId,
        DateTimeOffset at) =>
        GrantMatchesRawRecord(grant, rawRecordId)
        && IsGrantUsable(connection, transaction, grant, at);

    internal static RetentionOperationRenewalDisposition TryPrepareOperationLeaseRenewal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        RetentionGrantPublicationSet publications,
        int publicationIndex,
        DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(publications);
        if (publicationIndex < 0
            || publicationIndex >= publications.Count
            || !publications.IsForGrant(publicationIndex, grant))
            return RetentionOperationRenewalDisposition.LeaseLost;
        if (grant.LeaseKind != RetentionLeaseKind.Operation)
            return RetentionOperationRenewalDisposition.LeaseLost;
        if (!IsGrantUsable(connection, transaction, grant, at))
            return RetentionOperationRenewalDisposition.LeaseLost;
        var publishedExpiry = publications.LeaseExpiresAt(publicationIndex);
        if (publishedExpiry - at > RetentionV1Constants.LeaseRenewalDeadline)
            return RetentionOperationRenewalDisposition.NotDue;

        var item = FindForUpdate(connection, transaction, grant.OwnershipKey);
        if (item is null
            || !string.Equals(item.ItemId, grant.ItemId, StringComparison.Ordinal)
            || item.Revision != grant.AdmissionRevision)
            return RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable;
        if (!CoverageMatchesExactly(connection, transaction, RetentionV1Constants.AdapterCoverageVersion)
            || ClassifyRowReadability(item, at) != RetentionRowReadability.Readable)
            return RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable;

        var proof = SourceProof(connection, transaction, grant.OwnershipKey);
        if (proof == SourceReceiptProof.CatalogBusy)
            return RetentionOperationRenewalDisposition.CatalogBusy;
        if (proof != SourceReceiptProof.Match)
            return RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE retention_leases
            SET expires_at=$expiry
            WHERE item_id=$item_id
              AND lease_kind='operation'
              AND owner=$owner
              AND generation=$generation
              AND expires_at=$previous_expiry
              AND expires_at>$at;
            """;
        command.Parameters.AddWithValue("$expiry", Timestamp(at.Add(RetentionV1Constants.LeaseDuration)));
        command.Parameters.AddWithValue("$item_id", grant.ItemId);
        command.Parameters.AddWithValue("$owner", grant.LeaseOwner);
        command.Parameters.AddWithValue("$generation", grant.LeaseGeneration);
        command.Parameters.AddWithValue("$previous_expiry", Timestamp(publishedExpiry));
        command.Parameters.AddWithValue("$at", Timestamp(at));
        if (command.ExecuteNonQuery() == 1)
            return RetentionOperationRenewalDisposition.Renewed;
        return IsGrantUsable(connection, transaction, grant, at)
            ? RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable
            : RetentionOperationRenewalDisposition.LeaseLost;
    }

    internal RetentionOperationRenewalDisposition RenewOperationLease(
        RetentionReadGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        try
        {
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            analysisSdkDirectoryCheckpoint?.Invoke("renewal_transaction_began");
            using var publications = RetentionGrantPublicationSet.EnterInOrder(
                [new RetentionGrantPublicationMember(grant, 0)]);
            var transactionAt = timeProvider.GetUtcNow();
            var disposition = TryPrepareOperationLeaseRenewal(
                connection,
                transaction,
                grant,
                publications,
                0,
                transactionAt);
            if (disposition == RetentionOperationRenewalDisposition.Renewed)
            {
                transaction.Commit();
                publications.AdvanceExpiry(0, transactionAt.Add(RetentionV1Constants.LeaseDuration));
            }
            else
            {
                transaction.Rollback();
            }
            return disposition;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return RetentionOperationRenewalDisposition.CatalogBusy;
        }
        catch (SqliteException)
        {
            return RetentionOperationRenewalDisposition.LeaseLost;
        }
    }

    internal static bool TryPrepareOperationLeaseRenewals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RetentionReadGrant> grants,
        IReadOnlyList<long> rawRecordIds,
        RetentionGrantPublicationSet publications,
        DateTimeOffset at,
        out IReadOnlyList<int> renewedGrantIndices)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(rawRecordIds);
        ArgumentNullException.ThrowIfNull(publications);
        renewedGrantIndices = Array.Empty<int>();
        if (grants.Count != rawRecordIds.Count || grants.Count != publications.Count)
            return false;

        var dueIndices = new List<int>(grants.Count);
        for (var index = 0; index < grants.Count; index++)
        {
            var grant = grants[index];
            if (!GrantMatchesRawRecord(grant, rawRecordIds[index]))
                return false;
            var disposition = TryPrepareOperationLeaseRenewal(
                connection,
                transaction,
                grant,
                publications,
                index,
                at);
            if (disposition == RetentionOperationRenewalDisposition.Renewed)
                dueIndices.Add(index);
            else if (disposition != RetentionOperationRenewalDisposition.NotDue)
                return false;
        }

        renewedGrantIndices = dueIndices;
        return true;
    }

    private static bool GrantMatchesRawRecord(RetentionReadGrant grant, long rawRecordId) =>
        grant.LeaseKind == RetentionLeaseKind.Operation
        && grant.OwnershipKey.StoreKind == RetentionStoreKind.RawRecord
        && TryExactNumericSourceId(grant.OwnershipKey.SourceItemId, out var admittedRawRecordId)
        && admittedRawRecordId == rawRecordId;

    private static bool TryExactNumericSourceId(string value, out long sourceId) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out sourceId)
        && sourceId > 0
        && string.Equals(value, sourceId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}

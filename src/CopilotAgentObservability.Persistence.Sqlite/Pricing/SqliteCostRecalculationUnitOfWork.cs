using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal sealed class SqliteCostRecalculationUnitOfWork
{
    private readonly SqlitePricingStore store;

    internal SqliteCostRecalculationUnitOfWork(
        string databasePath,
        TimeProvider? timeProvider = null)
    {
        store = new SqlitePricingStore(databasePath, timeProvider);
    }

    internal PricingStoreResult<string> Start(
        string runId,
        CostRecalculationRequestV1 request,
        ReadOnlyMemory<byte> currentProviderCatalogBytes,
        DateTimeOffset calculationTimeUtc) =>
        store.StartRecalculationWithDynamicState(
            runId,
            request,
            currentProviderCatalogBytes,
            calculationTimeUtc);

}

public sealed partial class SqlitePricingStore
{
    internal PricingStoreResult<string> StartRecalculationWithDynamicState(
        string runId,
        CostRecalculationRequestV1 request,
        ReadOnlyMemory<byte> currentProviderCatalogBytes,
        DateTimeOffset calculationTimeUtc)
    {
        if (!IsCanonicalUuidV7(runId) || calculationTimeUtc.Offset != TimeSpan.Zero)
            return new(PricingStoreStatus.ContractRejected, null);

        byte[] requestBytes;
        byte[] providerBytes;
        PricingCatalog providerCatalog;
        try
        {
            requestBytes = CostRecalculationRequestCanonicalJsonV1.Serialize(request);
            var consumed = CostRecalculationRequestCanonicalJsonV1.Consume(requestBytes);
            if (consumed.Status != CostConsumerStatus.Success)
                return new(PricingStoreStatus.ContractRejected, null);
            providerBytes = currentProviderCatalogBytes.ToArray();
            providerCatalog = PricingCatalogSnapshotConsumer.Deserialize(providerBytes);
        }
        catch (Exception exception) when (
            exception is ArgumentException or PricingRegistryValidationException)
        {
            return new(PricingStoreStatus.ContractRejected, null);
        }

        try
        {
            using var connection = Open(SqliteOpenMode.ReadWrite);
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!PricingSchemaV1.ValidateRows(connection, transaction))
                return Rollback<string>(transaction, PricingStoreStatus.Unavailable);

            var digest = CostIdentityV1.Hash("cost-recalculation-request/v1", requestBytes);
            using (var replay = Command(
                connection,
                transaction,
                """
                SELECT run_id,request_digest,canonical_request_blob
                FROM pricing_recalculation_runs WHERE idempotency_key=$key;
                """,
                ("$key", request.IdempotencyKey)))
            using (var reader = replay.ExecuteReader())
            {
                if (reader.Read())
                {
                    var replayRunId = reader.GetString(0);
                    var equivalent = reader.GetString(1) == digest
                        && ((byte[])reader[2]).AsSpan().SequenceEqual(requestBytes);
                    transaction.Rollback();
                    return equivalent
                        ? new(PricingStoreStatus.Success, replayRunId)
                        : new(PricingStoreStatus.Conflict, null);
                }
            }

            using (var head = Command(
                connection,
                transaction,
                """
                SELECT h.head_revision,h.configuration_id,c.catalog_sha256,
                       s.canonical_blob
                FROM pricing_configuration_heads h
                JOIN pricing_configurations c ON c.configuration_id=h.configuration_id
                JOIN pricing_catalog_snapshots s ON s.catalog_sha256=c.catalog_sha256
                ORDER BY h.head_revision DESC LIMIT 1;
                """))
            using (var reader = head.ExecuteReader())
            {
                if (!reader.Read()
                    || reader.GetInt64(0) != request.ExpectedHeadRevision
                    || reader.GetString(1) != request.ConfigurationId)
                    return Rollback<string>(transaction, PricingStoreStatus.Conflict);
                if (reader.GetString(2) != request.CatalogSha256
                    || providerCatalog.CatalogSha256 != request.CatalogSha256
                    || !((byte[])reader[3]).AsSpan().SequenceEqual(providerBytes))
                    return Rollback<string>(transaction, PricingStoreStatus.Conflict);
            }

            using (var overlap = Command(
                connection,
                transaction,
                """
                SELECT COUNT(*) FROM pricing_recalculation_targets t
                WHERE t.session_id IN (SELECT value FROM json_each($sessions))
                  AND NOT EXISTS(
                    SELECT 1 FROM pricing_recalculation_events e
                    WHERE e.run_id=t.run_id AND e.event_kind IN ('succeeded','failed'));
                """,
                ("$sessions", JsonSerializer.Serialize(request.SessionIds))))
            {
                if (Convert.ToInt64(overlap.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                    return Rollback<string>(transaction, PricingStoreStatus.Conflict);
            }

            var targets = new List<PricingRecalculationTargetCapture>(request.SessionIds.Count);
            foreach (var sessionId in request.SessionIds)
            {
                string status;
                string effective;
                string updated;
                using (var session = Command(
                    connection,
                    transaction,
                    """
                    SELECT status,last_seen_at,updated_at
                    FROM sessions WHERE session_id=$session;
                    """,
                    ("$session", sessionId)))
                using (var reader = session.ExecuteReader())
                {
                    if (!reader.Read() || reader.GetString(0) is not ("completed" or "failed"))
                        return Rollback<string>(transaction, PricingStoreStatus.Conflict);
                    status = reader.GetString(0);
                    effective = reader.GetString(1);
                    updated = reader.GetString(2);
                }

                var resolved = SqliteCostSessionSourcePartitionResolverV1.Resolve(
                    connection,
                    transaction,
                    sessionId);
                long? baseHead = null;
                string? baseEstimate = null;
                using (var head = Command(
                    connection,
                    transaction,
                    """
                    SELECT head_revision,estimate_id FROM pricing_estimate_heads
                    WHERE session_id=$session ORDER BY head_revision DESC LIMIT 1;
                    """,
                    ("$session", sessionId)))
                using (var reader = head.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        baseHead = reader.GetInt64(0);
                        baseEstimate = reader.GetString(1);
                    }
                }
                long baseAttempt;
                using (var attempt = Command(
                    connection,
                    transaction,
                    """
                    SELECT COALESCE(MAX(attempt_revision),0)
                    FROM pricing_session_attempts WHERE session_id=$session;
                    """,
                    ("$session", sessionId)))
                {
                    baseAttempt = Convert.ToInt64(
                        attempt.ExecuteScalar(),
                        CultureInfo.InvariantCulture);
                }

                targets.Add(new(
                    sessionId,
                    status,
                    ParseTimestamp(effective),
                    ParseTimestamp(updated),
                    Wire(resolved.State),
                    resolved.ObservationCount,
                    resolved.Digest,
                    resolved.SourceSurface,
                    resolved.SourceApplicationVersion,
                    baseHead,
                    baseEstimate,
                    baseAttempt));
            }

            foreach (var scope in request.BudgetScopes.Where(item => item.ScopeKind == "session"))
            {
                var target = targets.Single(item => item.SessionId == scope.SessionId);
                if (target.SourcePartitionState != "resolved")
                    return Rollback<string>(transaction, PricingStoreStatus.Conflict);
            }

            using (var root = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_recalculation_runs(
                    run_id,request_schema_version,idempotency_key,request_digest,
                    canonical_request_blob,configuration_id,configuration_head_revision,
                    catalog_sha256,calculation_time_utc,target_count,scope_count,created_at_utc)
                VALUES(
                    $run,'cost.recalculation-request.v1',$key,$digest,$request,
                    $configuration,$head,$catalog,$time,$target_count,$scope_count,$time);
                """,
                ("$run", runId),
                ("$key", request.IdempotencyKey),
                ("$digest", digest),
                ("$request", requestBytes),
                ("$configuration", request.ConfigurationId),
                ("$head", request.ExpectedHeadRevision),
                ("$catalog", request.CatalogSha256),
                ("$time", Format(calculationTimeUtc)),
                ("$target_count", targets.Count),
                ("$scope_count", request.BudgetScopes.Count)))
            {
                root.ExecuteNonQuery();
            }

            for (var ordinal = 0; ordinal < targets.Count; ordinal++)
            {
                var target = targets[ordinal];
                using var insert = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO pricing_recalculation_targets(
                        run_id,target_ordinal,session_id,session_status,session_effective_at_utc,
                        session_updated_at_utc,source_partition_state,source_partition_count,
                        source_partition_digest,source_surface,source_application_version,
                        base_head_revision,base_estimate_id,base_attempt_revision)
                    VALUES(
                        $run,$ordinal,$session,$status,$effective,$updated,$source_state,
                        $source_count,$source_digest,$surface,$version,$head,$estimate,$attempt);
                    """,
                    ("$run", runId),
                    ("$ordinal", ordinal),
                    ("$session", target.SessionId),
                    ("$status", target.SessionStatus),
                    ("$effective", Format(target.SessionEffectiveAtUtc)),
                    ("$updated", Format(target.SessionUpdatedAtUtc)),
                    ("$source_state", target.SourcePartitionState),
                    ("$source_count", target.SourcePartitionCount),
                    ("$source_digest", target.SourcePartitionDigest),
                    ("$surface", (object?)target.SourceSurface ?? DBNull.Value),
                    ("$version", (object?)target.SourceApplicationVersion ?? DBNull.Value),
                    ("$head", (object?)target.BaseHeadRevision ?? DBNull.Value),
                    ("$estimate", (object?)target.BaseEstimateId ?? DBNull.Value),
                    ("$attempt", target.BaseAttemptRevision));
                insert.ExecuteNonQuery();
            }

            using (var requested = Command(
                connection,
                transaction,
                """
                INSERT INTO pricing_recalculation_events(
                    run_id,event_sequence,event_kind,occurred_at_utc)
                VALUES($run,0,'requested',$time);
                """,
                ("$run", runId),
                ("$time", Format(calculationTimeUtc))))
            {
                requested.ExecuteNonQuery();
            }
            transaction.Commit();
            return new(PricingStoreStatus.Success, runId);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(PricingStoreStatus.Busy, null);
        }
        catch (SqliteException)
        {
            return new(PricingStoreStatus.Unavailable, null);
        }
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.ParseExact(
            value,
            "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None);

    private static string Wire(CostSessionSourcePartitionStateV1 state) =>
        state switch
        {
            CostSessionSourcePartitionStateV1.Missing => "missing",
            CostSessionSourcePartitionStateV1.Incomplete => "incomplete",
            CostSessionSourcePartitionStateV1.Mixed => "mixed",
            CostSessionSourcePartitionStateV1.Resolved => "resolved",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CopilotAgentObservability.Alerts;
using CopilotAgentObservability.Persistence.Sqlite.Costs;
using CopilotAgentObservability.Pricing;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal enum PricingCompletionStatus
{
    Success,
    StaleRecalculationInput,
    StaleActiveEstimate,
    AlertEvaluationFailed,
    AlertStoreFailed,
    PricingStoreFailed,
    FailureLedgerUnavailable,
    ContractRejected,
}

internal sealed record PricingCompletionResult(
    PricingCompletionStatus Status,
    string? FailureCode = null);

internal enum PricingCompletionSnapshotStatus
{
    Current,
    AlreadyTerminal,
    StaleRecalculationInput,
    StaleActiveEstimate,
    Unavailable,
}

internal sealed record PricingCompletionSnapshotResult(
    PricingCompletionSnapshotStatus Status,
    int TargetOrdinal);

internal sealed record CompletionTargetSnapshot(
    int TargetOrdinal,
    string SessionId,
    string SessionStatus,
    string SessionEffectiveAtUtc,
    string SessionUpdatedAtUtc,
    string SourcePartitionState,
    int SourcePartitionCount,
    string SourcePartitionDigest,
    string? SourceSurface,
    string? SourceApplicationVersion,
    long? BaseHeadRevision,
    string? BaseEstimateId,
    long BaseAttemptRevision);

internal sealed class SqliteCostRecalculationUnitOfWork
{
    private readonly string databasePath;
    private readonly SqlitePricingStore store;
    private readonly ISqliteAlertEngineTransactionParticipantV2? alertParticipant;

    internal SqliteCostRecalculationUnitOfWork(
        string databasePath,
        TimeProvider? timeProvider = null)
    {
        this.databasePath = databasePath;
        store = new SqlitePricingStore(databasePath, timeProvider);
    }

    internal SqliteCostRecalculationUnitOfWork(
        string databasePath,
        ISqliteAlertEngineTransactionParticipantV2 alertParticipant,
        TimeProvider? timeProvider = null)
        : this(databasePath, timeProvider)
    {
        this.alertParticipant = alertParticipant
            ?? throw new ArgumentNullException(nameof(alertParticipant));
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

    internal PricingCompletionResult Complete(
        string runId,
        IReadOnlyList<PricingTargetCompletionWrite> targetResults,
        IReadOnlyList<AlertEvaluationResultV2> alertEvaluations,
        IReadOnlyList<PricingBudgetResultWrite> budgetResults)
    {
        ArgumentNullException.ThrowIfNull(targetResults);
        ArgumentNullException.ThrowIfNull(alertEvaluations);
        ArgumentNullException.ThrowIfNull(budgetResults);
        var frozenTargetResults = targetResults.ToArray();
        var frozenBudgetResults = budgetResults.ToArray();
        var frozenAlertEvaluations = alertEvaluations.ToArray();
        PricingRunFailureWrite? preflightFailure = null;
        if (frozenAlertEvaluations.Length != frozenBudgetResults.Length)
        {
            if (frozenBudgetResults.Length == 0)
            {
                preflightFailure = new(
                    "pricing_store",
                    "target",
                    0,
                    "pricing_store_failed");
            }
            else
            {
                var failureOrdinal = Math.Min(
                    Math.Min(frozenAlertEvaluations.Length, frozenBudgetResults.Length),
                    frozenBudgetResults.Length - 1);
                preflightFailure = new(
                    "alert_evaluation",
                    "scope",
                    failureOrdinal,
                    "alert_evaluation_failed");
            }
        }
        else if (alertParticipant is null && frozenAlertEvaluations.Length > 0)
        {
            preflightFailure = new(
                "alert_store",
                "scope",
                0,
                "alert_store_failed");
        }
        var invalidEvaluationOrdinal = 0;
        if (preflightFailure is null)
        {
            try
            {
                for (var ordinal = 0; ordinal < frozenAlertEvaluations.Length; ordinal++)
                {
                    invalidEvaluationOrdinal = ordinal;
                    AlertEvaluationConsumerV2.Validate(
                        AlertCanonicalJsonV2.SerializeEvaluation(
                            frozenAlertEvaluations[ordinal]));
                }
            }
            catch (Exception)
            {
                preflightFailure = new(
                    "alert_evaluation",
                    "scope",
                    invalidEvaluationOrdinal,
                    "alert_evaluation_failed");
            }
        }

        PricingRunFailureWrite? selectedFailure = null;
        var preserveUnavailable = false;
        PricingStoreResult core;
        try
        {
            using var connection = OpenCompletionConnection();
            using var transaction = connection.BeginTransaction(deferred: false);
            var snapshot = store.ValidateCompletionSnapshot(
                connection,
                transaction,
                runId);
            if (snapshot.Status == PricingCompletionSnapshotStatus.AlreadyTerminal)
            {
                transaction.Rollback();
                return new(PricingCompletionStatus.ContractRejected);
            }
            if (snapshot.Status is PricingCompletionSnapshotStatus.StaleRecalculationInput
                or PricingCompletionSnapshotStatus.StaleActiveEstimate)
            {
                selectedFailure = new(
                    "head_input",
                    "target",
                    snapshot.TargetOrdinal,
                    snapshot.Status == PricingCompletionSnapshotStatus.StaleActiveEstimate
                        ? "stale_active_estimate"
                        : "stale_recalculation_input");
                core = new(PricingStoreStatus.Conflict);
            }
            else if (snapshot.Status != PricingCompletionSnapshotStatus.Current)
            {
                core = new(PricingStoreStatus.Unavailable);
            }
            else if (preflightFailure is not null)
            {
                selectedFailure = preflightFailure;
                preserveUnavailable = true;
                core = new(PricingStoreStatus.ContractRejected);
            }
            else
            {
                core = store.AppendRecalculationCompletionCore(
                    runId,
                    frozenTargetResults,
                    frozenBudgetResults,
                    failure: null,
                    connection,
                    transaction,
                    (sharedConnection, sharedTransaction) =>
                    {
                        for (var ordinal = 0; ordinal < frozenAlertEvaluations.Length; ordinal++)
                        {
                            try
                            {
                                var append = alertParticipant!.AppendEvaluation(
                                    sharedConnection,
                                    sharedTransaction,
                                    frozenAlertEvaluations[ordinal]);
                                if (append.Status != AlertEngineTransactionAppendStatusV2.Success)
                                {
                                    selectedFailure = new(
                                        "alert_store",
                                        "scope",
                                        ordinal,
                                        "alert_store_failed");
                                    preserveUnavailable = true;
                                    return PricingStoreStatus.Unavailable;
                                }
                            }
                            catch (Exception)
                            {
                                selectedFailure = new(
                                    "alert_store",
                                    "scope",
                                    ordinal,
                                    "alert_store_failed");
                                preserveUnavailable = true;
                                return PricingStoreStatus.Unavailable;
                            }
                        }
                        return PricingStoreStatus.Success;
                    });
            }
            if (core.Status == PricingStoreStatus.Success)
            {
                transaction.Commit();
                return new(PricingCompletionStatus.Success);
            }
            transaction.Rollback();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            core = new(PricingStoreStatus.Busy);
        }
        catch (SqliteException)
        {
            core = new(PricingStoreStatus.Unavailable);
        }
        catch (Exception)
        {
            core = new(PricingStoreStatus.Unavailable);
        }

        if (selectedFailure is null)
        {
            selectedFailure = new(
                "pricing_store",
                "target",
                0,
                "pricing_store_failed");
            preserveUnavailable = true;
        }

        return CloseFailure(
            runId,
            frozenTargetResults,
            selectedFailure,
            preserveUnavailable);
    }

    private PricingCompletionResult CloseFailure(
        string runId,
        IReadOnlyList<PricingTargetCompletionWrite> targetResults,
        PricingRunFailureWrite selectedFailure,
        bool preserveUnavailable)
    {
        var closed = targetResults.Select(item =>
            preserveUnavailable
                && item.ResultKind == "unavailable"
                && !(selectedFailure.FailureOrdinalKind == "target"
                    && selectedFailure.FailureOrdinal == item.TargetOrdinal)
                ? PricingTargetCompletionWrite.Unavailable(
                    item.TargetOrdinal,
                    item.ResultCode!)
                : PricingTargetCompletionWrite.Failed(
                    item.TargetOrdinal,
                    selectedFailure.FailureCode)).ToArray();
        var failure = store.AppendRecalculationCompletionApplication(
            runId,
            closed,
            [],
            selectedFailure);
        if (failure.Status != PricingStoreStatus.Success)
            return new(PricingCompletionStatus.FailureLedgerUnavailable, "pricing_store_failed");
        return selectedFailure.FailureCode switch
        {
            "stale_recalculation_input" => new(
                PricingCompletionStatus.StaleRecalculationInput,
                selectedFailure.FailureCode),
            "stale_active_estimate" => new(
                PricingCompletionStatus.StaleActiveEstimate,
                selectedFailure.FailureCode),
            "alert_evaluation_failed" => new(
                PricingCompletionStatus.AlertEvaluationFailed,
                selectedFailure.FailureCode),
            "alert_store_failed" => new(
                PricingCompletionStatus.AlertStoreFailed,
                selectedFailure.FailureCode),
            _ => new(
                PricingCompletionStatus.PricingStoreFailed,
                selectedFailure.FailureCode),
        };
    }

    private SqliteConnection OpenCompletionConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            ForeignKeys = true,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys;";
            if (Convert.ToInt64(
                    command.ExecuteScalar(),
                    CultureInfo.InvariantCulture) != 1)
                throw new InvalidOperationException("SQLite foreign keys are not enabled.");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

}

public sealed partial class SqlitePricingStore
{
    internal PricingCompletionSnapshotResult ValidateCompletionSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string runId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Connection != connection)
            return new(PricingCompletionSnapshotStatus.Unavailable, 0);

        var eventKinds = new List<string>();
        using (var events = Command(
            connection,
            transaction,
            """
            SELECT event_kind FROM pricing_recalculation_events
            WHERE run_id=$run ORDER BY event_sequence;
            """,
            ("$run", runId)))
        using (var reader = events.ExecuteReader())
        {
            while (reader.Read()) eventKinds.Add(reader.GetString(0));
        }
        if (eventKinds.Count > 0
            && eventKinds[^1] is "succeeded" or "failed")
            return new(PricingCompletionSnapshotStatus.AlreadyTerminal, 0);
        if (!eventKinds.SequenceEqual(["requested", "running"], StringComparer.Ordinal))
            return new(PricingCompletionSnapshotStatus.Unavailable, 0);

        string configurationId;
        long configurationHeadRevision;
        string catalogSha256;
        using (var root = Command(
            connection,
            transaction,
            """
            SELECT configuration_id,configuration_head_revision,catalog_sha256
            FROM pricing_recalculation_runs WHERE run_id=$run;
            """,
            ("$run", runId)))
        using (var reader = root.ExecuteReader())
        {
            if (!reader.Read())
                return new(PricingCompletionSnapshotStatus.Unavailable, 0);
            configurationId = reader.GetString(0);
            configurationHeadRevision = reader.GetInt64(1);
            catalogSha256 = reader.GetString(2);
        }

        using (var head = Command(
            connection,
            transaction,
            """
            SELECT h.head_revision,h.configuration_id,c.catalog_sha256
            FROM pricing_configuration_heads h
            JOIN pricing_configurations c ON c.configuration_id=h.configuration_id
            ORDER BY h.head_revision DESC LIMIT 1;
            """))
        using (var reader = head.ExecuteReader())
        {
            if (!reader.Read()
                || reader.GetInt64(0) != configurationHeadRevision
                || reader.GetString(1) != configurationId
                || reader.GetString(2) != catalogSha256)
                return new(PricingCompletionSnapshotStatus.StaleRecalculationInput, 0);
        }

        var targets = new List<CompletionTargetSnapshot>();
        using (var command = Command(
            connection,
            transaction,
            """
            SELECT target_ordinal,session_id,session_status,session_effective_at_utc,
                   session_updated_at_utc,source_partition_state,source_partition_count,
                   source_partition_digest,source_surface,source_application_version,
                   base_head_revision,base_estimate_id,base_attempt_revision
            FROM pricing_recalculation_targets
            WHERE run_id=$run ORDER BY target_ordinal;
            """,
            ("$run", runId)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                targets.Add(new(
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.GetInt64(12)));
            }
        }

        foreach (var target in targets)
        {
            using (var session = Command(
                connection,
                transaction,
                """
                SELECT status,last_seen_at,updated_at
                FROM sessions WHERE session_id=$session;
                """,
                ("$session", target.SessionId)))
            using (var reader = session.ExecuteReader())
            {
                if (!reader.Read()
                    || reader.GetString(0) != target.SessionStatus
                    || reader.GetString(1) != target.SessionEffectiveAtUtc
                    || reader.GetString(2) != target.SessionUpdatedAtUtc)
                    return new(
                        PricingCompletionSnapshotStatus.StaleRecalculationInput,
                        target.TargetOrdinal);
            }

            var source = SqliteCostSessionSourcePartitionResolverV1.Resolve(
                connection,
                transaction,
                target.SessionId);
            if (Wire(source.State) != target.SourcePartitionState
                || source.ObservationCount != target.SourcePartitionCount
                || source.Digest != target.SourcePartitionDigest
                || source.SourceSurface != target.SourceSurface
                || source.SourceApplicationVersion != target.SourceApplicationVersion)
                return new(
                    PricingCompletionSnapshotStatus.StaleRecalculationInput,
                    target.TargetOrdinal);

            long? currentHeadRevision = null;
            string? currentEstimateId = null;
            using (var head = Command(
                connection,
                transaction,
                """
                SELECT head_revision,estimate_id
                FROM pricing_estimate_heads
                WHERE session_id=$session
                ORDER BY head_revision DESC LIMIT 1;
                """,
                ("$session", target.SessionId)))
            using (var reader = head.ExecuteReader())
            {
                if (reader.Read())
                {
                    currentHeadRevision = reader.GetInt64(0);
                    currentEstimateId = reader.GetString(1);
                }
            }
            long currentAttemptRevision;
            using (var attempt = Command(
                connection,
                transaction,
                """
                SELECT COALESCE(MAX(attempt_revision),0)
                FROM pricing_session_attempts WHERE session_id=$session;
                """,
                ("$session", target.SessionId)))
            {
                currentAttemptRevision = Convert.ToInt64(
                    attempt.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
            if (currentHeadRevision != target.BaseHeadRevision
                || currentEstimateId != target.BaseEstimateId
                || currentAttemptRevision != target.BaseAttemptRevision)
                return new(
                    PricingCompletionSnapshotStatus.StaleActiveEstimate,
                    target.TargetOrdinal);
        }

        return new(PricingCompletionSnapshotStatus.Current, 0);
    }

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

using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum SkillProjectionWorkOutcome
{
    NoWork,
    Published,
    Retrying,
    Superseded,
    InputUnavailable,
    FailedTerminal,
    StaleOwner,
}

internal sealed record SkillProjectionQueuedInput(
    int Ordinal,
    long SourceObservationId,
    long RawRecordId,
    SkillProjectionInputEvidenceKind EvidenceKind,
    string? RawPayloadSha256,
    string? SourceSurface);

internal sealed record SkillProjectionQueueLease(
    long GenerationId,
    string TraceId,
    long CompatibilityRevision,
    string InputFrontierSha256,
    string ProjectorVersion,
    long AttemptCount,
    string LeaseOwner,
    long LeaseGeneration,
    DateTimeOffset LeaseExpiresAt,
    string ExactVersion,
    IReadOnlyList<SkillProjectionQueuedInput> Inputs);

internal sealed record SkillProjectionProjectedInput(
    long RawRecordId,
    RawTelemetryRecord RawRecord,
    MonitorSkillProjectionBatch Projection);

internal sealed class SqliteSkillProjectionStore
{
    private static readonly TimeSpan QueueLeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan QueueHeartbeatInterval = TimeSpan.FromSeconds(10);
    private readonly string databasePath;
    private readonly RawTelemetryStore rawStore;

    internal SqliteSkillProjectionStore(string databasePath, RawTelemetryStore rawStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.rawStore = rawStore ?? throw new ArgumentNullException(nameof(rawStore));
        if (!string.Equals(
                Path.GetFullPath(databasePath),
                Path.GetFullPath(rawStore.DatabasePath),
                StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The raw store belongs to a different database.", nameof(rawStore));
        this.databasePath = databasePath;
    }

    internal SkillProjectionQueueLease? ClaimNext(DateTimeOffset claimedAt)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var candidate = connection.CreateCommand();
        candidate.Transaction = transaction;
        candidate.CommandText =
            """
            SELECT
                queue.generation_id,queue.trace_id,queue.compatibility_revision,
                queue.input_frontier_sha256,queue.projector_version,
                queue.attempt_count,queue.lease_generation,
                revision.current_exact_version
            FROM skill_projection_queue AS queue
            JOIN skill_projection_generations AS generation
              ON generation.generation_id=queue.generation_id
            JOIN skill_projection_trace_heads AS head
              ON head.trace_id=queue.trace_id
             AND head.desired_generation_id=queue.generation_id
            JOIN source_trace_compatibility_revisions AS revision
              ON revision.trace_id=queue.trace_id
            WHERE generation.lifecycle IN ('pending','retry_pending')
              AND NOT EXISTS(
                    SELECT 1
                    FROM skill_projection_generation_inputs AS input
                    WHERE input.generation_id=queue.generation_id
                      AND input.input_evidence_kind='deleted_before_digest_v10'
              )
              AND revision.current_effective_state='resolved'
              AND revision.current_exact_version IS NOT NULL
              AND (
                    (queue.state='pending'
                     AND (queue.next_attempt_at IS NULL OR queue.next_attempt_at<=$now))
                 OR (queue.state='leased' AND queue.lease_expires_at<=$now)
              )
            ORDER BY queue.generation_id
            LIMIT 1;
            """;
        candidate.Parameters.AddWithValue("$now", Timestamp(claimedAt));
        using var reader = candidate.ExecuteReader();
        if (!reader.Read())
        {
            transaction.Commit();
            return null;
        }
        var generationId = reader.GetInt64(0);
        var traceId = reader.GetString(1);
        var revision = reader.GetInt64(2);
        var frontier = reader.GetString(3);
        var projector = reader.GetString(4);
        var attempts = reader.GetInt64(5);
        var previousLeaseGeneration = reader.GetInt64(6);
        var exactVersion = reader.GetString(7);
        reader.Close();

        var owner = Guid.NewGuid().ToString("N");
        var leaseGeneration = previousLeaseGeneration == long.MaxValue
            ? throw new InvalidOperationException("skill_projection_lease_generation_exhausted")
            : previousLeaseGeneration + 1;
        var attemptCount = attempts == long.MaxValue ? long.MaxValue : attempts + 1;
        var expiresAt = claimedAt.Add(QueueLeaseDuration);
        using var claim = connection.CreateCommand();
        claim.Transaction = transaction;
        claim.CommandText =
            """
            UPDATE skill_projection_queue
            SET state='leased',attempt_count=$attempt_count,lease_owner=$owner,
                lease_generation=$lease_generation,lease_expires_at=$lease_expires_at,
                next_attempt_at=NULL,error_code=NULL
            WHERE generation_id=$generation_id
              AND lease_generation=$previous_lease_generation
              AND (
                    (state='pending'
                     AND (next_attempt_at IS NULL OR next_attempt_at<=$now))
                 OR (state='leased' AND lease_expires_at<=$now)
              )
              AND EXISTS(
                    SELECT 1
                    FROM skill_projection_trace_heads
                    WHERE trace_id=$trace_id
                      AND desired_generation_id=$generation_id
              );
            """;
        claim.Parameters.AddWithValue("$attempt_count", attemptCount);
        claim.Parameters.AddWithValue("$owner", owner);
        claim.Parameters.AddWithValue("$lease_generation", leaseGeneration);
        claim.Parameters.AddWithValue("$lease_expires_at", Timestamp(expiresAt));
        claim.Parameters.AddWithValue("$generation_id", generationId);
        claim.Parameters.AddWithValue("$previous_lease_generation", previousLeaseGeneration);
        claim.Parameters.AddWithValue("$now", Timestamp(claimedAt));
        claim.Parameters.AddWithValue("$trace_id", traceId);
        if (claim.ExecuteNonQuery() != 1)
        {
            transaction.Rollback();
            return null;
        }
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_generations
            SET lifecycle='pending',updated_at=$updated_at
            WHERE generation_id=$generation_id AND lifecycle='retry_pending';
            """,
            ("$updated_at", Timestamp(claimedAt)),
            ("$generation_id", generationId));
        var inputs = ReadInputs(connection, transaction, generationId);
        transaction.Commit();
        return new(
            generationId,
            traceId,
            revision,
            frontier,
            projector,
            attemptCount,
            owner,
            leaseGeneration,
            expiresAt,
            exactVersion,
            inputs);
    }

    internal ValueTask<RetentionBatchReadResult<IReadOnlyList<RawTelemetryRecord>>> ReadFrontierAsync(
        SkillProjectionQueueLease lease,
        CancellationToken cancellationToken)
    {
        if (lease.Inputs.Any(static input =>
                input.EvidenceKind == SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10))
            throw new InvalidOperationException("skill_projection_input_unavailable");
        return rawStore.ReadRawRecordsAsync(
            lease.Inputs.Select(static input => input.RawRecordId).ToArray(),
            RetentionReadKind.Operation,
            cancellationToken);
    }

    internal SkillProjectionQueueLease? Heartbeat(
        SkillProjectionQueueLease lease,
        RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> retentionLease,
        DateTimeOffset heartbeatAt)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(retentionLease);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var persistedExpiryText = ScalarText(
            connection,
            transaction,
            """
            SELECT queue.lease_expires_at
            FROM skill_projection_queue AS queue
            JOIN skill_projection_trace_heads AS head
              ON head.trace_id=queue.trace_id
             AND head.desired_generation_id=queue.generation_id
            WHERE queue.generation_id=$generation_id
              AND queue.state='leased'
              AND queue.lease_owner=$owner
              AND queue.lease_generation=$lease_generation
              AND queue.lease_expires_at>$heartbeat_at;
            """,
            ("$generation_id", lease.GenerationId),
            ("$owner", lease.LeaseOwner),
            ("$lease_generation", lease.LeaseGeneration),
            ("$heartbeat_at", Timestamp(heartbeatAt)));
        if (persistedExpiryText is null)
        {
            transaction.Rollback();
            return null;
        }
        var persistedInputs = ReadInputs(connection, transaction, lease.GenerationId);
        if (!LeaseFrontierMatches(lease, persistedInputs))
        {
            transaction.Rollback();
            return null;
        }
        var persistedExpiry = DateTimeOffset.Parse(
            persistedExpiryText,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);
        var previousHeartbeatAt = persistedExpiry.Subtract(QueueLeaseDuration);
        if (heartbeatAt - previousHeartbeatAt < QueueHeartbeatInterval)
        {
            transaction.Commit();
            return lease with { LeaseExpiresAt = persistedExpiry };
        }
        if (!RetentionCatalogStore.ValidateSkillProjectionOperationLeases(
                connection,
                transaction,
                retentionLease.Grants,
                persistedInputs.Select(static input => input.RawRecordId).ToArray(),
                heartbeatAt))
        {
            transaction.Rollback();
            return null;
        }

        var queueExpiry = heartbeatAt.Add(QueueLeaseDuration);
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_queue
            SET lease_expires_at=$lease_expires_at
            WHERE generation_id=$generation_id
              AND state='leased'
              AND lease_owner=$owner
              AND lease_generation=$lease_generation
              AND lease_expires_at>$heartbeat_at
              AND EXISTS(
                    SELECT 1
                    FROM skill_projection_trace_heads
                    WHERE trace_id=$trace_id
                      AND desired_generation_id=$generation_id
              );
            """,
            ("$lease_expires_at", Timestamp(queueExpiry)),
            ("$generation_id", lease.GenerationId),
            ("$owner", lease.LeaseOwner),
            ("$lease_generation", lease.LeaseGeneration),
            ("$heartbeat_at", Timestamp(heartbeatAt)),
            ("$trace_id", lease.TraceId));
        if (Changes(connection, transaction) != 1)
        {
            transaction.Rollback();
            return null;
        }

        var renewedRetentionExpiry = heartbeatAt.Add(RetentionV1Constants.LeaseDuration);
        var renewedGrants = new List<RetentionReadGrant>();
        foreach (var grant in retentionLease.Grants)
        {
            if (grant.LeaseExpiresAt - heartbeatAt > RetentionV1Constants.LeaseRenewalDeadline)
                continue;
            Execute(
                connection,
                transaction,
                """
                UPDATE retention_leases
                SET expires_at=$expires_at
                WHERE item_id=$item_id
                  AND lease_kind='operation'
                  AND owner=$owner
                  AND generation=$generation
                  AND expires_at>$heartbeat_at;
                """,
                ("$expires_at", Timestamp(renewedRetentionExpiry)),
                ("$item_id", grant.ItemId),
                ("$owner", grant.LeaseOwner),
                ("$generation", grant.LeaseGeneration),
                ("$heartbeat_at", Timestamp(heartbeatAt)));
            if (Changes(connection, transaction) != 1)
            {
                transaction.Rollback();
                return null;
            }
            renewedGrants.Add(grant);
        }
        transaction.Commit();
        foreach (var grant in renewedGrants)
            grant.AdvanceExpiry(renewedRetentionExpiry);
        return lease with { LeaseExpiresAt = queueExpiry };
    }

    internal SkillProjectionWorkOutcome Publish(
        SkillProjectionQueueLease lease,
        IReadOnlyList<SkillProjectionProjectedInput> projectedInputs,
        RetentionBatchReadLease<IReadOnlyList<RawTelemetryRecord>> retentionLease,
        DateTimeOffset publishedAt)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(projectedInputs);
        ArgumentNullException.ThrowIfNull(retentionLease);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!OwnsCurrentQueueLease(connection, transaction, lease, publishedAt))
        {
            transaction.Rollback();
            return SkillProjectionWorkOutcome.StaleOwner;
        }

        var persistedInputs = ReadInputs(connection, transaction, lease.GenerationId);
        if (persistedInputs.Any(static input =>
                input.EvidenceKind == SkillProjectionInputEvidenceKind.DeletedBeforeDigestV10))
            return FinishTerminal(
                connection,
                transaction,
                lease,
                publishedAt,
                "skill_projection_input_unavailable");
        if (!FrontierMatches(lease, persistedInputs, projectedInputs))
            return FinishTerminal(connection, transaction, lease, publishedAt, "skill_projection_frontier_mismatch");
        var current = SourceCompatibilityReconciler.ReadEffectiveTrace(connection, transaction, lease.TraceId);
        var revision = ScalarLong(
            connection,
            transaction,
            "SELECT current_revision FROM source_trace_compatibility_revisions WHERE trace_id=$trace_id;",
            ("$trace_id", lease.TraceId));
        var desired = ScalarLong(
            connection,
            transaction,
            "SELECT desired_generation_id FROM skill_projection_trace_heads WHERE trace_id=$trace_id;",
            ("$trace_id", lease.TraceId));
        if (current is not
            {
                State: TraceSourceVersionResolutionState.Resolved,
                SourceApplicationVersion: not null,
            }
            || revision != lease.CompatibilityRevision
            || desired != lease.GenerationId
            || !string.Equals(current.SourceApplicationVersion, lease.ExactVersion, StringComparison.Ordinal)
            || !string.Equals(lease.ProjectorVersion, SkillProjectionGenerationParticipant.CurrentProjectorVersion, StringComparison.Ordinal))
        {
            return FinishSuperseded(connection, transaction, lease, publishedAt);
        }
        if (!RetentionCatalogStore.ValidateSkillProjectionOperationLeases(
                connection,
                transaction,
                retentionLease.Grants,
                persistedInputs.Select(static input => input.RawRecordId).ToArray(),
                publishedAt))
        {
            return FinishRetry(connection, transaction, lease, publishedAt, "retention_lease_lost");
        }

        foreach (var projectedInput in projectedInputs)
            InsertProjection(connection, transaction, lease.GenerationId, projectedInput, publishedAt);
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_generations
            SET lifecycle='current',updated_at=$updated_at
            WHERE generation_id=$generation_id AND lifecycle='pending';
            """,
            ("$updated_at", Timestamp(publishedAt)),
            ("$generation_id", lease.GenerationId));
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_queue
            SET state='completed',lease_owner=NULL,lease_expires_at=NULL,
                next_attempt_at=NULL,error_code=NULL
            WHERE generation_id=$generation_id
              AND state='leased' AND lease_owner=$owner
              AND lease_generation=$lease_generation;
            """,
            ("$generation_id", lease.GenerationId),
            ("$owner", lease.LeaseOwner),
            ("$lease_generation", lease.LeaseGeneration));
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_trace_heads
            SET current_generation_id=$generation_id,updated_at=$updated_at
            WHERE trace_id=$trace_id AND desired_generation_id=$generation_id;
            """,
            ("$generation_id", lease.GenerationId),
            ("$updated_at", Timestamp(publishedAt)),
            ("$trace_id", lease.TraceId));
        transaction.Commit();
        return SkillProjectionWorkOutcome.Published;
    }

    internal SkillProjectionWorkOutcome RecordInputUnavailable(
        SkillProjectionQueueLease lease,
        DateTimeOffset at) =>
        FinishOwned(
            lease,
            at,
            generationLifecycle: "input_unavailable",
            queueState: "input_unavailable",
            errorCode: "retention_input_unavailable",
            SkillProjectionWorkOutcome.InputUnavailable);

    internal SkillProjectionWorkOutcome RecordRetry(
        SkillProjectionQueueLease lease,
        DateTimeOffset at,
        string errorCode) =>
        FinishOwned(
            lease,
            at,
            generationLifecycle: "retry_pending",
            queueState: "pending",
            errorCode,
            SkillProjectionWorkOutcome.Retrying);

    internal SkillProjectionWorkOutcome RecordTerminal(
        SkillProjectionQueueLease lease,
        DateTimeOffset at,
        string errorCode) =>
        FinishOwned(
            lease,
            at,
            generationLifecycle: "failed_terminal",
            queueState: "failed_terminal",
            errorCode,
            SkillProjectionWorkOutcome.FailedTerminal);

    private SkillProjectionWorkOutcome FinishOwned(
        SkillProjectionQueueLease lease,
        DateTimeOffset at,
        string generationLifecycle,
        string queueState,
        string errorCode,
        SkillProjectionWorkOutcome outcome)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (!OwnsCurrentQueueLease(connection, transaction, lease, at))
        {
            transaction.Rollback();
            return SkillProjectionWorkOutcome.StaleOwner;
        }
        var nextAttemptAt = queueState == "pending"
            ? Timestamp(at.AddSeconds(RetryDelaySeconds(lease.AttemptCount)))
            : null;
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_generations
            SET lifecycle=$lifecycle,updated_at=$updated_at
            WHERE generation_id=$generation_id;
            """,
            ("$lifecycle", generationLifecycle),
            ("$updated_at", Timestamp(at)),
            ("$generation_id", lease.GenerationId));
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_queue
            SET state=$state,lease_owner=NULL,lease_expires_at=NULL,
                next_attempt_at=$next_attempt_at,error_code=$error_code
            WHERE generation_id=$generation_id
              AND lease_owner=$owner AND lease_generation=$lease_generation;
            """,
            ("$state", queueState),
            ("$next_attempt_at", nextAttemptAt is null ? DBNull.Value : nextAttemptAt),
            ("$error_code", errorCode),
            ("$generation_id", lease.GenerationId),
            ("$owner", lease.LeaseOwner),
            ("$lease_generation", lease.LeaseGeneration));
        transaction.Commit();
        return outcome;
    }

    private static SkillProjectionWorkOutcome FinishSuperseded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionQueueLease lease,
        DateTimeOffset at)
    {
        SetOwnedTerminalState(
            connection,
            transaction,
            lease,
            at,
            generationLifecycle: "superseded",
            queueState: "superseded",
            errorCode: null);
        transaction.Commit();
        return SkillProjectionWorkOutcome.Superseded;
    }

    private static SkillProjectionWorkOutcome FinishTerminal(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionQueueLease lease,
        DateTimeOffset at,
        string errorCode)
    {
        SetOwnedTerminalState(
            connection,
            transaction,
            lease,
            at,
            generationLifecycle: "failed_terminal",
            queueState: "failed_terminal",
            errorCode);
        transaction.Commit();
        return SkillProjectionWorkOutcome.FailedTerminal;
    }

    private static SkillProjectionWorkOutcome FinishRetry(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionQueueLease lease,
        DateTimeOffset at,
        string errorCode)
    {
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_generations
            SET lifecycle='retry_pending',updated_at=$updated_at
            WHERE generation_id=$generation_id;
            """,
            ("$updated_at", Timestamp(at)),
            ("$generation_id", lease.GenerationId));
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_queue
            SET state='pending',lease_owner=NULL,lease_expires_at=NULL,
                next_attempt_at=$next_attempt_at,error_code=$error_code
            WHERE generation_id=$generation_id
              AND state='leased' AND lease_owner=$owner
              AND lease_generation=$lease_generation;
            """,
            ("$next_attempt_at", Timestamp(at.AddSeconds(RetryDelaySeconds(lease.AttemptCount)))),
            ("$error_code", errorCode),
            ("$generation_id", lease.GenerationId),
            ("$owner", lease.LeaseOwner),
            ("$lease_generation", lease.LeaseGeneration));
        transaction.Commit();
        return SkillProjectionWorkOutcome.Retrying;
    }

    private static void SetOwnedTerminalState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionQueueLease lease,
        DateTimeOffset at,
        string generationLifecycle,
        string queueState,
        string? errorCode)
    {
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_generations
            SET lifecycle=$lifecycle,updated_at=$updated_at
            WHERE generation_id=$generation_id;
            """,
            ("$lifecycle", generationLifecycle),
            ("$updated_at", Timestamp(at)),
            ("$generation_id", lease.GenerationId));
        Execute(
            connection,
            transaction,
            """
            UPDATE skill_projection_queue
            SET state=$state,lease_owner=NULL,lease_expires_at=NULL,
                next_attempt_at=NULL,error_code=$error_code
            WHERE generation_id=$generation_id
              AND state='leased' AND lease_owner=$owner
              AND lease_generation=$lease_generation;
            """,
            ("$state", queueState),
            ("$error_code", errorCode is null ? DBNull.Value : errorCode),
            ("$generation_id", lease.GenerationId),
            ("$owner", lease.LeaseOwner),
            ("$lease_generation", lease.LeaseGeneration));
    }

    private static bool OwnsCurrentQueueLease(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SkillProjectionQueueLease lease,
        DateTimeOffset at) =>
        ScalarLong(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM skill_projection_queue
            WHERE generation_id=$generation_id
              AND state='leased'
              AND lease_owner=$owner
              AND lease_generation=$lease_generation
              AND lease_expires_at>$at;
            """,
            ("$generation_id", lease.GenerationId),
            ("$owner", lease.LeaseOwner),
            ("$lease_generation", lease.LeaseGeneration),
            ("$at", Timestamp(at))) == 1;

    private static bool FrontierMatches(
        SkillProjectionQueueLease lease,
        IReadOnlyList<SkillProjectionQueuedInput> persistedInputs,
        IReadOnlyList<SkillProjectionProjectedInput> projectedInputs)
    {
        if (!LeaseFrontierMatches(lease, persistedInputs)
            || projectedInputs.Count != lease.Inputs.Count)
            return false;
        for (var index = 0; index < persistedInputs.Count; index++)
        {
            var expected = lease.Inputs[index];
            var projected = projectedInputs[index];
            if (projected.RawRecordId != expected.RawRecordId
                || expected.EvidenceKind != SkillProjectionInputEvidenceKind.PayloadSha256
                || !string.Equals(
                    SkillProjectionHashing.InputDigest(projected.RawRecord.PayloadJson),
                    expected.RawPayloadSha256,
                    StringComparison.Ordinal))
                return false;
        }
        var digest = SkillProjectionHashing.FrontierDigest(
            lease.TraceId,
            persistedInputs
                .Select(static input => new SkillProjectionFrontierInput(
                    input.SourceObservationId,
                    input.RawRecordId,
                    input.EvidenceKind,
                    input.RawPayloadSha256))
                .ToArray());
        return string.Equals(digest, lease.InputFrontierSha256, StringComparison.Ordinal);
    }

    private static bool LeaseFrontierMatches(
        SkillProjectionQueueLease lease,
        IReadOnlyList<SkillProjectionQueuedInput> persistedInputs)
    {
        if (persistedInputs.Count != lease.Inputs.Count)
            return false;
        for (var index = 0; index < persistedInputs.Count; index++)
        {
            var expected = lease.Inputs[index];
            var persisted = persistedInputs[index];
            if (persisted.Ordinal != index
                || expected.Ordinal != index
                || persisted.SourceObservationId != expected.SourceObservationId
                || persisted.RawRecordId != expected.RawRecordId
                || persisted.EvidenceKind != expected.EvidenceKind
                || !string.Equals(
                    persisted.RawPayloadSha256,
                    expected.RawPayloadSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    persisted.SourceSurface,
                    expected.SourceSurface,
                    StringComparison.Ordinal))
                return false;
        }
        var digest = SkillProjectionHashing.FrontierDigest(
            lease.TraceId,
            persistedInputs
                .Select(static input => new SkillProjectionFrontierInput(
                    input.SourceObservationId,
                    input.RawRecordId,
                    input.EvidenceKind,
                    input.RawPayloadSha256))
                .ToArray());
        return string.Equals(digest, lease.InputFrontierSha256, StringComparison.Ordinal);
    }

    private static IReadOnlyList<SkillProjectionQueuedInput> ReadInputs(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT input.input_ordinal,input.source_observation_id,input.raw_record_id,
                   input.input_evidence_kind,input.raw_payload_sha256,source.source_surface
            FROM skill_projection_generation_inputs AS input
            LEFT JOIN source_schema_observations AS source
              ON source.id=input.source_observation_id
             AND source.raw_record_id=input.raw_record_id
            WHERE input.generation_id=$generation_id
            ORDER BY input.input_ordinal;
            """;
        command.Parameters.AddWithValue("$generation_id", generationId);
        using var reader = command.ExecuteReader();
        var rows = new List<SkillProjectionQueuedInput>();
        while (reader.Read())
        {
            rows.Add(new(
                reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                SkillProjectionHashing.ParseEvidenceKind(reader.GetString(3)),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }
        return rows;
    }

    private static void InsertProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        SkillProjectionProjectedInput projectedInput,
        DateTimeOffset projectedAt)
    {
        foreach (var invocation in projectedInput.Projection.Invocations)
        {
            if (invocation.SpanId is null)
                continue;
            var sessionId = ResolveExactCopilotCliSessionId(
                connection,
                transaction,
                invocation.NativeSessionId);
            Execute(
                connection,
                transaction,
                """
                INSERT INTO skill_projection_invocations(
                    generation_id,source_arm,raw_record_id,trace_id,span_id,span_ordinal,
                    session_id,skill_name,skill_source,invocation_trigger,
                    source_application_version,projected_at)
                VALUES($generation_id,'otel_trace_span',$raw_record_id,$trace_id,$span_id,
                    $span_ordinal,$session_id,$skill_name,$skill_source,$invocation_trigger,
                    $source_application_version,$projected_at);
                """,
                ("$generation_id", generationId),
                ("$raw_record_id", projectedInput.RawRecordId),
                ("$trace_id", invocation.TraceId),
                ("$span_id", invocation.SpanId),
                ("$span_ordinal", invocation.SpanOrdinal),
                ("$session_id", sessionId is null ? DBNull.Value : sessionId),
                ("$skill_name", invocation.SkillName),
                ("$skill_source", invocation.SkillSource is null ? DBNull.Value : invocation.SkillSource),
                ("$invocation_trigger", invocation.InvocationTrigger is null ? DBNull.Value : invocation.InvocationTrigger),
                ("$source_application_version", invocation.SourceApplicationVersion),
                ("$projected_at", Timestamp(projectedAt)));
        }
        foreach (var inventory in projectedInput.Projection.Inventories)
        {
            var sessionId = ResolveExactCopilotCliSessionId(
                connection,
                transaction,
                inventory.NativeSessionId);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO skill_projection_inventories(
                    generation_id,source_arm,raw_record_id,trace_id,session_id,
                    observed_name_count,retained_name_count,names_truncated,
                    source_application_version,projected_at)
                VALUES($generation_id,'otel_trace_span',$raw_record_id,$trace_id,$session_id,
                    $observed_name_count,$retained_name_count,$names_truncated,
                    $source_application_version,$projected_at);
                SELECT last_insert_rowid();
                """;
            insert.Parameters.AddWithValue("$generation_id", generationId);
            insert.Parameters.AddWithValue("$raw_record_id", projectedInput.RawRecordId);
            insert.Parameters.AddWithValue("$trace_id", inventory.TraceId);
            insert.Parameters.AddWithValue("$session_id", sessionId is null ? DBNull.Value : sessionId);
            insert.Parameters.AddWithValue("$observed_name_count", inventory.ObservedNameCount);
            insert.Parameters.AddWithValue("$retained_name_count", inventory.RetainedNames.Count);
            insert.Parameters.AddWithValue("$names_truncated", inventory.NamesTruncated ? 1 : 0);
            insert.Parameters.AddWithValue("$source_application_version", inventory.SourceApplicationVersion);
            insert.Parameters.AddWithValue("$projected_at", Timestamp(projectedAt));
            var inventoryId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
            for (var index = 0; index < inventory.RetainedNames.Count; index++)
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO skill_projection_inventory_names(
                        inventory_id,name_ordinal,skill_name)
                    VALUES($inventory_id,$ordinal,$skill_name);
                    """,
                    ("$inventory_id", inventoryId),
                    ("$ordinal", index),
                    ("$skill_name", inventory.RetainedNames[index]));
            }
        }
    }

    private static string? ResolveExactCopilotCliSessionId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? nativeSessionId)
    {
        if (string.IsNullOrWhiteSpace(nativeSessionId))
            return null;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT session_id
            FROM session_native_ids
            WHERE source_surface='copilot-cli'
              AND native_session_id=$native_session_id COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$native_session_id", nativeSessionId);
        return command.ExecuteScalar() as string;
    }

    private static long RetryDelaySeconds(long attemptCount)
    {
        var exponent = Math.Min(Math.Max(attemptCount - 1, 0), 9);
        return Math.Min(1L << (int)exponent, 300L);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false,
            }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static long? ScalarLong(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static string? ScalarText(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private static long Changes(SqliteConnection connection, SqliteTransaction transaction) =>
        ScalarLong(connection, transaction, "SELECT changes();") ?? 0;

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}

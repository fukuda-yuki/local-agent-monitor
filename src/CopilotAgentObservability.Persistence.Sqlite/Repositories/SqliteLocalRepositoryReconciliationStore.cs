using System.Globalization;
using System.Security.Cryptography;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Repositories;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalRepositoryQueueTransitionResult { Applied, NoWork, Busy, StaleOwner, Corrupt }

internal sealed record LocalRepositoryQueueClaimResult(LocalRepositoryQueueTransitionResult Status, LocalRepositoryQueueLease? Lease);

internal sealed record LocalRepositoryQueueRenewResult(LocalRepositoryQueueTransitionResult Status, LocalRepositoryQueueLease? Lease);

internal sealed record LocalRepositoryQueueHeartbeatResult(LocalRepositoryQueueTransitionResult Status, LocalRepositoryQueueLease? Lease);

internal sealed record LocalRepositoryQueueLease(
    string QueueId,
    long RawRecordId,
    string? RawPayloadSha256,
    string ProjectorVersion,
    string ReconciliationFingerprint,
    long AttemptCount,
    string LeaseToken,
    DateTimeOffset LeaseExpiresAt);

internal enum LocalRepositoryReconciliationCheckpoint
{
    BeforeDiscoveryPublication,
    BeforeDiscoveryRawAvailabilityRead,
    BeforeRawAvailabilityRead,
    AfterRawAvailabilityRead,
    AfterHeartbeatTransactionBegun,
    BeforeHeartbeatPublicationLock,
    AfterPeriodicHeartbeatApplied,
    AfterPeriodicHeartbeatRejected,
    BeforeHandoffHeartbeat,
    AfterHandoffHeartbeat,
    AfterHandoffRejected,
    BeforeRetentionRenewalPublication,
    AfterHeartbeatBusy,
    HeartbeatLeaseExpired,
}

internal interface ILocalRepositoryReconciliationCheckpoint
{
    void Reached(LocalRepositoryReconciliationCheckpoint checkpoint);
}

internal sealed partial class SqliteLocalRepositoryReconciliationStore
{
    private const int DiscoverySpanBatchSize = 256;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WaitingSessionDelay = TimeSpan.FromSeconds(5);
    private readonly string databasePath;
    private readonly LocalRepositoryStoreBinding binding;
    private readonly TimeProvider timeProvider;
    private readonly Func<string> leaseTokenFactory;
    private readonly ILocalRepositoryReconciliationCheckpoint? checkpoint;

    internal SqliteLocalRepositoryReconciliationStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        Func<string>? leaseTokenFactory = null,
        ILocalRepositoryReconciliationCheckpoint? checkpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        binding = LocalRepositoryStoreBinding.Create(databasePath, RetentionCatalogContext.AdoptExistingCatalogV1(databasePath));
        this.databasePath = binding.CanonicalDatabasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.leaseTokenFactory = leaseTokenFactory ?? (() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));
        this.checkpoint = checkpoint;
    }

    internal bool IsBoundTo(LocalRepositoryRawAvailabilityReader rawAvailability) =>
        rawAvailability is not null && binding.Matches(rawAvailability.Binding);

    internal LocalRepositoryQueueClaimResult TryClaimNext(DateTimeOffset? claimedAt = null)
    {
        var at = (claimedAt ?? timeProvider.GetUtcNow()).ToUniversalTime();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var candidate = connection.CreateCommand();
            candidate.Transaction = transaction;
            candidate.CommandText = """
                SELECT queue_id,raw_record_id,raw_payload_sha256,projector_version,reconciliation_fingerprint,attempt_count
                FROM local_repository_reconciliation_queue
                WHERE state='pending'
                   OR (state='waiting_session' AND updated_at <= $waiting_at)
                ORDER BY raw_record_id
                LIMIT 1;
                """;
            candidate.Parameters.AddWithValue("$waiting_at", Timestamp(at - WaitingSessionDelay));
            using var reader = candidate.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return new(LocalRepositoryQueueTransitionResult.NoWork, null);
            }
            var queueId = reader.GetString(0);
            var rawRecordId = reader.GetInt64(1);
            var digest = reader.IsDBNull(2) ? null : reader.GetString(2);
            var projectorVersion = reader.GetString(3);
            var fingerprint = reader.GetString(4);
            var priorAttempts = reader.GetInt64(5);
            reader.Close();
            var attemptCount = priorAttempts == long.MaxValue ? long.MaxValue : priorAttempts + 1;
            var token = leaseTokenFactory();
            if (!IsLeaseToken(token))
                throw new InvalidOperationException("local_repository_queue_lease_token_invalid");
            var expiry = at + LeaseDuration;
            using var claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE local_repository_reconciliation_queue
                SET state='leased',attempt_count=$attempt_count,lease_token=$lease_token,
                    lease_expires_at=$lease_expires_at,terminal_reason=NULL,updated_at=$updated_at
                WHERE queue_id=$queue_id
                  AND (state='pending' OR (state='waiting_session' AND updated_at <= $waiting_at));
                """;
            claim.Parameters.AddWithValue("$attempt_count", attemptCount);
            claim.Parameters.AddWithValue("$lease_token", token);
            claim.Parameters.AddWithValue("$lease_expires_at", Timestamp(expiry));
            claim.Parameters.AddWithValue("$updated_at", Timestamp(at));
            claim.Parameters.AddWithValue("$queue_id", queueId);
            claim.Parameters.AddWithValue("$waiting_at", Timestamp(at - WaitingSessionDelay));
            if (claim.ExecuteNonQuery() != 1)
            {
                transaction.Rollback();
                return new(LocalRepositoryQueueTransitionResult.StaleOwner, null);
            }
            transaction.Commit();
            return new(LocalRepositoryQueueTransitionResult.Applied, new(queueId, rawRecordId, digest, projectorVersion, fingerprint, attemptCount, token, expiry));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(LocalRepositoryQueueTransitionResult.Busy, null);
        }
    }

    internal async ValueTask<LocalRepositoryQueueTransitionResult> DiscoverAsync(
        LocalRepositoryRawAvailabilityReader rawAvailability,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rawAvailability);
        if (!IsBoundTo(rawAvailability)) throw new InvalidOperationException("local_repository_store_binding_mismatch");
        var prepared = new List<(long RawRecordId, LocalRepositoryRawAvailabilityResult Result)>();
        try
        {
            var frontier = ReadDiscoveryFrontier();
            if (frontier.SpanIds.Count == 0) return LocalRepositoryQueueTransitionResult.NoWork;
            foreach (var rawRecordId in frontier.RawRecordIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.BeforeDiscoveryRawAvailabilityRead);
                var read = await rawAvailability.ReadAsync(rawRecordId, null, RetentionReadKind.Operation, cancellationToken).ConfigureAwait(false);
                if (read.Status == LocalRepositoryRawAvailabilityStatus.Busy)
                    return LocalRepositoryQueueTransitionResult.Busy;
                prepared.Add((rawRecordId, read));
            }
            cancellationToken.ThrowIfCancellationRequested();
            checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.BeforeDiscoveryPublication);
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadDiscoveryFrontier(connection, transaction);
            if (!frontier.SpanIds.SequenceEqual(current.SpanIds) || !frontier.RawRecordIds.SequenceEqual(current.RawRecordIds))
            {
                transaction.Rollback();
                return LocalRepositoryQueueTransitionResult.StaleOwner;
            }
            var publicationMembers = prepared
                .Select((input, index) => (input, index))
                .Where(static item =>
                    item.input.Result.Status == LocalRepositoryRawAvailabilityStatus.Success
                    && item.input.Result.Availability == LocalRepositoryRawAvailability.Available
                    && item.input.Result.Lease is not null)
                .Select(static item => new RetentionGrantPublicationMember(
                    item.input.Result.Lease!.Grant,
                    item.index))
                .ToArray();
            using var publications = RetentionGrantPublicationSet.EnterInOrder(publicationMembers);
            var at = timeProvider.GetUtcNow().ToUniversalTime();
            var publicationIndex = 0;
            foreach (var input in prepared)
            {
                var rawRecordId = input.RawRecordId;
                if (input.Result.Status is LocalRepositoryRawAvailabilityStatus.Corrupt or LocalRepositoryRawAvailabilityStatus.PayloadDigestMismatch)
                {
                    transaction.Rollback();
                    return LocalRepositoryQueueTransitionResult.Corrupt;
                }
                string evidenceKind;
                string? digest;
                string fingerprint;
                string state;
                if (input.Result.Status == LocalRepositoryRawAvailabilityStatus.Success && input.Result.Availability == LocalRepositoryRawAvailability.Available && input.Result.Lease?.Grant is { } grant)
                {
                    if (!RetentionCatalogStore.ValidateLocalRepositoryOperationLease(
                            connection,
                            transaction,
                            grant,
                            rawRecordId,
                            publications.ScopeFor(publicationIndex++, grant),
                            at))
                    {
                        transaction.Rollback();
                        return LocalRepositoryQueueTransitionResult.StaleOwner;
                    }
                    using var rawReference = input.Result.Lease.AcquireValueReference();
                    digest = SkillProjectionHashing.InputDigest(rawReference.Value.PayloadJson);
                    evidenceKind = "payload_sha256";
                    fingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, digest));
                    state = "pending";
                }
                else
                {
                    digest = null;
                    evidenceKind = "input_unavailable";
                    fingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(LocalRepositoryReconciliationEvidence.InputUnavailable(rawRecordId));
                    state = "input_unavailable";
                }
                InsertOrRequireEqual(connection, transaction, rawRecordId, evidenceKind, digest, fingerprint, state, at);
            }
            using (var cursor = connection.CreateCommand())
            {
                cursor.Transaction = transaction;
                cursor.CommandText = """
                    UPDATE local_repository_reconciliation_state
                    SET last_discovered_span_id=$last_discovered_span_id,
                        updated_at=$updated_at
                    WHERE projector_key=$projector_key;
                    """;
                cursor.Parameters.AddWithValue("$projector_key", LocalRepositoryCatalogConstants.ProjectorKey);
                cursor.Parameters.AddWithValue("$last_discovered_span_id", frontier.SpanIds[^1]);
                cursor.Parameters.AddWithValue("$updated_at", Timestamp(at));
                if (cursor.ExecuteNonQuery() != 1)
                {
                    transaction.Rollback();
                    return LocalRepositoryQueueTransitionResult.Corrupt;
                }
            }
            if (!publications.AreCommittedHandlesPublished())
            {
                transaction.Rollback();
                return LocalRepositoryQueueTransitionResult.StaleOwner;
            }
            transaction.Commit();
            return LocalRepositoryQueueTransitionResult.Applied;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return LocalRepositoryQueueTransitionResult.Busy; }
        catch (LocalRepositoryQueueConflictException) { return LocalRepositoryQueueTransitionResult.Corrupt; }
        catch (LocalRepositoryStateAuthorityException) { return LocalRepositoryQueueTransitionResult.Corrupt; }
        finally
        {
            foreach (var input in prepared) await input.Result.DisposeAsync().ConfigureAwait(false);
        }
    }

    internal LocalRepositoryQueueTransitionResult RecoverExpiredLeases(DateTimeOffset? recoveredAt = null)
    {
        var at = (recoveredAt ?? timeProvider.GetUtcNow()).ToUniversalTime();
        return ExecuteRecovery(at);
    }

    internal LocalRepositoryQueueRenewResult Renew(LocalRepositoryQueueLease lease, DateTimeOffset? renewedAt = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var at = (renewedAt ?? timeProvider.GetUtcNow()).ToUniversalTime();
        var expiry = at + LeaseDuration;
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var changed = UpdateOwned(connection, transaction, lease, at, "lease_expires_at=$expiry,updated_at=$at", ("$expiry", Timestamp(expiry)));
            if (!changed) { transaction.Rollback(); return new(LocalRepositoryQueueTransitionResult.StaleOwner, null); }
            transaction.Commit();
            return new(LocalRepositoryQueueTransitionResult.Applied, lease with { LeaseExpiresAt = expiry });
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return new(LocalRepositoryQueueTransitionResult.Busy, null); }
    }

    internal LocalRepositoryQueueHeartbeatResult Heartbeat(
        LocalRepositoryQueueLease lease,
        RetentionReadLease<RawTelemetryRecord> retentionLease,
        // Caller time is scheduling evidence only; trusted time is sampled after BEGIN IMMEDIATE and publication locks.
        DateTimeOffset? heartbeatAt = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(retentionLease);
        var grant = retentionLease.Grant;
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.AfterHeartbeatTransactionBegun);
            checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.BeforeHeartbeatPublicationLock);
            using var publications = RetentionGrantPublicationSet.EnterInOrder(
                [new RetentionGrantPublicationMember(grant, 0)]);
            var at = timeProvider.GetUtcNow().ToUniversalTime();
            var queueExpiry = at + LeaseDuration;
            if (!RetentionCatalogStore.TryPrepareOperationLeaseRenewals(
                    connection,
                    transaction,
                    [grant],
                    [lease.RawRecordId],
                    publications,
                    at,
                    out var renewedGrantIndices,
                    out var notificationRenewal)
                || !UpdateOwned(connection, transaction, lease, at, "lease_expires_at=$expiry,updated_at=$at", ("$expiry", Timestamp(queueExpiry))))
            {
                notificationRenewal?.Dispose();
                transaction.Rollback();
                return new(LocalRepositoryQueueTransitionResult.StaleOwner, null);
            }
            using (notificationRenewal)
            {
                try { transaction.Commit(); }
                catch
                {
                    notificationRenewal?.Dispose();
                    try { transaction.Rollback(); }
                    catch { }
                    return new(LocalRepositoryQueueTransitionResult.StaleOwner, null);
                }
                if (renewedGrantIndices.Count > 0)
                {
                    var notificationPublished = false;
                    try
                    {
                        checkpoint?.Reached(LocalRepositoryReconciliationCheckpoint.BeforeRetentionRenewalPublication);
                    }
                    finally
                    {
                        notificationPublished = notificationRenewal?.Publish() == true;
                    }
                    if (!notificationPublished)
                        return new(LocalRepositoryQueueTransitionResult.StaleOwner, null);
                }
            }
            return new(LocalRepositoryQueueTransitionResult.Applied, lease with { LeaseExpiresAt = queueExpiry });
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(LocalRepositoryQueueTransitionResult.Busy, null);
        }
    }

    internal LocalRepositoryQueueTransitionResult ReturnPending(LocalRepositoryQueueLease lease, DateTimeOffset? at = null) =>
        Transition(lease, at, "pending", null);

    internal LocalRepositoryQueueTransitionResult RecordInputUnavailable(LocalRepositoryQueueLease lease, DateTimeOffset? at = null) =>
        Transition(lease, at, "input_unavailable", null);

    internal LocalRepositoryQueueTransitionResult RecordPayloadDigestMismatch(LocalRepositoryQueueLease lease, DateTimeOffset? at = null) =>
        Transition(lease, at, "failed_terminal", "catalog_payload_digest_mismatch");

    internal LocalRepositoryQueueTransitionResult RecordCatalogSchemaViolation(LocalRepositoryQueueLease lease, DateTimeOffset? at = null) =>
        Transition(lease, at, "failed_terminal", "catalog_schema_violation");

    internal LocalRepositoryQueueTransitionResult TryComplete(SqliteConnection connection, SqliteTransaction transaction, LocalRepositoryQueueLease lease, DateTimeOffset at) =>
        TransitionBound(connection, transaction, lease, at, "completed", null);

    internal LocalRepositoryQueueTransitionResult TryWaitForSession(SqliteConnection connection, SqliteTransaction transaction, LocalRepositoryQueueLease lease, DateTimeOffset at) =>
        TransitionBound(connection, transaction, lease, at, "waiting_session", null);

    internal LocalRepositoryQueueTransitionResult TryFailTerminal(SqliteConnection connection, SqliteTransaction transaction, LocalRepositoryQueueLease lease, DateTimeOffset at, string terminalReason) =>
        TransitionBound(connection, transaction, lease, at, "failed_terminal", terminalReason);

    private LocalRepositoryQueueTransitionResult Transition(LocalRepositoryQueueLease lease, DateTimeOffset? time, string state, string? terminalReason)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var at = (time ?? timeProvider.GetUtcNow()).ToUniversalTime();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = TransitionBound(connection, transaction, lease, at, state, terminalReason);
            if (result != LocalRepositoryQueueTransitionResult.Applied) { transaction.Rollback(); return result; }
            transaction.Commit();
            return result;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return LocalRepositoryQueueTransitionResult.Busy; }
    }

    private LocalRepositoryQueueTransitionResult TransitionBound(SqliteConnection connection, SqliteTransaction transaction, LocalRepositoryQueueLease lease, DateTimeOffset at, string state, string? terminalReason)
    {
        if (!binding.Matches(connection, transaction)) throw new InvalidOperationException("local_repository_store_binding_mismatch");
        if (state == "failed_terminal" && terminalReason is not ("catalog_identity_conflict" or "catalog_session_identity_conflict" or "catalog_cardinality_exceeded" or "catalog_payload_digest_mismatch" or "catalog_parse_failure" or "catalog_schema_violation"))
            throw new ArgumentOutOfRangeException(nameof(terminalReason));
        var set = "state=$state,lease_token=NULL,lease_expires_at=NULL,terminal_reason=$terminal_reason,updated_at=$at";
        return UpdateOwned(connection, transaction, lease, at, set, ("$state", state), ("$terminal_reason", terminalReason))
            ? LocalRepositoryQueueTransitionResult.Applied : LocalRepositoryQueueTransitionResult.StaleOwner;
    }

    private static bool UpdateOwned(SqliteConnection connection, SqliteTransaction transaction, LocalRepositoryQueueLease lease, DateTimeOffset at, string set, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE local_repository_reconciliation_queue SET {set} WHERE queue_id=$queue_id AND state='leased' AND lease_token=$lease_token AND lease_expires_at>$at;";
        command.Parameters.AddWithValue("$queue_id", lease.QueueId);
        command.Parameters.AddWithValue("$lease_token", lease.LeaseToken);
        command.Parameters.AddWithValue("$at", Timestamp(at));
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command.ExecuteNonQuery() == 1;
    }

    private LocalRepositoryQueueTransitionResult ExecuteRecovery(DateTimeOffset at)
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE local_repository_reconciliation_queue
                SET state='pending',lease_token=NULL,lease_expires_at=NULL,terminal_reason=NULL,updated_at=$at
                WHERE state='leased' AND lease_expires_at <= $at;
                """;
            command.Parameters.AddWithValue("$at", Timestamp(at));
            var changes = command.ExecuteNonQuery();
            transaction.Commit();
            return changes == 0 ? LocalRepositoryQueueTransitionResult.NoWork : LocalRepositoryQueueTransitionResult.Applied;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return LocalRepositoryQueueTransitionResult.Busy; }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false, DefaultTimeout = 1 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static bool IsLeaseToken(string value) => value.Length == 64 && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private (List<long> SpanIds, List<long> RawRecordIds) ReadDiscoveryFrontier()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        var result = ReadDiscoveryFrontier(connection, transaction);
        transaction.Commit();
        return result;
    }

    private static (List<long> SpanIds, List<long> RawRecordIds) ReadDiscoveryFrontier(SqliteConnection connection, SqliteTransaction transaction)
    {
        long cursor;
        using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                SELECT projector_key,typeof(projector_key),
                       last_discovered_span_id,typeof(last_discovered_span_id),
                       updated_at,typeof(updated_at)
                FROM local_repository_reconciliation_state
                LIMIT 2;
                """;
            using var stateReader = state.ExecuteReader();
            if (!stateReader.Read()
                || stateReader.GetString(1) != "text"
                || stateReader.GetString(0) != LocalRepositoryCatalogConstants.ProjectorKey
                || stateReader.GetString(3) is not ("null" or "integer")
                || !stateReader.IsDBNull(2) && stateReader.GetInt64(2) <= 0
                || stateReader.GetString(5) != "text"
                || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(stateReader.GetString(4)))
                throw new LocalRepositoryStateAuthorityException();
            cursor = stateReader.IsDBNull(2) ? 0 : stateReader.GetInt64(2);
            if (stateReader.Read())
                throw new LocalRepositoryStateAuthorityException();
        }
        using var spans = connection.CreateCommand();
        spans.Transaction = transaction;
        spans.CommandText = "SELECT id,raw_record_id FROM monitor_spans WHERE id>$cursor ORDER BY id LIMIT $limit;";
        spans.Parameters.AddWithValue("$cursor", cursor);
        spans.Parameters.AddWithValue("$limit", DiscoverySpanBatchSize);
        using var reader = spans.ExecuteReader();
        var ids = new List<long>();
        var rawIds = new List<long>();
        var seen = new HashSet<long>();
        while (reader.Read())
        {
            ids.Add(reader.GetInt64(0));
            var raw = reader.GetInt64(1);
            if (seen.Add(raw)) rawIds.Add(raw);
        }
        return (ids, rawIds);
    }

    private static void InsertOrRequireEqual(SqliteConnection connection, SqliteTransaction transaction, long rawRecordId, string evidenceKind, string? digest, string fingerprint, string state, DateTimeOffset at)
    {
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO local_repository_reconciliation_queue(queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,terminal_reason,created_at,updated_at)
            VALUES($queue_id,$raw_record_id,$input_evidence_kind,$raw_payload_sha256,$projector_version,$reconciliation_fingerprint,$state,0,NULL,NULL,NULL,$at,$at)
            ON CONFLICT(raw_record_id,projector_version) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$queue_id", Guid.CreateVersion7(at).ToString("D", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        insert.Parameters.AddWithValue("$input_evidence_kind", evidenceKind);
        insert.Parameters.AddWithValue("$raw_payload_sha256", digest ?? (object)DBNull.Value);
        insert.Parameters.AddWithValue("$projector_version", LocalRepositoryCatalogConstants.ProjectorVersion);
        insert.Parameters.AddWithValue("$reconciliation_fingerprint", fingerprint);
        insert.Parameters.AddWithValue("$state", state);
        insert.Parameters.AddWithValue("$at", Timestamp(at));
        insert.ExecuteNonQuery();
        using var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT input_evidence_kind,raw_payload_sha256,reconciliation_fingerprint FROM local_repository_reconciliation_queue WHERE raw_record_id=$raw_record_id AND projector_version=$projector_version;";
        existing.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        existing.Parameters.AddWithValue("$projector_version", LocalRepositoryCatalogConstants.ProjectorVersion);
        using var reader = existing.ExecuteReader();
        if (!reader.Read()
            || !string.Equals(reader.GetString(0), evidenceKind, StringComparison.Ordinal)
            || !string.Equals(reader.IsDBNull(1) ? null : reader.GetString(1), digest, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(2), fingerprint, StringComparison.Ordinal))
            throw new LocalRepositoryQueueConflictException();
    }

    private sealed class LocalRepositoryQueueConflictException : InvalidOperationException
    {
        internal LocalRepositoryQueueConflictException() : base("local_repository_reconciliation_queue_conflict") { }
    }

    private sealed class LocalRepositoryStateAuthorityException : InvalidOperationException
    {
        internal LocalRepositoryStateAuthorityException() : base("local_repository_reconciliation_state_invalid") { }
    }
}

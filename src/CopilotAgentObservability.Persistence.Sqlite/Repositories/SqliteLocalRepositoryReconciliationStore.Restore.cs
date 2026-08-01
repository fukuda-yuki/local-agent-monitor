using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteLocalRepositoryReconciliationStore
{
    private const string RestoreInvalid = "local_repository_reconciliation_restore_invalid";

    internal static LocalRepositoryValidatedReconciliationState ValidateRestorableState(
        SqliteConnection connection,
        SqliteTransaction transaction) =>
        ValidateRestorableState(connection, transaction, null, null);

    internal static LocalRepositoryValidatedReconciliationState ValidateRestorableState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Action<int>? queuePageObserver,
        Action<int>? lookupObserver) =>
        LocalRepositoryValidatedReconciliationState.ValidateAndCreate(
            connection,
            transaction,
            queuePageObserver,
            lookupObserver);

    internal static void ValidateRestorableStateForCapability(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Action<int>? queuePageObserver)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        try
        {
            LocalRepositoryCatalogValidation.ValidateRestorableReconciliationQueueRows(
                connection,
                transaction,
                queuePageObserver);
        }
        catch (InvalidOperationException exception) when (exception.Message == "local_repository_catalog_canonical_value_invalid")
        {
            throw new InvalidOperationException(RestoreInvalid);
        }

        ValidateCursorAndFrontier(connection, transaction);
        ValidatePublicationOwnership(connection, transaction);
        ValidateSessionCandidateBound(connection, transaction);
    }

    internal static void NormalizeRestoredLeases(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE local_repository_reconciliation_queue
            SET state='pending',
                lease_token=NULL,
                lease_expires_at=NULL
            WHERE state='leased';
            """;
        command.ExecuteNonQuery();
    }

    private static void ValidateCursorAndFrontier(SqliteConnection connection, SqliteTransaction transaction)
    {
        string? projectorKey = null;
        long? frontier = null;
        string? updatedAt = null;
        using (var command = CreateCommand(connection, transaction, "SELECT projector_key,last_discovered_span_id,updated_at FROM local_repository_reconciliation_state LIMIT 2;"))
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                projectorKey = reader.GetString(0);
                frontier = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                updatedAt = reader.GetString(2);
            }
            if (reader.Read())
                RejectRestore();
        }

        var queueCount = ScalarLong(connection, transaction, "SELECT COUNT(*) FROM local_repository_reconciliation_queue;");
        if (projectorKey is null)
        {
            if (queueCount != 0)
                RejectRestore();
            return;
        }
        if (projectorKey != LocalRepositoryCatalogConstants.ProjectorKey
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(updatedAt))
            RejectRestore();
        if (frontier is null)
        {
            if (queueCount != 0)
                RejectRestore();
            return;
        }
        if (frontier <= 0
            || !Exists(connection, transaction, "SELECT 1 FROM monitor_spans WHERE id=$frontier;", ("$frontier", frontier.Value))
            || Exists(connection, transaction, """
                SELECT 1
                FROM local_repository_reconciliation_queue q
                WHERE NOT EXISTS(
                    SELECT 1 FROM monitor_spans s
                    WHERE s.raw_record_id=q.raw_record_id AND s.id<=$frontier)
                LIMIT 1;
                """, ("$frontier", frontier.Value))
            || Exists(connection, transaction, """
                SELECT 1
                FROM monitor_spans s
                WHERE s.id<=$frontier
                  AND NOT EXISTS(
                      SELECT 1 FROM local_repository_reconciliation_queue q
                      WHERE q.raw_record_id=s.raw_record_id
                        AND q.projector_version='local-repository-catalog:1')
                LIMIT 1;
                """, ("$frontier", frontier.Value)))
            RejectRestore();
    }

    private static void ValidatePublicationOwnership(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Exists(connection, transaction, """
            SELECT 1
            FROM session_repository_observations o
            WHERE (SELECT COUNT(*) FROM local_repository_reconciliation_queue q WHERE q.raw_record_id=o.raw_record_id)<>1
               OR NOT EXISTS(
                    SELECT 1 FROM local_repository_reconciliation_queue q
                    WHERE q.raw_record_id=o.raw_record_id
                      AND q.projector_version='local-repository-catalog:1'
                      AND q.state='completed'
                      AND q.input_evidence_kind='payload_sha256'
                      AND q.raw_payload_sha256=o.raw_payload_sha256)
            LIMIT 1;
            """)
            || Exists(connection, transaction, """
                SELECT 1
                FROM session_repository_assignment_history h
                WHERE h.cause_kind='source_reconciliation'
                  AND ((SELECT COUNT(*)
                        FROM local_repository_reconciliation_queue q
                        WHERE q.reconciliation_fingerprint=h.reconciliation_fingerprint)<>1
                       OR NOT EXISTS(
                           SELECT 1
                           FROM local_repository_reconciliation_queue q
                           WHERE q.reconciliation_fingerprint=h.reconciliation_fingerprint
                             AND q.state='completed'
                             AND q.input_evidence_kind='payload_sha256'))
                LIMIT 1;
                """))
            RejectRestore();
    }

    private static void ValidateSessionCandidateBound(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Exists(connection, transaction, """
            SELECT 1
            FROM session_repository_observation_contexts
            WHERE admission_state='admitted'
            GROUP BY session_id
            HAVING COUNT(DISTINCT repository_id)>128
            LIMIT 1;
            """))
            RejectRestore();
    }

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static bool Exists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using var command = CreateCommand(connection, transaction, sql, parameters);
        return command.ExecuteScalar() is not null;
    }

    private static long ScalarLong(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = CreateCommand(connection, transaction, sql);
        return command.ExecuteScalar() is long value ? value : throw new InvalidOperationException(RestoreInvalid);
    }

    private static void RejectRestore() => throw new InvalidOperationException(RestoreInvalid);
}

internal sealed class LocalRepositoryValidatedReconciliationState
{
    private readonly SqliteConnection connection;
    private readonly SqliteTransaction transaction;
    private readonly Action<int>? lookupObserver;

    private LocalRepositoryValidatedReconciliationState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Action<int>? lookupObserver)
    {
        this.connection = connection;
        this.transaction = transaction;
        this.lookupObserver = lookupObserver;
    }

    internal static LocalRepositoryValidatedReconciliationState ValidateAndCreate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Action<int>? queuePageObserver,
        Action<int>? lookupObserver)
    {
        SqliteLocalRepositoryReconciliationStore.ValidateRestorableStateForCapability(
            connection,
            transaction,
            queuePageObserver);
        return new LocalRepositoryValidatedReconciliationState(connection, transaction, lookupObserver);
    }

    internal bool IsBoundTo(SqliteConnection connection, SqliteTransaction transaction) =>
        ReferenceEquals(this.connection, connection) && ReferenceEquals(this.transaction, transaction);

    internal bool TryGetCompletedPayloadRawRecordId(string reconciliationFingerprint, out long rawRecordId)
    {
        if (!LocalRepositoryCatalogValidation.IsLowerSha256(reconciliationFingerprint))
            throw new InvalidOperationException("local_repository_reconciliation_restore_fingerprint_invalid");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT q.raw_record_id
            FROM local_repository_reconciliation_queue q
            WHERE q.reconciliation_fingerprint=$fingerprint
              AND q.state='completed'
              AND q.input_evidence_kind='payload_sha256'
              AND (SELECT COUNT(*)
                   FROM local_repository_reconciliation_queue owned
                   WHERE owned.reconciliation_fingerprint=q.reconciliation_fingerprint)=1
              AND EXISTS(
                  SELECT 1 FROM session_repository_assignment_history h
                  WHERE h.cause_kind='source_reconciliation'
                    AND h.reconciliation_fingerprint=q.reconciliation_fingerprint)
            ORDER BY q.queue_id COLLATE BINARY
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$fingerprint", reconciliationFingerprint);
        using var reader = command.ExecuteReader();
        var count = 0;
        var candidate = 0L;
        while (reader.Read())
        {
            count++;
            candidate = reader.GetInt64(0);
        }
        lookupObserver?.Invoke(count);
        rawRecordId = count == 1 ? candidate : 0;
        return count == 1;
    }
}

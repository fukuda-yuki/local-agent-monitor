using System.Data;
using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;

public interface IHistoricalImportCommitCheckpoint
{
    void AfterQueued(string operationId)
    {
    }

    void AfterRunning(string operationId)
    {
    }

    void BeforeCommitTransaction()
    {
    }

    void AfterCandidate(int candidateOrdinal);

    void AfterOperationPersisted()
    {
    }

    void AfterConfirmationConsumed()
    {
    }

    void AfterPreviewPurged()
    {
    }
}

internal interface IHistoricalImportLifecycleCheckpoint
{
    void BeforeMarkOperationRunning();

    void BeforeTerminalOperation();
}

public sealed class SqliteHistoricalImportStore
{
    public const int SchemaVersion = 1;
    private const string Component = "historical_import";

    private static readonly string[] OwnedTables =
    [
        "historical_import_previews",
        "historical_import_confirmation_bindings",
        "historical_import_operations",
        "historical_import_observations",
        "historical_import_observation_fields",
        "historical_import_observation_provenance",
        "historical_import_conflicts",
    ];

    private static readonly IReadOnlyDictionary<string, string> TableDefinitions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["historical_import_previews"] =
                """
                CREATE TABLE historical_import_previews(
                    preview_id TEXT PRIMARY KEY,
                    preview_digest TEXT NOT NULL,
                    snapshot_version TEXT NOT NULL,
                    snapshot_digest TEXT NOT NULL,
                    source_selection_id TEXT NOT NULL,
                    private_selection_json TEXT,
                    probe_json TEXT,
                    candidate_batch_json TEXT,
                    preview_json TEXT NOT NULL,
                    eligible INTEGER NOT NULL CHECK(eligible IN (0,1)),
                    expires_at TEXT NOT NULL,
                    created_at TEXT NOT NULL
                );
                """,
            ["historical_import_confirmation_bindings"] =
                """
                CREATE TABLE historical_import_confirmation_bindings(
                    confirmation_id TEXT PRIMARY KEY,
                    preview_id TEXT NOT NULL,
                    preview_digest TEXT NOT NULL,
                    snapshot_version TEXT NOT NULL,
                    confirmation_json TEXT NOT NULL,
                    expires_at TEXT NOT NULL,
                    consumed_operation_id TEXT,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(preview_id) REFERENCES historical_import_previews(preview_id)
                );
                """,
            ["historical_import_operations"] =
                """
                CREATE TABLE historical_import_operations(
                    operation_id TEXT PRIMARY KEY,
                    request_id TEXT NOT NULL,
                    idempotency_key_hash TEXT NOT NULL UNIQUE,
                    request_digest TEXT NOT NULL,
                    preview_id TEXT NOT NULL,
                    source_surface TEXT NOT NULL,
                    profile_id TEXT NOT NULL,
                    adapter_id TEXT NOT NULL,
                    status_json TEXT NOT NULL,
                    result_json TEXT,
                    new_observation_count INTEGER NOT NULL,
                    duplicate_count INTEGER NOT NULL,
                    conflict_count INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    completed_at TEXT,
                    FOREIGN KEY(preview_id) REFERENCES historical_import_previews(preview_id)
                );
                """,
            ["historical_import_observations"] =
                """
                CREATE TABLE historical_import_observations(
                    observation_id TEXT PRIMARY KEY,
                    identity_hash TEXT NOT NULL UNIQUE,
                    candidate_fingerprint TEXT NOT NULL,
                    source_surface TEXT NOT NULL,
                    source_tier TEXT NOT NULL,
                    profile_id TEXT NOT NULL,
                    adapter_id TEXT NOT NULL,
                    identity_resolution TEXT NOT NULL,
                    binding_basis TEXT NOT NULL,
                    binding_target_token TEXT,
                    completeness TEXT NOT NULL CHECK(completeness='partial'),
                    content_state TEXT NOT NULL CHECK(content_state='not_captured'),
                    created_operation_id TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY(created_operation_id) REFERENCES historical_import_operations(operation_id) DEFERRABLE INITIALLY DEFERRED
                );
                """,
            ["historical_import_observation_fields"] =
                """
                CREATE TABLE historical_import_observation_fields(
                    observation_id TEXT NOT NULL,
                    field_ordinal INTEGER NOT NULL,
                    field_name TEXT NOT NULL,
                    canonical_value_json TEXT NOT NULL CHECK(json_valid(canonical_value_json)),
                    PRIMARY KEY(observation_id,field_ordinal),
                    UNIQUE(observation_id,field_name),
                    FOREIGN KEY(observation_id) REFERENCES historical_import_observations(observation_id)
                );
                """,
            ["historical_import_observation_provenance"] =
                """
                CREATE TABLE historical_import_observation_provenance(
                    observation_id TEXT NOT NULL,
                    field_ordinal INTEGER NOT NULL,
                    field_name TEXT NOT NULL,
                    provenance_json TEXT NOT NULL CHECK(json_valid(provenance_json)),
                    PRIMARY KEY(observation_id,field_ordinal),
                    UNIQUE(observation_id,field_name),
                    FOREIGN KEY(observation_id) REFERENCES historical_import_observations(observation_id)
                );
                """,
            ["historical_import_conflicts"] =
                """
                CREATE TABLE historical_import_conflicts(
                    conflict_id TEXT PRIMARY KEY,
                    operation_id TEXT NOT NULL,
                    observation_id TEXT NOT NULL,
                    field_names_json TEXT NOT NULL CHECK(json_valid(field_names_json)),
                    existing_fingerprint TEXT NOT NULL,
                    incoming_fingerprint TEXT NOT NULL,
                    conflict_code TEXT NOT NULL CHECK(conflict_code='source_record_conflict'),
                    created_at TEXT NOT NULL,
                    UNIQUE(operation_id,observation_id,incoming_fingerprint),
                    FOREIGN KEY(operation_id) REFERENCES historical_import_operations(operation_id) DEFERRABLE INITIALLY DEFERRED,
                    FOREIGN KEY(observation_id) REFERENCES historical_import_observations(observation_id)
                );
                """,
        };

    private readonly Func<SqliteConnection> connectionFactory;
    private readonly IHistoricalImportCommitCheckpoint? checkpoint;

    public SqliteHistoricalImportStore(string databasePath, IHistoricalImportCommitCheckpoint? checkpoint = null)
        : this(databasePath, SqliteOpenMode.ReadWriteCreate, checkpoint)
    {
    }

    internal static SqliteHistoricalImportStore OpenExistingDatabase(
        string databasePath,
        IHistoricalImportCommitCheckpoint? checkpoint = null) =>
        new(databasePath, SqliteOpenMode.ReadWrite, checkpoint);

    internal static SqliteHistoricalImportStore OpenExistingDatabase(
        Func<SqliteConnection> connectionFactory,
        IHistoricalImportCommitCheckpoint? checkpoint = null) =>
        new(connectionFactory, checkpoint);

    private SqliteHistoricalImportStore(
        string databasePath,
        SqliteOpenMode openMode,
        IHistoricalImportCommitCheckpoint? checkpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            Mode = openMode,
        }.ToString();
        connectionFactory = () => new SqliteConnection(connectionString);
        this.checkpoint = checkpoint;
    }

    private SqliteHistoricalImportStore(
        Func<SqliteConnection> connectionFactory,
        IHistoricalImportCommitCheckpoint? checkpoint)
    {
        ArgumentNullException.ThrowIfNull(connectionFactory);
        this.connectionFactory = connectionFactory;
        this.checkpoint = checkpoint;
    }

    public void CreateSchema()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Execute(connection, transaction,
            "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");

        var version = ReadVersion(connection, transaction);
        var existingOwnedObjects = ReadOwnedObjects(connection, transaction);
        if (version is > SchemaVersion)
            throw new InvalidOperationException("The historical import schema is newer than this runtime.");
        if (version == SchemaVersion)
        {
            if (!SchemaMatches(connection, transaction, existingOwnedObjects))
                throw new InvalidOperationException("The historical import schema is partial.");
            transaction.Commit();
            return;
        }
        if (version is not null || existingOwnedObjects.Count != 0)
            throw new InvalidOperationException("The historical import schema is partial.");

        foreach (var table in OwnedTables)
            Execute(connection, transaction, TableDefinitions[table]);
        Execute(connection, transaction,
            "INSERT INTO schema_version(component,version) VALUES('historical_import',1);");
        if (!SchemaMatches(connection, transaction, ReadOwnedObjects(connection, transaction)))
            throw new InvalidOperationException("The historical import schema is partial.");
        transaction.Commit();
    }

    internal void SavePreview(HistoricalStoredPreview value)
    {
        var probe = value.Probe ?? throw new InvalidOperationException("A newly created preview requires a probe.");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO historical_import_previews(
                preview_id,preview_digest,snapshot_version,snapshot_digest,source_selection_id,
                private_selection_json,probe_json,candidate_batch_json,preview_json,eligible,expires_at,created_at)
            VALUES($id,$digest,$version,$snapshot,$selection_id,$selection,$probe,$batch,$preview,$eligible,$expires,$created);
            """;
        Add(command, "$id", value.Preview.PreviewId);
        Add(command, "$digest", value.Preview.PreviewDigest);
        Add(command, "$version", value.Preview.SnapshotVersion);
        Add(command, "$snapshot", probe.SnapshotDigest);
        Add(command, "$selection_id", value.Preview.SourceSelectionId);
        Add(command, "$selection", value.Preview.CommitAllowed ? HistoricalImportJson.SerializeString(value.Selection) : DBNull.Value);
        Add(command, "$probe", value.Preview.CommitAllowed
            ? HistoricalImportJson.SerializeString(probe with { CandidateBatch = null })
            : DBNull.Value);
        Add(command, "$batch", value.Preview.CommitAllowed && value.Batch is not null
            ? HistoricalImportJson.SerializeString(value.Batch)
            : DBNull.Value);
        Add(command, "$preview", HistoricalImportJson.SerializeString(value.Preview));
        Add(command, "$eligible", value.Preview.CommitAllowed ? 1 : 0);
        Add(command, "$expires", Format(value.ExpiresAt));
        Add(command, "$created", Format(value.CreatedAt));
        command.ExecuteNonQuery();
    }

    internal HistoricalStoredPreview? ReadStoredPreview(string previewId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT private_selection_json,probe_json,candidate_batch_json,preview_json,expires_at,created_at
            FROM historical_import_previews WHERE preview_id=$id;
            """;
        Add(command, "$id", previewId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var preview = HistoricalImportJson.Deserialize<HistoricalImportPreview>(reader.GetString(3));
        var selection = reader.IsDBNull(0)
            ? null
            : HistoricalImportJson.Deserialize<HistoricalSourceSelection>(reader.GetString(0));
        var batch = reader.IsDBNull(2)
            ? null
            : HistoricalImportJson.Deserialize<HistoricalCandidateBatch>(reader.GetString(2));
        var probe = reader.IsDBNull(1)
            ? null
            : HistoricalImportJson.Deserialize<HistoricalSourceProbe>(reader.GetString(1)) with { CandidateBatch = batch };
        return new HistoricalStoredPreview(preview, selection, probe, batch, Parse(reader.GetString(4)), Parse(reader.GetString(5)));
    }

    internal void SaveConfirmation(HistoricalImportConfirmation confirmation, DateTimeOffset expiresAt, DateTimeOffset createdAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO historical_import_confirmation_bindings(
                confirmation_id,preview_id,preview_digest,snapshot_version,confirmation_json,expires_at,created_at)
            VALUES($id,$preview,$digest,$version,$json,$expires,$created);
            """;
        Add(command, "$id", confirmation.ConfirmationId);
        Add(command, "$preview", confirmation.PreviewId);
        Add(command, "$digest", confirmation.PreviewDigest);
        Add(command, "$version", confirmation.SnapshotVersion);
        Add(command, "$json", HistoricalImportJson.SerializeString(confirmation));
        Add(command, "$expires", Format(expiresAt));
        Add(command, "$created", Format(createdAt));
        command.ExecuteNonQuery();
    }

    internal HistoricalStoredConfirmation? ReadConfirmation(string confirmationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT confirmation_json,expires_at,consumed_operation_id
            FROM historical_import_confirmation_bindings WHERE confirmation_id=$id;
            """;
        Add(command, "$id", confirmationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(
            HistoricalImportJson.Deserialize<HistoricalImportConfirmation>(reader.GetString(0)),
            Parse(reader.GetString(1)),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    internal void ClearEphemeralPreviewState(string previewId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE historical_import_previews SET private_selection_json=NULL,probe_json=NULL,candidate_batch_json=NULL WHERE preview_id=$id;";
        Add(command, "$id", previewId);
        command.ExecuteNonQuery();
    }

    internal void ClearExpiredEphemeralPreviewState(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE historical_import_previews SET private_selection_json=NULL,probe_json=NULL,candidate_batch_json=NULL WHERE expires_at<=$now AND (private_selection_json IS NOT NULL OR probe_json IS NOT NULL OR candidate_batch_json IS NOT NULL);";
        Add(command, "$now", Format(now));
        command.ExecuteNonQuery();
    }

    internal IReadOnlyList<HistoricalLivePreviewExpiry> ListLiveEphemeralPreviews(DateTimeOffset now)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT preview_id,expires_at
            FROM historical_import_previews
            WHERE eligible=1 AND expires_at>$now
              AND (private_selection_json IS NOT NULL OR probe_json IS NOT NULL OR candidate_batch_json IS NOT NULL)
            ORDER BY expires_at,preview_id;
            """;
        Add(command, "$now", Format(now));
        using var reader = command.ExecuteReader();
        var values = new List<HistoricalLivePreviewExpiry>();
        while (reader.Read()) values.Add(new(reader.GetString(0), Parse(reader.GetString(1))));
        return values;
    }

    internal HistoricalImportResult? ResolveIdempotentOperation(string idempotencyKeyHash, string requestDigest)
    {
        using var connection = Open();
        var existing = FindOperationByIdempotency(connection, null, idempotencyKeyHash);
        return existing is null ? null : ResolveExistingOperation(existing, requestDigest);
    }

    internal HistoricalQueuedOperation QueueOperation(HistoricalQueueCommand value)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var existing = FindOperationByIdempotency(connection, transaction, value.IdempotencyKeyHash);
        if (existing is not null)
        {
            var replay = ResolveExistingOperation(existing, value.RequestDigest);
            transaction.Commit();
            return new(existing.Status.OperationId, replay);
        }

        var transactionNow = value.TimeProvider.GetUtcNow();
        EnsureExactQueueBinding(connection, transaction, value, transactionNow);
        var operationId = HistoricalImportIdentifiers.New("hop_");
        var status = PendingStatus(
            operationId,
            value.Request.RequestId,
            value.TotalCandidateCount,
            running: false);
        using (var insert = Command(connection, transaction,
            """
            INSERT INTO historical_import_operations(
                operation_id,request_id,idempotency_key_hash,request_digest,preview_id,source_surface,profile_id,adapter_id,
                status_json,result_json,new_observation_count,duplicate_count,conflict_count,created_at,completed_at)
            VALUES($operation,$request,$idempotency,$digest,$preview,$surface,$profile,$adapter,$status,NULL,0,0,0,$created,NULL);
            """,
            ("$operation", operationId), ("$request", value.Request.RequestId),
            ("$idempotency", value.IdempotencyKeyHash), ("$digest", value.RequestDigest),
            ("$preview", value.Preview.PreviewId), ("$surface", value.Preview.SourceSurface),
            ("$profile", value.Preview.ProfileId), ("$adapter", value.Preview.AdapterId),
            ("$status", HistoricalImportJson.SerializeString(status)), ("$created", Format(transactionNow))))
            insert.ExecuteNonQuery();
        transaction.Commit();
        checkpoint?.AfterQueued(operationId);
        return new(operationId, ReplayResult: null);
    }

    internal void MarkOperationRunning(string operationId)
    {
        if (checkpoint is IHistoricalImportLifecycleCheckpoint lifecycleCheckpoint)
            lifecycleCheckpoint.BeforeMarkOperationRunning();
        HistoricalImportStatus running;
        using (var connection = Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var current = ReadOperationById(connection, transaction, operationId)
                ?? throw new HistoricalImportException(HistoricalImportErrorCodes.OperationNotFound);
            if (current.Status.State != "queued" || current.Status.OperationVersion != 1 || current.Result is not null)
                throw new InvalidOperationException("The historical import operation cannot enter running state.");
            running = PendingStatus(operationId, current.Status.RequestId, current.Status.Counts.Total, running: true);
            using var update = Command(connection, transaction,
                "UPDATE historical_import_operations SET status_json=$status WHERE operation_id=$operation;",
                ("$status", HistoricalImportJson.SerializeString(running)), ("$operation", operationId));
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The historical import operation was not updated.");
            transaction.Commit();
        }
        checkpoint?.AfterRunning(operationId);
    }

    internal void RejectOperation(string operationId, string failureCode, DateTimeOffset completedAt) =>
        CompleteWithoutResult(operationId, "rejected", "not_started", failureCode, completedAt);

    internal void FailOperation(string operationId, DateTimeOffset completedAt) =>
        CompleteWithoutResult(
            operationId,
            "failed",
            "rolled_back",
            HistoricalImportErrorCodes.TransactionFailed,
            completedAt);

    internal void RecoverAbandonedOperations(DateTimeOffset completedAt) =>
        RecoverAbandonedOperations(completedAt, previewId: null);

    internal void RecoverAbandonedOperation(string previewId, DateTimeOffset completedAt) =>
        RecoverAbandonedOperations(completedAt, previewId);

    private void RecoverAbandonedOperations(DateTimeOffset completedAt, string? previewId)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var read = Command(connection, transaction,
            """
            SELECT o.operation_id,o.preview_id,o.status_json
            FROM historical_import_operations AS o
            JOIN historical_import_previews AS p ON p.preview_id=o.preview_id
            WHERE o.result_json IS NULL AND o.completed_at IS NULL AND p.expires_at<=$completed
              AND ($preview IS NULL OR o.preview_id=$preview)
            ORDER BY o.created_at,o.operation_id;
            """);
        Add(read, "$completed", Format(completedAt));
        Add(read, "$preview", previewId is null ? DBNull.Value : previewId);
        using var reader = read.ExecuteReader();
        var abandoned = new List<(string OperationId, string PreviewId, HistoricalImportStatus Status)>();
        while (reader.Read())
        {
            var status = HistoricalImportJson.Deserialize<HistoricalImportStatus>(reader.GetString(2));
            if (status.State is "queued" or "running")
                abandoned.Add((reader.GetString(0), reader.GetString(1), status));
        }
        reader.Close();
        foreach (var item in abandoned)
        {
            var failed = TerminalStatus(
                item.Status.OperationId,
                item.Status.RequestId,
                item.Status.Counts.Total,
                "failed",
                "rolled_back",
                HistoricalImportErrorCodes.TransactionFailed);
            using (var update = Command(connection, transaction,
                """
                UPDATE historical_import_operations
                SET status_json=$status,result_json=NULL,new_observation_count=0,duplicate_count=0,conflict_count=0,completed_at=$completed
                WHERE operation_id=$operation AND result_json IS NULL AND completed_at IS NULL;
                """,
                ("$status", HistoricalImportJson.SerializeString(failed)), ("$completed", Format(completedAt)),
                ("$operation", item.OperationId)))
                update.ExecuteNonQuery();
            PurgePreview(connection, transaction, item.PreviewId);
        }
        transaction.Commit();
    }

    internal HistoricalCandidateDecisions ClassifyCandidates(
        HistoricalAdmissionProfile profile,
        HistoricalCandidateBatch batch)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var decisions = ClassifyCandidates(connection, transaction, profile, batch);
        transaction.Commit();
        return decisions.Summary;
    }

    internal HistoricalCommitOutcome Commit(HistoricalCommitCommand value)
    {
        checkpoint?.BeforeCommitTransaction();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        try
        {
            return CommitTransaction(value, connection, transaction);
        }
        catch (HistoricalImportException exception) when (IsDeterministicCommitRejection(exception.Code))
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HistoricalImportDomainTransactionException(exception);
        }
    }

    private HistoricalCommitOutcome CommitTransaction(
        HistoricalCommitCommand value,
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var operation = ReadOperationById(connection, transaction, value.OperationId)
            ?? throw new InvalidOperationException("The historical import operation is missing.");
        if (operation.IdempotencyKeyHash != value.IdempotencyKeyHash
            || operation.RequestDigest != value.RequestDigest
            || operation.Status.State != "running"
            || operation.Status.OperationVersion != 2
            || operation.Result is not null)
            throw new InvalidOperationException("The historical import operation is not the accepted running attempt.");

        var transactionNow = value.TimeProvider.GetUtcNow();
        using (var preview = Command(connection, transaction,
            "SELECT preview_digest,snapshot_version,eligible,expires_at,private_selection_json FROM historical_import_previews WHERE preview_id=$id;",
            ("$id", (object)value.Request.PreviewId)))
        using (var reader = preview.ExecuteReader())
        {
            if (!reader.Read()) throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewNotFound);
            if (!string.Equals(reader.GetString(0), value.Request.PreviewDigest, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), value.Request.SnapshotVersion, StringComparison.Ordinal))
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
            if (reader.GetInt32(2) != 1)
                throw new HistoricalImportException(HistoricalImportErrorCodes.NoEligibleCandidates);
            var previewExpiresAt = Parse(reader.GetString(3));
            if (previewExpiresAt != value.PreviewExpiresAt)
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
            if (previewExpiresAt <= transactionNow)
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewExpired);
            if (reader.IsDBNull(4))
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
        }

        using (var confirmation = Command(connection, transaction,
            "SELECT preview_id,preview_digest,snapshot_version,expires_at,consumed_operation_id FROM historical_import_confirmation_bindings WHERE confirmation_id=$id;",
            ("$id", (object)value.Request.ConfirmationId)))
        using (var reader = confirmation.ExecuteReader())
        {
            if (!reader.Read()) throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationInvalid);
            if (!string.Equals(reader.GetString(0), value.Request.PreviewId, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(1), value.Request.PreviewDigest, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), value.Request.SnapshotVersion, StringComparison.Ordinal))
                throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationInvalid);
            var confirmationExpiresAt = Parse(reader.GetString(3));
            if (confirmationExpiresAt != value.ConfirmationExpiresAt)
                throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationInvalid);
            if (confirmationExpiresAt <= transactionNow)
                throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationExpired);
            if (!reader.IsDBNull(4))
                throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationConsumed, reader.GetString(4));
        }

        var currentDecisions = ClassifyCandidates(connection, transaction, value.Profile, value.Batch);
        if (!string.Equals(currentDecisions.Summary.Signature, value.ExpectedDecisionSignature, StringComparison.Ordinal))
            throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);

        var operationId = value.OperationId;
        var newObservations = new List<HistoricalImportObservationResult>();
        var duplicates = new List<HistoricalImportDuplicateResult>();
        var conflicts = new List<HistoricalImportConflictResult>();
        var candidateOrdinal = 0;
        var bindings = value.CandidateBindings.ToDictionary(binding => binding.CandidateKey, StringComparer.Ordinal);

        foreach (var candidateDecision in currentDecisions.Items)
        {
            var candidate = candidateDecision.Candidate;
            var candidateFingerprint = candidateDecision.CandidateFingerprint;
            var existing = candidateDecision.Existing;
            bindings.TryGetValue(candidate.CandidateKey, out var binding);
            if (existing is null)
            {
                var observationId = HistoricalImportIdentifiers.New("hob_");
                InsertObservation(
                    connection,
                    transaction,
                    operationId,
                    observationId,
                    candidateDecision.IdentityHash,
                    candidateFingerprint,
                    value,
                    candidate,
                    binding,
                    transactionNow);
                newObservations.Add(HistoricalImportApplicationService.ObservationResult(observationId, binding));
            }
            else if (string.Equals(existing.CandidateFingerprint, candidateFingerprint, StringComparison.Ordinal))
            {
                duplicates.Add(new(
                    HistoricalImportIdentifiers.New("hrr_"),
                    existing.ObservationId,
                    candidateFingerprint,
                    "exact_duplicate_noop"));
            }
            else
            {
                var fields = FindConflictFields(connection, transaction, existing.ObservationId, candidate);
                var conflict = new HistoricalImportConflictResult(
                    HistoricalImportIdentifiers.New("hrr_"),
                    existing.ObservationId,
                    "source_record_conflict",
                    fields,
                    existing.CandidateFingerprint,
                    candidateFingerprint,
                    "preserve_existing");
                conflicts.Add(conflict);
                InsertConflict(connection, transaction, operationId, conflict, transactionNow);
            }

            checkpoint?.AfterCandidate(candidateOrdinal++);
        }

        var total = value.Batch.Candidates.Count;
        var statusCounts = new HistoricalImportStatusCounts(
            total, total, newObservations.Count, duplicates.Count, conflicts.Count, conflicts.Count);
        var status = new HistoricalImportStatus(
            HistoricalImportContractVersions.Workflow,
            HistoricalImportContractVersions.ImportStatus,
            operationId,
            value.Request.RequestId,
            3,
            "succeeded",
            ["queued", "running", "succeeded"],
            "committed",
            statusCounts,
            ResultAvailable: true,
            FailureCode: null);
        var result = new HistoricalImportResult(
            HistoricalImportContractVersions.Workflow,
            HistoricalImportContractVersions.ImportResult,
            operationId,
            value.Request.RequestId,
            value.Preview.PreviewId,
            value.Preview.PreviewDigest,
            value.Preview.SnapshotVersion,
            "succeeded",
            "committed",
            "first_application",
            "historical",
            value.Batch.SourceSurface,
            value.Batch.SourceTier,
            value.Batch.ProfileId,
            value.Batch.AdapterId,
            "production",
            new(
                HistoricalImportCount.Available(total),
                HistoricalImportCount.Available(newObservations.Count),
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Unavailable,
                HistoricalImportCount.Available(duplicates.Count),
                HistoricalImportCount.Available(conflicts.Count),
                HistoricalImportCount.Available(conflicts.Count)),
            newObservations,
            duplicates,
            conflicts,
            new("not_applicable", 0, "not_applicable", "not_applicable"));

        using (var update = Command(connection, transaction,
            """
            UPDATE historical_import_operations
            SET status_json=$status,result_json=$result,new_observation_count=$new,duplicate_count=$duplicates,
                conflict_count=$conflicts,completed_at=$completed
            WHERE operation_id=$operation AND result_json IS NULL AND completed_at IS NULL;
            """,
            ("$operation", (object)operationId),
            ("$status", HistoricalImportJson.SerializeString(status)), ("$result", HistoricalImportJson.SerializeString(result)),
            ("$new", newObservations.Count), ("$duplicates", duplicates.Count), ("$conflicts", conflicts.Count),
            ("$completed", Format(transactionNow))))
        {
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The historical import operation did not reach its terminal state.");
        }
        checkpoint?.AfterOperationPersisted();

        using (var consume = Command(connection, transaction,
            "UPDATE historical_import_confirmation_bindings SET consumed_operation_id=$operation WHERE confirmation_id=$confirmation AND consumed_operation_id IS NULL;",
            ("$operation", (object)operationId), ("$confirmation", value.Request.ConfirmationId)))
        {
            if (consume.ExecuteNonQuery() != 1)
                throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationConsumed);
        }
        checkpoint?.AfterConfirmationConsumed();

        using (var purge = Command(connection, transaction,
            "UPDATE historical_import_previews SET private_selection_json=NULL,probe_json=NULL,candidate_batch_json=NULL WHERE preview_id=$preview;",
            ("$preview", (object)value.Preview.PreviewId)))
            purge.ExecuteNonQuery();
        checkpoint?.AfterPreviewPurged();

        transaction.Commit();
        return new(result, status);
    }

    internal HistoricalImportStatus? ReadStatusOrNull(string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status_json FROM historical_import_operations WHERE operation_id=$id;";
        Add(command, "$id", operationId);
        return command.ExecuteScalar() is string json
            ? HistoricalImportJson.Deserialize<HistoricalImportStatus>(json)
            : null;
    }

    internal HistoricalImportResult? ReadResultOrNull(string operationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_json FROM historical_import_operations WHERE operation_id=$id;";
        Add(command, "$id", operationId);
        return command.ExecuteScalar() is string json
            ? HistoricalImportJson.Deserialize<HistoricalImportResult>(json)
            : null;
    }

    internal HistoricalImportHistory ListHistoryRows(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT status_json,source_surface,profile_id,adapter_id,new_observation_count,duplicate_count,conflict_count
            FROM historical_import_operations
            WHERE completed_at IS NOT NULL
            ORDER BY completed_at DESC,operation_id DESC
            LIMIT $limit;
            """;
        Add(command, "$limit", limit);
        using var reader = command.ExecuteReader();
        var items = new List<HistoricalImportHistoryItem>();
        while (reader.Read())
        {
            var status = HistoricalImportJson.Deserialize<HistoricalImportStatus>(reader.GetString(0));
            var hasCommittedObservations = status.State == "succeeded";
            items.Add(new(
                status.OperationId,
                status.State,
                status.TransactionOutcome,
                "historical",
                reader.GetString(1),
                "historical",
                "tier_b",
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetInt32(6),
                hasCommittedObservations ? "partial" : "none",
                hasCommittedObservations ? ["historical_summary_only"] : [],
                "not_captured",
                "not_applicable"));
        }
        return new(HistoricalImportContractVersions.Workflow, HistoricalImportContractVersions.ImportHistory, items);
    }

    internal HistoricalObservationList ListObservationRows(int limit, string? cursor)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT observation_id,source_surface,source_tier,binding_basis FROM historical_import_observations
            ORDER BY created_at DESC,observation_id;
            """;
        using var reader = command.ExecuteReader();
        var all = new List<HistoricalObservationListItem>();
        while (reader.Read())
            all.Add(new(reader.GetString(0), "historical", reader.GetString(1), "historical", reader.GetString(2),
                "partial", ["historical_summary_only"], HistoricalImportApplicationService.MissingCapabilitiesFor(reader.GetString(3)),
                "not_captured", TraceControlsEnabled: false));

        var start = 0;
        if (cursor is not null)
        {
            var cursorIndex = all.FindIndex(item => CursorFor(item.ObservationId) == cursor);
            if (cursorIndex < 0)
                throw new HistoricalImportException(HistoricalImportErrorCodes.RequestInvalid);
            start = cursorIndex + 1;
        }

        var items = all.Skip(start).Take(limit).ToArray();
        var nextCursor = start + items.Length < all.Count && items.Length > 0
            ? CursorFor(items[^1].ObservationId)
            : null;
        return new(HistoricalImportContractVersions.Workflow, HistoricalImportContractVersions.ObservationList, "historical", items, nextCursor);
    }

    internal HistoricalObservationDetail? ReadObservationDetail(string observationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT source_surface,source_tier,profile_id,adapter_id,identity_resolution,binding_basis
            FROM historical_import_observations WHERE observation_id=$id;
            """;
        Add(command, "$id", observationId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var surface = reader.GetString(0);
        var tier = reader.GetString(1);
        var profile = reader.GetString(2);
        var adapter = reader.GetString(3);
        var identity = reader.GetString(4);
        var basis = reader.GetString(5);
        reader.Close();
        using var fields = connection.CreateCommand();
        fields.CommandText = "SELECT field_name FROM historical_import_observation_fields WHERE observation_id=$id ORDER BY field_ordinal;";
        Add(fields, "$id", observationId);
        using var fieldReader = fields.ExecuteReader();
        var names = new List<string>();
        while (fieldReader.Read()) names.Add(fieldReader.GetString(0));
        return new(
            HistoricalImportContractVersions.Workflow,
            HistoricalImportContractVersions.ObservationDetail,
            observationId,
            "historical",
            surface,
            "historical",
            tier,
            profile,
            adapter,
            identity,
            basis,
            "partial",
            ["historical_summary_only"],
            HistoricalImportApplicationService.MissingCapabilitiesFor(basis),
            "not_captured",
            names,
            TraceControlsEnabled: false,
            "not_applicable");
    }

    private static HistoricalStoredObservation? FindObservation(SqliteConnection connection, SqliteTransaction transaction, string identityHash)
    {
        using var command = Command(connection, transaction,
            "SELECT observation_id,candidate_fingerprint FROM historical_import_observations WHERE identity_hash=$identity;",
            ("$identity", identityHash));
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1)) : null;
    }

    private static CandidateDecisionSet ClassifyCandidates(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistoricalAdmissionProfile profile,
        HistoricalCandidateBatch batch)
    {
        var newCount = 0;
        var duplicateCount = 0;
        var conflictCount = 0;
        var newCandidateKeys = new HashSet<string>(StringComparer.Ordinal);
        var signatureParts = new List<string>(batch.Candidates.Count);
        var items = new List<CandidateDecision>(batch.Candidates.Count);
        foreach (var candidate in batch.Candidates)
        {
            var identityHash = HistoricalImportApplicationService.CandidateIdentity(profile, batch, candidate);
            var candidateFingerprint = HistoricalImportApplicationService.CandidateFingerprint(batch, candidate);
            var existing = FindObservation(connection, transaction, identityHash);
            var decision = existing is null
                ? "new"
                : existing.CandidateFingerprint == candidateFingerprint
                    ? "duplicate"
                    : "conflict";
            if (decision == "new")
            {
                newCount++;
                newCandidateKeys.Add(candidate.CandidateKey);
            }
            else if (decision == "duplicate") duplicateCount++;
            else conflictCount++;
            signatureParts.Add(Frame(
                candidate.CandidateKey,
                identityHash,
                candidateFingerprint,
                existing?.ObservationId ?? string.Empty,
                existing?.CandidateFingerprint ?? string.Empty,
                decision));
            items.Add(new(candidate, identityHash, candidateFingerprint, existing));
        }

        var signature = HistoricalImportIdentifiers.Digest(Frame(signatureParts.ToArray()));
        return new(
            new HistoricalCandidateDecisions(newCount, duplicateCount, conflictCount, signature, newCandidateKeys),
            items);
    }

    private static void InsertObservation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        string observationId,
        string identityHash,
        string candidateFingerprint,
        HistoricalCommitCommand value,
        HistoricalCandidate candidate,
        HistoricalCandidateBinding? binding,
        DateTimeOffset createdAt)
    {
        var bindingBasis = binding?.Basis ?? "none";
        var resolution = binding is null ? "distinct_unbound" : "attached_exact";
        var candidateFields = HistoricalImportApplicationService.FlattenCandidate(candidate);
        using (var command = Command(connection, transaction,
            """
            INSERT INTO historical_import_observations(
                observation_id,identity_hash,candidate_fingerprint,source_surface,source_tier,profile_id,adapter_id,
                identity_resolution,binding_basis,binding_target_token,completeness,content_state,created_operation_id,created_at)
            VALUES($id,$identity,$fingerprint,$surface,$tier,$profile,$adapter,$resolution,$basis,$target,'partial','not_captured',$operation,$created);
            """,
            ("$id", observationId), ("$identity", identityHash), ("$fingerprint", candidateFingerprint),
            ("$surface", value.Batch.SourceSurface), ("$tier", value.Batch.SourceTier),
            ("$profile", value.Batch.ProfileId), ("$adapter", value.Batch.AdapterId),
            ("$resolution", resolution), ("$basis", bindingBasis),
            ("$target", binding?.TargetToken ?? (object)DBNull.Value),
            ("$operation", operationId), ("$created", Format(createdAt))))
            command.ExecuteNonQuery();

        for (var index = 0; index < candidateFields.Count; index++)
        {
            using var field = Command(connection, transaction,
                "INSERT INTO historical_import_observation_fields(observation_id,field_ordinal,field_name,canonical_value_json) VALUES($id,$ordinal,$name,$value);",
                ("$id", observationId), ("$ordinal", index), ("$name", candidateFields[index].Field),
                ("$value", candidateFields[index].CanonicalJson));
            field.ExecuteNonQuery();
            using var provenance = Command(connection, transaction,
                "INSERT INTO historical_import_observation_provenance(observation_id,field_ordinal,field_name,provenance_json) VALUES($id,$ordinal,$name,$value);",
                ("$id", observationId), ("$ordinal", index), ("$name", candidate.FieldProvenance[index].Field),
                ("$value", HistoricalImportJson.SerializeString(candidate.FieldProvenance[index])));
            provenance.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<string> FindConflictFields(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string observationId,
        HistoricalCandidate candidate)
    {
        using var command = Command(connection, transaction,
            """
            SELECT f.field_name,f.canonical_value_json,p.provenance_json
            FROM historical_import_observation_fields AS f
            JOIN historical_import_observation_provenance AS p
              ON p.observation_id=f.observation_id AND p.field_ordinal=f.field_ordinal AND p.field_name=f.field_name
            WHERE f.observation_id=$id;
            """,
            ("$id", observationId));
        using var reader = command.ExecuteReader();
        var existing = new Dictionary<string, (string Value, string Provenance)>(StringComparer.Ordinal);
        while (reader.Read()) existing.Add(reader.GetString(0), (reader.GetString(1), reader.GetString(2)));
        var fields = HistoricalImportApplicationService.FlattenCandidate(candidate);
        var incoming = fields
            .Select((field, index) => new
            {
                field.Field,
                field.CanonicalJson,
                Provenance = HistoricalImportJson.SerializeString(candidate.FieldProvenance[index]),
            })
            .ToArray();
        return incoming
            .Where(field => !existing.TryGetValue(field.Field, out var current)
                || !string.Equals(current.Value, field.CanonicalJson, StringComparison.Ordinal)
                || !string.Equals(current.Provenance, field.Provenance, StringComparison.Ordinal))
            .Select(field => field.Field)
            .Concat(existing.Keys.Where(field => incoming.All(value => value.Field != field)))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(field => Array.IndexOf(HistoricalImportApplicationService.FieldOrder, field))
            .ToArray();
    }

    private static void InsertConflict(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId,
        HistoricalImportConflictResult conflict,
        DateTimeOffset createdAt)
    {
        using var command = Command(connection, transaction,
            """
            INSERT INTO historical_import_conflicts(
                conflict_id,operation_id,observation_id,field_names_json,existing_fingerprint,incoming_fingerprint,conflict_code,created_at)
            VALUES($id,$operation,$observation,$fields,$existing,$incoming,'source_record_conflict',$created);
            """,
            ("$id", HistoricalImportIdentifiers.New("hcf_")), ("$operation", operationId),
            ("$observation", conflict.ObservationId), ("$fields", HistoricalImportJson.SerializeString(conflict.ConflictFields)),
            ("$existing", conflict.ExistingFingerprint), ("$incoming", conflict.IncomingFingerprint),
            ("$created", Format(createdAt)));
        command.ExecuteNonQuery();
    }

    private static HistoricalStoredOperation? FindOperationByIdempotency(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string idempotencyKeyHash)
    {
        using var command = Command(connection, transaction,
            "SELECT operation_id,idempotency_key_hash,request_digest,status_json,result_json,preview_id FROM historical_import_operations WHERE idempotency_key_hash=$key;",
            ("$key", idempotencyKeyHash));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? ReadStoredOperation(reader)
            : null;
    }

    private static HistoricalStoredOperation? ReadOperationById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string operationId)
    {
        using var command = Command(connection, transaction,
            "SELECT operation_id,idempotency_key_hash,request_digest,status_json,result_json,preview_id FROM historical_import_operations WHERE operation_id=$operation;",
            ("$operation", operationId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStoredOperation(reader) : null;
    }

    private static HistoricalStoredOperation ReadStoredOperation(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        HistoricalImportJson.Deserialize<HistoricalImportStatus>(reader.GetString(3)),
        reader.IsDBNull(4) ? null : HistoricalImportJson.Deserialize<HistoricalImportResult>(reader.GetString(4)),
        reader.GetString(5));

    private static HistoricalImportResult ResolveExistingOperation(
        HistoricalStoredOperation existing,
        string requestDigest)
    {
        if (!string.Equals(existing.RequestDigest, requestDigest, StringComparison.Ordinal))
            throw new HistoricalImportException(HistoricalImportErrorCodes.IdempotencyConflict);
        if (existing.Result is not null)
            return existing.Result with { IdempotencyOutcome = "replayed" };
        throw new HistoricalImportException(
            existing.Status.FailureCode ?? HistoricalImportErrorCodes.ResultNotAvailable,
            existing.OperationId);
    }

    private static void EnsureExactQueueBinding(
        SqliteConnection connection,
        SqliteTransaction transaction,
        HistoricalQueueCommand value,
        DateTimeOffset transactionNow)
    {
        var request = value.Request;
        var expectedPreview = value.Preview;
        using (var preview = Command(connection, transaction,
            "SELECT preview_digest,snapshot_version,eligible,expires_at,private_selection_json,probe_json,candidate_batch_json FROM historical_import_previews WHERE preview_id=$preview;",
            ("$preview", request.PreviewId)))
        using (var reader = preview.ExecuteReader())
        {
            if (!reader.Read())
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewNotFound);
            if (reader.GetString(0) != request.PreviewDigest
                || reader.GetString(1) != request.SnapshotVersion
                || expectedPreview.PreviewId != request.PreviewId
                || expectedPreview.PreviewDigest != request.PreviewDigest
                || expectedPreview.SnapshotVersion != request.SnapshotVersion)
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
            if (reader.GetInt32(2) != 1)
                throw new HistoricalImportException(HistoricalImportErrorCodes.NoEligibleCandidates);
            var expiresAt = Parse(reader.GetString(3));
            if (expiresAt != value.PreviewExpiresAt)
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
            if (expiresAt <= transactionNow)
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewExpired);
            if (reader.IsDBNull(4) || reader.IsDBNull(5) || reader.IsDBNull(6))
                throw new HistoricalImportException(HistoricalImportErrorCodes.PreviewStale);
        }

        using var confirmation = Command(connection, transaction,
            "SELECT preview_id,preview_digest,snapshot_version,expires_at,consumed_operation_id FROM historical_import_confirmation_bindings WHERE confirmation_id=$confirmation;",
            ("$confirmation", request.ConfirmationId));
        using var confirmationReader = confirmation.ExecuteReader();
        if (!confirmationReader.Read()
            || confirmationReader.GetString(0) != request.PreviewId
            || confirmationReader.GetString(1) != request.PreviewDigest
            || confirmationReader.GetString(2) != request.SnapshotVersion)
            throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationInvalid);
        var confirmationExpiresAt = Parse(confirmationReader.GetString(3));
        if (confirmationExpiresAt != value.ConfirmationExpiresAt)
            throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationInvalid);
        if (confirmationExpiresAt <= transactionNow)
            throw new HistoricalImportException(HistoricalImportErrorCodes.ConfirmationExpired);
        if (!confirmationReader.IsDBNull(4))
            throw new HistoricalImportException(
                HistoricalImportErrorCodes.ConfirmationConsumed,
                confirmationReader.GetString(4));
    }

    private static bool IsDeterministicCommitRejection(string code) => code is
        HistoricalImportErrorCodes.PreviewNotFound or
        HistoricalImportErrorCodes.PreviewExpired or
        HistoricalImportErrorCodes.PreviewStale or
        HistoricalImportErrorCodes.NoEligibleCandidates or
        HistoricalImportErrorCodes.ConfirmationInvalid or
        HistoricalImportErrorCodes.ConfirmationExpired or
        HistoricalImportErrorCodes.ConfirmationConsumed;

    private void CompleteWithoutResult(
        string operationId,
        string state,
        string transactionOutcome,
        string failureCode,
        DateTimeOffset completedAt)
    {
        if (!HistoricalImportErrorCodes.All.Contains(failureCode))
            throw new ArgumentOutOfRangeException(nameof(failureCode));
        if (checkpoint is IHistoricalImportLifecycleCheckpoint lifecycleCheckpoint)
            lifecycleCheckpoint.BeforeTerminalOperation();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = ReadOperationById(connection, transaction, operationId)
            ?? throw new HistoricalImportException(HistoricalImportErrorCodes.OperationNotFound);
        if (current.Status.State is "succeeded" or "failed" or "rejected")
        {
            transaction.Commit();
            return;
        }
        var terminal = TerminalStatus(
            operationId,
            current.Status.RequestId,
            current.Status.Counts.Total,
            state,
            transactionOutcome,
            failureCode);
        using (var update = Command(connection, transaction,
            """
            UPDATE historical_import_operations
            SET status_json=$status,result_json=NULL,new_observation_count=0,duplicate_count=0,conflict_count=0,completed_at=$completed
            WHERE operation_id=$operation AND result_json IS NULL AND completed_at IS NULL;
            """,
            ("$status", HistoricalImportJson.SerializeString(terminal)), ("$completed", Format(completedAt)),
            ("$operation", operationId)))
        {
            if (update.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("The historical import operation did not reach its terminal state.");
        }
        PurgePreview(connection, transaction, current.PreviewId);
        transaction.Commit();
    }

    private static void PurgePreview(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string previewId)
    {
        using var purge = Command(connection, transaction,
            "UPDATE historical_import_previews SET private_selection_json=NULL,probe_json=NULL,candidate_batch_json=NULL WHERE preview_id=$preview;",
            ("$preview", previewId));
        purge.ExecuteNonQuery();
    }

    private static HistoricalImportStatus PendingStatus(
        string operationId,
        string requestId,
        int total,
        bool running) => new(
        HistoricalImportContractVersions.Workflow,
        HistoricalImportContractVersions.ImportStatus,
        operationId,
        requestId,
        running ? 2 : 1,
        running ? "running" : "queued",
        running ? ["queued", "running"] : ["queued"],
        "pending",
        new(total, 0, 0, 0, 0, 0),
        ResultAvailable: false,
        FailureCode: null);

    private static HistoricalImportStatus TerminalStatus(
        string operationId,
        string requestId,
        int total,
        string state,
        string transactionOutcome,
        string failureCode) => new(
        HistoricalImportContractVersions.Workflow,
        HistoricalImportContractVersions.ImportStatus,
        operationId,
        requestId,
        3,
        state,
        ["queued", "running", state],
        transactionOutcome,
        new(total, 0, 0, 0, 0, 0),
        ResultAvailable: false,
        failureCode);

    private SqliteConnection Open()
    {
        var connection = connectionFactory()
            ?? throw new InvalidOperationException("The historical import connection factory returned null.");
        try
        {
            if (connection.State == ConnectionState.Closed)
                connection.Open();
            else if (connection.State != ConnectionState.Open)
                throw new InvalidOperationException("The historical import connection is not open or closed.");
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static long? ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT version FROM schema_version WHERE component='historical_import';");
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ReadOwnedObjects(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction,
            """
            SELECT type || ':' || name
            FROM sqlite_schema
            WHERE (name GLOB 'historical_import_*' OR tbl_name GLOB 'historical_import_*')
              AND name NOT GLOB 'sqlite_autoindex_*'
            ORDER BY type,name;
            """);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static bool SchemaMatches(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<string> ownedObjects)
    {
        var expectedObjects = OwnedTables.Select(table => $"table:{table}").Order(StringComparer.Ordinal).ToArray();
        if (!ownedObjects.Order(StringComparer.Ordinal).SequenceEqual(expectedObjects, StringComparer.Ordinal))
            return false;
        foreach (var table in OwnedTables)
        {
            using var command = Command(connection, transaction,
                "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name;", ("$name", table));
            if (command.ExecuteScalar() is not string sql
                || NormalizeSql(sql) != NormalizeSql(TableDefinitions[table]))
                return false;
        }
        return true;
    }

    private static string NormalizeSql(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters) Add(command, parameter.Name, parameter.Value);
        return command;
    }

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string CursorFor(string observationId) =>
        "hoc_" + HistoricalImportIdentifiers.Digest("copilot-agent-observability/historical-import-cursor/v1" + observationId)[7..39];

    private static string Frame(params string[] values)
    {
        var output = new StringBuilder();
        foreach (var value in values)
            output.Append(Encoding.UTF8.GetByteCount(value)).Append(':').Append(value);
        return output.ToString();
    }

    private sealed record CandidateDecision(
        HistoricalCandidate Candidate,
        string IdentityHash,
        string CandidateFingerprint,
        HistoricalStoredObservation? Existing);

    private sealed record CandidateDecisionSet(
        HistoricalCandidateDecisions Summary,
        IReadOnlyList<CandidateDecision> Items);
}

internal sealed record HistoricalStoredPreview(
    HistoricalImportPreview Preview,
    HistoricalSourceSelection? Selection,
    HistoricalSourceProbe? Probe,
    HistoricalCandidateBatch? Batch,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);

internal sealed record HistoricalStoredConfirmation(
    HistoricalImportConfirmation Confirmation,
    DateTimeOffset ExpiresAt,
    string? ConsumedOperationId);

internal sealed record HistoricalLivePreviewExpiry(string PreviewId, DateTimeOffset ExpiresAt);

internal sealed record HistoricalStoredObservation(string ObservationId, string CandidateFingerprint);

internal sealed record HistoricalQueueCommand(
    HistoricalImportCommitRequest Request,
    HistoricalImportPreview Preview,
    int TotalCandidateCount,
    string IdempotencyKeyHash,
    string RequestDigest,
    DateTimeOffset PreviewExpiresAt,
    DateTimeOffset ConfirmationExpiresAt,
    TimeProvider TimeProvider);

internal sealed record HistoricalQueuedOperation(string OperationId, HistoricalImportResult? ReplayResult);

internal sealed record HistoricalCommitCommand(
    string OperationId,
    HistoricalImportCommitRequest Request,
    HistoricalImportPreview Preview,
    HistoricalCandidateBatch Batch,
    IReadOnlyList<HistoricalCandidateBinding> CandidateBindings,
    HistoricalAdmissionProfile Profile,
    string ExpectedDecisionSignature,
    string IdempotencyKeyHash,
    string RequestDigest,
    DateTimeOffset PreviewExpiresAt,
    DateTimeOffset ConfirmationExpiresAt,
    TimeProvider TimeProvider);

internal sealed record HistoricalCommitOutcome(HistoricalImportResult Result, HistoricalImportStatus Status);

internal sealed class HistoricalImportDomainTransactionException(Exception innerException)
    : Exception("The historical import domain transaction failed.", innerException);

internal sealed record HistoricalStoredOperation(
    string OperationId,
    string IdempotencyKeyHash,
    string RequestDigest,
    HistoricalImportStatus Status,
    HistoricalImportResult? Result,
    string PreviewId);

internal sealed record HistoricalCandidateDecisions(
    int NewCount,
    int DuplicateCount,
    int ConflictCount,
    string Signature,
    IReadOnlySet<string> NewCandidateKeys);

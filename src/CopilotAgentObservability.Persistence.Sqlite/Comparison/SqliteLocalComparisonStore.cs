using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SqliteLocalComparisonStore
{
    private const int MaximumMembershipRows = 200;
    private const int MaximumMembershipFactBytes = 1_048_576;
    private const int MaximumResultRows = 1_048_576 / 32;
    private const int MaximumResultPayloadBytes = 2 * 1_048_576;
    private const int MaximumEvidenceRows = 1_048_576 / 64;
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly Func<SqliteConnection>? connectionFactory;

    internal SqliteLocalComparisonStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        Func<SqliteConnection>? connectionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.connectionFactory = connectionFactory;
    }

    internal LocalComparisonAcceptStatus Accept(
        LocalComparisonSnapshotWrite snapshot,
        CancellationToken cancellationToken)
    {
        LocalComparisonSnapshotValidation.Validate(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            LocalComparisonSchemaV1.Validate(connection, transaction);
            var existing = ReadSnapshotById(
                connection,
                transaction,
                snapshot.ComparisonId,
                cancellationToken);
            if (existing is not null)
            {
                if (!LocalComparisonSnapshotValidation.Identical(snapshot, existing))
                    throw new InvalidOperationException("local_comparison_insert_mismatch");
                transaction.Commit();
                return LocalComparisonAcceptStatus.Identical;
            }
            if (TombstoneExists(connection, transaction, snapshot.ComparisonId))
                throw new InvalidOperationException("local_comparison_insert_after_expiry");

            EnableDeferredForeignKeys(connection, transaction);
            foreach (var membership in snapshot.Memberships)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InsertMembership(connection, transaction, membership);
            }
            foreach (var result in snapshot.Results)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InsertResult(connection, transaction, result);
            }
            foreach (var evidence in snapshot.Evidence)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InsertEvidence(connection, transaction, evidence);
            }
            InsertSnapshot(connection, transaction, snapshot);
            LocalComparisonSchemaV1.Validate(connection, transaction);
            transaction.Commit();
            return LocalComparisonAcceptStatus.Accepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return LocalComparisonAcceptStatus.PersistenceBusy;
        }
    }

    internal LocalComparisonReadResult Read(
        string repositoryId,
        string comparisonId,
        CancellationToken cancellationToken)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(comparisonId))
        {
            throw new ArgumentException("local_comparison_read_invalid");
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            LocalComparisonSchemaV1.Validate(connection, transaction);
            var tombstoneRepository = ReadTombstoneRepository(
                connection,
                transaction,
                comparisonId);
            if (tombstoneRepository is not null)
            {
                transaction.Commit();
                return new(
                    string.Equals(tombstoneRepository, repositoryId, StringComparison.Ordinal)
                        ? LocalComparisonReadStatus.Expired
                        : LocalComparisonReadStatus.NotFound,
                    Snapshot: null);
            }

            var snapshot = ReadSnapshotByPair(
                connection,
                transaction,
                repositoryId,
                comparisonId,
                cancellationToken);
            if (snapshot is null)
            {
                transaction.Commit();
                return new(LocalComparisonReadStatus.NotFound, Snapshot: null);
            }
            if (timeProvider.GetUtcNow() >= snapshot.ExpiresAt)
            {
                transaction.Commit();
                return new(LocalComparisonReadStatus.Expired, Snapshot: null);
            }
            transaction.Commit();
            return new(LocalComparisonReadStatus.Found, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(LocalComparisonReadStatus.PersistenceBusy, Snapshot: null);
        }
    }

    internal LocalComparisonCleanupResult CleanupExpired(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            LocalComparisonSchemaV1.Validate(connection, transaction);
            var now = Timestamp(timeProvider.GetUtcNow());
            var expired = ReadExpired(connection, transaction, now, cancellationToken);
            if (expired.Count == 0)
            {
                transaction.Commit();
                return new(LocalComparisonCleanupStatus.Completed, CleanedCount: 0);
            }

            LocalComparisonSchemaV1.DropOperationalDeleteGuards(connection, transaction);
            var cleanedCount = 0;
            while (expired.Count > 0)
            {
                foreach (var item in expired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    InsertOrValidateTombstone(connection, transaction, item);
                    DeleteOperationalRows(connection, transaction, item.ComparisonId);
                }
                cleanedCount = checked(cleanedCount + expired.Count);
                expired = ReadExpired(connection, transaction, now, cancellationToken);
            }
            LocalComparisonSchemaV1.RestoreOperationalDeleteGuards(connection, transaction);
            LocalComparisonSchemaV1.Validate(connection, transaction);
            transaction.Commit();
            return new(LocalComparisonCleanupStatus.Completed, cleanedCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(LocalComparisonCleanupStatus.PersistenceBusy, CleanedCount: 0);
        }
    }

    private SqliteConnection Open()
    {
        var connection = connectionFactory?.Invoke() ?? new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static void EnableDeferredForeignKeys(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using (var enable = connection.CreateCommand())
        {
            enable.Transaction = transaction;
            enable.CommandText = "PRAGMA defer_foreign_keys=ON;";
            enable.ExecuteNonQuery();
        }
        using var verify = connection.CreateCommand();
        verify.Transaction = transaction;
        verify.CommandText = "PRAGMA defer_foreign_keys;";
        if (Convert.ToInt64(verify.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("local_comparison_foreign_key_defer_failed");
    }

    private static LocalComparisonFrozenSnapshot? ReadSnapshotById(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId,
        CancellationToken cancellationToken) =>
        ReadSnapshot(connection, transaction, comparisonId, repositoryId: null, cancellationToken);

    internal static LocalComparisonFrozenSnapshot ReadSnapshotForValidation(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string comparisonId)
    {
        if (transaction is null)
        {
            using var owned = connection.BeginTransaction(deferred: true);
            var result = ReadSnapshot(
                connection, owned, comparisonId, repositoryId: null, CancellationToken.None)
                ?? throw new InvalidOperationException("local_comparison_snapshot_missing");
            owned.Commit();
            return result;
        }
        return ReadSnapshot(
            connection, transaction, comparisonId, repositoryId: null, CancellationToken.None)
            ?? throw new InvalidOperationException("local_comparison_snapshot_missing");
    }

    private static LocalComparisonFrozenSnapshot? ReadSnapshotByPair(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string repositoryId,
        string comparisonId,
        CancellationToken cancellationToken) =>
        ReadSnapshot(connection, transaction, comparisonId, repositoryId, cancellationToken);

    private static LocalComparisonFrozenSnapshot? ReadSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId,
        string? repositoryId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT comparison_id,repository_id,created_at,expires_at,
                   selection_frame,selection_sha256,scope_condition_sha256
            FROM local_comparison_snapshots
            WHERE comparison_id=$comparison
              AND ($repository IS NULL OR repository_id=$repository);
            """;
        command.Parameters.AddWithValue("$comparison", comparisonId);
        command.Parameters.Add("$repository", SqliteType.Text).Value =
            repositoryId is null ? DBNull.Value : repositoryId;
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        var id = reader.GetString(0);
        var repository = reader.GetString(1);
        var created = ParseTimestamp(reader.GetString(2));
        var expires = ParseTimestamp(reader.GetString(3));
        var selection = (byte[])reader.GetValue(4);
        var selectionHash = reader.GetString(5);
        var scopeConditionSha256 = (byte[])reader.GetValue(6);
        if (reader.Read())
            throw new InvalidOperationException("local_comparison_snapshot_duplicate");
        reader.Close();

        var memberships = ReadMemberships(connection, transaction, id, cancellationToken);
        var results = ReadResults(connection, transaction, id, cancellationToken);
        var evidence = ReadEvidence(connection, transaction, id, cancellationToken);
        var snapshot = new LocalComparisonFrozenSnapshot(
            id,
            repository,
            created,
            expires,
            selection.ToArray(),
            selectionHash,
            scopeConditionSha256.ToArray(),
            memberships,
            results,
            evidence);
        LocalComparisonSnapshotValidation.Validate(snapshot);
        return snapshot;
    }

    private static IReadOnlyList<LocalComparisonStoredMembership> ReadMemberships(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT comparison_id,cohort,ordinal,session_id,workspace_revision,fact_frame,fact_sha256
            FROM local_comparison_cohort_memberships
            WHERE comparison_id=$comparison
            ORDER BY cohort COLLATE BINARY,ordinal;
            """;
        command.Parameters.AddWithValue("$comparison", comparisonId);
        using var reader = command.ExecuteReader();
        var values = new List<LocalComparisonStoredMembership>();
        var factBytes = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (values.Count == MaximumMembershipRows)
                throw new InvalidOperationException("local_comparison_snapshot_invalid");
            var factFrame = ((byte[])reader.GetValue(5)).ToArray();
            if (factFrame.Length > MaximumMembershipFactBytes - factBytes)
                throw new InvalidOperationException("local_comparison_snapshot_invalid");
            factBytes += factFrame.Length;
            values.Add(new(
                reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4),
                factFrame, reader.GetString(6)));
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyList<LocalComparisonStoredResult> ReadResults(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT comparison_id,result_ordinal,section_ordinal,row_kind,row_key,payload,payload_sha256
            FROM local_comparison_results
            WHERE comparison_id=$comparison
            ORDER BY result_ordinal;
            """;
        command.Parameters.AddWithValue("$comparison", comparisonId);
        using var reader = command.ExecuteReader();
        var values = new List<LocalComparisonStoredResult>();
        var payloadBytes = 0;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (values.Count == MaximumResultRows)
                throw new InvalidOperationException("local_comparison_snapshot_invalid");
            var payload = ((byte[])reader.GetValue(5)).ToArray();
            if (payload.Length > MaximumResultPayloadBytes - payloadBytes)
                throw new InvalidOperationException("local_comparison_snapshot_invalid");
            payloadBytes += payload.Length;
            values.Add(LocalComparisonStoredResult.Read(
                reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4),
                payload, reader.GetString(6)));
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyList<LocalComparisonStoredEvidence> ReadEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT comparison_id,result_ordinal,evidence_ordinal,field_key,cohort,session_id,
                   availability_state,source_kind,source_identity,trace_id,span_id,event_id,revision_sha256
            FROM local_comparison_evidence
            WHERE comparison_id=$comparison
            ORDER BY result_ordinal,evidence_ordinal;
            """;
        command.Parameters.AddWithValue("$comparison", comparisonId);
        using var reader = command.ExecuteReader();
        var values = new List<LocalComparisonStoredEvidence>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (values.Count == MaximumEvidenceRows)
                throw new InvalidOperationException("local_comparison_snapshot_invalid");
            values.Add(new(
                reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                Nullable(reader, 7), Nullable(reader, 8), Nullable(reader, 9),
                Nullable(reader, 10), Nullable(reader, 11), Nullable(reader, 12)));
        }
        return Array.AsReadOnly(values.ToArray());
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void InsertSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalComparisonSnapshotWrite snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_comparison_snapshots(
              comparison_id,repository_id,created_at,expires_at,selection_frame,
              selection_sha256,scope_condition_sha256)
            VALUES($comparison,$repository,$created,$expires,$selection,
                   $selection_hash,$scope_condition_sha256);
            """;
        command.Parameters.AddWithValue("$comparison", snapshot.ComparisonId);
        command.Parameters.AddWithValue("$repository", snapshot.RepositoryId);
        command.Parameters.AddWithValue("$created", Timestamp(snapshot.CreatedAt));
        command.Parameters.AddWithValue("$expires", Timestamp(snapshot.ExpiresAt));
        command.Parameters.Add("$selection", SqliteType.Blob).Value = snapshot.SelectionFrame;
        command.Parameters.AddWithValue("$selection_hash", snapshot.SelectionSha256);
        command.Parameters.Add("$scope_condition_sha256", SqliteType.Blob).Value =
            snapshot.ScopeConditionSha256;
        command.ExecuteNonQuery();
    }

    private static void InsertMembership(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalComparisonStoredMembership item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_comparison_cohort_memberships(
              comparison_id,cohort,ordinal,session_id,workspace_revision,fact_frame,fact_sha256)
            VALUES($comparison,$cohort,$ordinal,$session,$revision,$frame,$hash);
            """;
        command.Parameters.AddWithValue("$comparison", item.ComparisonId);
        command.Parameters.AddWithValue("$cohort", item.Cohort);
        command.Parameters.AddWithValue("$ordinal", item.Ordinal);
        command.Parameters.AddWithValue("$session", item.SessionId);
        command.Parameters.AddWithValue("$revision", item.WorkspaceRevision);
        command.Parameters.Add("$frame", SqliteType.Blob).Value = item.FactFrame;
        command.Parameters.AddWithValue("$hash", item.FactSha256);
        command.ExecuteNonQuery();
    }

    private static void InsertResult(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalComparisonStoredResult item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_comparison_results(
              comparison_id,result_ordinal,section_ordinal,row_kind,row_key,payload,payload_sha256)
            VALUES($comparison,$ordinal,$section,$kind,$key,$payload,$hash);
            """;
        command.Parameters.AddWithValue("$comparison", item.ComparisonId);
        command.Parameters.AddWithValue("$ordinal", item.ResultOrdinal);
        command.Parameters.AddWithValue("$section", item.SectionOrdinal);
        command.Parameters.AddWithValue("$kind", item.RowKind);
        command.Parameters.AddWithValue("$key", item.RowKey);
        command.Parameters.Add("$payload", SqliteType.Blob).Value = item.Payload;
        command.Parameters.AddWithValue("$hash", item.PayloadSha256);
        command.ExecuteNonQuery();
    }

    private static void InsertEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalComparisonStoredEvidence item)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO local_comparison_evidence(
              comparison_id,result_ordinal,evidence_ordinal,field_key,cohort,session_id,
              availability_state,source_kind,source_identity,trace_id,span_id,event_id,revision_sha256)
            VALUES($comparison,$result,$ordinal,$field,$cohort,$session,$state,$kind,$identity,
                   $trace,$span,$event,$revision);
            """;
        command.Parameters.AddWithValue("$comparison", item.ComparisonId);
        command.Parameters.AddWithValue("$result", item.ResultOrdinal);
        command.Parameters.AddWithValue("$ordinal", item.EvidenceOrdinal);
        command.Parameters.AddWithValue("$field", item.FieldKey);
        command.Parameters.AddWithValue("$cohort", item.Cohort);
        command.Parameters.AddWithValue("$session", item.SessionId);
        command.Parameters.AddWithValue("$state", item.AvailabilityState);
        AddNullable(command, "$kind", item.SourceKind);
        AddNullable(command, "$identity", item.SourceIdentity);
        AddNullable(command, "$trace", item.TraceId);
        AddNullable(command, "$span", item.SpanId);
        AddNullable(command, "$event", item.EventId);
        AddNullable(command, "$revision", item.RevisionSha256);
        command.ExecuteNonQuery();
    }

    private static void AddNullable(SqliteCommand command, string name, string? value) =>
        command.Parameters.Add(name, SqliteType.Text).Value = value is null ? DBNull.Value : value;

    private static bool TombstoneExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId) =>
        ReadTombstoneRepository(connection, transaction, comparisonId) is not null;

    private static string? ReadTombstoneRepository(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT repository_id FROM local_comparison_expiry_tombstones
            WHERE comparison_id=$comparison;
            """;
        command.Parameters.AddWithValue("$comparison", comparisonId);
        return command.ExecuteScalar() as string;
    }

    private static IReadOnlyList<ExpiredIdentity> ReadExpired(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string now,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT comparison_id,repository_id,expires_at
            FROM local_comparison_snapshots
            WHERE expires_at<=$now
            ORDER BY expires_at,comparison_id COLLATE BINARY
            LIMIT 256;
            """;
        command.Parameters.AddWithValue("$now", now);
        using var reader = command.ExecuteReader();
        var values = new List<ExpiredIdentity>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            values.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }
        return values;
    }

    private static void InsertOrValidateTombstone(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ExpiredIdentity item)
    {
        using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = """
                SELECT repository_id,expired_at
                FROM local_comparison_expiry_tombstones
                WHERE comparison_id=$comparison;
                """;
            existing.Parameters.AddWithValue("$comparison", item.ComparisonId);
            using var reader = existing.ExecuteReader();
            if (reader.Read())
            {
                var identical = reader.GetString(0) == item.RepositoryId
                    && reader.GetString(1) == item.ExpiredAt
                    && !reader.Read();
                if (!identical)
                    throw new InvalidOperationException("local_comparison_tombstone_mismatch");
                return;
            }
        }
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO local_comparison_expiry_tombstones(
                  comparison_id,repository_id,expired_at)
                VALUES($comparison,$repository,$expired);
                """;
            insert.Parameters.AddWithValue("$comparison", item.ComparisonId);
            insert.Parameters.AddWithValue("$repository", item.RepositoryId);
            insert.Parameters.AddWithValue("$expired", item.ExpiredAt);
            insert.ExecuteNonQuery();
        }
        using var validate = connection.CreateCommand();
        validate.Transaction = transaction;
        validate.CommandText = """
            SELECT COUNT(*) FROM local_comparison_expiry_tombstones
            WHERE comparison_id=$comparison AND repository_id=$repository AND expired_at=$expired;
            """;
        validate.Parameters.AddWithValue("$comparison", item.ComparisonId);
        validate.Parameters.AddWithValue("$repository", item.RepositoryId);
        validate.Parameters.AddWithValue("$expired", item.ExpiredAt);
        if (Convert.ToInt64(validate.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("local_comparison_tombstone_mismatch");
    }

    private static void DeleteOperationalRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string comparisonId)
    {
        foreach (var table in new[]
        {
            "local_comparison_evidence",
            "local_comparison_results",
            "local_comparison_cohort_memberships",
            "local_comparison_snapshots",
        })
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {table} WHERE comparison_id=$comparison;";
            command.Parameters.AddWithValue("$comparison", comparisonId);
            command.ExecuteNonQuery();
        }
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result)
            || result.Offset != TimeSpan.Zero
            || result.ToString("O", CultureInfo.InvariantCulture) != value)
        {
            throw new InvalidOperationException("local_comparison_timestamp_invalid");
        }
        return result;
    }

    private sealed record ExpiredIdentity(
        string ComparisonId,
        string RepositoryId,
        string ExpiredAt);
}

internal static class LocalComparisonSnapshotValidation
{
    private const int MaximumMembershipRows = 200;
    private const int MaximumMembershipFactBytes = 1_048_576;
    private const int MaximumResultRows = 1_048_576 / 32;
    private const int MaximumResultPayloadBytes = 2 * 1_048_576;
    private const int MaximumEvidenceRows = 1_048_576 / 64;

    internal static void Validate(LocalComparisonSnapshotWrite snapshot) =>
        ValidateCore(
            snapshot.ComparisonId, snapshot.RepositoryId, snapshot.CreatedAt, snapshot.ExpiresAt,
            snapshot.SelectionFrame, snapshot.SelectionSha256, snapshot.ScopeConditionSha256,
            snapshot.Memberships, snapshot.Results, snapshot.Evidence);

    internal static void Validate(LocalComparisonFrozenSnapshot snapshot) =>
        ValidateCore(
            snapshot.ComparisonId, snapshot.RepositoryId, snapshot.CreatedAt, snapshot.ExpiresAt,
            snapshot.SelectionFrame, snapshot.SelectionSha256, snapshot.ScopeConditionSha256,
            snapshot.Memberships, snapshot.Results, snapshot.Evidence);

    private static void ValidateCore(
        string comparisonId,
        string repositoryId,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        byte[] selectionFrame,
        string selectionSha256,
        byte[] scopeConditionSha256,
        IReadOnlyList<LocalComparisonStoredMembership> memberships,
        IReadOnlyList<LocalComparisonStoredResult> results,
        IReadOnlyList<LocalComparisonStoredEvidence> evidence)
    {
        ArgumentNullException.ThrowIfNull(selectionFrame);
        ArgumentNullException.ThrowIfNull(scopeConditionSha256);
        ArgumentNullException.ThrowIfNull(memberships);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(comparisonId)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
            || createdAt.Offset != TimeSpan.Zero
            || expiresAt.Offset != TimeSpan.Zero
            || expiresAt - createdAt != TimeSpan.FromHours(24)
            || selectionFrame.Length is < 1 or > 16_384
            || scopeConditionSha256.Length != 32
            || !Matches(selectionFrame, selectionSha256))
        {
            Reject();
        }

        if (memberships.Count is < 2 or > MaximumMembershipRows
            || memberships.Sum(static item => (long)item.FactFrame.Length) > MaximumMembershipFactBytes
            || results.Count is < 1 or > MaximumResultRows
            || results.Sum(static item => (long)item.Payload.Length) > MaximumResultPayloadBytes
            || evidence.Count > MaximumEvidenceRows)
            Reject();
        var ordered = memberships
            .OrderBy(static item => item.Cohort, StringComparer.Ordinal)
            .ThenBy(static item => item.Ordinal)
            .ToArray();
        if (!memberships.SequenceEqual(ordered))
            Reject();
        var seenSessions = new HashSet<string>(StringComparer.Ordinal);
        var decoded = new Dictionary<string, IReadOnlyList<LocalComparisonSessionFact>>(StringComparer.Ordinal);
        foreach (var cohort in new[] { "a", "b" })
        {
            var items = memberships.Where(item => item.Cohort == cohort).ToArray();
            if (items.Length == 0)
                Reject();
            var facts = new LocalComparisonSessionFact[items.Length];
            for (var index = 0; index < items.Length; index++)
            {
                var item = items[index];
                if (item.ComparisonId != comparisonId
                    || item.Ordinal != index
                    || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(item.SessionId)
                    || !seenSessions.Add(item.SessionId)
                    || !IsHash(item.WorkspaceRevision)
                    || item.FactFrame.Length is < 1 or > 1_048_576
                    || !Matches(item.FactFrame, item.FactSha256))
                {
                    Reject();
                }
                try
                {
                    facts[index] = LocalComparisonFactFrame.Decode(item.FactFrame);
                }
                catch (InvalidOperationException)
                {
                    Reject();
                }
                if (facts[index].SessionId != item.SessionId
                    || facts[index].RepositoryId != repositoryId
                    || facts[index].WorkspaceRevision != item.WorkspaceRevision)
                {
                    Reject();
                }
            }
            decoded.Add(cohort, Array.AsReadOnly(facts));
        }
        var expectedSelection = LocalComparisonSelectionFrame.Create(
            decoded["a"].Select(static item => item.SessionId).ToArray(),
            decoded["b"].Select(static item => item.SessionId).ToArray());
        if (!selectionFrame.SequenceEqual(expectedSelection.Bytes)
            || selectionSha256 != expectedSelection.Sha256)
        {
            Reject();
        }
        int excludedA;
        int excludedB;
        try
        {
            if (results[2].ResultOrdinal != 2
                || results[2].SectionOrdinal != 1
                || results[2].RowKind != "scalar"
                || results[2].RowKey != "excluded_session_count")
            {
                Reject();
            }
            var values = LocalComparisonResultPayloadCodec.Decode(
                results[2].Payload, 1, "scalar", "excluded_session_count");
            excludedA = ReadNonNegativeCount(values, "a_count");
            excludedB = ReadNonNegativeCount(values, "b_count");
        }
        catch (InvalidOperationException)
        {
            Reject();
            return;
        }
        LocalComparisonSnapshotWrite expected;
        try
        {
            var canonicalInput = LocalComparisonApplicationValidation.Freeze(new(
                repositoryId,
                new(decoded["a"], excludedA),
                new(decoded["b"], excludedB),
                scopeConditionSha256.ToArray()));
            expected = LocalComparisonApplicationService.BuildSnapshot(
                comparisonId,
                createdAt,
                canonicalInput);
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or OverflowException
            or LocalComparisonTooLargeException)
        {
            Reject();
            return;
        }
        if (expected.ExpiresAt != expiresAt
            || !expected.SelectionFrame.SequenceEqual(selectionFrame)
            || expected.SelectionSha256 != selectionSha256
            || !expected.ScopeConditionSha256.SequenceEqual(scopeConditionSha256)
            || !Sequence(expected.Memberships, memberships, MembershipEqual)
            || !Sequence(expected.Results, results, ResultEqual)
            || !expected.Evidence.SequenceEqual(evidence))
        {
            Reject();
        }
    }

    private static int ReadNonNegativeCount(
        IReadOnlyList<KeyValuePair<string, string>> values,
        string key)
    {
        var matches = values.Where(item => item.Key == key).ToArray();
        if (matches.Length != 1
            || !int.TryParse(
                matches[0].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var result)
            || result is < 0 or > 1_000_000
            || result.ToString(CultureInfo.InvariantCulture) != matches[0].Value)
        {
            throw new InvalidOperationException("local_comparison_snapshot_invalid");
        }
        return result;
    }

    internal static bool Identical(
        LocalComparisonSnapshotWrite expected,
        LocalComparisonFrozenSnapshot actual) =>
        expected.ComparisonId == actual.ComparisonId
        && expected.RepositoryId == actual.RepositoryId
        && expected.CreatedAt == actual.CreatedAt
        && expected.ExpiresAt == actual.ExpiresAt
        && expected.SelectionFrame.SequenceEqual(actual.SelectionFrame)
        && expected.SelectionSha256 == actual.SelectionSha256
        && expected.ScopeConditionSha256.SequenceEqual(actual.ScopeConditionSha256)
        && Sequence(expected.Memberships, actual.Memberships, MembershipEqual)
        && Sequence(expected.Results, actual.Results, ResultEqual)
        && expected.Evidence.SequenceEqual(actual.Evidence);

    private static bool MembershipEqual(
        LocalComparisonStoredMembership left,
        LocalComparisonStoredMembership right) =>
        left.ComparisonId == right.ComparisonId
        && left.Cohort == right.Cohort
        && left.Ordinal == right.Ordinal
        && left.SessionId == right.SessionId
        && left.WorkspaceRevision == right.WorkspaceRevision
        && left.FactFrame.SequenceEqual(right.FactFrame)
        && left.FactSha256 == right.FactSha256;

    private static bool ResultEqual(
        LocalComparisonStoredResult left,
        LocalComparisonStoredResult right) =>
        left.ComparisonId == right.ComparisonId
        && left.ResultOrdinal == right.ResultOrdinal
        && left.SectionOrdinal == right.SectionOrdinal
        && left.RowKind == right.RowKind
        && left.RowKey == right.RowKey
        && left.Payload.SequenceEqual(right.Payload)
        && left.PayloadSha256 == right.PayloadSha256;

    private static bool Sequence<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        Func<T, T, bool> equal) =>
        left.Count == right.Count
        && Enumerable.Range(0, left.Count).All(index => equal(left[index], right[index]));

    private static bool Matches(byte[] bytes, string hash) =>
        IsHash(hash)
        && CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(bytes),
            Convert.FromHexString(hash));

    private static bool IsHash(string value) => IsHex(value, 64);

    private static bool IsHex(string? value, int length) =>
        value is not null
        && value.Length == length
        && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static void Reject() =>
        throw new InvalidOperationException("local_comparison_snapshot_invalid");
}

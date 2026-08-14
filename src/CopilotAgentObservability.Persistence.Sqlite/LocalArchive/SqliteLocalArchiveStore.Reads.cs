using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteLocalArchiveStore
{
    internal LocalArchiveReadResult Read(
        LocalArchiveTargetKind targetKind,
        string targetId,
        CancellationToken cancellationToken)
    {
        if (!IsDefined(targetKind)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(targetId))
        {
            throw new ArgumentException("local_archive_read_invalid");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var requested = Array.AsReadOnly(new[] { targetId });
            var existing = ReadExisting(
                connection,
                transaction,
                targetKind,
                requested,
                cancellationToken);
            if (existing.Count != 1
                || !string.Equals(existing[0], targetId, StringComparison.Ordinal))
            {
                transaction.Commit();
                return ReadError(LocalArchiveStoreError.TargetNotFound);
            }

            var current = ReadCurrent(connection, transaction, targetKind, targetId, cancellationToken);
            transaction.Commit();
            return new LocalArchiveReadResult(
                current ?? new LocalArchiveMutationTargetSuccess(
                    targetId,
                    LocalArchiveState.Active,
                    0,
                    ArchivedAt: null,
                    UpdatedAt: null),
                Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return ReadError(LocalArchiveStoreError.PersistenceBusy);
        }
        catch
        {
            return ReadError(LocalArchiveStoreError.ArchiveStoreUnavailable);
        }
    }

    internal LocalArchiveListResult ListArchived(
        LocalArchiveTargetKind targetKind,
        string? afterArchivedAt,
        string? afterTargetId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!IsDefined(targetKind)
            || limit is < 1 or > 200
            || (afterArchivedAt is null) != (afterTargetId is null)
            || afterArchivedAt is not null
            && (!LocalRepositoryCatalogValidation.IsCanonicalTimestamp(afterArchivedAt)
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(afterTargetId)))
        {
            throw new ArgumentException("local_archive_list_invalid");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            var rows = ReadArchivedPage(
                connection,
                transaction,
                targetKind,
                afterArchivedAt,
                afterTargetId,
                limit + 1,
                cancellationToken);
            if (rows.Count != 0)
                ProvePageParents(connection, transaction, targetKind, rows, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();

            var hasMore = rows.Count > limit;
            var emitted = hasMore ? rows.Take(limit).ToArray() : rows.ToArray();
            return new LocalArchiveListResult(
                new LocalArchiveListSuccess(Array.AsReadOnly(emitted), hasMore),
                Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return ListError(LocalArchiveStoreError.PersistenceBusy);
        }
        catch
        {
            return ListError(LocalArchiveStoreError.ArchiveStoreUnavailable);
        }
    }

    private IReadOnlyList<string> ReadExisting(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveTargetKind targetKind,
        IReadOnlyList<string> targetIds,
        CancellationToken cancellationToken) =>
        targetKind == LocalArchiveTargetKind.Session
            ? sessionExistence.ReadExisting(connection, transaction, targetIds, cancellationToken)
            : repositoryExistence.ReadExisting(connection, transaction, targetIds, cancellationToken);

    private static LocalArchiveMutationTargetSuccess? ReadCurrent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveTargetKind targetKind,
        string targetId,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CurrentSelect +
            " WHERE c.target_kind=$target_kind AND c.target_id=$target_id;";
        command.Parameters.Add("$target_kind", SqliteType.Text).Value = KindText(targetKind);
        command.Parameters.Add("$target_id", SqliteType.Text).Value = targetId;
        using var reader = command.ExecuteReader();
        cancellationToken.ThrowIfCancellationRequested();
        if (!reader.Read())
        {
            reader.Close();
            return HasAnyEvent(connection, transaction, targetKind, targetId, cancellationToken)
                ? throw StoreUnavailable()
                : null;
        }
        var current = ReadValidatedCurrent(reader, targetKind);
        if (reader.Read())
            throw StoreUnavailable();
        cancellationToken.ThrowIfCancellationRequested();
        return current;
    }

    private static bool HasAnyEvent(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveTargetKind targetKind,
        string targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
              SELECT 1
              FROM local_archive_events
              WHERE target_kind=$target_kind AND target_id=$target_id);
            """;
        command.Parameters.Add("$target_kind", SqliteType.Text).Value = KindText(targetKind);
        command.Parameters.Add("$target_id", SqliteType.Text).Value = targetId;
        var exists = Convert.ToInt64(
            command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
        cancellationToken.ThrowIfCancellationRequested();
        return exists;
    }

    private static IReadOnlyList<LocalArchiveMutationTargetSuccess> ReadArchivedPage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveTargetKind targetKind,
        string? afterArchivedAt,
        string? afterTargetId,
        int take,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CurrentSelect + """
             WHERE c.target_kind=$target_kind
               AND c.state='archived'
               AND ($after_archived_at IS NULL
                 OR c.archived_at < $after_archived_at
                 OR (c.archived_at = $after_archived_at AND c.target_id < $after_target_id))
             ORDER BY c.archived_at DESC, c.target_id DESC
             LIMIT $take;
            """;
        command.Parameters.Add("$target_kind", SqliteType.Text).Value = KindText(targetKind);
        command.Parameters.Add("$after_archived_at", SqliteType.Text).Value =
            (object?)afterArchivedAt ?? DBNull.Value;
        command.Parameters.Add("$after_target_id", SqliteType.Text).Value =
            (object?)afterTargetId ?? DBNull.Value;
        command.Parameters.Add("$take", SqliteType.Integer).Value = take;

        using var reader = command.ExecuteReader();
        var rows = new List<LocalArchiveMutationTargetSuccess>(take);
        LocalArchiveMutationTargetSuccess? previous = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadValidatedCurrent(reader, targetKind);
            if (previous is not null
                && (StringComparer.Ordinal.Compare(previous.ArchivedAt, current.ArchivedAt) < 0
                    || string.Equals(previous.ArchivedAt, current.ArchivedAt, StringComparison.Ordinal)
                    && StringComparer.Ordinal.Compare(previous.TargetId, current.TargetId) <= 0))
            {
                throw StoreUnavailable();
            }
            rows.Add(current);
            previous = current;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }

    private void ProvePageParents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveTargetKind targetKind,
        IReadOnlyList<LocalArchiveMutationTargetSuccess> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(row => row.TargetId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw StoreUnavailable();

        var proven = new List<string>(ids.Length);
        for (var offset = 0; offset < ids.Length; offset += 200)
        {
            var chunk = Array.AsReadOnly(ids.Skip(offset).Take(200).ToArray());
            proven.AddRange(ReadExisting(connection, transaction, targetKind, chunk, cancellationToken));
        }
        if (!ids.SequenceEqual(proven.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || proven.Count != ids.Length)
        {
            throw StoreUnavailable();
        }
    }

    private static LocalArchiveMutationTargetSuccess ReadValidatedCurrent(
        SqliteDataReader reader,
        LocalArchiveTargetKind targetKind)
    {
        if (reader.FieldCount != 20
            || reader.GetValue(0) is not string rowKind
            || reader.GetValue(1) is not string targetId
            || reader.GetValue(2) is not string stateText
            || reader.GetValue(3) is not long revision
            || reader.GetValue(4) is not (string or DBNull)
            || reader.GetValue(5) is not string updatedAt
            || reader.GetValue(6) is not string eventId
            || reader.GetValue(7) is not string eventKind
            || reader.GetValue(8) is not string eventTargetId
            || reader.GetValue(9) is not string actionText
            || reader.GetValue(10) is not long previousRevision
            || reader.GetValue(11) is not long newRevision
            || reader.GetValue(12) is not string occurredAt
            || reader.GetString(13) != "text"
            || reader.GetString(14) != "text"
            || reader.GetString(15) != "text"
            || reader.GetString(16) != "integer"
            || reader.GetString(17) is not ("text" or "null")
            || reader.GetString(18) != "text"
            || reader.GetString(19) != "text")
        {
            throw StoreUnavailable();
        }

        var state = stateText switch
        {
            "active" => LocalArchiveState.Active,
            "archived" => LocalArchiveState.Archived,
            _ => throw StoreUnavailable(),
        };
        var action = actionText switch
        {
            "archive" => LocalArchiveAction.Archive,
            "restore" => LocalArchiveAction.Restore,
            _ => throw StoreUnavailable(),
        };
        var expectedKind = KindText(targetKind);
        var current = new LocalArchiveMutationTargetSuccess(
            targetId,
            state,
            revision,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            updatedAt);
        var head = new LocalArchiveStoredEvent(
            eventId,
            targetKind,
            eventTargetId,
            action,
            previousRevision,
            newRevision,
            occurredAt);
        if (!string.Equals(rowKind, expectedKind, StringComparison.Ordinal)
            || !string.Equals(eventKind, expectedKind, StringComparison.Ordinal)
            || !LocalArchiveValidation.IsValidCurrentAndHead(targetKind, current, head))
        {
            throw StoreUnavailable();
        }
        return current;
    }

    private const string CurrentSelect = """
        SELECT c.target_kind,c.target_id,c.state,c.revision,c.archived_at,c.updated_at,
               e.event_id,e.target_kind,e.target_id,e.action,e.previous_revision,e.new_revision,e.occurred_at,
               typeof(c.target_kind),typeof(c.target_id),typeof(c.state),typeof(c.revision),
               typeof(c.archived_at),typeof(c.updated_at),typeof(e.event_id)
        FROM local_archive_current AS c
        LEFT JOIN local_archive_events AS e
          ON e.target_kind=c.target_kind
         AND e.target_id=c.target_id
         AND e.new_revision=(
           SELECT MAX(head.new_revision)
           FROM local_archive_events AS head
           WHERE head.target_kind=c.target_kind AND head.target_id=c.target_id)
        """;

    private static string KindText(LocalArchiveTargetKind targetKind) =>
        targetKind == LocalArchiveTargetKind.Session ? "session" : "repository";

    private static InvalidOperationException StoreUnavailable() =>
        new("local_archive_store_unavailable");
}

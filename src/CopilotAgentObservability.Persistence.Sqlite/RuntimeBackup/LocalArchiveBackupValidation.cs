using System.Data;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal static class LocalArchiveBackupValidation
{
    private const int ParentPageSize = 200;
    private const string InvalidState = "local_archive_backup_invalid";

    internal static void Validate(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveSessionTargetExistenceAuthority sessionExistence,
        ILocalRepositoryTargetExistenceAuthority repositoryExistence)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(sessionExistence);
        ArgumentNullException.ThrowIfNull(repositoryExistence);

        if (connection.State != ConnectionState.Open
            || !ReferenceEquals(transaction.Connection, connection))
        {
            Reject();
        }

        try
        {
            LocalArchiveSchemaV1.Validate(connection, transaction, allowLegacyRepositoryCatalog: true);
            RejectOrphans(connection, transaction);
            ValidateChains(connection, transaction);
            ValidateParents(
                connection,
                transaction,
                "session",
                (ids) => sessionExistence.ReadExisting(
                    connection,
                    transaction,
                    ids,
                    CancellationToken.None));
            ValidateParents(
                connection,
                transaction,
                "repository",
                (ids) => repositoryExistence.ReadExisting(
                    connection,
                    transaction,
                    ids,
                    CancellationToken.None));
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or ArgumentException or SqliteException
            && exception.Message != InvalidState)
        {
            throw new InvalidOperationException(InvalidState, exception);
        }
    }

    private static void RejectOrphans(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
                EXISTS(
                    SELECT 1
                    FROM local_archive_current current
                    WHERE NOT EXISTS(
                        SELECT 1
                        FROM local_archive_events event
                        WHERE event.target_kind=current.target_kind
                          AND event.target_id=current.target_id)
                    LIMIT 1),
                EXISTS(
                    SELECT 1
                    FROM local_archive_events event
                    WHERE NOT EXISTS(
                        SELECT 1
                        FROM local_archive_current current
                        WHERE current.target_kind=event.target_kind
                          AND current.target_id=event.target_id)
                    LIMIT 1);
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.FieldCount != 2
            || reader.GetValue(0) is not long currentWithoutEvent
            || reader.GetValue(1) is not long eventWithoutCurrent
            || currentWithoutEvent != 0
            || eventWithoutCurrent != 0
            || reader.Read())
        {
            Reject();
        }
    }

    private static void ValidateChains(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT current.target_kind,current.target_id,current.state,current.revision,
                   current.archived_at,current.updated_at,
                   event.event_id,event.target_kind,event.target_id,event.action,
                   event.previous_revision,event.new_revision,event.occurred_at
            FROM local_archive_events event
            JOIN local_archive_current current
              ON current.target_kind=event.target_kind
             AND current.target_id=event.target_id
            ORDER BY event.target_kind COLLATE BINARY,
                     event.target_id COLLATE BINARY,
                     event.new_revision;
            """;
        using var reader = command.ExecuteReader();

        CurrentRow currentChain = default;
        var hasCurrentChain = false;
        long expectedRevision = 0;
        string? headAction = null;
        string? headOccurredAt = null;
        while (reader.Read())
        {
            if (reader.FieldCount != 13)
                Reject();
            var current = ReadCurrent(reader);
            var eventRow = ReadEvent(reader);
            if (!hasCurrentChain
                || !string.Equals(currentChain.TargetKind, current.TargetKind, StringComparison.Ordinal)
                || !string.Equals(currentChain.TargetId, current.TargetId, StringComparison.Ordinal))
            {
                if (hasCurrentChain)
                    ValidateHead(currentChain, expectedRevision, headAction!, headOccurredAt!);
                currentChain = current;
                hasCurrentChain = true;
                expectedRevision = 0;
            }
            else if (current != currentChain)
            {
                Reject();
            }

            if (!string.Equals(eventRow.TargetKind, current.TargetKind, StringComparison.Ordinal)
                || !string.Equals(eventRow.TargetId, current.TargetId, StringComparison.Ordinal)
                || eventRow.PreviousRevision != expectedRevision
                || eventRow.NewRevision != expectedRevision + 1
                || eventRow.Action != (eventRow.NewRevision % 2 == 1 ? "archive" : "restore"))
            {
                Reject();
            }

            expectedRevision = eventRow.NewRevision;
            headAction = eventRow.Action;
            headOccurredAt = eventRow.OccurredAt;
        }

        if (hasCurrentChain)
            ValidateHead(currentChain, expectedRevision, headAction!, headOccurredAt!);
    }

    private static CurrentRow ReadCurrent(SqliteDataReader reader)
    {
        var targetKind = ExactText(reader, 0);
        var targetId = ExactText(reader, 1);
        var state = ExactText(reader, 2);
        var revision = ExactInteger(reader, 3);
        var archivedAt = NullableExactText(reader, 4);
        var updatedAt = ExactText(reader, 5);
        if (targetKind is not ("session" or "repository")
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(targetId)
            || state is not ("active" or "archived")
            || revision < 1
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(updatedAt)
            || archivedAt is not null
            && !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(archivedAt))
        {
            Reject();
        }
        return new(targetKind, targetId, state, revision, archivedAt, updatedAt);
    }

    private static EventRow ReadEvent(SqliteDataReader reader)
    {
        var eventId = ExactText(reader, 6);
        var targetKind = ExactText(reader, 7);
        var targetId = ExactText(reader, 8);
        var action = ExactText(reader, 9);
        var previousRevision = ExactInteger(reader, 10);
        var newRevision = ExactInteger(reader, 11);
        var occurredAt = ExactText(reader, 12);
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(eventId)
            || targetKind is not ("session" or "repository")
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(targetId)
            || action is not ("archive" or "restore")
            || previousRevision < 0
            || newRevision < 1
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(occurredAt))
        {
            Reject();
        }
        return new(targetKind, targetId, action, previousRevision, newRevision, occurredAt);
    }

    private static void ValidateHead(
        CurrentRow current,
        long headRevision,
        string headAction,
        string headOccurredAt)
    {
        if (headRevision != current.Revision
            || !string.Equals(current.UpdatedAt, headOccurredAt, StringComparison.Ordinal))
        {
            Reject();
        }

        var valid = current.State switch
        {
            "active" => current.Revision % 2 == 0
                && current.ArchivedAt is null
                && headAction == "restore",
            "archived" => current.Revision % 2 == 1
                && headAction == "archive"
                && string.Equals(current.ArchivedAt, headOccurredAt, StringComparison.Ordinal),
            _ => false,
        };
        if (!valid)
            Reject();
    }

    private static void ValidateParents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetKind,
        Func<IReadOnlyList<string>, IReadOnlyList<string>> readExisting)
    {
        var after = string.Empty;
        while (true)
        {
            var page = new List<string>(ParentPageSize);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT target_id
                    FROM local_archive_current
                    WHERE target_kind=$kind
                      AND target_id COLLATE BINARY>$after
                    ORDER BY target_id COLLATE BINARY
                    LIMIT 200;
                    """;
                command.Parameters.AddWithValue("$kind", targetKind);
                command.Parameters.AddWithValue("$after", after);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.FieldCount != 1)
                        Reject();
                    var id = ExactText(reader, 0);
                    if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(id)
                        || page.Count > 0
                        && StringComparer.Ordinal.Compare(page[^1], id) >= 0)
                    {
                        Reject();
                    }
                    page.Add(id);
                }
            }

            if (page.Count == 0)
                return;
            var existingPage = readExisting(page) ?? throw Invalid();
            if (existingPage.Count != page.Count)
                Reject();
            for (var index = 0; index < page.Count; index++)
            {
                if (!string.Equals(existingPage[index], page[index], StringComparison.Ordinal))
                    Reject();
            }
            after = page[^1];
        }
    }

    private static string ExactText(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is string value ? value : throw Invalid();

    private static string? NullableExactText(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) switch
        {
            DBNull => null,
            string value => value,
            _ => throw Invalid(),
        };

    private static long ExactInteger(SqliteDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is long value ? value : throw Invalid();

    private static InvalidOperationException Invalid() => new(InvalidState);

    [DoesNotReturn]
    private static void Reject() => throw Invalid();

    private readonly record struct CurrentRow(
        string TargetKind,
        string TargetId,
        string State,
        long Revision,
        string? ArchivedAt,
        string UpdatedAt);

    private readonly record struct EventRow(
        string TargetKind,
        string TargetId,
        string Action,
        long PreviousRevision,
        long NewRevision,
        string OccurredAt);
}

using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class LocalArchiveSessionTargetExistenceAuthority
{
    internal static LocalArchiveSessionTargetExistenceAuthority Instance { get; } = new();

    private LocalArchiveSessionTargetExistenceAuthority()
    {
    }

    internal IReadOnlyList<string> ReadExisting(
        SqliteConnection openConnection,
        SqliteTransaction exactTransaction,
        IReadOnlyList<string> canonicalSessionIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ArgumentNullException.ThrowIfNull(exactTransaction);
        ArgumentNullException.ThrowIfNull(canonicalSessionIds);

        if (openConnection.State != ConnectionState.Open
            || exactTransaction.Connection is not { } transactionConnection
            || !ReferenceEquals(transactionConnection, openConnection))
        {
            throw new InvalidOperationException(
                "local_archive_session_target_existence_transaction_invalid");
        }

        var count = canonicalSessionIds.Count;
        if (count is < 1 or > 200)
            throw InvalidInput();

        var frozenSessionIds = new string[count];
        for (var index = 0; index < count; index++)
            frozenSessionIds[index] = canonicalSessionIds[index];

        for (var index = 0; index < count; index++)
        {
            var sessionId = frozenSessionIds[index];
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
                || index > 0
                && StringComparer.Ordinal.Compare(frozenSessionIds[index - 1], sessionId) >= 0)
            {
                throw InvalidInput();
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var command = openConnection.CreateCommand();
        command.Transaction = exactTransaction;
        command.CommandText = CommandText(count);
        for (var index = 0; index < count; index++)
        {
            command.Parameters.Add(ParameterName(index), SqliteType.Text).Value =
                frozenSessionIds[index];
        }

        using var reader = command.ExecuteReader();
        if (reader.FieldCount != 2)
            throw InvalidResult();
        var existing = new List<string>(count);
        string? previous = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.GetValue(0) is not string sessionId
                || reader.GetValue(1) is not string storageType
                || storageType != "text"
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(sessionId)
                || Array.BinarySearch(frozenSessionIds, sessionId, StringComparer.Ordinal) < 0
                || previous is not null
                && StringComparer.Ordinal.Compare(previous, sessionId) >= 0)
            {
                throw InvalidResult();
            }
            existing.Add(sessionId);
            previous = sessionId;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(existing.ToArray());
    }

    private static string CommandText(int count)
    {
        var sql = new StringBuilder(
            "SELECT session_id, typeof(session_id)\n" +
            "FROM sessions\n" +
            "WHERE session_id IN (");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                sql.Append(", ");
            sql.Append(ParameterName(index));
        }
        return sql.Append(")\nORDER BY session_id COLLATE BINARY;").ToString();
    }

    private static string ParameterName(int index) => $"$session_id_{index:D3}";

    private static ArgumentException InvalidInput() =>
        new("local_archive_session_target_ids_invalid", "canonicalSessionIds");

    private static InvalidOperationException InvalidResult() =>
        new("local_archive_session_target_existence_result_invalid");
}

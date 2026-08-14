using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SqliteLocalArchiveFactSnapshotContributor : ILocalArchiveFactSnapshotContributor
{
    private const int ChunkSize = 200;

    internal static SqliteLocalArchiveFactSnapshotContributor Instance { get; } = new();

    private SqliteLocalArchiveFactSnapshotContributor()
    {
    }

    public ValueTask<LocalArchiveFactContribution> ReadAsync(
        ILocalRepositoryReadTransaction transaction,
        LocalRepositoryArchiveInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(input);

        return transaction.ReadAsync(
            (connection, exactTransaction, token) => ValueTask.FromResult(
                Read(connection, exactTransaction, input, token)),
            cancellationToken);
    }

    private static LocalArchiveFactContribution Read(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalRepositoryArchiveInput input,
        CancellationToken cancellationToken)
    {
        if (input.SessionIds.Count == 0 && input.RepositoryIds.Count == 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteEmptyRead(connection, transaction, cancellationToken);
            return new(Array.Empty<LocalArchiveSessionFact>(), Array.Empty<LocalArchiveRepositoryFact>());
        }

        var sessions = new LocalArchiveSessionFact[input.SessionIds.Count];
        for (var start = 0; start < input.SessionIds.Count; start += ChunkSize)
        {
            var count = Math.Min(ChunkSize, input.SessionIds.Count - start);
            var rows = ReadChunk(
                connection,
                transaction,
                "session",
                input.SessionIds,
                start,
                count,
                cancellationToken);
            for (var offset = 0; offset < count; offset++)
            {
                var id = input.SessionIds[start + offset];
                var value = rows.TryGetValue(id, out var row)
                    ? row
                    : new ArchiveValue(LocalArchiveState.Active, 0);
                sessions[start + offset] = new(id, value.State, value.Revision);
            }
        }

        var repositories = new LocalArchiveRepositoryFact[input.RepositoryIds.Count];
        for (var start = 0; start < input.RepositoryIds.Count; start += ChunkSize)
        {
            var count = Math.Min(ChunkSize, input.RepositoryIds.Count - start);
            var rows = ReadChunk(
                connection,
                transaction,
                "repository",
                input.RepositoryIds,
                start,
                count,
                cancellationToken);
            for (var offset = 0; offset < count; offset++)
            {
                var id = input.RepositoryIds[start + offset];
                var value = rows.TryGetValue(id, out var row)
                    ? row
                    : new ArchiveValue(LocalArchiveState.Active, 0);
                repositories[start + offset] = new(id, value.State, value.Revision);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(sessions), Array.AsReadOnly(repositories));
    }

    private static Dictionary<string, ArchiveValue> ReadChunk(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetKind,
        IReadOnlyList<string> input,
        int start,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = CommandText(count);
        command.Parameters.Add("$target_kind", SqliteType.Text).Value = targetKind;
        var chunkIds = new string[count];
        for (var offset = 0; offset < count; offset++)
        {
            var id = input[start + offset];
            chunkIds[offset] = id;
            command.Parameters.Add(ParameterName(offset), SqliteType.Text).Value = id;
        }

        using var reader = command.ExecuteReader();
        if (reader.FieldCount != 8)
            throw InvalidResult();

        var rows = new Dictionary<string, ArchiveValue>(count, StringComparer.Ordinal);
        string? previousId = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.GetValue(0) is not string rowKind
                || reader.GetValue(1) is not string kindType
                || reader.GetValue(2) is not string targetId
                || reader.GetValue(3) is not string idType
                || reader.GetValue(4) is not string stateText
                || reader.GetValue(5) is not string stateType
                || reader.GetValue(6) is not long revision
                || reader.GetValue(7) is not string revisionType
                || kindType != "text"
                || idType != "text"
                || stateType != "text"
                || revisionType != "integer"
                || rowKind != targetKind
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(targetId)
                || Array.BinarySearch(chunkIds, targetId, StringComparer.Ordinal) < 0
                || previousId is not null && StringComparer.Ordinal.Compare(previousId, targetId) >= 0
                || !TryReadState(stateText, revision, out var state))
            {
                throw InvalidResult();
            }

            rows.Add(targetId, new(state, revision));
            previousId = targetId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return rows;
    }

    private static void ExecuteEmptyRead(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT target_kind, typeof(target_kind),\n" +
            "       target_id, typeof(target_id),\n" +
            "       state, typeof(state),\n" +
            "       revision, typeof(revision)\n" +
            "FROM local_archive_current\n" +
            "WHERE 0\n" +
            "ORDER BY target_kind COLLATE BINARY, target_id COLLATE BINARY;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            throw InvalidResult();
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static string CommandText(int count)
    {
        var sql = new StringBuilder(
            "SELECT target_kind, typeof(target_kind),\n" +
            "       target_id, typeof(target_id),\n" +
            "       state, typeof(state),\n" +
            "       revision, typeof(revision)\n" +
            "FROM local_archive_current\n" +
            "WHERE target_kind = $target_kind\n" +
            "  AND target_id IN (");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                sql.Append(", ");
            sql.Append(ParameterName(index));
        }
        return sql.Append(")\nORDER BY target_id COLLATE BINARY;").ToString();
    }

    private static string ParameterName(int index) => $"$target_id_{index:D3}";

    private static bool TryReadState(string state, long revision, out LocalArchiveState parsed)
    {
        if (state == "active" && revision >= 0 && revision % 2 == 0)
        {
            parsed = LocalArchiveState.Active;
            return true;
        }
        if (state == "archived" && revision > 0 && revision % 2 == 1)
        {
            parsed = LocalArchiveState.Archived;
            return true;
        }
        parsed = default;
        return false;
    }

    private static InvalidOperationException InvalidResult() =>
        new("local_archive_fact_contribution_invalid");

    private sealed record ArchiveValue(LocalArchiveState State, long Revision);
}

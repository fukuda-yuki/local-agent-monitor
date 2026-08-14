using System.Data;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class SqliteLocalRepositoryTargetExistenceAuthority : ILocalRepositoryTargetExistenceAuthority
{
    internal static SqliteLocalRepositoryTargetExistenceAuthority Instance { get; } = new();

    private SqliteLocalRepositoryTargetExistenceAuthority()
    {
    }

    public IReadOnlyList<string> ReadExisting(
        SqliteConnection openConnection,
        SqliteTransaction exactTransaction,
        IReadOnlyList<string> canonicalRepositoryIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(openConnection);
        ArgumentNullException.ThrowIfNull(exactTransaction);
        ArgumentNullException.ThrowIfNull(canonicalRepositoryIds);

        if (openConnection.State != ConnectionState.Open
            || exactTransaction.Connection is not { } transactionConnection
            || !ReferenceEquals(transactionConnection, openConnection))
        {
            throw new InvalidOperationException(
                "local_repository_target_existence_transaction_invalid");
        }

        var count = canonicalRepositoryIds.Count;
        if (count is < 1 or > 200)
        {
            throw new ArgumentException(
                "local_repository_target_ids_invalid",
                nameof(canonicalRepositoryIds));
        }

        var frozenRepositoryIds = new string[count];
        for (var index = 0; index < count; index++)
            frozenRepositoryIds[index] = canonicalRepositoryIds[index];

        for (var index = 0; index < count; index++)
        {
            var repositoryId = frozenRepositoryIds[index];
            if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
                || index > 0
                && StringComparer.Ordinal.Compare(frozenRepositoryIds[index - 1], repositoryId) >= 0)
            {
                throw new ArgumentException(
                    "local_repository_target_ids_invalid",
                    nameof(canonicalRepositoryIds));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        using var command = openConnection.CreateCommand();
        command.Transaction = exactTransaction;
        command.CommandText = CommandText(count);
        for (var index = 0; index < count; index++)
        {
            command.Parameters.Add(ParameterName(index), SqliteType.Text).Value =
                frozenRepositoryIds[index];
        }

        using var reader = command.ExecuteReader();
        if (reader.FieldCount != 2)
            throw InvalidResult();

        var existing = new List<string>(count);
        string? previous = null;
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.GetValue(0) is not string repositoryId
                || reader.GetValue(1) is not string storageType
                || storageType != "text"
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
                || Array.BinarySearch(frozenRepositoryIds, repositoryId, StringComparer.Ordinal) < 0
                || previous is not null
                && StringComparer.Ordinal.Compare(previous, repositoryId) >= 0)
            {
                throw InvalidResult();
            }

            existing.Add(repositoryId);
            previous = repositoryId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Array.AsReadOnly(existing.ToArray());
    }

    private static string CommandText(int count)
    {
        var sql = new StringBuilder(
            "SELECT repository_id, typeof(repository_id)\n" +
            "FROM local_repositories\n" +
            "WHERE repository_id IN (");
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                sql.Append(", ");
            sql.Append(ParameterName(index));
        }
        return sql.Append(")\nORDER BY repository_id COLLATE BINARY;").ToString();
    }

    private static string ParameterName(int index) =>
        $"$repository_id_{index:D3}";

    private static InvalidOperationException InvalidResult() =>
        new("local_repository_target_existence_result_invalid");
}

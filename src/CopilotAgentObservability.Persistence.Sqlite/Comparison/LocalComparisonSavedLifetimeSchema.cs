using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalComparisonSavedLifetimeSchema
{
    internal const string Sql = """
        CREATE TABLE local_comparison_saved_lifetimes(
          comparison_id TEXT COLLATE BINARY PRIMARY KEY NOT NULL CHECK(typeof(comparison_id)='text'),
          first_saved_at TEXT COLLATE BINARY NOT NULL CHECK(typeof(first_saved_at)='text'),
          saved_until TEXT COLLATE BINARY NOT NULL CHECK(typeof(saved_until)='text' AND saved_until>first_saved_at),
          is_saved INTEGER NOT NULL CHECK(typeof(is_saved)='integer' AND is_saved IN (0,1)),
          FOREIGN KEY(comparison_id) REFERENCES local_comparison_snapshots(comparison_id)
            ON UPDATE RESTRICT ON DELETE RESTRICT
        );
        """;

    internal static void ValidateRows(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT saved.comparison_id,saved.first_saved_at,saved.saved_until,saved.is_saved,
                   snapshot.created_at,snapshot.expires_at,
                   typeof(saved.comparison_id),typeof(saved.first_saved_at),typeof(saved.saved_until),typeof(saved.is_saved)
            FROM local_comparison_saved_lifetimes AS saved
            LEFT JOIN local_comparison_snapshots AS snapshot ON snapshot.comparison_id=saved.comparison_id;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(4) || reader.IsDBNull(5)
                || reader.GetString(6) != "text" || reader.GetString(7) != "text" || reader.GetString(8) != "text" || reader.GetString(9) != "integer"
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(reader.GetString(0))
                || !TryInstant(reader.GetString(1), out var first)
                || !TryInstant(reader.GetString(2), out var until)
                || !TryInstant(reader.GetString(4), out var created)
                || !TryInstant(reader.GetString(5), out var originalExpiry)
                || first < created || first >= originalExpiry || until - first != TimeSpan.FromDays(30)
                || reader.GetInt64(3) is not (0 or 1))
                throw new InvalidOperationException("local_comparison_saved_lifetime_invalid");
        }
    }

    private static bool TryInstant(string value, out DateTimeOffset instant) =>
        DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out instant)
        && instant.Offset == TimeSpan.Zero && instant.ToString("O", CultureInfo.InvariantCulture) == value;
}

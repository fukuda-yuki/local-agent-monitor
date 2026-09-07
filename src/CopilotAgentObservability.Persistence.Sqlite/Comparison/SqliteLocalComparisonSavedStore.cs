using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record LocalComparisonSavedLifetime(
    string ComparisonId, string RepositoryId, bool IsSaved,
    DateTimeOffset? FirstSavedAt, DateTimeOffset? SavedUntil, DateTimeOffset EffectiveExpiresAt,
    bool Available);

internal enum LocalComparisonSaveStatus { Found, NotFound, Expired, LimitReached, PersistenceBusy }
internal sealed record LocalComparisonSaveResult(LocalComparisonSaveStatus Status, LocalComparisonSavedLifetime? Lifetime = null);
internal sealed record LocalComparisonSavedListItem(string ComparisonId, string RepositoryId,
    DateTimeOffset CreatedAt, DateTimeOffset FirstSavedAt, DateTimeOffset SavedUntil, int CohortACount, int CohortBCount);
internal sealed record LocalComparisonSavedListResult(LocalComparisonSaveStatus Status, IReadOnlyList<LocalComparisonSavedListItem> Items);

internal sealed partial class SqliteLocalComparisonStore
{
    internal const int MaximumSavedComparisons = 20;

    internal LocalComparisonSaveResult SavedLifetime(string repositoryId, string comparisonId, bool? save, CancellationToken ct)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(comparisonId))
            throw new ArgumentException("local_comparison_read_invalid");
        ct.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: save is null);
            LocalComparisonSchemaV1.Validate(connection, transaction);
            var tombstone = ReadTombstoneRepository(connection, transaction, comparisonId);
            if (tombstone is not null)
                return new(tombstone == repositoryId ? LocalComparisonSaveStatus.Expired : LocalComparisonSaveStatus.NotFound);
            var snapshot = ReadSnapshotByPair(connection, transaction, repositoryId, comparisonId, ct);
            if (snapshot is null) return new(LocalComparisonSaveStatus.NotFound);
            var lifetime = ReadSavedLifetime(connection, transaction, snapshot);
            var now = timeProvider.GetUtcNow();
            if (now >= lifetime.EffectiveExpiresAt) return new(LocalComparisonSaveStatus.Expired);
            if (save is null || save == lifetime.IsSaved)
            {
                transaction.Commit();
                return new(LocalComparisonSaveStatus.Found, lifetime with { Available = true });
            }
            if (save == true)
            {
                using var count = connection.CreateCommand();
                count.Transaction = transaction;
                count.CommandText = "SELECT COUNT(*) FROM local_comparison_saved_lifetimes WHERE is_saved=1 AND saved_until>$now;";
                count.Parameters.AddWithValue("$now", Timestamp(now));
                if (Convert.ToInt64(count.ExecuteScalar()) >= MaximumSavedComparisons)
                    return new(LocalComparisonSaveStatus.LimitReached);
            }
            using var mutation = connection.CreateCommand();
            mutation.Transaction = transaction;
            mutation.CommandText = """
                INSERT INTO local_comparison_saved_lifetimes(comparison_id,first_saved_at,saved_until,is_saved)
                VALUES($comparison,$first,$until,$saved)
                ON CONFLICT(comparison_id) DO UPDATE SET is_saved=excluded.is_saved;
                """;
            mutation.Parameters.AddWithValue("$comparison", comparisonId);
            mutation.Parameters.AddWithValue("$first", Timestamp(lifetime.FirstSavedAt ?? now));
            mutation.Parameters.AddWithValue("$until", Timestamp(lifetime.SavedUntil ?? now.AddDays(30)));
            mutation.Parameters.AddWithValue("$saved", save == true ? 1 : 0);
            mutation.ExecuteNonQuery();
            lifetime = ReadSavedLifetime(connection, transaction, snapshot);
            LocalComparisonSavedLifetimeSchema.ValidateRows(connection, transaction);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return new(LocalComparisonSaveStatus.Found, lifetime with { Available = now < lifetime.EffectiveExpiresAt });
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        { return new(LocalComparisonSaveStatus.PersistenceBusy); }
    }

    internal LocalComparisonSavedListResult ListSaved(string repositoryId, CancellationToken ct)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId))
            throw new ArgumentException("local_comparison_read_invalid");
        ct.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            LocalComparisonSchemaV1.Validate(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT snapshot.comparison_id,snapshot.repository_id,snapshot.created_at,saved.first_saved_at,saved.saved_until,
                       (SELECT COUNT(*) FROM local_comparison_cohort_memberships AS m WHERE m.comparison_id=snapshot.comparison_id AND m.cohort='a'),
                       (SELECT COUNT(*) FROM local_comparison_cohort_memberships AS m WHERE m.comparison_id=snapshot.comparison_id AND m.cohort='b')
                FROM local_comparison_saved_lifetimes AS saved
                JOIN local_comparison_snapshots AS snapshot ON snapshot.comparison_id=saved.comparison_id
                WHERE snapshot.repository_id=$repository AND saved.is_saved=1 AND saved.saved_until>$now
                ORDER BY saved.first_saved_at DESC,snapshot.comparison_id COLLATE BINARY
                LIMIT 21;
                """;
            command.Parameters.AddWithValue("$repository", repositoryId);
            command.Parameters.AddWithValue("$now", Timestamp(timeProvider.GetUtcNow()));
            var items = new List<LocalComparisonSavedListItem>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (items.Count == MaximumSavedComparisons) throw new InvalidOperationException("local_comparison_saved_limit_invalid");
                    items.Add(new(reader.GetString(0), reader.GetString(1), ParseTimestamp(reader.GetString(2)),
                        ParseTimestamp(reader.GetString(3)), ParseTimestamp(reader.GetString(4)), reader.GetInt32(5), reader.GetInt32(6)));
                }
            }
            transaction.Commit();
            return new(LocalComparisonSaveStatus.Found, items);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6)
        { return new(LocalComparisonSaveStatus.PersistenceBusy, []); }
    }

    private static LocalComparisonSavedLifetime ReadSavedLifetime(SqliteConnection connection, SqliteTransaction transaction, LocalComparisonFrozenSnapshot snapshot)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT first_saved_at,saved_until,is_saved FROM local_comparison_saved_lifetimes WHERE comparison_id=$comparison;";
        command.Parameters.AddWithValue("$comparison", snapshot.ComparisonId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(snapshot.ComparisonId, snapshot.RepositoryId, false, null, null, snapshot.ExpiresAt, true);
        var first = ParseTimestamp(reader.GetString(0));
        var until = ParseTimestamp(reader.GetString(1));
        var saved = reader.GetInt32(2) == 1;
        return new(snapshot.ComparisonId, snapshot.RepositoryId, saved, first, until, saved ? until : snapshot.ExpiresAt, true);
    }
}

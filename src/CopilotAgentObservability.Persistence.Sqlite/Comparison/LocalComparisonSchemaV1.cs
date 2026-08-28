using System.Text;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalComparisonComponentCategory
{
    ComparisonSnapshot,
    ComparisonCohortMembership,
    ComparisonResult,
    ComparisonEvidence,
    ComparisonExpiryTombstone,
}

internal sealed record LocalComparisonRegisteredObject(
    LocalComparisonComponentCategory Category,
    SqliteOwnedSchemaObject Object);

internal static class LocalComparisonComponentRegistryV1
{
    internal static IReadOnlyList<string> CategoryTokens { get; } =
    [
        "comparison_snapshot",
        "comparison_cohort_membership",
        "comparison_result",
        "comparison_evidence",
        "comparison_expiry_tombstone",
    ];

    internal static IReadOnlyList<string> ReverseDependencyTableNames { get; } =
    [
        "local_comparison_expiry_tombstones",
        "local_comparison_evidence",
        "local_comparison_results",
        "local_comparison_cohort_memberships",
        "local_comparison_snapshots",
    ];

    internal static IReadOnlyList<LocalComparisonRegisteredObject> Objects =>
        LocalComparisonSchemaV1.RegisteredObjects;
}

internal static class LocalComparisonSchemaV1
{
    internal const string ComponentName = "local_comparison";
    internal const int Version = 1;
    internal static string CanonicalSql { get; } = ReadCanonicalSql();

    internal static readonly string[] TableNames =
    [
        "local_comparison_snapshots",
        "local_comparison_cohort_memberships",
        "local_comparison_results",
        "local_comparison_evidence",
        "local_comparison_expiry_tombstones",
    ];

    internal static readonly string[] IndexNames =
    [
        "UX_local_comparison_membership_session",
        "IX_local_comparison_snapshots_expiry",
        "IX_local_comparison_evidence_session",
    ];

    internal static readonly string[] TriggerNames =
    [
        "local_comparison_snapshots_insert_replacement_rejected",
        "local_comparison_snapshots_update_rejected",
        "local_comparison_snapshots_delete_rejected",
        "local_comparison_cohort_memberships_insert_replacement_rejected",
        "local_comparison_cohort_memberships_update_rejected",
        "local_comparison_cohort_memberships_delete_rejected",
        "local_comparison_results_insert_replacement_rejected",
        "local_comparison_results_update_rejected",
        "local_comparison_results_delete_rejected",
        "local_comparison_evidence_insert_replacement_rejected",
        "local_comparison_evidence_update_rejected",
        "local_comparison_evidence_delete_rejected",
        "local_comparison_expiry_tombstones_insert_replacement_rejected",
        "local_comparison_expiry_tombstones_update_rejected",
        "local_comparison_expiry_tombstones_delete_rejected",
    ];

    private static readonly IReadOnlyList<SqliteOwnedSchemaDefinition> Definitions =
        BuildDefinitions();
    private static readonly IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        ExpectedObjects = SqliteOwnedSchemaAuthority.Compile(Definitions);

    internal static IReadOnlyList<LocalComparisonRegisteredObject> RegisteredObjects { get; } =
        BuildRegisteredObjects();

    internal static IEnumerable<SqliteOwnedSchemaObject> OwnedObjects => ExpectedObjects.Values;

    internal static void Ensure(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var transaction = connection.BeginTransaction();
        Ensure(connection, transaction);
        transaction.Commit();
    }

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        var declared = ReadDeclaredVersion(connection, transaction);
        var objects = ReadOwnedObjects(connection, transaction);
        if (declared is not null || objects.Count != 0)
        {
            Validate(connection, transaction);
            return;
        }

        ValidateDependencies(connection, transaction);
        Execute(connection, transaction, CanonicalSql);
        if (!HasExactOwnedSchema(connection, transaction)
            || TableNames.Any(table => Count(connection, transaction, table) != 0))
        {
            Reject();
        }
        Execute(connection, transaction,
            "INSERT INTO schema_version(component,version) VALUES('local_comparison',1);");
    }

    internal static void Validate(SqliteConnection connection, SqliteTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (ReadDeclaredVersion(connection, transaction) != Version)
            Reject();
        ValidateDependencies(connection, transaction);
        if (!HasExactOwnedSchema(connection, transaction))
            Reject();
        ValidateRows(connection, transaction);
    }

    internal static bool HasExactOwnedSchema(
        SqliteConnection connection,
        SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Equal(ReadOwnedObjects(connection, transaction), ExpectedObjects);

    internal static void DropOperationalDeleteGuards(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        foreach (var name in new[]
        {
            TriggerNames[11], TriggerNames[8], TriggerNames[5], TriggerNames[2],
        })
        {
            Execute(connection, transaction, $"DROP TRIGGER {name};");
        }
    }

    internal static void RestoreOperationalDeleteGuards(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        foreach (var name in new[]
        {
            TriggerNames[2], TriggerNames[5], TriggerNames[8], TriggerNames[11],
        })
        {
            var definition = Definitions.Single(item =>
                item.Type == "trigger" && item.Name == name);
            Execute(connection, transaction, definition.Sql);
        }
    }

    private static IReadOnlyDictionary<(string Type, string Name), SqliteOwnedSchemaObject>
        ReadOwnedObjects(SqliteConnection connection, SqliteTransaction? transaction) =>
        SqliteOwnedSchemaAuthority.Read(connection, transaction, static (name, table) =>
            name.StartsWith("local_comparison_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("IX_local_comparison_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("UX_local_comparison_", StringComparison.OrdinalIgnoreCase)
            || table.StartsWith("local_comparison_", StringComparison.OrdinalIgnoreCase));

    private static long? ReadDeclaredVersion(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        if (!ObjectExists(connection, transaction, "table", "schema_version"))
            return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT component,typeof(component),version,typeof(version) " +
            "FROM schema_version WHERE component='local_comparison' COLLATE NOCASE;";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetValue(0) is not string component
            || component != ComponentName
            || reader.GetString(1) != "text"
            || reader.GetString(3) != "integer")
            Reject();
        var version = reader.GetInt64(2);
        if (reader.Read())
            Reject();
        return version;
    }

    private static void ValidateDependencies(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        try
        {
            LocalRepositoryCatalogSchemaV1.Validate(connection, transaction);
            LocalArchiveSchemaV1.Validate(connection, transaction);
            LocalWorkspaceProjectionSchemaV1.Validate(connection, transaction);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidOperationException(
                "local_comparison_component_dependency_invalid",
                exception);
        }
    }

    internal static void ValidateRows(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        ValidateTombstones(connection, transaction);
        ValidateNoOrphanOperationalRows(connection, transaction);
        string? after = null;
        while (true)
        {
            var comparisonIds = new List<string>(64);
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT comparison_id,repository_id,created_at,expires_at,
                           selection_frame,selection_sha256,scope_condition_sha256,
                           typeof(comparison_id),typeof(repository_id),typeof(created_at),typeof(expires_at),
                           typeof(selection_frame),typeof(selection_sha256),typeof(scope_condition_sha256)
                    FROM local_comparison_snapshots
                    WHERE $after IS NULL OR comparison_id>$after COLLATE BINARY
                    ORDER BY comparison_id COLLATE BINARY
                    LIMIT 64;
                    """;
                command.Parameters.Add("$after", SqliteType.Text).Value =
                    after is null ? DBNull.Value : after;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.FieldCount != 14
                        || reader.GetValue(0) is not string comparisonId
                        || reader.GetValue(1) is not string repositoryId
                        || reader.GetValue(2) is not string createdText
                        || reader.GetValue(3) is not string expiresText
                        || reader.GetValue(4) is not byte[] selection
                        || reader.GetValue(5) is not string selectionHash
                        || reader.GetValue(6) is not byte[] scopeConditionSha256
                        || Enumerable.Range(7, 7).Any(index =>
                            reader.GetString(index) != (index is 11 or 13 ? "blob" : "text"))
                        || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(comparisonId)
                        || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
                        || !TryCanonicalInstant(createdText, out var created)
                        || !TryCanonicalInstant(expiresText, out var expires)
                        || expires - created != TimeSpan.FromHours(24)
                        || !MatchesHash(selection, selectionHash)
                        || scopeConditionSha256.Length != 32)
                    {
                        Reject();
                    }
                    comparisonIds.Add(reader.GetString(0));
                }
            }
            foreach (var comparisonId in comparisonIds)
                _ = SqliteLocalComparisonStore.ReadSnapshotForValidation(
                    connection, transaction, comparisonId);
            if (comparisonIds.Count < 64)
                break;
            after = comparisonIds[^1];
        }
    }

    private static void ValidateNoOrphanOperationalRows(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT
              EXISTS(
                SELECT 1 FROM local_comparison_cohort_memberships AS child
                LEFT JOIN local_comparison_snapshots AS snapshot
                  ON snapshot.comparison_id=child.comparison_id
                WHERE snapshot.comparison_id IS NULL)
              OR EXISTS(
                SELECT 1 FROM local_comparison_results AS child
                LEFT JOIN local_comparison_snapshots AS snapshot
                  ON snapshot.comparison_id=child.comparison_id
                WHERE snapshot.comparison_id IS NULL)
              OR EXISTS(
                SELECT 1 FROM local_comparison_evidence AS child
                LEFT JOIN local_comparison_snapshots AS snapshot
                  ON snapshot.comparison_id=child.comparison_id
                WHERE snapshot.comparison_id IS NULL);
            """;
        if (Convert.ToInt64(command.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            Reject();
        }
    }

    private static void ValidateTombstones(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT comparison_id,repository_id,expired_at,
                   typeof(comparison_id),typeof(repository_id),typeof(expired_at)
            FROM local_comparison_expiry_tombstones
            ORDER BY comparison_id COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (reader.FieldCount != 6
                || reader.GetValue(0) is not string comparisonId
                || reader.GetValue(1) is not string repositoryId
                || reader.GetValue(2) is not string expiredAt
                || Enumerable.Range(3, 3).Any(index => reader.GetString(index) != "text")
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(comparisonId)
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(repositoryId)
                || !TryCanonicalInstant(expiredAt, out _))
            {
                Reject();
            }
        }
        reader.Close();
        using var overlap = connection.CreateCommand();
        overlap.Transaction = transaction;
        overlap.CommandText = """
            SELECT COUNT(*) FROM local_comparison_snapshots AS snapshot
            JOIN local_comparison_expiry_tombstones AS tombstone
              ON tombstone.comparison_id=snapshot.comparison_id;
            """;
        if (Convert.ToInt64(overlap.ExecuteScalar(),
                System.Globalization.CultureInfo.InvariantCulture) != 0)
        {
            Reject();
        }
    }

    private static bool TryCanonicalInstant(string value, out DateTimeOffset instant)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalTimestamp(value)
            || !DateTimeOffset.TryParseExact(
                value,
                "O",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out instant))
        {
            instant = default;
            return false;
        }
        return instant.Offset == TimeSpan.Zero
            && string.Equals(
                instant.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                value,
                StringComparison.Ordinal);
    }

    private static bool MatchesHash(byte[] bytes, string hash) =>
        string.Equals(
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            hash,
            StringComparison.Ordinal);

    private static IReadOnlyList<SqliteOwnedSchemaDefinition> BuildDefinitions()
    {
        var statements = CanonicalSql.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        var expected = TableNames.Length + IndexNames.Length + TriggerNames.Length;
        if (statements.Length != expected)
            throw new InvalidOperationException("local_comparison_schema_artifact_invalid");
        var definitions = new List<SqliteOwnedSchemaDefinition>(expected);
        for (var index = 0; index < TableNames.Length; index++)
            definitions.Add(new("table", TableNames[index], TableNames[index], statements[index]));

        var indexTables = new[]
        {
            TableNames[1],
            TableNames[0],
            TableNames[3],
        };
        for (var index = 0; index < IndexNames.Length; index++)
            definitions.Add(new("index", IndexNames[index], indexTables[index],
                statements[TableNames.Length + index]));

        var triggerOffset = TableNames.Length + IndexNames.Length;
        for (var index = 0; index < TriggerNames.Length; index++)
        {
            var tableIndex = index / 3;
            definitions.Add(new("trigger", TriggerNames[index], TableNames[tableIndex],
                statements[triggerOffset + index]));
        }
        return definitions.AsReadOnly();
    }

    private static IReadOnlyList<LocalComparisonRegisteredObject> BuildRegisteredObjects()
    {
        var categories = new Dictionary<string, LocalComparisonComponentCategory>(StringComparer.Ordinal)
        {
            [TableNames[0]] = LocalComparisonComponentCategory.ComparisonSnapshot,
            [TableNames[1]] = LocalComparisonComponentCategory.ComparisonCohortMembership,
            [TableNames[2]] = LocalComparisonComponentCategory.ComparisonResult,
            [TableNames[3]] = LocalComparisonComponentCategory.ComparisonEvidence,
            [TableNames[4]] = LocalComparisonComponentCategory.ComparisonExpiryTombstone,
        };
        return ExpectedObjects.Values
            .OrderBy(item => item.Type, StringComparer.Ordinal)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .Select(item => new LocalComparisonRegisteredObject(categories[item.Table], item))
            .ToArray();
    }

    private static string ReadCanonicalSql()
    {
        using var stream = typeof(LocalComparisonSchemaV1).Assembly.GetManifestResourceStream(
            "local_comparison.schema.v1.sql")
            ?? throw new InvalidOperationException("local_comparison_schema_artifact_missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd();
    }

    private static bool ObjectExists(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string type,
        string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type=$type AND name=$name);";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static long Count(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt64(command.ExecuteScalar(),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void Reject() =>
        throw new InvalidOperationException(
            "Unsupported incomplete local_comparison schema version 1.");
}

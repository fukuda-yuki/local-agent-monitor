using System.Reflection;
using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalComparisonSchemaTests
{
    [Fact]
    public void Ensure_InstallsTheExactEmptyFiveCategoryAuthority()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        var schema = typeof(LocalArchiveSchemaV1).Assembly.GetType(
            "CopilotAgentObservability.Persistence.Sqlite.LocalComparisonSchemaV1");

        Assert.NotNull(schema);
        var ensure = schema.GetMethod(
            "Ensure",
            BindingFlags.Static | BindingFlags.NonPublic,
            binder: null,
            [typeof(SqliteConnection)],
            modifiers: null);
        Assert.NotNull(ensure);
        ensure.Invoke(null, [connection]);

        Assert.Equal(1L, Scalar(connection,
            "SELECT version FROM schema_version WHERE component='local_comparison';"));
        Assert.Equal(
            [
                "local_comparison_cohort_memberships",
                "local_comparison_evidence",
                "local_comparison_expiry_tombstones",
                "local_comparison_results",
                "local_comparison_snapshots",
            ],
            Strings(connection,
                "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'local_comparison_%' ORDER BY name;"));
        Assert.Equal(
            [
                "comparison_id", "repository_id", "created_at", "expires_at",
                "selection_frame", "selection_sha256", "scope_condition_sha256",
            ],
            Strings(connection,
                "SELECT name FROM pragma_table_info('local_comparison_snapshots') ORDER BY cid;"));
        Assert.DoesNotContain("scope_receipt", LocalComparisonSchemaV1.CanonicalSql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotSchema_RejectsCallerScopeBytesInsteadOfAnExactDigest()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        LocalComparisonSchemaV1.Ensure(connection);
        InsertRepository(connection, RepositoryId);

        Assert.Throws<SqliteException>(() => InsertSnapshot(
            connection,
            ComparisonId,
            RepositoryId,
            "2026-08-28T00:00:00.0000000+00:00",
            "2026-08-29T00:00:00.0000000+00:00",
            scopeConditionSha256: new byte[31]));
    }

    [Fact]
    public void Validate_RejectsASnapshotWhoseExpiryIsNotExactlyTwentyFourHours()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        LocalComparisonSchemaV1.Ensure(connection);
        InsertRepository(connection, RepositoryId);
        InsertSnapshot(
            connection,
            ComparisonId,
            RepositoryId,
            "2026-08-28T00:00:00.0000000+00:00",
            "2026-08-28T23:00:00.0000000+00:00");

        Assert.Throws<InvalidOperationException>(() =>
            LocalComparisonSchemaV1.Validate(connection, transaction: null));
    }

    [Fact]
    public void Validate_RejectsCorruptSnapshotIdentityAtTheFirstPagingBoundary()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        LocalComparisonSchemaV1.Ensure(connection);
        InsertRepository(connection, RepositoryId);
        Execute(connection, "PRAGMA ignore_check_constraints=ON;");
        InsertSnapshot(
            connection,
            comparisonId: string.Empty,
            RepositoryId,
            "2026-08-28T00:00:00.0000000+00:00",
            "2026-08-29T00:00:00.0000000+00:00");
        Execute(connection, "PRAGMA ignore_check_constraints=OFF;");

        Assert.Throws<InvalidOperationException>(() =>
            LocalComparisonSchemaV1.Validate(connection, transaction: null));
    }

    [Fact]
    public void Validate_RejectsOrphanOwnedRowsEvenWhenForeignKeysWereBypassed()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        LocalComparisonSchemaV1.Ensure(connection);
        var result = LocalComparisonStoredResult.Create(
            ComparisonId,
            1,
            1,
            "scalar",
            "included_session_count",
            Array.AsReadOnly(new[]
            {
                new KeyValuePair<string, string>("a_count", "1"),
            }));
        Execute(connection, "PRAGMA foreign_keys=OFF;");
        Execute(connection, $"""
            INSERT INTO local_comparison_results(
              comparison_id,result_ordinal,section_ordinal,row_kind,row_key,payload,payload_sha256)
            VALUES('{ComparisonId}',1,1,'scalar','included_session_count',
                   X'{Convert.ToHexString(result.Payload)}','{result.PayloadSha256}');
            """);
        Execute(connection, "PRAGMA foreign_keys=ON;");

        Assert.Throws<InvalidOperationException>(() =>
            LocalComparisonSchemaV1.ValidateRows(connection, transaction: null));
    }

    [Fact]
    public void Registry_AssignsEveryOwnedObjectToOneOfTheFiveCategories()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        LocalComparisonSchemaV1.Ensure(connection);

        Assert.Equal(
        [
            "comparison_snapshot",
            "comparison_cohort_membership",
            "comparison_result",
            "comparison_evidence",
            "comparison_expiry_tombstone",
        ], LocalComparisonComponentRegistryV1.CategoryTokens);
        Assert.Equal(
        [
            "local_comparison_expiry_tombstones",
            "local_comparison_evidence",
            "local_comparison_results",
            "local_comparison_cohort_memberships",
            "local_comparison_snapshots",
        ], LocalComparisonComponentRegistryV1.ReverseDependencyTableNames);
        Assert.Equal(23, LocalComparisonComponentRegistryV1.Objects.Count);
        Assert.All(LocalComparisonComponentRegistryV1.Objects,
            item => Assert.True(Enum.IsDefined(item.Category)));
    }

    [Fact]
    public void Ensure_RejectsFutureOwnedResidueWithoutChangingIt()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        Execute(connection, "CREATE TABLE local_comparison_future(value TEXT);");

        Assert.Throws<InvalidOperationException>(() => LocalComparisonSchemaV1.Ensure(connection));

        Assert.Equal(1L, Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name='local_comparison_future';"));
        Assert.Equal(0L, Scalar(connection,
            "SELECT COUNT(*) FROM schema_version WHERE component='local_comparison';"));
    }

    [Fact]
    public void Validate_RejectsANonCalendarTombstoneThatPassesTheSqlShapeCheck()
    {
        using var database = new ComparisonDatabase();
        using var connection = database.OpenCurrentDependencies();
        LocalComparisonSchemaV1.Ensure(connection);
        Execute(connection, $"""
            INSERT INTO local_comparison_expiry_tombstones(comparison_id,repository_id,expired_at)
            VALUES('{ComparisonId}','{RepositoryId}','2026-02-31T00:00:00.0000000+00:00');
            """);

        Assert.Throws<InvalidOperationException>(() =>
            LocalComparisonSchemaV1.Validate(connection, transaction: null));
    }

    private const string ComparisonId = "0198f5b8-0c00-7000-8000-000000000001";
    private const string RepositoryId = "0198f5b8-0c00-7000-8000-000000000002";

    private static void InsertRepository(SqliteConnection connection, string repositoryId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at)
            VALUES($id,'Repository',1,'2026-08-28T00:00:00.0000000+00:00','2026-08-28T00:00:00.0000000+00:00');
            """;
        command.Parameters.AddWithValue("$id", repositoryId);
        command.ExecuteNonQuery();
    }

    private static void InsertSnapshot(
        SqliteConnection connection,
        string comparisonId,
        string repositoryId,
        string createdAt,
        string expiresAt,
        byte[]? scopeConditionSha256 = null)
    {
        var selection = new byte[] { 1 };
        scopeConditionSha256 ??= SHA256.HashData(new byte[] { 2 });
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_comparison_snapshots(
              comparison_id,repository_id,created_at,expires_at,
              selection_frame,selection_sha256,scope_condition_sha256)
            VALUES($comparison,$repository,$created,$expires,$selection,$selection_hash,$scope_condition_sha256);
            """;
        command.Parameters.AddWithValue("$comparison", comparisonId);
        command.Parameters.AddWithValue("$repository", repositoryId);
        command.Parameters.AddWithValue("$created", createdAt);
        command.Parameters.AddWithValue("$expires", expiresAt);
        command.Parameters.Add("$selection", SqliteType.Blob).Value = selection;
        command.Parameters.AddWithValue("$selection_hash", Convert.ToHexStringLower(SHA256.HashData(selection)));
        command.Parameters.Add("$scope_condition_sha256", SqliteType.Blob).Value = scopeConditionSha256;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string[] Strings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ComparisonDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"local-comparison-schema-{Guid.NewGuid():N}");

        internal ComparisonDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "comparison.sqlite");
        }

        internal string Path { get; }

        internal SqliteConnection OpenCurrentDependencies()
        {
            new SqliteSessionStore(Path).CreateSchema();
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
            LocalArchiveSchemaV1.Ensure(connection);
            LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.UnixEpoch);
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

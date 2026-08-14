using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryTargetExistenceAuthorityTests
{
    private const string RepositoryOne = "01900000-0000-7000-8000-000000000001";
    private const string RepositoryTwo = "01900000-0000-7000-8000-000000000002";
    private const string RepositoryThree = "01900000-0000-7000-8000-000000000003";

    [Fact]
    public void ReadExisting_ReturnsTheExistingRepositoryFromTheSuppliedTransaction()
    {
        using var database = new TargetDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        Execute(
            connection,
            transaction,
            "INSERT INTO local_repositories(repository_id) VALUES($repository_id);",
            RepositoryOne);

        var existing = SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
            connection,
            transaction,
            [RepositoryOne],
            CancellationToken.None);

        Assert.Equal([RepositoryOne], existing);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        transaction.Rollback();
        Assert.Equal(0, database.Count());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ReadExisting_ReturnsFrozenEmptyPartialAndFullSubsets(int existingCount)
    {
        string[] repositoryIds = [RepositoryOne, RepositoryTwo, RepositoryThree];
        using var database = new TargetDatabase();
        foreach (var repositoryId in repositoryIds.Take(existingCount))
            database.Insert(repositoryId);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
            connection,
            transaction,
            repositoryIds,
            CancellationToken.None);

        Assert.Equal(repositoryIds.Take(existingCount), existing);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)existing).Add(RepositoryThree));
        repositoryIds[0] = RepositoryThree;
        Assert.Equal(existingCount == 0 ? [] : [RepositoryOne], existing.Take(1));
    }

    [Fact]
    public void ReadExisting_FreezesEachHostileInputItemWithExactlyOneRead()
    {
        using var database = new TargetDatabase();
        database.Insert(RepositoryOne);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var repositoryIds = new OneReadRepositoryIds([RepositoryOne, RepositoryTwo]);

        var existing = SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
            connection,
            transaction,
            repositoryIds,
            CancellationToken.None);

        Assert.Equal([RepositoryOne], existing);
        Assert.Equal(1, repositoryIds.CountReads);
        Assert.Equal([1, 1], repositoryIds.ItemReads);
    }

    [Fact]
    public void ReadExisting_CopiesEveryHostileInputItemOnceBeforeRejectingAnEarlyInvalidValue()
    {
        using var database = new TargetDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var repositoryIds = new OneReadRepositoryIds(
            ["not-a-repository-id", RepositoryTwo, RepositoryThree]);

        var error = Assert.Throws<ArgumentException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection,
                transaction,
                repositoryIds,
                CancellationToken.None));

        Assert.Equal("canonicalRepositoryIds", error.ParamName);
        Assert.StartsWith("local_repository_target_ids_invalid", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, repositoryIds.CountReads);
        Assert.Equal([1, 1, 1], repositoryIds.ItemReads);
    }

    [Fact]
    public void ReadExisting_AcceptsTheExactTwoHundredIdFrontier()
    {
        using var database = new TargetDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var repositoryIds = Enumerable.Range(1, 200).Select(RepositoryId).ToArray();

        var existing = SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
            connection,
            transaction,
            repositoryIds,
            CancellationToken.None);

        Assert.Empty(existing);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)existing).Add(RepositoryOne));
    }

    [Fact]
    public void ReadExisting_UsesNormalBclNullGuardsInArgumentOrder()
    {
        using var database = new TargetDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        Assert.Equal("openConnection", Assert.Throws<ArgumentNullException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                null!, null!, null!, CancellationToken.None)).ParamName);
        Assert.Equal("exactTransaction", Assert.Throws<ArgumentNullException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection, null!, null!, CancellationToken.None)).ParamName);
        Assert.Equal("canonicalRepositoryIds", Assert.Throws<ArgumentNullException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, null!, CancellationToken.None)).ParamName);
    }

    [Fact]
    public void ReadExisting_RejectsTheWrongTransactionBeforeReadingInputOrCancellation()
    {
        using var database = new TargetDatabase();
        using var otherDatabase = new TargetDatabase();
        using var connection = database.Open();
        using var exactTransaction = connection.BeginTransaction();
        using var otherConnection = otherDatabase.Open();
        using var otherTransaction = otherConnection.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var repositoryIds = new ThrowingRepositoryIds();

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection,
                otherTransaction,
                repositoryIds,
                cancellation.Token));

        Assert.Equal("local_repository_target_existence_transaction_invalid", error.Message);
        Assert.Equal(0, repositoryIds.Reads);
        Assert.Same(connection, exactTransaction.Connection);
        Assert.Same(otherConnection, otherTransaction.Connection);
    }

    [Fact]
    public void ReadExisting_RejectsAClosedConnectionAndAnInactiveTransaction()
    {
        using var database = new TargetDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        transaction.Commit();

        var inactive = Assert.Throws<InvalidOperationException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [RepositoryOne], CancellationToken.None));
        Assert.Equal("local_repository_target_existence_transaction_invalid", inactive.Message);

        using var closedConnection = database.Open();
        using var closedTransaction = closedConnection.BeginTransaction();
        closedConnection.Close();
        var closed = Assert.Throws<InvalidOperationException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                closedConnection, closedTransaction, [RepositoryOne], CancellationToken.None));
        Assert.Equal("local_repository_target_existence_transaction_invalid", closed.Message);
    }

    public static TheoryData<IReadOnlyList<string>> InvalidRepositoryIdSets => new()
    {
        Array.Empty<string>(),
        Enumerable.Range(1, 201).Select(RepositoryId).ToArray(),
        new[] { "01900000-0000-7000-8000-00000000000A" },
        new[] { "01900000-0000-6000-8000-000000000001" },
        new[] { RepositoryOne, RepositoryOne },
        new[] { RepositoryTwo, RepositoryOne },
        new string[] { null! },
    };

    [Theory]
    [MemberData(nameof(InvalidRepositoryIdSets))]
    public void ReadExisting_RejectsInvalidBoundsCanonicalFormsAndOrdinalOrderBeforeQuery(
        IReadOnlyList<string> repositoryIds)
    {
        using var database = new TargetDatabase(createTable: false);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var error = Assert.Throws<ArgumentException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection,
                transaction,
                repositoryIds,
                CancellationToken.None));

        Assert.Equal("canonicalRepositoryIds", error.ParamName);
        Assert.StartsWith("local_repository_target_ids_invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadExisting_ValidatesIdsBeforeObservingCancellationAndCancelsBeforeQuery()
    {
        using var database = new TargetDatabase(createTable: false);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var invalid = Assert.Throws<ArgumentException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [], cancellation.Token));
        Assert.StartsWith("local_repository_target_ids_invalid", invalid.Message, StringComparison.Ordinal);
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [RepositoryOne], cancellation.Token));
        Assert.Same(connection, transaction.Connection);
    }

    [Fact]
    public void ReadExisting_ExecutesOneExactTextParameterizedStatement()
    {
        using var database = new TargetDatabase();
        database.Insert(RepositoryOne);
        database.Insert(RepositoryThree);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var traces = new List<StatementTrace>();
        connection.Handle!.enable_sqlite3_next_stmt(true);
        SQLitePCL.strdelegate_trace trace = (_, expandedSql) =>
        {
            for (var statement = SQLitePCL.raw.sqlite3_next_stmt(connection.Handle, null);
                 statement is not null;
                 statement = SQLitePCL.raw.sqlite3_next_stmt(connection.Handle, statement))
            {
                var sql = SQLitePCL.raw.sqlite3_sql(statement).utf8_to_string();
                if (sql.Contains("FROM local_repositories", StringComparison.Ordinal))
                {
                    traces.Add(new(
                        sql,
                        expandedSql,
                        Enumerable.Range(1, SQLitePCL.raw.sqlite3_bind_parameter_count(statement))
                            .Select(index => SQLitePCL.raw.sqlite3_bind_parameter_name(statement, index).utf8_to_string())
                            .ToArray()));
                }
            }
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, null);

        try
        {
            _ = SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection,
                transaction,
                [RepositoryOne, RepositoryTwo, RepositoryThree],
                CancellationToken.None);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }

        var observed = Assert.Single(traces);
        Assert.Equal(
            "SELECT repository_id, typeof(repository_id)\n" +
            "FROM local_repositories\n" +
            "WHERE repository_id IN ($repository_id_000, $repository_id_001, $repository_id_002)\n" +
            "ORDER BY repository_id COLLATE BINARY;",
            observed.Sql.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(
            ["$repository_id_000", "$repository_id_001", "$repository_id_002"],
            observed.ParameterNames);
        Assert.DoesNotContain(RepositoryOne, observed.Sql, StringComparison.Ordinal);
        Assert.Contains($"'{RepositoryOne}'", observed.ExpandedSql, StringComparison.Ordinal);
        Assert.Contains($"'{RepositoryTwo}'", observed.ExpandedSql, StringComparison.Ordinal);
        Assert.Contains($"'{RepositoryThree}'", observed.ExpandedSql, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadExisting_RejectsDuplicateRowsWithoutReturningAPartialSet()
    {
        using var database = new TargetDatabase();
        database.Insert(RepositoryOne);
        database.Insert(RepositoryOne);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        AssertResultInvalid(() => SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
            connection, transaction, [RepositoryOne], CancellationToken.None));
    }

    [Theory]
    [InlineData("noncanonical")]
    [InlineData("out_of_input")]
    public void ReadExisting_RejectsHostileProjectedRows(string corruption)
    {
        using var database = new TargetDatabase(createTable: false);
        using var connection = database.Open();
        connection.CreateCollation("TARGET_MATCH", (_, _) => 0);
        Execute(
            connection,
            null,
            "CREATE TEMP TABLE local_repositories(repository_id TEXT COLLATE TARGET_MATCH NOT NULL);");
        Execute(
            connection,
            null,
            "INSERT INTO local_repositories(repository_id) VALUES($repository_id);",
            corruption == "noncanonical"
                ? "01900000-0000-7000-8000-00000000000A"
                : RepositoryTwo);
        using var transaction = connection.BeginTransaction();

        AssertResultInvalid(() => SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
            connection, transaction, [RepositoryOne], CancellationToken.None));
    }

    [Fact]
    public void ReadExisting_RejectsANonTextStorageShapeReportedBySqlite()
    {
        using var database = new TargetDatabase();
        database.Insert(RepositoryOne);
        using var connection = database.Open();
        connection.CreateFunction<string, string>("typeof", _ => "blob");
        using var transaction = connection.BeginTransaction();

        AssertResultInvalid(() => SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
            connection, transaction, [RepositoryOne], CancellationToken.None));
    }

    [Fact]
    public void ReadExisting_PropagatesStorageFailureAndLeavesCallerOwnershipIntact()
    {
        using var database = new TargetDatabase(createTable: false);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var error = Assert.Throws<SqliteException>(() =>
            SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [RepositoryOne], CancellationToken.None));

        Assert.Equal(1, error.SqliteErrorCode);
        Assert.Null(error.InnerException);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        transaction.Rollback();
    }

    [Fact]
    public void ReadExisting_PropagatesBusyOnceAndLeavesTheCallerTransactionActive()
    {
        using var database = new TargetDatabase();
        database.Insert(RepositoryOne);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var blocker = database.Open();
        var attempts = 0;
        Exception? blockerFailure = null;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (attempts == 0
                && sql.Contains("FROM local_repositories", StringComparison.Ordinal))
            {
                attempts++;
                try
                {
                    Execute(blocker, null, "BEGIN EXCLUSIVE;");
                }
                catch (Exception error)
                {
                    blockerFailure = error;
                }
            }
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, null);

        SqliteException error;
        try
        {
            error = Assert.Throws<SqliteException>(() =>
                SqliteLocalRepositoryTargetExistenceAuthority.Instance.ReadExisting(
                    connection, transaction, [RepositoryOne], CancellationToken.None));
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
            if (blockerFailure is null && attempts > 0)
                Execute(blocker, null, "ROLLBACK;");
            GC.KeepAlive(trace);
        }

        Assert.Null(blockerFailure);
        Assert.Contains(error.SqliteErrorCode, new[] { 5, 6 });
        Assert.Equal(1, attempts);
        Assert.Null(error.InnerException);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
        Assert.Same(connection, transaction.Connection);
        transaction.Rollback();
    }

    private static void AssertResultInvalid(Func<IReadOnlyList<string>> read)
    {
        var error = Assert.Throws<InvalidOperationException>(() => read());
        Assert.Equal("local_repository_target_existence_result_invalid", error.Message);
        Assert.Null(error.InnerException);
    }

    private static string RepositoryId(int value) =>
        $"01900000-0000-7000-8000-{value:D12}";

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string? repositoryId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (repositoryId is not null)
            command.Parameters.Add("$repository_id", SqliteType.Text).Value = repositoryId;
        command.ExecuteNonQuery();
    }

    private sealed record StatementTrace(
        string Sql,
        string ExpandedSql,
        IReadOnlyList<string> ParameterNames);

    private sealed class OneReadRepositoryIds(string[] values) : IReadOnlyList<string>
    {
        private readonly int[] itemReads = new int[values.Length];

        internal int CountReads { get; private set; }
        internal IReadOnlyList<int> ItemReads => itemReads;

        public int Count => ++CountReads == 1
            ? values.Length
            : throw new InvalidOperationException("count_read_more_than_once");

        public string this[int index] => ++itemReads[index] == 1
            ? values[index]
            : throw new InvalidOperationException("item_read_more_than_once");

        public IEnumerator<string> GetEnumerator() =>
            throw new InvalidOperationException("enumeration_forbidden");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingRepositoryIds : IReadOnlyList<string>
    {
        internal int Reads { get; private set; }
        public int Count { get { Reads++; throw new InvalidOperationException("input_read_forbidden"); } }
        public string this[int index] { get { Reads++; throw new InvalidOperationException("input_read_forbidden"); } }
        public IEnumerator<string> GetEnumerator() { Reads++; throw new InvalidOperationException("input_read_forbidden"); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TargetDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"repository-target-existence-{Guid.NewGuid():N}");

        internal TargetDatabase(bool createTable = true)
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "catalog.sqlite");
            using var connection = Open();
            if (createTable)
                Execute(connection, null, "CREATE TABLE local_repositories(repository_id TEXT NOT NULL);");
        }

        internal string Path { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void Insert(string repositoryId)
        {
            using var connection = Open();
            Execute(
                connection,
                null,
                "INSERT INTO local_repositories(repository_id) VALUES($repository_id);",
                repositoryId);
        }

        internal long Count()
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM local_repositories;";
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

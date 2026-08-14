using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveSessionTargetExistenceAuthorityTests
{
    private const string SessionOne = "01900000-0000-7000-8000-000000000001";
    private const string SessionTwo = "01900000-0000-7000-8000-000000000002";
    private const string SessionThree = "01900000-0000-7000-8000-000000000003";

    [Fact]
    public void ReadExisting_ReturnsAFrozenExactSubsetFromTheSuppliedTransaction()
    {
        using var database = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        database.Insert(connection, transaction, SessionOne);
        string[] requested = [SessionOne, SessionTwo];

        var existing = LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
            connection, transaction, requested, CancellationToken.None);

        Assert.Equal([SessionOne], existing);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)existing).Add(SessionTwo));
        requested[0] = SessionThree;
        Assert.Equal([SessionOne], existing);
        Assert.Same(connection, transaction.Connection);
        transaction.Rollback();
        Assert.Equal(0L, database.Count());
    }

    [Fact]
    public void ReadExisting_FreezesCountAndEveryItemExactlyOnce()
    {
        using var database = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var requested = new OneReadIds([SessionOne, SessionTwo]);

        var existing = LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
            connection, transaction, requested, CancellationToken.None);

        Assert.Empty(existing);
        Assert.Equal(1, requested.CountReads);
        Assert.Equal([1, 1], requested.ItemReads);
    }

    [Fact]
    public void ReadExisting_FreezesAllItemsBeforeRejectingAnEarlyInvalidValue()
    {
        using var database = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var requested = new OneReadIds(["invalid", SessionTwo, SessionThree]);

        var error = Assert.Throws<ArgumentException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, requested, CancellationToken.None));

        Assert.Equal("canonicalSessionIds", error.ParamName);
        Assert.Equal(1, requested.CountReads);
        Assert.Equal([1, 1, 1], requested.ItemReads);
    }

    [Fact]
    public void ReadExisting_AcceptsTheExactTwoHundredIdFrontier()
    {
        using var database = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var existing = LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
            connection,
            transaction,
            Enumerable.Range(1, 200).Select(SessionId).ToArray(),
            CancellationToken.None);

        Assert.Empty(existing);
    }

    [Fact]
    public void ReadExisting_UsesNormalBclNullGuardsInArgumentOrder()
    {
        using var database = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        Assert.Equal("openConnection", Assert.Throws<ArgumentNullException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                null!, null!, null!, CancellationToken.None)).ParamName);
        Assert.Equal("exactTransaction", Assert.Throws<ArgumentNullException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, null!, null!, CancellationToken.None)).ParamName);
        Assert.Equal("canonicalSessionIds", Assert.Throws<ArgumentNullException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, null!, CancellationToken.None)).ParamName);
    }

    public static TheoryData<IReadOnlyList<string>> InvalidSets => new()
    {
        Array.Empty<string>(),
        Enumerable.Range(1, 201).Select(SessionId).ToArray(),
        new[] { "01900000-0000-7000-8000-00000000000A" },
        new[] { "01900000-0000-6000-8000-000000000001" },
        new[] { SessionOne, SessionOne },
        new[] { SessionTwo, SessionOne },
        new string[] { null! },
    };

    [Theory]
    [MemberData(nameof(InvalidSets))]
    public void ReadExisting_RejectsInvalidInputBeforeCancellationOrQuery(IReadOnlyList<string> requested)
    {
        using var database = new SessionDatabase(createTable: false);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var error = Assert.Throws<ArgumentException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, requested, cancellation.Token));

        Assert.Equal("canonicalSessionIds", error.ParamName);
        Assert.StartsWith("local_archive_session_target_ids_invalid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadExisting_RejectsInvalidTransactionBeforeInputAndCancellation()
    {
        using var database = new SessionDatabase();
        using var other = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        using var otherConnection = other.Open();
        using var otherTransaction = otherConnection.BeginTransaction();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var requested = new ThrowingIds();

        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, otherTransaction, requested, cancellation.Token));

        Assert.Equal("local_archive_session_target_existence_transaction_invalid", error.Message);
        Assert.Equal(0, requested.Reads);
        Assert.Same(connection, transaction.Connection);
    }

    [Fact]
    public void ReadExisting_RejectsInactiveTransactionsAndClosedConnections()
    {
        using var database = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        transaction.Commit();

        var inactive = Assert.Throws<InvalidOperationException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [SessionOne], CancellationToken.None));
        Assert.Equal("local_archive_session_target_existence_transaction_invalid", inactive.Message);

        using var closedConnection = database.Open();
        using var closedTransaction = closedConnection.BeginTransaction();
        closedConnection.Close();
        var closed = Assert.Throws<InvalidOperationException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                closedConnection, closedTransaction, [SessionOne], CancellationToken.None));
        Assert.Equal("local_archive_session_target_existence_transaction_invalid", closed.Message);
    }

    [Fact]
    public void ReadExisting_ExecutesOneExactTextParameterizedQuery()
    {
        using var database = new SessionDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();
        var observed = new List<StatementTrace>();
        connection.Handle!.enable_sqlite3_next_stmt(true);
        SQLitePCL.strdelegate_trace trace = (_, expandedSql) =>
        {
            for (var statement = SQLitePCL.raw.sqlite3_next_stmt(connection.Handle, null);
                 statement is not null;
                 statement = SQLitePCL.raw.sqlite3_next_stmt(connection.Handle, statement))
            {
                var sql = SQLitePCL.raw.sqlite3_sql(statement).utf8_to_string();
                if (sql.Contains("FROM sessions", StringComparison.Ordinal))
                {
                    observed.Add(new(
                        sql.Replace("\r\n", "\n", StringComparison.Ordinal),
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
            _ = LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [SessionOne, SessionTwo], CancellationToken.None);
        }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }

        var traceEntry = Assert.Single(observed);
        Assert.Equal(
            "SELECT session_id, typeof(session_id)\n" +
            "FROM sessions\n" +
            "WHERE session_id IN ($session_id_000, $session_id_001)\n" +
            "ORDER BY session_id COLLATE BINARY;",
            traceEntry.Sql);
        Assert.Equal(["$session_id_000", "$session_id_001"], traceEntry.ParameterNames);
        Assert.DoesNotContain(SessionOne, traceEntry.Sql, StringComparison.Ordinal);
        Assert.Contains($"'{SessionOne}'", traceEntry.ExpandedSql, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadExisting_PropagatesStorageFailureWithoutOwningTheTransaction()
    {
        using var database = new SessionDatabase(createTable: false);
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction();

        var error = Assert.Throws<SqliteException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [SessionOne], CancellationToken.None));

        Assert.Equal(1, error.SqliteErrorCode);
        Assert.Null(error.InnerException);
        Assert.Same(connection, transaction.Connection);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("noncanonical")]
    [InlineData("out-of-input")]
    [InlineData("nontext")]
    public void ReadExisting_RejectsEveryInvalidResultShape(string corruption)
    {
        using var database = new SessionDatabase(createTable: false);
        using var connection = database.Open();
        connection.CreateCollation("TARGET_MATCH", (_, _) => 0);
        Execute(
            connection,
            null,
            "CREATE TEMP TABLE sessions(session_id TEXT COLLATE TARGET_MATCH NOT NULL);");
        if (corruption == "nontext")
            connection.CreateFunction<string, string>("typeof", _ => "blob");
        var inserted = corruption switch
        {
            "noncanonical" => "01900000-0000-7000-8000-00000000000A",
            "out-of-input" => SessionTwo,
            _ => SessionOne,
        };
        Execute(connection, null, "INSERT INTO sessions(session_id) VALUES($session_id);", inserted);
        if (corruption == "duplicate")
            Execute(connection, null, "INSERT INTO sessions(session_id) VALUES($session_id);", inserted);
        using var transaction = connection.BeginTransaction();

        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalArchiveSessionTargetExistenceAuthority.Instance.ReadExisting(
                connection, transaction, [SessionOne], CancellationToken.None));

        Assert.Equal("local_archive_session_target_existence_result_invalid", error.Message);
        Assert.Null(error.InnerException);
    }

    private static string SessionId(int value) => $"01900000-0000-7000-8000-{value:D12}";

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string? sessionId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (sessionId is not null)
            command.Parameters.Add("$session_id", SqliteType.Text).Value = sessionId;
        command.ExecuteNonQuery();
    }

    private sealed record StatementTrace(
        string Sql,
        string ExpandedSql,
        IReadOnlyList<string> ParameterNames);

    private sealed class OneReadIds(string[] values) : IReadOnlyList<string>
    {
        private readonly int[] reads = new int[values.Length];
        internal int CountReads { get; private set; }
        internal IReadOnlyList<int> ItemReads => reads;
        public int Count => ++CountReads == 1 ? values.Length : throw new InvalidOperationException();
        public string this[int index] => ++reads[index] == 1 ? values[index] : throw new InvalidOperationException();
        public IEnumerator<string> GetEnumerator() => throw new InvalidOperationException();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingIds : IReadOnlyList<string>
    {
        internal int Reads { get; private set; }
        public int Count { get { Reads++; throw new InvalidOperationException(); } }
        public string this[int index] { get { Reads++; throw new InvalidOperationException(); } }
        public IEnumerator<string> GetEnumerator() { Reads++; throw new InvalidOperationException(); }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SessionDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"archive-session-existence-{Guid.NewGuid():N}");
        private readonly bool createTable;

        internal SessionDatabase(bool createTable = true)
        {
            this.createTable = createTable;
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "session.sqlite");
            using var connection = Open();
        }

        internal string Path { get; }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            if (createTable && Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sessions';") == 0)
                Execute(connection, null, "CREATE TABLE sessions(session_id TEXT NOT NULL);");
            return connection;
        }

        internal void Insert(SqliteConnection connection, SqliteTransaction transaction, string sessionId) =>
            Execute(connection, transaction, "INSERT INTO sessions(session_id) VALUES($session_id);", sessionId);

        internal long Count()
        {
            using var connection = Open();
            return Scalar(connection, "SELECT COUNT(*) FROM sessions;");
        }

        private static long Scalar(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteLocalArchiveFactSnapshotContributorTests
{
    private const string SessionOne = "01900000-0000-7000-8000-000000000001";
    private const string SessionTwo = "01900000-0000-7000-8000-000000000002";
    private const string RepositoryOne = "01900000-0000-7000-9000-000000000001";
    private const string RepositoryTwo = "01900000-0000-7000-9000-000000000002";

    [Fact]
    public async Task ReadAsync_ReturnsPersistedFactsAndMaterializesMissingTargetsAsActiveZero()
    {
        using var database = new ArchiveDatabase();
        database.Insert("session", SessionOne, "archived", 3);
        database.Insert("repository", RepositoryOne, "active", 4);
        var capability = database.CreateCapability();

        var result = await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
            capability,
            new([SessionOne, SessionTwo], [RepositoryOne, RepositoryTwo]),
            CancellationToken.None);

        Assert.Equal(
            [(SessionOne, LocalArchiveState.Archived, 3L), (SessionTwo, LocalArchiveState.Active, 0L)],
            result.Sessions.Select(item => (item.SessionId, item.State, item.Revision)));
        Assert.Equal(
            [(RepositoryOne, LocalArchiveState.Active, 4L), (RepositoryTwo, LocalArchiveState.Active, 0L)],
            result.Repositories.Select(item => (item.RepositoryId, item.State, item.Revision)));
        Assert.Equal(1, capability.ReadCount);
    }

    [Fact]
    public async Task ReadAsync_UsesSessionFirstConsecutiveChunksOfAtMostTwoHundredWithExactSqlAndTextBindings()
    {
        using var database = new ArchiveDatabase();
        var sessions = Enumerable.Range(1, 201).Select(SessionId).ToArray();
        var repositories = Enumerable.Range(1, 201).Select(RepositoryId).ToArray();
        var observed = database.TraceArchiveReads();
        var capability = database.CreateCapability();

        var result = await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
            capability,
            new(sessions, repositories),
            CancellationToken.None);

        Assert.Equal(201, result.Sessions.Count);
        Assert.Equal(201, result.Repositories.Count);
        Assert.Equal(1, capability.ReadCount);
        Assert.Equal(4, observed.Count);
        Assert.Equal(["session", "session", "repository", "repository"],
            observed.Select(statement => statement.Kind));
        Assert.Equal([201, 2, 201, 2], observed.Select(statement => statement.ParameterNames.Count));
        Assert.Equal(
            ExpectedSql(200),
            observed[0].Sql);
        Assert.Equal(
            ExpectedSql(1),
            observed[1].Sql);
        Assert.Equal("$target_kind", observed[0].ParameterNames[0]);
        Assert.Equal("$target_id_000", observed[0].ParameterNames[1]);
        Assert.Equal("$target_id_199", observed[0].ParameterNames[200]);
        Assert.DoesNotContain(SessionOne, observed[0].Sql, StringComparison.Ordinal);
        Assert.Contains($"'{SessionOne}'", observed[0].ExpandedSql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_UsesOneFixedBoundedReadWhenBothSetsAreEmpty()
    {
        using var database = new ArchiveDatabase();
        var observed = database.TraceArchiveReads();
        var capability = database.CreateCapability();

        var result = await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
            capability,
            new([], []),
            CancellationToken.None);

        Assert.Empty(result.Sessions);
        Assert.Empty(result.Repositories);
        Assert.Equal(1, capability.ReadCount);
        var statement = Assert.Single(observed);
        Assert.Equal("", statement.Kind);
        Assert.Empty(statement.ParameterNames);
        Assert.Equal(
            "SELECT target_kind, typeof(target_kind),\n" +
            "       target_id, typeof(target_id),\n" +
            "       state, typeof(state),\n" +
            "       revision, typeof(revision)\n" +
            "FROM local_archive_current\n" +
            "WHERE 0\n" +
            "ORDER BY target_kind COLLATE BINARY, target_id COLLATE BINARY;",
            statement.Sql);
    }

    [Theory]
    [InlineData("state_value")]
    [InlineData("state_type")]
    [InlineData("revision_type")]
    [InlineData("active_odd")]
    [InlineData("archived_zero")]
    [InlineData("archived_even")]
    [InlineData("negative")]
    public async Task ReadAsync_RejectsInvalidStorageAndStateRevisionParity(string corruption)
    {
        using var database = new ArchiveDatabase(createCanonicalTable: false);
        database.CreateLooseTable();
        var state = corruption == "state_value" ? "unknown" : corruption.StartsWith("archived", StringComparison.Ordinal) ? "archived" : "active";
        object stateValue = corruption == "state_type" ? 1L : state;
        object revision = corruption switch
        {
            "revision_type" => "2",
            "active_odd" => 1L,
            "archived_zero" => 0L,
            "archived_even" => 2L,
            "negative" => -2L,
            _ => 2L,
        };
        database.InsertLoose("session", SessionOne, stateValue, revision);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
                database.CreateCapability(), new([SessionOne], []), CancellationToken.None));

        Assert.Equal("local_archive_fact_contribution_invalid", error.Message);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("wrong_kind")]
    [InlineData("noncanonical")]
    [InlineData("out_of_chunk")]
    [InlineData("duplicate")]
    [InlineData("kind_type")]
    [InlineData("id_type")]
    public async Task ReadAsync_RejectsInvalidIdentityRows(string corruption)
    {
        using var database = new ArchiveDatabase(createCanonicalTable: false);
        database.CreateLooseTable(
            equalText: corruption is "wrong_kind" or "noncanonical" or "out_of_chunk");
        var firstKind = corruption == "wrong_kind" ? "repository" : "session";
        object kind = firstKind;
        object id = corruption switch
        {
            "noncanonical" => "01900000-0000-7000-8000-00000000000A",
            "out_of_chunk" => SessionTwo,
            _ => SessionOne,
        };
        if (corruption is "kind_type" or "id_type")
            database.OverrideTypeof();
        database.InsertLoose(kind, id, "active", 2L);
        if (corruption == "duplicate")
            database.InsertLoose(kind, id, "active", 2L);
        var requested = new[] { SessionOne };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
                database.CreateCapability(), new(requested, []), CancellationToken.None));

        Assert.Equal("local_archive_fact_contribution_invalid", error.Message);
    }

    [Fact]
    public async Task ReadAsync_ChecksCancellationBeforeTheFirstCommand()
    {
        using var database = new ArchiveDatabase(createCanonicalTable: false);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
                database.CreateCapability(), new([SessionOne], []), cancellation.Token));
    }

    [Fact]
    public async Task ReadAsync_ChecksCancellationWhileFreezingRows()
    {
        using var database = new ArchiveDatabase();
        database.Insert("session", SessionOne, "active", 2);
        using var cancellation = new CancellationTokenSource();
        database.CancelOnArchiveRead(cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
                database.CreateCapability(), new([SessionOne], []), cancellation.Token));
    }

    [Fact]
    public async Task ReadAsync_PropagatesStorageFailuresWithoutRetryingOrReplacingTheCapabilityTransaction()
    {
        using var database = new ArchiveDatabase(createCanonicalTable: false);
        var capability = database.CreateCapability();

        var error = await Assert.ThrowsAsync<SqliteException>(async () =>
            await SqliteLocalArchiveFactSnapshotContributor.Instance.ReadAsync(
                capability, new([SessionOne], []), CancellationToken.None));

        Assert.Equal(1, error.SqliteErrorCode);
        Assert.Equal(1, capability.ReadCount);
        Assert.True(capability.TransactionIsActive);
    }

    private static string ExpectedSql(int count) =>
        "SELECT target_kind, typeof(target_kind),\n" +
        "       target_id, typeof(target_id),\n" +
        "       state, typeof(state),\n" +
        "       revision, typeof(revision)\n" +
        "FROM local_archive_current\n" +
        "WHERE target_kind = $target_kind\n" +
        $"  AND target_id IN ({string.Join(", ", Enumerable.Range(0, count).Select(index => $"$target_id_{index:D3}"))})\n" +
        "ORDER BY target_id COLLATE BINARY;";

    private static string SessionId(int value) => $"01900000-0000-7000-8000-{value:D12}";
    private static string RepositoryId(int value) => $"01900000-0000-7000-9000-{value:D12}";

    private sealed class ArchiveDatabase : IDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"archive-facts-{Guid.NewGuid():N}.db");
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;

        internal ArchiveDatabase(bool createCanonicalTable = true)
        {
            connection = new($"Data Source={path}");
            connection.Open();
            transaction = connection.BeginTransaction();
            if (createCanonicalTable)
                Execute("CREATE TABLE local_archive_current(target_kind TEXT NOT NULL, target_id TEXT NOT NULL, state TEXT NOT NULL, revision INTEGER NOT NULL);");
        }

        internal TestReadTransaction CreateCapability() => new(connection, transaction);

        internal void CreateLooseTable(bool equalText = false)
        {
            if (equalText)
                connection.CreateCollation("ALL_EQUAL", (_, _) => 0);
            var collation = equalText ? " COLLATE ALL_EQUAL" : "";
            Execute($"CREATE TABLE local_archive_current(target_kind{collation}, target_id{collation}, state, revision);");
        }

        internal void OverrideTypeof() =>
            connection.CreateFunction<string, string>("typeof", _ => "integer");

        internal void Insert(string kind, string id, string state, long revision) =>
            InsertLoose(kind, id, state, revision);

        internal void InsertLoose(object kind, object id, object state, object revision)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO local_archive_current(target_kind,target_id,state,revision) VALUES($kind,$id,$state,$revision);";
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$revision", revision);
            command.ExecuteNonQuery();
        }

        internal List<StatementTrace> TraceArchiveReads()
        {
            var observed = new List<StatementTrace>();
            connection.Handle!.enable_sqlite3_next_stmt(true);
            SQLitePCL.strdelegate_trace trace = (_, expandedSql) =>
            {
                for (var statement = SQLitePCL.raw.sqlite3_next_stmt(connection.Handle, null);
                     statement is not null;
                     statement = SQLitePCL.raw.sqlite3_next_stmt(connection.Handle, statement))
                {
                    var sql = SQLitePCL.raw.sqlite3_sql(statement).utf8_to_string();
                    if (!sql.Contains("FROM local_archive_current", StringComparison.Ordinal))
                        continue;
                    var parameters = Enumerable.Range(1, SQLitePCL.raw.sqlite3_bind_parameter_count(statement))
                        .Select(index => SQLitePCL.raw.sqlite3_bind_parameter_name(statement, index).utf8_to_string())
                        .ToArray();
                    var kind = expandedSql.Contains("'session'", StringComparison.Ordinal) ? "session"
                        : expandedSql.Contains("'repository'", StringComparison.Ordinal) ? "repository" : "";
                    observed.Add(new(
                        sql.Replace("\r\n", "\n", StringComparison.Ordinal),
                        expandedSql,
                        kind,
                        parameters));
                }
            };
            SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, null);
            traces.Add(trace);
            return observed;
        }

        internal void CancelOnArchiveRead(CancellationTokenSource cancellation)
        {
            SQLitePCL.strdelegate_trace trace = (_, sql) =>
            {
                if (sql.Contains("FROM local_archive_current", StringComparison.Ordinal))
                    cancellation.Cancel();
            };
            SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, null);
            traces.Add(trace);
        }

        private readonly List<SQLitePCL.strdelegate_trace> traces = [];

        private void Execute(string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.strdelegate_trace)null!, null);
            transaction.Dispose();
            connection.Dispose();
            SqliteConnection.ClearPool(connection);
            foreach (var trace in traces)
                GC.KeepAlive(trace);
            File.Delete(path);
        }
    }

    private sealed class TestReadTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction) : ILocalRepositoryReadTransaction
    {
        internal int ReadCount { get; private set; }
        internal bool TransactionIsActive => transaction.Connection is not null;

        public async ValueTask<T> ReadAsync<T>(
            Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> read,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            return await read(connection, transaction, cancellationToken);
        }
    }

    private sealed record StatementTrace(
        string Sql,
        string ExpandedSql,
        string Kind,
        IReadOnlyList<string> ParameterNames);
}

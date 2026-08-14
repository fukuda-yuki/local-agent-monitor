using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveSchemaTests
{
    [Fact]
    public void CanonicalArtifact_HasThePinnedBytesAndDigest()
    {
        var bytes = new UTF8Encoding(false, true).GetBytes(LocalArchiveSchemaV1.CanonicalSql);

        Assert.Equal(6994, bytes.Length);
        Assert.Equal(
            "d33265ffbf06a5087d2b83354b6fdd5cc35ece74907fc988b6123ec2eceefb95",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.False(bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }));
        Assert.True(bytes.AsSpan().EndsWith("\n"u8));
        Assert.False(bytes.AsSpan(0, bytes.Length - 1).EndsWith("\n"u8));
        Assert.DoesNotContain((byte)'\r', bytes);
    }

    [Fact]
    public void Ensure_InstallsExactEmptyAuthorityAndIsRepeatable()
    {
        using var database = new ArchiveDatabase();
        using var connection = database.OpenCurrentDependencies();

        LocalArchiveSchemaV1.Ensure(connection);
        LocalArchiveSchemaV1.Ensure(connection);

        Assert.Equal(1L, Scalar(connection,
            "SELECT version FROM schema_version WHERE component='local_archive';"));
        Assert.Equal(2L, Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name LIKE 'local_archive_%';"));
        Assert.Equal(2L, Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name LIKE 'IX_local_archive_%';"));
        Assert.Equal(6L, Scalar(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'local_archive_%';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_archive_current;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM local_archive_events;"));
        Assert.True(LocalArchiveSchemaV1.HasExactOwnedSchema(connection, transaction: null));
    }

    [Fact]
    public void Ensure_InstallsObjectsBeforeTheSingleTerminalStamp()
    {
        using var database = new ArchiveDatabase();
        using var connection = database.OpenCurrentDependencies();
        var statements = new List<string>();
        strdelegate_trace trace = (_, sql) => statements.Add(sql.Replace("\r\n", "\n", StringComparison.Ordinal));
        raw.sqlite3_trace(connection.Handle, trace, null);
        try
        {
            LocalArchiveSchemaV1.Ensure(connection);
        }
        finally
        {
            raw.sqlite3_trace(connection.Handle, (strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }

        var stampIndex = statements.FindIndex(static sql =>
            sql.Contains("INSERT INTO schema_version(component,version) VALUES('local_archive',1);", StringComparison.Ordinal));
        Assert.True(stampIndex >= 0);
        Assert.Equal(1, statements.Count(static sql => sql.Contains("VALUES('local_archive',1)", StringComparison.Ordinal)));
        Assert.All(
            statements.Where(static sql => sql.TrimStart().StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase)),
            sql => Assert.True(statements.IndexOf(sql) < stampIndex));
    }

    [Theory]
    [InlineData("partial")]
    [InlineData("newer")]
    [InlineData("case-alias")]
    [InlineData("extra")]
    public void Ensure_RejectsAnyReservedIncompleteOrInvalidAuthorityWithoutRepair(string shape)
    {
        using var database = new ArchiveDatabase();
        using var connection = database.OpenCurrentDependencies();
        Execute(connection, shape switch
        {
            "partial" => "CREATE TABLE local_archive_current(id INTEGER);",
            "newer" => "INSERT INTO schema_version(component,version) VALUES('local_archive',2);",
            "case-alias" => "CREATE TABLE Local_Archive_Current(id INTEGER);",
            "extra" => "CREATE TABLE local_archive_unknown(id INTEGER);",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        });
        var before = Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE lower(name) LIKE 'local_archive_%';");

        var error = Assert.Throws<InvalidOperationException>(() => LocalArchiveSchemaV1.Ensure(connection));

        Assert.Equal("Unsupported incomplete local_archive schema version 1.", error.Message);
        Assert.Equal(before, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE lower(name) LIKE 'local_archive_%';"));
    }

    [Fact]
    public void Ensure_RejectsACaseAliasComponentStamp()
    {
        using var database = new ArchiveDatabase();
        using var connection = database.OpenCurrentDependencies();
        Execute(connection, "INSERT INTO schema_version(component,version) VALUES('LOCAL_ARCHIVE',1);");

        var error = Assert.Throws<InvalidOperationException>(() => LocalArchiveSchemaV1.Ensure(connection));

        Assert.Equal("Unsupported incomplete local_archive schema version 1.", error.Message);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE lower(name) LIKE 'local_archive_%';"));
    }

    [Theory]
    [InlineData("DROP INDEX IX_local_archive_current_archived_page;")]
    [InlineData("DROP TRIGGER local_archive_events_delete_rejected;")]
    [InlineData("ALTER TABLE local_archive_current ADD COLUMN altered INTEGER;")]
    [InlineData("CREATE TABLE local_archive_extra(id INTEGER);")]
    [InlineData("CREATE INDEX IX_local_archive_extra ON sessions(session_id);")]
    public void Validate_RejectsEveryOwnedInventoryOrDefinitionDeviation(string mutation)
    {
        using var database = new ArchiveDatabase();
        using var connection = database.OpenCurrentDependencies();
        LocalArchiveSchemaV1.Ensure(connection);
        Execute(connection, mutation);

        Assert.False(LocalArchiveSchemaV1.HasExactOwnedSchema(connection, transaction: null));
        Assert.Throws<InvalidOperationException>(() => LocalArchiveSchemaV1.Validate(connection, transaction: null));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("catalog")]
    public void Ensure_RequiresExactCurrentDependenciesBeforeWriting(string dependency)
    {
        using var database = new ArchiveDatabase();
        using var connection = database.OpenCurrentDependencies();
        Execute(connection, dependency == "session"
            ? "UPDATE schema_version SET version=13 WHERE component='session';"
            : "UPDATE schema_version SET version=2 WHERE component='local_repository_catalog';");

        var error = Assert.Throws<InvalidOperationException>(() => LocalArchiveSchemaV1.Ensure(connection));

        Assert.Equal("local_archive_component_dependency_invalid", error.Message);
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE lower(name) LIKE 'local_archive_%';"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM schema_version WHERE component='local_archive';"));
    }

    [Fact]
    public void Ensure_UsesTheSuppliedTransactionAndLeavesCommitToTheCaller()
    {
        using var database = new ArchiveDatabase();
        using var connection = database.OpenCurrentDependencies();
        using var transaction = connection.BeginTransaction();

        LocalArchiveSchemaV1.Ensure(connection, transaction);

        Assert.Same(connection, transaction.Connection);
        Assert.Equal(1L, Scalar(connection, transaction,
            "SELECT version FROM schema_version WHERE component='local_archive';"));
        transaction.Rollback();
        Assert.Equal(0L, Scalar(connection,
            "SELECT COUNT(*) FROM schema_version WHERE component='local_archive';"));
    }

    private static long Scalar(SqliteConnection connection, string sql) =>
        Scalar(connection, null, sql);

    private static long Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed class ArchiveDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"local-archive-schema-{Guid.NewGuid():N}");

        internal ArchiveDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "archive.sqlite");
        }

        internal string Path { get; }

        internal SqliteConnection OpenCurrentDependencies()
        {
            new SqliteSessionStore(Path).CreateSchema();
            var connection = Open();
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
            return connection;
        }

        private SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

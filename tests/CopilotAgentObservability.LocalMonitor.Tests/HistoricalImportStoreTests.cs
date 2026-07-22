using System.Globalization;
using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class HistoricalImportStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingDatabaseConnectionFactorySupportsClosedAndOpenConnections(bool returnOpenConnection)
    {
        using var database = new HistoricalImportTestDatabase();
        new SqliteHistoricalImportStore(database.Path).CreateSchema();
        var factoryCalls = 0;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = database.Path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString();
        var store = SqliteHistoricalImportStore.OpenExistingDatabase(() =>
        {
            factoryCalls++;
            var connection = new SqliteConnection(connectionString);
            if (returnOpenConnection) connection.Open();
            return connection;
        });

        Assert.Empty(store.ListHistoryRows(limit: 1).Items);
        Assert.Empty(store.ListObservationRows(limit: 1, cursor: null).Items);

        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public void CreateSchema_AddsIndependentV1ComponentToRealPreIssue79Database()
    {
        using var database = new HistoricalImportTestDatabase();
        var fixture = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "SchemaMigrations",
            "session",
            "session-v10.sqlite");
        File.Copy(fixture, database.Path);
        string[] existingTables;
        long sessionRows;
        using (var connection = database.Open())
        {
            Execute(connection, "CREATE TABLE unrelated_rows(id INTEGER PRIMARY KEY, value TEXT NOT NULL);");
            Execute(connection, "INSERT INTO unrelated_rows(value) VALUES('preserved');");
            existingTables = Tables(connection);
            sessionRows = Scalar(connection, "SELECT COUNT(*) FROM sessions;");
        }

        var store = new SqliteHistoricalImportStore(database.Path);
        store.CreateSchema();
        store.CreateSchema();

        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification, "SELECT version FROM schema_version WHERE component='historical_import';"));
        Assert.Equal(10L, Scalar(verification, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(1L, Scalar(verification, "SELECT COUNT(*) FROM unrelated_rows;"));
        Assert.Equal(sessionRows, Scalar(verification, "SELECT COUNT(*) FROM sessions;"));
        Assert.All(existingTables, table => Assert.Contains(table, Tables(verification)));
        Assert.Equal(
            [
                "historical_import_confirmation_bindings",
                "historical_import_conflicts",
                "historical_import_observation_fields",
                "historical_import_observation_provenance",
                "historical_import_observations",
                "historical_import_operations",
                "historical_import_previews",
            ],
            Tables(verification).Where(name => name.StartsWith("historical_import_", StringComparison.Ordinal)).Order(StringComparer.Ordinal));

        Assert.Equal(0L, Scalar(verification, "SELECT COUNT(*) FROM historical_import_previews;"));
        Assert.DoesNotContain("source_path", Columns(verification, "historical_import_previews"));
        Assert.DoesNotContain("raw_payload", Columns(verification, "historical_import_observations"));
        Assert.DoesNotContain("conflicting_value", Columns(verification, "historical_import_conflicts"));
        Assert.False(IsRequired(verification, "historical_import_operations", "result_json"));
        Assert.False(IsRequired(verification, "historical_import_operations", "completed_at"));
    }

    [Fact]
    public void CreateSchema_PreservesAcceptedHistoricalInstructionAnalysisComponent()
    {
        using var database = new HistoricalImportTestDatabase();
        new SqliteHistoricalInstructionAnalysisStoreV1(database.Path).CreateSchema();
        string[] existingTables;
        using (var connection = database.Open())
        {
            existingTables = Tables(connection);
            Assert.Equal(1L, Scalar(connection,
                "SELECT version FROM schema_version WHERE component='historical_instruction_analysis';"));
        }

        var store = new SqliteHistoricalImportStore(database.Path);
        store.CreateSchema();
        store.CreateSchema();

        using var verification = database.Open();
        Assert.Equal(1L, Scalar(verification,
            "SELECT version FROM schema_version WHERE component='historical_instruction_analysis';"));
        Assert.Equal(1L, Scalar(verification,
            "SELECT version FROM schema_version WHERE component='historical_import';"));
        Assert.All(existingTables, table => Assert.Contains(table, Tables(verification)));
        Assert.Contains("historical_instruction_analysis_runs", Tables(verification));
        Assert.Equal(0L, Scalar(verification,
            "SELECT COUNT(*) FROM historical_instruction_analysis_runs;"));
    }

    [Fact]
    public void CreateSchema_RejectsNewerVersionWithoutChangingIt()
    {
        using var database = new HistoricalImportTestDatabase();
        var store = new SqliteHistoricalImportStore(database.Path);
        store.CreateSchema();
        using (var connection = database.Open())
        {
            Execute(connection, "UPDATE schema_version SET version=2 WHERE component='historical_import';");
        }

        var exception = Assert.Throws<InvalidOperationException>(store.CreateSchema);

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        using var verification = database.Open();
        Assert.Equal(2L, Scalar(verification, "SELECT version FROM schema_version WHERE component='historical_import';"));
    }

    [Fact]
    public void CreateSchema_RejectsStampedPartialV1InsteadOfRepairingIt()
    {
        using var database = new HistoricalImportTestDatabase();
        using (var connection = database.Open())
        {
            Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY, version INTEGER NOT NULL);");
            Execute(connection, "INSERT INTO schema_version(component,version) VALUES('historical_import',1);");
            Execute(connection, "CREATE TABLE historical_import_previews(preview_id TEXT PRIMARY KEY);");
        }

        var exception = Assert.Throws<InvalidOperationException>(() => new SqliteHistoricalImportStore(database.Path).CreateSchema());

        Assert.Contains("partial", exception.Message, StringComparison.OrdinalIgnoreCase);
        using var verification = database.Open();
        Assert.Equal(["preview_id"], Columns(verification, "historical_import_previews"));
        Assert.DoesNotContain("historical_import_operations", Tables(verification));
    }

    [Fact]
    public void CreateSchema_RejectsStampedV1WithChangedOwnedDefinition()
    {
        using var database = new HistoricalImportTestDatabase();
        var store = new SqliteHistoricalImportStore(database.Path);
        store.CreateSchema();
        using (var connection = database.Open())
        {
            Execute(connection,
                "ALTER TABLE historical_import_previews RENAME COLUMN preview_digest TO changed_digest;");
        }

        var exception = Assert.Throws<InvalidOperationException>(store.CreateSchema);

        Assert.Contains("partial", exception.Message, StringComparison.OrdinalIgnoreCase);
        using var verification = database.Open();
        Assert.Contains("changed_digest", Columns(verification, "historical_import_previews"));
        Assert.DoesNotContain("preview_digest", Columns(verification, "historical_import_previews"));
        Assert.Equal(1L, Scalar(verification,
            "SELECT version FROM schema_version WHERE component='historical_import';"));
    }

    private static string[] Tables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static string[] Columns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_xinfo($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values.ToArray();
    }

    private static bool IsRequired(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT \"notnull\" FROM pragma_table_xinfo($table) WHERE name=$column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

internal sealed class HistoricalImportTestDatabase : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"historical-import-{Guid.NewGuid():N}");
    private readonly List<IDisposable> tracked = [];

    public HistoricalImportTestDatabase()
    {
        Directory.CreateDirectory(directory);
        Path = System.IO.Path.Combine(directory, "monitor.sqlite");
    }

    public string Path { get; }

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public T Track<T>(T value) where T : IDisposable
    {
        tracked.Add(value);
        return value;
    }

    public void Dispose()
    {
        for (var index = tracked.Count - 1; index >= 0; index--) tracked[index].Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }
}

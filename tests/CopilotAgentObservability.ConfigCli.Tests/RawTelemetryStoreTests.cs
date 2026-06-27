using System.Globalization;
using CopilotAgentObservability.ConfigCli;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public class RawTelemetryStoreTests
{
    [Fact]
    public void DefaultDatabasePath_UsesDataRawStoreDb()
    {
        Assert.Equal(Path.Combine("data", "raw-store.db"), RawStoreDefaults.DefaultDatabasePath);
    }

    [Fact]
    public void Sources_AllowOnlySprint2MvpValues()
    {
        Assert.True(RawTelemetrySources.IsAllowed(RawTelemetrySources.RawOtlp));
        Assert.True(RawTelemetrySources.IsAllowed(RawTelemetrySources.CollectorOutput));
        Assert.True(RawTelemetrySources.IsAllowed(RawTelemetrySources.LangfuseExport));
        Assert.False(RawTelemetrySources.IsAllowed("unknown"));
    }

    [Fact]
    public void CreateSchema_CreatesFixedColumnsAndIndexes()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateSchema();

        using var connection = OpenConnection(tempDirectory.DatabasePath);
        Assert.Equal(
            [
                "id",
                "source",
                "trace_id",
                "received_at",
                "resource_attributes_json",
                "payload_json",
                "schema_version",
            ],
            ReadColumnNames(connection));
        Assert.Equal(
            [
                "IX_raw_records_received_at",
                "IX_raw_records_source",
                "IX_raw_records_trace_id",
            ],
            ReadIndexNames(connection));
    }

    [Fact]
    public void CreateSchema_CreatesExpectedSqliteConstraints()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateSchema();

        using var connection = OpenConnection(tempDirectory.DatabasePath);
        var createSql = ReadCreateSql(connection, "raw_records");
        Assert.Contains("id INTEGER PRIMARY KEY AUTOINCREMENT", createSql);
        Assert.Contains("source TEXT NOT NULL CHECK (source IN ('raw-otlp', 'collector-output', 'langfuse-export'))", createSql);
        Assert.Contains("payload_json TEXT NOT NULL", createSql);
        Assert.Contains("schema_version INTEGER NOT NULL CHECK (schema_version = 1)", createSql);
    }

    [Fact]
    public void CreateSchema_CanRunMultipleTimesOnSameDatabase()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateSchema();
        store.CreateSchema();

        using var connection = OpenConnection(tempDirectory.DatabasePath);
        Assert.Equal(1, CountTables(connection, "raw_records"));
    }

    [Fact]
    public void Insert_StoresAndReadsRawRecordWithFixedReceivedAt()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);
        var receivedAt = new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero);
        var resourceAttributesJson = """{"client.kind":"copilot-cli","experiment.id":"baseline"}""";
        var payloadJson = File.ReadAllText(FixturePath());

        store.CreateSchema();
        var id = store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "11111111111111111111111111111111",
            ReceivedAt: receivedAt,
            ResourceAttributesJson: resourceAttributesJson,
            PayloadJson: payloadJson));

        var record = Assert.Single(store.ListRecords());
        Assert.Equal(id, record.Id);
        Assert.Equal(RawTelemetrySources.RawOtlp, record.Source);
        Assert.Equal("11111111111111111111111111111111", record.TraceId);
        Assert.Equal(receivedAt, record.ReceivedAt);
        Assert.Equal(resourceAttributesJson, record.ResourceAttributesJson);
        Assert.Equal(payloadJson, record.PayloadJson);
        Assert.Equal(1, record.SchemaVersion);
    }

    [Fact]
    public void Insert_NormalizesReceivedAtToUtc()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);
        var receivedAt = new DateTimeOffset(2026, 6, 5, 10, 2, 3, TimeSpan.FromHours(9));

        store.CreateSchema();
        store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "11111111111111111111111111111111",
            ReceivedAt: receivedAt,
            ResourceAttributesJson: null,
            PayloadJson: "{}"));

        var record = Assert.Single(store.ListRecords());
        Assert.Equal(new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero), record.ReceivedAt);
        Assert.Equal(TimeSpan.Zero, record.ReceivedAt.Offset);
    }

    [Fact]
    public void Insert_AllowsNullableTraceIdAndResourceAttributes()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateSchema();
        store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.CollectorOutput,
            TraceId: null,
            ReceivedAt: new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero),
            ResourceAttributesJson: null,
            PayloadJson: "{}"));

        var record = Assert.Single(store.ListRecords());
        Assert.Null(record.TraceId);
        Assert.Null(record.ResourceAttributesJson);
    }

    [Fact]
    public void Insert_RejectsUnknownSourceWithSqliteConstraint()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateSchema();

        var exception = Assert.Throws<SqliteException>(() => store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: "unknown",
            TraceId: "11111111111111111111111111111111",
            ReceivedAt: new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero),
            ResourceAttributesJson: null,
            PayloadJson: "{}")));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public void CreateMonitorSchema_OnRawRecordsOnlyDatabase_AddsSchemaVersionAndPreservesRows()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);
        store.CreateSchema();
        store.Insert(new RawTelemetryRecord(
            Id: null,
            Source: RawTelemetrySources.RawOtlp,
            TraceId: "trace-1",
            ReceivedAt: new DateTimeOffset(2026, 6, 5, 1, 2, 3, TimeSpan.Zero),
            ResourceAttributesJson: null,
            PayloadJson: "{}"));

        store.CreateMonitorSchema();

        using var connection = OpenConnection(tempDirectory.DatabasePath);
        Assert.Equal(1, CountTables(connection, "schema_version"));
        Assert.Equal((long)RawTelemetryStore.MonitorSchemaVersion, ReadMonitorSchemaVersion(connection));

        var record = Assert.Single(store.ListRecords());
        Assert.Equal("trace-1", record.TraceId);
    }

    [Fact]
    public void CreateMonitorSchema_AddsProjectionTablesWithAllowlistColumns()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateMonitorSchema();

        using var connection = OpenConnection(tempDirectory.DatabasePath);
        Assert.Equal(
            [
                "id",
                "raw_record_id",
                "received_at",
                "source",
                "trace_id",
                "client_kind",
                "span_count",
                "projected_at",
            ],
            ReadColumns(connection, "monitor_ingestions"));
        Assert.Equal(
            [
                "id",
                "trace_id",
                "client_kind",
                "experiment_id",
                "task_id",
                "task_category",
                "agent_variant",
                "prompt_version",
                "span_count",
                "tool_call_count",
                "error_count",
                "first_seen_at",
                "last_seen_at",
                "projected_at",
            ],
            ReadColumns(connection, "monitor_traces"));
    }

    [Fact]
    public void CreateMonitorSchema_ProjectionsCarryNoRawOrPiiColumns()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateMonitorSchema();

        using var connection = OpenConnection(tempDirectory.DatabasePath);
        string[] forbidden = ["payload_json", "resource_attributes_json", "user_id", "user_email"];
        foreach (var table in new[] { "monitor_ingestions", "monitor_traces" })
        {
            var columns = ReadColumns(connection, table);
            foreach (var column in forbidden)
            {
                Assert.DoesNotContain(column, columns);
            }
        }
    }

    [Fact]
    public void CreateMonitorSchema_IsIdempotent()
    {
        using var tempDirectory = new TempDirectory();
        var store = new RawTelemetryStore(tempDirectory.DatabasePath);

        store.CreateMonitorSchema();
        store.CreateMonitorSchema();

        using var connection = OpenConnection(tempDirectory.DatabasePath);
        Assert.Equal(1, CountTables(connection, "monitor_ingestions"));
        Assert.Equal(1, CountTables(connection, "monitor_traces"));
        Assert.Equal(1, CountTables(connection, "schema_version"));
        Assert.Equal(1, CountMonitorSchemaRows(connection));
    }

    private static string FixturePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "TestData", "raw-otlp.synthetic.json");
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static IReadOnlyList<string> ReadColumnNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(raw_records);";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(1));
        }

        return columns;
    }

    private static IReadOnlyList<string> ReadIndexNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_list(raw_records);";
        using var reader = command.ExecuteReader();
        var indexes = new List<string>();
        while (reader.Read())
        {
            indexes.Add(reader.GetString(1));
        }

        indexes.Sort(StringComparer.Ordinal);
        return indexes;
    }

    private static int CountTables(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static IReadOnlyList<string> ReadColumns(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid;";
        command.Parameters.AddWithValue("$table", tableName);
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static long ReadMonitorSchemaVersion(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version WHERE component = 'monitor';";
        return (long)command.ExecuteScalar()!;
    }

    private static int CountMonitorSchemaRows(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schema_version WHERE component = 'monitor';";
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ReadCreateSql(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return (string)command.ExecuteScalar()!;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"m2-raw-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            DatabasePath = System.IO.Path.Combine(Path, "raw-store.db");
        }

        public string Path { get; }

        public string DatabasePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, recursive: true);
        }
    }
}

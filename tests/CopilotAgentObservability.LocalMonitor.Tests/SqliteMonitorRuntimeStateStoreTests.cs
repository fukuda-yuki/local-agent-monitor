using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteMonitorRuntimeStateStoreTests
{
    private static readonly DateTimeOffset FirstWrite = new(2026, 7, 17, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public void Upsert_round_trips_latest_mode_and_timestamp_as_one_row()
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(FirstWrite);
        var store = new SqliteMonitorRuntimeStateStore(temp.DatabasePath, time, RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateSchema();

        store.Upsert(sanitizedOnly: false);
        Assert.Equal(
            new MonitorRuntimeState(MonitorRawAccessMode.Available, FirstWrite),
            store.Get());

        var secondWrite = FirstWrite.AddMinutes(1);
        time.Advance(TimeSpan.FromMinutes(1));
        store.Upsert(sanitizedOnly: true);

        Assert.Equal(
            new MonitorRuntimeState(MonitorRawAccessMode.SanitizedOnly, secondWrite),
            store.Get());
        using var connection = Open(temp.DatabasePath);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM monitor_runtime_state;"));
    }

    [Fact]
    public void CreateSchema_migrates_committed_v5_fixture_preserves_data_and_survives_restart()
    {
        using var temp = new MonitorTempDirectory();
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "SchemaMigrations",
            "monitor",
            "monitor-v5.sqlite");
        File.Copy(fixturePath, temp.DatabasePath);

        using (var historical = Open(temp.DatabasePath))
        {
            Assert.Equal(5L, Scalar(historical, "SELECT version FROM schema_version WHERE component = 'monitor';"));
            Assert.Equal("fixture-monitor-v5-trace", Scalar<string>(historical, "SELECT trace_id FROM monitor_traces WHERE id = 503;"));
        }

        var store = new SqliteMonitorRuntimeStateStore(temp.DatabasePath, timeProvider: null, connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter);
        store.CreateSchema();
        AssertMigrationResult(temp.DatabasePath);
        Assert.Null(store.Get());

        new SqliteMonitorRuntimeStateStore(temp.DatabasePath, timeProvider: null, connectionOptions: RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        AssertMigrationResult(temp.DatabasePath);
        Assert.Null(store.Get());
    }

    [Theory]
    [InlineData(false, MonitorRawAccessMode.Available)]
    [InlineData(true, MonitorRawAccessMode.SanitizedOnly)]
    public async Task MonitorHost_startup_records_effective_raw_access_mode(bool sanitizedOnly, MonitorRawAccessMode expectedMode)
    {
        using var temp = new MonitorTempDirectory();
        var time = new MutableTimeProvider(FirstWrite);
        await using var host = await MonitorTestHost.StartAsync(
            temp,
            sanitizedOnly: sanitizedOnly,
            testOptions: new MonitorHostTestOptions
            {
                StartWriter = false,
                StartProjectionWorker = false,
                StartSessionWriter = false,
                StartSessionOtelEnrichment = false,
                TimeProvider = time,
                UseUserSecrets = false,
            });

        var state = new SqliteMonitorRuntimeStateStore(temp.DatabasePath, time).Get();
        Assert.Equal(new MonitorRuntimeState(expectedMode, FirstWrite), state);
    }

    private static void AssertMigrationResult(string databasePath)
    {
        using var connection = Open(databasePath);
        Assert.Equal(6L, Scalar(connection, "SELECT version FROM schema_version WHERE component = 'monitor';"));
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'monitor_runtime_state';"));
        Assert.Equal(501L, Scalar(connection, "SELECT id FROM raw_records WHERE id = 501;"));
        Assert.Equal("fixture-monitor-v5-trace", Scalar<string>(connection, "SELECT trace_id FROM monitor_traces WHERE id = 503;"));
        Assert.Equal("fixture-monitor-v5-span", Scalar<string>(connection, "SELECT span_id FROM monitor_spans WHERE id = 504;"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM source_schema_observations;"));
        Assert.Empty(ReadRows(connection, "monitor_runtime_state"));
    }

    private static SqliteConnection Open(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static long Scalar(SqliteConnection connection, string sql) => Scalar<long>(connection, sql);

    private static string[] ReadRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY rowid;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index => reader.IsDBNull(index) ? "<null>" : reader.GetValue(index).ToString())));
        }
        return rows.ToArray();
    }
}

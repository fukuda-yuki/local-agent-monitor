namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class RawTelemetryStore
{
    private readonly string databasePath;
    private readonly RawTelemetryStoreConnectionOptions connectionOptions;

    public RawTelemetryStore(string databasePath, RawTelemetryStoreConnectionOptions? connectionOptions = null)
    {
        this.databasePath = databasePath;
        this.connectionOptions = connectionOptions ?? RawTelemetryStoreConnectionOptions.Default;
    }

    public const int MonitorSchemaVersion = 1;

    public void CreateSchema()
    {
        EnsureParentDirectory();

        using var connection = OpenConnection();
        ApplyWriteAheadLog(connection);
        EnsureRawRecordsSchema(connection);
    }

    /// <summary>
    /// Idempotent additive migration for the Local Ingestion Monitor: ensures the
    /// raw_records store, then adds the schema_version table and the empty
    /// monitor_ingestions / monitor_traces projection tables defined in
    /// docs/specifications/layers/raw-store-normalization.md. Existing raw_records
    /// rows are preserved; the projection tables are not populated here (M4 owns
    /// projection population).
    /// </summary>
    public void CreateMonitorSchema()
    {
        EnsureParentDirectory();

        using var connection = OpenConnection();
        ApplyWriteAheadLog(connection);
        EnsureRawRecordsSchema(connection);
        EnsureMonitorProjectionSchema(connection);
    }

    private void EnsureParentDirectory()
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private static void EnsureRawRecordsSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS raw_records (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT NOT NULL CHECK (source IN ('raw-otlp', 'collector-output', 'langfuse-export')),
                trace_id TEXT NULL,
                received_at TEXT NOT NULL,
                resource_attributes_json TEXT NULL,
                payload_json TEXT NOT NULL,
                schema_version INTEGER NOT NULL CHECK (schema_version = 1)
            );
            """);
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_trace_id ON raw_records(trace_id);");
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_received_at ON raw_records(received_at);");
        ExecuteNonQuery(
            connection,
            "CREATE INDEX IF NOT EXISTS IX_raw_records_source ON raw_records(source);");
    }

    private static void EnsureMonitorProjectionSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_version (
                component TEXT PRIMARY KEY,
                version INTEGER NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            $"""
            INSERT INTO schema_version (component, version)
            VALUES ('monitor', {MonitorSchemaVersion})
            ON CONFLICT (component) DO UPDATE SET version = excluded.version;
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_ingestions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                raw_record_id INTEGER NOT NULL UNIQUE,
                received_at TEXT NOT NULL,
                source TEXT NOT NULL,
                trace_id TEXT NULL,
                client_kind TEXT NULL,
                span_count INTEGER NULL,
                projected_at TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS monitor_traces (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                trace_id TEXT NOT NULL UNIQUE,
                client_kind TEXT NULL,
                experiment_id TEXT NULL,
                task_id TEXT NULL,
                task_category TEXT NULL,
                agent_variant TEXT NULL,
                prompt_version TEXT NULL,
                span_count INTEGER NULL,
                tool_call_count INTEGER NULL,
                error_count INTEGER NULL,
                first_seen_at TEXT NULL,
                last_seen_at TEXT NULL,
                projected_at TEXT NOT NULL
            );
            """);
    }

    public long Insert(RawTelemetryRecord record)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO raw_records (
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version
            )
            VALUES (
                $source,
                $trace_id,
                $received_at,
                $resource_attributes_json,
                $payload_json,
                $schema_version
            );
            SELECT last_insert_rowid();
            """;
        AddParameter(command, "$source", record.Source);
        AddParameter(command, "$trace_id", record.TraceId);
        AddParameter(command, "$received_at", record.ReceivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddParameter(command, "$resource_attributes_json", record.ResourceAttributesJson);
        AddParameter(command, "$payload_json", record.PayloadJson);
        AddParameter(command, "$schema_version", record.SchemaVersion);
        return (long)command.ExecuteScalar()!;
    }

    public IReadOnlyList<RawTelemetryRecord> ListRecords()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
                id,
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version
            FROM raw_records
            ORDER BY id;
            """;

        using var reader = command.ExecuteReader();
        var records = new List<RawTelemetryRecord>();
        while (reader.Read())
        {
            records.Add(new RawTelemetryRecord(
                Id: reader.GetInt64(0),
                Source: reader.GetString(1),
                TraceId: reader.IsDBNull(2) ? null : reader.GetString(2),
                ReceivedAt: DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ResourceAttributesJson: reader.IsDBNull(4) ? null : reader.GetString(4),
                PayloadJson: reader.GetString(5),
                SchemaVersion: reader.GetInt32(6)));
        }

        return records;
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        ApplyBusyTimeout(connection);
        return connection;
    }

    private void ApplyBusyTimeout(SqliteConnection connection)
    {
        if (connectionOptions.BusyTimeoutMilliseconds is { } busyTimeoutMilliseconds)
        {
            ExecuteNonQuery(connection, $"PRAGMA busy_timeout = {busyTimeoutMilliseconds.ToString(CultureInfo.InvariantCulture)};");
        }
    }

    private void ApplyWriteAheadLog(SqliteConnection connection)
    {
        if (connectionOptions.EnableWriteAheadLog)
        {
            ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL;");
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object? value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }
}

internal sealed record RawTelemetryStoreConnectionOptions(
    bool EnableWriteAheadLog,
    int? BusyTimeoutMilliseconds)
{
    public static RawTelemetryStoreConnectionOptions Default { get; } = new(
        EnableWriteAheadLog: false,
        BusyTimeoutMilliseconds: null);

    public static RawTelemetryStoreConnectionOptions MonitorWriter { get; } = new(
        EnableWriteAheadLog: true,
        BusyTimeoutMilliseconds: 5_000);
}

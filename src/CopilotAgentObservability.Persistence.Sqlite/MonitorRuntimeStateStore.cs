namespace CopilotAgentObservability.Persistence.Sqlite;

public enum MonitorRawAccessMode
{
    Available,
    SanitizedOnly,
}

public sealed record MonitorRuntimeState(
    MonitorRawAccessMode RawAccess,
    DateTimeOffset UpdatedAt);

public sealed class SqliteMonitorRuntimeStateStore
{
    public const int MonitorSchemaVersion = 6;

    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly RawTelemetryStoreConnectionOptions connectionOptions;

    public SqliteMonitorRuntimeStateStore(string databasePath, TimeProvider? timeProvider = null)
        : this(databasePath, timeProvider, RawTelemetryStoreConnectionOptions.Default)
    {
    }

    internal SqliteMonitorRuntimeStateStore(
        string databasePath,
        TimeProvider? timeProvider,
        RawTelemetryStoreConnectionOptions connectionOptions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.connectionOptions = connectionOptions;
    }

    public void CreateSchema()
    {
        EnsureParentDirectory();

        using var connection = OpenConnection();
        ApplyWriteAheadLog(connection);
        using var transaction = connection.BeginTransaction();
        var existingVersion = MonitorSchemaMigrator.ReadMonitorSchemaVersion(connection, transaction);
        if (existingVersion > MonitorSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Monitor schema version {existingVersion} is newer than supported version {MonitorSchemaVersion}.");
        }

        MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
        MonitorSchemaMigrator.EnsureRuntimeStateSchema(connection, transaction);
        MonitorSchemaMigrator.SetMonitorSchemaVersion(connection, transaction, MonitorSchemaVersion);
        transaction.Commit();
    }

    public void Upsert(bool sanitizedOnly) => Upsert(
        sanitizedOnly ? MonitorRawAccessMode.SanitizedOnly : MonitorRawAccessMode.Available,
        timeProvider.GetUtcNow());

    public void Upsert(MonitorRawAccessMode rawAccess, DateTimeOffset updatedAt)
    {
        ValidateRawAccess(rawAccess);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO monitor_runtime_state (id, raw_access, updated_at)
            VALUES (1, $raw_access, $updated_at)
            ON CONFLICT (id) DO UPDATE SET
                raw_access = excluded.raw_access,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$raw_access", ToWireValue(rawAccess));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(updatedAt));
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public MonitorRuntimeState? Get()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT raw_access, updated_at FROM monitor_runtime_state WHERE id = 1;";
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new MonitorRuntimeState(ParseRawAccess(reader.GetString(0)), ParseTimestamp(reader.GetString(1)))
            : null;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        if (connectionOptions.BusyTimeoutMilliseconds is { } timeout)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {timeout.ToString(CultureInfo.InvariantCulture)};";
            command.ExecuteNonQuery();
        }
        return connection;
    }

    private void ApplyWriteAheadLog(SqliteConnection connection)
    {
        if (connectionOptions.EnableWriteAheadLog)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteNonQuery();
        }
    }

    private void EnsureParentDirectory()
    {
        var parentDirectory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }
    }

    private static string ToWireValue(MonitorRawAccessMode rawAccess) => rawAccess switch
    {
        MonitorRawAccessMode.Available => "available",
        MonitorRawAccessMode.SanitizedOnly => "sanitized_only",
        _ => throw new ArgumentOutOfRangeException(nameof(rawAccess)),
    };

    private static MonitorRawAccessMode ParseRawAccess(string rawAccess) => rawAccess switch
    {
        "available" => MonitorRawAccessMode.Available,
        "sanitized_only" => MonitorRawAccessMode.SanitizedOnly,
        _ => throw new InvalidOperationException("Stored monitor raw-access mode is invalid."),
    };

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateRawAccess(MonitorRawAccessMode rawAccess)
    {
        if (!Enum.IsDefined(rawAccess))
        {
            throw new ArgumentOutOfRangeException(nameof(rawAccess));
        }
    }
}

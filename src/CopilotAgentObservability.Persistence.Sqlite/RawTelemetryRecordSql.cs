namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class RawTelemetryRecordSql
{
    public static long Insert(SqliteConnection connection, SqliteTransaction? transaction, RawTelemetryRecord record, byte[]? retentionOwnerToken = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(record);
        retentionOwnerToken ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        if (retentionOwnerToken.Length != 32) throw new ArgumentException("Retention owner token must be 32 bytes.", nameof(retentionOwnerToken));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO raw_records (
                source,
                trace_id,
                received_at,
                resource_attributes_json,
                payload_json,
                schema_version,
                retention_owner_token
            )
            VALUES (
                $source,
                $trace_id,
                $received_at,
                $resource_attributes_json,
                $payload_json,
                $schema_version,
                $retention_owner_token
            );
            SELECT last_insert_rowid();
            """;
        Add(command, "$source", record.Source);
        Add(command, "$trace_id", record.TraceId);
        Add(command, "$received_at", record.ReceivedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$resource_attributes_json", record.ResourceAttributesJson);
        Add(command, "$payload_json", record.PayloadJson);
        Add(command, "$schema_version", record.SchemaVersion);
        Add(command, "$retention_owner_token", retentionOwnerToken);
        return (long)command.ExecuteScalar()!;
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
}

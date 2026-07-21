namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class RawTelemetryStore
{
    public IReadOnlyList<long> ListRecentRawRecordIdsForRepositoryMetadataDiagnostics(
        int limit,
        int maxPayloadBytes,
        int maxTotalPayloadBytes)
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (maxPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        if (maxTotalPayloadBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxTotalPayloadBytes));

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, length(CAST(payload_json AS BLOB)) AS payload_bytes
            FROM raw_records
            WHERE length(CAST(payload_json AS BLOB)) <= $max_payload_bytes
            ORDER BY id DESC
            LIMIT $limit;
            """;
        AddParameter(command, "$max_payload_bytes", maxPayloadBytes);
        AddParameter(command, "$limit", limit);

        using var reader = command.ExecuteReader();
        var ids = new List<long>(limit);
        var totalPayloadBytes = 0L;
        while (reader.Read())
        {
            var payloadBytes = reader.GetInt64(1);
            if (totalPayloadBytes + payloadBytes > maxTotalPayloadBytes)
            {
                break;
            }

            ids.Add(reader.GetInt64(0));
            totalPayloadBytes += payloadBytes;
        }

        return ids;
    }
}

using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class RawTelemetryStore
{
    public RawReceiveActivity GetRawReceiveActivity(DateTimeOffset windowStartInclusive)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT MAX(received_at),
                   SUM(CASE WHEN received_at >= $window_start THEN 1 ELSE 0 END)
            FROM raw_records;
            """;
        AddParameter(command, "$window_start", windowStartInclusive.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var reader = command.ExecuteReader();
        reader.Read();
        var latest = reader.IsDBNull(0)
            ? (DateTimeOffset?)null
            : DateTimeOffset.ParseExact(reader.GetString(0), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        return new RawReceiveActivity(latest, reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
    }
}

internal sealed record RawReceiveActivity(DateTimeOffset? LatestReceivedAt, int RecentReceivedCount);

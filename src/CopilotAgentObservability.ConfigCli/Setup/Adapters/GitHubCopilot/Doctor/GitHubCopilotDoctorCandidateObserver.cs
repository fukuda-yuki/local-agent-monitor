using CopilotAgentObservability.Doctor;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal static class GitHubCopilotDoctorCandidateObserver
{
    public static void Observe(
        string databasePath,
        TimeProvider timeProvider,
        DoctorVerification verification,
        string target)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(verification);

        foreach (var rawRecordId in ReadWindowRawRecordIds(databasePath, verification))
        {
            var observed = GitHubCopilotDoctorEvidenceAdapter.Observe(
                databasePath,
                timeProvider,
                new GitHubCopilotDoctorEvidenceSelection(
                    verification.VerificationId,
                    target,
                    rawRecordId,
                    NativeSession: null));
            if (observed.ObservationResult.Code != DoctorResultCode.VerificationActive)
            {
                return;
            }
        }
    }

    private static IReadOnlyList<long> ReadWindowRawRecordIds(
        string databasePath,
        DoctorVerification verification)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            if (!TableExists(connection, "raw_records") ||
                !TableExists(connection, "source_schema_observations"))
            {
                return [];
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT DISTINCT r.id,r.received_at FROM raw_records r " +
                "JOIN source_schema_observations o ON o.raw_record_id=r.id " +
                "WHERE r.source='raw-otlp' AND o.source_surface='raw-otlp' " +
                "AND o.source_adapter='raw-otlp' " +
                "AND r.received_at >= $started_at AND r.received_at < $expires_at " +
                "ORDER BY r.received_at COLLATE BINARY,r.id;";
            command.Parameters.AddWithValue("$started_at", Timestamp(verification.StartedAt));
            command.Parameters.AddWithValue("$expires_at", Timestamp(verification.ExpiresAt));
            using var reader = command.ExecuteReader();
            var rawRecordIds = new List<long>();
            while (reader.Read())
            {
                rawRecordIds.Add(reader.GetInt64(0));
            }
            return rawRecordIds;
        }
        catch (SqliteException)
        {
            return [];
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(
            "O",
            System.Globalization.CultureInfo.InvariantCulture);
}

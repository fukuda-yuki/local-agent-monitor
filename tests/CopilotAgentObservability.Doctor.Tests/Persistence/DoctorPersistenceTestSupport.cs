using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Doctor.Tests.Persistence;

internal sealed class DoctorTestDatabase : IDisposable
{
    private readonly string directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"doctor-store-tests-{Guid.NewGuid():N}");

    public DoctorTestDatabase()
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
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=ON;";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(directory, recursive: true);
    }

    public static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        return command.ExecuteScalar();
    }

    public static IReadOnlyList<string> Rows(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(index =>
                reader.IsDBNull(index)
                    ? "<null>"
                    : Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture))));
        }
        return rows;
    }

    public static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        }
        command.ExecuteNonQuery();
    }
}

internal sealed class DoctorTestTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal static class DoctorTestData
{
    private static long nextCandidateId;

    public static readonly DateTimeOffset Now = new(2026, 7, 16, 1, 2, 3, TimeSpan.Zero);

    public static DoctorEvidenceCandidate Candidate(
        DoctorVerification verification,
        string evidenceRef,
        DoctorEvidenceClass evidenceClass = DoctorEvidenceClass.RealSource,
        DoctorEvidenceKind evidenceKind = DoctorEvidenceKind.Ingest,
        string? sourceSurface = null,
        string? sourceAdapter = "otel",
        DateTimeOffset? observedAt = null,
        DateTimeOffset? expiresAt = null) =>
        new(
            $"01890abc-def0-7000-8000-{Interlocked.Increment(ref nextCandidateId):x12}",
            verification.VerificationId,
            sourceSurface ?? verification.ExpectedSourceSurface,
            sourceAdapter,
            evidenceClass,
            evidenceKind,
            evidenceRef,
            observedAt ?? Now,
            expiresAt ?? Now.AddMinutes(5));
}

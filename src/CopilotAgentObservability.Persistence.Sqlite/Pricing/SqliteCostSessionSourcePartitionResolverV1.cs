using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Pricing;

internal enum CostSessionSourcePartitionStateV1
{
    Missing,
    Incomplete,
    Mixed,
    Resolved,
}

internal sealed record CostSessionSourcePartitionResultV1(
    CostSessionSourcePartitionStateV1 State,
    int ObservationCount,
    string Digest,
    string? SourceSurface,
    string? SourceApplicationVersion);

internal static class SqliteCostSessionSourcePartitionResolverV1
{
    private const int MaximumObservations = 256;

    internal static CostSessionSourcePartitionResultV1 Resolve(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        if (transaction.Connection != connection
            || connection.State != System.Data.ConnectionState.Open
            || !Guid.TryParseExact(sessionId, "D", out var parsed)
            || parsed.ToString("D") != sessionId)
            throw new ArgumentException("Cost Session source-partition input is invalid.");

        var observations = new List<Observation>();
        ReadRuns(connection, transaction, sessionId, observations);
        ReadEvents(connection, transaction, sessionId, observations);
        ReadSourceObservations(connection, transaction, sessionId, observations);

        var overflow = observations.Count > MaximumObservations;
        var bounded = observations.Take(MaximumObservations + 1).ToArray();
        var digest = CreateDigest(bounded);
        if (overflow)
            return new(CostSessionSourcePartitionStateV1.Incomplete, bounded.Length, digest, null, null);
        if (bounded.Length == 0)
            return new(CostSessionSourcePartitionStateV1.Missing, 0, digest, null, null);

        var malformed = bounded.Any(item =>
            item.Surface is null
            || item.MappedSurface is null
            || item.RequiresVersion && item.ApplicationVersion is null
            || item.AmbiguousOwnership);
        var versions = bounded
            .Select(item => item.ApplicationVersion)
            .Where(item => item is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (versions.Length == 0) malformed = true;
        if (malformed)
            return new(CostSessionSourcePartitionStateV1.Incomplete, bounded.Length, digest, null, null);

        var surfaces = bounded
            .Select(item => item.MappedSurface!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (surfaces.Length != 1 || versions.Length != 1)
            return new(CostSessionSourcePartitionStateV1.Mixed, bounded.Length, digest, null, null);

        return new(
            CostSessionSourcePartitionStateV1.Resolved,
            bounded.Length,
            digest,
            surfaces[0],
            versions[0]);
    }

    private static void ReadRuns(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        ICollection<Observation> observations)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT run_id,session_id,trace_id,source_surface,status
            FROM session_runs
            WHERE session_id=$session
            ORDER BY run_id COLLATE BINARY
            LIMIT 258;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var surface = reader.IsDBNull(3) ? null : reader.GetString(3);
            observations.Add(new(
                0,
                "session_run",
                [
                    reader.GetString(0),
                    reader.GetString(1),
                    Nullable(reader, 2),
                    Nullable(reader, 3),
                    reader.GetString(4),
                ],
                surface,
                MapSurface(surface),
                null,
                false,
                false));
        }
    }

    private static void ReadEvents(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        ICollection<Observation> observations)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT event_id,session_id,run_id,trace_id,source_surface,
                   source_application_version,source_adapter,source_event_id,occurred_at
            FROM session_events
            WHERE session_id=$session
            ORDER BY event_id COLLATE BINARY
            LIMIT 258;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var surface = reader.IsDBNull(4) ? null : reader.GetString(4);
            var version = reader.IsDBNull(5) ? null : reader.GetString(5);
            observations.Add(new(
                1,
                "session_event",
                [
                    reader.GetString(0),
                    reader.GetString(1),
                    Nullable(reader, 2),
                    Nullable(reader, 3),
                    Nullable(reader, 4),
                    Nullable(reader, 5),
                    reader.GetString(6),
                    reader.GetString(7),
                    reader.GetString(8),
                ],
                surface,
                MapSurface(surface),
                version,
                false,
                false));
        }
    }

    private static void ReadSourceObservations(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        ICollection<Observation> observations)
    {
        if (!TableExists(connection, transaction, "source_schema_observations")
            || !TableExists(connection, transaction, "monitor_spans"))
            return;

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT o.observation_id,o.raw_record_id,o.source_surface,
                   o.source_application_version,o.source_adapter,o.adapter_version,
                   o.schema_fingerprint,o.observed_at,
                   COUNT(DISTINCT owner.session_id)
            FROM source_schema_observations o
            JOIN monitor_spans ms ON ms.raw_record_id=o.raw_record_id
            JOIN (
                SELECT trace_id,session_id FROM session_runs WHERE trace_id IS NOT NULL
                UNION
                SELECT trace_id,session_id FROM session_events WHERE trace_id IS NOT NULL
            ) owner ON owner.trace_id=ms.trace_id
            WHERE EXISTS(
                SELECT 1 FROM (
                    SELECT trace_id FROM session_runs
                    WHERE session_id=$session AND trace_id IS NOT NULL
                    UNION
                    SELECT trace_id FROM session_events
                    WHERE session_id=$session AND trace_id IS NOT NULL
                ) mine WHERE mine.trace_id=ms.trace_id)
            GROUP BY o.observation_id,o.raw_record_id,o.source_surface,
                     o.source_application_version,o.source_adapter,o.adapter_version,
                     o.schema_fingerprint,o.observed_at
            ORDER BY o.raw_record_id,o.observation_id COLLATE BINARY
            LIMIT 258;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var surface = reader.IsDBNull(2) ? null : reader.GetString(2);
            var version = reader.IsDBNull(3) ? null : reader.GetString(3);
            var ambiguous = reader.GetInt64(8) != 1;
            observations.Add(new(
                2,
                "source_schema_observation",
                [
                    reader.GetString(0),
                    reader.GetInt64(1).ToString(CultureInfo.InvariantCulture),
                    Nullable(reader, 2),
                    Nullable(reader, 3),
                    Nullable(reader, 4),
                    Nullable(reader, 5),
                    Nullable(reader, 6),
                    reader.GetString(7),
                ],
                surface,
                surface,
                version,
                true,
                ambiguous));
        }
    }

    private static string CreateDigest(IEnumerable<Observation> observations)
    {
        using var stream = new MemoryStream();
        Frame(stream, "cost-session-source-partition/v1");
        foreach (var observation in observations)
        {
            Frame(stream, observation.Rank.ToString(CultureInfo.InvariantCulture));
            Frame(stream, observation.Kind);
            foreach (var value in observation.Identity) FrameNullable(stream, value);
            FrameNullable(stream, observation.Surface);
            FrameNullable(stream, observation.MappedSurface);
            FrameNullable(stream, observation.ApplicationVersion);
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static string? MapSurface(string? surface) =>
        surface switch
        {
            "vscode" => "github-copilot-vscode",
            "copilot-cli" => "github-copilot-cli",
            "claude-code" => "claude-code",
            _ => null,
        };

    private static bool TableExists(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static void FrameNullable(Stream stream, string? value)
    {
        Frame(stream, value is null ? "0" : "1");
        if (value is not null) Frame(stream, value);
    }

    private static void Frame(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        stream.Write(length);
        stream.Write(bytes);
    }

    private sealed record Observation(
        int Rank,
        string Kind,
        IReadOnlyList<string?> Identity,
        string? Surface,
        string? MappedSurface,
        string? ApplicationVersion,
        bool RequiresVersion,
        bool AmbiguousOwnership);
}

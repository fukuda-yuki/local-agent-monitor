using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal interface IInstructionFindingHandoffStore
{
    void CreateSchema();
    void Save(InstructionFindingHandoffV1 handoff, DateTimeOffset createdAt);
    InstructionFindingHandoffV1? Get(long analysisRunId);
}

internal sealed class SqliteInstructionFindingHandoffStore : IInstructionFindingHandoffStore
{
    private readonly string databasePath;

    internal SqliteInstructionFindingHandoffStore(string databasePath) => this.databasePath = databasePath;

    public void CreateSchema()
    {
        using var connection = OpenConnection();
        CreateSchema(connection, null);
    }

    internal static void CreateSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS instruction_finding_handoffs (
                analysis_run_id INTEGER PRIMARY KEY,
                schema_version TEXT NOT NULL,
                payload_json TEXT NOT NULL,
                payload_sha256 TEXT NOT NULL CHECK(length(payload_sha256) = 64 AND payload_sha256 = lower(payload_sha256)),
                created_at TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void Save(InstructionFindingHandoffV1 handoff, DateTimeOffset createdAt)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        Save(connection, transaction, handoff, createdAt);
        transaction.Commit();
    }

    internal static void Save(
        SqliteConnection connection,
        SqliteTransaction transaction,
        InstructionFindingHandoffV1 handoff,
        DateTimeOffset createdAt)
    {
        var payloadBytes = InstructionFindingJsonV1.Serialize(handoff);
        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        var checksum = Checksum(payloadBytes);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO instruction_finding_handoffs (
                analysis_run_id, schema_version, payload_json, payload_sha256, created_at
            ) VALUES (
                $analysis_run_id, $schema_version, $payload_json, $payload_sha256, $created_at
            ) ON CONFLICT(analysis_run_id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$analysis_run_id", handoff.AnalysisRunId);
        insert.Parameters.AddWithValue("$schema_version", handoff.SchemaVersion);
        insert.Parameters.AddWithValue("$payload_json", payloadJson);
        insert.Parameters.AddWithValue("$payload_sha256", checksum);
        insert.Parameters.AddWithValue("$created_at", createdAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        var inserted = insert.ExecuteNonQuery();
        if (inserted == 0)
        {
            var existing = ReadStored(connection, transaction, handoff.AnalysisRunId);
            if (existing is null
                || !string.Equals(existing.Value.SchemaVersion, handoff.SchemaVersion, StringComparison.Ordinal)
                || !string.Equals(existing.Value.PayloadJson, payloadJson, StringComparison.Ordinal)
                || !string.Equals(existing.Value.Checksum, checksum, StringComparison.Ordinal))
                throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.ConflictingPersistence);
        }

    }

    public InstructionFindingHandoffV1? Get(long analysisRunId)
    {
        if (analysisRunId <= 0)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidContract);
        using var connection = OpenConnection();
        var stored = ReadStored(connection, null, analysisRunId);
        if (stored is null) return null;
        if (!string.Equals(stored.Value.SchemaVersion, InstructionFindingContractsV1.HandoffSchemaVersion, StringComparison.Ordinal))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidPersistence);
        var payloadBytes = Encoding.UTF8.GetBytes(stored.Value.PayloadJson);
        if (!string.Equals(stored.Value.Checksum, Checksum(payloadBytes), StringComparison.Ordinal))
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidPersistence);
        var handoff = InstructionFindingJsonV1.Deserialize(payloadBytes);
        if (handoff.AnalysisRunId != analysisRunId)
            throw new InstructionFindingValidationException(InstructionFindingValidationCodeV1.InvalidPersistence);
        return handoff;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private static StoredHandoff? ReadStored(SqliteConnection connection, SqliteTransaction? transaction, long analysisRunId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT schema_version, payload_json, payload_sha256
            FROM instruction_finding_handoffs
            WHERE analysis_run_id = $analysis_run_id;
            """;
        command.Parameters.AddWithValue("$analysis_run_id", analysisRunId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new StoredHandoff(reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private static string Checksum(ReadOnlySpan<byte> payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private readonly record struct StoredHandoff(string SchemaVersion, string PayloadJson, string Checksum);
}

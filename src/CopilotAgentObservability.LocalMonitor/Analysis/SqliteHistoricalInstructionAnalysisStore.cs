using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.InstructionFindings;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class SqliteHistoricalInstructionAnalysisStoreV1
{
    internal const string SchemaComponent = "historical_instruction_analysis";
    internal const int SchemaVersion = 1;
    private const string RunsTable = "historical_instruction_analysis_runs";
    private const string RunsTableSql =
        """
        CREATE TABLE historical_instruction_analysis_runs (
            run_id INTEGER PRIMARY KEY AUTOINCREMENT,
            request_schema_version TEXT NOT NULL,
            extraction_id TEXT NOT NULL,
            extraction_sha256 TEXT NOT NULL CHECK(length(extraction_sha256)=64 AND extraction_sha256=lower(extraction_sha256)),
            model TEXT NOT NULL,
            provider TEXT NOT NULL,
            configuration_sha256 TEXT NOT NULL CHECK(length(configuration_sha256)=64 AND configuration_sha256=lower(configuration_sha256)),
            timeout_ms INTEGER NOT NULL CHECK(timeout_ms>0 AND timeout_ms<=3600000),
            prompt_template_version TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN (
                'queued','running','succeeded','zero_findings','no_eligible_sessions','content_unavailable',
                'stale_extraction','invalid_citation','provider_partial','provider_failed','timed_out')),
            requested_at TEXT NOT NULL,
            started_at TEXT NULL,
            completed_at TEXT NULL,
            receipt_json TEXT NULL CHECK(receipt_json IS NULL OR length(CAST(receipt_json AS BLOB))<=67108864),
            receipt_sha256 TEXT NULL,
            handoff_json TEXT NULL CHECK(handoff_json IS NULL OR length(CAST(handoff_json AS BLOB))<=1048576),
            handoff_sha256 TEXT NULL,
            CHECK((receipt_json IS NULL)=(receipt_sha256 IS NULL)),
            CHECK((handoff_json IS NULL)=(handoff_sha256 IS NULL))
        );
        """;
    private readonly string databasePath;

    internal SqliteHistoricalInstructionAnalysisStoreV1(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
    }

    internal void CreateSchema()
    {
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var version = ReadSchemaVersion(connection, transaction);
            if (version is null)
            {
                if (AnyOwnedObjectExists(connection, transaction)) throw InvalidPersistence();
                Execute(connection, transaction,
                    "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
                Execute(connection, transaction, RunsTableSql);
                Execute(connection, transaction,
                    "INSERT INTO schema_version(component,version) VALUES('historical_instruction_analysis',1);");
            }
            if (!IsSchemaValid(connection, transaction)) throw InvalidPersistence();
            transaction.Commit();
        }
        catch (HistoricalInstructionAnalysisValidationException) { throw; }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException
            or InvalidCastException or FormatException or OverflowException)
        {
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence,
                exception);
        }
    }

    internal long Start(HistoricalInstructionAnalysisRequestV1 request, DateTimeOffset requestedAt)
    {
        HistoricalInstructionAnalysisJsonV1.ValidateRequest(request);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO historical_instruction_analysis_runs (
                request_schema_version,extraction_id,extraction_sha256,model,provider,configuration_sha256,
                timeout_ms,prompt_template_version,state,requested_at
            ) VALUES (
                $schema,$extraction,$extraction_sha,$model,$provider,$configuration_sha,
                $timeout,$template,'queued',$requested
            );
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$schema", request.SchemaVersion);
        command.Parameters.AddWithValue("$extraction", request.ExtractionId);
        command.Parameters.AddWithValue("$extraction_sha", request.ExtractionSha256);
        command.Parameters.AddWithValue("$model", request.Model);
        command.Parameters.AddWithValue("$provider", request.Provider);
        command.Parameters.AddWithValue("$configuration_sha", request.ConfigurationSha256);
        command.Parameters.AddWithValue("$timeout", request.TimeoutMilliseconds);
        command.Parameters.AddWithValue("$template", request.PromptTemplateVersion);
        command.Parameters.AddWithValue("$requested", Utc(requestedAt));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal void MarkRunning(long runId, DateTimeOffset startedAt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE historical_instruction_analysis_runs
            SET state='running',started_at=$started
            WHERE run_id=$id AND state='queued' AND started_at IS NULL AND completed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", runId);
        command.Parameters.AddWithValue("$started", Utc(startedAt));
        if (command.ExecuteNonQuery() != 1) throw InvalidTransition();
    }

    internal void Complete(
        long runId,
        HistoricalInstructionAnalysisStateV1 state,
        HistoricalInstructionAnalysisReceiptV1? receipt,
        byte[]? handoffBytes,
        DateTimeOffset completedAt)
    {
        if (!IsTerminal(state)) throw InvalidTransition();
        var successful = state is HistoricalInstructionAnalysisStateV1.Succeeded or HistoricalInstructionAnalysisStateV1.ZeroFindings;
        if (successful != (receipt is not null && handoffBytes is { Length: > 0 })) throw InvalidTransition();

        string? receiptJson = null;
        string? receiptSha = null;
        string? handoffJson = null;
        string? handoffSha = null;
        if (successful)
        {
            var exactReceipt = receipt!;
            var exactHandoff = handoffBytes!;
            ValidateSuccessfulPair(runId, state, exactReceipt, exactHandoff);
            var receiptBytes = HistoricalInstructionAnalysisJsonV1.Serialize(exactReceipt);
            receiptJson = Encoding.UTF8.GetString(receiptBytes);
            receiptSha = Sha256(receiptBytes);
            handoffJson = Encoding.UTF8.GetString(exactHandoff);
            handoffSha = Sha256(exactHandoff);
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        var existing = Read(connection, transaction, runId) ?? throw InvalidTransition();
        if (existing.State != HistoricalInstructionAnalysisStateV1.Running
            || receipt is not null && !ReceiptMatchesRequest(receipt, existing.Request))
            throw InvalidTransition();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            UPDATE historical_instruction_analysis_runs
            SET state=$state,completed_at=$completed,
                receipt_json=$receipt,receipt_sha256=$receipt_sha,
                handoff_json=$handoff,handoff_sha256=$handoff_sha
            WHERE run_id=$id AND state='running' AND completed_at IS NULL;
            """;
        command.Parameters.AddWithValue("$state", state.ToWireValue());
        command.Parameters.AddWithValue("$completed", Utc(completedAt));
        command.Parameters.AddWithValue("$receipt", (object?)receiptJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$receipt_sha", (object?)receiptSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$handoff", (object?)handoffJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$handoff_sha", (object?)handoffSha ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", runId);
        if (command.ExecuteNonQuery() != 1) throw InvalidTransition();
        transaction.Commit();
    }

    internal HistoricalInstructionAnalysisRunV1? Get(long runId)
    {
        if (runId <= 0) throw new HistoricalInstructionAnalysisValidationException(HistoricalInstructionAnalysisValidationCodeV1.InvalidContract);
        try
        {
            using var connection = Open();
            return Read(connection, null, runId);
        }
        catch (HistoricalInstructionAnalysisValidationException) { throw; }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or InvalidCastException
            or FormatException or ArgumentException or OverflowException or NullReferenceException)
        {
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence,
                exception);
        }
    }

    private static HistoricalInstructionAnalysisRunV1? Read(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long runId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT request_schema_version,extraction_id,extraction_sha256,model,provider,configuration_sha256,
                   timeout_ms,prompt_template_version,state,requested_at,started_at,completed_at,
                   receipt_json,receipt_sha256,handoff_json,handoff_sha256
            FROM historical_instruction_analysis_runs WHERE run_id=$id;
            """;
        command.Parameters.AddWithValue("$id", runId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var request = new HistoricalInstructionAnalysisRequestV1(
            reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetInt32(6), reader.GetString(7));
        HistoricalInstructionAnalysisJsonV1.ValidateRequest(request);
        var state = HistoricalInstructionAnalysisStateWireV1.Parse(reader.GetString(8));
        var requestedAt = ParseUtc(reader.GetString(9));
        DateTimeOffset? startedAt = reader.IsDBNull(10) ? null : ParseUtc(reader.GetString(10));
        DateTimeOffset? completedAt = reader.IsDBNull(11) ? null : ParseUtc(reader.GetString(11));
        if (state == HistoricalInstructionAnalysisStateV1.Queued && (startedAt is not null || completedAt is not null)
            || state == HistoricalInstructionAnalysisStateV1.Running && (startedAt is null || completedAt is not null)
            || IsTerminal(state) && (startedAt is null || completedAt is null))
            throw InvalidPersistence();

        var hasReceipt = !reader.IsDBNull(12);
        var hasHandoff = !reader.IsDBNull(14);
        if (hasReceipt != !reader.IsDBNull(13) || hasHandoff != !reader.IsDBNull(15) || hasReceipt != hasHandoff)
            throw InvalidPersistence();
        HistoricalInstructionAnalysisReceiptV1? receipt = null;
        byte[] handoffBytes = [];
        if (hasReceipt)
        {
            var receiptBytes = Encoding.UTF8.GetBytes(reader.GetString(12));
            if (receiptBytes.Length > HistoricalInstructionAnalysisContractsV1.MaximumReceiptBytes
                || Sha256(receiptBytes) != reader.GetString(13)) throw InvalidPersistence();
            receipt = HistoricalInstructionAnalysisJsonV1.Deserialize(receiptBytes);
            handoffBytes = Encoding.UTF8.GetBytes(reader.GetString(14));
            if (handoffBytes.Length > InstructionFindingHandoffConsumerV1.MaxPayloadBytes
                || Sha256(handoffBytes) != reader.GetString(15)) throw InvalidPersistence();
            ValidateSuccessfulPair(runId, state, receipt, handoffBytes);
            if (!ReceiptMatchesRequest(receipt, request)) throw InvalidPersistence();
        }
        else if (state is HistoricalInstructionAnalysisStateV1.Succeeded or HistoricalInstructionAnalysisStateV1.ZeroFindings)
        {
            throw InvalidPersistence();
        }

        return new(runId, request, state, requestedAt, startedAt, completedAt, receipt, handoffBytes);
    }

    private static void ValidateSuccessfulPair(
        long runId,
        HistoricalInstructionAnalysisStateV1 state,
        HistoricalInstructionAnalysisReceiptV1 receipt,
        byte[] handoffBytes)
    {
        try
        {
            HistoricalInstructionAnalysisJsonV1.ValidateReceipt(receipt);
            if (receipt.RunId != runId || receipt.State != state
                || receipt.HandoffSha256 != Sha256(handoffBytes)
                || InstructionFindingHandoffConsumerV1.Validate(handoffBytes) != runId)
                throw InvalidPersistence();
            var handoff = InstructionFindingJsonV1.Deserialize(handoffBytes);
            if (!receipt.Findings.Select(finding => finding.FindingId)
                    .SequenceEqual(handoff.Findings.Select(finding => finding.FindingId), StringComparer.Ordinal)
                || (state == HistoricalInstructionAnalysisStateV1.ZeroFindings) != (handoff.Findings.Count == 0))
                throw InvalidPersistence();
            foreach (var support in receipt.Findings)
            {
                var finding = handoff.Findings.SingleOrDefault(value => value.FindingId == support.FindingId);
                if (finding is null || finding.Verdict != support.Verdict
                    || finding.CandidateEligibility != support.CandidateEligibility)
                    throw InvalidPersistence();
            }
        }
        catch (InstructionFindingHandoffConsumerValidationException exception)
        {
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence,
                exception);
        }
    }

    private static bool ReceiptMatchesRequest(
        HistoricalInstructionAnalysisReceiptV1 receipt,
        HistoricalInstructionAnalysisRequestV1 request) =>
        receipt.ExtractionId == request.ExtractionId
        && receipt.ExtractionSha256 == request.ExtractionSha256
        && receipt.Model == request.Model
        && receipt.Provider == request.Provider
        && receipt.ConfigurationSha256 == request.ConfigurationSha256
        && receipt.TimeoutMilliseconds == request.TimeoutMilliseconds
        && receipt.PromptTemplateVersion == request.PromptTemplateVersion;

    private static bool IsTerminal(HistoricalInstructionAnalysisStateV1 state) =>
        state is not (HistoricalInstructionAnalysisStateV1.Queued or HistoricalInstructionAnalysisStateV1.Running);

    private static DateTimeOffset ParseUtc(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || parsed.Offset != TimeSpan.Zero || parsed.ToString("O", CultureInfo.InvariantCulture) != value)
            throw InvalidPersistence();
        return parsed;
    }

    private static string Utc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static long? ReadSchemaVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (!TableExists(connection, transaction, "schema_version")) return null;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_version WHERE component='historical_instruction_analysis';";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool IsSchemaValid(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (ReadSchemaVersion(connection, transaction) != SchemaVersion) return false;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", RunsTable);
        return command.ExecuteScalar() is string actual
            && NormalizeSql(actual) == NormalizeSql(RunsTableSql);
    }

    private static bool AnyOwnedObjectExists(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT count(*) FROM sqlite_schema WHERE name GLOB 'historical_instruction_analysis_*';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", name);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static string NormalizeSql(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
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

    private static HistoricalInstructionAnalysisValidationException InvalidTransition() =>
        new(HistoricalInstructionAnalysisValidationCodeV1.InvalidTransition);

    private static HistoricalInstructionAnalysisValidationException InvalidPersistence() =>
        new(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence);
}

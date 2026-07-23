using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.HistoricalInstructionAnalysis;

internal static class HistoricalInstructionAnalysisSchemaV1
{
    internal const string Component = "historical_instruction_analysis";
    internal const int Version = 1;
    internal const string RunsTable = "historical_instruction_analysis_runs";

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
            dataset_projection_json TEXT NOT NULL CHECK(length(CAST(dataset_projection_json AS BLOB))<=67108864),
            dataset_projection_sha256 TEXT NOT NULL CHECK(length(dataset_projection_sha256)=64 AND dataset_projection_sha256=lower(dataset_projection_sha256)),
            state TEXT NOT NULL CHECK(state IN (
                'queued','running','succeeded','zero_findings','no_eligible_sessions','content_unavailable',
                'stale_extraction','extraction_invalid','invalid_citation','provider_partial','provider_failed','timed_out','canceled')),
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

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        Execute(connection, transaction,
            "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        var version = ReadVersion(connection, transaction);
        if (version is > Version)
            throw new InvalidOperationException("The historical instruction analysis schema is newer than this runtime.");
        if (version is null)
        {
            if (AnyOwnedObjectExists(connection, transaction))
                throw new InvalidOperationException("The historical instruction analysis schema is partial.");
            Execute(connection, transaction, RunsTableSql);
            Execute(connection, transaction,
                "INSERT INTO schema_version(component,version) VALUES('historical_instruction_analysis',1);");
        }
        if (!IsValid(connection, transaction))
            throw new InvalidOperationException("The historical instruction analysis schema is partial.");
    }

    internal static bool IsValid(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (ReadVersion(connection, transaction) != Version) return false;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", RunsTable);
        return command.ExecuteScalar() is string actual
            && NormalizeSql(actual) == NormalizeSql(RunsTableSql)
            && ReadOwnedObjects(connection, transaction).SequenceEqual([$"table:{RunsTable}"], StringComparer.Ordinal);
    }

    private static long? ReadVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_version WHERE component='historical_instruction_analysis';";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static bool AnyOwnedObjectExists(SqliteConnection connection, SqliteTransaction transaction) =>
        ReadOwnedObjects(connection, transaction).Count != 0;

    private static IReadOnlyList<string> ReadOwnedObjects(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT type || ':' || name
            FROM sqlite_schema
            WHERE (name GLOB 'historical_instruction_analysis_*'
                OR tbl_name GLOB 'historical_instruction_analysis_*')
              AND name NOT GLOB 'sqlite_autoindex_*'
            ORDER BY type,name;
            """;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
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
}

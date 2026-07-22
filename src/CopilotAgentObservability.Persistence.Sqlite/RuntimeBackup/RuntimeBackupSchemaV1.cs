using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal static class RuntimeBackupSchemaV1
{
    internal const int Version = 1;
    private const string ReceiptTableSql = """
        CREATE TABLE runtime_backup_receipts(
            operation_id TEXT NOT NULL PRIMARY KEY CHECK(
                typeof(operation_id)='text'
                AND length(CAST(operation_id AS BLOB))=36
                AND operation_id=lower(operation_id)
                AND substr(operation_id,9,1)='-'
                AND substr(operation_id,14,1)='-'
                AND substr(operation_id,15,1)='7'
                AND substr(operation_id,19,1)='-'
                AND substr(operation_id,20,1) IN ('8','9','a','b')
                AND substr(operation_id,24,1)='-'
                AND length(replace(operation_id,'-',''))=32
                AND replace(operation_id,'-','') NOT GLOB '*[^0-9a-f]*'),
            operation_kind TEXT NOT NULL CHECK(typeof(operation_kind)='text' AND operation_kind IN ('backup','restore')),
            artifact_sha256 TEXT NOT NULL CHECK(typeof(artifact_sha256)='text' AND length(CAST(artifact_sha256 AS BLOB))=64 AND artifact_sha256 NOT GLOB '*[^0-9a-f]*'),
            result_code TEXT NOT NULL CHECK(typeof(result_code)='text' AND result_code IN ('backup_succeeded','restore_succeeded')),
            occurred_at TEXT NOT NULL CHECK(
                typeof(occurred_at)='text'
                AND length(CAST(occurred_at AS BLOB))=33
                AND occurred_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
                AND CAST(substr(occurred_at,1,4) AS INTEGER) BETWEEN 1 AND 9999
                AND CAST(substr(occurred_at,6,2) AS INTEGER) BETWEEN 1 AND 12
                AND CAST(substr(occurred_at,9,2) AS INTEGER) BETWEEN 1 AND CASE CAST(substr(occurred_at,6,2) AS INTEGER)
                    WHEN 2 THEN CASE WHEN CAST(substr(occurred_at,1,4) AS INTEGER)%400=0 OR (CAST(substr(occurred_at,1,4) AS INTEGER)%4=0 AND CAST(substr(occurred_at,1,4) AS INTEGER)%100<>0) THEN 29 ELSE 28 END
                    WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30 ELSE 31 END
                AND CAST(substr(occurred_at,12,2) AS INTEGER) BETWEEN 0 AND 23
                AND CAST(substr(occurred_at,15,2) AS INTEGER) BETWEEN 0 AND 59
                AND CAST(substr(occurred_at,18,2) AS INTEGER) BETWEEN 0 AND 59
                AND julianday(occurred_at) IS NOT NULL),
            reintroduction_count INTEGER NOT NULL CHECK(typeof(reintroduction_count)='integer' AND reintroduction_count BETWEEN 0 AND 2147483647),
            pre_restore_backup_created INTEGER NOT NULL CHECK(typeof(pre_restore_backup_created)='integer' AND pre_restore_backup_created IN (0,1)),
            CHECK(
                (operation_kind='backup' AND result_code='backup_succeeded' AND reintroduction_count=0 AND pre_restore_backup_created=0)
                OR (operation_kind='restore' AND result_code='restore_succeeded'))
        );
        """;
    internal const string ReceiptNoUpdateTriggerSql =
        "CREATE TRIGGER runtime_backup_receipts_no_update BEFORE UPDATE ON runtime_backup_receipts BEGIN SELECT RAISE(ABORT,'runtime_backup_receipts_append_only'); END;";
    internal const string ReceiptNoDeleteTriggerSql =
        "CREATE TRIGGER runtime_backup_receipts_no_delete BEFORE DELETE ON runtime_backup_receipts BEGIN SELECT RAISE(ABORT,'runtime_backup_receipts_append_only'); END;";
    internal const string ReceiptNoReplaceTriggerSql =
        "CREATE TRIGGER runtime_backup_receipts_no_replace BEFORE INSERT ON runtime_backup_receipts WHEN EXISTS(SELECT 1 FROM runtime_backup_receipts WHERE operation_id=NEW.operation_id) BEGIN SELECT RAISE(ABORT,'runtime_backup_receipts_append_only'); END;";
    private static readonly string[] ReceiptColumns =
    [
        "operation_id", "operation_kind", "artifact_sha256", "result_code", "occurred_at", "reintroduction_count", "pre_restore_backup_created"
    ];

    internal static void Ensure(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        var existing = Scalar(connection, transaction, "SELECT version FROM schema_version WHERE component='runtime_backup';");
        if (existing is not null && Convert.ToInt32(existing, CultureInfo.InvariantCulture) != Version)
            throw new InvalidOperationException(RuntimeBackupErrorCodes.RestoreIncompatible);
        if (existing is null)
            Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('runtime_backup',1);");
        if (existing is null) Execute(connection, transaction, ReceiptTableSql);
        Execute(connection, transaction, ReceiptNoUpdateTriggerSql.Replace("CREATE TRIGGER ", "CREATE TRIGGER IF NOT EXISTS ", StringComparison.Ordinal));
        Execute(connection, transaction, ReceiptNoDeleteTriggerSql.Replace("CREATE TRIGGER ", "CREATE TRIGGER IF NOT EXISTS ", StringComparison.Ordinal));
        Execute(connection, transaction, ReceiptNoReplaceTriggerSql.Replace("CREATE TRIGGER ", "CREATE TRIGGER IF NOT EXISTS ", StringComparison.Ordinal));
        if (!IsValid(connection, transaction)) throw new InvalidOperationException(RuntimeBackupErrorCodes.RestoreIncompatible);
    }

    internal static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM sqlite_schema WHERE type='table' AND name IN ('schema_version','runtime_backup_receipts');") ?? 0, CultureInfo.InvariantCulture) != 2)
            return false;
        using var columns = connection.CreateCommand();
        columns.Transaction = transaction;
        columns.CommandText = "SELECT name FROM pragma_table_info('runtime_backup_receipts') ORDER BY cid;";
        using var reader = columns.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        if (!names.SequenceEqual(ReceiptColumns, StringComparer.Ordinal)) return false;
        var definition = Convert.ToString(Scalar(connection, transaction, "SELECT sql FROM sqlite_schema WHERE type='table' AND name='runtime_backup_receipts';"), CultureInfo.InvariantCulture);
        var update = Convert.ToString(Scalar(connection, transaction, "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='runtime_backup_receipts_no_update' AND tbl_name='runtime_backup_receipts';"), CultureInfo.InvariantCulture);
        var delete = Convert.ToString(Scalar(connection, transaction, "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='runtime_backup_receipts_no_delete' AND tbl_name='runtime_backup_receipts';"), CultureInfo.InvariantCulture);
        var replace = Convert.ToString(Scalar(connection, transaction, "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name='runtime_backup_receipts_no_replace' AND tbl_name='runtime_backup_receipts';"), CultureInfo.InvariantCulture);
        if (definition is null || Normalize(definition) != Normalize(ReceiptTableSql)
            || update is null || NormalizeTrigger(update) != NormalizeTrigger(ReceiptNoUpdateTriggerSql)
            || delete is null || NormalizeTrigger(delete) != NormalizeTrigger(ReceiptNoDeleteTriggerSql)
            || replace is null || NormalizeTrigger(replace) != NormalizeTrigger(ReceiptNoReplaceTriggerSql)
            || Convert.ToInt64(Scalar(connection, transaction, "SELECT COUNT(*) FROM schema_version WHERE component='runtime_backup' AND version=1;") ?? 0, CultureInfo.InvariantCulture) != 1)
            return false;

        using (var invalid = connection.CreateCommand())
        {
            invalid.Transaction = transaction;
            invalid.CommandText = """
                SELECT EXISTS(
                    SELECT 1 FROM runtime_backup_receipts
                    WHERE typeof(operation_id)<>'text'
                       OR length(CAST(operation_id AS BLOB))<>36
                       OR operation_id<>lower(operation_id)
                       OR substr(operation_id,9,1)<>'-'
                       OR substr(operation_id,14,1)<>'-'
                       OR substr(operation_id,15,1)<>'7'
                       OR substr(operation_id,19,1)<>'-'
                       OR substr(operation_id,20,1) NOT IN ('8','9','a','b')
                       OR substr(operation_id,24,1)<>'-'
                       OR length(replace(operation_id,'-',''))<>32
                       OR replace(operation_id,'-','') GLOB '*[^0-9a-f]*'
                       OR typeof(operation_kind)<>'text'
                       OR operation_kind NOT IN ('backup','restore')
                       OR typeof(artifact_sha256)<>'text'
                       OR length(CAST(artifact_sha256 AS BLOB))<>64
                       OR artifact_sha256 GLOB '*[^0-9a-f]*'
                       OR typeof(result_code)<>'text'
                       OR result_code NOT IN ('backup_succeeded','restore_succeeded')
                       OR typeof(occurred_at)<>'text'
                       OR length(CAST(occurred_at AS BLOB))<>33
                       OR occurred_at NOT GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]+00:00'
                       OR CAST(substr(occurred_at,1,4) AS INTEGER) NOT BETWEEN 1 AND 9999
                       OR CAST(substr(occurred_at,6,2) AS INTEGER) NOT BETWEEN 1 AND 12
                       OR CAST(substr(occurred_at,9,2) AS INTEGER) NOT BETWEEN 1 AND CASE CAST(substr(occurred_at,6,2) AS INTEGER)
                            WHEN 2 THEN CASE WHEN CAST(substr(occurred_at,1,4) AS INTEGER)%400=0 OR (CAST(substr(occurred_at,1,4) AS INTEGER)%4=0 AND CAST(substr(occurred_at,1,4) AS INTEGER)%100<>0) THEN 29 ELSE 28 END
                            WHEN 4 THEN 30 WHEN 6 THEN 30 WHEN 9 THEN 30 WHEN 11 THEN 30 ELSE 31 END
                       OR CAST(substr(occurred_at,12,2) AS INTEGER) NOT BETWEEN 0 AND 23
                       OR CAST(substr(occurred_at,15,2) AS INTEGER) NOT BETWEEN 0 AND 59
                       OR CAST(substr(occurred_at,18,2) AS INTEGER) NOT BETWEEN 0 AND 59
                       OR julianday(occurred_at) IS NULL
                       OR typeof(reintroduction_count)<>'integer'
                       OR reintroduction_count NOT BETWEEN 0 AND 2147483647
                       OR typeof(pre_restore_backup_created)<>'integer'
                       OR pre_restore_backup_created NOT IN (0,1)
                       OR operation_kind='backup' AND (result_code<>'backup_succeeded' OR reintroduction_count<>0 OR pre_restore_backup_created<>0)
                       OR operation_kind='restore' AND result_code<>'restore_succeeded');
                """;
            if (Convert.ToInt64(invalid.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) return false;
        }

        using var rows = connection.CreateCommand();
        rows.Transaction = transaction;
        rows.CommandText = "SELECT operation_id,occurred_at FROM runtime_backup_receipts ORDER BY operation_id COLLATE BINARY;";
        using var rowReader = rows.ExecuteReader();
        while (rowReader.Read())
        {
            if (rowReader.GetFieldType(0) != typeof(string)
                || rowReader.GetFieldType(1) != typeof(string)) return false;
            var operationId = rowReader.GetString(0);
            var occurredAt = rowReader.GetString(1);
            if (!Guid.TryParseExact(operationId, "D", out var parsedId)
                || parsedId.ToString("D", CultureInfo.InvariantCulture) != operationId
                || !DateTimeOffset.TryParseExact(occurredAt, "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedAt)
                || parsedAt.Offset != TimeSpan.Zero
                || parsedAt.ToString("O", CultureInfo.InvariantCulture) != occurredAt) return false;
        }
        return true;
    }

    internal static void AppendReceipt(SqliteConnection connection, SqliteTransaction transaction, string kind, string digest, string code, DateTimeOffset at, int count, bool preRestore, string? operationId = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO runtime_backup_receipts(operation_id,operation_kind,artifact_sha256,result_code,occurred_at,reintroduction_count,pre_restore_backup_created) VALUES($id,$kind,$digest,$code,$at,$count,$pre);";
        command.Parameters.AddWithValue("$id", operationId ?? Guid.CreateVersion7(at).ToString("D"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$code", code);
        command.Parameters.AddWithValue("$at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$count", count);
        command.Parameters.AddWithValue("$pre", preRestore ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; return command.ExecuteScalar();
    }
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.ExecuteNonQuery();
    }
    private static string Normalize(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');
    private static string NormalizeTrigger(string value) => Normalize(value).Replace("CREATE TRIGGER IF NOT EXISTS ", "CREATE TRIGGER ", StringComparison.Ordinal);
}

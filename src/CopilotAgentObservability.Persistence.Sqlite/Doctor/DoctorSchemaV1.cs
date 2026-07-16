namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class DoctorSchemaV1
{
    public const int Version = 1;

    private static readonly string[] VerificationColumns =
    [
        "verification_id",
        "expected_source_surface",
        "expected_source_adapter",
        "state",
        "revision",
        "started_at",
        "expires_at",
        "completed_at",
        "cancelled_at",
    ];

    private static readonly string[] EvidenceColumns =
    [
        "candidate_id",
        "verification_id",
        "source_surface",
        "source_adapter",
        "evidence_class",
        "evidence_kind",
        "evidence_ref",
        "observed_at",
        "expires_at",
        "accepted",
        "accepted_ordinal",
    ];

    public static void EnsureSchemaVersionTable(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(
            connection,
            transaction,
            "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");

    public static long? ReadVersion(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (!TableExists(connection, transaction, "schema_version"))
        {
            return null;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version FROM schema_version WHERE component='doctor';";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    public static bool DoctorTablesExist(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name LIKE 'doctor_%';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    public static void Create(SqliteConnection connection, SqliteTransaction transaction)
    {
        Execute(connection, transaction, VerificationTableSql);
        Execute(connection, transaction, EvidenceTableSql);
    }

    public static void SetVersion(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, "INSERT INTO schema_version(component,version) VALUES('doctor',1);");

    public static bool IsValid(SqliteConnection connection, SqliteTransaction? transaction)
    {
        if (ReadVersion(connection, transaction) != Version)
        {
            return false;
        }

        using (var tables = connection.CreateCommand())
        {
            tables.Transaction = transaction;
            tables.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name LIKE 'doctor_%' ORDER BY name;";
            using var reader = tables.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
            {
                names.Add(reader.GetString(0));
            }
            if (!names.SequenceEqual(["doctor_verification_evidence", "doctor_verifications"], StringComparer.Ordinal))
            {
                return false;
            }
        }

        if (!ReadColumns(connection, transaction, "doctor_verifications").SequenceEqual(VerificationColumns, StringComparer.Ordinal)
            || !ReadColumns(connection, transaction, "doctor_verification_evidence").SequenceEqual(EvidenceColumns, StringComparer.Ordinal))
        {
            return false;
        }
        if (!TableDefinitionMatches(connection, transaction, "doctor_verifications", VerificationTableSql)
            || !TableDefinitionMatches(connection, transaction, "doctor_verification_evidence", EvidenceTableSql))
        {
            return false;
        }

        using var foreignKey = connection.CreateCommand();
        foreignKey.Transaction = transaction;
        foreignKey.CommandText = "SELECT \"table\",\"from\",\"to\",on_delete FROM pragma_foreign_key_list('doctor_verification_evidence');";
        using var foreignKeyReader = foreignKey.ExecuteReader();
        return foreignKeyReader.Read()
            && foreignKeyReader.GetString(0) == "doctor_verifications"
            && foreignKeyReader.GetString(1) == "verification_id"
            && foreignKeyReader.GetString(2) == "verification_id"
            && foreignKeyReader.GetString(3) == "CASCADE"
            && !foreignKeyReader.Read();
    }

    private static IReadOnlyList<string> ReadColumns(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }
        return columns;
    }

    private static bool TableExists(SqliteConnection connection, SqliteTransaction? transaction, string table)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type='table' AND name=$table;";
        command.Parameters.AddWithValue("$table", table);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    }

    private static bool TableDefinitionMatches(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string table,
        string expectedSql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name=$table;";
        command.Parameters.AddWithValue("$table", table);
        return command.ExecuteScalar() is string actualSql
            && string.Equals(NormalizeSql(expectedSql), NormalizeSql(actualSql), StringComparison.Ordinal);
    }

    private static string NormalizeSql(string sql) =>
        string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).TrimEnd(';');

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private const string VerificationTableSql =
        """
        CREATE TABLE doctor_verifications (
            verification_id TEXT NOT NULL PRIMARY KEY
                CHECK (length(verification_id)=36 AND verification_id=lower(verification_id)
                    AND substr(verification_id,9,1)='-' AND substr(verification_id,14,1)='-'
                    AND substr(verification_id,15,1)='7' AND substr(verification_id,19,1)='-'
                    AND substr(verification_id,20,1) IN ('8','9','a','b') AND substr(verification_id,24,1)='-'
                    AND length(replace(verification_id,'-',''))=32
                    AND replace(verification_id,'-','') NOT GLOB '*[^0-9a-f]*'),
            expected_source_surface TEXT NOT NULL
                CHECK (length(expected_source_surface) BETWEEN 1 AND 64
                    AND substr(expected_source_surface,1,1) GLOB '[a-z0-9]'
                    AND expected_source_surface NOT GLOB '*[^a-z0-9._-]*'),
            expected_source_adapter TEXT NULL
                CHECK (expected_source_adapter IS NULL OR
                    (length(expected_source_adapter) BETWEEN 1 AND 64
                    AND substr(expected_source_adapter,1,1) GLOB '[a-z0-9]'
                    AND expected_source_adapter NOT GLOB '*[^a-z0-9._-]*')),
            state TEXT NOT NULL CHECK (state IN ('active','completed','cancelled')),
            revision INTEGER NOT NULL CHECK (revision > 0),
            started_at TEXT NOT NULL CHECK (started_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'
                AND julianday(started_at) IS NOT NULL),
            expires_at TEXT NOT NULL CHECK (expires_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'
                AND julianday(expires_at) IS NOT NULL
                AND expires_at > started_at
                AND (julianday(expires_at)-julianday(started_at))*1440.0 BETWEEN 0.99999 AND 30.00001),
            completed_at TEXT NULL CHECK (completed_at IS NULL OR
                (completed_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'
                AND julianday(completed_at) IS NOT NULL)),
            cancelled_at TEXT NULL CHECK (cancelled_at IS NULL OR
                (cancelled_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'
                AND julianday(cancelled_at) IS NOT NULL)),
            CHECK (
                (state='active' AND completed_at IS NULL AND cancelled_at IS NULL) OR
                (state='completed' AND completed_at IS NOT NULL AND cancelled_at IS NULL) OR
                (state='cancelled' AND completed_at IS NULL AND cancelled_at IS NOT NULL)
            )
        );
        """;

    private const string EvidenceTableSql =
        """
        CREATE TABLE doctor_verification_evidence (
            candidate_id TEXT NOT NULL PRIMARY KEY
                CHECK (length(candidate_id)=36 AND candidate_id=lower(candidate_id)
                    AND substr(candidate_id,9,1)='-' AND substr(candidate_id,14,1)='-'
                    AND substr(candidate_id,15,1)='7' AND substr(candidate_id,19,1)='-'
                    AND substr(candidate_id,20,1) IN ('8','9','a','b') AND substr(candidate_id,24,1)='-'
                    AND length(replace(candidate_id,'-',''))=32
                    AND replace(candidate_id,'-','') NOT GLOB '*[^0-9a-f]*'),
            verification_id TEXT NOT NULL,
            source_surface TEXT NOT NULL
                CHECK (length(source_surface) BETWEEN 1 AND 64
                    AND substr(source_surface,1,1) GLOB '[a-z0-9]'
                    AND source_surface NOT GLOB '*[^a-z0-9._-]*'),
            source_adapter TEXT NULL
                CHECK (source_adapter IS NULL OR
                    (length(source_adapter) BETWEEN 1 AND 64
                    AND substr(source_adapter,1,1) GLOB '[a-z0-9]'
                    AND source_adapter NOT GLOB '*[^a-z0-9._-]*')),
            evidence_class TEXT NOT NULL CHECK (evidence_class IN ('real_source','synthetic_probe')),
            evidence_kind TEXT NOT NULL CHECK (evidence_kind IN ('ingest','raw_persistence','projection','exact_session_binding','completeness_content')),
            evidence_ref TEXT NOT NULL CHECK (length(evidence_ref) BETWEEN 1 AND 128
                AND instr(evidence_ref,char(10))=0 AND instr(evidence_ref,char(13))=0 AND instr(evidence_ref,char(0))=0),
            observed_at TEXT NOT NULL CHECK (observed_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'
                AND julianday(observed_at) IS NOT NULL),
            expires_at TEXT NOT NULL CHECK (expires_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9][0-9][0-9][0-9][0-9]Z'
                AND julianday(expires_at) IS NOT NULL AND expires_at > observed_at),
            accepted INTEGER NOT NULL CHECK (accepted IN (0,1)),
            accepted_ordinal INTEGER NULL,
            FOREIGN KEY (verification_id) REFERENCES doctor_verifications(verification_id) ON DELETE CASCADE,
            UNIQUE (verification_id,evidence_ref),
            UNIQUE (verification_id,accepted_ordinal),
            CHECK ((accepted=0 AND accepted_ordinal IS NULL) OR
                (accepted=1 AND accepted_ordinal IS NOT NULL AND accepted_ordinal >= 0))
        );
        """;
}

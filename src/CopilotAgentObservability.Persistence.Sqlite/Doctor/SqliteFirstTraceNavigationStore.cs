using System.Globalization;
using System.Text.RegularExpressions;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum FirstTraceNavigationTargetKind
{
    Trace,
    Session,
    SourceDiagnostic,
}

internal sealed record FirstTraceNavigationTarget(
    string EvidenceRef,
    FirstTraceNavigationTargetKind TargetKind,
    string TargetId);

internal sealed partial class SqliteFirstTraceNavigationStore
{
    private const int BusyTimeoutMilliseconds = 250;
    private readonly string databasePath;

    public SqliteFirstTraceNavigationStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = databasePath;
        EnsureSchema();
    }

    public void Record(
        string verificationId,
        string evidenceRef,
        FirstTraceNavigationTargetKind targetKind,
        string targetId)
    {
        if (!DoctorValidation.IsUuidV7(verificationId)
            || !DoctorValidation.IsValidEvidenceReference(evidenceRef)
            || !IsValidTarget(targetKind, targetId))
        {
            throw new ArgumentException("First-trace navigation target is invalid.");
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO first_trace_evidence_navigation(
                    verification_id,evidence_ref,target_kind,target_id)
                VALUES($verification_id,$evidence_ref,$target_kind,$target_id);
                """;
            Add(insert, "$verification_id", verificationId);
            Add(insert, "$evidence_ref", evidenceRef);
            Add(insert, "$target_kind", ToWire(targetKind));
            Add(insert, "$target_id", targetId);
            try
            {
                insert.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("First-trace evidence linkage does not exist.");
            }
        }

        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT target_id FROM first_trace_evidence_navigation
                WHERE verification_id=$verification_id
                  AND evidence_ref=$evidence_ref
                  AND target_kind=$target_kind;
                """;
            Add(read, "$verification_id", verificationId);
            Add(read, "$evidence_ref", evidenceRef);
            Add(read, "$target_kind", ToWire(targetKind));
            if (read.ExecuteScalar() is not string stored
                || !string.Equals(stored, targetId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("First-trace evidence linkage conflicts with the persisted target.");
            }
        }

        transaction.Commit();
    }

    public IReadOnlyList<FirstTraceNavigationTarget> List(
        string verificationId,
        IReadOnlyList<string> evidenceRefs)
    {
        if (!DoctorValidation.IsUuidV7(verificationId)
            || evidenceRefs.Count > DoctorValidation.MaximumEvidenceCandidates
            || evidenceRefs.Distinct(StringComparer.Ordinal).Count() != evidenceRefs.Count
            || evidenceRefs.Any(reference => !DoctorValidation.IsValidEvidenceReference(reference)))
        {
            throw new ArgumentException("First-trace navigation selection is invalid.");
        }
        if (evidenceRefs.Count == 0)
        {
            return [];
        }

        using var connection = Open();
        var targets = new List<FirstTraceNavigationTarget>();
        foreach (var evidenceRef in evidenceRefs)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT target_kind,target_id
                FROM first_trace_evidence_navigation
                WHERE verification_id=$verification_id AND evidence_ref=$evidence_ref
                ORDER BY CASE target_kind
                    WHEN 'trace' THEN 0
                    WHEN 'session' THEN 1
                    WHEN 'source_diagnostic' THEN 2
                    ELSE 3 END,target_id COLLATE BINARY;
                """;
            Add(command, "$verification_id", verificationId);
            Add(command, "$evidence_ref", evidenceRef);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                targets.Add(new(evidenceRef, FromWire(reader.GetString(0)), reader.GetString(1)));
            }
        }
        return targets;
    }

    private void EnsureSchema()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction,
            "CREATE TABLE IF NOT EXISTS schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL);");
        using (var version = connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT version FROM schema_version WHERE component='first_trace_navigation';";
            var existing = version.ExecuteScalar();
            if (existing is not null && Convert.ToInt64(existing, CultureInfo.InvariantCulture) != 1)
            {
                throw new InvalidOperationException("First-trace navigation store is unavailable.");
            }
        }
        Execute(connection, transaction, """
            CREATE TABLE IF NOT EXISTS first_trace_evidence_navigation (
                verification_id TEXT NOT NULL,
                evidence_ref TEXT NOT NULL,
                target_kind TEXT NOT NULL CHECK(target_kind IN ('trace','session','source_diagnostic')),
                target_id TEXT NOT NULL CHECK(length(target_id) BETWEEN 1 AND 128
                    AND instr(target_id,char(10))=0 AND instr(target_id,char(13))=0
                    AND instr(target_id,char(0))=0),
                PRIMARY KEY(verification_id,evidence_ref,target_kind,target_id),
                UNIQUE(verification_id,evidence_ref,target_kind),
                FOREIGN KEY(verification_id,evidence_ref)
                    REFERENCES doctor_verification_evidence(verification_id,evidence_ref)
                    ON DELETE CASCADE
            );
            """);
        Execute(connection, transaction,
            "INSERT OR IGNORE INTO schema_version(component,version) VALUES('first_trace_navigation',1);");
        transaction.Commit();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_keys=ON; PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
        return connection;
    }

    private static bool IsValidTarget(FirstTraceNavigationTargetKind kind, string? targetId) => kind switch
    {
        FirstTraceNavigationTargetKind.Trace => targetId is not null && TraceIdRegex().IsMatch(targetId),
        FirstTraceNavigationTargetKind.Session => DoctorValidation.IsUuidV7(targetId),
        FirstTraceNavigationTargetKind.SourceDiagnostic => DoctorValidation.IsValidEvidenceReference(targetId),
        _ => false,
    };

    private static string ToWire(FirstTraceNavigationTargetKind kind) => kind switch
    {
        FirstTraceNavigationTargetKind.Trace => "trace",
        FirstTraceNavigationTargetKind.Session => "session",
        FirstTraceNavigationTargetKind.SourceDiagnostic => "source_diagnostic",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static FirstTraceNavigationTargetKind FromWire(string kind) => kind switch
    {
        "trace" => FirstTraceNavigationTargetKind.Trace,
        "session" => FirstTraceNavigationTargetKind.Session,
        "source_diagnostic" => FirstTraceNavigationTargetKind.SourceDiagnostic,
        _ => throw new InvalidOperationException("First-trace navigation store is unavailable."),
    };

    private static void Add(SqliteCommand command, string name, object value) =>
        command.Parameters.AddWithValue(name, value);

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceIdRegex();
}

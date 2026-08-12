using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

internal static class SessionVersion13TestFixture
{
    internal static void DowngradeSessionEvents(SqliteConnection connection)
    {
        Execute(connection, "PRAGMA foreign_keys=OFF; PRAGMA legacy_alter_table=ON;");
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE session_events RENAME TO session_events_v14;
            CREATE TABLE session_events (
                event_id TEXT PRIMARY KEY,
                session_id TEXT NOT NULL,
                run_id TEXT NULL,
                source_surface TEXT NULL CHECK (source_surface IS NULL OR source_surface IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code')),
                parent_event_id TEXT NULL,
                trace_id TEXT NULL,
                status TEXT NULL,
                source_adapter TEXT NOT NULL,
                source_event_id TEXT NOT NULL,
                type TEXT NOT NULL,
                occurred_at TEXT NOT NULL,
                content_state TEXT NOT NULL CHECK (content_state IN ('available','not_captured','redacted','unsupported','expired_pending_deletion')),
                source_application_version TEXT NULL,
                adapter_version TEXT NULL,
                schema_fingerprint TEXT NULL,
                normalization_version TEXT NULL,
                match_kind TEXT NULL CHECK (match_kind IS NULL OR match_kind IN ('exact_native','explicit_link','trace_continuity','conversation_id','none')),
                UNIQUE (source_adapter, source_event_id),
                UNIQUE (session_id, event_id),
                FOREIGN KEY (session_id) REFERENCES sessions(session_id) ON DELETE CASCADE,
                FOREIGN KEY (session_id, run_id) REFERENCES session_runs(session_id, run_id),
                FOREIGN KEY (session_id, parent_event_id) REFERENCES session_events(session_id, event_id)
            );
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,
                occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind)
            SELECT event_id,session_id,run_id,source_surface,parent_event_id,trace_id,status,source_adapter,source_event_id,type,
                occurred_at,content_state,source_application_version,adapter_version,schema_fingerprint,normalization_version,match_kind
            FROM session_events_v14;
            DROP TABLE session_events_v14;
            UPDATE schema_version SET version=13 WHERE component='session';
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        Execute(connection, "PRAGMA legacy_alter_table=OFF; PRAGMA foreign_keys=ON;");
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

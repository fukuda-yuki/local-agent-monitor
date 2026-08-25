using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal static class LocalWorkspaceProjectionBackupValidation
{
    internal static void Validate(SqliteConnection connection, SqliteTransaction transaction)
    {
        try
        {
            LocalWorkspaceProjectionSchemaV1.Validate(connection, transaction);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT EXISTS(
                  SELECT 1 FROM local_workspace_sessions p
                  LEFT JOIN sessions s ON s.session_id=p.session_id
                  WHERE s.session_id IS NULL
                     OR length(p.revision_seed)=0
                     OR (SELECT COUNT(*) FROM local_workspace_session_sources x WHERE x.session_id=p.session_id)>5
                     OR (SELECT COUNT(*) FROM local_workspace_session_models x WHERE x.session_id=p.session_id)>16
                     OR (SELECT COUNT(*) FROM local_workspace_session_activity x WHERE x.session_id=p.session_id)<>5
                     OR p.label_state NOT IN ('recorded','not_observed','not_captured','expired')
                     OR p.timing_state NOT IN ('recorded','not_observed','inconsistent')
                     OR p.capture_notes NOT IN ('','raw_content_expired','raw_content_not_captured')
                     OR p.label_state='recorded' AND NOT EXISTS(
                       SELECT 1 FROM session_events e
                       JOIN session_event_content c ON c.event_id=e.event_id
                       WHERE e.event_id=p.label_source_identity COLLATE BINARY
                         AND e.session_id=p.session_id COLLATE BINARY
                         AND e.type='user_prompt' COLLATE BINARY
                         AND e.content_state='available')
                     OR EXISTS(SELECT 1 FROM local_workspace_session_activity a WHERE a.session_id=p.session_id AND a.state NOT IN ('recorded','not_observed'))
                );
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException();
            command.CommandText = "SELECT label_expires_at FROM local_workspace_sessions WHERE label_state='recorded';";
            using var reader = command.ExecuteReader();
            var now = DateTimeOffset.UtcNow;
            while (reader.Read())
            {
                var value = reader.GetString(0);
                if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
                    || expiry.Offset != TimeSpan.Zero || expiry <= now)
                    throw new InvalidOperationException();
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("local_workspace_projection_backup_invalid", exception);
        }
    }
}

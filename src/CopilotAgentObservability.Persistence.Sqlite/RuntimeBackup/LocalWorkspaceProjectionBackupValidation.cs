using Microsoft.Data.Sqlite;

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
                     OR p.label_state='recorded' AND p.label_expires_at<=CURRENT_TIMESTAMP
                );
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("local_workspace_projection_backup_invalid", exception);
        }
    }
}

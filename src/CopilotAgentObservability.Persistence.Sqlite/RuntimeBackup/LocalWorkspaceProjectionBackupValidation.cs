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
                     OR EXISTS(SELECT 1 FROM local_workspace_session_sources x WHERE x.session_id=p.session_id AND x.source NOT IN ('copilot-sdk','copilot-cli','vscode','hook-unknown','claude-code'))
                     OR EXISTS(SELECT 1 FROM local_workspace_session_models x WHERE x.session_id=p.session_id AND NOT EXISTS(SELECT 1 FROM session_runs r WHERE r.session_id=p.session_id AND r.model=x.model COLLATE BINARY))
                     OR EXISTS(SELECT 1 FROM local_workspace_session_sources x WHERE x.session_id=p.session_id AND NOT EXISTS(
                       SELECT 1 FROM session_native_ids n WHERE n.session_id=p.session_id AND n.source_surface=x.source COLLATE BINARY
                       UNION ALL SELECT 1 FROM session_runs r WHERE r.session_id=p.session_id AND r.source_surface=x.source COLLATE BINARY
                       UNION ALL SELECT 1 FROM session_events e WHERE e.session_id=p.session_id AND e.source_surface=x.source COLLATE BINARY))
                     OR (p.source_state='recorded')<>(EXISTS(SELECT 1 FROM local_workspace_session_sources x WHERE x.session_id=p.session_id))
                     OR (p.model_state='recorded')<>(EXISTS(SELECT 1 FROM local_workspace_session_models x WHERE x.session_id=p.session_id))
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
                         AND e.content_state='available'
                         AND c.expires_at=p.label_expires_at COLLATE BINARY)
                     OR EXISTS(SELECT 1 FROM local_workspace_session_activity a WHERE a.session_id=p.session_id AND a.state NOT IN ('recorded','not_observed'))
                );
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException();
            command.CommandText = "SELECT label_expires_at FROM local_workspace_sessions WHERE label_state='recorded';";
            var now = DateTimeOffset.UtcNow;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var value = reader.GetString(0);
                    if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
                        || expiry.Offset != TimeSpan.Zero || expiry <= now)
                        throw new InvalidOperationException();
                }
            }
            ValidateCanonicalProjection(connection, transaction);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("local_workspace_projection_backup_invalid", exception);
        }
    }

    private static void ValidateCanonicalProjection(SqliteConnection connection, SqliteTransaction transaction)
    {
        var before = Snapshot(connection, transaction);
        using var readNow = connection.CreateCommand();
        readNow.Transaction = transaction;
        readNow.CommandText = "SELECT refreshed_at FROM local_workspace_projection_state WHERE projector_key='local-workspace-projection-v1';";
        var text = readNow.ExecuteScalar() as string;
        if (text is null || !DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var projectorNow))
            throw new InvalidOperationException();
        using var replica = new SqliteConnection("Data Source=:memory:");
        replica.Open();
        connection.BackupDatabase(replica);
        using (var replicaTransaction = replica.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(replica, replicaTransaction, projectorNow);
            if (!before.SequenceEqual(Snapshot(replica, replicaTransaction), StringComparer.Ordinal))
                throw new InvalidOperationException();
            replicaTransaction.Rollback();
        }
    }

    private static string[] Snapshot(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = new List<string>();
        foreach (var table in LocalWorkspaceProjectionSchemaV1.TableNames.Order(StringComparer.Ordinal))
        {
            var columns = new List<string>();
            using (var info = connection.CreateCommand())
            {
                info.Transaction = transaction;
                info.CommandText = "SELECT name FROM pragma_table_xinfo($table) WHERE hidden=0 ORDER BY cid;";
                info.Parameters.AddWithValue("$table", table);
                using var reader = info.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(0));
            }
            var projection = string.Join(',', columns.Select(static column => $"\"{column.Replace("\"", "\"\"")}\""));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"SELECT {projection} FROM \"{table}\" ORDER BY {projection};";
            using var data = command.ExecuteReader();
            while (data.Read())
            {
                var values = new string[data.FieldCount];
                for (var index = 0; index < values.Length; index++)
                    values[index] = data.IsDBNull(index) ? "N" : data.GetFieldType(index) == typeof(byte[]) ? "B" + Convert.ToHexString((byte[])data.GetValue(index)) : "V" + Convert.ToString(data.GetValue(index), CultureInfo.InvariantCulture);
                rows.Add(table + "\0" + string.Join("\0", values));
            }
        }
        return rows.ToArray();
    }

}

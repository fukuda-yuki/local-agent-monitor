using Microsoft.Data.Sqlite;
using System.Globalization;

namespace CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;

internal static class LocalWorkspaceProjectionBackupValidation
{
    internal static void Validate(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset? publicationTime = null, ISkillRegistryGenerationAuthority? skillRegistryAuthority = null)
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
                     OR (p.sort_group=1 AND p.sort_epoch_ms<>0)
                     OR (p.last_seen_at IS NULL)<>(p.last_seen_epoch_ms IS NULL)
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
                     OR p.label_state='recorded' AND NOT EXISTS(
                       SELECT 1 FROM session_events e
                       JOIN session_event_content c ON c.event_id=e.event_id
                       WHERE e.event_id=p.label_source_identity COLLATE BINARY
                         AND e.session_id=p.session_id COLLATE BINARY
                         AND e.type IN ('user.message','UserPromptSubmit','userPromptSubmitted')
                         AND e.content_state='available'
                         AND c.expires_at=p.label_expires_at COLLATE BINARY)
                     OR EXISTS(SELECT 1 FROM local_workspace_session_activity a WHERE a.session_id=p.session_id AND a.state NOT IN ('recorded','not_observed','capture_gap','source_unsupported'))
                );
                """;
            if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                throw new InvalidOperationException();
            command.CommandText = "SELECT label_expires_at FROM local_workspace_sessions WHERE label_state='recorded';";
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var value = reader.GetString(0);
                    if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
                        || expiry.Offset != TimeSpan.Zero)
                        throw new InvalidOperationException();
                }
            }
            command.CommandText = "SELECT last_seen_at,last_seen_epoch_ms FROM local_workspace_sessions WHERE last_seen_at IS NOT NULL;";
            using (var timing = command.ExecuteReader())
            {
                while (timing.Read())
                {
                    if (!DateTimeOffset.TryParseExact(timing.GetString(0), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant)
                        || instant.Offset != TimeSpan.Zero || instant.ToUnixTimeMilliseconds() != timing.GetInt64(1))
                        throw new InvalidOperationException();
                }
            }
            command.CommandText = "SELECT normalized_text FROM local_workspace_session_search_facts;";
            using (var facts = command.ExecuteReader())
                while (facts.Read())
                {
                    var value = facts.GetString(0);
                    if (value.Length == 0 || !string.Equals(value, value.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), StringComparison.Ordinal))
                        throw new InvalidOperationException();
                }
            command.CommandText = "SELECT capture_notes FROM local_workspace_sessions;";
            using (var notes = command.ExecuteReader())
            {
                while (notes.Read())
                {
                    var persisted = notes.GetString(0);
                    var parts = persisted.Length == 0 ? Array.Empty<string>() : persisted.Split(',', StringSplitOptions.None);
                    if (!string.Equals(persisted, LocalWorkspaceProjectionStore.CanonicalizeCaptureNotes(parts), StringComparison.Ordinal)
                        || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length)
                        throw new InvalidOperationException();
                }
            }
            ValidateCanonicalProjection(connection, transaction, publicationTime, skillRegistryAuthority);
            ValidateSpanFacts(connection, transaction);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("local_workspace_projection_backup_invalid", exception);
        }
    }

    private static void ValidateSpanFacts(SqliteConnection connection, SqliteTransaction transaction)
    {
        using (var availability = connection.CreateCommand())
        {
            availability.Transaction = transaction;
            availability.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='raw_records') AND EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='monitor_spans');";
            if (Convert.ToInt64(availability.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                availability.CommandText = "SELECT COUNT(*) FROM local_workspace_span_facts;";
                if (Convert.ToInt64(availability.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new InvalidOperationException();
                return;
            }
        }
        var monitorKeys = new HashSet<(long RawId, int Ordinal)>();
        using (var spans = connection.CreateCommand())
        {
            spans.Transaction = transaction;
            spans.CommandText = "SELECT raw_record_id,span_ordinal FROM monitor_spans ORDER BY raw_record_id,span_ordinal;";
            using var reader = spans.ExecuteReader();
            while (reader.Read()) monitorKeys.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }

        bool hasRetention;
        using (var retention = connection.CreateCommand())
        {
            retention.Transaction = transaction;
            retention.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_schema WHERE type='table' AND name='retention_items');";
            hasRetention = Convert.ToInt64(retention.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
        }
        var expected = new Dictionary<(long RawId, int Ordinal), (long? Retry, long? Total)>();
        var availableRawIds = new HashSet<long>();
        if (hasRetention)
        using (var records = connection.CreateCommand())
        {
            records.Transaction = transaction;
            records.CommandText = """
                SELECT r.id,r.source,r.trace_id,r.received_at,r.resource_attributes_json,r.payload_json,r.schema_version
                FROM raw_records r
                JOIN retention_items i ON i.store_kind='raw_record' AND i.source_item_id=CAST(r.id AS TEXT)
                WHERE i.state IN ('expiring','retained_by_policy') AND i.read_denied_at IS NULL
                  AND i.deleted_at IS NULL AND i.error_code IS NULL
                ORDER BY r.id;
                """;
            using var reader = records.ExecuteReader();
            while (reader.Read())
            {
                var rawId = reader.GetInt64(0);
                availableRawIds.Add(rawId);
                if (!DateTimeOffset.TryParseExact(reader.GetString(3), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var receivedAt))
                    throw new InvalidOperationException();
                var raw = new RawTelemetryRecord(rawId, reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), receivedAt,
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetInt32(6));
                foreach (var span in MonitorSpanProjectionBuilder.Build(raw))
                    if (monitorKeys.Contains((rawId, span.SpanOrdinal)))
                        expected.Add((rawId, span.SpanOrdinal), (span.RetryCount, span.ProducerTotalTokens));
            }
        }

        var actual = new Dictionary<(long RawId, int Ordinal), (long? Retry, long? Total)>();
        using (var facts = connection.CreateCommand())
        {
            facts.Transaction = transaction;
            facts.CommandText = "SELECT raw_record_id,span_ordinal,retry_count,producer_total_tokens FROM local_workspace_span_facts ORDER BY raw_record_id,span_ordinal;";
            using var reader = facts.ExecuteReader();
            while (reader.Read())
                actual.Add((reader.GetInt64(0), reader.GetInt32(1)),
                    (reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetInt64(3)));
        }
        if (hasRetention)
        using (var deleted = connection.CreateCommand())
        {
            deleted.Transaction = transaction;
            deleted.CommandText = """
                SELECT EXISTS(SELECT 1 FROM local_workspace_span_facts f
                  JOIN retention_items i ON i.store_kind='raw_record' AND i.source_item_id=CAST(f.raw_record_id AS TEXT)
                  JOIN retention_tombstones t ON t.item_id=i.item_id
                  WHERE i.state='deleted' AND i.read_denied_at IS NOT NULL AND i.deleted_at=t.deleted_at);
                """;
            if (Convert.ToInt64(deleted.ExecuteScalar(), CultureInfo.InvariantCulture) != 0) throw new InvalidOperationException();
        }
        if (actual.Keys.Any(key => !monitorKeys.Contains(key)))
            throw new InvalidOperationException();
        var availableActual = actual.Where(pair => availableRawIds.Contains(pair.Key.RawId)).ToDictionary();
        if (expected.Count != availableActual.Count || expected.Any(pair => !availableActual.TryGetValue(pair.Key, out var value) || value != pair.Value))
            throw new InvalidOperationException();
    }

    private static void ValidateCanonicalProjection(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset? publicationTime, ISkillRegistryGenerationAuthority? skillRegistryAuthority)
    {
        var before = Snapshot(connection, transaction);
        if (publicationTime is null)
        {
            using var readNow = connection.CreateCommand();
            readNow.Transaction = transaction;
            readNow.CommandText = "SELECT refreshed_at FROM local_workspace_projection_state WHERE projector_key='local-workspace-projection-v1';";
            var text = readNow.ExecuteScalar() as string;
            if (text is null || !DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var projectorNow))
                throw new InvalidOperationException();
            publicationTime = projectorNow;
        }
        using var replica = new SqliteConnection("Data Source=:memory:");
        replica.Open();
        connection.BackupDatabase(replica);
        using (var replicaTransaction = replica.BeginTransaction())
        {
            LocalWorkspaceProjectionStore.Refresh(replica, replicaTransaction, publicationTime.Value, skillRegistryAuthority);
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

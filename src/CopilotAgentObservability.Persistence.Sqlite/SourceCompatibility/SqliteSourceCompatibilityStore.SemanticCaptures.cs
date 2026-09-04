namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record SemanticAttributeKeyRow(string KeyHash, int OccurrenceCount);
internal sealed record SemanticAttributeCaptureRow(
    string SourceFamily, string State, string CaptureId, DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt, bool Incomplete, int ObservationCount,
    IReadOnlyList<SemanticAttributeKeyRow> Keys,
    IReadOnlyList<SemanticAttributeKeyRow> AddedKeys, IReadOnlyList<string> NotObservedKeys);

internal sealed partial class SqliteSourceCompatibilityStore
{
    private int pendingSemanticGap;
    private int semanticIngestionsInFlight;

    internal void BeginSemanticIngestion() => Interlocked.Increment(ref semanticIngestionsInFlight);
    internal void EndSemanticIngestion(bool succeeded)
    {
        if (!succeeded) RecordSemanticCaptureGap();
        Interlocked.Decrement(ref semanticIngestionsInFlight);
    }

    internal void RecordSemanticCaptureGap()
    {
        var revision = Interlocked.Increment(ref pendingSemanticGap);
        try { MarkSemanticCaptureGap(); Interlocked.CompareExchange(ref pendingSemanticGap, 0, revision); }
        catch (SqliteException) { }
    }

    internal string StartSemanticCapture(string sourceFamily, DateTimeOffset now)
    {
        if (!SemanticAttributeKeyBaseline.Supports(sourceFamily)) throw new ArgumentException("Unsupported source.", nameof(sourceFamily));
        var gapRevision = Volatile.Read(ref pendingSemanticGap);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        FlushSemanticGap(connection, transaction);
        ExpireSemanticCaptures(connection, transaction, now);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO source_semantic_captures
            (source_family,state,capture_id,baseline_id,started_at,expires_at,incomplete,observation_count)
            VALUES($source,'active',$id,$baseline,$now,$expires,0,0);
            SELECT capture_id FROM source_semantic_captures WHERE source_family=$source AND state='active';
            """;
        Add(command, "$source", sourceFamily);
        Add(command, "$id", Guid.NewGuid().ToString("N"));
        Add(command, "$baseline", SemanticAttributeKeyBaseline.Id);
        Add(command, "$now", Timestamp(now));
        Add(command, "$expires", Timestamp(now.AddHours(24)));
        var id = (string)command.ExecuteScalar()!;
        if (Volatile.Read(ref semanticIngestionsInFlight) != 0) MarkSemanticCaptureGap(connection, transaction);
        transaction.Commit();
        Interlocked.CompareExchange(ref pendingSemanticGap, 0, gapRevision);
        return id;
    }

    internal bool CompleteSemanticCapture(string sourceFamily, string captureId, DateTimeOffset now)
    {
        if (!SemanticAttributeKeyBaseline.Supports(sourceFamily) || !Guid.TryParseExact(captureId, "N", out _)) return false;
        var gapRevision = Volatile.Read(ref pendingSemanticGap);
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        FlushSemanticGap(connection, transaction);
        ExpireSemanticCaptures(connection, transaction, now);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        Add(command, "$source", sourceFamily);
        Add(command, "$id", captureId);
        command.CommandText = "SELECT COUNT(*) FROM source_semantic_captures WHERE source_family=$source AND state='active' AND capture_id=$id;";
        if ((long)command.ExecuteScalar()! == 0) return false;
        if (Volatile.Read(ref semanticIngestionsInFlight) != 0) MarkSemanticCaptureGap(connection, transaction);
        command.CommandText = """
            DELETE FROM source_semantic_capture_keys WHERE capture_id IN
                (SELECT capture_id FROM source_semantic_captures WHERE source_family=$source AND state='completed');
            DELETE FROM source_semantic_captures WHERE source_family=$source AND state='completed';
            UPDATE source_semantic_captures SET state='completed',completed_at=$now,expires_at=$expires,
                incomplete=CASE WHEN observation_count=0 THEN 1 ELSE incomplete END
            WHERE capture_id=$id AND state='active';
            """;
        Add(command, "$now", Timestamp(now));
        Add(command, "$expires", Timestamp(now.AddHours(24)));
        command.ExecuteNonQuery();
        transaction.Commit();
        Interlocked.CompareExchange(ref pendingSemanticGap, 0, gapRevision);
        return true;
    }

    internal IReadOnlyList<SemanticAttributeCaptureRow> ListSemanticCaptures(DateTimeOffset now)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT source_family,state,capture_id,started_at,completed_at,incomplete,observation_count
            FROM source_semantic_captures WHERE expires_at>$now ORDER BY source_family,state;
            """;
        Add(command, "$now", Timestamp(now));
        var rows = new List<SemanticAttributeCaptureRow>();
        using (var reader = command.ExecuteReader())
            while (reader.Read()) rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                ParseTimestamp(reader.GetString(3)), reader.IsDBNull(4) ? null : ParseTimestamp(reader.GetString(4)),
                reader.GetInt32(5) != 0, reader.GetInt32(6), [], [], []));
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var keys = ReadSemanticKeys(connection, transaction, row.CaptureId);
            var baseline = SemanticAttributeKeyBaseline.ForSource(row.SourceFamily);
            rows[index] = row with
            {
                Keys = keys,
                AddedKeys = keys.Where(key => !baseline.Contains(key.KeyHash)).ToArray(),
                NotObservedKeys = row.State == "completed" && !row.Incomplete
                    ? baseline.Except(keys.Select(key => key.KeyHash), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() : []
            };
        }
        transaction.Commit();
        return rows;
    }

    internal void MarkSemanticCaptureGap()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        MarkSemanticCaptureGap(connection, transaction);
        transaction.Commit();
    }

    private void FlushSemanticGap(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (Volatile.Read(ref pendingSemanticGap) != 0)
            MarkSemanticCaptureGap(connection, transaction);
    }

    private static void MarkSemanticCaptureGap(SqliteConnection connection, SqliteTransaction transaction) =>
        Execute(connection, transaction, "UPDATE source_semantic_captures SET incomplete=1 WHERE state='active';");

    internal void ExpireSemanticCaptures(DateTimeOffset now)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        ExpireSemanticCaptures(connection, transaction, now);
        transaction.Commit();
    }

    private static void ExpireSemanticCaptures(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM source_semantic_capture_keys WHERE capture_id IN
                (SELECT capture_id FROM source_semantic_captures WHERE expires_at<=$now);
            DELETE FROM source_semantic_captures WHERE expires_at<=$now;
            """;
        Add(command, "$now", Timestamp(now));
        command.ExecuteNonQuery();
    }

    internal static void ObserveSemanticKeys(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<TraceSourceResolutionDraft> resolutions,
        bool incomplete, DateTimeOffset observedAt)
    {
        if (incomplete || resolutions.Count == 0 || resolutions.Any(item => item.State != TraceSourceResolutionState.Resolved))
            MarkSemanticCaptureGap(connection, transaction);
        foreach (var resolution in resolutions.Where(item => item.State == TraceSourceResolutionState.Resolved))
        {
            using var capture = connection.CreateCommand();
            capture.Transaction = transaction;
            capture.CommandText = "SELECT capture_id FROM source_semantic_captures WHERE source_family=$source AND state='active' AND started_at<=$now AND expires_at>$now;";
            Add(capture, "$source", resolution.SourceFamily);
            Add(capture, "$now", Timestamp(observedAt));
            if (capture.ExecuteScalar() is not string id) continue;
            var retained = ReadSemanticKeys(connection, transaction, id).ToDictionary(key => key.KeyHash, key => key.OccurrenceCount, StringComparer.Ordinal);
            var truncated = resolution.AttributeInventoryIncomplete;
            foreach (var key in resolution.AttributeKeys.OrderBy(key => key.Key, StringComparer.Ordinal))
            {
                if (!retained.ContainsKey(key.Key) && retained.Count >= 256) { truncated = true; continue; }
                var count = (int)Math.Min(SourceOccurrenceCount.Maximum, retained.GetValueOrDefault(key.Key) + (long)key.Value);
                retained[key.Key] = count;
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO source_semantic_capture_keys(capture_id,key_hash,occurrence_count) VALUES($id,$key,$count) ON CONFLICT(capture_id,key_hash) DO UPDATE SET occurrence_count=excluded.occurrence_count;";
                Add(insert, "$id", id); Add(insert, "$key", key.Key); Add(insert, "$count", count);
                insert.ExecuteNonQuery();
            }
            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE source_semantic_captures SET
                    incomplete=MAX(incomplete,$incomplete),
                    observation_count=MIN(1000000,observation_count+1)
                WHERE capture_id=$id;
                """;
            Add(update, "$incomplete", truncated ? 1 : 0); Add(update, "$id", id);
            update.ExecuteNonQuery();
        }
    }

    private static IReadOnlyList<SemanticAttributeKeyRow> ReadSemanticKeys(SqliteConnection connection, SqliteTransaction transaction, string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT key_hash,occurrence_count FROM source_semantic_capture_keys WHERE capture_id=$id ORDER BY key_hash;";
        Add(command, "$id", id);
        using var reader = command.ExecuteReader();
        var keys = new List<SemanticAttributeKeyRow>();
        while (reader.Read()) keys.Add(new(reader.GetString(0), reader.GetInt32(1)));
        return keys;
    }
}

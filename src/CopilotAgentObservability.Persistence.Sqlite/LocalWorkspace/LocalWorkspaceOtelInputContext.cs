using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalWorkspaceOtelInputContext
{
    private const int MaximumRawBytes = 16_777_216;
    private const int MaximumSelectedBytes = 1_048_576;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string BindingSql = """
        FROM local_workspace_nodes n
        JOIN session_events e ON e.session_id=n.session_id AND (
          n.source_kind='session_event' AND e.event_id=n.source_identity
          OR n.source_kind='semantic_tool' AND EXISTS (
            SELECT 1 FROM local_workspace_node_source_references reference
            WHERE reference.node_id=n.node_id AND reference.event_id=e.event_id
              AND reference.trace_id=e.trace_id COLLATE BINARY
              AND reference.span_id=substr(e.source_event_id,34) COLLATE BINARY))
        JOIN monitor_spans m ON m.trace_id=e.trace_id COLLATE BINARY
          AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
        LEFT JOIN raw_records r ON r.id=m.raw_record_id
        LEFT JOIN retention_items i ON i.store_kind='raw_record'
          AND i.source_item_id=CAST(m.raw_record_id AS TEXT)
          AND i.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
        LEFT JOIN retention_tombstones tombstone ON tombstone.item_id=i.item_id
        WHERE n.session_id=$session_id
          AND e.source_adapter='otel-exact' AND e.type='otel.span'
          AND (n.source_kind='session_event' AND m.operation='chat'
            OR n.source_kind='semantic_tool' AND m.operation='execute_tool' AND m.status='error')
          AND length(m.trace_id)=32 AND m.trace_id NOT GLOB '*[^0-9a-f]*'
          AND length(m.span_id)=16 AND m.span_id NOT GLOB '*[^0-9a-f]*'
          AND COALESCE((WITH owners AS MATERIALIZED (
              SELECT lower(trace_id) trace_key,lower(span_id) span_key,COUNT(*) owner_count
              FROM monitor_spans GROUP BY lower(trace_id),lower(span_id)) SELECT other.owner_count FROM owners other
            WHERE other.trace_key=m.trace_id AND other.span_key=m.span_id),0)=1
          AND COALESCE((WITH owners AS MATERIALIZED (
              SELECT lower(source_event_id) source_key,COUNT(*) owner_count
              FROM session_events WHERE source_adapter='otel-exact' GROUP BY lower(source_event_id)) SELECT other.owner_count FROM owners other
            WHERE other.source_key=m.trace_id||'/'||m.span_id),0)=1
        """;

    internal static LocalWorkspaceSessionDetailContribution Apply(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        LocalWorkspaceSessionDetailContribution detail, DateTimeOffset acceptedAt)
    {
        var calls = detail.Nodes.Where(node => node.Kind == "llm_call" || node.Kind == "tool" && node.Status == "failed").ToArray();
        if (calls.Length == 0) return detail;
        var content = detail.Content.ToList();
        var callIds = calls.Select(call => call.NodeId).ToHashSet(StringComparer.Ordinal);
        using var command = AvailabilityCommand(connection, transaction, sessionId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var nodeId = reader.GetString(18);
            if (!callIds.Contains(nodeId)) continue;
            var availability = ReadAvailabilityRow(reader, nodeId, acceptedAt);
            content.RemoveAll(item => item.NodeId == nodeId && item.Part == availability.Part);
            content.Add(availability);
        }
        return detail with { Content = content.AsReadOnly() };
    }

    internal static LocalWorkspaceContentAvailability? ReadAvailability(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        string nodeId, DateTimeOffset acceptedAt, string part = "event_content")
    {
        using var command = AvailabilityCommand(connection, transaction, sessionId);
        command.CommandText += " AND n.node_id=$node_id";
        command.Parameters.AddWithValue("$node_id", nodeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var availability = ReadAvailabilityRow(reader, nodeId, acceptedAt);
        return availability.Part == part ? availability : null;
    }

    private static SqliteCommand AvailabilityCommand(SqliteConnection connection, SqliteTransaction transaction, string sessionId)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.raw_record_id,m.trace_id,m.span_id,r.received_at,r.schema_version,r.retention_owner_token,
              i.item_id,i.store_instance_id,i.captured_at,i.expires_at,i.state,i.revision,i.ownership_receipt,
              i.read_denied_at,i.deleted_at,i.error_code,tombstone.receipt_at,tombstone.deleted_at,n.node_id,m.operation
            """ + " " + BindingSql;
        command.Parameters.AddWithValue("$session_id", sessionId);
        return command;
    }

    private static LocalWorkspaceContentAvailability ReadAvailabilityRow(SqliteDataReader reader, string nodeId, DateTimeOffset acceptedAt)
    {
        var sourceId = reader.GetInt64(0).ToString(CultureInfo.InvariantCulture);
        var identity = reader.GetString(1) + "/" + reader.GetString(2);
        string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
        var state = State(Text(10), Text(9), Text(13), Text(14), Text(15), Text(16), Text(17), acceptedAt);
        var token = reader.IsDBNull(5) ? null : (byte[])reader[5];
        var receipt = reader.IsDBNull(12) ? null : (byte[])reader[12];
        if (state == "available")
        {
            if (Text(3) is not { } received || Text(7) is not { } store || Text(8) != received
                || token is not { Length: 32 } || receipt is not { Length: 32 }
                || !DateTimeOffset.TryParseExact(received, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                || !CryptographicOperations.FixedTimeEquals(receipt, RetentionOwnershipReceipt.CreateRawRecord(
                    new(store, reader.GetInt64(0), received, at.UtcTicks, reader.GetInt32(4), token))))
                state = "invalid";
        }
        var revision = reader.IsDBNull(11) ? (long?)null : reader.GetInt64(11);
        var part = reader.GetString(19) == "chat" ? "event_content" : "error_message";
        return new(nodeId, part, state, sourceId,
            identity + "|" + sourceId + "|" + revision?.ToString(CultureInfo.InvariantCulture),
            "raw_record", part == "event_content" ? "otel_input_context" : "otel_error", null, null,
            Text(6), Text(7), Text(8), Text(9), revision, receipt, token);
    }

    internal static void AppendRevision(IncrementalHash hash, SqliteConnection connection,
        SqliteTransaction transaction, string sessionId, DateTimeOffset acceptedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT n.node_id,m.raw_record_id,i.store_instance_id,i.captured_at,i.expires_at,i.state,i.revision,
              i.ownership_receipt,i.read_denied_at,i.deleted_at,i.error_code,tombstone.receipt_at,tombstone.deleted_at
            """ + " " + BindingSql + " ORDER BY n.node_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        using var reader = command.ExecuteReader();
        // Typed, length-framed rows keep owner changes in the coherent snapshot without reading raw bodies.
        hash.AppendData("local-monitor-otel-call-context-v2\0"u8);
        Span<byte> length = stackalloc byte[4];
        while (reader.Read())
        {
            for (var index = 0; index < reader.FieldCount; index++)
            {
                var value = reader.GetValue(index);
                var bytes = value switch
                {
                    DBNull => new byte[] { 0 },
                    byte[] blob => [3, .. blob],
                    long number => StrictUtf8.GetBytes("i" + number.ToString(CultureInfo.InvariantCulture)),
                    _ => StrictUtf8.GetBytes("s" + (string)value),
                };
                System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                hash.AppendData(length);
                hash.AppendData(bytes);
            }
            string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
            hash.AppendData(StrictUtf8.GetBytes(State(Text(5), Text(4), Text(8), Text(9), Text(10), Text(11), Text(12), acceptedAt)));
            hash.AppendData([0xff]);
        }
    }

    private static string State(string? state, string? expiry, string? denied, string? deleted,
        string? error, string? tombstoneReceipt, string? tombstoneDeleted, DateTimeOffset now)
    {
        if (state == "deleted" && deleted is not null && deleted == tombstoneReceipt && deleted == tombstoneDeleted) return "deleted";
        if (state is null || deleted is not null || tombstoneReceipt is not null || tombstoneDeleted is not null) return "invalid";
        if (!DateTimeOffset.TryParseExact(expiry, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiresAt)) return "invalid";
        if (state == "expired_pending_deletion" || state == "expiring" && expiresAt <= now) return "expired";
        if (denied is not null || error is not null || state is "deletion_queued" or "deleting" or "deletion_failed") return "read_denied";
        return state is "retained_by_policy" or "expiring" ? "available" : "invalid";
    }

    internal static async ValueTask<LocalWorkspaceNodeContentReadResult> ReadAsync(
        RetentionCatalogContext context, TimeProvider clock, string sessionId, string nodeId,
        LocalWorkspaceContentAvailability locator, CancellationToken cancellationToken,
        IRetentionReadBoundaryCheckpoint? readBoundaryCheckpoint = null)
    {
        var catalog = readBoundaryCheckpoint is null
            ? new RetentionCatalogStore(context, clock)
            : new RetentionCatalogStore(context, clock, readBoundaryCheckpoint);
        using var gate = await catalog.EnterAdmissionGateAsync(cancellationToken).ConfigureAwait(false);
        using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(context.DatabasePath, SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = clock.GetUtcNow();
        var current = ReadAvailability(connection, transaction, sessionId, nodeId, now, locator.Part);
        if (current?.State != "available")
        {
            transaction.Rollback();
            return new(current?.State switch
            {
                "expired" => LocalWorkspaceNodeContentReadDisposition.Expired,
                "deleted" => LocalWorkspaceNodeContentReadDisposition.Deleted,
                "read_denied" => LocalWorkspaceNodeContentReadDisposition.ReadDenied,
                _ => LocalWorkspaceNodeContentReadDisposition.Unavailable,
            }, null);
        }
        if (locator.Part != current.Part || locator.StoreKind != "raw_record" || locator.LocatorKind != current.LocatorKind
            || locator.RevisionInput != current.RevisionInput || locator.RetentionItemId != current.RetentionItemId
            || locator.SourceItemId != current.SourceItemId || locator.RetentionStoreInstanceId != current.RetentionStoreInstanceId
            || locator.SourceCapturedAt != current.SourceCapturedAt || locator.SourceExpiresAt != current.SourceExpiresAt
            || locator.RetentionRevision != current.RetentionRevision
            || locator.RetentionOwnershipReceipt is null || !locator.RetentionOwnershipReceipt.AsSpan().SequenceEqual(current.RetentionOwnershipReceipt)
            || locator.RetentionOwnerToken is null || !locator.RetentionOwnerToken.AsSpan().SequenceEqual(current.RetentionOwnerToken))
        {
            transaction.Rollback();
            return new(LocalWorkspaceNodeContentReadDisposition.Stale, null);
        }
        var selectionFailure = LocalWorkspaceNodeContentReadDisposition.Unavailable;
        var request = new RetentionReadRequest(new(current.RetentionStoreInstanceId!, RetentionStoreKind.RawRecord, current.SourceItemId!),
            RetentionReadKind.Access, now, current.RetentionRevision);
        var result = await catalog.ReadWithinCallerTransactionAsync<byte[]>(connection, transaction, request,
            async (c, t, grant, token) =>
            {
                var selected = await SelectAsync(c, t, grant, current, token).ConfigureAwait(false);
                selectionFailure = selected.Failure;
                return selected.Bytes;
            }, cancellationToken).ConfigureAwait(false);
        if (result.Lease is { } failedLease && result.Disposition is not null)
        {
            await using (failedLease.ConfigureAwait(false))
            {
                var terminal = result.CompletePostGrantFailure();
                return new(result.Disposition == RetentionReadDisposition.Busy || terminal != RetentionRawTerminalResult.CompletedWithoutRaw
                    ? LocalWorkspaceNodeContentReadDisposition.Busy : selectionFailure, null);
            }
        }
        if (result.Lease is { } lease) return new(LocalWorkspaceNodeContentReadDisposition.Granted, new(lease));
        return new(result.Disposition switch
        {
            RetentionReadDisposition.Busy => LocalWorkspaceNodeContentReadDisposition.Busy,
            RetentionReadDisposition.SelectorUnavailable or RetentionReadDisposition.ConsumptionUnavailable =>
                selectionFailure == LocalWorkspaceNodeContentReadDisposition.Granted
                    ? LocalWorkspaceNodeContentReadDisposition.Unavailable : selectionFailure,
            _ => LocalWorkspaceNodeContentReadDisposition.Stale,
        }, null);
    }

    private static async ValueTask<(byte[]? Bytes, LocalWorkspaceNodeContentReadDisposition Failure)> SelectAsync(
        SqliteConnection connection, SqliteTransaction transaction, RetentionReadGrant grant,
        LocalWorkspaceContentAvailability locator, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT r.payload_json FROM raw_records r
            JOIN retention_items i ON i.item_id=$retention_read_item_id AND i.store_kind='raw_record'
              AND i.source_item_id=CAST(r.id AS TEXT) AND i.revision=$retention_read_revision
            JOIN retention_leases l ON l.item_id=i.item_id AND l.lease_kind=$retention_read_lease_kind
              AND l.owner=$retention_read_lease_owner AND l.generation=$retention_read_lease_generation
              AND l.expires_at=$retention_read_lease_expires_at
            WHERE r.id=$raw_id AND r.retention_owner_token=$retention_read_source_token;
            """;
        command.Parameters.AddWithValue("$raw_id", long.Parse(locator.SourceItemId!, CultureInfo.InvariantCulture));
        grant.BindAdmissionSelectorCapability(command);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
        await using var stream = reader.GetStream(0);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > MaximumRawBytes) return (null, LocalWorkspaceNodeContentReadDisposition.Oversized);
            buffer.Write(chunk, 0, count);
        }
        var identity = locator.RevisionInput!.Split('|')[0].Split('/');
        return SelectInput(buffer.ToArray(), identity[0], identity[1], locator.Part == "error_message");
    }

    internal static (byte[]? Bytes, LocalWorkspaceNodeContentReadDisposition Failure) SelectInput(byte[] raw, string traceId, string spanId, bool error = false)
    {
        using var document = JsonDocument.Parse(raw);
        var matches = new List<JsonElement>();
        foreach (var resource in Array(document.RootElement, "resourceSpans"))
            foreach (var scope in Array(resource, "scopeSpans"))
                foreach (var span in Array(scope, "spans"))
                    if (Text(span, "traceId") == traceId && Text(span, "spanId") == spanId) matches.Add(span);
        if (matches.Count != 1) return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in error ? new[] { "error.type" } : new[] { "gen_ai.system_instructions", "gen_ai.input.messages", "gen_ai.output.messages" })
        {
            var attributes = Array(matches[0], "attributes").Where(item => Text(item, "key") == key).ToArray();
            if (attributes.Length > 1) return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
            if (attributes.Length == 0) continue;
            if (!attributes[0].TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
            var text = Text(value, "stringValue");
            if (text is null) return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
            fields.Add(key, text);
        }
        if (fields.Count == 0) return (null, LocalWorkspaceNodeContentReadDisposition.NotCaptured);
        var output = error ? StrictUtf8.GetBytes(fields["error.type"]) : JsonSerializer.SerializeToUtf8Bytes(fields);
        return output.Length > MaximumSelectedBytes
            ? (null, LocalWorkspaceNodeContentReadDisposition.Oversized)
            : (output, LocalWorkspaceNodeContentReadDisposition.Granted);
    }

    private static IEnumerable<JsonElement> Array(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray() : [];
    private static string? Text(JsonElement owner, string name) =>
        owner.ValueKind == JsonValueKind.Object && owner.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}

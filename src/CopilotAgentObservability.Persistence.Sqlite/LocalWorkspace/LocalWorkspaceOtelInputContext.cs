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
        JOIN session_events e ON e.event_id=n.source_identity AND e.session_id=n.session_id
        JOIN monitor_spans m ON m.trace_id=e.trace_id COLLATE BINARY
          AND e.source_event_id=m.trace_id||'/'||m.span_id COLLATE BINARY
        LEFT JOIN raw_records r ON r.id=m.raw_record_id
        LEFT JOIN retention_items i ON i.store_kind='raw_record'
          AND i.source_item_id=CAST(m.raw_record_id AS TEXT)
          AND i.store_instance_id=(SELECT store_instance_id FROM retention_store_instances WHERE id=1)
        LEFT JOIN retention_tombstones tombstone ON tombstone.item_id=i.item_id
        WHERE n.session_id=$session_id AND n.source_kind='session_event'
          AND e.source_adapter='otel-exact' AND e.type='otel.span' AND m.operation='chat'
          AND length(m.trace_id)=32 AND m.trace_id NOT GLOB '*[^0-9a-f]*'
          AND length(m.span_id)=16 AND m.span_id NOT GLOB '*[^0-9a-f]*'
          AND (SELECT COUNT(*) FROM monitor_spans other
            WHERE lower(other.trace_id)=m.trace_id AND lower(other.span_id)=m.span_id)=1
          AND (SELECT COUNT(*) FROM session_events other WHERE other.source_adapter='otel-exact'
            AND lower(other.source_event_id)=m.trace_id||'/'||m.span_id)=1
        """;

    internal static LocalWorkspaceSessionDetailContribution Apply(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        LocalWorkspaceSessionDetailContribution detail, DateTimeOffset acceptedAt)
    {
        var calls = detail.Nodes.Where(node => node.Kind == "llm_call").ToArray();
        if (calls.Length == 0) return detail;
        var content = detail.Content.ToList();
        foreach (var call in calls)
        {
            var availability = ReadAvailability(connection, transaction, sessionId, call.NodeId, acceptedAt);
            if (availability is null) continue;
            content.RemoveAll(item => item.NodeId == call.NodeId && item.Part == "event_content");
            content.Add(availability);
        }
        return detail with { Content = content.AsReadOnly() };
    }

    internal static LocalWorkspaceContentAvailability? ReadAvailability(
        SqliteConnection connection, SqliteTransaction transaction, string sessionId,
        string nodeId, DateTimeOffset acceptedAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT m.raw_record_id,m.trace_id,m.span_id,r.received_at,r.schema_version,r.retention_owner_token,
              i.item_id,i.store_instance_id,i.captured_at,i.expires_at,i.state,i.revision,i.ownership_receipt,
              i.read_denied_at,i.deleted_at,i.error_code,tombstone.receipt_at,tombstone.deleted_at
            """ + " " + BindingSql + " AND n.node_id=$node_id;";
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$node_id", nodeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
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
        return new(nodeId, "event_content", state, sourceId,
            identity + "|" + sourceId + "|" + revision?.ToString(CultureInfo.InvariantCulture),
            "raw_record", "otel_input_context", null, null,
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
        hash.AppendData("local-monitor-otel-input-context-v1\0"u8);
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
        var current = ReadAvailability(connection, transaction, sessionId, nodeId, now);
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
        if (locator.Part != "event_content" || locator.StoreKind != "raw_record" || locator.LocatorKind != "otel_input_context"
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
        return SelectInput(buffer.ToArray(), identity[0], identity[1]);
    }

    internal static (byte[]? Bytes, LocalWorkspaceNodeContentReadDisposition Failure) SelectInput(byte[] raw, string traceId, string spanId)
    {
        using var document = JsonDocument.Parse(raw);
        var matches = new List<JsonElement>();
        foreach (var resource in Array(document.RootElement, "resourceSpans"))
            foreach (var scope in Array(resource, "scopeSpans"))
                foreach (var span in Array(scope, "spans"))
                    if (Text(span, "traceId") == traceId && Text(span, "spanId") == spanId) matches.Add(span);
        if (matches.Count != 1) return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
        var fields = new List<string>();
        foreach (var key in new[] { "gen_ai.system_instructions", "gen_ai.input.messages" })
        {
            var attributes = Array(matches[0], "attributes").Where(item => Text(item, "key") == key).ToArray();
            if (attributes.Length > 1) return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
            if (attributes.Length == 0) continue;
            if (!attributes[0].TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Object)
                return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
            var text = Text(value, "stringValue");
            if (text is null) return (null, LocalWorkspaceNodeContentReadDisposition.Unavailable);
            fields.Add(key + ":\n" + text);
        }
        if (fields.Count == 0) return (null, LocalWorkspaceNodeContentReadDisposition.NotCaptured);
        var output = StrictUtf8.GetBytes(string.Join("\n\n", fields));
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

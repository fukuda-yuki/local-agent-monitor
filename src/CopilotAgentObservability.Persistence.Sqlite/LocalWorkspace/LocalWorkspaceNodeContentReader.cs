using System.Text;
using System.Text.Json;
using System.Data;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal enum LocalWorkspaceNodeContentReadDisposition
{
    Granted,
    Stale,
    Unavailable,
    Busy,
    Expired,
    Deleted,
    ReadDenied,
}

internal enum LocalWorkspaceNodeContentTerminalResult { Sealed, CompletedWithoutRaw, Lost, Busy }

internal sealed record LocalWorkspaceNodeContentReadResult(
    LocalWorkspaceNodeContentReadDisposition Disposition,
    LocalWorkspaceNodeContentReadLease? Lease);

internal interface ILocalWorkspaceNodeContentReader
{
    ValueTask<LocalWorkspaceNodeContentReadResult> ReadAsync(
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken);
}

internal sealed class LocalWorkspaceNodeContentReadLease : IAsyncDisposable
{
    private readonly RetentionReadLease<byte[]> lease;

    internal LocalWorkspaceNodeContentReadLease(RetentionReadLease<byte[]> lease) =>
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));

    internal RetentionReadValueReference<byte[]> AcquireBytesReference() => lease.AcquireValueReference();

    internal LocalWorkspaceNodeContentTerminalResult TrySealRawResponse() => Map(lease.TrySealRawResponse());

    internal LocalWorkspaceNodeContentTerminalResult TryCompleteWithoutRaw() => Map(lease.TryCompleteWithoutRaw());

    public ValueTask DisposeAsync() => lease.DisposeAsync();

    private static LocalWorkspaceNodeContentTerminalResult Map(RetentionRawTerminalResult result) => result switch
    {
        RetentionRawTerminalResult.Sealed => LocalWorkspaceNodeContentTerminalResult.Sealed,
        RetentionRawTerminalResult.CompletedWithoutRaw => LocalWorkspaceNodeContentTerminalResult.CompletedWithoutRaw,
        RetentionRawTerminalResult.Busy => LocalWorkspaceNodeContentTerminalResult.Busy,
        _ => LocalWorkspaceNodeContentTerminalResult.Lost,
    };
}

internal sealed class LocalWorkspaceNodeContentReader(
    RetentionCatalogContext retentionContext,
    TimeProvider? timeProvider = null) : ILocalWorkspaceNodeContentReader
{
    private const int MaximumBytes = 1_048_576;
    private const int MaximumEncodedStringBytes = MaximumBytes * 6 + 2;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;

    public async ValueTask<LocalWorkspaceNodeContentReadResult> ReadAsync(
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCoreAsync(sessionId, nodeId, locator, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(LocalWorkspaceNodeContentReadDisposition.Busy, null);
        }
        catch (SqliteException)
        {
            return new(LocalWorkspaceNodeContentReadDisposition.Unavailable, null);
        }
    }

    private async ValueTask<LocalWorkspaceNodeContentReadResult> ReadCoreAsync(
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(locator);
        if (!CompleteLocator(locator)) return new(LocalWorkspaceNodeContentReadDisposition.Stale, null);

        var catalog = new RetentionCatalogStore(retentionContext, timeProvider);
        using var gate = await catalog.EnterAdmissionGateAsync(cancellationToken).ConfigureAwait(false);
        using var connection = RetentionCatalogConnectionPolicy.OpenOrdinary(
            retentionContext.DatabasePath, SqliteOpenMode.ReadWrite);
        using var transaction = connection.BeginTransaction(deferred: false);

        try
        {
            if (!MatchesSnapshotTuple(connection, transaction, sessionId, nodeId, locator))
            {
                var lifecycle = ClassifyLifecycle(connection, transaction, locator, timeProvider.GetUtcNow());
                transaction.Rollback();
                return new(lifecycle is LocalWorkspaceNodeContentReadDisposition.Expired
                    or LocalWorkspaceNodeContentReadDisposition.Deleted
                    or LocalWorkspaceNodeContentReadDisposition.ReadDenied
                    ? lifecycle
                    : LocalWorkspaceNodeContentReadDisposition.Stale, null);
            }
            var currentLifecycle = ClassifyLifecycle(connection, transaction, locator, timeProvider.GetUtcNow());
            if (currentLifecycle is LocalWorkspaceNodeContentReadDisposition.Expired
                or LocalWorkspaceNodeContentReadDisposition.Deleted
                or LocalWorkspaceNodeContentReadDisposition.ReadDenied)
            {
                transaction.Rollback();
                return new(currentLifecycle, null);
            }

            var request = new RetentionReadRequest(
                new(locator.RetentionStoreInstanceId!, RetentionStoreKind.SessionEventContent, locator.SourceItemId!),
                RetentionReadKind.Access,
                timeProvider.GetUtcNow(),
                locator.RetentionRevision);
            var result = await catalog.ReadWithinCallerTransactionAsync(
                connection,
                transaction,
                request,
                (c, t, grant, token) => SelectBoundedAsync(c, t, grant, sessionId, locator, token),
                cancellationToken).ConfigureAwait(false);

            if (result.Lease is { } postGrantLease && result.Disposition is { } postGrantDisposition)
            {
                await using (postGrantLease.ConfigureAwait(false))
                {
                    var terminal = result.CompletePostGrantFailure();
                    return new(
                        postGrantDisposition == RetentionReadDisposition.Busy
                            || terminal != RetentionRawTerminalResult.CompletedWithoutRaw
                            ? LocalWorkspaceNodeContentReadDisposition.Busy
                            : LocalWorkspaceNodeContentReadDisposition.Unavailable,
                        null);
                }
            }
            if (result.Lease is { } lease)
                return new(LocalWorkspaceNodeContentReadDisposition.Granted, new(lease));
            return new(result.Disposition switch
            {
                RetentionReadDisposition.Busy => LocalWorkspaceNodeContentReadDisposition.Busy,
                RetentionReadDisposition.SelectorUnavailable or RetentionReadDisposition.ConsumptionUnavailable =>
                    LocalWorkspaceNodeContentReadDisposition.Unavailable,
                _ => LocalWorkspaceNodeContentReadDisposition.Stale,
            }, null);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            TryRollback(transaction);
            return new(LocalWorkspaceNodeContentReadDisposition.Busy, null);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException or FormatException or DecoderFallbackException or JsonException)
        {
            TryRollback(transaction);
            return new(LocalWorkspaceNodeContentReadDisposition.Unavailable, null);
        }
    }

    private static bool CompleteLocator(LocalWorkspaceContentAvailability locator) =>
        locator.State == "available"
        && locator.StoreKind == "session_event_content"
        && locator.SourceItemId is not null
        && locator.RevisionInput is not null
        && locator.RetentionItemId is not null
        && locator.RetentionStoreInstanceId is not null
        && locator.SourceCapturedAt is not null
        && locator.SourceExpiresAt is not null
        && locator.RetentionRevision is not null
        && locator.RetentionOwnershipReceipt is { Length: 32 }
        && locator.RetentionOwnerToken is { Length: 32 }
        && locator.SelectedUtf8Bytes is >= 0 and <= MaximumBytes
        && (locator.LocatorKind == "whole_event" && locator.JsonPointer is null
            || locator.LocatorKind == "json_pointer" && locator.JsonPointer is not null);

    private static bool MatchesSnapshotTuple(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId,
        string nodeId,
        LocalWorkspaceContentAvailability locator)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM local_workspace_node_content_refs r
            JOIN local_workspace_nodes n ON n.node_id=r.node_id
            JOIN session_events e ON e.event_id=r.source_item_id AND e.session_id=n.session_id
            JOIN session_event_content c ON c.event_id=e.event_id
            JOIN retention_items i ON i.item_id=r.retention_item_id
            WHERE r.node_id=$node_id AND n.session_id=$session_id AND e.type<>'skill.invoked'
              AND r.part=$part AND r.availability_state='available'
              AND r.source_item_id=$source_item_id AND r.revision_input=$revision_input
              AND r.store_kind=$store_kind AND r.locator_kind=$locator_kind
              AND r.json_pointer IS $json_pointer AND r.selected_utf8_bytes=$selected_utf8_bytes
              AND r.retention_item_id=$retention_item_id
              AND r.retention_store_instance_id=$retention_store_instance_id
              AND r.source_captured_at=$source_captured_at AND r.source_expires_at=$source_expires_at
              AND r.retention_revision=$retention_revision
              AND r.retention_ownership_receipt=$retention_ownership_receipt
              AND r.retention_owner_token=$retention_owner_token
              AND i.store_instance_id=r.retention_store_instance_id
              AND i.store_kind=r.store_kind AND i.source_item_id=r.source_item_id
              AND i.revision=r.retention_revision AND i.ownership_receipt=r.retention_ownership_receipt
              AND i.captured_at=r.source_captured_at AND i.expires_at=r.source_expires_at
              AND c.captured_at=r.source_captured_at AND c.expires_at=r.source_expires_at
              AND c.retention_owner_token=r.retention_owner_token;
            """;
        Add(command, "$node_id", nodeId);
        Add(command, "$session_id", sessionId);
        Add(command, "$part", locator.Part);
        Add(command, "$source_item_id", locator.SourceItemId!);
        Add(command, "$revision_input", locator.RevisionInput!);
        Add(command, "$store_kind", locator.StoreKind!);
        Add(command, "$locator_kind", locator.LocatorKind!);
        Add(command, "$json_pointer", locator.JsonPointer);
        Add(command, "$selected_utf8_bytes", locator.SelectedUtf8Bytes!.Value);
        Add(command, "$retention_item_id", locator.RetentionItemId!);
        Add(command, "$retention_store_instance_id", locator.RetentionStoreInstanceId!);
        Add(command, "$source_captured_at", locator.SourceCapturedAt!);
        Add(command, "$source_expires_at", locator.SourceExpiresAt!);
        Add(command, "$retention_revision", locator.RetentionRevision!.Value);
        Add(command, "$retention_ownership_receipt", locator.RetentionOwnershipReceipt!);
        Add(command, "$retention_owner_token", locator.RetentionOwnerToken!);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static LocalWorkspaceNodeContentReadDisposition ClassifyLifecycle(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalWorkspaceContentAvailability locator,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT state,read_denied_at,deleted_at,expires_at FROM retention_items WHERE item_id=$item_id AND store_instance_id=$store_instance_id AND store_kind='session_event_content' AND source_item_id=$source_item_id;";
        Add(command, "$item_id", locator.RetentionItemId!);
        Add(command, "$store_instance_id", locator.RetentionStoreInstanceId!);
        Add(command, "$source_item_id", locator.SourceItemId!);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return LocalWorkspaceNodeContentReadDisposition.Stale;
        var state = reader.GetString(0);
        if (state == "deleted" || !reader.IsDBNull(2)) return LocalWorkspaceNodeContentReadDisposition.Deleted;
        if (state == "expired_pending_deletion"
            || state == "expiring" && DateTimeOffset.ParseExact(reader.GetString(3), "O", System.Globalization.CultureInfo.InvariantCulture) <= now)
            return LocalWorkspaceNodeContentReadDisposition.Expired;
        if (state is "deletion_queued" or "deleting" or "deletion_failed" || !reader.IsDBNull(1))
            return LocalWorkspaceNodeContentReadDisposition.ReadDenied;
        return LocalWorkspaceNodeContentReadDisposition.Stale;
    }

    private static async ValueTask<byte[]?> SelectBoundedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        string sessionId,
        LocalWorkspaceContentAvailability locator,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.rowid,c.content_json
            FROM session_event_content c
            JOIN session_events e ON e.event_id=c.event_id
            JOIN retention_items i ON i.item_id=$retention_read_item_id
              AND i.store_instance_id=$retention_store_instance_id
              AND i.store_kind='session_event_content' AND i.source_item_id=c.event_id
              AND i.revision=$retention_read_revision
            JOIN retention_leases l ON l.item_id=i.item_id
              AND l.lease_kind=$retention_read_lease_kind AND l.owner=$retention_read_lease_owner
              AND l.generation=$retention_read_lease_generation AND l.expires_at=$retention_read_lease_expires_at
            WHERE e.session_id=$session_id AND c.event_id=$event_id
              AND e.type<>'skill.invoked' AND c.retention_owner_token=$retention_read_source_token;
            """;
        Add(command, "$session_id", sessionId);
        Add(command, "$event_id", locator.SourceItemId!);
        Add(command, "$retention_store_instance_id", locator.RetentionStoreInstanceId!);
        grant.BindAdmissionSelectorCapability(command);
        using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(1)) return null;
        await using var stream = reader.GetStream(1);
        var bytes = locator.LocatorKind == "whole_event"
            ? ReadWholeEvent(stream)
            : TopLevelJsonValueExtractor.Read(stream, locator.JsonPointer![1..]);
        if (bytes is null || bytes.LongLength != locator.SelectedUtf8Bytes || bytes.Length > MaximumBytes) return null;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return bytes;
    }

    private static byte[]? ReadWholeEvent(Stream stream)
    {
        using var result = new MemoryStream(MaximumBytes + 1);
        var buffer = new byte[8192];
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, MaximumBytes + 1 - (int)result.Length));
            if (read == 0) break;
            result.Write(buffer, 0, read);
            if (result.Length > MaximumBytes) return null;
        }
        var bytes = result.ToArray();
        _ = StrictUtf8.GetString(bytes);
        using (JsonDocument.Parse(bytes)) { }
        return bytes;
    }

    private sealed class TopLevelJsonValueExtractor(Stream stream)
    {
        private readonly Decoder decoder = StrictUtf8.GetDecoder();
        private int pending = -1;

        internal static byte[]? Read(Stream stream, string property) => new TopLevelJsonValueExtractor(stream).ReadObject(property);

        private byte[]? ReadObject(string property)
        {
            if (NextNonWhitespace() != (byte)'{') throw new JsonException();
            byte[]? selected = null;
            var next = NextNonWhitespace();
            if (next == (byte)'}') { Finish(); return null; }
            PutBack(next);
            while (true)
            {
                var keyToken = ReadString(true, 256)!;
                var key = JsonSerializer.Deserialize<string>(keyToken) ?? throw new JsonException();
                if (NextNonWhitespace() != (byte)':') throw new JsonException();
                var value = ReadValue(StringComparer.Ordinal.Equals(key, property));
                if (StringComparer.Ordinal.Equals(key, property)) selected = value;
                next = NextNonWhitespace();
                if (next == (byte)'}') break;
                if (next != (byte)',') throw new JsonException();
            }
            if (NextNonWhitespaceOrEnd() is not null) throw new JsonException();
            Finish();
            return selected;
        }

        private byte[]? ReadValue(bool capture)
        {
            var first = NextNonWhitespace();
            if (first == (byte)'"')
            {
                PutBack(first);
                var raw = ReadString(capture, MaximumEncodedStringBytes);
                if (!capture) return null;
                var text = JsonSerializer.Deserialize<string>(raw!) ?? throw new JsonException();
                return StrictUtf8.GetBytes(text);
            }
            using var output = capture ? new MemoryStream(MaximumBytes + 1) : null;
            Write(output, first);
            if (first is (byte)'{' or (byte)'[')
            {
                var depth = 1;
                var inString = false;
                var escaped = false;
                while (depth > 0)
                {
                    var value = Next();
                    Write(output, value);
                    if (inString)
                    {
                        if (escaped) escaped = false;
                        else if (value == (byte)'\\') escaped = true;
                        else if (value == (byte)'"') inString = false;
                    }
                    else if (value == (byte)'"') inString = true;
                    else if (value is (byte)'{' or (byte)'[') depth++;
                    else if (value is (byte)'}' or (byte)']') depth--;
                }
            }
            else
            {
                while (true)
                {
                    var value = Next();
                    if (value is (byte)',' or (byte)'}' || IsWhitespace(value)) { PutBack(value); break; }
                    Write(output, value);
                }
            }
            if (!capture) return null;
            if (output!.Length > MaximumBytes) return null;
            var bytes = output.ToArray();
            using (JsonDocument.Parse(bytes)) { }
            return bytes;
        }

        private byte[]? ReadString(bool capture, int maximumBytes)
        {
            if (Next() != (byte)'"') throw new JsonException();
            using var output = capture ? new MemoryStream(Math.Min(maximumBytes + 1, 4096)) : null;
            Write(output, (byte)'"', maximumBytes);
            var escaped = false;
            while (true)
            {
                var value = Next();
                Write(output, value, maximumBytes);
                if (escaped) escaped = false;
                else if (value == (byte)'\\') escaped = true;
                else if (value == (byte)'"') break;
                else if (value < 0x20) throw new JsonException();
            }
            if (!capture) return null;
            if (output!.Length > maximumBytes) throw new JsonException();
            return output.ToArray();
        }

        private byte NextNonWhitespace()
        {
            byte value;
            do value = Next(); while (IsWhitespace(value));
            return value;
        }

        private byte? NextNonWhitespaceOrEnd()
        {
            while (true)
            {
                var value = NextOrEnd();
                if (value is null || !IsWhitespace(value.Value)) return value;
            }
        }

        private byte Next() => NextOrEnd() ?? throw new JsonException();

        private byte? NextOrEnd()
        {
            if (pending >= 0) { var value = (byte)pending; pending = -1; return value; }
            var valueRead = stream.ReadByte();
            if (valueRead < 0) return null;
            Span<byte> input = stackalloc byte[1] { (byte)valueRead };
            Span<char> chars = stackalloc char[2];
            decoder.Convert(input, chars, false, out _, out _, out _);
            return (byte)valueRead;
        }

        private void Finish()
        {
            Span<byte> input = [];
            Span<char> chars = stackalloc char[2];
            decoder.Convert(input, chars, true, out _, out _, out _);
        }

        private void PutBack(byte value)
        {
            if (pending >= 0) throw new InvalidOperationException();
            pending = value;
        }

        private static bool IsWhitespace(byte value) => value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';

        private static void Write(MemoryStream? output, byte value, int maximumBytes = MaximumBytes)
        {
            if (output is null || output.Length > maximumBytes) return;
            output.WriteByte(value);
        }
    }

    private static void Add(SqliteCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static void TryRollback(SqliteTransaction transaction)
    {
        try { transaction.Rollback(); }
        catch (Exception exception) when (exception is InvalidOperationException or SqliteException) { }
    }
}

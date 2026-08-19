using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

internal enum SkillInvocationSnapshotContentOutcome
{
    Granted,
    NotFound,
    Expired,
    ContentUnavailable,
    Unavailable,
    Busy,
    Aborted,
}

internal enum SkillInvocationSnapshotContentTerminalResult
{
    Sealed,
    CompletedWithoutRaw,
    Busy,
    Lost,
}

internal sealed record SkillInvocationSnapshotContentFacts(
    Guid SnapshotId,
    string Body,
    string DefinitionPath,
    string BodySha256,
    string DefinitionPathSha256,
    long BodyUtf8Bytes,
    long DefinitionPathUtf8Bytes,
    DateTimeOffset CapturedAt);

internal sealed record SkillInvocationSnapshotContentReadResult(
    SkillInvocationSnapshotContentOutcome Outcome,
    SkillInvocationSnapshotContentLease? Lease,
    SkillInvocationSnapshotContentFacts? Facts)
{
    internal bool IsGranted =>
        Outcome == SkillInvocationSnapshotContentOutcome.Granted && Lease is not null && Facts is not null;
}

internal sealed class SkillInvocationSnapshotContentLease : IAsyncDisposable
{
    private readonly RetentionReadLease<SkillInvocationSnapshotContentFacts> lease;
    private readonly SkillInvocationSnapshotContentFacts facts;

    // The facts are snapshotted through a short-lived value reference and the reference is released
    // immediately: Retention terminal operations require zero outstanding value references, so a
    // reference held for the lease lifetime would make every TrySealRawResponse/TryCompleteWithoutRaw
    // report Lost.
    internal SkillInvocationSnapshotContentLease(RetentionReadLease<SkillInvocationSnapshotContentFacts> lease)
    {
        this.lease = lease ?? throw new ArgumentNullException(nameof(lease));
        using (var reference = lease.AcquireValueReference())
        {
            facts = reference.Value;
        }
    }

    internal SkillInvocationSnapshotContentFacts Facts => facts;

    internal SkillInvocationSnapshotContentTerminalResult TrySealRawResponse() =>
        MapTerminal(lease.TrySealRawResponse());

    internal SkillInvocationSnapshotContentTerminalResult TryCompleteWithoutRaw() =>
        MapTerminal(lease.TryCompleteWithoutRaw());

    public ValueTask DisposeAsync() => lease.DisposeAsync();

    private static SkillInvocationSnapshotContentTerminalResult MapTerminal(RetentionRawTerminalResult result) =>
        result switch
        {
            RetentionRawTerminalResult.Sealed => SkillInvocationSnapshotContentTerminalResult.Sealed,
            RetentionRawTerminalResult.CompletedWithoutRaw => SkillInvocationSnapshotContentTerminalResult.CompletedWithoutRaw,
            RetentionRawTerminalResult.Busy => SkillInvocationSnapshotContentTerminalResult.Busy,
            _ => SkillInvocationSnapshotContentTerminalResult.Lost,
        };
}

// Gate 2 and Gate 5 of docs/specifications/interfaces/skill-invocation-snapshot.md are the sole
// authority for the read flow below: the metadata reader proves the graph before any lease is
// admitted, the selector re-proves the live graph under the grant, and every proof failure after
// the grant keeps the lease retained for a store-backed terminal completion.
internal static class SkillInvocationSnapshotContentReader
{
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'+00:00'";
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static async Task<SkillInvocationSnapshotContentReadResult> ReadAsync(
        string databasePath,
        RetentionCatalogStore retentionStore,
        TimeProvider timeProvider,
        Guid sessionId,
        Guid snapshotId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(retentionStore);
        ArgumentNullException.ThrowIfNull(timeProvider);
        cancellationToken.ThrowIfCancellationRequested();

        var metadata = SkillInvocationSnapshotMetadataReader.ReadOwnedTransaction(
            databasePath, sessionId, snapshotId, timeProvider);
        switch (metadata.Outcome)
        {
            case SkillInvocationSnapshotMetadataOutcome.NotFound:
                return new(SkillInvocationSnapshotContentOutcome.NotFound, null, null);
            case SkillInvocationSnapshotMetadataOutcome.Busy:
                return new(SkillInvocationSnapshotContentOutcome.Busy, null, null);
            case SkillInvocationSnapshotMetadataOutcome.Unavailable:
                return new(SkillInvocationSnapshotContentOutcome.Unavailable, null, null);
        }

        var metadataFacts = metadata.Facts!;
        if (metadataFacts.RetentionProjection == SkillInvocationSnapshotMetadataRetentionProjection.UnreadableOrDeleted)
            return new(SkillInvocationSnapshotContentOutcome.Expired, null, null);
        if (!metadataFacts.IsAvailable)
            return new(SkillInvocationSnapshotContentOutcome.ContentUnavailable, null, null);

        var request = new RetentionReadRequest(
            new(retentionStore.StoreInstanceId, RetentionStoreKind.SessionEventContent, metadataFacts.EventId.ToString("D")),
            RetentionReadKind.Access,
            timeProvider.GetUtcNow(),
            ExpectedRevision: null);

        RetentionReadResult<SkillInvocationSnapshotContentFacts> result;
        try
        {
            result = await retentionStore.ReadAsync(
                request,
                (connection, transaction, grant, token) => SelectContentAsync(
                    connection, transaction, grant, retentionStore.StoreInstanceId, sessionId, snapshotId, metadataFacts, token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(SkillInvocationSnapshotContentOutcome.Busy, null, null);
        }
        catch (SqliteException)
        {
            return new(SkillInvocationSnapshotContentOutcome.Unavailable, null, null);
        }

        if (result.Lease is { } retainedLease && result.Disposition is { } postGrantDisposition)
        {
            await using (retainedLease.ConfigureAwait(false))
            {
                var terminal = result.CompletePostGrantFailure();
                if (postGrantDisposition == RetentionReadDisposition.Busy)
                    return new(SkillInvocationSnapshotContentOutcome.Busy, null, null);
                return terminal == RetentionRawTerminalResult.CompletedWithoutRaw
                    ? new(SkillInvocationSnapshotContentOutcome.Unavailable, null, null)
                    : new(SkillInvocationSnapshotContentOutcome.Aborted, null, null);
            }
        }

        if (result.Lease is { } grantedLease)
        {
            SkillInvocationSnapshotContentLease contentLease;
            try
            {
                contentLease = new SkillInvocationSnapshotContentLease(grantedLease);
            }
            catch (InvalidOperationException)
            {
                await grantedLease.DisposeAsync().ConfigureAwait(false);
                return new(SkillInvocationSnapshotContentOutcome.Aborted, null, null);
            }

            return new(SkillInvocationSnapshotContentOutcome.Granted, contentLease, contentLease.Facts);
        }

        return result.Disposition switch
        {
            RetentionReadDisposition.LifecycleDenied => new(SkillInvocationSnapshotContentOutcome.Expired, null, null),
            RetentionReadDisposition.Busy => new(SkillInvocationSnapshotContentOutcome.Busy, null, null),
            RetentionReadDisposition.SelectorUnavailable => new(SkillInvocationSnapshotContentOutcome.Unavailable, null, null),
            _ => new(SkillInvocationSnapshotContentOutcome.Aborted, null, null),
        };
    }

    private static async ValueTask<SkillInvocationSnapshotContentFacts?> SelectContentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadGrant grant,
        string storeInstanceId,
        Guid sessionId,
        Guid snapshotId,
        SkillInvocationSnapshotMetadataFacts metadataFacts,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT c.content_json,s.payload_sha256,s.payload_bytes,s.content_document_sha256,
                   s.body_sha256,s.body_utf8_bytes,s.definition_path_sha256,s.definition_path_utf8_bytes
            FROM session_event_content c
            JOIN skill_invocation_snapshots s ON s.snapshot_id=$snapshot_id AND s.session_id=$session_id
                AND s.event_id=c.event_id
            JOIN retention_items i ON i.item_id=$retention_read_item_id
                AND i.store_instance_id=$retention_store_instance_id
                AND i.store_kind='session_event_content'
                AND i.source_item_id=c.event_id
                AND i.item_id=s.content_item_id
                AND i.revision=$retention_read_revision
            JOIN retention_leases l ON l.item_id=i.item_id
                AND l.lease_kind=$retention_read_lease_kind
                AND l.owner=$retention_read_lease_owner
                AND l.generation=$retention_read_lease_generation
                AND l.expires_at=$retention_read_lease_expires_at
            WHERE c.retention_owner_token=$retention_read_source_token
                AND c.content_kind='application/json'
                AND c.captured_at=$expected_captured_at
                AND c.expires_at=i.expires_at
                AND s.state='available';
            """;
        command.Parameters.AddWithValue("$snapshot_id", snapshotId.ToString("D"));
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$retention_store_instance_id", storeInstanceId);
        command.Parameters.AddWithValue("$expected_captured_at", FormatTimestamp(metadataFacts.CapturedAt));
        grant.BindAdmissionSelectorCapability(command);

        try
        {
            using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return null;

            var contentJson = reader.GetString(0);
            var payloadSha256 = reader.GetString(1);
            var payloadBytes = reader.GetInt64(2);
            var contentDocumentSha256 = reader.GetString(3);
            var bodySha256 = reader.IsDBNull(4) ? null : reader.GetString(4);
            var bodyUtf8Bytes = reader.IsDBNull(5) ? null : (long?)reader.GetInt64(5);
            var definitionPathSha256 = reader.IsDBNull(6) ? null : reader.GetString(6);
            var definitionPathUtf8Bytes = reader.IsDBNull(7) ? null : (long?)reader.GetInt64(7);
            return ProveContent(
                snapshotId, metadataFacts, contentJson, payloadSha256, payloadBytes, contentDocumentSha256,
                bodySha256, bodyUtf8Bytes, definitionPathSha256, definitionPathUtf8Bytes);
        }
        catch (Exception exception) when (
            exception is InvalidCastException or FormatException or OverflowException or ArithmeticException)
        {
            return null;
        }
    }

    private static SkillInvocationSnapshotContentFacts? ProveContent(
        Guid snapshotId,
        SkillInvocationSnapshotMetadataFacts metadataFacts,
        string contentJson,
        string payloadSha256,
        long payloadBytes,
        string contentDocumentSha256,
        string? bodySha256,
        long? bodyUtf8Bytes,
        string? definitionPathSha256,
        long? definitionPathUtf8Bytes)
    {
        byte[] documentUtf8;
        try
        {
            documentUtf8 = StrictUtf8.GetBytes(contentJson);
        }
        catch (EncoderFallbackException)
        {
            return null;
        }

        if (!string.Equals(
            SkillInvocationSnapshotContentDocumentV1.ContentDocumentSha256(documentUtf8),
            contentDocumentSha256,
            StringComparison.Ordinal))
            return null;

        if (!SkillInvocationSnapshotContentDocumentV1.TryReadPayloadToken(documentUtf8, out var payloadTokenUtf8, out _))
            return null;

        if (payloadTokenUtf8.LongLength != payloadBytes)
            return null;
        if (!string.Equals(
            SkillInvocationSnapshotContentDocumentV1.PayloadSha256(payloadTokenUtf8),
            payloadSha256,
            StringComparison.Ordinal))
            return null;

        var classification = SkillInvocationPayloadClassifierV1.Classify(payloadTokenUtf8);
        if (!classification.WellFormedToken
            || classification.ObservedInvalidUtf8
            || classification.State != SkillInvocationPayloadState.Available
            || classification.Reason != SkillInvocationPayloadReason.None
            || classification.AvailableFacts is not { } availableFacts)
            return null;

        var bodyUtf8 = StrictUtf8.GetBytes(availableFacts.Body);
        var definitionPathUtf8 = StrictUtf8.GetBytes(availableFacts.DefinitionPath);
        if (!HexEquals(SHA256.HashData(bodyUtf8), bodySha256) || bodyUtf8.LongLength != bodyUtf8Bytes)
            return null;
        if (!HexEquals(SHA256.HashData(definitionPathUtf8), definitionPathSha256)
            || definitionPathUtf8.LongLength != definitionPathUtf8Bytes)
            return null;

        if (!string.Equals(metadataFacts.BodySha256, bodySha256, StringComparison.Ordinal)
            || metadataFacts.BodyUtf8Bytes != (ulong?)bodyUtf8Bytes
            || !string.Equals(metadataFacts.DefinitionPathSha256, definitionPathSha256, StringComparison.Ordinal)
            || metadataFacts.DefinitionPathUtf8Bytes != (ulong?)definitionPathUtf8Bytes)
            return null;

        return new SkillInvocationSnapshotContentFacts(
            snapshotId,
            availableFacts.Body,
            availableFacts.DefinitionPath,
            bodySha256!,
            definitionPathSha256!,
            bodyUtf8Bytes!.Value,
            definitionPathUtf8Bytes!.Value,
            metadataFacts.CapturedAt);
    }

    private static bool HexEquals(ReadOnlySpan<byte> hash, string? hex) =>
        hex is not null
        && string.Equals(Convert.ToHexString(hash).ToLowerInvariant(), hex, StringComparison.Ordinal);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString(TimestampFormat, CultureInfo.InvariantCulture);
}

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli;

internal enum RetainedRawReplayReadDisposition { Granted, NotFound, Denied, Busy }

internal sealed record RetainedRawReplayReadResult(RetainedRawReplayReadDisposition Disposition, RetainedRawReplayLease? Lease);

internal sealed class RetainedRawReplayLease : IAsyncDisposable
{
    private readonly Func<ValueTask> release;
    private int released;
    internal RetainedRawReplayLease(RawReplayReceipt receipt, Func<ValueTask> release) => (Receipt, this.release) = (receipt, release);
    internal RawReplayReceipt Receipt { get; }
    public ValueTask DisposeAsync() => Interlocked.Exchange(ref released, 1) == 0 ? release() : ValueTask.CompletedTask;
}

internal sealed class RetentionRawReplayStore
{
    private readonly RetentionCatalogStore catalog;
    private readonly string parent;
    private readonly TimeProvider timeProvider;
    private readonly RawReplayEngine engine = new();

    internal RetentionRawReplayStore(RetentionCatalogStore catalog, string? parentLocator = null, TimeProvider? timeProvider = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        parent = Path.GetFullPath(parentLocator ?? Path.Combine(Path.GetTempPath(), "copilot-agent-observability", "raw-replays"));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async ValueTask<RawReplayExecutionResult> ReplayAsync(string replayId, byte[] archive, CancellationToken cancellationToken)
    {
        var existing = await ReadAsync(replayId, cancellationToken).ConfigureAwait(false);
        if (existing.Disposition is RetainedRawReplayReadDisposition.Busy) return Failure("replay_store_busy");
        if (existing.Disposition is RetainedRawReplayReadDisposition.Denied) return Failure("replay_store_denied");
        if (existing.Lease is not null)
        {
            await using var lease = existing.Lease;
            return engine.Replay(replayId, archive, existing: lease.Receipt);
        }

        var execution = engine.Replay(replayId, archive);
        if (!execution.Success) return execution;
        try
        {
            new RetentionSensitiveBundleStore(catalog).CaptureRawReplay(replayId, archive, execution, parent);
            return execution;
        }
        catch (ArgumentException)
        {
            return Failure("staging_size_limit_exceeded");
        }
        catch (InvalidOperationException)
        {
            var raced = await ReadAsync(replayId, cancellationToken).ConfigureAwait(false);
            if (raced.Lease is null) return Failure("replay_publish_failed");
            await using var lease = raced.Lease;
            return engine.Replay(replayId, archive, existing: lease.Receipt);
        }
    }

    internal async ValueTask<RetainedRawReplayReadResult> ReadAsync(string replayId, CancellationToken cancellationToken)
    {
        if (!ValidReplayId(replayId)) return new(RetainedRawReplayReadDisposition.NotFound, null);
        var captureId = CaptureId(replayId);
        var request = new RetentionReadRequest(
            new(catalog.StoreInstanceId, RetentionStoreKind.SensitiveBundle, captureId),
            RetentionReadKind.Operation,
            timeProvider.GetUtcNow(),
            ExpectedRevision: null);
        var result = await catalog.ReadAsync(request, (connection, transaction, grant, _) =>
            ValueTask.FromResult(ReadReceipt(connection, transaction, grant, replayId, captureId)), cancellationToken).ConfigureAwait(false);
        if (result.Disposition != RetentionReadDisposition.Granted || result.Lease is null)
            return new(result.Disposition switch
            {
                RetentionReadDisposition.NotFound => RetainedRawReplayReadDisposition.NotFound,
                RetentionReadDisposition.Busy => RetainedRawReplayReadDisposition.Busy,
                _ => RetainedRawReplayReadDisposition.Denied,
            }, null);
        var lease = result.Lease;
        return new(RetainedRawReplayReadDisposition.Granted, new(lease.Value, lease.DisposeAsync));
    }

    internal static string CaptureId(string replayId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Frame(hash, "copilot-agent-observability/raw-local-replay-namespace/v1");
        Frame(hash, replayId);
        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 16));
    }

    private RawReplayReceipt? ReadReceipt(SqliteConnection connection, SqliteTransaction transaction, RetentionReadGrant grant,
        string replayId, string captureId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.private_locator
            FROM retention_items i
            WHERE i.item_id=$retention_read_item_id AND i.revision=$retention_read_revision
              AND i.store_instance_id=$store AND i.store_kind='sensitive_bundle' AND i.source_item_id=$capture
              AND EXISTS (SELECT 1 FROM retention_file_capture_reservations r WHERE r.capture_id=$capture
                AND r.store_instance_id=$store AND r.store_kind='sensitive_bundle' AND r.source_item_id=$capture
                AND r.phase='complete' AND r.owner_token=$retention_read_source_token)
              AND EXISTS (SELECT 1 FROM retention_leases l WHERE l.item_id=i.item_id AND l.lease_kind='operation'
                AND l.owner=$retention_read_lease_owner AND l.generation=$retention_read_lease_generation AND l.expires_at=$retention_read_lease_expires_at);
            """;
        command.Parameters.AddWithValue("$store", catalog.StoreInstanceId);
        command.Parameters.AddWithValue("$capture", captureId);
        grant.BindSelectorCapability(command);
        var locator = command.ExecuteScalar() as string;
        if (locator is null || !string.Equals(locator, Path.Combine(parent, captureId), StringComparison.Ordinal)) return null;
        try
        {
            var manifestPath = Path.Combine(locator, "manifest.json");
            if (!SafeFile(manifestPath, 1024 * 1024, out var manifestBytes)) return null;
            var manifest = RawReplayJson.DeserializeExact<RawReplayRetentionManifest>(manifestBytes);
            if (!RawReplayJson.IsCanonical(manifestBytes, manifest)
                || manifest.SchemaVersion != RawReplaySensitiveBundlePlanBuilder.SchemaVersion
                || manifest.Profile != RawReplayContractVersions.BundleProfile || manifest.ReplayId != replayId
                || manifest.CaptureId != captureId || manifest.ExpiresAt != manifest.ReservedAt.Add(RetentionV1Constants.SensitiveBundleTtl)
                || manifest.Files.Count != 5 || manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != 5) return null;
            foreach (var file in manifest.Files)
            {
                if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(file.Path)
                    || file.Path is not ("input/archive.zip" or "output/result.json" or "output/normalized.json" or "output/projection.json" or "output/dashboard.json")
                    || file.Size < 0 || file.Size > RetentionV1Constants.MaximumFileBytes
                    || !IsSha(file.Sha256)) return null;
                var path = Path.GetFullPath(Path.Combine(locator, file.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!path.StartsWith(locator + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !SafeFile(path, file.Size, out var bytes, exact: true)
                    || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(file.Sha256))) return null;
            }
            var resultFile = manifest.Files.Single(file => file.Path == "output/result.json");
            if (!SafeFile(Path.Combine(locator, resultFile.Path.Replace('/', Path.DirectorySeparatorChar)), resultFile.Size, out var resultBytes, exact: true)) return null;
            var receipt = RawReplayJson.DeserializeExact<RawReplayReceipt>(resultBytes);
            return RawReplayJson.IsCanonical(resultBytes, receipt)
                && RawReplayJson.SerializeCanonical(receipt).AsSpan().SequenceEqual(RawReplayJson.SerializeCanonical(manifest.Receipt))
                && receipt.ReplayId == replayId ? receipt : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool SafeFile(string path, long maximum, out byte[] bytes, bool exact = false)
    {
        bytes = [];
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        var length = new FileInfo(path).Length;
        if (length < 0 || exact && length != maximum || !exact && length > maximum || length > int.MaxValue) return false;
        bytes = File.ReadAllBytes(path);
        return bytes.LongLength == length;
    }

    private static bool ValidReplayId(string? value) => value is { Length: >= 8 and <= 64 }
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && !RawReplayCredentialScanner.ContainsKnownCredential(value);
    private static bool IsSha(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static RawReplayExecutionResult Failure(string code) => new(false, code, false, null, null, null, null, null, [], []);
    private static void Frame(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value); Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length); hash.AppendData(length); hash.AppendData(bytes);
    }
}

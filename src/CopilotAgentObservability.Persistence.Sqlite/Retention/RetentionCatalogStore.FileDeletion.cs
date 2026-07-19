using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    private static SourceReceiptProof ValidateSensitiveBundleFirstIntentEvidence(SqliteConnection connection, SqliteTransaction transaction, string itemId, string store, string captureId, byte[] receipt)
    {
        try
        {
            if (!CanonicalId(captureId) || !string.Equals(store, StoreId(connection, transaction), StringComparison.Ordinal)) return SourceReceiptProof.InvalidIdentity;
            using var locatorCommand = connection.CreateCommand();
            locatorCommand.Transaction = transaction;
            locatorCommand.CommandText = "SELECT private_locator FROM retention_items WHERE item_id=$id;";
            locatorCommand.Parameters.AddWithValue("$id", itemId);
            var locator = locatorCommand.ExecuteScalar() as string;
            var row = Load(connection, transaction, captureId);
            if (row is null) return SourceReceiptProof.Missing;
            if (row.Phase != RetentionCapturePhase.Complete || row.MarkerSha256 is null || row.ManifestSha256 is null || locator is null
                || !string.Equals(row.Store, store, StringComparison.Ordinal) || !string.Equals(locator, row.Final, StringComparison.Ordinal)) return SourceReceiptProof.InvalidOrMismatched;
            var expectedReceipt = RetentionOwnershipReceipt.CreateSensitiveBundle(new(row.Store, row.CaptureId, row.ReservedAt, row.Ticks, row.MarkerSha256, row.ManifestSha256, row.Token));
            if (!RetentionOwnershipReceipt.Matches(expectedReceipt, receipt)) return SourceReceiptProof.InvalidOrMismatched;
            return ReadSensitiveBundleProof(row).Disposition switch
            {
                RetentionSensitiveBundleDeletionPlanDisposition.Ready => SourceReceiptProof.Match,
                RetentionSensitiveBundleDeletionPlanDisposition.Missing => SourceReceiptProof.Missing,
                RetentionSensitiveBundleDeletionPlanDisposition.InvalidIdentity => SourceReceiptProof.InvalidIdentity,
                RetentionSensitiveBundleDeletionPlanDisposition.Busy => SourceReceiptProof.CatalogBusy,
                _ => SourceReceiptProof.InvalidOrMismatched
            };
        }
        catch (RetentionMigrationBlockedException) { return SourceReceiptProof.InvalidOrMismatched; }
        catch (ArgumentException) { return SourceReceiptProof.InvalidIdentity; }
        catch (FormatException) { return SourceReceiptProof.InvalidIdentity; }
        catch (SqliteException) { return SourceReceiptProof.CatalogBusy; }
    }

    internal RetentionSensitiveBundleDeletionPlanResult LoadSensitiveBundleDeletionPlan(RetentionDeleteContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = OpenDeletion();
            using var transaction = connection.BeginTransaction(deferred: false);
            var result = LoadSensitiveBundleDeletionPlan(connection, transaction, context, now);
            transaction.Commit();
            return result;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(RetentionSensitiveBundleDeletionPlanDisposition.Busy, null);
        }
        catch (UnauthorizedAccessException)
        {
            return new(RetentionSensitiveBundleDeletionPlanDisposition.Busy, null);
        }
        catch (IOException)
        {
            return new(RetentionSensitiveBundleDeletionPlanDisposition.Busy, null);
        }
        catch (ArgumentException)
        {
            return new(RetentionSensitiveBundleDeletionPlanDisposition.InvalidIdentity, null);
        }
        catch (RetentionMigrationBlockedException)
        {
            return new(RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null);
        }
    }

    private RetentionSensitiveBundleDeletionPlanResult LoadSensitiveBundleDeletionPlan(SqliteConnection connection, SqliteTransaction transaction, RetentionDeleteContext context, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,private_locator,state,revision,adapter_coverage_version FROM retention_items WHERE item_id=$id;";
        command.Parameters.AddWithValue("$id", context.ItemId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new(RetentionSensitiveBundleDeletionPlanDisposition.LeaseLost, null);
        var store = reader.GetString(0);
        var kind = reader.GetString(1);
        var captureId = reader.GetString(2);
        var receiptVersion = reader.GetInt32(3);
        var receipt = reader.GetFieldValue<byte[]>(4);
        var locator = reader.IsDBNull(5) ? null : reader.GetString(5);
        var state = reader.GetString(6);
        var revision = reader.GetInt64(7);
        var coverage = reader.GetInt32(8);
        reader.Close();
        if (!string.Equals(store, context.StoreInstanceId, StringComparison.Ordinal)
            || context.StoreKind != RetentionStoreKind.SensitiveBundle
            || kind != "sensitive_bundle"
            || !string.Equals(captureId, context.SourceIdentity.SourceItemId, StringComparison.Ordinal)
            || !ReceiptMatches(context.SourceIdentity.OwnershipReceipt, receipt)
            || revision != context.ExpectedRevision
            || state != "deleting"
            || receiptVersion != 1 || receipt.Length != 32 || coverage != RetentionV1Constants.AdapterCoverageVersion
            || locator is null || context.PrivateLocator is null || !string.Equals(context.PrivateLocator.OpaqueHandle, locator, StringComparison.Ordinal)
            || !CurrentJournalMatches(connection, transaction, context)
            || !Owns(connection, transaction, new(context.ItemId, context.ExpectedRevision, context.LeaseOwner, context.LeaseGeneration), now))
            return new(RetentionSensitiveBundleDeletionPlanDisposition.LeaseLost, null);
        if (!CanonicalId(captureId) || !string.Equals(store, StoreId(connection, transaction), StringComparison.Ordinal))
            return new(RetentionSensitiveBundleDeletionPlanDisposition.InvalidIdentity, null);

        var row = Load(connection, transaction, captureId);
        if (row is null) return new(RetentionSensitiveBundleDeletionPlanDisposition.Missing, null);
        if (row.Phase != RetentionCapturePhase.Complete || row.MarkerSha256 is null || row.ManifestSha256 is null
            || !string.Equals(locator, row.Final, StringComparison.Ordinal) || !string.Equals(row.Store, store, StringComparison.Ordinal))
            return new(RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null);
        var expectedReceipt = RetentionOwnershipReceipt.CreateSensitiveBundle(new(row.Store, row.CaptureId, row.ReservedAt, row.Ticks, row.MarkerSha256, row.ManifestSha256, row.Token));
        if (!RetentionOwnershipReceipt.Matches(expectedReceipt, receipt)) return new(RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null);

        var marker = RetentionFileCaptureOwnershipMarker.Create(row.Store, row.CaptureId, row.ReservedAt, row.Ticks, row.Token);
        return new(RetentionSensitiveBundleDeletionPlanDisposition.Ready, new RetentionSensitiveBundleDeletionPlan(row.Final, marker, row.MarkerSha256, row.ManifestSha256, row.Members.OrderBy(static member => member.DeletionOrder).ToArray(), context.IntentCursor));
    }

    private static (RetentionSensitiveBundleDeletionPlanDisposition Disposition, byte[]? Marker, IReadOnlyList<RetentionFileCaptureMember> Members) ReadSensitiveBundleProof(Row row, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(row.Final)) return (File.Exists(row.Final) ? RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch : RetentionSensitiveBundleDeletionPlanDisposition.Missing, null, []);
            if (IsReparsePoint(row.Final)) return (RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null, []);
            var expectedMarker = RetentionFileCaptureOwnershipMarker.Create(row.Store, row.CaptureId, row.ReservedAt, row.Ticks, row.Token);
            var marker = ReadExactSmallFile(Path.Combine(row.Final, RetentionFileCaptureContracts.OwnerMarkerName), expectedMarker.Length);
            if (!CryptographicOperations.FixedTimeEquals(marker, expectedMarker) || !RetentionOwnershipReceipt.Matches(SHA256.HashData(marker), row.MarkerSha256!)) return (RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null, []);
            cancellationToken.ThrowIfCancellationRequested();
            var manifestMember = row.Members.Single(static member => member.Kind == RetentionFileCaptureMemberKind.File && member.RelativePath == "manifest.json");
            var manifest = ReadExactSmallFile(Path.Combine(row.Final, "manifest.json"), manifestMember.ByteLength!.Value);
            if (manifestMember.ByteLength != manifest.Length || !RetentionOwnershipReceipt.Matches(SHA256.HashData(manifest), row.ManifestSha256!)) return (RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null, []);
            return (RetentionSensitiveBundleDeletionPlanDisposition.Ready, marker, row.Members);
        }
        catch (FileNotFoundException) { return (RetentionSensitiveBundleDeletionPlanDisposition.Missing, null, []); }
        catch (DirectoryNotFoundException) { return (RetentionSensitiveBundleDeletionPlanDisposition.Missing, null, []); }
        catch (InvalidDataException) { return (RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null, []); }
        catch (EndOfStreamException) { return (RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch, null, []); }
        catch (UnauthorizedAccessException) { return (RetentionSensitiveBundleDeletionPlanDisposition.Busy, null, []); }
        catch (IOException) { return (RetentionSensitiveBundleDeletionPlanDisposition.Busy, null, []); }
    }

    private static byte[] ReadExactSmallFile(string path, long expectedLength)
    {
        if (expectedLength is < 0 or > 1024 * 1024) throw new InvalidDataException();
        if (!File.Exists(path))
        {
            if (Directory.Exists(path)) throw new InvalidDataException();
            throw new FileNotFoundException();
        }
        if (IsReparsePoint(path)) throw new InvalidDataException();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024, FileOptions.SequentialScan);
        if (stream.Length != expectedLength) throw new InvalidDataException();
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var count = stream.Read(bytes, offset, bytes.Length - offset);
            if (count == 0) throw new EndOfStreamException();
            offset += count;
        }
        return bytes;
    }

    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

internal enum RetentionSensitiveBundleDeletionPlanDisposition { Ready, Missing, InvalidIdentity, OwnershipMismatch, LeaseLost, Busy }

internal sealed class RetentionSensitiveBundleDeletionPlanResult
{
    internal RetentionSensitiveBundleDeletionPlanResult(RetentionSensitiveBundleDeletionPlanDisposition disposition, RetentionSensitiveBundleDeletionPlan? plan) => (Disposition, Plan) = (disposition, plan);
    internal RetentionSensitiveBundleDeletionPlanDisposition Disposition { get; }
    internal RetentionSensitiveBundleDeletionPlan? Plan { get; }
    public override string ToString() => nameof(RetentionSensitiveBundleDeletionPlanResult);
}

internal sealed class RetentionSensitiveBundleDeletionPlan
{
    private readonly byte[] markerBytes;
    private readonly byte[] markerSha256;
    private readonly byte[] manifestSha256;
    private readonly IReadOnlyList<RetentionFileCaptureMember> members;

    internal RetentionSensitiveBundleDeletionPlan(string finalChild, byte[] markerBytes, byte[] markerSha256, byte[] manifestSha256, IReadOnlyList<RetentionFileCaptureMember> members, int cursor)
    {
        FinalChild = finalChild;
        this.markerBytes = markerBytes.ToArray();
        this.markerSha256 = markerSha256.ToArray();
        this.manifestSha256 = manifestSha256.ToArray();
        this.members = members.Select(static member => member with { Sha256 = member.Sha256?.ToArray() }).ToArray();
        Cursor = cursor;
    }
    internal string FinalChild { get; }
    internal byte[] MarkerBytes => markerBytes.ToArray();
    internal byte[] MarkerSha256 => markerSha256.ToArray();
    internal byte[] ManifestSha256 => manifestSha256.ToArray();
    internal IReadOnlyList<RetentionFileCaptureMember> Members => members.Select(static member => member with { Sha256 = member.Sha256?.ToArray() }).ToArray();
    internal int Cursor { get; }
    public override string ToString() => nameof(RetentionSensitiveBundleDeletionPlan);
}

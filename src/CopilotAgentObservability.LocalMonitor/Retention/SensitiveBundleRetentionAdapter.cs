using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal enum SensitiveBundleRetentionCheckpointPhase { AfterUnlink, AfterAbsenceVerified, AfterCursorAdvanced }

internal sealed record SensitiveBundleRetentionCheckpoint(int Ordinal, SensitiveBundleRetentionCheckpointPhase Phase)
{
    public override string ToString() => nameof(SensitiveBundleRetentionCheckpoint);
}

internal sealed class SensitiveBundleRetentionAdapter : IRetentionDeletionAdapter
{
    private readonly RetentionCatalogStore catalog;
    private readonly TimeProvider time;
    private readonly Action<SensitiveBundleRetentionCheckpoint>? checkpoint;

    internal SensitiveBundleRetentionAdapter(RetentionCatalogStore catalog, TimeProvider? timeProvider = null, Action<SensitiveBundleRetentionCheckpoint>? checkpoint = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        time = timeProvider ?? TimeProvider.System;
        this.checkpoint = checkpoint;
    }

    public RetentionStoreKind StoreKind => RetentionStoreKind.SensitiveBundle;

    public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var loaded = catalog.LoadSensitiveBundleDeletionPlan(context, time.GetUtcNow());
            if (loaded.Disposition != RetentionSensitiveBundleDeletionPlanDisposition.Ready)
                return Map(loaded.Disposition);

            var plan = loaded.Plan!;
            var members = plan.Members;
            if (plan.Cursor != context.IntentCursor || plan.Cursor is < 0 or > 256 || plan.Cursor > members.Count + 1 || !MarkerIsLast(members))
                return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity);

            var cursor = plan.Cursor;
            if (cursor == members.Count + 1)
                return EntryKind(plan.FinalChild) == BundleEntryKind.Absent
                    ? RetentionAdapterResult.Deleted
                    : RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch);
            while (cursor < members.Count)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                ValidatePreflight(plan, cursor, context.CancellationToken);
                var member = members[cursor];
                if (Exists(plan.FinalChild, member.RelativePath))
                {
                    DeleteMember(plan.FinalChild, member);
                    checkpoint?.Invoke(new(member.Ordinal, SensitiveBundleRetentionCheckpointPhase.AfterUnlink));
                }

                if (Exists(plan.FinalChild, member.RelativePath)) throw new OwnershipException();
                checkpoint?.Invoke(new(member.Ordinal, SensitiveBundleRetentionCheckpointPhase.AfterAbsenceVerified));
                if (await AdvanceAsync(context, cursor, cursor + 1).ConfigureAwait(false) != RetentionMutationDisposition.Applied)
                    return RetentionAdapterResult.LeaseLost;
                checkpoint?.Invoke(new(member.Ordinal, SensitiveBundleRetentionCheckpointPhase.AfterCursorAdvanced));
                cursor++;
            }

            context.CancellationToken.ThrowIfCancellationRequested();
            ValidatePreflight(plan, cursor, context.CancellationToken);
            if (cursor == members.Count)
            {
                if (Directory.Exists(plan.FinalChild))
                {
                    Directory.Delete(plan.FinalChild, recursive: false);
                    checkpoint?.Invoke(new(members.Count, SensitiveBundleRetentionCheckpointPhase.AfterUnlink));
                }
                if (EntryExists(plan.FinalChild)) throw new OwnershipException();
                checkpoint?.Invoke(new(members.Count, SensitiveBundleRetentionCheckpointPhase.AfterAbsenceVerified));
                if (await AdvanceAsync(context, cursor, cursor + 1).ConfigureAwait(false) != RetentionMutationDisposition.Applied)
                    return RetentionAdapterResult.LeaseLost;
                checkpoint?.Invoke(new(members.Count, SensitiveBundleRetentionCheckpointPhase.AfterCursorAdvanced));
                cursor++;
            }

            return cursor == members.Count + 1 && !EntryExists(plan.FinalChild)
                ? RetentionAdapterResult.Deleted
                : RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch);
        }
        catch (OperationCanceledException) { throw; }
        catch (OwnershipException) { return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch); }
        catch (UnauthorizedAccessException) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeletePermissionDenied); }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy); }
        catch (SqliteException) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed); }
        catch (IOException) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed); }
        catch (ArgumentException) { return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity); }
    }

    private async ValueTask<RetentionMutationDisposition> AdvanceAsync(RetentionDeleteContext context, int expected, int next)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        return await catalog.TryAdvanceDeleteCursorAsync(
            new RetentionDeleteFence(context.ItemId, context.ExpectedRevision, context.LeaseOwner, context.LeaseGeneration),
            expected, next, time.GetUtcNow(), context.CancellationToken).ConfigureAwait(false);
    }

    private static RetentionAdapterResult Map(RetentionSensitiveBundleDeletionPlanDisposition disposition) => disposition switch
    {
        RetentionSensitiveBundleDeletionPlanDisposition.LeaseLost => RetentionAdapterResult.LeaseLost,
        RetentionSensitiveBundleDeletionPlanDisposition.InvalidIdentity => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity),
        RetentionSensitiveBundleDeletionPlanDisposition.OwnershipMismatch => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch),
        RetentionSensitiveBundleDeletionPlanDisposition.Missing => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.UnexpectedSourceMissing),
        RetentionSensitiveBundleDeletionPlanDisposition.Busy => RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy),
        _ => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity)
    };

    private static bool MarkerIsLast(IReadOnlyList<RetentionFileCaptureMember> members) => members.Count != 0
        && members[^1].Kind == RetentionFileCaptureMemberKind.OwnerMarker
        && members.All(static member => member.IsValid)
        && members.Select(static member => member.DeletionOrder).SequenceEqual(Enumerable.Range(0, members.Count));

    private static void ValidatePreflight(RetentionSensitiveBundleDeletionPlan plan, int cursor, CancellationToken cancellationToken)
    {
        var final = EntryKind(plan.FinalChild);
        if (final == BundleEntryKind.Absent)
        {
            if (cursor == plan.Members.Count) return;
            throw new OwnershipException();
        }
        if (final != BundleEntryKind.Directory) throw new OwnershipException();

        var expected = new HashSet<string>(plan.Members.Select(static member => member.RelativePath), StringComparer.Ordinal);
        if (expected.Count != plan.Members.Count || EnumerateActual(plan.FinalChild, cancellationToken).Any(path => !expected.Contains(path))) throw new OwnershipException();
        for (var index = 0; index < plan.Members.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var member = plan.Members[index];
            var exists = Exists(plan.FinalChild, member.RelativePath);
            if (index < cursor && exists) throw new OwnershipException();
            if (index > cursor && !exists) throw new OwnershipException();
            if (exists) ValidateMember(plan, member);
        }
    }

    private static IEnumerable<string> EnumerateActual(string finalChild, CancellationToken cancellationToken)
    {
        var pending = new Stack<(string Path, string Relative)>();
        pending.Push((finalChild, string.Empty));
        var count = 0;
        while (pending.Count != 0)
        {
            var (directory, relative) = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++count > RetentionFileCaptureContracts.MaximumMemberCount) throw new OwnershipException();
                var name = Path.GetFileName(entry);
                var child = relative.Length == 0 ? name : relative + "/" + name;
                yield return child;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new OwnershipException();
                    pending.Push((entry, child));
                }
            }
        }
    }

    private static void ValidateMember(RetentionSensitiveBundleDeletionPlan plan, RetentionFileCaptureMember member)
    {
        var path = MemberPath(plan.FinalChild, member.RelativePath);
        var attributes = File.GetAttributes(path);
        var directory = (attributes & FileAttributes.Directory) != 0;
        if ((attributes & FileAttributes.ReparsePoint) != 0 || directory != (member.Kind == RetentionFileCaptureMemberKind.Directory)) throw new OwnershipException();
        if (member.Kind == RetentionFileCaptureMemberKind.Directory) return;

        var length = new FileInfo(path).Length;
        if (member.Kind == RetentionFileCaptureMemberKind.OwnerMarker)
        {
            var marker = ReadExact(path, plan.MarkerBytes.Length);
            if (!CryptographicOperations.FixedTimeEquals(marker, plan.MarkerBytes) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(marker), plan.MarkerSha256)) throw new OwnershipException();
            return;
        }
        if (length != member.ByteLength || !CryptographicOperations.FixedTimeEquals(Hash(path), member.Sha256!)) throw new OwnershipException();
    }

    private static void DeleteMember(string finalChild, RetentionFileCaptureMember member)
    {
        var path = MemberPath(finalChild, member.RelativePath);
        if (member.Kind == RetentionFileCaptureMemberKind.Directory) Directory.Delete(path, recursive: false);
        else File.Delete(path);
    }

    private static string MemberPath(string finalChild, string relativePath)
    {
        if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(relativePath)) throw new OwnershipException();
        var root = Path.GetFullPath(finalChild).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var value = Path.GetFullPath(Path.Combine(finalChild, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!value.StartsWith(root, StringComparison.Ordinal)) throw new OwnershipException();
        return value;
    }

    private static bool Exists(string finalChild, string relativePath) => EntryKind(MemberPath(finalChild, relativePath)) != BundleEntryKind.Absent;
    private static bool EntryExists(string path) => EntryKind(path) != BundleEntryKind.Absent;
    private static BundleEntryKind EntryKind(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) return BundleEntryKind.Reparse;
            return (attributes & FileAttributes.Directory) != 0 ? BundleEntryKind.Directory : BundleEntryKind.File;
        }
        catch (FileNotFoundException) { return BundleEntryKind.Absent; }
        catch (DirectoryNotFoundException) { return BundleEntryKind.Absent; }
    }
    private static byte[] ReadExact(string path, int length) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024, FileOptions.SequentialScan); if (stream.Length != length) throw new OwnershipException(); var value = new byte[length]; stream.ReadExactly(value); return value; }
    private static byte[] Hash(string path) { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan); return SHA256.HashData(stream); }
    private sealed class OwnershipException : Exception;
}

internal enum BundleEntryKind { Absent, File, Directory, Reparse }

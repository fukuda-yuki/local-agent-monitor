using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Retention;

internal sealed class AnalysisSdkDirectoryRetentionAdapter : IRetentionDeletionAdapter
{
    private readonly RetentionCatalogStore catalog;
    private readonly TimeProvider time;
    internal AnalysisSdkDirectoryRetentionAdapter(RetentionCatalogStore catalog, TimeProvider? timeProvider = null) => (this.catalog, time) = (catalog ?? throw new ArgumentNullException(nameof(catalog)), timeProvider ?? TimeProvider.System);
    public RetentionStoreKind StoreKind => RetentionStoreKind.AnalysisSdkDirectory;

    public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            var loaded = catalog.LoadAnalysisSdkDirectoryDeletionPlan(context, time.GetUtcNow());
            if (loaded.Disposition != RetentionAnalysisSdkDirectoryDeletionPlanDisposition.Ready) return Map(loaded.Disposition);
            var plan = loaded.Plan!; var cursor = plan.Cursor;
            if (!PlanIsValid(plan)) return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity);
            if (cursor == plan.Members.Count + 1)
                return Kind(plan.Child) == Entry.Absent ? RetentionAdapterResult.Deleted : RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch);
            while (cursor < plan.Members.Count)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                if (await Renew(context).ConfigureAwait(false) != RetentionRenewalResult.Renewed) return RetentionAdapterResult.LeaseLost;
                ValidateBoundary(plan, cursor);
                var member = plan.Members[cursor]; var path = MemberPath(plan.Child, member.RelativePath);
                if (Kind(path) != Entry.Absent)
                {
                    ValidateMember(plan, member);
                    Delete(path, member);
                }
                // The immutable snapshot and delete intent are one transaction, so this
                // absence is only the unlink/CAS crash window for the current member.
                if (Kind(path) != Entry.Absent) return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch);
                if (await Advance(context, cursor, cursor + 1).ConfigureAwait(false) != RetentionMutationDisposition.Applied) return RetentionAdapterResult.LeaseLost;
                cursor++;
            }
            if (await Renew(context).ConfigureAwait(false) != RetentionRenewalResult.Renewed) return RetentionAdapterResult.LeaseLost;
            if (Kind(plan.Child) == Entry.Directory) Directory.Delete(plan.Child, recursive: false);
            if (Kind(plan.Child) != Entry.Absent) return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch);
            if (await Advance(context, cursor, cursor + 1).ConfigureAwait(false) != RetentionMutationDisposition.Applied) return RetentionAdapterResult.LeaseLost;
            return RetentionAdapterResult.Deleted;
        }
        catch (OperationCanceledException) { throw; }
        catch (SdkMismatchException) { return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch); }
        catch (UnauthorizedAccessException) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeletePermissionDenied); }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy); }
        catch (SqliteException) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed); }
        catch (IOException) { return RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteIoFailed); }
        catch (ArgumentException) { return RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity); }
    }

    private async ValueTask<RetentionRenewalResult> Renew(RetentionDeleteContext context) => await catalog.TryRenewDeletionLeaseAsync(new(context.ItemId, context.ExpectedRevision, context.LeaseOwner, context.LeaseGeneration), time.GetUtcNow(), null, context.CancellationToken).ConfigureAwait(false);
    private async ValueTask<RetentionMutationDisposition> Advance(RetentionDeleteContext context, int expected, int next) => await catalog.TryAdvanceDeleteCursorAsync(new(context.ItemId, context.ExpectedRevision, context.LeaseOwner, context.LeaseGeneration), expected, next, time.GetUtcNow(), context.CancellationToken).ConfigureAwait(false);
    private static RetentionAdapterResult Map(RetentionAnalysisSdkDirectoryDeletionPlanDisposition value) => value switch { RetentionAnalysisSdkDirectoryDeletionPlanDisposition.LeaseLost => RetentionAdapterResult.LeaseLost, RetentionAnalysisSdkDirectoryDeletionPlanDisposition.Missing => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.UnexpectedSourceMissing), RetentionAnalysisSdkDirectoryDeletionPlanDisposition.Busy => RetentionAdapterResult.TransientFailure(RetentionErrorCode.DeleteBusy), RetentionAnalysisSdkDirectoryDeletionPlanDisposition.InvalidIdentity => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.InvalidIdentity), _ => RetentionAdapterResult.TerminalFailure(RetentionErrorCode.OwnershipMismatch) };
    private static bool PlanIsValid(RetentionAnalysisSdkDirectoryDeletionPlan plan) => plan.Cursor is >= 0
        && plan.Cursor <= plan.Members.Count + 1
        && plan.Members.Count > 0 && plan.Members.Count <= RetentionFileCaptureContracts.MaximumMemberCount
        && plan.Members.All(static member => member.IsValid)
        && plan.Members.Select(static member => member.DeletionOrder).SequenceEqual(Enumerable.Range(0, plan.Members.Count))
        && plan.Members[^1].Kind == RetentionFileCaptureMemberKind.OwnerMarker;

    private static void ValidateBoundary(RetentionAnalysisSdkDirectoryDeletionPlan plan, int cursor)
    {
        if (Kind(plan.Child) == Entry.Absent)
        {
            if (cursor != plan.Members.Count) throw new SdkMismatchException();
            return;
        }
        if (Kind(plan.Child) != Entry.Directory) throw new SdkMismatchException();
        var actual = EnumerateActual(plan.Child);
        var expected = plan.Members.Skip(cursor).ToDictionary(static member => member.RelativePath, StringComparer.Ordinal);
        if (cursor < plan.Members.Count && !actual.Contains(plan.Members[cursor].RelativePath))
            expected.Remove(plan.Members[cursor].RelativePath);
        if (actual.Count != expected.Count || actual.Any(path => !expected.ContainsKey(path))) throw new SdkMismatchException();
        foreach (var member in expected.Values) ValidateMember(plan, member);
    }

    private static HashSet<string> EnumerateActual(string root)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal); var pending = new Stack<(string Path, string Relative)>(); pending.Push((root, string.Empty));
        while (pending.Count != 0)
        {
            var (directory, relative) = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var relativePath = relative.Length == 0 ? Path.GetFileName(path) : relative + "/" + Path.GetFileName(path);
                if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(relativePath) || !entries.Add(relativePath) || entries.Count > RetentionFileCaptureContracts.MaximumMemberCount) throw new SdkMismatchException();
                var kind = Kind(path); if (kind == Entry.Reparse || kind == Entry.Absent) throw new SdkMismatchException();
                if (kind == Entry.Directory) pending.Push((path, relativePath));
            }
        }
        return entries;
    }
    private static void ValidateMember(RetentionAnalysisSdkDirectoryDeletionPlan plan, RetentionFileCaptureMember member)
    {
        var path = MemberPath(plan.Child, member.RelativePath); var kind = Kind(path);
        if ((member.Kind == RetentionFileCaptureMemberKind.Directory && kind != Entry.Directory) || (member.Kind != RetentionFileCaptureMemberKind.Directory && kind != Entry.File)) throw new SdkMismatchException();
        if (member.Kind == RetentionFileCaptureMemberKind.Directory) return;
        var bytes = Read(path, member.Kind == RetentionFileCaptureMemberKind.OwnerMarker ? plan.MarkerBytes.Length : member.ByteLength!.Value);
        if (member.Kind == RetentionFileCaptureMemberKind.OwnerMarker)
        {
            if (!CryptographicOperations.FixedTimeEquals(bytes, plan.MarkerBytes) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), plan.MarkerSha256)) throw new SdkMismatchException();
        }
        else if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), member.Sha256!)) throw new SdkMismatchException();
    }
    private static byte[] Read(string path, long length) { if (length is < 0 or > 134217728) throw new SdkMismatchException(); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan); if (stream.Length != length) throw new SdkMismatchException(); var bytes = new byte[length]; stream.ReadExactly(bytes); if (stream.Length != length || Kind(path) != Entry.File) throw new SdkMismatchException(); return bytes; }
    private static void Delete(string path, RetentionFileCaptureMember member) { if (member.Kind == RetentionFileCaptureMemberKind.Directory) Directory.Delete(path, recursive: false); else File.Delete(path); }
    private static string MemberPath(string child, string relative) { if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(relative)) throw new SdkMismatchException(); var root = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; var value = Path.GetFullPath(Path.Combine(child, relative.Replace('/', Path.DirectorySeparatorChar))); if (!value.StartsWith(root, StringComparison.Ordinal)) throw new SdkMismatchException(); return value; }
    private static Entry Kind(string path) { try { var a = File.GetAttributes(path); return (a & FileAttributes.ReparsePoint) != 0 ? Entry.Reparse : (a & FileAttributes.Directory) != 0 ? Entry.Directory : Entry.File; } catch (FileNotFoundException) { return Entry.Absent; } catch (DirectoryNotFoundException) { return Entry.Absent; } }
    private sealed class SdkMismatchException : Exception;
    private enum Entry { Absent, File, Directory, Reparse }
}

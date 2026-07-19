using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class AnalysisSdkDirectoryOwner : IAnalysisSdkDirectoryOwner
{
    internal const string MarkerFileName = ".copilot-agent-observability-owner";
    private readonly RetentionCatalogStore catalog;
    private readonly TimeProvider timeProvider;
    private readonly Action? reservationCheckpoint;
    private readonly Action<string>? childCreatedCheckpoint;

    internal AnalysisSdkDirectoryOwner(RetentionCatalogStore catalog, TimeProvider timeProvider, Action? reservationCheckpoint = null, Action<string>? childCreatedCheckpoint = null)
    {
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.reservationCheckpoint = reservationCheckpoint;
        this.childCreatedCheckpoint = childCreatedCheckpoint;
    }

    public ValueTask<IAnalysisSdkDirectoryScope> OpenAsync(long runId, DateTimeOffset exactRequestedAt, string configuredParent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RetentionAnalysisSdkDirectoryReservation? reservation = null;
        RetentionAnalysisSdkDirectoryOperationLease? activatedLease = null;
        var createdChild = false;
        var recoveredMarkerOnlyChild = false;
        try
        {
            reservation = catalog.ReserveAnalysisSdkDirectory(runId, configuredParent);
            if (!MatchesRequestedAt(reservation, exactRequestedAt)) throw new AnalysisOwnershipException();
            if (reservation.Phase != RetentionAnalysisSdkDirectoryPhase.Reserved) throw new AnalysisOwnershipException();
            reservationCheckpoint?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            EnsureParent(reservation.ParentLocator);

            var childExists = Directory.Exists(reservation.ChildLocator);
            if (!childExists)
            {
                if (!NativeDirectory.Create(reservation.ChildLocator)) throw new AnalysisOwnershipException();
                createdChild = true;
                childCreatedCheckpoint?.Invoke(reservation.ChildLocator);
            }

            if (IsReparsePoint(reservation.ChildLocator)) throw new AnalysisOwnershipException();
            if (createdChild) WriteMarker(reservation.ChildLocator, reservation.OwnershipMarker, reservation.MarkerSha256);
            else if (IsMarkerOnly(reservation.ChildLocator, reservation.OwnershipMarker, allowAbsent: false)) recoveredMarkerOnlyChild = true;
            else throw new AnalysisOwnershipException();

            if (!IsMarkerOnly(reservation.ChildLocator, reservation.OwnershipMarker, allowAbsent: false)) throw new AnalysisOwnershipException();
            var markerProvenOwnedEmptyChild = createdChild || IsMarkerOnly(reservation.ChildLocator, reservation.OwnershipMarker, allowAbsent: false);
            var activation = catalog.ActivateAnalysisSdkDirectoryAndAcquireOperationLease(reservation, reservation.OwnershipMarker, markerProvenOwnedEmptyChild, timeProvider.GetUtcNow());
            if (!activation.IsActive) throw new AnalysisOwnershipException();
            activatedLease = activation.Lease!;
            return ValueTask.FromResult<IAnalysisSdkDirectoryScope>(new Scope(catalog, activatedLease, reservation.ChildLocator, timeProvider));
        }
        catch (OperationCanceledException)
        {
            CleanupOrReleaseActiveLease(reservation, activatedLease, createdChild, recoveredMarkerOnlyChild);
            throw;
        }
        catch (AnalysisOwnershipException)
        {
            CleanupOrReleaseActiveLease(reservation, activatedLease, createdChild, recoveredMarkerOnlyChild);
            throw;
        }
        catch
        {
            CleanupOrReleaseActiveLease(reservation, activatedLease, createdChild, recoveredMarkerOnlyChild);
            throw new AnalysisOwnershipException();
        }
    }

    private void CleanupOrReleaseActiveLease(RetentionAnalysisSdkDirectoryReservation? reservation, RetentionAnalysisSdkDirectoryOperationLease? activatedLease, bool createdChild, bool recoveredMarkerOnlyChild)
    {
        if (activatedLease is not null)
        {
            _ = catalog.ReleaseAnalysisSdkDirectoryOperationLease(activatedLease);
            return;
        }
        CleanupAndAbandon(reservation, createdChild, recoveredMarkerOnlyChild);
    }

    private void CleanupAndAbandon(RetentionAnalysisSdkDirectoryReservation? reservation, bool createdChild, bool recoveredMarkerOnlyChild)
    {
        if (reservation is null) return;
        if (!createdChild && !recoveredMarkerOnlyChild)
        {
            _ = catalog.AbandonReservedAnalysisSdkDirectory(reservation);
            return;
        }
        if (!DeleteOwnedChild(reservation, createdChild)) return;
        _ = catalog.AbandonReservedAnalysisSdkDirectory(reservation);
    }

    private static bool DeleteOwnedChild(RetentionAnalysisSdkDirectoryReservation reservation, bool createdChild)
    {
        try
        {
            if (IsMarkerOnly(reservation.ChildLocator, reservation.OwnershipMarker, allowAbsent: false))
                File.Delete(Path.Combine(reservation.ChildLocator, MarkerFileName));
            else if (!createdChild || !IsEmpty(reservation.ChildLocator)) return false;
            if (!IsEmpty(reservation.ChildLocator)) return false;
            Directory.Delete(reservation.ChildLocator, recursive: false);
            return !Directory.Exists(reservation.ChildLocator);
        }
        catch { return false; }
    }

    private static bool MatchesRequestedAt(RetentionAnalysisSdkDirectoryReservation reservation, DateTimeOffset requestedAt) =>
        requestedAt.Offset == TimeSpan.Zero
        && string.Equals(reservation.RequestedAtText, requestedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
        && reservation.RequestedAtUtcTicks == requestedAt.UtcDateTime.Ticks;

    private static void EnsureParent(string parent)
    {
        var full = Path.GetFullPath(parent);
        if (!string.Equals(full, parent, StringComparison.Ordinal)) throw new AnalysisOwnershipException();
        var current = parent;
        while (!Directory.Exists(current))
        {
            var next = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.Ordinal)) break;
            current = next;
        }
        for (var candidate = current; !string.IsNullOrEmpty(candidate); candidate = Path.GetDirectoryName(candidate))
        {
            if (Directory.Exists(candidate) && IsReparsePoint(candidate)) throw new AnalysisOwnershipException();
            var next = Path.GetDirectoryName(candidate);
            if (string.Equals(next, candidate, StringComparison.Ordinal)) break;
        }
        Directory.CreateDirectory(parent);
        for (var candidate = parent; !string.IsNullOrEmpty(candidate); candidate = Path.GetDirectoryName(candidate))
        {
            if (!Directory.Exists(candidate) || IsReparsePoint(candidate)) throw new AnalysisOwnershipException();
            var next = Path.GetDirectoryName(candidate);
            if (string.Equals(next, candidate, StringComparison.Ordinal)) break;
        }
    }

    private static void WriteMarker(string child, byte[] marker, byte[] digest)
    {
        if (IsReparsePoint(child)) throw new AnalysisOwnershipException();
        var path = Path.Combine(child, MarkerFileName);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(marker);
            stream.Flush(flushToDisk: true);
        }
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(marker) || !SHA256.HashData(marker).AsSpan().SequenceEqual(digest)) throw new AnalysisOwnershipException();
    }

    private static bool IsMarkerOnly(string child, byte[] marker, bool allowAbsent)
    {
        try
        {
            if (!Directory.Exists(child) || IsReparsePoint(child)) return false;
            var entries = Directory.EnumerateFileSystemEntries(child).ToArray();
            if (entries.Length == 0) return allowAbsent;
            if (entries.Length != 1 || !string.Equals(Path.GetFileName(entries[0]), MarkerFileName, StringComparison.Ordinal) || IsReparsePoint(entries[0])) return false;
            return File.ReadAllBytes(entries[0]).AsSpan().SequenceEqual(marker);
        }
        catch { return false; }
    }

    private static bool IsEmpty(string child)
    {
        try { return Directory.Exists(child) && !IsReparsePoint(child) && !Directory.EnumerateFileSystemEntries(child).Any(); }
        catch { return false; }
    }

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private sealed class Scope : IAnalysisSdkDirectoryScope
    {
        private readonly RetentionCatalogStore catalog;
        private readonly RetentionAnalysisSdkDirectoryOperationLease lease;
        private readonly TimeProvider timeProvider;
        private readonly CancellationTokenSource leaseLost = new();
        private readonly ITimer timer;
        private int disposed;
        private int lost;

        internal Scope(RetentionCatalogStore catalog, RetentionAnalysisSdkDirectoryOperationLease lease, string childDirectory, TimeProvider timeProvider)
        {
            this.catalog = catalog;
            this.lease = lease;
            this.timeProvider = timeProvider;
            ChildDirectory = childDirectory;
            var interval = TimeSpan.FromTicks(RetentionV1Constants.LeaseDuration.Ticks / 2);
            timer = timeProvider.CreateTimer(static state => ((Scope)state!).Renew(), this, interval, interval);
        }

        public string ChildDirectory { get; }
        public CancellationToken LeaseLostToken => leaseLost.Token;
        public bool IsLeaseLost => Volatile.Read(ref lost) != 0;

        private void Renew()
        {
            if (Volatile.Read(ref disposed) != 0 || IsLeaseLost) return;
            RetentionRenewalResult result;
            try { result = catalog.RenewAnalysisSdkDirectoryOperationLease(lease, timeProvider.GetUtcNow()); }
            catch { result = RetentionRenewalResult.LeaseLost; }
            if (result == RetentionRenewalResult.LeaseLost)
            {
                Interlocked.Exchange(ref lost, 1);
                leaseLost.Cancel();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            var failures = new List<Exception>();
            try { await timer.DisposeAsync(); }
            catch (Exception exception) { failures.Add(exception); }
            try
            {
                if (catalog.ReleaseAnalysisSdkDirectoryOperationLease(lease) != RetentionMutationDisposition.Applied)
                    failures.Add(new AnalysisOwnershipException());
            }
            catch (Exception exception) { failures.Add(exception); }
            try { leaseLost.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            if (failures.Count == 1 && failures[0] is AnalysisOwnershipException ownership) throw ownership;
            if (failures.Count == 1) throw new AnalysisOwnershipException(failures[0]);
            if (failures.Count > 1) throw new AnalysisOwnershipException(new AggregateException(failures));
        }
    }

    private static class NativeDirectory
    {
        internal static bool Create(string path)
        {
            if (OperatingSystem.IsWindows()) return CreateDirectoryW(path, IntPtr.Zero);
            return mkdir(path, 0x1ED) == 0;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

        [DllImport("libc", SetLastError = true)]
        private static extern int mkdir(string path, uint mode);
    }
}

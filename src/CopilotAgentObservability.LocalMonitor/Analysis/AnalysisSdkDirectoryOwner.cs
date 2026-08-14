using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal sealed class AnalysisSdkDirectoryOwner : IAnalysisSdkDirectoryOwner
{
    internal const string MarkerFileName = RetentionAnalysisSdkDirectoryOwnershipMarker.FileName;
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
            var activation = catalog.PrepareAndActivateAnalysisSdkDirectoryAndAcquireOperationLease(
                reservation,
                () =>
                {
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

                    if (!IsMarkerOnly(reservation.ChildLocator, reservation.OwnershipMarker, allowAbsent: false))
                        throw new AnalysisOwnershipException();
                });
            if (!activation.IsActive) throw new AnalysisOwnershipException();
            activatedLease = activation.Lease!;
            var initialObservation = catalog.RenewAndObserveAnalysisSdkDirectoryOperationLease(activatedLease);
            if (initialObservation.Disposition == RetentionOperationRenewalDisposition.LeaseLost
                || initialObservation.PublishedLeaseExpiresAt - initialObservation.ObservedAt <= RetentionV1Constants.LeaseRenewalDeadline)
                throw new AnalysisOwnershipException();
            return ValueTask.FromResult<IAnalysisSdkDirectoryScope>(new Scope(
                catalog,
                activatedLease,
                reservation.ChildLocator,
                timeProvider,
                initialObservation));
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
        _ = catalog.CleanupAndAbandonReservedAnalysisSdkDirectory(
            reservation,
            allowMarkerlessEmptyChild: createdChild);
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

    private static bool IsReparsePoint(string path)
    {
        try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
    }

    private sealed class Scope : IAnalysisSdkDirectoryScope
    {
        private readonly RetentionCatalogStore catalog;
        private readonly RetentionAnalysisSdkDirectoryOperationLease lease;
        private readonly CancellationTokenSource leaseLost = new();
        private readonly ITimer timer;
        private int disposed;
        private int lost;

        internal Scope(
            RetentionCatalogStore catalog,
            RetentionAnalysisSdkDirectoryOperationLease lease,
            string childDirectory,
            TimeProvider timeProvider,
            RetentionAnalysisSdkDirectoryLeaseObservation initialObservation)
        {
            this.catalog = catalog;
            this.lease = lease;
            ChildDirectory = childDirectory;
            timer = timeProvider.CreateTimer(
                static state => ((Scope)state!).Renew(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            Schedule(NextDue(initialObservation));
        }

        public string ChildDirectory { get; }
        public CancellationToken LeaseLostToken => leaseLost.Token;
        public bool IsLeaseLost => Volatile.Read(ref lost) != 0;

        private void Renew()
        {
            if (Volatile.Read(ref disposed) != 0 || IsLeaseLost) return;
            RetentionAnalysisSdkDirectoryLeaseObservation observation;
            try { observation = catalog.RenewAndObserveAnalysisSdkDirectoryOperationLease(lease); }
            catch
            {
                LoseLease();
                return;
            }

            if (observation.Disposition == RetentionOperationRenewalDisposition.LeaseLost
                || observation.ObservedAt >= observation.PublishedLeaseExpiresAt)
            {
                LoseLease();
                return;
            }

            Schedule(NextDue(observation));
        }

        private static TimeSpan NextDue(RetentionAnalysisSdkDirectoryLeaseObservation observation)
        {
            var remaining = observation.PublishedLeaseExpiresAt - observation.ObservedAt;
            if (observation.Disposition == RetentionOperationRenewalDisposition.NonrenewableGrantStillUsable
                || observation.Disposition == RetentionOperationRenewalDisposition.CatalogBusy
                    && remaining <= RetentionV1Constants.LeaseRenewalDeadline)
                return remaining;
            return remaining - RetentionV1Constants.LeaseRenewalDeadline;
        }

        private void LoseLease()
        {
            Interlocked.Exchange(ref lost, 1);
            leaseLost.Cancel();
        }

        private void Schedule(TimeSpan dueTime)
        {
            if (Volatile.Read(ref disposed) != 0 || IsLeaseLost) return;
            if (dueTime < TimeSpan.Zero) dueTime = TimeSpan.Zero;
            try { _ = timer.Change(dueTime, Timeout.InfiniteTimeSpan); }
            catch (ObjectDisposedException) when (Volatile.Read(ref disposed) != 0) { }
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

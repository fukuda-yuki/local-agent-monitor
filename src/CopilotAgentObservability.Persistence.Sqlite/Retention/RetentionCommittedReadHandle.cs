namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RetentionCommittedReadHandle : IAsyncDisposable
{
    private const int Hidden = 0;
    private const int Published = 1;
    private const int Lost = 2;
    private const int Released = 3;
    private static readonly TimeSpan PublicationRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly TimeProvider timeProvider;
    private readonly RetentionGrantPublicationMember[] publicationMembers;
    private readonly RetentionMandatoryLeaseCleanup cleanup;
    private RetentionExpiryNotification currentNotification;
    private int state;

    internal RetentionCommittedReadHandle(
        IReadOnlyList<RetentionReadGrant> grants,
        TimeProvider timeProvider,
        Func<IReadOnlyList<RetentionReadGrant>, bool> exactRelease)
    {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(exactRelease);
        if (grants.Count == 0) throw new ArgumentException("A committed handle requires at least one grant.", nameof(grants));
        var snapshot = grants.ToArray();
        var expiresAt = snapshot[0].LeaseExpiresAt;
        if (snapshot.Any(grant => grant.LeaseExpiresAt != expiresAt))
            throw new ArgumentException("A composite handle requires one common expiry.", nameof(grants));

        Grants = Array.AsReadOnly(snapshot);
        this.timeProvider = timeProvider;
        RetentionExpiryNotification? preparedExpiry = null;
        try
        {
            publicationMembers = Grants
                .Select((grant, index) => new RetentionGrantPublicationMember(grant, index))
                .ToArray();
            foreach (var grant in Grants) grant.AttachCommittedHandle(this);
            preparedExpiry = new RetentionExpiryNotification(this, 1, expiresAt, timeProvider, armDormant: false);
            cleanup = new RetentionMandatoryLeaseCleanup(Grants, timeProvider, exactRelease);
            currentNotification = preparedExpiry;
        }
        catch
        {
            preparedExpiry?.Invalidate();
            throw;
        }
    }

    internal IReadOnlyList<RetentionReadGrant> Grants { get; }
    internal bool IsPublished => Volatile.Read(ref state) == Published;

    internal bool Activate()
    {
        return currentNotification.Activate()
            && Volatile.Read(ref state) == Hidden;
    }

    internal bool Publish() =>
        Interlocked.CompareExchange(ref state, Published, Hidden) == Hidden;

    internal void AbandonBeforeCommit()
    {
        if (Interlocked.CompareExchange(ref state, Released, Hidden) != Hidden) return;
        currentNotification.Invalidate();
        cleanup.Abandon();
    }

    internal void Lose()
        => Lose(releaseSynchronously: true);

    internal void LoseAsynchronously()
        => Lose(releaseSynchronously: false);

    private void Lose(bool releaseSynchronously)
    {
        while (true)
        {
            var observed = Volatile.Read(ref state);
            if (observed == Lost)
            {
                if (releaseSynchronously) cleanup.ReleaseOrOwn();
                return;
            }
            if (observed == Released) return;
            if (Interlocked.CompareExchange(ref state, Lost, observed) != observed) continue;
            Volatile.Read(ref currentNotification).Invalidate();
            if (releaseSynchronously)
                cleanup.ReleaseOrOwn();
            else
                cleanup.Own();
            return;
        }
    }

    public ValueTask DisposeAsync()
    {
        while (true)
        {
            var observed = Volatile.Read(ref state);
            if (observed == Lost)
            {
                cleanup.ReleaseOrOwn();
                return ValueTask.CompletedTask;
            }
            if (observed == Released) return ValueTask.CompletedTask;
            if (Interlocked.CompareExchange(ref state, Released, observed) != observed) continue;
            Volatile.Read(ref currentNotification).Invalidate();
            cleanup.ReleaseOrOwn();
            return ValueTask.CompletedTask;
        }
    }

    public override string ToString() => nameof(RetentionCommittedReadHandle);

    internal RetentionExpiryNotificationPreparation PrepareExpiryNotification(DateTimeOffset expiresAt)
    {
        var current = Volatile.Read(ref currentNotification);
        var generation = checked(current.Generation + 1);
        var replacement = new RetentionExpiryNotification(
            this,
            generation,
            expiresAt,
            timeProvider,
            armDormant: true);
        return new RetentionExpiryNotificationPreparation(this, current, replacement);
    }

    internal bool PublishExpiryNotification(
        RetentionExpiryNotification expected,
        RetentionExpiryNotification replacement)
    {
        if (!ReferenceEquals(
                Interlocked.CompareExchange(ref currentNotification, replacement, expected),
                expected))
        {
            replacement.Invalidate();
            Lose(releaseSynchronously: false);
            return false;
        }

        var activated = replacement.Activate();
        expected.Invalidate();
        if (!activated) Lose(releaseSynchronously: false);
        return activated;
    }

    internal void ExpiryDue(RetentionExpiryNotification notification)
    {
        try
        {
            var current = Volatile.Read(ref currentNotification);
            if (!ReferenceEquals(current, notification)
                || current.Generation != notification.Generation)
                return;
            if (Volatile.Read(ref state) is Lost or Released) return;

            if (!RetentionGrantPublicationSet.TryEnterInOrder(publicationMembers, out var publications))
            {
                if (ReferenceEquals(Volatile.Read(ref currentNotification), notification))
                    notification.Rearm(PublicationRetryDelay);
                return;
            }

            TimeSpan nextDue;
            using (publications)
            {
                current = Volatile.Read(ref currentNotification);
                if (!ReferenceEquals(current, notification)
                    || current.Generation != notification.Generation)
                    return;
                var now = timeProvider.GetUtcNow();
                var earliestExpiry = publications.LeaseExpiresAt(0);
                for (var index = 1; index < publications.Count; index++)
                    if (publications.LeaseExpiresAt(index) < earliestExpiry)
                        earliestExpiry = publications.LeaseExpiresAt(index);

                if (now >= earliestExpiry)
                {
                    Lose(releaseSynchronously: false);
                    return;
                }

                nextDue = earliestExpiry - now;
            }

            notification.Rearm(nextDue);
        }
        catch
        {
            if (ReferenceEquals(Volatile.Read(ref currentNotification), notification))
                Lose(releaseSynchronously: false);
        }
    }
}

internal sealed class RetentionExpiryNotification
{
    private const int Dormant = 0;
    private const int Active = 1;
    private const int Invalid = 2;
    private readonly RetentionCommittedReadHandle handle;
    private readonly DateTimeOffset expiresAt;
    private readonly TimeProvider timeProvider;
    private readonly ITimer timer;
    private int state;
    private int armed;
    private int due;

    internal RetentionExpiryNotification(
        RetentionCommittedReadHandle handle,
        long generation,
        DateTimeOffset expiresAt,
        TimeProvider timeProvider,
        bool armDormant)
    {
        this.handle = handle;
        Generation = generation;
        this.expiresAt = expiresAt;
        this.timeProvider = timeProvider;
        timer = timeProvider.CreateTimer(
            static state => ((RetentionExpiryNotification)state!).Due(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
        if (!armDormant) return;
        try
        {
            if (!Arm()) throw new InvalidOperationException("The expiry notification could not be armed.");
        }
        catch
        {
            Invalidate();
            throw;
        }
    }

    internal long Generation { get; }

    internal bool Activate()
    {
        try
        {
            var wasArmed = Volatile.Read(ref armed) != 0;
            if (!wasArmed && !Arm()) return false;
            if (Interlocked.CompareExchange(ref state, Active, Dormant) != Dormant) return false;
            return Volatile.Read(ref due) == 0
                && (!wasArmed || timeProvider.GetUtcNow() < expiresAt);
        }
        catch { return false; }
    }

    internal void Rearm(TimeSpan delay)
    {
        if (Volatile.Read(ref state) != Active) return;
        try
        {
            if (!timer.Change(delay, Timeout.InfiniteTimeSpan))
                handle.LoseAsynchronously();
        }
        catch { handle.LoseAsynchronously(); }
    }

    internal void Invalidate()
    {
        if (Interlocked.Exchange(ref state, Invalid) == Invalid) return;
        try { timer.Dispose(); }
        catch { }
    }

    private bool Arm()
    {
        var delay = expiresAt - timeProvider.GetUtcNow();
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (!timer.Change(delay, Timeout.InfiniteTimeSpan)) return false;
        Volatile.Write(ref armed, 1);
        return true;
    }

    private void Due()
    {
        try
        {
            if (Volatile.Read(ref state) == Dormant)
            {
                Volatile.Write(ref due, 1);
                return;
            }
            if (Volatile.Read(ref state) == Active) handle.ExpiryDue(this);
        }
        catch { handle.LoseAsynchronously(); }
    }
}

internal sealed class RetentionExpiryNotificationPreparation : IDisposable
{
    private readonly RetentionCommittedReadHandle handle;
    private readonly RetentionExpiryNotification expected;
    private readonly RetentionExpiryNotification replacement;
    private int completed;

    internal RetentionExpiryNotificationPreparation(
        RetentionCommittedReadHandle handle,
        RetentionExpiryNotification expected,
        RetentionExpiryNotification replacement) =>
        (this.handle, this.expected, this.replacement) = (handle, expected, replacement);

    internal bool Publish()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return false;
        return handle.PublishExpiryNotification(expected, replacement);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0) replacement.Invalidate();
    }
}

internal sealed class RetentionExpiryNotificationRenewal : IDisposable
{
    private readonly RetentionGrantPublicationSet publications;
    private readonly IReadOnlyList<int> renewedIndices;
    private readonly DateTimeOffset expiry;
    private readonly IReadOnlyList<RetentionExpiryNotificationPreparation> preparations;
    private int completed;

    internal RetentionExpiryNotificationRenewal(
        RetentionGrantPublicationSet publications,
        IReadOnlyList<int> renewedIndices,
        DateTimeOffset expiry,
        IReadOnlyList<RetentionExpiryNotificationPreparation> preparations) =>
        (this.publications, this.renewedIndices, this.expiry, this.preparations) =
        (publications, renewedIndices, expiry, preparations);

    internal bool Publish()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return false;
        foreach (var index in renewedIndices) publications.AdvanceExpiry(index, expiry);
        var active = true;
        foreach (var preparation in preparations) active &= preparation.Publish();
        return active;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        foreach (var preparation in preparations) preparation.Dispose();
    }
}

internal sealed class RetentionMandatoryLeaseCleanup
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(10);
    private readonly IReadOnlyList<RetentionReadGrant> grants;
    private readonly Func<IReadOnlyList<RetentionReadGrant>, bool> exactRelease;
    private readonly ITimer retry;
    private int active;
    private int completed;

    internal RetentionMandatoryLeaseCleanup(
        IReadOnlyList<RetentionReadGrant> grants,
        TimeProvider timeProvider,
        Func<IReadOnlyList<RetentionReadGrant>, bool> exactRelease)
    {
        this.grants = grants;
        this.exactRelease = exactRelease;
        retry = timeProvider.CreateTimer(
            static state => ((RetentionMandatoryLeaseCleanup)state!).SignalRelease(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal void ReleaseOrOwn()
    {
        if (Volatile.Read(ref completed) != 0
            || Interlocked.CompareExchange(ref active, 1, 0) != 0)
            return;
        TryRelease(retryOnFailure: true);
    }

    internal void Own() => SignalRelease();

    internal void Abandon()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
            retry.Dispose();
    }

    public override string ToString() => nameof(RetentionMandatoryLeaseCleanup);

    private void SignalRelease()
    {
        if (TryQueueRelease()) return;
        ScheduleRetry();
    }

    private bool TryQueueRelease()
    {
        try
        {
            if (Volatile.Read(ref completed) != 0
                || Interlocked.CompareExchange(ref active, 1, 0) != 0)
                return true;
            if (!ThreadPool.UnsafeQueueUserWorkItem(
                    static cleanup => cleanup.TryRelease(retryOnFailure: true),
                    this,
                    preferLocal: false))
            {
                Volatile.Write(ref active, 0);
                return false;
            }
            return true;
        }
        catch
        {
            Volatile.Write(ref active, 0);
            return false;
        }
    }

    private void TryRelease(bool retryOnFailure)
    {
        var released = false;
        try
        {
            released = exactRelease(grants);
        }
        // Cleanup must not propagate release failures; the scheduled retry owns recovery.
        catch { }
        finally
        {
            Volatile.Write(ref active, 0);
        }

        if (released)
        {
            Complete();
            return;
        }
        if (retryOnFailure) ScheduleRetry();
    }

    private void ScheduleRetry()
    {
        while (Volatile.Read(ref completed) == 0)
        {
            try
            {
                if (retry.Change(RetryDelay, Timeout.InfiniteTimeSpan)) return;
            }
            catch { }
            if (TryQueueRelease()) return;
            Thread.Yield();
        }
    }

    private void Complete()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        try { retry.Dispose(); }
        catch { }
    }
}

internal sealed record RetentionReadAdmissionResult(
    RetentionReadDisposition? Disposition,
    RetentionCommittedReadHandle? Handle)
{
    internal static RetentionReadAdmissionResult Granted(RetentionCommittedReadHandle handle) =>
        new(null, handle ?? throw new ArgumentNullException(nameof(handle)));

    internal static RetentionReadAdmissionResult Failed(RetentionReadDisposition disposition) =>
        new(disposition, null);
}

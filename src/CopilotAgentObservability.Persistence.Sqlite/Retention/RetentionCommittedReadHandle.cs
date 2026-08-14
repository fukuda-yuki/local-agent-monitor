namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed class RetentionCommittedReadHandle : IAsyncDisposable
{
    private const int Hidden = 0;
    private const int Published = 1;
    private const int Lost = 2;
    private const int Released = 3;

    private readonly TimeProvider timeProvider;
    private readonly DateTimeOffset expiresAt;
    private readonly ITimer expiryNotification;
    private readonly RetentionMandatoryLeaseCleanup cleanup;
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
        expiresAt = snapshot[0].LeaseExpiresAt;
        if (snapshot.Any(grant => grant.LeaseExpiresAt != expiresAt))
            throw new ArgumentException("A composite handle requires one common expiry.", nameof(grants));

        Grants = Array.AsReadOnly(snapshot);
        this.timeProvider = timeProvider;
        ITimer? preparedExpiry = null;
        try
        {
            preparedExpiry = timeProvider.CreateTimer(
                static state => ((RetentionCommittedReadHandle)state!).ExpiryDue(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            cleanup = new RetentionMandatoryLeaseCleanup(Grants, timeProvider, exactRelease);
            expiryNotification = preparedExpiry;
        }
        catch
        {
            preparedExpiry?.Dispose();
            throw;
        }
    }

    internal IReadOnlyList<RetentionReadGrant> Grants { get; }
    internal bool IsPublished => Volatile.Read(ref state) == Published;

    internal bool Activate()
    {
        try
        {
            var due = expiresAt - timeProvider.GetUtcNow();
            if (due < TimeSpan.Zero) due = TimeSpan.Zero;
            return expiryNotification.Change(due, Timeout.InfiniteTimeSpan)
                && Volatile.Read(ref state) == Hidden;
        }
        catch
        {
            return false;
        }
    }

    internal bool Publish() =>
        Interlocked.CompareExchange(ref state, Published, Hidden) == Hidden;

    internal void AbandonBeforeCommit()
    {
        if (Interlocked.CompareExchange(ref state, Released, Hidden) != Hidden) return;
        expiryNotification.Dispose();
        cleanup.Abandon();
    }

    internal void Lose()
        => Lose(releaseSynchronously: true);

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
            expiryNotification.Dispose();
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
            expiryNotification.Dispose();
            cleanup.ReleaseOrOwn();
            return ValueTask.CompletedTask;
        }
    }

    public override string ToString() => nameof(RetentionCommittedReadHandle);

    private void ExpiryDue()
    {
        var now = timeProvider.GetUtcNow();
        if (now < expiresAt)
        {
            try
            {
                if (!expiryNotification.Change(expiresAt - now, Timeout.InfiniteTimeSpan))
                    Lose(releaseSynchronously: false);
            }
            catch { Lose(releaseSynchronously: false); }
            return;
        }
        Lose(releaseSynchronously: false);
    }
}

internal sealed class RetentionMandatoryLeaseCleanup
{
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
            static state => ((RetentionMandatoryLeaseCleanup)state!).TryRelease(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal void ReleaseOrOwn() => TryRelease();

    internal void Own()
    {
        if (Volatile.Read(ref completed) == 0)
            retry.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
    }

    internal void Abandon()
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
            retry.Dispose();
    }

    public override string ToString() => nameof(RetentionMandatoryLeaseCleanup);

    private void TryRelease()
    {
        if (Volatile.Read(ref completed) != 0) return;
        if (Interlocked.CompareExchange(ref active, 1, 0) != 0) return;
        try
        {
            if (exactRelease(grants))
            {
                if (Interlocked.Exchange(ref completed, 1) == 0)
                    retry.Dispose();
                return;
            }
            retry.Change(TimeSpan.FromMilliseconds(10), Timeout.InfiniteTimeSpan);
        }
        finally
        {
            Volatile.Write(ref active, 0);
        }
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

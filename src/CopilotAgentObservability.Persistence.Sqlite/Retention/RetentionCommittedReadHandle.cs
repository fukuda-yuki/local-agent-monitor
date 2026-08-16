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
    private readonly Func<RetentionCommittedReadHandle, RetentionRawTerminalOperation, RetentionRawTerminalResult>? terminalAuthority;
    private readonly object cancellationGate = new();
    private RetentionExpiryNotification currentNotification;
    private IRetentionReadValueOwner? valueOwner;
    private CancellationToken terminalCancellationToken;
    private CancellationTokenRegistration terminalCancellationRegistration;
    private bool terminalCancellationObserved;
    private bool terminalCancellationDisposed;
    private int state;
    private int terminalState;

    internal RetentionCommittedReadHandle(
        IReadOnlyList<RetentionReadGrant> grants,
        TimeProvider timeProvider,
        Func<IReadOnlyList<RetentionReadGrant>, bool> exactRelease,
        Action? beforeWaitingForReleaseForTesting = null,
        Func<RetentionCommittedReadHandle, RetentionRawTerminalOperation, RetentionRawTerminalResult>? terminalAuthority = null)
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
        this.terminalAuthority = terminalAuthority;
        RetentionExpiryNotification? preparedExpiry = null;
        try
        {
            publicationMembers = Grants
                .Select((grant, index) => new RetentionGrantPublicationMember(grant, index))
                .ToArray();
            foreach (var grant in Grants) grant.AttachCommittedHandle(this);
            preparedExpiry = new RetentionExpiryNotification(this, 1, expiresAt, timeProvider, armDormant: false);
            cleanup = new RetentionMandatoryLeaseCleanup(
                Grants,
                timeProvider,
                grantsToRelease =>
                {
                    CloseValueOwner();
                    return exactRelease(grantsToRelease);
                },
                beforeWaitingForReleaseForTesting);
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
    internal bool AllowsUse => IsPublished && TerminalState == RetentionRawTerminalState.Open;
    internal RetentionRawTerminalState TerminalState => (RetentionRawTerminalState)Volatile.Read(ref terminalState);

    internal void AttachValueOwner(IRetentionReadValueOwner owner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (Interlocked.CompareExchange(ref valueOwner, owner, null) is { } existing
            && !ReferenceEquals(existing, owner))
            throw new InvalidOperationException("A committed handle cannot own multiple value buffers.");
        terminalCancellationToken = cancellationToken;
        if (!AllowsUse) owner.Close();
    }

    internal void ObserveTerminalCancellation()
    {
        lock (cancellationGate)
        {
            if (terminalCancellationObserved || terminalCancellationDisposed) return;
            terminalCancellationObserved = true;
            terminalCancellationRegistration = terminalCancellationToken.UnsafeRegister(
                static state => ((RetentionCommittedReadHandle)state!).LoseAsynchronously(),
                this);
        }
    }

    internal RetentionRawTerminalResult TrySealRawResponse() => TryTerminal(RetentionRawTerminalOperation.SealRawResponse);

    internal RetentionRawTerminalResult TrySealRawReplayTransientPublication() =>
        TryTerminal(RetentionRawTerminalOperation.SealRawReplayTransientPublication);

    internal RetentionRawTerminalResult TrySealRawReplayFilePublication() =>
        TryTerminal(RetentionRawTerminalOperation.SealRawReplayFilePublication);

    internal RetentionRawTerminalResult TryCompleteWithoutRaw() => TryTerminal(RetentionRawTerminalOperation.CompleteWithoutRaw);

    private RetentionRawTerminalResult TryTerminal(RetentionRawTerminalOperation operation)
    {
        ObserveTerminalCancellation();
        if (Interlocked.CompareExchange(
                ref terminalState,
                (int)RetentionRawTerminalState.TerminalAttemptInProgress,
                (int)RetentionRawTerminalState.Open) != (int)RetentionRawTerminalState.Open)
            return TerminalState == RetentionRawTerminalState.Failed
                ? RetentionRawTerminalResult.Busy
                : RetentionRawTerminalResult.Lost;

        CloseValueOwner();
        if (!IsPublished)
        {
            LoseTerminalAttempt();
            return RetentionRawTerminalResult.Lost;
        }
        if (terminalAuthority is null)
        {
            FailTerminalAttempt();
            return RetentionRawTerminalResult.Busy;
        }
        return terminalAuthority(this, operation);
    }

    internal bool IsTerminalAttemptInProgress =>
        TerminalState == RetentionRawTerminalState.TerminalAttemptInProgress;

    internal void InvalidateExpiryNotificationForTerminal() =>
        Volatile.Read(ref currentNotification).Invalidate();

    internal bool TryMoveTerminalAttemptToPending(RetentionRawTerminalOperation operation) =>
        Interlocked.CompareExchange(
            ref terminalState,
            IsSealing(operation)
                ? (int)RetentionRawTerminalState.SealedPending
                : (int)RetentionRawTerminalState.CompletedWithoutRawPending,
            (int)RetentionRawTerminalState.TerminalAttemptInProgress) ==
        (int)RetentionRawTerminalState.TerminalAttemptInProgress;

    internal RetentionRawTerminalResult PublishTerminal(RetentionRawTerminalOperation operation)
    {
        var pending = IsSealing(operation)
            ? RetentionRawTerminalState.SealedPending
            : RetentionRawTerminalState.CompletedWithoutRawPending;
        var final = IsSealing(operation)
            ? RetentionRawTerminalState.Sealed
            : RetentionRawTerminalState.CompletedWithoutRaw;
        if (Interlocked.CompareExchange(ref terminalState, (int)final, (int)pending) != (int)pending)
            return RetentionRawTerminalResult.Lost;
        return IsSealing(operation)
            ? RetentionRawTerminalResult.Sealed
            : RetentionRawTerminalResult.CompletedWithoutRaw;
    }

    private static bool IsSealing(RetentionRawTerminalOperation operation) =>
        operation is RetentionRawTerminalOperation.SealRawResponse
            or RetentionRawTerminalOperation.SealRawReplayTransientPublication
            or RetentionRawTerminalOperation.SealRawReplayFilePublication;

    internal void LoseTerminalAttempt()
    {
        _ = TryLoseTerminalAttempt();
    }

    private bool TryLoseTerminalAttempt()
    {
        Volatile.Read(ref currentNotification).Invalidate();
        while (true)
        {
            var observed = TerminalState;
            if (observed is RetentionRawTerminalState.Lost or RetentionRawTerminalState.Failed) return true;
            if (observed is RetentionRawTerminalState.Sealed or RetentionRawTerminalState.CompletedWithoutRaw) return false;
            if (Interlocked.CompareExchange(ref terminalState, (int)RetentionRawTerminalState.Lost, (int)observed) == (int)observed) return true;
        }
    }

    internal void FailTerminalAttempt()
    {
        Volatile.Read(ref currentNotification).Invalidate();
        while (true)
        {
            var observed = TerminalState;
            if (observed == RetentionRawTerminalState.Failed) return;
            if (observed is RetentionRawTerminalState.Sealed or RetentionRawTerminalState.CompletedWithoutRaw) return;
            if (Interlocked.CompareExchange(ref terminalState, (int)RetentionRawTerminalState.Failed, (int)observed) == (int)observed) return;
        }
    }

    private void CloseValueOwner() => Volatile.Read(ref valueOwner)?.Close();

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
        if (!TryLoseTerminalAttempt()) return;
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

    public async ValueTask DisposeAsync()
    {
        DisposeTerminalCancellation();
        while (true)
        {
            var observed = Volatile.Read(ref state);
            if (observed == Lost)
            {
                await cleanup.ReleaseOrOwnAsync().ConfigureAwait(false);
                return;
            }
            if (observed == Released)
            {
                await cleanup.ReleaseOrOwnAsync().ConfigureAwait(false);
                return;
            }
            if (Interlocked.CompareExchange(ref state, Released, observed) != observed) continue;
            LoseTerminalAttempt();
            Volatile.Read(ref currentNotification).Invalidate();
            await cleanup.ReleaseOrOwnAsync().ConfigureAwait(false);
            return;
        }
    }

    private void DisposeTerminalCancellation()
    {
        lock (cancellationGate)
        {
            terminalCancellationDisposed = true;
            if (terminalCancellationObserved) terminalCancellationRegistration.Dispose();
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
        if (!expected.TryBeginReplacement())
        {
            replacement.Invalidate();
            if (ReferenceEquals(Volatile.Read(ref currentNotification), expected))
                Lose(releaseSynchronously: false);
            return false;
        }
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

    internal void RearmFailed(RetentionExpiryNotification notification)
    {
        var current = Volatile.Read(ref currentNotification);
        if (!ReferenceEquals(current, notification)
            || current.Generation != notification.Generation
            || !notification.TryInvalidateAfterRearmFailure())
            return;
        Lose(releaseSynchronously: false);
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
    private const int Replacing = 2;
    private const int Invalid = 3;
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
                handle.RearmFailed(this);
        }
        catch { handle.RearmFailed(this); }
    }

    internal bool TryBeginReplacement() =>
        Interlocked.CompareExchange(ref state, Replacing, Active) == Active;

    internal bool TryInvalidateAfterRearmFailure()
    {
        if (Interlocked.CompareExchange(ref state, Invalid, Active) != Active) return false;
        DisposeTimer();
        return true;
    }

    internal void Invalidate()
    {
        if (Interlocked.Exchange(ref state, Invalid) == Invalid) return;
        DisposeTimer();
    }

    private void DisposeTimer()
    {
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
    private readonly Action? beforeWaitingForReleaseForTesting;
    private readonly Func<Action, bool>? queueReleaseForTesting;
    private readonly ITimer retry;
    private ReleaseAttempt? active;
    private int pending;
    private int completed;

    internal RetentionMandatoryLeaseCleanup(
        IReadOnlyList<RetentionReadGrant> grants,
        TimeProvider timeProvider,
        Func<IReadOnlyList<RetentionReadGrant>, bool> exactRelease,
        Action? beforeWaitingForReleaseForTesting,
        Func<Action, bool>? queueReleaseForTesting = null)
    {
        this.grants = grants;
        this.exactRelease = exactRelease;
        this.beforeWaitingForReleaseForTesting = beforeWaitingForReleaseForTesting;
        this.queueReleaseForTesting = queueReleaseForTesting;
        retry = timeProvider.CreateTimer(
            static state => ((RetentionMandatoryLeaseCleanup)state!).SignalRelease(),
            this,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);
    }

    internal void ReleaseOrOwn()
    {
        while (Volatile.Read(ref completed) == 0)
        {
            var attempt = Volatile.Read(ref active);
            if (attempt is not null)
            {
                beforeWaitingForReleaseForTesting?.Invoke();
                if (attempt.TryStart())
                {
                    TryRelease(attempt, retryOnFailure: true);
                    return;
                }
                if (attempt.WaitForOutcome() != ReleaseAttemptOutcome.NotStarted) return;
                continue;
            }

            Volatile.Write(ref pending, 1);
            attempt = new ReleaseAttempt();
            if (Interlocked.CompareExchange(ref active, attempt, null) is not null) continue;
            AssertStarted(attempt);
            TryRelease(attempt, retryOnFailure: true);
            return;
        }
    }

    internal async ValueTask ReleaseOrOwnAsync()
    {
        while (Volatile.Read(ref completed) == 0)
        {
            Volatile.Write(ref pending, 1);
            var attempt = Volatile.Read(ref active);
            if (attempt is null)
            {
                TryQueueRelease();
                attempt = Volatile.Read(ref active);
                if (attempt is null)
                {
                    await Task.Yield();
                    continue;
                }
            }

            beforeWaitingForReleaseForTesting?.Invoke();
            if (await attempt.WaitForOutcomeAsync().ConfigureAwait(false) != ReleaseAttemptOutcome.NotStarted)
                return;
        }
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
        Volatile.Write(ref pending, 1);
        TryQueueRelease();
    }

    private bool TryQueueRelease()
    {
        if (Volatile.Read(ref completed) != 0 || Volatile.Read(ref pending) == 0) return true;
        var attempt = new ReleaseAttempt();
        try
        {
            if (Volatile.Read(ref completed) != 0
                || Interlocked.CompareExchange(ref active, attempt, null) is not null)
                return true;
            var queued = queueReleaseForTesting is null
                ? ThreadPool.UnsafeQueueUserWorkItem(
                    static state => state.Cleanup.TryRelease(state.Attempt, retryOnFailure: true),
                    (Cleanup: this, Attempt: attempt),
                    preferLocal: false)
                : queueReleaseForTesting(() => TryRelease(attempt, retryOnFailure: true));
            if (!queued)
            {
                if (!attempt.TryCancelBeforeStart()) return true;
                Interlocked.CompareExchange(ref active, null, attempt);
                attempt.Complete(ReleaseAttemptOutcome.NotStarted);
                return false;
            }
            return true;
        }
        catch
        {
            if (!attempt.TryCancelBeforeStart()) return true;
            Interlocked.CompareExchange(ref active, null, attempt);
            attempt.Complete(ReleaseAttemptOutcome.NotStarted);
            return false;
        }
    }

    private void TryRelease(ReleaseAttempt attempt, bool retryOnFailure)
    {
        if (!attempt.IsStarted && !attempt.TryStart()) return;
        Volatile.Write(ref pending, 0);
        var released = false;
        try
        {
            released = exactRelease(grants);
        }
        // Cleanup must not propagate release failures; the scheduled retry owns recovery.
        catch { }

        if (released)
            Complete();
        Interlocked.CompareExchange(ref active, null, attempt);
        attempt.Complete(
            released
                ? ReleaseAttemptOutcome.Released
                : ReleaseAttemptOutcome.RetainedForRetry);
        if (!released && retryOnFailure)
        {
            Volatile.Write(ref pending, 1);
            ScheduleRetry();
        }
    }

    private void ScheduleRetry()
    {
        if (Volatile.Read(ref completed) != 0) return;
        try
        {
            if (retry.Change(RetryDelay, Timeout.InfiniteTimeSpan)) return;
        }
        catch { }
        TryQueueRelease();
    }

    private static void AssertStarted(ReleaseAttempt attempt)
    {
        if (!attempt.TryStart()) throw new InvalidOperationException("A newly owned release attempt must be startable.");
    }

    private void Complete()
    {
        if (Interlocked.Exchange(ref completed, 1) != 0) return;
        try { retry.Dispose(); }
        catch { }
    }

    private enum ReleaseAttemptOutcome
    {
        NotStarted,
        Released,
        RetainedForRetry,
    }

    private sealed class ReleaseAttempt
    {
        private const int Waiting = 0;
        private const int Started = 1;
        private const int Cancelled = 2;
        private readonly TaskCompletionSource<ReleaseAttemptOutcome> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int state;

        internal bool IsStarted => Volatile.Read(ref state) == Started;

        internal bool TryStart() => Interlocked.CompareExchange(ref state, Started, Waiting) == Waiting;

        internal bool TryCancelBeforeStart() =>
            Interlocked.CompareExchange(ref state, Cancelled, Waiting) == Waiting;

        internal void Complete(ReleaseAttemptOutcome outcome) => completion.TrySetResult(outcome);

        internal ReleaseAttemptOutcome WaitForOutcome() => completion.Task.GetAwaiter().GetResult();

        internal Task<ReleaseAttemptOutcome> WaitForOutcomeAsync() => completion.Task;
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

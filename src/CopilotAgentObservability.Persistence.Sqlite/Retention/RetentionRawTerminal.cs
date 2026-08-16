namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionRawTerminalResult
{
    Sealed,
    CompletedWithoutRaw,
    Lost,
    Busy,
}

internal enum RetentionRawTerminalState
{
    Open,
    TerminalAttemptInProgress,
    SealedPending,
    CompletedWithoutRawPending,
    Sealed,
    CompletedWithoutRaw,
    Lost,
    Failed,
}

internal enum RetentionRawTerminalOperation
{
    SealRawResponse,
    SealRawReplayTransientPublication,
    SealRawReplayFilePublication,
    CompleteWithoutRaw,
}

internal enum RetentionRawTerminalCheckpoint
{
    AfterClaimBeforeTransaction,
    AfterTransactionBeganBeforePublicationScopes,
    AfterPublicationScopesAcquiredBeforeClockSample,
    AfterClockSampleBeforeProof,
    AfterProofBeforeStateMove,
    AfterStateMoveBeforeCommit,
    AfterCommitBeforePublish,
    AfterPublish,
    RenewalCommittedBeforePublication,
}

internal interface IRetentionRawTerminalCheckpoint
{
    void Reached(RetentionRawTerminalCheckpoint checkpoint);
}

internal sealed class RetentionRawTerminalBusyException : Exception;

internal interface IRetentionReadValueOwner
{
    void Close();
    bool TryClose();
}

internal sealed class RetentionReadValueOwner<T> : IRetentionReadValueOwner
{
    private readonly object gate = new();
    private T? value;
    private int references;
    private bool closed;
    private bool cleared;

    internal RetentionReadValueOwner(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.value = value;
    }

    internal T Value
    {
        get
        {
            lock (gate)
            {
                if (closed || cleared) throw new InvalidOperationException("The retention value buffer is closed.");
                return value!;
            }
        }
    }

    internal bool IsCleared
    {
        get { lock (gate) return cleared; }
    }

    internal RetentionReadValueReference<T> Acquire()
    {
        lock (gate)
        {
            if (closed || cleared) throw new InvalidOperationException("The retention value buffer is closed.");
            references++;
            return new RetentionReadValueReference<T>(this);
        }
    }

    internal T ReadForReference()
    {
        lock (gate)
        {
            if (cleared) throw new InvalidOperationException("The retention value buffer was cleared while referenced.");
            return value!;
        }
    }

    internal void ReleaseReference()
    {
        lock (gate)
        {
            if (references <= 0) throw new InvalidOperationException("The retention value reference count is inconsistent.");
            references--;
            ClearIfDrained();
        }
    }

    public void Close()
    {
        TryClose();
    }

    public bool TryClose()
    {
        lock (gate)
        {
            closed = true;
            ClearIfDrained();
            return references == 0;
        }
    }

    private void ClearIfDrained()
    {
        if (!closed || references != 0 || cleared) return;
        value = default;
        cleared = true;
    }
}

internal sealed class RetentionReadValueReference<T> : IDisposable
{
    private RetentionReadValueOwner<T>? owner;

    internal RetentionReadValueReference(RetentionReadValueOwner<T> owner) =>
        this.owner = owner ?? throw new ArgumentNullException(nameof(owner));

    internal T Value => (owner ?? throw new ObjectDisposedException(nameof(RetentionReadValueReference<T>))).ReadForReference();

    public void Dispose() => Interlocked.Exchange(ref owner, null)?.ReleaseReference();
}

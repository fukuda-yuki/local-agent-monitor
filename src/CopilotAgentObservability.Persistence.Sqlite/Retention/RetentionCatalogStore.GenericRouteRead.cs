using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    // Every other admission takes the catalog gate before it opens its transaction. The generic
    // Session route opens the transaction itself, so it must take the gate through this scope
    // first: acquiring it after an already-open IMMEDIATE transaction would invert the lock order
    // against every ordinary admission.
    internal async ValueTask<IDisposable> EnterAdmissionGateAsync(CancellationToken cancellationToken)
    {
        var gate = context?.Gate;
        if (gate is null)
            return NullGateScope.Instance;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateScope(gate);
    }

    private sealed class GateScope(SemaphoreSlim gate) : IDisposable
    {
        private int released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
                gate.Release();
        }
    }

    private sealed class NullGateScope : IDisposable
    {
        internal static readonly NullGateScope Instance = new();

        public void Dispose()
        {
        }
    }

    // The narrow transaction-aware arm carved for Group 6's generic Session content route.
    //
    // It is not a second raw authority: it shares the same predicate, capability, lease, selector,
    // consumption, seal, and release implementation as every other read, and differs only in who
    // owns the transaction. The Session route must hold one BEGIN IMMEDIATE across its type-only
    // policy check, the lease insertion, and the content selection, so that no concurrent Event
    // type change can commit between them; that is only expressible if the admission and the
    // selector both run on the caller's connection and transaction.
    //
    // This method takes over completing that transaction: it always commits or rolls back before
    // returning, and the caller must not do either itself. A failure before the commit rolls the
    // uncommitted lease back and discards the selected content, so no grant survives it and no
    // terminal method is called.
    internal async ValueTask<RetentionReadResult<T>> ReadWithinCallerTransactionAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionReadRequest request,
        Func<SqliteConnection, SqliteTransaction, RetentionReadGrant, CancellationToken, ValueTask<T?>> selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selector);

        var prepared = PrepareOrdinaryAdmission(
            connection, transaction, [request], timeProvider.GetUtcNow(), cancellationToken);

        if (prepared.Handle is null)
        {
            ApplyAdmissionTransactionDecision(transaction, prepared.TransactionDecision);
            return RetentionReadResult<T>.FromDisposition(prepared.Failure!.Value);
        }

        var handle = prepared.Handle;
        var grant = handle.Grants[0];

        T? value;
        try
        {
            value = await RetentionPostGrantConsumptionContradiction.NormalizeAsync(
                    () => selector(connection, transaction, grant, cancellationToken))
                .ConfigureAwait(false);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return AbandonUncommittedAdmission<T>(handle, transaction, RetentionReadDisposition.Busy);
        }
        catch (RetentionPostGrantConsumptionContradictionException)
        {
            return AbandonUncommittedAdmission<T>(
                handle, transaction, RetentionReadDisposition.SelectorUnavailable);
        }
        catch (OperationCanceledException)
        {
            AbandonUncommittedAdmission<T>(handle, transaction, RetentionReadDisposition.SelectorUnavailable);
            throw;
        }

        if (value is null)
        {
            return AbandonUncommittedAdmission<T>(
                handle, transaction, RetentionReadDisposition.SelectorUnavailable);
        }

        try
        {
            transaction.Commit();
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            handle.AbandonBeforeCommit();
            return RetentionReadResult<T>.FromDisposition(RetentionReadDisposition.Busy);
        }
        catch
        {
            handle.AbandonBeforeCommit();
            throw;
        }

        var activation = ActivateAndPublishCommittedHandle(handle, cancellationToken);
        if (activation.Handle is null)
            return RetentionReadResult<T>.FromDisposition(activation.Disposition!.Value);

        return await PublishValueForCommittedHandleAsync(handle, grant, value, cancellationToken)
            .ConfigureAwait(false);
    }

    // The content was already selected under the caller's transaction, so this fence only re-proves
    // that the committed grant is still usable before the value becomes caller-accessible. It is
    // the same publication fence every other read uses.
    private async ValueTask<RetentionReadResult<T>> PublishValueForCommittedHandleAsync<T>(
        RetentionCommittedReadHandle handle,
        RetentionReadGrant grant,
        T value,
        CancellationToken cancellationToken)
    {
        try
        {
            readBoundaryCheckpoint?.Reached(RetentionReadBoundaryCheckpoint.BeforeConsumptionTransaction);
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            if (!ProveGrantsUsable(connection, transaction, handle.Grants))
            {
                transaction.Rollback();
                await handle.DisposeAsync().ConfigureAwait(false);
                return RetentionReadResult<T>.FromDisposition(RetentionReadDisposition.LeaseLost);
            }

            var (published, publicationRetained) = TryPublishReadValue(
                connection,
                transaction,
                handle,
                cancellationToken,
                () => new RetentionReadLease<T>(
                    value,
                    RetentionRevisionFence.Create(),
                    grant,
                    handle,
                    cancellationToken));

            if (published is not null && publicationRetained)
                return RetentionReadResult<T>.FromHandle(published);
            if (published is not null)
                await published.DisposeAsync().ConfigureAwait(false);
            else
                await handle.DisposeAsync().ConfigureAwait(false);
            return RetentionReadResult<T>.FromDisposition(RetentionReadDisposition.LeaseLost);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return RetainSinglePostGrantFailure<T>(handle, RetentionReadDisposition.Busy, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static RetentionReadResult<T> AbandonUncommittedAdmission<T>(
        RetentionCommittedReadHandle handle,
        SqliteTransaction transaction,
        RetentionReadDisposition disposition)
    {
        handle.AbandonBeforeCommit();
        try
        {
            transaction.Rollback();
        }
        catch (SqliteException)
        {
            // The rollback target is already gone; the abandoned handle owns no committed grant
            // either way, so there is nothing further to release.
        }

        return RetentionReadResult<T>.FromDisposition(disposition);
    }
}

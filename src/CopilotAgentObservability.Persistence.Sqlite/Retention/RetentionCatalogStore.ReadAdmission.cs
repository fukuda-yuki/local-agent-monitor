using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionHandlePublicationDisposition
{
    Published,
    LeaseLost,
    Busy,
}

public sealed partial class RetentionCatalogStore
{
    private async ValueTask<RetentionReadAdmissionResult> AdmitReadAsync(
        RetentionReadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await AdmitFixedReadBatchAsync([request], cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<RetentionReadAdmissionResult> AdmitFixedReadBatchAsync(
        IReadOnlyList<RetentionReadRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var frozenRequests = requests.ToArray();
        if (frozenRequests.Length == 0)
            throw new ArgumentException("A nonempty admission requires at least one request.", nameof(requests));

        var gate = context?.Gate;
        if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            var initialAt = timeProvider.GetUtcNow();
            return CompleteOrdinaryAdmission(connection, transaction, frozenRequests, initialAt, cancellationToken);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.Busy);
        }
        catch (SqliteException)
        {
            return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.SelectorUnavailable);
        }
        finally
        {
            gate?.Release();
        }
    }

    private async ValueTask<(RetentionReadAdmissionResult? Admission, T? EmptyValue)> AdmitSelectedReadBatchAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<IReadOnlyList<RetentionReadRequest>>> candidateSelector,
        Func<SqliteConnection, SqliteTransaction, IReadOnlyList<RetentionReadGrant>, CancellationToken, ValueTask<T?>> emptySelector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidateSelector);
        ArgumentNullException.ThrowIfNull(emptySelector);
        var gate = context?.Gate;
        if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            var selected = await candidateSelector(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (selected is null)
            {
                transaction.Rollback();
                return (RetentionReadAdmissionResult.Failed(RetentionReadDisposition.SelectorUnavailable), default);
            }
            var requests = selected.ToArray();
            if (requests.Length == 0)
            {
                var emptyValue = await emptySelector(
                    connection,
                    transaction,
                    Array.Empty<RetentionReadGrant>(),
                    cancellationToken).ConfigureAwait(false);
                if (emptyValue is null)
                {
                    transaction.Rollback();
                    return (RetentionReadAdmissionResult.Failed(RetentionReadDisposition.SelectorUnavailable), default);
                }
                transaction.Commit();
                return (null, emptyValue);
            }

            var initialAt = timeProvider.GetUtcNow();
            return (CompleteOrdinaryAdmission(connection, transaction, requests, initialAt, cancellationToken), default);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return (RetentionReadAdmissionResult.Failed(RetentionReadDisposition.Busy), default);
        }
        catch (SqliteException)
        {
            return (RetentionReadAdmissionResult.Failed(RetentionReadDisposition.SelectorUnavailable), default);
        }
        finally
        {
            gate?.Release();
        }
    }

    private RetentionReadAdmissionResult CompleteOrdinaryAdmission(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RetentionReadRequest> requests,
        DateTimeOffset initialAt,
        CancellationToken cancellationToken)
    {
        var items = new RetentionCatalogItem[requests.Count];
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var item = FindForUpdate(connection, transaction, request.OwnershipKey);
            if (item is null)
            {
                transaction.Commit();
                return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.LifecycleDenied);
            }
            if (request.ExpectedRevision is not null && item.Revision != request.ExpectedRevision)
            {
                transaction.Commit();
                return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.LifecycleDenied);
            }
            items[index] = item;
        }

        for (var index = 0; index < requests.Count; index++)
        {
            var item = items[index];
            var readability = ClassifyRowReadability(item, initialAt);
            if (readability != RetentionRowReadability.Readable)
            {
                if (readability == RetentionRowReadability.ExpiredExpiring)
                {
                    foreach (var expired in items.Where(candidate =>
                        candidate is not null
                        && ClassifyRowReadability(candidate, initialAt) == RetentionRowReadability.ExpiredExpiring))
                        DenyAndQueue(connection, transaction, expired, initialAt);
                }
                transaction.Commit();
                return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.LifecycleDenied);
            }

            var proof = SourceProof(connection, transaction, requests[index].OwnershipKey);
            if (proof == SourceReceiptProof.CatalogBusy)
            {
                transaction.Rollback();
                return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.Busy);
            }
            if (proof != SourceReceiptProof.Match)
            {
                if (proof == SourceReceiptProof.Missing)
                    DenyMissingSource(connection, transaction, item, initialAt);
                else
                    DenyInvalidSource(connection, transaction, item, initialAt, proof);
                transaction.Commit();
                return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.LifecycleDenied);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        var finalAt = timeProvider.GetUtcNow();
        var crossed = items.Where(item =>
            ClassifyRowReadability(item, finalAt) == RetentionRowReadability.ExpiredExpiring).ToArray();
        if (crossed.Length > 0)
        {
            foreach (var item in crossed)
                DenyAndQueue(connection, transaction, item, finalAt);
            transaction.Commit();
            return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.LifecycleDenied);
        }

        DateTimeOffset leaseExpiry;
        try
        {
            leaseExpiry = finalAt.Add(RetentionV1Constants.LeaseDuration);
        }
        catch (ArgumentOutOfRangeException)
        {
            transaction.Rollback();
            return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.SelectorUnavailable);
        }

        var owner = Guid.NewGuid().ToString("N");
        var grants = new List<RetentionReadGrant>(requests.Count);
        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];
            var item = items[index];
            var kind = request.LeaseKind == RetentionReadKind.Access
                ? RetentionLeaseKind.Access
                : RetentionLeaseKind.Operation;
            var generation = AcquireLease(
                connection,
                transaction,
                item.ItemId,
                kind,
                owner,
                finalAt,
                leaseExpiry);
            if (generation is null)
            {
                transaction.Rollback();
                return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.Busy);
            }
            var token = SourceToken(connection, transaction, request.OwnershipKey);
            if (token is null)
            {
                ReleaseWithinTransaction(connection, transaction, item.ItemId, kind, owner, generation.Value);
                ReleaseWithinTransaction(connection, transaction, grants);
                DenyInvalidSource(connection, transaction, item, finalAt, SourceReceiptProof.InvalidIdentity);
                transaction.Commit();
                return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.LifecycleDenied);
            }
            grants.Add(new RetentionReadGrant(
                request.OwnershipKey,
                item.ItemId,
                item.Revision,
                kind,
                owner,
                generation.Value,
                leaseExpiry,
                token));
        }

        RetentionCommittedReadHandle handle;
        try
        {
            handle = new RetentionCommittedReadHandle(
                grants,
                timeProvider,
                TryReleaseCommittedGrants,
                terminalAuthority: TryCompleteRawTerminal);
        }
        catch (Exception)
        {
            transaction.Rollback();
            return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.SelectorUnavailable);
        }

        try
        {
            transaction.Commit();
        }
        catch
        {
            handle.AbandonBeforeCommit();
            throw;
        }

        if (!handle.Activate())
        {
            handle.Lose();
            return RetentionReadAdmissionResult.Failed(RetentionReadDisposition.LeaseLost);
        }

        var publication = TryPublishCommittedHandle(handle, cancellationToken);
        if (publication == RetentionHandlePublicationDisposition.Published)
            return RetentionReadAdmissionResult.Granted(handle);
        handle.Lose();
        return RetentionReadAdmissionResult.Failed(
            publication == RetentionHandlePublicationDisposition.Busy
                ? RetentionReadDisposition.Busy
                : RetentionReadDisposition.LeaseLost);
    }

    private bool TryReleaseCommittedGrants(IReadOnlyList<RetentionReadGrant> grants)
    {
        try
        {
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            ReleaseWithinTransaction(connection, transaction, grants);
            transaction.Commit();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}

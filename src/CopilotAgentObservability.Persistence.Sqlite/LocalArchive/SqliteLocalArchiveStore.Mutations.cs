using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteLocalArchiveStore
{
    internal LocalArchiveMutationResult Mutate(
        LocalArchiveAction action,
        LocalArchiveTargetKind targetKind,
        IReadOnlyList<LocalArchiveMutationTarget> targets,
        LocalArchiveSuccessEntityWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(writer);
        var requestOrder = FreezeTargets(action, targetKind, targets);
        var canonicalOrder = requestOrder
            .OrderBy(target => target.TargetId, StringComparer.Ordinal)
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            var canonicalIds = Array.AsReadOnly(canonicalOrder.Select(target => target.TargetId).ToArray());
            var existing = ReadExisting(
                connection,
                transaction,
                targetKind,
                canonicalIds,
                cancellationToken);
            if (!canonicalIds.SequenceEqual(existing, StringComparer.Ordinal))
            {
                transaction.Commit();
                return MutationError(LocalArchiveStoreError.TargetNotFound);
            }

            var facts = new Dictionary<string, MutationFact>(canonicalOrder.Length, StringComparer.Ordinal);
            foreach (var target in canonicalOrder)
            {
                var current = ReadCurrent(connection, transaction, targetKind, target.TargetId, cancellationToken)
                    ?? ActiveZero(target.TargetId);
                var history = ReadHistory(
                    connection,
                    transaction,
                    targetKind,
                    target.TargetId,
                    cancellationToken);
                if (!LocalArchiveValidation.TryFreezeAndValidateHistory(
                    targetKind,
                    current,
                    history,
                    out var frozenHistory))
                {
                    throw StoreUnavailable();
                }
                facts.Add(target.TargetId, new MutationFact(current, frozenHistory));
            }

            var classified = canonicalOrder
                .Select(target => new ClassifiedTarget(
                    target,
                    facts[target.TargetId],
                    Classify(action, target, facts[target.TargetId])))
                .ToArray();
            if (classified.Any(item => item.Classification == MutationClassification.Stale)
                || classified.Any(item => item.Classification == MutationClassification.Apply)
                && classified.Any(item => item.Classification == MutationClassification.SemanticRetry))
            {
                transaction.Commit();
                return MutationError(LocalArchiveStoreError.RevisionConflict);
            }
            if (classified.Any(item => item.Classification == MutationClassification.RevisionExhausted))
            {
                transaction.Commit();
                return MutationError(LocalArchiveStoreError.ArchiveStoreUnavailable);
            }

            Dictionary<string, LocalArchiveMutationTargetSuccess>? applied = null;
            if (classified.Any(item => item.Classification == MutationClassification.Apply))
            {
                var instant = timeProvider.GetUtcNow().ToUniversalTime();
                var occurredAt = instant.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
                if (!LocalRepositoryCatalogValidation.IsCanonicalTimestamp(occurredAt))
                    throw StoreUnavailable();
                applied = new Dictionary<string, LocalArchiveMutationTargetSuccess>(StringComparer.Ordinal);
                var eventIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in classified.Where(item => item.Classification == MutationClassification.Apply))
                {
                    var eventId = eventIdFactory(instant);
                    if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(eventId)
                        || !eventIds.Add(eventId))
                    {
                        throw StoreUnavailable();
                    }
                    var appliedTarget = Apply(
                        connection,
                        transaction,
                        action,
                        targetKind,
                        item.Target,
                        eventId,
                        occurredAt,
                        cancellationToken);
                    applied.Add(item.Target.TargetId, appliedTarget);
                }
            }

            var successfulTargets = new LocalArchiveMutationTargetSuccess[requestOrder.Length];
            for (var index = 0; index < requestOrder.Length; index++)
            {
                var target = requestOrder[index];
                successfulTargets[index] = applied is not null && applied.TryGetValue(target.TargetId, out var value)
                    ? value
                    : facts[target.TargetId].Current;
            }
            var success = new LocalArchiveMutationSuccess(action, targetKind, successfulTargets);
            cancellationToken.ThrowIfCancellationRequested();
            var entity = writer(success);
            if (!LocalArchiveValidation.TryCopySuccessEntity(entity, out var succeeded))
                throw StoreUnavailable();
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new LocalArchiveMutationResult(succeeded, Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return MutationError(LocalArchiveStoreError.PersistenceBusy);
        }
        catch
        {
            return MutationError(LocalArchiveStoreError.ArchiveStoreUnavailable);
        }
    }

    private static LocalArchiveMutationTarget[] FreezeTargets(
        LocalArchiveAction action,
        LocalArchiveTargetKind targetKind,
        IReadOnlyList<LocalArchiveMutationTarget> targets)
    {
        if (action is not (LocalArchiveAction.Archive or LocalArchiveAction.Restore)
            || !IsDefined(targetKind))
        {
            throw new ArgumentException("local_archive_mutation_invalid");
        }
        var count = targets.Count;
        if (count is < 1 or > 200 || targetKind == LocalArchiveTargetKind.Repository && count != 1)
            throw new ArgumentException("local_archive_mutation_invalid", nameof(targets));

        var frozen = new LocalArchiveMutationTarget[count];
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var target = targets[index];
            if (target is null
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(target.TargetId)
                || target.ExpectedRevision < 0
                || !ids.Add(target.TargetId))
            {
                throw new ArgumentException("local_archive_mutation_invalid", nameof(targets));
            }
            frozen[index] = target;
        }
        return frozen;
    }

    private static MutationClassification Classify(
        LocalArchiveAction action,
        LocalArchiveMutationTarget target,
        MutationFact fact)
    {
        var desired = action == LocalArchiveAction.Archive
            ? LocalArchiveState.Archived
            : LocalArchiveState.Active;
        if (fact.Current.Revision == target.ExpectedRevision)
        {
            if (fact.Current.State == desired)
                return MutationClassification.NoOp;
            return fact.Current.Revision == long.MaxValue
                ? MutationClassification.RevisionExhausted
                : MutationClassification.Apply;
        }
        if (target.ExpectedRevision < long.MaxValue
            && fact.Current.Revision == target.ExpectedRevision + 1
            && fact.Current.State == desired
            && fact.History.Count != 0
            && fact.History[^1].Action == action
            && fact.History[^1].PreviousRevision == target.ExpectedRevision
            && fact.History[^1].NewRevision == fact.Current.Revision)
        {
            return MutationClassification.SemanticRetry;
        }
        return MutationClassification.Stale;
    }

    private static IReadOnlyList<LocalArchiveStoredEvent> ReadHistory(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveTargetKind targetKind,
        string targetId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at,
                   typeof(event_id),typeof(target_kind),typeof(target_id),typeof(action),
                   typeof(previous_revision),typeof(new_revision),typeof(occurred_at)
            FROM local_archive_events
            WHERE target_kind=$kind AND target_id=$id
            ORDER BY new_revision ASC;
            """;
        command.Parameters.Add("$kind", SqliteType.Text).Value = KindText(targetKind);
        command.Parameters.Add("$id", SqliteType.Text).Value = targetId;
        using var reader = command.ExecuteReader();
        var events = new List<LocalArchiveStoredEvent>();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.FieldCount != 14
                || reader.GetValue(0) is not string eventId
                || reader.GetValue(1) is not string kind
                || reader.GetValue(2) is not string rowTargetId
                || reader.GetValue(3) is not string action
                || reader.GetValue(4) is not long previous
                || reader.GetValue(5) is not long revision
                || reader.GetValue(6) is not string occurredAt
                || Enumerable.Range(7, 7).Any(index => reader.GetValue(index) is not string type || type != (index is 11 or 12 ? "integer" : "text"))
                || kind != KindText(targetKind)
                || rowTargetId != targetId)
            {
                throw StoreUnavailable();
            }
            events.Add(new LocalArchiveStoredEvent(
                eventId,
                targetKind,
                rowTargetId,
                action switch
                {
                    "archive" => LocalArchiveAction.Archive,
                    "restore" => LocalArchiveAction.Restore,
                    _ => throw StoreUnavailable(),
                },
                previous,
                revision,
                occurredAt));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return events.ToArray();
    }

    private static LocalArchiveMutationTargetSuccess Apply(
        SqliteConnection connection,
        SqliteTransaction transaction,
        LocalArchiveAction action,
        LocalArchiveTargetKind targetKind,
        LocalArchiveMutationTarget target,
        string eventId,
        string occurredAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var newRevision = checked(target.ExpectedRevision + 1);
        var state = action == LocalArchiveAction.Archive ? "archived" : "active";
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            if (target.ExpectedRevision == 0)
            {
                current.CommandText = """
                    INSERT INTO local_archive_current(target_kind,target_id,state,revision,archived_at,updated_at)
                    VALUES($kind,$id,$state,$revision,$archived_at,$occurred_at);
                    """;
            }
            else
            {
                current.CommandText = """
                    UPDATE local_archive_current
                    SET state=$state,revision=$revision,archived_at=$archived_at,updated_at=$occurred_at
                    WHERE target_kind=$kind AND target_id=$id AND revision=$expected;
                    """;
                current.Parameters.Add("$expected", SqliteType.Integer).Value = target.ExpectedRevision;
            }
            current.Parameters.Add("$kind", SqliteType.Text).Value = KindText(targetKind);
            current.Parameters.Add("$id", SqliteType.Text).Value = target.TargetId;
            current.Parameters.Add("$state", SqliteType.Text).Value = state;
            current.Parameters.Add("$revision", SqliteType.Integer).Value = newRevision;
            current.Parameters.Add("$archived_at", SqliteType.Text).Value =
                action == LocalArchiveAction.Archive ? occurredAt : DBNull.Value;
            current.Parameters.Add("$occurred_at", SqliteType.Text).Value = occurredAt;
            if (current.ExecuteNonQuery() != 1)
                throw StoreUnavailable();
        }
        using (var append = connection.CreateCommand())
        {
            append.Transaction = transaction;
            append.CommandText = """
                INSERT INTO local_archive_events(
                  event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at)
                VALUES($event_id,$kind,$id,$action,$previous,$revision,$occurred_at);
                """;
            append.Parameters.Add("$event_id", SqliteType.Text).Value = eventId;
            append.Parameters.Add("$kind", SqliteType.Text).Value = KindText(targetKind);
            append.Parameters.Add("$id", SqliteType.Text).Value = target.TargetId;
            append.Parameters.Add("$action", SqliteType.Text).Value = action == LocalArchiveAction.Archive ? "archive" : "restore";
            append.Parameters.Add("$previous", SqliteType.Integer).Value = target.ExpectedRevision;
            append.Parameters.Add("$revision", SqliteType.Integer).Value = newRevision;
            append.Parameters.Add("$occurred_at", SqliteType.Text).Value = occurredAt;
            if (append.ExecuteNonQuery() != 1)
                throw StoreUnavailable();
        }
        return new LocalArchiveMutationTargetSuccess(
            target.TargetId,
            action == LocalArchiveAction.Archive ? LocalArchiveState.Archived : LocalArchiveState.Active,
            newRevision,
            action == LocalArchiveAction.Archive ? occurredAt : null,
            occurredAt);
    }

    private static LocalArchiveMutationTargetSuccess ActiveZero(string targetId) =>
        new(targetId, LocalArchiveState.Active, 0, ArchivedAt: null, UpdatedAt: null);

    private sealed record MutationFact(
        LocalArchiveMutationTargetSuccess Current,
        IReadOnlyList<LocalArchiveStoredEvent> History);

    private sealed record ClassifiedTarget(
        LocalArchiveMutationTarget Target,
        MutationFact Fact,
        MutationClassification Classification);

    private enum MutationClassification
    {
        Apply,
        NoOp,
        SemanticRetry,
        RevisionExhausted,
        Stale,
    }
}

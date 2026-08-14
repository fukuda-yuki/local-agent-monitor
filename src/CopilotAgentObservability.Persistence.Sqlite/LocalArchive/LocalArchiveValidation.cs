namespace CopilotAgentObservability.Persistence.Sqlite;

internal static class LocalArchiveValidation
{
    internal static bool TryFreezeMutationSuccess(
        LocalArchiveAction action,
        LocalArchiveTargetKind targetKind,
        IReadOnlyList<LocalArchiveMutationTargetSuccess> targets,
        out LocalArchiveMutationSuccess? success)
    {
        ArgumentNullException.ThrowIfNull(targets);

        success = null;
        if (!IsDefined(action) || !IsDefined(targetKind))
            return false;

        var count = targets.Count;
        if (count is < 1 or > 200 || targetKind == LocalArchiveTargetKind.Repository && count != 1)
            return false;

        var frozen = new LocalArchiveMutationTargetSuccess[count];
        var targetIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var target = targets[index];
            if (target is null
                || !IsValidTargetFact(target)
                || !targetIds.Add(target.TargetId)
                || action == LocalArchiveAction.Archive && target.State != LocalArchiveState.Archived
                || action == LocalArchiveAction.Restore && target.State != LocalArchiveState.Active)
                return false;
            frozen[index] = target;
        }

        success = new LocalArchiveMutationSuccess(action, targetKind, frozen);
        return true;
    }

    internal static bool TryFreezeAndValidateHistory(
        LocalArchiveTargetKind targetKind,
        LocalArchiveMutationTargetSuccess current,
        IReadOnlyList<LocalArchiveStoredEvent> events,
        out IReadOnlyList<LocalArchiveStoredEvent> frozen)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(events);

        frozen = Array.Empty<LocalArchiveStoredEvent>();
        if (!IsDefined(targetKind) || !IsValidTargetFact(current))
            return false;

        var count = events.Count;
        if ((long)count != current.Revision)
            return false;

        var items = new LocalArchiveStoredEvent[count];
        var eventIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            var item = events[index];
            var expectedNewRevision = (long)index + 1;
            var expectedAction = expectedNewRevision % 2 == 1
                ? LocalArchiveAction.Archive
                : LocalArchiveAction.Restore;
            if (item is null
                || item.TargetKind != targetKind
                || !string.Equals(item.TargetId, current.TargetId, StringComparison.Ordinal)
                || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(item.EventId)
                || !eventIds.Add(item.EventId)
                || item.Action != expectedAction
                || item.PreviousRevision != index
                || item.NewRevision != expectedNewRevision
                || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(item.OccurredAt))
                return false;
            items[index] = item;
        }

        if (count > 0)
        {
            var head = items[^1];
            if (!IsValidCurrentAndHead(targetKind, current, head))
                return false;
        }

        frozen = items;
        return true;
    }

    internal static bool IsValidCurrentAndHead(
        LocalArchiveTargetKind targetKind,
        LocalArchiveMutationTargetSuccess current,
        LocalArchiveStoredEvent? head)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (!IsDefined(targetKind) || !IsValidTargetFact(current))
            return false;
        if (current.Revision == 0)
            return head is null;
        if (head is null)
            return false;

        var expectedAction = current.State == LocalArchiveState.Archived
            ? LocalArchiveAction.Archive
            : LocalArchiveAction.Restore;
        return head.TargetKind == targetKind
            && string.Equals(head.TargetId, current.TargetId, StringComparison.Ordinal)
            && LocalRepositoryCatalogValidation.IsCanonicalUuidV7(head.EventId)
            && head.Action == expectedAction
            && head.PreviousRevision == current.Revision - 1
            && head.NewRevision == current.Revision
            && LocalRepositoryCatalogValidation.IsCanonicalTimestamp(head.OccurredAt)
            && string.Equals(head.OccurredAt, current.UpdatedAt, StringComparison.Ordinal)
            && (current.State != LocalArchiveState.Archived
                || string.Equals(head.OccurredAt, current.ArchivedAt, StringComparison.Ordinal));
    }

    internal static bool TryCopySuccessEntity(
        ReadOnlyMemory<byte> entity,
        out LocalArchiveMutationSucceeded succeeded)
    {
        if (entity.IsEmpty)
        {
            succeeded = null!;
            return false;
        }

        succeeded = new LocalArchiveMutationSucceeded(entity.ToArray());
        return true;
    }

    internal static bool IsValidTargetFact(LocalArchiveMutationTargetSuccess target)
    {
        if (!LocalRepositoryCatalogValidation.IsCanonicalUuidV7(target.TargetId))
            return false;

        return target.State switch
        {
            LocalArchiveState.Active when target.Revision == 0 =>
                target.ArchivedAt is null && target.UpdatedAt is null,
            LocalArchiveState.Active when target.Revision > 0 && target.Revision % 2 == 0 =>
                target.ArchivedAt is null
                && LocalRepositoryCatalogValidation.IsCanonicalTimestamp(target.UpdatedAt),
            LocalArchiveState.Archived when target.Revision > 0 && target.Revision % 2 == 1 =>
                LocalRepositoryCatalogValidation.IsCanonicalTimestamp(target.ArchivedAt)
                && string.Equals(target.ArchivedAt, target.UpdatedAt, StringComparison.Ordinal),
            _ => false,
        };
    }

    private static bool IsDefined(LocalArchiveAction value) =>
        value is LocalArchiveAction.Archive or LocalArchiveAction.Restore;

    private static bool IsDefined(LocalArchiveTargetKind value) =>
        value is LocalArchiveTargetKind.Session or LocalArchiveTargetKind.Repository;
}

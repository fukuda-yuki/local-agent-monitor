namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed record RetentionMutationTransitionEvaluation(
    RetentionMutationStageClassification Classification,
    string? Code,
    IReadOnlyList<RetentionItemLifecycle> StateSequence,
    int RevisionIncrementCount,
    RetentionMutationEffects Effects)
{
    public bool MutationAllowed => Classification is RetentionMutationStageClassification.PreviewStageAllowed or RetentionMutationStageClassification.CommitStageOutcome;
    public string? ResultCode => Code;
}

public static class RetentionMutationTransitions
{
    public static RetentionMutationTransitionEvaluation EvaluatePreview(
        RetentionMutationOperation operation,
        RetentionItemLifecycle state,
        DateTimeOffset now,
        DateTimeOffset? expiresAt = null)
    {
        _ = now;
        _ = expiresAt;
        var rejection = PreviewRejection(operation, state);
        return rejection is not null
            ? new(RetentionMutationStageClassification.PreviewStageRejection, rejection, [], 0, new(false, false, true, false))
            : new(RetentionMutationStageClassification.PreviewStageAllowed, null, [], 0, new(false, false, false, false));
    }

    public static RetentionMutationTransitionEvaluation EvaluateCommit(
        RetentionMutationOperation operation,
        RetentionMutationItemState item,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(item);
        var previewRejection = PreviewRejection(operation, item.State);
        if (previewRejection is not null)
            return new(RetentionMutationStageClassification.PreviewStageRejection, previewRejection, [], 0, new(false, false, true, false));

        return operation switch
        {
            RetentionMutationOperation.Pin => EvaluatePin(item, now),
            RetentionMutationOperation.Unpin => EvaluateUnpin(item, now),
            RetentionMutationOperation.DeleteNow => EvaluateDeleteNow(item),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }

    public static RetentionMutationTransitionEvaluation EvaluateCommit(
        RetentionMutationOperation operation,
        RetentionItemLifecycle state,
        DateTimeOffset now,
        DateTimeOffset expiresAt) =>
        EvaluateCommit(operation, new(state, now, expiresAt, RetentionV1Constants.RawDefaultPolicyId, 1, 0), now);

    private static RetentionMutationTransitionEvaluation EvaluatePin(RetentionMutationItemState item, DateTimeOffset now)
    {
        if (item.State == RetentionItemLifecycle.Expiring && item.ExpiresAt <= now)
            return new(RetentionMutationStageClassification.CommitStageRejection, RetentionMutationErrorCodes.PinExpired, [], 0, new(false, false, true, false));
        if (item.State == RetentionItemLifecycle.RetainedByPolicy)
            return new(RetentionMutationStageClassification.CommitStageOutcome, RetentionMutationCompletionCodes.PinNoop, [], 0, new(true, true, true, false));
        return new(RetentionMutationStageClassification.CommitStageOutcome, RetentionMutationCompletionCodes.PinApplied, [RetentionItemLifecycle.RetainedByPolicy], 1, new(true, true, true, true));
    }

    private static RetentionMutationTransitionEvaluation EvaluateUnpin(RetentionMutationItemState item, DateTimeOffset now)
    {
        var recalculatedExpiry = RetentionUnpinExpiryCalculator.Recalculate(item.CapturedAt, item.PolicyId, item.PolicyVersion);
        if (item.State == RetentionItemLifecycle.Expiring)
        {
            return recalculatedExpiry <= now
                ? Queued(RetentionMutationCompletionCodes.UnpinExpiredQueued, [RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued])
                : new(RetentionMutationStageClassification.CommitStageOutcome, RetentionMutationCompletionCodes.UnpinNoop, [], 0, new(true, true, true, false));
        }

        if (item.State == RetentionItemLifecycle.RetainedByPolicy)
        {
            return recalculatedExpiry <= now
                ? Queued(RetentionMutationCompletionCodes.UnpinExpiredQueued, [RetentionItemLifecycle.Expiring, RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued])
                : new(RetentionMutationStageClassification.CommitStageOutcome, RetentionMutationCompletionCodes.UnpinApplied, [RetentionItemLifecycle.Expiring], 1, new(true, true, true, true));
        }

        throw new ArgumentOutOfRangeException(nameof(item));
    }

    private static RetentionMutationTransitionEvaluation EvaluateDeleteNow(RetentionMutationItemState item) => item.State switch
    {
        RetentionItemLifecycle.Expiring => Queued(RetentionMutationCompletionCodes.DeleteQueued, [RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued]),
        RetentionItemLifecycle.RetainedByPolicy => Queued(RetentionMutationCompletionCodes.DeleteNowSupersededPin, [RetentionItemLifecycle.Expiring, RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued]),
        RetentionItemLifecycle.ExpiredPendingDeletion => Queued(RetentionMutationCompletionCodes.DeleteQueued, [RetentionItemLifecycle.DeletionQueued]),
        RetentionItemLifecycle.DeletionQueued => new(RetentionMutationStageClassification.CommitStageOutcome, RetentionMutationErrorCodes.DeleteAlreadyQueued, [], 0, new(true, true, true, false)),
        _ => throw new ArgumentOutOfRangeException(nameof(item))
    };

    private static RetentionMutationTransitionEvaluation Queued(string code, IReadOnlyList<RetentionItemLifecycle> sequence) =>
        new(RetentionMutationStageClassification.CommitStageOutcome, code, sequence, sequence.Count, new(true, true, true, true));

    private static string? PreviewRejection(RetentionMutationOperation operation, RetentionItemLifecycle state) => (operation, state) switch
    {
        (RetentionMutationOperation.Pin, RetentionItemLifecycle.ExpiredPendingDeletion or RetentionItemLifecycle.DeletionQueued or RetentionItemLifecycle.DeletionFailed) => RetentionMutationErrorCodes.PinReadDenied,
        (RetentionMutationOperation.Pin, RetentionItemLifecycle.Deleting) => RetentionMutationErrorCodes.PinDeleting,
        (RetentionMutationOperation.Pin, RetentionItemLifecycle.Deleted) => RetentionMutationErrorCodes.PinDeleted,
        (RetentionMutationOperation.Unpin, RetentionItemLifecycle.ExpiredPendingDeletion or RetentionItemLifecycle.DeletionQueued or RetentionItemLifecycle.DeletionFailed) => RetentionMutationErrorCodes.UnpinReadDenied,
        (RetentionMutationOperation.Unpin, RetentionItemLifecycle.Deleting) => RetentionMutationErrorCodes.UnpinDeleting,
        (RetentionMutationOperation.Unpin, RetentionItemLifecycle.Deleted) => RetentionMutationErrorCodes.UnpinDeleted,
        (RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.Deleting) => RetentionMutationErrorCodes.DeleteAlreadyDeleting,
        (RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.Deleted) => RetentionMutationErrorCodes.DeleteAlreadyDeleted,
        (RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.DeletionFailed) => RetentionMutationErrorCodes.DeleteFailed,
        _ => null
    };
}

public static class RetentionUnpinExpiryCalculator
{
    public static DateTimeOffset Recalculate(DateTimeOffset originalCapturedAt, string policyId, int policyVersion)
    {
        if (policyVersion != 1) throw new ArgumentException("Unsupported retention policy version.", nameof(policyVersion));
        var ttl = policyId switch
        {
            "raw-default-90d" => TimeSpan.FromDays(90),
            "sensitive-bundle-7d" => TimeSpan.FromDays(7),
            _ => throw new ArgumentException("Unsupported retention policy.", nameof(policyId))
        };
        return originalCapturedAt + ttl;
    }
}

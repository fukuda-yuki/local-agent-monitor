using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal sealed partial class RetentionMutationApplicationService
{
    internal RetentionMutationApplicationResult ExecuteMutation(
        RetentionMutationConfirmRequest? request,
        string? workflowKey)
    {
        if (!IsValidMutationRequest(request))
            return new(null, RetentionMutationErrorCodes.RequestInvalid);
        if (!RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey))
            return new(null, RetentionMutationErrorCodes.IdempotencyKeyInvalid);

        var canonicalRequest = RetentionMutationApplicationCanonicalization.MutationRequest(request!);
        var lookupRequest = new RetentionIdempotencyRequest(
            workflowKey!,
            RetentionMutationOperationStep.Mutation,
            canonicalRequest,
            FailureJson(RetentionMutationErrorCodes.RequestInvalid),
            null);
        using var connection = catalog.OpenMutationConnection();
        try
        {
            mutationCheckpoint?.Invoke("before_transaction");
        }
        catch
        {
            return new(null, RetentionMutationErrorCodes.MutationTransactionFailed);
        }
        using var transaction = catalog.BeginMutationTransaction(connection);
        try
        {
            var bindingByToken = catalog.ReadConfirmationBindingByTokenWithinTransaction(connection, transaction, request!.ConfirmationToken);
            if (bindingByToken is not null
                && !string.Equals(bindingByToken.WorkflowIdempotencyKey, workflowKey, StringComparison.Ordinal))
            {
                transaction.Rollback();
                return new(null, RetentionMutationErrorCodes.RequestInvalid);
            }

            var lookup = catalog.LookupIdempotencyWithinTransaction(connection, transaction, lookupRequest);
            if (lookup is not null)
            {
                var replay = ReplayOrFailure(lookup);
                if (lookup.Disposition == RetentionIdempotencyDisposition.Replayed && replay.Result is { } replayedResult)
                    catalog.MarkOperationReceiptReplayedWithinTransaction(connection, transaction, replayedResult.OperationId, timeProvider.GetUtcNow().ToUniversalTime());
                transaction.Commit();
                return replay;
            }

            var now = timeProvider.GetUtcNow().ToUniversalTime();
            var validation = catalog.ValidateConfirmationTokenWithinTransaction(connection, transaction, request.ConfirmationToken);
            var earlyEvaluation = RetentionMutationEvaluationOrder.Evaluate(new RetentionMutationEvaluationInput
            {
                TokenValid = validation.Disposition != RetentionConfirmationValidationDisposition.Invalid,
                TokenConsumed = validation.Disposition == RetentionConfirmationValidationDisposition.Consumed,
                TokenUnexpired = validation.Disposition != RetentionConfirmationValidationDisposition.Expired
            });
            if (!earlyEvaluation.Passed)
                return CommitFailure(connection, transaction, lookupRequest, earlyEvaluation.Code!);

            var binding = validation.Binding!;
            var storedPreview = catalog.ReadMutationPreviewWithinTransaction(connection, transaction, binding.PreviewId);
            var bindingMatches = storedPreview is not null
                && BindingMatches(request, binding, storedPreview.Response);
            if (!bindingMatches)
            {
                var bindingEvaluation = RetentionMutationEvaluationOrder.Evaluate(new RetentionMutationEvaluationInput { BindingMatches = false });
                return CommitFailure(connection, transaction, lookupRequest, bindingEvaluation.Code!);
            }

            var materialization = catalog.MaterializeMutationPreviewWithinTransaction(
                connection,
                transaction,
                new(binding.TargetKind, binding.TargetId),
                binding.Operation,
                binding.Scope,
                now);
            var currentProjection = materialization.Projection;
            var targetItems = currentProjection?.TargetItems ?? [];
            var itemIds = targetItems.Select(static item => item.ItemId).ToArray();
            RetentionMutationVersionVector? currentVector = null;
            if (materialization.Outcome == RetentionMutationPreviewProjectionOutcome.Ready && targetItems.Count > 0)
                currentVector = catalog.MaterializeMutationVersionVectorWithinTransaction(connection, transaction, itemIds);

            var targetSetMatches = currentVector is not null
                && string.Equals(binding.TargetItemSetDigest, currentVector.TargetItemSetDigest, StringComparison.Ordinal);
            var pinVectorMatches = targetSetMatches
                && TargetItemsMatchPinVector(storedPreview!.Response.TargetItems, targetItems);
            var retentionMatches = targetSetMatches
                && TargetItemsMatchRetention(storedPreview!.Response.TargetItems, targetItems);
            var conflictMatches = materialization.Outcome == RetentionMutationPreviewProjectionOutcome.Ready
                && string.Equals(binding.ConflictVersion, RetentionMutationDigests.ConflictVersion(materialization.ConflictSnapshot.Select(static item => new RetentionMutationConflictItem(item.ItemId, item.ConflictCode, item.LeaseGeneration))), StringComparison.Ordinal)
                && string.Equals(binding.ActiveConflictSnapshot, RetentionMutationApplicationCanonicalization.ConflictSnapshot(materialization.ConflictSnapshot.Select(static item => new RetentionMutationConflictItem(item.ItemId, item.ConflictCode, item.LeaseGeneration))), StringComparison.Ordinal);
            var versionMatches = currentVector is not null
                && string.Equals(binding.ExpectedStateVersion, currentVector.ExpectedStateVersion, StringComparison.Ordinal);
            var evaluation = RetentionMutationEvaluationOrder.Evaluate(new RetentionMutationEvaluationInput
            {
                TargetSetMatches = targetSetMatches,
                PinVectorMatches = pinVectorMatches,
                RetentionMatches = retentionMatches,
                ConflictMatches = conflictMatches,
                VersionMatches = versionMatches
            });
            if (!evaluation.Passed)
                return CommitFailure(connection, transaction, lookupRequest, evaluation.Code!);

            var transitions = targetItems
                .Select(item => (Item: item, Evaluation: RetentionMutationTransitions.EvaluateCommit(
                    binding.Operation,
                    new RetentionMutationItemState(
                        item.State,
                        item.CapturedAt ?? throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed),
                        item.ExpiresAt ?? throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed),
                        item.PolicyId,
                        item.PolicyVersion,
                        item.Revision),
                    now)))
                .ToArray();
            var transitionFailure = transitions.Select(static value => value.Evaluation).FirstOrDefault(static value => value.Classification != RetentionMutationStageClassification.CommitStageOutcome);
            if (transitionFailure is not null)
                return CommitFailure(connection, transaction, lookupRequest, transitionFailure.Code!);

            var operationId = operationIdGenerator();
            var completedAt = now;
            var finalItems = new List<RetentionPreviewItem>(transitions.Length);
            var stateUpdateIndex = 0;
            foreach (var transition in transitions)
            {
                finalItems.Add(ApplyTransition(connection, transaction, transition.Item, transition.Evaluation, binding.Operation, now,
                    () => $"state_update_{++stateUpdateIndex}"));
                mutationCheckpoint?.Invoke("state_mutated");
            }

            var consumed = catalog.TryConsumeConfirmationWithinTransaction(connection, transaction, request.ConfirmationToken);
            if (consumed.Disposition != RetentionConfirmationConsumptionDisposition.Consumed || consumed.Binding is null)
                throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed);
            mutationCheckpoint?.Invoke("token_consumed");
            catalog.SetConfirmationOperationIdWithinTransaction(connection, transaction, binding.ConfirmationId, operationId);

            var resultVector = catalog.MaterializeMutationVersionVectorWithinTransaction(connection, transaction, itemIds);
            var completionCode = ResolveCompletionCode(binding.Operation, transitions);
            var result = new RetentionMutationResult(
                RetentionMutationConstants.SchemaVersion,
                operationId,
                completionCode,
                binding.TargetKind,
                binding.TargetId,
                binding.Operation,
                binding.Scope,
                finalItems.Count,
                PinState(finalItems.Select(static item => item.State)),
                RetentionMutationLifecycleCounts.From(finalItems.Select(static item => item.State)),
                finalItems.Any(static item => item.ReadDeniedAt is not null),
                auditEventIdGenerator(),
                binding.ExpectedStateVersion,
                resultVector.ExpectedStateVersion,
                RetentionMutationConstants.BackupWarningCode,
                false,
                completedAt,
                completedAt);

            var auditEvent = new RetentionAuditEvent(
                result.AuditEventId!,
                result.OperationId,
                RetentionMutationConstants.EventType,
                binding.TargetKind,
                binding.TargetId,
                binding.TargetKind == RetentionMutationTargetKind.Session
                    ? binding.TargetId
                    : catalog.ReadSessionIdForItemWithinTransaction(connection, transaction, binding.TargetId),
                completedAt,
                RetentionMutationConstants.ActorLabel,
                binding.Operation,
                binding.ReasonCode,
                storedPreview!.Comment,
                PinState(storedPreview!.Response.TargetItems.Select(static item => item.State)),
                result.PinState,
                RetentionMutationLifecycleCounts.From(storedPreview.Response.TargetItems.Select(static item => item.State)),
                result.LifecycleCounts,
                workflowKey!,
                binding.ExpectedStateVersion,
                result.ResultVersion,
                binding.TargetItemSetDigest,
                completionCode,
                null);

            catalog.InsertOperationReceiptWithinTransaction(connection, transaction, result, binding.TargetItemSetDigest);
            mutationCheckpoint?.Invoke("receipt_written");
            catalog.AppendAuditEventWithinTransaction(connection, transaction, auditEvent);
            mutationCheckpoint?.Invoke("audit_written");
            var committedIdempotency = catalog.GetOrCreateIdempotencyWithinTransaction(
                connection,
                transaction,
                lookupRequest with { ResultJson = JsonSerializer.Serialize(result), CompletionCode = completionCode });
            if (committedIdempotency.Disposition != RetentionIdempotencyDisposition.Created)
                throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed);
            mutationCheckpoint?.Invoke("idempotency_written");
            transaction.Commit();
            return new(result, null);
        }
        catch (NotSupportedException)
        {
            transaction.Rollback();
            throw;
        }
        catch
        {
            try { transaction.Rollback(); } catch (InvalidOperationException) { }
            return new(null, RetentionMutationErrorCodes.MutationTransactionFailed);
        }
    }

    private RetentionMutationApplicationResult CommitFailure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionIdempotencyRequest request,
        string code)
    {
        var outcome = catalog.GetOrCreateIdempotencyWithinTransaction(
            connection,
            transaction,
            request with { ResultJson = FailureJson(code) });
        if (outcome.Disposition != RetentionIdempotencyDisposition.Created)
            throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed);
        transaction.Commit();
        return new(null, code);
    }

    private static RetentionMutationApplicationResult ReplayOrFailure(RetentionIdempotencyOutcome lookup)
    {
        if (lookup.Disposition == RetentionIdempotencyDisposition.Conflict)
            return new(null, RetentionMutationErrorCodes.IdempotencyConflict);
        if (lookup.Disposition == RetentionIdempotencyDisposition.Expired)
            return new(null, RetentionMutationErrorCodes.IdempotencyExpired);
        if (lookup.ResultJson is null)
            return new(null, RetentionMutationErrorCodes.MutationTransactionFailed, true);
        using var document = JsonDocument.Parse(lookup.ResultJson);
        if (document.RootElement.TryGetProperty("error_code", out var error))
            return new(null, error.GetString(), true);
        var result = JsonSerializer.Deserialize<RetentionMutationResult>(lookup.ResultJson)
            ?? throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed);
        return new(result with { ResultCode = RetentionMutationResultCodes.Replayed, IdempotentReplay = true }, null, true);
    }

    private static string FailureJson(string code) => JsonSerializer.Serialize(new { error_code = code });

    private static bool IsValidMutationRequest(RetentionMutationConfirmRequest? request) =>
        request is not null
        && !string.IsNullOrWhiteSpace(request.ConfirmationToken)
        && Enum.IsDefined(request.Operation)
        && Enum.IsDefined(request.Scope)
        && Enum.IsDefined(request.TargetKind)
        && RetentionMutationTargetValidator.Validate(new(request.TargetKind, request.TargetId)).IsValid
        && (request.TargetKind == RetentionMutationTargetKind.Session && request.Scope == RetentionMutationScope.SessionItems
            || request.TargetKind == RetentionMutationTargetKind.Item && request.Scope == RetentionMutationScope.SingleItem);

    private static bool BindingMatches(
        RetentionMutationConfirmRequest request,
        RetentionConfirmationBinding binding,
        RetentionMutationPreviewResponse preview) =>
        binding.Operation == request.Operation
        && binding.Scope == request.Scope
        && binding.TargetKind == request.TargetKind
        && string.Equals(binding.TargetId, request.TargetId, StringComparison.Ordinal)
        && preview.Operation == binding.Operation
        && preview.Scope == binding.Scope
        && preview.TargetKind == binding.TargetKind
        && string.Equals(preview.TargetId, binding.TargetId, StringComparison.Ordinal)
        && string.Equals(preview.PreviewDigest, binding.PreviewDigest, StringComparison.Ordinal);

    private static bool TargetItemsMatchPinVector(IReadOnlyList<RetentionPreviewItem> expected, IReadOnlyList<RetentionPreviewItem> current) =>
        expected.Count == current.Count
        && expected.OrderBy(static item => item.ItemId, StringComparer.Ordinal).Zip(current.OrderBy(static item => item.ItemId, StringComparer.Ordinal))
            .All(static pair => string.Equals(pair.First.ItemId, pair.Second.ItemId, StringComparison.Ordinal)
                && pair.First.PinState == pair.Second.PinState);

    private static bool TargetItemsMatchRetention(IReadOnlyList<RetentionPreviewItem> expected, IReadOnlyList<RetentionPreviewItem> current) =>
        expected.Count == current.Count
        && expected.OrderBy(static item => item.ItemId, StringComparer.Ordinal).Zip(current.OrderBy(static item => item.ItemId, StringComparer.Ordinal))
            .All(static pair => string.Equals(pair.First.ItemId, pair.Second.ItemId, StringComparison.Ordinal)
                && pair.First.CapturedAt == pair.Second.CapturedAt
                && pair.First.ExpiresAt == pair.Second.ExpiresAt
                && string.Equals(pair.First.PolicyId, pair.Second.PolicyId, StringComparison.Ordinal)
                && pair.First.PolicyVersion == pair.Second.PolicyVersion);

    private RetentionPreviewItem ApplyTransition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RetentionPreviewItem item,
        RetentionMutationTransitionEvaluation evaluation,
        RetentionMutationOperation operation,
        DateTimeOffset now,
        Func<string> stateUpdateCheckpoint)
    {
        var state = item.State;
        var expiry = item.ExpiresAt ?? throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed);
        var readDeniedAt = item.ReadDeniedAt;
        var queuedAt = item.QueuedAt;
        var revision = item.Revision;
        foreach (var next in evaluation.StateSequence)
        {
            var nextExpiry = state == RetentionItemLifecycle.RetainedByPolicy && next == RetentionItemLifecycle.Expiring
                ? RetentionUnpinExpiryCalculator.Recalculate(item.CapturedAt ?? throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed), item.PolicyId, item.PolicyVersion)
                : expiry;
            ExecuteTransition(connection, transaction, item.ItemId, revision, state, next, nextExpiry, operation, now);
            mutationCheckpoint?.Invoke(stateUpdateCheckpoint());
            if (next == RetentionItemLifecycle.ExpiredPendingDeletion)
                mutationCheckpoint?.Invoke("denial_write");
            state = next;
            expiry = nextExpiry;
            revision++;
            if (next == RetentionItemLifecycle.ExpiredPendingDeletion)
            {
                readDeniedAt = now;
                queuedAt = now;
            }
        }

        return item with
        {
            State = state,
            PinState = RetentionMutationStateProjection.PinState(state),
            DeleteState = RetentionMutationStateProjection.DeleteState(state),
            ExpiresAt = expiry,
            ReadDeniedAt = readDeniedAt,
            QueuedAt = queuedAt,
            Revision = revision
        };
    }

    private void ExecuteTransition(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        long revision,
        RetentionItemLifecycle from,
        RetentionItemLifecycle to,
        DateTimeOffset expiry,
        RetentionMutationOperation operation,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = (from, to) switch
        {
            (RetentionItemLifecycle.Expiring, RetentionItemLifecycle.RetainedByPolicy) => "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE item_id=$item AND revision=$revision AND state='expiring' AND read_denied_at IS NULL AND expires_at>$now;",
            (RetentionItemLifecycle.RetainedByPolicy, RetentionItemLifecycle.Expiring) => "UPDATE retention_items SET state='expiring',expires_at=$expires,revision=revision+1 WHERE item_id=$item AND revision=$revision AND state='retained_by_policy' AND read_denied_at IS NULL;",
            (RetentionItemLifecycle.Expiring, RetentionItemLifecycle.ExpiredPendingDeletion) when operation == RetentionMutationOperation.DeleteNow => "UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$now,queued_at=$now,revision=revision+1 WHERE item_id=$item AND revision=$revision AND state='expiring' AND read_denied_at IS NULL;",
            (RetentionItemLifecycle.Expiring, RetentionItemLifecycle.ExpiredPendingDeletion) => "UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$now,queued_at=$now,revision=revision+1 WHERE item_id=$item AND revision=$revision AND state='expiring' AND expires_at<=$now AND read_denied_at IS NULL;",
            (RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued) => "UPDATE retention_items SET state='deletion_queued',revision=revision+1,next_retry_at=NULL,error_code=NULL WHERE item_id=$item AND revision=$revision AND state='expired_pending_deletion' AND read_denied_at IS NOT NULL;",
            _ => throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed)
        };
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$now", Timestamp(now));
        command.Parameters.AddWithValue("$expires", Timestamp(expiry));
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed);
    }

    private static string Timestamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string ResolveCompletionCode(
        RetentionMutationOperation operation,
        IReadOnlyList<(RetentionPreviewItem Item, RetentionMutationTransitionEvaluation Evaluation)> transitions)
    {
        return operation switch
        {
            RetentionMutationOperation.Pin when transitions.Any(static value => value.Evaluation.StateSequence.Count > 0) => RetentionMutationCompletionCodes.PinApplied,
            RetentionMutationOperation.Pin => RetentionMutationCompletionCodes.PinNoop,
            RetentionMutationOperation.Unpin when transitions.Any(static value => value.Evaluation.Code == RetentionMutationCompletionCodes.UnpinExpiredQueued) => RetentionMutationCompletionCodes.UnpinExpiredQueued,
            RetentionMutationOperation.Unpin when transitions.Any(static value => value.Evaluation.StateSequence.Count > 0) => RetentionMutationCompletionCodes.UnpinApplied,
            RetentionMutationOperation.Unpin => RetentionMutationCompletionCodes.UnpinNoop,
            RetentionMutationOperation.DeleteNow when transitions.Any(static value => value.Evaluation.Code == RetentionMutationCompletionCodes.DeleteNowSupersededPin) => RetentionMutationCompletionCodes.DeleteNowSupersededPin,
            RetentionMutationOperation.DeleteNow when transitions.Any(static value => value.Evaluation.StateSequence.Count > 0) => RetentionMutationCompletionCodes.DeleteQueued,
            RetentionMutationOperation.DeleteNow => RetentionMutationCompletionCodes.DeleteAlreadyQueued,
            _ => throw new InvalidOperationException(RetentionMutationErrorCodes.MutationTransactionFailed)
        };
    }

    private static RetentionPinState PinState(IEnumerable<RetentionItemLifecycle> states)
    {
        var values = states.Select(RetentionMutationStateProjection.PinState).Distinct().ToArray();
        return values switch
        {
            [] => RetentionPinState.NotApplicable,
            [var value] => value!,
            _ => RetentionPinState.Mixed
        };
    }
}

internal static partial class RetentionMutationApplicationCanonicalization
{
    internal static string MutationRequest(RetentionMutationConfirmRequest request) =>
        RetentionMutationJcs.Canonicalize(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["confirmation_token"] = request.ConfirmationToken,
            ["operation"] = RetentionMutationWire.Operation(request.Operation),
            ["scope"] = RetentionMutationWire.Scope(request.Scope),
            ["target_kind"] = RetentionMutationWire.TargetKind(request.TargetKind),
            ["target_id"] = request.TargetId
        });
}

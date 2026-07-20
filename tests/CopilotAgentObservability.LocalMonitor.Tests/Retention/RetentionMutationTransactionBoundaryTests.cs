using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationTransactionBoundaryTests
{
    [Theory]
    [InlineData("state_update_1")]
    [InlineData("state_update_2")]
    [InlineData("state_update_3")]
    [InlineData("state_mutated")]
    [InlineData("denial_write")]
    [InlineData("token_consumed")]
    [InlineData("receipt_written")]
    [InlineData("audit_written")]
    [InlineData("idempotency_written")]
    public void ExecuteMutation_DeleteNowInjectedFailureLeavesNoDurablePartialStateAfterReopen(string failurePoint)
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        fixture.SetRetention("retained_by_policy", DateTimeOffset.Parse("2026-07-12T00:00:00.0000000+00:00"),
            DateTimeOffset.Parse("2026-08-12T00:00:00.0000000+00:00"), "sensitive-bundle-7d", 1);
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var failingApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == failurePoint) throw new InvalidOperationException("injected mutation statement failure");
        });

        var result = failingApplication.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationErrorCodes.MutationTransactionFailed, result.ErrorCode);
        AssertNoDurableMutationAfterReopen(fixture, confirmation.ConfirmationId, fixture.ItemIds,
            expectedState: "retained_by_policy", expectedRevision: 1);
    }

    [Theory]
    [InlineData("state_update_4")]
    [InlineData("state_update_5")]
    [InlineData("state_update_6")]
    public void ExecuteMutation_SessionInjectedFailureAtSecondItemWriteLeavesEveryItemUnchangedAfterReopen(string failurePoint)
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create(itemCount: 2);
        foreach (var itemId in fixture.ItemIds)
            fixture.SetRetentionFor(itemId, "retained_by_policy", DateTimeOffset.Parse("2026-07-12T00:00:00.0000000+00:00"),
                DateTimeOffset.Parse("2026-08-12T00:00:00.0000000+00:00"), "sensitive-bundle-7d", 1);
        var preview = fixture.CreatePreview(session: true, operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var failingApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == failurePoint) throw new InvalidOperationException("injected mutation statement failure");
        });

        var result = failingApplication.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SessionItems,
                RetentionMutationTargetKind.Session, fixture.SessionId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationErrorCodes.MutationTransactionFailed, result.ErrorCode);
        AssertNoDurableMutationAfterReopen(fixture, confirmation.ConfirmationId, fixture.ItemIds,
            expectedState: "retained_by_policy", expectedRevision: 1);
    }

    [Fact]
    public void ExecuteMutation_DeleteNowCommittedStateAndReceiptSurviveReopen()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);

        var committed = fixture.Application.ExecuteMutation(request, fixture.WorkflowKey(40));
        Assert.Null(committed.ErrorCode);
        var reopened = new RetentionCatalogStore(fixture.Path, fixture.Time);
        var replay = new RetentionMutationApplicationService(reopened, fixture.Time).ExecuteMutation(request, fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationResultCodes.Replayed, replay.Result!.ResultCode);
        Assert.True(replay.Result.IdempotentReplay);
        Assert.Equal(committed.Result!.OperationId, replay.Result.OperationId);
        Assert.Equal("deletion_queued", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(3L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
    }

    [Fact]
    public void ReadOperationStatus_ReturnsTheStoredCommittedReceipt()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);

        var committed = fixture.Application.ExecuteMutation(request, fixture.WorkflowKey(40));
        var result = Assert.IsType<RetentionMutationResult>(committed.Result);
        var status = fixture.Application.ReadOperationStatus(result.OperationId);

        Assert.Null(status.ErrorCode);
        var response = Assert.IsType<RetentionMutationStatusResponse>(status.Status);
        Assert.Equal(result.SchemaVersion, response.SchemaVersion);
        Assert.Equal(result.OperationId, response.OperationId);
        Assert.Equal(result.Operation, response.Operation);
        Assert.Equal(result.TargetKind, response.TargetKind);
        Assert.Equal(result.TargetId, response.TargetId);
        Assert.Equal(RetentionMutationResultStatus.Committed, response.Status);
        Assert.Equal(result.ResultCode, response.ResultCode);
        Assert.Equal(result.LifecycleCounts, response.LifecycleCounts);
        Assert.Equal(result.ReadDenied, response.ReadDenied);
        Assert.Equal(result.AuditEventId, response.AuditEventId);
        Assert.False(response.IdempotentReplay);
        Assert.Equal(result.CreatedAt, response.CreatedAt);
        Assert.Equal(result.CompletedAt, response.CompletedAt);
        Assert.Equal(result.BackupNonPurgeWarningCode, response.BackupNonPurgeWarningCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteMutation_SameTokenRaceReachesMutationPathConcurrentlyAndCommitsExactlyOnceForBothStartOrders(bool secondStartsFirst)
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);
        using var entryBarrier = new Barrier(2);
        var entryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var winnerStateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWinner = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entryCount = 0;
        var winnerApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == "before_transaction")
            {
                if (Interlocked.Increment(ref entryCount) == 2) entryReached.TrySetResult();
                if (!entryBarrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
            if (point == "state_update_1")
            {
                winnerStateReached.TrySetResult();
                releaseWinner.Task.GetAwaiter().GetResult();
            }
        });
        var loserApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == "before_transaction")
            {
                if (Interlocked.Increment(ref entryCount) == 2) entryReached.TrySetResult();
                if (!entryBarrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                winnerStateReached.Task.GetAwaiter().GetResult();
            }
        });

        var first = secondStartsFirst
            ? Task.Run(() => loserApplication.ExecuteMutation(request, fixture.WorkflowKey(40)))
            : Task.Run(() => winnerApplication.ExecuteMutation(request, fixture.WorkflowKey(40)));
        var second = secondStartsFirst
            ? Task.Run(() => winnerApplication.ExecuteMutation(request, fixture.WorkflowKey(40)))
            : Task.Run(() => loserApplication.ExecuteMutation(request, fixture.WorkflowKey(40)));
        await entryReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await winnerStateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseWinner.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(2, entryCount);
        Assert.Single(results, static result => result.ErrorCode is null && result.Result?.IdempotentReplay != true);
        Assert.Single(results, static result => result.Result?.IdempotentReplay == true);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteMutation_SameTokenDifferentWorkflowKeyRaceGetsRequestInvalidBeforeTokenConsumptionForBothStartOrders(bool mismatchedRequestStartsFirst)
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var validKey = fixture.WorkflowKey(40);
        var mismatchedKey = fixture.WorkflowKey(41);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);
        using var entryBarrier = new Barrier(2);
        var entryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var validStateReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseValid = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mismatchedFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entryCount = 0;
        var validApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == "before_transaction")
            {
                if (Interlocked.Increment(ref entryCount) == 2) entryReached.TrySetResult();
                if (!entryBarrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                if (mismatchedRequestStartsFirst) mismatchedFinished.Task.GetAwaiter().GetResult();
            }
            if (point == "state_update_1" && !mismatchedRequestStartsFirst)
            {
                validStateReached.TrySetResult();
                releaseValid.Task.GetAwaiter().GetResult();
            }
        });
        var mismatchedApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == "before_transaction")
            {
                if (Interlocked.Increment(ref entryCount) == 2) entryReached.TrySetResult();
                if (!entryBarrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                if (!mismatchedRequestStartsFirst) validStateReached.Task.GetAwaiter().GetResult();
            }
        });

        var mismatched = Task.Run(() =>
        {
            try { return mismatchedApplication.ExecuteMutation(request, mismatchedKey); }
            finally { mismatchedFinished.TrySetResult(); }
        });
        var valid = Task.Run(() => validApplication.ExecuteMutation(request, validKey));
        if (!mismatchedRequestStartsFirst)
        {
            await validStateReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            releaseValid.TrySetResult();
        }
        await entryReached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (mismatchedRequestStartsFirst) await mismatchedFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
        releaseValid.TrySetResult();
        var results = await Task.WhenAll(mismatched, valid);

        Assert.Equal(2, entryCount);
        Assert.Single(results, static result => result.ErrorCode is null);
        Assert.Single(results, static result => result.ErrorCode == RetentionMutationErrorCodes.RequestInvalid);
        Assert.Equal("deletion_queued", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(3L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
    }

    [Fact]
    public async Task ExecuteMutation_DifferentTokenRaceHasOneCasWinnerAndVersionChangedLoser()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        var request = fixture.Request() with { Operation = RetentionMutationOperation.DeleteNow };
        var firstKey = fixture.WorkflowKey(40);
        var secondKey = fixture.WorkflowKey(41);
        var firstPreview = Assert.IsType<RetentionMutationPreviewResponse>(fixture.Application.CreatePreview(request, firstKey).Preview);
        var secondPreview = Assert.IsType<RetentionMutationPreviewResponse>(fixture.Application.CreatePreview(request, secondKey).Preview);
        var firstConfirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(firstPreview.PreviewId, firstPreview.PreviewDigest), firstKey).Confirmation);
        var secondConfirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(secondPreview.PreviewId, secondPreview.PreviewDigest), secondKey).Confirmation);
        var firstRequest = new RetentionMutationConfirmRequest(firstConfirmation.ConfirmationToken,
            RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);
        var secondRequest = firstRequest with { ConfirmationToken = secondConfirmation.ConfirmationToken };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == "state_update_1" && Interlocked.Exchange(ref fixture.GateEntered, 1) == 0)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });

        var first = Task.Run(() => gatedApplication.ExecuteMutation(firstRequest, firstKey));
        await entered.Task;
        var second = Task.Run(() => fixture.Application.ExecuteMutation(secondRequest, secondKey));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, static result => result.ErrorCode is null);
        Assert.Single(results, static result => result.ErrorCode == RetentionMutationErrorCodes.ConfirmationVersionChanged);
        Assert.Equal("deletion_queued", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(3L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
    }

    private static void AssertNoDurableMutationAfterReopen(
        RetentionMutationConfirmationApplicationTests.Fixture fixture,
        string confirmationId,
        IReadOnlyList<string> itemIds,
        string expectedState = "expiring",
        long expectedRevision = 1)
    {
        var reopened = new RetentionCatalogStore(fixture.Path, fixture.Time);
        Assert.All(itemIds, itemId =>
        {
            Assert.Equal(expectedState, fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", itemId)));
            Assert.Equal(expectedRevision, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", itemId)));
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_items WHERE item_id=$item AND read_denied_at IS NOT NULL;", ("$item", itemId)));
        });
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
        Assert.NotNull(reopened.ReadConfirmationBinding(confirmationId));
    }
}

using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationTransactionBoundaryTests
{
    [Theory]
    [InlineData("state_update")]
    [InlineData("denial_write")]
    [InlineData("token_consumed")]
    [InlineData("receipt_written")]
    [InlineData("audit_written")]
    [InlineData("idempotency_written")]
    public void ExecuteMutation_DeleteNowInjectedFailureLeavesNoDurablePartialStateAfterReopen(string failurePoint)
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
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
        var reopened = new RetentionCatalogStore(fixture.Path, fixture.Time);
        Assert.Equal("expiring", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_items WHERE item_id=$item AND read_denied_at IS NOT NULL;", ("$item", fixture.ItemId)));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
        Assert.NotNull(reopened.ReadConfirmationBinding(confirmation.ConfirmationId));
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

    [Fact]
    public async Task ExecuteMutation_SameTokenRaceCommitsExactlyOnceAndReplaysTheLoser()
    {
        using var fixture = RetentionMutationConfirmationApplicationTests.Fixture.Create();
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedApplication = fixture.NewApplication(mutationCheckpoint: point =>
        {
            if (point == "state_update" && Interlocked.Exchange(ref fixture.GateEntered, 1) == 0)
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        });

        var first = Task.Run(() => gatedApplication.ExecuteMutation(request, fixture.WorkflowKey(40)));
        await entered.Task;
        var second = Task.Run(() => fixture.Application.ExecuteMutation(request, fixture.WorkflowKey(40)));
        release.TrySetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, static result => result.ErrorCode is null && result.Result?.IdempotentReplay != true);
        Assert.Single(results, static result => result.Result?.IdempotentReplay == true);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
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
            if (point == "state_update" && Interlocked.Exchange(ref fixture.GateEntered, 1) == 0)
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
}

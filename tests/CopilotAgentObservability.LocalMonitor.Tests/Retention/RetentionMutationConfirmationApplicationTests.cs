using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationConfirmationApplicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IssueConfirmation_ValidatesStoredPreviewAndKeepsTokenOutOfPersistedState()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();

        var result = fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest),
            fixture.WorkflowKey(40));

        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(result.Confirmation);
        Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh, result.Disposition);
        Assert.Null(result.ErrorCode);
        Assert.StartsWith("rcid1_", confirmation.ConfirmationId, StringComparison.Ordinal);
        Assert.StartsWith("rt90v1_", confirmation.ConfirmationToken, StringComparison.Ordinal);
        Assert.Equal(preview.ConfirmationExpiresAt, confirmation.ConfirmationExpiresAt);
        Assert.DoesNotContain(confirmation.ConfirmationToken, fixture.PersistedText(), StringComparison.Ordinal);
        Assert.DoesNotContain(confirmation.ConfirmationToken, fixture.Store.ReadConfirmationBinding(confirmation.ConfirmationId)!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void IssueConfirmation_RejectsDigestExpiryEmptyAndPreviewRejectionBeforeBinding()
    {
        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            var mismatch = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, "sha256-" + new string('f', 64)),
                fixture.WorkflowKey(40));
            Assert.Equal(RetentionMutationErrorCodes.PreviewDigestMismatch, mismatch.ErrorCode);
            Assert.Null(mismatch.Confirmation);
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings;"));
        }

        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            fixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);
            var expired = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey(40));
            Assert.Equal(RetentionMutationErrorCodes.PreviewExpired, expired.ErrorCode);
        }

        using (var fixture = Fixture.Create(itemCount: 0))
        {
            var preview = fixture.CreatePreview(session: true);
            var empty = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey(40));
            Assert.Equal(RetentionMutationErrorCodes.TargetEmpty, empty.ErrorCode);
        }

        using (var fixture = Fixture.Create())
        {
            fixture.SetState("deleting");
            var preview = fixture.CreatePreview();
            var rejected = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey(40));
            Assert.Equal(RetentionMutationErrorCodes.PinDeleting, rejected.ErrorCode);
        }
    }

    [Fact]
    public void IssueConfirmation_UsesStoredPreviewBindingValuesWithoutIssuanceDriftRecheck()
    {
        var cases = new Action<Fixture>[]
        {
            fixture => fixture.Execute(
                "UPDATE retention_items SET item_id='replacement-item' WHERE item_id=$item;",
                ("$item", fixture.ItemId)),
            fixture => fixture.Execute(
                "UPDATE retention_items SET state='retained_by_policy' WHERE item_id=$item;",
                ("$item", fixture.ItemId)),
            fixture => fixture.Execute(
                "UPDATE retention_items SET expires_at=$expires WHERE item_id=$item;",
                ("$item", fixture.ItemId), ("$expires", Now.AddDays(2).ToString("O", CultureInfo.InvariantCulture))),
            fixture => fixture.InsertReadLease(),
            fixture => fixture.Execute(
                "UPDATE retention_items SET revision=revision+1 WHERE item_id=$item;",
                ("$item", fixture.ItemId))
        };

        foreach (var mutate in cases)
        {
            using var fixture = Fixture.Create();
            var preview = fixture.CreatePreview();
            mutate(fixture);

            var result = fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest),
                fixture.WorkflowKey(40));

            Assert.Null(result.ErrorCode);
            var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(result.Confirmation);
            var binding = fixture.Store.ReadConfirmationBinding(confirmation.ConfirmationId);
            Assert.Equal(preview.PreviewDigest, binding!.PreviewDigest);
            Assert.Equal(preview.ExpectedStateVersion, binding.ExpectedStateVersion);
            Assert.Equal(preview.TargetItemSetDigest, binding.TargetItemSetDigest);
            Assert.Equal(fixture.WorkflowKey(40), binding.WorkflowIdempotencyKey);
        }
    }

    [Fact]
    public void IssueConfirmation_RejectsDifferentWorkflowKeyBeforePreviewOrBindingInspection()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();
        fixture.Execute(
            "UPDATE retention_mutation_previews SET preview_json='not-json' WHERE preview_id=$preview;",
            ("$preview", preview.PreviewId));

        var result = fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest),
            fixture.WorkflowKey(41));

        Assert.Equal(RetentionMutationErrorCodes.RequestInvalid, result.ErrorCode);
        Assert.Null(result.Confirmation);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings;"));
    }

    [Fact]
    public void IssueConfirmation_NotApplicablePreviewReturnsStoredRejectionBeforeEmptyState()
    {
        using var fixture = Fixture.Create();
        fixture.SetOwnershipReceiptToZero();
        var workflowKey = fixture.WorkflowKey(40);
        var preview = Assert.IsType<RetentionMutationPreviewResponse>(fixture.Application.CreatePreview(fixture.Request(), workflowKey).Preview);

        Assert.Equal(RetentionMutationErrorCodes.TargetNotApplicable, preview.RejectionCode);
        Assert.Equal(0, preview.TargetItemCount);

        var result = fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest),
            workflowKey);

        Assert.Equal(RetentionMutationErrorCodes.TargetNotApplicable, result.ErrorCode);
        Assert.Null(result.Confirmation);
    }

    [Fact]
    public void ExecuteMutation_PinAppliesAndCommitsReceiptAuditAndConsumption()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Null(result.ErrorCode);
        Assert.Equal(RetentionMutationCompletionCodes.PinApplied, result.Result!.ResultCode);
        Assert.Equal(2L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal("retained_by_policy", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(1, result.Result.LifecycleCounts.RetainedByPolicy);
        Assert.Equal(1, result.Result.LifecycleCounts.Expiring + result.Result.LifecycleCounts.RetainedByPolicy
            + result.Result.LifecycleCounts.ExpiredPendingDeletion + result.Result.LifecycleCounts.DeletionQueued
            + result.Result.LifecycleCounts.Deleting + result.Result.LifecycleCounts.Deleted + result.Result.LifecycleCounts.DeletionFailed);
    }

    [Fact]
    public void ExecuteMutation_PinNoopConsumesTokenAndAuditsWithoutRevisionChange()
    {
        using var fixture = Fixture.Create();
        fixture.SetState("retained_by_policy");
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationCompletionCodes.PinNoop, result.Result!.ResultCode);
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
    }

    [Fact]
    public void ExecuteMutation_PinAtExpiryRejectsWithoutStateChangeOrTokenConsumption()
    {
        using var fixture = Fixture.Create();
        fixture.SetExpiresAt(Now.AddMinutes(2));
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        fixture.Time.Advance(preview.TargetItems[0].ExpiresAt!.Value - Now);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationErrorCodes.PinExpired, result.ErrorCode);
        Assert.Null(result.Result);
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal("expiring", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
    }

    [Fact]
    public void ExecuteMutation_ReplayReturnsStoredResultWithoutSecondAudit()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);

        var first = fixture.Application.ExecuteMutation(request, fixture.WorkflowKey(40));
        var replay = fixture.Application.ExecuteMutation(request, fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationCompletionCodes.PinApplied, first.Result!.ResultCode);
        Assert.Equal(RetentionMutationResultCodes.Replayed, replay.Result!.ResultCode);
        Assert.True(replay.Result.IdempotentReplay);
        Assert.Equal(first.Result.OperationId, replay.Result.OperationId);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
    }

    [Fact]
    public void ReadOperationStatus_ReportsReplayedAfterReplayBookkeeping()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);

        var first = fixture.Application.ExecuteMutation(request, fixture.WorkflowKey(40));
        var replay = fixture.Application.ExecuteMutation(request, fixture.WorkflowKey(40));
        var status = fixture.Application.ReadOperationStatus(first.Result!.OperationId);

        Assert.Equal(RetentionMutationResultCodes.Replayed, replay.Result!.ResultCode);
        Assert.True(replay.Result.IdempotentReplay);
        var response = Assert.IsType<RetentionMutationStatusResponse>(status.Status);
        Assert.Equal(RetentionMutationResultStatus.Replayed, response.Status);
        Assert.Equal(RetentionMutationResultCodes.Replayed, response.ResultCode);
        Assert.True(response.IdempotentReplay);
        Assert.Equal(first.Result.OperationId, response.OperationId);
        Assert.NotNull(fixture.ScalarText("SELECT last_replayed_at FROM retention_operation_receipts WHERE operation_id=$operation;",
            ("$operation", first.Result.OperationId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
    }

    [Theory]
    [InlineData("invalid-token", RetentionMutationErrorCodes.ConfirmationInvalid)]
    [InlineData("consumed-token", RetentionMutationErrorCodes.ConfirmationConsumed)]
    [InlineData("expired-token", RetentionMutationErrorCodes.ConfirmationExpired)]
    [InlineData("binding-echo-mismatch", RetentionMutationErrorCodes.ConfirmationBindingMismatch)]
    [InlineData("retention-drift", RetentionMutationErrorCodes.ConfirmationRetentionChanged)]
    [InlineData("conflict-drift", RetentionMutationErrorCodes.ConfirmationConflictChanged)]
    public void ExecuteMutation_MutationTimeOrderChecksRunThroughApplicationWithoutStateMutation(string scenario, string expectedCode)
    {
        using var fixture = Fixture.Create();
        var workflowKey = fixture.WorkflowKey(40);
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), workflowKey).Confirmation);
        var request = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);

        switch (scenario)
        {
            case "invalid-token":
                request = request with { ConfirmationToken = "rt90v1_invalid" };
                break;
            case "consumed-token":
                Assert.Equal(RetentionConfirmationConsumptionDisposition.Consumed,
                    fixture.Store.ConsumeConfirmation(confirmation.ConfirmationToken).Disposition);
                break;
            case "expired-token":
                fixture.Time.Advance(RetentionMutationConstants.ConfirmationLifetime);
                break;
            case "binding-echo-mismatch":
                request = request with { Operation = RetentionMutationOperation.Unpin };
                break;
            case "retention-drift":
                fixture.SetRetentionBasis(Now.AddDays(-2), Now.AddDays(30), "raw-default-90d", 1);
                break;
            case "conflict-drift":
                fixture.InsertReadLease();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
        }

        var result = fixture.Application.ExecuteMutation(request, workflowKey);

        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.Null(result.Result);
        Assert.Equal("expiring", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(scenario == "consumed-token" ? 1L : 0L,
            fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
    }

    [Fact]
    public void ExecuteMutation_DriftReturnsFirstPinnedCheckWithoutMutation()
    {
        var cases = new (Action<Fixture> Mutate, string Code)[]
        {
            (fixture => fixture.Execute("UPDATE retention_items SET item_id='replacement-item' WHERE item_id=$item;", ("$item", fixture.ItemId)), RetentionMutationErrorCodes.ConfirmationTargetChanged),
            (fixture => fixture.SetState("retained_by_policy"), RetentionMutationErrorCodes.ConfirmationPinChanged),
            (fixture => fixture.Execute("UPDATE retention_items SET revision=revision+1 WHERE item_id=$item;", ("$item", fixture.ItemId)), RetentionMutationErrorCodes.ConfirmationVersionChanged)
        };

        foreach (var (mutate, code) in cases)
        {
            using var fixture = Fixture.Create();
            var preview = fixture.CreatePreview();
            var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
                new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
            mutate(fixture);

            var result = fixture.Application.ExecuteMutation(
                new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
                    RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

            Assert.Equal(code, result.ErrorCode);
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
            Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        }
    }

    [Fact]
    public void ExecuteMutation_RollbackLeavesStateAndTokenUnchanged()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var failingApplication = fixture.NewApplication(mutationCheckpoint: _ => throw new InvalidOperationException("injected"));

        var result = failingApplication.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationErrorCodes.MutationTransactionFailed, result.ErrorCode);
        Assert.Equal("expiring", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_confirmation_bindings WHERE consumed_at IS NOT NULL;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
    }

    [Theory]
    [InlineData("raw-default-90d", -1)]
    [InlineData("sensitive-bundle-7d", -1)]
    public void ExecuteMutation_UnpinRecalculatesExpiryFromCaptureAndPolicy(string policyId, int capturedDays)
    {
        using var fixture = Fixture.Create();
        var capturedAt = Now.AddDays(capturedDays);
        fixture.SetRetention("retained_by_policy", capturedAt, Now.AddDays(30), policyId, 1);
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.Unpin);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Unpin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        var expectedExpiry = policyId == "raw-default-90d" ? capturedAt.AddDays(90) : capturedAt.AddDays(7);
        Assert.Equal(RetentionMutationCompletionCodes.UnpinApplied, result.Result!.ResultCode);
        Assert.Equal(expectedExpiry, DateTimeOffset.Parse(fixture.ScalarText("SELECT expires_at FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId))!, CultureInfo.InvariantCulture));
        Assert.Equal("expiring", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(2L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
    }

    [Fact]
    public void ExecuteMutation_UnpinFutureExpiringIsNoop()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.Unpin);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Unpin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationCompletionCodes.UnpinNoop, result.Result!.ResultCode);
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
    }

    [Fact]
    public void ExecuteMutation_UnpinRetainedExpiredRecalculatesThenQueuesWithThreeRevisions()
    {
        using var fixture = Fixture.Create();
        fixture.SetRetention("retained_by_policy", Now.AddDays(-8), Now.AddDays(30), "sensitive-bundle-7d", 1);
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.Unpin);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Unpin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationCompletionCodes.UnpinExpiredQueued, result.Result!.ResultCode);
        Assert.Equal("deletion_queued", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(4L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.NotNull(fixture.ScalarText("SELECT read_denied_at FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(RetentionMutationDigests.ExpectedStateVersion([
            new RetentionMutationExpectedStateItem(fixture.ItemId, 4, RetentionPinState.Unpinned, RetentionItemLifecycle.DeletionQueued)
        ]), result.Result.ResultVersion);
        Assert.Equal(1, result.Result.LifecycleCounts.DeletionQueued);
        Assert.Equal(0, result.Result.LifecycleCounts.Expiring + result.Result.LifecycleCounts.RetainedByPolicy
            + result.Result.LifecycleCounts.ExpiredPendingDeletion);
        var audit = fixture.Store.ReadAuditEvents(new(RetentionMutationTargetKind.Item, fixture.ItemId)).Single();
        Assert.Equal(1, audit.PreviousOperationState.RetainedByPolicy);
        Assert.Equal(1, audit.NewOperationState.DeletionQueued);
        Assert.Equal(1, audit.NewOperationState.Expiring + audit.NewOperationState.RetainedByPolicy
            + audit.NewOperationState.ExpiredPendingDeletion + audit.NewOperationState.DeletionQueued
            + audit.NewOperationState.Deleting + audit.NewOperationState.Deleted + audit.NewOperationState.DeletionFailed);
    }

    [Fact]
    public void ExecuteMutation_UnpinExpiringAtNowQueuesWithTwoRevisions()
    {
        using var fixture = Fixture.Create();
        fixture.SetExpiresAt(Now);
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.Unpin);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Unpin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Equal(RetentionMutationCompletionCodes.UnpinExpiredQueued, result.Result!.ResultCode);
        Assert.Equal("deletion_queued", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(3L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1, result.Result.LifecycleCounts.DeletionQueued + result.Result.LifecycleCounts.Expiring
            + result.Result.LifecycleCounts.RetainedByPolicy + result.Result.LifecycleCounts.ExpiredPendingDeletion
            + result.Result.LifecycleCounts.Deleting + result.Result.LifecycleCounts.Deleted + result.Result.LifecycleCounts.DeletionFailed);
    }

    [Theory]
    [InlineData("expiring", 3, RetentionMutationCompletionCodes.DeleteQueued)]
    [InlineData("retained_by_policy", 4, RetentionMutationCompletionCodes.DeleteNowSupersededPin)]
    [InlineData("expired_pending_deletion", 2, RetentionMutationCompletionCodes.DeleteQueued)]
    [InlineData("deletion_queued", 1, RetentionMutationCompletionCodes.DeleteAlreadyQueued)]
    public void ExecuteMutation_DeleteNowFollowsThePinnedCommitRows(string state, long expectedRevision, string completionCode)
    {
        using var fixture = Fixture.Create();
        if (state == "retained_by_policy")
            fixture.SetRetention(state, Now.AddDays(-8), Now.AddDays(30), "sensitive-bundle-7d", 1);
        else if (state is "expired_pending_deletion" or "deletion_queued")
            fixture.SetDeniedState(state);

        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        Assert.True(preview.MutationAllowed);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Null(result.ErrorCode);
        Assert.Equal(completionCode, result.Result!.ResultCode);
        Assert.Equal("deletion_queued", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(expectedRevision, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.NotNull(fixture.ScalarText("SELECT read_denied_at FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_operation_receipts;"));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
        Assert.Equal(result.Result.OperationId, fixture.Store.ReadAuditEvents(new(RetentionMutationTargetKind.Item, fixture.ItemId)).Single().OperationId);
        Assert.Equal(result.Result.ResultVersion, RetentionMutationDigests.ExpectedStateVersion([
            new RetentionMutationExpectedStateItem(fixture.ItemId, expectedRevision, RetentionPinState.Unpinned, RetentionItemLifecycle.DeletionQueued)
        ]));
    }

    [Fact]
    public async Task ExecuteMutation_DeleteNowDeniesReadAccessBeforeAnyWorkerDeletion()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview(operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(40));

        Assert.Null(result.ErrorCode);
        var key = fixture.ItemOwnershipKey();
        Assert.Null(await fixture.Store.TryAcquireAsync(key, 3, RetentionLeaseKind.Access, Now, CancellationToken.None));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM session_event_content WHERE event_id=$event;", ("$event", key.SourceItemId)));
    }

    [Fact]
    public async Task DeleteNowQueue_IsClaimedAndProcessedByTheExistingWorkerLikeExpiryQueue()
    {
        using var fixture = Fixture.Create(itemCount: 2);
        var preview = fixture.CreatePreview(itemIndex: 0, operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemIds[0]), fixture.WorkflowKey(40));
        Assert.Null(result.ErrorCode);
        fixture.SetExpiresAtFor(fixture.ItemIds[1], Now);
        fixture.Execute("DELETE FROM retention_adapter_coverage; INSERT INTO retention_adapter_coverage(store_kind,coverage_version) VALUES ('session_event_content',1),('raw_record',1),('analysis_run_raw',1),('sensitive_bundle',1),('analysis_sdk_directory',1);");

        var adapter = new Fixture.GatedAdapter(RetentionStoreKind.SessionEventContent);
        var registry = new RetentionAdapterRegistry([
            adapter,
            new Fixture.GatedAdapter(RetentionStoreKind.RawRecord),
            new Fixture.GatedAdapter(RetentionStoreKind.AnalysisRunRaw),
            new Fixture.GatedAdapter(RetentionStoreKind.SensitiveBundle),
            new Fixture.GatedAdapter(RetentionStoreKind.AnalysisSdkDirectory)
        ]);
        var worker = new RetentionCleanupWorker(new RetentionCleanupCoordinator(fixture.Store, registry, fixture.Time), fixture.Time);
        var run = worker.RunOnceAsync(CancellationToken.None).AsTask();
        await adapter.Entered.Task;
        adapter.Release.TrySetResult();
        await run;

        Assert.Equal(2, adapter.Contexts.Count);
        Assert.All(fixture.ItemIds, item => Assert.Equal("deleted", fixture.ScalarText("SELECT state FROM retention_items WHERE item_id=$item;", ("$item", item))));
        Assert.All(fixture.ItemIds, item => Assert.Equal(5L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", item))));
    }

    [Fact]
    public void ExecuteMutation_DeleteNowSessionMixedStatesCommitsExactBeforeAndAfterCounts()
    {
        using var fixture = Fixture.Create(itemCount: 4);
        fixture.SetStateFor(fixture.ItemIds[0], "expiring");
        fixture.SetRetentionFor(fixture.ItemIds[1], "retained_by_policy", Now.AddDays(-8), Now.AddDays(30), "sensitive-bundle-7d", 1);
        fixture.SetDeniedStateFor(fixture.ItemIds[2], "expired_pending_deletion");
        fixture.SetDeniedStateFor(fixture.ItemIds[3], "deletion_queued");
        var preview = fixture.CreatePreview(session: true, operation: RetentionMutationOperation.DeleteNow);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.DeleteNow, RetentionMutationScope.SessionItems,
                RetentionMutationTargetKind.Session, fixture.SessionId), fixture.WorkflowKey(40));

        Assert.Null(result.ErrorCode);
        Assert.Equal(RetentionMutationCompletionCodes.DeleteNowSupersededPin, result.Result!.ResultCode);
        Assert.Equal(4, result.Result.LifecycleCounts.DeletionQueued);
        Assert.Equal(0, result.Result.LifecycleCounts.Expiring + result.Result.LifecycleCounts.RetainedByPolicy
            + result.Result.LifecycleCounts.ExpiredPendingDeletion + result.Result.LifecycleCounts.Deleting
            + result.Result.LifecycleCounts.Deleted + result.Result.LifecycleCounts.DeletionFailed);
        var audit = fixture.Store.ReadAuditEvents(new(RetentionMutationTargetKind.Session, fixture.SessionId)).Single();
        Assert.Equal(1, audit.PreviousOperationState.Expiring);
        Assert.Equal(1, audit.PreviousOperationState.RetainedByPolicy);
        Assert.Equal(1, audit.PreviousOperationState.ExpiredPendingDeletion);
        Assert.Equal(1, audit.PreviousOperationState.DeletionQueued);
        Assert.Equal(4, audit.NewOperationState.DeletionQueued);
        Assert.Equal(0, audit.NewOperationState.Expiring + audit.NewOperationState.RetainedByPolicy
            + audit.NewOperationState.ExpiredPendingDeletion + audit.NewOperationState.Deleting
            + audit.NewOperationState.Deleted + audit.NewOperationState.DeletionFailed);
        Assert.Equal(3L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemIds[0])));
        Assert.Equal(4L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemIds[1])));
        Assert.Equal(2L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemIds[2])));
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemIds[3])));
    }

    [Fact]
    public void ExecuteMutation_AuditPreservesNormalizedReasonAndComment()
    {
        using var fixture = Fixture.Create();
        var workflowKey = fixture.WorkflowKey(40);
        var preview = fixture.CreatePreview(comment: "  needs review  ");
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), workflowKey).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), workflowKey);

        Assert.Null(result.ErrorCode);
        var audit = fixture.Store.ReadAuditEvents(new(RetentionMutationTargetKind.Item, fixture.ItemId)).Single();
        Assert.Equal(RetentionMutationReasonCodes.ResearchNeeded, audit.ReasonCode);
        Assert.Equal("  needs review  ", audit.Comment);
        Assert.Equal(workflowKey, audit.RequestIdempotencyKey);
        Assert.Equal(fixture.SessionId, audit.SessionId);
    }

    [Fact]
    public void ExecuteMutation_SessionAppliesMixedPinTransitionsAtomically()
    {
        using var fixture = Fixture.Create(itemCount: 2);
        fixture.SetStateFor(fixture.ItemIds[0], "retained_by_policy");
        var workflowKey = fixture.WorkflowKey(40);
        var preview = fixture.CreatePreview(session: true);
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), workflowKey).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SessionItems,
                RetentionMutationTargetKind.Session, fixture.SessionId), workflowKey);

        Assert.Equal(RetentionMutationCompletionCodes.PinApplied, result.Result!.ResultCode);
        Assert.Equal(1L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemIds[0])));
        Assert.Equal(2L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemIds[1])));
        Assert.Equal(2, result.Result.LifecycleCounts.RetainedByPolicy);
        Assert.Equal(2, result.Result.TargetItemCount);
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
    }

    [Fact]
    public void ExecuteMutation_CrossStepKeyMismatchPrecedesMutationIdempotency()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);

        var result = fixture.Application.ExecuteMutation(
            new(confirmation.ConfirmationToken, RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem,
                RetentionMutationTargetKind.Item, fixture.ItemId), fixture.WorkflowKey(41));

        Assert.Equal(RetentionMutationErrorCodes.RequestInvalid, result.ErrorCode);
        Assert.Equal(0L, fixture.Scalar("SELECT COUNT(*) FROM retention_mutation_idempotency WHERE step='mutation';"));
    }

    [Fact]
    public void ExecuteMutation_DifferentRequestWithSameKeyConflictsAfterCommittedRequest()
    {
        using var fixture = Fixture.Create();
        var preview = fixture.CreatePreview();
        var confirmation = Assert.IsType<RetentionConfirmationIssueResponse>(fixture.Application.IssueConfirmation(
            new(preview.PreviewId, preview.PreviewDigest), fixture.WorkflowKey(40)).Confirmation);
        var key = fixture.WorkflowKey(40);
        var first = new RetentionMutationConfirmRequest(confirmation.ConfirmationToken,
            RetentionMutationOperation.Pin, RetentionMutationScope.SingleItem, RetentionMutationTargetKind.Item, fixture.ItemId);
        Assert.Null(fixture.Application.ExecuteMutation(first, key).ErrorCode);

        var conflict = fixture.Application.ExecuteMutation(first with { Operation = RetentionMutationOperation.Unpin }, key);

        Assert.Equal(RetentionMutationErrorCodes.IdempotencyConflict, conflict.ErrorCode);
        Assert.Equal(2L, fixture.Scalar("SELECT revision FROM retention_items WHERE item_id=$item;", ("$item", fixture.ItemId)));
        Assert.Equal(1L, fixture.Scalar("SELECT COUNT(*) FROM retention_audit_events;"));
    }

    [Fact]
    public void IssueConfirmation_SurfacesFreshReissueConsumedLinkageConflictAndNonceCollision()
    {
        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            var request = new RetentionConfirmationIssueRequest(preview.PreviewId, preview.PreviewDigest);
            var key = fixture.WorkflowKey(40);
            var fresh = fixture.Application.IssueConfirmation(request, key);
            var reissued = fixture.Application.IssueConfirmation(request, key);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh, fresh.Disposition);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.ReissuedAfterInvalidation, reissued.Disposition);

            var mutation = fixture.Application.ExecuteMutation(
                new(
                    reissued.Confirmation!.ConfirmationToken,
                    RetentionMutationOperation.Pin,
                    RetentionMutationScope.SingleItem,
                    RetentionMutationTargetKind.Item,
                    fixture.ItemId),
                key);
            Assert.NotNull(mutation.Result);
            var linkage = fixture.Application.IssueConfirmation(request, key);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.ConsumedLinkage, linkage.Disposition);
            Assert.Equal(RetentionMutationErrorCodes.ConfirmationConsumed, linkage.ErrorCode);
            Assert.Equal(mutation.Result.OperationId, linkage.OperationId);
            Assert.Null(linkage.Confirmation);
        }

        using (var fixture = Fixture.Create(itemCount: 2))
        {
            var first = fixture.CreatePreview(itemIndex: 0);
            var second = fixture.CreatePreview(itemIndex: 1);
            var key = fixture.WorkflowKey(40);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh,
                fixture.Application.IssueConfirmation(new(first.PreviewId, first.PreviewDigest), key).Disposition);
            var conflict = fixture.Application.IssueConfirmation(new(second.PreviewId, second.PreviewDigest), key);
            Assert.Equal(RetentionMutationErrorCodes.RequestInvalid, conflict.ErrorCode);
            Assert.Null(conflict.Confirmation);
        }

        using (var fixture = Fixture.Create())
        {
            var preview = fixture.CreatePreview();
            var token = fixture.Token(31, 32);
            var application = fixture.NewApplication(
                tokenGenerator: () => token,
                confirmationIdGenerator: () => fixture.ConfirmationId(33));
            var request = new RetentionConfirmationIssueRequest(preview.PreviewId, preview.PreviewDigest);
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.IssuedFresh,
                application.IssueConfirmation(request, fixture.WorkflowKey(40)).Disposition);
            var collision = application.IssueConfirmation(request, fixture.WorkflowKey(40));
            Assert.Equal(RetentionConfirmationIssuePersistenceDisposition.GenerationFailed, collision.Disposition);
            Assert.Equal(RetentionMutationErrorCodes.ConfirmationGenerationFailed, collision.ErrorCode);
        }
    }

    internal sealed class Fixture : IDisposable
    {
        private byte issuanceEntropy = 1;

        private Fixture(string path, MutableTimeProvider time, RetentionCatalogStore store, string sessionId, string itemId, IReadOnlyList<string> itemIds)
        {
            Path = path;
            Time = time;
            Store = store;
            SessionId = sessionId;
            ItemId = itemId;
            ItemIds = itemIds;
            Application = new RetentionMutationApplicationService(
                store,
                time,
                confirmationIdGenerator: () => ConfirmationId(issuanceEntropy++),
                tokenGenerator: () =>
                {
                    var nonce = issuanceEntropy++;
                    return Token(nonce, (byte)(nonce + 1));
                });
        }

        internal string Path { get; }
        internal MutableTimeProvider Time { get; }
        internal RetentionCatalogStore Store { get; }
        internal string SessionId { get; }
        internal string ItemId { get; }
        internal IReadOnlyList<string> ItemIds { get; }
        internal RetentionMutationApplicationService Application { get; }
        internal int GateEntered;

        internal static Fixture Create(int itemCount = 1)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"retention-confirmation-application-{Guid.NewGuid():N}.sqlite");
            var time = new MutableTimeProvider(Now);
            var context = RetentionCatalogContext.InitializeNewOwnedDatabase(path, time);
            var sessionStore = new SqliteSessionStore(path, context, time);
            sessionStore.CreateSchema();
            var sessionId = Guid.Parse("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071");
            var session = new ObservedSession(sessionId, ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
                Now.AddMinutes(-1), Now, Now, SessionRawRetentionState.Expiring, Now.AddMinutes(-1), Now);
            var events = Enumerable.Range(0, itemCount).Select(index => new ObservedSessionEvent(
                Guid.Parse($"018f2b4e-7c1a-7f1a-8a2b-{index + 0x6072:X12}"), sessionId, null,
                SessionSourceSurface.CopilotSdk, null, $"trace-{index}", "received", "copilot-sdk-stream",
                $"event-{index}", "user.message", Now.AddSeconds(index), SessionContentState.Available)).ToArray();
            var content = events.Select((item, index) => new SessionEventContent(item.EventId, "application/json",
                $"{{\"index\":{index}}}", Now.AddSeconds(index), Now.AddDays(90).AddSeconds(index))).ToArray();
            sessionStore.Write(new(new(session, [], [], events), content));
            var store = new RetentionCatalogStore(context, time);
            var ids = itemCount == 0 ? Array.Empty<string>() : ReadItems(path);
            return new(path, time, store, sessionId.ToString("D"), ids.FirstOrDefault() ?? "missing-item", ids);
        }

        internal RetentionMutationPreviewResponse CreatePreview(bool session = false, int itemIndex = 0, RetentionMutationOperation operation = RetentionMutationOperation.Pin, string? comment = null)
        {
            var request = new RetentionMutationPreviewRequest(
                session ? new(RetentionMutationTargetKind.Session, SessionId) : new(RetentionMutationTargetKind.Item, ItemIds[itemIndex]),
                operation,
                session ? RetentionMutationScope.SessionItems : RetentionMutationScope.SingleItem,
                RetentionMutationReasonCodes.ResearchNeeded,
                comment);
            return Assert.IsType<RetentionMutationPreviewResponse>(Application.CreatePreview(request, WorkflowKey((byte)(40 + itemIndex))).Preview);
        }

        internal RetentionMutationPreviewRequest Request() => new(
            new(RetentionMutationTargetKind.Item, ItemId),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SingleItem,
            RetentionMutationReasonCodes.ResearchNeeded,
            null);

        internal RetentionMutationApplicationService NewApplication(
            Func<string>? tokenGenerator = null,
            Func<string>? confirmationIdGenerator = null,
            Action<string>? mutationCheckpoint = null) =>
            new(Store, Time, previewIdGenerator: () => RetentionMutationIdentifiers.CreatePreviewId(Enumerable.Repeat((byte)50, 16).ToArray()),
                confirmationIdGenerator: confirmationIdGenerator, tokenGenerator: tokenGenerator, mutationCheckpoint: mutationCheckpoint);

        internal string WorkflowKey(byte value) => RetentionMutationIdentifiers.CreateWorkflowKey(Enumerable.Repeat(value, 32).ToArray());

        internal string ConfirmationId(byte value) => RetentionMutationIdentifiers.CreateConfirmationId(Enumerable.Repeat(value, 16).ToArray());

        internal string Token(byte nonce, byte secret) => RetentionMutationToken.Create(
            Enumerable.Repeat(nonce, 16).ToArray(), Enumerable.Repeat(secret, 32).ToArray());

        internal void SetState(string state) => Execute("UPDATE retention_items SET state=$state WHERE item_id=$item;", ("$state", state), ("$item", ItemId));

        internal void SetStateFor(string itemId, string state) => Execute("UPDATE retention_items SET state=$state WHERE item_id=$item;", ("$state", state), ("$item", itemId));

        internal void SetDeniedState(string state) => SetDeniedStateFor(ItemId, state);

        internal void SetDeniedStateFor(string itemId, string state) => Execute(
            "UPDATE retention_items SET state=$state,read_denied_at=$now,queued_at=$now WHERE item_id=$item;",
            ("$state", state), ("$now", Now.ToString("O", CultureInfo.InvariantCulture)), ("$item", itemId));

        internal void SetExpiresAt(DateTimeOffset expiresAt) => Execute(
            "UPDATE retention_items SET expires_at=$expires WHERE item_id=$item;",
            ("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture)), ("$item", ItemId));

        internal void SetExpiresAtFor(string itemId, DateTimeOffset expiresAt) => Execute(
            "UPDATE retention_items SET expires_at=$expires WHERE item_id=$item;",
            ("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture)), ("$item", itemId));

        internal void SetRetention(string state, DateTimeOffset capturedAt, DateTimeOffset expiresAt, string policyId, int policyVersion) => Execute(
            "UPDATE retention_items SET state=$state,captured_at=$captured,expires_at=$expires,policy_id=$policy,policy_version=$version WHERE item_id=$item;",
            ("$state", state), ("$captured", capturedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture)), ("$policy", policyId),
            ("$version", policyVersion), ("$item", ItemId));

        internal void SetRetentionFor(string itemId, string state, DateTimeOffset capturedAt, DateTimeOffset expiresAt, string policyId, int policyVersion) => Execute(
            "UPDATE retention_items SET state=$state,captured_at=$captured,expires_at=$expires,policy_id=$policy,policy_version=$version WHERE item_id=$item;",
            ("$state", state), ("$captured", capturedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture)), ("$policy", policyId),
            ("$version", policyVersion), ("$item", itemId));

        internal void SetRetentionBasis(DateTimeOffset capturedAt, DateTimeOffset expiresAt, string policyId, int policyVersion) => Execute(
            "UPDATE retention_items SET captured_at=$captured,expires_at=$expires,policy_id=$policy,policy_version=$version WHERE item_id=$item;",
            ("$captured", capturedAt.ToString("O", CultureInfo.InvariantCulture)),
            ("$expires", expiresAt.ToString("O", CultureInfo.InvariantCulture)), ("$policy", policyId),
            ("$version", policyVersion), ("$item", ItemId));

        internal RetentionOwnershipKey ItemOwnershipKey()
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT source_item_id FROM retention_items WHERE item_id=$item;";
            command.Parameters.AddWithValue("$item", ItemId);
            return new(Store.StoreInstanceId, RetentionStoreKind.SessionEventContent, (string)command.ExecuteScalar()!);
        }

        internal void SetOwnershipReceiptToZero() => Execute("UPDATE retention_items SET ownership_receipt=zeroblob(32) WHERE item_id=$item;", ("$item", ItemId));

        internal void InsertReadLease() => Execute(
            "INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($item,'access','owner',$expires,99);",
            ("$item", ItemId), ("$expires", Now.AddMinutes(1).ToString("O", CultureInfo.InvariantCulture)));

        internal string PersistedText()
        {
            using var connection = Open(Path);
            using var tables = connection.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';";
            var names = new List<string>();
            using (var reader = tables.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
            var values = new StringBuilder();
            foreach (var table in names)
            {
                using var columns = connection.CreateCommand();
                columns.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\");";
                var columnNames = new List<string>();
                using (var reader = columns.ExecuteReader()) while (reader.Read()) columnNames.Add(reader.GetString(1));
                foreach (var column in columnNames)
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT \"{column.Replace("\"", "\"\"")}\" FROM \"{table.Replace("\"", "\"\"")}\";";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) if (!reader.IsDBNull(0) && reader.GetValue(0) is string value) values.Append(value);
                }
            }
            return values.ToString();
        }

        internal long Scalar(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        internal string? ScalarText(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            return command.ExecuteScalar() as string;
        }

        internal void Execute(string sql, params (string Name, object Value)[] parameters)
        {
            using var connection = Open(Path);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in new[] { Path, Path + "-wal", Path + "-shm" }) if (File.Exists(file)) File.Delete(file);
        }

        internal sealed class GatedAdapter(RetentionStoreKind kind) : IRetentionDeletionAdapter
        {
            internal readonly List<RetentionDeleteContext> Contexts = [];
            internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private int calls;

            public RetentionStoreKind StoreKind => kind;

            public async ValueTask<RetentionAdapterResult> DeleteAsync(RetentionDeleteContext context)
            {
                lock (Contexts) Contexts.Add(context);
                if (Interlocked.Increment(ref calls) == 1)
                {
                    Entered.TrySetResult();
                    await Release.Task.ConfigureAwait(false);
                }
                return RetentionAdapterResult.Deleted;
            }
        }

        private static string[] ReadItems(string path)
        {
            using var connection = Open(path);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT item_id FROM retention_items WHERE store_kind='session_event_content' ORDER BY item_id;";
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(reader.GetString(0));
            return values.ToArray();
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }
    }
}

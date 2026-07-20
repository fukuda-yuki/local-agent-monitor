using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;

namespace CopilotAgentObservability.LocalMonitor.Tests.Retention;

public sealed class RetentionMutationContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CapturedAt = new(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TargetValidation_UsesClosedKindsAndExactScopePairing()
    {
        var sessionId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";
        Assert.True(RetentionMutationTargetValidator.Validate(new(RetentionMutationTargetKind.Session, sessionId)).IsValid);
        Assert.True(RetentionMutationTargetValidator.Validate(new(RetentionMutationTargetKind.Item, "opaque-item-0001")).IsValid);
        Assert.False(RetentionMutationTargetValidator.Validate(new(RetentionMutationTargetKind.Session, sessionId.ToUpperInvariant())).IsValid);
        Assert.False(RetentionMutationTargetValidator.Validate(new(RetentionMutationTargetKind.Session, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071-extra")).IsValid);
        Assert.False(RetentionMutationTargetValidator.Validate(new(RetentionMutationTargetKind.Item, " ")).IsValid);
        Assert.False(RetentionMutationRequestValidator.Validate(new(
            new(RetentionMutationTargetKind.Session, sessionId),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SingleItem,
            RetentionMutationReasonCodes.ResearchNeeded,
            null)).IsValid);
        Assert.False(RetentionMutationRequestValidator.Validate(new(
            new(RetentionMutationTargetKind.Item, "opaque-item-0001"),
            RetentionMutationOperation.Pin,
            RetentionMutationScope.SessionItems,
            RetentionMutationReasonCodes.ResearchNeeded,
            null)).IsValid);

        var opaque = new RetentionMutationTarget(RetentionMutationTargetKind.Item, "Opaque/ID?kept-byte-for-byte");
        Assert.Equal("Opaque/ID?kept-byte-for-byte", RetentionMutationTargetValidator.NormalizeOpaqueItemId(opaque));
    }

    [Fact]
    public void LifecycleAndDerivedStateModels_UseTheExistingSevenStates()
    {
        Assert.Equal(
            [
                RetentionItemLifecycle.Expiring,
                RetentionItemLifecycle.RetainedByPolicy,
                RetentionItemLifecycle.ExpiredPendingDeletion,
                RetentionItemLifecycle.DeletionQueued,
                RetentionItemLifecycle.Deleting,
                RetentionItemLifecycle.Deleted,
                RetentionItemLifecycle.DeletionFailed
            ],
            RetentionMutationLifecycleStates.All);

        Assert.Equal(RetentionPinState.Pinned, RetentionMutationStateProjection.PinState(RetentionItemLifecycle.RetainedByPolicy));
        Assert.All(
            new[]
            {
                RetentionItemLifecycle.Expiring,
                RetentionItemLifecycle.ExpiredPendingDeletion,
                RetentionItemLifecycle.DeletionQueued,
                RetentionItemLifecycle.Deleting,
                RetentionItemLifecycle.Deleted,
                RetentionItemLifecycle.DeletionFailed
            },
            state => Assert.Equal(RetentionPinState.Unpinned, RetentionMutationStateProjection.PinState(state)));
        Assert.Equal(RetentionDeleteState.NotRequested, RetentionMutationStateProjection.DeleteState(RetentionItemLifecycle.Expiring));
        Assert.Equal(RetentionDeleteState.NotRequested, RetentionMutationStateProjection.DeleteState(RetentionItemLifecycle.RetainedByPolicy));
        Assert.Equal(RetentionDeleteState.Queued, RetentionMutationStateProjection.DeleteState(RetentionItemLifecycle.ExpiredPendingDeletion));
        Assert.Equal(RetentionDeleteState.Queued, RetentionMutationStateProjection.DeleteState(RetentionItemLifecycle.DeletionQueued));
        Assert.Equal(RetentionDeleteState.InProgress, RetentionMutationStateProjection.DeleteState(RetentionItemLifecycle.Deleting));
        Assert.Equal(RetentionDeleteState.Deleted, RetentionMutationStateProjection.DeleteState(RetentionItemLifecycle.Deleted));
        Assert.Equal(RetentionDeleteState.Failed, RetentionMutationStateProjection.DeleteState(RetentionItemLifecycle.DeletionFailed));
    }

    [Theory]
    [MemberData(nameof(PreviewRejectionRows))]
    public void PreviewTransitionMatrix_ReturnsPinnedRejectionAndEffects(
        RetentionMutationOperation operation,
        RetentionItemLifecycle state,
        string code)
    {
        var result = RetentionMutationTransitions.EvaluatePreview(operation, state, Now, Now.AddDays(1));

        Assert.Equal(RetentionMutationStageClassification.PreviewStageRejection, result.Classification);
        Assert.Equal(code, result.Code);
        Assert.Empty(result.StateSequence);
        Assert.Equal(0, result.RevisionIncrementCount);
        Assert.Equal(new(false, false, true, false), result.Effects);
    }

    public static IEnumerable<object[]> PreviewRejectionRows() =>
    [
        [RetentionMutationOperation.Pin, RetentionItemLifecycle.ExpiredPendingDeletion, RetentionMutationErrorCodes.PinReadDenied],
        [RetentionMutationOperation.Pin, RetentionItemLifecycle.DeletionQueued, RetentionMutationErrorCodes.PinReadDenied],
        [RetentionMutationOperation.Pin, RetentionItemLifecycle.DeletionFailed, RetentionMutationErrorCodes.PinReadDenied],
        [RetentionMutationOperation.Pin, RetentionItemLifecycle.Deleting, RetentionMutationErrorCodes.PinDeleting],
        [RetentionMutationOperation.Pin, RetentionItemLifecycle.Deleted, RetentionMutationErrorCodes.PinDeleted],
        [RetentionMutationOperation.Unpin, RetentionItemLifecycle.ExpiredPendingDeletion, RetentionMutationErrorCodes.UnpinReadDenied],
        [RetentionMutationOperation.Unpin, RetentionItemLifecycle.DeletionQueued, RetentionMutationErrorCodes.UnpinReadDenied],
        [RetentionMutationOperation.Unpin, RetentionItemLifecycle.DeletionFailed, RetentionMutationErrorCodes.UnpinReadDenied],
        [RetentionMutationOperation.Unpin, RetentionItemLifecycle.Deleting, RetentionMutationErrorCodes.UnpinDeleting],
        [RetentionMutationOperation.Unpin, RetentionItemLifecycle.Deleted, RetentionMutationErrorCodes.UnpinDeleted],
        [RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.Deleting, RetentionMutationErrorCodes.DeleteAlreadyDeleting],
        [RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.Deleted, RetentionMutationErrorCodes.DeleteAlreadyDeleted],
        [RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.DeletionFailed, RetentionMutationErrorCodes.DeleteFailed]
    ];

    [Theory]
    [MemberData(nameof(CommitRows))]
    public void CommitTransitionMatrix_ReturnsExactSequenceRevisionAndEffects(
        RetentionMutationOperation operation,
        RetentionItemLifecycle state,
        DateTimeOffset expiresAt,
        string code,
        RetentionItemLifecycle[] expectedSequence,
        int revisions,
        RetentionMutationStageClassification classification,
        RetentionMutationEffects effects)
    {
        var capturedAt = operation == RetentionMutationOperation.Unpin && expiresAt > Now
            ? Now.AddDays(-30)
            : CapturedAt;
        var item = new RetentionMutationItemState(state, capturedAt, expiresAt, "raw-default-90d", 1, 7);
        var result = RetentionMutationTransitions.EvaluateCommit(operation, item, Now);

        Assert.Equal(classification, result.Classification);
        Assert.Equal(code, result.Code);
        Assert.Equal(expectedSequence, result.StateSequence);
        Assert.Equal(revisions, result.RevisionIncrementCount);
        Assert.Equal(effects, result.Effects);
    }

    public static IEnumerable<object[]> CommitRows()
    {
        var committed = new RetentionMutationEffects(true, true, true, true);
        var noop = new RetentionMutationEffects(true, true, true, false);
        var rejection = new RetentionMutationEffects(false, false, true, false);
        yield return [RetentionMutationOperation.Pin, RetentionItemLifecycle.Expiring, Now.AddDays(1), RetentionMutationCompletionCodes.PinApplied, new[] { RetentionItemLifecycle.RetainedByPolicy }, 1, RetentionMutationStageClassification.CommitStageOutcome, committed];
        yield return [RetentionMutationOperation.Pin, RetentionItemLifecycle.RetainedByPolicy, Now.AddDays(1), RetentionMutationCompletionCodes.PinNoop, Array.Empty<RetentionItemLifecycle>(), 0, RetentionMutationStageClassification.CommitStageOutcome, noop];
        yield return [RetentionMutationOperation.Pin, RetentionItemLifecycle.Expiring, Now, RetentionMutationErrorCodes.PinExpired, Array.Empty<RetentionItemLifecycle>(), 0, RetentionMutationStageClassification.CommitStageRejection, rejection];
        yield return [RetentionMutationOperation.Unpin, RetentionItemLifecycle.RetainedByPolicy, Now.AddDays(1), RetentionMutationCompletionCodes.UnpinApplied, new[] { RetentionItemLifecycle.Expiring }, 1, RetentionMutationStageClassification.CommitStageOutcome, committed];
        yield return [RetentionMutationOperation.Unpin, RetentionItemLifecycle.RetainedByPolicy, Now.AddDays(-1), RetentionMutationCompletionCodes.UnpinExpiredQueued, new[] { RetentionItemLifecycle.Expiring, RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued }, 3, RetentionMutationStageClassification.CommitStageOutcome, committed];
        yield return [RetentionMutationOperation.Unpin, RetentionItemLifecycle.Expiring, Now.AddDays(1), RetentionMutationCompletionCodes.UnpinNoop, Array.Empty<RetentionItemLifecycle>(), 0, RetentionMutationStageClassification.CommitStageOutcome, noop];
        yield return [RetentionMutationOperation.Unpin, RetentionItemLifecycle.Expiring, Now, RetentionMutationCompletionCodes.UnpinExpiredQueued, new[] { RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued }, 2, RetentionMutationStageClassification.CommitStageOutcome, committed];
        yield return [RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.Expiring, Now.AddDays(1), RetentionMutationCompletionCodes.DeleteQueued, new[] { RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued }, 2, RetentionMutationStageClassification.CommitStageOutcome, committed];
        yield return [RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.RetainedByPolicy, Now.AddDays(1), RetentionMutationCompletionCodes.DeleteNowSupersededPin, new[] { RetentionItemLifecycle.Expiring, RetentionItemLifecycle.ExpiredPendingDeletion, RetentionItemLifecycle.DeletionQueued }, 3, RetentionMutationStageClassification.CommitStageOutcome, committed];
        yield return [RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.ExpiredPendingDeletion, Now.AddDays(1), RetentionMutationCompletionCodes.DeleteQueued, new[] { RetentionItemLifecycle.DeletionQueued }, 1, RetentionMutationStageClassification.CommitStageOutcome, committed];
        yield return [RetentionMutationOperation.DeleteNow, RetentionItemLifecycle.DeletionQueued, Now.AddDays(1), RetentionMutationErrorCodes.DeleteAlreadyQueued, Array.Empty<RetentionItemLifecycle>(), 0, RetentionMutationStageClassification.CommitStageOutcome, noop];
    }

    [Fact]
    public void UnpinExpiry_UsesOriginalCaptureAndRecordedPolicyVersionAtBoundary()
    {
        Assert.Equal(CapturedAt.AddDays(90), RetentionUnpinExpiryCalculator.Recalculate(CapturedAt, "raw-default-90d", 1));
        Assert.Equal(CapturedAt.AddDays(7), RetentionUnpinExpiryCalculator.Recalculate(CapturedAt, "sensitive-bundle-7d", 1));
        Assert.Equal(
            RetentionMutationCompletionCodes.UnpinExpiredQueued,
            RetentionMutationTransitions.EvaluateCommit(
                RetentionMutationOperation.Unpin,
                new(RetentionItemLifecycle.Expiring, CapturedAt, CapturedAt.AddDays(90), "raw-default-90d", 1, 4),
                CapturedAt.AddDays(90)).Code);
        Assert.Equal(
            RetentionMutationCompletionCodes.UnpinApplied,
            RetentionMutationTransitions.EvaluateCommit(
                RetentionMutationOperation.Unpin,
                new(RetentionItemLifecycle.RetainedByPolicy, CapturedAt, CapturedAt.AddDays(7), "sensitive-bundle-7d", 1, 4),
                CapturedAt.AddDays(7).AddTicks(-1)).Code);
        Assert.Throws<ArgumentException>(() => RetentionUnpinExpiryCalculator.Recalculate(CapturedAt, "raw-default-90d", 2));
    }

    [Fact]
    public void UnpinExpiry_UsesSensitiveBundleSevenDayBoundary()
    {
        var expiry = CapturedAt.AddDays(7);
        var item = new RetentionMutationItemState(
            RetentionItemLifecycle.RetainedByPolicy,
            CapturedAt,
            CapturedAt.AddDays(90),
            "sensitive-bundle-7d",
            1,
            4);

        Assert.Equal(
            RetentionMutationCompletionCodes.UnpinExpiredQueued,
            RetentionMutationTransitions.EvaluateCommit(RetentionMutationOperation.Unpin, item, expiry).Code);
        var expired = RetentionMutationTransitions.EvaluateCommit(RetentionMutationOperation.Unpin, item, expiry);
        Assert.Equal(
            [
                RetentionItemLifecycle.Expiring,
                RetentionItemLifecycle.ExpiredPendingDeletion,
                RetentionItemLifecycle.DeletionQueued
            ],
            expired.StateSequence);
        Assert.Equal(new(true, true, true, true), expired.Effects);
        Assert.Equal(
            RetentionMutationCompletionCodes.UnpinApplied,
            RetentionMutationTransitions.EvaluateCommit(RetentionMutationOperation.Unpin, item, expiry.AddTicks(-1)).Code);
    }

    [Fact]
    public void Digests_UseStableJcsOrderingAndExactStateBearingInputs()
    {
        var first = new[]
        {
            new RetentionMutationDigestItem("z-item", RetentionStoreKind.RawRecord),
            new RetentionMutationDigestItem("a-item", RetentionStoreKind.SessionEventContent)
        };
        var second = first.Reverse().ToArray();
        var firstDigest = RetentionMutationDigests.TargetItemSetDigest(first);
        Assert.Equal(firstDigest, RetentionMutationDigests.TargetItemSetDigest(second));
        Assert.StartsWith("sha256-", firstDigest);
        Assert.Equal(
            "[{\"item_id\":\"a-item\",\"store_kind\":\"session_event_content\"},{\"item_id\":\"z-item\",\"store_kind\":\"raw_record\"}]",
            RetentionMutationDigests.TargetItemSetCanonicalJson(first));
        Assert.Equal("sha256-44c3b233431f7f8fe6ffa45593431a62775c4c1c4499a40eed3598ec0eaa15f8", firstDigest);

        var states = new[] { new RetentionMutationExpectedStateItem("a-item", 1, RetentionPinState.Unpinned, RetentionItemLifecycle.Expiring) };
        var expected = RetentionMutationDigests.ExpectedStateVersion(states);
        Assert.StartsWith("v1-", expected);
        Assert.Equal(expected, RetentionMutationDigests.ExpectedStateVersion(states.Reverse().ToArray()));
        Assert.NotEqual(expected, RetentionMutationDigests.ExpectedStateVersion([states[0] with { State = RetentionItemLifecycle.RetainedByPolicy }]));

        var conflicts = new[] { new RetentionMutationConflictItem("a-item", "active_read_lease", 1) };
        Assert.StartsWith("v1-", RetentionMutationDigests.ConflictVersion(conflicts));

        var digestInput = CreatePreviewDigestInput();
        var digestA = RetentionMutationDigests.PreviewDigest(digestInput);
        Assert.StartsWith("sha256-", digestA);
        Assert.Equal(digestA, RetentionMutationDigests.PreviewDigest(digestInput));
        Assert.NotEqual(digestA, RetentionMutationDigests.PreviewDigest(digestInput with { RejectionCode = RetentionMutationErrorCodes.PinDeleted }));
        Assert.NotEqual(digestA, RetentionMutationDigests.PreviewDigest(digestInput with
        {
            CurrentState = digestInput.CurrentState with { ReadableItemCount = digestInput.CurrentState.ReadableItemCount + 1 }
        }));
    }

    [Fact]
    public void PreviewDigest_UsesEveryTypedPinnedFieldAndExposesNoExcludedFields()
    {
        var input = CreatePreviewDigestInput();
        var baseline = RetentionMutationDigests.PreviewDigest(input);
        var changes = new (string Name, Func<RetentionPreviewDigestInput, RetentionPreviewDigestInput> Change)[]
        {
            (nameof(RetentionPreviewDigestInput.SchemaVersion), value => value with { SchemaVersion = 2 }),
            (nameof(RetentionPreviewDigestInput.Result), value => value with { Result = RetentionMutationPreviewResult.EmptyNotApplicable }),
            (nameof(RetentionPreviewDigestInput.EmptyReason), value => value with { EmptyReason = RetentionMutationEmptyReason.AllCandidatesExcluded }),
            (nameof(RetentionPreviewDigestInput.MutationAllowed), value => value with { MutationAllowed = false }),
            (nameof(RetentionPreviewDigestInput.TargetKind), value => value with { TargetKind = RetentionMutationTargetKind.Item }),
            (nameof(RetentionPreviewDigestInput.TargetId), value => value with { TargetId = "item-2" }),
            (nameof(RetentionPreviewDigestInput.Operation), value => value with { Operation = RetentionMutationOperation.Unpin }),
            (nameof(RetentionPreviewDigestInput.Scope), value => value with { Scope = RetentionMutationScope.SingleItem }),
            (nameof(RetentionPreviewDigestInput.SourceState), value => value with { SourceState = RetentionMutationSourceState.Unknown }),
            (nameof(RetentionPreviewDigestInput.SessionCompleteness), value => value with { SessionCompleteness = RetentionMutationSessionCompleteness.Partial }),
            (nameof(RetentionPreviewDigestInput.ContentState), value => value with { ContentState = "expired_pending_deletion" }),
            (nameof(RetentionPreviewDigestInput.CurrentState), value => value with { CurrentState = value.CurrentState with { ReadDeniedItemCount = 2 } }),
            (nameof(RetentionPreviewDigestInput.TargetItems), value => value with { TargetItems = [value.TargetItems[0] with { ItemId = "item-2" }] }),
            (nameof(RetentionPreviewDigestInput.TargetItemCount), value => value with { TargetItemCount = 2 }),
            (nameof(RetentionPreviewDigestInput.StoreKindSummary), value => value with { StoreKindSummary = [value.StoreKindSummary[0] with { ItemCount = 2 }] }),
            (nameof(RetentionPreviewDigestInput.ExcludedItemCount), value => value with { ExcludedItemCount = 2 }),
            (nameof(RetentionPreviewDigestInput.ExcludedItemsByReason), value => value with { ExcludedItemsByReason = [value.ExcludedItemsByReason[0] with { ItemCount = 2 }] }),
            (nameof(RetentionPreviewDigestInput.CaptureExpiryPolicySummary), value => value with { CaptureExpiryPolicySummary = [value.CaptureExpiryPolicySummary[0] with { ItemCount = 2 }] }),
            (nameof(RetentionPreviewDigestInput.RetainedMetadataImpact), value => value with { RetainedMetadataImpact = value.RetainedMetadataImpact with { SafeSummaryRetainedCount = 3 } }),
            (nameof(RetentionPreviewDigestInput.ActiveCleanupExclusionConflicts), value => value with { ActiveCleanupExclusionConflicts = [value.ActiveCleanupExclusionConflicts[0] with { ItemCount = 2 }] }),
            (nameof(RetentionPreviewDigestInput.BackupNonPurgeWarningCode), value => value with { BackupNonPurgeWarningCode = "retention_backup_not_purged_changed" }),
            (nameof(RetentionPreviewDigestInput.RejectionCode), value => value with { RejectionCode = RetentionMutationErrorCodes.PinDeleted }),
            (nameof(RetentionPreviewDigestInput.ExpectedStateVersion), value => value with { ExpectedStateVersion = "v1-changed" }),
            (nameof(RetentionPreviewDigestInput.TargetItemSetDigest), value => value with { TargetItemSetDigest = "sha256-changed" })
        };

        Assert.Equal(24, changes.Length);
        foreach (var (name, change) in changes)
            Assert.True(baseline != RetentionMutationDigests.PreviewDigest(change(input)), name);

        Assert.Equal(
            [
                "SchemaVersion", "Result", "EmptyReason", "MutationAllowed", "TargetKind", "TargetId",
                "Operation", "Scope", "SourceState", "SessionCompleteness", "ContentState", "CurrentState",
                "TargetItems", "TargetItemCount", "StoreKindSummary", "ExcludedItemCount", "ExcludedItemsByReason",
                "CaptureExpiryPolicySummary", "RetainedMetadataImpact", "ActiveCleanupExclusionConflicts",
                "BackupNonPurgeWarningCode", "RejectionCode", "ExpectedStateVersion", "TargetItemSetDigest"
            ],
            typeof(RetentionPreviewDigestInput).GetProperties().Select(static property => property.Name));
        var properties = typeof(RetentionPreviewDigestInput).GetProperties().Select(static property => property.Name).ToArray();
        Assert.DoesNotContain("PreviewId", properties);
        Assert.DoesNotContain("PreviewDigest", properties);
        Assert.DoesNotContain("ConfirmationExpiresAt", properties);
        Assert.DoesNotContain("Comment", properties);
        Assert.DoesNotContain("Comments", properties);
    }

    [Fact]
    public void Identifiers_UsePinnedBase64UrlFormatsAndRejectNonCanonicalVectors()
    {
        var nonce = Enumerable.Range(0, 16).Select(static i => (byte)i).ToArray();
        var secret = Enumerable.Range(16, 32).Select(static i => (byte)i).ToArray();
        var previewId = RetentionMutationIdentifiers.CreatePreviewId(nonce);
        var confirmationId = RetentionMutationIdentifiers.CreateConfirmationId(nonce);
        var auditId = RetentionMutationIdentifiers.CreateAuditEventId(nonce);
        var cursor = RetentionMutationIdentifiers.CreateHistoryCursor(nonce);
        var workflowKey = RetentionMutationIdentifiers.CreateWorkflowKey(secret);
        var token = RetentionMutationToken.Create(nonce, secret);

        Assert.Equal(RetentionMutationIdentifierFormats.PreviewIdLength, previewId.Length);
        Assert.Equal(28, confirmationId.Length);
        Assert.Equal(27, auditId.Length);
        Assert.Equal(27, cursor.Length);
        Assert.Equal(48, workflowKey.Length);
        Assert.Equal(73, token.Length);
        Assert.StartsWith(RetentionMutationIdentifierFormats.PreviewIdPrefix, previewId);
        Assert.StartsWith(RetentionMutationIdentifierFormats.ConfirmationIdPrefix, confirmationId);
        Assert.StartsWith(RetentionMutationIdentifierFormats.AuditEventIdPrefix, auditId);
        Assert.StartsWith(RetentionMutationIdentifierFormats.HistoryCursorPrefix, cursor);
        Assert.StartsWith(RetentionMutationIdentifierFormats.WorkflowKeyPrefix, workflowKey);
        Assert.True(
            token.StartsWith(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, StringComparison.Ordinal),
            "Generated confirmation material was not canonical.");

        Assert.True(RetentionMutationIdentifiers.TryParsePreviewId(previewId, out var parsedNonce));
        Assert.Equal(nonce, parsedNonce);
        Assert.True(RetentionMutationIdentifiers.TryParseConfirmationId(confirmationId, out var parsedConfirmationNonce));
        Assert.Equal(nonce, parsedConfirmationNonce);
        Assert.True(RetentionMutationIdentifiers.TryParseAuditEventId(auditId, out var parsedAuditNonce));
        Assert.Equal(nonce, parsedAuditNonce);
        Assert.True(RetentionMutationIdentifiers.TryParseHistoryCursor(cursor, out var parsedCursorNonce));
        Assert.Equal(nonce, parsedCursorNonce);
        Assert.True(RetentionMutationToken.TryParse(token, out var parts));
        Assert.Equal(nonce, parts.Nonce);
        Assert.Equal(secret, parts.Secret);
        Assert.True(RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey));
        Assert.False(RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey + "x"));
        Assert.False(RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey[..^1] + "="));
        Assert.False(RetentionMutationIdentifiers.TryParsePreviewId(previewId + "=", out _));
        Assert.False(RetentionMutationToken.TryParse(token.Replace('_', '-'), out _));
        Assert.Throws<ArgumentException>(() => RetentionMutationIdentifiers.CreatePreviewId(new byte[15]));
        Assert.Throws<ArgumentException>(() => RetentionMutationToken.Create(nonce, new byte[31]));
    }

    [Fact]
    public void Identifiers_RejectWrongPrefixLengthPaddingAlphabetAndNonAsciiVectors()
    {
        var nonce = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var secret = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var previewId = RetentionMutationIdentifiers.CreatePreviewId(nonce);
        var workflowKey = RetentionMutationIdentifiers.CreateWorkflowKey(secret);
        var token = RetentionMutationToken.Create(nonce, secret);

        Assert.False(RetentionMutationIdentifiers.TryParsePreviewId("wrong_" + previewId[RetentionMutationIdentifierFormats.PreviewIdPrefix.Length..], out _));
        Assert.False(RetentionMutationIdentifiers.TryParsePreviewId(previewId[..^1], out _));
        Assert.False(RetentionMutationIdentifiers.TryParsePreviewId(previewId[..^1] + "=", out _));
        Assert.False(RetentionMutationIdentifiers.TryParsePreviewId(previewId[..^1] + "!", out _));
        Assert.False(RetentionMutationIdentifiers.TryParsePreviewId(previewId + "é", out _));

        Assert.False(RetentionMutationIdentifiers.IsValidWorkflowKey("wrong_" + workflowKey[RetentionMutationIdentifierFormats.WorkflowKeyPrefix.Length..]));
        Assert.False(RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey[..^1]));
        Assert.False(RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey[..^1] + "="));
        Assert.False(RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey[..^1] + "!"));
        Assert.False(RetentionMutationIdentifiers.IsValidWorkflowKey(workflowKey + "é"));

        Assert.False(RetentionMutationToken.TryParse("wrong_" + token[RetentionMutationIdentifierFormats.ConfirmationTokenPrefix.Length..], out _));
        Assert.False(RetentionMutationToken.TryParse(token[..^1], out _));
        Assert.False(RetentionMutationToken.TryParse(token[..^1] + "=", out _));
        Assert.False(RetentionMutationToken.TryParse(token[..^1] + "!", out _));
        Assert.False(RetentionMutationToken.TryParse(token + "é", out _));
    }

    [Fact]
    public void ConfirmationToken_ParsesBase64UrlUnderscoreInsideNonce()
    {
        var nonce = Enumerable.Repeat((byte)0xff, RetentionMutationIdentifierFormats.NonceByteLength).ToArray();
        var secret = Enumerable.Repeat((byte)0x22, RetentionMutationIdentifierFormats.SecretByteLength).ToArray();
        var token = RetentionMutationToken.Create(nonce, secret);

        Assert.True(RetentionMutationToken.TryParse(token, out var parts));
        Assert.Equal(nonce, parts.Nonce);
        Assert.Equal(secret, parts.Secret);
    }

    [Fact]
    public void TokenHash_UsesTheExactFullAsciiTokenString()
    {
        var nonce = Enumerable.Repeat((byte)0x11, 16).ToArray();
        var secret = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var token = RetentionMutationToken.Create(nonce, secret);
        var expected = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token));
        Assert.Equal(expected, RetentionMutationToken.HashFullToken(token));
        Assert.NotEqual(expected, RetentionMutationToken.HashFullToken(Convert.ToBase64String(secret)));
        Assert.Equal(Convert.ToHexString(expected).ToLowerInvariant(), RetentionMutationToken.HashFullTokenHex(token));
    }

    [Fact]
    public void ConfirmationDecisions_EnforceSingleConsumptionAndFreshReissueInvalidation()
    {
        var reissue = RetentionMutationConfirmationDecisions.DecideIssueRetry(sameKeyAndRequest: true, priorTokenConsumed: false);
        Assert.True(reissue.IssueFreshToken);
        Assert.True(reissue.InvalidatePriorToken);
        Assert.Null(reissue.Code);

        var consumed = RetentionMutationConfirmationDecisions.DecideIssueRetry(sameKeyAndRequest: true, priorTokenConsumed: true);
        Assert.False(consumed.IssueFreshToken);
        Assert.Equal(RetentionMutationErrorCodes.ConfirmationConsumed, consumed.Code);

        var first = RetentionMutationConfirmationDecisions.DecideConsumption(priorTokenConsumed: false);
        Assert.True(first.ConsumeToken);
        Assert.Null(first.Code);
        var second = RetentionMutationConfirmationDecisions.DecideConsumption(priorTokenConsumed: true);
        Assert.False(second.ConsumeToken);
        Assert.Equal(RetentionMutationErrorCodes.ConfirmationConsumed, second.Code);
        Assert.Equal(RetentionMutationErrorCodes.ConfirmationGenerationFailed, RetentionMutationConfirmationDecisions.DecideNonceCollision(true).Code);
        Assert.True(RetentionMutationConfirmationDecisions.DecideNonceCollision(false).IssueFreshToken);
    }

    [Fact]
    public void MutationEvaluation_StopsAtTheFirstOfTheNinePinnedChecks()
    {
        var failures = new (RetentionMutationEvaluationCheck Check, string Code)[]
        {
            (RetentionMutationEvaluationCheck.TokenValidity, RetentionMutationErrorCodes.ConfirmationInvalid),
            (RetentionMutationEvaluationCheck.TokenConsumption, RetentionMutationErrorCodes.ConfirmationConsumed),
            (RetentionMutationEvaluationCheck.Expiry, RetentionMutationErrorCodes.ConfirmationExpired),
            (RetentionMutationEvaluationCheck.Binding, RetentionMutationErrorCodes.ConfirmationBindingMismatch),
            (RetentionMutationEvaluationCheck.TargetSet, RetentionMutationErrorCodes.ConfirmationTargetChanged),
            (RetentionMutationEvaluationCheck.PinVector, RetentionMutationErrorCodes.ConfirmationPinChanged),
            (RetentionMutationEvaluationCheck.Retention, RetentionMutationErrorCodes.ConfirmationRetentionChanged),
            (RetentionMutationEvaluationCheck.Conflict, RetentionMutationErrorCodes.ConfirmationConflictChanged),
            (RetentionMutationEvaluationCheck.Version, RetentionMutationErrorCodes.ConfirmationVersionChanged)
        };

        foreach (var (check, code) in failures)
        {
            var input = new RetentionMutationEvaluationInput();
            input = check switch
            {
                RetentionMutationEvaluationCheck.TokenValidity => input with { TokenValid = false },
                RetentionMutationEvaluationCheck.TokenConsumption => input with { TokenConsumed = true },
                RetentionMutationEvaluationCheck.Expiry => input with { TokenUnexpired = false },
                RetentionMutationEvaluationCheck.Binding => input with { BindingMatches = false },
                RetentionMutationEvaluationCheck.TargetSet => input with { TargetSetMatches = false },
                RetentionMutationEvaluationCheck.PinVector => input with { PinVectorMatches = false },
                RetentionMutationEvaluationCheck.Retention => input with { RetentionMatches = false },
                RetentionMutationEvaluationCheck.Conflict => input with { ConflictMatches = false },
                _ => input with { VersionMatches = false }
            };

            var result = RetentionMutationEvaluationOrder.Evaluate(input);
            Assert.Equal(check, result.FailedCheck);
            Assert.Equal(code, result.Code);
            Assert.Single(result.FailedChecks);
        }
    }

    [Fact]
    public void MutationEvaluation_AllNineFailuresReturnCheckOne()
    {
        var result = RetentionMutationEvaluationOrder.Evaluate(new RetentionMutationEvaluationInput
        {
            TokenValid = false,
            TokenConsumed = true,
            TokenUnexpired = false,
            BindingMatches = false,
            TargetSetMatches = false,
            PinVectorMatches = false,
            RetentionMatches = false,
            ConflictMatches = false,
            VersionMatches = false
        });

        Assert.Equal(RetentionMutationEvaluationCheck.TokenValidity, result.FailedCheck);
        Assert.Equal(RetentionMutationErrorCodes.ConfirmationInvalid, result.Code);
        Assert.Equal([RetentionMutationEvaluationCheck.TokenValidity], result.FailedChecks);
    }

    [Fact]
    public void MutationEvaluation_ChecksFiveSixAndNineFailuresReturnCheckFive()
    {
        var result = RetentionMutationEvaluationOrder.Evaluate(new RetentionMutationEvaluationInput
        {
            TargetSetMatches = false,
            PinVectorMatches = false,
            VersionMatches = false
        });

        Assert.Equal(RetentionMutationEvaluationCheck.TargetSet, result.FailedCheck);
        Assert.Equal(RetentionMutationErrorCodes.ConfirmationTargetChanged, result.Code);
        Assert.Equal([RetentionMutationEvaluationCheck.TargetSet], result.FailedChecks);
    }

    [Fact]
    public void CommentValidation_NormalizesNfcAndRejectsForbiddenClasses()
    {
        Assert.True(RetentionMutationCommentValidator.Validate(null).IsValid);
        var decomposed = "e\u0301";
        var normalized = RetentionMutationCommentValidator.Validate(decomposed);
        Assert.True(normalized.IsValid);
        Assert.Equal("é", normalized.NormalizedComment);
        Assert.True(RetentionMutationCommentValidator.Validate(new string('a', 256)).IsValid);
        foreach (var invalid in new[]
        {
            "line\nfeed", "line\rreturn", "control\u0001", "https://example.test", "mailto:user@example.test", "www.example.test", "scheme://value", "C:\\temp\\file",
            "password", "passwd", "PWD", "secret=synthetic", "token", "apikey", "api_key", "authorization", "bearer", "credential", "password: synthetic", "rowid", "primary key", "primary_key", "autoincrement",
            "rpv1_abc", "rcid1_abc", "rt90v1_abc", "rid1_abc", "rae1_abc", "rhc1_abc"
        })
        {
            Assert.False(RetentionMutationCommentValidator.Validate(invalid).IsValid, invalid);
        }
        Assert.True(RetentionMutationCommentValidator.Validate("Benign review note").IsValid);
        Assert.True(RetentionMutationCommentValidator.Validate("See item note twelve").IsValid);
        Assert.False(RetentionMutationCommentValidator.Validate(new string('a', 257)).IsValid);
        Assert.False(RetentionMutationCommentValidator.Validate(string.Concat(Enumerable.Repeat("🙂", 257))).IsValid);
    }

    [Fact]
    public void SessionLinkage_OnlyExactSessionEventContentJoinQualifies()
    {
        const string requestedSessionId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";

        Assert.True(RetentionMutationSessionLinkage.Qualifies(
            RetentionStoreKind.SessionEventContent,
            "event-1",
            requestedSessionId,
            requestedSessionId));
        Assert.False(RetentionMutationSessionLinkage.Qualifies(
            RetentionStoreKind.SessionEventContent,
            "event-1",
            requestedSessionId.ToUpperInvariant(),
            requestedSessionId));
        Assert.False(RetentionMutationSessionLinkage.Qualifies(
            RetentionStoreKind.SessionEventContent,
            "event-1",
            null,
            requestedSessionId));
        Assert.False(RetentionMutationSessionLinkage.Qualifies(
            RetentionStoreKind.SessionEventContent,
            null,
            requestedSessionId,
            requestedSessionId));

        foreach (var (storeKind, sourceItemId) in new[]
        {
            (RetentionStoreKind.RawRecord, "run-1"),
            (RetentionStoreKind.AnalysisRunRaw, "trace-1"),
            (RetentionStoreKind.SensitiveBundle, "evidence-1"),
            (RetentionStoreKind.AnalysisSdkDirectory, "native-1"),
            (RetentionStoreKind.RawRecord, "C:\\capture\\item"),
            (RetentionStoreKind.RawRecord, "2026-07-20T12:00:00.0000000Z")
        })
        {
            Assert.False(RetentionMutationSessionLinkage.Qualifies(storeKind, sourceItemId, requestedSessionId, requestedSessionId));
        }
    }

    [Fact]
    public void DomainDtoContracts_ExposeEveryPinnedMutationPropertyRoster()
    {
        AssertDtoProperties<RetentionMutationTarget>("Kind", "Id");
        AssertDtoProperties<RetentionMutationPreviewRequest>("Target", "Operation", "Scope", "ReasonCode", "Comment");
        AssertDtoProperties<RetentionMutationConfirmRequest>("ConfirmationToken", "Operation", "Scope", "TargetKind", "TargetId");
        AssertDtoProperties<RetentionPreviewItem>("ItemId", "StoreKind", "State", "PinState", "DeleteState", "CapturedAt", "ExpiresAt", "PolicyId", "PolicyVersion", "ReadDeniedAt", "QueuedAt", "Revision", "RetryExhausted", "ErrorCode");
        AssertDtoProperties<RetentionMutationPreviewResponse>("SchemaVersion", "Result", "EmptyReason", "MutationAllowed", "PreviewId", "TargetKind", "TargetId", "Operation", "Scope", "SourceState", "SessionCompleteness", "ContentState", "CurrentState", "TargetItems", "TargetItemCount", "StoreKindSummary", "ExcludedItemCount", "ExcludedItemsByReason", "CaptureExpiryPolicySummary", "RetainedMetadataImpact", "ActiveCleanupExclusionConflicts", "BackupNonPurgeWarningCode", "ExpectedStateVersion", "TargetItemSetDigest", "PreviewDigest", "ConfirmationExpiresAt", "RejectionCode");
        AssertDtoProperties<RetentionMutationLifecycleCounts>("Expiring", "RetainedByPolicy", "ExpiredPendingDeletion", "DeletionQueued", "Deleting", "Deleted", "DeletionFailed");
        AssertDtoProperties<RetentionCurrentStateSummary>("ReadableItemCount", "ReadDeniedItemCount", "PinnedItemCount", "UnpinnedItemCount", "LifecycleCounts");
        AssertDtoProperties<RetentionStoreKindSummary>("StoreKind", "ItemCount", "ReadableCount", "ReadDeniedCount");
        AssertDtoProperties<RetentionExclusionSummary>("ReasonCode", "ItemCount");
        AssertDtoProperties<RetentionCaptureExpiryPolicySummary>("PolicyId", "PolicyVersion", "ItemCount", "CapturedAtMin", "CapturedAtMax", "OriginalExpiresAtMin", "OriginalExpiresAtMax");
        AssertDtoProperties<RetentionRetainedImpact>("RawContentWillBeDeleted", "SessionMetadataRetained", "EventMetadataRetainedCount", "SafeSummaryRetainedCount", "EvidenceReferenceRetainedCount");
        AssertDtoProperties<RetentionActiveConflictSummary>("ConflictCode", "ItemCount", "ConflictVersion");
        AssertDtoProperties<RetentionConfirmationIssueRequest>("PreviewId", "PreviewDigest");
        AssertDtoProperties<RetentionConfirmationIssueResponse>("SchemaVersion", "ConfirmationId", "ConfirmationToken", "ConfirmationExpiresAt");
        AssertDtoProperties<RetentionMutationResult>("SchemaVersion", "OperationId", "ResultCode", "TargetKind", "TargetId", "Operation", "Scope", "TargetItemCount", "PinState", "LifecycleCounts", "ReadDenied", "AuditEventId", "ExpectedVersion", "ResultVersion", "BackupNonPurgeWarningCode", "IdempotentReplay", "CreatedAt", "CompletedAt");
        AssertDtoProperties<RetentionMutationStatusResponse>("SchemaVersion", "OperationId", "Operation", "TargetKind", "TargetId", "Status", "ResultCode", "LifecycleCounts", "ReadDenied", "AuditEventId", "IdempotentReplay", "CreatedAt", "CompletedAt", "BackupNonPurgeWarningCode");
        AssertDtoProperties<RetentionItemStateResponse>("SchemaVersion", "ItemId", "StoreKind", "State", "PinState", "DeleteState", "PolicyId", "PolicyVersion", "CapturedAt", "ExpiresAt", "ReadDeniedAt", "QueuedAt", "DeletionStartedAt", "DeletedAt", "AttemptCount", "RetryExhausted", "ErrorCode", "RetryAt", "Revision", "SessionId");
        AssertDtoProperties<RetentionAuditEvent>("EventId", "OperationId", "EventType", "TargetKind", "TargetId", "SessionId", "OccurredAt", "ActorLabel", "Operation", "ReasonCode", "Comment", "PreviousPinState", "NewPinState", "PreviousOperationState", "NewOperationState", "RequestIdempotencyKey", "ExpectedVersion", "ResultVersion", "TargetItemSetDigest", "CompletionCode", "ErrorCode");
        AssertDtoProperties<RetentionHistoryResponse>("SchemaVersion", "TargetKind", "TargetId", "Events", "NextCursor");
    }

    [Fact]
    public void ErrorRegistry_CoversEveryCodeWithCanonicalReachabilityAndHttpMapping()
    {
        var expected = new Dictionary<string, (RetentionMutationReachabilityClass Reachability, int? HttpStatus, int? PreviewHttpStatus, int? ConfirmationIssueHttpStatus)>
        {
            [RetentionMutationErrorCodes.RequestInvalid] = (RetentionMutationReachabilityClass.RequestStage, 400, null, null),
            [RetentionMutationErrorCodes.TargetNotFound] = (RetentionMutationReachabilityClass.RequestStage, 404, null, null),
            [RetentionMutationErrorCodes.TargetLimitExceeded] = (RetentionMutationReachabilityClass.RequestStage, 413, null, null),
            [RetentionMutationErrorCodes.PreviewNotFound] = (RetentionMutationReachabilityClass.RequestStage, 404, null, null),
            [RetentionMutationErrorCodes.IdempotencyKeyInvalid] = (RetentionMutationReachabilityClass.RequestStage, 400, null, null),
            [RetentionMutationErrorCodes.IdempotencyConflict] = (RetentionMutationReachabilityClass.RequestStage, 409, null, null),
            [RetentionMutationErrorCodes.IdempotencyExpired] = (RetentionMutationReachabilityClass.RequestStage, 409, null, null),
            [RetentionMutationErrorCodes.OperationNotFound] = (RetentionMutationReachabilityClass.RequestStage, 404, null, null),
            [RetentionMutationErrorCodes.HistoryCursorInvalid] = (RetentionMutationReachabilityClass.RequestStage, 400, null, null),
            [RetentionMutationErrorCodes.CatalogUnavailable] = (RetentionMutationReachabilityClass.RequestStage, 503, null, null),
            [RetentionMutationErrorCodes.TargetNotApplicable] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.PinReadDenied] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.PinDeleting] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.PinDeleted] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.UnpinReadDenied] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.UnpinDeleting] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.UnpinDeleted] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.DeleteAlreadyDeleting] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.DeleteAlreadyDeleted] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.DeleteFailed] = (RetentionMutationReachabilityClass.PreviewStage, 409, 200, 409),
            [RetentionMutationErrorCodes.TargetEmpty] = (RetentionMutationReachabilityClass.ConfirmationIssueStage, 409, null, null),
            [RetentionMutationErrorCodes.PreviewExpired] = (RetentionMutationReachabilityClass.ConfirmationIssueStage, 409, null, null),
            [RetentionMutationErrorCodes.PreviewDigestMismatch] = (RetentionMutationReachabilityClass.ConfirmationIssueStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationGenerationFailed] = (RetentionMutationReachabilityClass.ConfirmationIssueStage, 503, null, null),
            [RetentionMutationErrorCodes.ConfirmationConsumed] = (RetentionMutationReachabilityClass.ConfirmationIssueStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationInvalid] = (RetentionMutationReachabilityClass.CommitStage, 401, null, null),
            [RetentionMutationErrorCodes.ConfirmationExpired] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationBindingMismatch] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationTargetChanged] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationPinChanged] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationRetentionChanged] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationConflictChanged] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.ConfirmationVersionChanged] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.PinExpired] = (RetentionMutationReachabilityClass.CommitStage, 409, null, null),
            [RetentionMutationErrorCodes.MutationTransactionFailed] = (RetentionMutationReachabilityClass.CommitStage, 503, null, null),
            [RetentionMutationErrorCodes.AuditWriteFailed] = (RetentionMutationReachabilityClass.CommitStage, 503, null, null),
            [RetentionMutationErrorCodes.DeleteAlreadyQueued] = (RetentionMutationReachabilityClass.CommitStage, 200, null, null),
            [RetentionMutationErrorCodes.BackupNotPurged] = (RetentionMutationReachabilityClass.Warning, 200, null, null)
        };

        Assert.Equal(expected.Count, RetentionMutationErrorCodeRegistry.All.Count);
        Assert.Equal(expected.Count, RetentionMutationErrorCodeRegistry.All.Select(static entry => entry.Code).Distinct(StringComparer.Ordinal).Count());
        foreach (var entry in RetentionMutationErrorCodeRegistry.All)
        {
            Assert.True(expected.TryGetValue(entry.Code, out var mapping), entry.Code);
            Assert.True(mapping.Reachability == entry.Reachability, entry.Code);
            Assert.True(mapping.HttpStatus == entry.HttpStatus, entry.Code);
            Assert.True(mapping.PreviewHttpStatus == entry.PreviewHttpStatus, entry.Code);
            Assert.True(mapping.ConfirmationIssueHttpStatus == entry.ConfirmationIssueHttpStatus, entry.Code);
            Assert.Same(entry, RetentionMutationErrorCodeRegistry.Get(entry.Code));
        }
    }

    private static RetentionPreviewDigestInput CreatePreviewDigestInput() => new(
        SchemaVersion: 1,
        Result: RetentionMutationPreviewResult.Actionable,
        EmptyReason: null,
        MutationAllowed: true,
        TargetKind: RetentionMutationTargetKind.Session,
        TargetId: "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071",
        Operation: RetentionMutationOperation.Pin,
        Scope: RetentionMutationScope.SessionItems,
        SourceState: RetentionMutationSourceState.Available,
        SessionCompleteness: RetentionMutationSessionCompleteness.Full,
        ContentState: "available",
        CurrentState: new(
            1,
            0,
            1,
            0,
            new(1, 0, 0, 0, 0, 0, 0)),
        TargetItems:
        [
            new(
                "item-1",
                RetentionStoreKind.SessionEventContent,
                RetentionItemLifecycle.Expiring,
                RetentionPinState.Unpinned,
                RetentionDeleteState.NotRequested,
                CapturedAt,
                CapturedAt.AddDays(90),
                "raw-default-90d",
                1,
                null,
                null,
                1,
                false,
                null)
        ],
        TargetItemCount: 1,
        StoreKindSummary: [new(RetentionStoreKind.SessionEventContent, 1, 1, 0)],
        ExcludedItemCount: 0,
        ExcludedItemsByReason: [new(RetentionMutationExclusionCodes.MissingOwnershipProof, 0)],
        CaptureExpiryPolicySummary: [new("raw-default-90d", 1, 1, CapturedAt, CapturedAt, CapturedAt.AddDays(90), CapturedAt.AddDays(90))],
        RetainedMetadataImpact: new(false, true, 1, 2, 3),
        ActiveCleanupExclusionConflicts: [new(RetentionMutationConflictCodes.ActiveReadLease, 1, "v1-conflict")],
        BackupNonPurgeWarningCode: RetentionMutationConstants.BackupWarningCode,
        RejectionCode: null,
        ExpectedStateVersion: "v1-state",
        TargetItemSetDigest: "sha256-item-set");

    private static void AssertDtoProperties<T>(params string[] expected) =>
        Assert.Equal(expected, typeof(T).GetProperties().Select(static property => property.Name));

    [Fact]
    public void Registries_AreClosedAndReachabilityAnnotated()
    {
        Assert.Equal(7, RetentionMutationReasonCodes.All.Count);
        Assert.Equal(8, RetentionMutationCompletionCodes.All.Count);
        Assert.Equal(["missing_ownership_proof"], RetentionMutationExclusionCodes.All);
        Assert.Equal(["active_read_lease", "active_operation_lease", "active_deletion_lease", "active_delete_intent"], RetentionMutationConflictCodes.All);
        Assert.Equal(38, RetentionMutationErrorCodeRegistry.All.Count);
        Assert.All(RetentionMutationErrorCodeRegistry.All, entry => Assert.StartsWith("retention_", entry.Code));
        Assert.Equal(1, RetentionMutationErrorCodeRegistry.Get(RetentionMutationErrorCodes.ConfirmationInvalid).MutationTimeCheck);
        Assert.Equal(9, RetentionMutationErrorCodeRegistry.Get(RetentionMutationErrorCodes.ConfirmationVersionChanged).MutationTimeCheck);
        Assert.Equal(RetentionMutationReachabilityClass.Warning, RetentionMutationErrorCodeRegistry.Get(RetentionMutationErrorCodes.BackupNotPurged).Reachability);
        Assert.Equal("retention_mutation_replayed", RetentionMutationResultCodes.Replayed);
        Assert.DoesNotContain(RetentionMutationResultCodes.Replayed, RetentionMutationCompletionCodes.All);
    }

    [Fact]
    public void DomainDtoContracts_ExposeThePinnedSanitizedShapes()
    {
        Assert.Equal(
            ["Target", "Operation", "Scope", "ReasonCode", "Comment"],
            typeof(RetentionMutationPreviewRequest).GetProperties().Select(static p => p.Name));
        Assert.Equal(
            ["ConfirmationToken", "Operation", "Scope", "TargetKind", "TargetId"],
            typeof(RetentionMutationConfirmRequest).GetProperties().Select(static p => p.Name));
        Assert.Equal("local-user", RetentionMutationConstants.ActorLabel);
        Assert.Equal(TimeSpan.FromMinutes(5), RetentionMutationConstants.ConfirmationLifetime);
        Assert.Equal(100, RetentionMutationConstants.TargetItemLimit);
        Assert.Equal(365, RetentionMutationConstants.IdempotencyLifetimeDays);
    }
}

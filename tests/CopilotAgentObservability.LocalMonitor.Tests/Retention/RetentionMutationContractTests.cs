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

        var fieldsA = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["z_field"] = 2,
            ["a_field"] = "safe",
            ["state"] = "expiring",
            ["preview_id"] = "rpv1_ignored",
            ["comments"] = "ignored"
        };
        var fieldsB = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["comments"] = "different ignored value",
            ["state"] = "expiring",
            ["a_field"] = "safe",
            ["z_field"] = 2,
            ["preview_id"] = "rpv1_other"
        };
        var digestA = RetentionMutationDigests.PreviewDigest(new(fieldsA, "v1-state", firstDigest, null));
        var digestB = RetentionMutationDigests.PreviewDigest(new(fieldsB, "v1-state", firstDigest, null));
        Assert.Equal(digestA, digestB);
        Assert.NotEqual(digestA, RetentionMutationDigests.PreviewDigest(new(fieldsA, "v1-state", firstDigest, RetentionMutationErrorCodes.PinDeleted)));
        var changedFields = new Dictionary<string, object?>(fieldsA, StringComparer.Ordinal) { ["state"] = "retained_by_policy" };
        Assert.NotEqual(digestA, RetentionMutationDigests.PreviewDigest(new(changedFields, "v1-state", firstDigest, null)));
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

        Assert.Equal(27, previewId.Length);
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
        Assert.StartsWith(RetentionMutationIdentifierFormats.ConfirmationTokenPrefix, token);

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
            "line\nfeed", "line\rreturn", "control\u0001", "https://example.test", "C:\\temp\\file",
            "secret=synthetic", "password: synthetic", "database_key=synthetic", "rt90v1_abc_def"
        })
        {
            Assert.False(RetentionMutationCommentValidator.Validate(invalid).IsValid, invalid);
        }
        Assert.False(RetentionMutationCommentValidator.Validate(new string('a', 257)).IsValid);
        Assert.False(RetentionMutationCommentValidator.Validate(string.Concat(Enumerable.Repeat("🙂", 257))).IsValid);
    }

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

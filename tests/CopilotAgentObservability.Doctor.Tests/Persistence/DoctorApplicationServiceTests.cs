namespace CopilotAgentObservability.Doctor.Tests.Persistence;

public sealed class DoctorApplicationServiceTests
{
    [Fact]
    public void ProductionStore_HasNoReadyFixedInterfaceCompletionPath()
    {
        var storeType = typeof(SqliteDoctorVerificationStore);

        Assert.DoesNotContain(typeof(IDoctorVerificationStore), storeType.GetInterfaces());
        var completion = Assert.Single(
            storeType.GetMethods(),
            method => method.Name == nameof(SqliteDoctorVerificationStore.Complete));
        Assert.Contains(
            completion.GetParameters(),
            parameter => parameter.ParameterType.IsGenericType
                && parameter.ParameterType.GetGenericTypeDefinition() == typeof(Func<,>));
    }

    [Fact]
    public void Complete_ResolvesTrustedCandidatesAndEvaluatesExactlyOnce()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = new SqliteDoctorVerificationStore(database.Path, time);
        var evaluatorCalls = 0;
        DoctorFactSnapshot? evaluatedSnapshot = null;
        var application = SqliteDoctorApplicationService.Create(
            store,
            snapshot =>
            {
                evaluatorCalls++;
                evaluatedSnapshot = snapshot;
                return DoctorEvaluator.Evaluate(snapshot);
            });
        var started = application.Start("claude-code", "claude-code-hook", time.UtcNow.AddMinutes(5));
        var verification = Assert.IsType<DoctorVerification>(started.Verification);
        var candidates = new[]
        {
            Candidate(verification, "receipt-ingest", DoctorEvidenceKind.Ingest),
            Candidate(verification, "receipt-raw", DoctorEvidenceKind.RawPersistence),
            Candidate(verification, "receipt-projection", DoctorEvidenceKind.Projection),
            Candidate(verification, "receipt-binding", DoctorEvidenceKind.ExactSessionBinding),
            Candidate(verification, "receipt-content", DoctorEvidenceKind.CompletenessContent),
        };
        foreach (var candidate in candidates)
        {
            Assert.Equal(DoctorResultCode.VerificationActive, application.ObserveCandidate(candidate).Code);
        }

        var snapshot = DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true) with
        {
            SourceSurface = verification.ExpectedSourceSurface,
            ExpectedSourceAdapter = verification.ExpectedSourceAdapter,
            VerificationId = verification.VerificationId,
            ObservedAt = time.UtcNow,
            Observations = [],
        };

        var result = application.Complete(
            verification.VerificationId,
            expectedRevision: 1,
            snapshot,
            candidates.Select(candidate => candidate.EvidenceRef).ToArray());

        Assert.Equal(1, evaluatorCalls);
        Assert.Equal(DoctorResultCode.VerificationCompleted, result.Code);
        Assert.True(result.Success);
        Assert.Equal(DoctorStateCode.FirstTraceReady, result.Evaluation?.PrimaryState?.StateCode);
        var completed = Assert.IsType<DoctorVerification>(result.Verification);
        Assert.Equal(DoctorVerificationState.Completed, completed.State);
        Assert.Equal(2, completed.Revision);
        Assert.Equal(candidates.Select(candidate => candidate.EvidenceRef), completed.AcceptedEvidenceRefs);
        Assert.Equal(
            candidates.Select(candidate => new DoctorObservation(
                candidate.SourceSurface,
                candidate.SourceAdapter,
                candidate.EvidenceClass,
                candidate.EvidenceKind,
                candidate.EvidenceRef,
                candidate.ObservedAt)),
            Assert.IsType<DoctorFactSnapshot>(evaluatedSnapshot).Observations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Complete_AdvisoryOnlyUnknownContentContext_CompletesAtomically(bool contentCaptureUnknown)
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var application = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(database.Path, time));
        var verification = Assert.IsType<DoctorVerification>(
            application.Start("claude-code", "otel", time.UtcNow.AddMinutes(5)).Verification);
        var candidates = Enum.GetValues<DoctorEvidenceKind>()
            .Select(kind => Candidate(verification, $"receipt-{kind.ToString().ToLowerInvariant()}", kind))
            .ToArray();
        foreach (var candidate in candidates)
        {
            Assert.Equal(DoctorResultCode.VerificationActive, application.ObserveCandidate(candidate).Code);
        }
        var snapshot = Context(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification, time) with
        {
            CompletenessAndContent = new CompletenessAndContentFacts(
                DoctorCompleteness.Full,
                contentCaptureUnknown ? ContentCaptureStatus.Unknown : ContentCaptureStatus.Enabled,
                contentCaptureUnknown ? RawAccessStatus.Available : RawAccessStatus.Unknown),
        };

        var result = application.Complete(
            verification.VerificationId,
            1,
            snapshot,
            candidates.Select(candidate => candidate.EvidenceRef).ToArray());

        Assert.Equal(DoctorResultCode.VerificationCompleted, result.Code);
        Assert.Equal(DoctorStateCode.FirstTraceReady, result.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(["completeness_and_content"], result.Evaluation?.MissingFactFamilies);
        Assert.Equal(DoctorVerificationState.Completed, result.Verification?.State);
    }

    [Fact]
    public void Lifecycle_NonReadyAndPartialDoNotMutateBeforeReadyCompletion()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var application = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(database.Path, time));
        var started = application.Start("claude-code", "claude-code-hook", time.UtcNow.AddMinutes(5));
        var verification = Assert.IsType<DoctorVerification>(started.Verification);
        var candidates = Enum.GetValues<DoctorEvidenceKind>()
            .Select(kind => Candidate(verification, $"receipt-{kind.ToString().ToLowerInvariant()}", kind))
            .ToArray();
        foreach (var candidate in candidates)
        {
            Assert.Equal(DoctorResultCode.VerificationActive, application.ObserveCandidate(candidate).Code);
        }
        var references = candidates.Select(candidate => candidate.EvidenceRef).ToArray();

        var nonReady = application.Complete(
            verification.VerificationId,
            expectedRevision: 1,
            Context(DoctorTestSnapshots.ReadyNoRealTrace(), verification, time),
            references);
        var partial = application.Complete(
            verification.VerificationId,
            expectedRevision: 1,
            Context(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification, time) with
            {
                InstallAndSourceVersion = null,
            },
            references);
        var active = application.Status(verification.VerificationId);
        var completed = application.Complete(
            verification.VerificationId,
            expectedRevision: 1,
            Context(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification, time),
            references);
        var terminalConflict = application.Complete(
            verification.VerificationId,
            expectedRevision: 2,
            Context(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification, time),
            references);

        Assert.Equal(DoctorResultCode.EvaluationCompleted, nonReady.Code);
        Assert.Equal(DoctorStateCode.ReadyNoRealTrace, nonReady.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorVerificationState.Active, nonReady.Verification?.State);
        Assert.Equal(DoctorResultCode.PartialFactSnapshot, partial.Code);
        Assert.Equal(DoctorVerificationState.Active, partial.Verification?.State);
        Assert.Equal(DoctorResultCode.VerificationActive, active.Code);
        Assert.Equal(1, active.Verification?.Revision);
        Assert.Equal(DoctorResultCode.VerificationCompleted, completed.Code);
        Assert.Equal(DoctorStateCode.FirstTraceReady, completed.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorVerificationState.Completed, completed.Verification?.State);
        Assert.Equal(DoctorResultCode.VerificationAlreadyCompleted, terminalConflict.Code);
    }

    [Fact]
    public void Lifecycle_CancelExpiryAndStaleRevisionUseFixedOutcomes()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var application = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(database.Path, time));

        var cancelledStart = Assert.IsType<DoctorVerification>(
            application.Start("claude-code", null, time.UtcNow.AddMinutes(5)).Verification);
        var stale = application.Cancel(cancelledStart.VerificationId, expectedRevision: 2);
        var cancelled = application.Cancel(cancelledStart.VerificationId, expectedRevision: 1);
        var alreadyCancelled = application.Cancel(cancelledStart.VerificationId, expectedRevision: 2);

        var expiring = Assert.IsType<DoctorVerification>(
            application.Start("claude-code", null, time.UtcNow.AddMinutes(1)).Verification);
        time.UtcNow = time.UtcNow.AddMinutes(1);
        var expiredStatus = application.Status(expiring.VerificationId);
        var expiredCancel = application.Cancel(expiring.VerificationId, expectedRevision: 1);

        Assert.Equal(DoctorResultCode.VerificationStale, stale.Code);
        Assert.Equal(DoctorResultCode.VerificationCancelled, cancelled.Code);
        Assert.Equal(2, cancelled.Verification?.Revision);
        Assert.Equal(DoctorResultCode.VerificationAlreadyCancelled, alreadyCancelled.Code);
        Assert.Equal(DoctorResultCode.VerificationExpired, expiredStatus.Code);
        Assert.Equal(DoctorVerificationState.Expired, expiredStatus.Verification?.State);
        Assert.Equal(DoctorResultCode.VerificationExpired, expiredCancel.Code);
    }

    [Fact]
    public void Complete_RejectsCallerObservationsAndContextMismatchBeforeEvaluation()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var evaluatorCalls = 0;
        var application = SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(database.Path, time),
            snapshot =>
            {
                evaluatorCalls++;
                return DoctorEvaluator.Evaluate(snapshot);
            });
        var verification = Assert.IsType<DoctorVerification>(
            application.Start("claude-code", "otel", time.UtcNow.AddMinutes(5)).Verification);
        var readySnapshot = DoctorTestSnapshots.FirstTraceReady();
        var callerObservation = Context(readySnapshot, verification, time) with
        {
            Observations = readySnapshot.Observations,
        };
        var wrongId = callerObservation with
        {
            VerificationId = "01890abc-def0-7000-8000-000000000999",
            Observations = [],
        };
        var wrongAdapter = Context(readySnapshot, verification, time) with
        {
            ExpectedSourceAdapter = "wrong-adapter",
        };

        var observationsRejected = application.Complete(
            verification.VerificationId,
            1,
            callerObservation,
            ["receipt-ingest"]);
        var contextRejected = application.Complete(
            verification.VerificationId,
            1,
            wrongId,
            ["receipt-ingest"]);
        var adapterRejected = application.Complete(
            verification.VerificationId,
            1,
            wrongAdapter,
            ["receipt-ingest"]);

        Assert.Equal(DoctorResultCode.InvalidInput, observationsRejected.Code);
        Assert.Equal(DoctorResultCode.InvalidInput, contextRejected.Code);
        Assert.Equal(DoctorResultCode.ExpectedSourceMismatch, adapterRejected.Code);
        Assert.Equal(0, evaluatorCalls);
    }

    [Fact]
    public void Complete_PostEvaluationStoreFailureReturnsOnlySanitizedStoreOutcome()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var evaluatorCalls = 0;
        var store = new SqliteDoctorVerificationStore(
            database.Path,
            time,
            busyTimeoutMilliseconds: 5_000,
            checkpoint: stage =>
            {
                if (stage == "after-evidence-acceptance")
                {
                    throw new InvalidOperationException("SECRET sqlite path");
                }
            });
        var application = SqliteDoctorApplicationService.Create(
            store,
            snapshot =>
            {
                evaluatorCalls++;
                return DoctorEvaluator.Evaluate(snapshot);
            });
        var verification = Assert.IsType<DoctorVerification>(
            application.Start("claude-code", null, time.UtcNow.AddMinutes(5)).Verification);
        var candidates = Enum.GetValues<DoctorEvidenceKind>()
            .Select(kind => Candidate(verification, $"receipt-{kind.ToString().ToLowerInvariant()}", kind))
            .ToArray();
        foreach (var candidate in candidates)
        {
            Assert.Equal(DoctorResultCode.VerificationActive, application.ObserveCandidate(candidate).Code);
        }

        var result = application.Complete(
            verification.VerificationId,
            1,
            Context(DoctorTestSnapshots.FirstTraceReady(exactBindingRequired: true), verification, time),
            candidates.Select(candidate => candidate.EvidenceRef).ToArray());

        Assert.Equal(1, evaluatorCalls);
        Assert.False(result.Success);
        Assert.Equal(DoctorResultCode.DoctorStoreUnavailable, result.Code);
        Assert.Null(result.Evaluation);
        Assert.Null(result.Verification);
        Assert.Equal(DoctorResultCode.VerificationActive, application.Status(verification.VerificationId).Code);
    }

    private static DoctorEvidenceCandidate Candidate(
        DoctorVerification verification,
        string evidenceRef,
        DoctorEvidenceKind evidenceKind) =>
        DoctorTestData.Candidate(
            verification,
            evidenceRef,
            evidenceKind: evidenceKind,
            sourceAdapter: verification.ExpectedSourceAdapter);

    private static DoctorFactSnapshot Context(
        DoctorFactSnapshot snapshot,
        DoctorVerification verification,
        DoctorTestTimeProvider time) => snapshot with
        {
            SourceSurface = verification.ExpectedSourceSurface,
            ExpectedSourceAdapter = verification.ExpectedSourceAdapter,
            VerificationId = verification.VerificationId,
            ObservedAt = time.UtcNow,
            Observations = [],
        };
}

using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Doctor.Tests.Persistence;

public sealed class DoctorVerificationStoreTests
{
    [Fact]
    public void StartAndGet_GenerateCanonicalUuidV7AndDeriveExpiryWithoutPersistingIt()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = NewStore(database, time);

        var invalidLow = store.Start("github-copilot-vscode", "otel", TimeSpan.FromSeconds(59));
        var invalidHigh = store.Start("github-copilot-vscode", "otel", TimeSpan.FromMinutes(31));
        var started = store.Start("github-copilot-vscode", "otel", TimeSpan.FromMinutes(5));

        Assert.Equal(DoctorResultCode.InvalidInput, invalidLow.Code);
        Assert.Equal(DoctorResultCode.InvalidInput, invalidHigh.Code);
        Assert.Equal(DoctorResultCode.VerificationStarted, started.Code);
        var verification = Assert.IsType<DoctorVerification>(started.Verification);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", verification.VerificationId);
        Assert.Equal(DoctorVerificationState.Active, verification.State);
        Assert.Equal(1, verification.Revision);
        Assert.Equal(DoctorTestData.Now, verification.StartedAt);
        Assert.Equal(DoctorTestData.Now.AddMinutes(5), verification.ExpiresAt);
        Assert.Empty(verification.AcceptedEvidenceRefs);

        time.UtcNow = verification.ExpiresAt;
        var expired = store.Get(verification.VerificationId);
        Assert.Equal(DoctorResultCode.VerificationExpired, expired.Code);
        Assert.Equal(DoctorVerificationState.Expired, Assert.IsType<DoctorVerification>(expired.Verification).State);

        using var connection = database.Open();
        Assert.Equal("active", DoctorTestDatabase.Scalar(connection, "SELECT state FROM doctor_verifications WHERE verification_id=$id;", ("$id", verification.VerificationId)));
        Assert.Equal("2026-07-16T01:02:03.0000000Z", DoctorTestDatabase.Scalar(connection, "SELECT started_at FROM doctor_verifications WHERE verification_id=$id;", ("$id", verification.VerificationId)));
        Assert.Equal("2026-07-16T01:07:03.0000000Z", DoctorTestDatabase.Scalar(connection, "SELECT expires_at FROM doctor_verifications WHERE verification_id=$id;", ("$id", verification.VerificationId)));
    }

    [Fact]
    public void StartAndObserveCandidate_EnforceBoundsSafetyAndMaximumCardinality()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = NewStore(database, time);
        Assert.Equal(DoctorResultCode.InvalidInput, store.Start("UPPER", null, TimeSpan.FromMinutes(1)).Code);
        var verification = Start(store);

        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, @"C:\private\trace.json")).Code);
        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, "Authorization: Bearer synthetic-secret")).Code);
        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, "user@example.invalid")).Code);
        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, "private/trace.json")).Code);
        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(
                verification,
                "session:synthetic-binding",
                DoctorEvidenceClass.SyntheticProbe,
                DoctorEvidenceKind.ExactSessionBinding)).Code);

        for (var index = 0; index < 100; index++)
        {
            Assert.Equal(
                DoctorResultCode.VerificationActive,
                store.ObserveCandidate(DoctorTestData.Candidate(verification, $"session:candidate-{index:D3}")).Code);
        }

        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, "session:candidate-100")).Code);
        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, "session:candidate-000")).Code);

        using var connection = database.Open();
        Assert.Equal(100L, DoctorTestDatabase.Scalar(connection, "SELECT count(*) FROM doctor_verification_evidence;"));
    }

    [Fact]
    public void SourceAdapterReferenceAndAcceptedSelectionBounds_AreInclusiveAndEnforced()
    {
        using var database = new DoctorTestDatabase();
        var store = NewStore(database);
        var maximumToken = "a" + new string('b', 63);
        Assert.NotNull(store.Start(maximumToken, maximumToken, TimeSpan.FromMinutes(1)).Verification);
        Assert.Equal(DoctorResultCode.InvalidInput, store.Start(maximumToken + "c", null, TimeSpan.FromMinutes(1)).Code);
        Assert.Equal(DoctorResultCode.InvalidInput, store.Start("source", maximumToken + "c", TimeSpan.FromMinutes(1)).Code);

        var verification = Start(store);
        var maximumReference = new string('r', 128);
        Assert.Equal(
            DoctorResultCode.VerificationActive,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, maximumReference)).Code);
        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.ObserveCandidate(DoctorTestData.Candidate(verification, maximumReference + "r")).Code);

        var references = new List<string>();
        for (var index = 0; index < 17; index++)
        {
            var reference = $"trace:accepted-{index:D2}";
            references.Add(reference);
            Assert.Equal(
                DoctorResultCode.VerificationActive,
                store.ObserveCandidate(DoctorTestData.Candidate(verification, reference)).Code);
        }
        Assert.Equal(
            DoctorResultCode.EvaluationCompleted,
            store.Complete(
                verification.VerificationId,
                1,
                verification.ExpectedSourceSurface,
                verification.ExpectedSourceAdapter,
                references.Take(16).ToArray(),
                _ => DoctorCompletionDecision.NotReady).Code);
        Assert.Equal(
            DoctorResultCode.InvalidInput,
            store.Complete(
                verification.VerificationId,
                1,
                verification.ExpectedSourceSurface,
                verification.ExpectedSourceAdapter,
                references,
                _ => DoctorCompletionDecision.Ready).Code);
        AssertUnchangedActive(database, verification.VerificationId);
    }

    [Fact]
    public void Complete_AcceptsSelectedEvidenceAndLifecycleAtomicallyInCallerOrder()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = NewStore(database, time);
        var verification = Start(store);
        var projection = DoctorTestData.Candidate(verification, "trace:projection", evidenceKind: DoctorEvidenceKind.Projection);
        var ingest = DoctorTestData.Candidate(verification, "trace:ingest", evidenceKind: DoctorEvidenceKind.Ingest);
        Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(projection).Code);
        Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(ingest).Code);

        time.UtcNow = DoctorTestData.Now.AddMinutes(1);
        var completed = store.Complete(
            verification.VerificationId,
            expectedRevision: 1,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            [ingest.EvidenceRef, projection.EvidenceRef],
            _ => DoctorCompletionDecision.Ready);

        Assert.Equal(DoctorResultCode.VerificationCompleted, completed.Code);
        var result = Assert.IsType<DoctorVerification>(completed.Verification);
        Assert.Equal(DoctorVerificationState.Completed, result.State);
        Assert.Equal(2, result.Revision);
        Assert.Equal(time.UtcNow, result.CompletedAt);
        Assert.Null(result.CancelledAt);
        Assert.Equal([ingest.EvidenceRef, projection.EvidenceRef], result.AcceptedEvidenceRefs);

        using (var connection = database.Open())
        {
            Assert.Equal(
                ["trace:ingest|1|0", "trace:projection|1|1"],
                DoctorTestDatabase.Rows(connection, "SELECT evidence_ref,accepted,accepted_ordinal FROM doctor_verification_evidence WHERE accepted=1 ORDER BY accepted_ordinal;"));
        }

        var reopened = new SqliteDoctorVerificationStore(database.Path, time);
        Assert.Equal(DoctorResultCode.VerificationActive, reopened.CreateSchema().Code);
        var afterRestart = reopened.Get(verification.VerificationId);
        Assert.Equal(DoctorResultCode.VerificationCompleted, afterRestart.Code);
        Assert.Equivalent(result, afterRestart.Verification, strict: true);
    }

    [Theory]
    [InlineData("not-ready", DoctorResultCode.EvaluationCompleted)]
    [InlineData("partial", DoctorResultCode.PartialFactSnapshot)]
    public void Complete_NonReadyOrPartialEvaluation_DoesNotMutateAnything(
        string decisionName,
        DoctorResultCode expectedCode)
    {
        var decision = decisionName == "partial"
            ? DoctorCompletionDecision.Partial
            : DoctorCompletionDecision.NotReady;
        using var database = new DoctorTestDatabase();
        var store = NewStore(database);
        var verification = Start(store);
        var candidate = DoctorTestData.Candidate(verification, "trace:not-ready");
        Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(candidate).Code);

        var result = store.Complete(
            verification.VerificationId,
            1,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            [candidate.EvidenceRef],
            resolved =>
            {
                Assert.Equivalent(candidate, Assert.Single(resolved), strict: true);
                return decision;
            });

        Assert.Equal(expectedCode, result.Code);
        Assert.Equivalent(verification, result.Verification, strict: true);
        AssertUnchangedActive(database, verification.VerificationId);
    }

    [Fact]
    public void Complete_RejectsMissingExpiredMismatchedAndSyntheticOnlyEvidenceWithoutMutation()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = NewStore(database, time);
        var verification = Start(store);

        Assert.Equal(
            DoctorResultCode.EvidenceNotFound,
            store.Complete(verification.VerificationId, 1, verification.ExpectedSourceSurface, "otel", ["trace:missing"], _ => DoctorCompletionDecision.Ready).Code);

        var expiring = DoctorTestData.Candidate(verification, "trace:expired", expiresAt: DoctorTestData.Now.AddMinutes(1));
        Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(expiring).Code);
        time.UtcNow = DoctorTestData.Now.AddMinutes(1);
        Assert.Equal(
            DoctorResultCode.EvidenceExpired,
            store.Complete(verification.VerificationId, 1, verification.ExpectedSourceSurface, "otel", [expiring.EvidenceRef], _ => DoctorCompletionDecision.Ready).Code);

        time.UtcNow = DoctorTestData.Now;
        var mismatched = DoctorTestData.Candidate(verification, "trace:mismatch", sourceSurface: "claude-code");
        Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(mismatched).Code);
        Assert.Equal(
            DoctorResultCode.ExpectedSourceMismatch,
            store.Complete(verification.VerificationId, 1, verification.ExpectedSourceSurface, "otel", [mismatched.EvidenceRef], _ => DoctorCompletionDecision.Ready).Code);

        var synthetic = DoctorTestData.Candidate(verification, "probe:ingest", DoctorEvidenceClass.SyntheticProbe);
        Assert.Equal(DoctorResultCode.VerificationActive, store.ObserveCandidate(synthetic).Code);
        Assert.Equal(
            DoctorResultCode.ExpectedSourceMismatch,
            store.Complete(verification.VerificationId, 1, verification.ExpectedSourceSurface, "otel", [synthetic.EvidenceRef], _ => DoctorCompletionDecision.Ready).Code);

        Assert.Equal(
            DoctorResultCode.ExpectedSourceMismatch,
            store.Complete(verification.VerificationId, 1, "claude-code", "otel", [synthetic.EvidenceRef], _ => DoctorCompletionDecision.Ready).Code);
        Assert.Equal(
            DoctorResultCode.ExpectedSourceMismatch,
            store.Complete(verification.VerificationId, 1, verification.ExpectedSourceSurface, "wrong-adapter", [synthetic.EvidenceRef], _ => DoctorCompletionDecision.Ready).Code);
        AssertUnchangedActive(database, verification.VerificationId);
    }

    [Fact]
    public void Cancel_UsesExpectedRevisionAndReturnsFixedTerminalOrStaleOutcomes()
    {
        using var database = new DoctorTestDatabase();
        var time = new DoctorTestTimeProvider(DoctorTestData.Now);
        var store = NewStore(database, time);
        var verification = Start(store);

        Assert.Equal(DoctorResultCode.VerificationStale, store.Cancel(verification.VerificationId, 2).Code);
        time.UtcNow = DoctorTestData.Now.AddSeconds(30);
        var cancelled = store.Cancel(verification.VerificationId, 1);
        Assert.Equal(DoctorResultCode.VerificationCancelled, cancelled.Code);
        var terminal = Assert.IsType<DoctorVerification>(cancelled.Verification);
        Assert.Equal(DoctorVerificationState.Cancelled, terminal.State);
        Assert.Equal(2, terminal.Revision);
        Assert.Equal(time.UtcNow, terminal.CancelledAt);
        Assert.Null(terminal.CompletedAt);
        Assert.Equal(DoctorResultCode.VerificationAlreadyCancelled, store.Cancel(verification.VerificationId, 1).Code);
        Assert.Equal(DoctorResultCode.VerificationAlreadyCancelled, store.Cancel(verification.VerificationId, 2).Code);
        Assert.Equal(DoctorResultCode.VerificationNotFound, store.Cancel(Guid.CreateVersion7(time.UtcNow).ToString("D"), 1).Code);
    }

    [Theory]
    [InlineData("after-candidate-insert")]
    [InlineData("after-evidence-acceptance")]
    [InlineData("after-terminal-update")]
    public void InjectedWriteFailure_RollsBackExactPriorRows(string failurePoint)
    {
        using var database = new DoctorTestDatabase();
        var baseStore = NewStore(database);
        var verification = Start(baseStore);
        var candidate = DoctorTestData.Candidate(verification, "trace:rollback");
        if (failurePoint != "after-candidate-insert")
        {
            Assert.Equal(DoctorResultCode.VerificationActive, baseStore.ObserveCandidate(candidate).Code);
        }

        string before;
        using (var connection = database.Open())
        {
            before = SnapshotDoctorRows(connection);
        }

        var failingStore = new SqliteDoctorVerificationStore(
            database.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now),
            checkpoint: point =>
            {
                if (point == failurePoint)
                {
                    throw new InvalidOperationException("injected Doctor write failure");
                }
            });

        var result = failurePoint == "after-candidate-insert"
            ? failingStore.ObserveCandidate(candidate)
            : failingStore.Complete(
                verification.VerificationId,
                1,
                verification.ExpectedSourceSurface,
                verification.ExpectedSourceAdapter,
                [candidate.EvidenceRef],
                _ => DoctorCompletionDecision.Ready);

        Assert.Equal(DoctorResultCode.DoctorStoreUnavailable, result.Code);
        using var reopened = database.Open();
        Assert.Equal(before, SnapshotDoctorRows(reopened));
    }

    [Fact]
    public async Task CompleteVersusCancel_BarrierAllowsExactlyOneTerminalWinner()
    {
        using var database = new DoctorTestDatabase();
        var baseStore = NewStore(database);
        var verification = Start(baseStore);
        var candidate = DoctorTestData.Candidate(verification, "trace:race");
        Assert.Equal(DoctorResultCode.VerificationActive, baseStore.ObserveCandidate(candidate).Code);
        using var barrier = new Barrier(2);

        SqliteDoctorVerificationStore RacingStore() => new(
            database.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now),
            checkpoint: point =>
            {
                if (point == "before-terminal-transaction")
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                }
            });

        var completeTask = Task.Run(() => RacingStore().Complete(
            verification.VerificationId,
            1,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            [candidate.EvidenceRef],
            _ => DoctorCompletionDecision.Ready));
        var cancelTask = Task.Run(() => RacingStore().Cancel(verification.VerificationId, 1));
        var results = await Task.WhenAll(completeTask, cancelTask);

        Assert.Single(results, result => result.Code is DoctorResultCode.VerificationCompleted or DoctorResultCode.VerificationCancelled);
        Assert.Single(results, result => result.Code is DoctorResultCode.VerificationAlreadyCompleted or DoctorResultCode.VerificationAlreadyCancelled);
        var persisted = Assert.IsType<DoctorVerification>(baseStore.Get(verification.VerificationId).Verification);
        Assert.Equal(2, persisted.Revision);
        Assert.Contains(persisted.State, new[] { DoctorVerificationState.Completed, DoctorVerificationState.Cancelled });
    }

    [Fact]
    public async Task SameCancelRace_BarrierAllowsOneWinnerAndOneTypedTerminalLoser()
    {
        using var database = new DoctorTestDatabase();
        var baseStore = NewStore(database);
        var verification = Start(baseStore);
        using var barrier = new Barrier(2);
        SqliteDoctorVerificationStore RacingStore() => new(
            database.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now),
            checkpoint: point =>
            {
                if (point == "before-terminal-transaction")
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                }
            });

        var results = await Task.WhenAll(
            Task.Run(() => RacingStore().Cancel(verification.VerificationId, 1)),
            Task.Run(() => RacingStore().Cancel(verification.VerificationId, 1)));

        Assert.Single(results, result => result.Code == DoctorResultCode.VerificationCancelled);
        Assert.Single(results, result => result.Code == DoctorResultCode.VerificationAlreadyCancelled);
    }

    [Fact]
    public async Task SameCompleteRace_BarrierAllowsOneAtomicEvidenceWinnerAndOneTypedTerminalLoser()
    {
        using var database = new DoctorTestDatabase();
        var baseStore = NewStore(database);
        var verification = Start(baseStore);
        var candidate = DoctorTestData.Candidate(verification, "trace:same-complete-race");
        Assert.Equal(DoctorResultCode.VerificationActive, baseStore.ObserveCandidate(candidate).Code);
        using var barrier = new Barrier(2);
        SqliteDoctorVerificationStore RacingStore() => new(
            database.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now),
            checkpoint: point =>
            {
                if (point == "before-terminal-transaction")
                {
                    Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
                }
            });
        DoctorStoreOutcome Complete() => RacingStore().Complete(
            verification.VerificationId,
            1,
            verification.ExpectedSourceSurface,
            verification.ExpectedSourceAdapter,
            [candidate.EvidenceRef],
            _ => DoctorCompletionDecision.Ready);

        var results = await Task.WhenAll(Task.Run(Complete), Task.Run(Complete));

        Assert.Single(results, result => result.Code == DoctorResultCode.VerificationCompleted);
        Assert.Single(results, result => result.Code == DoctorResultCode.VerificationAlreadyCompleted);
        using var connection = database.Open();
        Assert.Equal(
            ["1|0"],
            DoctorTestDatabase.Rows(connection, "SELECT accepted,accepted_ordinal FROM doctor_verification_evidence;"));
    }

    [Fact]
    public void ControlledSqliteWriteLock_MapsToDoctorStoreBusyWithoutRetry()
    {
        using var database = new DoctorTestDatabase();
        var store = NewStore(database);
        using var lockConnection = database.Open();
        using var writeLock = lockConnection.BeginTransaction(deferred: false);

        var lockedStore = new SqliteDoctorVerificationStore(
            database.Path,
            new DoctorTestTimeProvider(DoctorTestData.Now),
            busyTimeoutMilliseconds: 0);
        var result = lockedStore.Start("github-copilot-vscode", "otel", TimeSpan.FromMinutes(5));

        Assert.Equal(DoctorResultCode.DoctorStoreBusy, result.Code);
        Assert.Null(result.Verification);
        writeLock.Rollback();
    }

    private static SqliteDoctorVerificationStore NewStore(
        DoctorTestDatabase database,
        DoctorTestTimeProvider? time = null)
    {
        var store = new SqliteDoctorVerificationStore(database.Path, time ?? new DoctorTestTimeProvider(DoctorTestData.Now));
        Assert.Equal(DoctorResultCode.VerificationActive, store.CreateSchema().Code);
        return store;
    }

    private static DoctorVerification Start(SqliteDoctorVerificationStore store) =>
        Assert.IsType<DoctorVerification>(store.Start("github-copilot-vscode", "otel", TimeSpan.FromMinutes(5)).Verification);

    private static void AssertUnchangedActive(DoctorTestDatabase database, string verificationId)
    {
        using var connection = database.Open();
        Assert.Equal(
            ["active|1|<null>|<null>"],
            DoctorTestDatabase.Rows(connection, $"SELECT state,revision,completed_at,cancelled_at FROM doctor_verifications WHERE verification_id='{verificationId}';"));
        Assert.All(
            DoctorTestDatabase.Rows(connection, "SELECT accepted,accepted_ordinal FROM doctor_verification_evidence ORDER BY evidence_ref;"),
            row => Assert.Equal("0|<null>", row));
    }

    private static string SnapshotDoctorRows(SqliteConnection connection) => string.Join(
        '\n',
        DoctorTestDatabase.Rows(connection, "SELECT * FROM doctor_verifications ORDER BY verification_id;")
            .Concat(DoctorTestDatabase.Rows(connection, "SELECT * FROM doctor_verification_evidence ORDER BY candidate_id;")));
}

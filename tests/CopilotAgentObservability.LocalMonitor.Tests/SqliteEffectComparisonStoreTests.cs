using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteEffectComparisonStoreTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(8)]
    public void CreateSchema_UpgradesSupportedPriorVersions_AdditivelyAndWithoutSensitiveColumns(int previousVersion)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var session = SeedComparableSession(temp.DatabasePath, DateTimeOffset.Parse("2026-07-12T11:00:00+00:00"));

        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            Execute(connection, "DROP TABLE effect_receipts; DROP TABLE effect_comparison_evidence; DROP TABLE effect_comparison_sessions; DROP TABLE effect_comparisons;");
            if (previousVersion == 7) Execute(connection, "DROP TABLE objective_evaluation_evidence; DROP TABLE objective_evaluations;");
            if (previousVersion == 3) Execute(connection, "DROP TABLE proposal_apply_pending; DROP TABLE proposal_apply_audit; DROP TABLE proposal_applies; DROP TABLE proposal_apply_revisions; DROP TABLE proposal_apply_hunks; DROP TABLE proposal_apply_files; DROP TABLE proposal_apply_drafts; DROP TABLE improvement_proposal_evidence; DROP TABLE improvement_proposal_sessions; DROP TABLE improvement_proposals; DROP TABLE objective_evaluation_evidence; DROP TABLE objective_evaluations;");
            Execute(connection, "UPDATE schema_version SET version=$version WHERE component='session';", ("$version", previousVersion));
        }

        store.CreateSchema();

        using var verify = new SqliteConnection($"Data Source={temp.DatabasePath}");
        verify.Open();
        Assert.Equal(9L, Scalar(temp.DatabasePath, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(session.ToString("D"), Text(verify, "SELECT session_id FROM sessions LIMIT 1;"));
        foreach (var table in new[] { "effect_comparisons", "effect_comparison_sessions", "effect_comparison_evidence", "effect_receipts" })
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;", ("$name", table)));
        foreach (var forbidden in new[] { "raw", "prompt", "response", "path", "source", "diff", "replacement", "snapshot", "credential", "token", "note" })
            Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_comparisons') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden))
                + Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_comparison_sessions') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden))
                + Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_comparison_evidence') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden))
                + Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_receipts') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden)));
    }

    [Fact]
    public void RecordEffectComparison_ThreeByThreeQualityImprovementPersistsReceiptAndAtomicallyVerifies()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var appliedAt = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, appliedAt);
        var sessions = Enumerable.Range(0, 6).Select(index => SeedComparableSession(temp.DatabasePath, index < 3 ? appliedAt.AddMinutes(-10 - index) : appliedAt.AddMinutes(10 + index))).ToArray();
        foreach (var (id, index) in sessions.Select((id, index) => (id, index)))
            store.UpsertHumanEvaluation(new(id, index < 3 ? "problem" : "expected", appliedAt.AddMinutes(index)));

        var receipt = store.RecordEffectComparison(new(proposal, 1, apply,
            sessions.Select((id, index) => new EffectCohortSession(id, index < 3 ? "pre" : "post", "case", null)).ToArray()), appliedAt.AddHours(1));

        Assert.Equal(EffectVerdict.Improved, receipt.Result.Verdict);
        Assert.Equal(ImprovementProposalStatus.Verified, store.GetImprovementProposal(proposal)!.Status);
        var persisted = Assert.Single(store.ListEffectReceipts(proposal));
        Assert.Equal(receipt.ComparisonId, persisted.ComparisonId);
        Assert.Equal(receipt.Result, persisted.Result, new EffectVerdictResultComparer());
        Assert.Equal(6, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparison_sessions;"));
        Assert.Equal(6, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparison_evidence;"));
    }

    [Fact]
    public void RecordEffectComparison_MissingDecisiveQualityEvidencePersistsInsufficientWithoutVerifying()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var proposal = Guid.CreateVersion7();
        var apply = Guid.CreateVersion7();
        var appliedAt = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, appliedAt);
        var sessions = Enumerable.Range(0, 6).Select(index => SeedComparableSession(temp.DatabasePath, index < 3 ? appliedAt.AddMinutes(-10 - index) : appliedAt.AddMinutes(10 + index))).ToArray();

        var receipt = store.RecordEffectComparison(new(proposal, 1, apply,
            sessions.Select((id, index) => new EffectCohortSession(id, index < 3 ? "pre" : "post", "case", null)).ToArray()), appliedAt.AddHours(1));

        Assert.Equal(EffectVerdict.InsufficientEvidence, receipt.Result.Verdict);
        Assert.Equal(ImprovementProposalStatus.Recommended, store.GetImprovementProposal(proposal)!.Status);
        Assert.Single(store.ListEffectReceipts(proposal));
    }

    [Theory]
    [InlineData("equal", EffectVerdict.NoChange)]
    [InlineData("regressed", EffectVerdict.Regressed)]
    [InlineData("severe", EffectVerdict.Regressed)]
    [InlineData("counts", EffectVerdict.InsufficientEvidence)]
    [InlineData("metrics", EffectVerdict.InsufficientEvidence)]
    public void RecordEffectComparison_PersistsEveryNonImprovedVerdictWithoutChangingProposalMaturity(string scenario, EffectVerdict expected)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var appliedAt = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, appliedAt);
        var count = scenario == "counts" ? 5 : 6;
        var sessions = Enumerable.Range(0, count).Select(index => SeedComparableSession(temp.DatabasePath, index < 3 ? appliedAt.AddMinutes(-10 - index) : appliedAt.AddMinutes(10 + index), scenario == "metrics" ? null : 10)).ToArray();
        if (scenario == "metrics")
        {
            using var connection = new SqliteConnection($"Data Source={temp.DatabasePath}");
            connection.Open();
            Execute(connection, "UPDATE sessions SET ended_at=started_at;");
        }
        foreach (var (id, index) in sessions.Select((id, index) => (id, index)))
            store.UpsertHumanEvaluation(new(id, scenario is "regressed" or "severe" && index >= 3 && index == sessions.Length - 1 ? "problem" : "expected", appliedAt.AddMinutes(index)));
        if (scenario == "severe") AddSevereObjective(store, temp.DatabasePath, sessions[^1], appliedAt);

        var receipt = store.RecordEffectComparison(new(proposal, 1, apply,
            sessions.Select((id, index) => new EffectCohortSession(id, index < 3 ? "pre" : "post", "case", null)).ToArray()), appliedAt.AddHours(1));

        Assert.Equal(expected, receipt.Result.Verdict);
        Assert.Equal(ImprovementProposalStatus.Recommended, store.GetImprovementProposal(proposal)!.Status);
        Assert.Single(store.ListEffectReceipts(proposal));
    }

    [Fact]
    public void RecordEffectComparison_CapturesExactHumanAndObjectiveEvidenceIdentityAndFailsClosedOnConflict()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var appliedAt = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, appliedAt);
        var sessions = Enumerable.Range(0, 6).Select(index => SeedComparableSession(temp.DatabasePath, index < 3 ? appliedAt.AddMinutes(-10 - index) : appliedAt.AddMinutes(10 + index))).ToArray();
        foreach (var id in sessions) store.UpsertHumanEvaluation(new(id, "expected", appliedAt));
        var objectiveId = AddObjective(store, temp.DatabasePath, sessions[^1], ObjectiveResult.Fail, ObjectiveSeverity.Normal, appliedAt);

        var receipt = store.RecordEffectComparison(new(proposal, 1, apply, sessions.Select((id, index) => new EffectCohortSession(id, index < 3 ? "pre" : "post", "case", null)).ToArray()), appliedAt.AddHours(1));

        Assert.Equal(EffectVerdict.Regressed, receipt.Result.Verdict);
        Assert.Equal(6L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparison_evidence WHERE kind='human' AND recorded_at=$at;", ("$at", appliedAt.ToString("O"))));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparison_evidence WHERE kind='objective' AND reference_id=$id;", ("$id", objectiveId.ToString("D"))));
    }

    [Fact]
    public void RecordEffectComparison_RejectsDuplicateSessionClassificationBeforePersisting()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();
        var id = Guid.CreateVersion7();

        Assert.Throws<ArgumentException>(() => store.RecordEffectComparison(new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(),
            [new(id, "pre", "case", null), new(id, "post", "case", null)]), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("unknown", "case", null)]
    [InlineData("pre", "case", "user_excluded")]
    [InlineData("excluded", "", null)]
    [InlineData("excluded", "", "invalid")]
    [InlineData("pre", "", null)]
    [InlineData("post", "bad/key", null)]
    public void RecordEffectComparison_RejectsInvalidCohortClassificationAndCaseKey(string classification, string caseKey, string? exclusionReason)
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();

        Assert.Throws<ArgumentException>(() => store.RecordEffectComparison(new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(),
            [new(Guid.CreateVersion7(), classification, caseKey, exclusionReason)]), DateTimeOffset.UtcNow));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparisons;"));
    }

    [Fact]
    public void RecordEffectComparison_RejectsOversizeCaseKeyBeforePersisting()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();

        Assert.Throws<ArgumentException>(() => store.RecordEffectComparison(new(Guid.CreateVersion7(), 1, Guid.CreateVersion7(),
            [new(Guid.CreateVersion7(), "pre", new string('a', 201), null)]), DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("unbound", "completed", false)]
    [InlineData("partial", "completed", false)]
    [InlineData("rich", "completed", false)]
    [InlineData("full", "active", false)]
    [InlineData("full", "unknown", false)]
    [InlineData("full", "completed", true)]
    public void RecordEffectComparison_RequiresExactFullTerminalSession(string completeness, string status, bool removeNative)
    {
        using var temp = new MonitorTempDirectory(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var session = SeedComparableSession(temp.DatabasePath, at.AddMinutes(-2));
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            Execute(connection, "UPDATE sessions SET completeness=$completeness,status=$status WHERE session_id=$id;", ("$completeness", completeness), ("$status", status), ("$id", session.ToString("D")));
            if (removeNative) Execute(connection, "DELETE FROM session_native_ids WHERE session_id=$id;", ("$id", session.ToString("D")));
        }

        Assert.Throws<InvalidOperationException>(() => store.RecordEffectComparison(new(proposal, 1, apply, [new(session, "pre", "case", null)]), at));
    }

    [Theory]
    [InlineData("missing_start")]
    [InlineData("missing_end")]
    [InlineData("end_before_start")]
    public void RecordEffectComparison_RejectsMissingOrInvalidSessionTimes(string scenario)
    {
        using var temp = new MonitorTempDirectory(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var session = SeedComparableSession(temp.DatabasePath, at.AddMinutes(-2));
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"); connection.Open();
        Execute(connection, scenario switch
        {
            "missing_start" => "UPDATE sessions SET started_at=NULL WHERE session_id=$id;",
            "missing_end" => "UPDATE sessions SET ended_at=NULL WHERE session_id=$id;",
            _ => "UPDATE sessions SET ended_at=$time WHERE session_id=$id;"
        }, ("$id", session.ToString("D")), ("$time", at.AddMinutes(-3).ToString("O")));

        Assert.Throws<InvalidOperationException>(() => store.RecordEffectComparison(new(proposal, 1, apply, [new(session, "pre", "case", null)]), at));
    }

    [Theory]
    [InlineData("pre_exact", false)]
    [InlineData("post_exact", false)]
    [InlineData("pre_after", true)]
    [InlineData("post_before", true)]
    [InlineData("spanning", true)]
    public void RecordEffectComparison_EnforcesApplicationTimeBoundary(string scenario, bool rejects)
    {
        using var temp = new MonitorTempDirectory(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var session = SeedComparableSession(temp.DatabasePath, at.AddMinutes(-2));
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            var (start, end, side) = scenario switch
            {
                "pre_exact" => (at.AddMinutes(-1), at, "pre"),
                "post_exact" => (at, at.AddMinutes(1), "post"),
                "pre_after" => (at.AddMinutes(-1), at.AddTicks(1), "pre"),
                "post_before" => (at.AddTicks(-1), at.AddMinutes(1), "post"),
                _ => (at.AddMinutes(-1), at.AddMinutes(1), "pre")
            };
            Execute(connection, "UPDATE sessions SET started_at=$start,ended_at=$end WHERE session_id=$id;", ("$start", start.ToString("O")), ("$end", end.ToString("O")), ("$id", session.ToString("D")));
            if (rejects)
                Assert.Throws<ArgumentException>(() => store.RecordEffectComparison(new(proposal, 1, apply, [new(session, side, "case", null)]), at));
            else
                Assert.Equal(EffectVerdict.InsufficientEvidence, store.RecordEffectComparison(new(proposal, 1, apply, [new(session, side, "case", null)]), at).Result.Verdict);
        }
    }

    [Fact]
    public void EffectComparisonRead_RestartsWithSameStoredSessionAndEvidenceIdentity()
    {
        using var temp = new MonitorTempDirectory(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = Enumerable.Range(0, 6).Select(i => SeedComparableSession(temp.DatabasePath, i < 3 ? at.AddMinutes(-10 - i) : at.AddMinutes(10 + i))).ToArray();
        foreach (var id in sessions) store.UpsertHumanEvaluation(new(id, "expected", at));
        var receipt = store.RecordEffectComparison(new(proposal, 1, apply, sessions.Select((id, i) => new EffectCohortSession(id, i < 3 ? "pre" : "post", i < 3 ? "case-a" : "case-b", null)).ToArray()), at);

        var reloaded = new SqliteSessionStore(temp.DatabasePath).GetEffectComparison(receipt.ComparisonId)!;

        Assert.Equal(receipt.Result, reloaded.Receipt.Result, new EffectVerdictResultComparer());
        Assert.Equal(receipt with { Result = reloaded.Receipt.Result }, reloaded.Receipt);
        Assert.Equal(6, reloaded.Sessions.Count);
        Assert.Equal(6, reloaded.Evidence.Count(e => e.Kind == "human"));
        Assert.Equal(reloaded.Evidence.Where(e => e.Kind == "human").Select(e => e.SessionId).Order(), reloaded.Sessions.Where(s => s.Classification is "pre" or "post").Select(s => s.SessionId).Order());
        Assert.Equal(new[] { "case-a", "case-b" }, reloaded.Sessions.Where(s => s.Classification != "excluded").Select(s => s.CaseKey).Distinct().Order());
    }

    [Theory]
    [InlineData("missing_proposal")]
    [InlineData("wrong_proposal_revision")]
    [InlineData("candidate")]
    [InlineData("verified")]
    [InlineData("missing_apply")]
    [InlineData("wrong_apply_revision")]
    [InlineData("failed")]
    [InlineData("rolled_back")]
    [InlineData("rollback_pending")]
    public void RecordEffectComparison_RejectsStaleProposalOrInactiveApplication(string scenario)
    {
        using var temp = new MonitorTempDirectory(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var session = SeedComparableSession(temp.DatabasePath, at.AddMinutes(-2));
        var requestProposal = scenario == "missing_proposal" ? Guid.CreateVersion7() : proposal;
        var requestApply = scenario == "missing_apply" ? Guid.CreateVersion7() : apply;
        var requestRevision = scenario == "wrong_proposal_revision" ? 2 : 1;
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            if (scenario is "candidate" or "verified") Execute(connection, "UPDATE improvement_proposals SET status=$state WHERE proposal_id=$id;", ("$state", scenario), ("$id", proposal.ToString("D")));
            if (scenario == "wrong_apply_revision") Execute(connection, "UPDATE proposal_applies SET proposal_revision=2 WHERE apply_id=$id;", ("$id", apply.ToString("D")));
            if (scenario is "failed" or "rolled_back") Execute(connection, "UPDATE proposal_applies SET state=$state WHERE apply_id=$id;", ("$state", scenario), ("$id", apply.ToString("D")));
            if (scenario == "rollback_pending") Execute(connection, "INSERT INTO proposal_apply_pending(apply_id,draft_id,proposal_id,root_id,actor_kind,file_count,operation_kind,recorded_at) SELECT a.apply_id,a.draft_id,d.proposal_id,d.root_id,'local_user',0,'rollback',$at FROM proposal_applies a JOIN proposal_apply_drafts d ON d.draft_id=a.draft_id WHERE a.apply_id=$id;", ("$at", at.ToString("O")), ("$id", apply.ToString("D")));
        }

        Assert.Throws<InvalidOperationException>(() => store.RecordEffectComparison(new(requestProposal, requestRevision, requestApply, [new(session, "pre", "case", null)]), at));
        Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_comparisons;"));
    }

    [Fact]
    public void RecordEffectComparison_CapturesCurrentHumanEvidenceAndTreatsDeletedObjectiveEvidenceAsMissing()
    {
        using var temp = new MonitorTempDirectory(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = Enumerable.Range(0, 6).Select(i => SeedComparableSession(temp.DatabasePath, i < 3 ? at.AddMinutes(-10 - i) : at.AddMinutes(10 + i))).ToArray();
        foreach (var id in sessions) store.UpsertHumanEvaluation(new(id, "expected", at));
        var changedAt = at.AddMinutes(1);
        store.UpsertHumanEvaluation(new(sessions[0], "problem", changedAt));
        var objective = AddObjective(store, temp.DatabasePath, sessions[1], ObjectiveResult.Pass, ObjectiveSeverity.Normal, at);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}")) { connection.Open(); Execute(connection, "DELETE FROM objective_evaluation_evidence WHERE objective_evaluation_id=$id; DELETE FROM objective_evaluations WHERE objective_evaluation_id=$id;", ("$id", objective.ToString("D"))); }

        var receipt = store.RecordEffectComparison(new(proposal, 1, apply, sessions.Select((id, i) => new EffectCohortSession(id, i < 3 ? "pre" : "post", "case", null)).ToArray()), at.AddHours(1));
        var detail = store.GetEffectComparison(receipt.ComparisonId)!;

        Assert.Equal(EffectVerdict.Improved, receipt.Result.Verdict);
        Assert.Contains(detail.Evidence, e => e.SessionId == sessions[0] && e.Kind == "human" && e.RecordedAt == changedAt);
        Assert.DoesNotContain(detail.Evidence, e => e.ReferenceId == objective.ToString("D"));
    }

    [Theory]
    [InlineData("after_cohort_session_evidence_insert")]
    [InlineData("after_effect_receipt_insert")]
    public void RecordEffectComparison_InjectedFailureRollsBackEveryComparisonRowAndVerification(string failurePoint)
    {
        using var temp = new MonitorTempDirectory();
        var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7();
        var setup = new SqliteSessionStore(temp.DatabasePath); setup.CreateSchema();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = SeedImprovedCohort(temp.DatabasePath, setup, at);
        var store = new SqliteSessionStore(temp.DatabasePath, point =>
        {
            if (point == failurePoint) throw new InvalidOperationException("injected comparison failure");
        });

        Assert.Throws<InvalidOperationException>(() => store.RecordEffectComparison(Request(proposal, apply, sessions), at.AddHours(1)));

        AssertComparisonRows(temp.DatabasePath, 0);
        Assert.Equal(ImprovementProposalStatus.Recommended, setup.GetImprovementProposal(proposal)!.Status);
    }

    [Fact]
    public async Task RecordEffectComparison_SerializesRollbackAfterActiveApplyReadAndKeepsHistoricalVerifiedReceipt()
    {
        using var temp = new MonitorTempDirectory();
        var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7();
        var setup = new SqliteSessionStore(temp.DatabasePath); setup.CreateSchema();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = SeedImprovedCohort(temp.DatabasePath, setup, at);
        using var comparisonAtApplyRead = new ManualResetEventSlim();
        using var releaseComparison = new ManualResetEventSlim();
        var comparing = new SqliteSessionStore(temp.DatabasePath, point =>
        {
            if (point != "after_active_apply_read") return;
            comparisonAtApplyRead.Set();
            releaseComparison.Wait();
        });
        var compareTask = Task.Run(() => comparing.RecordEffectComparison(Request(proposal, apply, sessions), at.AddHours(1)));
        comparisonAtApplyRead.Wait();
        var rollbackStarted = new ManualResetEventSlim();
        var rollbackTask = Task.Run(() =>
        {
            rollbackStarted.Set();
            return StartRollback(setup, temp.DatabasePath, proposal, apply, at.AddHours(2));
        });
        rollbackStarted.Wait();
        releaseComparison.Set();
        var receipt = await compareTask;
        Assert.True(await rollbackTask);
        CompleteRollback(setup, temp.DatabasePath, proposal, apply, at.AddHours(3));

        var restarted = new SqliteSessionStore(temp.DatabasePath);
        var first = restarted.GetEffectComparison(receipt.ComparisonId)!.Receipt;
        var second = restarted.GetEffectComparison(receipt.ComparisonId)!.Receipt;
        Assert.Equal(EffectVerdict.Improved, first.Result.Verdict);
        Assert.Equal("invalidated", first.VerificationState);
        Assert.Equal(first.ComparisonId, second.ComparisonId);
        Assert.Equal(first.CohortRevision, second.CohortRevision);
        Assert.Equal(first.RecordedAt, second.RecordedAt);
        Assert.Equal(first.Result, second.Result, new EffectVerdictResultComparer());
        Assert.Equal(ImprovementProposalStatus.Verified, restarted.GetImprovementProposal(proposal)!.Status);
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM effect_receipts;"));
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("apply")]
    public void RecordEffectComparison_RejectsWhenApplyOperationIsPendingWithoutPersistingReceipt(string operationKind)
    {
        using var temp = new MonitorTempDirectory(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00"); var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = SeedImprovedCohort(temp.DatabasePath, store, at);
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            Execute(connection, "INSERT INTO proposal_apply_pending(apply_id,draft_id,proposal_id,root_id,actor_kind,file_count,operation_kind,recorded_at) SELECT a.apply_id,a.draft_id,d.proposal_id,d.root_id,'local_user',0,$kind,$at FROM proposal_applies a JOIN proposal_apply_drafts d ON d.draft_id=a.draft_id WHERE a.apply_id=$apply;", ("$kind", operationKind), ("$at", at.ToString("O")), ("$apply", apply.ToString("D")));
        }

        Assert.Throws<InvalidOperationException>(() => store.RecordEffectComparison(Request(proposal, apply, sessions), at.AddHours(1)));
        AssertComparisonRows(temp.DatabasePath, 0);
        Assert.Equal(ImprovementProposalStatus.Recommended, store.GetImprovementProposal(proposal)!.Status);
    }

    [Fact]
    public void RecordEffectComparison_FailureDoesNotBlockRollbackRecovery()
    {
        using var temp = new MonitorTempDirectory(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var setup = new SqliteSessionStore(temp.DatabasePath); setup.CreateSchema();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = SeedImprovedCohort(temp.DatabasePath, setup, at);
        var failing = new SqliteSessionStore(temp.DatabasePath, point => { if (point == "after_effect_receipt_insert") throw new InvalidOperationException("injected"); });

        Assert.Throws<InvalidOperationException>(() => failing.RecordEffectComparison(Request(proposal, apply, sessions), at));
        Assert.True(StartRollback(setup, temp.DatabasePath, proposal, apply, at.AddMinutes(1)));
        CompleteRollback(setup, temp.DatabasePath, proposal, apply, at.AddMinutes(2));

        Assert.Empty(setup.ListProposalApplyPending());
        Assert.Equal("rolled_back", setup.ListApplicationReceipts(proposal).Single().State);
        AssertComparisonRows(temp.DatabasePath, 0);
    }

    private static void SeedProposalAndApply(string databasePath, Guid proposalId, Guid applyId, DateTimeOffset appliedAt)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        Execute(connection, "INSERT INTO improvement_proposals(proposal_id,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at,recommended_at) VALUES($id,'recommended','skill','fixture','fixture','fixture','fixture','fixture',$at,$at,$at);", ("$id", proposalId.ToString("D")), ("$at", appliedAt.ToString("O")));
        var draft = Guid.CreateVersion7();
        Execute(connection, "INSERT INTO proposal_apply_drafts(draft_id,proposal_id,proposal_revision,root_id,selection_revision,approval_digest,state,created_at,updated_at) VALUES($draft,$proposal,1,$root,1,'digest','applied',$at,$at);", ("$draft", draft.ToString("D")), ("$proposal", proposalId.ToString("D")), ("$root", Guid.CreateVersion7().ToString("D")), ("$at", appliedAt.ToString("O")));
        Execute(connection, "INSERT INTO proposal_applies(apply_id,draft_id,proposal_revision,state,created_at) VALUES($apply,$draft,1,'applied',$at);", ("$apply", applyId.ToString("D")), ("$draft", draft.ToString("D")), ("$at", appliedAt.ToString("O")));
    }

    private static Guid[] SeedImprovedCohort(string databasePath, SqliteSessionStore store, DateTimeOffset at)
    {
        var sessions = Enumerable.Range(0, 6).Select(i => SeedComparableSession(databasePath, i < 3 ? at.AddMinutes(-10 - i) : at.AddMinutes(10 + i))).ToArray();
        foreach (var (id, index) in sessions.Select((id, index) => (id, index))) store.UpsertHumanEvaluation(new(id, index < 3 ? "problem" : "expected", at));
        return sessions;
    }

    private static EffectComparisonRequest Request(Guid proposal, Guid apply, IReadOnlyList<Guid> sessions) => new(proposal, 1, apply, sessions.Select((id, i) => new EffectCohortSession(id, i < 3 ? "pre" : "post", "case", null)).ToArray());

    private static void AssertComparisonRows(string databasePath, long expected)
    {
        Assert.Equal(expected, Scalar(databasePath, "SELECT COUNT(*) FROM effect_comparisons;"));
        Assert.Equal(expected == 0 ? 0 : expected * 6, Scalar(databasePath, "SELECT COUNT(*) FROM effect_comparison_sessions;"));
        Assert.Equal(expected == 0 ? 0 : expected * 6, Scalar(databasePath, "SELECT COUNT(*) FROM effect_comparison_evidence;"));
        Assert.Equal(expected, Scalar(databasePath, "SELECT COUNT(*) FROM effect_receipts;"));
    }

    private static bool StartRollback(SqliteSessionStore store, string databasePath, Guid proposal, Guid apply, DateTimeOffset at)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}"); connection.Open();
        var draft = Guid.Parse(Text(connection, "SELECT draft_id FROM proposal_applies WHERE apply_id=$apply;", ("$apply", apply.ToString("D")))!);
        var root = Guid.Parse(Text(connection, "SELECT root_id FROM proposal_apply_drafts WHERE draft_id=$draft;", ("$draft", draft.ToString("D")))!);
        return store.TryStartProposalApplyRollback(new(apply, draft, proposal, root, 0, "rollback", at));
    }

    private static void CompleteRollback(SqliteSessionStore store, string databasePath, Guid proposal, Guid apply, DateTimeOffset at)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}"); connection.Open();
        var draft = Guid.Parse(Text(connection, "SELECT draft_id FROM proposal_applies WHERE apply_id=$apply;", ("$apply", apply.ToString("D")))!);
        var root = Guid.Parse(Text(connection, "SELECT root_id FROM proposal_apply_drafts WHERE draft_id=$draft;", ("$draft", draft.ToString("D")))!);
        store.CompleteProposalApplyPending(new(apply, draft, ProposalApplyState.RolledBack, at), proposal, root, 0, null);
    }

    private static Guid SeedComparableSession(string databasePath, DateTimeOffset boundary, long? totalTokens = 10)
    {
        var id = Guid.CreateVersion7();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        Execute(connection, "INSERT INTO sessions(session_id,status,completeness,started_at,ended_at,last_seen_at,raw_retention_state,created_at,updated_at) VALUES($id,'completed','full',$start,$end,$end,'not_captured',$start,$end);", ("$id", id.ToString("D")), ("$start", boundary.ToString("O")), ("$end", boundary.AddMinutes(1).ToString("O")));
        Execute(connection, "INSERT INTO session_native_ids(session_id,source_surface,native_session_id,binding_kind,observed_at) VALUES($id,'copilot-sdk',$native,'native',$at);", ("$id", id.ToString("D")), ("$native", "native-" + id.ToString("N")), ("$at", boundary.ToString("O")));
        Execute(connection, "INSERT INTO session_runs(run_id,session_id,trace_id,status,total_tokens) VALUES($run,$id,$trace,'completed',$tokens);", ("$run", Guid.CreateVersion7().ToString("D")), ("$id", id.ToString("D")), ("$trace", "trace-" + id.ToString("N")), ("$tokens", totalTokens));
        return id;
    }

    private static void AddSevereObjective(SqliteSessionStore store, string databasePath, Guid sessionId, DateTimeOffset recordedAt) =>
        _ = AddObjective(store, databasePath, sessionId, ObjectiveResult.Fail, ObjectiveSeverity.Severe, recordedAt);

    private static Guid AddObjective(SqliteSessionStore store, string databasePath, Guid sessionId, ObjectiveResult result, ObjectiveSeverity severity, DateTimeOffset recordedAt)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}"); connection.Open();
        var run = Text(connection, "SELECT run_id FROM session_runs WHERE session_id=$id;", ("$id", sessionId.ToString("D")))!;
        var trace = Text(connection, "SELECT trace_id FROM session_runs WHERE session_id=$id;", ("$id", sessionId.ToString("D")))!;
        var objective = Guid.CreateVersion7();
        store.CreateObjectiveEvaluation(new(objective, sessionId, Guid.Parse(run), trace, result, severity, "eval", "v1", "criterion", "case", [new("run", run)], recordedAt));
        return objective;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static long Scalar(string databasePath, string sql, params (string Name, object? Value)[] values)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return (long)command.ExecuteScalar()!;
    }

    private static string? Text(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
        return command.ExecuteScalar() as string;
    }

    private sealed class EffectVerdictResultComparer : IEqualityComparer<EffectVerdictResult>
    {
        public bool Equals(EffectVerdictResult? x, EffectVerdictResult? y) => x is not null && y is not null
            && x.Verdict == y.Verdict && x.PrePass == y.PrePass && x.PreCount == y.PreCount && x.PostPass == y.PostPass && x.PostCount == y.PostCount
            && x.PreDurationMedian == y.PreDurationMedian && x.PostDurationMedian == y.PostDurationMedian && x.DurationDelta == y.DurationDelta
            && x.PreTokenMedian == y.PreTokenMedian && x.PostTokenMedian == y.PostTokenMedian && x.TokenDelta == y.TokenDelta
            && x.Reasons.SequenceEqual(y.Reasons);
        public int GetHashCode(EffectVerdictResult value) => value.GetHashCode();
    }
}

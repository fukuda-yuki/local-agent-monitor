using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.LocalMonitor;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteEffectComparisonStoreTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void CreateSchema_UpgradesGenuineSupportedPriorSchema_AdditivelyAndPreservesLegacyRows(int previousVersion)
    {
        using var temp = new MonitorTempDirectory();
        var legacy = CreateGenuineLegacySchema(temp.DatabasePath, previousVersion);
        Assert.Equal(previousVersion == 9 ? 1L : 0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='effect_comparisons';"));
        Assert.Equal(previousVersion >= 2 ? 1L : 0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='session_human_evaluation';"));
        Assert.Equal(previousVersion >= 3 ? 1L : 0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='improvement_proposals';"));
        Assert.Equal(previousVersion >= 4 ? 1L : 0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='proposal_applies';"));
        Assert.Equal(previousVersion >= 6 ? 1L : 0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='proposal_apply_pending';"));
        Assert.Equal(previousVersion >= 8 ? 1L : 0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='objective_evaluations';"));
        if (previousVersion < 7)
            Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('improvement_proposals') WHERE name='revision';"));
        if (previousVersion == 4)
            Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('proposal_apply_drafts') WHERE name='updated_at';"));
        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();

        using var verify = new SqliteConnection($"Data Source={temp.DatabasePath}");
        verify.Open();
        Assert.Equal(10L, Scalar(temp.DatabasePath, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(legacy.SessionId.ToString("D"), Text(verify, "SELECT session_id FROM sessions LIMIT 1;"));
        if (previousVersion >= 2) Assert.Equal("expected", Text(verify, "SELECT verdict FROM session_human_evaluation WHERE session_id=$session;", ("$session", legacy.SessionId.ToString("D"))));
        if (previousVersion >= 3) Assert.Equal("legacy", Text(verify, "SELECT title FROM improvement_proposals LIMIT 1;"));
        if (previousVersion >= 4) Assert.Equal("applied", Text(verify, "SELECT state FROM proposal_applies LIMIT 1;"));
        if (previousVersion is 4 or 5)
        {
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='proposal_apply_pending';"));
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('improvement_proposals') WHERE name='revision';"));
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('proposal_apply_drafts') WHERE name='proposal_revision';"));
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('proposal_applies') WHERE name='proposal_revision';"));
            Assert.Single(store.ListApplicationReceipts(legacy.ProposalId));
        }
        foreach (var table in new[] { "effect_comparisons", "effect_comparison_sessions", "effect_comparison_evidence", "effect_receipts" })
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;", ("$name", table)));
        foreach (var forbidden in new[] { "raw", "prompt", "response", "path", "source", "diff", "replacement", "snapshot", "credential", "token", "note" })
            Assert.Equal(0L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_comparisons') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden))
                + Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_comparison_sessions') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden))
                + Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_comparison_evidence') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden))
                + Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('effect_receipts') WHERE lower(name) LIKE '%' || $name || '%';", ("$name", forbidden)));

        if (previousVersion != 9) return;
        var detail = store.GetEffectComparison(legacy.ComparisonId!.Value)!;
        Assert.Equal(legacy.ComparisonId, detail.Receipt.ComparisonId);
        Assert.Equal(EffectVerdict.NoChange, detail.Receipt.Result.Verdict);
        Assert.Equal(legacy.SessionId, Assert.Single(detail.Sessions).SessionId);
        Assert.Equal("case", detail.Sessions.Single().CaseKey);
        Assert.Equal(legacy.SessionId, Assert.Single(detail.Evidence).SessionId);
        Assert.Equal(legacy.SessionId.ToString("D"), detail.Evidence.Single().ReferenceId);
        Assert.Null(detail.Sessions.Single().EffectiveQuality);
        Assert.False(detail.Sessions.Single().SevereFailure);
        Assert.Null(detail.Evidence.Single().HumanVerdict);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void CreateSchema_repairs_known_v10_stamped_issue55_gaps_without_losing_apply_data(int historicalVersion)
    {
        using var temp = new MonitorTempDirectory();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, DateTimeOffset.Parse("2026-07-12T12:00:00+00:00"));
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            Execute(connection, "UPDATE schema_version SET version=10 WHERE component='session';");
            Execute(connection, "ALTER TABLE improvement_proposals DROP COLUMN revision; ALTER TABLE proposal_apply_drafts DROP COLUMN proposal_revision; ALTER TABLE proposal_applies DROP COLUMN proposal_revision;");
            if (historicalVersion == 4) Execute(connection, "DROP TABLE proposal_apply_pending;");
        }

        var store = new SqliteSessionStore(temp.DatabasePath);
        store.CreateSchema();

        using var verify = new SqliteConnection($"Data Source={temp.DatabasePath}");
        verify.Open();
        Assert.Equal("fixture", Text(verify, "SELECT title FROM improvement_proposals WHERE proposal_id=$id;", ("$id", proposal.ToString("D"))));
        Assert.Equal("applied", Text(verify, "SELECT state FROM proposal_applies WHERE apply_id=$id;", ("$id", apply.ToString("D"))));
        Assert.Single(store.ListApplicationReceipts(proposal));
        Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='proposal_apply_pending';"));
        foreach (var (table, column) in new[] { ("improvement_proposals", "revision"), ("proposal_apply_drafts", "proposal_revision"), ("proposal_applies", "proposal_revision") })
            Assert.Equal(1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column;", ("$table", table), ("$column", column)));
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("pending")]
    public void CreateSchema_rejects_unknown_stamped_v10_gaps_without_schema_or_data_mutation(string missingObject)
    {
        using var temp = new MonitorTempDirectory();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, DateTimeOffset.Parse("2026-07-12T12:00:00+00:00"));
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            Execute(connection, "UPDATE schema_version SET version=10 WHERE component='session';");
            Execute(connection, missingObject == "revision"
                ? "ALTER TABLE proposal_applies DROP COLUMN proposal_revision;"
                : "DROP TABLE proposal_apply_pending;");
        }

        Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(temp.DatabasePath).CreateSchema());

        using var verify = new SqliteConnection($"Data Source={temp.DatabasePath}");
        verify.Open();
        Assert.Equal("fixture", Text(verify, "SELECT title FROM improvement_proposals WHERE proposal_id=$id;", ("$id", proposal.ToString("D"))));
        Assert.Equal("applied", Text(verify, "SELECT state FROM proposal_applies WHERE apply_id=$id;", ("$id", apply.ToString("D"))));
        Assert.Equal(10L, Scalar(temp.DatabasePath, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(missingObject == "pending" ? 0L : 1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='proposal_apply_pending';"));
        Assert.Equal(missingObject == "revision" ? 0L : 1L, Scalar(temp.DatabasePath, "SELECT COUNT(*) FROM pragma_table_info('proposal_applies') WHERE name='proposal_revision';"));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void MonitorHost_starts_and_reads_current_application_after_genuine_legacy_migration(int historicalVersion)
    {
        using var temp = new MonitorTempDirectory();
        var legacy = CreateGenuineLegacySchema(temp.DatabasePath, historicalVersion);

        using var app = MonitorHost.Build(new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes), new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, UseUserSecrets = false });

        var store = app.Services.GetRequiredService<ISessionStore>();
        Assert.Single(store.ListApplicationReceipts(legacy.ProposalId));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void MonitorHost_starts_and_reads_current_application_after_known_stamped_v10_repair(int historicalVersion)
    {
        using var temp = new MonitorTempDirectory();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, DateTimeOffset.Parse("2026-07-12T12:00:00+00:00"));
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath}"))
        {
            connection.Open();
            Execute(connection, "ALTER TABLE improvement_proposals DROP COLUMN revision; ALTER TABLE proposal_apply_drafts DROP COLUMN proposal_revision; ALTER TABLE proposal_applies DROP COLUMN proposal_revision;");
            if (historicalVersion == 4) Execute(connection, "DROP TABLE proposal_apply_pending;");
        }

        using var app = MonitorHost.Build(new MonitorOptions(temp.DatabasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes), new MonitorHostTestOptions { StartWriter = false, StartProjectionWorker = false, UseUserSecrets = false });

        var store = app.Services.GetRequiredService<ISessionStore>();
        Assert.Single(store.ListApplicationReceipts(proposal));
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

    [Fact]
    public void EffectComparisonRead_RestartsWithCapturedHumanVerdictAndSessionFactsAfterHumanEvaluationChanges()
    {
        using var temp = new MonitorTempDirectory();
        var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = SeedImprovedCohort(temp.DatabasePath, store, at);
        var receipt = store.RecordEffectComparison(Request(proposal, apply, sessions), at.AddHours(1));
        var before = store.GetEffectComparison(receipt.ComparisonId)!;

        store.UpsertHumanEvaluation(new(sessions[0], "expected", at.AddHours(2)));
        store.ClearHumanEvaluation(sessions[1]);

        var after = new SqliteSessionStore(temp.DatabasePath).GetEffectComparison(receipt.ComparisonId)!;
        Assert.Equal(before.Sessions, after.Sessions);
        Assert.Equal(before.Evidence, after.Evidence);
        Assert.Equal("problem", Assert.Single(after.Evidence, evidence => evidence.SessionId == sessions[0] && evidence.Kind == "human").HumanVerdict);
        Assert.Equal("fail", Assert.Single(after.Sessions, session => session.SessionId == sessions[0]).EffectiveQuality);
        Assert.False(Assert.Single(after.Sessions, session => session.SessionId == sessions[0]).SevereFailure);
        Assert.Equal("fail", Assert.Single(after.Sessions, session => session.SessionId == sessions[1]).EffectiveQuality);
        Assert.Equal(EffectVerdict.Improved, after.Receipt.Result.Verdict);
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
    public async Task RecordEffectComparison_RollbackStartsFirst_ObservesPendingStateWithoutReceiptOrMaturityChange()
    {
        using var temp = new MonitorTempDirectory(); var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        var proposal = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var store = new SqliteSessionStore(temp.DatabasePath); store.CreateSchema();
        SeedProposalAndApply(temp.DatabasePath, proposal, apply, at);
        var sessions = SeedImprovedCohort(temp.DatabasePath, store, at);
        using var rollbackStarted = new ManualResetEventSlim();
        var rollbackTask = Task.Run(() => { var started = StartRollback(store, temp.DatabasePath, proposal, apply, at.AddHours(1)); rollbackStarted.Set(); return started; });
        rollbackStarted.Wait();
        Assert.True(await rollbackTask);

        Assert.Throws<InvalidOperationException>(() => store.RecordEffectComparison(Request(proposal, apply, sessions), at.AddHours(2)));
        AssertComparisonRows(temp.DatabasePath, 0);
        Assert.Equal(ImprovementProposalStatus.Recommended, store.GetImprovementProposal(proposal)!.Status);
        CompleteRollback(store, temp.DatabasePath, proposal, apply, at.AddHours(3));
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

    private static LegacyFixture CreateGenuineLegacySchema(string databasePath, int version)
    {
        var at = DateTimeOffset.Parse("2026-07-12T12:00:00+00:00");
        var session = Guid.CreateVersion7(); var proposal = Guid.CreateVersion7(); var draft = Guid.CreateVersion7(); var apply = Guid.CreateVersion7(); var comparison = Guid.CreateVersion7();
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        Execute(connection, "PRAGMA foreign_keys=ON; CREATE TABLE schema_version(component TEXT PRIMARY KEY,version INTEGER NOT NULL); INSERT INTO schema_version(component,version) VALUES('session',$version); CREATE TABLE sessions(session_id TEXT PRIMARY KEY,status TEXT NOT NULL,completeness TEXT NOT NULL,repository TEXT NULL,workspace TEXT NULL,started_at TEXT NULL,ended_at TEXT NULL,last_seen_at TEXT NOT NULL,raw_retention_state TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL); CREATE TABLE session_runs(run_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,trace_id TEXT NULL,status TEXT NOT NULL,total_tokens INTEGER NULL);", ("$version", version));
        Execute(connection, "INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) VALUES($id,'completed','full',$at,'not_captured',$at,$at); INSERT INTO session_runs(run_id,session_id,trace_id,status,total_tokens) VALUES($run,$id,'trace-legacy','completed',1);", ("$id", session.ToString("D")), ("$run", Guid.CreateVersion7().ToString("D")), ("$at", at.ToString("O")));
        if (version >= 2) CreateLegacyHumanSchema(connection, session, at);
        if (version >= 3) CreateLegacyProposalSchema(connection, proposal, version >= 7, at);
        if (version >= 4) CreateLegacyApplySchema(connection, proposal, draft, apply, version >= 5, version >= 7, at);
        if (version >= 6) Execute(connection, "CREATE TABLE proposal_apply_pending(apply_id TEXT PRIMARY KEY,draft_id TEXT NOT NULL,proposal_id TEXT NOT NULL,root_id TEXT NOT NULL,actor_kind TEXT NOT NULL,file_count INTEGER NOT NULL,operation_kind TEXT NOT NULL,recorded_at TEXT NOT NULL);");
        if (version >= 8) CreateLegacyObjectiveSchema(connection, session, at);
        if (version == 9) CreateLegacyEffectSchema(connection, session, proposal, apply, comparison, at);
        return new(session, proposal, apply, version == 9 ? comparison : null);
    }

    private static void CreateLegacyHumanSchema(SqliteConnection connection, Guid session, DateTimeOffset at) =>
        Execute(connection, "CREATE TABLE session_human_evaluation(session_id TEXT PRIMARY KEY,verdict TEXT NOT NULL,recorded_at TEXT NOT NULL); INSERT INTO session_human_evaluation(session_id,verdict,recorded_at) VALUES($id,'expected',$at);", ("$id", session.ToString("D")), ("$at", at.ToString("O")));

    private static void CreateLegacyProposalSchema(SqliteConnection connection, Guid proposal, bool withRevision, DateTimeOffset at)
    {
        Execute(connection, $"CREATE TABLE improvement_proposals(proposal_id TEXT PRIMARY KEY,{(withRevision ? "revision INTEGER NOT NULL DEFAULT 1," : string.Empty)}status TEXT NOT NULL,target_kind TEXT NOT NULL,target_label TEXT NOT NULL,title TEXT NOT NULL,summary TEXT NOT NULL,expected_effect TEXT NOT NULL,risk_note TEXT NOT NULL,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,recommended_at TEXT NULL,verified_at TEXT NULL); CREATE TABLE improvement_proposal_sessions(proposal_id TEXT NOT NULL,proposal_revision INTEGER NOT NULL DEFAULT 1,session_id TEXT NOT NULL,source_order INTEGER NOT NULL,PRIMARY KEY(proposal_id,session_id)); CREATE TABLE improvement_proposal_evidence(proposal_id TEXT NOT NULL,evidence_order INTEGER NOT NULL,kind TEXT NOT NULL,reference_id TEXT NOT NULL,PRIMARY KEY(proposal_id,evidence_order)); INSERT INTO improvement_proposals(proposal_id,{(withRevision ? "revision," : string.Empty)}status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at,recommended_at) VALUES($proposal,{(withRevision ? "1," : string.Empty)}'recommended','skill','legacy','legacy','legacy','legacy','legacy',$at,$at,$at);", ("$proposal", proposal.ToString("D")), ("$at", at.ToString("O")));
    }

    private static void CreateLegacyApplySchema(SqliteConnection connection, Guid proposal, Guid draft, Guid apply, bool withUpdatedAt, bool withRevision, DateTimeOffset at)
    {
        var updated = withUpdatedAt ? ",updated_at TEXT NOT NULL" : string.Empty;
        var revisions = withRevision ? ",proposal_revision INTEGER NOT NULL DEFAULT 1" : string.Empty;
        Execute(connection, $"CREATE TABLE proposal_apply_drafts(draft_id TEXT PRIMARY KEY,proposal_id TEXT NOT NULL{revisions},root_id TEXT NOT NULL,selection_revision INTEGER NOT NULL,approval_digest TEXT NOT NULL,state TEXT NOT NULL,created_at TEXT NOT NULL{updated}); CREATE TABLE proposal_apply_files(draft_id TEXT NOT NULL,file_order INTEGER NOT NULL,base_sha256 TEXT NOT NULL,replacement_sha256 TEXT NOT NULL,PRIMARY KEY(draft_id,file_order)); CREATE TABLE proposal_apply_hunks(draft_id TEXT NOT NULL,hunk_id TEXT NOT NULL,selected INTEGER NOT NULL,replacement_sha256 TEXT NOT NULL,PRIMARY KEY(draft_id,hunk_id)); CREATE TABLE proposal_apply_revisions(draft_id TEXT NOT NULL,selection_revision INTEGER NOT NULL,approval_digest TEXT NOT NULL,approved_at TEXT NULL,PRIMARY KEY(draft_id,selection_revision)); CREATE TABLE proposal_applies(apply_id TEXT PRIMARY KEY,draft_id TEXT NOT NULL{revisions},state TEXT NOT NULL,created_at TEXT NOT NULL); CREATE TABLE proposal_apply_audit(audit_id INTEGER PRIMARY KEY,apply_id TEXT NULL,draft_id TEXT NULL,proposal_id TEXT NOT NULL,root_id TEXT NOT NULL,actor_kind TEXT NOT NULL,state TEXT NOT NULL,error_code TEXT NULL,file_count INTEGER NOT NULL,recorded_at TEXT NOT NULL); INSERT INTO proposal_apply_drafts(draft_id,proposal_id{(withRevision ? ",proposal_revision" : string.Empty)},root_id,selection_revision,approval_digest,state,created_at{(withUpdatedAt ? ",updated_at" : string.Empty)}) VALUES($draft,$proposal{(withRevision ? ",1" : string.Empty)},$root,1,'legacy','applied',$at{(withUpdatedAt ? ",$at" : string.Empty)}); INSERT INTO proposal_applies(apply_id,draft_id{(withRevision ? ",proposal_revision" : string.Empty)},state,created_at) VALUES($apply,$draft{(withRevision ? ",1" : string.Empty)},'applied',$at);", ("$proposal", proposal.ToString("D")), ("$draft", draft.ToString("D")), ("$apply", apply.ToString("D")), ("$root", Guid.CreateVersion7().ToString("D")), ("$at", at.ToString("O")));
    }

    private static void CreateLegacyObjectiveSchema(SqliteConnection connection, Guid session, DateTimeOffset at) =>
        Execute(connection, "CREATE TABLE objective_evaluations(objective_evaluation_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,run_id TEXT NOT NULL,trace_id TEXT NOT NULL,result TEXT NOT NULL,severity TEXT NOT NULL,evaluator_id TEXT NOT NULL,evaluator_version TEXT NOT NULL,criterion_id TEXT NOT NULL,case_key TEXT NOT NULL,recorded_at TEXT NOT NULL); CREATE TABLE objective_evaluation_evidence(objective_evaluation_id TEXT NOT NULL,evidence_order INTEGER NOT NULL,kind TEXT NOT NULL,reference_id TEXT NOT NULL,PRIMARY KEY(objective_evaluation_id,evidence_order)); INSERT INTO objective_evaluations(objective_evaluation_id,session_id,run_id,trace_id,result,severity,evaluator_id,evaluator_version,criterion_id,case_key,recorded_at) SELECT $objective,session_id,run_id,trace_id,'pass','normal','legacy','v1','legacy','case',$at FROM session_runs WHERE session_id=$session;", ("$objective", Guid.CreateVersion7().ToString("D")), ("$session", session.ToString("D")), ("$at", at.ToString("O")));

    private static void CreateLegacyEffectSchema(SqliteConnection connection, Guid session, Guid proposal, Guid apply, Guid comparison, DateTimeOffset at)
    {
        var result = System.Text.Json.JsonSerializer.Serialize(new EffectVerdictResult(EffectVerdict.NoChange, 1, 1, 1, 1, null, null, null, null, null, null, ["legacy"]));
        Execute(connection, "CREATE TABLE effect_comparisons(comparison_id TEXT PRIMARY KEY,cohort_revision INTEGER NOT NULL,proposal_id TEXT NOT NULL,proposal_revision INTEGER NOT NULL,apply_id TEXT NOT NULL,recorded_at TEXT NOT NULL); CREATE TABLE effect_comparison_sessions(comparison_id TEXT NOT NULL,session_id TEXT NOT NULL,classification TEXT NOT NULL,case_key TEXT NOT NULL,exclusion_reason TEXT NULL,session_order INTEGER NOT NULL,PRIMARY KEY(comparison_id,session_id)); CREATE TABLE effect_comparison_evidence(comparison_id TEXT NOT NULL,evidence_order INTEGER NOT NULL,session_id TEXT NOT NULL,kind TEXT NOT NULL,reference_id TEXT NOT NULL,recorded_at TEXT NULL,PRIMARY KEY(comparison_id,evidence_order)); CREATE TABLE effect_receipts(comparison_id TEXT PRIMARY KEY,verdict TEXT NOT NULL,result_json TEXT NOT NULL,recorded_at TEXT NOT NULL); INSERT INTO effect_comparisons(comparison_id,cohort_revision,proposal_id,proposal_revision,apply_id,recorded_at) VALUES($comparison,1,$proposal,1,$apply,$at); INSERT INTO effect_comparison_sessions(comparison_id,session_id,classification,case_key,exclusion_reason,session_order) VALUES($comparison,$session,'pre','case',NULL,0); INSERT INTO effect_comparison_evidence(comparison_id,evidence_order,session_id,kind,reference_id,recorded_at) VALUES($comparison,0,$session,'human',$session,$at); INSERT INTO effect_receipts(comparison_id,verdict,result_json,recorded_at) VALUES($comparison,'no_change',$result,$at);", ("$comparison", comparison.ToString("D")), ("$proposal", proposal.ToString("D")), ("$apply", apply.ToString("D")), ("$session", session.ToString("D")), ("$result", result), ("$at", at.ToString("O")));
    }

    private sealed record LegacyFixture(Guid SessionId, Guid ProposalId, Guid ApplyId, Guid? ComparisonId);

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

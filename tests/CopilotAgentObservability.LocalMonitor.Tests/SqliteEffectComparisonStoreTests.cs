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

    private static void SeedProposalAndApply(string databasePath, Guid proposalId, Guid applyId, DateTimeOffset appliedAt)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        Execute(connection, "INSERT INTO improvement_proposals(proposal_id,status,target_kind,target_label,title,summary,expected_effect,risk_note,created_at,updated_at,recommended_at) VALUES($id,'recommended','skill','fixture','fixture','fixture','fixture','fixture',$at,$at,$at);", ("$id", proposalId.ToString("D")), ("$at", appliedAt.ToString("O")));
        var draft = Guid.CreateVersion7();
        Execute(connection, "INSERT INTO proposal_apply_drafts(draft_id,proposal_id,proposal_revision,root_id,selection_revision,approval_digest,state,created_at,updated_at) VALUES($draft,$proposal,1,$root,1,'digest','applied',$at,$at);", ("$draft", draft.ToString("D")), ("$proposal", proposalId.ToString("D")), ("$root", Guid.CreateVersion7().ToString("D")), ("$at", appliedAt.ToString("O")));
        Execute(connection, "INSERT INTO proposal_applies(apply_id,draft_id,proposal_revision,state,created_at) VALUES($apply,$draft,1,'applied',$at);", ("$apply", applyId.ToString("D")), ("$draft", draft.ToString("D")), ("$at", appliedAt.ToString("O")));
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

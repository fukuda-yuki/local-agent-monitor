using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteSessionStoreTests
{
    private static SqliteSessionStore CreateRawStore(string databasePath, TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? new SessionStoreTimeProvider(DateTimeOffset.UnixEpoch);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(databasePath, clock);
        return new SqliteSessionStore(databasePath, context, clock);
    }

    [Fact]
    public void CreateSchema_CreatesExactSessionVersionFourteenOutcomeColumns()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);

        store.CreateSchema();

        using var connection = database.Open();
        Assert.Equal(14L, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(
            new[] { "match_kind", "terminal_outcome", "terminal_policy_version" },
            ReadColumns(connection, "session_events").TakeLast(3));
    }

    [Fact]
    public void Write_ReducesTerminalFactsWithFailedPrecedenceAndLatestTerminalTimestamp()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var at = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
        var batch = CreateBatch(at, "outcome-reducer");
        var clean = new ObservedSessionEvent(
            Guid.CreateVersion7(), batch.Detail.Session.SessionId, batch.Detail.Runs[0].RunId,
            SessionSourceSurface.CopilotSdk, null, "trace-1", null, "copilot-sdk-stream", "terminal-clean",
            "session.task_complete", at.AddMinutes(1), SessionContentState.NotCaptured);
        var failed = clean with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "terminal-failed",
            Type = "session.shutdown",
            OccurredAt = at.AddMinutes(2),
            ContentState = SessionContentState.Available,
        };
        var neutral = failed with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "terminal-neutral",
            OccurredAt = at.AddMinutes(3),
            ContentState = SessionContentState.NotCaptured,
        };
        batch = batch with
        {
            Detail = batch.Detail with
            {
                Session = batch.Detail.Session with
                {
                    Status = ObservedSessionStatus.Completed,
                    EndedAt = at.AddDays(1),
                    LastSeenAt = at.AddMinutes(3),
                },
                Events = [.. batch.Detail.Events, clean, failed, neutral],
            },
            Content = [.. batch.Content, new SessionEventContent(
                failed.EventId, "application/json", "{\"shutdownType\":\"error\"}",
                failed.OccurredAt, failed.OccurredAt.AddDays(90))],
        };

        WriteClassified(store, batch,
            new(clean.EventId, SessionTerminalOutcome.Clean),
            new(failed.EventId, SessionTerminalOutcome.Failed),
            new(neutral.EventId, SessionTerminalOutcome.Neutral));

        var detail = store.GetDetail(batch.Detail.Session.SessionId)!;
        Assert.Equal(ObservedSessionStatus.Failed, detail.Session.Status);
        Assert.Equal(at.AddMinutes(3), detail.Session.EndedAt);
        using var connection = database.Open();
        Assert.Equal(
            new[]
            {
                "terminal-clean|clean|1",
                "terminal-failed|failed|1",
                "terminal-neutral|neutral|1",
            },
            ReadStrings(connection, "SELECT source_event_id||'|'||terminal_outcome||'|'||terminal_policy_version FROM session_events WHERE terminal_outcome IS NOT NULL ORDER BY source_event_id;"));
    }

    [Fact]
    public void Write_StopOnlyDoesNotPreserveCallerTerminalState()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var at = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
        var batch = CreateBatch(at, "stop-only");
        var stop = new ObservedSessionEvent(
            Guid.CreateVersion7(), batch.Detail.Session.SessionId, batch.Detail.Runs[0].RunId,
            SessionSourceSurface.CopilotSdk, null, "trace-1", "error", "copilot-sdk-stream", "stop-only-event",
            "Stop", at.AddMinutes(1), SessionContentState.NotCaptured);
        batch = batch with
        {
            Detail = batch.Detail with
            {
                Session = batch.Detail.Session with
                {
                    Status = ObservedSessionStatus.Failed,
                    EndedAt = at.AddMinutes(1),
                    LastSeenAt = at.AddMinutes(1),
                },
                Events = [.. batch.Detail.Events, stop],
            },
        };

        store.Write(batch);

        var session = store.GetDetail(batch.Detail.Session.SessionId)!.Session;
        Assert.Equal(ObservedSessionStatus.Active, session.Status);
        Assert.Null(session.EndedAt);
    }

    [Fact]
    public void Write_ReplayFactMismatchRollsBackWholeBatch()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var at = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
        var first = CreateBatch(at, "replay-fact");
        var terminal = first.Detail.Events[0] with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "replay-terminal",
            Type = "session.task_complete",
            ContentState = SessionContentState.NotCaptured,
        };
        first = first with { Detail = first.Detail with { Events = [terminal] }, Content = [] };
        store.Write(first);
        var conflicting = first with
        {
            Detail = first.Detail with
            {
                Events = [terminal with { EventId = Guid.CreateVersion7(), Type = "Stop" }],
            },
        };

        Assert.Throws<InvalidOperationException>(() => store.Write(conflicting));

        var detail = store.GetDetail(first.Detail.Session.SessionId)!;
        Assert.Equal(ObservedSessionStatus.Completed, detail.Session.Status);
        Assert.Equal("session.task_complete", Assert.Single(detail.Events).Type);
    }

    [Fact]
    public void Write_IdenticalSameBatchTerminalFactsReplayThroughCanonicalComparator()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.Parse("2026-08-09T00:00:00Z"), "same-batch-identical");
        var first = batch.Detail.Events[0] with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "same-batch-terminal",
            Type = "session.shutdown",
            ContentState = SessionContentState.NotCaptured,
        };
        var second = first with { EventId = Guid.CreateVersion7() };
        batch = batch with
        {
            Detail = batch.Detail with { Events = [first, second] },
            Content = [],
        };

        WriteClassified(
            store,
            batch,
            new(first.EventId, SessionTerminalOutcome.Clean),
            new(second.EventId, SessionTerminalOutcome.Clean));

        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal("clean|1", Scalar<string>(connection, "SELECT terminal_outcome||'|'||terminal_policy_version FROM session_events;"));
    }

    [Fact]
    public void Write_ContradictorySameBatchTerminalFactsRollBackAtomically()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.Parse("2026-08-09T00:00:00Z"), "same-batch-conflict");
        var first = batch.Detail.Events[0] with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "same-batch-terminal",
            Type = "session.shutdown",
            ContentState = SessionContentState.NotCaptured,
        };
        var second = first with { EventId = Guid.CreateVersion7() };
        batch = batch with
        {
            Detail = batch.Detail with { Events = [first, second] },
            Content = [],
        };

        Assert.Throws<InvalidOperationException>(() => WriteClassified(
            store,
            batch,
            new(first.EventId, SessionTerminalOutcome.Clean),
            new(second.EventId, SessionTerminalOutcome.Failed)));

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
    }

    [Fact]
    public void Write_MixedNewAndContradictoryReplayRollsBackWholeBatch()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var original = CreateBatch(DateTimeOffset.Parse("2026-08-09T00:00:00Z"), "mixed-replay");
        original = original with
        {
            Detail = original.Detail with
            {
                Events = [original.Detail.Events[0] with { ContentState = SessionContentState.NotCaptured }],
            },
            Content = [],
        };
        store.Write(original);
        var persisted = original.Detail.Events[0];
        var fresh = persisted with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "mixed-replay-new",
            Type = "Stop",
            ContentState = SessionContentState.Available,
        };
        var contradictoryReplay = persisted with
        {
            EventId = Guid.CreateVersion7(),
            Type = "Stop",
        };
        var mixed = original with
        {
            Detail = original.Detail with { Events = [fresh, contradictoryReplay] },
            Content = [new SessionEventContent(fresh.EventId, "application/json", "{}", fresh.OccurredAt, fresh.OccurredAt.AddDays(90))],
        };

        Assert.Throws<InvalidOperationException>(() => store.Write(mixed));

        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_native_ids;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_projection_state;"));
        Assert.Equal(persisted.SourceEventId, Scalar<string>(connection, "SELECT source_event_id FROM session_events;"));
        Assert.Equal(persisted.Type, Scalar<string>(connection, "SELECT type FROM session_events;"));
        Assert.Equal("null|null", Scalar<string>(connection, "SELECT typeof(terminal_outcome)||'|'||typeof(terminal_policy_version) FROM session_events;"));
        Assert.Equal(
            "active|partial|<null>|2026-08-09T00:00:00.0000000+00:00",
            Scalar<string>(connection, "SELECT status||'|'||completeness||'|'||IFNULL(ended_at,'<null>')||'|'||last_seen_at FROM sessions;"));
    }

    [Fact]
    public void Write_ExactReplayDoesNotBackfillMissingContent()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var at = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
        var first = CreateBatch(at, "replay-content");
        first = first with
        {
            Detail = first.Detail with
            {
                Events = [first.Detail.Events[0] with { ContentState = SessionContentState.NotCaptured }],
            },
            Content = [],
        };
        store.Write(first);
        var replayId = Guid.CreateVersion7();
        var replay = first with
        {
            Detail = first.Detail with { Events = [first.Detail.Events[0] with { EventId = replayId }] },
            Content = [new SessionEventContent(replayId, "application/json", "{}", at, at.AddDays(90))],
        };

        store.Write(replay);

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_ClassifiedLiveFactDoesNotDependOnRetainedDiscriminatorContent(bool retainFilteredContent)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var at = DateTimeOffset.Parse("2026-08-09T00:00:00Z");
        var batch = CreateBatch(at, "live-classified");
        var terminal = batch.Detail.Events[0] with
        {
            EventId = Guid.CreateVersion7(),
            SourceSurface = SessionSourceSurface.CopilotCli,
            SourceAdapter = "copilot-compatible-hook",
            SourceEventId = "classified-session-end",
            Type = "SessionEnd",
            ContentState = retainFilteredContent ? SessionContentState.Available : SessionContentState.NotCaptured,
        };
        batch = batch with
        {
            Detail = batch.Detail with { Events = [terminal] },
            Content = retainFilteredContent
                ? [new SessionEventContent(terminal.EventId, "application/json", "{}", at, at.AddDays(90))]
                : [],
        };

        WriteClassified(store, batch, new SessionTerminalFact(terminal.EventId, SessionTerminalOutcome.Failed));

        var session = store.GetDetail(batch.Detail.Session.SessionId)!.Session;
        Assert.Equal(ObservedSessionStatus.Failed, session.Status);
        using var connection = database.Open();
        Assert.Equal("failed|1", Scalar<string>(connection, "SELECT terminal_outcome||'|'||terminal_policy_version FROM session_events;"));
    }

    [Fact]
    public void ObjectiveEvaluations_RejectNonVersionSevenReceiptIdentifiers()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "objective-v4");
        var full = batch with { Detail = batch.Detail with { Session = batch.Detail.Session with { Completeness = SessionCompleteness.Full } } };
        store.Write(full);
        var receipt = new ObjectiveEvaluationReceipt(Guid.NewGuid(), full.Detail.Session.SessionId, full.Detail.Runs[0].RunId, full.Detail.Runs[0].TraceId!, ObjectiveResult.Fail, ObjectiveSeverity.Normal, "eval", "v1", "criterion", "case", [new("run", full.Detail.Runs[0].RunId.ToString("D"))], DateTimeOffset.UnixEpoch);

        Assert.Throws<ArgumentException>(() => store.CreateObjectiveEvaluation(receipt));
    }

    [Fact]
    public void CreateSchema_upgrades_real_version_three_database_with_revisions_and_survives_restart()
    {
        using var database = new SessionTestDatabase();
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "session", "session-v3.sqlite");
        File.Copy(fixturePath, database.Path);
        var proposalId = Guid.Parse("00000004-0000-7000-8000-000000000003");
        using (var historical = database.Open())
        {
            Assert.Equal(3L, Scalar<long>(historical, "SELECT version FROM schema_version WHERE component='session';"));
            Assert.Equal(0L, Scalar<long>(historical, "SELECT COUNT(*) FROM pragma_table_info('improvement_proposals') WHERE name='revision';"));
            Assert.Equal(0L, Scalar<long>(historical, "SELECT COUNT(*) FROM pragma_table_info('improvement_proposal_sessions') WHERE name='proposal_revision';"));
        }

        new SqliteSessionStore(database.Path).CreateSchema();
        using (var migrated = database.Open())
        {
            Assert.Equal(14L, Scalar<long>(migrated, "SELECT version FROM schema_version WHERE component='session';"));
            Assert.Equal(proposalId.ToString("D"), Scalar<string>(migrated, "SELECT proposal_id FROM improvement_proposals;"));
        }

        new SqliteSessionStore(database.Path).CreateSchema();

        using var restarted = database.Open();
        Assert.Equal(1L, Scalar<long>(restarted, "SELECT revision FROM improvement_proposals WHERE proposal_id='00000004-0000-7000-8000-000000000003';"));
        Assert.Equal(1L, Scalar<long>(restarted, "SELECT proposal_revision FROM improvement_proposal_sessions WHERE proposal_id='00000004-0000-7000-8000-000000000003';"));
        Assert.Equal(1L, Scalar<long>(restarted, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='objective_evaluations';"));
        Assert.Equal(1L, Scalar<long>(restarted, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='objective_evaluation_evidence';"));
    }

    [Fact]
    public void ObjectiveEvaluations_RequireNativeExactBindingAsWellAsTerminalFullScope()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "unbound-objective");
        var fullButUnbound = batch with
        {
            Detail = batch.Detail with
            {
                Session = batch.Detail.Session with { Completeness = SessionCompleteness.Full },
                NativeIds = []
            }
        };
        store.Write(fullButUnbound);
        var receipt = new ObjectiveEvaluationReceipt(Guid.CreateVersion7(), fullButUnbound.Detail.Session.SessionId, fullButUnbound.Detail.Runs[0].RunId, fullButUnbound.Detail.Runs[0].TraceId!, ObjectiveResult.Fail, ObjectiveSeverity.Normal, "eval", "v1", "criterion", "case", [new("run", fullButUnbound.Detail.Runs[0].RunId.ToString("D"))], DateTimeOffset.UnixEpoch);

        Assert.Throws<ArgumentException>(() => store.CreateObjectiveEvaluation(receipt));
    }

    [Fact]
    public void ObjectiveEvaluations_PersistAcrossStoreRestartAndAreImmutable()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "objective");
        var full = batch with { Detail = batch.Detail with { Session = batch.Detail.Session with { Completeness = SessionCompleteness.Full } } };
        store.Write(full);
        var receipt = new ObjectiveEvaluationReceipt(Guid.CreateVersion7(), full.Detail.Session.SessionId, full.Detail.Runs[0].RunId, full.Detail.Runs[0].TraceId!, ObjectiveResult.Fail, ObjectiveSeverity.Severe, "eval", "v1", "criterion", "case", [new("run", full.Detail.Runs[0].RunId.ToString("D")), new("event", full.Detail.Events[0].EventId.ToString("D")), new("trace", full.Detail.Runs[0].TraceId!)], DateTimeOffset.UnixEpoch);

        store.CreateObjectiveEvaluation(receipt);

        var restarted = new SqliteSessionStore(database.Path);
        var persisted = Assert.Single(restarted.ListObjectiveEvaluations(full.Detail.Session.SessionId));
        Assert.Equal(receipt with { Evidence = [] }, persisted with { Evidence = [] });
        Assert.Equal(receipt.Evidence, persisted.Evidence);
        Assert.Throws<SqliteException>(() => restarted.CreateObjectiveEvaluation(receipt));
    }

    [Fact]
    public void ImprovementProposals_PersistCandidateWithOpaqueReferences()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "proposal-persist");
        store.Write(batch);
        var proposal = CreateProposal(batch);

        store.CreateImprovementProposal(proposal);

        var actual = Assert.Single(store.ListImprovementProposals(batch.Detail.Session.SessionId));
        Assert.Equal(proposal.ProposalId, actual.ProposalId);
        Assert.Equal(ImprovementProposalStatus.Candidate, actual.Status);
        Assert.Equal(proposal.EvidenceReferences, actual.EvidenceReferences);
    }

    [Fact]
    public void ImprovementProposals_GetByProposalIdReturnsOnlyTheRequestedProposal()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "proposal-get");
        store.Write(batch);
        var proposal = CreateProposal(batch);
        store.CreateImprovementProposal(proposal);

        var actual = store.GetImprovementProposal(proposal.ProposalId);

        Assert.NotNull(actual);
        Assert.Equal(proposal.ProposalId, actual.ProposalId);
        Assert.Equal(proposal.SourceSessionIds, actual.SourceSessionIds);
        Assert.Equal(proposal.EvidenceReferences, actual.EvidenceReferences);
        Assert.Null(store.GetImprovementProposal(Guid.CreateVersion7()));
    }

    [Fact]
    public void Promote_WhenAnySourceSessionAlreadyHasRecommendation_Throws()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "native-first");
        var second = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "native-second");
        store.Write(first);
        store.Write(second);
        var existing = CreateProposal([first, second]);
        var competing = CreateProposal([first, second]);
        store.CreateImprovementProposal(existing);
        store.CreateImprovementProposal(competing);
        store.UpdateImprovementProposalStatus(existing.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch);

        AssertProposalFailure(ImprovementProposalFailure.RecommendationAlreadyExists, () =>
            store.UpdateImprovementProposalStatus(
                competing.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ImprovementProposals_RejectVerifiedWrites()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch) with { Status = ImprovementProposalStatus.Verified };

        AssertProposalFailure(ImprovementProposalFailure.InvalidStatus, () => store.CreateImprovementProposal(proposal));
    }

    [Theory]
    [InlineData(ImprovementProposalStatus.Candidate)]
    [InlineData(ImprovementProposalStatus.Recommended)]
    public void ImprovementProposals_VerifiedProposalCannotBeChangedByCanvasStatusUpdates(ImprovementProposalStatus requestedStatus)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "native-verified-first");
        var second = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "native-verified-second");
        store.Write(first);
        store.Write(second);
        var proposal = CreateProposal([first, second]);
        store.CreateImprovementProposal(proposal);
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE improvement_proposals SET status='verified', verified_at='2026-07-12T00:00:00.0000000+00:00' WHERE proposal_id=$proposal_id;";
            command.Parameters.AddWithValue("$proposal_id", proposal.ProposalId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        AssertProposalFailure(ImprovementProposalFailure.VerificationOwnedByComparison, () =>
            store.UpdateImprovementProposalStatus(proposal.ProposalId, requestedStatus, DateTimeOffset.UnixEpoch.AddDays(1)));

        var actual = store.GetImprovementProposal(proposal.ProposalId)!;
        Assert.Equal(ImprovementProposalStatus.Verified, actual.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-07-12T00:00:00.0000000+00:00"), actual.VerifiedAt);
        Assert.Equal(proposal.UpdatedAt, actual.UpdatedAt);
        Assert.Null(actual.RecommendedAt);
    }

    [Fact]
    public void ImprovementProposals_CreateAcceptsOnlyCandidateWithoutLifecycleTimestamps()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch);

        AssertProposalFailure(ImprovementProposalFailure.InvalidStatus, () => store.CreateImprovementProposal(proposal with { Status = ImprovementProposalStatus.Recommended }));
        AssertProposalFailure(ImprovementProposalFailure.InvalidShape, () => store.CreateImprovementProposal(proposal with { RecommendedAt = DateTimeOffset.UnixEpoch }));
        AssertProposalFailure(ImprovementProposalFailure.InvalidShape, () => store.CreateImprovementProposal(proposal with { VerifiedAt = DateTimeOffset.UnixEpoch }));
    }

    [Fact]
    public void Promotion_RequiresTwoTerminalNativeSourceSessions()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "promotion-one-source");
        store.Write(batch);
        var proposal = CreateProposal(batch);
        store.CreateImprovementProposal(proposal);

        AssertProposalFailure(ImprovementProposalFailure.InsufficientRecommendationEvidence, () =>
            store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ImprovementProposals_CreateDistinguishesEvidenceOutsideClaimedSourceSession()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var source = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "proposal-source");
        var other = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "proposal-other");
        store.Write(source);
        store.Write(other);
        var proposal = CreateProposal(source) with
        {
            EvidenceReferences = [new ImprovementProposalEvidenceReference("event", other.Detail.Events[0].EventId.ToString("D"))],
        };

        AssertProposalFailure(
            ImprovementProposalFailure.EvidenceNotExactBound,
            () => store.CreateImprovementProposal(proposal));

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposals;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_sessions;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_evidence;"));
    }

    [Theory]
    [InlineData("status")]
    [InlineData("completeness")]
    [InlineData("native")]
    [InlineData("fact")]
    public void ImprovementProposals_CreateDistinguishesSourceEligibilityLoss(string mutation)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var source = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "proposal-ineligible");
        store.Write(source);
        using (var connection = database.Open())
        {
            Execute(connection, mutation switch
            {
                "status" => "UPDATE sessions SET status='active';",
                "completeness" => "UPDATE sessions SET completeness='rich';",
                "native" => "DELETE FROM session_native_ids;",
                "fact" => "UPDATE session_events SET terminal_outcome=NULL,terminal_policy_version=NULL WHERE terminal_outcome IS NOT NULL;",
                _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
            });
        }

        var proposal = CreateProposal(source);

        AssertProposalFailure(
            ImprovementProposalFailure.EvidenceNotExactBound,
            () => store.CreateImprovementProposal(proposal));

        using var verify = database.Open();
        Assert.Equal(0L, Scalar<long>(verify, "SELECT COUNT(*) FROM improvement_proposals;"));
    }

    [Theory]
    [InlineData("completed", true)]
    [InlineData("failed", true)]
    [InlineData("active", false)]
    [InlineData("unknown", false)]
    [InlineData("non_full", false)]
    [InlineData("no_fact", false)]
    [InlineData("no_native", true)]
    [InlineData("raw_stop", false)]
    public void PricingCoreCurrentUseEligibility_UsesStatusFullAndSessionFactWithoutNativeBinding(string state, bool expected)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "pricing-core-" + state);
        store.Write(batch);
        using var connection = database.Open();
        Execute(connection, state switch
        {
            "completed" => "SELECT 1;",
            "failed" => "UPDATE sessions SET status='failed'; UPDATE session_events SET terminal_outcome='failed' WHERE terminal_outcome IS NOT NULL;",
            "active" => "UPDATE sessions SET status='active';",
            "unknown" => "UPDATE sessions SET status='unknown';",
            "non_full" => "UPDATE sessions SET completeness='rich';",
            "no_fact" => "UPDATE session_events SET terminal_outcome=NULL,terminal_policy_version=NULL WHERE terminal_outcome IS NOT NULL;",
            "no_native" => "DELETE FROM session_native_ids;",
            "raw_stop" => "UPDATE session_events SET type='Stop',terminal_outcome=NULL,terminal_policy_version=NULL WHERE terminal_outcome IS NOT NULL;",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        });
        using var transaction = connection.BeginTransaction(deferred: true);

        Assert.Equal(
            expected,
            SessionCurrentUseEligibilitySqlV1.Contains(connection, transaction, batch.Detail.Session.SessionId));
    }

    [Fact]
    public void ImprovementProposals_PromotionDistinguishesMissingReferencedEvidenceBeforeOtherSemanticFailures()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "promotion-missing-first");
        var second = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "promotion-missing-second");
        store.Write(first);
        store.Write(second);
        var proposal = CreateProposal([first, second]);
        store.CreateImprovementProposal(proposal);
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM session_events WHERE event_id=$event_id;";
            command.Parameters.AddWithValue("$event_id", first.Detail.Events[0].EventId.ToString("D"));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        var before = store.GetImprovementProposal(proposal.ProposalId);

        AssertProposalFailure(
            ImprovementProposalFailure.EvidenceNotFound,
            () => store.UpdateImprovementProposalStatus(
                proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch.AddMinutes(2)));
        var after = store.GetImprovementProposal(proposal.ProposalId);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(
            (before.Status, before.Revision, before.UpdatedAt, before.RecommendedAt, before.VerifiedAt),
            (after.Status, after.Revision, after.UpdatedAt, after.RecommendedAt, after.VerifiedAt));
    }

    [Fact]
    public void Promotion_RequiresEvidenceFromTwoDistinctSourceSessions()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "native-first");
        var second = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "native-second");
        store.Write(first);
        store.Write(second);
        var proposal = CreateProposal([first, second]) with
        {
            EvidenceReferences = [new ImprovementProposalEvidenceReference("event", first.Detail.Events[0].EventId.ToString("D"))],
        };
        store.CreateImprovementProposal(proposal);

        AssertProposalFailure(ImprovementProposalFailure.InsufficientRecommendationEvidence, () =>
            store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ImprovementProposals_RejectNonVersionSevenProposalId()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch) with { ProposalId = Guid.NewGuid() };

        AssertProposalFailure(ImprovementProposalFailure.InvalidShape, () => store.CreateImprovementProposal(proposal));
    }

    [Fact]
    public void Promotion_RejectsEvidenceOutsideSourceSessions()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "native-first");
        var second = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "native-second");
        var other = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(2), "native-other");
        store.Write(first);
        store.Write(second);
        store.Write(other);
        var proposal = CreateProposal([first, second]);
        store.CreateImprovementProposal(proposal);
        using (var connection = database.Open())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE improvement_proposal_evidence SET reference_id=$reference_id WHERE proposal_id=$proposal_id;";
            command.Parameters.AddWithValue("$reference_id", other.Detail.Events[0].EventId.ToString("D"));
            command.Parameters.AddWithValue("$proposal_id", proposal.ProposalId.ToString("D"));
            command.ExecuteNonQuery();
        }

        AssertProposalFailure(ImprovementProposalFailure.InsufficientRecommendationEvidence, () =>
            store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ImprovementProposals_InvalidWriteRollsBackWithoutPartialState()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch) with { TargetKind = "invalid" };

        AssertProposalFailure(ImprovementProposalFailure.InvalidShape, () => store.CreateImprovementProposal(proposal));

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposals;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_sessions;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_evidence;"));
    }

    [Fact]
    public void ImprovementProposals_TransactionFailureRollsBackRootAndAssociations()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "proposal-transaction");
        store.Write(batch);
        var proposal = CreateProposal(batch) with { SourceSessionIds = [batch.Detail.Session.SessionId, Guid.CreateVersion7()] };

        AssertProposalFailure(ImprovementProposalFailure.EvidenceNotFound, () => store.CreateImprovementProposal(proposal));

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposals;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_sessions;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_evidence;"));
    }

    [Fact]
    public void ImprovementProposals_RejectMalformedDomainValues()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch);

        var invalidProposals = new[]
        {
            proposal with { TargetLabel = new string('x', 201) },
            proposal with { SourceSessionIds = [Guid.NewGuid()] },
            proposal with { SourceSessionIds = [batch.Detail.Session.SessionId, batch.Detail.Session.SessionId] },
            proposal with { EvidenceReferences = [] },
            proposal with { EvidenceReferences = [new ImprovementProposalEvidenceReference("unknown", "reference")] },
            proposal with { EvidenceReferences = [new ImprovementProposalEvidenceReference("event", "not-a-guid")] },
        };

        foreach (var invalid in invalidProposals)
        {
            AssertProposalFailure(ImprovementProposalFailure.InvalidShape, () => store.CreateImprovementProposal(invalid));
        }
    }

    [Fact]
    public void CreateSchema_EmptyDatabaseCreatesSessionSchemaAndIsIdempotent()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);

        store.CreateSchema();
        store.CreateSchema();

        using var connection = database.Open();
        Assert.Equal(14L, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component = 'session';"));
        Assert.Equal(
            new[] { "source_application_version", "adapter_version", "schema_fingerprint", "normalization_version", "terminal_policy_version" },
            ReadColumns(connection, "session_events").Where(column => column.EndsWith("version", StringComparison.Ordinal) || column == "schema_fingerprint"));
        foreach (var table in new[] { "sessions", "session_native_ids", "session_runs", "session_events", "session_event_content", "session_projection_state", "session_human_evaluation", "improvement_proposals", "improvement_proposal_sessions", "improvement_proposal_evidence", "proposal_apply_drafts", "proposal_apply_files", "proposal_apply_hunks", "proposal_apply_revisions", "proposal_applies", "proposal_apply_audit", "proposal_apply_pending", "objective_evaluations", "objective_evaluation_evidence" })
        {
            Assert.Equal(1L, Scalar<long>(connection, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        }
    }

    [Fact]
    public void CreateSchema_ExistingDatabasePreservesExistingTablesAndRows()
    {
        using var database = new SessionTestDatabase();
        using (var connection = database.Open())
        {
            Execute(connection, "CREATE TABLE preserved (value TEXT NOT NULL); INSERT INTO preserved VALUES ('keep');");
        }

        new SqliteSessionStore(database.Path).CreateSchema();

        using var verify = database.Open();
        Assert.Equal("keep", Scalar<string>(verify, "SELECT value FROM preserved;"));
    }

    [Fact]
    public void CreateSchema_NewerSessionVersionPreservesCurrentSchemaAndRows()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        using (var connection = database.Open()) Execute(connection, "UPDATE schema_version SET version=15 WHERE component='session';");

        Assert.Throws<InvalidOperationException>(store.CreateSchema);

        using var verify = database.Open();
        Assert.Equal(15L, Scalar<long>(verify, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(batch.Detail.Session.SessionId.ToString("D"), Scalar<string>(verify, "SELECT session_id FROM sessions;"));
        Assert.Equal(batch.Detail.Events[0].EventId.ToString("D"), Scalar<string>(verify, "SELECT event_id FROM session_events;"));
    }

    [Fact]
    public void CreateSchema_VersionOneDatabaseAddsHumanEvaluationTable()
    {
        using var database = new SessionTestDatabase();
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "session", "session-v1.sqlite");
        File.Copy(fixture, database.Path);
        var store = CreateRawStore(database.Path);

        store.CreateSchema();

        using var verify = database.Open();
        Assert.Equal(14L, Scalar<long>(verify, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='session_human_evaluation';"));
        Assert.NotNull(store.GetDetail(Guid.Parse("00000001-0000-7000-8000-000000000001")));
    }

    [Fact]
    public void ProposalApply_rollback_linkage_is_durable_and_failure_keeps_applied_state()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "proposal-apply");
        store.Write(batch);
        var proposal = CreateProposal(batch);
        store.CreateImprovementProposal(proposal);
        var draftId = Guid.CreateVersion7();
        var applyId = Guid.CreateVersion7();
        var rootId = Guid.CreateVersion7();
        var now = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
        var draft = new ProposalApplyDraftMetadata(draftId, proposal.ProposalId, 1, rootId, 2, "digest", ProposalApplyState.Approved, 1, now, now);
        store.SaveProposalApplyDraft(draft, [("base", "replacement")], [("hunk", true, "replacement")], new(draftId, 2, "digest", now));
        store.SaveProposalApplyOutcome(new(applyId, draftId, ProposalApplyState.Applied, now), proposal.ProposalId, rootId, 1, null);

        var linkage = Assert.Single(store.ListAppliedProposalApplyLinkages());
        Assert.Equal((applyId, draftId, proposal.ProposalId, rootId, 1, 2, "digest"), (linkage.ApplyId, linkage.DraftId, linkage.ProposalId, linkage.RootId, linkage.FileCount, linkage.SelectionRevision, linkage.ApprovalDigest));
        Assert.True(store.TryStartProposalApplyRollback(new(applyId, draftId, proposal.ProposalId, rootId, 1, "rollback", now)));
        Assert.False(store.TryStartProposalApplyRollback(new(applyId, draftId, proposal.ProposalId, rootId, 1, "rollback", now)));

        store.CompleteProposalApplyPending(new(applyId, draftId, ProposalApplyState.Failed, now), proposal.ProposalId, rootId, 1, "rollback_failed");
        store.CompleteProposalApplyPending(new(applyId, draftId, ProposalApplyState.Failed, now), proposal.ProposalId, rootId, 1, "rollback_failed");

        Assert.Equal(ProposalApplyState.Applied, store.GetProposalApplyDraft(draftId)!.State);
        Assert.Single(store.ListAppliedProposalApplyLinkages());
        using var connection = database.Open();
        Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM proposal_apply_audit;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM proposal_apply_pending;"));
    }

    [Fact]
    public void CreateSchema_VersionTwoDatabaseAddsProposalTablesAndPreservesSessionRow()
    {
        using var database = new SessionTestDatabase();
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "session", "session-v2.sqlite");
        File.Copy(fixture, database.Path);
        var store = CreateRawStore(database.Path);

        store.CreateSchema();

        using var verify = database.Open();
        Assert.Equal(14L, Scalar<long>(verify, "SELECT version FROM schema_version WHERE component='session';"));
        foreach (var table in new[] { "improvement_proposals", "improvement_proposal_sessions", "improvement_proposal_evidence" })
        {
            Assert.Equal(1L, Scalar<long>(verify, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        }
        var sessionId = Guid.Parse("00000001-0000-7000-8000-000000000002");
        Assert.Equal(sessionId, store.GetDetail(sessionId)?.Session.SessionId);
    }

    [Fact]
    public void Write_PersistsMetadataAndDuplicateSourceReplayIsIdempotent()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(lastSeenAt: DateTimeOffset.Parse("2026-07-11T01:00:00Z"));

        store.Write(batch);
        var replayEventId = Guid.CreateVersion7();
        var replay = batch with
        {
            Detail = batch.Detail with { Events = [batch.Detail.Events[0] with { EventId = replayEventId }] },
            Content = [batch.Content[0] with { EventId = replayEventId }],
        };
        store.Write(replay);

        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_native_ids;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));

        var detail = store.GetDetail(batch.Detail.Session.SessionId);
        Assert.NotNull(detail);
        Assert.Equal(batch.Detail.Session, detail.Session);
        Assert.Equal(batch.Detail.NativeIds, detail.NativeIds);
        Assert.Equal(batch.Detail.Runs, detail.Runs);
        Assert.Equal(batch.Detail.Events, detail.Events);
    }

    [Fact]
    public void Write_CapturesContentAndRetentionReceiptInOneTransaction()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        new RetentionCatalogStore(database.Path).CreateSchema();
        var batch = CreateBatch(DateTimeOffset.Parse("2026-07-11T01:00:00Z"));

        store.Write(batch);

        using var connection = database.Open();
        Assert.Equal(14L, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content WHERE typeof(retention_owner_token)='blob' AND length(retention_owner_token)=32;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=(SELECT event_id FROM session_event_content);"));
    }

    [Theory]
    [InlineData("after-session-content-source")]
    [InlineData("after-session-content-catalog")]
    public void Write_CheckpointFailureRollsBackContentAndCatalogTogether(string checkpoint)
    {
        using var database = new SessionTestDatabase();
        new SqliteSessionStore(database.Path).CreateSchema();
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path);
        var store = new SqliteSessionStore(database.Path, context, TimeProvider.System, point =>
        {
            if (point == checkpoint) throw new InvalidOperationException("injected");
        });

        Assert.Throws<InvalidOperationException>(() => store.Write(CreateBatch(DateTimeOffset.Parse("2026-07-11T01:00:00Z"))));
        new RetentionCatalogStore(database.Path).CreateSchema();

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
    }

    [Fact]
    public void Write_RoundTripsClaudeProvenanceAndKeepsLegacyEventProvenanceNullAfterRestart()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var legacy = CreateBatch(DateTimeOffset.UnixEpoch, "legacy-native");
        var claude = CreateBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "claude-native");
        var fingerprint = new string('a', 64);
        claude = claude with
        {
            Detail = claude.Detail with
            {
                NativeIds = [claude.Detail.NativeIds[0] with { SourceSurface = SessionSourceSurface.ClaudeCode }],
                Runs = [claude.Detail.Runs[0] with { SourceSurface = SessionSourceSurface.ClaudeCode }],
                Events =
                [
                    claude.Detail.Events[0] with
                    {
                        SourceSurface = SessionSourceSurface.ClaudeCode,
                        SourceAdapter = "claude-code-hook",
                        SourceApplicationVersion = "2.1.207",
                        AdapterVersion = "claude-hook-v1",
                        SchemaFingerprint = fingerprint,
                        NormalizationVersion = "session-normalization-v1",
                    },
                ],
            },
        };

        store.Write(legacy);
        store.Write(claude);
        store.CreateSchema();
        var restarted = new SqliteSessionStore(database.Path);

        var legacyEvent = Assert.Single(restarted.GetDetail(legacy.Detail.Session.SessionId)!.Events);
        Assert.Null(legacyEvent.SourceApplicationVersion);
        Assert.Null(legacyEvent.AdapterVersion);
        Assert.Null(legacyEvent.SchemaFingerprint);
        Assert.Null(legacyEvent.NormalizationVersion);

        var claudeEvent = Assert.Single(restarted.GetDetail(claude.Detail.Session.SessionId)!.Events);
        Assert.Equal(SessionSourceSurface.ClaudeCode, claudeEvent.SourceSurface);
        Assert.Equal("claude-code-hook", claudeEvent.SourceAdapter);
        Assert.Equal("2.1.207", claudeEvent.SourceApplicationVersion);
        Assert.Equal("claude-hook-v1", claudeEvent.AdapterVersion);
        Assert.Equal(fingerprint, claudeEvent.SchemaFingerprint);
        Assert.Equal("session-normalization-v1", claudeEvent.NormalizationVersion);
    }

    [Fact]
    public void Write_ChildRunMayBeListedBeforeNewParentRun()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        var parent = batch.Detail.Runs[0];
        var child = parent with { RunId = Guid.CreateVersion7(), ParentRunId = parent.RunId, NativeRunId = "child-run" };
        batch = batch with { Detail = batch.Detail with { Runs = [child, parent] } };

        store.Write(batch);

        var detail = Assert.IsType<SessionDetail>(store.GetDetail(batch.Detail.Session.SessionId));
        Assert.Equal(2, detail.Runs.Count);
        Assert.Contains(detail.Runs, run => run.RunId == child.RunId && run.ParentRunId == parent.RunId);
    }

    [Fact]
    public void Write_ChildEventMayBeListedBeforeNewParentEvent()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        var parent = batch.Detail.Events[0];
        var child = parent with
        {
            EventId = Guid.CreateVersion7(),
            ParentEventId = parent.EventId,
            SourceEventId = "child-event",
            OccurredAt = parent.OccurredAt.AddSeconds(1),
            ContentState = SessionContentState.NotCaptured,
        };
        batch = batch with { Detail = batch.Detail with { Events = [child, parent] } };

        store.Write(batch);

        var detail = Assert.IsType<SessionDetail>(store.GetDetail(batch.Detail.Session.SessionId));
        Assert.Equal(2, detail.Events.Count);
        Assert.Contains(detail.Events, item => item.EventId == child.EventId && item.ParentEventId == parent.EventId);
    }

    [Fact]
    public void Write_ChildMayReferenceReplayedParentByDifferentInputEventId()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var original = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(original);
        var canonicalParent = original.Detail.Events[0];
        var replayEventId = Guid.CreateVersion7();
        var replayedParent = canonicalParent with { EventId = replayEventId };
        var child = canonicalParent with
        {
            EventId = Guid.CreateVersion7(),
            ParentEventId = replayEventId,
            SourceEventId = "child-of-replay",
            OccurredAt = canonicalParent.OccurredAt.AddSeconds(1),
            ContentState = SessionContentState.NotCaptured,
        };
        var replay = original with
        {
            Detail = original.Detail with { Events = [child, replayedParent] },
            Content = [original.Content[0] with { EventId = replayEventId }],
        };

        store.Write(replay);

        var detail = Assert.IsType<SessionDetail>(store.GetDetail(original.Detail.Session.SessionId));
        Assert.Contains(detail.Events, item => item.EventId == child.EventId && item.ParentEventId == canonicalParent.EventId);
        Assert.Equal(2, detail.Events.Count);
        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("event")]
    public void Write_ParentCycleFailsDeterministicallyAndRollsBack(string relationship)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        if (relationship == "run")
        {
            var first = batch.Detail.Runs[0] with { RunId = Guid.CreateVersion7(), NativeRunId = "cycle-run-a" };
            var second = first with { RunId = Guid.CreateVersion7(), NativeRunId = "cycle-run-b", ParentRunId = first.RunId };
            first = first with { ParentRunId = second.RunId };
            batch = batch with
            {
                Detail = batch.Detail with
                {
                    Runs = [first, second],
                    Events = [batch.Detail.Events[0] with { RunId = first.RunId }],
                },
            };
        }
        else
        {
            var first = batch.Detail.Events[0] with { EventId = Guid.CreateVersion7(), SourceEventId = "cycle-event-a" };
            var second = first with { EventId = Guid.CreateVersion7(), SourceEventId = "cycle-event-b", ParentEventId = first.EventId };
            first = first with { ParentEventId = second.EventId };
            batch = batch with
            {
                Detail = batch.Detail with { Events = [first, second] },
                Content = [batch.Content[0] with { EventId = first.EventId }],
            };
        }

        Assert.Throws<InvalidOperationException>(() => store.Write(batch));
        Assert.Empty(store.ListMostRecent(10));
    }

    [Fact]
    public void Write_InvalidChildRollsBackWholeBatch()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        batch = batch with
        {
            Content = [batch.Content[0] with { EventId = Guid.CreateVersion7() }],
        };

        Assert.Throws<InvalidOperationException>(() => store.Write(batch));

        Assert.Empty(store.ListMostRecent(10));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("run")]
    [InlineData("event")]
    public void Write_DuplicateIdentityOwnedByAnotherSessionRejectsAndRollsBackBatch(string identity)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var existing = CreateBatch(DateTimeOffset.UnixEpoch, "shared-native");
        store.Write(existing);
        var conflicting = CreateBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), identity == "native" ? "shared-native" : "unique-native");
        conflicting = identity switch
        {
            "native" => conflicting,
            "run" => conflicting with
            {
                Detail = conflicting.Detail with
                {
                    Runs = [conflicting.Detail.Runs[0] with { RunId = existing.Detail.Runs[0].RunId }],
                    Events = [conflicting.Detail.Events[0] with { RunId = existing.Detail.Runs[0].RunId }],
                },
            },
            "event" => conflicting with
            {
                Detail = conflicting.Detail with
                {
                    Events = [conflicting.Detail.Events[0] with
                    {
                        SourceAdapter = existing.Detail.Events[0].SourceAdapter,
                        SourceEventId = existing.Detail.Events[0].SourceEventId,
                    }],
                },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(identity)),
        };

        Assert.Throws<InvalidOperationException>(() => store.Write(conflicting));

        Assert.Equal([existing.Detail.Session], store.ListMostRecent(10));
        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_runs;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_events;"));
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Theory]
    [InlineData("native")]
    [InlineData("run")]
    [InlineData("event")]
    [InlineData("content")]
    public void Write_BatchMembersMustBelongToAggregateSession(string member)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        var otherSessionId = Guid.CreateVersion7();
        batch = member switch
        {
            "native" => batch with { Detail = batch.Detail with { NativeIds = [batch.Detail.NativeIds[0] with { SessionId = otherSessionId }] } },
            "run" => batch with { Detail = batch.Detail with { Runs = [batch.Detail.Runs[0] with { SessionId = otherSessionId }] } },
            "event" => batch with { Detail = batch.Detail with { Events = [batch.Detail.Events[0] with { SessionId = otherSessionId }] } },
            "content" => batch with { Content = [batch.Content[0] with { EventId = Guid.CreateVersion7() }] },
            _ => throw new ArgumentOutOfRangeException(nameof(member)),
        };

        Assert.Throws<InvalidOperationException>(() => store.Write(batch));
        Assert.Empty(store.ListMostRecent(10));
    }

    [Theory]
    [InlineData("parent-run")]
    [InlineData("event-run")]
    [InlineData("parent-event")]
    [InlineData("content-event")]
    public void Write_ExistingCrossSessionReferencesAreRejected(string reference)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateBatch(DateTimeOffset.UnixEpoch, "native-a");
        store.Write(first);
        var second = CreateBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "native-b");
        second = reference switch
        {
            "parent-run" => second with { Detail = second.Detail with { Runs = [second.Detail.Runs[0] with { ParentRunId = first.Detail.Runs[0].RunId }] } },
            "event-run" => second with { Detail = second.Detail with { Events = [second.Detail.Events[0] with { RunId = first.Detail.Runs[0].RunId }] } },
            "parent-event" => second with { Detail = second.Detail with { Events = [second.Detail.Events[0] with { ParentEventId = first.Detail.Events[0].EventId }] } },
            "content-event" => second with { Content = [second.Content[0] with { EventId = first.Detail.Events[0].EventId }] },
            _ => throw new ArgumentOutOfRangeException(nameof(reference)),
        };

        Assert.Throws<InvalidOperationException>(() => store.Write(second));
        Assert.Equal([first.Detail.Session], store.ListMostRecent(10));
    }

    [Fact]
    public void Resolve_IsExactAndListIsMostRecentFirstWithLimit()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var older = CreateBatch(DateTimeOffset.Parse("2026-07-10T00:00:00Z"), "Native-A");
        var newer = CreateBatch(DateTimeOffset.Parse("2026-07-11T00:00:00Z"), "Native-B");
        store.Write(older);
        store.Write(newer);

        Assert.Equal(newer.Detail.Session.SessionId, store.Resolve(SessionSourceSurface.CopilotSdk, "Native-B")?.SessionId);
        Assert.Null(store.Resolve(SessionSourceSurface.CopilotSdk, "native-b"));
        Assert.Null(store.Resolve(SessionSourceSurface.VisualStudioCode, "Native-B"));
        Assert.Equal([newer.Detail.Session], store.ListMostRecent(1));
    }

    [Fact]
    public async Task ReadContentAsync_UsesSessionAndEventKeysAndExpiresAtExactBoundary()
    {
        using var database = new SessionTestDatabase();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        var store = new SqliteSessionStore(database.Path, context, time);
        store.CreateSchema();
        var batch = CreateBatch(now);
        store.Write(batch);
        var sessionId = batch.Detail.Session.SessionId;
        var eventId = batch.Detail.Events[0].EventId;

        var available = await store.ReadContentAsync(sessionId, eventId, CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.Granted, available.Disposition);
        await using var lease = available.Lease!;
        Assert.Equal(batch.Content[0], lease.Content);
        Assert.Equal(SessionContentReadDisposition.NotFound, (await store.ReadContentAsync(Guid.CreateVersion7(), eventId, CancellationToken.None)).Disposition);
        Assert.Equal(SessionContentReadDisposition.NotFound, (await store.ReadContentAsync(sessionId, Guid.CreateVersion7(), CancellationToken.None)).Disposition);

        time.Advance(TimeSpan.FromDays(90));
        var expired = await store.ReadContentAsync(sessionId, eventId, CancellationToken.None);
        Assert.Equal(SessionContentReadDisposition.Denied, expired.Disposition);
        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Fact]
    public async Task ContentReadMaterialization_RejectsEveryPerturbedAdmissionParameter()
    {
        using var database = new SessionTestDatabase();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        var store = new SqliteSessionStore(database.Path, context, time);
        store.CreateSchema();
        var batch = CreateBatch(now, "materialization-capability");
        store.Write(batch);
        var sessionId = batch.Detail.Session.SessionId;
        var eventId = batch.Detail.Events[0].EventId;
        var catalog = new RetentionCatalogStore(context, time);
        var request = new RetentionReadRequest(
            new(context.StoreInstanceId, RetentionStoreKind.SessionEventContent, eventId.ToString("D")),
            RetentionReadKind.Access,
            now,
            ExpectedRevision: null);

        var result = await catalog.ReadAsync<bool>(request, async (connection, transaction, grant, token) =>
        {
            using (var baseline = connection.CreateCommand())
            {
                SqliteSessionStore.ConfigureContentReadMaterializationCommand(
                    baseline,
                    transaction,
                    grant,
                    context.StoreInstanceId,
                    sessionId,
                    eventId);
                Assert.Equal(1, await CountRowsAsync(baseline, token));
            }

            string[] capabilityParameters =
            [
                "$retention_read_source_token",
                "$retention_read_item_id",
                "$retention_read_revision",
                "$retention_read_lease_kind",
                "$retention_read_lease_owner",
                "$retention_read_lease_generation",
                "$retention_read_lease_expires_at",
            ];
            foreach (var parameterName in capabilityParameters)
            {
                using var perturbed = connection.CreateCommand();
                SqliteSessionStore.ConfigureContentReadMaterializationCommand(
                    perturbed,
                    transaction,
                    grant,
                    context.StoreInstanceId,
                    sessionId,
                    eventId);
                var parameter = perturbed.Parameters[parameterName];
                parameter.Value = PerturbCapabilityParameter(parameterName, parameter.Value!);

                Assert.Equal(0, await CountRowsAsync(perturbed, token));
            }

            return true;
        }, CancellationToken.None);

        Assert.Equal(RetentionReadDisposition.Granted, result.Disposition);
        Assert.True(Assert.IsType<RetentionReadLease<bool>>(result.Lease).Value);
        await result.Lease.DisposeAsync();

        static async ValueTask<int> CountRowsAsync(SqliteCommand command, CancellationToken cancellationToken)
        {
            using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var count = 0;
            while (await reader.ReadAsync(cancellationToken)) count++;
            return count;
        }

        static object PerturbCapabilityParameter(string parameterName, object value) => parameterName switch
        {
            "$retention_read_source_token" => MutateToken(Assert.IsType<byte[]>(value)),
            "$retention_read_item_id" => MutateHexIdentifier(Assert.IsType<string>(value)),
            "$retention_read_revision" => Assert.IsType<long>(value) + 1,
            "$retention_read_lease_kind" => Assert.IsType<string>(value) == "access" ? "operation" : "access",
            "$retention_read_lease_owner" => MutateHexIdentifier(Assert.IsType<string>(value)),
            "$retention_read_lease_generation" => Assert.IsType<long>(value) + 1,
            "$retention_read_lease_expires_at" => DateTimeOffset.ParseExact(
                    Assert.IsType<string>(value),
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture)
                .AddTicks(1)
                .ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new ArgumentOutOfRangeException(nameof(parameterName)),
        };

        static byte[] MutateToken(byte[] value)
        {
            var changed = value.ToArray();
            changed[0] ^= 0xff;
            return changed;
        }

        static string MutateHexIdentifier(string value) =>
            (value[0] == '0' ? "1" : "0") + value[1..];
    }

    [Fact]
    public async Task ReadContentAsync_PinnedPastHistoricalExpiryRemainsGrantedAndProjectsExpiringWithoutMutation()
    {
        using var database = new SessionTestDatabase();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        var store = new SqliteSessionStore(database.Path, context, time);
        store.CreateSchema();
        var batch = CreateBatch(now, "pinned-past-expiry");
        store.Write(batch);
        var sessionId = batch.Detail.Session.SessionId;
        var eventId = batch.Detail.Events[0].EventId;
        IReadOnlyDictionary<string, byte[]> retentionItemBefore;
        IReadOnlyDictionary<string, byte[]> contentBefore;
        using (var connection = database.Open())
        {
            using var pin = connection.CreateCommand();
            pin.CommandText = "UPDATE retention_items SET state='retained_by_policy',revision=revision+1 WHERE store_kind='session_event_content' AND source_item_id=$event_id;";
            pin.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
            Assert.Equal(1, pin.ExecuteNonQuery());
            retentionItemBefore = ReadCompleteSessionRowBytes(connection, "retention_items", eventId);
            contentBefore = ReadCompleteSessionRowBytes(connection, "session_event_content", eventId);
        }
        time.Advance(TimeSpan.FromDays(91));

        var result = await store.ReadContentAsync(sessionId, eventId, CancellationToken.None);

        Assert.Equal(SessionContentReadDisposition.Granted, result.Disposition);
        await using (var lease = Assert.IsType<SessionContentReadLease>(result.Lease))
        {
            Assert.Equal(batch.Content[0], lease.Content);
            using var activeLeaseVerification = database.Open();
            Assert.Equal(1L, CountSessionContentAccessLease(activeLeaseVerification, eventId));
        }
        Assert.Equal(SessionRawRetentionState.Expiring, store.GetRawRetentionState(sessionId));
        using var verification = database.Open();
        Assert.Equal(0L, CountSessionContentAccessLease(verification, eventId));
        AssertCompleteRowBytesEqual(
            retentionItemBefore,
            ReadCompleteSessionRowBytes(verification, "retention_items", eventId));
        AssertCompleteRowBytesEqual(
            contentBefore,
            ReadCompleteSessionRowBytes(verification, "session_event_content", eventId));
    }

    [Fact]
    public void GetRawRetentionState_ExactExpiryBoundaryPrefersReadableSiblingUntilAllRepresentedItemsExpire()
    {
        using var database = new SessionTestDatabase();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var firstCapturedAt = now.AddMinutes(-1);
        var siblingCapturedAt = now;
        var firstExpiry = firstCapturedAt.AddDays(90);
        var siblingExpiry = siblingCapturedAt.AddDays(90);
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        var store = new SqliteSessionStore(database.Path, context, time);
        store.CreateSchema();
        var batch = CreateBatch(now, "raw-state-precedence");
        var siblingEvent = batch.Detail.Events[0] with
        {
            EventId = Guid.CreateVersion7(),
            SourceEventId = "event-raw-state-precedence-sibling",
        };
        var firstContent = batch.Content[0] with { CapturedAt = firstCapturedAt, ExpiresAt = firstExpiry };
        var siblingContent = batch.Content[0] with
        {
            EventId = siblingEvent.EventId,
            CapturedAt = siblingCapturedAt,
            ExpiresAt = siblingExpiry,
        };
        store.Write(batch with
        {
            Detail = batch.Detail with { Events = [batch.Detail.Events[0], siblingEvent] },
            Content = [firstContent, siblingContent],
        });

        time.Advance(firstExpiry - now);
        Assert.Equal(firstExpiry, time.GetUtcNow());
        using (var connection = database.Open())
        {
            Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content';"));
            Assert.Equal("expiring", Scalar<string>(connection, $"SELECT state FROM retention_items WHERE source_item_id='{batch.Detail.Events[0].EventId:D}';"));
            Assert.Equal(firstCapturedAt.ToString("O"), Scalar<string>(connection, $"SELECT captured_at FROM retention_items WHERE source_item_id='{batch.Detail.Events[0].EventId:D}';"));
            Assert.Equal(firstExpiry.ToString("O"), Scalar<string>(connection, $"SELECT expires_at FROM retention_items WHERE source_item_id='{batch.Detail.Events[0].EventId:D}';"));
            Assert.Equal(1L, Scalar<long>(connection, $"SELECT read_denied_at IS NULL FROM retention_items WHERE source_item_id='{batch.Detail.Events[0].EventId:D}';"));
            Assert.Equal("expiring", Scalar<string>(connection, $"SELECT state FROM retention_items WHERE source_item_id='{siblingEvent.EventId:D}';"));
            Assert.Equal(siblingCapturedAt.ToString("O"), Scalar<string>(connection, $"SELECT captured_at FROM retention_items WHERE source_item_id='{siblingEvent.EventId:D}';"));
            Assert.Equal(siblingExpiry.ToString("O"), Scalar<string>(connection, $"SELECT expires_at FROM retention_items WHERE source_item_id='{siblingEvent.EventId:D}';"));
            Assert.Equal(1L, Scalar<long>(connection, $"SELECT read_denied_at IS NULL FROM retention_items WHERE source_item_id='{siblingEvent.EventId:D}';"));
            Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND policy_id='raw-default-90d' AND policy_version=1;"));
        }

        Assert.Equal(SessionRawRetentionState.Expiring, store.GetRawRetentionState(batch.Detail.Session.SessionId));

        time.Advance(siblingExpiry - firstExpiry);
        Assert.Equal(siblingExpiry, time.GetUtcNow());
        Assert.Equal(SessionRawRetentionState.ExpiredPendingDeletion, store.GetRawRetentionState(batch.Detail.Session.SessionId));
        using (var connection = database.Open())
        {
            Assert.Equal(2L, Scalar<long>(connection, "SELECT COUNT(*) FROM retention_items WHERE store_kind='session_event_content' AND state='expiring' AND read_denied_at IS NULL;"));
            Assert.Equal(firstExpiry.ToString("O"), Scalar<string>(connection, $"SELECT expires_at FROM retention_items WHERE source_item_id='{batch.Detail.Events[0].EventId:D}';"));
            Assert.Equal(siblingExpiry.ToString("O"), Scalar<string>(connection, $"SELECT expires_at FROM retention_items WHERE source_item_id='{siblingEvent.EventId:D}';"));
        }
    }

    [Fact]
    public void GetRawRetentionState_DeletedCatalogItemRemainsRepresentedAfterContentRemoval()
    {
        using var database = new SessionTestDatabase();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        var store = new SqliteSessionStore(database.Path, context, time);
        store.CreateSchema();
        var batch = CreateBatch(now, "raw-state-deleted");
        store.Write(batch);
        var eventId = batch.Detail.Events[0].EventId;
        using (var connection = database.Open())
        {
            var itemId = Scalar<string>(connection, $"SELECT item_id FROM retention_items WHERE store_kind='session_event_content' AND source_item_id='{eventId:D}';");
            using var transaction = connection.BeginTransaction();
            using (var deleteContent = connection.CreateCommand())
            {
                deleteContent.Transaction = transaction;
                deleteContent.CommandText = "DELETE FROM session_event_content WHERE event_id=$event_id;";
                deleteContent.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
                Assert.Equal(1, deleteContent.ExecuteNonQuery());
            }
            using (var tombstone = connection.CreateCommand())
            {
                tombstone.Transaction = transaction;
                tombstone.CommandText = "INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES($item_id,$now,$now);";
                tombstone.Parameters.AddWithValue("$item_id", itemId);
                tombstone.Parameters.AddWithValue("$now", now.ToString("O"));
                Assert.Equal(1, tombstone.ExecuteNonQuery());
            }
            using (var complete = connection.CreateCommand())
            {
                complete.Transaction = transaction;
                complete.CommandText = "UPDATE retention_items SET state='deleted',read_denied_at=$now,queued_at=$now,deleted_at=$now,revision=revision+1 WHERE item_id=$item_id;";
                complete.Parameters.AddWithValue("$item_id", itemId);
                complete.Parameters.AddWithValue("$now", now.ToString("O"));
                Assert.Equal(1, complete.ExecuteNonQuery());
            }
            transaction.Commit();

            Assert.Equal(1L, Scalar<long>(connection, $"SELECT COUNT(*) FROM session_events WHERE event_id='{eventId:D}';"));
            Assert.Equal(0L, Scalar<long>(connection, $"SELECT COUNT(*) FROM session_event_content WHERE event_id='{eventId:D}';"));
            Assert.Equal("deleted", Scalar<string>(connection, $"SELECT state FROM retention_items WHERE item_id='{itemId}';"));
            Assert.Equal(1L, Scalar<long>(connection, $"SELECT read_denied_at IS NOT NULL AND deleted_at IS NOT NULL FROM retention_items WHERE item_id='{itemId}';"));
            Assert.Equal(1L, Scalar<long>(connection, $"SELECT COUNT(*) FROM retention_tombstones WHERE item_id='{itemId}';"));
        }

        Assert.Equal(SessionRawRetentionState.ExpiredPendingDeletion, store.GetRawRetentionState(batch.Detail.Session.SessionId));
    }

    [Fact]
    public void GetRawRetentionState_ExistingSessionWithoutRepresentedRetentionItemIsNotCaptured()
    {
        using var database = new SessionTestDatabase();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var time = new MutableTimeProvider(now);
        var context = RetentionCatalogContext.InitializeNewOwnedDatabase(database.Path, time);
        var store = new SqliteSessionStore(database.Path, context, time);
        store.CreateSchema();
        var uncaptured = CreateBatch(time.GetUtcNow(), "raw-state-absent");
        store.Write(uncaptured with
        {
            Detail = uncaptured.Detail with
            {
                Events = [uncaptured.Detail.Events[0] with { ContentState = SessionContentState.NotCaptured }],
            },
            Content = [],
        });
        using (var connection = database.Open())
        {
            Assert.Equal(1L, Scalar<long>(connection, $"SELECT COUNT(*) FROM sessions WHERE session_id='{uncaptured.Detail.Session.SessionId:D}';"));
            Assert.Equal(0L, Scalar<long>(connection, $"""
                SELECT COUNT(*)
                FROM retention_items AS i
                JOIN session_events AS e ON e.event_id=i.source_item_id
                WHERE i.store_kind='session_event_content'
                  AND e.session_id='{uncaptured.Detail.Session.SessionId:D}';
                """));
        }

        Assert.Equal(SessionRawRetentionState.NotCaptured, store.GetRawRetentionState(uncaptured.Detail.Session.SessionId));
    }

    [Fact]
    public async Task ReadContentAsync_WithoutRetentionContextFailsClosedBeforeDatabaseAccess()
    {
        using var database = new SessionTestDatabase();
        File.Delete(database.Path);
        var store = new SqliteSessionStore(database.Path);

        var result = await store.ReadContentAsync(Guid.CreateVersion7(), Guid.CreateVersion7(), CancellationToken.None);

        Assert.Equal(SessionContentReadDisposition.Denied, result.Disposition);
        Assert.Null(result.Lease);
        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public void WriteRawContent_WithoutRetentionContextFailsClosedBeforeDatabaseMutation()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);

        Assert.Throws<RetentionCatalogUnavailableException>(() => store.Write(CreateBatch(DateTimeOffset.UnixEpoch)));

        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public void WriteReplayContent_WithoutRetentionContextFailsClosedBeforeDatabaseMutation()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        var batch = CreateBatch(DateTimeOffset.UnixEpoch) with { Content = [] };
        var candidate = new SessionReplayContentCandidate(
            batch.Detail.Events[0].EventId,
            "application/json",
            "{}");

        Assert.Throws<RetentionCatalogUnavailableException>(() =>
            ((IClassifiedSessionStore)store).WriteClassified(batch, [], [candidate]));

        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public void GetDetail_RemainsAvailableWithoutContentTable()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        using (var connection = database.Open())
        {
            Execute(connection, "DROP TABLE session_event_content;");
        }

        var detail = store.GetDetail(batch.Detail.Session.SessionId);

        Assert.NotNull(detail);
        Assert.Single(detail.Events);
    }

    [Fact]
    public void ProjectionState_GetAndUpsertRoundTripsAndUpdates()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        Assert.Null(store.GetProjectionState("otel-enricher"));
        var first = new SessionProjectionState("otel-enricher", null, 2, DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        var updated = first with { ProjectionCursor = 42, UnsupportedEventVersionCount = 3, UpdatedAt = first.UpdatedAt.AddMinutes(1) };

        store.UpsertProjectionState(first);
        Assert.Equal(first, store.GetProjectionState("otel-enricher"));
        store.UpsertProjectionState(updated);

        Assert.Equal(updated, store.GetProjectionState("otel-enricher"));
    }

    [Theory]
    [InlineData("UPDATE sessions SET status='invalid';")]
    [InlineData("UPDATE sessions SET completeness='invalid';")]
    [InlineData("UPDATE sessions SET raw_retention_state='invalid';")]
    [InlineData("UPDATE session_native_ids SET source_surface='invalid';")]
    [InlineData("UPDATE session_native_ids SET binding_kind='invalid';")]
    [InlineData("UPDATE session_runs SET source_surface='invalid';")]
    [InlineData("UPDATE session_runs SET status='invalid';")]
    [InlineData("UPDATE session_events SET source_surface='invalid';")]
    [InlineData("UPDATE session_events SET content_state='invalid';")]
    public void Schema_RejectsEveryInvalidEnumColumn(string sql)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        store.Write(CreateBatch(DateTimeOffset.UnixEpoch));
        using var connection = database.Open();

        Assert.Throws<SqliteException>(() => Execute(connection, sql));
    }

    [Theory]
    [InlineData("UPDATE session_runs SET input_tokens=-1;")]
    [InlineData("UPDATE session_runs SET output_tokens=-1;")]
    [InlineData("UPDATE session_runs SET total_tokens=-1;")]
    [InlineData("INSERT INTO session_projection_state(projector_key,projection_cursor,unsupported_event_version_count,updated_at) VALUES('bad-cursor',-1,0,'2026-07-11T00:00:00Z');")]
    [InlineData("INSERT INTO session_projection_state(projector_key,projection_cursor,unsupported_event_version_count,updated_at) VALUES('bad-version-count',NULL,-1,'2026-07-11T00:00:00Z');")]
    public void Schema_RejectsNegativeCounts(string sql)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        store.Write(CreateBatch(DateTimeOffset.UnixEpoch));
        using var connection = database.Open();

        Assert.Throws<SqliteException>(() => Execute(connection, sql));
    }

    [Theory]
    [InlineData("UPDATE session_runs SET parent_run_id=(SELECT run_id FROM session_runs WHERE session_id=$first) WHERE session_id=$second;")]
    [InlineData("UPDATE session_events SET run_id=(SELECT run_id FROM session_runs WHERE session_id=$first) WHERE session_id=$second;")]
    [InlineData("UPDATE session_events SET parent_event_id=(SELECT event_id FROM session_events WHERE session_id=$first) WHERE session_id=$second;")]
    public void Schema_RejectsCrossSessionRunAndEventOwnership(string sql)
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateBatch(DateTimeOffset.UnixEpoch, "native-a");
        var second = CreateBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "native-b");
        store.Write(first);
        store.Write(second);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$first", first.Detail.Session.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$second", second.Detail.Session.SessionId.ToString("D"));

        Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Write_StoresCanonicalLowercaseUuidText()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        using var connection = database.Open();

        Assert.Equal(batch.Detail.Session.SessionId.ToString("D").ToLowerInvariant(), Scalar<string>(connection, "SELECT session_id FROM sessions;"));
        Assert.Equal(batch.Detail.Runs[0].RunId.ToString("D").ToLowerInvariant(), Scalar<string>(connection, "SELECT run_id FROM session_runs;"));
        Assert.Equal(batch.Detail.Events[0].EventId.ToString("D").ToLowerInvariant(), Scalar<string>(connection, "SELECT event_id FROM session_events;"));
    }

    private static SessionWriteBatch CreateBatch(DateTimeOffset lastSeenAt, string nativeId = "native-1")
    {
        var session = new ObservedSession(
            Guid.CreateVersion7(),
            ObservedSessionStatus.Active,
            SessionCompleteness.Partial,
            "owner/repository",
            "workspace",
            lastSeenAt.AddMinutes(-2),
            null,
            lastSeenAt,
            SessionRawRetentionState.Expiring,
            lastSeenAt.AddMinutes(-2),
            lastSeenAt);
        var native = new SessionNativeId(session.SessionId, SessionSourceSurface.CopilotSdk, nativeId, SessionBindingKind.Native, lastSeenAt.AddMinutes(-2));
        var run = new ObservedSessionRun(
            Guid.CreateVersion7(), session.SessionId, SessionSourceSurface.CopilotSdk, "run-1", "trace-1", null,
            "gpt-5", ObservedSessionStatus.Active, lastSeenAt.AddMinutes(-1), null, 10, 20, 30);
        var @event = new ObservedSessionEvent(
            Guid.CreateVersion7(), session.SessionId, run.RunId, SessionSourceSurface.CopilotSdk, null, "trace-1", "received",
            "copilot-sdk-stream", $"event-{nativeId}", "user.message", lastSeenAt, SessionContentState.Available);
        var content = new SessionEventContent(@event.EventId, "application/json", "{\"text\":\"synthetic\"}", lastSeenAt, lastSeenAt.AddDays(90));
        return new(new SessionDetail(session, [native], [run], [@event]), [content]);
    }

    [Fact]
    public void Improvement_proposal_revision_starts_at_one_and_increments_on_lifecycle_changes()
    {
        using var database = new SessionTestDatabase();
        var store = CreateRawStore(database.Path);
        store.CreateSchema();
        var first = CreateTerminalBatch(DateTimeOffset.UnixEpoch, "revision-a");
        var second = CreateTerminalBatch(DateTimeOffset.UnixEpoch.AddMinutes(1), "revision-b");
        store.Write(first);
        store.Write(second);
        var proposal = CreateProposal([first, second]);
        store.CreateImprovementProposal(proposal);

        Assert.Equal(1, store.GetImprovementProposal(proposal.ProposalId)!.Revision);
        store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch.AddMinutes(2));
        Assert.Equal(2, store.GetImprovementProposal(proposal.ProposalId)!.Revision);
        store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch.AddMinutes(3));
        Assert.Equal(2, store.GetImprovementProposal(proposal.ProposalId)!.Revision);
        store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Candidate, DateTimeOffset.UnixEpoch.AddMinutes(4));
        Assert.Equal(3, store.GetImprovementProposal(proposal.ProposalId)!.Revision);
    }

    [Fact]
    public void CreateSchema_upgrades_a_real_version_six_database_and_keeps_legacy_application_receipts_queryable()
    {
        using var database = new SessionTestDatabase();
        var proposalId = Guid.Parse("00000004-0000-7000-8000-000000000006");
        var draftId = Guid.Parse("00000005-0000-7000-8000-000000000006");
        var applyId = Guid.Parse("00000006-0000-7000-8000-000000000006");
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "SchemaMigrations", "session", "session-v6.sqlite");
        File.Copy(fixture, database.Path);

        var store = CreateRawStore(database.Path);
        store.CreateSchema();

        using var verify = database.Open();
        Assert.Equal(14L, Scalar<long>(verify, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT revision FROM improvement_proposals;"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT COUNT(*) FROM pragma_table_info('improvement_proposal_sessions') WHERE name='proposal_revision';"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT proposal_revision FROM proposal_apply_drafts;"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT proposal_revision FROM proposal_applies;"));
        var receipt = Assert.Single(store.ListApplicationReceipts(proposalId));
        Assert.Equal((applyId, draftId, proposalId, 1, "applied"), (receipt.ApplyId, receipt.DraftId, receipt.ProposalId, receipt.ProposalRevision, receipt.State));
    }

    private static SessionWriteBatch CreateTerminalBatch(DateTimeOffset lastSeenAt, string nativeId)
    {
        var batch = CreateBatch(lastSeenAt, nativeId);
        var sessionId = batch.Detail.Session.SessionId;
        var runId = batch.Detail.Runs[0].RunId;
        var lifecycle = new ObservedSessionEvent(
            Guid.CreateVersion7(), sessionId, runId, SessionSourceSurface.CopilotSdk, null, "trace-1", "received",
            "copilot-sdk-stream", $"start-{nativeId}", "session.start", lastSeenAt.AddMinutes(-2), SessionContentState.NotCaptured);
        var exact = new ObservedSessionEvent(
            Guid.CreateVersion7(), sessionId, runId, SessionSourceSurface.CopilotSdk, null, "trace-1", "received",
            "otel-exact", $"otel-{nativeId}", "otel.span", lastSeenAt.AddMinutes(-1), SessionContentState.NotCaptured,
            MatchKind: SessionMatchKind.ExactNative);
        var terminal = new ObservedSessionEvent(
            Guid.CreateVersion7(), sessionId, runId, SessionSourceSurface.CopilotSdk, null, "trace-1", "received",
            "copilot-sdk-stream", $"terminal-{nativeId}", "session.task_complete", lastSeenAt, SessionContentState.NotCaptured);
        return batch with
        {
            Detail = batch.Detail with
            {
                Session = batch.Detail.Session with { Status = ObservedSessionStatus.Completed, Completeness = SessionCompleteness.Full, EndedAt = lastSeenAt },
                Runs = [batch.Detail.Runs[0] with { Status = ObservedSessionStatus.Completed, EndedAt = lastSeenAt }],
                Events = [batch.Detail.Events[0], lifecycle, exact, terminal],
            },
        };
    }

    private static ImprovementProposal CreateProposal(SessionWriteBatch batch) => CreateProposal([batch]);

    private static ImprovementProposal CreateProposal(IReadOnlyList<SessionWriteBatch> batches)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new(
            Guid.CreateVersion7(),
            1,
            ImprovementProposalStatus.Candidate,
            "skill",
            "Opaque target",
            "Improve evidence selection",
            "Use existing exact-bound evidence.",
            "More consistent review.",
            "Requires user review.",
            batches.Select(batch => batch.Detail.Session.SessionId).ToArray(),
            batches.Select(batch => new ImprovementProposalEvidenceReference("event", batch.Detail.Events[0].EventId.ToString("D"))).ToArray(),
            now,
            now,
            null,
            null);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T));
    }

    private static long CountSessionContentAccessLease(SqliteConnection connection, Guid eventId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM retention_leases AS lease
            JOIN retention_items AS item ON item.item_id=lease.item_id
            WHERE item.store_kind='session_event_content'
              AND item.source_item_id=$event_id
              AND lease.lease_kind='access';
            """;
        command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
        return (long)command.ExecuteScalar()!;
    }

    private static void WriteClassified(
        SqliteSessionStore store,
        SessionWriteBatch batch,
        params SessionTerminalFact[] terminalFacts) =>
        ((IClassifiedSessionStore)store).WriteClassified(batch, terminalFacts);

    private static void AssertProposalFailure(ImprovementProposalFailure expected, Action action)
    {
        var failure = Assert.Throws<ImprovementProposalStoreException>(action);
        Assert.Equal(expected, failure.Failure);
    }

    private static IReadOnlyList<string> ReadStrings(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return values;
    }

    private static IReadOnlyList<string> ReadColumns(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}') ORDER BY cid;";
        using var reader = command.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static IReadOnlyDictionary<string, byte[]> ReadCompleteSessionRowBytes(
        SqliteConnection connection,
        string tableName,
        Guid eventId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = tableName switch
        {
            "retention_items" => "SELECT * FROM retention_items WHERE store_kind='session_event_content' AND source_item_id=$event_id;",
            "session_event_content" => "SELECT * FROM session_event_content WHERE event_id=$event_id;",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName)),
        };
        command.Parameters.AddWithValue("$event_id", eventId.ToString("D"));
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        var value = Enumerable.Range(0, reader.FieldCount)
            .ToDictionary(reader.GetName, index => EncodeSqliteValue(reader.GetValue(index)), StringComparer.Ordinal);
        Assert.False(reader.Read());
        return value;
    }

    private static void AssertCompleteRowBytesEqual(
        IReadOnlyDictionary<string, byte[]> expected,
        IReadOnlyDictionary<string, byte[]> actual)
    {
        Assert.Equal(expected.Keys, actual.Keys);
        foreach (var column in expected.Keys)
        {
            Assert.Equal(expected[column], actual[column]);
        }
    }

    private static byte[] EncodeSqliteValue(object value)
    {
        switch (value)
        {
            case DBNull:
                return [0];
            case long integer:
                {
                    var bytes = new byte[9];
                    bytes[0] = 1;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), integer);
                    return bytes;
                }
            case double real:
                {
                    var bytes = new byte[9];
                    bytes[0] = 2;
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(1), BitConverter.DoubleToInt64Bits(real));
                    return bytes;
                }
            case string text:
                return PrefixSqliteValue(3, System.Text.Encoding.UTF8.GetBytes(text));
            case byte[] blob:
                return PrefixSqliteValue(4, blob);
            default:
                throw new Xunit.Sdk.XunitException($"Unexpected SQLite value type '{value.GetType().FullName}'.");
        }
    }

    private static byte[] PrefixSqliteValue(byte storageClass, byte[] value)
    {
        var bytes = new byte[value.Length + 1];
        bytes[0] = storageClass;
        value.CopyTo(bytes, 1);
        return bytes;
    }

    private sealed class SessionTestDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cao-session-{Guid.NewGuid():N}");

        public SessionTestDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "sessions.db");
        }

        public string Path { get; }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open();
            Execute(connection, "PRAGMA foreign_keys=ON;");
            return connection;
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    private sealed class SessionStoreTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SqliteSessionStoreTests
{
    [Fact]
    public void ImprovementProposals_PersistCandidateWithOpaqueReferences()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
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
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
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
        var store = new SqliteSessionStore(database.Path);
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

        Assert.Throws<InvalidOperationException>(() =>
            store.UpdateImprovementProposalStatus(
                competing.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ImprovementProposals_RejectVerifiedWrites()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch) with { Status = ImprovementProposalStatus.Verified };

        Assert.Throws<InvalidOperationException>(() => store.CreateImprovementProposal(proposal));
    }

    [Theory]
    [InlineData(ImprovementProposalStatus.Candidate)]
    [InlineData(ImprovementProposalStatus.Recommended)]
    public void ImprovementProposals_VerifiedProposalCannotBeChangedByCanvasStatusUpdates(ImprovementProposalStatus requestedStatus)
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
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

        Assert.Throws<InvalidOperationException>(() =>
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
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch);

        Assert.Throws<InvalidOperationException>(() => store.CreateImprovementProposal(proposal with { Status = ImprovementProposalStatus.Recommended }));
        Assert.Throws<InvalidOperationException>(() => store.CreateImprovementProposal(proposal with { RecommendedAt = DateTimeOffset.UnixEpoch }));
        Assert.Throws<InvalidOperationException>(() => store.CreateImprovementProposal(proposal with { VerifiedAt = DateTimeOffset.UnixEpoch }));
    }

    [Fact]
    public void Promotion_RequiresTwoTerminalNativeSourceSessions()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch);
        store.CreateImprovementProposal(proposal);

        Assert.Throws<InvalidOperationException>(() =>
            store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Promotion_RequiresEvidenceFromTwoDistinctSourceSessions()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
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

        Assert.Throws<InvalidOperationException>(() =>
            store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ImprovementProposals_RejectNonVersionSevenProposalId()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch) with { ProposalId = Guid.NewGuid() };

        Assert.Throws<InvalidOperationException>(() => store.CreateImprovementProposal(proposal));
    }

    [Fact]
    public void Promotion_RejectsEvidenceOutsideSourceSessions()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
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

        Assert.Throws<InvalidOperationException>(() =>
            store.UpdateImprovementProposalStatus(proposal.ProposalId, ImprovementProposalStatus.Recommended, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void ImprovementProposals_InvalidWriteRollsBackWithoutPartialState()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch) with { TargetKind = "invalid" };

        Assert.Throws<InvalidOperationException>(() => store.CreateImprovementProposal(proposal));

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposals;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_sessions;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_evidence;"));
    }

    [Fact]
    public void ImprovementProposals_TransactionFailureRollsBackRootAndAssociations()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.UnixEpoch);
        store.Write(batch);
        var proposal = CreateProposal(batch) with { SourceSessionIds = [batch.Detail.Session.SessionId, Guid.CreateVersion7()] };

        Assert.Throws<SqliteException>(() => store.CreateImprovementProposal(proposal));

        using var connection = database.Open();
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposals;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_sessions;"));
        Assert.Equal(0L, Scalar<long>(connection, "SELECT COUNT(*) FROM improvement_proposal_evidence;"));
    }

    [Fact]
    public void ImprovementProposals_RejectMalformedDomainValues()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
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
            Assert.Throws<InvalidOperationException>(() => store.CreateImprovementProposal(invalid));
        }
    }

    [Fact]
    public void CreateSchema_EmptyDatabaseCreatesSessionSchemaAndIsIdempotent()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);

        store.CreateSchema();
        store.CreateSchema();

        using var connection = database.Open();
        Assert.Equal(3L, Scalar<long>(connection, "SELECT version FROM schema_version WHERE component = 'session';"));
        foreach (var table in new[] { "sessions", "session_native_ids", "session_runs", "session_events", "session_event_content", "session_projection_state", "session_human_evaluation", "improvement_proposals", "improvement_proposal_sessions", "improvement_proposal_evidence" })
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
    public void CreateSchema_IncompatibleSessionVersionRollsBackBeforeSessionTableDdl()
    {
        using var database = new SessionTestDatabase();
        using (var connection = database.Open())
        {
            Execute(connection, "CREATE TABLE schema_version(component TEXT PRIMARY KEY, version INTEGER NOT NULL); INSERT INTO schema_version VALUES('session', 4);");
        }

        Assert.Throws<InvalidOperationException>(() => new SqliteSessionStore(database.Path).CreateSchema());

        using var verify = database.Open();
        Assert.Equal(4L, Scalar<long>(verify, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(0L, Scalar<long>(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('sessions','session_native_ids','session_runs','session_events','session_event_content','session_projection_state','session_human_evaluation');"));
    }

    [Fact]
    public void CreateSchema_VersionOneDatabaseAddsHumanEvaluationTable()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        store.Write(batch);
        using (var connection = database.Open())
        {
            Execute(connection, "DROP TABLE session_human_evaluation; UPDATE schema_version SET version=1 WHERE component='session';");
        }

        store.CreateSchema();

        using var verify = database.Open();
        Assert.Equal(3L, Scalar<long>(verify, "SELECT version FROM schema_version WHERE component='session';"));
        Assert.Equal(1L, Scalar<long>(verify, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='session_human_evaluation';"));
        Assert.NotNull(store.GetDetail(batch.Detail.Session.SessionId));
    }

    [Fact]
    public void CreateSchema_VersionTwoDatabaseAddsProposalTablesAndPreservesSessionRow()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
        store.CreateSchema();
        var batch = CreateBatch(DateTimeOffset.Parse("2026-07-11T00:00:00Z"));
        store.Write(batch);
        using (var connection = database.Open())
        {
            Execute(connection, "DROP TABLE improvement_proposal_evidence; DROP TABLE improvement_proposal_sessions; DROP TABLE improvement_proposals; UPDATE schema_version SET version=2 WHERE component='session';");
        }

        store.CreateSchema();

        using var verify = database.Open();
        Assert.Equal(3L, Scalar<long>(verify, "SELECT version FROM schema_version WHERE component='session';"));
        foreach (var table in new[] { "improvement_proposals", "improvement_proposal_sessions", "improvement_proposal_evidence" })
        {
            Assert.Equal(1L, Scalar<long>(verify, $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='{table}';"));
        }
        Assert.Equal(batch.Detail.Session.SessionId, store.GetDetail(batch.Detail.Session.SessionId)?.Session.SessionId);
    }

    [Fact]
    public void Write_PersistsMetadataAndDuplicateSourceReplayIsIdempotent()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
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
    public void Write_ChildRunMayBeListedBeforeNewParentRun()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
    public void GetContent_UsesSessionAndEventKeysAndExpiresAtExactBoundary()
    {
        using var database = new SessionTestDatabase();
        var now = DateTimeOffset.Parse("2026-07-11T00:00:00Z");
        var time = new MutableTimeProvider(now);
        var store = new SqliteSessionStore(database.Path, time);
        store.CreateSchema();
        var batch = CreateBatch(now);
        store.Write(batch);
        var sessionId = batch.Detail.Session.SessionId;
        var eventId = batch.Detail.Events[0].EventId;

        var available = store.GetContent(sessionId, eventId);
        Assert.Equal(SessionContentState.Available, available?.State);
        Assert.Equal(batch.Content[0], available?.Content);
        Assert.Null(store.GetContent(Guid.CreateVersion7(), eventId));
        Assert.Null(store.GetContent(sessionId, Guid.CreateVersion7()));

        time.Advance(TimeSpan.FromDays(90));
        var expired = store.GetContent(sessionId, eventId);
        Assert.Equal(SessionContentState.ExpiredPendingDeletion, expired?.State);
        Assert.Null(expired?.Content);
        using var connection = database.Open();
        Assert.Equal(1L, Scalar<long>(connection, "SELECT COUNT(*) FROM session_event_content;"));
    }

    [Fact]
    public void GetDetail_RemainsAvailableWithoutContentTable()
    {
        using var database = new SessionTestDatabase();
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
        var store = new SqliteSessionStore(database.Path);
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
            SessionCompleteness.Rich,
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

    private static SessionWriteBatch CreateTerminalBatch(DateTimeOffset lastSeenAt, string nativeId)
    {
        var batch = CreateBatch(lastSeenAt, nativeId);
        return batch with
        {
            Detail = batch.Detail with
            {
                Session = batch.Detail.Session with { Status = ObservedSessionStatus.Completed, EndedAt = lastSeenAt },
                Runs = [batch.Detail.Runs[0] with { Status = ObservedSessionStatus.Completed, EndedAt = lastSeenAt }],
            },
        };
    }

    private static ImprovementProposal CreateProposal(SessionWriteBatch batch) => CreateProposal([batch]);

    private static ImprovementProposal CreateProposal(IReadOnlyList<SessionWriteBatch> batches)
    {
        var now = DateTimeOffset.UnixEpoch;
        return new(
            Guid.CreateVersion7(),
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
}

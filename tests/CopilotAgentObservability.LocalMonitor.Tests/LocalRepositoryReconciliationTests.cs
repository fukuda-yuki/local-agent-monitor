using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryReconciliationTests
{
    [Fact]
    public async Task SessionConflictWinsOverMissingAndPublishesNoCatalogDomainRows()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Missing"),
            new(LocalRepositoryAdmissionFixture.Trace(2), LocalRepositoryAdmissionFixture.Span(2), "https://github.com/Example/Conflict"));

        await fixture.RunAsync(payload, [
            LocalRepositoryAdmissionFixture.MissingEvent(1),
            LocalRepositoryAdmissionFixture.ConflictingEvent(2),
        ]);

        Assert.Equal("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal("catalog_session_identity_conflict", fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task MissingSessionWaitsAndPublishesNoCatalogDomainRows()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Waiting"));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MissingEvent(1)]);

        Assert.Equal("waiting_session", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task ContradictoryRawLinkedProvenanceIsSchemaViolationWithoutFallback()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Schema"));
        var prepared = fixture.Prepare(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)], contradictoryProvenance: true);

        await fixture.RunPreparedAsync(prepared);

        Assert.Equal("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal("catalog_schema_violation", fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task Worker_HoldsItsRawReferenceThroughPreparationAndDisposesItBeforeReturning()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/ReferenceLifetime"));
        var prepared = fixture.Prepare(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        Func<RawTelemetryRecord>? retainedAccess = null;
        var observedPayload = string.Empty;

        var outcome = await fixture.RunPreparedAsync(
            prepared,
            lastRawAccessObserverForTesting: access =>
            {
                retainedAccess = access;
                observedPayload = access().PayloadJson;
            });

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal(payload, observedPayload);
        Assert.Throws<ObjectDisposedException>(() => retainedAccess!());
        Assert.True(fixture.DomainRowCount() > 0);
    }

    [Fact]
    public async Task MalformedPayloadIsParseFailureWithNoDomainRows()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();

        await fixture.RunAsync("{", []);

        Assert.Equal("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal("catalog_parse_failure", fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task ProcessorPayloadMismatchRetriesWithoutTerminalOrDomainPublication()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Verified")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        var substitutedPayload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Substituted"));

        var outcome = await fixture.RunWithProcessorPayloadAsync(prepared, substitutedPayload);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(string.Empty, fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
        Assert.Equal("local_repository_verified_payload_mismatch", fixture.LastProcessorException?.Message);
    }

    [Theory]
    [InlineData("parse")]
    [InlineData("provenance")]
    [InlineData("join_conflict")]
    [InlineData("join_wait")]
    public async Task TerminalAndWaitingFinalizersRequireFreshRetentionAuthority(string scenario)
    {
        LocalRepositoryAdmissionFixture? fixture = null;
        var reconciliationCheckpoint = new DelegatingReconciliationCheckpoint(stage =>
        {
            if (stage == LocalRepositoryReconciliationCheckpoint.AfterRawAvailabilityRead)
            {
                fixture!.Execute($"""
                    UPDATE local_repository_reconciliation_queue
                    SET lease_expires_at='{fixture.Clock.GetUtcNow().AddMinutes(5):O}'
                    WHERE state='leased';
                    """);
            }
        });
        using (fixture = new LocalRepositoryAdmissionFixture(
            reconciliationCheckpoint: reconciliationCheckpoint,
            processorTimeProvider: new FinalizationExpiringTimeProvider()))
        {
            var payload = LocalRepositoryAdmissionFixture.SpanPayload(
                new LocalRepositoryAdmissionFixture.SpanInput(
                    LocalRepositoryAdmissionFixture.Trace(1),
                    LocalRepositoryAdmissionFixture.Span(1),
                    "https://github.com/Example/FinalAuthority"));
            var prepared = scenario switch
            {
                "parse" => fixture.Prepare("{", []),
                "provenance" => fixture.Prepare(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)], contradictoryProvenance: true),
                "join_conflict" => fixture.Prepare(payload, [LocalRepositoryAdmissionFixture.ConflictingEvent(1)]),
                "join_wait" => fixture.Prepare(payload, [LocalRepositoryAdmissionFixture.MissingEvent(1)]),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };

            var outcome = await fixture.RunPreparedAsync(prepared);

            Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
            Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
            Assert.Equal(string.Empty, fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
            Assert.Equal(0, fixture.DomainRowCount());
            Assert.Equal("local_repository_retention_authority_lost", fixture.LastProcessorException?.Message);
        }
    }

    [Fact]
    public async Task Exactly128DistinctAutomaticCandidatesAreAllowed()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var sessionId = LocalRepositoryAdmissionFixture.Session(999);
        var spans = Enumerable.Range(1, 128)
            .Select(index => new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(index),
                LocalRepositoryAdmissionFixture.Span(index),
                $"https://github.com/example/repository-{index}"))
            .ToArray();
        var events = Enumerable.Range(1, 128)
            .Select(index => LocalRepositoryAdmissionFixture.MatchedEvent(index, sessionId))
            .ToArray();

        await fixture.RunAsync(LocalRepositoryAdmissionFixture.SpanPayload(spans), events);

        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(128, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal("conflict", fixture.ScalarText("SELECT new_state FROM session_repository_assignment_history;"));
    }

    [Fact]
    public async Task Candidate129FailsTerminalAndRollsBackTheEntireRawRecordGraph()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var sessionId = LocalRepositoryAdmissionFixture.Session(999);
        var spans = Enumerable.Range(1, 129)
            .Select(index => new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(index),
                LocalRepositoryAdmissionFixture.Span(index),
                $"https://github.com/example/repository-{index}"))
            .ToArray();
        var events = Enumerable.Range(1, 129)
            .Select(index => LocalRepositoryAdmissionFixture.MatchedEvent(index, sessionId))
            .ToArray();

        await fixture.RunAsync(LocalRepositoryAdmissionFixture.SpanPayload(spans), events);

        Assert.Equal("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal("catalog_cardinality_exceeded", fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task ExistingPlusProspectiveCandidateUnionAllows128AndRejects129WithoutChangingRevision()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var sessionId = LocalRepositoryAdmissionFixture.Session(777);
        var firstSpans = Enumerable.Range(1, 127)
            .Select(index => new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(index),
                LocalRepositoryAdmissionFixture.Span(index),
                $"https://github.com/example/union-{index}"))
            .ToArray();
        var firstEvents = Enumerable.Range(1, 127)
            .Select(index => LocalRepositoryAdmissionFixture.MatchedEvent(index, sessionId))
            .ToArray();
        await fixture.RunAsync(LocalRepositoryAdmissionFixture.SpanPayload(firstSpans), firstEvents);

        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(128),
                LocalRepositoryAdmissionFixture.Span(128),
                "https://github.com/example/union-128")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(128, sessionId)]);

        Assert.Equal(128, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions WHERE session_id='" + sessionId + "';"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='" + sessionId + "';"));

        var rejected = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(129),
                LocalRepositoryAdmissionFixture.Span(129),
                "https://github.com/example/union-129")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(129, sessionId)]);
        await fixture.RunPreparedAsync(rejected);

        Assert.Equal("catalog_cardinality_exceeded", fixture.ScalarText(
            "SELECT terminal_reason FROM local_repository_reconciliation_queue WHERE raw_record_id=" + rejected.RawRecordId + ";"));
        Assert.Equal(128, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, fixture.ScalarLong(
            "SELECT COUNT(*) FROM session_repository_observations WHERE raw_record_id=" + rejected.RawRecordId + ";"));
        Assert.Equal(2, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions WHERE session_id='" + sessionId + "';"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='" + sessionId + "';"));
    }

    public static TheoryData<int> PublicationFailureStages => new()
    {
        (int)LocalRepositoryAdmissionCheckpoint.BeforePublication,
        (int)LocalRepositoryAdmissionCheckpoint.AfterRepositories,
        (int)LocalRepositoryAdmissionCheckpoint.AfterLocators,
        (int)LocalRepositoryAdmissionCheckpoint.AfterLocatorHeads,
        (int)LocalRepositoryAdmissionCheckpoint.AfterRepositoryHistory,
        (int)LocalRepositoryAdmissionCheckpoint.AfterObservations,
        (int)LocalRepositoryAdmissionCheckpoint.AfterContexts,
        (int)LocalRepositoryAdmissionCheckpoint.AfterAssignments,
        (int)LocalRepositoryAdmissionCheckpoint.BeforeQueueCompletion,
    };

    [Theory]
    [MemberData(nameof(PublicationFailureStages))]
    public async Task FailureAtEveryPublicationStageRollsBackDomainRowsAndQueueCompletion(int checkpointValue)
    {
        var checkpoint = (LocalRepositoryAdmissionCheckpoint)checkpointValue;
        using var fixture = new LocalRepositoryAdmissionFixture(new ThrowingAdmissionCheckpoint(checkpoint));
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Rollback"));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task ProcessingTimesUseTrustedClockWhileObservedAtUsesExactSourceProvenance()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Times"));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal(LocalRepositoryAdmissionFixture.ObservedAt, fixture.ScalarText("SELECT observed_at FROM session_repository_observations;"));
        Assert.Equal(LocalRepositoryAdmissionFixture.ObservedAt, fixture.ScalarText("SELECT observed_at FROM session_repository_observation_contexts;"));
        Assert.Equal(fixture.Clock.GetUtcNow().ToString("O"), fixture.ScalarText("SELECT created_at FROM local_repositories;"));
        Assert.Equal(fixture.Clock.GetUtcNow().ToString("O"), fixture.ScalarText("SELECT occurred_at FROM local_repository_history;"));
    }

    [Fact]
    public void CatalogAndQueueStoreBindingMismatch_IsRejectedBeforeProcessing()
    {
        using var queueFixture = new LocalRepositoryAdmissionFixture();
        using var catalogFixture = new LocalRepositoryAdmissionFixture();
        var queue = new SqliteLocalRepositoryReconciliationStore(queueFixture.DatabasePath);

        var exception = Assert.Throws<InvalidOperationException>(() => new SqliteLocalRepositoryCatalogStore(
            catalogFixture.DatabasePath,
            queue,
            new LocalRepositoryAssignmentResolver()));

        Assert.Equal("local_repository_store_binding_mismatch", exception.Message);
        Assert.Equal(0, queueFixture.DomainRowCount());
        Assert.Equal(0, catalogFixture.DomainRowCount());
    }

    [Fact]
    public async Task StaleQueueCompletionWithFreshRetentionRollsBackGraphAndReturnsPending()
    {
        var processorClock = new MutableTimeProvider(DateTimeOffset.UnixEpoch);
        LocalRepositoryAdmissionFixture? fixture = null;
        var completionCheckpoints = 0;
        var checkpoint = new DelegatingAdmissionCheckpoint(stage =>
        {
            if (stage != LocalRepositoryAdmissionCheckpoint.BeforeQueueCompletion)
                return;

            Assert.Equal(DateTimeOffset.UnixEpoch, fixture!.Clock.GetUtcNow());
            processorClock.Advance(TimeSpan.FromSeconds(31));
            Interlocked.Increment(ref completionCheckpoints);
        });
        using (fixture = new LocalRepositoryAdmissionFixture(
            checkpoint,
            processorTimeProvider: processorClock))
        {
            var outcome = await fixture.RunAsync(
                LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                    LocalRepositoryAdmissionFixture.Trace(1),
                    LocalRepositoryAdmissionFixture.Span(1),
                    "https://github.com/Example/StaleCompletion")),
                [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

            Assert.Equal(1, Volatile.Read(ref completionCheckpoints));
            Assert.Equal(DateTimeOffset.UnixEpoch, fixture.Clock.GetUtcNow());
            Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(31), processorClock.GetUtcNow());
            Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
            Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
            Assert.Equal(string.Empty, fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
            Assert.Equal(0, fixture.DomainRowCount());
            var exception = Assert.IsType<InvalidOperationException>(fixture.LastProcessorException);
            Assert.Equal("local_repository_queue_authority_lost", exception.Message);
        }
    }

    [Fact]
    public async Task FinalQueueFenceExpiryRollsBackThePublishedGraph()
    {
        LocalRepositoryAdmissionFixture? fixture = null;
        var checkpoint = new DelegatingAdmissionCheckpoint((stage) =>
        {
            if (stage == LocalRepositoryAdmissionCheckpoint.BeforeQueueCompletion)
                fixture!.Clock.Advance(TimeSpan.FromSeconds(31));
        });
        using (fixture = new LocalRepositoryAdmissionFixture(checkpoint))
        {
            var outcome = await fixture.RunAsync(
                LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                    LocalRepositoryAdmissionFixture.Trace(1),
                    LocalRepositoryAdmissionFixture.Span(1),
                    "https://github.com/Example/ExpiredFence")),
                [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

            Assert.Equal(LocalRepositoryReconciliationWorkOutcome.StaleOwner, outcome);
            Assert.Equal("leased", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
            Assert.Equal(string.Empty, fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
            Assert.Equal(0, fixture.DomainRowCount());
            var exception = Assert.IsType<InvalidOperationException>(fixture.LastProcessorException);
            Assert.Equal("local_repository_queue_authority_lost", exception.Message);
        }
    }

    [Fact]
    public async Task HeartbeatBusyBeforeLocalExpiryKeepsProductionPublicationAuthority()
    {
        using var admission = new HoldingPreparationCheckpoint();
        using var heartbeat = new HeartbeatOutcomeCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(admission, heartbeat);
        heartbeat.DatabasePath = fixture.DatabasePath;
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/HeartbeatBusy")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(admission.Held.Wait(TimeSpan.FromSeconds(10)));
        heartbeat.HoldWriterLock();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(heartbeat.Busy.Wait(TimeSpan.FromSeconds(10)));
        heartbeat.ReleaseWriterLock();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(heartbeat.Applied.Wait(TimeSpan.FromSeconds(10)));
        admission.Release.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        AssertSinglePublishedGraph(fixture);
    }

    [Fact]
    public async Task HeartbeatAtExactLocalExpiryFencesProductionPublicationUntilRecovery()
    {
        using var admission = new HoldingPreparationCheckpoint();
        using var heartbeat = new HeartbeatOutcomeCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(admission, heartbeat);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/HeartbeatExpiry")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var firstWork = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(admission.Held.Wait(TimeSpan.FromSeconds(10)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(heartbeat.Expired.Wait(TimeSpan.FromSeconds(10)));

        admission.Release.Set();
        var expired = await firstWork.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.StaleOwner, expired);
        Assert.Equal("leased", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));

        var recovered = await fixture.RunExistingAsync().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, recovered);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        AssertSinglePublishedGraph(fixture);
    }

    [Fact]
    public async Task CallerCancellationAfterRejectedHandoffPropagatesWithoutPublication()
    {
        using var cancellation = new CancellationTokenSource();
        using var checkpoint = new PreparationHandoffCheckpoint(
            holdAfterRejection: true,
            onHandoffRejected: cancellation.Cancel);
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/CancelledHandoff")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared, cancellation.Token));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        checkpoint.ReleasePreparation.Set();
        Assert.True(checkpoint.BeforeHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        fixture.Execute("""
            CREATE TRIGGER reject_cancelled_repository_handoff
            BEFORE UPDATE OF lease_expires_at ON local_repository_reconciliation_queue
            WHEN OLD.state='leased' AND NEW.state='leased'
            BEGIN
                SELECT RAISE(IGNORE);
            END;
            """);
        checkpoint.ReleaseHandoff.Set();
        Assert.True(checkpoint.HandoffRejected.Wait(TimeSpan.FromSeconds(10)));
        checkpoint.ReleaseRejection.Set();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);

        Assert.Equal("leased", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task PayloadParsingCompletesWithoutAWriterTransaction()
    {
        using var checkpoint = new TransactionFreePayloadParsingCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/TransactionFreeParse")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.BeforeParsingHeld.Wait(TimeSpan.FromSeconds(10)));
        using var blockerConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Pooling = false,
        }.ToString());
        blockerConnection.Open();
        using var blockerTransaction = blockerConnection.BeginTransaction(deferred: false);
        checkpoint.ReleaseParsing.Set();

        Assert.True(checkpoint.AfterPreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        blockerTransaction.Dispose();
        blockerConnection.Dispose();
        checkpoint.ReleasePreparation.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        AssertSinglePublishedGraph(fixture);
    }

    [Fact]
    public async Task UnexpectedPeriodicHeartbeatFaultReturnsTheLatestRenewedLeaseToRetry()
    {
        using var checkpoint = new FaultingPeriodicHeartbeatCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/HeartbeatFault")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(checkpoint.AppliedThenFaulted.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            fixture.Clock.GetUtcNow().AddSeconds(30).ToString("O"),
            fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        checkpoint.ReleasePreparation.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task DuePeriodicRetentionAuthorityDenialCancelsPreparationAndReturnsTheQueueToRetry()
    {
        using var checkpoint = new PreparationHandoffCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/PeriodicRetentionDenial")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        var retentionExpiry = fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';");
        for (var tick = 1; tick <= 5; tick++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(
                () => checkpoint.PeriodicHeartbeatCount + checkpoint.PeriodicHeartbeatRejectedCount >= tick,
                TimeSpan.FromSeconds(10)));
            Assert.Equal(0, checkpoint.PeriodicHeartbeatRejectedCount);
        }
        fixture.Execute($"UPDATE retention_items SET revision=revision+1 WHERE store_kind='raw_record' AND source_item_id='{prepared.RawRecordId}';");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(SpinWait.SpinUntil(
            () => checkpoint.PeriodicHeartbeatRejectedCount == 1,
            TimeSpan.FromSeconds(10)));
        Assert.Equal(5, checkpoint.PeriodicHeartbeatCount);
        Assert.Equal(retentionExpiry, fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';"));
        checkpoint.ReleasePreparation.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task NonDueRetentionRevisionDriftKeepsPeriodicAndLatestHandoffAuthorityPublishable()
    {
        using var checkpoint = new PreparationHandoffCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/NonDueRetentionDrift")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        var retentionExpiry = fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';");
        fixture.Execute($"UPDATE retention_items SET revision=revision+1 WHERE store_kind='raw_record' AND source_item_id='{prepared.RawRecordId}';");
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.True(SpinWait.SpinUntil(
            () => checkpoint.PeriodicHeartbeatCount == 1,
            TimeSpan.FromSeconds(10)));
        Assert.Equal(0, checkpoint.PeriodicHeartbeatRejectedCount);
        Assert.Equal(
            fixture.Clock.GetUtcNow().AddSeconds(30).ToString("O"),
            fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        Assert.Equal(retentionExpiry, fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';"));

        checkpoint.ReleasePreparation.Set();
        Assert.True(checkpoint.BeforeHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        checkpoint.ReleaseHandoff.Set();
        Assert.True(checkpoint.AfterHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            fixture.Clock.GetUtcNow().AddSeconds(30).ToString("O"),
            fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        Assert.Equal(retentionExpiry, fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, checkpoint.PeriodicHeartbeatRejectedCount);
        Assert.Equal(0, fixture.DomainRowCount());
        checkpoint.ReleaseFinalization.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        AssertSinglePublishedGraph(fixture);
    }

    [Fact]
    public async Task LongPayloadPreparationKeepsPeriodicAuthorityAndCompletesTheFirstAttempt()
    {
        using var checkpoint = new PreparationHandoffCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/LongPreparation")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        foreach (var seconds in Enumerable.Range(1, 17).Select(static tick => tick * 10))
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(
                () => checkpoint.PeriodicHeartbeatCount + checkpoint.PeriodicHeartbeatRejectedCount >= seconds / 10,
                TimeSpan.FromSeconds(10)));
            Assert.Equal(0, checkpoint.PeriodicHeartbeatRejectedCount);
            var expectedExpiry = fixture.Clock.GetUtcNow().AddSeconds(30).ToString("O");
            Assert.Equal(
                expectedExpiry,
                fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        }
        Assert.Equal(
            DateTimeOffset.UnixEpoch.AddSeconds(240).ToString("O"),
            fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';"));
        checkpoint.ReleasePreparation.Set();
        Assert.True(checkpoint.BeforeHandoffHeld.Wait(TimeSpan.FromSeconds(10)));

        foreach (var tick in Enumerable.Range(18, 6))
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(
                () => checkpoint.PeriodicHeartbeatCount + checkpoint.PeriodicHeartbeatRejectedCount >= tick,
                TimeSpan.FromSeconds(10)));
            Assert.Equal(0, checkpoint.PeriodicHeartbeatRejectedCount);
            Assert.Equal(
                fixture.Clock.GetUtcNow().AddSeconds(30).ToString("O"),
                fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        }
        checkpoint.ReleaseHandoff.Set();
        Assert.True(checkpoint.AfterHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(
            fixture.Clock.GetUtcNow().AddSeconds(30).ToString("O"),
            fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        Assert.Equal(
            DateTimeOffset.UnixEpoch.AddSeconds(300).ToString("O"),
            fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';"));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(23, checkpoint.PeriodicHeartbeatCount);
        Assert.Equal(0, fixture.DomainRowCount());
        checkpoint.ReleaseFinalization.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        AssertSinglePublishedGraph(fixture);
    }

    [Theory]
    [InlineData("queue")]
    [InlineData("retention")]
    public async Task RejectedHandoffRollsBackBothRenewalsAndPublishesNothing(string rejectedCas)
    {
        using var checkpoint = new PreparationHandoffCheckpoint(holdAfterRejection: true);
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/RejectedHandoff")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        for (var tick = 1; tick <= 4; tick++)
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(10));
            Assert.True(SpinWait.SpinUntil(
                () => checkpoint.PeriodicHeartbeatCount + checkpoint.PeriodicHeartbeatRejectedCount >= tick,
                TimeSpan.FromSeconds(10)));
            Assert.Equal(0, checkpoint.PeriodicHeartbeatRejectedCount);
        }
        checkpoint.ReleasePreparation.Set();
        Assert.True(checkpoint.BeforeHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        var queueExpiry = fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;");
        fixture.Execute(rejectedCas == "queue"
            ? """
                CREATE TRIGGER reject_repository_queue_handoff
                BEFORE UPDATE OF updated_at ON local_repository_reconciliation_queue
                WHEN NEW.state='leased'
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """
            : """
                CREATE TRIGGER reject_repository_retention_handoff
                BEFORE UPDATE OF expires_at ON retention_leases
                WHEN OLD.lease_kind='operation'
                BEGIN
                    SELECT RAISE(IGNORE);
                END;
                """);
        var retentionExpiry = fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';");
        using var blockerConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Pooling = false,
        }.ToString());
        blockerConnection.Open();
        using var blockerTransaction = blockerConnection.BeginTransaction(deferred: false);
        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(checkpoint.HeartbeatBusy.Wait(TimeSpan.FromSeconds(10)));
        blockerTransaction.Dispose();
        blockerConnection.Dispose();
        checkpoint.ReleaseHandoff.Set();
        Assert.True(checkpoint.HandoffRejected.Wait(TimeSpan.FromSeconds(10)));

        Assert.Equal(queueExpiry, fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        Assert.Equal(retentionExpiry, fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, fixture.DomainRowCount());
        checkpoint.ReleaseRejection.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT attempt_count FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task BusyHandoffPublishesNothingAndReturnsTheOwnedQueueToRetry()
    {
        using var checkpoint = new PreparationHandoffCheckpoint(holdAfterRejection: true);
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/BusyHandoff")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        checkpoint.ReleasePreparation.Set();
        Assert.True(checkpoint.BeforeHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        var queueExpiry = fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;");
        var retentionExpiry = fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';");
        using var blockerConnection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.DatabasePath,
            Pooling = false,
        }.ToString());
        blockerConnection.Open();
        using var blockerTransaction = blockerConnection.BeginTransaction(deferred: false);
        checkpoint.ReleaseHandoff.Set();
        Assert.True(checkpoint.HandoffRejected.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(queueExpiry, fixture.ScalarText("SELECT lease_expires_at FROM local_repository_reconciliation_queue;"));
        Assert.Equal(retentionExpiry, fixture.ScalarText("SELECT expires_at FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, fixture.DomainRowCount());
        blockerTransaction.Dispose();
        blockerConnection.Dispose();
        checkpoint.ReleaseRejection.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Theory]
    [InlineData("source_surface")]
    [InlineData("source_application_version")]
    [InlineData("observed_at")]
    public async Task FinalTransactionRejectsAnyPreparedProvenanceDrift(string field)
    {
        using var checkpoint = new PreparationHandoffCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint, checkpoint);
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/ProvenanceDrift")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        var work = Task.Run(() => fixture.RunPreparedAsync(prepared));
        Assert.True(checkpoint.PreparationHeld.Wait(TimeSpan.FromSeconds(10)));
        fixture.Execute(field switch
        {
            "source_surface" => $"UPDATE source_schema_observations SET source_surface='github-copilot-vscode' WHERE raw_record_id={prepared.RawRecordId};",
            "source_application_version" => $"UPDATE source_schema_observations SET source_application_version='2.0' WHERE raw_record_id={prepared.RawRecordId};",
            "observed_at" => $"UPDATE source_schema_observations SET observed_at='2026-08-01T01:02:04.1234567+00:00' WHERE raw_record_id={prepared.RawRecordId};",
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        });
        checkpoint.ReleasePreparation.Set();
        Assert.True(checkpoint.BeforeHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        checkpoint.ReleaseHandoff.Set();
        Assert.True(checkpoint.AfterHandoffHeld.Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, fixture.DomainRowCount());
        checkpoint.ReleaseFinalization.Set();

        var outcome = await work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal("catalog_schema_violation", fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    private static void AssertSinglePublishedGraph(LocalRepositoryAdmissionFixture fixture)
    {
        foreach (var table in new[]
        {
            "local_repositories",
            "local_repository_locators",
            "local_repository_locator_heads",
            "local_repository_history",
            "session_repository_observations",
            "session_repository_observation_contexts",
            "session_repository_assignment_revisions",
            "session_repository_assignment_history",
        })
            Assert.Equal(1, fixture.ScalarLong($"SELECT COUNT(*) FROM {table};"));
    }

    [Fact]
    public async Task CallerCancellationAtPublicationPropagatesAndRollsBack()
    {
        using var cancellation = new CancellationTokenSource();
        var checkpoint = new DelegatingAdmissionCheckpoint((stage) =>
        {
            if (stage == LocalRepositoryAdmissionCheckpoint.BeforePublication)
                cancellation.Cancel();
        });
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Cancelled")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)],
            cancellation.Token));

        Assert.NotEqual("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task BusyAtAdmissionBeginIsRetryableAndPublishesNoRows()
    {
        using var checkpoint = new WriteLockAdmissionCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint);
        checkpoint.DatabasePath = fixture.DatabasePath;

        var outcome = await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Busy")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Busy, outcome);
        Assert.NotEqual("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task UnexpectedSqliteReadErrorIsRetryableAndDoesNotBecomeSchemaViolation()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/SqliteError")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        fixture.Execute("ALTER TABLE local_repository_locators RENAME TO local_repository_locators_broken;");

        var outcome = await fixture.RunPreparedAsync(prepared);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(string.Empty, fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_locators_broken;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        var exception = Assert.IsType<SqliteException>(fixture.LastProcessorException);
        Assert.Equal(1, exception.SqliteErrorCode);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    [InlineData("cross_table")]
    public async Task GeneratedIdFailureIsRetryableAndDoesNotBecomeDomainIdentityConflict(string scenario)
    {
        const string firstGeneratedId = "01900000-0000-7000-8000-000000010000";
        Func<DateTimeOffset, string>? idFactory = scenario switch
        {
            "invalid" => static _ => "not-a-uuid",
            "duplicate" => static _ => firstGeneratedId,
            "cross_table" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        using var fixture = new LocalRepositoryAdmissionFixture(idFactory: idFactory);
        var sessionId = scenario == "cross_table"
            ? firstGeneratedId
            : LocalRepositoryAdmissionFixture.Session(1);

        var outcome = await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/GeneratedId")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1, sessionId)]);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.Retrying, outcome);
        Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(string.Empty, fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
        var exception = Assert.IsType<LocalRepositoryAdmissionRetryableException>(fixture.LastProcessorException);
        Assert.Equal(scenario switch
        {
            "invalid" => "local_repository_catalog_generated_id_invalid",
            "duplicate" => "local_repository_catalog_generated_id_duplicate",
            "cross_table" => "local_repository_catalog_generated_id_unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        }, exception.Message);
    }

    [Fact]
    public async Task SeparateReaderCannotObserveDomainRowsBeforeQueueCompletionCommits()
    {
        var checkpoint = new ObservingAdmissionCheckpoint();
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint);
        checkpoint.DatabasePath = fixture.DatabasePath;

        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/AtomicVisibility")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal("leased", checkpoint.ObservedQueueState);
        Assert.Equal(0, checkpoint.ObservedDomainRows);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.True(fixture.DomainRowCount() > 0);
    }

    private sealed class ThrowingAdmissionCheckpoint(LocalRepositoryAdmissionCheckpoint target) : ILocalRepositoryAdmissionCheckpoint
    {
        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint == target)
                throw new InvalidOperationException("synthetic_publication_failure");
        }
    }

    private sealed class DelegatingAdmissionCheckpoint(Action<LocalRepositoryAdmissionCheckpoint> action) : ILocalRepositoryAdmissionCheckpoint
    {
        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint) => action(checkpoint);
    }

    private sealed class DelegatingReconciliationCheckpoint(Action<LocalRepositoryReconciliationCheckpoint> action) : ILocalRepositoryReconciliationCheckpoint
    {
        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint) => action(checkpoint);
    }

    private sealed class HoldingPreparationCheckpoint : ILocalRepositoryAdmissionCheckpoint, IDisposable
    {
        internal ManualResetEventSlim Held { get; } = new();
        internal ManualResetEventSlim Release { get; } = new();

        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryAdmissionCheckpoint.AfterPreparationBeforeHandoff)
                return;
            Held.Set();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release repository preparation.");
        }

        public void Dispose()
        {
            Release.Set();
            Held.Dispose();
            Release.Dispose();
        }
    }

    private sealed class TransactionFreePayloadParsingCheckpoint : ILocalRepositoryAdmissionCheckpoint, IDisposable
    {
        internal ManualResetEventSlim BeforeParsingHeld { get; } = new();
        internal ManualResetEventSlim ReleaseParsing { get; } = new();
        internal ManualResetEventSlim AfterPreparationHeld { get; } = new();
        internal ManualResetEventSlim ReleasePreparation { get; } = new();

        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint == LocalRepositoryAdmissionCheckpoint.BeforePayloadParsing)
            {
                BeforeParsingHeld.Set();
                if (!ReleaseParsing.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out waiting to release repository payload parsing.");
            }
            else if (checkpoint == LocalRepositoryAdmissionCheckpoint.AfterPreparationBeforeHandoff)
            {
                AfterPreparationHeld.Set();
                if (!ReleasePreparation.Wait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("Timed out waiting to release parsed repository input.");
            }
        }

        public void Dispose()
        {
            ReleaseParsing.Set();
            ReleasePreparation.Set();
            BeforeParsingHeld.Dispose();
            ReleaseParsing.Dispose();
            AfterPreparationHeld.Dispose();
            ReleasePreparation.Dispose();
        }
    }

    private sealed class FaultingPeriodicHeartbeatCheckpoint :
        ILocalRepositoryAdmissionCheckpoint,
        ILocalRepositoryReconciliationCheckpoint,
        IDisposable
    {
        internal ManualResetEventSlim PreparationHeld { get; } = new();
        internal ManualResetEventSlim ReleasePreparation { get; } = new();
        internal ManualResetEventSlim AppliedThenFaulted { get; } = new();

        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryAdmissionCheckpoint.AfterPreparationBeforeHandoff)
                return;
            PreparationHeld.Set();
            if (!ReleasePreparation.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release repository preparation after heartbeat fault.");
        }

        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryReconciliationCheckpoint.AfterPeriodicHeartbeatApplied)
                return;
            AppliedThenFaulted.Set();
            throw new InvalidOperationException("synthetic_periodic_heartbeat_fault");
        }

        public void Dispose()
        {
            ReleasePreparation.Set();
            PreparationHeld.Dispose();
            ReleasePreparation.Dispose();
            AppliedThenFaulted.Dispose();
        }
    }

    private sealed class PreparationHandoffCheckpoint(
        bool holdAfterRejection = false,
        Action? onHandoffRejected = null) :
        ILocalRepositoryAdmissionCheckpoint,
        ILocalRepositoryReconciliationCheckpoint,
        IDisposable
    {
        private int periodicHeartbeatCount;
        private int periodicHeartbeatRejectedCount;
        internal ManualResetEventSlim PreparationHeld { get; } = new();
        internal ManualResetEventSlim ReleasePreparation { get; } = new();
        internal ManualResetEventSlim BeforeHandoffHeld { get; } = new();
        internal ManualResetEventSlim ReleaseHandoff { get; } = new();
        internal ManualResetEventSlim AfterHandoffHeld { get; } = new();
        internal ManualResetEventSlim ReleaseFinalization { get; } = new();
        internal ManualResetEventSlim HandoffRejected { get; } = new();
        internal ManualResetEventSlim ReleaseRejection { get; } = new();
        internal ManualResetEventSlim HeartbeatBusy { get; } = new();
        internal int PeriodicHeartbeatCount => Volatile.Read(ref periodicHeartbeatCount);
        internal int PeriodicHeartbeatRejectedCount => Volatile.Read(ref periodicHeartbeatRejectedCount);

        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryAdmissionCheckpoint.AfterPreparationBeforeHandoff)
                return;
            PreparationHeld.Set();
            if (!ReleasePreparation.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release prepared repository input.");
        }

        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            switch (checkpoint)
            {
                case LocalRepositoryReconciliationCheckpoint.AfterPeriodicHeartbeatApplied:
                    Interlocked.Increment(ref periodicHeartbeatCount);
                    break;
                case LocalRepositoryReconciliationCheckpoint.AfterPeriodicHeartbeatRejected:
                    Interlocked.Increment(ref periodicHeartbeatRejectedCount);
                    break;
                case LocalRepositoryReconciliationCheckpoint.BeforeHandoffHeartbeat:
                    BeforeHandoffHeld.Set();
                    if (!ReleaseHandoff.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Timed out waiting to release repository handoff.");
                    break;
                case LocalRepositoryReconciliationCheckpoint.AfterHandoffHeartbeat:
                    AfterHandoffHeld.Set();
                    if (!ReleaseFinalization.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Timed out waiting to release repository finalization.");
                    break;
                case LocalRepositoryReconciliationCheckpoint.AfterHandoffRejected when holdAfterRejection:
                    onHandoffRejected?.Invoke();
                    HandoffRejected.Set();
                    if (!ReleaseRejection.Wait(TimeSpan.FromSeconds(10)))
                        throw new TimeoutException("Timed out waiting to release rejected repository handoff.");
                    break;
                case LocalRepositoryReconciliationCheckpoint.AfterHeartbeatBusy:
                    HeartbeatBusy.Set();
                    break;
            }
        }

        public void Dispose()
        {
            ReleasePreparation.Set();
            ReleaseHandoff.Set();
            ReleaseFinalization.Set();
            ReleaseRejection.Set();
            PreparationHeld.Dispose();
            ReleasePreparation.Dispose();
            BeforeHandoffHeld.Dispose();
            ReleaseHandoff.Dispose();
            AfterHandoffHeld.Dispose();
            ReleaseFinalization.Dispose();
            HandoffRejected.Dispose();
            ReleaseRejection.Dispose();
            HeartbeatBusy.Dispose();
        }
    }

    private sealed class HeartbeatOutcomeCheckpoint : ILocalRepositoryReconciliationCheckpoint, IDisposable
    {
        private SqliteConnection? connection;
        private SqliteTransaction? transaction;
        internal ManualResetEventSlim Busy { get; } = new();
        internal ManualResetEventSlim Applied { get; } = new();
        internal ManualResetEventSlim Expired { get; } = new();
        internal string? DatabasePath { get; set; }

        internal void HoldWriterLock()
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath ?? throw new InvalidOperationException("Database path was not configured."),
                Pooling = false,
            }.ToString());
            connection.Open();
            transaction = connection.BeginTransaction(deferred: false);
        }

        internal void ReleaseWriterLock()
        {
            transaction?.Dispose();
            connection?.Dispose();
            transaction = null;
            connection = null;
        }

        public void Reached(LocalRepositoryReconciliationCheckpoint checkpoint)
        {
            if (checkpoint == LocalRepositoryReconciliationCheckpoint.AfterHeartbeatBusy)
                Busy.Set();
            else if (checkpoint == LocalRepositoryReconciliationCheckpoint.AfterPeriodicHeartbeatApplied)
                Applied.Set();
            else if (checkpoint == LocalRepositoryReconciliationCheckpoint.HeartbeatLeaseExpired)
                Expired.Set();
        }

        public void Dispose()
        {
            ReleaseWriterLock();
            Busy.Dispose();
            Applied.Dispose();
            Expired.Dispose();
        }
    }

    private sealed class FinalizationExpiringTimeProvider : TimeProvider
    {
        private int readCount;

        public override DateTimeOffset GetUtcNow() =>
            Interlocked.Increment(ref readCount) == 1
                ? DateTimeOffset.UnixEpoch
                : DateTimeOffset.UnixEpoch.AddMinutes(2).AddSeconds(1);
    }

    private sealed class WriteLockAdmissionCheckpoint : ILocalRepositoryAdmissionCheckpoint, IDisposable
    {
        private SqliteConnection? connection;
        private SqliteTransaction? transaction;

        internal string? DatabasePath { get; set; }

        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryAdmissionCheckpoint.BeforeTransaction || transaction is not null)
                return;
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath ?? throw new InvalidOperationException("Database path was not configured."),
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            transaction = connection.BeginTransaction(deferred: false);
        }

        public void Dispose()
        {
            transaction?.Dispose();
            connection?.Dispose();
        }
    }

    private sealed class ObservingAdmissionCheckpoint : ILocalRepositoryAdmissionCheckpoint
    {
        internal string? DatabasePath { get; set; }
        internal string? ObservedQueueState { get; private set; }
        internal long ObservedDomainRows { get; private set; }

        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryAdmissionCheckpoint.BeforeQueueCompletion)
                return;
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath ?? throw new InvalidOperationException("Database path was not configured."),
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            using var queue = connection.CreateCommand();
            queue.CommandText = "SELECT state FROM local_repository_reconciliation_queue;";
            ObservedQueueState = Assert.IsType<string>(queue.ExecuteScalar());
            using var domain = connection.CreateCommand();
            domain.CommandText = """
                SELECT (SELECT COUNT(*) FROM local_repositories)
                     + (SELECT COUNT(*) FROM local_repository_locators)
                     + (SELECT COUNT(*) FROM local_repository_history)
                     + (SELECT COUNT(*) FROM session_repository_observations)
                     + (SELECT COUNT(*) FROM session_repository_observation_contexts)
                     + (SELECT COUNT(*) FROM session_repository_assignment_history);
                """;
            ObservedDomainRows = Assert.IsType<long>(domain.ExecuteScalar());
        }
    }
}

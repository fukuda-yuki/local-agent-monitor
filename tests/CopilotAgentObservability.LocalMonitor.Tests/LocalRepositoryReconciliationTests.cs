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
    public async Task FinalRetentionAndQueueFenceExpiryRollsBackThePublishedGraph()
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
            Assert.IsAssignableFrom<OperationCanceledException>(fixture.LastProcessorException);
        }
    }

    [Fact]
    public async Task HeartbeatFenceLossDuringPublicationCancelsAndReturnsPendingWithoutRows()
    {
        LocalRepositoryAdmissionFixture? fixture = null;
        var checkpoint = new DelegatingAdmissionCheckpoint((stage) =>
        {
            if (stage != LocalRepositoryAdmissionCheckpoint.AfterContexts)
                return;
            fixture!.Clock.Advance(TimeSpan.FromSeconds(10));
            Thread.Sleep(100);
        });
        using (fixture = new LocalRepositoryAdmissionFixture(checkpoint))
        {
            var outcome = await fixture.RunAsync(
                LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                    LocalRepositoryAdmissionFixture.Trace(1),
                    LocalRepositoryAdmissionFixture.Span(1),
                    "https://github.com/Example/HeartbeatLoss")),
                [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

            Assert.Contains(outcome, new[]
            {
                LocalRepositoryReconciliationWorkOutcome.Retrying,
                LocalRepositoryReconciliationWorkOutcome.StaleOwner,
            });
            Assert.Equal("pending", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
            Assert.Equal(0, fixture.DomainRowCount());
        }
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

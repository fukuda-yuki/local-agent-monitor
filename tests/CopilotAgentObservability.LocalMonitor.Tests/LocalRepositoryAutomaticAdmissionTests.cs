using System.Globalization;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryAutomaticAdmissionTests
{
    [Fact]
    public async Task SingleMemberPublicationFencePublishesRows()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/Widget.git"));

        var outcome = await fixture.RunAsync(
            payload,
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Fact]
    public async Task MissingLocator_CreatesOneObservedOwnerAndAutomaticAssignmentAtomically()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Widget.git"));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_locators;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_locator_heads;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history WHERE action='create_observed';"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
        Assert.Equal("Widget", fixture.ScalarText("SELECT display_name FROM local_repositories;"));
        Assert.Equal("github.com/example/widget", fixture.ScalarText("SELECT canonical_locator FROM local_repository_locators;"));
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        Assert.Equal(
            LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
                new LocalRepositoryAssignmentState("unassigned", "none", null, [])),
            fixture.ScalarText("SELECT previous_assignment_state_sha256 FROM session_repository_assignment_history;"));
        Assert.Equal(
            LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
                new LocalRepositoryAssignmentState("assigned", "automatic", repositoryId, [repositoryId])),
            fixture.ScalarText("SELECT new_assignment_state_sha256 FROM session_repository_assignment_history;"));
        Assert.Equal(
            fixture.ScalarText("SELECT reconciliation_fingerprint FROM local_repository_reconciliation_queue;"),
            fixture.ScalarText("SELECT reconciliation_fingerprint FROM session_repository_assignment_history;"));
        Assert.Equal(
            fixture.ScalarText("SELECT context_identity_sha256 FROM session_repository_observation_contexts;"),
            fixture.ScalarText("SELECT context_identity_sha256 FROM local_repository_history;"));
        Assert.All(
            fixture.QueryStrings("""
                SELECT repository_id FROM local_repositories
                UNION ALL SELECT locator_id FROM local_repository_locators
                UNION ALL SELECT history_id FROM local_repository_history
                UNION ALL SELECT observation_id FROM session_repository_observations
                UNION ALL SELECT context_id FROM session_repository_observation_contexts
                UNION ALL SELECT history_id FROM session_repository_assignment_history;
                """),
            value => Assert.True(LocalRepositoryCatalogValidation.IsCanonicalUuidV7(value), value));
    }

    [Fact]
    public async Task RepeatedLocatorOccurrences_CreateOneOwnerAndRetainEveryExactContext()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "git@github.com:Example/Widget.git"),
            new(LocalRepositoryAdmissionFixture.Trace(2), LocalRepositoryAdmissionFixture.Span(2), "https://GITHUB.COM/example/widget"));

        await fixture.RunAsync(payload, [
            LocalRepositoryAdmissionFixture.MatchedEvent(2, LocalRepositoryAdmissionFixture.Session(99)),
            LocalRepositoryAdmissionFixture.MatchedEvent(1, LocalRepositoryAdmissionFixture.Session(99))]);

        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_locators;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Equal("Widget", fixture.ScalarText("SELECT display_name FROM local_repositories;"));
        Assert.Equal("Example", fixture.ScalarText("SELECT display_owner FROM local_repository_locators;"));
        Assert.Equal(LocalRepositoryAdmissionFixture.Trace(1), fixture.ScalarText("""
            SELECT c.trace_id
            FROM local_repository_history h
            JOIN session_repository_observation_contexts c
              ON c.context_identity_sha256=h.context_identity_sha256;
            """));
        Assert.Equal("assigned", fixture.ScalarText("SELECT new_state FROM session_repository_assignment_history ORDER BY new_revision DESC LIMIT 1;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
    }

    [Fact]
    public async Task DistinctMissingLocators_ArePublishedTogetherAndProduceOneConflictRevision()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var sessionId = LocalRepositoryAdmissionFixture.Session(77);
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Alpha"),
            new(LocalRepositoryAdmissionFixture.Trace(2), LocalRepositoryAdmissionFixture.Span(2), "https://github.com/Example/Beta"));

        await fixture.RunAsync(payload, [
            LocalRepositoryAdmissionFixture.MatchedEvent(1, sessionId),
            LocalRepositoryAdmissionFixture.MatchedEvent(2, sessionId)]);

        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Equal("conflict", fixture.ScalarText("SELECT new_state FROM session_repository_assignment_history;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
    }

    [Fact]
    public async Task ExistingManualLocatorOwner_IsReusedWithoutChangingItsOriginOrRepositoryHistory()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var owner = fixture.SeedManualOwner("https://github.com/Manual/OwnerCase");
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "git@github.com:manual/ownercase.git"));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal(owner.RepositoryId, fixture.ScalarText("SELECT repository_id FROM local_repositories;"));
        Assert.Equal("manual", fixture.ScalarText("SELECT source FROM local_repository_locators;"));
        Assert.Equal("ManualOwner", fixture.ScalarText("SELECT display_name FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
    }

    [Fact]
    public async Task ExistingObservedOwner_IsReusedWithoutRenameRevisionOrDuplicateCreationHistory()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/FirstCase")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");

        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(2), LocalRepositoryAdmissionFixture.Span(2), "https://github.com/example/FIRSTCASE")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(2)]);

        Assert.Equal(repositoryId, fixture.ScalarText("SELECT repository_id FROM local_repositories;"));
        Assert.Equal("FirstCase", fixture.ScalarText("SELECT display_name FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT revision FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
        Assert.Null(fixture.LastProcessorException);
        Assert.Equal(["completed", "completed"], fixture.QueryStrings("SELECT state FROM local_repository_reconciliation_queue ORDER BY raw_record_id;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Fact]
    public async Task ResourceOccurrence_WithTwoSpanContexts_UsesOnePhysicalObservation()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.ResourcePayload(
            "https://github.com/Example/ResourceRepo",
            (LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1)),
            (LocalRepositoryAdmissionFixture.Trace(2), LocalRepositoryAdmissionFixture.Span(2)));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1), LocalRepositoryAdmissionFixture.MatchedEvent(2)]);

        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(DISTINCT observation_id) FROM session_repository_observation_contexts;"));
        Assert.Equal("resource", fixture.ScalarText("SELECT scope_kind FROM session_repository_observations;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations WHERE scope_span_ordinal IS NULL AND span_ordinal IS NULL;"));
    }

    [Fact]
    public async Task ResourceOccurrence_WithoutASpanContext_CompletesWithoutDomainRows()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();

        await fixture.RunAsync(LocalRepositoryAdmissionFixture.ResourcePayload("https://github.com/Example/NoSpan"), []);

        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.DomainRowCount());
    }

    [Fact]
    public async Task InvalidAndShadowedCandidates_PreserveEvidenceWithoutCreatingOwners()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.ResourceAndSpanPayload(
            "https://github.com/Example/Shadowed",
            LocalRepositoryAdmissionFixture.Trace(1),
            LocalRepositoryAdmissionFixture.Span(1),
            "not-a-github-locator");

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(new[] { "invalid_locator", "shadowed" }, fixture.QueryStrings("SELECT admission_state FROM session_repository_observation_contexts ORDER BY admission_state;"));
    }

    [Fact]
    public async Task DuplicateAndInvalidTypeCandidates_PreserveEveryContextWithoutCreatingOwners()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.DuplicateAndInvalidTypePayload(
            LocalRepositoryAdmissionFixture.Trace(1),
            LocalRepositoryAdmissionFixture.Span(1));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);

        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(3, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(new[] { "duplicate_key", "duplicate_key", "invalid_type" },
            fixture.QueryStrings("SELECT admission_state FROM session_repository_observation_contexts ORDER BY admission_state;"));
    }

    [Fact]
    public async Task IdenticalReplay_ReusesImmutableRowsAndCreatesNoAdditionalHistory()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Replay"));

        await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        var immutableIds = fixture.QueryStrings("""
            SELECT kind || ':' || id
            FROM (
                SELECT 'repository' AS kind,repository_id AS id FROM local_repositories
                UNION ALL SELECT 'locator',locator_id FROM local_repository_locators
                UNION ALL SELECT 'repository_history',history_id FROM local_repository_history
                UNION ALL SELECT 'observation',observation_id FROM session_repository_observations
                UNION ALL SELECT 'context',context_id FROM session_repository_observation_contexts
                UNION ALL SELECT 'assignment_history',history_id FROM session_repository_assignment_history)
            ORDER BY kind COLLATE BINARY,id COLLATE BINARY;
            """);
        fixture.ResetQueueToPending();
        var replayOutcome = await fixture.RunExistingAsync();

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, replayOutcome);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(immutableIds, fixture.QueryStrings("""
            SELECT kind || ':' || id
            FROM (
                SELECT 'repository' AS kind,repository_id AS id FROM local_repositories
                UNION ALL SELECT 'locator',locator_id FROM local_repository_locators
                UNION ALL SELECT 'repository_history',history_id FROM local_repository_history
                UNION ALL SELECT 'observation',observation_id FROM session_repository_observations
                UNION ALL SELECT 'context',context_id FROM session_repository_observation_contexts
                UNION ALL SELECT 'assignment_history',history_id FROM session_repository_assignment_history)
            ORDER BY kind COLLATE BINARY,id COLLATE BINARY;
            """));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_history;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        Assert.Null(fixture.LastProcessorException);
    }

    [Fact]
    public async Task ExistingSourceIdentityWithDifferentSemantics_IsTerminalAndPublishesNoProspectiveGraph()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(1), LocalRepositoryAdmissionFixture.Span(1), "https://github.com/Example/Conflict"));
        var prepared = fixture.Prepare(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
            LocalRepositorySourceIdentityInput.Span(prepared.RawRecordId, 0, 0, 0, 0, "vcs.repository.url.full"));
        fixture.Execute($"""
            INSERT INTO session_repository_observations(
                observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,
                resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,
                scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,
                locator_sha256,display_owner,display_repository,source_surface,
                source_application_version,observed_at)
            VALUES(
                '01900000-0000-7000-8000-00000000ff01','{sourceIdentity}',{prepared.RawRecordId},'{prepared.Digest}',
                0,0,0,0,'span','vcs.repository.url.full','invalid_type',NULL,NULL,NULL,NULL,NULL,
                'github-copilot-cli',NULL,'{LocalRepositoryAdmissionFixture.ObservedAt}');
            """);

        await fixture.RunPreparedAsync(prepared);

        Assert.Equal("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal("catalog_identity_conflict", fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingContextIdentityOrObservationEventCollision_IsTerminalWithoutProspectivePublication(
        bool reuseExpectedContextIdentity)
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(
            new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/ContextCollision"));
        var matchedEvent = LocalRepositoryAdmissionFixture.MatchedEvent(1);
        var prepared = fixture.Prepare(payload, [matchedEvent]);
        fixture.SeedContextCollision(prepared, matchedEvent, reuseExpectedContextIdentity);

        await fixture.RunPreparedAsync(prepared);

        Assert.Equal("failed_terminal", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal("catalog_identity_conflict", fixture.ScalarText("SELECT terminal_reason FROM local_repository_reconciliation_queue;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observations;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Fact]
    public async Task ManualOverride_RemainsAuthoritativeWhileNewAutomaticEvidenceIsRetained()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var sessionId = LocalRepositoryAdmissionFixture.Session(55);
        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(1),
                LocalRepositoryAdmissionFixture.Span(1),
                "https://github.com/Example/ManualShield")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1, sessionId)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        fixture.SeedManualAssignment(sessionId, repositoryId);

        await fixture.RunAsync(
            LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
                LocalRepositoryAdmissionFixture.Trace(2),
                LocalRepositoryAdmissionFixture.Span(2),
                "https://github.com/Example/AdditionalEvidence")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(2, sessionId)]);

        Assert.Equal("assigned", fixture.ScalarText("SELECT state FROM session_repository_manual_overrides;"));
        Assert.Equal(repositoryId, fixture.ScalarText("SELECT repository_id FROM session_repository_manual_overrides;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
    }

    [Theory]
    [InlineData("expiring")]
    [InlineData("retained_by_policy")]
    [InlineData("expired_pending_deletion")]
    [InlineData("deletion_queued")]
    public async Task AdmittedOperationGrant_RemainsAuthoritativeAcrossCurrentRetentionLifecycleDrift(
        string lifecycle)
    {
        var checkpoint = new AdmittedRetentionLifecycleCheckpoint(lifecycle);
        using var fixture = new LocalRepositoryAdmissionFixture(checkpoint);
        fixture.Clock.Advance(
            DateTimeOffset.ParseExact(
                LocalRepositoryAdmissionFixture.ObservedAt,
                "O",
                CultureInfo.InvariantCulture) - fixture.Clock.GetUtcNow());
        var prepared = fixture.Prepare(
            LocalRepositoryAdmissionFixture.SpanPayload(
                new LocalRepositoryAdmissionFixture.SpanInput(
                    LocalRepositoryAdmissionFixture.Trace(1),
                    LocalRepositoryAdmissionFixture.Span(1),
                    "https://github.com/Example/AdmittedGrant")),
            [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        var sentinelRawRecordId = fixture.InsertRawForRetentionSentinel("{\"resourceSpans\":[]}");
        checkpoint.Configure(fixture.DatabasePath, prepared.RawRecordId, sentinelRawRecordId);

        var outcome = await fixture.RunPreparedAsync(prepared);

        Assert.Equal(LocalRepositoryReconciliationWorkOutcome.ProcessorInvoked, outcome);
        Assert.Equal(1, checkpoint.Count);
        Assert.True(checkpoint.AdmittedTupleWasLiveAfterMutation);
        Assert.Equal("completed", fixture.ScalarText("SELECT state FROM local_repository_reconciliation_queue;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        Assert.Null(fixture.LastProcessorException);
        Assert.Equal(
            lifecycle,
            fixture.ScalarText($"SELECT state FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{prepared.RawRecordId}';"));
        Assert.Equal(
            checkpoint.AdmissionRevision + 1,
            fixture.ScalarLong($"SELECT revision FROM retention_items WHERE store_kind='raw_record' AND source_item_id='{prepared.RawRecordId}';"));
        Assert.Equal(0, checkpoint.ExactTargetOperationLeaseCount());
        Assert.Equal(2, checkpoint.ExactSentinelLeaseCount());
    }

    private sealed class AdmittedRetentionLifecycleCheckpoint(string lifecycle) : ILocalRepositoryAdmissionCheckpoint
    {
        private const string Timestamp = LocalRepositoryAdmissionFixture.ObservedAt;
        private string? databasePath;
        private long rawRecordId;
        private long sentinelRawRecordId;
        private string itemId = string.Empty;
        private string unrelatedItemId = string.Empty;
        private string leaseOwner = string.Empty;
        private string leaseExpiry = string.Empty;
        private long leaseGeneration;

        internal int Count { get; private set; }
        internal long AdmissionRevision { get; private set; }
        internal bool AdmittedTupleWasLiveAfterMutation { get; private set; }

        internal void Configure(
            string configuredDatabasePath,
            long configuredRawRecordId,
            long configuredSentinelRawRecordId)
        {
            databasePath = configuredDatabasePath;
            rawRecordId = configuredRawRecordId;
            sentinelRawRecordId = configuredSentinelRawRecordId;
        }

        public void Reached(LocalRepositoryAdmissionCheckpoint checkpoint)
        {
            if (checkpoint != LocalRepositoryAdmissionCheckpoint.BeforeTransaction)
                return;
            if (++Count != 1)
                throw new InvalidOperationException("automatic admission transaction checkpoint repeated");

            using var connection = Open();
            using var transaction = connection.BeginTransaction(deferred: false);
            using (var admitted = connection.CreateCommand())
            {
                admitted.Transaction = transaction;
                admitted.CommandText = """
                    SELECT item.item_id,item.revision,hex(raw.retention_owner_token),
                           lease.owner,lease.generation,lease.expires_at
                    FROM retention_items AS item
                    JOIN raw_records AS raw
                      ON raw.id=CAST(item.source_item_id AS INTEGER)
                    JOIN retention_leases AS lease
                      ON lease.item_id=item.item_id AND lease.lease_kind='operation'
                    WHERE item.store_kind='raw_record' AND item.source_item_id=$raw_record_id;
                    """;
                admitted.Parameters.AddWithValue("$raw_record_id", rawRecordId.ToString(CultureInfo.InvariantCulture));
                using var reader = admitted.ExecuteReader();
                if (!reader.Read())
                    throw new InvalidOperationException("admitted operation tuple was not present at the transaction checkpoint");
                itemId = reader.GetString(0);
                AdmissionRevision = reader.GetInt64(1);
                var sourceToken = reader.GetString(2);
                leaseOwner = reader.GetString(3);
                leaseGeneration = reader.GetInt64(4);
                leaseExpiry = reader.GetString(5);
                if (reader.Read())
                    throw new InvalidOperationException("admitted operation tuple was not unique");
                reader.Close();

                MutateCurrentLifecycle(connection, transaction);
                SeedLeaseSentinels(connection, transaction);
                AdmittedTupleWasLiveAfterMutation = ExactAdmittedTupleCount(
                    connection,
                    transaction,
                    sourceToken) == 1;
            }
            transaction.Commit();
        }

        internal long ExactTargetOperationLeaseCount() => ExactLeaseCount(itemId, "operation");

        internal long ExactSentinelLeaseCount() =>
            ExactLeaseCount(itemId, "access") + ExactLeaseCount(unrelatedItemId, "operation");

        private void MutateCurrentLifecycle(SqliteConnection connection, SqliteTransaction transaction)
        {
            var denied = lifecycle is "expired_pending_deletion" or "deletion_queued";
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE retention_items
                SET state=$state,
                    revision=revision+1,
                    read_denied_at=CASE WHEN $denied=1 THEN $at ELSE NULL END,
                    queued_at=CASE WHEN $denied=1 THEN $at ELSE NULL END
                WHERE item_id=$item_id AND revision=$revision;
                """;
            command.Parameters.AddWithValue("$state", lifecycle);
            command.Parameters.AddWithValue("$denied", denied ? 1 : 0);
            command.Parameters.AddWithValue("$at", Timestamp);
            command.Parameters.AddWithValue("$item_id", itemId);
            command.Parameters.AddWithValue("$revision", AdmissionRevision);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("current Retention lifecycle mutation did not apply");
        }

        private void SeedLeaseSentinels(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = """
                SELECT item_id
                FROM retention_items
                WHERE store_kind='raw_record' AND source_item_id=$source_item_id;
                """;
            item.Parameters.AddWithValue("$source_item_id", sentinelRawRecordId.ToString(CultureInfo.InvariantCulture));
            unrelatedItemId = Convert.ToString(item.ExecuteScalar(), CultureInfo.InvariantCulture)
                ?? throw new InvalidOperationException("Retention sentinel item was not created");

            using var leases = connection.CreateCommand();
            leases.Transaction = transaction;
            leases.CommandText = """
                INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation)
                VALUES
                    ($item_id,'access',$owner,$expiry,$generation),
                    ($sentinel_item_id,'operation',$owner,$expiry,$generation);
                """;
            leases.Parameters.AddWithValue("$item_id", itemId);
            leases.Parameters.AddWithValue("$sentinel_item_id", unrelatedItemId);
            leases.Parameters.AddWithValue("$owner", leaseOwner);
            leases.Parameters.AddWithValue("$expiry", leaseExpiry);
            leases.Parameters.AddWithValue("$generation", leaseGeneration);
            if (leases.ExecuteNonQuery() != 2)
                throw new InvalidOperationException("Retention lease sentinels were not created");
        }

        private long ExactAdmittedTupleCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sourceToken)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COUNT(*)
                FROM retention_items AS item
                JOIN raw_records AS raw
                  ON raw.id=CAST(item.source_item_id AS INTEGER)
                 AND hex(raw.retention_owner_token)=$source_token
                JOIN retention_leases AS lease
                  ON lease.item_id=item.item_id
                 AND lease.lease_kind='operation'
                 AND lease.owner=$owner
                 AND lease.generation=$generation
                 AND lease.expires_at=$expiry
                 AND lease.expires_at>$at
                WHERE item.item_id=$item_id;
                """;
            command.Parameters.AddWithValue("$source_token", sourceToken);
            command.Parameters.AddWithValue("$owner", leaseOwner);
            command.Parameters.AddWithValue("$generation", leaseGeneration);
            command.Parameters.AddWithValue("$expiry", leaseExpiry);
            command.Parameters.AddWithValue("$at", Timestamp);
            command.Parameters.AddWithValue("$item_id", itemId);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private long ExactLeaseCount(string exactItemId, string leaseKind)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*)
                FROM retention_leases
                WHERE item_id=$item_id AND lease_kind=$lease_kind AND owner=$owner
                  AND generation=$generation AND expires_at=$expiry;
                """;
            command.Parameters.AddWithValue("$item_id", exactItemId);
            command.Parameters.AddWithValue("$lease_kind", leaseKind);
            command.Parameters.AddWithValue("$owner", leaseOwner);
            command.Parameters.AddWithValue("$generation", leaseGeneration);
            command.Parameters.AddWithValue("$expiry", leaseExpiry);
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private SqliteConnection Open()
        {
            var path = databasePath ?? throw new InvalidOperationException("automatic admission checkpoint was not configured");
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON;";
            command.ExecuteNonQuery();
            return connection;
        }
    }
}

internal sealed class LocalRepositoryAdmissionFixture : IDisposable
{
    internal const string ObservedAt = "2026-08-01T01:02:03.1234567+00:00";
    private readonly MonitorTempDirectory temp = new();
    private readonly RawTelemetryStore rawStore;
    private readonly SqliteLocalRepositoryReconciliationStore queue;
    private readonly SequentialUuidV7Factory ids = new();
    private readonly ILocalRepositoryAdmissionCheckpoint? checkpoint;
    private readonly ILocalRepositoryReconciliationCheckpoint? reconciliationCheckpoint;
    private readonly TimeProvider processorTimeProvider;
    private readonly Func<DateTimeOffset, string> idFactory;
    private PreparedInput? current;

    internal LocalRepositoryAdmissionFixture(
        ILocalRepositoryAdmissionCheckpoint? checkpoint = null,
        ILocalRepositoryReconciliationCheckpoint? reconciliationCheckpoint = null,
        TimeProvider? processorTimeProvider = null,
        Func<DateTimeOffset, string>? idFactory = null)
    {
        this.checkpoint = checkpoint;
        this.reconciliationCheckpoint = reconciliationCheckpoint;
        rawStore = temp.CreateRawStore();
        rawStore.CreateMonitorSchema();
        new SqliteSourceCompatibilityStore(temp.DatabasePath).CreateSchema();
        new SqliteSessionStore(temp.DatabasePath).CreateSchema();
        using var connection = Open();
        LocalRepositoryCatalogSchemaV1.Ensure(connection);
        using var coverage = connection.CreateCommand();
        coverage.CommandText = """
            INSERT INTO retention_adapter_coverage(store_kind,coverage_version)
            VALUES
                ('session_event_content',1),
                ('raw_record',1),
                ('analysis_run_raw',1),
                ('sensitive_bundle',1),
                ('analysis_sdk_directory',1);
            """;
        coverage.ExecuteNonQuery();
        queue = new SqliteLocalRepositoryReconciliationStore(
            temp.DatabasePath,
            temp.TimeProvider,
            static () => new string('a', 64),
            reconciliationCheckpoint);
        this.processorTimeProvider = processorTimeProvider ?? temp.TimeProvider;
        this.idFactory = idFactory ?? ids.Next;
    }

    internal string DatabasePath => temp.DatabasePath;
    internal MutableTimeProvider Clock => Assert.IsType<MutableTimeProvider>(temp.TimeProvider);
    internal Exception? LastProcessorException { get; private set; }

    internal async Task<LocalRepositoryReconciliationWorkOutcome> RunAsync(
        string payload,
        IReadOnlyCollection<EventInput> events,
        CancellationToken cancellationToken = default)
    {
        var prepared = Prepare(payload, events);
        return await RunPreparedAsync(prepared, cancellationToken);
    }

    internal PreparedInput Prepare(
        string payload,
        IReadOnlyCollection<EventInput> events,
        bool contradictoryProvenance = false)
    {
        long rawRecordId;
        try
        {
            rawRecordId = InsertRaw(payload);
        }
        catch (JsonException)
        {
            rawRecordId = InsertRaw("{}");
            Execute($"UPDATE raw_records SET payload_json='{{' WHERE id={rawRecordId};");
        }
        var digest = SkillProjectionHashing.InputDigest(payload);
        using var connection = Open();
        var provenanceDigest = contradictoryProvenance
            ? (digest[0] == 'f' ? new string('e', 64) : new string('f', 64))
            : digest;
        InsertProvenance(connection, rawRecordId, provenanceDigest);
        foreach (var item in events)
            InsertEvent(connection, item);
        var queueId = Queue(rawRecordId);
        InsertQueue(connection, queueId, rawRecordId, digest);
        current = new(queueId, rawRecordId, digest, payload);
        return current;
    }

    private long InsertRaw(string payload) => rawStore.Insert(new RawTelemetryRecord(
        null,
        RawTelemetrySources.RawOtlp,
        null,
        DateTimeOffset.ParseExact(ObservedAt, "O", CultureInfo.InvariantCulture),
        null,
        payload));

    internal long InsertRawForRetentionSentinel(string payload) => InsertRaw(payload);

    internal async Task<LocalRepositoryReconciliationWorkOutcome> RunPreparedAsync(
        PreparedInput prepared,
        CancellationToken cancellationToken = default)
    {
        current = prepared;
        var processor = CreateProcessor();
        var worker = new LocalRepositoryReconciliationWorker(
            queue,
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            processor,
            temp.TimeProvider,
            reconciliationCheckpoint);
        return await worker.RunOnceAsync(cancellationToken);
    }

    internal async Task<LocalRepositoryReconciliationWorkOutcome> RunWithProcessorPayloadAsync(
        PreparedInput prepared,
        string payloadJson)
    {
        current = prepared;
        var worker = new LocalRepositoryReconciliationWorker(
            queue,
            new LocalRepositoryRawAvailabilityReader(rawStore, temp.RetentionContext),
            new PayloadSubstitutingProcessor(CreateProcessor(), payloadJson),
            temp.TimeProvider,
            reconciliationCheckpoint);
        return await worker.RunOnceAsync(CancellationToken.None);
    }

    private ILocalRepositoryRawRecordProcessor CreateProcessor() =>
        new CapturingProcessor(new SqliteLocalRepositoryCatalogStore(
            temp.DatabasePath,
            queue,
            new LocalRepositoryAssignmentResolver(idFactory),
            processorTimeProvider,
            idFactory,
            checkpoint), exception => LastProcessorException = exception);

    internal Task<LocalRepositoryReconciliationWorkOutcome> RunExistingAsync()
    {
        if (current is null)
            throw new InvalidOperationException("No prepared input exists.");
        return RunPreparedAsync(current);
    }

    internal void ResetQueueToPending() => Execute($"""
        UPDATE local_repository_reconciliation_queue
        SET state='pending',lease_token=NULL,lease_expires_at=NULL,terminal_reason=NULL,
            updated_at='{Clock.GetUtcNow():O}'
        WHERE queue_id='{current?.QueueId ?? throw new InvalidOperationException("No prepared input exists.")}';
        """);

    internal long DomainRowCount() => ScalarLong("""
        SELECT (SELECT COUNT(*) FROM local_repositories)
             + (SELECT COUNT(*) FROM local_repository_locators)
             + (SELECT COUNT(*) FROM local_repository_locator_heads)
             + (SELECT COUNT(*) FROM local_repository_history)
             + (SELECT COUNT(*) FROM session_repository_observations)
             + (SELECT COUNT(*) FROM session_repository_observation_contexts)
             + (SELECT COUNT(*) FROM session_repository_assignment_revisions)
             + (SELECT COUNT(*) FROM session_repository_assignment_history);
        """);

    internal long ScalarLong(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal string ScalarText(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    internal string[] QueryStrings(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
            values.Add(reader.GetString(0));
        return values.ToArray();
    }

    internal void Execute(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static EventInput MatchedEvent(int ordinal, string? sessionId = null) => new(
        Trace(ordinal),
        Span(ordinal),
        sessionId ?? Session(ordinal),
        Event(ordinal),
        EventDisposition.Matched);

    internal static EventInput MissingEvent(int ordinal, string? sessionId = null) => MatchedEvent(ordinal, sessionId) with { Disposition = EventDisposition.Missing };
    internal static EventInput ConflictingEvent(int ordinal, string? sessionId = null) => MatchedEvent(ordinal, sessionId) with { Disposition = EventDisposition.Conflicting };

    internal static string Trace(int ordinal) => ordinal.ToString("x32", CultureInfo.InvariantCulture);
    internal static string Span(int ordinal) => ordinal.ToString("x16", CultureInfo.InvariantCulture);
    internal static string Session(int ordinal) => $"01900000-0000-7000-8000-{ordinal:x12}";
    internal static string Event(int ordinal) => $"01900000-0000-7000-9000-{ordinal:x12}";

    internal static string SpanPayload(params SpanInput[] spans)
    {
        var items = string.Join(",", spans.Select(item =>
            $"{{\"traceId\":\"{item.TraceId}\",\"spanId\":\"{item.SpanId}\",\"attributes\":[{{\"key\":\"vcs.repository.url.full\",\"value\":{{\"stringValue\":\"{item.Locator}\"}}}}]}}"));
        return $"{{\"resourceSpans\":[{{\"scopeSpans\":[{{\"spans\":[{items}]}}]}}]}}";
    }

    internal static string ResourcePayload(string locator, params (string TraceId, string SpanId)[] spans)
    {
        var items = string.Join(",", spans.Select(item => $"{{\"traceId\":\"{item.TraceId}\",\"spanId\":\"{item.SpanId}\"}}"));
        return $"{{\"resourceSpans\":[{{\"resource\":{{\"attributes\":[{{\"key\":\"vcs.repository.url.full\",\"value\":{{\"stringValue\":\"{locator}\"}}}}]}},\"scopeSpans\":[{{\"spans\":[{items}]}}]}}]}}";
    }

    internal static string ResourceAndSpanPayload(string resourceLocator, string traceId, string spanId, string spanLocator) =>
        $"{{\"resourceSpans\":[{{\"resource\":{{\"attributes\":[{{\"key\":\"vcs.repository.url.full\",\"value\":{{\"stringValue\":\"{resourceLocator}\"}}}}]}},\"scopeSpans\":[{{\"spans\":[{{\"traceId\":\"{traceId}\",\"spanId\":\"{spanId}\",\"attributes\":[{{\"key\":\"vcs.repository.url.full\",\"value\":{{\"stringValue\":\"{spanLocator}\"}}}}]}}]}}]}}]}}";

    internal static string DuplicateAndInvalidTypePayload(string traceId, string spanId) =>
        $"{{\"resourceSpans\":[{{\"scopeSpans\":[{{\"spans\":[{{\"traceId\":\"{traceId}\",\"spanId\":\"{spanId}\",\"attributes\":["
        + "{\"key\":\"vcs.repository.url.full\",\"value\":{\"stringValue\":\"https://github.com/Example/One\"}},"
        + "{\"key\":\"vcs.repository.url.full\",\"value\":{\"stringValue\":\"https://github.com/Example/Two\"}},"
        + "{\"key\":\"copilot_chat.repo.remote_url\",\"value\":{\"intValue\":1}}"
        + "]}]}]}]}";

    internal RepositoryOwner SeedManualOwner(string locatorInput)
    {
        Assert.True(GitHubRepositoryLocatorParser.TryParse(locatorInput, out var locator));
        const string repositoryId = "01900000-0000-7000-8000-00000000aa01";
        const string locatorId = "01900000-0000-7000-8000-00000000aa02";
        const string historyId = "01900000-0000-7000-8000-00000000aa03";
        var operationKey = OperationKey(0);
        Execute($"""
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at)
            VALUES('{operationKey}','{new string('b', 64)}',201,'application/json; charset=utf-8','no-store',X'7B7D','{ObservedAt}');
            INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at)
            VALUES('{repositoryId}','ManualOwner',1,'{ObservedAt}','{ObservedAt}');
            INSERT INTO local_repository_locators(
                locator_id,repository_id,kind,canonical_locator,locator_sha256,source,
                display_owner,display_repository,created_at)
            VALUES('{locatorId}','{repositoryId}','github_repository','{locator!.CanonicalLocator}',
                '{locator.LocatorSha256}','manual','{locator.DisplayOwner}','{locator.DisplayRepository}','{ObservedAt}');
            INSERT INTO local_repository_locator_heads(repository_id,kind,locator_id,updated_at)
            VALUES('{repositoryId}','github_repository','{locatorId}','{ObservedAt}');
            INSERT INTO local_repository_history(
                history_id,repository_id,action,previous_revision,new_revision,locator_id,
                cause_kind,operation_key,context_identity_sha256,occurred_at)
            VALUES('{historyId}','{repositoryId}','create',0,1,'{locatorId}',
                'user_operation','{operationKey}',NULL,'{ObservedAt}');
            """);
        return new(repositoryId, locatorId);
    }

    internal void SeedManualAssignment(string sessionId, string repositoryId)
    {
        var operationKey = OperationKey(1);
        var previousFingerprint = ScalarText("SELECT new_assignment_state_sha256 FROM session_repository_assignment_history WHERE session_id='" + sessionId + "' AND new_revision=1;");
        var newFingerprint = LocalRepositoryIdentityHashing.AssignmentStateFingerprint(
            new LocalRepositoryAssignmentState("assigned", "manual", repositoryId, []));
        Execute($"""
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at)
            VALUES('{operationKey}','{new string('c', 64)}',200,'application/json; charset=utf-8','no-store',X'7B7D','{ObservedAt}');
            UPDATE session_repository_assignment_revisions SET revision=2,updated_at='{ObservedAt}' WHERE session_id='{sessionId}';
            INSERT INTO session_repository_assignment_history(
                history_id,session_id,action,previous_revision,new_revision,
                previous_assignment_state_sha256,new_assignment_state_sha256,
                previous_state,new_state,previous_authority,new_authority,
                previous_repository_id,new_repository_id,cause_kind,operation_key,
                reconciliation_fingerprint,occurred_at)
            VALUES('01900000-0000-7000-8000-00000000aa04','{sessionId}','assign',1,2,
                '{previousFingerprint}','{newFingerprint}','assigned','assigned','automatic','manual',
                '{repositoryId}','{repositoryId}','user_operation','{operationKey}',NULL,'{ObservedAt}');
            INSERT INTO session_repository_manual_overrides(session_id,state,repository_id,revision,updated_at)
            VALUES('{sessionId}','assigned','{repositoryId}',2,'{ObservedAt}');
            """);
    }

    internal void SeedContextCollision(
        PreparedInput prepared,
        EventInput matchedEvent,
        bool reuseExpectedContextIdentity)
    {
        var parsed = LocalRepositoryObservationParser.Parse(
            prepared.RawRecordId,
            prepared.Payload,
            prepared.Digest,
            "github-copilot-cli",
            "1.2.3",
            DateTimeOffset.ParseExact(ObservedAt, "O", CultureInfo.InvariantCulture));
        var occurrence = Assert.Single(parsed.Occurrences);
        var locator = Assert.IsType<GitHubRepositoryLocator>(occurrence.Locator);
        var expectedContextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
            occurrence.SourceIdentitySha256,
            matchedEvent.SessionId,
            matchedEvent.EventId,
            matchedEvent.TraceId,
            matchedEvent.SpanId));
        var storedContextIdentity = reuseExpectedContextIdentity
            ? expectedContextIdentity
            : expectedContextIdentity[0] == 'd' ? new string('e', 64) : new string('d', 64);
        Execute($"""
            INSERT INTO session_repository_observations(
                observation_id,source_identity_sha256,raw_record_id,raw_payload_sha256,
                resource_span_ordinal,scope_span_ordinal,span_ordinal,attribute_ordinal,
                scope_kind,attribute_key,value_classification,locator_kind,canonical_locator,
                locator_sha256,display_owner,display_repository,source_surface,
                source_application_version,observed_at)
            VALUES(
                '01900000-0000-7000-8000-00000000aa10','{occurrence.SourceIdentitySha256}',
                {prepared.RawRecordId},'{prepared.Digest}',0,0,0,0,'span','{occurrence.AttributeKey}',
                'admitted','github_repository','{locator.CanonicalLocator}','{locator.LocatorSha256}',
                '{locator.DisplayOwner}','{locator.DisplayRepository}','github-copilot-cli','1.2.3','{ObservedAt}');
            INSERT INTO session_repository_observation_contexts(
                context_id,observation_id,context_identity_sha256,session_event_id,session_id,
                trace_id,span_id,admission_state,repository_id,locator_id,observed_at)
            VALUES(
                '01900000-0000-7000-8000-00000000aa11',
                '01900000-0000-7000-8000-00000000aa10','{storedContextIdentity}',
                '{matchedEvent.EventId}','{matchedEvent.SessionId}','{matchedEvent.TraceId}',
                '{matchedEvent.SpanId}','invalid_locator',NULL,NULL,'{ObservedAt}');
            """);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temp.DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertProvenance(SqliteConnection connection, long rawRecordId, string digest)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO source_schema_observations(
                observation_id,raw_record_id,input_evidence_kind,raw_payload_sha256,
                ingest_batch_id,source_surface,source_application_version,source_adapter,
                adapter_version,schema_fingerprint,inventory_hash,compatibility_state,
                reason_code,next_action,capture_content_state,unknown_span_count,
                unknown_event_count,unknown_attribute_count,overflow_distinct_count,
                overflow_occurrence_count,observed_at)
            VALUES(
                $observation_id,$raw_record_id,'payload_sha256',$digest,
                $ingest_batch_id,'github-copilot-cli','1.2.3','raw-otlp',
                '1','synthetic','synthetic','supported',NULL,'none','available',0,0,0,0,0,$observed_at);
            """;
        command.Parameters.AddWithValue("$observation_id", $"source-observation-{rawRecordId}");
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$ingest_batch_id", $"batch-{rawRecordId}");
        command.Parameters.AddWithValue("$observed_at", ObservedAt);
        command.ExecuteNonQuery();
    }

    private static void InsertEvent(SqliteConnection connection, EventInput input)
    {
        if (input.Disposition == EventDisposition.Missing)
            return;
        using (var session = connection.CreateCommand())
        {
            session.CommandText = """
                INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES($session_id,'active','unbound',$at,'not_captured',$at,$at)
                ON CONFLICT(session_id) DO NOTHING;
                """;
            session.Parameters.AddWithValue("$session_id", input.SessionId);
            session.Parameters.AddWithValue("$at", ObservedAt);
            session.ExecuteNonQuery();
        }
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_events(
                event_id,session_id,source_surface,trace_id,source_adapter,source_event_id,
                type,occurred_at,content_state)
            VALUES(
                $event_id,$session_id,$source_surface,$trace_id,'otel-exact',$source_event_id,
                $type,$at,'not_captured');
            """;
        command.Parameters.AddWithValue("$event_id", input.EventId);
        command.Parameters.AddWithValue("$session_id", input.SessionId);
        command.Parameters.AddWithValue("$source_surface", input.Disposition == EventDisposition.Conflicting ? "vscode" : "copilot-cli");
        command.Parameters.AddWithValue("$trace_id", input.TraceId);
        command.Parameters.AddWithValue("$source_event_id", $"{input.TraceId}/{input.SpanId}");
        command.Parameters.AddWithValue("$type", "otel.span");
        command.Parameters.AddWithValue("$at", ObservedAt);
        command.ExecuteNonQuery();
    }

    private static void InsertQueue(SqliteConnection connection, string queueId, long rawRecordId, string digest)
    {
        var fingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
            LocalRepositoryReconciliationEvidence.PayloadSha256(rawRecordId, digest));
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO local_repository_reconciliation_queue(
                queue_id,raw_record_id,input_evidence_kind,raw_payload_sha256,projector_version,
                reconciliation_fingerprint,state,attempt_count,lease_token,lease_expires_at,
                terminal_reason,created_at,updated_at)
            VALUES($queue_id,$raw_record_id,'payload_sha256',$digest,'local-repository-catalog:1',
                $fingerprint,'pending',0,NULL,NULL,NULL,$at,$at);
            """;
        command.Parameters.AddWithValue("$queue_id", queueId);
        command.Parameters.AddWithValue("$raw_record_id", rawRecordId);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$fingerprint", fingerprint);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        temp.Dispose();
    }

    internal sealed record PreparedInput(string QueueId, long RawRecordId, string Digest, string Payload);
    internal sealed record RepositoryOwner(string RepositoryId, string LocatorId);
    internal sealed record SpanInput(string TraceId, string SpanId, string Locator);
    internal sealed record EventInput(string TraceId, string SpanId, string SessionId, string EventId, EventDisposition Disposition);
    internal enum EventDisposition { Matched, Missing, Conflicting }

    private sealed class SequentialUuidV7Factory
    {
        private long next = 0x10000;
        internal string Next(DateTimeOffset _) => $"01900000-0000-7000-8000-{next++:x12}";
    }

    private sealed class CapturingProcessor(
        ILocalRepositoryRawRecordProcessor inner,
        Action<Exception> capture) : ILocalRepositoryRawRecordProcessor
    {
        public async ValueTask<ILocalRepositoryPreparedRawRecord> PrepareAsync(
            LocalRepositoryQueueLease queueLease,
            RawTelemetryRecord rawRecord,
            RetentionReadLease<RawTelemetryRecord> retentionLease,
            CancellationToken cancellationToken)
        {
            try
            {
                return new CapturingPreparedRawRecord(
                    await inner.PrepareAsync(queueLease, rawRecord, retentionLease, cancellationToken),
                    capture);
            }
            catch (Exception exception)
            {
                capture(exception);
                throw;
            }
        }

        public async ValueTask ProcessAsync(
            LocalRepositoryQueueLease queueLease,
            RawTelemetryRecord rawRecord,
            RetentionReadLease<RawTelemetryRecord> retentionLease,
            CancellationToken cancellationToken)
        {
            try
            {
                await inner.ProcessAsync(queueLease, rawRecord, retentionLease, cancellationToken);
            }
            catch (Exception exception)
            {
                capture(exception);
                throw;
            }
        }

        private sealed class CapturingPreparedRawRecord(
            ILocalRepositoryPreparedRawRecord inner,
            Action<Exception> capture) : ILocalRepositoryPreparedRawRecord
        {
            public async ValueTask FinalizeAsync(
                LocalRepositoryQueueLease queueLease,
                CancellationToken cancellationToken)
            {
                try
                {
                    await inner.FinalizeAsync(queueLease, cancellationToken);
                }
                catch (Exception exception)
                {
                    capture(exception);
                    throw;
                }
            }

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    private sealed class PayloadSubstitutingProcessor(
        ILocalRepositoryRawRecordProcessor inner,
        string payloadJson) : ILocalRepositoryRawRecordProcessor
    {
        public ValueTask<ILocalRepositoryPreparedRawRecord> PrepareAsync(
            LocalRepositoryQueueLease queueLease,
            RawTelemetryRecord rawRecord,
            RetentionReadLease<RawTelemetryRecord> retentionLease,
            CancellationToken cancellationToken) =>
            inner.PrepareAsync(
                queueLease,
                rawRecord with { PayloadJson = payloadJson },
                retentionLease,
                cancellationToken);

        public ValueTask ProcessAsync(
            LocalRepositoryQueueLease queueLease,
            RawTelemetryRecord rawRecord,
            RetentionReadLease<RawTelemetryRecord> retentionLease,
            CancellationToken cancellationToken) =>
            inner.ProcessAsync(
                queueLease,
                rawRecord with { PayloadJson = payloadJson },
                retentionLease,
                cancellationToken);
    }

    private static string Queue(long rawRecordId) => $"01900000-0000-7001-8000-{rawRecordId:x12}";
    private static string OperationKey(byte fill) =>
        "lrc1_" + Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

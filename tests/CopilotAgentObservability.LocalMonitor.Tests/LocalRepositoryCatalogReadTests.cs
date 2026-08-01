namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryCatalogReadTests
{
    [Fact]
    public async Task LocatorRead_ReturnsTheCompleteImmutableCurrentFirstManualList()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", "https://github.com/example/one", fixture.Key(90)));
        _ = await fixture.SetLocatorAsync(repository.RepositoryId, 1, "https://github.com/example/two", fixture.Key(91));
        _ = await fixture.SetLocatorAsync(repository.RepositoryId, 2, "https://github.com/example/three", fixture.Key(92));

        var found = Assert.IsType<LocalRepositoryLocatorsFound>(await fixture.Application.ReadLocatorsAsync(repository.RepositoryId, CancellationToken.None));

        Assert.Equal(3, found.Value.RepositoryRevision);
        Assert.Equal(3, found.Value.Locators.Count);
        Assert.True(found.Value.Locators[0].IsCurrent);
        Assert.Equal("github.com/example/three", found.Value.Locators[0].CanonicalLocator);
        Assert.All(found.Value.Locators, locator =>
        {
            Assert.Equal("manual", locator.Source);
            Assert.Null(locator.Provenance);
        });
        Assert.Throws<NotSupportedException>(() => ((IList<LocalRepositoryLocatorItem>)found.Value.Locators).Add(found.Value.Locators[0]));
    }

    [Fact]
    public async Task LocatorRead_ReturnsZeroOneAndTheExact128ItemFrontier()
    {
        using var emptyFixture = new LocalRepositoryCatalogFixture();
        var emptyRepository = emptyFixture.Repository(await emptyFixture.CreateAsync("Empty", null, emptyFixture.Key(93)));
        var empty = Assert.IsType<LocalRepositoryLocatorsFound>(await emptyFixture.Application.ReadLocatorsAsync(emptyRepository.RepositoryId, CancellationToken.None));
        Assert.Empty(empty.Value.Locators);

        using var oneFixture = new LocalRepositoryCatalogFixture();
        var oneRepository = oneFixture.Repository(await oneFixture.CreateAsync("One", "https://github.com/example/one", oneFixture.Key(94)));
        var one = Assert.IsType<LocalRepositoryLocatorsFound>(await oneFixture.Application.ReadLocatorsAsync(oneRepository.RepositoryId, CancellationToken.None));
        Assert.Single(one.Value.Locators);

        using var fullFixture = new LocalRepositoryCatalogFixture();
        var fullRepository = fullFixture.Repository(await fullFixture.CreateAsync("Full", "https://github.com/example/full", fullFixture.Key(95)));
        fullFixture.SeedHistoricalLocators(fullRepository.RepositoryId, 127);
        var full = Assert.IsType<LocalRepositoryLocatorsFound>(await fullFixture.Application.ReadLocatorsAsync(fullRepository.RepositoryId, CancellationToken.None));
        Assert.Equal(128, full.Value.Locators.Count);
        Assert.Equal(
            full.Value.Locators.Skip(1).Select(static locator => locator.LocatorId).OrderBy(static value => value, StringComparer.Ordinal),
            full.Value.Locators.Skip(1).Select(static locator => locator.LocatorId));
    }

    [Fact]
    public async Task LocatorRead_RejectsAStored129ItemFrontierWithoutTruncation()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("Full", "https://github.com/example/full", fixture.Key(96)));
        fixture.SeedHistoricalLocators(repository.RepositoryId, 127);
        Assert.True(CopilotAgentObservability.Telemetry.Repositories.GitHubRepositoryLocatorParser.TryParse("https://github.com/example/overflow", out var overflow));
        fixture.Execute($"INSERT INTO local_repository_locators VALUES('{LocalRepositoryCatalogFixture.RepositoryId(9999)}','{repository.RepositoryId}','github_repository','{overflow!.CanonicalLocator}','{overflow.LocatorSha256}','manual','{overflow.DisplayOwner}','{overflow.DisplayRepository}','{LocalRepositoryCatalogFixture.At}');");

        Assert.IsType<LocalRepositoryLocatorReadCorrupt>(
            await fixture.Application.ReadLocatorsAsync(repository.RepositoryId, CancellationToken.None));
    }

    [Fact]
    public async Task ObservedLocatorRead_ProjectsExactAvailableProvenanceAndReleasesTheLeaseBeforeReturning()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
            LocalRepositoryAdmissionFixture.Trace(1),
            LocalRepositoryAdmissionFixture.Span(1),
            "https://github.com/Example/Observed.git"));
        _ = await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(1)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        var application = ReadApplication(fixture.DatabasePath);

        var found = Assert.IsType<LocalRepositoryLocatorsFound>(
            await application.ReadLocatorsAsync(repositoryId, CancellationToken.None));

        var locator = Assert.Single(found.Value.Locators);
        Assert.Equal("observed", locator.Source);
        Assert.NotNull(locator.Provenance);
        Assert.Equal(LocalRepositoryAdmissionFixture.Trace(1), locator.Provenance.TraceId);
        Assert.Equal(LocalRepositoryAdmissionFixture.Span(1), locator.Provenance.SpanId);
        Assert.Equal("available", locator.Provenance.SourceContentAvailability);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='access';"));
        fixture.Execute("BEGIN IMMEDIATE; UPDATE retention_items SET state=state WHERE item_id=(SELECT item_id FROM retention_items WHERE store_kind='raw_record' LIMIT 1); COMMIT;");
    }

    [Fact]
    public async Task ObservedLocatorRead_ReportsRawAvailabilityBusyAfterCatalogFrontierWasRead()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        _ = await fixture.RunAsync(LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(11), LocalRepositoryAdmissionFixture.Span(11), "https://github.com/Example/Busy.git")), [LocalRepositoryAdmissionFixture.MatchedEvent(11)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        using var checkpoint = new LocatorReadCheckpoint(fixture.DatabasePath, cancel: null, blockAvailability: true);

        var result = await ReadApplication(fixture.DatabasePath, checkpoint).ReadLocatorsAsync(repositoryId, CancellationToken.None);

        Assert.IsType<LocalRepositoryLocatorReadBusy>(result);
        Assert.True(checkpoint.BeforeAvailabilityReached);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='access';"));
    }

    [Fact]
    public async Task ObservedLocatorRead_CancellationAfterLeaseAcquisitionUnwindsTheAccessLease()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        _ = await fixture.RunAsync(LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(LocalRepositoryAdmissionFixture.Trace(12), LocalRepositoryAdmissionFixture.Span(12), "https://github.com/Example/Cancel.git")), [LocalRepositoryAdmissionFixture.MatchedEvent(12)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        using var cancellation = new CancellationTokenSource();
        using var checkpoint = new LocatorReadCheckpoint(fixture.DatabasePath, cancellation, blockAvailability: false);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await ReadApplication(fixture.DatabasePath, checkpoint).ReadLocatorsAsync(repositoryId, cancellation.Token));

        Assert.True(checkpoint.AfterLeaseReached);
        Assert.True(checkpoint.LiveAccessLeaseObserved);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='access';"));
    }

    [Fact]
    public async Task ObservedLocatorRead_ProjectsExpiredFromTheExactRetentionFact()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
            LocalRepositoryAdmissionFixture.Trace(3),
            LocalRepositoryAdmissionFixture.Span(3),
            "https://github.com/Example/Expired.git"));
        _ = await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(3)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        fixture.Execute("""
            INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at)
            SELECT item_id,'2026-08-01T00:00:00.0000000+00:00','2026-08-01T00:00:00.0000000+00:00'
            FROM retention_items
            WHERE store_kind='raw_record'
              AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1);
            UPDATE retention_items
            SET state='deleted',read_denied_at='2026-08-01T00:00:00.0000000+00:00',deleted_at='2026-08-01T00:00:00.0000000+00:00'
            WHERE store_kind='raw_record'
              AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1);
            """);
        var application = ReadApplication(fixture.DatabasePath);

        var found = Assert.IsType<LocalRepositoryLocatorsFound>(
            await application.ReadLocatorsAsync(repositoryId, CancellationToken.None));

        Assert.Equal("expired", Assert.Single(found.Value.Locators).Provenance!.SourceContentAvailability);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='access';"));
    }

    [Fact]
    public async Task ObservedLocatorRead_ProjectsUnknownForACatalogOnlyRawReference()
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
            LocalRepositoryAdmissionFixture.Trace(4),
            LocalRepositoryAdmissionFixture.Span(4),
            "https://github.com/Example/Unknown.git"));
        _ = await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(4)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        fixture.Execute("""
            DELETE FROM retention_capture_journal
            WHERE item_id=(SELECT item_id FROM retention_items
              WHERE store_kind='raw_record'
                AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1));
            DELETE FROM retention_items
            WHERE store_kind='raw_record'
              AND source_item_id=(SELECT CAST(raw_record_id AS TEXT) FROM session_repository_observations LIMIT 1);
            DELETE FROM raw_records
            WHERE id=(SELECT raw_record_id FROM session_repository_observations LIMIT 1);
            """);
        var application = ReadApplication(fixture.DatabasePath);

        var found = Assert.IsType<LocalRepositoryLocatorsFound>(
            await application.ReadLocatorsAsync(repositoryId, CancellationToken.None));

        Assert.Equal("unknown", Assert.Single(found.Value.Locators).Provenance!.SourceContentAvailability);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='access';"));
    }

    [Theory]
    [InlineData("source_identity")]
    [InlineData("payload_digest")]
    [InlineData("trace")]
    public async Task ObservedLocatorRead_RejectsInexactSourceFactsAndLeavesNoLease(string corruption)
    {
        using var fixture = new LocalRepositoryAdmissionFixture();
        var payload = LocalRepositoryAdmissionFixture.SpanPayload(new LocalRepositoryAdmissionFixture.SpanInput(
            LocalRepositoryAdmissionFixture.Trace(2),
            LocalRepositoryAdmissionFixture.Span(2),
            "https://github.com/Example/Observed.git"));
        _ = await fixture.RunAsync(payload, [LocalRepositoryAdmissionFixture.MatchedEvent(2)]);
        var repositoryId = fixture.ScalarText("SELECT repository_id FROM local_repositories;");
        if (corruption == "trace")
        {
            fixture.Execute("DROP TRIGGER session_repository_observation_contexts_update_rejected; PRAGMA ignore_check_constraints=ON; UPDATE session_repository_observation_contexts SET trace_id='ABCDEF00000000000000000000000000';");
        }
        else
        {
            fixture.Execute("DROP TRIGGER session_repository_observations_update_rejected;");
        }
        if (corruption == "source_identity")
            fixture.Execute("UPDATE session_repository_observations SET raw_record_id=raw_record_id+1000;");
        else if (corruption == "payload_digest")
            fixture.Execute($"UPDATE session_repository_observations SET raw_payload_sha256='{new string('f', 64)}';");
        var application = ReadApplication(fixture.DatabasePath);

        Assert.IsType<LocalRepositoryLocatorReadCorrupt>(
            await application.ReadLocatorsAsync(repositoryId, CancellationToken.None));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM retention_leases WHERE lease_kind='access';"));
    }

    [Fact]
    public async Task InvalidOrMissingReadTargets_ReturnTheTypedNotFoundUnions()
    {
        using var fixture = new LocalRepositoryCatalogFixture();

        Assert.IsType<LocalRepositoryLocatorRepositoryNotFound>(await fixture.Application.ReadLocatorsAsync("NOT-A-UUID", CancellationToken.None));
        Assert.IsType<LocalRepositoryLocatorRepositoryNotFound>(await fixture.Application.ReadLocatorsAsync(LocalRepositoryCatalogFixture.RepositoryId(999), CancellationToken.None));
        Assert.IsType<LocalRepositoryAssignmentSessionNotFound>(await fixture.Application.ReadAssignmentAsync("NOT-A-UUID", CancellationToken.None));
        Assert.IsType<LocalRepositoryAssignmentSessionNotFound>(await fixture.Application.ReadAssignmentAsync(LocalRepositoryCatalogFixture.SessionId(999), CancellationToken.None));
    }

    [Fact]
    public async Task AssignmentRead_ProjectsRevisionZeroAndConflictIdsOnlyForConflict()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var sessionId = LocalRepositoryCatalogFixture.SessionId(100);
        fixture.CreateSession(sessionId);

        var empty = Assert.IsType<LocalRepositoryAssignmentFound>(await fixture.Application.ReadAssignmentAsync(sessionId, CancellationToken.None));
        Assert.Equal(0, empty.Value.AssignmentRevision);
        Assert.Equal("unassigned", empty.Value.State);
        Assert.Equal("none", empty.Value.Authority);
        Assert.Null(empty.Value.UpdatedAt);
        Assert.Empty(empty.Value.ConflictingRepositoryIds);

        var second = fixture.Repository(await fixture.CreateAsync("Second", "https://github.com/example/second", fixture.Key(101)));
        var first = fixture.Repository(await fixture.CreateAsync("First", "https://github.com/example/first", fixture.Key(102)));
        fixture.SeedAutomaticCandidate(sessionId, second.RepositoryId, 1001);
        fixture.SeedAutomaticCandidate(sessionId, first.RepositoryId, 1002);

        var conflict = Assert.IsType<LocalRepositoryAssignmentFound>(await fixture.Application.ReadAssignmentAsync(sessionId, CancellationToken.None));
        Assert.Equal("conflict", conflict.Value.State);
        Assert.Equal("automatic", conflict.Value.Authority);
        Assert.Equal(new[] { second.RepositoryId, first.RepositoryId }.OrderBy(static value => value, StringComparer.Ordinal), conflict.Value.ConflictingRepositoryIds);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)conflict.Value.ConflictingRepositoryIds).Add(first.RepositoryId));

        _ = await fixture.SessionActionAsync(sessionId, conflict.Value.AssignmentRevision, "assign", first.RepositoryId, fixture.Key(103));
        var manual = Assert.IsType<LocalRepositoryAssignmentFound>(await fixture.Application.ReadAssignmentAsync(sessionId, CancellationToken.None));
        Assert.Equal("assigned", manual.Value.State);
        Assert.Equal("manual", manual.Value.Authority);
        Assert.Equal(first.RepositoryId, manual.Value.RepositoryId);
        Assert.Empty(manual.Value.ConflictingRepositoryIds);

        _ = await fixture.SessionActionAsync(sessionId, manual.Value.AssignmentRevision, "resume_automatic", null, fixture.Key(104));
        var resumed = Assert.IsType<LocalRepositoryAssignmentFound>(await fixture.Application.ReadAssignmentAsync(sessionId, CancellationToken.None));
        Assert.Equal("conflict", resumed.Value.State);
        Assert.Equal(conflict.Value.ConflictingRepositoryIds, resumed.Value.ConflictingRepositoryIds);
    }

    [Fact]
    public async Task AssignmentActions_ApplyExactRevisionAndNoOpRules()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var sessionId = LocalRepositoryCatalogFixture.SessionId(110);
        fixture.CreateSession(sessionId);
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(110)));

        var assigned = Assert.IsType<LocalRepositoryMutationSucceeded>(await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(111)));
        var noOp = Assert.IsType<LocalRepositoryMutationSucceeded>(await fixture.SessionActionAsync(sessionId, 1, "assign", repository.RepositoryId, fixture.Key(112)));
        var unassigned = Assert.IsType<LocalRepositoryMutationSucceeded>(await fixture.SessionActionAsync(sessionId, 1, "explicitly_unassign", null, fixture.Key(113)));
        var stale = Assert.IsType<LocalRepositoryMutationRejected>(await fixture.SessionActionAsync(sessionId, 1, "resume_automatic", null, fixture.Key(114)));

        Assert.Equal(200, assigned.Response.StatusCode);
        Assert.Equal(200, noOp.Response.StatusCode);
        Assert.Equal(200, unassigned.Response.StatusCode);
        Assert.Equal(LocalRepositoryMutationFailure.RevisionConflict, stale.Failure);
        Assert.Equal(2, fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{sessionId}';"));
        Assert.Equal(["assign", "explicitly_unassign"], fixture.QueryStrings($"SELECT action FROM session_repository_assignment_history WHERE session_id='{sessionId}' ORDER BY new_revision;"));
        Assert.Equal(4, fixture.ScalarLong("SELECT COUNT(*) FROM local_repository_operation_receipts;"));
    }

    [Fact]
    public async Task ManualAssignFromAutomaticAndResume_UseTheCurrentSingleCandidateSet()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var sessionId = LocalRepositoryCatalogFixture.SessionId(120);
        fixture.CreateSession(sessionId);
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            "https://github.com/example/one",
            fixture.Key(120)));
        fixture.SeedAutomaticCandidate(sessionId, repository.RepositoryId, 1200);

        var automatic = Assert.IsType<LocalRepositoryAssignmentFound>(
            await fixture.Application.ReadAssignmentAsync(sessionId, CancellationToken.None));
        Assert.Equal("automatic", automatic.Value.Authority);
        var manual = Assert.IsType<LocalRepositoryMutationSucceeded>(
            await fixture.SessionActionAsync(sessionId, automatic.Value.AssignmentRevision, "assign", repository.RepositoryId, fixture.Key(121)));
        var resumed = Assert.IsType<LocalRepositoryMutationSucceeded>(
            await fixture.SessionActionAsync(sessionId, 2, "resume_automatic", null, fixture.Key(122)));
        var final = Assert.IsType<LocalRepositoryAssignmentFound>(
            await fixture.Application.ReadAssignmentAsync(sessionId, CancellationToken.None));

        Assert.Equal(200, manual.Response.StatusCode);
        Assert.Equal(200, resumed.Response.StatusCode);
        Assert.Equal(3, final.Value.AssignmentRevision);
        Assert.Equal("assigned", final.Value.State);
        Assert.Equal("automatic", final.Value.Authority);
        Assert.Equal(repository.RepositoryId, final.Value.RepositoryId);
        Assert.Empty(final.Value.ConflictingRepositoryIds);
        Assert.Equal(
            ["automatic_reconcile", "assign", "resume_automatic"],
            fixture.QueryStrings($"SELECT action FROM session_repository_assignment_history WHERE session_id='{sessionId}' ORDER BY new_revision;"));
    }

    private static LocalRepositoryCatalogApplication ReadApplication(string databasePath, ILocalRepositoryLocatorReadCheckpoint? checkpoint = null)
    {
        var queue = new SqliteLocalRepositoryReconciliationStore(
            databasePath,
            TimeProvider.System,
            static () => new string('c', 64));
        return new(new SqliteLocalRepositoryCatalogStore(
            databasePath,
            queue,
            new LocalRepositoryAssignmentResolver(),
            TimeProvider.System,
            locatorReadCheckpoint: checkpoint));
    }

    private sealed class LocatorReadCheckpoint(string databasePath, CancellationTokenSource? cancel, bool blockAvailability) : ILocalRepositoryLocatorReadCheckpoint, IDisposable
    {
        private Microsoft.Data.Sqlite.SqliteConnection? blocker;
        private Microsoft.Data.Sqlite.SqliteTransaction? transaction;
        internal bool BeforeAvailabilityReached { get; private set; }
        internal bool AfterLeaseReached { get; private set; }
        internal bool LiveAccessLeaseObserved { get; private set; }
        public void Reached(LocalRepositoryLocatorReadCheckpoint checkpoint)
        {
            if (checkpoint == LocalRepositoryLocatorReadCheckpoint.BeforeAvailabilityRead)
            {
                BeforeAvailabilityReached = true;
                if (blockAvailability)
                {
                    blocker = new($"Data Source={databasePath};Pooling=False");
                    blocker.Open();
                    transaction = blocker.BeginTransaction(deferred: false);
                }
            }
            else
            {
                AfterLeaseReached = true;
                using var observer = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
                observer.Open();
                using var command = observer.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='access';";
                LiveAccessLeaseObserved = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
                Assert.True(LiveAccessLeaseObserved);
                cancel?.Cancel();
            }
        }
        public void Dispose() { transaction?.Dispose(); blocker?.Dispose(); }
    }
}

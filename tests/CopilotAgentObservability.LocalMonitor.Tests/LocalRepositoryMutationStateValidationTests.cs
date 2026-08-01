using CopilotAgentObservability.Telemetry.Repositories;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryMutationStateValidationTests
{
    [Fact]
    public void EmptyCatalog_ValidatesInsideTheCallerOwnedTransaction()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        using var connection = fixture.Open();
        using var transaction = connection.BeginTransaction();

        SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, transaction);

        transaction.Rollback();
    }

    [Fact]
    public async Task CompleteUserMutationHistoryAndStaleNoOpReceipts_Validate()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            "https://github.com/example/one",
            fixture.Key(1)));
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(2));
        _ = await fixture.SetLocatorAsync(repository.RepositoryId, 2, "https://github.com/example/two", fixture.Key(3));
        _ = await fixture.SetLocatorAsync(repository.RepositoryId, 3, "https://github.com/example/two", fixture.Key(4));
        _ = await fixture.RenameAsync(repository.RepositoryId, 3, "Three", fixture.Key(5));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(1);
        fixture.CreateSession(sessionId);
        _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(6));
        _ = await fixture.SessionActionAsync(sessionId, 1, "explicitly_unassign", null, fixture.Key(7));
        _ = await fixture.SessionActionAsync(sessionId, 2, "resume_automatic", null, fixture.Key(8));

        Validate(fixture);
    }

    [Theory]
    [InlineData("missing_first")]
    [InlineData("missing_latest")]
    [InlineData("head_wrong_kind")]
    [InlineData("head_wrong_owner")]
    public async Task RepositoryChainContradiction_UsesTheFixedValueFreeFailure(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            "https://github.com/example/one",
            fixture.Key(10)));
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(11));
        fixture.CorruptRepositoryHistoryTransition(repository.RepositoryId, corruption);

        var error = Assert.Throws<InvalidOperationException>(() => Validate(fixture));

        Assert.Equal("local_repository_mutation_state_invalid", error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(repository.RepositoryId, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AssignmentChainFingerprintContradiction_IsRejected()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(20)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(2);
        fixture.CreateSession(sessionId);
        _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(21));
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER session_repository_assignment_history_update_rejected;
            UPDATE session_repository_assignment_history
            SET new_assignment_state_sha256='{new string('a', 64)}'
            WHERE session_id='{sessionId}';
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task CandidateBearingSessionWhoseRevisionChainWasRemoved_IsStillVisitedAtLogicalRevisionZero()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            "https://github.com/example/one",
            fixture.Key(30)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(3);
        fixture.CreateSession(sessionId);
        fixture.SeedAutomaticCandidate(sessionId, repository.RepositoryId, 30);
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER session_repository_assignment_history_delete_rejected;
            DELETE FROM session_repository_assignment_history WHERE session_id='{sessionId}';
            DELETE FROM session_repository_assignment_revisions WHERE session_id='{sessionId}';
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task AutomaticHistoryValidation_DoesNotQueryTheTask5QueueRelationship()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            "https://github.com/example/one",
            fixture.Key(35)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(35);
        fixture.CreateSession(sessionId);
        fixture.SeedAutomaticCandidate(sessionId, repository.RepositoryId, 35);
        fixture.Execute("DELETE FROM local_repository_reconciliation_queue;");

        Validate(fixture);
    }

    [Fact]
    public async Task LatestProvableRenameFingerprint_IsValidated()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(40)));
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(41));
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts
            SET request_fingerprint='{new string('a', 64)}'
            WHERE operation_key='{fixture.Key(41)}';
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task SupersededCreateAndRenameFingerprints_AreNotReconstructed()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(50)));
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(51));
        _ = await fixture.RenameAsync(repository.RepositoryId, 2, "Three", fixture.Key(52));
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts
            SET request_fingerprint='{new string('a', 64)}'
            WHERE operation_key IN ('{fixture.Key(50)}','{fixture.Key(51)}');
            """);

        Validate(fixture);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("oversized")]
    public async Task ReceiptEntityStorageGuard_RejectsBeforeMaterialization(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        _ = await fixture.CreateAsync("One", null, fixture.Key(60));
        var value = corruption == "text" ? "CAST(response_entity AS TEXT)" : "zeroblob(16385)";
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts SET response_entity={value};
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task UnlinkedCreateReceipt_IsRejected()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        _ = await fixture.CreateAsync("One", null, fixture.Key(70));
        fixture.Execute($"""
            INSERT INTO local_repository_operation_receipts
            SELECT '{fixture.Key(71)}',request_fingerprint,status_code,content_type,cache_control,response_entity,created_at
            FROM local_repository_operation_receipts
            WHERE operation_key='{fixture.Key(70)}';
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task PagingObserver_ReportsOnlyBoundedPerCallResidentPages()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        string? firstRepositoryId = null;
        for (var index = 0; index < 129; index++)
        {
            var repository = fixture.Repository(await fixture.CreateAsync($"Repository {index}", null, OperationKey(index + 100)));
            firstRepositoryId ??= repository.RepositoryId;
        }
        for (var index = 0; index < 129; index++)
        {
            var sessionId = LocalRepositoryCatalogFixture.SessionId(2_000 + index);
            CreateDistinctSession(fixture, sessionId, 2_000 + index);
            _ = await fixture.SessionActionAsync(
                sessionId,
                0,
                "assign",
                firstRepositoryId,
                OperationKey(index + 2_000));
        }
        var first = new RecordingObserver();
        var second = new RecordingObserver();

        Validate(fixture, first);
        Validate(fixture);
        Validate(fixture, second);

        Assert.All(first.Counts, static item => Assert.InRange(item.Count, 1, 128));
        Assert.All(second.Counts, static item => Assert.InRange(item.Count, 1, 128));
        Assert.Equal(first.Counts, second.Counts);
        Assert.True(first.Counts.Count(item => item.Buffer == LocalRepositoryMutationValidationBuffer.RepositoryHeadPage) >= 129);
        Assert.True(first.Counts.Count(item => item.Buffer == LocalRepositoryMutationValidationBuffer.RepositoryHistoryPage) >= 129);
        Assert.True(first.Counts.Count(item => item.Buffer == LocalRepositoryMutationValidationBuffer.AssignmentHeadPage) >= 129);
        Assert.True(first.Counts.Count(item => item.Buffer == LocalRepositoryMutationValidationBuffer.AssignmentHistoryPage) >= 129);
        Assert.True(first.Counts.Count(item => item.Buffer == LocalRepositoryMutationValidationBuffer.ReceiptPage) >= 129);
        Assert.True(first.Counts.Count(item => item.Buffer == LocalRepositoryMutationValidationBuffer.ReceiptHistoryLinkPage) >= 129);
    }

    [Fact]
    public async Task RepositoryHistoryPaging_TraversesPast128AndReportsResidentMaximum128()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("Revision 1", null, fixture.Key(91)));
        for (var revision = 1; revision <= 128; revision++)
        {
            _ = await fixture.RenameAsync(
                repository.RepositoryId,
                revision,
                $"Revision {revision + 1}",
                OperationKey(1_000 + revision));
        }
        var observer = new RecordingObserver();

        Validate(fixture, observer);

        var historyCounts = observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.RepositoryHistoryPage)
            .Select(static item => item.Count)
            .ToArray();
        Assert.Equal(129, historyCounts.Length);
        Assert.Equal(128, historyCounts.Max());
        Assert.Equal(1, historyCounts[^1]);
    }

    [Fact]
    public async Task RepositoryHistoryPaging_LaterPageContradictionIsRejectedAfterResidentMaximum128()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("Revision 1", null, fixture.Key(92)));
        for (var revision = 1; revision <= 128; revision++)
        {
            _ = await fixture.RenameAsync(
                repository.RepositoryId,
                revision,
                $"Revision {revision + 1}",
                OperationKey(1_200 + revision));
        }
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER local_repository_history_update_rejected;
            UPDATE local_repository_history
            SET cause_kind='source_context'
            WHERE repository_id='{repository.RepositoryId}' AND new_revision=129;
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        var historyCounts = observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.RepositoryHistoryPage)
            .Select(static item => item.Count)
            .ToArray();
        Assert.Equal(129, historyCounts.Length);
        Assert.Equal(128, historyCounts.Max());
        Assert.Equal(1, historyCounts[^1]);
    }

    [Fact]
    public async Task LocatorRow129_IsRejectedBeforeConversionOrNotification()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            "https://github.com/example/one",
            fixture.Key(80)));
        fixture.SeedHistoricalLocators(repository.RepositoryId, 127);
        var locatorId = LocalRepositoryCatalogFixture.RepositoryId(999_999);
        Assert.True(GitHubRepositoryLocatorParser.TryParse("https://github.com/example/overflow", out var locator));
        fixture.Execute($"""
            INSERT INTO local_repository_locators
            VALUES('{locatorId}','{repository.RepositoryId}','github_repository','{locator!.CanonicalLocator}',
                   '{locator.LocatorSha256}','manual','{locator.DisplayOwner}','{locator.DisplayRepository}',
                   '{LocalRepositoryCatalogFixture.At}');
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        Assert.Equal(128, observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.RepositoryLocatorCollection)
            .Max(static item => item.Count));
    }

    [Fact]
    public async Task CurrentCandidateRow129_IsRejectedByTheObservedResolverCoreAtResident128()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repositories = new List<(string RepositoryId, string LocatorId)>();
        for (var index = 0; index < 129; index++)
        {
            var repository = fixture.Repository(await fixture.CreateAsync(
                $"Candidate {index}",
                $"https://github.com/example/candidate{index}",
                OperationKey(index + 400)));
            repositories.Add((repository.RepositoryId, fixture.ScalarText(
                $"SELECT locator_id FROM local_repository_locator_heads WHERE repository_id='{repository.RepositoryId}';")));
        }
        var sessionId = LocalRepositoryCatalogFixture.SessionId(400);
        fixture.CreateSession(sessionId);
        var eventId = fixture.ScalarText($"SELECT event_id FROM session_events WHERE session_id='{sessionId}';");
        var sql = new System.Text.StringBuilder();
        for (var index = 0; index < repositories.Count; index++)
        {
            var observationId = LocalRepositoryCatalogFixture.RepositoryId(500_000 + index);
            var contextId = LocalRepositoryCatalogFixture.RepositoryId(600_000 + index);
            var sourceIdentity = (index + 1).ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            var contextIdentity = (index + 1000).ToString("x64", System.Globalization.CultureInfo.InvariantCulture);
            var (repositoryId, locatorId) = repositories[index];
            sql.Append($"""
                INSERT INTO session_repository_observations VALUES(
                    '{observationId}','{sourceIdentity}',{index + 1},'{new string('b', 64)}',0,0,0,0,'span',
                    'vcs.repository.url.full','admitted','github_repository',
                    (SELECT canonical_locator FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    (SELECT locator_sha256 FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    (SELECT display_owner FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    (SELECT display_repository FROM local_repository_locators WHERE locator_id='{locatorId}'),
                    'github-copilot-vscode','1.2.3','{LocalRepositoryCatalogFixture.At}');
                INSERT INTO session_repository_observation_contexts VALUES(
                    '{contextId}','{observationId}','{contextIdentity}','{eventId}','{sessionId}',
                    '11111111111111111111111111111111','2222222222222222','admitted',
                    '{repositoryId}','{locatorId}','{LocalRepositoryCatalogFixture.At}');
                """);
        }
        fixture.Execute(sql.ToString());
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        Assert.Equal(128, observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.CurrentCandidateCollection)
            .Max(static item => item.Count));
    }

    [Fact]
    public void RepositoryKeyset_VisitsMalformedBinarySmallestKey()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        fixture.ExecuteUnchecked($"""
            INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at)
            VALUES('','One',1,'{LocalRepositoryCatalogFixture.At}','{LocalRepositoryCatalogFixture.At}');
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        Assert.Contains(
            (LocalRepositoryMutationValidationBuffer.RepositoryHeadPage, 1),
            observer.Counts);
    }

    [Fact]
    public void AssignmentHeadKeyset_VisitsMalformedBinarySmallestKey()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        fixture.ExecuteUnchecked($"""
            INSERT INTO session_repository_assignment_revisions(session_id,revision,updated_at)
            VALUES('',1,'{LocalRepositoryCatalogFixture.At}');
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        Assert.Contains(
            (LocalRepositoryMutationValidationBuffer.AssignmentHeadPage, 1),
            observer.Counts);
    }

    [Fact]
    public async Task ReceiptKeyset_VisitsMalformedBinarySmallestKey()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(94)));
        InsertReceipt(fixture, string.Empty, 200, LocalRepositoryCatalogFixture.RepositoryEntity(repository));
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        Assert.Contains(
            (LocalRepositoryMutationValidationBuffer.ReceiptPage, 1),
            observer.Counts);
    }

    [Fact]
    public async Task AssignmentHeadKeyset_LaterPageContradictionIsNotOmitted()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(95)));
        string? lastSessionId = null;
        for (var index = 0; index < 129; index++)
        {
            lastSessionId = LocalRepositoryCatalogFixture.SessionId(3_000 + index);
            CreateDistinctSession(fixture, lastSessionId, 3_000 + index);
            _ = await fixture.SessionActionAsync(
                lastSessionId,
                0,
                "assign",
                repository.RepositoryId,
                OperationKey(3_000 + index));
        }
        fixture.ExecuteUnchecked($"""
            UPDATE session_repository_assignment_revisions SET revision=0 WHERE session_id='{lastSessionId}';
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        var counts = observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.AssignmentHeadPage)
            .Select(static item => item.Count)
            .ToArray();
        Assert.Equal(129, counts.Length);
        Assert.Equal(128, counts.Max());
        Assert.Equal(1, counts[^1]);
    }

    [Fact]
    public async Task ReceiptKeyset_LaterPageContradictionIsNotOmitted()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        for (var index = 0; index < 129; index++)
            _ = await fixture.CreateAsync($"Repository {index}", null, OperationKey(3_200 + index));
        var lastKey = fixture.ScalarText("SELECT MAX(operation_key COLLATE BINARY) FROM local_repository_operation_receipts;");
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts SET content_type='application/json' WHERE operation_key='{lastKey}';
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        var counts = observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.ReceiptPage)
            .Select(static item => item.Count)
            .ToArray();
        Assert.Equal(129, counts.Length);
        Assert.Equal(128, counts.Max());
        Assert.Equal(1, counts[^1]);
    }

    [Fact]
    public async Task AssignmentHistoryPaging_TraversesPast128WithResidentMaximum128()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(96)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(3_400);
        fixture.CreateSession(sessionId);
        for (var revision = 0; revision < 129; revision++)
        {
            var assigned = revision % 2 == 0;
            _ = await fixture.SessionActionAsync(
                sessionId,
                revision,
                assigned ? "assign" : "explicitly_unassign",
                assigned ? repository.RepositoryId : null,
                OperationKey(3_400 + revision));
        }
        var observer = new RecordingObserver();

        Validate(fixture, observer);

        var counts = observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.AssignmentHistoryPage)
            .Select(static item => item.Count)
            .ToArray();
        Assert.Equal(129, counts.Length);
        Assert.Equal(128, counts.Max());
        Assert.Equal(1, counts[^1]);
    }

    [Theory]
    [InlineData("47")]
    [InlineData("49")]
    [InlineData("padding")]
    [InlineData("alphabet")]
    [InlineData("noncanonical_final")]
    [InlineData("canonical_32")]
    public async Task ReceiptOperationKey_UsesExactCanonical32ByteAuthority(string operationKeyCase)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(97)));
        var key = operationKeyCase switch
        {
            "47" => "lrc1_" + new string('A', 42),
            "49" => "lrc1_" + new string('A', 44),
            "padding" => "lrc1_" + new string('A', 42) + "=",
            "alphabet" => "lrc1_" + new string('A', 42) + "!",
            "noncanonical_final" => "lrc1_" + new string('A', 42) + "B",
            "canonical_32" => "lrc1_" + new string('A', 43),
            _ => throw new ArgumentOutOfRangeException(nameof(operationKeyCase)),
        };
        InsertReceipt(fixture, key, 200, LocalRepositoryCatalogFixture.RepositoryEntity(repository));

        if (operationKeyCase == "canonical_32")
            Validate(fixture);
        else
            AssertInvalid(fixture);
    }

    [Fact]
    public async Task CandidateBearingSessionWithTwoRepositoriesAndNoChain_IsRejectedAtLogicalRevisionZero()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var first = fixture.Repository(await fixture.CreateAsync("One", "https://github.com/example/one", fixture.Key(98)));
        var second = fixture.Repository(await fixture.CreateAsync("Two", "https://github.com/example/two", fixture.Key(99)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(3_600);
        fixture.CreateSession(sessionId);
        fixture.SeedAutomaticCandidate(sessionId, first.RepositoryId, 3_601);
        fixture.SeedAutomaticCandidate(sessionId, second.RepositoryId, 3_602);
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER session_repository_assignment_history_delete_rejected;
            DELETE FROM session_repository_assignment_history WHERE session_id='{sessionId}';
            DELETE FROM session_repository_assignment_revisions WHERE session_id='{sessionId}';
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public void TrulyEmptySessionWithoutAssignmentMaterialization_Validates()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        fixture.CreateSession(LocalRepositoryCatalogFixture.SessionId(3_700));

        Validate(fixture);
    }

    [Fact]
    public async Task HistoricalConflictFingerprint_RemainsSyntaxOnlyWhenLaterCurrentHeadIsExact()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var first = fixture.Repository(await fixture.CreateAsync("One", "https://github.com/example/one", fixture.Key(100)));
        var second = fixture.Repository(await fixture.CreateAsync("Two", "https://github.com/example/two", fixture.Key(101)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(3_800);
        fixture.CreateSession(sessionId);
        fixture.SeedAutomaticCandidate(sessionId, first.RepositoryId, 3_801);
        fixture.SeedAutomaticCandidate(sessionId, second.RepositoryId, 3_802);
        _ = await fixture.SessionActionAsync(sessionId, 2, "assign", first.RepositoryId, fixture.Key(102));
        var unavailableHistoricalFingerprint = new string('c', 64);
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER session_repository_assignment_history_update_rejected;
            UPDATE session_repository_assignment_history
            SET new_assignment_state_sha256='{unavailableHistoricalFingerprint}'
            WHERE session_id='{sessionId}' AND new_revision=2;
            UPDATE session_repository_assignment_history
            SET previous_assignment_state_sha256='{unavailableHistoricalFingerprint}'
            WHERE session_id='{sessionId}' AND new_revision=3;
            """);

        Validate(fixture);
    }

    [Theory]
    [InlineData("missing_middle")]
    [InlineData("noncontiguous")]
    [InlineData("head_mismatch")]
    [InlineData("orphan_locator")]
    [InlineData("orphan_history")]
    [InlineData("orphan_head")]
    [InlineData("invalid_add")]
    [InlineData("invalid_replace")]
    [InlineData("wrong_locator_source")]
    [InlineData("missing_cause")]
    [InlineData("dual_cause")]
    [InlineData("wrong_cause_kind")]
    [InlineData("unrelated_cause")]
    [InlineData("missing_receipt")]
    public async Task RepositoryChainAndCauseMatrix_RejectsEveryContradiction(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(110)));
        _ = await fixture.SetLocatorAsync(repository.RepositoryId, 1, "https://github.com/example/one", fixture.Key(111));
        _ = await fixture.SetLocatorAsync(repository.RepositoryId, 2, "https://github.com/example/two", fixture.Key(112));
        _ = await fixture.RenameAsync(repository.RepositoryId, 3, "Four", fixture.Key(113));
        var firstLocator = fixture.ScalarText($"SELECT locator_id FROM local_repository_history WHERE repository_id='{repository.RepositoryId}' AND new_revision=2;");
        var secondLocator = fixture.ScalarText($"SELECT locator_id FROM local_repository_history WHERE repository_id='{repository.RepositoryId}' AND new_revision=3;");
        var orphanRepositoryId = LocalRepositoryCatalogFixture.RepositoryId(900_000);
        fixture.ExecuteUnchecked("""
            DROP TRIGGER IF EXISTS local_repository_history_update_rejected;
            DROP TRIGGER IF EXISTS local_repository_history_delete_rejected;
            DROP TRIGGER IF EXISTS local_repository_locators_update_rejected;
            DROP TRIGGER IF EXISTS local_repository_locator_heads_update_rejected;
            DROP TRIGGER IF EXISTS local_repository_operation_receipts_delete_rejected;
            """);
        switch (corruption)
        {
            case "missing_middle":
                fixture.ExecuteUnchecked($"DELETE FROM local_repository_history WHERE repository_id='{repository.RepositoryId}' AND new_revision=2;");
                break;
            case "noncontiguous":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET previous_revision=1 WHERE repository_id='{repository.RepositoryId}' AND new_revision=3;");
                break;
            case "head_mismatch":
                fixture.ExecuteUnchecked($"UPDATE local_repository_locator_heads SET locator_id='{firstLocator}' WHERE repository_id='{repository.RepositoryId}';");
                break;
            case "orphan_locator":
                Assert.True(GitHubRepositoryLocatorParser.TryParse("https://github.com/example/orphan", out var orphan));
                fixture.ExecuteUnchecked($"""
                    INSERT INTO local_repository_locators
                    VALUES(
                        '{LocalRepositoryCatalogFixture.RepositoryId(900_001)}','{orphanRepositoryId}',
                        'github_repository','{orphan!.CanonicalLocator}','{orphan.LocatorSha256}',
                        'manual','{orphan.DisplayOwner}','{orphan.DisplayRepository}','{LocalRepositoryCatalogFixture.At}');
                    """);
                break;
            case "orphan_history":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET repository_id='{orphanRepositoryId}' WHERE repository_id='{repository.RepositoryId}' AND new_revision=4;");
                break;
            case "orphan_head":
                fixture.ExecuteUnchecked($"UPDATE local_repository_locator_heads SET repository_id='{orphanRepositoryId}' WHERE repository_id='{repository.RepositoryId}';");
                break;
            case "invalid_add":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET action='add_locator' WHERE repository_id='{repository.RepositoryId}' AND new_revision=3;");
                break;
            case "invalid_replace":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET action='replace_locator' WHERE repository_id='{repository.RepositoryId}' AND new_revision=2;");
                break;
            case "wrong_locator_source":
                fixture.ExecuteUnchecked($"UPDATE local_repository_locators SET source='observed' WHERE locator_id='{firstLocator}';");
                break;
            case "missing_cause":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET operation_key=NULL WHERE repository_id='{repository.RepositoryId}' AND new_revision=4;");
                break;
            case "dual_cause":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET context_identity_sha256='{new string('a', 64)}' WHERE repository_id='{repository.RepositoryId}' AND new_revision=4;");
                break;
            case "wrong_cause_kind":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET cause_kind='source_context' WHERE repository_id='{repository.RepositoryId}' AND new_revision=4;");
                break;
            case "unrelated_cause":
                fixture.ExecuteUnchecked($"UPDATE local_repository_history SET operation_key='{fixture.Key(110)}' WHERE repository_id='{repository.RepositoryId}' AND new_revision=4;");
                break;
            case "missing_receipt":
                fixture.ExecuteUnchecked($"DELETE FROM local_repository_operation_receipts WHERE operation_key='{fixture.Key(113)}';");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        AssertInvalid(fixture);
    }

    [Theory]
    [InlineData("persisted_revision_zero")]
    [InlineData("missing_head")]
    [InlineData("missing_history")]
    [InlineData("nonadjacent")]
    [InlineData("before_endpoint")]
    [InlineData("equal_fingerprints")]
    [InlineData("invalid_endpoint")]
    [InlineData("override_revision")]
    [InlineData("override_head")]
    [InlineData("orphan_history")]
    [InlineData("orphan_override")]
    [InlineData("current_head")]
    public async Task AssignmentChainMatrix_RejectsEveryContradiction(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var first = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(120)));
        var second = fixture.Repository(await fixture.CreateAsync("Two", null, fixture.Key(121)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(4_000);
        fixture.CreateSession(sessionId);
        _ = await fixture.SessionActionAsync(sessionId, 0, "assign", first.RepositoryId, fixture.Key(122));
        var orphanSessionId = LocalRepositoryCatalogFixture.SessionId(499_999);
        fixture.ExecuteUnchecked("""
            DROP TRIGGER IF EXISTS session_repository_assignment_history_update_rejected;
            DROP TRIGGER IF EXISTS session_repository_assignment_history_delete_rejected;
            DROP TRIGGER IF EXISTS session_repository_manual_overrides_update_rejected;
            DROP TRIGGER IF EXISTS session_repository_manual_overrides_delete_rejected;
            """);
        switch (corruption)
        {
            case "persisted_revision_zero":
                fixture.ExecuteUnchecked($"UPDATE session_repository_assignment_revisions SET revision=0 WHERE session_id='{sessionId}';");
                break;
            case "missing_head":
                fixture.ExecuteUnchecked($"DELETE FROM session_repository_assignment_revisions WHERE session_id='{sessionId}';");
                break;
            case "missing_history":
                fixture.ExecuteUnchecked($"DELETE FROM session_repository_assignment_history WHERE session_id='{sessionId}';");
                break;
            case "nonadjacent":
                fixture.ExecuteUnchecked($"UPDATE session_repository_assignment_history SET previous_revision=1 WHERE session_id='{sessionId}';");
                break;
            case "before_endpoint":
                fixture.ExecuteUnchecked($"UPDATE session_repository_assignment_history SET previous_state='assigned',previous_authority='manual',previous_repository_id='{first.RepositoryId}' WHERE session_id='{sessionId}';");
                break;
            case "equal_fingerprints":
                fixture.ExecuteUnchecked($"UPDATE session_repository_assignment_history SET new_assignment_state_sha256=previous_assignment_state_sha256 WHERE session_id='{sessionId}';");
                break;
            case "invalid_endpoint":
                fixture.ExecuteUnchecked($"UPDATE session_repository_assignment_history SET new_state='conflict',new_authority='manual',new_repository_id=NULL WHERE session_id='{sessionId}';");
                break;
            case "override_revision":
                fixture.ExecuteUnchecked($"UPDATE session_repository_manual_overrides SET revision=2 WHERE session_id='{sessionId}';");
                break;
            case "override_head":
                fixture.ExecuteUnchecked($"UPDATE session_repository_manual_overrides SET repository_id='{second.RepositoryId}' WHERE session_id='{sessionId}';");
                break;
            case "orphan_history":
                fixture.ExecuteUnchecked($"UPDATE session_repository_assignment_history SET session_id='{orphanSessionId}' WHERE session_id='{sessionId}';");
                break;
            case "orphan_override":
                fixture.ExecuteUnchecked($"UPDATE session_repository_manual_overrides SET session_id='{orphanSessionId}' WHERE session_id='{sessionId}';");
                break;
            case "current_head":
                fixture.ExecuteUnchecked($"DELETE FROM session_repository_manual_overrides WHERE session_id='{sessionId}';");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        AssertInvalid(fixture);
    }

    [Theory]
    [InlineData("automatic_to_manual")]
    [InlineData("automatic_from_manual")]
    [InlineData("assign_nonmanual")]
    [InlineData("unassign_retains_repository")]
    [InlineData("resume_from_nonmanual")]
    [InlineData("automatic_equal")]
    public async Task SharedAssignmentTransitionAuthority_RejectsInvalidManualAndAutomaticTransitions(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            "https://github.com/example/one",
            fixture.Key(130)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(4_100);
        fixture.CreateSession(sessionId);
        if (corruption is "assign_nonmanual" or "resume_from_nonmanual" or "automatic_equal")
        {
            fixture.SeedAutomaticCandidate(sessionId, repository.RepositoryId, 4_101);
        }
        else
        {
            _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(131));
            if (corruption == "automatic_from_manual")
                _ = await fixture.SessionActionAsync(sessionId, 1, "explicitly_unassign", null, fixture.Key(132));
        }
        fixture.ExecuteUnchecked("DROP TRIGGER IF EXISTS session_repository_assignment_history_update_rejected;");
        var userOperationKey = fixture.Key(130);
        switch (corruption)
        {
            case "automatic_to_manual":
                fixture.ExecuteUnchecked($"""
                    UPDATE session_repository_assignment_history
                    SET action='automatic_reconcile',cause_kind='source_reconciliation',operation_key=NULL,
                        reconciliation_fingerprint='{new string('e', 64)}'
                    WHERE session_id='{sessionId}' AND new_revision=1;
                    """);
                break;
            case "automatic_from_manual":
                fixture.ExecuteUnchecked($"""
                    UPDATE session_repository_assignment_history
                    SET action='automatic_reconcile',cause_kind='source_reconciliation',operation_key=NULL,
                        reconciliation_fingerprint='{new string('e', 64)}'
                    WHERE session_id='{sessionId}' AND new_revision=2;
                    """);
                break;
            case "assign_nonmanual":
                fixture.ExecuteUnchecked($"""
                    UPDATE session_repository_assignment_history
                    SET action='assign',cause_kind='user_operation',operation_key='{userOperationKey}',
                        reconciliation_fingerprint=NULL
                    WHERE session_id='{sessionId}' AND new_revision=1;
                    """);
                break;
            case "unassign_retains_repository":
                fixture.ExecuteUnchecked($"UPDATE session_repository_assignment_history SET action='explicitly_unassign' WHERE session_id='{sessionId}' AND new_revision=1;");
                break;
            case "resume_from_nonmanual":
                fixture.ExecuteUnchecked($"""
                    UPDATE session_repository_assignment_history
                    SET action='resume_automatic',cause_kind='user_operation',operation_key='{userOperationKey}',
                        reconciliation_fingerprint=NULL
                    WHERE session_id='{sessionId}' AND new_revision=1;
                    """);
                break;
            case "automatic_equal":
                fixture.ExecuteUnchecked($"""
                    UPDATE session_repository_assignment_history
                    SET new_assignment_state_sha256=previous_assignment_state_sha256
                    WHERE session_id='{sessionId}' AND new_revision=1;
                    """);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task DuplicateOperationKeyLinkedAcrossRepositoryAndAssignmentHistory_IsRejected()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var createKey = fixture.Key(140);
        var assignmentKey = fixture.Key(141);
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, createKey));
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(142));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(4_200);
        fixture.CreateSession(sessionId);
        _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, assignmentKey);
        var assignmentFingerprint = fixture.ScalarText($"SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key='{assignmentKey}';");
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER IF EXISTS session_repository_assignment_history_update_rejected;
            DROP TRIGGER IF EXISTS local_repository_operation_receipts_update_rejected;
            UPDATE session_repository_assignment_history SET operation_key='{createKey}' WHERE session_id='{sessionId}';
            UPDATE local_repository_operation_receipts SET request_fingerprint='{assignmentFingerprint}' WHERE operation_key='{createKey}';
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task ReceiptHistoryLinkRow129_IsRejectedBeforeNotificationAtResident128()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        for (var index = 0; index < 126; index++)
            _ = await fixture.CreateAsync($"Repository {index}", null, OrderedOperationKey(index));
        var sharedKey = OrderedOperationKey(126);
        var repository = fixture.Repository(await fixture.CreateAsync("Shared", null, sharedKey));
        var renameKey = OrderedOperationKey(1, high: true);
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Shared renamed", renameKey);
        var sessionId = LocalRepositoryCatalogFixture.SessionId(4_300);
        fixture.CreateSession(sessionId);
        var assignmentKey = OrderedOperationKey(2, high: true);
        _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, assignmentKey);
        var assignmentFingerprint = fixture.ScalarText($"SELECT request_fingerprint FROM local_repository_operation_receipts WHERE operation_key='{assignmentKey}';");
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER IF EXISTS session_repository_assignment_history_update_rejected;
            DROP TRIGGER IF EXISTS local_repository_operation_receipts_update_rejected;
            UPDATE session_repository_assignment_history SET operation_key='{sharedKey}' WHERE session_id='{sessionId}';
            UPDATE local_repository_operation_receipts SET request_fingerprint='{assignmentFingerprint}' WHERE operation_key='{sharedKey}';
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        Assert.Equal(128, observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.ReceiptHistoryLinkPage)
            .Max(static item => item.Count));
        Assert.DoesNotContain(
            observer.Counts,
            item => item.Buffer == LocalRepositoryMutationValidationBuffer.ReceiptHistoryLinkPage && item.Count > 128);
    }

    [Theory]
    [InlineData("status")]
    [InlineData("content_type")]
    [InlineData("cache_control")]
    [InlineData("text_entity")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("noncanonical")]
    [InlineData("opposite_kind")]
    public async Task ReceiptEnvelopeAndExactDecoderMatrix_RejectsBeforeFingerprintUse(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(150)));
        var key = fixture.Key(151);
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", key);
        var assignment = corruption switch
        {
            "status" => "status_code=201",
            "content_type" => "content_type='application/json'",
            "cache_control" => "cache_control='public'",
            "text_entity" => "response_entity=CAST(response_entity AS TEXT)",
            "oversized" => "response_entity=zeroblob(16385)",
            "malformed" => "response_entity=X'7B7D'",
            "noncanonical" => "response_entity=X'207B7D'",
            "opposite_kind" => $"response_entity=X'{Convert.ToHexString(LocalRepositoryCatalogFixture.AssignmentEntity(new(
                LocalRepositoryCatalogFixture.SessionId(4_400), 0, "unassigned", "none", null, [], null)).Span)}'",
            _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
        };
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER IF EXISTS local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts SET {assignment} WHERE operation_key='{key}';
            """);
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        Assert.Empty(observer.Checkpoints);
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("target")]
    [InlineData("revision")]
    [InlineData("status_action")]
    [InlineData("assignment_state")]
    [InlineData("missing_receipt")]
    public async Task LinkedReceiptCorrelationMatrix_RejectsWrongDurableFacts(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(160)));
        string key;
        if (corruption == "assignment_state")
        {
            var sessionId = LocalRepositoryCatalogFixture.SessionId(4_500);
            fixture.CreateSession(sessionId);
            key = fixture.Key(161);
            _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, key);
            var wrongAssignment = new LocalRepositoryMutationAssignment(
                sessionId,
                1,
                "explicitly_unassigned",
                "manual",
                null,
                [],
                DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", System.Globalization.CultureInfo.InvariantCulture));
            UpdateReceiptEntity(fixture, key, LocalRepositoryCatalogFixture.AssignmentEntity(wrongAssignment));
        }
        else
        {
            key = fixture.Key(162);
            _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", key);
            switch (corruption)
            {
                case "kind":
                    UpdateReceiptEntity(fixture, key, LocalRepositoryCatalogFixture.AssignmentEntity(new(
                        LocalRepositoryCatalogFixture.SessionId(4_501), 0, "unassigned", "none", null, [], null)));
                    break;
                case "target":
                    UpdateReceiptEntity(fixture, key, LocalRepositoryCatalogFixture.RepositoryEntity(
                        repository with { RepositoryId = LocalRepositoryCatalogFixture.RepositoryId(910_000), DisplayName = "Two", Revision = 2 }));
                    break;
                case "revision":
                    UpdateReceiptEntity(fixture, key, LocalRepositoryCatalogFixture.RepositoryEntity(
                        repository with { DisplayName = "Two", Revision = 3 }));
                    break;
                case "status_action":
                    fixture.ExecuteUnchecked($"""
                        DROP TRIGGER IF EXISTS local_repository_operation_receipts_update_rejected;
                        UPDATE local_repository_operation_receipts SET status_code=201 WHERE operation_key='{key}';
                        """);
                    break;
                case "missing_receipt":
                    fixture.ExecuteUnchecked($"""
                        DROP TRIGGER IF EXISTS local_repository_operation_receipts_delete_rejected;
                        DELETE FROM local_repository_operation_receipts WHERE operation_key='{key}';
                        """);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption));
            }
        }

        AssertInvalid(fixture);
    }

    [Theory]
    [InlineData("create")]
    [InlineData("rename")]
    [InlineData("locator_add")]
    [InlineData("locator_replace")]
    [InlineData("assign")]
    [InlineData("explicitly_unassign")]
    [InlineData("resume_automatic")]
    public async Task EveryProvableOperationFingerprintArm_IsRecomputed(string arm)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var createKey = fixture.Key(170);
        var repository = fixture.Repository(await fixture.CreateAsync(
            "One",
            arm == "locator_replace" ? "https://github.com/example/one" : null,
            createKey));
        var targetKey = createKey;
        switch (arm)
        {
            case "create":
                break;
            case "rename":
                targetKey = fixture.Key(171);
                _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", targetKey);
                break;
            case "locator_add":
            case "locator_replace":
                targetKey = fixture.Key(172);
                _ = await fixture.SetLocatorAsync(repository.RepositoryId, 1, "https://github.com/example/two", targetKey);
                break;
            case "assign":
            case "explicitly_unassign":
            case "resume_automatic":
                var sessionId = LocalRepositoryCatalogFixture.SessionId(4_600);
                fixture.CreateSession(sessionId);
                var assignKey = fixture.Key(173);
                _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, assignKey);
                if (arm == "assign")
                {
                    targetKey = assignKey;
                }
                else if (arm == "explicitly_unassign")
                {
                    targetKey = fixture.Key(174);
                    _ = await fixture.SessionActionAsync(sessionId, 1, "explicitly_unassign", null, targetKey);
                }
                else
                {
                    targetKey = fixture.Key(175);
                    _ = await fixture.SessionActionAsync(sessionId, 1, "resume_automatic", null, targetKey);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(arm));
        }
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER IF EXISTS local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts SET request_fingerprint='{new string('a', 64)}' WHERE operation_key='{targetKey}';
            """);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task UnlinkedNoOpFingerprint_IsNotRecomputed()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(180)));
        var noOpKey = fixture.Key(181);
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "One", noOpKey);
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER IF EXISTS local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts SET request_fingerprint='{new string('a', 64)}' WHERE operation_key='{noOpKey}';
            """);

        Validate(fixture);
    }

    [Fact]
    public async Task StaleAssignmentNoOpReceipt_ValidatesAgainstHistoricalStateAfterTheHeadAdvances()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(182)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(4_700);
        fixture.CreateSession(sessionId);
        _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(183));
        _ = await fixture.SessionActionAsync(sessionId, 1, "assign", repository.RepositoryId, fixture.Key(184));
        _ = await fixture.SessionActionAsync(sessionId, 1, "explicitly_unassign", null, fixture.Key(185));

        Validate(fixture);
    }

    [Theory]
    [InlineData("missing_repository")]
    [InlineData("future_repository_revision")]
    [InlineData("missing_assignment")]
    [InlineData("missing_positive_assignment_revision")]
    [InlineData("wrong_assignment_state")]
    public async Task UnlinkedNoOpReceiptBounds_RejectImpossibleTargetsRevisionsAndStates(string corruption)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(190)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(4_800);
        fixture.CreateSession(sessionId);
        ReadOnlyMemory<byte> entity;
        switch (corruption)
        {
            case "missing_repository":
                entity = LocalRepositoryCatalogFixture.RepositoryEntity(
                    repository with { RepositoryId = LocalRepositoryCatalogFixture.RepositoryId(920_000) });
                break;
            case "future_repository_revision":
                entity = LocalRepositoryCatalogFixture.RepositoryEntity(repository with { Revision = 2 });
                break;
            case "missing_assignment":
                entity = LocalRepositoryCatalogFixture.AssignmentEntity(new(
                    LocalRepositoryCatalogFixture.SessionId(920_001), 0, "unassigned", "none", null, [], null));
                break;
            case "missing_positive_assignment_revision":
                entity = LocalRepositoryCatalogFixture.AssignmentEntity(new(
                    sessionId, 1, "assigned", "manual", repository.RepositoryId, [],
                    DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", System.Globalization.CultureInfo.InvariantCulture)));
                break;
            case "wrong_assignment_state":
                _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(191));
                entity = LocalRepositoryCatalogFixture.AssignmentEntity(new(
                    sessionId, 1, "explicitly_unassigned", "manual", null, [],
                    DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", System.Globalization.CultureInfo.InvariantCulture)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(corruption));
        }
        InsertReceipt(fixture, OperationKey(4_800), 200, entity);

        AssertInvalid(fixture);
    }

    [Fact]
    public async Task UnlinkedNoOpReceiptBounds_AcceptRepositoryRevision1AndAssignmentRevision0AndPositive()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(192)));
        var sessionId = LocalRepositoryCatalogFixture.SessionId(4_900);
        fixture.CreateSession(sessionId);
        InsertReceipt(fixture, OperationKey(4_901), 200, LocalRepositoryCatalogFixture.RepositoryEntity(repository));
        InsertReceipt(fixture, OperationKey(4_902), 200, LocalRepositoryCatalogFixture.AssignmentEntity(new(
            sessionId, 0, "unassigned", "none", null, [], null)));
        _ = await fixture.SessionActionAsync(sessionId, 0, "assign", repository.RepositoryId, fixture.Key(193));
        InsertReceipt(fixture, OperationKey(4_903), 200, LocalRepositoryCatalogFixture.AssignmentEntity(new(
            sessionId, 1, "assigned", "manual", repository.RepositoryId, [],
            DateTimeOffset.ParseExact(LocalRepositoryCatalogFixture.At, "O", System.Globalization.CultureInfo.InvariantCulture))));

        Validate(fixture);
    }

    [Fact]
    public async Task ReceiptHistoryLinkCorrelation_LaterReceiptPageContradictionIsNotOmitted()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        LocalRepositoryMutationRepository? lastRepository = null;
        for (var index = 0; index < 129; index++)
        {
            lastRepository = fixture.Repository(await fixture.CreateAsync(
                $"Repository {index}",
                null,
                OrderedOperationKey(index)));
        }
        UpdateReceiptEntity(
            fixture,
            OrderedOperationKey(128),
            LocalRepositoryCatalogFixture.RepositoryEntity(lastRepository! with
            {
                RepositoryId = LocalRepositoryCatalogFixture.RepositoryId(930_000),
            }));
        var observer = new RecordingObserver();

        AssertInvalid(fixture, observer);

        var receiptCounts = observer.Counts
            .Where(item => item.Buffer == LocalRepositoryMutationValidationBuffer.ReceiptPage)
            .Select(static item => item.Count)
            .ToArray();
        Assert.Equal(129, receiptCounts.Length);
        Assert.Equal(1, receiptCounts[^1]);
        Assert.Equal(129, observer.Counts.Count(
            item => item.Buffer == LocalRepositoryMutationValidationBuffer.ReceiptHistoryLinkPage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Validation_ValidAndCorruptPathsLeaveBackupDigestAndDatabaseCountsUnchanged(bool corrupt)
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        var repository = fixture.Repository(await fixture.CreateAsync("One", null, fixture.Key(194)));
        _ = await fixture.RenameAsync(repository.RepositoryId, 1, "Two", fixture.Key(195));
        if (corrupt)
        {
            fixture.ExecuteUnchecked($"""
                DROP TRIGGER IF EXISTS local_repository_history_update_rejected;
                UPDATE local_repository_history SET operation_key=NULL
                WHERE repository_id='{repository.RepositoryId}' AND new_revision=2;
                """);
        }
        var beforeDigest = BackupDigest(fixture);
        var beforeCounts = DatabaseObjectAndRowCounts(fixture);
        using (var connection = fixture.Open())
        using (var transaction = connection.BeginTransaction())
        {
            if (corrupt)
            {
                var error = Assert.Throws<InvalidOperationException>(() =>
                    SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, transaction));
                Assert.Equal("local_repository_mutation_state_invalid", error.Message);
                Assert.Null(error.InnerException);
            }
            else
            {
                SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, transaction);
            }
            transaction.Rollback();
        }

        Assert.Equal(beforeDigest, BackupDigest(fixture));
        Assert.Equal(beforeCounts, DatabaseObjectAndRowCounts(fixture));
    }

    [Fact]
    public void CallerContractFailures_AreDistinctFromPersistedCorruption()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        using var connection = fixture.Open();
        using var transaction = connection.BeginTransaction();
        using var other = fixture.Open();

        Assert.Throws<ArgumentNullException>(() =>
            SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(null!, transaction));
        Assert.Throws<ArgumentNullException>(() =>
            SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, null!));
        var mismatch = Assert.Throws<InvalidOperationException>(() =>
            SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(other, transaction));

        Assert.Equal("local_repository_mutation_state_transaction_mismatch", mismatch.Message);
    }

    [Fact]
    public void OperationalSqliteFailure_EscapesUntranslated()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        fixture.ExecuteUnchecked("DROP TABLE local_repository_operation_receipts;");

        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => Validate(fixture));
    }

    [Fact]
    public async Task Validation_IsReadOnlyAndLeavesTheCallerTransactionUsable()
    {
        using var fixture = new LocalRepositoryCatalogFixture();
        _ = await fixture.CreateAsync("One", null, fixture.Key(90));
        using var connection = fixture.Open();
        using var transaction = connection.BeginTransaction();
        var before = ScalarLong(connection, transaction, "SELECT total_changes();");

        SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, transaction);

        Assert.Equal(before, ScalarLong(connection, transaction, "SELECT total_changes();"));
        transaction.Rollback();
    }

    private static void Validate(LocalRepositoryCatalogFixture fixture)
    {
        using var connection = fixture.Open();
        using var transaction = connection.BeginTransaction();
        SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, transaction);
    }

    private static void Validate(LocalRepositoryCatalogFixture fixture, ILocalRepositoryMutationValidationObserver observer)
    {
        using var connection = fixture.Open();
        using var transaction = connection.BeginTransaction();
        SqliteLocalRepositoryCatalogStore.ValidateRestorableMutationState(connection, transaction, observer);
    }

    private static void AssertInvalid(LocalRepositoryCatalogFixture fixture, ILocalRepositoryMutationValidationObserver? observer = null)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            if (observer is null)
                Validate(fixture);
            else
                Validate(fixture, observer);
        });
        Assert.Equal("local_repository_mutation_state_invalid", error.Message);
        Assert.Null(error.InnerException);
    }

    private static long ScalarLong(Microsoft.Data.Sqlite.SqliteConnection connection, Microsoft.Data.Sqlite.SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string OperationKey(int value) => "lrc1_" + Convert.ToBase64String(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"mutation-validation-{value}")))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string OrderedOperationKey(int value, bool high = false)
    {
        var bytes = Enumerable.Repeat(high ? (byte)0xff : (byte)0x00, 32).ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(28), value);
        return "lrc1_" + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void InsertReceipt(
        LocalRepositoryCatalogFixture fixture,
        string operationKey,
        int statusCode,
        ReadOnlyMemory<byte> responseEntity,
        string? requestFingerprint = null)
    {
        fixture.ExecuteUnchecked($"""
            INSERT INTO local_repository_operation_receipts(
                operation_key,request_fingerprint,status_code,content_type,cache_control,response_entity,created_at)
            VALUES(
                '{operationKey}','{requestFingerprint ?? new string('d', 64)}',{statusCode},
                'application/json; charset=utf-8','no-store',X'{Convert.ToHexString(responseEntity.Span)}',
                '{LocalRepositoryCatalogFixture.At}');
            """);
    }

    private static void UpdateReceiptEntity(
        LocalRepositoryCatalogFixture fixture,
        string operationKey,
        ReadOnlyMemory<byte> responseEntity)
    {
        fixture.ExecuteUnchecked($"""
            DROP TRIGGER IF EXISTS local_repository_operation_receipts_update_rejected;
            UPDATE local_repository_operation_receipts
            SET response_entity=X'{Convert.ToHexString(responseEntity.Span)}'
            WHERE operation_key='{operationKey}';
            """);
    }

    private static void CreateDistinctSession(LocalRepositoryCatalogFixture fixture, string sessionId, int value)
    {
        var runId = LocalRepositoryCatalogFixture.RepositoryId(800_000 + value * 2);
        var eventId = LocalRepositoryCatalogFixture.RepositoryId(800_001 + value * 2);
        var traceId = value.ToString("x32", System.Globalization.CultureInfo.InvariantCulture);
        var spanId = value.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        fixture.Execute($"""
            INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
            VALUES('{sessionId}','completed','full','{LocalRepositoryCatalogFixture.At}','not_captured','{LocalRepositoryCatalogFixture.At}','{LocalRepositoryCatalogFixture.At}');
            INSERT INTO session_runs(run_id,session_id,source_surface,status)
            VALUES('{runId}','{sessionId}','vscode','completed');
            INSERT INTO session_events(
                event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,
                content_state,source_application_version)
            VALUES(
                '{eventId}','{sessionId}','{runId}','vscode','otel-exact','{traceId}/{spanId}',
                'otel.span','{LocalRepositoryCatalogFixture.At}','not_captured','1.2.3');
            """);
    }

    private static string BackupDigest(LocalRepositoryCatalogFixture fixture)
    {
        var backupPath = Path.Combine(Path.GetTempPath(), $"mutation-validation-{Guid.NewGuid():N}.db");
        try
        {
            using (var source = fixture.Open())
            using (var destination = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = backupPath,
                    Pooling = false,
                }.ToString()))
            {
                destination.Open();
                source.BackupDatabase(destination);
            }
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(backupPath)));
        }
        finally
        {
            if (File.Exists(backupPath))
                File.Delete(backupPath);
        }
    }

    private static string[] DatabaseObjectAndRowCounts(LocalRepositoryCatalogFixture fixture)
    {
        using var connection = fixture.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type,name,tbl_name,COALESCE(sql,'')
            FROM sqlite_schema
            ORDER BY type COLLATE BINARY,name COLLATE BINARY;
            """;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        var tables = new List<string>();
        while (reader.Read())
        {
            var type = reader.GetString(0);
            var name = reader.GetString(1);
            values.Add($"{type}|{name}|{reader.GetString(2)}|{reader.GetString(3)}");
            if (type == "table")
                tables.Add(name);
        }
        reader.Close();
        foreach (var table in tables.Order(StringComparer.Ordinal))
        {
            using var count = connection.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\";";
            values.Add($"rows|{table}|{Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return values.ToArray();
    }

    private sealed class RecordingObserver : ILocalRepositoryMutationValidationObserver
    {
        internal List<(LocalRepositoryMutationValidationBuffer Buffer, int Count)> Counts { get; } = [];
        internal List<LocalRepositoryMutationValidationCheckpoint> Checkpoints { get; } = [];

        public void Materialized(LocalRepositoryMutationValidationBuffer buffer, int count) => Counts.Add((buffer, count));

        public void Reached(LocalRepositoryMutationValidationCheckpoint checkpoint) => Checkpoints.Add(checkpoint);
    }
}

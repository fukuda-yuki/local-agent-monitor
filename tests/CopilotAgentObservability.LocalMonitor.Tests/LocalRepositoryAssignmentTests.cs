using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Repositories;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryAssignmentTests
{
    private const string At = "2026-08-01T00:00:00.0000000+00:00";
    private const string Later = "2026-08-01T01:00:00.0000000+00:00";
    private const string SessionOne = "01900000-0000-7000-8000-000000000101";
    private const string SessionTwo = "01900000-0000-7000-8000-000000000102";
    private const string RfcByteFirstRepository = "00010000-0000-7000-8000-000000000002";
    private const string LittleEndianFirstRepository = "01000000-0000-7000-8000-000000000001";
    private const string ThirdRepository = "02000000-0000-7000-8000-000000000003";
    private const string UnassignedFingerprint = "b321e722b4705092a9cf43264ea57893e988ef52dccb894108bcd2b33993d455";
    private const string RfcByteFirstAssignedFingerprint = "62dbed4e3539db62427958d78a82c8e428919d998a480cb0db473f51deaa3f49";
    private const string TwoCandidateConflictFingerprint = "12fcb9f910d4f9a88c18f4619b9d1cb371c9ad583c7191f5e7fb32d28af89f73";
    private const string ThreeCandidateConflictFingerprint = "e8fad5335f5e286bb5400bce9521770f887c39b72764c39a277d312135484f05";
    private const string ManualAssignedFingerprint = "a6e12441dab08a4f6b5704897b94a288e99c875c475aea330e3d87b0224498f8";
    private const string ManualUnassignedFingerprint = "a5d77abeb67afa8f1f05f2b83cdff6d347b7a4e4a3014f1357c29e6b5a79d71d";
    private const string WrongManualFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void RevisionZeroAbsence_ResolvesUnassignedWithoutCreatingRevisionOrHistoryRows()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        var resolver = new LocalRepositoryAssignmentResolver();

        LocalRepositoryAssignmentReconcileResult result;
        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                10,
                [SessionOne],
                [],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            result = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            transaction.Commit();
        }

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal(LocalRepositoryAssignmentReconcileStatus.Applied, result.Status);
        Assert.Equal("unassigned", resolution.State);
        Assert.Equal("none", resolution.Authority);
        Assert.Null(resolution.RepositoryId);
        Assert.Empty(resolution.AutomaticCandidateRepositoryIds);
        Assert.Equal(0, resolution.PreviousRevision);
        Assert.Equal(0, resolution.NewRevision);
        Assert.Equal(UnassignedFingerprint, resolution.PreviousAssignmentStateSha256);
        Assert.Equal(UnassignedFingerprint, resolution.NewAssignmentStateSha256);
        Assert.False(resolution.RevisionChanged);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
    }

    [Fact]
    public void AutomaticReconcile_AssignsOneCandidateAndStoresTheExactSourceCauseAndFingerprints()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.DefineRepository(RfcByteFirstRepository, 1);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 10);
        var resolver = new LocalRepositoryAssignmentResolver();

        LocalRepositoryAssignmentReconcileResult result;
        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                context.RawRecordId,
                [SessionOne],
                [context.Assignment],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(LocalRepositoryAssignmentReconcileStatus.Applied, preparation.Status);
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
            Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
            fixture.PublishRepository(RfcByteFirstRepository, transaction);
            fixture.PublishAdmittedContext(context, transaction);
            result = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            transaction.Commit();
        }

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("assigned", resolution.State);
        Assert.Equal("automatic", resolution.Authority);
        Assert.Equal(RfcByteFirstRepository, resolution.RepositoryId);
        Assert.Equal([RfcByteFirstRepository], resolution.AutomaticCandidateRepositoryIds);
        Assert.Equal(0, resolution.PreviousRevision);
        Assert.Equal(1, resolution.NewRevision);
        Assert.Equal(UnassignedFingerprint, resolution.PreviousAssignmentStateSha256);
        Assert.Equal(RfcByteFirstAssignedFingerprint, resolution.NewAssignmentStateSha256);
        Assert.True(resolution.RevisionChanged);
        Assert.Equal(1, fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{SessionOne}';"));
        Assert.Equal(
            $"automatic_reconcile|0|1|{UnassignedFingerprint}|{RfcByteFirstAssignedFingerprint}|unassigned|assigned|none|automatic|source_reconciliation|{fixture.ReconciliationFingerprint}",
            fixture.ScalarText("""
                SELECT action||'|'||previous_revision||'|'||new_revision||'|'||previous_assignment_state_sha256||'|'||new_assignment_state_sha256||'|'||previous_state||'|'||new_state||'|'||previous_authority||'|'||new_authority||'|'||cause_kind||'|'||reconciliation_fingerprint
                FROM session_repository_assignment_history;
                """));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history WHERE operation_key IS NULL;"));
        LocalRepositoryCatalogValidation.Validate(fixture.Connection, transaction: null);
    }

    [Fact]
    public void AutomaticReconcile_DeduplicatesAndSortsCandidatesByCanonicalUuidBytes()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        var firstContext = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 10, attributeOrdinal: 0);
        var secondContext = fixture.PrepareAdmittedContext(SessionOne, LittleEndianFirstRepository, 10, attributeOrdinal: 1);
        var resolver = new LocalRepositoryAssignmentResolver();

        LocalRepositoryAssignmentReconcileResult result;
        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                10,
                [SessionOne],
                [
                    secondContext.Assignment,
                    firstContext.Assignment,
                    secondContext.Assignment,
                ],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            fixture.PublishAdmittedContext(secondContext, transaction);
            fixture.PublishAdmittedContext(firstContext, transaction);
            result = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            transaction.Commit();
        }

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal("conflict", resolution.State);
        Assert.Equal("automatic", resolution.Authority);
        Assert.Null(resolution.RepositoryId);
        Assert.Equal([RfcByteFirstRepository, LittleEndianFirstRepository], resolution.AutomaticCandidateRepositoryIds);
        Assert.Equal(TwoCandidateConflictFingerprint, resolution.NewAssignmentStateSha256);
    }

    [Fact]
    public void ConflictCandidateSetChange_IncrementsRevisionWhileDuplicateEvidenceAndReplayAreNoOps()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        fixture.CreateRepository(ThirdRepository, 3);
        var initialFirst = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 10, attributeOrdinal: 0);
        var initialSecond = fixture.PrepareAdmittedContext(SessionOne, LittleEndianFirstRepository, 10, attributeOrdinal: 1);
        var duplicateContext = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 11);
        var third = fixture.PrepareAdmittedContext(SessionOne, ThirdRepository, 12);
        var resolver = new LocalRepositoryAssignmentResolver();

        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                10,
                [SessionOne],
                [initialSecond.Assignment, initialFirst.Assignment],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(At, System.Globalization.CultureInfo.InvariantCulture));
            fixture.PublishAdmittedContext(initialSecond, transaction);
            fixture.PublishAdmittedContext(initialFirst, transaction);
            var initial = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            Assert.True(Assert.Single(initial.Resolutions).RevisionChanged);
            transaction.Commit();
        }

        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                duplicateContext.RawRecordId,
                [SessionOne],
                [duplicateContext.Assignment],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            fixture.PublishAdmittedContext(duplicateContext, transaction);
            var duplicate = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            var resolution = Assert.Single(duplicate.Resolutions);
            Assert.False(resolution.RevisionChanged);
            Assert.Equal(1, resolution.NewRevision);
            Assert.Equal(TwoCandidateConflictFingerprint, resolution.PreviousAssignmentStateSha256);
            Assert.Equal(TwoCandidateConflictFingerprint, resolution.NewAssignmentStateSha256);
            transaction.Commit();
        }

        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                third.RawRecordId,
                [SessionOne],
                [third.Assignment],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            fixture.PublishAdmittedContext(third, transaction);
            var changed = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            var resolution = Assert.Single(changed.Resolutions);
            Assert.True(resolution.RevisionChanged);
            Assert.Equal(1, resolution.PreviousRevision);
            Assert.Equal(2, resolution.NewRevision);
            Assert.Equal(TwoCandidateConflictFingerprint, resolution.PreviousAssignmentStateSha256);
            Assert.Equal(ThreeCandidateConflictFingerprint, resolution.NewAssignmentStateSha256);
            transaction.Commit();
        }

        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                third.RawRecordId,
                [SessionOne],
                [third.Assignment],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            var replay = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            Assert.False(Assert.Single(replay.Resolutions).RevisionChanged);
            transaction.Commit();
        }

        Assert.Equal(2, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
        Assert.Equal(2, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        Assert.Equal(4, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Theory]
    [InlineData("assigned", LittleEndianFirstRepository, "assigned", ManualAssignedFingerprint)]
    [InlineData("explicitly_unassigned", null, "explicitly_unassigned", ManualUnassignedFingerprint)]
    public void ManualOverride_ShieldsTheEffectiveFingerprintAndRevisionWhileAutomaticEvidenceIsRetained(
        string overrideState,
        string? overrideRepositoryId,
        string expectedState,
        string expectedFingerprint)
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        fixture.CreateManualOverride(SessionOne, overrideState, overrideRepositoryId, expectedFingerprint);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 10);
        var resolver = new LocalRepositoryAssignmentResolver();

        LocalRepositoryAssignmentReconcileResult result;
        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                context.RawRecordId,
                [SessionOne],
                [context.Assignment],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            fixture.PublishAdmittedContext(context, transaction);
            result = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            transaction.Commit();
        }

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal(expectedState, resolution.State);
        Assert.Equal("manual", resolution.Authority);
        Assert.Equal(overrideRepositoryId, resolution.RepositoryId);
        Assert.Equal([RfcByteFirstRepository], resolution.AutomaticCandidateRepositoryIds);
        Assert.Equal(expectedFingerprint, resolution.PreviousAssignmentStateSha256);
        Assert.Equal(expectedFingerprint, resolution.NewAssignmentStateSha256);
        Assert.Equal(1, resolution.PreviousRevision);
        Assert.Equal(1, resolution.NewRevision);
        Assert.False(resolution.RevisionChanged);
        Assert.Equal(At, fixture.ScalarText("SELECT updated_at FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history WHERE cause_kind='source_reconciliation';"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Theory]
    [InlineData("assigned", LittleEndianFirstRepository)]
    [InlineData("explicitly_unassigned", null)]
    public void PrepareAutomatic_RejectsAWellFormedButSemanticallyWrongManualHistoryHeadFingerprint(
        string overrideState,
        string? overrideRepositoryId)
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        fixture.CreateManualOverride(SessionOne, overrideState, overrideRepositoryId, WrongManualFingerprint);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 9);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Equal(1, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
        transaction.Rollback();
    }

    [Fact]
    public void ApplyAutomatic_RejectsAWellFormedButSemanticallyWrongEarlierManualHistoryEndpoint()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        var resolver = new LocalRepositoryAssignmentResolver();
        EstablishAutomaticAssignment(fixture, resolver, SessionOne, RfcByteFirstRepository, 10);
        fixture.CreateHistoricalManualRoundTripWithWrongFingerprint(
            SessionOne,
            RfcByteFirstRepository,
            LittleEndianFirstRepository);
        var context = fixture.PrepareAdmittedContext(SessionOne, LittleEndianFirstRepository, 11);

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(context, transaction);

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(3, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
        Assert.Equal(3, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history WHERE new_revision=4;"));
        transaction.Rollback();
    }

    [Fact]
    public void CandidateBound_AcceptsExactly128DistinctRepositories()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        var candidates = CreateCandidateRepositoryIds(128);
        for (var index = 0; index < candidates.Length; index++)
            fixture.DefineRepository(candidates[index], index + 100);
        var contexts = candidates.Select((repositoryId, index) =>
            fixture.PrepareAdmittedContext(SessionOne, repositoryId, 10, index)).ToArray();
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            10,
            [SessionOne],
            contexts.Select(static context => context.Assignment).ToArray(),
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        for (var index = 0; index < candidates.Length; index++)
        {
            fixture.PublishRepository(candidates[index], transaction);
            fixture.PublishAdmittedContext(contexts[index], transaction);
        }
        var result = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
        transaction.Commit();

        var resolution = Assert.Single(result.Resolutions);
        Assert.Equal(LocalRepositoryAssignmentReconcileStatus.Applied, result.Status);
        Assert.Equal(128, resolution.AutomaticCandidateRepositoryIds.Count);
        Assert.Equal("conflict", resolution.State);
        Assert.Equal(1, resolution.NewRevision);
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
    }

    [Fact]
    public void CandidateBound_Rejects129BeforeWritingAnySessionInTheBatch()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateSession(SessionTwo);
        fixture.DefineRepository(RfcByteFirstRepository, 1);
        var excessive = CreateCandidateRepositoryIds(129, start: 1000);
        for (var index = 0; index < excessive.Length; index++)
            fixture.DefineRepository(excessive[index], index + 1000);
        var prospective = new List<PreparedContext>
        {
            fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 10, attributeOrdinal: 0),
        };
        prospective.AddRange(excessive.Select((repositoryId, index) =>
            fixture.PrepareAdmittedContext(SessionTwo, repositoryId, 10, index + 1)));
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            10,
            [SessionOne, SessionTwo],
            prospective.Select(static context => context.Assignment).ToArray(),
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        transaction.Commit();

        Assert.Equal(LocalRepositoryAssignmentReconcileStatus.CardinalityExceeded, preparation.Status);
        Assert.Equal(SessionTwo, preparation.RejectedSessionId);
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM local_repositories;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Fact]
    public void PrepareAndApply_RollBackWithTheCallerTransactionAndRejectMismatchedTransactions()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 10);
        var resolver = new LocalRepositoryAssignmentResolver();

        using (var transaction = fixture.Connection.BeginTransaction(deferred: false))
        {
            var preparation = resolver.PrepareAutomatic(
                fixture.Connection,
                transaction,
                context.RawRecordId,
                [SessionOne],
                [context.Assignment],
                fixture.ReconciliationFingerprint,
                DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
            fixture.PublishAdmittedContext(context, transaction);
            var result = resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
            Assert.True(Assert.Single(result.Resolutions).RevisionChanged);
            transaction.Rollback();
        }

        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));

        using var otherConnection = CatalogFixture.Open(fixture.Path);
        using var ownerTransaction = fixture.Connection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => resolver.PrepareAutomatic(
            otherConnection,
            ownerTransaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture)));
        var stalePreparation = resolver.PrepareAutomatic(
            fixture.Connection,
            ownerTransaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        ownerTransaction.Rollback();
        using var otherTransaction = otherConnection.BeginTransaction(deferred: false);
        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(otherConnection, otherTransaction, stalePreparation));
        otherTransaction.Rollback();
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong_repository")]
    [InlineData("wrong_locator")]
    public void ApplyAutomatic_RejectsMissingOrOwnershipDriftedExpectedContextBeforeAssignmentWrites(string mutation)
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        var alternateLocatorId = fixture.CreateAdditionalLocator(RfcByteFirstRepository, 3);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 20);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        if (mutation == "wrong_repository")
        {
            fixture.PublishAdmittedContext(
                context,
                transaction,
                actualRepositoryId: LittleEndianFirstRepository,
                actualLocatorId: fixture.LocatorId(LittleEndianFirstRepository));
        }
        else if (mutation == "wrong_locator")
        {
            fixture.PublishAdmittedContext(context, transaction, actualLocatorId: alternateLocatorId);
        }

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApplyAutomatic_RejectsUnexpectedContextIncludingAnUnpreparedAffectedSession(bool useUnexpectedSession)
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateSession(SessionTwo);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        var expected = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 21, attributeOrdinal: 0);
        var unexpected = fixture.PrepareAdmittedContext(
            useUnexpectedSession ? SessionTwo : SessionOne,
            LittleEndianFirstRepository,
            21,
            attributeOrdinal: 1);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            21,
            [SessionOne],
            [expected.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(expected, transaction);
        fixture.PublishAdmittedContext(unexpected, transaction);

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Fact]
    public void ApplyAutomatic_RejectsCandidateFrontierAddedOutsideThePreparedRawGraph()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        var expected = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 22);
        var unexpected = fixture.PrepareAdmittedContext(SessionOne, LittleEndianFirstRepository, 23);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            expected.RawRecordId,
            [SessionOne],
            [expected.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(expected, transaction);
        fixture.PublishAdmittedContext(unexpected, transaction);

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Fact]
    public void ApplyAutomatic_RejectsAnExtraContextOutsideTheRawGraphEvenWhenTheCandidateSetIsUnchanged()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        var expected = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 27);
        var unexpectedDuplicateEvidence = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 28);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            expected.RawRecordId,
            [SessionOne],
            [expected.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(expected, transaction);
        fixture.PublishAdmittedContext(unexpectedDuplicateEvidence, transaction);

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Fact]
    public void ApplyAutomatic_RejectsAContextIdentityThatDoesNotMatchItsDurableObservation()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 29);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(context, transaction, actualSourceIdentity: new string('f', 64));

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Fact]
    public void ApplyAutomatic_RejectsA129thActualCandidateBeforeAssignmentWrites()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        var candidates = CreateCandidateRepositoryIds(129, start: 2000);
        for (var index = 0; index < candidates.Length; index++)
            fixture.CreateRepository(candidates[index], index + 2000);
        var contexts = candidates.Select((repositoryId, index) =>
            fixture.PrepareAdmittedContext(SessionOne, repositoryId, 24, index)).ToArray();
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            24,
            [SessionOne],
            contexts.Take(128).Select(static context => context.Assignment).ToArray(),
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        foreach (var context in contexts)
            fixture.PublishAdmittedContext(context, transaction);

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_observation_contexts;"));
    }

    [Fact]
    public void ApplyAutomatic_RejectsPreparationProducedByAnotherResolver()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 25);
        var owner = new LocalRepositoryAssignmentResolver();
        var other = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = owner.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(context, transaction);

        Assert.Throws<InvalidOperationException>(() => other.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Fact]
    public void ManualOverride_RejectsOmittedPreparedEvidenceInsteadOfReportingItAsRetained()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        fixture.CreateManualOverride(SessionOne, "assigned", LittleEndianFirstRepository, ManualAssignedFingerprint);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 26);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(1, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Theory]
    [InlineData("changed_revision")]
    [InlineData("deleted_revision")]
    [InlineData("deleted_head")]
    [InlineData("changed_head_fingerprint")]
    [InlineData("changed_head_state")]
    public void ApplyAutomatic_RejectsRevisionOrHistoryHeadDriftBeforeAppendingTheChain(string mutation)
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        var resolver = new LocalRepositoryAssignmentResolver();
        EstablishAutomaticAssignment(fixture, resolver, SessionOne, RfcByteFirstRepository, 30);
        var context = fixture.PrepareAdmittedContext(SessionOne, LittleEndianFirstRepository, 31);

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(context, transaction);
        switch (mutation)
        {
            case "changed_revision":
                fixture.Execute(transaction, $"UPDATE session_repository_assignment_revisions SET revision=2 WHERE session_id='{SessionOne}';");
                break;
            case "deleted_revision":
                fixture.Execute(transaction, $"DELETE FROM session_repository_assignment_revisions WHERE session_id='{SessionOne}';");
                break;
            case "deleted_head":
                fixture.Execute(transaction, "DROP TRIGGER session_repository_assignment_history_delete_rejected;");
                fixture.Execute(transaction, $"DELETE FROM session_repository_assignment_history WHERE session_id='{SessionOne}' AND new_revision=1;");
                break;
            case "changed_head_fingerprint":
                fixture.Execute(transaction, "DROP TRIGGER session_repository_assignment_history_update_rejected;");
                fixture.Execute(transaction, $"UPDATE session_repository_assignment_history SET new_assignment_state_sha256='{new string('b', 64)}' WHERE session_id='{SessionOne}' AND new_revision=1;");
                break;
            case "changed_head_state":
                fixture.Execute(transaction, "DROP TRIGGER session_repository_assignment_history_update_rejected;");
                fixture.Execute(transaction, $"""
                    UPDATE session_repository_assignment_history
                    SET new_state='conflict',new_authority='automatic',new_repository_id=NULL,
                        new_assignment_state_sha256='{TwoCandidateConflictFingerprint}'
                    WHERE session_id='{SessionOne}' AND new_revision=1;
                    """);
                break;
            default:
                throw new InvalidOperationException(mutation);
        }

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{SessionOne}' AND new_revision=2;"));
        transaction.Rollback();
        Assert.Equal(1, fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{SessionOne}';"));
        Assert.Equal(1, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{SessionOne}';"));
    }

    [Fact]
    public void ApplyAutomatic_PreflightsEveryHistoryHeadBeforeWritingAnEarlierBatchSession()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateSession(SessionTwo);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        var resolver = new LocalRepositoryAssignmentResolver();
        EstablishAutomaticAssignment(fixture, resolver, SessionOne, RfcByteFirstRepository, 32);
        EstablishAutomaticAssignment(fixture, resolver, SessionTwo, RfcByteFirstRepository, 33);
        var first = fixture.PrepareAdmittedContext(SessionOne, LittleEndianFirstRepository, 34, attributeOrdinal: 0);
        var second = fixture.PrepareAdmittedContext(SessionTwo, LittleEndianFirstRepository, 34, attributeOrdinal: 1);

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            34,
            [SessionOne, SessionTwo],
            [first.Assignment, second.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(first, transaction);
        fixture.PublishAdmittedContext(second, transaction);
        fixture.Execute(transaction, "DROP TRIGGER session_repository_assignment_history_delete_rejected;");
        fixture.Execute(transaction, $"DELETE FROM session_repository_assignment_history WHERE session_id='{SessionTwo}' AND new_revision=1;");

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(1, fixture.ScalarLong($"SELECT revision FROM session_repository_assignment_revisions WHERE session_id='{SessionOne}';"));
        Assert.Equal(0, fixture.ScalarLong($"SELECT COUNT(*) FROM session_repository_assignment_history WHERE session_id='{SessionOne}' AND new_revision=2;"));
        transaction.Rollback();
    }

    [Fact]
    public void ApplyAutomatic_RejectsARevisionZeroPreparationWhenHistoryAppearsBeforeApply()
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 35);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(context, transaction);
        fixture.Execute(transaction, $"""
            INSERT INTO session_repository_assignment_history VALUES(
                '{Guid.CreateVersion7():D}','{SessionOne}','automatic_reconcile',0,1,
                '{UnassignedFingerprint}','{RfcByteFirstAssignedFingerprint}','unassigned','assigned','none','automatic',NULL,'{RfcByteFirstRepository}',
                'source_reconciliation',NULL,'{fixture.ReconciliationFingerprint}','{At}');
            """);

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(0, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_revisions;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("changed")]
    public void ApplyAutomatic_RejectsManualOverrideDriftAfterPreparation(string mutation)
    {
        using var fixture = new CatalogFixture();
        fixture.CreateSession(SessionOne);
        fixture.CreateRepository(RfcByteFirstRepository, 1);
        fixture.CreateRepository(LittleEndianFirstRepository, 2);
        fixture.CreateManualOverride(SessionOne, "assigned", LittleEndianFirstRepository, ManualAssignedFingerprint);
        var context = fixture.PrepareAdmittedContext(SessionOne, RfcByteFirstRepository, 36);
        var resolver = new LocalRepositoryAssignmentResolver();

        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            context.RawRecordId,
            [SessionOne],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(Later, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(context, transaction);
        fixture.Execute(
            transaction,
            mutation == "removed"
                ? $"DELETE FROM session_repository_manual_overrides WHERE session_id='{SessionOne}';"
                : $"UPDATE session_repository_manual_overrides SET state='explicitly_unassigned',repository_id=NULL WHERE session_id='{SessionOne}';");

        Assert.Throws<InvalidOperationException>(() => resolver.ApplyAutomatic(fixture.Connection, transaction, preparation));
        Assert.Equal(1, fixture.ScalarLong("SELECT revision FROM session_repository_assignment_revisions;"));
        Assert.Equal(1, fixture.ScalarLong("SELECT COUNT(*) FROM session_repository_assignment_history;"));
        transaction.Rollback();
    }

    private static string[] CreateCandidateRepositoryIds(int count, int start = 100)
    {
        var repositories = new string[count];
        for (var index = 0; index < count; index++)
            repositories[index] = $"01900000-{start + index:x4}-7000-8000-{start + index:x12}";
        return repositories;
    }

    private static void EstablishAutomaticAssignment(
        CatalogFixture fixture,
        LocalRepositoryAssignmentResolver resolver,
        string sessionId,
        string repositoryId,
        long rawRecordId)
    {
        var context = fixture.PrepareAdmittedContext(sessionId, repositoryId, rawRecordId);
        using var transaction = fixture.Connection.BeginTransaction(deferred: false);
        var preparation = resolver.PrepareAutomatic(
            fixture.Connection,
            transaction,
            rawRecordId,
            [sessionId],
            [context.Assignment],
            fixture.ReconciliationFingerprint,
            DateTimeOffset.Parse(At, System.Globalization.CultureInfo.InvariantCulture));
        fixture.PublishAdmittedContext(context, transaction);
        resolver.ApplyAutomatic(fixture.Connection, transaction, preparation);
        transaction.Commit();
    }

    private sealed class CatalogFixture : IDisposable
    {
        private const string OperationKey = "lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"local-repository-assignment-{Guid.NewGuid():N}");
        private readonly Dictionary<string, string> sessionEvents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RepositoryLocator> repositories = new(StringComparer.Ordinal);

        internal CatalogFixture()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "monitor.sqlite");
            new SqliteSessionStore(Path).CreateSchema();
            Connection = Open(Path);
            LocalRepositoryCatalogSchemaV1.Ensure(Connection);
            ReconciliationFingerprint = LocalRepositoryIdentityHashing.ReconciliationFingerprint(
                LocalRepositoryReconciliationEvidence.PayloadSha256(1, new string('a', 64)));
            Execute($"""
                INSERT INTO local_repository_reconciliation_queue VALUES(
                    '01900000-0000-7000-8000-000000000001',1,'payload_sha256','{new string('a', 64)}','local-repository-catalog:1','{ReconciliationFingerprint}','pending',0,NULL,NULL,NULL,'{At}','{At}');
                """);
        }

        internal string Path { get; }
        internal SqliteConnection Connection { get; }
        internal string ReconciliationFingerprint { get; }

        internal string LocatorId(string repositoryId) => repositories[repositoryId].LocatorId;

        internal void CreateSession(string sessionId)
        {
            var runId = Guid.CreateVersion7().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
            var eventId = Guid.CreateVersion7().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
            sessionEvents.Add(sessionId, eventId);
            Execute($"""
                INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at)
                VALUES('{sessionId}','completed','full','{At}','not_captured','{At}','{At}');
                INSERT INTO session_runs(run_id,session_id,source_surface,status)
                VALUES('{runId}','{sessionId}','vscode','completed');
                INSERT INTO session_events(event_id,session_id,run_id,source_surface,source_adapter,source_event_id,type,occurred_at,content_state,source_application_version)
                VALUES('{eventId}','{sessionId}','{runId}','vscode','otel-exact','{eventId}','otel.span','{At}','not_captured','1.2.3');
                """);
        }

        internal void CreateRepository(string repositoryId, int suffix, SqliteTransaction? transaction = null)
        {
            DefineRepository(repositoryId, suffix);
            if (transaction is null)
                PublishRepository(repositoryId);
            else
                PublishRepository(repositoryId, transaction);
        }

        internal void DefineRepository(string repositoryId, int suffix)
        {
            Assert.True(GitHubRepositoryLocatorParser.TryParse($"https://github.com/example/repository{suffix}", out var locator));
            var locatorId = Guid.CreateVersion7().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
            repositories.Add(repositoryId, new(locatorId, locator!));
        }

        internal void PublishRepository(string repositoryId, SqliteTransaction? transaction = null)
        {
            var repository = repositories[repositoryId];
            var sql = $"""
                INSERT INTO local_repositories VALUES('{repositoryId}','{repository.Locator.DisplayRepository}',1,'{At}','{At}');
                INSERT INTO local_repository_locators VALUES(
                    '{repository.LocatorId}','{repositoryId}','github_repository','{repository.Locator.CanonicalLocator}','{repository.Locator.LocatorSha256}','manual','{repository.Locator.DisplayOwner}','{repository.Locator.DisplayRepository}','{At}');
                INSERT INTO local_repository_locator_heads VALUES('{repositoryId}','github_repository','{repository.LocatorId}','{At}');
                """;
            if (transaction is null)
                Execute(sql);
            else
                Execute(transaction, sql);
        }

        internal string CreateAdditionalLocator(string repositoryId, int suffix)
        {
            Assert.True(GitHubRepositoryLocatorParser.TryParse($"https://github.com/example/alternate{suffix}", out var locator));
            var locatorId = Guid.CreateVersion7().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
            Execute($"""
                INSERT INTO local_repository_locators VALUES(
                    '{locatorId}','{repositoryId}','github_repository','{locator!.CanonicalLocator}','{locator.LocatorSha256}','manual','{locator.DisplayOwner}','{locator.DisplayRepository}','{At}');
                """);
            return locatorId;
        }

        internal PreparedContext PrepareAdmittedContext(
            string sessionId,
            string repositoryId,
            long rawRecordId,
            int attributeOrdinal = 0)
        {
            var eventId = sessionEvents[sessionId];
            var repository = repositories[repositoryId];
            var observationId = Guid.CreateVersion7().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
            var contextId = Guid.CreateVersion7().ToString("D", System.Globalization.CultureInfo.InvariantCulture);
            var sourceIdentity = LocalRepositoryIdentityHashing.SourceIdentity(
                LocalRepositorySourceIdentityInput.Span(rawRecordId, 0, 0, 0, attributeOrdinal, "vcs.repository.url.full"));
            var contextIdentity = LocalRepositoryIdentityHashing.ContextIdentity(new(
                sourceIdentity,
                sessionId,
                eventId,
                "11111111111111111111111111111111",
                "2222222222222222"));
            return new(
                new(contextId, contextIdentity, sessionId, repositoryId, repository.LocatorId),
                rawRecordId,
                observationId,
                sourceIdentity,
                attributeOrdinal);
        }

        internal void PublishAdmittedContext(
            PreparedContext context,
            SqliteTransaction transaction,
            string? actualSessionId = null,
            string? actualRepositoryId = null,
            string? actualLocatorId = null,
            string? actualSourceIdentity = null)
        {
            actualSessionId ??= context.Assignment.SessionId;
            actualRepositoryId ??= context.Assignment.RepositoryId;
            actualLocatorId ??= context.Assignment.LocatorId;
            actualSourceIdentity ??= context.SourceIdentity;
            var eventId = sessionEvents[actualSessionId];
            var repository = repositories[actualRepositoryId];
            Execute(transaction, $"""
                INSERT INTO session_repository_observations VALUES(
                    '{context.ObservationId}','{actualSourceIdentity}',{context.RawRecordId},'{new string('a', 64)}',0,0,0,{context.AttributeOrdinal},'span','vcs.repository.url.full','admitted',
                    'github_repository','{repository.Locator.CanonicalLocator}','{repository.Locator.LocatorSha256}','{repository.Locator.DisplayOwner}','{repository.Locator.DisplayRepository}',
                    'github-copilot-vscode','1.2.3','{At}');
                INSERT INTO session_repository_observation_contexts VALUES(
                    '{context.Assignment.ContextId}','{context.ObservationId}','{context.Assignment.ContextIdentitySha256}','{eventId}','{actualSessionId}','11111111111111111111111111111111','2222222222222222','admitted','{actualRepositoryId}','{actualLocatorId}','{At}');
                """);
        }

        internal void CreateManualOverride(string sessionId, string state, string? repositoryId, string fingerprint)
        {
            Execute($"""
                INSERT INTO local_repository_operation_receipts VALUES(
                    '{OperationKey}','{new string('a', 64)}',200,'application/json; charset=utf-8','no-store',X'7B7D','{At}');
                INSERT INTO session_repository_assignment_revisions VALUES('{sessionId}',1,'{At}');
                INSERT INTO session_repository_assignment_history VALUES(
                    '{Guid.CreateVersion7():D}','{sessionId}','{(state == "assigned" ? "assign" : "explicitly_unassign")}',0,1,
                    '{UnassignedFingerprint}','{fingerprint}','unassigned','{state}','none','manual',NULL,{(repositoryId is null ? "NULL" : $"'{repositoryId}'")},
                    'user_operation','{OperationKey}',NULL,'{At}');
                INSERT INTO session_repository_manual_overrides VALUES(
                    '{sessionId}','{state}',{(repositoryId is null ? "NULL" : $"'{repositoryId}'")},1,'{At}');
                """);
        }

        internal void CreateHistoricalManualRoundTripWithWrongFingerprint(
            string sessionId,
            string automaticRepositoryId,
            string manualRepositoryId)
        {
            Execute($"""
                INSERT INTO local_repository_operation_receipts VALUES(
                    'lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA','{new string('b', 64)}',200,'application/json; charset=utf-8','no-store',X'7B7D','{At}');
                INSERT INTO local_repository_operation_receipts VALUES(
                    'lrc1_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBA','{new string('c', 64)}',200,'application/json; charset=utf-8','no-store',X'7B7D','{At}');
                INSERT INTO session_repository_assignment_history VALUES(
                    '{Guid.CreateVersion7():D}','{sessionId}','assign',1,2,
                    '{RfcByteFirstAssignedFingerprint}','{WrongManualFingerprint}','assigned','assigned','automatic','manual','{automaticRepositoryId}','{manualRepositoryId}',
                    'user_operation','lrc1_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',NULL,'{At}');
                INSERT INTO session_repository_assignment_history VALUES(
                    '{Guid.CreateVersion7():D}','{sessionId}','resume_automatic',2,3,
                    '{WrongManualFingerprint}','{RfcByteFirstAssignedFingerprint}','assigned','assigned','manual','automatic','{manualRepositoryId}','{automaticRepositoryId}',
                    'user_operation','lrc1_BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBA',NULL,'{At}');
                UPDATE session_repository_assignment_revisions
                SET revision=3,updated_at='{At}'
                WHERE session_id='{sessionId}' AND revision=1;
                """);
        }

        internal long ScalarLong(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        internal string ScalarText(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }

        internal static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                DefaultTimeout = 1,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=1;";
            command.ExecuteNonQuery();
            return connection;
        }

        private void Execute(string sql)
        {
            using var command = Connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        internal void Execute(SqliteTransaction transaction, string sql)
        {
            using var command = Connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Connection.Dispose();
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }

        private sealed record RepositoryLocator(string LocatorId, GitHubRepositoryLocator Locator);
    }

    private sealed record PreparedContext(
        LocalRepositoryProspectiveAssignmentContext Assignment,
        long RawRecordId,
        string ObservationId,
        string SourceIdentity,
        int AttributeOrdinal);
}

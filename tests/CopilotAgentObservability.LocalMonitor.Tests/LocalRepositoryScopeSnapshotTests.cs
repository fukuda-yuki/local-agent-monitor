using System.Data;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryScopeSnapshotTests
{
    private const string RepositoryA = "01900000-0000-7000-8000-000000000001";
    private const string RepositoryB = "01900000-0000-7000-8000-000000000002";
    private const string RepositoryC = "01900000-0000-7000-8000-000000000003";
    private const string LocatorA = "01900000-0000-7000-8000-000000000011";
    private const string LocatorB = "01900000-0000-7000-8000-000000000012";
    private const string LocatorC = "01900000-0000-7000-8000-000000000013";

    [Fact]
    public async Task ReadAsync_UsesOneSequentialSnapshotAndKeepsContributorRowsOpaque()
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryA, LocatorA, revision: 3);
        database.AddRepository(RepositoryB, LocatorB, revision: 5);
        var assignedSessionId = SessionId(1);
        var unassignedSessionId = SessionId(2);
        database.SetRevision(assignedSessionId, 4);
        database.AddCandidate(assignedSessionId, RepositoryA);
        var calls = new List<string>();
        SqliteConnection? sessionConnection = null;
        SqliteTransaction? sessionTransaction = null;
        SqliteConnection? archiveConnection = null;
        SqliteTransaction? archiveTransaction = null;
        LocalRepositoryArchiveInput? observedArchiveInput = null;
        var assignedRow = new FakeSessionRow(assignedSessionId, "opaque-assigned-row");
        var unassignedRow = new FakeSessionRow(unassignedSessionId, "opaque-unassigned-row");
        var session = new FakeSessionContributor(async (capability, _, cancellationToken) =>
        {
            calls.Add("session");
            await capability.ReadAsync(async (connection, transaction, token) =>
            {
                sessionConnection = connection;
                sessionTransaction = transaction;
                await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", token);
                return true;
            }, cancellationToken);
            return new([unassignedRow, assignedRow]);
        });
        var archive = new FakeArchiveContributor(async (capability, input, cancellationToken) =>
        {
            calls.Add("archive");
            observedArchiveInput = input;
            Assert.Equal([assignedSessionId, unassignedSessionId], input.SessionIds);
            Assert.Equal([RepositoryA, RepositoryB], input.RepositoryIds);
            await capability.ReadAsync(async (connection, transaction, token) =>
            {
                archiveConnection = connection;
                archiveTransaction = transaction;
                await ScalarAsync(connection, transaction, "SELECT value FROM archive_snapshot_source;", token);
                return true;
            }, cancellationToken);
            return new(
                [
                    new(unassignedSessionId, LocalArchiveState.Active, 2),
                    new(assignedSessionId, LocalArchiveState.Archived, 3),
                ],
                [
                    new(RepositoryB, LocalArchiveState.Archived, 1),
                    new(RepositoryA, LocalArchiveState.Active, 2),
                ]);
        });
        var openCount = 0;
        SqliteConnection? openedByService = null;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            connectionOpenedObserver: connection =>
            {
                openCount++;
                openedByService = connection;
            });

        var snapshot = await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Equal(["session", "archive"], calls);
        Assert.Same(session.LastCapability, archive.LastCapability);
        Assert.Same(sessionConnection, archiveConnection);
        Assert.Same(openedByService, sessionConnection);
        Assert.Same(sessionTransaction, archiveTransaction);
        Assert.Equal(1, openCount);
        Assert.False(observedArchiveInput!.SessionIds is string[]);
        Assert.False(observedArchiveInput.RepositoryIds is string[]);
        Assert.Same(assignedRow, snapshot.Sessions[0].Session);
        Assert.Same(unassignedRow, snapshot.Sessions[1].Session);
        Assert.Equal(4, snapshot.Sessions[0].AssignmentRevision);
        Assert.Equal(RepositoryA, snapshot.Sessions[0].RepositoryId);
        Assert.True(snapshot.Sessions[0].IsRequestedScopeMember);
        Assert.Equal(LocalArchiveState.Archived, snapshot.Sessions[0].ArchiveState);
        Assert.Equal(3, snapshot.Sessions[0].ArchiveRevision);
        Assert.False(snapshot.Sessions[0].IsEffectivelyEligible);
        Assert.Equal("session_archived", snapshot.Sessions[0].ArchiveExclusionReason);
        Assert.Equal(LocalArchiveState.Active, snapshot.Sessions[1].ArchiveState);
        Assert.Equal(2, snapshot.Sessions[1].ArchiveRevision);
        Assert.True(snapshot.Sessions[1].IsEffectivelyEligible);
        var repository = snapshot.Repositories[0];
        Assert.Equal(3, repository.Revision);
        Assert.Equal(LocatorA, repository.CurrentLocatorId);
        Assert.Equal(0, repository.AssignmentConflictCount);
        Assert.Equal(LocalArchiveState.Active, repository.ArchiveState);
        Assert.Equal(2, repository.ArchiveRevision);
        Assert.Equal(LocalArchiveState.Archived, snapshot.Repositories[1].ArchiveState);
        Assert.Equal(1, snapshot.Repositories[1].ArchiveRevision);
        Assert.False(snapshot.Repositories is LocalRepositoryCatalogSnapshot[]);
        Assert.False(snapshot.Sessions is List<LocalRepositoryScopeSessionSnapshot>);
        Assert.False(snapshot.Sessions[0].CandidateRepositoryIds is string[]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LocalRepositoryScopeSessionSnapshot>)snapshot.Sessions)[0] = snapshot.Sessions[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<LocalRepositoryCatalogSnapshot>)snapshot.Repositories)[0] = repository);
        Assert.Equal([nameof(ILocalRepositoryReadTransaction.ReadAsync)],
            typeof(ILocalRepositoryReadTransaction).GetMethods().Select(method => method.Name).Distinct());
        Assert.Empty(typeof(ILocalRepositoryReadTransaction).GetProperties());
        Assert.Equal(ConnectionState.Closed, sessionConnection!.State);
        Assert.Null(sessionTransaction!.Connection);
    }

    [Fact]
    public async Task ReadAsync_PerformsTheSessionReadBeforeAnyCatalogRead()
    {
        using var database = new EmptyDatabase();
        var firstReadCompleted = false;
        SqliteConnection? openedConnection = null;
        var session = new FakeSessionContributor(async (capability, _, cancellationToken) =>
        {
            await capability.ReadAsync(async (connection, transaction, token) =>
            {
                openedConnection = connection;
                await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", token);
                firstReadCompleted = true;
                return true;
            }, cancellationToken);
            return new([new FakeSessionRow(SessionId(1), "first")]);
        });
        var archive = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));
        var service = new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));

        Assert.True(firstReadCompleted);
        Assert.Equal(1, session.CallCount);
        Assert.Equal(0, archive.CallCount);
        Assert.Equal(ConnectionState.Closed, openedConnection!.State);
    }

    [Fact]
    public async Task ReadAsync_ComposesExactAssignmentScopesConflictCountsAndArchiveFacts()
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryB, LocatorB, revision: 5);
        database.AddRepository(RepositoryA, LocatorA, revision: 2);
        var automatic = SessionId(1);
        var manual = SessionId(2);
        var unassigned = SessionId(3);
        var explicitlyUnassigned = SessionId(4);
        var conflict = SessionId(5);
        database.AddCandidate(automatic, RepositoryA);
        database.AddCandidate(manual, RepositoryA);
        database.SetManualOverride(manual, "assigned", RepositoryB, revision: 7);
        database.SetManualOverride(explicitlyUnassigned, "explicitly_unassigned", null, revision: 8);
        database.AddCandidate(conflict, RepositoryB);
        database.AddCandidate(conflict, RepositoryA);
        database.SetRevision(automatic, 1);
        database.SetRevision(manual, 7);
        database.SetRevision(explicitlyUnassigned, 8);
        database.SetRevision(conflict, 9);
        var rows = new[] { automatic, manual, unassigned, explicitlyUnassigned, conflict }
            .Select((id, index) => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(id, $"row-{index}"))
            .ToArray();

        static FakeSessionContributor Sessions(IReadOnlyList<ILocalRepositorySessionSnapshotRow> rows) =>
            new((capability, _, token) => ReadSessionContribution(capability, rows, token));
        var archive = new FakeArchiveContributor(async (capability, input, token) =>
        {
            await ReadArchiveTable(capability, token);
            return new(
                input.SessionIds.Select(id => new LocalArchiveSessionFact(
                    id,
                    id == automatic ? LocalArchiveState.Archived : LocalArchiveState.Active,
                    id == automatic ? 1 : 0)).ToArray(),
                input.RepositoryIds.Select(id => new LocalArchiveRepositoryFact(id, LocalArchiveState.Active, 0)).ToArray());
        });

        var all = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, Sessions(rows), archive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        var repositoryA = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, Sessions(rows), archive)
            .ReadAsync(new(LocalRepositoryScopeKind.Repository, RepositoryA), CancellationToken.None);
        var unassignedScope = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, Sessions(rows), archive)
            .ReadAsync(new(LocalRepositoryScopeKind.Unassigned, null), CancellationToken.None);

        Assert.All(all.Sessions, item => Assert.True(item.IsRequestedScopeMember));
        Assert.False(all.Sessions.Single(item => item.SessionId == automatic).IsEffectivelyEligible);
        Assert.Equal("session_archived", all.Sessions.Single(item => item.SessionId == automatic).ArchiveExclusionReason);
        Assert.True(all.Sessions.Single(item => item.SessionId == manual).IsEffectivelyEligible);
        Assert.Equal(LocalRepositoryScopeAssignmentState.Assigned, all.Sessions.Single(item => item.SessionId == manual).AssignmentState);
        Assert.Equal(LocalRepositoryScopeAssignmentAuthority.Manual, all.Sessions.Single(item => item.SessionId == manual).AssignmentAuthority);
        Assert.Equal(7, all.Sessions.Single(item => item.SessionId == manual).AssignmentRevision);
        Assert.Equal(RepositoryB, all.Sessions.Single(item => item.SessionId == manual).RepositoryId);
        Assert.Equal(LocalRepositoryScopeAssignmentAuthority.Automatic, all.Sessions.Single(item => item.SessionId == automatic).AssignmentAuthority);
        Assert.Equal(1, all.Sessions.Single(item => item.SessionId == automatic).AssignmentRevision);
        Assert.Equal(LocalRepositoryScopeAssignmentAuthority.None, all.Sessions.Single(item => item.SessionId == unassigned).AssignmentAuthority);
        Assert.Equal(0, all.Sessions.Single(item => item.SessionId == unassigned).AssignmentRevision);
        Assert.Equal(LocalRepositoryScopeAssignmentState.ExplicitlyUnassigned, all.Sessions.Single(item => item.SessionId == explicitlyUnassigned).AssignmentState);
        Assert.Equal(LocalRepositoryScopeAssignmentAuthority.Manual, all.Sessions.Single(item => item.SessionId == explicitlyUnassigned).AssignmentAuthority);
        Assert.Equal(8, all.Sessions.Single(item => item.SessionId == explicitlyUnassigned).AssignmentRevision);
        Assert.Equal(LocalRepositoryScopeAssignmentState.Conflict, all.Sessions.Single(item => item.SessionId == conflict).AssignmentState);
        Assert.Equal(LocalRepositoryScopeAssignmentAuthority.Automatic, all.Sessions.Single(item => item.SessionId == conflict).AssignmentAuthority);
        Assert.Equal(9, all.Sessions.Single(item => item.SessionId == conflict).AssignmentRevision);
        Assert.Equal([RepositoryA, RepositoryB], all.Sessions.Single(item => item.SessionId == conflict).CandidateRepositoryIds);
        Assert.Equal(1, all.Repositories.Single(item => item.RepositoryId == RepositoryA).AssignmentConflictCount);
        Assert.Equal(1, all.Repositories.Single(item => item.RepositoryId == RepositoryB).AssignmentConflictCount);
        Assert.Equal([RepositoryA, RepositoryB], all.Repositories.Select(item => item.RepositoryId));

        Assert.True(repositoryA.Sessions.Single(item => item.SessionId == automatic).IsRequestedScopeMember);
        Assert.False(repositoryA.Sessions.Single(item => item.SessionId == manual).IsRequestedScopeMember);
        Assert.True(repositoryA.Sessions.Single(item => item.SessionId == manual).IsEffectivelyEligible);
        Assert.False(repositoryA.Sessions.Single(item => item.SessionId == conflict).IsRequestedScopeMember);
        Assert.Equal([unassigned, explicitlyUnassigned, conflict],
            unassignedScope.Sessions.Where(item => item.IsRequestedScopeMember).Select(item => item.SessionId));
    }

    [Fact]
    public async Task ReadAsync_FailsClosedForInvalidRequestsContributionsAndCancellation()
    {
        using var database = new ScopeDatabase();
        var oneRow = new[] { (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(SessionId(1), "one") };
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(capability, oneRow, token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));
        var service = new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive);

        await Assert.ThrowsAsync<ArgumentException>(async () => await service.ReadAsync(new((LocalRepositoryScopeKind)99, null), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.ReadAsync(new(LocalRepositoryScopeKind.Repository, null), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.ReadAsync(new(LocalRepositoryScopeKind.All, RepositoryA), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(async () => await service.ReadAsync(new(LocalRepositoryScopeKind.Repository, "01900000-0000-7000-8000-00000000000A"), CancellationToken.None));

        var tooMany = Enumerable.Range(1, 10_001)
            .Select(index => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(SessionId(index), "bounded"))
            .ToArray();
        var boundedService = new SqliteLocalRepositoryScopeSnapshotService(database.Path,
            new FakeSessionContributor((capability, _, token) => ReadSessionContribution(capability, tooMany, token)), archive);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await boundedService.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));

        using var cancellation = new CancellationTokenSource();
        SqliteConnection? canceledConnection = null;
        var cancelingSession = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                canceledConnection = connection;
                await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
                cancellation.Cancel();
                return true;
            }, token);
            return new(oneRow);
        });
        var laterArchive = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, cancelingSession, laterArchive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellation.Token));
        Assert.Equal(0, laterArchive.CallCount);
        Assert.Equal(ConnectionState.Closed, canceledConnection!.State);

        SqliteConnection? failedSessionConnection = null;
        var failingSession = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                failedSessionConnection = connection;
                await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
                return true;
            }, token);
            throw new SyntheticFailureException();
        });
        var archiveAfterFailure = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));
        await Assert.ThrowsAsync<SyntheticFailureException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, failingSession, archiveAfterFailure)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
        Assert.Equal(0, archiveAfterFailure.CallCount);
        Assert.Equal(ConnectionState.Closed, failedSessionConnection!.State);

        SqliteConnection? failedArchiveConnection = null;
        var failingArchive = new FakeArchiveContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                failedArchiveConnection = connection;
                await ScalarAsync(connection, transaction, "SELECT value FROM archive_snapshot_source;", innerToken);
                return true;
            }, token);
            throw new SyntheticFailureException();
        });
        await Assert.ThrowsAsync<SyntheticFailureException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, failingArchive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
        Assert.Equal(ConnectionState.Closed, failedArchiveConnection!.State);
    }

    [Fact]
    public async Task ReadAsync_MapsBusyOnceWithoutRetryOrFallback()
    {
        using var database = new ScopeDatabase();
        database.SetDeleteJournalMode();
        using var blocker = database.Open();
        using (var command = blocker.CreateCommand())
        {
            command.CommandText = "PRAGMA locking_mode=EXCLUSIVE; BEGIN EXCLUSIVE; UPDATE local_repositories SET display_name=display_name;";
            command.ExecuteNonQuery();
        }
        var sessionId = SessionId(1);
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync((connection, transaction, _) =>
                ValueTask.FromResult(Convert.ToInt64(Scalar(connection, transaction, "SELECT value FROM session_snapshot_source;")) == 1), token);
            return new([new FakeSessionRow(sessionId, "busy")]);
        });
        var archive = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));
        var serviceOpenCount = 0;
        SqliteConnection? serviceConnection = null;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            busyTimeoutMilliseconds: 25,
            connectionFactory: () =>
            {
                serviceOpenCount++;
                return database.CreateReadOnlyConnection();
            },
            connectionOpenedObserver: connection => serviceConnection = connection);

        var error = await Assert.ThrowsAsync<LocalRepositoryScopeSnapshotException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));

        Assert.Equal(LocalRepositoryScopeSnapshotError.PersistenceBusy, error.Error);
        Assert.Equal("persistence_busy", error.ErrorCode);
        Assert.Equal(1, session.CallCount);
        Assert.Equal(0, archive.CallCount);
        Assert.Equal(1, serviceOpenCount);
        Assert.Equal(ConnectionState.Closed, serviceConnection!.State);
    }

    [Fact]
    public async Task ReadAsync_ComposesTenThousandSessionsInDeterministicOrderWithFixedCatalogQueries()
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryA, LocatorA, revision: 1);
        database.AddCandidate(SessionId(1), RepositoryA);
        database.SetRevision(SessionId(1), 1);
        var rows = Enumerable.Range(1, 10_000)
            .Select(index => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(SessionId(index), "bounded"))
            .Reverse()
            .ToArray();
        var statements = new List<string>();
        SQLitePCL.strdelegate_trace? trace = null;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                trace = (_, statement) => statements.Add(statement);
                SQLitePCL.raw.sqlite3_trace(connection.Handle, trace, null);
                await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
                return true;
            }, token);
            return new(rows);
        });
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        var openCount = 0;
        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            connectionFactory: () =>
            {
                openCount++;
                return database.CreateReadOnlyConnection();
            })
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Equal(10_000, snapshot.Sessions.Count);
        Assert.Equal(Enumerable.Range(1, 10_000).Select(SessionId), snapshot.Sessions.Select(item => item.SessionId));
        var reads = statements.Where(statement => statement.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || statement.Contains("scope-catalog-query", StringComparison.Ordinal)).ToArray();
        var workStatements = statements.Where(statement =>
            !statement.TrimStart().StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(5, workStatements.Length);
        Assert.Equal(5, reads.Length);
        Assert.Contains("session_snapshot_source", reads[0], StringComparison.Ordinal);
        Assert.Contains("scope-catalog-query:assignments", reads[1], StringComparison.Ordinal);
        Assert.Contains("scope-catalog-query:candidates", reads[2], StringComparison.Ordinal);
        Assert.Contains("scope-catalog-query:repositories", reads[3], StringComparison.Ordinal);
        Assert.Contains("archive_snapshot_source", reads[4], StringComparison.Ordinal);
        Assert.Equal(1, openCount);
        Assert.DoesNotContain(statements, statement => statement.Contains("WHERE session_id=$session_id", StringComparison.Ordinal));
        GC.KeepAlive(trace);
    }

    [Fact]
    public async Task ReadAsync_PreservesOneDatabaseSnapshotAcrossBothContributorPhases()
    {
        using var database = new ScopeDatabase();
        var sessionId = SessionId(1);
        long? sessionValue = null;
        long? archiveValue = null;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            sessionValue = await capability.ReadAsync((connection, transaction, innerToken) =>
                ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken), token);
            database.UpdateSessionSource(2);
            return new([new FakeSessionRow(sessionId, "snapshot")]);
        });
        var archive = new FakeArchiveContributor(async (capability, input, token) =>
        {
            archiveValue = await capability.ReadAsync((connection, transaction, innerToken) =>
                ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken), token);
            return ActiveArchiveFacts(input);
        });

        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Single(snapshot.Sessions);
        Assert.Equal(1, sessionValue);
        Assert.Equal(1, archiveValue);
        Assert.Equal(2, database.ReadSessionSource());
    }

    [Fact]
    public async Task ReadAsync_RejectsNestedAndParallelCapabilityReads()
    {
        using var database = new ScopeDatabase();
        var sessionId = SessionId(1);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            var first = capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
                callbackStarted.SetResult();
                await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                    await capability.ReadAsync((_, _, _) => ValueTask.FromResult(0L), innerToken));
                await releaseCallback.Task;
                return true;
            }, token).AsTask();
            await callbackStarted.Task;
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await capability.ReadAsync((_, _, _) => ValueTask.FromResult(0L), token));
            releaseCallback.SetResult();
            await first;
            return new([new FakeSessionRow(sessionId, "overlap")]);
        });
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Single(snapshot.Sessions);
    }

    [Fact]
    public async Task ReadAsync_RejectsAnEntryDelayedPastPhaseRevocationWithoutUsingDisposedHandles()
    {
        using var database = new ScopeDatabase();
        var entryReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEntry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<long>? delayedRead = null;
        var callbackRan = false;
        var session = new FakeSessionContributor((capability, _, _) =>
        {
            delayedRead = capability.ReadAsync((_, _, _) =>
            {
                callbackRan = true;
                return ValueTask.FromResult(1L);
            }, CancellationToken.None).AsTask();
            return ValueTask.FromResult(new LocalRepositorySessionContribution(
                [new FakeSessionRow(SessionId(1), "delayed-entry")]));
        });
        var archive = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            capabilityEntryObserver: async () =>
            {
                entryReached.SetResult();
                await releaseEntry.Task;
            });

        var serviceRead = service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None).AsTask();
        await entryReached.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await serviceRead);
        releaseEntry.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await delayedRead!);
        Assert.False(callbackRan);
        Assert.Equal(0, archive.CallCount);
    }

    [Fact]
    public async Task ReadAsync_RevokesNewEntriesButDrainsActiveCallbackUnderItsContributorRestrictions()
    {
        using var database = new ScopeDatabase();
        var activeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var phaseRevoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>? activeRead = null;
        SqliteConnection? activeConnection = null;
        var catalogDenied = false;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            activeRead = capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                activeConnection = connection;
                await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
                activeStarted.SetResult();
                await phaseRevoked.Task;
                try
                {
                    await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM local_repositories;", innerToken);
                }
                catch (SqliteException)
                {
                    catalogDenied = true;
                }
                await releaseActive.Task;
                return true;
            }, token).AsTask();
            await activeStarted.Task;
            return new([new FakeSessionRow(SessionId(1), "drain")]);
        });
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));
        var revokedCount = 0;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            contributorPhaseRevokedObserver: () =>
            {
                if (Interlocked.Increment(ref revokedCount) == 1)
                    phaseRevoked.SetResult();
            });

        var serviceRead = service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None).AsTask();
        await phaseRevoked.Task;
        Assert.False(serviceRead.IsCompleted);
        releaseActive.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await serviceRead);
        Assert.True(await activeRead!);
        Assert.True(catalogDenied);
        Assert.Equal(0, archive.CallCount);
        Assert.Equal(ConnectionState.Closed, activeConnection!.State);
    }

    [Fact]
    public async Task ReadAsync_DeniesCatalogTablesToContributors()
    {
        using var database = new ScopeDatabase();
        var sessionCatalogDenied = false;
        var archiveCatalogDenied = false;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                await Assert.ThrowsAsync<SqliteException>(async () =>
                    await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM local_repositories;", innerToken));
                sessionCatalogDenied = true;
                return await ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
            }, token);
            return new([new FakeSessionRow(SessionId(1), "catalog-denial")]);
        });
        var archive = new FakeArchiveContributor(async (capability, input, token) =>
        {
            await capability.ReadAsync(async (connection, transaction, innerToken) =>
            {
                await Assert.ThrowsAsync<SqliteException>(async () =>
                    await ScalarAsync(connection, transaction, "SELECT COUNT(*) FROM session_repository_assignment_revisions;", innerToken));
                archiveCatalogDenied = true;
                return await ScalarAsync(connection, transaction, "SELECT value FROM archive_snapshot_source;", innerToken);
            }, token);
            return ActiveArchiveFacts(input);
        });

        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.True(sessionCatalogDenied);
        Assert.True(archiveCatalogDenied);
        Assert.Single(snapshot.Sessions);
    }

    [Fact]
    public async Task ReadAsync_RejectsZeroReadRetainedAndCrossPhaseCapabilityUse()
    {
        using var database = new ScopeDatabase();
        var sessionId = SessionId(1);
        var archive = new FakeArchiveContributor((_, input, _) => ValueTask.FromResult(
            ActiveArchiveFacts(input)));
        var zeroRead = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            new FakeSessionContributor((_, _, _) => ValueTask.FromResult(
                new LocalRepositorySessionContribution([new FakeSessionRow(sessionId, "zero-read")]))),
            archive);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await zeroRead.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));

        ILocalRepositoryReadTransaction? retained = null;
        var releaseLateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ValueTask<long>>? delayedInvocation = null;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            retained = capability;
            await capability.ReadAsync((connection, transaction, innerToken) =>
                ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken), token);
            delayedInvocation = Task.Run(async () =>
            {
                await releaseLateRead.Task;
                return capability.ReadAsync((_, _, _) => ValueTask.FromResult(9L), CancellationToken.None);
            });
            return new([new FakeSessionRow(sessionId, "phase")]);
        });
        var phaseArchive = new FakeArchiveContributor(async (capability, input, token) =>
        {
            releaseLateRead.SetResult();
            var delayed = await delayedInvocation!;
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await delayed);
            await capability.ReadAsync((connection, transaction, innerToken) =>
                ScalarAsync(connection, transaction, "SELECT value FROM archive_snapshot_source;", innerToken), token);
            return ActiveArchiveFacts(input);
        });
        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, phaseArchive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        Assert.Single(snapshot.Sessions);

        var afterReturn = retained!.ReadAsync((_, _, _) => ValueTask.FromResult(1L), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await afterReturn);
    }

    [Fact]
    public async Task ReadAsync_FreezesExactSessionIdentityFromOneGetterRead()
    {
        using var database = new ScopeDatabase();
        var exact = SessionId(1);
        var changed = SessionId(2);
        var row = new MutableSessionRow(exact, changed);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(capability, [row], token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Equal(1, row.GetterCount);
        Assert.Equal(exact, Assert.Single(snapshot.Sessions).SessionId);
        Assert.Same(row, snapshot.Sessions[0].Session);
    }

    [Theory]
    [InlineData(null, 7)]
    [InlineData(6, 7)]
    [InlineData(8, 7)]
    public async Task ReadAsync_RejectsManualOverrideWithoutMatchingAuthoritativeRevision(int? authoritativeRevision, int overrideRevision)
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryB, LocatorB, revision: 1);
        var sessionId = SessionId(1);
        database.SetManualOverride(sessionId, "assigned", RepositoryB, overrideRevision);
        if (authoritativeRevision is not null)
            database.SetRevision(sessionId, authoritativeRevision.Value);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(sessionId, "manual-corruption")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
    }

    [Theory]
    [InlineData("01900000-0000-7000-8000-00000000000A", "Repository", 1, LocatorA)]
    [InlineData(RepositoryA, "", 1, LocatorA)]
    [InlineData(RepositoryA, "Repository", 0, LocatorA)]
    [InlineData(RepositoryA, "Repository", 1, "01900000-0000-7000-8000-00000000001A")]
    public async Task ReadAsync_RejectsInvalidRepositoryCatalogRows(
        string repositoryId,
        string displayName,
        long revision,
        string locatorId)
    {
        using var database = new ScopeDatabase();
        database.AddRawRepository(repositoryId, displayName, revision, locatorId);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(SessionId(1), "catalog-corruption")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RejectsNonPositiveAuthoritativeAssignmentRevision()
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryA, LocatorA, revision: 1);
        var sessionId = SessionId(1);
        database.AddCandidate(sessionId, RepositoryA);
        database.SetRevision(sessionId, 0);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(sessionId, "revision-corruption")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_AcceptsPersistedRevisionZeroForExactEmptyAutomaticBase()
    {
        using var database = new ScopeDatabase();
        var sessionId = SessionId(1);
        database.SetRevision(sessionId, 0);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(sessionId, "empty-zero")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        var row = Assert.Single(snapshot.Sessions);
        Assert.Equal(0, row.AssignmentRevision);
        Assert.Equal(LocalRepositoryScopeAssignmentState.Unassigned, row.AssignmentState);
        Assert.Equal(LocalRepositoryScopeAssignmentAuthority.None, row.AssignmentAuthority);
        Assert.Empty(row.CandidateRepositoryIds);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task ReadAsync_EnforcesCandidateBoundAtExactEdge(int candidateCount, bool succeeds)
    {
        using var database = new ScopeDatabase();
        var sessionId = SessionId(999);
        database.AddCandidateSet(sessionId, candidateCount);
        database.SetRevision(sessionId, 1);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(sessionId, "candidate-bound")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));
        var service = new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive);

        if (!succeeds)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
            return;
        }

        var snapshot = await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);
        var row = Assert.Single(snapshot.Sessions);
        Assert.Equal(LocalRepositoryScopeAssignmentState.Conflict, row.AssignmentState);
        Assert.Equal(128, row.CandidateRepositoryIds.Count);
        Assert.All(snapshot.Repositories, repository => Assert.Equal(1, repository.AssignmentConflictCount));
    }

    [Fact]
    public async Task ReadAsync_RejectsAutomaticAssignmentWithoutAuthoritativeRevision()
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryA, LocatorA, revision: 1);
        var sessionId = SessionId(1);
        database.AddCandidate(sessionId, RepositoryA);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(sessionId, "missing-revision")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("duplicate")]
    public async Task ReadAsync_RejectsInvalidOrDuplicateSessionContribution(string corruption)
    {
        using var database = new ScopeDatabase();
        var valid = SessionId(1);
        IReadOnlyList<ILocalRepositorySessionSnapshotRow> rows = corruption == "invalid"
            ? [new FakeSessionRow("01900000-0000-7000-8000-00000000000A", corruption)]
            : [new FakeSessionRow(valid, corruption), new FakeSessionRow(valid, corruption)];
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(capability, rows, token));
        var archive = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
        Assert.Equal(0, archive.CallCount);
    }

    [Fact]
    public async Task ReadAsync_RejectsCandidateWhoseRepositoryOwnerIsMissing()
    {
        using var database = new ScopeDatabase();
        var sessionId = SessionId(1);
        database.AddCandidate(sessionId, RepositoryA);
        database.SetRevision(sessionId, 1);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(sessionId, "missing-owner")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_RejectsDuplicateRepositoryRows()
    {
        using var database = new ScopeDatabase();
        database.InstallDuplicateRepositoryRows(RepositoryA);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(SessionId(1), "duplicate-repository")],
            token));
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));
    }

    [Fact]
    public async Task ReadAsync_MapsNullArchiveContributionToFixedFailureBeforeComposition()
    {
        using var database = new ScopeDatabase();
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(SessionId(1), "null-contribution")],
            token));
        var archive = new FakeArchiveContributor(async (capability, _, token) =>
        {
            await ReadArchiveTable(capability, token);
            return null!;
        });
        var composeCalls = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(
                database.Path,
                session,
                archive,
                compositionObserver: _ => composeCalls++)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));

        Assert.Equal("local_archive_fact_contribution_invalid", exception.Message);
        Assert.Equal(0, composeCalls);
    }

    [Theory]
    [InlineData("session", "null_list")]
    [InlineData("session", "null_item")]
    [InlineData("session", "missing")]
    [InlineData("session", "extra")]
    [InlineData("session", "duplicate")]
    [InlineData("session", "substitution")]
    [InlineData("session", "noncanonical")]
    [InlineData("session", "undefined_state")]
    [InlineData("session", "active_negative")]
    [InlineData("session", "active_odd")]
    [InlineData("session", "archived_negative")]
    [InlineData("session", "archived_zero")]
    [InlineData("session", "archived_even")]
    [InlineData("repository", "null_list")]
    [InlineData("repository", "null_item")]
    [InlineData("repository", "missing")]
    [InlineData("repository", "extra")]
    [InlineData("repository", "duplicate")]
    [InlineData("repository", "substitution")]
    [InlineData("repository", "noncanonical")]
    [InlineData("repository", "undefined_state")]
    [InlineData("repository", "active_negative")]
    [InlineData("repository", "active_odd")]
    [InlineData("repository", "archived_negative")]
    [InlineData("repository", "archived_zero")]
    [InlineData("repository", "archived_even")]
    public async Task ReadAsync_RejectsEachInvalidArchiveCollectionWithOneFixedNoDataFailure(
        string target,
        string corruption)
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryA, LocatorA, revision: 1);
        database.AddRepository(RepositoryB, LocatorB, revision: 1);
        var sessionIds = new[] { SessionId(1), SessionId(2) };
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            sessionIds.Select(id => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(id, corruption)).ToArray(),
            token));
        var archive = new FakeArchiveContributor(async (capability, input, token) =>
        {
            await ReadArchiveTable(capability, token);
            return CorruptArchiveFacts(input, target, corruption);
        });
        var composeCalls = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(
                database.Path,
                session,
                archive,
                compositionObserver: _ => composeCalls++)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));

        Assert.Equal("local_archive_fact_contribution_invalid", exception.Message);
        Assert.Equal(0, composeCalls);
    }

    [Fact]
    public async Task ReadAsync_CopiesHostileArchiveListsByOneCountAndOneIndexedReadWithoutEnumeration()
    {
        using var database = new ScopeDatabase();
        database.AddRepository(RepositoryA, LocatorA, revision: 1);
        database.AddRepository(RepositoryB, LocatorB, revision: 1);
        database.AddRepository(RepositoryC, LocatorC, revision: 1);
        var sessionIds = new[] { SessionId(1), SessionId(2), SessionId(3) };
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            sessionIds.Select(id => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(id, "hostile-list")).ToArray(),
            token));
        OneReadList<LocalArchiveSessionFact>? hostileSessions = null;
        OneReadList<LocalArchiveRepositoryFact>? hostileRepositories = null;
        var archive = new FakeArchiveContributor(async (capability, _, token) =>
        {
            await ReadArchiveTable(capability, token);
            hostileSessions = new(
            [
                new(sessionIds[2], LocalArchiveState.Archived, 3),
                new(sessionIds[0], LocalArchiveState.Active, 0),
                new(sessionIds[1], LocalArchiveState.Active, 2),
            ]);
            hostileRepositories = new(
            [
                new(RepositoryC, LocalArchiveState.Active, 4),
                new(RepositoryA, LocalArchiveState.Archived, 5),
                new(RepositoryB, LocalArchiveState.Active, 0),
            ]);
            return new(hostileSessions, hostileRepositories);
        });

        var snapshot = await new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive)
            .ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        Assert.Equal(1, hostileSessions!.CountReads);
        Assert.Equal([1, 1, 1], hostileSessions.IndexReads);
        Assert.Equal(0, hostileSessions.EnumerationAttempts);
        Assert.Equal(1, hostileRepositories!.CountReads);
        Assert.Equal([1, 1, 1], hostileRepositories.IndexReads);
        Assert.Equal(0, hostileRepositories.EnumerationAttempts);
        Assert.Equal(
            [
                (SessionId(1), LocalArchiveState.Active, 0L),
                (SessionId(2), LocalArchiveState.Active, 2L),
                (SessionId(3), LocalArchiveState.Archived, 3L),
            ],
            snapshot.Sessions.Select(item => (item.SessionId, item.ArchiveState, item.ArchiveRevision)));
        Assert.Equal(
            [
                (RepositoryA, LocalArchiveState.Archived, 5L),
                (RepositoryB, LocalArchiveState.Active, 0L),
                (RepositoryC, LocalArchiveState.Active, 4L),
            ],
            snapshot.Repositories.Select(item => (item.RepositoryId, item.ArchiveState, item.ArchiveRevision)));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("repository")]
    public async Task ReadAsync_ObservesCancellationDuringEachArchiveFreezeBeforeComposition(string target)
    {
        using var database = new ScopeDatabase();
        using var cancellation = new CancellationTokenSource();
        database.AddRepository(RepositoryA, LocatorA, revision: 1);
        database.AddRepository(RepositoryB, LocatorB, revision: 1);
        var sessionIds = new[] { SessionId(1), SessionId(2) };
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            sessionIds.Select(id => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(id, "freeze-cancel")).ToArray(),
            token));
        var archive = new FakeArchiveContributor(async (capability, input, token) =>
        {
            await ReadArchiveTable(capability, token);
            IReadOnlyList<LocalArchiveSessionFact> sessions = input.SessionIds
                .Select(id => new LocalArchiveSessionFact(id, LocalArchiveState.Active, 0))
                .ToArray();
            IReadOnlyList<LocalArchiveRepositoryFact> repositories = input.RepositoryIds
                .Select(id => new LocalArchiveRepositoryFact(id, LocalArchiveState.Active, 0))
                .ToArray();
            if (target == "session")
                sessions = new CancelingIndexedList<LocalArchiveSessionFact>(sessions, cancellation);
            else
                repositories = new CancelingIndexedList<LocalArchiveRepositoryFact>(repositories, cancellation);
            return new(sessions, repositories);
        });
        var composeCalls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await new SqliteLocalRepositoryScopeSnapshotService(
                database.Path,
                session,
                archive,
                compositionObserver: _ => composeCalls++)
                .ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellation.Token));

        Assert.Equal(0, composeCalls);
    }

    [Theory]
    [InlineData("noncanonical")]
    [InlineData("duplicate")]
    public async Task ReadAsync_RejectsInvalidFrozenCatalogBeforeCallingArchiveContributor(string corruption)
    {
        using var database = new ScopeDatabase();
        if (corruption == "noncanonical")
            database.AddRawRepository("01900000-0000-7000-8000-00000000000A", "Repository", 1, LocatorA);
        else
            database.InstallDuplicateRepositoryRows(RepositoryA);
        var session = new FakeSessionContributor((capability, _, token) => ReadSessionContribution(
            capability,
            [new FakeSessionRow(SessionId(1), "catalog-order")],
            token));
        var archive = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));
        var service = new SqliteLocalRepositoryScopeSnapshotService(database.Path, session, archive);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), CancellationToken.None));

        Assert.Equal("local_repository_catalog_snapshot_invalid", exception.Message);
        Assert.Equal(0, archive.CallCount);
    }

    [Fact]
    public async Task ReadAsync_ObservesCancellationWhileCatalogRowsAreBeingRead()
    {
        using var database = new ScopeDatabase();
        using var cancellation = new CancellationTokenSource();
        SqliteConnection? connection = null;
        var rows = Enumerable.Range(1, 100)
            .Select(index => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(SessionId(index), "catalog-cancel"))
            .ToArray();
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (currentConnection, transaction, innerToken) =>
            {
                connection = currentConnection;
                return await ScalarAsync(currentConnection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
            }, token);
            return new(rows);
        });
        var archive = new FakeArchiveContributor((_, _, _) => throw new InvalidOperationException("must not run"));
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            catalogRowObserver: (query, index) =>
            {
                if (query == "assignments" && index == 10)
                    cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellation.Token));
        Assert.Equal(0, archive.CallCount);
        Assert.Equal(ConnectionState.Closed, connection!.State);
    }

    [Fact]
    public async Task ReadAsync_ObservesCancellationInsideArchiveContributorPhase()
    {
        using var database = new ScopeDatabase();
        using var cancellation = new CancellationTokenSource();
        SqliteConnection? connection = null;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (currentConnection, transaction, innerToken) =>
            {
                connection = currentConnection;
                return await ScalarAsync(currentConnection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
            }, token);
            return new([new FakeSessionRow(SessionId(1), "archive-cancel")]);
        });
        var archive = new FakeArchiveContributor(async (capability, input, token) =>
        {
            await ReadArchiveTable(capability, token);
            cancellation.Cancel();
            return ActiveArchiveFacts(input);
        });
        var composeCalls = 0;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            compositionObserver: _ => composeCalls++);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellation.Token));
        Assert.Equal(0, composeCalls);
        Assert.Equal(ConnectionState.Closed, connection!.State);
    }

    [Fact]
    public async Task ReadAsync_ObservesCancellationDuringFinalCompositionAndDisposesSnapshot()
    {
        using var database = new ScopeDatabase();
        using var cancellation = new CancellationTokenSource();
        SqliteConnection? connection = null;
        var rows = Enumerable.Range(1, 100)
            .Select(index => (ILocalRepositorySessionSnapshotRow)new FakeSessionRow(SessionId(index), "compose-cancel"))
            .ToArray();
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (currentConnection, transaction, innerToken) =>
            {
                connection = currentConnection;
                return await ScalarAsync(currentConnection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
            }, token);
            return new(rows);
        });
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            compositionObserver: index =>
            {
                if (index == 10)
                    cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellation.Token));

        Assert.Equal(ConnectionState.Closed, connection!.State);
    }

    [Fact]
    public async Task ReadAsync_ObservesCancellationAtFinalReturnFence()
    {
        using var database = new ScopeDatabase();
        using var cancellation = new CancellationTokenSource();
        SqliteConnection? connection = null;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (currentConnection, transaction, innerToken) =>
            {
                connection = currentConnection;
                return await ScalarAsync(currentConnection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
            }, token);
            return new([new FakeSessionRow(SessionId(1), "final-return")]);
        });
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            finalReturnObserver: cancellation.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellation.Token));
        Assert.Equal(ConnectionState.Closed, connection!.State);
    }

    [Fact]
    public async Task ReadAsync_ObservesCancellationDuringRepositoryProjection()
    {
        using var database = new ScopeDatabase();
        using var cancellation = new CancellationTokenSource();
        var sessionId = SessionId(999);
        database.AddCandidateSet(sessionId, 100);
        database.SetRevision(sessionId, 1);
        SqliteConnection? connection = null;
        var session = new FakeSessionContributor(async (capability, _, token) =>
        {
            await capability.ReadAsync(async (currentConnection, transaction, innerToken) =>
            {
                connection = currentConnection;
                return await ScalarAsync(currentConnection, transaction, "SELECT value FROM session_snapshot_source;", innerToken);
            }, token);
            return new([new FakeSessionRow(sessionId, "repository-cancel")]);
        });
        var archive = new FakeArchiveContributor((capability, input, token) => ReadArchiveContribution(capability, input, token));
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            database.Path,
            session,
            archive,
            compositionObserver: index =>
            {
                if (index == 11)
                    cancellation.Cancel();
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.ReadAsync(new(LocalRepositoryScopeKind.All, null), cancellation.Token));
        Assert.Equal(ConnectionState.Closed, connection!.State);
    }

    private static string SessionId(int value) => $"01900000-0000-7000-8000-{value:D12}";

    private static async ValueTask<long> ScalarAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object Scalar(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command.ExecuteScalar()!;
    }

    private static async ValueTask<LocalRepositorySessionContribution> ReadSessionContribution(
        ILocalRepositoryReadTransaction capability,
        IReadOnlyList<ILocalRepositorySessionSnapshotRow> rows,
        CancellationToken token)
    {
        await capability.ReadAsync((connection, transaction, innerToken) =>
            ScalarAsync(connection, transaction, "SELECT value FROM session_snapshot_source;", innerToken), token);
        return new(rows);
    }

    private static async ValueTask ReadArchiveTable(
        ILocalRepositoryReadTransaction capability,
        CancellationToken token) =>
        await capability.ReadAsync((connection, transaction, innerToken) =>
            ScalarAsync(connection, transaction, "SELECT value FROM archive_snapshot_source;", innerToken), token);

    private static async ValueTask<LocalArchiveFactContribution> ReadArchiveContribution(
        ILocalRepositoryReadTransaction capability,
        LocalRepositoryArchiveInput input,
        CancellationToken token)
    {
        await ReadArchiveTable(capability, token);
        return ActiveArchiveFacts(input);
    }

    private static LocalArchiveFactContribution ActiveArchiveFacts(LocalRepositoryArchiveInput input) =>
        new(
            input.SessionIds.Select(id => new LocalArchiveSessionFact(id, LocalArchiveState.Active, 0)).ToArray(),
            input.RepositoryIds.Select(id => new LocalArchiveRepositoryFact(id, LocalArchiveState.Active, 0)).ToArray());

    private static LocalArchiveFactContribution CorruptArchiveFacts(
        LocalRepositoryArchiveInput input,
        string target,
        string corruption)
    {
        IReadOnlyList<LocalArchiveSessionFact> sessions = input.SessionIds
            .Select(id => new LocalArchiveSessionFact(id, LocalArchiveState.Active, 0))
            .ToArray();
        IReadOnlyList<LocalArchiveRepositoryFact> repositories = input.RepositoryIds
            .Select(id => new LocalArchiveRepositoryFact(id, LocalArchiveState.Active, 0))
            .ToArray();
        if (target == "session")
        {
            sessions = corruption switch
            {
                "null_list" => null!,
                "null_item" => [null!, sessions[1]],
                "missing" => [sessions[0]],
                "extra" => [sessions[0], sessions[1], new(SessionId(3), LocalArchiveState.Active, 0)],
                "duplicate" => [sessions[0], new(input.SessionIds[0], LocalArchiveState.Active, 0)],
                "substitution" => [sessions[0], new(SessionId(3), LocalArchiveState.Active, 0)],
                "noncanonical" => [sessions[0], new("01900000-0000-7000-8000-00000000000A", LocalArchiveState.Active, 0)],
                "undefined_state" => [new(input.SessionIds[0], (LocalArchiveState)99, 0), sessions[1]],
                "active_negative" => [new(input.SessionIds[0], LocalArchiveState.Active, -2), sessions[1]],
                "active_odd" => [new(input.SessionIds[0], LocalArchiveState.Active, 1), sessions[1]],
                "archived_negative" => [new(input.SessionIds[0], LocalArchiveState.Archived, -1), sessions[1]],
                "archived_zero" => [new(input.SessionIds[0], LocalArchiveState.Archived, 0), sessions[1]],
                "archived_even" => [new(input.SessionIds[0], LocalArchiveState.Archived, 2), sessions[1]],
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            };
        }
        else
        {
            repositories = corruption switch
            {
                "null_list" => null!,
                "null_item" => [null!, repositories[1]],
                "missing" => [repositories[0]],
                "extra" => [repositories[0], repositories[1], new(RepositoryC, LocalArchiveState.Active, 0)],
                "duplicate" => [repositories[0], new(input.RepositoryIds[0], LocalArchiveState.Active, 0)],
                "substitution" => [repositories[0], new(RepositoryC, LocalArchiveState.Active, 0)],
                "noncanonical" => [repositories[0], new("01900000-0000-7000-8000-00000000000A", LocalArchiveState.Active, 0)],
                "undefined_state" => [new(input.RepositoryIds[0], (LocalArchiveState)99, 0), repositories[1]],
                "active_negative" => [new(input.RepositoryIds[0], LocalArchiveState.Active, -2), repositories[1]],
                "active_odd" => [new(input.RepositoryIds[0], LocalArchiveState.Active, 1), repositories[1]],
                "archived_negative" => [new(input.RepositoryIds[0], LocalArchiveState.Archived, -1), repositories[1]],
                "archived_zero" => [new(input.RepositoryIds[0], LocalArchiveState.Archived, 0), repositories[1]],
                "archived_even" => [new(input.RepositoryIds[0], LocalArchiveState.Archived, 2), repositories[1]],
                _ => throw new ArgumentOutOfRangeException(nameof(corruption)),
            };
        }
        return new(sessions, repositories);
    }

    private sealed record FakeSessionRow(string SessionId, string OpaqueValue) : ILocalRepositorySessionSnapshotRow;

    private sealed class MutableSessionRow(string first, string later) : ILocalRepositorySessionSnapshotRow
    {
        public int GetterCount { get; private set; }
        public string SessionId => ++GetterCount == 1 ? first : later;
    }

    private sealed class OneReadList<T>(IReadOnlyList<T> source) : IReadOnlyList<T>
    {
        private readonly T[] items = source.ToArray();
        private readonly int[] indexReads = new int[source.Count];

        public int CountReads { get; private set; }
        public int EnumerationAttempts { get; private set; }
        public IReadOnlyList<int> IndexReads => indexReads;

        public int Count
        {
            get
            {
                if (++CountReads != 1)
                    throw new InvalidOperationException("Count was reread");
                return items.Length;
            }
        }

        public T this[int index]
        {
            get
            {
                if (++indexReads[index] != 1)
                    throw new InvalidOperationException("Item was reread");
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            EnumerationAttempts++;
            throw new InvalidOperationException("Enumeration is not allowed");
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelingIndexedList<T>(
        IReadOnlyList<T> items,
        CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index]
        {
            get
            {
                if (index == items.Count - 1)
                    cancellation.Cancel();
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            for (var index = 0; index < items.Count; index++)
                yield return this[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class SyntheticFailureException : Exception;

    private sealed class FakeSessionContributor(
        Func<ILocalRepositoryReadTransaction, LocalRepositoryScopeRequest, CancellationToken, ValueTask<LocalRepositorySessionContribution>> read)
        : ILocalRepositorySessionSnapshotContributor
    {
        public int CallCount { get; private set; }
        public ILocalRepositoryReadTransaction? LastCapability { get; private set; }

        public ValueTask<LocalRepositorySessionContribution> ReadAsync(
            ILocalRepositoryReadTransaction transaction,
            LocalRepositoryScopeRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastCapability = transaction;
            return read(transaction, request, cancellationToken);
        }
    }

    private sealed class FakeArchiveContributor(
        Func<ILocalRepositoryReadTransaction, LocalRepositoryArchiveInput, CancellationToken, ValueTask<LocalArchiveFactContribution>> read)
        : ILocalArchiveFactSnapshotContributor
    {
        public int CallCount { get; private set; }
        public ILocalRepositoryReadTransaction? LastCapability { get; private set; }

        public ValueTask<LocalArchiveFactContribution> ReadAsync(
            ILocalRepositoryReadTransaction transaction,
            LocalRepositoryArchiveInput input,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastCapability = transaction;
            return read(transaction, input, cancellationToken);
        }
    }

    private sealed class EmptyDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repository-scope-empty-{Guid.NewGuid():N}");
        public EmptyDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "scope.sqlite");
            using var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE session_snapshot_source(value INTEGER NOT NULL); INSERT INTO session_snapshot_source VALUES(1);";
            command.ExecuteNonQuery();
        }
        public string Path { get; }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    private sealed class ScopeDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"repository-scope-{Guid.NewGuid():N}");
        public ScopeDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "scope.sqlite");
            using var connection = Open();
            Execute(connection, """
                PRAGMA journal_mode=WAL;
                CREATE TABLE local_repositories(repository_id TEXT PRIMARY KEY,display_name TEXT NOT NULL,revision INTEGER NOT NULL);
                CREATE TABLE local_repository_locator_heads(repository_id TEXT NOT NULL,kind TEXT NOT NULL,locator_id TEXT NOT NULL,PRIMARY KEY(repository_id,kind));
                CREATE TABLE session_repository_assignment_revisions(session_id TEXT PRIMARY KEY,revision INTEGER NOT NULL);
                CREATE TABLE session_repository_manual_overrides(session_id TEXT PRIMARY KEY,state TEXT NOT NULL,repository_id TEXT NULL,revision INTEGER NOT NULL);
                CREATE TABLE session_repository_observation_contexts(context_id INTEGER PRIMARY KEY,session_id TEXT NOT NULL,admission_state TEXT NOT NULL,repository_id TEXT NULL);
                CREATE TABLE session_snapshot_source(value INTEGER NOT NULL);
                CREATE TABLE archive_snapshot_source(value INTEGER NOT NULL);
                INSERT INTO session_snapshot_source VALUES(1);
                INSERT INTO archive_snapshot_source VALUES(1);
                """);
        }

        public string Path { get; }

        public SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString());
            connection.Open();
            return connection;
        }

        public SqliteConnection CreateReadOnlyConnection() => new(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 1,
        }.ToString());

        public void AddRepository(string repositoryId, string locatorId, long revision)
            => AddRawRepository(repositoryId, $"Repository {repositoryId[^1]}", revision, locatorId);

        public void AddRawRepository(string repositoryId, string displayName, long revision, string locatorId)
        {
            using var connection = Open();
            Execute(connection, $"INSERT INTO local_repositories VALUES('{repositoryId}','{displayName}',{revision}); INSERT INTO local_repository_locator_heads VALUES('{repositoryId}','github_repository','{locatorId}');");
        }

        public void InstallDuplicateRepositoryRows(string repositoryId)
        {
            using var connection = Open();
            Execute(connection, $"""
                DROP TABLE local_repository_locator_heads;
                DROP TABLE local_repositories;
                CREATE TABLE local_repositories(repository_id TEXT NOT NULL,display_name TEXT NOT NULL,revision INTEGER NOT NULL);
                CREATE TABLE local_repository_locator_heads(repository_id TEXT NOT NULL,kind TEXT NOT NULL,locator_id TEXT NOT NULL);
                INSERT INTO local_repositories VALUES('{repositoryId}','Repository A',1);
                INSERT INTO local_repositories VALUES('{repositoryId}','Repository B',1);
                """);
        }

        public void AddCandidate(string sessionId, string repositoryId)
        {
            using var connection = Open();
            Execute(connection, $"INSERT INTO session_repository_observation_contexts(session_id,admission_state,repository_id) VALUES('{sessionId}','admitted','{repositoryId}');");
        }

        public void AddCandidateSet(string sessionId, int count)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            for (var index = 1; index <= count; index++)
            {
                var repositoryId = SessionId(index);
                var locatorId = $"01900000-0000-7001-8000-{index:D12}";
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO local_repositories VALUES($repository_id,$display_name,1);
                    INSERT INTO local_repository_locator_heads VALUES($repository_id,'github_repository',$locator_id);
                    INSERT INTO session_repository_observation_contexts(session_id,admission_state,repository_id)
                    VALUES($session_id,'admitted',$repository_id);
                    """;
                command.Parameters.AddWithValue("$repository_id", repositoryId);
                command.Parameters.AddWithValue("$display_name", $"Repository {index}");
                command.Parameters.AddWithValue("$locator_id", locatorId);
                command.Parameters.AddWithValue("$session_id", sessionId);
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }

        public void SetRevision(string sessionId, long revision)
        {
            using var connection = Open();
            Execute(connection, $"INSERT INTO session_repository_assignment_revisions VALUES('{sessionId}',{revision});");
        }

        public void SetManualOverride(string sessionId, string state, string? repositoryId, long revision)
        {
            using var connection = Open();
            var repository = repositoryId is null ? "NULL" : $"'{repositoryId}'";
            Execute(connection, $"INSERT INTO session_repository_manual_overrides VALUES('{sessionId}','{state}',{repository},{revision});");
        }

        public void SetDeleteJournalMode()
        {
            using var connection = Open();
            Execute(connection, "PRAGMA journal_mode=DELETE;");
        }

        public void UpdateSessionSource(long value)
        {
            using var connection = Open();
            Execute(connection, $"UPDATE session_snapshot_source SET value={value};");
        }

        public long ReadSessionSource()
        {
            using var connection = Open();
            return Convert.ToInt64(Scalar(connection, null, "SELECT value FROM session_snapshot_source;"), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static object Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return command.ExecuteScalar()!;
        }

        private static void Execute(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }
}

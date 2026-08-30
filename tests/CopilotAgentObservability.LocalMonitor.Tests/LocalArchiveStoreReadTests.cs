using Microsoft.Data.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Tests;

[Trait("ValidationLane", "Nightly")]
public sealed class LocalArchiveStoreReadTests
{
    private const string SessionOne = "01900000-0000-7000-8000-000000000101";
    private const string RepositoryOne = "01900000-0000-7000-8000-000000000201";
    private const string AtOne = "2026-08-14T01:02:03.0000000+00:00";

    [Fact]
    public void Read_ProvesTheParentBeforeReturningTheExactAbsentCurrentFact()
    {
        using var database = new ArchiveReadDatabase();
        database.InsertSession(SessionOne);
        var store = database.CreateStore();

        var found = store.Read(LocalArchiveTargetKind.Session, SessionOne, CancellationToken.None);
        var missing = store.Read(
            LocalArchiveTargetKind.Session,
            "01900000-0000-7000-8000-000000000102",
            CancellationToken.None);

        Assert.Null(found.Error);
        Assert.Equal(
            new LocalArchiveMutationTargetSuccess(
                SessionOne,
                LocalArchiveState.Active,
                0,
                ArchivedAt: null,
                UpdatedAt: null),
            found.Success);
        Assert.Equal(LocalArchiveStoreError.TargetNotFound, missing.Error);
        Assert.Null(missing.Success);
    }

    [Fact]
    public void Read_MissingParentWinsBeforeAnUnavailableArchiveTable()
    {
        using var database = new ArchiveReadDatabase();
        database.Execute("DROP TABLE local_archive_events; DROP TABLE local_archive_current;");

        var result = database.CreateStore().Read(
            LocalArchiveTargetKind.Session,
            SessionOne,
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.TargetNotFound, result.Error);
    }

    [Fact]
    public void Read_ReturnsAValidatedStoredHeadAndRejectsAContradictoryHead()
    {
        using var database = new ArchiveReadDatabase();
        database.InsertSession(SessionOne);
        database.InsertArchive(LocalArchiveTargetKind.Session, SessionOne, 1, AtOne);
        var store = database.CreateStore();

        var valid = store.Read(LocalArchiveTargetKind.Session, SessionOne, CancellationToken.None);
        database.Execute(
            "PRAGMA foreign_keys=OFF; PRAGMA recursive_triggers=OFF; " +
            "DROP TRIGGER local_archive_events_update_rejected; " +
            "UPDATE local_archive_events SET occurred_at='2026-08-13T01:02:03.0000000+00:00';");
        var corrupt = store.Read(LocalArchiveTargetKind.Session, SessionOne, CancellationToken.None);

        Assert.Equal(
            new LocalArchiveMutationTargetSuccess(
                SessionOne,
                LocalArchiveState.Archived,
                1,
                AtOne,
                AtOne),
            valid.Success);
        Assert.Equal(LocalArchiveStoreError.ArchiveStoreUnavailable, corrupt.Error);
        Assert.Null(corrupt.Success);
    }

    [Theory]
    [InlineData("event_beyond_current")]
    [InlineData("event_without_current")]
    public void Read_RejectsARelevantEventCurrentContradiction(string corruption)
    {
        using var database = new ArchiveReadDatabase();
        database.InsertSession(SessionOne);
        database.InsertArchive(LocalArchiveTargetKind.Session, SessionOne, 1, AtOne);
        if (corruption == "event_beyond_current")
        {
            database.Execute(
                "INSERT INTO local_archive_events(event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at) " +
                "VALUES($event,'session',$id,'restore',1,2,$at);",
                ("$event", Guid.CreateVersion7().ToString("D")), ("$id", SessionOne), ("$at", AtOne));
        }
        else
        {
            database.Execute(
                "PRAGMA foreign_keys=OFF; DROP TRIGGER local_archive_current_delete_rejected; " +
                "DELETE FROM local_archive_current WHERE target_kind='session' AND target_id=$id;",
                ("$id", SessionOne));
        }

        var result = database.CreateStore().Read(
            LocalArchiveTargetKind.Session,
            SessionOne,
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.ArchiveStoreUnavailable, result.Error);
        Assert.Null(result.Success);
    }

    [Fact]
    public void ListArchived_OrdersByArchivedAtThenIdAndUsesTheEmittedLimitAsLookaheadBoundary()
    {
        using var database = new ArchiveReadDatabase();
        var first = "01900000-0000-7000-8000-000000000211";
        var second = "01900000-0000-7000-8000-000000000212";
        var third = "01900000-0000-7000-8000-000000000213";
        foreach (var id in new[] { first, second, third })
            database.InsertRepository(id);
        database.InsertArchive(LocalArchiveTargetKind.Repository, first, 1, "2026-08-14T01:02:01.0000000+00:00");
        database.InsertArchive(LocalArchiveTargetKind.Repository, second, 1, AtOne);
        database.InsertArchive(LocalArchiveTargetKind.Repository, third, 1, AtOne);

        var page = database.CreateStore().ListArchived(
            LocalArchiveTargetKind.Repository,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 2,
            CancellationToken.None);

        Assert.Null(page.Error);
        Assert.True(page.Success!.HasMore);
        Assert.Equal(new[] { third, second }, page.Success.Items.Select(item => item.TargetId));
    }

    [Fact]
    public void ListArchived_UsesRevisionAsHistoryAuthorityWhenEventTimestampsMoveBackward()
    {
        using var database = new ArchiveReadDatabase();
        database.InsertSession(SessionOne);
        database.InsertArchive(LocalArchiveTargetKind.Session, SessionOne, 3, AtOne);

        var page = database.CreateStore().ListArchived(
            LocalArchiveTargetKind.Session,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 1,
            CancellationToken.None);

        Assert.Null(page.Error);
        Assert.Equal(3, page.Success!.Items.Single().Revision);
    }

    [Theory]
    [InlineData("missing_head")]
    [InlineData("contradictory_head")]
    [InlineData("event_beyond_current")]
    public void ListArchived_RejectsCurrentHeadCorruptionWithoutReturningAPartialPage(string corruption)
    {
        using var database = new ArchiveReadDatabase();
        database.InsertRepository(RepositoryOne);
        database.InsertArchive(LocalArchiveTargetKind.Repository, RepositoryOne, 1, AtOne);
        if (corruption == "missing_head")
        {
            database.Execute(
                "PRAGMA foreign_keys=OFF; DROP TRIGGER local_archive_events_delete_rejected; " +
                "DELETE FROM local_archive_events WHERE target_kind='repository' AND target_id=$id;",
                ("$id", RepositoryOne));
        }
        else if (corruption == "contradictory_head")
        {
            database.Execute(
                "DROP TRIGGER local_archive_events_update_rejected; " +
                "UPDATE local_archive_events SET occurred_at='2026-08-13T01:02:03.0000000+00:00' " +
                "WHERE target_kind='repository' AND target_id=$id;",
                ("$id", RepositoryOne));
        }
        else
        {
            database.Execute(
                "INSERT INTO local_archive_events(event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at) " +
                "VALUES($event,'repository',$id,'restore',1,2,$at);",
                ("$event", Guid.CreateVersion7().ToString("D")), ("$id", RepositoryOne), ("$at", AtOne));
        }

        var result = database.CreateStore().ListArchived(
            LocalArchiveTargetKind.Repository,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.ArchiveStoreUnavailable, result.Error);
        Assert.Null(result.Success);
    }

    [Fact]
    public void ListArchived_KeysetResumeContinuesStrictlyAcrossATimestampTieAndEarlierTimestamp()
    {
        using var database = new ArchiveReadDatabase();
        var later = "01900000-0000-7000-8000-000000000221";
        var tieHigh = "01900000-0000-7000-8000-000000000224";
        var tieLow = "01900000-0000-7000-8000-000000000223";
        var earlier = "01900000-0000-7000-8000-000000000222";
        foreach (var id in new[] { later, tieHigh, tieLow, earlier })
            database.InsertRepository(id);
        database.InsertArchive(LocalArchiveTargetKind.Repository, later, 1, "2026-08-14T01:02:04.0000000+00:00");
        database.InsertArchive(LocalArchiveTargetKind.Repository, tieHigh, 1, AtOne);
        database.InsertArchive(LocalArchiveTargetKind.Repository, tieLow, 1, AtOne);
        database.InsertArchive(LocalArchiveTargetKind.Repository, earlier, 1, "2026-08-14T01:02:02.0000000+00:00");
        var store = database.CreateStore();

        var first = store.ListArchived(
            LocalArchiveTargetKind.Repository, null, null, 2, CancellationToken.None);
        var second = store.ListArchived(
            LocalArchiveTargetKind.Repository, AtOne, tieHigh, 2, CancellationToken.None);
        var terminal = store.ListArchived(
            LocalArchiveTargetKind.Repository,
            "2026-08-14T01:02:02.0000000+00:00",
            earlier,
            2,
            CancellationToken.None);

        Assert.Equal(new[] { later, tieHigh }, first.Success!.Items.Select(item => item.TargetId));
        Assert.True(first.Success.HasMore);
        Assert.Equal(new[] { tieLow, earlier }, second.Success!.Items.Select(item => item.TargetId));
        Assert.False(second.Success.HasMore);
        Assert.Empty(terminal.Success!.Items);
        Assert.Equal(
            new[] { later, tieHigh, tieLow, earlier },
            first.Success.Items.Concat(second.Success.Items).Select(item => item.TargetId));
    }

    [Fact]
    public void ListArchived_ProvesAllTwoHundredAndOneLookaheadParentsInSortedChunks()
    {
        using var database = new ArchiveReadDatabase();
        var ids = Enumerable.Range(1, 201)
            .Select(index => $"01900000-0000-7000-8000-{index:D12}")
            .ToArray();
        foreach (var id in ids)
        {
            database.InsertRepository(id);
            database.InsertArchive(LocalArchiveTargetKind.Repository, id, 1, AtOne);
        }
        var authority = new RecordingRepositoryAuthority();

        var result = database.CreateStore(authority).ListArchived(
            LocalArchiveTargetKind.Repository,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 200,
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(new[] { 200, 1 }, authority.Calls.Select(call => call.Count));
        Assert.All(authority.Calls, call => Assert.Equal(call.Order(StringComparer.Ordinal), call));
        Assert.Contains(ids[0], authority.Calls.SelectMany(call => call));
    }

    [Fact]
    public void ListArchived_PartialParentProofReturnsNoPartialPage()
    {
        using var database = new ArchiveReadDatabase();
        database.InsertRepository(RepositoryOne);
        database.InsertArchive(LocalArchiveTargetKind.Repository, RepositoryOne, 1, AtOne);
        var authority = new RecordingRepositoryAuthority(returnPartial: true);

        var result = database.CreateStore(authority).ListArchived(
            LocalArchiveTargetKind.Repository,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.ArchiveStoreUnavailable, result.Error);
        Assert.Null(result.Success);
    }

    [Fact]
    public void ListArchived_EmptyPageDoesNotCallTheParentAuthority()
    {
        using var database = new ArchiveReadDatabase();
        var authority = new RecordingRepositoryAuthority();

        var result = database.CreateStore(authority).ListArchived(
            LocalArchiveTargetKind.Repository,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Empty(result.Success!.Items);
        Assert.False(result.Success.HasMore);
        Assert.Empty(authority.Calls);
    }

    [Fact]
    public void Read_MapsOneBusyAttemptAndPreservesCancellation()
    {
        using var database = new ArchiveReadDatabase();
        database.InsertRepository(RepositoryOne);
        var authority = new BusyRepositoryAuthority();

        var busy = database.CreateStore(authority).Read(
            LocalArchiveTargetKind.Repository,
            RepositoryOne,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(LocalArchiveStoreError.PersistenceBusy, busy.Error);
        Assert.Equal(1, authority.Calls);
        Assert.Throws<OperationCanceledException>(() => database.CreateStore().Read(
            LocalArchiveTargetKind.Session,
            SessionOne,
            cancellation.Token));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void ListArchived_MapsBusyOrLockedAfterExactlyOneParentAttempt(int primaryCode)
    {
        using var database = new ArchiveReadDatabase();
        database.InsertRepository(RepositoryOne);
        database.InsertArchive(LocalArchiveTargetKind.Repository, RepositoryOne, 1, AtOne);
        var authority = new BusyRepositoryAuthority(primaryCode);

        var result = database.CreateStore(authority).ListArchived(
            LocalArchiveTargetKind.Repository,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 50,
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.PersistenceBusy, result.Error);
        Assert.Null(result.Success);
        Assert.Equal(1, authority.Calls);
    }

    [Fact]
    public void ListArchived_PreservesCancellationObservedDuringParentProof()
    {
        using var database = new ArchiveReadDatabase();
        database.InsertRepository(RepositoryOne);
        database.InsertArchive(LocalArchiveTargetKind.Repository, RepositoryOne, 1, AtOne);
        using var cancellation = new CancellationTokenSource();
        var authority = new CancellingRepositoryAuthority(cancellation);

        Assert.Throws<OperationCanceledException>(() => database.CreateStore(authority).ListArchived(
            LocalArchiveTargetKind.Repository,
            afterArchivedAt: null,
            afterTargetId: null,
            limit: 50,
            cancellation.Token));
        Assert.Equal(1, authority.Calls);
    }

    private sealed class BusyRepositoryAuthority(int primaryCode = 5)
        : ILocalRepositoryTargetExistenceAuthority
    {
        internal int Calls { get; private set; }

        public IReadOnlyList<string> ReadExisting(
            SqliteConnection openConnection,
            SqliteTransaction exactTransaction,
            IReadOnlyList<string> canonicalRepositoryIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new SqliteException("synthetic busy", primaryCode);
        }
    }

    private sealed class CancellingRepositoryAuthority(CancellationTokenSource cancellation)
        : ILocalRepositoryTargetExistenceAuthority
    {
        internal int Calls { get; private set; }

        public IReadOnlyList<string> ReadExisting(
            SqliteConnection openConnection,
            SqliteTransaction exactTransaction,
            IReadOnlyList<string> canonicalRepositoryIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException();
        }
    }

    private sealed class RecordingRepositoryAuthority(bool returnPartial = false)
        : ILocalRepositoryTargetExistenceAuthority
    {
        internal List<IReadOnlyList<string>> Calls { get; } = [];

        public IReadOnlyList<string> ReadExisting(
            SqliteConnection openConnection,
            SqliteTransaction exactTransaction,
            IReadOnlyList<string> canonicalRepositoryIds,
            CancellationToken cancellationToken)
        {
            var frozen = canonicalRepositoryIds.ToArray();
            Calls.Add(frozen);
            return returnPartial ? frozen.Skip(1).ToArray() : frozen;
        }
    }

    private sealed class ArchiveReadDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"local-archive-reads-{Guid.NewGuid():N}");

        internal ArchiveReadDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "archive.sqlite");
            new SqliteSessionStore(Path).CreateSchema();
            using var connection = Open();
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
            LocalArchiveSchemaV1.Ensure(connection);
        }

        internal string Path { get; }

        internal SqliteLocalArchiveStore CreateStore(
            ILocalRepositoryTargetExistenceAuthority? authority = null,
            Func<SqliteConnection>? connectionFactory = null) =>
            new(
                Path,
                authority ?? SqliteLocalRepositoryTargetExistenceAuthority.Instance,
                LocalArchiveSessionTargetExistenceAuthority.Instance,
                connectionFactory);

        internal SqliteConnection NewConnection(bool sharedCache = false) => new(
            new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
                Cache = sharedCache ? SqliteCacheMode.Shared : SqliteCacheMode.Private,
                DefaultTimeout = 0,
            }.ToString());

        internal SqliteConnection Open(bool sharedCache = false)
        {
            var connection = NewConnection(sharedCache);
            connection.Open();
            return connection;
        }

        internal void InsertSession(string id) => Execute(
            "INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) " +
            "VALUES($id,'completed','full',$at,'not_captured',$at,$at);",
            ("$id", id), ("$at", AtOne));

        internal void InsertRepository(string id) => Execute(
            "INSERT INTO local_repositories(repository_id,display_name,revision,created_at,updated_at) " +
            "VALUES($id,'Synthetic',1,$at,$at);",
            ("$id", id), ("$at", AtOne));

        internal void InsertArchive(
            LocalArchiveTargetKind kind,
            string id,
            long revision,
            string currentAt)
        {
            var kindText = kind == LocalArchiveTargetKind.Session ? "session" : "repository";
            var state = revision % 2 == 1 ? "archived" : "active";
            Execute(
                "INSERT INTO local_archive_current(target_kind,target_id,state,revision,archived_at,updated_at) " +
                "VALUES($kind,$id,$state,$revision,$archived,$updated);",
                ("$kind", kindText), ("$id", id), ("$state", state), ("$revision", revision),
                ("$archived", state == "archived" ? currentAt : null), ("$updated", currentAt));
            for (var value = 1L; value <= revision; value++)
            {
                var eventAt = value == revision
                    ? currentAt
                    : $"2026-08-{14 - value:D2}T01:02:03.0000000+00:00";
                Execute(
                    "INSERT INTO local_archive_events(event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at) " +
                    "VALUES($event,$kind,$id,$action,$previous,$revision,$at);",
                    ("$event", Guid.CreateVersion7().ToString("D")), ("$kind", kindText), ("$id", id),
                    ("$action", value % 2 == 1 ? "archive" : "restore"), ("$previous", value - 1),
                    ("$revision", value), ("$at", eventAt));
            }
        }

        internal void Execute(string sql, params (string Name, object? Value)[] parameters)
        {
            using var connection = Open();
            Execute(connection, transaction: null, sql, parameters);
        }

        internal void Execute(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            string sql,
            params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}

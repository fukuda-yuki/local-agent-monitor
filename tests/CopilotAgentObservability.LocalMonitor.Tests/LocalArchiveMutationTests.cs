using System.Text;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveMutationTests
{
    private const string First = "01890f65-4c31-7f42-8a7d-111111111111";
    private const string Second = "01890f65-4c31-7f42-8a7d-222222222222";
    private const string Now = "2026-08-09T12:34:56.1234567+00:00";

    [Fact]
    public void Mutate_AppliesBatchAtomicallyWithOneInstantAndOriginalResponseOrder()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        database.InsertSession(Second);
        var writerCalls = 0;

        var result = database.CreateStore().Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [new(Second, 0), new(First, 0)],
            success =>
            {
                writerCalls++;
                Assert.Equal([Second, First], success.Targets.Select(target => target.TargetId));
                Assert.All(success.Targets, target => Assert.Equal(Now, target.UpdatedAt));
                return Encoding.UTF8.GetBytes("entity");
            },
            CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal("entity", Encoding.UTF8.GetString(result.Success!.Entity.Span));
        Assert.Equal(1, writerCalls);
        Assert.Equal(2, database.EventCount());
        Assert.Equal(2, database.DistinctEventIdCount());
        Assert.Equal(("archived", 1L, Now), database.Current(First));
        Assert.Equal(("archived", 1L, Now), database.Current(Second));
    }

    [Fact]
    public void Mutate_NoOpAndAdjacentSemanticRetrySucceedWithoutWriting()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        database.InsertSession(Second);
        database.InsertHistory(LocalArchiveTargetKind.Session, First, 1, Now);
        database.InsertHistory(LocalArchiveTargetKind.Session, Second, 1, Now);

        var noOp = database.CreateStore().Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [new(First, 1)],
            _ => "noop"u8.ToArray(),
            CancellationToken.None);
        var retry = database.CreateStore().Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [new(Second, 0)],
            _ => "retry"u8.ToArray(),
            CancellationToken.None);

        Assert.Equal("noop", Encoding.UTF8.GetString(noOp.Success!.Entity.Span));
        Assert.Equal("retry", Encoding.UTF8.GetString(retry.Success!.Entity.Span));
        Assert.Equal(2, database.EventCount());
    }

    [Fact]
    public void Mutate_RejectsStaleAndApplySemanticRetryMixtureWithoutWriting()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        database.InsertSession(Second);
        database.InsertHistory(LocalArchiveTargetKind.Session, Second, 1, Now);

        var mixed = database.CreateStore().Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [new(First, 0), new(Second, 0)],
            _ => throw new InvalidOperationException("writer must not run"),
            CancellationToken.None);
        var stale = database.CreateStore().Mutate(
            LocalArchiveAction.Restore,
            LocalArchiveTargetKind.Session,
            [new(Second, 0)],
            _ => throw new InvalidOperationException("writer must not run"),
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.RevisionConflict, mixed.Error);
        Assert.Equal(LocalArchiveStoreError.RevisionConflict, stale.Error);
        Assert.Equal(1, database.EventCount());
        Assert.Null(database.Current(First));
    }

    [Fact]
    public void Mutate_ProvesEveryParentBeforeReadingArchiveTables()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        database.InsertHistory(LocalArchiveTargetKind.Session, Second, 1, Now);

        var result = database.CreateStore().Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [new(First, 0), new(Second, 0)],
            _ => "unused"u8.ToArray(),
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.TargetNotFound, result.Error);
    }

    [Fact]
    public void Mutate_RejectsContradictoryCompleteHistoryWithoutWriting()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        database.InsertHistory(LocalArchiveTargetKind.Session, First, 1, Now);
        database.Execute(
            "DROP TRIGGER local_archive_events_update_rejected; " +
            "UPDATE local_archive_events SET occurred_at='2026-08-08T01:02:03.0000000+00:00';");

        var result = database.CreateStore().Mutate(
            LocalArchiveAction.Restore,
            LocalArchiveTargetKind.Session,
            [new(First, 1)],
            _ => "unused"u8.ToArray(),
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.ArchiveStoreUnavailable, result.Error);
        Assert.Equal(1, database.EventCount());
    }

    [Fact]
    public void Mutate_RejectsRevisionExhaustionAsUnavailable()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(First);
        database.InsertCurrentWithoutHistory(LocalArchiveTargetKind.Session, First, long.MaxValue, Now);

        var result = database.CreateStore().Mutate(
            LocalArchiveAction.Restore,
            LocalArchiveTargetKind.Session,
            [new(First, long.MaxValue)],
            _ => "unused"u8.ToArray(),
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.ArchiveStoreUnavailable, result.Error);
    }
}

internal sealed class LocalArchiveMutationDatabase : IDisposable
{
    internal const string FixedNow = "2026-08-09T12:34:56.1234567+00:00";
    private readonly string directory = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        $"local-archive-mutations-{Guid.NewGuid():N}");

    internal LocalArchiveMutationDatabase()
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
            new FixedTimeProvider(DateTimeOffset.Parse(FixedNow)),
            value => Guid.CreateVersion7(value).ToString("D"),
            connectionFactory);

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Pooling = false,
            DefaultTimeout = 0,
        }.ToString());
        connection.Open();
        return connection;
    }

    internal void InsertSession(string id) => Execute(
        "INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) " +
        "VALUES($id,'completed','full',$at,'not_captured',$at,$at);",
        ("$id", id), ("$at", FixedNow));

    internal void InsertHistory(LocalArchiveTargetKind kind, string id, long revision, string at)
    {
        InsertCurrentWithoutHistory(kind, id, revision, at);
        for (var value = 1L; value <= revision; value++)
        {
            Execute(
                "INSERT INTO local_archive_events(event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at) " +
                "VALUES($event,$kind,$id,$action,$previous,$revision,$at);",
                ("$event", Guid.CreateVersion7().ToString("D")), ("$kind", Kind(kind)), ("$id", id),
                ("$action", value % 2 == 1 ? "archive" : "restore"), ("$previous", value - 1),
                ("$revision", value), ("$at", value == revision ? at : "2026-08-08T01:02:03.0000000+00:00"));
        }
    }

    internal void InsertCurrentWithoutHistory(LocalArchiveTargetKind kind, string id, long revision, string at) => Execute(
        "INSERT INTO local_archive_current(target_kind,target_id,state,revision,archived_at,updated_at) " +
        "VALUES($kind,$id,$state,$revision,$archived,$at);",
        ("$kind", Kind(kind)), ("$id", id), ("$state", revision % 2 == 1 ? "archived" : "active"),
        ("$revision", revision), ("$archived", revision % 2 == 1 ? at : null), ("$at", at));

    internal int EventCount() => Scalar<int>("SELECT COUNT(*) FROM local_archive_events;");
    internal int DistinctEventIdCount() => Scalar<int>("SELECT COUNT(DISTINCT event_id) FROM local_archive_events;");

    internal (string State, long Revision, string UpdatedAt)? Current(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state,revision,updated_at FROM local_archive_current WHERE target_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt64(1), reader.GetString(2)) : null;
    }

    internal T Scalar<T>(string sql)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal void Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
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

    private static string Kind(LocalArchiveTargetKind kind) => kind == LocalArchiveTargetKind.Session ? "session" : "repository";

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}

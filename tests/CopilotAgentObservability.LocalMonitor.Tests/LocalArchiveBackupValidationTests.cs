using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.RuntimeBackup;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveBackupValidationTests
{
    private const string Newer = "2026-08-09T12:34:56.1234567+00:00";
    private const string Older = "2026-08-08T01:02:03.0000000+00:00";

    [Fact]
    public void Validate_AcceptsAnEmptyArchiveWithoutCallingAParentAuthority()
    {
        using var database = new BackupValidationDatabase();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);

        LocalArchiveBackupValidation.Validate(
            connection,
            transaction,
            LocalArchiveSessionTargetExistenceAuthority.Instance,
            new UnexpectedRepositoryAuthority());

        Assert.Same(connection, transaction.Connection);
        transaction.Rollback();
    }

    [Fact]
    public void Validate_AcceptsBackwardTimeAndPagesEveryParentOnTheCallerTransaction()
    {
        using var database = new BackupValidationDatabase();
        using var connection = database.Open();
        using var seed = connection.BeginTransaction();
        for (var index = 1; index <= 201; index++)
            database.InsertChain(seed, "session", SessionId(index), revision: 2, Newer, Older);
        for (var index = 1; index <= 401; index++)
            database.InsertChain(seed, "repository", RepositoryId(index), revision: 1, Older, Older);
        seed.Commit();
        var repositories = new RecordingRepositoryAuthority();
        using var transaction = connection.BeginTransaction(deferred: true);

        LocalArchiveBackupValidation.Validate(
            connection,
            transaction,
            LocalArchiveSessionTargetExistenceAuthority.Instance,
            repositories);

        Assert.Same(connection, transaction.Connection);
        Assert.Equal([200, 200, 1], repositories.PageSizes);
        Assert.All(repositories.Connections, value => Assert.Same(connection, value));
        Assert.All(repositories.Transactions, value => Assert.Same(transaction, value));
        Assert.Equal(602L, database.Scalar(transaction, "SELECT COUNT(*) FROM local_archive_current;"));
        transaction.Rollback();
    }

    [Theory]
    [InlineData("current_target_kind", "UPDATE local_archive_current SET target_kind=1;")]
    [InlineData("current_target_id", "UPDATE local_archive_current SET target_id=substr(target_id,1,35) || 'A';")]
    [InlineData("current_state", "UPDATE local_archive_current SET state='other';")]
    [InlineData("current_revision", "UPDATE local_archive_current SET revision=x'01';")]
    [InlineData("current_archived_at", "UPDATE local_archive_current SET archived_at='2026-08-09T12:34:56.1234567Z';")]
    [InlineData("current_updated_at", "UPDATE local_archive_current SET updated_at=x'00';")]
    [InlineData("event_id", "UPDATE local_archive_events SET event_id=substr(event_id,1,35) || 'A';")]
    [InlineData("event_target_kind", "UPDATE local_archive_events SET target_kind=1;")]
    [InlineData("event_target_id", "UPDATE local_archive_events SET target_id=substr(target_id,1,35) || 'A';")]
    [InlineData("event_action", "UPDATE local_archive_events SET action='other';")]
    [InlineData("event_previous_revision", "UPDATE local_archive_events SET previous_revision=x'00';")]
    [InlineData("event_new_revision", "UPDATE local_archive_events SET new_revision=x'01';")]
    [InlineData("event_occurred_at", "UPDATE local_archive_events SET occurred_at=x'00';")]
    public void Validate_RejectsNoncanonicalScalarStorageOrBytes(string _, string corruption)
    {
        using var database = BackupValidationDatabase.WithOneSessionChain();
        database.Corrupt(corruption);

        AssertInvalid(database);
    }

    [Theory]
    [InlineData("current_without_event")]
    [InlineData("event_without_current")]
    [InlineData("first_not_archive")]
    [InlineData("revision_gap")]
    [InlineData("nonalternating")]
    [InlineData("head_state")]
    [InlineData("head_timestamp")]
    [InlineData("active_odd")]
    [InlineData("archived_at_mismatch")]
    public void Validate_RejectsIncompleteOrContradictoryChains(string contradiction)
    {
        using var database = BackupValidationDatabase.WithOneSessionChain(
            revision: contradiction is "active_odd" or "archived_at_mismatch" ? 1 : 2);
        database.ApplyChainContradiction(contradiction);

        AssertInvalid(database);
    }

    [Fact]
    public void Validate_RejectsMissingSessionAndRepositoryParents()
    {
        using var missingSession = new BackupValidationDatabase();
        missingSession.InsertChain("session", SessionId(1), revision: 1, Newer, Older, insertSession: false);
        AssertInvalid(missingSession);

        using var missingRepository = new BackupValidationDatabase();
        missingRepository.InsertChain("repository", RepositoryId(1), revision: 1, Newer, Older);
        AssertInvalid(missingRepository, new RecordingRepositoryAuthority(returnNone: true));
    }

    [Fact]
    public void Validate_RejectsAConnectionOrTransactionItDoesNotOwn()
    {
        using var first = new BackupValidationDatabase();
        using var second = new BackupValidationDatabase();
        using var firstConnection = first.Open();
        using var secondConnection = second.Open();
        using var secondTransaction = secondConnection.BeginTransaction(deferred: true);

        var error = Assert.Throws<InvalidOperationException>(() => LocalArchiveBackupValidation.Validate(
            firstConnection,
            secondTransaction,
            LocalArchiveSessionTargetExistenceAuthority.Instance,
            SqliteLocalRepositoryTargetExistenceAuthority.Instance));

        Assert.Equal("local_archive_backup_invalid", error.Message);
    }

    private static void AssertInvalid(
        BackupValidationDatabase database,
        ILocalRepositoryTargetExistenceAuthority? repositories = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        Assert.True(LocalArchiveSchemaV1.HasExactOwnedSchema(connection, transaction));
        var error = Assert.Throws<InvalidOperationException>(() => LocalArchiveBackupValidation.Validate(
            connection,
            transaction,
            LocalArchiveSessionTargetExistenceAuthority.Instance,
            repositories ?? SqliteLocalRepositoryTargetExistenceAuthority.Instance));
        Assert.Equal("local_archive_backup_invalid", error.Message);
    }

    private static string SessionId(int value) => $"02900000-{value & 0xffff:x4}-7000-8000-{value:x12}";
    private static string RepositoryId(int value) => $"01900000-{value & 0xffff:x4}-7000-8000-{value:x12}";

    private sealed class RecordingRepositoryAuthority(bool returnNone = false) : ILocalRepositoryTargetExistenceAuthority
    {
        internal List<int> PageSizes { get; } = [];
        internal List<SqliteConnection> Connections { get; } = [];
        internal List<SqliteTransaction> Transactions { get; } = [];

        public IReadOnlyList<string> ReadExisting(
            SqliteConnection openConnection,
            SqliteTransaction exactTransaction,
            IReadOnlyList<string> canonicalRepositoryIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PageSizes.Add(canonicalRepositoryIds.Count);
            Connections.Add(openConnection);
            Transactions.Add(exactTransaction);
            return returnNone ? [] : canonicalRepositoryIds.ToArray();
        }
    }

    private sealed class UnexpectedRepositoryAuthority : ILocalRepositoryTargetExistenceAuthority
    {
        public IReadOnlyList<string> ReadExisting(
            SqliteConnection openConnection,
            SqliteTransaction exactTransaction,
            IReadOnlyList<string> canonicalRepositoryIds,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Parent authority must not be called for an empty archive.");
    }

    private sealed class BackupValidationDatabase : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"local-archive-backup-validation-{Guid.NewGuid():N}");

        internal BackupValidationDatabase()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "archive.sqlite");
            new Persistence.Sqlite.Sessions.SqliteSessionStore(Path).CreateSchema();
            using var connection = Open();
            LocalRepositoryCatalogSchemaV1.Ensure(connection);
            LocalArchiveSchemaV1.Ensure(connection);
        }

        internal string Path { get; }

        internal static BackupValidationDatabase WithOneSessionChain(long revision = 1)
        {
            var database = new BackupValidationDatabase();
            database.InsertChain("session", SessionId(1), revision, Newer, Older);
            return database;
        }

        internal SqliteConnection Open()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        internal void InsertChain(
            string kind,
            string id,
            long revision,
            string headTime,
            string firstTime,
            bool insertSession = true)
        {
            using var connection = Open();
            using var transaction = connection.BeginTransaction();
            InsertChain(transaction, kind, id, revision, headTime, firstTime, insertSession);
            transaction.Commit();
        }

        internal void InsertChain(
            SqliteTransaction transaction,
            string kind,
            string id,
            long revision,
            string headTime,
            string firstTime,
            bool insertSession = true)
        {
            var connection = transaction.Connection!;
            if (kind == "session" && insertSession)
                Execute(connection, transaction,
                    "INSERT INTO sessions(session_id,status,completeness,last_seen_at,raw_retention_state,created_at,updated_at) VALUES($id,'active','unbound',$at,'not_captured',$at,$at);",
                    ("$id", id), ("$at", Newer));
            Execute(connection, transaction,
                "INSERT INTO local_archive_current(target_kind,target_id,state,revision,archived_at,updated_at) VALUES($kind,$id,$state,$revision,$archived,$updated);",
                ("$kind", kind), ("$id", id), ("$state", revision % 2 == 1 ? "archived" : "active"),
                ("$revision", revision), ("$archived", revision % 2 == 1 ? headTime : null), ("$updated", headTime));
            for (var current = 1L; current <= revision; current++)
            {
                Execute(connection, transaction,
                    "INSERT INTO local_archive_events(event_id,target_kind,target_id,action,previous_revision,new_revision,occurred_at) VALUES($event,$kind,$id,$action,$previous,$revision,$at);",
                    ("$event", Guid.CreateVersion7().ToString("D")), ("$kind", kind), ("$id", id),
                    ("$action", current % 2 == 1 ? "archive" : "restore"), ("$previous", current - 1),
                    ("$revision", current), ("$at", current == revision ? headTime : firstTime));
            }
        }

        internal void Corrupt(string sql)
        {
            using var connection = Open();
            using var setup = connection.CreateCommand();
            setup.CommandText = "PRAGMA foreign_keys=OFF; PRAGMA ignore_check_constraints=ON;";
            setup.ExecuteNonQuery();
            using var transaction = connection.BeginTransaction();
            using (var read = connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = "SELECT revision FROM local_archive_current WHERE target_kind='session' AND target_id=$id;";
                read.Parameters.AddWithValue("$id", SessionId(1));
                var revision = Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
                Execute(connection, transaction,
                    "DELETE FROM schema_version WHERE component='local_archive'; DROP TABLE local_archive_events; DROP TABLE local_archive_current;");
                var statements = LocalArchiveSchemaV1.CanonicalSql.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < 4; index++)
                    Execute(connection, transaction, statements[index]);
                InsertChain(transaction, "session", SessionId(1), revision, Newer, Older, insertSession: false);
                Execute(connection, transaction, sql);
                for (var index = 4; index < statements.Length; index++)
                    Execute(connection, transaction, statements[index]);
                Execute(connection, transaction,
                    "INSERT INTO schema_version(component,version) VALUES('local_archive',1);");
            }
            transaction.Commit();
        }

        internal void ApplyChainContradiction(string contradiction)
        {
            switch (contradiction)
            {
                case "current_without_event":
                    Corrupt("DELETE FROM local_archive_events;");
                    break;
                case "event_without_current":
                    Corrupt("DELETE FROM local_archive_current;");
                    break;
                case "first_not_archive":
                    Corrupt("UPDATE local_archive_events SET action='restore' WHERE new_revision=1;");
                    break;
                case "revision_gap":
                    Corrupt("UPDATE local_archive_events SET previous_revision=0,new_revision=3 WHERE new_revision=2;");
                    break;
                case "nonalternating":
                    Corrupt("UPDATE local_archive_events SET action='archive' WHERE new_revision=2;");
                    break;
                case "head_state":
                    Corrupt("UPDATE local_archive_current SET state='archived',archived_at=updated_at WHERE revision=2;");
                    break;
                case "head_timestamp":
                    Corrupt($"UPDATE local_archive_current SET updated_at='{Older}' WHERE revision=2;");
                    break;
                case "active_odd":
                    Corrupt("UPDATE local_archive_current SET state='active',archived_at=NULL WHERE revision=1;");
                    break;
                case "archived_at_mismatch":
                    Corrupt($"UPDATE local_archive_current SET archived_at='{Older}' WHERE revision=1;");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(contradiction));
            }
        }

        internal long Scalar(SqliteTransaction transaction, string sql)
        {
            using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Execute(
            SqliteConnection connection,
            SqliteTransaction transaction,
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

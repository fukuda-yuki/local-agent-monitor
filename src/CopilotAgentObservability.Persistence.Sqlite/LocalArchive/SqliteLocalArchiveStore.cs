using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed partial class SqliteLocalArchiveStore
{
    private readonly string databasePath;
    private readonly ILocalRepositoryTargetExistenceAuthority repositoryExistence;
    private readonly LocalArchiveSessionTargetExistenceAuthority sessionExistence;
    private readonly TimeProvider timeProvider;
    private readonly Func<DateTimeOffset, string> eventIdFactory;
    private readonly Func<SqliteConnection>? connectionFactory;

    internal SqliteLocalArchiveStore(
        string databasePath,
        ILocalRepositoryTargetExistenceAuthority repositoryExistence,
        LocalArchiveSessionTargetExistenceAuthority sessionExistence,
        Func<SqliteConnection>? connectionFactory = null)
        : this(
            databasePath,
            repositoryExistence,
            sessionExistence,
            TimeProvider.System,
            value => Guid.CreateVersion7(value).ToString("D"),
            connectionFactory)
    {
    }

    internal SqliteLocalArchiveStore(
        string databasePath,
        ILocalRepositoryTargetExistenceAuthority repositoryExistence,
        LocalArchiveSessionTargetExistenceAuthority sessionExistence,
        TimeProvider timeProvider,
        Func<DateTimeOffset, string> eventIdFactory,
        Func<SqliteConnection>? connectionFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(repositoryExistence);
        ArgumentNullException.ThrowIfNull(sessionExistence);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(eventIdFactory);
        this.databasePath = Path.GetFullPath(databasePath);
        this.repositoryExistence = repositoryExistence;
        this.sessionExistence = sessionExistence;
        this.timeProvider = timeProvider;
        this.eventIdFactory = eventIdFactory;
        this.connectionFactory = connectionFactory;
    }

    private SqliteConnection Open()
    {
        var connection = connectionFactory?.Invoke() ?? new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 0,
            }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=0;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static LocalArchiveReadResult ReadError(LocalArchiveStoreError error) =>
        new(Success: null, error);

    private static LocalArchiveListResult ListError(LocalArchiveStoreError error) =>
        new(Success: null, error);

    private static LocalArchiveMutationResult MutationError(LocalArchiveStoreError error) =>
        new(Success: null, error);

    private static bool IsDefined(LocalArchiveTargetKind targetKind) =>
        targetKind is LocalArchiveTargetKind.Session or LocalArchiveTargetKind.Repository;
}

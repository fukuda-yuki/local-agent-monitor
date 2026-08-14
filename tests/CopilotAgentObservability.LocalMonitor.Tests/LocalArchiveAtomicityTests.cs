using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveAtomicityTests
{
    private const string Target = "01890f65-4c31-7f42-8a7d-111111111111";

    [Theory]
    [InlineData("empty")]
    [InlineData("throw")]
    public void Mutate_WriterFailureRollsBackEveryTable(string failure)
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(Target);

        var result = database.CreateStore().Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [new(Target, 0)],
            failure == "empty"
                ? _ => ReadOnlyMemory<byte>.Empty
                : _ => throw new InvalidOperationException("synthetic writer failure"),
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.ArchiveStoreUnavailable, result.Error);
        Assert.Equal(0, database.EventCount());
        Assert.Null(database.Current(Target));
    }

    [Fact]
    public void Mutate_CancellationRaisedByWriterRollsBackAndEmitsNoEntity()
    {
        using var database = new LocalArchiveMutationDatabase();
        database.InsertSession(Target);
        using var cancellation = new CancellationTokenSource();

        Assert.Throws<OperationCanceledException>(() => database.CreateStore().Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Session,
            [new(Target, 0)],
            _ =>
            {
                cancellation.Cancel();
                return "copied"u8.ToArray();
            },
            cancellation.Token));

        Assert.Equal(0, database.EventCount());
        Assert.Null(database.Current(Target));
    }

    [Fact]
    public void Mutate_BusyIsOneAttemptAndDoesNotInvokeWriter()
    {
        using var database = new LocalArchiveMutationDatabase();
        var authority = new BusyRepositoryAuthority();
        var calls = 0;

        var result = database.CreateStore(authority).Mutate(
            LocalArchiveAction.Archive,
            LocalArchiveTargetKind.Repository,
            [new(Target, 0)],
            _ =>
            {
                calls++;
                return "unused"u8.ToArray();
            },
            CancellationToken.None);

        Assert.Equal(LocalArchiveStoreError.PersistenceBusy, result.Error);
        Assert.Equal(0, calls);
        Assert.Equal(1, authority.Calls);
        Assert.Equal(0, database.EventCount());
    }

    private sealed class BusyRepositoryAuthority : ILocalRepositoryTargetExistenceAuthority
    {
        internal int Calls { get; private set; }

        public IReadOnlyList<string> ReadExisting(
            SqliteConnection openConnection,
            SqliteTransaction exactTransaction,
            IReadOnlyList<string> canonicalRepositoryIds,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new SqliteException("synthetic busy", 5);
        }
    }
}

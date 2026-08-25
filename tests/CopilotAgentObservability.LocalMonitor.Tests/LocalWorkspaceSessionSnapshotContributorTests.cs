namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalWorkspaceSessionSnapshotContributorTests
{
    [Fact]
    public async Task ContributorReturnsTypedImmutableRowsInStableOrder()
    {
        using var connection = LocalWorkspaceProjectionSchemaTests.OpenSessionDatabase();
        LocalWorkspaceProjectionSchemaTests.Execute(connection, "INSERT INTO sessions VALUES('0198f5b8-0c00-7000-8000-000000000002','active','unbound',NULL,NULL,NULL,NULL,'2026-08-24T00:01:00.0000000+00:00','not_captured','2026-08-24T00:01:00.0000000+00:00','2026-08-24T00:01:00.0000000+00:00'),('0198f5b8-0c00-7000-8000-000000000001','completed','partial',NULL,NULL,NULL,NULL,'2026-08-24T00:00:00.0000000+00:00','not_captured','2026-08-24T00:00:00.0000000+00:00','2026-08-24T00:00:00.0000000+00:00');");
        LocalWorkspaceProjectionSchemaV1.Ensure(connection, DateTimeOffset.Parse("2026-08-25T00:00:00Z"));
        var contributor = new LocalWorkspaceSessionSnapshotContributor();
        var capability = new TestReadTransaction(connection);

        var result = await contributor.ReadAsync(capability, new(LocalRepositoryScopeKind.All, null), CancellationToken.None);

        var rows = Assert.IsAssignableFrom<IReadOnlyList<ILocalRepositorySessionSnapshotRow>>(result.Sessions);
        Assert.Equal(["0198f5b8-0c00-7000-8000-000000000001", "0198f5b8-0c00-7000-8000-000000000002"], rows.Select(row => row.SessionId));
        Assert.All(rows, row => Assert.IsType<LocalWorkspaceProjectionRow>(row));
        Assert.Throws<NotSupportedException>(() => ((IList<ILocalRepositorySessionSnapshotRow>)rows)[0] = rows[0]);
    }

    private sealed class TestReadTransaction(Microsoft.Data.Sqlite.SqliteConnection connection) : ILocalRepositoryReadTransaction
    {
        public async ValueTask<T> ReadAsync<T>(Func<Microsoft.Data.Sqlite.SqliteConnection, Microsoft.Data.Sqlite.SqliteTransaction, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
        {
            using var transaction = connection.BeginTransaction(deferred: true);
            return await read(connection, transaction, cancellationToken);
        }
    }
}

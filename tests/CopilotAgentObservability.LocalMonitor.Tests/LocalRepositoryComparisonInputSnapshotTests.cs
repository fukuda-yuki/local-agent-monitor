using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryComparisonInputSnapshotTests
{
    [Fact]
    public async Task ProductionComparisonInputFreezesAllNamedSemanticFactsAndIndependentVersionsInOneRead()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(
            temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010",
            "018f0000-0000-7000-8000-000000000020");
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE session_events SET source_application_version='source-9.1',adapter_version='adapter-4.2';";
            Assert.True(command.ExecuteNonQuery() > 0);
        }
        var clock = new FixedClock();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var connectionCount = 0;
        var capabilityEntries = 0;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            capabilityEntryObserver: () => { capabilityEntries++; return ValueTask.CompletedTask; },
            connectionOpenedObserver: _ => connectionCount++,
            skillRegistryAuthority: authority,
            timeProvider: clock);

        var snapshot = await service.ReadComparisonInputAsync(
            new(LocalRepositoryScopeKind.All, null, ExactTargetSessionIds: [sessionId]), CancellationToken.None);

        var comparison = Assert.Single(snapshot.Sessions).ComparisonDetail!;
        Assert.Contains(comparison.Nodes, node => node.Kind == "tool" && node.SourceReferences is { Count: > 0 });
        Assert.Contains(comparison.Nodes, node => node.Kind == "subagent" && node.SourceReferences is { Count: > 0 });
        Assert.All(comparison.Nodes, node =>
        {
            Assert.NotNull(node.Activity);
            Assert.NotNull(node.Tokens);
        });
        Assert.Equal(["source-9.1"], comparison.SourceApplicationVersions);
        Assert.Equal(["adapter-4.2"], comparison.AdapterVersions);
        Assert.Equal(1, connectionCount);
        Assert.True(capabilityEntries >= 4);
    }

    [Fact]
    public async Task ConsumedComparisonContributionChangesTheCoherentRevision()
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(
            temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010",
            "018f0000-0000-7000-8000-000000000020");
        var clock = new FixedClock();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            skillRegistryAuthority: authority, timeProvider: clock);
        var request = new LocalRepositoryScopeRequest(LocalRepositoryScopeKind.All, null, ExactTargetSessionIds: [sessionId]);
        var before = Assert.Single((await service.ReadComparisonInputAsync(request, default)).Sessions).WorkspaceRevision;
        using (var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE local_workspace_nodes SET retry_activity_state='recorded',retry_activity_count=7 WHERE source_kind='semantic_tool';";
            Assert.True(command.ExecuteNonQuery() > 0);
        }

        var after = Assert.Single((await service.ReadComparisonInputAsync(request, default)).Sessions).WorkspaceRevision;

        Assert.NotEqual(before, after);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-08-26T00:10:00Z");
    }
}

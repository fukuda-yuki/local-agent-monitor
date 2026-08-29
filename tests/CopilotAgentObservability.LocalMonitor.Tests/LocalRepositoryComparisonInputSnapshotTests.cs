using CopilotAgentObservability.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalRepositoryComparisonInputSnapshotTests
{
    [Theory]
    [InlineData("source", 63, false)]
    [InlineData("adapter", 63, false)]
    [InlineData("source", 64, true)]
    [InlineData("adapter", 64, true)]
    public async Task ProductionComparisonVersionsEnforceIndependentCompleteBoundaries(string dimension, int count, bool tooLarge)
    {
        using var temp = new MonitorTempDirectory();
        const string sessionId = "018f0000-0000-7000-8000-000000000001";
        LocalWorkspaceSessionDetailSnapshotTests.InitializeRoundFiveSemanticFixture(
            temp.DatabasePath, sessionId,
            "018f0000-0000-7000-8000-000000000010",
            "018f0000-0000-7000-8000-000000000020");
        SeedVersions(temp.DatabasePath, sessionId, dimension, count);
        var clock = new FixedClock();
        var authority = FixedSkillRegistryGenerationAuthority.Load();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            skillRegistryAuthority: authority,
            timeProvider: clock);

        if (tooLarge)
        {
            var error = await Assert.ThrowsAsync<LocalWorkspaceSessionDetailException>(async () =>
                await service.ReadComparisonInputAsync(new(LocalRepositoryScopeKind.All, null, ExactTargetSessionIds: [sessionId]), default));
            Assert.Equal("workspace_too_large", error.Error);
            return;
        }

        var snapshot = await service.ReadComparisonInputAsync(
            new(LocalRepositoryScopeKind.All, null, ExactTargetSessionIds: [sessionId]), default);
        var detail = Assert.Single(snapshot.Sessions).ComparisonDetail;
        var actual = dimension == "source" ? detail.SourceApplicationVersions : detail.AdapterVersions;
        Assert.Equal(63, actual.Count);
        Assert.Contains(actual, static value => value.Length == 256);
        Assert.Contains("V1+meta", actual);
        Assert.Empty(dimension == "source" ? detail.AdapterVersions : detail.SourceApplicationVersions);
    }

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
        LocalWorkspaceSessionDetailSnapshotTests.NativeDetailObserver? sqlObserver = null;
        IReadOnlyList<string>? statements = null;
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            temp.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: authority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: authority, timeProvider: clock),
            capabilityEntryObserver: () => { capabilityEntries++; return ValueTask.CompletedTask; },
            connectionOpenedObserver: connection =>
            {
                connectionCount++;
                sqlObserver = new(connection);
            },
            finalReturnObserver: () =>
            {
                statements = sqlObserver!.Sql.ToArray();
                sqlObserver.Dispose();
            },
            skillRegistryAuthority: authority,
            timeProvider: clock);

        var snapshot = await service.ReadComparisonInputAsync(
            new(LocalRepositoryScopeKind.All, null, ExactTargetSessionIds: [sessionId]), CancellationToken.None);

        var input = Assert.Single(snapshot.Sessions);
        Assert.NotNull(input.ComparisonDetail);
        var comparison = input.ComparisonDetail;
        var tool = Assert.Single(comparison.Nodes, node => node.Kind == "tool" && node.TraceId == new string('a', 32));
        Assert.Equal("completed", tool.Lifecycle);
        Assert.Equal("completed", tool.Status);
        Assert.Equal("not_observed", tool.Activity.Error.State);
        Assert.Null(tool.Activity.Error.Value);
        Assert.Equal("not_observed", tool.Activity.Retry.State);
        Assert.Null(tool.Activity.Retry.Value);
        Assert.Equal("not_observed", tool.Tokens.Total.State);
        Assert.Null(tool.Tokens.Total.Value);
        var toolReference = Assert.Single(tool.SourceReferences!);
        Assert.Equal("otel_span", toolReference.SourceKind);
        Assert.Equal("018f0000-0000-7000-8000-000000000011", toolReference.SourceIdentity);
        Assert.Equal(new string('a', 32), toolReference.TraceId);
        Assert.Equal(new string('b', 16), toolReference.SpanId);
        Assert.Equal("018f0000-0000-7000-8000-000000000011", toolReference.EventId);
        Assert.True(toolReference.AuthorityValidated);

        var subagent = Assert.Single(comparison.Nodes, node => node.Kind == "subagent");
        Assert.Equal("unknown", subagent.Lifecycle);
        Assert.Equal("unknown", subagent.Status);
        Assert.Equal("recorded", subagent.SubagentLifecycle!.StartedState);
        Assert.Equal("recorded", subagent.SubagentLifecycle.CompletedState);
        Assert.Equal("not_observed", subagent.SubagentLifecycle.FailedState);
        Assert.Equal("not_observed", subagent.Tokens.Total.State);
        Assert.Equal(
            ["018f0000-0000-7000-8000-000000000021", "018f0000-0000-7000-8000-000000000022"],
            subagent.SourceReferences!.Select(static reference => reference.EventId).Order(StringComparer.Ordinal));
        var session = Assert.IsType<LocalWorkspaceProjectionRow>(input.Session.Session);
        Assert.Equal("recorded", session.Activity.Tool.State);
        Assert.Equal(1, session.Activity.Tool.Value);
        Assert.Equal("recorded", session.Activity.Subagent.State);
        Assert.Equal(1, session.Activity.Subagent.Value);
        Assert.Equal(["source-9.1"], comparison.SourceApplicationVersions);
        Assert.Equal(["adapter-4.2"], comparison.AdapterVersions);
        Assert.Equal(1, connectionCount);
        Assert.Equal(4, capabilityEntries);
        Assert.Equal(1, statements!.Count(statement => statement.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)));
    }

    private static void SeedVersions(string path, string sessionId, string dimension, int count)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using (var clear = connection.CreateCommand())
        {
            clear.CommandText = "UPDATE session_events SET source_application_version=NULL,adapter_version=NULL;";
            clear.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction();
        for (var index = 0; index < count; index++)
        {
            var token = index == count - 1
                ? char.ToUpperInvariant(dimension[0]) + new string('x', 253) + "+1"
                : index == 0 ? "V1+meta" : $"{dimension}-{index:D3}";
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"""
                INSERT INTO session_events(event_id,session_id,source_adapter,source_event_id,type,occurred_at,content_state,{(dimension == "source" ? "source_application_version" : "adapter_version")})
                VALUES($event_id,$session_id,'compare-version-test',$source_event_id,'version.fact','2026-08-29T00:00:00.0000000+00:00','not_captured',$version);
                """;
            command.Parameters.AddWithValue("$event_id", $"018f0000-0000-7000-8000-{index + 0x100:x12}");
            command.Parameters.AddWithValue("$session_id", sessionId);
            command.Parameters.AddWithValue("$source_event_id", $"{dimension}-{index:D3}");
            command.Parameters.AddWithValue("$version", token);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    [Fact]
    public async Task ProductionComparisonInputFreezesAdmittedSkillFactsAndExactReference()
    {
        using var fixture = new CurrentInvocationProjectionFixture();
        fixture.SeedSdkOnlyWithFixedReference("compare-skill", "review-skill");
        fixture.RefreshWorkspace();
        var sessionId = fixture.SessionId("compare-skill");
        var clock = new SkillClock();
        var service = new SqliteLocalRepositoryScopeSnapshotService(
            fixture.DatabasePath,
            new LocalWorkspaceSessionSnapshotContributor(clock, registryAuthority: fixture.RegistryAuthority),
            SqliteLocalArchiveFactSnapshotContributor.Instance,
            new LocalWorkspaceSessionDetailSnapshotContributor(registryAuthority: fixture.RegistryAuthority, timeProvider: clock),
            skillRegistryAuthority: fixture.RegistryAuthority,
            timeProvider: clock);

        var snapshot = await service.ReadComparisonInputAsync(
            new(LocalRepositoryScopeKind.All, null, ExactTargetSessionIds: [sessionId]), default);

        var input = Assert.Single(snapshot.Sessions);
        Assert.NotNull(input.ComparisonDetail);
        var comparison = input.ComparisonDetail;
        var skill = Assert.Single(comparison.Nodes, node => node.Kind == "skill");
        Assert.Equal("review-skill", skill.NameText);
        Assert.Equal("recorded", skill.NameState);
        Assert.Equal("completed", skill.Lifecycle);
        Assert.Equal("completed", skill.Status);
        Assert.Equal("recorded", skill.Activity.Skill.State);
        Assert.Equal(1, skill.Activity.Skill.Value);
        Assert.Equal("not_observed", skill.Activity.Error.State);
        Assert.Equal("not_observed", skill.Activity.Retry.State);
        Assert.Equal("not_observed", skill.Tokens.Total.State);
        var reference = Assert.Single(skill.SourceReferences!);
        Assert.Equal("skill_claim", reference.SourceKind);
        Assert.Equal("sdk:018f0000-0000-7000-8000-000000000033", reference.SourceIdentity);
        Assert.Equal("018f0000-0000-7000-8000-000000000031", reference.EventId);
        Assert.Null(reference.TraceId);
        Assert.Null(reference.SpanId);
        Assert.True(reference.AuthorityValidated);
        var session = Assert.IsType<LocalWorkspaceProjectionRow>(input.Session.Session);
        Assert.Equal("recorded", session.Activity.Skill.State);
        Assert.Equal(1, session.Activity.Skill.Value);
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

    private sealed class SkillClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-01-01T01:00:00Z");
    }
}

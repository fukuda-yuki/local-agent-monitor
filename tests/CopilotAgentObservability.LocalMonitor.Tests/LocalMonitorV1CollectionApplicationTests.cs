using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1CollectionApplicationTests
{
    [Fact]
    public void RepositoryQueryRejectsUnknownDuplicateAndEmptyValues()
    {
        Assert.False(LocalMonitorV1RepositoryRequestParser.TryParse("?unknown=x", out _));
        Assert.False(LocalMonitorV1RepositoryRequestParser.TryParse("?limit=1&limit=2", out _));
        Assert.False(LocalMonitorV1RepositoryRequestParser.TryParse("?archive_scope=", out _));
        Assert.True(LocalMonitorV1RepositoryRequestParser.TryParse("", out var request));
        Assert.Equal("active_only", request!.ArchiveScope);
        Assert.Equal(50, request.Limit);
    }

    [Fact]
    public void EmptySessionSnapshotHasNormativeBytes()
    {
        var snapshot = new LocalRepositoryScopeSnapshot(
            new(LocalRepositoryScopeKind.All, null), [], []);
        var request = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}");

        var bytes = LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request, new byte[32]);

        Assert.Equal("{\"schema_version\":\"local-monitor-sessions.response.v1\",\"workspace_revision\":\"0000000000000000000000000000000000000000000000000000000000000000\",\"items\":[],\"next_cursor\":null}", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void TenThousandCandidatesUseTwoStableExclusivePagesOfTwoHundred()
    {
        var sessions = Enumerable.Range(0, 10_000).Select(index => Session(index)).ToArray();
        var snapshot = new LocalRepositoryScopeSnapshot(new(LocalRepositoryScopeKind.All, null), [], sessions);
        var request = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":200}");
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

        using var first = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request, key));
        var firstIds = first.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("session_id").GetString()!).ToArray();
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();
        using var second = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request with { Cursor = cursor }, key));
        var secondIds = second.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("session_id").GetString()!).ToArray();

        Assert.Equal(200, firstIds.Length); Assert.NotNull(cursor); Assert.Equal(147, cursor!.Length); Assert.Equal(200, secondIds.Length);
        Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));
        Assert.Equal("018f0000-0000-7000-8000-000000000000", firstIds[0]);
        Assert.Equal("018f0000-0000-7000-8000-0000000000c7", firstIds[^1]);
        Assert.Equal("018f0000-0000-7000-8000-0000000000c8", secondIds[0]);
    }

    [Fact]
    public void ScopeArchiveAndDynamicFiltersComposeWithoutReflectingQueryOrModel()
    {
        var repositoryId = "018f0000-0000-7000-8000-000000000101";
        var matching = Session(1, repositoryId, source: "copilot-sdk", model: "model-a", label: "ＦＩＲＳＴ Instruction", skillCount: 0);
        var archived = Session(2, repositoryId, archived: true, source: "vscode", model: "model-b", label: "other", skillCount: 2);
        var snapshot = new LocalRepositoryScopeSnapshot(new(LocalRepositoryScopeKind.Repository, repositoryId), [], [matching, archived]);
        var request = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"repository\",\"repository_id\":\"018f0000-0000-7000-8000-000000000101\",\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[\"copilot-sdk\"],\"model\":[\"model-a\"],\"status\":[\"active\"],\"has_skill\":false,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":\"first instruction\",\"cursor\":null,\"limit\":null}");

        var json = Encoding.UTF8.GetString(LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request, new byte[32]));

        using var document = JsonDocument.Parse(json); Assert.Single(document.RootElement.GetProperty("items").EnumerateArray());
        Assert.DoesNotContain("first instruction", json, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryCollectionSortsCountsPaginatesAndRevisesFromOneSnapshot()
    {
        var firstId="018f0000-0000-7000-8000-000000000101"; var secondId="018f0000-0000-7000-8000-000000000102";
        var repositories = new[] { new LocalRepositoryCatalogSnapshot(secondId,"Zulu",2,null,1,LocalArchiveState.Archived,1), new LocalRepositoryCatalogSnapshot(firstId,"Alpha",1,null,0,LocalArchiveState.Active,0) };
        var snapshot = new LocalRepositoryScopeSnapshot(new(LocalRepositoryScopeKind.All,null),repositories,[Session(1,firstId),Session(2)]);
        Assert.True(LocalMonitorV1RepositoryRequestParser.TryParse("?archive_scope=include_archived&limit=1",out var request));

        using var first=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot,request!));
        var root=first.RootElement; Assert.Equal(firstId,root.GetProperty("repositories")[0].GetProperty("repository_id").GetString());
        Assert.Equal(2,root.GetProperty("all_session_count").GetInt32()); Assert.Equal(1,root.GetProperty("unassigned_active_session_count").GetInt32()); Assert.Equal(1,root.GetProperty("archived_repository_count").GetInt32());
        var revision=root.GetProperty("workspace_revision").GetString(); Assert.Equal(firstId,root.GetProperty("next_cursor").GetString());
        Assert.True(LocalMonitorV1RepositoryRequestParser.TryParse($"?archive_scope=include_archived&after={firstId}&limit=1",out var continuation));
        using var second=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot,continuation!));
        Assert.Equal(secondId,second.RootElement.GetProperty("repositories")[0].GetProperty("repository_id").GetString()); Assert.Equal(JsonValueKind.Null,second.RootElement.GetProperty("next_cursor").ValueKind);
        using var stable=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot,request!)); Assert.Equal(revision,stable.RootElement.GetProperty("workspace_revision").GetString());
        using var changed=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot with { Repositories=[repositories[0],repositories[1] with { Revision=3 }] },request!)); Assert.NotEqual(revision,changed.RootElement.GetProperty("workspace_revision").GetString());
    }

    private static LocalRepositoryScopeSessionSnapshot Session(int index, string? repositoryId = null, bool archived = false, string source = "copilot-sdk", string model = "m", string label = "label", long skillCount = 0)
    {
        var id = $"018f0000-0000-7000-8000-{index:x12}";
        var fact = new LocalWorkspaceFact<long>("recorded", 0);
        var projection = new LocalWorkspaceProjectionRow(id, 0, 10_000-index, "recorded", label, "active", "rich", new("recorded", [source]), new("recorded", [model]), new(new("recorded", skillCount), fact, fact, fact, fact),
            new("none", "not_observed", 0, 0, new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null)),
            "not_observed", null, null, null, [], $"seed-{index}");
        return new(id, projection, 0, repositoryId is null ? LocalRepositoryScopeAssignmentState.Unassigned : LocalRepositoryScopeAssignmentState.Assigned,
            repositoryId is null ? LocalRepositoryScopeAssignmentAuthority.None : LocalRepositoryScopeAssignmentAuthority.Automatic, repositoryId, [], true, repositoryId is null, repositoryId is not null,
            archived ? LocalArchiveState.Archived : LocalArchiveState.Active, archived ? 1 : 0, !archived, archived ? "session_archived" : null);
    }

    private static LocalMonitorV1SessionSearchRequest Parse(string json)
    {
        Assert.Equal(LocalMonitorV1SessionSearchParseStatus.Success,
            LocalMonitorV1SessionSearchRequestParser.Parse(Encoding.UTF8.GetBytes(json), out var request));
        return request!;
    }
}

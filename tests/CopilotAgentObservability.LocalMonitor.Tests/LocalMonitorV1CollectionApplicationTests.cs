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
        Assert.False(LocalMonitorV1RepositoryRequestParser.TryParse("?after=first&after=second", out _));
        Assert.False(LocalMonitorV1RepositoryRequestParser.TryParse("?archive_scope=", out _));
        Assert.True(LocalMonitorV1RepositoryRequestParser.TryParse("", out var request));
        Assert.Equal("active_only", request!.ArchiveScope);
        Assert.Equal(50, request.Limit);
        foreach (var cursor in new[] { "", "short", "padded=", "illegal%2Fcharacter", "illegal+character", "018f0000-0000-7000-8000-000000000101" })
        {
            Assert.True(LocalMonitorV1RepositoryRequestParser.TryParse("?after=" + cursor, out var malformedCursorRequest));
            Assert.Equal(cursor, malformedCursorRequest!.After);
        }
        foreach (var query in new[] { "?%6cimit=1", "?limit=%31", "?limit=+1", "?limit=1;archive_scope=active_only", "?LIMIT=1", "?limit=%", "?archive_scope=ACTIVE_ONLY" })
            Assert.False(LocalMonitorV1RepositoryRequestParser.TryParse(query, out _));
    }

    [Fact]
    public void AcceptedEpochDateWindowIsInclusiveExclusiveForActiveAndCompleted()
    {
        var from = "2026-01-01T00:00:00.0010000+00:00"; var to = "2026-01-01T00:00:00.0030000+00:00";
        var sessions = new[]
        {
            Session(1) with { Session = Projection(1, 0, 1_767_225_600_000, ["label"]) },
            Session(2) with { Session = Projection(2, 0, 1_767_225_600_001, ["label"]) },
            Session(3, status:"completed") with { Session = Projection(3, 0, 1_767_225_600_002, ["label"]) },
            Session(4, status:"completed") with { Session = Projection(4, 0, 1_767_225_600_003, ["label"]) }
        };
        var request = Parse($"{{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":\"{from}\",\"to\":\"{to}\",\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}}");
        using var json = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(new(new(LocalRepositoryScopeKind.All,null),[],sessions),request,new byte[32]));
        Assert.Equal([sessions[1].SessionId,sessions[2].SessionId], json.RootElement.GetProperty("items").EnumerateArray().Select(x=>x.GetProperty("session_id").GetString()!).Order().ToArray());
    }

    [Fact]
    public void DateWindowQuantizesSubMillisecondBoundsToAcceptedEpochAcrossCursorPages()
    {
        var sessions = new[]
        {
            Session(1) with { Session = Projection(1, 0, -2, ["label"]) },
            Session(2) with { Session = Projection(2, 0, -1, ["label"]) },
            Session(3) with { Session = Projection(3, 0, 0, ["label"]) },
            Session(4) with { Session = Projection(4, 0, 1, ["label"]) }
        };
        var request = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":\"1969-12-31T23:59:59.9985000+00:00\",\"to\":\"1970-01-01T00:00:00.0019000+00:00\",\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":2}");
        var snapshot = new LocalRepositoryScopeSnapshot(new(LocalRepositoryScopeKind.All, null), [], sessions);
        var key = new byte[32];

        using var first = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request, key));
        var cursor = first.RootElement.GetProperty("next_cursor").GetString();
        using var second = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(snapshot, request with { Cursor = cursor }, key));
        var ids = first.RootElement.GetProperty("items").EnumerateArray()
            .Concat(second.RootElement.GetProperty("items").EnumerateArray())
            .Select(item => item.GetProperty("session_id").GetString()!)
            .ToArray();

        Assert.Equal([sessions[2].SessionId, sessions[1].SessionId, sessions[0].SessionId], ids);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ValidAndInvalidTimesCrossCursorWithoutDropsOrDuplicates()
    {
        var sessions = new[] { Session(1,status:"active",startedAt:"2026-01-02T00:00:00.0000000+00:00"), Session(2,status:"completed",startedAt:"2026-01-01T00:00:00.0000000+00:00"), Session(3,status:"active",startedAt:null), Session(4,status:"completed",startedAt:null) };
        var request=Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":2}"); var key=new byte[32];
        using var first=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(new(new(LocalRepositoryScopeKind.All,null),[],sessions),request,key)); var cursor=first.RootElement.GetProperty("next_cursor").GetString();
        using var second=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(new(new(LocalRepositoryScopeKind.All,null),[],sessions),request with { Cursor=cursor },key));
        var ids=first.RootElement.GetProperty("items").EnumerateArray().Concat(second.RootElement.GetProperty("items").EnumerateArray()).Select(x=>x.GetProperty("session_id").GetString()).ToArray(); Assert.Equal(4,ids.Distinct().Count());
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
    public void TenThousandCandidatesCombineAllFiltersAcrossTwoStableExclusivePagesOfTwoHundred()
    {
        const long start = 1_767_225_600_000;
        var sessions = Enumerable.Range(0, 10_000).Select(index =>
        {
            var matching = index < 500;
            var row = Session(index, source: matching ? "copilot-sdk" : "vscode", skillCount: matching ? 1 : 0, status: matching ? "active" : "completed");
            var projection = (LocalWorkspaceProjectionRow)row.Session;
            return row with { Session = projection with { SortEpochMilliseconds = start + 10_000 - index, SearchTexts = matching ? ["label needle", "tool needle"] : ["other"] } };
        }).ToArray();
        var snapshot = new LocalRepositoryScopeSnapshot(new(LocalRepositoryScopeKind.All, null), [], sessions);
        var request = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":\"2026-01-01T00:00:00.0000000+00:00\",\"to\":\"2026-01-02T00:00:00.0000000+00:00\",\"source\":[\"copilot-sdk\"],\"model\":[],\"status\":[\"active\"],\"has_skill\":true,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":\"needle\",\"cursor\":null,\"limit\":200}");
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
    public void DateWindowUsesPersistedAcceptedEpochAndExcludesInvalidRowsWhenBounded()
    {
        var inside = Session(1) with { Session = Projection(1, 0, 1_767_225_600_000, ["fallback"]) };
        var outside = Session(2) with { Session = Projection(2, 0, 1_767_225_599_999, ["fallback"]) };
        var invalid = Session(3) with { Session = Projection(3, 1, 0, ["fallback"]) };
        var request = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":\"2026-01-01T00:00:00.0000000+00:00\",\"to\":\"2026-01-01T00:00:00.0010000+00:00\",\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}");

        using var json = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(new(new(LocalRepositoryScopeKind.All, null), [], [inside, outside, invalid]), request, new byte[32]));

        Assert.Equal([inside.SessionId], json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("session_id").GetString()).ToArray());
    }

    [Fact]
    public void QueryMatchesOnlyProjectionOwnedLabelSkillAndToolSearchTexts()
    {
        var rows = new[]
        {
            Session(1) with { Session = Projection(1, 0, 10, ["label alpha"]) },
            Session(2) with { Session = Projection(2, 0, 9, ["skill alpha"]) },
            Session(3) with { Session = Projection(3, 0, 8, ["tool alpha"]) },
            Session(4, label: "alpha stale label and prohibited body path prompt") with { Session = Projection(4, 0, 7, []) }
        };
        var request = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":\"alpha\",\"cursor\":null,\"limit\":null}");

        using var json = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(new(new(LocalRepositoryScopeKind.All, null), [], rows), request, new byte[32]));

        Assert.Equal([rows[0].SessionId, rows[1].SessionId, rows[2].SessionId], json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("session_id").GetString()).Order().ToArray());
    }

    [Fact]
    public void CurrentSkillFilterDoesNotPromotePendingAggregateOrChangeUnfilteredBytes()
    {
        var source = Session(1);
        var aggregatePending = ((LocalWorkspaceProjectionRow)source.Session) with
        {
            Activity = ((LocalWorkspaceProjectionRow)source.Session).Activity with
            {
                Skill = new("certification_pending", null),
            },
            SearchTexts = ["paired-skill"],
        };
        var currentPair = source with
        {
            Session = aggregatePending with
            {
                CurrentSkillFilter = new("recorded", 1),
            },
        };
        var withoutFilterFact = source with { Session = aggregatePending };
        var key = new byte[32];
        var unfiltered = Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}");

        var expectedBytes = LocalMonitorV1CollectionApplication.SerializeSessions(
            new(new(LocalRepositoryScopeKind.All, null), [], [withoutFilterFact]), unfiltered, key);
        var actualBytes = LocalMonitorV1CollectionApplication.SerializeSessions(
            new(new(LocalRepositoryScopeKind.All, null), [], [currentPair]), unfiltered, key);

        Assert.Equal(expectedBytes, actualBytes);
        using (var document = JsonDocument.Parse(actualBytes))
        {
            var skill = Assert.Single(document.RootElement.GetProperty("items").EnumerateArray())
                .GetProperty("summary").GetProperty("skill");
            Assert.Equal("certification_pending", skill.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.Null, skill.GetProperty("count").ValueKind);
        }

        var hasSkill = unfiltered with { HasSkill = true };
        using var filtered = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(
            new(new(LocalRepositoryScopeKind.All, null), [], [currentPair]), hasSkill, key));
        Assert.Equal(source.SessionId,
            Assert.Single(filtered.RootElement.GetProperty("items").EnumerateArray()).GetProperty("session_id").GetString());
    }

    [Theory]
    [InlineData("copilot-sdk")]
    [InlineData("copilot-cli")]
    [InlineData("vscode")]
    [InlineData("hook-unknown")]
    [InlineData("claude-code")]
    public void EveryClosedSourceTokenFiltersAnActualProjection(string source)
    {
        var request=Parse("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[\""+source+"\"],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}");
        using var json=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeSessions(new(new(LocalRepositoryScopeKind.All,null),[],[Session(1,source:source),Session(2,source:"copilot-sdk"==source?"vscode":"copilot-sdk")]),request,new byte[32]));
        Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public void RepositoryCollectionSortsCountsPaginatesAndRevisesFromOneSnapshot()
    {
        var firstId="018f0000-0000-7000-8000-000000000101"; var secondId="018f0000-0000-7000-8000-000000000102";
        var repositories = new[] { new LocalRepositoryCatalogSnapshot(secondId,"Same",2,null,1,LocalArchiveState.Archived,1), new LocalRepositoryCatalogSnapshot(firstId,"Same",1,null,0,LocalArchiveState.Active,0) };
        var snapshot = new LocalRepositoryScopeSnapshot(new(LocalRepositoryScopeKind.All,null),repositories,[Session(1,firstId),Session(2)]);
        Assert.True(LocalMonitorV1RepositoryRequestParser.TryParse("?archive_scope=include_archived&limit=1",out var request));

        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var first=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot,request!, key));
        var root=first.RootElement; Assert.Equal(firstId,root.GetProperty("repositories")[0].GetProperty("repository_id").GetString());
        Assert.Equal("2026-01-01T00:00:01.0000000+00:00",root.GetProperty("repositories")[0].GetProperty("last_observed_at").GetString());
        Assert.Equal(2,root.GetProperty("all_session_count").GetInt32()); Assert.Equal(1,root.GetProperty("unassigned_active_session_count").GetInt32()); Assert.Equal(1,root.GetProperty("archived_repository_count").GetInt32());
        var revision=root.GetProperty("workspace_revision").GetString(); var cursor = root.GetProperty("next_cursor").GetString(); Assert.Equal(135, cursor!.Length);
        Assert.True(LocalMonitorV1RepositoryRequestParser.TryParse($"?archive_scope=include_archived&after={cursor}&limit=1",out var continuation));
        var renamed = snapshot with { Repositories = [repositories[0], repositories[1] with { DisplayName = "Renamed" }] };
        using var second=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(renamed,continuation!, key));
        Assert.Equal(secondId,second.RootElement.GetProperty("repositories")[0].GetProperty("repository_id").GetString()); Assert.Equal(JsonValueKind.Null,second.RootElement.GetProperty("next_cursor").ValueKind);
        using var stable=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot,request!, key)); Assert.Equal(revision,stable.RootElement.GetProperty("workspace_revision").GetString());
        using var changed=JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(snapshot with { Repositories=[repositories[0],repositories[1] with { Revision=3 }] },request!, key)); Assert.NotEqual(revision,changed.RootElement.GetProperty("workspace_revision").GetString());
    }

    [Fact]
    public void RepositoryLatestObservationUsesGreatestEligibleFullPrecisionInstant()
    {
        const string repositoryId = "018f0000-0000-7000-8000-000000000101";
        var repository = new LocalRepositoryCatalogSnapshot(repositoryId, "Same", 1, null, 0, LocalArchiveState.Active, 0);
        var earlier = Session(1, repositoryId) with { Session = LastSeenProjection(1, "2026-01-01T01:00:00.0000001+01:00", 1_767_225_600_000) };
        var greatest = Session(2, repositoryId) with { Session = LastSeenProjection(2, "2025-12-31T19:00:00.0000002-05:00", 1_767_225_600_000) };
        var ineligible = Session(3, repositoryId, archived: true) with { Session = LastSeenProjection(3, "2026-01-02T00:00:00.0000000+00:00", 1_767_312_000_000) };
        var malformed = Session(4, repositoryId) with { Session = LastSeenProjection(4, "malformed", 1_767_398_400_000) };
        var conflict = Session(5, repositoryId) with { AssignmentState = LocalRepositoryScopeAssignmentState.Conflict, Session = LastSeenProjection(5, "2026-01-04T00:00:00.0000000+00:00", 1_767_484_800_000) };
        var request = new LocalMonitorV1RepositoryRequest("active_only", null, 50);

        using var json = JsonDocument.Parse(LocalMonitorV1CollectionApplication.SerializeRepositories(new(new(LocalRepositoryScopeKind.All, null), [repository], [earlier, greatest, ineligible, malformed, conflict]), request, new byte[32]));

        var card = json.RootElement.GetProperty("repositories")[0];
        Assert.Equal(3, card.GetProperty("active_session_count").GetInt32());
        Assert.Equal("2026-01-01T00:00:00.0000002+00:00", card.GetProperty("last_observed_at").GetString());
    }

    private static LocalWorkspaceProjectionRow LastSeenProjection(int index, string? lastSeenAt, long? epoch)
    {
        var source = (LocalWorkspaceProjectionRow)Session(index).Session;
        return source with { LastSeenAt = lastSeenAt, LastSeenEpochMilliseconds = epoch };
    }

    private static LocalWorkspaceProjectionRow Projection(int index, long group, long epoch, IReadOnlyList<string> searchTexts)
    {
        var source = (LocalWorkspaceProjectionRow)Session(index).Session;
        return source with { SortGroup = group, SortEpochMilliseconds = epoch, SearchTexts = searchTexts };
    }

    private static LocalRepositoryScopeSessionSnapshot Session(int index, string? repositoryId = null, bool archived = false, string source = "copilot-sdk", string model = "m", string label = "label", long skillCount = 0, string status = "active", string? startedAt = "2026-01-01T00:00:00.0000000+00:00")
    {
        var id = $"018f0000-0000-7000-8000-{index:x12}";
        var fact = new LocalWorkspaceFact<long>("recorded", 0);
        var projection = new LocalWorkspaceProjectionRow(id, startedAt is null ? 1 : 0, startedAt is null ? 0 : 10_000-index, "recorded", label, status, "rich", new("recorded", [source]), new("recorded", [model]), new(new("recorded", skillCount), fact, fact, fact, fact),
            new("none", "not_observed", 0, 0, new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null), new("not_observed", null)),
            "not_observed", startedAt, null, "2026-01-01T00:00:01.0000000+00:00", null, [], $"seed-{index}");
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

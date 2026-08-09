using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1RouteTransportQueryTests
{
    private const string RepositoryId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";
    private const string SessionId = "018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072";
    private const string ComparisonId = "018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073";
    private const string ExecutionId = "018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6074";
    private const string AnalysisId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6075";
    private const string NodeId = "node-0123456789abcdef0123456789abcdef";
    private const string Cursor = "AZvvJSfubUCDILx2dEkk4j_S1wLGQUOW4o1TpZMGBmrYAAAAAZ_mZOZ7MDE4ZjJiNGUtN2MxYS03ZjFhLTlhMmItNmMzZDRlNWY2MDcyZb_UESMy6-2NWv8kzNcu3qwsgZxvWyIdPDe5nrnqQaw";

    [Theory]
    [InlineData("")]
    [InlineData("?")]
    public void EmptyQuery_IsEquivalentToNoQuery(string rawQuery)
    {
        Assert.True(LocalMonitorV1PageQueryParser.TryParse(LocalMonitorV1PrimaryRouteKind.AllSessions, rawQuery, out var query));

        Assert.NotNull(query);
        Assert.Empty(query.Sources);
        Assert.Empty(query.Statuses);
        Assert.Equal("active_only", query.ArchiveScope);
        Assert.Equal("/sessions", LocalMonitorV1CanonicalUrlBuilder.Build(
            LocalMonitorV1PrimaryPathParser.Classify("/sessions"), query));
    }

    [Fact]
    public void ExplorerQuery_ParsesOrderIndependentlyAndBuildsPinnedCanonicalOrder()
    {
        var rawQuery = $"?settings=storage&status=failed&source=vscode&to=2026-08-09T12:00:00.0000000%2B00:00&source=claude-code&has_retry=false&archive_scope=include_archived&cursor={Cursor}&from=2026-08-01T00:00:00.0000000%2B00:00&mode=compare&has_skill=true&has_error=false&has_subagent=true";

        Assert.True(LocalMonitorV1PageQueryParser.TryParse(LocalMonitorV1PrimaryRouteKind.AllSessions, rawQuery, out var query));
        Assert.NotNull(query);
        Assert.Equal(["claude-code", "vscode"], query.Sources);
        Assert.Equal(["failed"], query.Statuses);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), query.From);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero), query.To);
        Assert.True(query.HasSkill);
        Assert.True(query.HasSubagent);
        Assert.False(query.HasError);
        Assert.False(query.HasRetry);
        Assert.Equal("include_archived", query.ArchiveScope);
        Assert.Equal(Cursor, query.Cursor);
        Assert.True(query.CompareMode);
        Assert.Equal("storage", query.Settings);

        Assert.Equal(
            $"/sessions?from=2026-08-01T00:00:00.0000000%2B00:00&to=2026-08-09T12:00:00.0000000%2B00:00&source=claude-code&source=vscode&status=failed&has_skill=true&has_subagent=true&has_error=false&has_retry=false&archive_scope=include_archived&cursor={Cursor}&mode=compare&settings=storage",
            LocalMonitorV1CanonicalUrlBuilder.Build(
                LocalMonitorV1PrimaryPathParser.Classify("/sessions"),
                query,
                new(
                    "all",
                    null,
                    "include_archived",
                    query.From,
                    query.To,
                    query.Sources,
                    [],
                    query.Statuses,
                    query.HasSkill,
                    query.HasSubagent,
                    query.HasError,
                    query.HasRetry,
                    null,
                    null,
                    Cursor,
                    null)));
    }

    [Fact]
    public void ExplorerBuilder_OmitsDefaultArchiveScope()
    {
        Assert.True(LocalMonitorV1PageQueryParser.TryParse(
            LocalMonitorV1PrimaryRouteKind.RepositorySessions,
            "?archive_scope=active_only&status=unknown",
            out var query));

        Assert.Equal(
            $"/repositories/{RepositoryId}/sessions?status=unknown",
            LocalMonitorV1CanonicalUrlBuilder.Build(
                LocalMonitorV1PrimaryPathParser.Classify($"/repositories/{RepositoryId}/sessions"),
                query!));
    }

    [Fact]
    public void CanonicalUrlBuilder_RequiresRequestEvidenceBeforeWritingCursor()
    {
        Assert.True(LocalMonitorV1PageQueryParser.TryParse(
            LocalMonitorV1PrimaryRouteKind.AllSessions,
            $"?cursor={Cursor}",
            out var query));

        Assert.Throws<InvalidOperationException>(() => LocalMonitorV1CanonicalUrlBuilder.Build(
            LocalMonitorV1PrimaryPathParser.Classify("/sessions"),
            query!));
    }

    [Fact]
    public void SessionDetailQuery_UsesExactChildIdentitiesAndCanonicalOrder()
    {
        var rawQuery = $"?settings=ai&analysis={AnalysisId}&node={NodeId}&execution={ExecutionId}";

        Assert.True(LocalMonitorV1PageQueryParser.TryParse(LocalMonitorV1PrimaryRouteKind.SessionDetail, rawQuery, out var query));
        Assert.NotNull(query);
        Assert.Equal(ExecutionId, query.ExecutionId);
        Assert.Equal(NodeId, query.NodeId);
        Assert.Equal(AnalysisId, query.AnalysisId);
        Assert.Equal("ai", query.Settings);
        Assert.Equal(
            $"/sessions/{SessionId}?execution={ExecutionId}&node={NodeId}&analysis={AnalysisId}&settings=ai",
            LocalMonitorV1CanonicalUrlBuilder.Build(
                LocalMonitorV1PrimaryPathParser.Classify($"/sessions/{SessionId}"),
                query));
    }

    [Theory]
    [InlineData("/", "?settings=repositories", "/?settings=repositories")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/comparisons/018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073", "?settings=diagnostics", "/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/comparisons/018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073?settings=diagnostics")]
    public void SelectionAndComparison_AcceptOnlySettings(string path, string rawQuery, string expected)
    {
        var parsedPath = LocalMonitorV1PrimaryPathParser.Classify(path);
        Assert.True(LocalMonitorV1PageQueryParser.TryParse(parsedPath.RouteKind!.Value, rawQuery, out var query));

        Assert.Equal(expected, LocalMonitorV1CanonicalUrlBuilder.Build(parsedPath, query!));
    }

    [Theory]
    [InlineData("?unknown=value")]
    [InlineData("?q=search")]
    [InlineData("?model=model-a")]
    [InlineData("?scope=all")]
    [InlineData("?repository_id=018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071")]
    [InlineData("?limit=50")]
    [InlineData("?activity=true")]
    [InlineData("?source=vscode&source=vscode")]
    [InlineData("?settings=ai&settings=storage")]
    [InlineData("?source=")]
    [InlineData("?=vscode")]
    [InlineData("?source=vscode&")]
    [InlineData("?source=vscode;status=failed")]
    [InlineData("?source=vscode=status")]
    [InlineData("?source=%76scode")]
    [InlineData("?source=VSCODE")]
    [InlineData("?status=missing")]
    [InlineData("?has_skill=True")]
    [InlineData("?archive_scope=all")]
    [InlineData("?mode=single")]
    [InlineData("?settings=AI")]
    [InlineData("?cursor=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("?cursor=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void ExplorerQuery_RejectsUnknownDuplicateEmptyOrNoncanonicalComponents(string rawQuery) =>
        Assert.False(LocalMonitorV1PageQueryParser.TryParse(LocalMonitorV1PrimaryRouteKind.AllSessions, rawQuery, out _));

    [Theory]
    [InlineData("?from=2026-08-01T00:00:00.0000000+00:00")]
    [InlineData("?from=2026-08-01T00:00:00.0000000%2b00:00")]
    [InlineData("?from=2026-08-01T00%3A00:00.0000000%2B00:00")]
    [InlineData("?from=2026-08-01T00:00:00Z")]
    [InlineData("?from=2026-02-30T00:00:00.0000000%2B00:00")]
    [InlineData("?from=2026-08-01T00:00:00.0000000%2B00:00&to=2026-08-01T00:00:00.0000000%2B00:00")]
    [InlineData("?from=2026-08-02T00:00:00.0000000%2B00:00&to=2026-08-01T00:00:00.0000000%2B00:00")]
    public void ExplorerQuery_RejectsNoncanonicalOrNonIncreasingTimestamps(string rawQuery) =>
        Assert.False(LocalMonitorV1PageQueryParser.TryParse(LocalMonitorV1PrimaryRouteKind.AllSessions, rawQuery, out _));

    [Theory]
    [InlineData("?execution=018F2B4E-7C1A-7F1A-BA2B-6C3D4E5F6074")]
    [InlineData("?node=node-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("?analysis=latest")]
    [InlineData("?source=vscode")]
    public void SessionDetailQuery_RejectsMalformedChildrenAndExplorerKeys(string rawQuery) =>
        Assert.False(LocalMonitorV1PageQueryParser.TryParse(LocalMonitorV1PrimaryRouteKind.SessionDetail, rawQuery, out _));

    [Theory]
    [InlineData("?source=vscode")]
    [InlineData("?execution=018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6074")]
    [InlineData("?settings=ai&settings=ai")]
    public void SelectionQuery_RejectsEveryKeyExceptOneSettings(string rawQuery) =>
        Assert.False(LocalMonitorV1PageQueryParser.TryParse(LocalMonitorV1PrimaryRouteKind.RepositorySelection, rawQuery, out _));
}

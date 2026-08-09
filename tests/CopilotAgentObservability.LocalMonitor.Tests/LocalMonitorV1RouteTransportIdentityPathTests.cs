using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1RouteTransportIdentityPathTests
{
    private const string RepositoryId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";
    private const string SessionId = "018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072";
    private const string ComparisonId = "018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073";

    [Theory]
    [InlineData("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071", true)]
    [InlineData("018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6071", true)]
    [InlineData("018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6071", true)]
    [InlineData("018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6071", true)]
    [InlineData("018F2B4E-7C1A-7F1A-8A2B-6C3D4E5F6071", false)]
    [InlineData("018f2b4e-7c1a-6f1a-8a2b-6c3d4e5f6071", false)]
    [InlineData("018f2b4e-7c1a-7f1a-7a2b-6c3d4e5f6071", false)]
    [InlineData("{018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071}", false)]
    [InlineData("018f2b4e7c1a7f1a8a2b6c3d4e5f6071", false)]
    [InlineData("018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f607g", false)]
    [InlineData(" 018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071", false)]
    public void CanonicalUuidV7Parser_RequiresExactLowercaseDForm(string value, bool accepted)
    {
        var parsed = LocalMonitorV1Identity.TryParseUuidV7(value, out var id);

        Assert.Equal(accepted, parsed);
        if (accepted) Assert.Equal(value, id.ToString("D"));
    }

    [Theory]
    [InlineData("node-0123456789abcdef0123456789abcdef", true)]
    [InlineData("node-0123456789abcdef0123456789abcde", false)]
    [InlineData("node-0123456789abcdef0123456789abcdef0", false)]
    [InlineData("node-0123456789ABCDEF0123456789ABCDEF", false)]
    [InlineData("NODE-0123456789abcdef0123456789abcdef", false)]
    [InlineData("node-0123456789abcdef0123456789abcdeg", false)]
    public void NodeParser_RequiresPrefixAndThirtyTwoLowerHex(string value, bool accepted) =>
        Assert.Equal(accepted, LocalMonitorV1Identity.IsCanonicalNodeId(value));

    [Theory]
    [MemberData(nameof(MatchedPaths))]
    public void PrimaryPathParser_ReturnsTypedIdentityForExactPrimaryPaths(
        string rawPath,
        string kind,
        string? repositoryId,
        string? sessionId,
        string? comparisonId)
    {
        var result = LocalMonitorV1PrimaryPathParser.Classify(rawPath);

        Assert.Equal(LocalMonitorV1PathClassification.Matched, result.Classification);
        Assert.Equal(kind, result.RouteKind?.ToString());
        Assert.Equal(repositoryId, result.RepositoryId);
        Assert.Equal(sessionId, result.SessionId);
        Assert.Equal(comparisonId, result.ComparisonId);
    }

    public static TheoryData<string, string, string?, string?, string?> MatchedPaths => new()
    {
        { "/", "RepositorySelection", null, null, null },
        { $"/repositories/{RepositoryId}/sessions", "RepositorySessions", RepositoryId, null, null },
        { "/sessions", "AllSessions", null, null, null },
        { "/sessions/unassigned", "UnassignedSessions", null, null, null },
        { $"/sessions/{SessionId}", "SessionDetail", null, SessionId, null },
        { $"/repositories/{RepositoryId}/comparisons/{ComparisonId}", "ComparisonDetail", RepositoryId, null, ComparisonId },
    };

    [Theory]
    [InlineData("/sessions/018F2B4E-7C1A-7F1A-9A2B-6C3D4E5F6072")]
    [InlineData("/sessions/not-a-session")]
    [InlineData("/sessions/.")]
    [InlineData("/sessions/..")]
    [InlineData("/sessions/%41")]
    [InlineData("/sessions/%")]
    [InlineData("/sessions/back\\slash")]
    [InlineData("/sessions/control\u001f")]
    [InlineData("/repositories/not-a-repository/sessions")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/comparisons/not-a-comparison")]
    public void PrimaryPathParser_ClassifiesMatchedMalformedVariableAsInvalidRequest(string rawPath)
    {
        var result = LocalMonitorV1PrimaryPathParser.Classify(rawPath);

        Assert.Equal(LocalMonitorV1PathClassification.MatchedInvalid, result.Classification);
        Assert.Null(result.RouteKind);
        Assert.Null(result.RepositoryId);
        Assert.Null(result.SessionId);
        Assert.Null(result.ComparisonId);
    }

    [Theory]
    [InlineData("/sessions/Unassigned")]
    [InlineData("/sessions/UNASSIGNED")]
    [InlineData("/Sessions/unassigned")]
    [InlineData("/sessions/")]
    [InlineData("/sessions//")]
    [InlineData("/sessions/not%2Fa-session")]
    [InlineData("/sessions/not%2fa-session")]
    [InlineData("/sessions/not%5Ca-session")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071//sessions")]
    [InlineData("/Repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/sessions")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/Sessions")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/sessions/")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/comparisons")]
    [InlineData("/repositories/018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071/comparisons/018f2b4e-7c1a-7f1a-aa2b-6c3d4e5f6073/extra")]
    [InlineData("/sess%69ons/018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072")]
    [InlineData("")]
    public void PrimaryPathParser_ClassifiesNearPathsAsEmptyNotFound(string rawPath)
    {
        var result = LocalMonitorV1PrimaryPathParser.Classify(rawPath);

        Assert.Equal(LocalMonitorV1PathClassification.NearPath, result.Classification);
        Assert.Null(result.RouteKind);
        Assert.Null(result.RepositoryId);
        Assert.Null(result.SessionId);
        Assert.Null(result.ComparisonId);
    }
}

using System.Text;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionSearchRequestTests
{
    private const string RepositoryId = "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071";
    private const string Cursor = "AZvvJSfubUCDILx2dEkk4j_S1wLGQUOW4o1TpZMGBmrYAAAAAZ_mZOZ7MDE4ZjJiNGUtN2MxYS03ZjFhLTlhMmItNmMzZDRlNWY2MDcyZb_UESMy6-2NWv8kzNcu3qwsgZxvWyIdPDe5nrnqQaw";
    private const string MinimalBody = "{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}";

    [Fact]
    public void Parser_AcceptsCanonicalMinimumAndAppliesOnlyTypedDefaultLimit()
    {
        var status = Parse(MinimalBody, out var request);

        Assert.Equal("Success", status);
        Assert.NotNull(request);
        Assert.Equal("all", request.Scope);
        Assert.Null(request.RepositoryId);
        Assert.Equal("active_only", request.ArchiveScope);
        Assert.Empty(request.Sources);
        Assert.Empty(request.Models);
        Assert.Empty(request.Statuses);
        Assert.Null(request.QueryOriginal);
        Assert.Null(request.QueryNormalized);
        Assert.Null(request.Limit);
        Assert.Equal(50, request.EffectiveLimit);
        Assert.True(LocalMonitorV1UrlState.IsCursorEligible(request));
    }

    [Fact]
    public void Parser_AcceptsNonsemanticPropertyAndArrayOrderAndReturnsExactNormalizedValues()
    {
        var body = "{\"limit\":200,\"cursor\":null,\"q\":\"ＦＯＯ\",\"has_retry\":false,\"has_error\":true,\"has_subagent\":false,\"has_skill\":true,\"status\":[\"unknown\",\"active\"],\"model\":[\"z-model\",\"Model-A\"],\"source\":[\"vscode\",\"claude-code\"],\"to\":\"2026-08-09T12:00:00.0000000+00:00\",\"from\":\"2026-08-01T00:00:00.0000000+00:00\",\"archive_scope\":\"include_archived\",\"repository_id\":\"018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071\",\"scope\":\"repository\",\"schema_version\":\"local-monitor-session-search.request.v1\"}";

        var status = Parse(body, out var request);

        Assert.Equal("Success", status);
        Assert.NotNull(request);
        Assert.Equal("repository", request.Scope);
        Assert.Equal(RepositoryId, request.RepositoryId);
        Assert.Equal("include_archived", request.ArchiveScope);
        Assert.Equal(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), request.From);
        Assert.Equal(new DateTimeOffset(2026, 8, 9, 12, 0, 0, TimeSpan.Zero), request.To);
        Assert.Equal(["claude-code", "vscode"], request.Sources);
        Assert.Equal(["Model-A", "z-model"], request.Models);
        Assert.Equal(["active", "unknown"], request.Statuses);
        Assert.True(request.HasSkill);
        Assert.False(request.HasSubagent);
        Assert.True(request.HasError);
        Assert.False(request.HasRetry);
        Assert.Equal("ＦＯＯ", request.QueryOriginal);
        Assert.Equal("foo", request.QueryNormalized);
        Assert.Equal(200, request.Limit);
        Assert.Equal(200, request.EffectiveLimit);
        Assert.False(LocalMonitorV1UrlState.IsCursorEligible(request));
    }

    [Fact]
    public void Parser_AcceptsExactlyThirtyTwoKiBIncludingTrailingJsonWhitespace()
    {
        var bytes = Encoding.UTF8.GetBytes(MinimalBody);
        Array.Resize(ref bytes, 32_768);
        bytes.AsSpan(Encoding.UTF8.GetByteCount(MinimalBody)).Fill((byte)' ');

        var status = LocalMonitorV1SessionSearchRequestParser.Parse(bytes, out var request);

        Assert.Equal("Success", status.ToString());
        Assert.NotNull(request);
    }

    [Fact]
    public void Parser_ClassifiesOneByteOverBodyLimitBeforeJsonParsing()
    {
        var status = LocalMonitorV1SessionSearchRequestParser.Parse(new byte[32_769], out var request);

        Assert.Equal("RequestTooLarge", status.ToString());
        Assert.Null(request);
    }

    [Fact]
    public void Parser_PreservesCursorTextForTheLaterInvalidCursorStage()
    {
        var body = MinimalBody.Replace("\"cursor\":null", "\"cursor\":\"not-a-cursor\"", StringComparison.Ordinal);

        var status = Parse(body, out var request);

        Assert.Equal("Success", status);
        Assert.Equal("not-a-cursor", request!.Cursor);
    }

    [Theory]
    [MemberData(nameof(ClosedObjectFailures))]
    public void Parser_RejectsNonclosedOrMistypedObjects(string body) =>
        Assert.Equal("InvalidRequest", Parse(body, out _));

    public static IEnumerable<object[]> ClosedObjectFailures()
    {
        yield return [MinimalBody.Replace(",\"limit\":null", string.Empty, StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("\"limit\":null}", "\"limit\":null,\"unknown\":null}", StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("\"scope\":\"all\"", "\"scope\":\"all\",\"scope\":\"all\"", StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("\"scope\":\"all\"", "\"Scope\":\"all\"", StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("local-monitor-session-search.request.v1", "local-monitor-session-search.request.v2", StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("\"archive_scope\":\"active_only\"", "\"archive_scope\":null", StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("\"source\":[]", "\"source\":null", StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("\"has_skill\":null", "\"has_skill\":\"true\"", StringComparison.Ordinal)];
        yield return [MinimalBody.Replace("\"q\":null", "\"q\":123", StringComparison.Ordinal)];
        yield return ["[]"];
        yield return [MinimalBody + "{}"];
        yield return [MinimalBody.Replace("\"model\":[]", "\"model\":[[[[[]]]]]", StringComparison.Ordinal)];
    }

    [Theory]
    [InlineData("all", null, true)]
    [InlineData("unassigned", null, true)]
    [InlineData("repository", RepositoryId, true)]
    [InlineData("all", RepositoryId, false)]
    [InlineData("unassigned", RepositoryId, false)]
    [InlineData("repository", null, false)]
    [InlineData("other", null, false)]
    [InlineData("Repository", RepositoryId, false)]
    public void Parser_EnforcesExactScopeRepositoryPairing(string scope, string? repositoryId, bool accepted)
    {
        var body = MinimalBody
            .Replace("\"scope\":\"all\"", $"\"scope\":{JsonSerializer.Serialize(scope)}", StringComparison.Ordinal)
            .Replace("\"repository_id\":null", $"\"repository_id\":{JsonSerializer.Serialize(repositoryId)}", StringComparison.Ordinal);

        Assert.Equal(accepted ? "Success" : "InvalidRequest", Parse(body, out _));
    }

    [Theory]
    [InlineData(null, null, true)]
    [InlineData("2026-08-01T00:00:00.0000000+00:00", "2026-08-09T00:00:00.0000000+00:00", true)]
    [InlineData("2026-08-01T00:00:00.0000000Z", null, false)]
    [InlineData("2026-08-01T00:00:00.000000+00:00", null, false)]
    [InlineData("2026-02-30T00:00:00.0000000+00:00", null, false)]
    [InlineData("2026-08-01T00:00:00.0000000+01:00", null, false)]
    [InlineData("2026-08-09T00:00:00.0000000+00:00", "2026-08-01T00:00:00.0000000+00:00", false)]
    [InlineData("2026-08-01T00:00:00.0000000+00:00", "2026-08-01T00:00:00.0000000+00:00", false)]
    public void Parser_EnforcesExactUtcInterval(string? from, string? to, bool accepted)
    {
        var body = MinimalBody
            .Replace("\"from\":null", $"\"from\":{JsonSerializer.Serialize(from)}", StringComparison.Ordinal)
            .Replace("\"to\":null", $"\"to\":{JsonSerializer.Serialize(to)}", StringComparison.Ordinal);

        Assert.Equal(accepted ? "Success" : "InvalidRequest", Parse(body, out _));
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("[\"vscode\",\"claude-code\"]", true)]
    [InlineData("[\"vscode\",\"vscode\"]", false)]
    [InlineData("[\"VSCODE\"]", false)]
    [InlineData("[null]", false)]
    public void Parser_EnforcesClosedDistinctSourceSet(string json, bool accepted)
    {
        var body = MinimalBody.Replace("\"source\":[]", $"\"source\":{json}", StringComparison.Ordinal);

        Assert.Equal(accepted ? "Success" : "InvalidRequest", Parse(body, out _));
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("[\"failed\",\"active\"]", true)]
    [InlineData("[\"failed\",\"failed\"]", false)]
    [InlineData("[\"missing\"]", false)]
    public void Parser_EnforcesClosedDistinctStatusSet(string json, bool accepted)
    {
        var body = MinimalBody.Replace("\"status\":[]", $"\"status\":{json}", StringComparison.Ordinal);

        Assert.Equal(accepted ? "Success" : "InvalidRequest", Parse(body, out _));
    }

    [Fact]
    public void Parser_EnforcesModelScalarByteCharacterCountAndDistinctness()
    {
        var twoHundredFiftySixBytes = string.Concat(Enumerable.Repeat("😀", 64));
        var overBytes = string.Concat(Enumerable.Repeat("😀", 65));
        var sixteen = Enumerable.Range(0, 16).Select(index => $"model-{index:D2}").ToArray();
        var seventeen = Enumerable.Range(0, 17).Select(index => $"model-{index:D2}").ToArray();

        Assert.Equal("Success", Parse(WithArray("model", [new string('a', 128)]), out _));
        Assert.Equal("Success", Parse(WithArray("model", [twoHundredFiftySixBytes]), out _));
        Assert.Equal("Success", Parse(WithArray("model", sixteen), out _));
        Assert.Equal("InvalidRequest", Parse(WithArray("model", [string.Empty]), out _));
        Assert.Equal("InvalidRequest", Parse(WithArray("model", [new string('a', 129)]), out _));
        Assert.Equal("InvalidRequest", Parse(WithArray("model", [overBytes]), out _));
        Assert.Equal("InvalidRequest", Parse(WithArray("model", ["model\u0085value"]), out _));
        Assert.Equal("InvalidRequest", Parse(WithArray("model", ["model\u2028value"]), out _));
        Assert.Equal("InvalidRequest", Parse(WithArray("model", ["same", "same"]), out _));
        Assert.Equal("InvalidRequest", Parse(WithArray("model", seventeen), out _));
    }

    [Fact]
    public void Parser_PreservesOriginalQueryAndBoundsNfkcLowercaseNormalization()
    {
        var eightHundredBytes = string.Concat(Enumerable.Repeat("😀", 200));
        var overScalars = string.Concat(Enumerable.Repeat("😀", 201));
        var expandsPastEightHundredBytes = new string('\ufdfa', 25);

        Assert.Equal("Success", Parse(WithQuery(eightHundredBytes), out var request));
        Assert.Equal(eightHundredBytes, request!.QueryOriginal);
        Assert.Equal(eightHundredBytes, request.QueryNormalized);
        Assert.Equal("InvalidRequest", Parse(WithQuery(string.Empty), out _));
        Assert.Equal("InvalidRequest", Parse(WithQuery(overScalars), out _));
        Assert.Equal("InvalidRequest", Parse(WithQuery(expandsPastEightHundredBytes), out _));
        Assert.Equal("InvalidRequest", Parse(MinimalBody.Replace("\"q\":null", "\"q\":\"\\ud800\"", StringComparison.Ordinal), out _));
    }

    [Theory]
    [InlineData("null", true, 50)]
    [InlineData("1", true, 1)]
    [InlineData("200", true, 200)]
    [InlineData("0", false, 0)]
    [InlineData("201", false, 0)]
    [InlineData("1.0", false, 0)]
    [InlineData("1e2", false, 0)]
    [InlineData("-0", false, 0)]
    [InlineData("\"1\"", false, 0)]
    [InlineData("true", false, 0)]
    public void Parser_EnforcesCanonicalNullableLimit(string json, bool accepted, int effectiveLimit)
    {
        var body = MinimalBody.Replace("\"limit\":null", $"\"limit\":{json}", StringComparison.Ordinal);

        var status = Parse(body, out var request);

        Assert.Equal(accepted ? "Success" : "InvalidRequest", status);
        if (accepted) Assert.Equal(effectiveLimit, request!.EffectiveLimit);
    }

    [Fact]
    public void Parser_RejectsBomInvalidUtf8AndExtraJsonValue()
    {
        var valid = Encoding.UTF8.GetBytes(MinimalBody);
        var bom = new byte[valid.Length + 3];
        "\ufeff"u8.CopyTo(bom);
        valid.CopyTo(bom.AsSpan(3));

        Assert.Equal("InvalidRequest", LocalMonitorV1SessionSearchRequestParser.Parse(bom, out _).ToString());
        Assert.Equal("InvalidRequest", LocalMonitorV1SessionSearchRequestParser.Parse(new byte[] { 0xc3, 0x28 }, out _).ToString());
        Assert.Equal("InvalidRequest", Parse(MinimalBody + " {}", out _));
    }

    [Fact]
    public void CanonicalUrlBuilder_RemovesCursorUnlessDynamicStateAndLimitAreExactlyDefault()
    {
        Assert.True(LocalMonitorV1PageQueryParser.TryParse(
            LocalMonitorV1PrimaryRouteKind.AllSessions,
            $"?cursor={Cursor}&status=failed",
            out var pageQuery));
        var path = LocalMonitorV1PrimaryPathParser.Classify("/sessions");
        var filteredBody = MinimalBody
            .Replace("\"status\":[]", "\"status\":[\"failed\"]", StringComparison.Ordinal)
            .Replace("\"cursor\":null", $"\"cursor\":\"{Cursor}\"", StringComparison.Ordinal);
        var safeRequest = ParseRequest(filteredBody);
        var qRequest = ParseRequest(filteredBody.Replace("\"q\":null", "\"q\":\"search\"", StringComparison.Ordinal));
        var modelRequest = ParseRequest(filteredBody.Replace("\"model\":[]", "\"model\":[\"model-a\"]", StringComparison.Ordinal));
        var limitRequest = ParseRequest(filteredBody.Replace("\"limit\":null", "\"limit\":50", StringComparison.Ordinal));

        Assert.Equal($"/sessions?status=failed&cursor={Cursor}", LocalMonitorV1CanonicalUrlBuilder.Build(path, pageQuery!, safeRequest));
        Assert.Equal("/sessions?status=failed", LocalMonitorV1CanonicalUrlBuilder.Build(path, pageQuery!, qRequest));
        Assert.Equal("/sessions?status=failed", LocalMonitorV1CanonicalUrlBuilder.Build(path, pageQuery!, modelRequest));
        Assert.Equal("/sessions?status=failed", LocalMonitorV1CanonicalUrlBuilder.Build(path, pageQuery!, limitRequest));
    }

    [Fact]
    public void CanonicalUrlBuilder_FailsClosedWhenPathAndBodySafeStateDiffer()
    {
        Assert.True(LocalMonitorV1PageQueryParser.TryParse(
            LocalMonitorV1PrimaryRouteKind.AllSessions,
            "?status=failed",
            out var pageQuery));
        var path = LocalMonitorV1PrimaryPathParser.Classify("/sessions");
        var wrongScope = ParseRequest(MinimalBody
            .Replace("\"scope\":\"all\"", "\"scope\":\"unassigned\"", StringComparison.Ordinal)
            .Replace("\"status\":[]", "\"status\":[\"failed\"]", StringComparison.Ordinal));
        var wrongStatus = ParseRequest(MinimalBody.Replace("\"status\":[]", "\"status\":[\"active\"]", StringComparison.Ordinal));

        Assert.Throws<InvalidOperationException>(() => LocalMonitorV1CanonicalUrlBuilder.Build(path, pageQuery!, wrongScope));
        Assert.Throws<InvalidOperationException>(() => LocalMonitorV1CanonicalUrlBuilder.Build(path, pageQuery!, wrongStatus));
    }

    private static string WithArray(string property, IReadOnlyList<string> values) =>
        MinimalBody.Replace($"\"{property}\":[]", $"\"{property}\":{JsonSerializer.Serialize(values)}", StringComparison.Ordinal);

    private static string WithQuery(string value) =>
        MinimalBody.Replace("\"q\":null", $"\"q\":{JsonSerializer.Serialize(value)}", StringComparison.Ordinal);

    private static string Parse(string body, out LocalMonitorV1SessionSearchRequest? request) =>
        LocalMonitorV1SessionSearchRequestParser.Parse(Encoding.UTF8.GetBytes(body), out request).ToString();

    private static LocalMonitorV1SessionSearchRequest ParseRequest(string body)
    {
        Assert.Equal("Success", Parse(body, out var request));
        return request!;
    }
}

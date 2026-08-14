using System.Text;
using CopilotAgentObservability.LocalMonitor.Archive;
using Microsoft.Extensions.Primitives;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalArchiveWireTests
{
    private const string SessionId = "01890f65-4c31-7f42-8a7d-111111111111";
    private const string RepositoryId = "01900000-0000-7000-8000-000000000001";
    private const string Timestamp = "2026-08-09T12:34:56.1234567+00:00";
    private const string GoldenSessionCursor = "bG9jYWwtYXJjaGl2ZS1jdXJzb3IAdjEAc2Vzc2lvbgAyMDI2LTA4LTA5VDEyOjM0OjU2LjEyMzQ1NjcrMDA6MDAAMDE4OTBmNjUtNGMzMS03ZjQyLThhN2QtMTExMTExMTExMTEx";

    [Fact]
    public void DirectQueryParser_AcceptsEitherFieldOrderAndRejectsNoncanonicalGrammar()
    {
        Assert.True(LocalArchiveWire.TryParseDirectQuery(
            $"?target_id={SessionId}&target_kind=session", out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(LocalArchiveTargetKind.Session, parsed!.TargetKind);
        Assert.Equal(SessionId, parsed.TargetId);

        foreach (var raw in new[]
        {
            null, "", "?", $"?target_kind=session", $"?target_id={SessionId}",
            $"?target_kind=session&target_kind=session&target_id={SessionId}",
            $"?target_kind=session&target_id={SessionId}&unknown=x",
            $"?target%5Fkind=session&target_id={SessionId}",
            $"?target_kind=%73ession&target_id={SessionId}",
            "?target_kind=session&target_id=01900000-0000-4000-8000-000000000001",
        })
        {
            Assert.False(LocalArchiveWire.TryParseDirectQuery(raw, out _, out error));
            Assert.Equal(LocalArchiveWireError.InvalidRequest, error);
        }
    }

    [Fact]
    public void ListQueryParser_CompletesGrammarBeforeDecodingCursor()
    {
        Assert.True(LocalArchiveWire.TryParseListQuery(
            $"?after={GoldenSessionCursor}&limit=200&target_kind=session", out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(200, parsed!.Limit);
        Assert.Equal(Timestamp, parsed.After!.ArchivedAt);
        Assert.Equal(SessionId, parsed.After.TargetId);

        Assert.False(LocalArchiveWire.TryParseListQuery("?target_kind=invalid&after=abc", out _, out error));
        Assert.Equal(LocalArchiveWireError.InvalidRequest, error);
        Assert.False(LocalArchiveWire.TryParseListQuery("?target_kind=session&limit=0&after=abc", out _, out error));
        Assert.Equal(LocalArchiveWireError.InvalidRequest, error);
        Assert.False(LocalArchiveWire.TryParseListQuery("?target_kind=session&after=abc", out _, out error));
        Assert.Equal(LocalArchiveWireError.InvalidCursor, error);
    }

    [Theory]
    [InlineData("?target_kind=session", 50)]
    [InlineData("?target_kind=repository&limit=1", 1)]
    [InlineData("?limit=200&target_kind=session", 200)]
    public void ListQueryParser_AcceptsCanonicalLimits(string raw, int expectedLimit)
    {
        Assert.True(LocalArchiveWire.TryParseListQuery(raw, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(expectedLimit, parsed!.Limit);
        Assert.Null(parsed.After);
    }

    [Theory]
    [InlineData("?target_kind=session&limit=00")]
    [InlineData("?target_kind=session&limit=01")]
    [InlineData("?target_kind=session&limit=201")]
    [InlineData("?target_kind=session&limit=+1")]
    [InlineData("?target_kind=session&after=")]
    [InlineData("?target_kind=session&after=a+b")]
    [InlineData("?target_kind=session&after=a%2Db")]
    [InlineData("?target_kind=session&after=abc=")]
    [InlineData("?target_kind=session&after=abc&after=abc")]
    [InlineData("?target_kind=session&unknown=x")]
    public void ListQueryParser_RejectsNoncanonicalLexemes(string raw)
    {
        Assert.False(LocalArchiveWire.TryParseListQuery(raw, out _, out var error));
        Assert.Equal(LocalArchiveWireError.InvalidRequest, error);
    }

    [Fact]
    public void CursorCodec_RoundTripsGoldenFrameAndRejectsCrossKindReuse()
    {
        var cursor = new LocalArchiveCursor(Timestamp, SessionId);
        Assert.Equal(GoldenSessionCursor, LocalArchiveCursorCodec.Encode(LocalArchiveTargetKind.Session, cursor));
        Assert.True(LocalArchiveCursorCodec.TryDecode(GoldenSessionCursor, LocalArchiveTargetKind.Session, out var decoded));
        Assert.Equal(cursor, decoded);
        Assert.False(LocalArchiveCursorCodec.TryDecode(GoldenSessionCursor, LocalArchiveTargetKind.Repository, out _));

        var repositoryCursor = LocalArchiveCursorCodec.Encode(
            LocalArchiveTargetKind.Repository, new(Timestamp, RepositoryId));
        Assert.Equal(140, repositoryCursor.Length);
        Assert.True(LocalArchiveCursorCodec.TryDecode(repositoryCursor, LocalArchiveTargetKind.Repository, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void CursorCodec_RejectsWrongLengthOrFrame(string encoded)
    {
        Assert.False(LocalArchiveCursorCodec.TryDecode(encoded, LocalArchiveTargetKind.Session, out _));
    }

    [Fact]
    public void CursorCodec_RejectsExactLengthInvalidTimestampAndUuid()
    {
        Assert.False(LocalArchiveCursorCodec.TryDecode(
            RawCursor("session", "2026-13-09T12:34:56.1234567+00:00", SessionId),
            LocalArchiveTargetKind.Session,
            out _));
        Assert.False(LocalArchiveCursorCodec.TryDecode(
            RawCursor("session", Timestamp, "01890f65-4c31-4f42-8a7d-111111111111"),
            LocalArchiveTargetKind.Session,
            out _));
    }

    [Fact]
    public void ActionParser_AcceptsPropertyReorderingAndFreezesOriginalTargetOrder()
    {
        var second = "01890f65-4c31-7f42-8a7d-222222222222";
        var json = $$"""
            {"targets":[{"expected_revision":0,"target_id":"{{SessionId}}"},{"target_id":"{{second}}","expected_revision":9223372036854775807}],"target_kind":"session","action":"archive","schema_version":"local-archive-action.v1"}
            """;

        Assert.True(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(json), out var parsed));
        Assert.Equal(LocalArchiveAction.Archive, parsed!.Action);
        Assert.Equal([SessionId, second], parsed.Targets.Select(static target => target.TargetId));
        Assert.Equal([0, long.MaxValue], parsed.Targets.Select(static target => target.ExpectedRevision));
    }

    [Fact]
    public void PostAdmission_AcceptsOnlyEmptyQueryAndExactJsonMedia()
    {
        Assert.True(LocalArchiveWire.HasNoSemanticQuery(null));
        Assert.True(LocalArchiveWire.HasNoSemanticQuery(string.Empty));
        Assert.True(LocalArchiveWire.HasNoSemanticQuery("?"));
        Assert.False(LocalArchiveWire.HasNoSemanticQuery("?x="));

        Assert.True(LocalArchiveWire.HasSupportedPostMedia(
            new StringValues("application/json"), StringValues.Empty));
        Assert.True(LocalArchiveWire.HasSupportedPostMedia(
            new StringValues("Application/Json; Charset=UTF-8"), StringValues.Empty));
        foreach (var contentType in new[]
        {
            "application/json; charset=\"utf-8\"",
            "application/json; charset=utf-16",
            "application/json; unknown=utf-8",
            "application/json; charset=utf-8; charset=utf-8",
            "text/json",
        })
        {
            Assert.False(LocalArchiveWire.HasSupportedPostMedia(
                new StringValues(contentType), StringValues.Empty));
        }
        Assert.False(LocalArchiveWire.HasSupportedPostMedia(StringValues.Empty, StringValues.Empty));
        Assert.False(LocalArchiveWire.HasSupportedPostMedia(
            new StringValues(["application/json", "application/json"]), StringValues.Empty));
        Assert.False(LocalArchiveWire.HasSupportedPostMedia(
            new StringValues("application/json"), new StringValues("identity")));
    }

    public static IEnumerable<object[]> InvalidActionBodies()
    {
        yield return ["{}"];
        yield return ["[]"];
        yield return ["{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"repository\",\"targets\":[]}"];
        yield return [ValidAction().Replace("\"expected_revision\":0", "\"expected_revision\":-0", StringComparison.Ordinal)];
        yield return [ValidAction().Replace("\"expected_revision\":0", "\"expected_revision\":1e0", StringComparison.Ordinal)];
        yield return [ValidAction().Replace("\"expected_revision\":0", "\"expected_revision\":9223372036854775808", StringComparison.Ordinal)];
        yield return [ValidAction().Replace("\"target_id\":", "\"extra\":null,\"target_id\":", StringComparison.Ordinal)];
        yield return [ValidAction().Replace("\"action\":\"archive\"", "\"action\":\"archive\",\"action\":\"archive\"", StringComparison.Ordinal)];
        yield return [ValidAction().Replace(SessionId, "01890F65-4C31-7F42-8A7D-111111111111", StringComparison.Ordinal)];
        yield return [ValidAction().Replace("local-archive-action.v1", "local-archive-action.v2", StringComparison.Ordinal)];
        yield return [ValidAction().Replace("\"action\":\"archive\"", "\"action\":\"remove\"", StringComparison.Ordinal)];
        yield return [ValidAction().Replace("\"target_kind\":\"session\"", "\"target_kind\":\"trace\"", StringComparison.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(InvalidActionBodies))]
    public void ActionParser_RejectsEveryClosedShapeViolation(string json)
    {
        Assert.False(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void ActionParser_RejectsBomDuplicateTargetsAndWrongRepositoryCardinality()
    {
        var bom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(ValidAction())).ToArray();
        Assert.False(LocalArchiveWire.TryParseActionBody(bom, out _));

        var target = $"{{\"target_id\":\"{SessionId}\",\"expected_revision\":0}}";
        var duplicate = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{target},{target}]}}";
        Assert.False(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(duplicate), out _));

        var twoRepositories = duplicate.Replace("\"target_kind\":\"session\"", "\"target_kind\":\"repository\"", StringComparison.Ordinal);
        Assert.False(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(twoRepositories), out _));
    }

    [Fact]
    public void ActionParser_RejectsSessionCardinalityAboveTwoHundred()
    {
        var targets = string.Join(',', Enumerable.Range(1, 201).Select(index =>
            $"{{\"target_id\":\"01900000-0000-7000-8000-{index:000000000000}\",\"expected_revision\":0}}"));
        var json = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{targets}]}}";

        Assert.False(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void ActionParser_AcceptsTwoHundredSessionsAndOneRepositoryRestore()
    {
        var targets = string.Join(',', Enumerable.Range(1, 200).Select(index =>
            $"{{\"target_id\":\"01900000-0000-7000-8000-{index:000000000000}\",\"expected_revision\":0}}"));
        var sessions = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{targets}]}}";
        Assert.True(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(sessions), out var parsed));
        Assert.Equal(200, parsed!.Targets.Count);

        var repository = $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"restore\",\"target_kind\":\"repository\",\"targets\":[{{\"target_id\":\"{RepositoryId}\",\"expected_revision\":1}}]}}";
        Assert.True(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(repository), out parsed));
        Assert.Equal(LocalArchiveAction.Restore, parsed!.Action);
        Assert.Equal(LocalArchiveTargetKind.Repository, parsed.TargetKind);
    }

    [Fact]
    public void ActionParser_RejectsInvalidUtf8AndDepthAboveEight()
    {
        var invalidUtf8 = Encoding.UTF8.GetBytes(ValidAction());
        invalidUtf8[invalidUtf8.Length - 2] = 0xff;
        Assert.False(LocalArchiveWire.TryParseActionBody(invalidUtf8, out _));

        var nested = new string('[', 9) + "0" + new string(']', 9);
        var tooDeep = ValidAction().Replace("\"local-archive-action.v1\"", nested, StringComparison.Ordinal);
        Assert.False(LocalArchiveWire.TryParseActionBody(Encoding.UTF8.GetBytes(tooDeep), out _));
    }

    [Fact]
    public void JsonWriter_EmitsExactDirectActionAndListBytes()
    {
        var archived = new LocalArchiveMutationTargetSuccess(
            SessionId, LocalArchiveState.Archived, 1, Timestamp, Timestamp);
        var direct = Encoding.UTF8.GetString(LocalArchiveWire.WriteDirect(LocalArchiveTargetKind.Session, archived).Span);
        Assert.Equal($"{{\"schema_version\":\"local-archive.response.v1\",\"target_kind\":\"session\",\"target_id\":\"{SessionId}\",\"state\":\"archived\",\"revision\":1,\"archived_at\":\"{Timestamp}\",\"updated_at\":\"{Timestamp}\"}}", direct);

        var action = Encoding.UTF8.GetString(LocalArchiveWire.WriteAction(new(
            LocalArchiveAction.Archive, LocalArchiveTargetKind.Session, [archived])).Span);
        Assert.Equal($"{{\"schema_version\":\"local-archive-action.response.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{SessionId}\",\"state\":\"archived\",\"revision\":1,\"archived_at\":\"{Timestamp}\",\"updated_at\":\"{Timestamp}\"}}]}}", action);

        var list = Encoding.UTF8.GetString(LocalArchiveWire.WriteList(
            LocalArchiveTargetKind.Session, [archived], null).Span);
        Assert.Equal($"{{\"schema_version\":\"local-archived-items.response.v1\",\"target_kind\":\"session\",\"items\":[{{\"target_id\":\"{SessionId}\",\"state\":\"archived\",\"revision\":1,\"archived_at\":\"{Timestamp}\",\"updated_at\":\"{Timestamp}\"}}],\"next_cursor\":null}}", list);
    }

    [Fact]
    public void JsonWriter_EmitsRevisionZeroNullsAndEveryClosedError()
    {
        var active = new LocalArchiveMutationTargetSuccess(
            SessionId, LocalArchiveState.Active, 0, null, null);
        Assert.EndsWith(
            "\"state\":\"active\",\"revision\":0,\"archived_at\":null,\"updated_at\":null}",
            Encoding.UTF8.GetString(LocalArchiveWire.WriteDirect(LocalArchiveTargetKind.Session, active).Span),
            StringComparison.Ordinal);

        foreach (var error in Enum.GetValues<LocalArchiveWireError>())
        {
            var code = error switch
            {
                LocalArchiveWireError.InvalidHost => "invalid_host",
                LocalArchiveWireError.InvalidRequest => "invalid_request",
                LocalArchiveWireError.InvalidCursor => "invalid_cursor",
                LocalArchiveWireError.CsrfRejected => "csrf_rejected",
                LocalArchiveWireError.TargetNotFound => "target_not_found",
                LocalArchiveWireError.MethodNotAllowed => "method_not_allowed",
                LocalArchiveWireError.RevisionConflict => "revision_conflict",
                LocalArchiveWireError.RequestTooLarge => "request_too_large",
                LocalArchiveWireError.UnsupportedMediaType => "unsupported_media_type",
                LocalArchiveWireError.ArchiveStoreUnavailable => "archive_store_unavailable",
                LocalArchiveWireError.PersistenceBusy => "persistence_busy",
                _ => throw new ArgumentOutOfRangeException(nameof(error)),
            };
            Assert.Equal($"{{\"error\":\"{code}\"}}", Encoding.UTF8.GetString(LocalArchiveWire.ErrorBytes(error).Span));
        }
    }

    [Fact]
    public void ListWriter_EmitsCanonicalLastItemCursorAndRejectsWrongOrder()
    {
        var olderId = "01890f65-4c31-7f42-8a7d-000000000000";
        var olderTimestamp = "2026-08-08T12:34:56.1234567+00:00";
        var newer = new LocalArchiveMutationTargetSuccess(
            SessionId, LocalArchiveState.Archived, 1, Timestamp, Timestamp);
        var older = new LocalArchiveMutationTargetSuccess(
            olderId, LocalArchiveState.Archived, 1, olderTimestamp, olderTimestamp);
        var cursor = LocalArchiveCursorCodec.Encode(
            LocalArchiveTargetKind.Session, new(olderTimestamp, olderId));

        var bytes = Encoding.UTF8.GetString(LocalArchiveWire.WriteList(
            LocalArchiveTargetKind.Session, [newer, older], cursor).Span);
        Assert.EndsWith($"\"next_cursor\":\"{cursor}\"}}", bytes, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => LocalArchiveWire.WriteList(
            LocalArchiveTargetKind.Session, [older, newer], null));
        Assert.Throws<InvalidOperationException>(() => LocalArchiveWire.WriteList(
            LocalArchiveTargetKind.Session, [newer, older], GoldenSessionCursor));
    }

    [Fact]
    public void ActionWriter_EmitsRepositoryRestoreMapping()
    {
        var restored = new LocalArchiveMutationTargetSuccess(
            RepositoryId,
            LocalArchiveState.Active,
            2,
            null,
            Timestamp);

        var bytes = Encoding.UTF8.GetString(LocalArchiveWire.WriteAction(new(
            LocalArchiveAction.Restore,
            LocalArchiveTargetKind.Repository,
            [restored])).Span);

        Assert.Equal(
            $"{{\"schema_version\":\"local-archive-action.response.v1\",\"action\":\"restore\",\"target_kind\":\"repository\",\"targets\":[{{\"target_id\":\"{RepositoryId}\",\"state\":\"active\",\"revision\":2,\"archived_at\":null,\"updated_at\":\"{Timestamp}\"}}]}}",
            bytes);
    }

    private static string ValidAction() =>
        $"{{\"schema_version\":\"local-archive-action.v1\",\"action\":\"archive\",\"target_kind\":\"session\",\"targets\":[{{\"target_id\":\"{SessionId}\",\"expected_revision\":0}}]}}";

    private static string RawCursor(string kind, string archivedAt, string targetId) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"local-archive-cursor\0v1\0{kind}\0{archivedAt}\0{targetId}"))
            .Replace('+', '-')
            .Replace('/', '_');
}

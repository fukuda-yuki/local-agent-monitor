using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1SessionCursorTests
{
    private const string SessionId = "018f2b4e-7c1a-7f1a-9a2b-6c3d4e5f6072";
    private const string GoldenFrameHex = "6c6f63616c2d6d6f6e69746f722d73657373696f6e2d66696c746572007631000000000a7265706f7369746f7279010000002430313866326234652d376331612d376631612d386132622d36633364346535663630373100000010696e636c7564655f61726368697665640100000021323032362d30382d30315430303a30303a30302e303030303030302b30303a30300000020000000b636c617564652d636f6465000000067673636f64650002000000074d6f64656c2d41000000077a2d6d6f64656c00020000000661637469766500000007756e6b6e6f776e020100020100000003666f6f004b";
    private const string GoldenBindingHex = "9bef2527ee6d408320bc76744924e23fd2d702c6414396e28d53a59306066ad8";
    private const string GoldenRawHex = "019bef2527ee6d408320bc76744924e23fd2d702c6414396e28d53a59306066ad8000000019fe664e67b30313866326234652d376331612d376631612d396132622d36633364346535663630373265bfd4112332ebed8d5aff24ccd72edeac2c819c6f5b221d3c37b99eb9ea41ac";
    private const string GoldenToken = "AZvvJSfubUCDILx2dEkk4j_S1wLGQUOW4o1TpZMGBmrYAAAAAZ_mZOZ7MDE4ZjJiNGUtN2MxYS03ZjFhLTlhMmItNmMzZDRlNWY2MDcyZb_UESMy6-2NWv8kzNcu3qwsgZxvWyIdPDe5nrnqQaw";

    [Fact]
    public void Codec_MatchesExactFilterFrameBindingLayoutAndTokenGoldenVector()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var request = ParseRequest(FullBody);
        var position = new LocalMonitorV1SessionCursorPosition(
            LocalMonitorV1SessionSortGroup.ValidTime,
            1_786_276_800_123,
            SessionId);

        var frame = LocalMonitorV1SessionCursorCodec.BuildSemanticFilterFrame(request);
        var binding = LocalMonitorV1SessionCursorCodec.ComputeFilterBinding(key, request);
        var token = LocalMonitorV1SessionCursorCodec.Encode(key, request, position);

        Assert.Equal(234, frame.Length);
        Assert.Equal(GoldenFrameHex, Convert.ToHexStringLower(frame));
        Assert.Equal(GoldenBindingHex, Convert.ToHexStringLower(binding));
        Assert.Equal(147, token.Length);
        Assert.Equal(GoldenToken, token);
        var raw = DecodeBase64Url(token);
        Assert.Equal(110, raw.Length);
        Assert.Equal(GoldenRawHex, Convert.ToHexStringLower(raw));
        Assert.Equal(1, raw[0]);
        Assert.Equal(0, raw[33]);
        Assert.Equal(1_786_276_800_123, BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(34, 8)));
        Assert.Equal(SessionId, Encoding.ASCII.GetString(raw, 42, 36));

        Assert.True(LocalMonitorV1SessionCursorCodec.TryDecode(token, key, request, out var decoded));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void Codec_BindsNormalizedQueryAndOrdinalSetsWithoutCarryingSensitiveValues()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var original = ParseRequest(FullBody);
        var equivalent = ParseRequest(FullBody
            .Replace("\"q\":\"ＦＯＯ\"", "\"q\":\"foo\"", StringComparison.Ordinal)
            .Replace("[\"vscode\",\"claude-code\"]", "[\"claude-code\",\"vscode\"]", StringComparison.Ordinal));
        var changedQuery = ParseRequest(FullBody.Replace("\"q\":\"ＦＯＯ\"", "\"q\":\"bar\"", StringComparison.Ordinal));
        var changedModel = ParseRequest(FullBody.Replace("\"z-model\"", "\"other-model\"", StringComparison.Ordinal));
        var token = LocalMonitorV1SessionCursorCodec.Encode(
            key,
            original,
            new(LocalMonitorV1SessionSortGroup.ValidTime, 1_786_276_800_123, SessionId));
        var raw = DecodeBase64Url(token);

        Assert.True(LocalMonitorV1SessionCursorCodec.TryDecode(token, key, equivalent, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(token, key, changedQuery, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(token, key, changedModel, out _));
        Assert.False(Contains(raw, Encoding.UTF8.GetBytes("foo")));
        Assert.False(Contains(raw, Encoding.UTF8.GetBytes("Model-A")));
        Assert.False(Contains(raw, Encoding.UTF8.GetBytes("z-model")));
    }

    [Fact]
    public void FilterFrame_UsesExactNullEmptyAndDefaultLimitMarkers()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var request = ParseRequest("{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"all\",\"repository_id\":null,\"archive_scope\":\"active_only\",\"from\":null,\"to\":null,\"source\":[],\"model\":[],\"status\":[],\"has_skill\":null,\"has_subagent\":null,\"has_error\":null,\"has_retry\":null,\"q\":null,\"cursor\":null,\"limit\":null}");

        Assert.Equal(
            "6c6f63616c2d6d6f6e69746f722d73657373696f6e2d66696c7465720076310000000003616c6c000000000b6163746976655f6f6e6c79000000000000000000000000000000",
            Convert.ToHexStringLower(LocalMonitorV1SessionCursorCodec.BuildSemanticFilterFrame(request)));
        Assert.Equal(
            "61227f9649b21086c6b460c32adbb59358c568dda07036bc7204a57ce4ddab44",
            Convert.ToHexStringLower(LocalMonitorV1SessionCursorCodec.ComputeFilterBinding(key, request)));
    }

    [Fact]
    public void Decoder_RejectsTamperRestartFilterMismatchAndNoncanonicalEncodings()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var restartedKey = Enumerable.Range(1, 32).Select(index => (byte)index).ToArray();
        var request = ParseRequest(FullBody);
        var changedLimit = ParseRequest(FullBody.Replace("\"limit\":75", "\"limit\":76", StringComparison.Ordinal));
        var tampered = GoldenToken[..50] + (GoldenToken[50] == 'A' ? "B" : "A") + GoldenToken[51..];

        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(tampered, key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken, restartedKey, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken, key, changedLimit, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken + "=", key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken + " ", key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken.Replace('-', '+'), key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken.Replace('_', '/'), key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken[..10] + "%2F" + GoldenToken[13..], key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(WithNoncanonicalPadBits(GoldenToken), key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken[..^1], key, request, out _));
        Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(GoldenToken, new byte[31], request, out _));
    }

    [Fact]
    public void Decoder_RejectsAuthenticatedStructuralViolationsAndFilterBindingMismatch()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var request = ParseRequest(FullBody);

        AssertRejectedAfterMutation(0, 2);
        AssertRejectedAfterMutation(33, 2);
        AssertRejectedAfterMutation(34, 1, group: 1);
        AssertRejectedAfterMutation(42, (byte)'A');
        AssertRejectedAfterMutation(1, (byte)(DecodeBase64Url(GoldenToken)[1] ^ 1));

        void AssertRejectedAfterMutation(int offset, byte value, byte? group = null)
        {
            var raw = DecodeBase64Url(GoldenToken);
            raw[offset] = value;
            if (group is not null) raw[33] = group.Value;
            var token = Resign(raw, key);
            Assert.False(LocalMonitorV1SessionCursorCodec.TryDecode(token, key, request, out _));
        }
    }

    [Fact]
    public void Codec_RequiresExactProcessKeyAndCanonicalPosition()
    {
        var request = ParseRequest(FullBody);
        var valid = new LocalMonitorV1SessionCursorPosition(
            LocalMonitorV1SessionSortGroup.ValidTime,
            1_786_276_800_123,
            SessionId);
        var invalidTimeBytes = new LocalMonitorV1SessionCursorPosition(
            LocalMonitorV1SessionSortGroup.InvalidTime,
            1,
            SessionId);
        var invalidId = new LocalMonitorV1SessionCursorPosition(
            LocalMonitorV1SessionSortGroup.ValidTime,
            1,
            SessionId.ToUpperInvariant());

        Assert.Throws<ArgumentException>(() => LocalMonitorV1SessionCursorCodec.Encode(new byte[31], request, valid));
        Assert.Throws<InvalidOperationException>(() => LocalMonitorV1SessionCursorCodec.Encode(new byte[32], request, invalidTimeBytes));
        Assert.Throws<InvalidOperationException>(() => LocalMonitorV1SessionCursorCodec.Encode(new byte[32], request, invalidId));
    }

    [Fact]
    public void InvalidTimeCursor_EncodesZeroTimeBytesAndRoundTrips()
    {
        var key = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var request = ParseRequest(FullBody);
        var position = new LocalMonitorV1SessionCursorPosition(
            LocalMonitorV1SessionSortGroup.InvalidTime,
            0,
            SessionId);

        var token = LocalMonitorV1SessionCursorCodec.Encode(key, request, position);
        var raw = DecodeBase64Url(token);

        Assert.Equal(1, raw[33]);
        Assert.Equal(new byte[8], raw[34..42]);
        Assert.True(LocalMonitorV1SessionCursorCodec.TryDecode(token, key, request, out var decoded));
        Assert.Equal(position, decoded);
    }

    [Fact]
    public void Keyset_ValidCursorResumesSmallerTimeThenIdAndAllInvalidRows()
    {
        var cursor = new LocalMonitorV1SessionCursorPosition(LocalMonitorV1SessionSortGroup.ValidTime, 100, SessionId);

        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.ValidTime, 99, "018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6073"), true);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.ValidTime, 100, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071"), true);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.ValidTime, 100, SessionId), false);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.ValidTime, 100, "018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6073"), false);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.ValidTime, 101, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071"), false);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.InvalidTime, 0, "018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6073"), true);
    }

    [Fact]
    public void Keyset_InvalidCursorResumesOnlySmallerInvalidIdsAndRejectsInvalidKeys()
    {
        var cursor = new LocalMonitorV1SessionCursorPosition(LocalMonitorV1SessionSortGroup.InvalidTime, 0, SessionId);

        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.ValidTime, 1, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071"), false);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.InvalidTime, 0, "018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071"), true);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.InvalidTime, 0, SessionId), false);
        AssertResume(cursor, new(LocalMonitorV1SessionSortGroup.InvalidTime, 0, "018f2b4e-7c1a-7f1a-ba2b-6c3d4e5f6073"), false);
        Assert.False(LocalMonitorV1SessionCursorKeyset.TryShouldResume(
            cursor,
            new(LocalMonitorV1SessionSortGroup.InvalidTime, 1, SessionId),
            out _));
        Assert.False(LocalMonitorV1SessionCursorKeyset.TryShouldResume(
            new((LocalMonitorV1SessionSortGroup)2, 0, SessionId),
            cursor,
            out _));
    }

    private static void AssertResume(
        LocalMonitorV1SessionCursorPosition cursor,
        LocalMonitorV1SessionCursorPosition row,
        bool expected)
    {
        Assert.True(LocalMonitorV1SessionCursorKeyset.TryShouldResume(cursor, row, out var actual));
        Assert.Equal(expected, actual);
    }

    private static LocalMonitorV1SessionSearchRequest ParseRequest(string body)
    {
        Assert.Equal(
            "Success",
            LocalMonitorV1SessionSearchRequestParser.Parse(Encoding.UTF8.GetBytes(body), out var request).ToString());
        return request!;
    }

    private static byte[] DecodeBase64Url(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));

    private static string Resign(byte[] raw, byte[] key)
    {
        var prefix = "local-monitor-session-cursor\0v1\0"u8;
        var authenticated = new byte[prefix.Length + 78];
        prefix.CopyTo(authenticated);
        raw.AsSpan(0, 78).CopyTo(authenticated.AsSpan(prefix.Length));
        HMACSHA256.HashData(key, authenticated).CopyTo(raw, 78);
        return Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string WithNoncanonicalPadBits(string token)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var index = alphabet.IndexOf(token[^1], StringComparison.Ordinal);
        Assert.Equal(0, index & 3);
        return token[..^1] + alphabet[index + 1];
    }

    private static bool Contains(ReadOnlySpan<byte> value, ReadOnlySpan<byte> sought)
    {
        for (var index = 0; index <= value.Length - sought.Length; index++)
        {
            if (value.Slice(index, sought.Length).SequenceEqual(sought)) return true;
        }
        return false;
    }

    private const string FullBody = "{\"schema_version\":\"local-monitor-session-search.request.v1\",\"scope\":\"repository\",\"repository_id\":\"018f2b4e-7c1a-7f1a-8a2b-6c3d4e5f6071\",\"archive_scope\":\"include_archived\",\"from\":\"2026-08-01T00:00:00.0000000+00:00\",\"to\":null,\"source\":[\"vscode\",\"claude-code\"],\"model\":[\"z-model\",\"Model-A\"],\"status\":[\"unknown\",\"active\"],\"has_skill\":true,\"has_subagent\":false,\"has_error\":null,\"has_retry\":true,\"q\":\"ＦＯＯ\",\"cursor\":null,\"limit\":75}";
}

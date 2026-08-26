using System.Buffers.Binary;
using System.Text;
using CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class LocalMonitorV1RepositoryCursorTests
{
    private const string RepositoryId = "018f0000-0000-7000-8000-000000000101";
    private const string GoldenToken = "AUF5XltXBkN5WI0-UHKGXqqaTTn02EoPxd6KDMHmVA_EMDE4ZjAwMDAtMDAwMC03MDAwLTgwMDAtMDAwMDAwMDAwMTAxFBbBpM3Al3rZwrDA2qFniGeiastYfbtmU9WoptoQ61I";

    [Fact]
    public void CodecMatchesExactGoldenAndRoundTrips()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var request = new LocalMonitorV1RepositoryRequest("include_archived", null, 1);

        var token = LocalMonitorV1RepositoryCursorCodec.Encode(key, request, RepositoryId);

        Assert.Equal(135, token.Length);
        Assert.Equal(GoldenToken, token);
        var raw = Decode(token);
        Assert.Equal(101, raw.Length);
        Assert.Equal(1, raw[0]);
        Assert.Equal(RepositoryId, Encoding.ASCII.GetString(raw, 33, 36));
        Assert.True(LocalMonitorV1RepositoryCursorCodec.TryDecode(token, key, request, out var decoded));
        Assert.Equal(RepositoryId, decoded);
    }

    [Fact]
    public void DecoderRejectsTamperRestartFilterMismatchAndMalformedValues()
    {
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var request = new LocalMonitorV1RepositoryRequest("include_archived", null, 1);
        var token = LocalMonitorV1RepositoryCursorCodec.Encode(key, request, RepositoryId);
        var tampered = token[..50] + (token[50] == 'A' ? "B" : "A") + token[51..];

        Assert.False(LocalMonitorV1RepositoryCursorCodec.TryDecode(tampered, key, request, out _));
        Assert.False(LocalMonitorV1RepositoryCursorCodec.TryDecode(token, Enumerable.Repeat((byte)7, 32).ToArray(), request, out _));
        Assert.False(LocalMonitorV1RepositoryCursorCodec.TryDecode(token, key, request with { Limit = 2 }, out _));
        Assert.False(LocalMonitorV1RepositoryCursorCodec.TryDecode(token, key, request with { ArchiveScope = "active_only" }, out _));
        Assert.False(LocalMonitorV1RepositoryCursorCodec.TryDecode(token + "=", key, request, out _));
        Assert.False(LocalMonitorV1RepositoryCursorCodec.TryDecode(RepositoryId, key, request, out _));
    }

    private static byte[] Decode(string value) =>
        Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
}

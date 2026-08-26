using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal static class LocalMonitorV1RepositoryCursorCodec
{
    private const int CursorBytes = 101;
    private const int CursorTextCharacters = 135;
    private static ReadOnlySpan<byte> FilterPrefix => "local-monitor-repository-filter\0v1\0archive_scope\0"u8;
    private static ReadOnlySpan<byte> CursorPrefix => "local-monitor-repository-cursor\0v1\0"u8;

    internal static string Encode(ReadOnlySpan<byte> processKey, LocalMonitorV1RepositoryRequest request, string repositoryId)
    {
        ValidateKey(processKey);
        ArgumentNullException.ThrowIfNull(request);
        if (!LocalMonitorV1Identity.TryParseUuidV7(repositoryId, out _))
            throw new InvalidOperationException("local_monitor_v1_repository_cursor_position_invalid");

        var raw = new byte[CursorBytes];
        raw[0] = 1;
        ComputeFilterBinding(processKey, request).CopyTo(raw, 1);
        Encoding.ASCII.GetBytes(repositoryId).CopyTo(raw, 33);
        ComputeTag(processKey, raw.AsSpan(0, 69)).CopyTo(raw, 69);
        return LocalMonitorV1Base64Url.Encode(raw);
    }

    internal static bool IsCursorSyntax(string? token) => token is not null
        && token.Length == CursorTextCharacters
        && token.All(character => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    internal static bool TryDecode(string? token, ReadOnlySpan<byte> processKey, LocalMonitorV1RepositoryRequest request, out string? repositoryId)
    {
        repositoryId = null;
        if (processKey.Length != 32 || request is null || token is null
            || !LocalMonitorV1Base64Url.TryDecodeCanonical(token, CursorBytes, CursorTextCharacters, out var raw)
            || raw[0] != 1)
            return false;

        var position = Encoding.ASCII.GetString(raw, 33, 36);
        if (!LocalMonitorV1Identity.TryParseUuidV7(position, out _)) return false;
        var tagMatches = CryptographicOperations.FixedTimeEquals(ComputeTag(processKey, raw.AsSpan(0, 69)), raw.AsSpan(69, 32));
        var bindingMatches = CryptographicOperations.FixedTimeEquals(ComputeFilterBinding(processKey, request), raw.AsSpan(1, 32));
        if (!(tagMatches & bindingMatches)) return false;
        repositoryId = position;
        return true;
    }

    internal static byte[] BuildSemanticFilterFrame(LocalMonitorV1RepositoryRequest request)
    {
        using var stream = new MemoryStream();
        stream.Write(FilterPrefix);
        stream.Write(Encoding.ASCII.GetBytes(request.ArchiveScope));
        stream.WriteByte(0);
        stream.Write("limit\0"u8);
        Span<byte> limit = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(limit, checked((ushort)request.Limit));
        stream.Write(limit);
        return stream.ToArray();
    }

    private static byte[] ComputeFilterBinding(ReadOnlySpan<byte> processKey, LocalMonitorV1RepositoryRequest request) =>
        HMACSHA256.HashData(processKey, BuildSemanticFilterFrame(request));

    private static byte[] ComputeTag(ReadOnlySpan<byte> processKey, ReadOnlySpan<byte> header)
    {
        var authenticated = new byte[CursorPrefix.Length + header.Length];
        CursorPrefix.CopyTo(authenticated);
        header.CopyTo(authenticated.AsSpan(CursorPrefix.Length));
        return HMACSHA256.HashData(processKey, authenticated);
    }

    private static void ValidateKey(ReadOnlySpan<byte> processKey)
    {
        if (processKey.Length != 32) throw new ArgumentException("repository_cursor_key must be exactly 32 bytes.", nameof(processKey));
    }
}

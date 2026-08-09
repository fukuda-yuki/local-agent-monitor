using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal enum LocalMonitorV1SessionSortGroup : byte
{
    ValidTime = 0,
    InvalidTime = 1,
}

internal sealed record LocalMonitorV1SessionCursorPosition(
    LocalMonitorV1SessionSortGroup SortGroup,
    long SortInstantEpochMilliseconds,
    string SessionId);

internal static class LocalMonitorV1SessionCursorKeyset
{
    internal static bool TryShouldResume(
        LocalMonitorV1SessionCursorPosition cursor,
        LocalMonitorV1SessionCursorPosition row,
        out bool shouldResume)
    {
        shouldResume = false;
        if (!IsValid(cursor) || !IsValid(row)) return false;

        shouldResume = cursor.SortGroup switch
        {
            LocalMonitorV1SessionSortGroup.ValidTime =>
                row.SortGroup == LocalMonitorV1SessionSortGroup.InvalidTime
                || row.SortInstantEpochMilliseconds < cursor.SortInstantEpochMilliseconds
                || row.SortInstantEpochMilliseconds == cursor.SortInstantEpochMilliseconds
                    && string.CompareOrdinal(row.SessionId, cursor.SessionId) < 0,
            LocalMonitorV1SessionSortGroup.InvalidTime =>
                row.SortGroup == LocalMonitorV1SessionSortGroup.InvalidTime
                && string.CompareOrdinal(row.SessionId, cursor.SessionId) < 0,
            _ => false,
        };
        return true;
    }

    internal static bool IsValid(LocalMonitorV1SessionCursorPosition position) =>
        position is not null
        && LocalMonitorV1Identity.TryParseUuidV7(position.SessionId, out _)
        && position.SortGroup switch
        {
            LocalMonitorV1SessionSortGroup.ValidTime => true,
            LocalMonitorV1SessionSortGroup.InvalidTime => position.SortInstantEpochMilliseconds == 0,
            _ => false,
        };
}

internal static class LocalMonitorV1SessionCursorCodec
{
    private const int CursorBytes = 110;
    private const int CursorTextCharacters = 147;
    private static ReadOnlySpan<byte> FilterPrefix => "local-monitor-session-filter\0v1\0"u8;
    private static ReadOnlySpan<byte> CursorPrefix => "local-monitor-session-cursor\0v1\0"u8;

    internal static byte[] BuildSemanticFilterFrame(LocalMonitorV1SessionSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var stream = new MemoryStream();
        stream.Write(FilterPrefix);
        WriteRequiredString(stream, request.Scope);
        WriteNullableString(stream, request.RepositoryId);
        WriteRequiredString(stream, request.ArchiveScope);
        WriteNullableString(stream, Timestamp(request.From));
        WriteNullableString(stream, Timestamp(request.To));
        WriteArray(stream, request.Sources);
        WriteArray(stream, request.Models);
        WriteArray(stream, request.Statuses);
        WriteBoolean(stream, request.HasSkill);
        WriteBoolean(stream, request.HasSubagent);
        WriteBoolean(stream, request.HasError);
        WriteBoolean(stream, request.HasRetry);
        WriteNullableString(stream, request.QueryNormalized);
        WriteUInt16(stream, request.Limit is null ? (ushort)0 : checked((ushort)request.Limit.Value));
        return stream.ToArray();
    }

    internal static byte[] ComputeFilterBinding(
        ReadOnlySpan<byte> processKey,
        LocalMonitorV1SessionSearchRequest request)
    {
        ValidateKey(processKey);
        return HMACSHA256.HashData(processKey, BuildSemanticFilterFrame(request));
    }

    internal static string Encode(
        ReadOnlySpan<byte> processKey,
        LocalMonitorV1SessionSearchRequest request,
        LocalMonitorV1SessionCursorPosition position)
    {
        ValidateKey(processKey);
        ArgumentNullException.ThrowIfNull(request);
        if (!LocalMonitorV1SessionCursorKeyset.IsValid(position))
            throw new InvalidOperationException("local_monitor_v1_cursor_position_invalid");

        var raw = new byte[CursorBytes];
        raw[0] = 1;
        ComputeFilterBinding(processKey, request).CopyTo(raw, 1);
        raw[33] = (byte)position.SortGroup;
        BinaryPrimitives.WriteInt64BigEndian(raw.AsSpan(34, 8), position.SortInstantEpochMilliseconds);
        Encoding.ASCII.GetBytes(position.SessionId).CopyTo(raw, 42);
        ComputeCursorTag(processKey, raw.AsSpan(0, 78)).CopyTo(raw, 78);
        return LocalMonitorV1Base64Url.Encode(raw);
    }

    internal static bool IsStructurallyCanonical(string? token)
    {
        if (token is null
            || !LocalMonitorV1Base64Url.TryDecodeCanonical(
                token,
                CursorBytes,
                CursorTextCharacters,
                out var raw)
            || raw[0] != 1
            || raw[33] is not (0 or 1))
        {
            return false;
        }

        var epochMilliseconds = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(34, 8));
        return (raw[33] == 0 || epochMilliseconds == 0)
            && LocalMonitorV1Identity.TryParseUuidV7(Encoding.ASCII.GetString(raw, 42, 36), out _);
    }

    internal static bool TryDecode(
        string? token,
        ReadOnlySpan<byte> processKey,
        LocalMonitorV1SessionSearchRequest request,
        out LocalMonitorV1SessionCursorPosition? position)
    {
        position = null;
        if (token is null
            || processKey.Length != 32
            || request is null
            || !LocalMonitorV1Base64Url.TryDecodeCanonical(
                token,
                CursorBytes,
                CursorTextCharacters,
                out var raw)
            || raw[0] != 1
            || raw[33] is not (0 or 1))
        {
            return false;
        }

        var group = (LocalMonitorV1SessionSortGroup)raw[33];
        var epochMilliseconds = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(34, 8));
        if (group == LocalMonitorV1SessionSortGroup.InvalidTime && epochMilliseconds != 0) return false;

        var sessionId = Encoding.ASCII.GetString(raw, 42, 36);
        if (!LocalMonitorV1Identity.TryParseUuidV7(sessionId, out _)) return false;

        var expectedTag = ComputeCursorTag(processKey, raw.AsSpan(0, 78));
        var expectedBinding = ComputeFilterBinding(processKey, request);
        var tagMatches = CryptographicOperations.FixedTimeEquals(expectedTag, raw.AsSpan(78, 32));
        var bindingMatches = CryptographicOperations.FixedTimeEquals(expectedBinding, raw.AsSpan(1, 32));
        if (!(tagMatches & bindingMatches))
        {
            return false;
        }

        position = new(group, epochMilliseconds, sessionId);
        return true;
    }

    private static byte[] ComputeCursorTag(ReadOnlySpan<byte> processKey, ReadOnlySpan<byte> cursorHeader)
    {
        var authenticated = new byte[CursorPrefix.Length + cursorHeader.Length];
        CursorPrefix.CopyTo(authenticated);
        cursorHeader.CopyTo(authenticated.AsSpan(CursorPrefix.Length));
        return HMACSHA256.HashData(processKey, authenticated);
    }

    private static void WriteNullableString(Stream stream, string? value)
    {
        if (value is null)
        {
            stream.WriteByte(0);
            return;
        }

        stream.WriteByte(1);
        WriteRequiredString(stream, value);
    }

    private static void WriteRequiredString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        stream.Write(length);
        stream.Write(bytes);
    }

    private static void WriteArray(Stream stream, IReadOnlyList<string> values)
    {
        WriteUInt16(stream, checked((ushort)values.Count));
        foreach (var value in values.Order(StringComparer.Ordinal)) WriteRequiredString(stream, value);
    }

    private static void WriteBoolean(Stream stream, bool? value) =>
        stream.WriteByte(value switch { null => 0, false => 1, true => 2 });

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }

    private static string? Timestamp(DateTimeOffset? value) =>
        value?.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

    private static void ValidateKey(ReadOnlySpan<byte> processKey)
    {
        if (processKey.Length != 32)
            throw new ArgumentException("session_cursor_key must be exactly 32 bytes.", nameof(processKey));
    }
}

internal static class LocalMonitorV1Base64Url
{
    internal static bool TryDecodeCanonical(
        string value,
        int byteLength,
        int textLength,
        out byte[] bytes)
    {
        bytes = [];
        if (value.Length != textLength
            || value.Any(character => character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
            return bytes.Length == byteLength
                && string.Equals(Encode(bytes), value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            bytes = [];
            return false;
        }
    }

    internal static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

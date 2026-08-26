using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.LocalMonitorV1;

internal sealed record LocalMonitorV1TimelineFilter(
    string SessionId,
    string WorkspaceRevision,
    string? ExecutionId,
    string? ParentNodeId,
    int Limit);

internal sealed record LocalMonitorV1TimelinePosition(
    byte TimeGroup,
    long UtcTicks,
    ulong SourceOrdinal,
    string NodeId);

internal static class LocalMonitorV1TimelineCursor
{
    private const int FrameLength = 119;
    private const string CursorDomain = "local-monitor-timeline-cursor\0v1\0";
    private const string FilterDomain = "local-monitor-timeline-filter\0v1\0";

    internal static string Encode(
        ReadOnlySpan<byte> key,
        LocalMonitorV1TimelineFilter filter,
        LocalMonitorV1TimelinePosition position)
    {
        Validate(position);
        Span<byte> frame = stackalloc byte[FrameLength];
        frame[0] = 1;
        FilterMac(key, filter).CopyTo(frame[1..33]);
        frame[33] = position.TimeGroup;
        BinaryPrimitives.WriteInt64BigEndian(frame[34..42], position.UtcTicks);
        BinaryPrimitives.WriteUInt64BigEndian(frame[42..50], position.SourceOrdinal);
        Encoding.ASCII.GetBytes(position.NodeId, frame[50..87]);
        CursorMac(key, frame[..87]).CopyTo(frame[87..]);
        return Convert.ToBase64String(frame).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal static bool TryDecode(
        string value,
        ReadOnlySpan<byte> key,
        LocalMonitorV1TimelineFilter filter,
        out LocalMonitorV1TimelinePosition position)
    {
        position = default!;
        if (value.Length != 159 || !"AEIMQUYcgkosw048".Contains(value[^1], StringComparison.Ordinal))
            return false;
        Span<byte> frame = stackalloc byte[FrameLength];
        if (!Convert.TryFromBase64String(value.Replace('-', '+').Replace('_', '/') + "=", frame, out var written)
            || written != FrameLength
            || frame[0] != 1
            || !CryptographicOperations.FixedTimeEquals(frame[1..33], FilterMac(key, filter))
            || !CryptographicOperations.FixedTimeEquals(frame[87..], CursorMac(key, frame[..87])))
            return false;
        var nodeId = Encoding.ASCII.GetString(frame[50..87]);
        var candidate = new LocalMonitorV1TimelinePosition(
            frame[33],
            BinaryPrimitives.ReadInt64BigEndian(frame[34..42]),
            BinaryPrimitives.ReadUInt64BigEndian(frame[42..50]),
            nodeId);
        try { Validate(candidate); }
        catch (ArgumentException) { return false; }
        position = candidate;
        return true;
    }

    private static byte[] FilterMac(ReadOnlySpan<byte> key, LocalMonitorV1TimelineFilter filter)
    {
        using var stream = new MemoryStream();
        stream.Write(Encoding.ASCII.GetBytes(FilterDomain));
        foreach (var field in new[] { filter.SessionId, filter.WorkspaceRevision, filter.ExecutionId ?? "", filter.ParentNodeId ?? "", filter.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) })
        {
            stream.Write(Encoding.ASCII.GetBytes(field));
            stream.WriteByte(0);
        }
        return HMACSHA256.HashData(key, stream.ToArray());
    }

    private static byte[] CursorMac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> prefix)
    {
        var domain = Encoding.ASCII.GetBytes(CursorDomain);
        var bytes = new byte[domain.Length + prefix.Length];
        domain.CopyTo(bytes, 0);
        prefix.CopyTo(bytes.AsSpan(domain.Length));
        return HMACSHA256.HashData(key, bytes);
    }

    private static void Validate(LocalMonitorV1TimelinePosition position)
    {
        if (position.TimeGroup > 2
            || (position.TimeGroup != 0 && position.UtcTicks != 0)
            || position.NodeId.Length != 37
            || !position.NodeId.StartsWith("node-", StringComparison.Ordinal)
            || position.NodeId[5..].Any(static c => !Uri.IsHexDigit(c) || char.IsUpper(c)))
            throw new ArgumentException("invalid_timeline_position", nameof(position));
    }
}

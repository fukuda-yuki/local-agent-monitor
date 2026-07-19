using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

internal enum RetentionFileCaptureMemberKind { File, Directory, OwnerMarker }

internal sealed record RetentionFileCaptureMember(
    int Ordinal,
    string RelativePath,
    RetentionFileCaptureMemberKind Kind,
    long? ByteLength,
    byte[]? Sha256,
    int DeletionOrder)
{
    internal bool IsValid => Ordinal is >= 0 and < 256
        && RetentionFileCaptureContracts.IsCanonicalRelativePath(RelativePath)
        && DeletionOrder >= 0
        && (Kind == RetentionFileCaptureMemberKind.File
            ? ByteLength is >= 0 and <= 134217728 && Sha256 is { Length: 32 }
            : ByteLength is null && Sha256 is null);
}

internal static class RetentionFileCaptureContracts
{
    internal static int MaximumMemberCount => 256;
    internal static long MaximumMemberBytes => 128 * 1024 * 1024;
    internal const string OwnerMarkerName = ".retention-owner.v1";

    internal static bool IsCanonicalRelativePath(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Contains('\\') || value[0] == '/' || Path.IsPathRooted(value)) return false;
        var segments = value.Split('/');
        return segments.All(static segment => segment.Length != 0 && segment is not "." and not ".." && !segment.Contains(':'));
    }
}

internal static class RetentionFileCaptureOwnershipMarker
{
    private static readonly byte[] Domain = "copilot-agent-observability/retention-file-capture-owner-marker/v1"u8.ToArray();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] Create(string storeInstanceId, string captureId, string reservedAtText, long reservedAtUtcTicks, byte[] ownerToken)
    {
        if (!TryStoreInstance(storeInstanceId, out var store) || !IsCaptureId(captureId) || ownerToken is not { Length: 32 }
            || !Timestamp(reservedAtText, reservedAtUtcTicks)) throw new ArgumentException("Invalid retention file capture ownership marker input.");
        using var stream = new MemoryStream();
        Frame(stream, Domain); Frame(stream, store); Frame(stream, StrictUtf8.GetBytes("sensitive_bundle")); Frame(stream, StrictUtf8.GetBytes(captureId)); Frame(stream, StrictUtf8.GetBytes(reservedAtText));
        Span<byte> ticks = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(ticks, reservedAtUtcTicks); Frame(stream, ticks); Frame(stream, ownerToken);
        return stream.ToArray();
    }

    private static bool IsCaptureId(string? value) => value is { Length: 32 } && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool TryStoreInstance(string? value, out byte[] result) { result = []; if (value is not { Length: 32 } || !value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false; result = Convert.FromHexString(value); return true; }
    private static bool Timestamp(string? text, long ticks) => text is not null && DateTimeOffset.TryParseExact(text, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value) && value.UtcDateTime.Ticks == ticks;
    private static void Frame(Stream stream, ReadOnlySpan<byte> value) { Span<byte> length = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(length, value.Length); stream.Write(length); stream.Write(value); }

}

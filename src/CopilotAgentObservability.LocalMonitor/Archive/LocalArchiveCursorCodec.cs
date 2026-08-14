using System.Text;
using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.Archive;

internal static class LocalArchiveCursorCodec
{
    private static readonly byte[] Prefix = "local-archive-cursor\0v1\0"u8.ToArray();

    internal static string Encode(LocalArchiveTargetKind targetKind, LocalArchiveCursor cursor)
    {
        ArgumentNullException.ThrowIfNull(cursor);
        if (!IsDefined(targetKind)
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(cursor.ArchivedAt)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(cursor.TargetId))
        {
            throw new ArgumentException("local_archive_cursor_invalid", nameof(cursor));
        }

        var kind = Kind(targetKind);
        var text = $"local-archive-cursor\0v1\0{kind}\0{cursor.ArchivedAt}\0{cursor.TargetId}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static bool TryDecode(
        string encoded,
        LocalArchiveTargetKind expectedKind,
        out LocalArchiveCursor? cursor)
    {
        cursor = null;
        if (!IsDefined(expectedKind)
            || encoded is null
            || encoded.Length != (expectedKind == LocalArchiveTargetKind.Session ? 136 : 140)
            || encoded.Any(static character => character is not (>= 'A' and <= 'Z')
                and not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-' and not '_'))
        {
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(encoded.Replace('-', '+').Replace('_', '/'));
        }
        catch (FormatException)
        {
            return false;
        }

        var expectedLength = expectedKind == LocalArchiveTargetKind.Session ? 102 : 105;
        if (bytes.Length != expectedLength || !bytes.AsSpan().StartsWith(Prefix))
            return false;

        var remainder = bytes.AsSpan(Prefix.Length);
        var kindSeparator = remainder.IndexOf((byte)0);
        if (kindSeparator < 0)
            return false;
        var kind = Encoding.UTF8.GetString(remainder[..kindSeparator]);
        remainder = remainder[(kindSeparator + 1)..];
        var timestampSeparator = remainder.IndexOf((byte)0);
        if (timestampSeparator < 0)
            return false;
        var archivedAt = Encoding.UTF8.GetString(remainder[..timestampSeparator]);
        var targetId = Encoding.UTF8.GetString(remainder[(timestampSeparator + 1)..]);
        var decoded = new LocalArchiveCursor(archivedAt, targetId);
        if (!string.Equals(kind, Kind(expectedKind), StringComparison.Ordinal)
            || !LocalRepositoryCatalogValidation.IsCanonicalTimestamp(archivedAt)
            || !LocalRepositoryCatalogValidation.IsCanonicalUuidV7(targetId)
            || !string.Equals(Encode(expectedKind, decoded), encoded, StringComparison.Ordinal))
        {
            return false;
        }

        cursor = decoded;
        return true;
    }

    private static string Kind(LocalArchiveTargetKind targetKind) => targetKind switch
    {
        LocalArchiveTargetKind.Session => "session",
        LocalArchiveTargetKind.Repository => "repository",
        _ => throw new ArgumentOutOfRangeException(nameof(targetKind)),
    };

    private static bool IsDefined(LocalArchiveTargetKind targetKind) =>
        targetKind is LocalArchiveTargetKind.Session or LocalArchiveTargetKind.Repository;
}

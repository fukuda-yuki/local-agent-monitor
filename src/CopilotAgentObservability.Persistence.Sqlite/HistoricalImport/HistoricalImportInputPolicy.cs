using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;

internal static class HistoricalImportSemanticVersionPolicy
{
    private static readonly Regex ExactSemanticVersion = new(
        "\\A(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)" +
        "(?:-(?:(?:0|[1-9][0-9]*)|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\\.(?:(?:0|[1-9][0-9]*)|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?" +
        "(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsValid(string? value) =>
        value is not null && value.Length <= 64 && ExactSemanticVersion.IsMatch(value);
}

internal static class HistoricalSourceMetadataTokenPolicy
{
    private static readonly Regex ExactMetadataToken = new(
        "\\A[A-Za-z0-9][A-Za-z0-9._+-]*\\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool IsValid(string? value) =>
        value is not null && value.Length <= 64 && ExactMetadataToken.IsMatch(value);
}

internal static class HistoricalSourcePathPolicy
{
    private static readonly string[] WindowsDeviceNames =
    [
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    internal static bool IsCanonicalNativeAbsolute(
        string? value,
        int maximumCharacters = 4096,
        Func<string, DriveType>? windowsDriveType = null)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumCharacters
            || value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            return OperatingSystem.IsWindows()
                ? IsCanonicalWindowsAbsolute(value, windowsDriveType ?? ReadWindowsDriveType)
                : IsCanonicalUnixAbsolute(value);
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException)
        {
            return false;
        }
    }

    internal static bool IsSafeLocalFileSyntax(
        string? value,
        Func<string, DriveType>? windowsDriveType = null) =>
        TryNormalizeSafeLocalFilePath(value, out _, windowsDriveType);

    internal static bool TryNormalizeSafeLocalFilePath(
        string? value,
        out string normalizedPath,
        Func<string, DriveType>? windowsDriveType = null)
    {
        normalizedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return false;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                if (value.StartsWith("//", StringComparison.Ordinal)
                    || value.Contains('\\')
                    || LooksLikeUri(value))
                {
                    return false;
                }

                normalizedPath = Path.GetFullPath(value);
                return !IsUnixDeviceNamespace(normalizedPath);
            }

            if (value.Length >= 2
                && IsWindowsSeparator(value[0])
                && IsWindowsSeparator(value[1]))
            {
                return false;
            }

            var hasDrivePrefix = value.Length > 1 && char.IsAsciiLetter(value[0]) && value[1] == ':';
            if (hasDrivePrefix && (value.Length < 3 || value[2] is not ('\\' or '/')))
            {
                return false;
            }

            var start = hasDrivePrefix ? 2 : 0;
            if (value.AsSpan(start).IndexOf(':') >= 0
                || HasWindowsDeviceSegment(value.Replace('/', '\\')))
            {
                return false;
            }

            normalizedPath = Path.GetFullPath(value);
            var root = Path.GetPathRoot(normalizedPath);
            return !string.IsNullOrEmpty(root)
                && (windowsDriveType ?? ReadWindowsDriveType)(root) != DriveType.Network;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            NotSupportedException)
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static bool IsCanonicalWindowsAbsolute(
        string value,
        Func<string, DriveType> windowsDriveType)
    {
        if (value.Length < 4
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.Contains('/')
            || !char.IsAsciiLetter(value[0])
            || value[1] != ':'
            || value[2] != '\\'
            || value[^1] == '\\'
            || value.AsSpan(3).IndexOf(':') >= 0
            || !Path.IsPathFullyQualified(value)
            || !string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal)
            || windowsDriveType(Path.GetPathRoot(value)!) == DriveType.Network)
        {
            return false;
        }

        var tail = value[3..];
        if (tail.Length == 0)
        {
            return false;
        }

        var segments = tail.Split('\\', StringSplitOptions.None);
        return segments.All(segment => IsCanonicalWindowsSegment(segment))
            && !HasWindowsDeviceSegment(value);
    }

    private static bool IsCanonicalWindowsSegment(string segment) =>
        segment.Length > 0
        && segment is not ("." or "..")
        && segment[^1] is not (' ' or '.')
        && segment.IndexOfAny(['"', '<', '>', '|', ':', '*', '?']) < 0;

    private static bool HasWindowsDeviceSegment(string value)
    {
        var start = value.Length > 2
            && char.IsAsciiLetter(value[0])
            && value[1] == ':'
            && value[2] == '\\'
                ? 3
                : value.Length > 1 && char.IsAsciiLetter(value[0]) && value[1] == ':'
                    ? 2
                    : 0;
        return value[start..]
            .Split('\\', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => segment.TrimEnd(' ', '.').Split('.')[0])
            .Any(name => WindowsDeviceNames.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    private static bool IsWindowsSeparator(char value) => value is '\\' or '/';

    private static bool IsCanonicalUnixAbsolute(string value)
    {
        if (value.Length < 2
            || value[0] != '/'
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value[^1] == '/'
            || !Path.IsPathFullyQualified(value)
            || !string.Equals(Path.GetFullPath(value), value, StringComparison.Ordinal)
            || IsUnixDeviceNamespace(value))
        {
            return false;
        }

        var tail = value[1..];
        return tail.Length > 0
            && tail.Split('/', StringSplitOptions.None)
                .All(segment => segment.Length > 0 && segment is not ("." or ".."));
    }

    private static bool IsUnixDeviceNamespace(string value) =>
        IsAtOrBelow(value, "/dev")
        || IsAtOrBelow(value, "/proc")
        || IsAtOrBelow(value, "/sys");

    private static bool IsAtOrBelow(string value, string root) =>
        string.Equals(value, root, StringComparison.Ordinal)
        || value.StartsWith(root + "/", StringComparison.Ordinal);

    private static bool LooksLikeUri(string value)
    {
        var separator = value.IndexOf(':');
        return separator > 0
            && char.IsAsciiLetter(value[0])
            && value.AsSpan(1, separator - 1).IndexOfAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+.-") < 0;
    }

    private static DriveType ReadWindowsDriveType(string rootPathName) =>
        (DriveType)GetDriveType(rootPathName);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveType(string rootPathName);
}

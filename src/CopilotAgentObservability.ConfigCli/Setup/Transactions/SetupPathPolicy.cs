using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Transactions;

internal sealed class SetupFileStepException : Exception
{
    public SetupFileStepException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }
}

internal static class SetupPathPolicy
{
    public static string ValidateFileTarget(ISetupPlatform platform, string allowedRoot, string targetPath)
    {
        try
        {
            var root = Canonicalize(platform.PathStyle, allowedRoot);
            var target = Canonicalize(platform.PathStyle, targetPath);
            var comparison = platform.PathStyle == SetupPathStyle.Windows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var separator = platform.PathStyle == SetupPathStyle.Windows ? '\\' : '/';
            var rootPrefix = root.EndsWith(separator) ? root : root + separator;
            if (target.Equals(root, comparison) || !target.StartsWith(rootPrefix, comparison))
            {
                Reject();
            }

            ValidateDirectoryChain(platform, root);
            var relative = target[rootPrefix.Length..];
            var segments = relative.Split(separator, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = 0; index < segments.Length; index++)
            {
                current = current.EndsWith(separator) ? current + segments[index] : current + separator + segments[index];
                var isTarget = index == segments.Length - 1;
                var metadata = platform.FileSystem.GetPathMetadata(current);
                RejectReparse(metadata);
                if (!isTarget && (!metadata.Exists || metadata.Kind != SetupPathKind.Directory))
                {
                    Reject();
                }

                if (isTarget && metadata.Exists && metadata.Kind != SetupPathKind.File)
                {
                    Reject();
                }
            }

            return target;
        }
        catch (SetupFileStepException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new SetupFileStepException(SetupCodes.UnsafePath);
        }
    }

    private static string Canonicalize(SetupPathStyle style, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || LooksLikeUri(path))
        {
            Reject();
        }

        return style == SetupPathStyle.Windows ? CanonicalizeWindows(path) : CanonicalizeUnix(path);
    }

    private static string CanonicalizeWindows(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal) ||
            normalized.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Length < 3 ||
            !char.IsAsciiLetter(normalized[0]) ||
            normalized[1] != ':' ||
            normalized[2] != '\\' ||
            normalized.AsSpan(2).Contains(':'))
        {
            Reject();
        }

        var segments = normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".." || IsUnsafeWindowsSegment(segment)))
        {
            Reject();
        }

        var drive = char.ToUpperInvariant(normalized[0]);
        return segments.Length == 0 ? $"{drive}:\\" : $"{drive}:\\{string.Join('\\', segments)}";
    }

    private static bool IsUnsafeWindowsSegment(string segment)
    {
        if (segment.EndsWith('.') || segment.EndsWith(' '))
        {
            return true;
        }

        var baseName = segment.Split('.', 2)[0];
        return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            baseName.Length == 4 &&
            (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            baseName[3] is >= '1' and <= '9';
    }

    private static string CanonicalizeUnix(string path)
    {
        if (!path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal) || path.Contains('\\'))
        {
            Reject();
        }

        var segments = path[1..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            Reject();
        }

        return segments.Length == 0 ? "/" : "/" + string.Join('/', segments);
    }

    private static bool LooksLikeUri(string path)
    {
        var colon = path.IndexOf(':');
        if (colon <= 0 || colon + 2 >= path.Length || path[colon + 1] != '/' || path[colon + 2] != '/')
        {
            return false;
        }

        return path.AsSpan(0, colon).ToArray().All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '-' or '.');
    }

    private static void ValidateMetadata(ISetupPlatform platform, string path, SetupPathKind expectedKind)
    {
        var metadata = platform.FileSystem.GetPathMetadata(path);
        RejectReparse(metadata);
        if (!metadata.Exists || metadata.Kind != expectedKind)
        {
            Reject();
        }
    }

    private static void ValidateDirectoryChain(ISetupPlatform platform, string root)
    {
        var separator = platform.PathStyle == SetupPathStyle.Windows ? '\\' : '/';
        var filesystemRootLength = platform.PathStyle == SetupPathStyle.Windows ? 3 : 1;
        var current = root[..filesystemRootLength];
        ValidateMetadata(platform, current, SetupPathKind.Directory);
        foreach (var segment in root[filesystemRootLength..].Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.EndsWith(separator) ? current + segment : current + separator + segment;
            ValidateMetadata(platform, current, SetupPathKind.Directory);
        }
    }

    private static void RejectReparse(SetupPathMetadata metadata)
    {
        if (metadata.Exists && (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            Reject();
        }
    }

    private static void Reject() => throw new SetupFileStepException(SetupCodes.UnsafePath);
}

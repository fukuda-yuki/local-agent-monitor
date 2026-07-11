namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal sealed record ResolvedApplyPath(string RelativePath, string FullPath);

internal static class ApplyPathPolicy
{
    public static ResolvedApplyPath Resolve(ConfiguredApplyRoot root, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || Path.IsPathRooted(relativePath)
            || IsDriveRelativeOrDevicePath(relativePath)
            || relativePath.StartsWith("\\\\", StringComparison.Ordinal)
            || relativePath.StartsWith("\\?\\", StringComparison.Ordinal)
            || Uri.TryCreate(relativePath, UriKind.Absolute, out _)
            || relativePath.Split(['/', '\\']).Any(segment => segment is "." or ".." or ""))
        {
            throw new ApplyPathException("invalid_relative_path");
        }

        EnsureSafeExistingPath(root.CanonicalPath, "invalid_apply_root");
        var fullPath = Path.GetFullPath(Path.Combine(root.CanonicalPath, relativePath));
        var prefix = Path.TrimEndingDirectorySeparator(root.CanonicalPath) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApplyPathException("target_outside_root");
        }

        EnsureSafeExistingPath(fullPath, "unsafe_reparse_path");
        if (!File.Exists(fullPath) || (File.GetAttributes(fullPath) & FileAttributes.Directory) != 0)
        {
            throw new ApplyPathException("target_not_regular_file");
        }

        return new ResolvedApplyPath(relativePath.Replace('\\', '/'), fullPath);
    }

    private static bool IsDriveRelativeOrDevicePath(string value) =>
        (value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':')
        || value.StartsWith("\\\\?\\", StringComparison.Ordinal)
        || value.StartsWith("\\\\.\\", StringComparison.Ordinal)
        || value.StartsWith("\\?\\", StringComparison.Ordinal);

    internal static void EnsureSafeExistingPath(string path, string reparseCode)
    {
        var current = Path.GetFullPath(path);
        while (true)
        {
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                throw new ApplyPathException("target_not_regular_file");
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ApplyPathException(reparseCode);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return;
            current = parent;
        }
    }
}

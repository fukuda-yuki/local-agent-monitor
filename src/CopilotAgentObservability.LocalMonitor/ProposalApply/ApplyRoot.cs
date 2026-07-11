namespace CopilotAgentObservability.LocalMonitor.ProposalApply;

internal enum ApplyRootKind { UserConfig, Skill, Repository }

internal sealed record ConfiguredApplyRoot(Guid RootId, ApplyRootKind Kind, string Label, string CanonicalPath)
{
    public static ConfiguredApplyRoot Create(ApplyRootKind kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
        {
            throw new ApplyPathException("invalid_apply_root");
        }

        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        ApplyPathPolicy.EnsureSafeExistingPath(canonical, "invalid_apply_root");
        return new ConfiguredApplyRoot(Guid.CreateVersion7(), kind, LabelFor(kind), canonical);
    }

    public static bool TryParseKind(string value, out ApplyRootKind kind)
    {
        kind = value switch
        {
            "user_config" => ApplyRootKind.UserConfig,
            "skill" => ApplyRootKind.Skill,
            "repository" => ApplyRootKind.Repository,
            _ => default,
        };
        return value is "user_config" or "skill" or "repository";
    }

    private static string LabelFor(ApplyRootKind kind) => kind switch
    {
        ApplyRootKind.UserConfig => "User configuration",
        ApplyRootKind.Skill => "Skill",
        ApplyRootKind.Repository => "Repository",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal sealed class ApplyPathException(string code) : InvalidOperationException(code)
{
    public string Code { get; } = code;
}

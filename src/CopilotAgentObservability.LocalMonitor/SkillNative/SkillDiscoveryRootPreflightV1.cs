using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

internal enum SkillDiscoveryRootPreflightOutcomeV1
{
    NoConfiguredRoots,
    Certified,
    PlatformUnsupported,
    RootConfigurationInvalid
}

// The platform half of the Gate 8 startup gate: the certified path grammar plus the opener that
// can prove a retained root on it. Null means macOS/BSD/other or a Linux kernel below 5.8, where
// openat2 cannot supply the required resolve flags.
internal sealed record CertifiedDiscoveryPlatformV1(
    SkillProducerPathKeyPlatform Platform,
    IDiscoveryRootOpenerV1 Opener);

// Startup outcome of the Gate 8 root preflight. Certified owns the retained root handles for the
// whole process generation and releases them only when the generation is disposed; every other
// outcome owns nothing. AbortReason carries one of the two sanitized reasons and never a
// configured root value, a native fact, or a failure class.
internal sealed class SkillDiscoveryRootPreflightResultV1 : IDisposable
{
    private readonly IReadOnlyList<RetainedDiscoveryRootV1> retainedRoots;
    private int disposed;

    private SkillDiscoveryRootPreflightResultV1(
        SkillDiscoveryRootPreflightOutcomeV1 outcome,
        DiscoveryRootSetV1? rootSet,
        IReadOnlyList<RetainedDiscoveryRootV1> retainedRoots,
        string? abortReason)
    {
        Outcome = outcome;
        RootSet = rootSet;
        this.retainedRoots = retainedRoots;
        AbortReason = abortReason;
    }

    public SkillDiscoveryRootPreflightOutcomeV1 Outcome { get; }

    public DiscoveryRootSetV1? RootSet { get; }

    public IReadOnlyList<RetainedDiscoveryRootV1> RetainedRoots => retainedRoots;

    public string? AbortReason { get; }

    internal static SkillDiscoveryRootPreflightResultV1 NoConfiguredRoots() =>
        new(SkillDiscoveryRootPreflightOutcomeV1.NoConfiguredRoots, null, [], null);

    internal static SkillDiscoveryRootPreflightResultV1 PlatformUnsupported() =>
        new(
            SkillDiscoveryRootPreflightOutcomeV1.PlatformUnsupported,
            null,
            [],
            SkillDiscoveryRootPreflightV1.PlatformUnsupportedReason);

    internal static SkillDiscoveryRootPreflightResultV1 RootConfigurationInvalid() =>
        new(
            SkillDiscoveryRootPreflightOutcomeV1.RootConfigurationInvalid,
            null,
            [],
            SkillDiscoveryRootPreflightV1.RootConfigurationInvalidReason);

    internal static SkillDiscoveryRootPreflightResultV1 Certified(
        DiscoveryRootSetV1 rootSet,
        IReadOnlyList<RetainedDiscoveryRootV1> retainedRoots) =>
        new(SkillDiscoveryRootPreflightOutcomeV1.Certified, rootSet, retainedRoots, null);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        for (var index = retainedRoots.Count - 1; index >= 0; index--)
        {
            retainedRoots[index].Dispose();
        }
    }
}

// Gate 8 startup root preflight: it turns the parsed repeatable CLI values into one immutable
// per-platform DiscoveryRootSetV1 plus the retained root handles the process generation owns, or
// aborts host startup with exactly one of the two sanitized reasons.
//
// The fixed order is zero roots -> platform -> roots, because zero configured roots is valid route
// absence even where the platform is unsupported, while an unsupported or uncertified platform
// precedes every configured-root syntax, handle, identity, and filesystem fault. Any single root
// fault fails the whole configuration: Gate 8 admits no silent partial-root reduction, so the
// valid roots of a partly invalid configuration never start a degraded service.
internal static class SkillDiscoveryRootPreflightV1
{
    internal const string PlatformUnsupportedReason = "skill_discovery_platform_unsupported";
    internal const string RootConfigurationInvalidReason = "skill_discovery_root_configuration_invalid";

    internal static SkillDiscoveryRootPreflightResultV1 Run(
        IReadOnlyList<string>? configuredProjectPaths,
        IReadOnlyList<string>? configuredSkillDirectories) =>
        Run(configuredProjectPaths, configuredSkillDirectories, ResolveCertifiedPlatform());

    internal static SkillDiscoveryRootPreflightResultV1 Run(
        IReadOnlyList<string>? configuredProjectPaths,
        IReadOnlyList<string>? configuredSkillDirectories,
        CertifiedDiscoveryPlatformV1? certifiedPlatform)
    {
        var projectPaths = configuredProjectPaths ?? [];
        var skillDirectories = configuredSkillDirectories ?? [];

        if (projectPaths.Count == 0 && skillDirectories.Count == 0)
        {
            return SkillDiscoveryRootPreflightResultV1.NoConfiguredRoots();
        }

        if (certifiedPlatform is null)
        {
            return SkillDiscoveryRootPreflightResultV1.PlatformUnsupported();
        }

        var opened = new List<RetainedDiscoveryRootV1>(projectPaths.Count + skillDirectories.Count);
        var retainedRootsTransferred = false;
        try
        {
            foreach (var configuredRoot in projectPaths)
            {
                if (!TryOpen(certifiedPlatform.Opener, configuredRoot, DiscoveryRootKindV1.ProjectPath, opened))
                {
                    return SkillDiscoveryRootPreflightResultV1.RootConfigurationInvalid();
                }
            }

            foreach (var configuredRoot in skillDirectories)
            {
                if (!TryOpen(certifiedPlatform.Opener, configuredRoot, DiscoveryRootKindV1.SkillDirectory, opened))
                {
                    return SkillDiscoveryRootPreflightResultV1.RootConfigurationInvalid();
                }
            }

            // The retained-handle arm of the preflight: a root replaced between its own open and the
            // end of the walk still holds a valid handle whose captured identity no longer matches,
            // and that configuration must not reach the generation.
            foreach (var root in opened)
            {
                if (!certifiedPlatform.Opener.TryReproveRetainedRoot(root))
                {
                    return SkillDiscoveryRootPreflightResultV1.RootConfigurationInvalid();
                }
            }

            var rootSet = DiscoveryRootSetV1.Create(
                certifiedPlatform.Platform,
                opened.Select(root => new DiscoveryRootCandidateV1(root.Kind, root.NativeIdentity, root.PathKey)));

            var survivors = SelectSurvivingRoots(rootSet, opened);
            retainedRootsTransferred = true;
            return SkillDiscoveryRootPreflightResultV1.Certified(rootSet, survivors);
        }
        finally
        {
            if (!retainedRootsTransferred)
            {
                for (var index = opened.Count - 1; index >= 0; index--)
                {
                    opened[index].Dispose();
                }
            }
        }
    }

    private static bool TryOpen(
        IDiscoveryRootOpenerV1 opener,
        string configuredRoot,
        DiscoveryRootKindV1 kind,
        List<RetainedDiscoveryRootV1> opened)
    {
        var result = opener.TryOpenRetainedRoot(configuredRoot, kind);
        if (!result.IsSuccess)
        {
            return false;
        }

        opened.Add(result.Root!);
        return true;
    }

    // DiscoveryRootSetV1 is the sole dedupe authority, so the surviving handles are read back from
    // the canonical arrays it produced rather than deduped a second time here. A configured value
    // repeated verbatim opens twice and yields two handles for one surviving entry; only the first
    // is retained and the rest are released with the other losers.
    private static IReadOnlyList<RetainedDiscoveryRootV1> SelectSurvivingRoots(
        DiscoveryRootSetV1 rootSet,
        List<RetainedDiscoveryRootV1> opened)
    {
        var survivingProjectPathKeys = new HashSet<string>(rootSet.ProjectPathKeys, StringComparer.Ordinal);
        var survivingSkillDirectoryKeys = new HashSet<string>(rootSet.SkillDirectoryKeys, StringComparer.Ordinal);
        var claimed = new HashSet<(DiscoveryRootKindV1 Kind, string Key)>();
        var survivors = new List<RetainedDiscoveryRootV1>(
            rootSet.ProjectPathKeys.Count + rootSet.SkillDirectoryKeys.Count);
        var losers = new List<RetainedDiscoveryRootV1>();

        foreach (var root in opened)
        {
            var surviving = root.Kind == DiscoveryRootKindV1.ProjectPath
                ? survivingProjectPathKeys
                : survivingSkillDirectoryKeys;

            if (surviving.Contains(root.PathKey.Key) && claimed.Add((root.Kind, root.PathKey.Key)))
            {
                survivors.Add(root);
            }
            else
            {
                losers.Add(root);
            }
        }

        for (var index = losers.Count - 1; index >= 0; index--)
        {
            losers[index].Dispose();
        }

        return survivors;
    }

    private static CertifiedDiscoveryPlatformV1? ResolveCertifiedPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new CertifiedDiscoveryPlatformV1(
                SkillProducerPathKeyPlatform.Windows,
                new WindowsDiscoveryRootOpenerV1());
        }

        if (OperatingSystem.IsLinux() && LinuxNativeFileApisV1.IsSupportedKernel())
        {
            return new CertifiedDiscoveryPlatformV1(
                SkillProducerPathKeyPlatform.Linux,
                new LinuxDiscoveryRootOpenerV1());
        }

        return null;
    }
}

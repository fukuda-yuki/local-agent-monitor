using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// Gate 8 scan outcomes. DiscoveryUnavailable and NotDiscovered terminate before any native order;
// Unsafe covers both "more than one post-collapse candidate" and "a matching item whose source/root
// relation is malformed or resolves outside the retained roots".
internal enum SkillDiscoveryScanOutcome
{
    NotDiscovered,
    Proceed,
    Unsafe,
    DiscoveryUnavailable
}

internal sealed record SkillDiscoveryScanResult(
    SkillDiscoveryScanOutcome Outcome,
    CurrentSkillReadTargetV1? Target)
{
    internal static SkillDiscoveryScanResult NotDiscovered() =>
        new(SkillDiscoveryScanOutcome.NotDiscovered, null);

    internal static SkillDiscoveryScanResult ProceedWith(CurrentSkillReadTargetV1 target) =>
        new(SkillDiscoveryScanOutcome.Proceed, target);

    internal static SkillDiscoveryScanResult Unsafe() =>
        new(SkillDiscoveryScanOutcome.Unsafe, null);

    internal static SkillDiscoveryScanResult DiscoveryUnavailable() =>
        new(SkillDiscoveryScanOutcome.DiscoveryUnavailable, null);
}

// Single full-list scan of the materialized SDK discovery result against the historical
// (name, source, path) triple and the retained discovery roots. The scanner never selects first or
// by SDK order, never resolves a relative path, and collapses rows only when the complete
// eight-fact DTO tuple and the resolved root role/native identity/relative-segment target are
// identical. It retains at most two post-collapse eligible descriptors while continuing full-list
// validation: exactly zero is not-discovered, exactly one proceeds, and more than one is unsafe.
internal static class SkillDiscoveryCandidateScannerV1
{
    private const string SourceProject = "project";
    private const string SourceInherited = "inherited";
    private const string SourceCustom = "custom";
    private const string SourcePersonalCopilot = "personal-copilot";
    private const string SourcePersonalAgents = "personal-agents";
    private const string SourcePlugin = "plugin";

    internal static SkillDiscoveryScanResult Scan(
        string historicalName,
        string historicalSource,
        string historicalPath,
        IReadOnlyList<CopilotDiscoveredSkillFactV1>? facts,
        IReadOnlyList<RetainedDiscoveryRootV1> retainedRoots,
        string expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(historicalName);
        ArgumentNullException.ThrowIfNull(historicalSource);
        ArgumentNullException.ThrowIfNull(historicalPath);
        ArgumentNullException.ThrowIfNull(retainedRoots);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);

        // A null aggregate or any null item / unreadable documented nonnull member is discovery-
        // unavailable even after one or two candidates were already seen: the full list must be
        // validated, so unavailability is decided before any candidate count is honored.
        if (facts is null)
        {
            return SkillDiscoveryScanResult.DiscoveryUnavailable();
        }

        var platform = ResolvePlatform(retainedRoots);
        if (platform is null)
        {
            return SkillDiscoveryScanResult.DiscoveryUnavailable();
        }

        if (!SkillProducerPathKeyV1.TryParse(historicalPath, platform.Value, out var historicalPathKey, out _))
        {
            // A relative/malformed historical path can never be a candidate and is never resolved
            // from CWD, repository, workspace, or a configured root string.
            return SkillDiscoveryScanResult.NotDiscovered();
        }

        var observedMalformedMatch = false;
        var eligible = new Dictionary<EligibleCandidate, CurrentSkillReadTargetV1>();

        foreach (var item in facts)
        {
            if (item is null || item.Name is null || item.Source is null || item.Path is null)
            {
                return SkillDiscoveryScanResult.DiscoveryUnavailable();
            }

            if (!string.Equals(item.Name, historicalName, StringComparison.Ordinal)
                || !string.Equals(item.Source, historicalSource, StringComparison.Ordinal)
                || !string.Equals(item.Path, historicalPath, StringComparison.Ordinal))
            {
                // Unrelated readable nonmatches are ignored without ending the scan.
                continue;
            }

            if (!IsEligibleSource(historicalSource))
            {
                // builtin/remote/missing/unknown are unavailable in v1 and grant no candidate.
                continue;
            }

            if (!SkillProducerPathKeyV1.TryParse(item.Path, platform.Value, out var discoveryPathKey, out _))
            {
                observedMalformedMatch = true;
                continue;
            }

            var resolution = TryResolveTarget(
                historicalSource, item, discoveryPathKey, retainedRoots, expectedRevision);
            switch (resolution.Outcome)
            {
                case ResolutionOutcome.Malformed:
                    observedMalformedMatch = true;
                    continue;
                case ResolutionOutcome.NotEligible:
                    continue;
            }

            eligible.TryAdd(new EligibleCandidate(
                item.Name,
                item.Source,
                item.Path,
                item.ProjectPath,
                item.Description,
                item.ArgumentHint,
                item.Enabled,
                item.UserInvocable,
                resolution.Target!.RootRole,
                Convert.ToHexStringLower(resolution.Target.RetainedRoot.NativeIdentity.ToByteArray()),
                string.Join('\u0000', resolution.Target.RelativeSegments)),
                resolution.Target);
        }

        if (observedMalformedMatch)
        {
            return SkillDiscoveryScanResult.Unsafe();
        }

        return eligible.Count switch
        {
            0 => SkillDiscoveryScanResult.NotDiscovered(),
            1 => SkillDiscoveryScanResult.ProceedWith(eligible.Values.Single()),
            _ => SkillDiscoveryScanResult.Unsafe()
        };
    }

    private static SkillProducerPathKeyPlatform? ResolvePlatform(IReadOnlyList<RetainedDiscoveryRootV1> retainedRoots)
    {
        SkillProducerPathKeyPlatform? platform = null;
        foreach (var root in retainedRoots)
        {
            if (root is null)
            {
                return null;
            }

            if (platform is null)
            {
                platform = root.PathKey.Platform;
            }
            else if (platform.Value != root.PathKey.Platform)
            {
                return null;
            }
        }

        return platform;
    }

    private static bool IsEligibleSource(string source) =>
        source is SourceProject or SourceInherited or SourceCustom
            or SourcePersonalCopilot or SourcePersonalAgents or SourcePlugin;

    private enum ResolutionOutcome
    {
        Resolved,
        Malformed,
        NotEligible
    }

    private sealed record Resolution(ResolutionOutcome Outcome, CurrentSkillReadTargetV1? Target)
    {
        internal static Resolution ResolvedTarget(CurrentSkillReadTargetV1 target) =>
            new(ResolutionOutcome.Resolved, target);

        internal static Resolution MalformedRelation() =>
            new(ResolutionOutcome.Malformed, null);

        internal static Resolution NotEligible() =>
            new(ResolutionOutcome.NotEligible, null);
    }

    private static Resolution TryResolveTarget(
        string source,
        CopilotDiscoveredSkillFactV1 item,
        SkillProducerPathKeyV1 discoveryPathKey,
        IReadOnlyList<RetainedDiscoveryRootV1> retainedRoots,
        string expectedRevision)
    {
        RetainedDiscoveryRootV1? resolvedRoot;

        if (source is SourceProject or SourceInherited)
        {
            if (item.ProjectPath is null)
            {
                return Resolution.MalformedRelation();
            }

            if (!SkillProducerPathKeyV1.TryParse(item.ProjectPath, discoveryPathKey.Platform, out var projectPathKey, out _))
            {
                return Resolution.MalformedRelation();
            }

            resolvedRoot = null;
            foreach (var root in retainedRoots)
            {
                if (root.Kind == DiscoveryRootKindV1.ProjectPath && root.PathKey.Equals(projectPathKey))
                {
                    resolvedRoot = root;
                    break;
                }
            }

            if (resolvedRoot is null || !discoveryPathKey.IsStrictDescendantOf(projectPathKey))
            {
                return Resolution.MalformedRelation();
            }
        }
        else
        {
            if (source == SourceCustom && item.ProjectPath is not null)
            {
                return Resolution.MalformedRelation();
            }

            RetainedDiscoveryRootV1? ancestor = null;
            var ancestorCount = 0;
            foreach (var root in retainedRoots)
            {
                if (root.Kind == DiscoveryRootKindV1.SkillDirectory && discoveryPathKey.IsStrictDescendantOf(root.PathKey))
                {
                    ancestor = root;
                    ancestorCount++;
                }
            }

            // A path beneath more than one configured SkillDirectory root is ambiguous; Gate 8 never
            // selects first or by SDK order.
            if (ancestorCount != 1)
            {
                return Resolution.MalformedRelation();
            }

            resolvedRoot = ancestor;
        }

        try
        {
            var relativeSegments = discoveryPathKey.Segments
                .Skip(resolvedRoot!.PathKey.Segments.Count)
                .ToArray();
            var target = new CurrentSkillReadTargetV1(resolvedRoot, relativeSegments, expectedRevision);
            return Resolution.ResolvedTarget(target);
        }
        catch (ArgumentException)
        {
            return Resolution.MalformedRelation();
        }
    }

    private sealed record EligibleCandidate(
        string Name,
        string Source,
        string Path,
        string? ProjectPath,
        string? Description,
        string? ArgumentHint,
        bool Enabled,
        bool UserInvocable,
        DiscoveryRootKindV1 RootRole,
        string NativeIdentityHex,
        string RelativeSegmentsKey);
}

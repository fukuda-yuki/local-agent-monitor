using GitHub.Copilot;
using System.Text;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed record OwnedSessionSkillProofV1(string NativePath, string RootRevision, string Content, string Digest);

internal interface IOwnedSessionSkillProofProviderV1
{
    bool TryProve(CopilotDiscoveredSkillFactV1 fact, IReadOnlyList<string> roots,
        out OwnedSessionSkillProofV1? proof);
}

internal sealed class RetainedRootOwnedSessionSkillProofProviderV1(
    SkillDiscoveryRootLeaseV1 rootLease,
    ICurrentSkillNativeFileReaderV1 nativeReader,
    CancellationToken workToken) : IOwnedSessionSkillProofProviderV1
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public bool TryProve(
        CopilotDiscoveredSkillFactV1 fact,
        IReadOnlyList<string> roots,
        out OwnedSessionSkillProofV1? proof)
    {
        workToken.ThrowIfCancellationRequested();
        proof = null;
        if (!string.Equals(fact.Source, "custom", StringComparison.Ordinal)
            || fact.ProjectPath is not null
            || !roots.SequenceEqual(rootLease.RootSet.SkillDirectoryKeys, StringComparer.Ordinal))
            return false;

        var scan = SkillDiscoveryCandidateScannerV1.Scan(
            fact.Name, fact.Source, fact.Path, [fact], rootLease.RetainedRoots, rootLease.Revision);
        if (scan.Outcome != SkillDiscoveryScanOutcome.Proceed
            || scan.Target is not { RootRole: DiscoveryRootKindV1.SkillDirectory } target)
            return false;

        var read = nativeReader.Read(target, workToken);
        workToken.ThrowIfCancellationRequested();
        if (read.Outcome != CurrentSkillNativeOutcomeV1.Success || read.Body is null || read.BodySha256 is null)
            return false;
        try
        {
            proof = new(fact.Path, rootLease.Revision, StrictUtf8.GetString(read.Body),
                Convert.ToHexStringLower(read.BodySha256));
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}

internal sealed record OwnedSessionFrozenSkillV1(
    CopilotDiscoveredSkillFactV1 Descriptor,
    OwnedSessionSkillProofV1 Proof);

internal sealed record OwnedSessionFrozenSkillInventoryV1(
    IReadOnlyDictionary<string, OwnedSessionFrozenSkillV1> Retained,
    IReadOnlyDictionary<string, CopilotDiscoveredSkillFactV1> Probe,
    IReadOnlyList<string> DisabledSkills,
    IReadOnlyList<string>? RetainedRoots = null);

internal static class OwnedSessionSdkPolicyV1
{
    internal static SessionConfig CreateProbeConfig(
        SessionConfig baseline, IReadOnlyList<string> skillDirectories, Action<SessionEvent> onEvent) =>
        CreateConfig(baseline, skillDirectories, [], onEvent, execution: false);

    internal static SessionConfig CreateExecutionConfig(
        SessionConfig baseline, IReadOnlyList<string> skillDirectories, IReadOnlyList<string> disabledSkills,
        Action<SessionEvent> onEvent) =>
        CreateConfig(baseline, skillDirectories, disabledSkills, onEvent, execution: true);

    internal static OwnedSessionFrozenSkillInventoryV1? TryFreezeProbeInventory(
        IReadOnlyList<CopilotDiscoveredSkillFactV1>? facts,
        IReadOnlyList<string> retainedRoots,
        IOwnedSessionSkillProofProviderV1 proofProvider)
    {
        if (facts is null || retainedRoots.Count == 0) return null;
        var probe = new Dictionary<string, CopilotDiscoveredSkillFactV1>(StringComparer.Ordinal);
        var retained = new Dictionary<string, OwnedSessionFrozenSkillV1>(StringComparer.Ordinal);
        var disabled = new List<string>();
        foreach (var fact in facts)
        {
            if (string.IsNullOrEmpty(fact.Name) || string.IsNullOrEmpty(fact.Source)
                || string.IsNullOrEmpty(fact.Path) || !probe.TryAdd(fact.Name, fact)) return null;

            if (string.Equals(fact.Source, "custom", StringComparison.Ordinal))
            {
                if (fact.ProjectPath is not null || !proofProvider.TryProve(fact, retainedRoots, out var proof)
                    || proof is null || !EndsAtSkillFile(proof.NativePath)) return null;
                retained.Add(fact.Name, new(fact, proof));
            }
            else
            {
                disabled.Add(fact.Name);
            }
        }
        disabled.Sort(StringComparer.Ordinal);
        return new(retained, probe, disabled, [.. retainedRoots]);
    }

    internal static bool ValidateExecutionInventory(
        OwnedSessionFrozenSkillInventoryV1 frozen,
        IReadOnlyList<CopilotDiscoveredSkillFactV1>? facts,
        IOwnedSessionSkillProofProviderV1 proofProvider)
    {
        if (facts is null) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (!seen.Add(fact.Name) || !frozen.Probe.TryGetValue(fact.Name, out var probe)) return false;
            if (frozen.Retained.TryGetValue(fact.Name, out var retained))
            {
                if (!fact.Enabled || !SameDescriptor(fact, retained.Descriptor)
                    || !proofProvider.TryProve(fact, frozen.RetainedRoots ?? [], out var proof)
                    || proof != retained.Proof) return false;
            }
            else if (fact.Enabled || !SameDescriptor(fact, probe)) return false;
        }
        return frozen.Retained.Keys.All(seen.Contains);
    }

    private static SessionConfig CreateConfig(
        SessionConfig baseline, IReadOnlyList<string> skillDirectories, IReadOnlyList<string> disabledSkills,
        Action<SessionEvent> onEvent, bool execution)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(onEvent);
        var config = baseline.Clone();
        config.OnEvent = onEvent;
        config.EnableSkills = true;
        config.EnableConfigDiscovery = false;
        config.SkipCustomInstructions = true;
        config.SkillDirectories = [.. skillDirectories];
        config.PluginDirectories = [];
        config.InstructionDirectories = [];
        config.DisabledSkills = [.. disabledSkills];
        config.Commands = null;
        var available = baseline.AvailableTools?.ToList() ?? [];
        if (available.Count == 0
            || available.Any(static tool => string.IsNullOrEmpty(tool)
                || !tool.StartsWith("custom:", StringComparison.Ordinal))
            || available.Distinct(StringComparer.Ordinal).Count() != available.Count)
        {
            throw new InvalidOperationException("Owned session custom tool boundary is invalid.");
        }
        if (execution)
        {
            available.Add("builtin:skill");
            available.Add("builtin:task_complete");
        }
        config.AvailableTools = available;
        return config;
    }

    private static bool EndsAtSkillFile(string path) =>
        string.Equals(Path.GetFileName(path), "SKILL.md", StringComparison.Ordinal);

    private static bool SameDescriptor(CopilotDiscoveredSkillFactV1 left, CopilotDiscoveredSkillFactV1 right) =>
        string.Equals(left.Name, right.Name, StringComparison.Ordinal)
        && string.Equals(left.Source, right.Source, StringComparison.Ordinal)
        && string.Equals(left.Path, right.Path, StringComparison.Ordinal)
        && string.Equals(left.ProjectPath, right.ProjectPath, StringComparison.Ordinal)
        && string.Equals(left.Description, right.Description, StringComparison.Ordinal)
        && string.Equals(left.ArgumentHint, right.ArgumentHint, StringComparison.Ordinal)
        && left.UserInvocable == right.UserInvocable;
}

using System.Diagnostics.CodeAnalysis;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal interface ICopilotSkillRuntimeClient : IAsyncDisposable
{
    Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(
        IReadOnlyList<string> projectPaths,
        IReadOnlyList<string> skillDirectories,
        CancellationToken cancellationToken);
}

internal sealed record CopilotRuntimeStatusObservationV1(string? Version, int? ProtocolVersion, string? SessionStartCopilotVersion);

internal sealed record CopilotDiscoveredSkillFactV1(
    string Name, string Source, string Path, string? ProjectPath, string? Description,
    string? ArgumentHint, bool Enabled, bool UserInvocable);

internal abstract record CopilotSkillDiscoveryOutcome
{
    private CopilotSkillDiscoveryOutcome() { }
    public sealed record Discovered(IReadOnlyList<CopilotDiscoveredSkillFactV1> Facts) : CopilotSkillDiscoveryOutcome;
    public sealed record Unavailable : CopilotSkillDiscoveryOutcome;
}

internal sealed class CopilotSdkSkillDiscoveryGateway
{
    internal CopilotSdkSkillDiscoveryGateway() { }

    public async Task<CopilotSkillDiscoveryOutcome> DiscoverAsync(
        CopilotRuntimeOperationCapabilityV1 capability,
        DiscoveryRootSetV1 roots,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(roots);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, capability.WorkToken);
        try
        {
            var facts = await capability.Owner.Client
                .DiscoverSkillsAsync(roots.ProjectPathKeys, roots.SkillDirectoryKeys, linked.Token)
                .ConfigureAwait(false);
            return facts is null
                ? new CopilotSkillDiscoveryOutcome.Unavailable()
                : new CopilotSkillDiscoveryOutcome.Discovered(facts);
        }
        catch
        {
            return new CopilotSkillDiscoveryOutcome.Unavailable();
        }
    }
}

internal static class CopilotRuntimeIdentityCertifierV1
{
    internal static bool Certifies(CopilotRuntimeStatusObservationV1? status) => TryCertify(status, out _);

    internal static bool TryCertify(
        CopilotRuntimeStatusObservationV1? status,
        [NotNullWhen(true)] out CertifiedSkillProducerIdentityV1? identity)
    {
        identity = null;
        if (status?.Version is null
            || status.ProtocolVersion != CopilotRuntimeGenerationV1.AdmittedProtocolVersion
            || status.SessionStartCopilotVersion is not null
                && !string.Equals(status.SessionStartCopilotVersion, status.Version, StringComparison.Ordinal))
            return false;

        SkillInvocationV2ArtifactRegistry registry;
        try { registry = SkillInvocationV2ArtifactRegistry.Load(); }
        catch (InvalidOperationException) { return false; }

        var matches = registry.CurrentEntries.Where(entry =>
            entry.Disposition == SkillInvocationV2CompatibilityDisposition.Accepted
            && string.Equals(entry.Tuple.SourceApplicationVersion, status.Version, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1) return false;

        var tuple = matches[0].Tuple;
        identity = new CertifiedSkillProducerIdentityV1(
            tuple.SourceApplicationVersion, status.ProtocolVersion.Value, tuple.AdapterVersion,
            tuple.NormalizationVersion, tuple.PayloadSchema, tuple.SchemaFingerprint, registry.CurrentRevision);
        return true;
    }
}

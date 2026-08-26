using System.Diagnostics.CodeAnalysis;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class FixedSkillRegistryGenerationAuthority : ISkillRegistryGenerationAuthority
{
    private readonly IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> entries;
    private readonly int revision;
    private readonly string artifactFingerprint;
    private readonly Generation generation = new();

    private FixedSkillRegistryGenerationAuthority(SkillInvocationV2CompatibilityRegistryRevision revision)
    {
        ArgumentNullException.ThrowIfNull(revision);
        entries = revision.Entries;
        this.revision = revision.Revision;
        artifactFingerprint = revision.ArtifactFingerprint;
    }

    internal static FixedSkillRegistryGenerationAuthority Load()
    {
        var registry = SkillInvocationV2ArtifactRegistry.Load();
        return new(registry.History.Single(revision => revision.Revision == registry.CurrentRevision));
    }

    internal static FixedSkillRegistryGenerationAuthority ForWriterVersion(string writerVersion)
    {
        var registry = SkillInvocationV2ArtifactRegistry.Load();
        if (!registry.TryResolveWriterVersion(writerVersion, out var revision))
            throw new InvalidOperationException("Skill invocation v2 writer provenance is unavailable.");
        return new(revision);
    }

    internal bool MatchesWriterVersion(string writerVersion) =>
        SkillInvocationV2ArtifactRegistry.Load().TryResolveWriterVersion(writerVersion, out var candidate)
        && candidate.Revision == revision
        && string.Equals(candidate.ArtifactFingerprint, artifactFingerprint, StringComparison.Ordinal);

    public ISkillRegistryGenerationCapture CaptureGeneration() => generation;

    public bool TryAcquireGenerationReadLease(
        ISkillRegistryGenerationCapture capture,
        [NotNullWhen(true)] out ISkillRegistryGenerationLease? lease)
    {
        if (!ReferenceEquals(capture, generation))
        {
            lease = null;
            return false;
        }

        lease = generation;
        return true;
    }

    public bool VerifyGenerationIdentity(
        ISkillRegistryGenerationCapture capture,
        ISkillRegistryGenerationLease lease) =>
        ReferenceEquals(capture, generation) && ReferenceEquals(lease, generation);

    public bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple)
    {
        ArgumentNullException.ThrowIfNull(tuple);
        var candidate = new SkillInvocationV2CompatibilityTuple(
            tuple.SourceApplicationVersion,
            tuple.AdapterVersion,
            tuple.NormalizationVersion,
            tuple.PayloadSchema,
            tuple.SchemaFingerprint);
        return ReferenceEquals(lease, generation)
            && entries.Any(entry => entry.Disposition == SkillInvocationV2CompatibilityDisposition.Accepted && entry.Tuple == candidate);
    }

    private sealed class Generation : ISkillRegistryGenerationCapture, ISkillRegistryGenerationLease
    {
        public void Dispose() { }
    }
}

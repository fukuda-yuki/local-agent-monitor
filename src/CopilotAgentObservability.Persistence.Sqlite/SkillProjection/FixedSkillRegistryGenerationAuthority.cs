using System.Diagnostics.CodeAnalysis;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class FixedSkillRegistryGenerationAuthority : ISkillRegistryGenerationAuthority
{
    private readonly IReadOnlyList<SkillInvocationV2CompatibilityRegistryEntry> entries;
    private readonly int revision;
    private readonly string artifactFingerprint;
    private readonly string artifactAuthorityIdentity;
    private readonly string generationIdentity;
    private readonly Generation generation = new();

    private FixedSkillRegistryGenerationAuthority(
        SkillInvocationV2CompatibilityRegistryRevision revision,
        string? generationIdentity = null)
    {
        ArgumentNullException.ThrowIfNull(revision);
        entries = revision.Entries;
        this.revision = revision.Revision;
        artifactFingerprint = revision.ArtifactFingerprint;
        artifactAuthorityIdentity = $"{revision.Revision}:{revision.ArtifactFingerprint}";
        this.generationIdentity = generationIdentity ?? artifactAuthorityIdentity;
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

    internal static FixedSkillRegistryGenerationAuthority ForWriterVersion(
        string writerVersion,
        string generationIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationIdentity);
        var registry = SkillInvocationV2ArtifactRegistry.Load();
        if (!registry.TryResolveWriterVersion(writerVersion, out var revision))
            throw new InvalidOperationException("Skill invocation v2 writer provenance is unavailable.");
        return new(revision, generationIdentity);
    }

    internal static FixedSkillRegistryGenerationAuthority ForPersistedGenerationIdentity(
        string currentWriterVersion,
        string generationIdentity)
    {
        ArgumentException.ThrowIfNullOrEmpty(generationIdentity);
        var registry = SkillInvocationV2ArtifactRegistry.Load();
        var historical = registry.History.SingleOrDefault(revision =>
            string.Equals(
                generationIdentity,
                $"{revision.Revision}:{revision.ArtifactFingerprint}",
                StringComparison.Ordinal));
        if (historical is not null) return new(historical, generationIdentity);
        if (!registry.TryResolveWriterVersion(currentWriterVersion, out var current))
            throw new InvalidOperationException("Skill invocation v2 writer provenance is unavailable.");
        return new(current, generationIdentity);
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

    public string GetCanonicalGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease)
    {
        if (!VerifyGenerationIdentity(capture, lease)) throw new InvalidOperationException("skill_registry_generation_not_current");
        return generationIdentity;
    }

    public string? GetCanonicalArtifactAuthorityIdentity(
        ISkillRegistryGenerationCapture capture,
        ISkillRegistryGenerationLease lease) =>
        VerifyGenerationIdentity(capture, lease) ? artifactAuthorityIdentity : null;

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

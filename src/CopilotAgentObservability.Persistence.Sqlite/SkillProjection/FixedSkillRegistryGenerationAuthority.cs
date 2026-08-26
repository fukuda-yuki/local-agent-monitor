using System.Diagnostics.CodeAnalysis;

namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed class FixedSkillRegistryGenerationAuthority : ISkillRegistryGenerationAuthority
{
    private readonly SkillInvocationV2ArtifactRegistry registry;
    private readonly Generation generation = new();

    private FixedSkillRegistryGenerationAuthority(SkillInvocationV2ArtifactRegistry registry) =>
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));

    internal static FixedSkillRegistryGenerationAuthority Load() =>
        new(SkillInvocationV2ArtifactRegistry.Load());

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
        return ReferenceEquals(lease, generation) && registry.IsAccepted(new(
            tuple.SourceApplicationVersion,
            tuple.AdapterVersion,
            tuple.NormalizationVersion,
            tuple.PayloadSchema,
            tuple.SchemaFingerprint));
    }

    private sealed class Generation : ISkillRegistryGenerationCapture, ISkillRegistryGenerationLease
    {
        public void Dispose() { }
    }
}

using System.Diagnostics.CodeAnalysis;

namespace CopilotAgentObservability.Persistence.Sqlite;

// The exact producer five-tuple the #154 current-authorization proves against the registry
// generation. The fields mirror skill_projection_sdk_claims columns so the read owner never
// depends on the LocalMonitor compatibility-tuple type; the generation authority
// implementation reconstructs its own tuple from these strings.
internal sealed record SkillRegistryProducerTuple(
    string SourceApplicationVersion,
    string AdapterVersion,
    string NormalizationVersion,
    string PayloadSchema,
    string SchemaFingerprint);

// Opaque identity of one captured registry generation. Only the generation authority
// constructs or interprets these; the read owner treats them as handles. The marker keeps
// the read owner from ever reaching into a generation's registry file, history, or path.
internal interface ISkillRegistryGenerationCapture
{
}

// A non-mutating read lease on one captured registry generation. Disposing releases the
// lease so a publication waiting on outstanding leases may proceed.
internal interface ISkillRegistryGenerationLease : IDisposable
{
}

internal enum SkillRegistryCurrentAuthorizationOutcome
{
    Acquired,
    NotCurrent,
    Busy,
    Unavailable
}

// The registry generation authority consumed by the #154 read owner. The implementation owns
// the atomic current-generation pointer and the outstanding read leases; a publication of a
// new generation waits for every outstanding lease before swapping the pointer.
internal interface ISkillRegistryGenerationAuthority
{
    // Captures the greatest complete registry generation. Returns null only when the current
    // generation is malformed or unavailable.
    ISkillRegistryGenerationCapture? CaptureGeneration();

    // Acquires a non-mutating read lease for the captured generation. Returns false when the
    // current pointer no longer matches the capture (pre-lease churn), leaving no lease held.
    bool TryAcquireGenerationReadLease(
        ISkillRegistryGenerationCapture capture,
        [NotNullWhen(true)] out ISkillRegistryGenerationLease? lease);

    // Re-proves pointer/revision/object identity between the capture and the leased
    // generation. Returns false on any mismatch.
    bool VerifyGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease);

    string GetCanonicalGenerationIdentity(ISkillRegistryGenerationCapture capture, ISkillRegistryGenerationLease lease) =>
        capture.GetType().FullName ?? "skill-registry-generation";

    string? GetCanonicalArtifactAuthorityIdentity(
        ISkillRegistryGenerationCapture capture,
        ISkillRegistryGenerationLease lease) => null;

    // Whether the exact producer tuple is accepted by the generation behind the lease.
    bool IsProducerTupleAccepted(ISkillRegistryGenerationLease lease, SkillRegistryProducerTuple tuple);
}

// The opaque #154 current-authorization capability. Owns the registry-generation read lease
// and only the sanitized exact current-claim facts #158 needs (name and source); it exposes
// no registry file, history, path, or direct table reader.
internal sealed class SkillProjectionCurrentSdkClaimAuthorization : IDisposable
{
    private readonly ISkillRegistryGenerationLease generationLease;
    private int disposed;

    internal SkillProjectionCurrentSdkClaimAuthorization(
        string skillName,
        string? skillSource,
        ISkillRegistryGenerationLease generationLease)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        ArgumentNullException.ThrowIfNull(generationLease);
        SkillName = skillName;
        SkillSource = skillSource;
        this.generationLease = generationLease;
    }

    internal string SkillName { get; }

    internal string? SkillSource { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            generationLease.Dispose();
        }
    }
}

internal enum SkillProjectionSdkClaimProofOutcome
{
    Proved,
    Busy,
    Unavailable
}

// The sanitized result of proving one snapshot's SDK claim: the exact producer tuple plus the
// closed name/source facts, and nothing that identifies a registry file, path, or table.
internal sealed record SkillProjectionSdkClaimProofResult(
    SkillProjectionSdkClaimProofOutcome Outcome,
    SkillRegistryProducerTuple? Tuple,
    string? SkillName,
    string? SkillSource)
{
    internal static readonly SkillProjectionSdkClaimProofResult Busy =
        new(SkillProjectionSdkClaimProofOutcome.Busy, null, null, null);

    internal static readonly SkillProjectionSdkClaimProofResult Unavailable =
        new(SkillProjectionSdkClaimProofOutcome.Unavailable, null, null, null);

    internal static SkillProjectionSdkClaimProofResult ForProved(
        SkillRegistryProducerTuple tuple,
        string skillName,
        string? skillSource) =>
        new(SkillProjectionSdkClaimProofOutcome.Proved, tuple, skillName, skillSource);
}

internal sealed record SkillProjectionCurrentSdkClaimAuthorizationResult(
    SkillRegistryCurrentAuthorizationOutcome Outcome,
    SkillProjectionCurrentSdkClaimAuthorization? Authorization)
{
    internal static readonly SkillProjectionCurrentSdkClaimAuthorizationResult NotCurrent =
        new(SkillRegistryCurrentAuthorizationOutcome.NotCurrent, null);

    internal static readonly SkillProjectionCurrentSdkClaimAuthorizationResult Busy =
        new(SkillRegistryCurrentAuthorizationOutcome.Busy, null);

    internal static readonly SkillProjectionCurrentSdkClaimAuthorizationResult Unavailable =
        new(SkillRegistryCurrentAuthorizationOutcome.Unavailable, null);

    internal static SkillProjectionCurrentSdkClaimAuthorizationResult ForAcquired(
        SkillProjectionCurrentSdkClaimAuthorization authorization) =>
        new(SkillRegistryCurrentAuthorizationOutcome.Acquired, authorization);
}

using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

public enum SkillInvocationV2PayloadState
{
    Available,
    Malformed,
    Missing,
    Binary,
    Oversized
}

public enum SkillInvocationV2PayloadReason
{
    None,
    DuplicateProperty,
    UnknownProperty,
    InvalidFieldType,
    NameInvalid,
    PathInvalid,
    NameMissing,
    BodyMissing,
    DefinitionPathMissing,
    BodyUnicodeInvalid,
    PathUnicodeInvalid,
    BodyOversized,
    PathOversized
}

public interface ISkillInvocationV2RuntimeCapability
{
}

public sealed class SkillInvocationV2RawPayloadEvidence
{
    private readonly byte[] payloadUtf8;
    private readonly byte[] payloadSha256;

    public SkillInvocationV2RawPayloadEvidence(ReadOnlySpan<byte> payloadUtf8)
    {
        if (payloadUtf8.IsEmpty)
        {
            throw new ArgumentException("Raw payload evidence must contain one JSON value.", nameof(payloadUtf8));
        }

        this.payloadUtf8 = payloadUtf8.ToArray();
        payloadSha256 = SHA256.HashData(this.payloadUtf8);
    }

    public int PayloadByteLength => payloadUtf8.Length;

    public ReadOnlyMemory<byte> PayloadUtf8 => payloadUtf8.ToArray();

    public ReadOnlyMemory<byte> PayloadSha256 => payloadSha256.ToArray();
}

public sealed class SkillInvocationV2AcceptedEnvelope
{
    public SkillInvocationV2AcceptedEnvelope(
        SkillInvocationV2RawPayloadEvidence rawPayloadEvidence,
        SkillInvocationV2PayloadState payloadState,
        SkillInvocationV2PayloadReason payloadReason)
    {
        ArgumentNullException.ThrowIfNull(rawPayloadEvidence);

        if (payloadState == SkillInvocationV2PayloadState.Available != (payloadReason == SkillInvocationV2PayloadReason.None))
        {
            throw new ArgumentException("Only an available payload has the none reason.", nameof(payloadReason));
        }

        RawPayloadEvidence = rawPayloadEvidence;
        PayloadState = payloadState;
        PayloadReason = payloadReason;
    }

    public SkillInvocationV2RawPayloadEvidence RawPayloadEvidence { get; }

    public SkillInvocationV2PayloadState PayloadState { get; }

    public SkillInvocationV2PayloadReason PayloadReason { get; }
}

public sealed class ParsedSkillInvocationV2Batch
{
    private readonly ReadOnlyCollection<SkillInvocationV2AcceptedEnvelope> acceptedEnvelopes;

    public ParsedSkillInvocationV2Batch(
        IEnumerable<SkillInvocationV2AcceptedEnvelope> acceptedEnvelopes,
        ISkillInvocationV2RuntimeCapability runtimeCapability)
    {
        ArgumentNullException.ThrowIfNull(acceptedEnvelopes);
        ArgumentNullException.ThrowIfNull(runtimeCapability);

        var ownedEnvelopes = acceptedEnvelopes.ToArray();
        if (ownedEnvelopes.Any(envelope => envelope is null))
        {
            throw new ArgumentException("Accepted envelopes cannot contain null.", nameof(acceptedEnvelopes));
        }

        this.acceptedEnvelopes = Array.AsReadOnly(ownedEnvelopes);
        RuntimeCapability = runtimeCapability;
    }

    public IReadOnlyList<SkillInvocationV2AcceptedEnvelope> AcceptedEnvelopes => acceptedEnvelopes;

    public ISkillInvocationV2RuntimeCapability RuntimeCapability { get; }
}

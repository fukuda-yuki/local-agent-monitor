using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

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

    [JsonIgnore]
    public ReadOnlyMemory<byte> PayloadUtf8 => payloadUtf8.ToArray();

    [JsonIgnore]
    public ReadOnlyMemory<byte> PayloadSha256 => payloadSha256.ToArray();
}

public sealed class SkillInvocationV2TextEvidence
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly byte[] utf8;
    private readonly byte[] sha256;

    public SkillInvocationV2TextEvidence(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
        utf8 = EncodeStrict(text, nameof(text));
        sha256 = SHA256.HashData(utf8);
    }

    public string Text { get; }

    public int Utf8ByteLength => utf8.Length;

    [JsonIgnore]
    public ReadOnlyMemory<byte> Utf8 => utf8.ToArray();

    [JsonIgnore]
    public ReadOnlyMemory<byte> Sha256 => sha256.ToArray();

    internal static void AssertWellFormedUtf16(string value, string parameterName) => _ = EncodeStrict(value, parameterName);

    private static byte[] EncodeStrict(string value, string parameterName)
    {
        try
        {
            return StrictUtf8.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("Text evidence must be well-formed UTF-16.", parameterName, exception);
        }
    }
}

public sealed class SkillInvocationV2ParsedClaimFacts
{
    public SkillInvocationV2ParsedClaimFacts(
        string name,
        string? source,
        string? trigger,
        SkillInvocationV2TextEvidence body,
        SkillInvocationV2TextEvidence definitionPath)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentException("Available claim facts require a name.", nameof(name));
        }

        SkillInvocationV2TextEvidence.AssertWellFormedUtf16(name, nameof(name));
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(definitionPath);

        Name = name;
        Source = source;
        Trigger = trigger;
        Body = body;
        DefinitionPath = definitionPath;
    }

    public string Name { get; }

    public string? Source { get; }

    public string? Trigger { get; }

    public SkillInvocationV2TextEvidence Body { get; }

    public SkillInvocationV2TextEvidence DefinitionPath { get; }
}

public sealed class SkillInvocationV2AcceptedEnvelope
{
    public SkillInvocationV2AcceptedEnvelope(
        SkillInvocationV2RawPayloadEvidence rawPayloadEvidence,
        SkillInvocationV2PayloadState payloadState,
        SkillInvocationV2PayloadReason payloadReason,
        SkillInvocationV2ParsedClaimFacts? claimFacts)
    {
        ArgumentNullException.ThrowIfNull(rawPayloadEvidence);

        if (!IsReasonForState(payloadState, payloadReason))
        {
            throw new ArgumentException("Payload state and reason must form an exact Gate 6 pair.", nameof(payloadReason));
        }

        if (payloadState == SkillInvocationV2PayloadState.Available != (claimFacts is not null))
        {
            throw new ArgumentException("Only an available payload has claim facts.", nameof(claimFacts));
        }

        RawPayloadEvidence = rawPayloadEvidence;
        PayloadState = payloadState;
        PayloadReason = payloadReason;
        ClaimFacts = claimFacts;
    }

    public SkillInvocationV2RawPayloadEvidence RawPayloadEvidence { get; }

    public SkillInvocationV2PayloadState PayloadState { get; }

    public SkillInvocationV2PayloadReason PayloadReason { get; }

    public SkillInvocationV2ParsedClaimFacts? ClaimFacts { get; }

    public string? Name => ClaimFacts?.Name;

    public string? Source => ClaimFacts?.Source;

    public string? Trigger => ClaimFacts?.Trigger;

    public SkillInvocationV2TextEvidence? Body => ClaimFacts?.Body;

    public SkillInvocationV2TextEvidence? DefinitionPath => ClaimFacts?.DefinitionPath;

    private static bool IsReasonForState(SkillInvocationV2PayloadState state, SkillInvocationV2PayloadReason reason) =>
        (state, reason) switch
        {
            (SkillInvocationV2PayloadState.Available, SkillInvocationV2PayloadReason.None) => true,
            (SkillInvocationV2PayloadState.Malformed, SkillInvocationV2PayloadReason.DuplicateProperty or SkillInvocationV2PayloadReason.UnknownProperty or SkillInvocationV2PayloadReason.InvalidFieldType or SkillInvocationV2PayloadReason.NameInvalid or SkillInvocationV2PayloadReason.PathInvalid) => true,
            (SkillInvocationV2PayloadState.Missing, SkillInvocationV2PayloadReason.NameMissing or SkillInvocationV2PayloadReason.BodyMissing or SkillInvocationV2PayloadReason.DefinitionPathMissing) => true,
            (SkillInvocationV2PayloadState.Binary, SkillInvocationV2PayloadReason.BodyUnicodeInvalid or SkillInvocationV2PayloadReason.PathUnicodeInvalid) => true,
            (SkillInvocationV2PayloadState.Oversized, SkillInvocationV2PayloadReason.BodyOversized or SkillInvocationV2PayloadReason.PathOversized) => true,
            _ => false
        };
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

    [JsonIgnore]
    public IReadOnlyList<SkillInvocationV2AcceptedEnvelope> AcceptedEnvelopes => acceptedEnvelopes;

    [JsonIgnore]
    public ISkillInvocationV2RuntimeCapability RuntimeCapability { get; }

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public override string ToString() => $"{nameof(ParsedSkillInvocationV2Batch)} {{ AcceptedEnvelopeCount = {acceptedEnvelopes.Count} }}";
}

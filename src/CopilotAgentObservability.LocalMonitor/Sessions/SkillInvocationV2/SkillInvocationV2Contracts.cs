using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal interface ISkillInvocationV2RuntimeCapability
{
    CertifiedSkillProducerIdentityV1 CertifiedIdentity { get; }
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

public sealed class SkillInvocationV2EventIdentity
{
    public SkillInvocationV2EventIdentity(
        string sourceEventId,
        string? sourceParentEventId,
        DateTimeOffset occurredAt,
        string? runNativeId,
        bool sourceEphemeral,
        string? traceId,
        string? spanId)
    {
        if (string.IsNullOrEmpty(sourceEventId))
        {
            throw new ArgumentException("Event identity requires an admitted source event id.", nameof(sourceEventId));
        }

        // r0001 structurally admits only a null trace_id/span_id token; a nonnull value here
        // means the caller bypassed the parser's admission gate.
        if (traceId is not null)
        {
            throw new ArgumentException("The r0001 wire never admits a nonnull producer trace id.", nameof(traceId));
        }

        if (spanId is not null)
        {
            throw new ArgumentException("The r0001 wire never admits a nonnull producer span id.", nameof(spanId));
        }

        SourceEventId = sourceEventId;
        SourceParentEventId = sourceParentEventId;
        OccurredAt = occurredAt;
        RunNativeId = runNativeId;
        SourceEphemeral = sourceEphemeral;
        TraceId = traceId;
        SpanId = spanId;
    }

    public string SourceEventId { get; }

    public string? SourceParentEventId { get; }

    public DateTimeOffset OccurredAt { get; }

    public string? RunNativeId { get; }

    public bool SourceEphemeral { get; }

    public string? TraceId { get; }

    public string? SpanId { get; }
}

public sealed class SkillInvocationV2AcceptedEnvelope
{
    public SkillInvocationV2AcceptedEnvelope(
        SkillInvocationV2RawPayloadEvidence rawPayloadEvidence,
        SkillInvocationPayloadState payloadState,
        SkillInvocationPayloadReason payloadReason,
        SkillInvocationV2ParsedClaimFacts? claimFacts,
        SkillInvocationV2EventIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(rawPayloadEvidence);
        ArgumentNullException.ThrowIfNull(identity);

        if (!IsReasonForState(payloadState, payloadReason))
        {
            throw new ArgumentException("Payload state and reason must form an exact Gate 6 pair.", nameof(payloadReason));
        }

        if (payloadState == SkillInvocationPayloadState.Available != (claimFacts is not null))
        {
            throw new ArgumentException("Only an available payload has claim facts.", nameof(claimFacts));
        }

        RawPayloadEvidence = rawPayloadEvidence;
        PayloadState = payloadState;
        PayloadReason = payloadReason;
        ClaimFacts = claimFacts;
        Identity = identity;
    }

    public SkillInvocationV2RawPayloadEvidence RawPayloadEvidence { get; }

    public SkillInvocationPayloadState PayloadState { get; }

    public SkillInvocationPayloadReason PayloadReason { get; }

    public SkillInvocationV2ParsedClaimFacts? ClaimFacts { get; }

    [JsonIgnore]
    public SkillInvocationV2EventIdentity Identity { get; }

    public string? Name => ClaimFacts?.Name;

    public string? Source => ClaimFacts?.Source;

    public string? Trigger => ClaimFacts?.Trigger;

    public SkillInvocationV2TextEvidence? Body => ClaimFacts?.Body;

    public SkillInvocationV2TextEvidence? DefinitionPath => ClaimFacts?.DefinitionPath;

    private static bool IsReasonForState(SkillInvocationPayloadState state, SkillInvocationPayloadReason reason) =>
        (state, reason) switch
        {
            (SkillInvocationPayloadState.Available, SkillInvocationPayloadReason.None) => true,
            (SkillInvocationPayloadState.Malformed, SkillInvocationPayloadReason.DuplicateProperty or SkillInvocationPayloadReason.UnknownProperty or SkillInvocationPayloadReason.InvalidFieldType or SkillInvocationPayloadReason.NameInvalid or SkillInvocationPayloadReason.PathInvalid) => true,
            (SkillInvocationPayloadState.Missing, SkillInvocationPayloadReason.NameMissing or SkillInvocationPayloadReason.BodyMissing or SkillInvocationPayloadReason.DefinitionPathMissing) => true,
            (SkillInvocationPayloadState.Binary, SkillInvocationPayloadReason.BodyUnicodeInvalid or SkillInvocationPayloadReason.PathUnicodeInvalid) => true,
            (SkillInvocationPayloadState.Oversized, SkillInvocationPayloadReason.BodyOversized or SkillInvocationPayloadReason.PathOversized) => true,
            _ => false
        };
}

internal sealed class ParsedSkillInvocationV2Batch
{
    private readonly ReadOnlyCollection<SkillInvocationV2AcceptedEnvelope> acceptedEnvelopes;

    public ParsedSkillInvocationV2Batch(
        IEnumerable<SkillInvocationV2AcceptedEnvelope> acceptedEnvelopes,
        ISkillInvocationV2RuntimeCapability runtimeCapability,
        CertifiedSkillProducerIdentityV1 certifiedIdentity,
        string nativeSessionId)
    {
        ArgumentNullException.ThrowIfNull(acceptedEnvelopes);
        ArgumentNullException.ThrowIfNull(runtimeCapability);
        ArgumentNullException.ThrowIfNull(certifiedIdentity);
        ArgumentException.ThrowIfNullOrEmpty(nativeSessionId);

        var ownedEnvelopes = acceptedEnvelopes.ToArray();
        if (ownedEnvelopes.Any(envelope => envelope is null))
        {
            throw new ArgumentException("Accepted envelopes cannot contain null.", nameof(acceptedEnvelopes));
        }

        this.acceptedEnvelopes = Array.AsReadOnly(ownedEnvelopes);
        RuntimeCapability = runtimeCapability;
        CertifiedIdentity = certifiedIdentity;
        NativeSessionId = nativeSessionId;
    }

    [JsonIgnore]
    public IReadOnlyList<SkillInvocationV2AcceptedEnvelope> AcceptedEnvelopes => acceptedEnvelopes;

    [JsonIgnore]
    public ISkillInvocationV2RuntimeCapability RuntimeCapability { get; }

    [JsonIgnore]
    public CertifiedSkillProducerIdentityV1 CertifiedIdentity { get; }

    [JsonIgnore]
    public string NativeSessionId { get; }

    public override bool Equals(object? obj) => ReferenceEquals(this, obj);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public override string ToString() => $"{nameof(ParsedSkillInvocationV2Batch)} {{ AcceptedEnvelopeCount = {acceptedEnvelopes.Count} }}";
}

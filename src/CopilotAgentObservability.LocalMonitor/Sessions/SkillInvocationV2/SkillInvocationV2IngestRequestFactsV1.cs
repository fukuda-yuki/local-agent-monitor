using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.SkillInvocationSnapshot;

namespace CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;

internal sealed record SkillInvocationV2IngestRequestFactsV1(
    string NativeSessionId,
    SkillInvocationV2EventIdentity Identity,
    SkillInvocationPayloadState PayloadState,
    SkillInvocationPayloadReason PayloadReason,
    SkillInvocationV2ParsedClaimFacts? ClaimFacts,
    ReadOnlyMemory<byte> PayloadTokenUtf8,
    string StateToken,
    string ReasonToken,
    string PayloadSha256,
    ulong PayloadBytes,
    string ContentDocumentSha256,
    SkillRegistryProducerTuple ProducerTuple,
    string RequestFingerprintSha256)
{
    internal static SkillInvocationV2IngestRequestFactsV1 Derive(ParsedSkillInvocationV2Batch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.AcceptedEnvelopes.Count != 1)
        {
            throw new ArgumentException("Request facts require exactly one accepted envelope.", nameof(batch));
        }

        var envelope = batch.AcceptedEnvelopes[0];
        var payloadTokenUtf8 = envelope.RawPayloadEvidence.PayloadUtf8;
        var payloadSha256 = SkillInvocationSnapshotContentDocumentV1.PayloadSha256(payloadTokenUtf8.Span);
        var contentDocument = SkillInvocationSnapshotContentDocumentV1.Build(payloadTokenUtf8.Span);
        var contentDocumentSha256 = SkillInvocationSnapshotContentDocumentV1.ContentDocumentSha256(contentDocument);
        var stateToken = SkillInvocationPayloadTokensV1.StateToken(envelope.PayloadState);
        var reasonToken = SkillInvocationPayloadTokensV1.ReasonToken(envelope.PayloadReason);
        var certifiedIdentity = batch.CertifiedIdentity;
        var producerTuple = new SkillRegistryProducerTuple(
            certifiedIdentity.SourceApplicationVersion,
            certifiedIdentity.AdapterVersion,
            certifiedIdentity.NormalizationVersion,
            certifiedIdentity.PayloadSchema,
            certifiedIdentity.SchemaFingerprint);
        var identity = envelope.Identity;
        var claimFacts = envelope.ClaimFacts;
        var payloadBytes = checked((ulong)payloadTokenUtf8.Length);
        var fingerprintInput = new SkillInvocationSnapshotReceiptFingerprintInput(
            SkillInvocationV2Parser.SourceAdapter,
            identity.SourceEventId,
            SkillInvocationV2Parser.SourceSurface,
            batch.NativeSessionId,
            identity.RunNativeId,
            identity.SourceParentEventId,
            identity.SourceEphemeral,
            identity.TraceId,
            identity.SpanId,
            identity.OccurredAt,
            certifiedIdentity.SourceApplicationVersion,
            certifiedIdentity.AdapterVersion,
            certifiedIdentity.NormalizationVersion,
            certifiedIdentity.PayloadSchema,
            certifiedIdentity.SchemaFingerprint,
            payloadSha256,
            payloadBytes,
            stateToken,
            reasonToken,
            claimFacts?.Name,
            claimFacts?.Source,
            claimFacts?.Trigger,
            Hex(claimFacts?.Body.Sha256),
            (ulong?)claimFacts?.Body.Utf8ByteLength,
            Hex(claimFacts?.DefinitionPath.Sha256),
            (ulong?)claimFacts?.DefinitionPath.Utf8ByteLength,
            contentDocumentSha256);

        return new SkillInvocationV2IngestRequestFactsV1(
            batch.NativeSessionId,
            identity,
            envelope.PayloadState,
            envelope.PayloadReason,
            claimFacts,
            payloadTokenUtf8,
            stateToken,
            reasonToken,
            payloadSha256,
            payloadBytes,
            contentDocumentSha256,
            producerTuple,
            SkillInvocationSnapshotReceiptFingerprint.Compute(fingerprintInput));
    }

    private static string? Hex(ReadOnlyMemory<byte>? value) =>
        value is null ? null : Convert.ToHexString(value.Value.Span).ToLowerInvariant();
}

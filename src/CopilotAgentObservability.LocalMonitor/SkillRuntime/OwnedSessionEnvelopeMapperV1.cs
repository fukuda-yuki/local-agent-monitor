using System.Globalization;
using System.Text.Json;
using CopilotAgentObservability.LocalMonitor.Sessions;
using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal static class OwnedSessionEnvelopeMapperV1
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static SessionIngestEnvelope? TryMap(
        string nativeSessionId,
        CertifiedSkillProducerIdentityV1 identity,
        SessionStartEvent sourceEvent) =>
        TryMap(nativeSessionId, identity, sourceEvent, "session.start", sourceEvent.Data);

    internal static SessionIngestEnvelope? TryMap(
        string nativeSessionId,
        CertifiedSkillProducerIdentityV1 identity,
        SessionTaskCompleteEvent sourceEvent) =>
        TryMap(nativeSessionId, identity, sourceEvent, "session.task_complete", sourceEvent.Data);

    private static SessionIngestEnvelope? TryMap<TData>(
        string nativeSessionId,
        CertifiedSkillProducerIdentityV1 identity,
        SessionEvent sourceEvent,
        string expectedType,
        TData? data)
    {
        if (string.IsNullOrEmpty(nativeSessionId) || !IsUuidV4(sourceEvent.Id)
            || sourceEvent.ParentId is { } parentId && !IsUuidV4(parentId)
            || sourceEvent.Timestamp == default || data is null
            || !string.Equals(sourceEvent.Type, expectedType, StringComparison.Ordinal)) return null;
        var envelope = new SessionIngestEnvelope(
            1,
            "copilot-sdk-stream",
            "copilot-sdk",
            nativeSessionId,
            [new SessionIngestEvent(
                sourceEvent.Id.ToString("D").ToLowerInvariant(),
                expectedType,
                sourceEvent.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture),
                JsonSerializer.SerializeToElement(data, WebJson),
                sourceEvent.ParentId?.ToString("D").ToLowerInvariant(),
                sourceEvent.AgentId,
                null)],
            null,
            identity.SourceApplicationVersion,
            identity.AdapterVersion,
            identity.SchemaFingerprint,
            identity.NormalizationVersion);
        return SessionIngestValidation.IsValid(envelope) ? envelope : null;
    }

    private static bool IsUuidV4(Guid value) => value != Guid.Empty
        && value.ToByteArray()[7] >> 4 == 4;
}

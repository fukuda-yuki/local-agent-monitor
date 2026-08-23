using CopilotAgentObservability.LocalMonitor.Sessions;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class OwnedSessionPostCompletionImporterV1(
    SkillRuntimeCapabilityBridgeV1 bridge,
    SessionEventQueue sessionEventQueue,
    TimeSpan commitTimeout)
{
    public async Task<bool> ImportAsync(
        CopilotRuntimeGenerationV1 candidate,
        OwnedSessionPreparedImportV1 prepared,
        CancellationToken cancellationToken)
    {
        if (prepared.Bodies.Count == 0) return true;

        for (var index = 0; index < prepared.Bodies.Count; index++)
        {
            var body = prepared.Bodies[index];
            if (body.Ordinal != index || body.Length != body.BodyUtf8.Length
                || body.Sha256.Length != 32
                || !CryptographicOperations.FixedTimeEquals(body.Sha256.Span, SHA256.HashData(body.BodyUtf8.Span)))
                return false;
            if (await bridge.ForwardPreparedBodyAsync(candidate, body.BodyUtf8, cancellationToken).ConfigureAwait(false)
                != SkillRuntimeBridgeForwardOutcome.Forwarded)
                return false;
        }

        SessionIngestEnvelope? startEnvelope;
        SessionIngestEnvelope? terminalEnvelope;
        try
        {
            startEnvelope = JsonSerializer.Deserialize<SessionIngestEnvelope>(prepared.StartEnvelopeUtf8.Span,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            terminalEnvelope = JsonSerializer.Deserialize<SessionIngestEnvelope>(prepared.TerminalEnvelopeUtf8.Span,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return false;
        }
        return startEnvelope is not null && terminalEnvelope is not null
            && await CommitAsync(startEnvelope, cancellationToken).ConfigureAwait(false)
            && await CommitAsync(terminalEnvelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> CommitAsync(SessionIngestEnvelope envelope, CancellationToken cancellationToken)
    {
        if (!SessionIngestValidation.IsValid(envelope)
            || !sessionEventQueue.TryEnqueue(envelope, out var request)) return false;
        try
        {
            return await request.Completion.WaitAsync(commitTimeout, cancellationToken).ConfigureAwait(false)
                == SessionEventCommitStatus.Committed;
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            if (!request.TryAbandon())
                await request.Completion.ConfigureAwait(false);
            return false;
        }
    }
}

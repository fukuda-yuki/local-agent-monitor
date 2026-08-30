using CopilotAgentObservability.LocalMonitor.Sessions;
using System.Security.Cryptography;
using System.Text.Json;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal enum OwnedSessionPostFreezeOutcomeV1
{
    Success,
    CandidateNotAdmitted,
    PreparedBodyRejected,
    CandidateLostDuringFirstV2,
    FirstV2ForwardUnavailable,
    CandidateLostDuringLaterV2,
    LaterV2ForwardUnavailable,
    StartEnvelopeRejected,
    TerminalEnvelopeRejected,
    StartValidationRejected,
    StartQueueRefused,
    StartCommitBusy,
    StartCommitFailed,
    StartCommitTimeout,
    StartCommitCanceled,
    TerminalValidationRejected,
    TerminalQueueRefused,
    TerminalCommitBusy,
    TerminalCommitFailed,
    TerminalCommitTimeout,
    TerminalCommitCanceled,
    UnexpectedImportException,
}

internal static class OwnedSessionPostFreezeOutcomeObservationV1
{
    internal static void Notify(Action<OwnedSessionPostFreezeOutcomeV1>? observer, OwnedSessionPostFreezeOutcomeV1 outcome)
    {
        if (observer is null) return;
        try { observer(outcome); }
        catch { }
    }

    internal static string Wire(OwnedSessionPostFreezeOutcomeV1 outcome) => outcome switch
    {
        OwnedSessionPostFreezeOutcomeV1.Success => "none",
        OwnedSessionPostFreezeOutcomeV1.CandidateNotAdmitted => "candidate_not_admitted",
        OwnedSessionPostFreezeOutcomeV1.PreparedBodyRejected => "prepared_body_rejected",
        OwnedSessionPostFreezeOutcomeV1.CandidateLostDuringFirstV2 => "candidate_lost_during_first_v2",
        OwnedSessionPostFreezeOutcomeV1.FirstV2ForwardUnavailable => "first_v2_forward_unavailable",
        OwnedSessionPostFreezeOutcomeV1.CandidateLostDuringLaterV2 => "candidate_lost_during_later_v2",
        OwnedSessionPostFreezeOutcomeV1.LaterV2ForwardUnavailable => "later_v2_forward_unavailable",
        OwnedSessionPostFreezeOutcomeV1.StartEnvelopeRejected => "start_envelope_rejected",
        OwnedSessionPostFreezeOutcomeV1.TerminalEnvelopeRejected => "terminal_envelope_rejected",
        OwnedSessionPostFreezeOutcomeV1.StartValidationRejected => "start_validation_rejected",
        OwnedSessionPostFreezeOutcomeV1.StartQueueRefused => "start_queue_refused",
        OwnedSessionPostFreezeOutcomeV1.StartCommitBusy => "start_commit_busy",
        OwnedSessionPostFreezeOutcomeV1.StartCommitFailed => "start_commit_failed",
        OwnedSessionPostFreezeOutcomeV1.StartCommitTimeout => "start_commit_timeout",
        OwnedSessionPostFreezeOutcomeV1.StartCommitCanceled => "start_commit_canceled",
        OwnedSessionPostFreezeOutcomeV1.TerminalValidationRejected => "terminal_validation_rejected",
        OwnedSessionPostFreezeOutcomeV1.TerminalQueueRefused => "terminal_queue_refused",
        OwnedSessionPostFreezeOutcomeV1.TerminalCommitBusy => "terminal_commit_busy",
        OwnedSessionPostFreezeOutcomeV1.TerminalCommitFailed => "terminal_commit_failed",
        OwnedSessionPostFreezeOutcomeV1.TerminalCommitTimeout => "terminal_commit_timeout",
        OwnedSessionPostFreezeOutcomeV1.TerminalCommitCanceled => "terminal_commit_canceled",
        OwnedSessionPostFreezeOutcomeV1.UnexpectedImportException => "unexpected_import_exception",
        _ => throw new InvalidOperationException("post_freeze_outcome"),
    };
}

internal sealed class OwnedSessionPostCompletionImporterV1(
    SkillRuntimeCapabilityBridgeV1 bridge,
    SessionEventQueue sessionEventQueue,
    TimeSpan commitTimeout,
    TimeProvider timeProvider)
{
    public async Task<OwnedSessionPostFreezeOutcomeV1> ImportAsync(
        CopilotRuntimeGenerationV1 candidate,
        OwnedSessionPreparedImportV1 prepared,
        CancellationToken cancellationToken)
    {
        if (prepared.Bodies.Count == 0) return OwnedSessionPostFreezeOutcomeV1.Success;

        for (var index = 0; index < prepared.Bodies.Count; index++)
        {
            var body = prepared.Bodies[index];
            if (body.Ordinal != index || body.Length != body.BodyUtf8.Length
                || body.Sha256.Length != 32
                || !CryptographicOperations.FixedTimeEquals(body.Sha256.Span, SHA256.HashData(body.BodyUtf8.Span)))
                return OwnedSessionPostFreezeOutcomeV1.PreparedBodyRejected;
            if (await bridge.ForwardPreparedBodyAsync(candidate, body.BodyUtf8, cancellationToken).ConfigureAwait(false)
                != SkillRuntimeBridgeForwardOutcome.Forwarded)
                return candidate.IsAdmitted
                    ? index == 0 ? OwnedSessionPostFreezeOutcomeV1.FirstV2ForwardUnavailable : OwnedSessionPostFreezeOutcomeV1.LaterV2ForwardUnavailable
                    : index == 0 ? OwnedSessionPostFreezeOutcomeV1.CandidateLostDuringFirstV2 : OwnedSessionPostFreezeOutcomeV1.CandidateLostDuringLaterV2;
        }

        var startEnvelope = Deserialize(prepared.StartEnvelopeUtf8);
        if (startEnvelope is null) return OwnedSessionPostFreezeOutcomeV1.StartEnvelopeRejected;
        var terminalEnvelope = Deserialize(prepared.TerminalEnvelopeUtf8);
        if (terminalEnvelope is null) return OwnedSessionPostFreezeOutcomeV1.TerminalEnvelopeRejected;
        var start = await CommitAsync(startEnvelope, cancellationToken, true).ConfigureAwait(false);
        return start == OwnedSessionPostFreezeOutcomeV1.Success
            ? await CommitAsync(terminalEnvelope, cancellationToken, false).ConfigureAwait(false)
            : start;
    }

    private static SessionIngestEnvelope? Deserialize(ReadOnlyMemory<byte> envelope)
    {
        try { return JsonSerializer.Deserialize<SessionIngestEnvelope>(envelope.Span, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
    }

    private async Task<OwnedSessionPostFreezeOutcomeV1> CommitAsync(SessionIngestEnvelope envelope, CancellationToken cancellationToken, bool start)
    {
        if (!SessionIngestValidation.IsValid(envelope))
            return start ? OwnedSessionPostFreezeOutcomeV1.StartValidationRejected : OwnedSessionPostFreezeOutcomeV1.TerminalValidationRejected;
        if (!sessionEventQueue.TryEnqueue(envelope, out var request))
            return start ? OwnedSessionPostFreezeOutcomeV1.StartQueueRefused : OwnedSessionPostFreezeOutcomeV1.TerminalQueueRefused;
        try
        {
            return await request.Completion.WaitAsync(commitTimeout, timeProvider, cancellationToken).ConfigureAwait(false) switch
            {
                SessionEventCommitStatus.Committed => OwnedSessionPostFreezeOutcomeV1.Success,
                SessionEventCommitStatus.Busy => start ? OwnedSessionPostFreezeOutcomeV1.StartCommitBusy : OwnedSessionPostFreezeOutcomeV1.TerminalCommitBusy,
                _ => start ? OwnedSessionPostFreezeOutcomeV1.StartCommitFailed : OwnedSessionPostFreezeOutcomeV1.TerminalCommitFailed,
            };
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            var canceled = exception is OperationCanceledException;
            if (!request.TryAbandon())
                await request.Completion.ConfigureAwait(false);
            return (start, canceled) switch
            {
                (true, true) => OwnedSessionPostFreezeOutcomeV1.StartCommitCanceled,
                (true, false) => OwnedSessionPostFreezeOutcomeV1.StartCommitTimeout,
                (false, true) => OwnedSessionPostFreezeOutcomeV1.TerminalCommitCanceled,
                _ => OwnedSessionPostFreezeOutcomeV1.TerminalCommitTimeout,
            };
        }
    }
}

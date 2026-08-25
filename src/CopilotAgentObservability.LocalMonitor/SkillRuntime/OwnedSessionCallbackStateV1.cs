using GitHub.Copilot;
using CopilotAgentObservability.LocalMonitor.Analysis;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class OwnedSessionEnvelopeContractExceptionV1 : Exception { }

internal sealed class OwnedSessionCallbackStateV1
{
    private readonly object sync = new();
    private readonly OwnedSessionPreparedBufferV1 buffer = new();
    private readonly OwnedSessionFrozenSkillInventoryV1 inventory;
    private readonly IOwnedSessionSkillProofProviderV1 proofProvider;
    private readonly string sourceVersion;
    private readonly Func<SessionStartEvent, byte[]> prepareStart;
    private readonly Func<SkillInvokedEvent, string, byte[]> prepareInvocation;
    private readonly Func<SessionTaskCompleteEvent, byte[]> prepareTerminal;
    private readonly CancellationToken workToken;
    private readonly Action? onPoison;
    private readonly Action<OwnedSessionDiagnosticEventV1>? diagnosticObserver;
    private string? callbackSessionId;
    private string? createdSessionId;
    private bool terminal;
    private bool poisoned;
    private bool closed;

    internal OwnedSessionCallbackStateV1(
        OwnedSessionFrozenSkillInventoryV1 inventory,
        IOwnedSessionSkillProofProviderV1 proofProvider,
        string sourceVersion,
        Func<SessionStartEvent, byte[]> prepareStart,
        Func<SkillInvokedEvent, string, byte[]> prepareInvocation,
        Func<SessionTaskCompleteEvent, byte[]> prepareTerminal,
        CancellationToken workToken = default,
        Action? onPoison = null,
        Action<OwnedSessionDiagnosticEventV1>? diagnosticObserver = null)
    {
        this.inventory = inventory;
        this.proofProvider = proofProvider;
        this.sourceVersion = sourceVersion;
        this.prepareStart = prepareStart;
        this.prepareInvocation = prepareInvocation;
        this.prepareTerminal = prepareTerminal;
        this.workToken = workToken;
        this.onPoison = onPoison;
        this.diagnosticObserver = diagnosticObserver;
    }

    internal bool IsPoisoned { get { lock (sync) return poisoned; } }

    internal void OnEvent(SessionEvent sourceEvent)
    {
        lock (sync)
        {
            if (closed)
            {
                if (sourceEvent is SessionStartEvent or SkillInvokedEvent or SessionTaskCompleteEvent
                    or SessionErrorEvent or ModelCallFailureEvent or AbortEvent)
                    PoisonUnderLock(OwnedSessionDiagnosticEventV1.ClosedRelevantEvent);
                return;
            }
            try
            {
                if (poisoned) return;
                if (workToken.IsCancellationRequested) { PoisonUnderLock(OwnedSessionDiagnosticEventV1.WorkTokenPreCanceled); return; }
                switch (sourceEvent)
                {
                    case SessionStartEvent start:
                        if (terminal) { PoisonUnderLock(OwnedSessionDiagnosticEventV1.ClosedRelevantEvent); break; }
                        AcceptStart(start);
                        break;
                    case SkillInvokedEvent invocation:
                        if (terminal) { PoisonUnderLock(OwnedSessionDiagnosticEventV1.ClosedRelevantEvent); break; }
                        AcceptInvocation(invocation);
                        break;
                    case SessionTaskCompleteEvent completed:
                        if (terminal) { PoisonUnderLock(OwnedSessionDiagnosticEventV1.ClosedRelevantEvent); break; }
                        AcceptTerminal(completed);
                        break;
                    case SessionErrorEvent: PoisonUnderLock(OwnedSessionDiagnosticEventV1.SessionError); break;
                    case ModelCallFailureEvent: PoisonUnderLock(OwnedSessionDiagnosticEventV1.ModelCallFailure); break;
                    case AbortEvent: PoisonUnderLock(OwnedSessionDiagnosticEventV1.Abort);
                        break;
                }
            }
            catch
            {
                PoisonUnderLock(OwnedSessionDiagnosticEventV1.CallbackException);
            }
        }
    }

    internal bool TryBindCreatedSession(string sessionId)
    {
        lock (sync)
        {
            if (poisoned) return false;
            if (workToken.IsCancellationRequested)
            {
                PoisonUnderLock(OwnedSessionDiagnosticEventV1.WorkTokenPreCanceled);
                return false;
            }
            if (string.IsNullOrEmpty(sessionId) || createdSessionId is not null
                || !string.Equals(callbackSessionId, sessionId, StringComparison.Ordinal))
            {
                PoisonUnderLock(OwnedSessionDiagnosticEventV1.SessionBindingContract);
                return false;
            }
            createdSessionId = sessionId;
            return true;
        }
    }

    internal OwnedSessionPreparedImportV1? TryFreeze()
    {
        lock (sync)
        {
            closed = true;
            if (poisoned) return null;
            if (workToken.IsCancellationRequested)
            {
                PoisonUnderLock(OwnedSessionDiagnosticEventV1.WorkTokenPreCanceled);
                return null;
            }
            if (createdSessionId is null || !terminal)
            {
                PoisonUnderLock(OwnedSessionDiagnosticEventV1.TerminalContract);
                return null;
            }
            var prepared = buffer.TryFreeze(createdSessionId, sourceVersion);
            if (prepared is null) PoisonUnderLock(OwnedSessionDiagnosticEventV1.TerminalContract);
            return prepared;
        }
    }

    internal void Poison()
    {
        lock (sync) PoisonUnderLock(OwnedSessionDiagnosticEventV1.CallbackException);
    }

    private void AcceptStart(SessionStartEvent start)
    {
        if (callbackSessionId is not null || start.Data is null || string.IsNullOrEmpty(start.Data.SessionId)
            || !string.Equals(start.Data.CopilotVersion, sourceVersion, StringComparison.Ordinal))
        {
            PoisonUnderLock(OwnedSessionDiagnosticEventV1.SessionStartContract);
            return;
        }
        byte[] prepared;
        try { prepared = prepareStart(start); }
        catch (OwnedSessionEnvelopeContractExceptionV1) { PoisonUnderLock(OwnedSessionDiagnosticEventV1.SessionStartContract); return; }
        catch { PoisonUnderLock(OwnedSessionDiagnosticEventV1.CallbackException); return; }
        callbackSessionId = start.Data.SessionId;
        buffer.AcceptStart(callbackSessionId, sourceVersion, prepared);
    }

    private void AcceptInvocation(SkillInvokedEvent invocation)
    {
        if (callbackSessionId is null || invocation.Data is null
            || !inventory.Retained.TryGetValue(invocation.Data.Name, out var retained)
            || !string.Equals(invocation.Data.Source, retained.Descriptor.Source, StringComparison.Ordinal)
            || !string.Equals(invocation.Data.Path, retained.Descriptor.Path, StringComparison.Ordinal))
        {
            PoisonUnderLock(OwnedSessionDiagnosticEventV1.InvocationIdentity);
            return;
        }
        if (!string.Equals(invocation.Data.Description, retained.Descriptor.Description, StringComparison.Ordinal))
        { PoisonUnderLock(OwnedSessionDiagnosticEventV1.InvocationDescription); return; }
        if (!IsWellFormedRequired(invocation.Data.Content))
        { PoisonUnderLock(OwnedSessionDiagnosticEventV1.InvocationContent); return; }
        OwnedSessionSkillProofV1? currentProof;
        try
        {
            if (!proofProvider.TryProve(retained.Descriptor, inventory.RetainedRoots ?? [], out currentProof)
                || currentProof is null || currentProof != retained.Proof)
            { PoisonUnderLock(OwnedSessionDiagnosticEventV1.InvocationNativeReproof); return; }
        }
        catch { PoisonUnderLock(OwnedSessionDiagnosticEventV1.InvocationNativeReproof); return; }
        byte[] prepared;
        try { prepared = prepareInvocation(invocation, currentProof.Content); }
        catch { PoisonUnderLock(OwnedSessionDiagnosticEventV1.InvocationPreparation); return; }
        if (!buffer.TryAcceptInvocation(callbackSessionId, prepared))
            PoisonUnderLock(OwnedSessionDiagnosticEventV1.InvocationBuffer);
    }

    private static bool IsWellFormedRequired(string? value)
    {
        if (value is null) return false;
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (++index >= value.Length || !char.IsLowSurrogate(value[index])) return false;
            }
            else if (char.IsLowSurrogate(value[index])) return false;
        }
        return true;
    }

    private void AcceptTerminal(SessionTaskCompleteEvent completed)
    {
        if (callbackSessionId is null || completed.Data?.Success != true)
        {
            PoisonUnderLock(OwnedSessionDiagnosticEventV1.TerminalContract);
            return;
        }
        byte[] prepared;
        try { prepared = prepareTerminal(completed); }
        catch (OwnedSessionEnvelopeContractExceptionV1) { PoisonUnderLock(OwnedSessionDiagnosticEventV1.TerminalContract); return; }
        catch { PoisonUnderLock(OwnedSessionDiagnosticEventV1.CallbackException); return; }
        buffer.AcceptSuccessfulTerminal(callbackSessionId, prepared);
        terminal = true;
    }

    private void PoisonUnderLock(OwnedSessionDiagnosticEventV1 reason)
    {
        if (poisoned) return;
        poisoned = true;
        OwnedSessionDiagnosticObservationV1.Notify(diagnosticObserver, reason);
        buffer.Poison();
        onPoison?.Invoke();
    }
}

using GitHub.Copilot;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal sealed class OwnedSessionCallbackStateV1
{
    private readonly object sync = new();
    private readonly OwnedSessionPreparedBufferV1 buffer = new();
    private readonly OwnedSessionFrozenSkillInventoryV1 inventory;
    private readonly IOwnedSessionSkillProofProviderV1 proofProvider;
    private readonly string sourceVersion;
    private readonly Func<SessionStartEvent, byte[]> prepareStart;
    private readonly Func<SkillInvokedEvent, byte[]> prepareInvocation;
    private readonly Func<SessionTaskCompleteEvent, byte[]> prepareTerminal;
    private readonly CancellationToken workToken;
    private readonly Action? onPoison;
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
        Func<SkillInvokedEvent, byte[]> prepareInvocation,
        Func<SessionTaskCompleteEvent, byte[]> prepareTerminal,
        CancellationToken workToken = default,
        Action? onPoison = null)
    {
        this.inventory = inventory;
        this.proofProvider = proofProvider;
        this.sourceVersion = sourceVersion;
        this.prepareStart = prepareStart;
        this.prepareInvocation = prepareInvocation;
        this.prepareTerminal = prepareTerminal;
        this.workToken = workToken;
        this.onPoison = onPoison;
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
                    PoisonUnderLock();
                return;
            }
            try
            {
                if (poisoned || workToken.IsCancellationRequested) { PoisonUnderLock(); return; }
                switch (sourceEvent)
                {
                    case SessionStartEvent start:
                        if (terminal) { PoisonUnderLock(); break; }
                        AcceptStart(start);
                        break;
                    case SkillInvokedEvent invocation:
                        if (terminal) { PoisonUnderLock(); break; }
                        AcceptInvocation(invocation);
                        break;
                    case SessionTaskCompleteEvent completed:
                        if (terminal) { PoisonUnderLock(); break; }
                        AcceptTerminal(completed);
                        break;
                    case SessionErrorEvent:
                    case ModelCallFailureEvent:
                    case AbortEvent:
                        PoisonUnderLock();
                        break;
                }
            }
            catch
            {
                PoisonUnderLock();
            }
        }
    }

    internal bool TryBindCreatedSession(string sessionId)
    {
        lock (sync)
        {
            if (poisoned || workToken.IsCancellationRequested || string.IsNullOrEmpty(sessionId)
                || createdSessionId is not null || !string.Equals(callbackSessionId, sessionId, StringComparison.Ordinal))
            {
                PoisonUnderLock();
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
            if (poisoned || workToken.IsCancellationRequested || createdSessionId is null || !terminal)
            {
                PoisonUnderLock();
                return null;
            }
            return buffer.TryFreeze(createdSessionId, sourceVersion);
        }
    }

    internal void Poison()
    {
        lock (sync) PoisonUnderLock();
    }

    private void AcceptStart(SessionStartEvent start)
    {
        if (callbackSessionId is not null || start.Data is null || string.IsNullOrEmpty(start.Data.SessionId)
            || !string.Equals(start.Data.CopilotVersion, sourceVersion, StringComparison.Ordinal))
        {
            PoisonUnderLock();
            return;
        }
        var prepared = prepareStart(start);
        callbackSessionId = start.Data.SessionId;
        buffer.AcceptStart(callbackSessionId, sourceVersion, prepared);
    }

    private void AcceptInvocation(SkillInvokedEvent invocation)
    {
        if (callbackSessionId is null || invocation.Data is null
            || !inventory.Retained.TryGetValue(invocation.Data.Name, out var retained)
            || !string.Equals(invocation.Data.Source, retained.Descriptor.Source, StringComparison.Ordinal)
            || !string.Equals(invocation.Data.Path, retained.Descriptor.Path, StringComparison.Ordinal)
            || !string.Equals(invocation.Data.Description, retained.Descriptor.Description, StringComparison.Ordinal)
            || !string.Equals(invocation.Data.Content, retained.Proof.Content, StringComparison.Ordinal)
            || !proofProvider.TryProve(retained.Descriptor, inventory.RetainedRoots ?? [], out var currentProof)
            || currentProof != retained.Proof
            || !buffer.TryAcceptInvocation(callbackSessionId, prepareInvocation(invocation)))
        {
            PoisonUnderLock();
        }
    }

    private void AcceptTerminal(SessionTaskCompleteEvent completed)
    {
        if (callbackSessionId is null || completed.Data?.Success != true)
        {
            PoisonUnderLock();
            return;
        }
        buffer.AcceptSuccessfulTerminal(callbackSessionId, prepareTerminal(completed));
        terminal = true;
    }

    private void PoisonUnderLock()
    {
        if (poisoned) return;
        poisoned = true;
        buffer.Poison();
        onPoison?.Invoke();
    }
}

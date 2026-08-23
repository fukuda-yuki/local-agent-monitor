using System.Diagnostics.CodeAnalysis;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal enum CopilotRuntimeAcquisitionDispositionV1
{
    Acquired,
    MissingOrMismatched,
    NormalShutdownClosed
}

internal sealed class CopilotRuntimeAdmissionV1
{
    private readonly SkillHostShutdownGateV1 shutdownGate;
    private readonly object sync = new();
    private readonly List<Action> invalidationObservers = [];
    private CopilotRuntimeGenerationV1? currentGeneration;
    private GenerationDisposalGuard? currentGenerationDisposal;
    private Task? shutdownDrain;
    private bool shutdownClosed;

    internal CopilotRuntimeAdmissionV1(SkillHostShutdownGateV1 shutdownGate)
    {
        ArgumentNullException.ThrowIfNull(shutdownGate);
        this.shutdownGate = shutdownGate;
    }

    public bool IsShutdownClosed
    {
        get
        {
            lock (sync)
            {
                return shutdownClosed;
            }
        }
    }

    public void RegisterInvalidationObserver(Action observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (sync)
        {
            invalidationObservers.Add(observer);
        }
    }

    internal bool IsNormalShutdownStarted => shutdownGate.IsNormalShutdownStarted;

    public bool TryGetCurrentAdmittedGeneration([NotNullWhen(true)] out CopilotRuntimeGenerationV1? generation)
    {
        lock (sync)
        {
            generation = currentGeneration is { IsAdmitted: true } ? currentGeneration : null;
            return generation is not null;
        }
    }

    public bool TryAcquireCurrentFileCapability(
        CancellationToken callerToken,
        [NotNullWhen(true)] out CopilotRuntimeOperationCapabilityV1? capability) =>
        AcquireCurrentFileCapability(callerToken, out capability)
            == CopilotRuntimeAcquisitionDispositionV1.Acquired;

    // Normal shutdown closure and a missing/mismatched generation are two different current-file
    // dispositions -- the first is the cleanup-and-no-response abort, the second is the fixed
    // discovery-unavailable 503 -- so they must be told apart inside the same lock. Reading
    // IsShutdownClosed and then acquiring would race the closure between the two observations.
    public CopilotRuntimeAcquisitionDispositionV1 AcquireCurrentFileCapability(
        CancellationToken callerToken,
        [NotNullWhen(true)] out CopilotRuntimeOperationCapabilityV1? capability)
    {
        lock (sync)
        {
            capability = null;
            if (shutdownGate.IsNormalShutdownStarted || shutdownClosed)
            {
                return CopilotRuntimeAcquisitionDispositionV1.NormalShutdownClosed;
            }

            if (currentGeneration is not { IsAdmitted: true }
                || !currentGeneration.TryAcquireOperationCapability(callerToken, out capability))
            {
                return CopilotRuntimeAcquisitionDispositionV1.MissingOrMismatched;
            }

            return CopilotRuntimeAcquisitionDispositionV1.Acquired;
        }
    }

    public CopilotRuntimeGenerationV1? PublishAdmittedGeneration(
        ICopilotSkillRuntimeClient client,
        out CopilotRuntimeGenerationV1? replacedGeneration)
    {
        ArgumentNullException.ThrowIfNull(client);
        var generation = new CopilotRuntimeGenerationV1(client, shutdownGate);
        var generationDisposal = new GenerationDisposalGuard();
        GenerationDisposalGuard? replacedDisposal;
        lock (sync)
        {
            replacedGeneration = null;
            if (shutdownGate.IsNormalShutdownStarted || shutdownClosed)
            {
                // Publication does not take ownership on refusal; the caller that supplied the
                // client remains responsible for disposing it.
                return null;
            }

            replacedGeneration = currentGeneration;
            replacedDisposal = currentGenerationDisposal;
            currentGeneration = generation;
            currentGenerationDisposal = generationDisposal;
        }

        replacedGeneration?.Invalidate();
        if (replacedGeneration is not null && !replacedDisposal!.TryClaim())
        {
            replacedGeneration = null;
        }

        NotifyInvalidationObservers();
        return generation;
    }

    public CopilotRuntimeGenerationV1? InvalidateCurrentGeneration()
    {
        CopilotRuntimeGenerationV1? removed;
        GenerationDisposalGuard? removedDisposal;
        lock (sync)
        {
            removed = currentGeneration;
            removedDisposal = currentGenerationDisposal;
            currentGeneration = null;
            currentGenerationDisposal = null;
        }

        removed?.Invalidate();
        if (removed is not null)
        {
            NotifyInvalidationObservers();
        }

        return removed is not null && removedDisposal!.TryClaim() ? removed : null;
    }

    public CopilotRuntimeGenerationV1? InvalidateGenerationIfCurrent(CopilotRuntimeGenerationV1 generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        CopilotRuntimeGenerationV1? removed;
        GenerationDisposalGuard? removedDisposal;
        lock (sync)
        {
            removed = null;
            removedDisposal = null;
            if (ReferenceEquals(currentGeneration, generation))
            {
                removed = currentGeneration;
                removedDisposal = currentGenerationDisposal;
                currentGeneration = null;
                currentGenerationDisposal = null;
            }
        }

        removed?.Invalidate();
        if (removed is not null)
        {
            NotifyInvalidationObservers();
        }

        return removed is not null && removedDisposal!.TryClaim() ? removed : null;
    }

    public void CloseForShutdown()
    {
        CopilotRuntimeGenerationV1? generation;
        lock (sync)
        {
            shutdownClosed = true;
            generation = currentGeneration;
        }

        generation?.CloseAdmissionForDrain();
    }

    public async Task CloseForShutdownAndDrainAsync(CancellationToken cancellationToken)
    {
        Task drain;
        lock (sync)
        {
            shutdownClosed = true;
            if (shutdownDrain is null)
            {
                currentGeneration?.CloseAdmissionForDrain();
                shutdownDrain = currentGeneration is null
                    ? Task.CompletedTask
                    : DrainAndDisposeClientAsync(currentGeneration, currentGenerationDisposal!);
            }

            drain = shutdownDrain;
        }

        await drain.ConfigureAwait(false);
    }

    private static async Task DrainAndDisposeClientAsync(
        CopilotRuntimeGenerationV1 generation,
        GenerationDisposalGuard disposal)
    {
        await generation.WaitForDrainAsync(CancellationToken.None).ConfigureAwait(false);
        if (!disposal.TryClaim())
        {
            return;
        }

        try
        {
            await generation.Client.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Runtime details remain unavailable when best-effort shutdown disposal fails.
        }
    }

    private void NotifyInvalidationObservers()
    {
        Action[] observers;
        lock (sync)
        {
            observers = [.. invalidationObservers];
        }

        foreach (var observer in observers)
        {
            observer();
        }
    }

    private sealed class GenerationDisposalGuard
    {
        private int claimed;

        internal bool TryClaim() => Interlocked.Exchange(ref claimed, 1) == 0;
    }
}

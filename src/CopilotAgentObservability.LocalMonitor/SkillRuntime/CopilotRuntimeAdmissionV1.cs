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
    private readonly object sync = new();
    private readonly List<Action> invalidationObservers = [];
    private CopilotRuntimeGenerationV1? currentGeneration;
    private bool shutdownClosed;

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
            if (shutdownClosed)
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
        var generation = new CopilotRuntimeGenerationV1(client);
        lock (sync)
        {
            replacedGeneration = null;
            if (shutdownClosed)
            {
                return null;
            }

            replacedGeneration = currentGeneration;
            currentGeneration = generation;
        }

        replacedGeneration?.Invalidate();
        NotifyInvalidationObservers();
        return generation;
    }

    public CopilotRuntimeGenerationV1? InvalidateCurrentGeneration()
    {
        CopilotRuntimeGenerationV1? removed;
        lock (sync)
        {
            removed = currentGeneration;
            currentGeneration = null;
        }

        removed?.Invalidate();
        if (removed is not null)
        {
            NotifyInvalidationObservers();
        }

        return removed;
    }

    public CopilotRuntimeGenerationV1? InvalidateGenerationIfCurrent(CopilotRuntimeGenerationV1 generation)
    {
        ArgumentNullException.ThrowIfNull(generation);
        CopilotRuntimeGenerationV1? removed;
        lock (sync)
        {
            removed = null;
            if (ReferenceEquals(currentGeneration, generation))
            {
                removed = currentGeneration;
                currentGeneration = null;
            }
        }

        removed?.Invalidate();
        if (removed is not null)
        {
            NotifyInvalidationObservers();
        }

        return removed;
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
}

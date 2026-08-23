using System.Diagnostics.CodeAnalysis;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.Analysis;

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
    private readonly SemaphoreSlim publicationGate = new(1, 1);
    private readonly List<Action<CopilotRuntimeGenerationV1>> candidateInvalidationObservers = [];
    private CopilotRuntimeGenerationV1? currentGeneration;
    private Task? shutdownDrain;
    private bool shutdownClosed;
    private readonly HashSet<CopilotRuntimeGenerationV1> unpublishedCandidates = [];

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

    public void RegisterInvalidationObserver(Action<CopilotRuntimeGenerationV1> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (sync) candidateInvalidationObservers.Add(observer);
    }

    internal bool IsNormalShutdownStarted => shutdownGate.IsNormalShutdownStarted;

    public CopilotRuntimeGenerationV1 CreateUnpublishedCandidate(
        ICopilotSkillRuntimeClient client,
        CertifiedSkillProducerIdentityV1 certifiedIdentity,
        IAnalysisSdkDirectoryScope analysisScope,
        IAsyncDisposable? analysisScopeOwner = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(analysisScope);
        var candidate = new CopilotRuntimeGenerationV1(client, shutdownGate, certifiedIdentity, analysisScopeOwner ?? analysisScope);
        lock (sync)
        {
            if (shutdownClosed || shutdownGate.IsNormalShutdownStarted)
            {
                _ = candidate.InvalidateAndCleanupAsync();
                return candidate;
            }
            unpublishedCandidates.Add(candidate);
        }

        _ = ObserveLeaseLossAsync(candidate, analysisScope.LeaseLostToken);
        return candidate;
    }

    public async Task<bool> PublishCandidateAsync(CopilotRuntimeGenerationV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await publicationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            CopilotRuntimeGenerationV1? replaced;
            var duplicate = false;
            var claimed = false;
            lock (sync)
            {
                duplicate = ReferenceEquals(currentGeneration, candidate);
                claimed = !shutdownClosed && !shutdownGate.IsNormalShutdownStarted
                    && !duplicate && unpublishedCandidates.Contains(candidate) && candidate.TryPublish();
                replaced = currentGeneration;
                if (claimed)
                {
                    unpublishedCandidates.Remove(candidate);
                    currentGeneration = candidate;
                }
            }

            if (!claimed)
            {
                if (duplicate) return false;
                await DiscardCandidateAsync(candidate).ConfigureAwait(false);
                return false;
            }

            if (replaced is not null)
            {
                replaced.Invalidate();
                NotifyInvalidationObservers(replaced);
                await replaced.InvalidateAndCleanupAsync().ConfigureAwait(false);
            }
            return true;
        }
        finally
        {
            publicationGate.Release();
        }
    }

    public async Task<bool> DiscardCandidateAsync(CopilotRuntimeGenerationV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (sync)
        {
            unpublishedCandidates.Remove(candidate);
            if (ReferenceEquals(currentGeneration, candidate))
            {
                currentGeneration = null;
            }
        }
        candidate.Invalidate();
        NotifyInvalidationObservers(candidate);
        await candidate.InvalidateAndCleanupAsync().ConfigureAwait(false);
        return true;
    }

    internal void InvalidateCandidate(CopilotRuntimeGenerationV1 candidate)
    {
        lock (sync)
        {
            unpublishedCandidates.Remove(candidate);
            if (ReferenceEquals(currentGeneration, candidate))
            {
                currentGeneration = null;
            }
        }
        candidate.Invalidate();
        NotifyInvalidationObservers(candidate);
        _ = candidate.InvalidateAndCleanupAsync();
    }

    private async Task ObserveLeaseLossAsync(CopilotRuntimeGenerationV1 candidate, CancellationToken leaseLostToken)
    {
        try { await Task.Delay(Timeout.InfiniteTimeSpan, leaseLostToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (leaseLostToken.IsCancellationRequested) { }
        lock (sync)
        {
            unpublishedCandidates.Remove(candidate);
            if (ReferenceEquals(currentGeneration, candidate))
            {
                currentGeneration = null;
            }
        }
        candidate.Invalidate();
        NotifyInvalidationObservers(candidate);
        await candidate.InvalidateAndCleanupAsync().ConfigureAwait(false);
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
                var candidates = unpublishedCandidates.ToList();
                unpublishedCandidates.Clear();
                if (currentGeneration is not null && !candidates.Contains(currentGeneration))
                    candidates.Insert(0, currentGeneration);
                currentGeneration = null;
                foreach (var candidate in candidates)
                {
                    candidate.Invalidate();
                    NotifyInvalidationObservers(candidate);
                }
                var candidateDrain = Task.WhenAll(candidates.Select(candidate => candidate.InvalidateAndCleanupAsync()));
                shutdownDrain = candidateDrain;
            }

            drain = shutdownDrain;
        }

        await drain.ConfigureAwait(false);
    }

    private void NotifyInvalidationObservers(CopilotRuntimeGenerationV1 generation)
    {
        Action<CopilotRuntimeGenerationV1>[] candidateObservers;
        lock (sync)
        {
            candidateObservers = [.. candidateInvalidationObservers];
        }
        foreach (var observer in candidateObservers)
        {
            try { observer(generation); }
            catch { }
        }
    }

}

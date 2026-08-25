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
    private CopilotRuntimeGenerationV1? reservedCandidate;
    private readonly Action? publicationReservedForTesting;
    private readonly Action? candidateRegistrationAttachedForTesting;
    private readonly Action? commitOwnerLockAcquiredForTesting;
    private readonly Action? authorityCancellationRequestedForTesting;
    private readonly Action? authorityCancellationObservedForTesting;
    private readonly Action? reservationDisposingRegistrationForTesting;
    private readonly Action? reservationRegistrationDisposedForTesting;
    private readonly Action? publicationAuthorityBindingForTesting;

    internal sealed class PublicationReservation : IAsyncDisposable
    {
        private readonly CopilotRuntimeAdmissionV1 owner;
        private readonly SemaphoreSlim lifecycleGate = new(1, 1);
        private readonly CancellationTokenRegistration cancellationRegistration;
        private ReservationState state;
        private int publicationGateReleased;

        internal PublicationReservation(
            CopilotRuntimeAdmissionV1 owner,
            CopilotRuntimeGenerationV1 candidate,
            CancellationTokenRegistration cancellationRegistration)
        {
            this.owner = owner;
            Candidate = candidate;
            this.cancellationRegistration = cancellationRegistration;
        }

        internal CopilotRuntimeGenerationV1 Candidate { get; }

        internal async Task CommitAsync()
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (state != ReservationState.Active) return;
                CopilotRuntimeGenerationV1? replaced;
                lock (owner.sync)
                {
                    owner.commitOwnerLockAcquiredForTesting?.Invoke();
                    Candidate.CommitReservedPublication();
                    owner.unpublishedCandidates.Remove(Candidate);
                    replaced = owner.currentGeneration;
                    owner.currentGeneration = Candidate;
                    state = ReservationState.Committed;
                }
                if (replaced is not null && !ReferenceEquals(replaced, Candidate))
                {
                    if (replaced.Invalidate()) owner.NotifyInvalidationObservers(replaced);
                    await replaced.InvalidateAndCleanupAsync().ConfigureAwait(false);
                }
            }
            finally { lifecycleGate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (state == ReservationState.Disposed) return;
                owner.reservationDisposingRegistrationForTesting?.Invoke();
                cancellationRegistration.Dispose();
                owner.reservationRegistrationDisposedForTesting?.Invoke();
                var publicationAuthorityReleased = Candidate.TryReleasePublicationAuthority();
                if (!publicationAuthorityReleased && state == ReservationState.Committed)
                    owner.InvalidateCandidate(Candidate);
                if (state == ReservationState.Active)
                {
                    await owner.DiscardCandidateWithoutGateAsync(Candidate).ConfigureAwait(false);
                }
                state = ReservationState.Disposed;
                lock (owner.sync)
                {
                    if (ReferenceEquals(owner.reservedCandidate, Candidate)) owner.reservedCandidate = null;
                }
                if (Interlocked.Exchange(ref publicationGateReleased, 1) == 0)
                    owner.publicationGate.Release();
            }
            finally { lifecycleGate.Release(); }
        }

        private enum ReservationState { Active, Committed, Disposed }
    }

    internal CopilotRuntimeAdmissionV1(
        SkillHostShutdownGateV1 shutdownGate,
        Action? publicationReservedForTesting = null,
        Action? candidateRegistrationAttachedForTesting = null,
        Action? commitOwnerLockAcquiredForTesting = null,
        Action? authorityCancellationRequestedForTesting = null,
        Action? authorityCancellationObservedForTesting = null,
        Action? reservationDisposingRegistrationForTesting = null,
        Action? reservationRegistrationDisposedForTesting = null,
        Action? publicationAuthorityBindingForTesting = null)
    {
        ArgumentNullException.ThrowIfNull(shutdownGate);
        this.shutdownGate = shutdownGate;
        this.publicationReservedForTesting = publicationReservedForTesting;
        this.candidateRegistrationAttachedForTesting = candidateRegistrationAttachedForTesting;
        this.commitOwnerLockAcquiredForTesting = commitOwnerLockAcquiredForTesting;
        this.authorityCancellationRequestedForTesting = authorityCancellationRequestedForTesting;
        this.authorityCancellationObservedForTesting = authorityCancellationObservedForTesting;
        this.reservationDisposingRegistrationForTesting = reservationDisposingRegistrationForTesting;
        this.reservationRegistrationDisposedForTesting = reservationRegistrationDisposedForTesting;
        this.publicationAuthorityBindingForTesting = publicationAuthorityBindingForTesting;
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
        var leaseLostToken = analysisScope.LeaseLostToken;
        var candidate = new CopilotRuntimeGenerationV1(
            client, shutdownGate, certifiedIdentity, analysisScopeOwner ?? analysisScope, leaseLostToken);
        candidate.AttachInvalidationRegistration(
            leaseLostToken.Register(() => HandleAuthorityCancellation(candidate)));
        candidateRegistrationAttachedForTesting?.Invoke();
        lock (sync)
        {
            if (shutdownClosed || shutdownGate.IsNormalShutdownStarted
                || leaseLostToken.IsCancellationRequested || candidate.IsInvalid)
            {
                _ = candidate.InvalidateAndCleanupAsync();
                return candidate;
            }
            unpublishedCandidates.Add(candidate);
        }
        return candidate;
    }

    public async Task<bool> PublishCandidateAsync(CopilotRuntimeGenerationV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await using var reservation = await TryReservePublicationAsync(candidate, CancellationToken.None).ConfigureAwait(false);
        if (reservation is null) return false;
        await reservation.CommitAsync().ConfigureAwait(false);
        return true;
    }

    internal async Task<PublicationReservation?> TryReservePublicationAsync(
        CopilotRuntimeGenerationV1 candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var cancellationRegistration = cancellationToken.Register(() => HandleAuthorityCancellation(candidate));
        await publicationGate.WaitAsync().ConfigureAwait(false);
        var duplicate = false;
        var reserved = false;
        try
        {
            lock (sync)
            {
                duplicate = ReferenceEquals(currentGeneration, candidate);
                if (!shutdownClosed && !shutdownGate.IsNormalShutdownStarted
                    && !duplicate && unpublishedCandidates.Contains(candidate)
                    && candidate.TryReservePublication())
                {
                    publicationReservedForTesting?.Invoke();
                    if (!shutdownClosed && !shutdownGate.IsNormalShutdownStarted
                        && !candidate.IsInvalid && !candidate.IsLeaseLossRequested
                        && !cancellationToken.IsCancellationRequested)
                    {
                        publicationAuthorityBindingForTesting?.Invoke();
                        if (candidate.TryBindPublicationAuthority(cancellationToken))
                        {
                            reserved = true;
                            reservedCandidate = candidate;
                            return new PublicationReservation(this, candidate, cancellationRegistration);
                        }
                    }
                }
            }
            if (!duplicate) await DiscardCandidateWithoutGateAsync(candidate).ConfigureAwait(false);
            cancellationRegistration.Dispose();
        }
        catch
        {
            cancellationRegistration.Dispose();
            throw;
        }
        finally
        {
            if (!reserved) publicationGate.Release();
        }
        return null;
    }

    private void HandleAuthorityCancellation(CopilotRuntimeGenerationV1 candidate)
    {
        authorityCancellationRequestedForTesting?.Invoke();
        candidate.CloseAdmissionForDrain();
        authorityCancellationObservedForTesting?.Invoke();
        InvalidateCandidate(candidate);
    }

    private async Task DiscardCandidateWithoutGateAsync(CopilotRuntimeGenerationV1 candidate)
    {
        lock (sync) unpublishedCandidates.Remove(candidate);
        if (candidate.Invalidate()) NotifyInvalidationObservers(candidate);
        await candidate.InvalidateAndCleanupAsync().ConfigureAwait(false);
    }

    public async Task<bool> DiscardCandidateAsync(CopilotRuntimeGenerationV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        await publicationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await RemoveInvalidateAndCleanupAsync(candidate).ConfigureAwait(false);
            return true;
        }
        finally { publicationGate.Release(); }
    }

    internal void InvalidateCandidate(CopilotRuntimeGenerationV1 candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        lock (sync)
        {
            if (ReferenceEquals(reservedCandidate, candidate))
            {
                candidate.CloseAdmissionForDrain();
                _ = InvalidateCandidateSerializedAsync(candidate);
                return;
            }
            unpublishedCandidates.Remove(candidate);
            if (ReferenceEquals(currentGeneration, candidate)) currentGeneration = null;
        }
        if (candidate.Invalidate()) NotifyInvalidationObservers(candidate);
        _ = candidate.InvalidateAndCleanupAsync();
    }

    private async Task InvalidateCandidateSerializedAsync(CopilotRuntimeGenerationV1 candidate)
    {
        await publicationGate.WaitAsync().ConfigureAwait(false);
        try { await RemoveInvalidateAndCleanupAsync(candidate).ConfigureAwait(false); }
        finally { publicationGate.Release(); }
    }

    private async Task RemoveInvalidateAndCleanupAsync(CopilotRuntimeGenerationV1 candidate)
    {
        lock (sync)
        {
            unpublishedCandidates.Remove(candidate);
            if (ReferenceEquals(currentGeneration, candidate)) currentGeneration = null;
        }
        if (candidate.Invalidate()) NotifyInvalidationObservers(candidate);
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
        lock (sync) shutdownClosed = true;
        await publicationGate.WaitAsync().ConfigureAwait(false);
        Task drain;
        try
        {
            lock (sync)
            {
                if (shutdownDrain is null)
                {
                    var current = currentGeneration;
                    var candidates = unpublishedCandidates.Where(candidate => !ReferenceEquals(candidate, current)).ToList();
                    unpublishedCandidates.Clear();
                    currentGeneration = null;
                    foreach (var candidate in candidates)
                    {
                        if (candidate.Invalidate()) NotifyInvalidationObservers(candidate);
                    }
                    var currentDrain = current?.CloseForShutdownAndCleanupAsync() ?? Task.CompletedTask;
                    var candidateDrain = Task.WhenAll(candidates.Select(candidate => candidate.InvalidateAndCleanupAsync()));
                    shutdownDrain = Task.WhenAll(currentDrain, candidateDrain);
                }

                drain = shutdownDrain;
            }
        }
        finally { publicationGate.Release(); }
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

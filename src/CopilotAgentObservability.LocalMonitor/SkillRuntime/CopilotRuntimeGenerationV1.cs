using System.Diagnostics.CodeAnalysis;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.Analysis;

namespace CopilotAgentObservability.LocalMonitor.SkillRuntime;

internal enum SkillRuntimeTerminalSealV1
{
    V2NonCommitResponse,
    Commit,
    ReplaySuccess,
    Response
}

internal sealed class CopilotRuntimeOperationCapabilityV1 : ISkillInvocationV2RuntimeCapability
{
    private const int StateActive = 0;
    private const int StateSealed = 1;
    private const int StateAbandoned = 2;

    private readonly object workCancellationSync = new();
    private readonly CancellationTokenSource linkedWorkCancellation;
    private readonly CancellationToken workToken;
    private int state = StateActive;
    private int releaseCalls;
    private bool workCancellationDisposed;

    internal CopilotRuntimeOperationCapabilityV1(
        CopilotRuntimeGenerationV1 owner,
        CancellationToken callerToken,
        CancellationToken leaseLostToken)
    {
        Owner = owner;
        Handle = Guid.NewGuid();
        linkedWorkCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, leaseLostToken);
        workToken = linkedWorkCancellation.Token;
    }

    public CopilotRuntimeGenerationV1 Owner { get; }

    internal Guid Handle { get; }

    public CancellationToken WorkToken => workToken;

    public CertifiedSkillProducerIdentityV1 CertifiedIdentity => Owner.CertifiedIdentity;

    internal SkillRuntimeTerminalSealV1? WonSealKind { get; private set; }

    public bool TrySealV2NonCommitResponse() => Owner.TrySealCapability(this, SkillRuntimeTerminalSealV1.V2NonCommitResponse);

    public bool TrySealCommit() => Owner.TrySealCapability(this, SkillRuntimeTerminalSealV1.Commit);

    public bool TrySealReplaySuccess() => Owner.TrySealCapability(this, SkillRuntimeTerminalSealV1.ReplaySuccess);

    public bool TrySealResponse() => Owner.TrySealCapability(this, SkillRuntimeTerminalSealV1.Response);

    public bool TryAbandonWonSeal() => Owner.TryAbandonSealedCapability(this);

    public void Release() => Owner.ReleaseCapability(this);

    internal bool TryMarkSealedUnderOwnerLock(SkillRuntimeTerminalSealV1 sealKind)
    {
        if (Interlocked.CompareExchange(ref state, StateSealed, StateActive) != StateActive)
        {
            return false;
        }

        WonSealKind = sealKind;
        return true;
    }

    internal bool TryMarkAbandonedUnderOwnerLock()
        => Interlocked.CompareExchange(ref state, StateAbandoned, StateSealed) == StateSealed;

    internal bool IsUnsealedUnderOwnerLock => Volatile.Read(ref state) == StateActive;

    internal void CancelWork()
    {
        lock (workCancellationSync)
        {
            if (!workCancellationDisposed && !linkedWorkCancellation.IsCancellationRequested)
            {
                linkedWorkCancellation.Cancel();
            }
        }
    }

    internal bool TryBeginRelease() => Interlocked.Exchange(ref releaseCalls, 1) == 0;

    internal void DisposeWorkCancellation()
    {
        lock (workCancellationSync)
        {
            if (workCancellationDisposed)
            {
                return;
            }

            workCancellationDisposed = true;
            linkedWorkCancellation.Dispose();
        }
    }
}

internal enum CopilotRuntimeCandidatePhaseV1 { Raw, Ready, Reserved, Published, Invalid }

internal sealed class CopilotRuntimeGenerationV1
{
    public const int AdmittedProtocolVersion = 3;

    private readonly SkillHostShutdownGateV1 shutdownGate;
    private readonly CancellationToken leaseLostToken;
    private readonly object sync = new();
    private readonly Dictionary<Guid, CopilotRuntimeOperationCapabilityV1> outstandingCapabilities = [];
    private readonly TaskCompletionSource drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool invalid;
    private bool admissionClosed;
    private CopilotRuntimeCandidatePhaseV1 phase;
    private readonly IAsyncDisposable analysisScope;
    private Task? cleanupTask;
    private readonly TaskCompletionSource cleanupCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenRegistration invalidationRegistration;
    private bool invalidationRegistrationAttached;
    private CancellationToken publicationAuthorityToken;
    private bool publicationAuthorityBound;

    internal CopilotRuntimeGenerationV1(
        ICopilotSkillRuntimeClient client,
        SkillHostShutdownGateV1 shutdownGate,
        CertifiedSkillProducerIdentityV1 certifiedIdentity,
        IAsyncDisposable analysisScope,
        CancellationToken leaseLostToken)
    {
        ArgumentNullException.ThrowIfNull(shutdownGate);
        this.shutdownGate = shutdownGate;
        Client = client;
        CertifiedIdentity = certifiedIdentity ?? throw new ArgumentNullException(nameof(certifiedIdentity));
        Identity = Guid.NewGuid();
        this.analysisScope = analysisScope ?? throw new ArgumentNullException(nameof(analysisScope));
        this.leaseLostToken = leaseLostToken;
        phase = CopilotRuntimeCandidatePhaseV1.Raw;
    }

    internal Task CleanupTask => cleanupCompleted.Task;

    internal Task InvalidateAndCleanupAsync()
    {
        Invalidate();
        CloseAdmissionForDrain();
        return EnsureCleanupStarted();
    }

    internal Task CloseForShutdownAndCleanupAsync()
    {
        CloseAdmissionForDrain();
        return EnsureCleanupStarted();
    }

    private Task EnsureCleanupStarted()
    {
        TaskCompletionSource? completion = null;
        Task task;
        lock (sync)
        {
            if (cleanupTask is not null) return cleanupTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            task = cleanupTask = completion.Task;
        }
        _ = CompleteCleanupAsync(completion);
        return task;
    }

    private async Task CompleteCleanupAsync(TaskCompletionSource completion)
    {
        try
        {
            await CleanupCoreAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception) { completion.TrySetException(exception); }
    }

    private async Task CleanupCoreAsync()
    {
        try
        {
            await WaitForDrainAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Client.DisposeAsync().ConfigureAwait(false); } catch { }
            try { await analysisScope.DisposeAsync().ConfigureAwait(false); } catch { }
            CancellationTokenRegistration registration = default;
            var disposeRegistration = false;
            lock (sync)
            {
                if (invalidationRegistrationAttached)
                {
                    registration = invalidationRegistration;
                    disposeRegistration = true;
                }
            }
            if (disposeRegistration) await registration.DisposeAsync().ConfigureAwait(false);
        }
        finally { cleanupCompleted.TrySetResult(); }
    }


    public ICopilotSkillRuntimeClient Client { get; }

    public CertifiedSkillProducerIdentityV1 CertifiedIdentity { get; }

    public string FrozenVersion => CertifiedIdentity.SourceApplicationVersion;

    public int FrozenProtocolVersion => CertifiedIdentity.ProtocolVersion;


    // Opaque on purpose: the identity is a same-process correlation key and is never emitted
    // in logs, metrics, responses, persistence, or fingerprints.
    internal Guid Identity { get; }

    public bool IsAdmitted
    {
        get
        {
            lock (sync)
            {
                return !invalid && !admissionClosed && !IsAuthorityCancellationRequestedUnderLock();
            }
        }
    }

    internal bool IsInvalid
    {
        get
        {
            lock (sync)
            {
                return invalid;
            }
        }
    }

    internal int OutstandingCapabilityCount
    {
        get
        {
            lock (sync)
            {
                return outstandingCapabilities.Count;
            }
        }
    }

    public bool TryAcquireOperationCapability(
        CancellationToken callerToken,
        [NotNullWhen(true)] out CopilotRuntimeOperationCapabilityV1? capability)
    {
        lock (sync)
        {
            capability = null;
            if (shutdownGate.IsNormalShutdownStarted || invalid || admissionClosed
                || IsAuthorityCancellationRequestedUnderLock()
                || phase is CopilotRuntimeCandidatePhaseV1.Ready or CopilotRuntimeCandidatePhaseV1.Reserved)
            {
                return false;
            }

            capability = new CopilotRuntimeOperationCapabilityV1(this, callerToken, leaseLostToken);
            outstandingCapabilities[capability.Handle] = capability;
            return true;
        }
    }

    internal bool TrySealCapability(CopilotRuntimeOperationCapabilityV1 capability, SkillRuntimeTerminalSealV1 sealKind)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (sync)
        {
            if (invalid || IsAuthorityCancellationRequestedUnderLock()
                || !ReferenceEquals(capability.Owner, this))
            {
                return false;
            }

            return capability.TryMarkSealedUnderOwnerLock(sealKind);
        }
    }

    internal bool TryAbandonSealedCapability(CopilotRuntimeOperationCapabilityV1 capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        lock (sync)
        {
            if (!ReferenceEquals(capability.Owner, this))
            {
                return false;
            }

            // Abandonment stays valid after invalidation: a seal won while the generation was
            // admitted may still be abandoned and released without output when its later
            // independent authorization fails.
            return capability.TryMarkAbandonedUnderOwnerLock();
        }
    }

    internal bool Invalidate()
    {
        CopilotRuntimeOperationCapabilityV1[] unsealed;
        lock (sync)
        {
            if (invalid)
            {
                return false;
            }

            invalid = true;
            phase = CopilotRuntimeCandidatePhaseV1.Invalid;
            admissionClosed = true;
            publicationAuthorityBound = false;
            publicationAuthorityToken = default;
            unsealed = [.. outstandingCapabilities.Values.Where(c => c.IsUnsealedUnderOwnerLock)];
        }

        foreach (var capability in unsealed)
        {
            capability.CancelWork();
        }
        return true;
    }

    internal void CloseAdmissionForDrain()
    {
        lock (sync)
        {
            admissionClosed = true;
            if (outstandingCapabilities.Count == 0)
            {
                drained.TrySetResult();
            }
        }
    }

    internal bool IsLeaseLossRequested => leaseLostToken.IsCancellationRequested;

    internal bool TryBindPublicationAuthority(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (invalid || admissionClosed || leaseLostToken.IsCancellationRequested
                || phase != CopilotRuntimeCandidatePhaseV1.Reserved || publicationAuthorityBound
                || cancellationToken.IsCancellationRequested)
            {
                return false;
            }
            publicationAuthorityToken = cancellationToken;
            publicationAuthorityBound = true;
            return true;
        }
    }

    internal bool TryReleasePublicationAuthority()
    {
        lock (sync)
        {
            if (!publicationAuthorityBound) return true;
            if (publicationAuthorityToken.IsCancellationRequested)
            {
                admissionClosed = true;
                return false;
            }
            publicationAuthorityBound = false;
            publicationAuthorityToken = default;
            return true;
        }
    }

    private bool IsAuthorityCancellationRequestedUnderLock() =>
        leaseLostToken.IsCancellationRequested
        || publicationAuthorityBound && publicationAuthorityToken.IsCancellationRequested;

    internal bool TryMarkReady()
    {
        lock (sync)
        {
            if (invalid || phase != CopilotRuntimeCandidatePhaseV1.Raw || outstandingCapabilities.Count != 0) return false;
            phase = CopilotRuntimeCandidatePhaseV1.Ready;
            return true;
        }
    }

    internal void CommitReservedPublication()
    {
        lock (sync)
        {
            phase = CopilotRuntimeCandidatePhaseV1.Published;
        }
    }

    internal bool TryReservePublication()
    {
        lock (sync)
        {
            if (invalid || phase != CopilotRuntimeCandidatePhaseV1.Ready || outstandingCapabilities.Count != 0) return false;
            phase = CopilotRuntimeCandidatePhaseV1.Reserved;
            return true;
        }
    }

    internal void AttachInvalidationRegistration(CancellationTokenRegistration registration)
    {
        var disposeRegistration = false;
        lock (sync)
        {
            if (cleanupTask is not null)
            {
                disposeRegistration = true;
            }
            else
            {
                invalidationRegistration = registration;
                invalidationRegistrationAttached = true;
            }
        }
        if (disposeRegistration) registration.Dispose();
    }

    internal Task WaitForDrainAsync(CancellationToken cancellationToken) =>
        drained.Task.WaitAsync(cancellationToken);

    internal void ReleaseCapability(CopilotRuntimeOperationCapabilityV1 capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        if (!capability.TryBeginRelease())
        {
            return;
        }

        lock (sync)
        {
            outstandingCapabilities.Remove(capability.Handle);
            if (admissionClosed && outstandingCapabilities.Count == 0)
            {
                drained.TrySetResult();
            }
        }

        capability.DisposeWorkCancellation();
    }
}

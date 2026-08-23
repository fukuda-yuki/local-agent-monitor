using System.Diagnostics.CodeAnalysis;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;

namespace CopilotAgentObservability.LocalMonitor.SkillNative;

// One held admission on the process generation's retained discovery roots. The revision and the
// canonical arrays are reachable only through a lease, because Gate 8 lets a request capture them
// only after it has won the root CAS. Releasing is idempotent so the request's failure, abort, and
// success paths can all release without counting how many of them ran.
internal sealed class SkillDiscoveryRootLeaseV1 : IDisposable
{
    private readonly SkillDiscoveryRootGenerationV1 generation;
    private int released;

    internal SkillDiscoveryRootLeaseV1(
        SkillDiscoveryRootGenerationV1 generation,
        DiscoveryRootSetV1 rootSet,
        IReadOnlyList<RetainedDiscoveryRootV1> retainedRoots)
    {
        this.generation = generation ?? throw new ArgumentNullException(nameof(generation));
        RootSet = rootSet ?? throw new ArgumentNullException(nameof(rootSet));
        RetainedRoots = retainedRoots ?? throw new ArgumentNullException(nameof(retainedRoots));
    }

    internal DiscoveryRootSetV1 RootSet { get; }

    internal IReadOnlyList<RetainedDiscoveryRootV1> RetainedRoots { get; }

    internal string Revision => RootSet.Revision;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref released, 1) == 0)
        {
            generation.ReleaseLease();
        }
    }
}

// The immutable per-process discovery-root generation produced by the Gate 8 startup preflight.
//
// V1 has no hot reload and no in-process replacement, so this type owns exactly one preflighted
// root set for the host's lifetime. Its only dynamic state is the atomic admission closure and the
// outstanding lease count that shutdown drains.
//
// Shutdown deliberately does not cancel a root lease. Closing admission stops new requests from
// entering, and requests that already hold a lease run to their ordinary terminal result; only
// after the last lease is released may the retained root handles be disposed. Disposing them while
// a request still held one would turn an in-flight native walk into a native failure that the
// request would have to report, which Gate 8 forbids.
internal sealed class SkillDiscoveryRootGenerationV1
{
    private readonly SkillDiscoveryRootPreflightResultV1 preflight;
    private readonly SkillHostShutdownGateV1 shutdownGate;
    private readonly object sync = new();
    private readonly TaskCompletionSource drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int outstandingLeases;
    private bool admissionClosed;
    private bool rootsDisposed;

    internal SkillDiscoveryRootGenerationV1(
        SkillDiscoveryRootPreflightResultV1 preflight,
        SkillHostShutdownGateV1 shutdownGate)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(shutdownGate);

        if (preflight.Outcome != SkillDiscoveryRootPreflightOutcomeV1.Certified || preflight.RootSet is null)
        {
            throw new ArgumentException(
                "Only a certified root preflight owns retained roots and can back a generation.",
                nameof(preflight));
        }

        this.preflight = preflight;
        this.shutdownGate = shutdownGate;
    }

    // The composition fact that selects the native reader. Unlike the revision and the canonical
    // arrays it is not per-request state, so it does not require a held lease.
    internal SkillProducerPathKeyPlatform Platform => preflight.RootSet!.Platform;

    internal bool IsAdmissionClosed
    {
        get
        {
            lock (sync)
            {
                return admissionClosed;
            }
        }
    }

    internal int OutstandingLeaseCount
    {
        get
        {
            lock (sync)
            {
                return outstandingLeases;
            }
        }
    }

    internal bool TryAcquireLease([NotNullWhen(true)] out SkillDiscoveryRootLeaseV1? lease)
    {
        lock (sync)
        {
            if (shutdownGate.IsNormalShutdownStarted || admissionClosed)
            {
                lease = null;
                return false;
            }

            outstandingLeases++;
            lease = new SkillDiscoveryRootLeaseV1(this, preflight.RootSet!, preflight.RetainedRoots);
            return true;
        }
    }

    // The atomic closure half of host shutdown. It is separate from the drain so the host can close
    // root and runtime admission together before waiting on either.
    internal void CloseAdmission()
    {
        var completeDrain = false;
        lock (sync)
        {
            admissionClosed = true;
            completeDrain = outstandingLeases == 0;
        }

        if (completeDrain)
        {
            drained.TrySetResult();
        }
    }

    internal async Task DrainAsync()
    {
        CloseAdmission();
        await drained.Task.ConfigureAwait(false);
    }

    private Task DisposeRootsAsync()
    {
        lock (sync)
        {
            if (rootsDisposed)
            {
                return Task.CompletedTask;
            }

            rootsDisposed = true;
        }

        preflight.Dispose();
        return Task.CompletedTask;
    }

    internal async Task DrainAndDisposeRootsAsync()
    {
        await DrainAsync().ConfigureAwait(false);
        await DisposeRootsAsync().ConfigureAwait(false);
    }

    internal void ReleaseLease()
    {
        var completeDrain = false;
        lock (sync)
        {
            outstandingLeases--;
            completeDrain = admissionClosed && outstandingLeases == 0;
        }

        if (completeDrain)
        {
            drained.TrySetResult();
        }
    }
}

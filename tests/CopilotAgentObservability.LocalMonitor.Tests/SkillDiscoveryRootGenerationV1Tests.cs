using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillDiscoveryRootGenerationV1Tests : IDisposable
{
    private readonly TempHandleSource handleSource = new();

    public void Dispose() => handleSource.Dispose();

    [Fact]
    public void OnlyACertifiedPreflightCanBackAGeneration()
    {
        var noRoots = SkillDiscoveryRootPreflightV1.Run([], [], certifiedPlatform: null);
        var unsupported = SkillDiscoveryRootPreflightV1.Run([ProjectRoot], [], certifiedPlatform: null);

        Assert.Throws<ArgumentException>(() => new SkillDiscoveryRootGenerationV1(noRoots));
        Assert.Throws<ArgumentException>(() => new SkillDiscoveryRootGenerationV1(unsupported));
    }

    [Fact]
    public void AnOpenGenerationGrantsALeaseThatCarriesTheRevisionAndBothArrays()
    {
        var generation = CreateGeneration(out var preflight);

        Assert.True(generation.TryAcquireLease(out var lease));
        using (lease)
        {
            Assert.Equal(preflight.RootSet!.Revision, lease!.Revision);
            Assert.Same(preflight.RootSet, lease.RootSet);
            Assert.Equal(preflight.RetainedRoots.Count, lease.RetainedRoots.Count);
            Assert.Equal(1, generation.OutstandingLeaseCount);
        }

        Assert.Equal(0, generation.OutstandingLeaseCount);
    }

    [Fact]
    public void ReleasingALeaseTwiceDecrementsTheCountOnce()
    {
        var generation = CreateGeneration(out _);

        Assert.True(generation.TryAcquireLease(out var first));
        Assert.True(generation.TryAcquireLease(out var second));
        Assert.Equal(2, generation.OutstandingLeaseCount);

        first!.Dispose();
        first.Dispose();

        Assert.Equal(1, generation.OutstandingLeaseCount);
        second!.Dispose();
        Assert.Equal(0, generation.OutstandingLeaseCount);
    }

    [Fact]
    public void ClosedAdmissionRefusesEveryNewLease()
    {
        var generation = CreateGeneration(out _);

        generation.CloseAdmission();

        Assert.True(generation.IsAdmissionClosed);
        Assert.False(generation.TryAcquireLease(out var lease));
        Assert.Null(lease);
        Assert.Equal(0, generation.OutstandingLeaseCount);
    }

    [Fact]
    public async Task ShutdownDoesNotDisposeRetainedRootsWhileALeaseIsStillHeld()
    {
        var generation = CreateGeneration(out var preflight);
        var roots = preflight.RetainedRoots.ToArray();

        Assert.True(generation.TryAcquireLease(out var lease));
        var drain = generation.DrainAndDisposeRootsAsync();

        Assert.True(generation.IsAdmissionClosed);
        Assert.False(drain.IsCompleted);
        Assert.All(roots, root => Assert.False(root.IsDisposed));

        lease!.Dispose();
        await drain;

        Assert.All(roots, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public async Task ShutdownWithNoOutstandingLeaseDisposesRetainedRootsImmediately()
    {
        var generation = CreateGeneration(out var preflight);
        var roots = preflight.RetainedRoots.ToArray();

        await generation.DrainAndDisposeRootsAsync();

        Assert.All(roots, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public async Task ARequestThatLosesToTheClosureAcquiresNothingAndTheDrainStillCompletes()
    {
        var generation = CreateGeneration(out var preflight);
        var roots = preflight.RetainedRoots.ToArray();

        Assert.True(generation.TryAcquireLease(out var winner));
        generation.CloseAdmission();

        Assert.False(generation.TryAcquireLease(out var loser));
        Assert.Null(loser);

        var drain = generation.DrainAndDisposeRootsAsync();
        Assert.False(drain.IsCompleted);

        winner!.Dispose();
        await drain;

        Assert.All(roots, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public async Task DrainingTwiceDisposesTheRetainedRootsExactlyOnce()
    {
        var generation = CreateGeneration(out var preflight);
        var roots = preflight.RetainedRoots.ToArray();

        await generation.DrainAndDisposeRootsAsync();
        await generation.DrainAndDisposeRootsAsync();

        Assert.All(roots, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public async Task ConcurrentLeasesAllDrainBeforeTheRootsAreDisposed()
    {
        var generation = CreateGeneration(out var preflight);
        var roots = preflight.RetainedRoots.ToArray();
        var leases = new List<SkillDiscoveryRootLeaseV1>();

        for (var index = 0; index < 8; index++)
        {
            Assert.True(generation.TryAcquireLease(out var lease));
            leases.Add(lease!);
        }

        var drain = generation.DrainAndDisposeRootsAsync();

        foreach (var lease in leases)
        {
            Assert.False(drain.IsCompleted);
            Assert.All(roots, root => Assert.False(root.IsDisposed));
            lease.Dispose();
        }

        await drain;
        Assert.All(roots, root => Assert.True(root.IsDisposed));
    }

    private const string ProjectRoot = @"C:\repo";
    private const string SkillRoot = @"C:\skills";

    private SkillDiscoveryRootGenerationV1 CreateGeneration(out SkillDiscoveryRootPreflightResultV1 preflight)
    {
        preflight = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRoot],
            [SkillRoot],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, new StubOpener(handleSource)));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.Certified, preflight.Outcome);
        return new SkillDiscoveryRootGenerationV1(preflight);
    }

    private sealed class StubOpener(TempHandleSource handleSource) : IDiscoveryRootOpenerV1
    {
        private int openCount;

        public DiscoveryRootOpenResultV1 TryOpenRetainedRoot(string configuredRootPath, DiscoveryRootKindV1 kind)
        {
            if (!SkillProducerPathKeyV1.TryParse(
                    configuredRootPath,
                    SkillProducerPathKeyPlatform.Windows,
                    out var pathKey,
                    out var reason))
            {
                throw new InvalidOperationException($"Test root path failed to parse ({reason}).");
            }

            var seed = (ulong)++openCount;
            var fileId = new byte[16];
            fileId[0] = (byte)seed;

            return DiscoveryRootOpenResultV1.Succeeded(new RetainedDiscoveryRootV1(
                kind,
                pathKey,
                DiscoveryRootNativeIdentityV1.CreateWindows(seed, fileId),
                handleSource.OpenHandle()));
        }

        public bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root) => !root.IsDisposed;
    }

    private sealed class TempHandleSource : IDisposable
    {
        private readonly string directoryPath =
            Path.Combine(Path.GetTempPath(), $"cao-rootgen-{Guid.NewGuid():N}");

        private readonly string filePath;

        public TempHandleSource()
        {
            Directory.CreateDirectory(directoryPath);
            filePath = Path.Combine(directoryPath, "handle-source.bin");
            File.WriteAllBytes(filePath, [1, 2, 3]);
        }

        public SafeFileHandle OpenHandle() => File.OpenHandle(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        public void Dispose()
        {
            try
            {
                Directory.Delete(directoryPath, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}

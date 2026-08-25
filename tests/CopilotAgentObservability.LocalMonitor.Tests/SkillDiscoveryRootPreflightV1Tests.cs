using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillDiscoveryRootPreflightV1Tests : IDisposable
{
    private readonly TempHandleSource handleSource = new();

    public void Dispose() => handleSource.Dispose();

    [Fact]
    public void ZeroConfiguredRoots_StartsHostWithoutRootSet()
    {
        var opener = new FakeOpener(handleSource);

        var result = SkillDiscoveryRootPreflightV1.Run(
            [],
            [],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.NoConfiguredRoots, result.Outcome);
        Assert.Null(result.AbortReason);
        Assert.Null(result.RootSet);
        Assert.Empty(result.RetainedRoots);
        Assert.Equal(0, opener.OpenCallCount);
    }

    [Fact]
    public void AbsentConfiguredRootLists_StartHostWithoutRootSet()
    {
        var result = SkillDiscoveryRootPreflightV1.Run(
            null,
            null,
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, new FakeOpener(handleSource)));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.NoConfiguredRoots, result.Outcome);
        Assert.Null(result.AbortReason);
    }

    [Fact]
    public void ZeroConfiguredRoots_OnUnsupportedPlatform_RemainsValidRouteAbsence()
    {
        var result = SkillDiscoveryRootPreflightV1.Run([], [], certifiedPlatform: null);

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.NoConfiguredRoots, result.Outcome);
        Assert.Null(result.AbortReason);
    }

    [Fact]
    public void ConfiguredRoots_OnUnsupportedPlatform_AbortWithPlatformUnsupported()
    {
        var result = SkillDiscoveryRootPreflightV1.Run([ProjectRootOne], [], certifiedPlatform: null);

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.PlatformUnsupported, result.Outcome);
        Assert.Equal("skill_discovery_platform_unsupported", result.AbortReason);
        Assert.Null(result.RootSet);
        Assert.Empty(result.RetainedRoots);
    }

    [Fact]
    public void UnsupportedPlatform_PrecedesEveryInvalidRootClass()
    {
        Assert.All(Enum.GetValues<DiscoveryRootOpenFailureV1>(), failure =>
        {
            var opener = new FakeOpener(handleSource);
            opener.FailAt(0, failure);

            var result = SkillDiscoveryRootPreflightV1.Run(
                [ProjectRootOne],
                [SkillRootAlpha],
                certifiedPlatform: null);

            Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.PlatformUnsupported, result.Outcome);
            Assert.Equal("skill_discovery_platform_unsupported", result.AbortReason);
            Assert.Equal(0, opener.OpenCallCount);
        });
    }

    [Fact]
    public void EveryInvalidRootClass_CollapsesToOneRootConfigurationInvalidReason()
    {
        Assert.All(Enum.GetValues<DiscoveryRootOpenFailureV1>(), failure =>
        {
            var opener = new FakeOpener(handleSource);
            opener.FailAt(0, failure);

            var result = SkillDiscoveryRootPreflightV1.Run(
                [ProjectRootOne],
                [],
                new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

            Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.RootConfigurationInvalid, result.Outcome);
            Assert.Equal("skill_discovery_root_configuration_invalid", result.AbortReason);
            Assert.Null(result.RootSet);
            Assert.Empty(result.RetainedRoots);
        });
    }

    [Fact]
    public void OneInvalidRoot_NeverReducesTheSetToItsValidRoots()
    {
        var opener = new FakeOpener(handleSource);
        opener.FailAt(2, DiscoveryRootOpenFailureV1.NotADirectory);

        var result = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRootOne, ProjectRootTwo],
            [SkillRootAlpha],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.RootConfigurationInvalid, result.Outcome);
        Assert.Null(result.RootSet);
        Assert.Empty(result.RetainedRoots);
    }

    [Fact]
    public void InvalidRoot_ReleasesEveryAlreadyRetainedRoot()
    {
        var opener = new FakeOpener(handleSource);
        opener.FailAt(2, DiscoveryRootOpenFailureV1.Unopenable);

        var result = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRootOne, ProjectRootTwo, ProjectRootThree],
            [],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.RootConfigurationInvalid, result.Outcome);
        Assert.Equal(2, opener.Opened.Count);
        Assert.All(opener.Opened, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public void RetainedHandleThatCannotBeReproved_AbortsWithRootConfigurationInvalid()
    {
        var opener = new FakeOpener(handleSource);
        opener.RefuseReproofAt(1);

        var result = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRootOne, ProjectRootTwo],
            [],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.RootConfigurationInvalid, result.Outcome);
        Assert.Equal("skill_discovery_root_configuration_invalid", result.AbortReason);
        Assert.All(opener.Opened, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public void ValidRootSet_CarriesBothCanonicalArraysAndTheRetainedRoots()
    {
        var opener = new FakeOpener(handleSource);

        using var result = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRootOne, ProjectRootTwo],
            [SkillRootAlpha],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.Certified, result.Outcome);
        Assert.Null(result.AbortReason);
        Assert.NotNull(result.RootSet);
        Assert.Equal([ProjectRootOne, ProjectRootTwo], result.RootSet!.ProjectPathKeys);
        Assert.Equal([SkillRootAlpha], result.RootSet.SkillDirectoryKeys);
        Assert.Equal(3, result.RetainedRoots.Count);
        Assert.All(result.RetainedRoots, root => Assert.False(root.IsDisposed));
    }

    [Fact]
    public void ValidRootSet_RevisionEqualsTheSetBuiltFromTheSurvivingCandidates()
    {
        var opener = new FakeOpener(handleSource);

        using var result = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRootOne],
            [SkillRootAlpha],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        var expected = DiscoveryRootSetV1.Create(
            SkillProducerPathKeyPlatform.Windows,
            result.RetainedRoots.Select(root =>
                new DiscoveryRootCandidateV1(root.Kind, root.NativeIdentity, root.PathKey)));

        Assert.Equal(expected.Revision, result.RootSet!.Revision);
    }

    [Fact]
    public void DuplicateRoleAndNativeIdentity_KeepsTheOrdinallySmallestKeyAndReleasesTheLoser()
    {
        var opener = new FakeOpener(handleSource);
        opener.UseIdentitySeed(LowerCaseRoot, 77);
        opener.UseIdentitySeed(UpperCaseRoot, 77);

        using var result = SkillDiscoveryRootPreflightV1.Run(
            [LowerCaseRoot, UpperCaseRoot],
            [],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.Certified, result.Outcome);
        Assert.Equal([UpperCaseRoot], result.RootSet!.ProjectPathKeys);
        var survivor = Assert.Single(result.RetainedRoots);
        Assert.Equal(UpperCaseRoot, survivor.PathKey.Key);
        Assert.False(survivor.IsDisposed);
        Assert.True(opener.OpenedFor(LowerCaseRoot).IsDisposed);
    }

    [Fact]
    public void RepeatedIdenticalRootValue_RetainsExactlyOneOfTheTwoOpenedHandles()
    {
        var opener = new FakeOpener(handleSource);
        opener.UseIdentitySeed(ProjectRootOne, 5);

        using var result = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRootOne, ProjectRootOne],
            [],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal([ProjectRootOne], result.RootSet!.ProjectPathKeys);
        Assert.Single(result.RetainedRoots);
        Assert.Equal(2, opener.Opened.Count);
        Assert.Equal(1, opener.Opened.Count(root => root.IsDisposed));
    }

    [Fact]
    public void SameNativeRootInBothRoles_SurvivesAsTwoRetainedRoots()
    {
        var opener = new FakeOpener(handleSource);
        opener.UseIdentitySeed(SharedRoot, 9);

        using var result = SkillDiscoveryRootPreflightV1.Run(
            [SharedRoot],
            [SharedRoot],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        Assert.Equal([SharedRoot], result.RootSet!.ProjectPathKeys);
        Assert.Equal([SharedRoot], result.RootSet.SkillDirectoryKeys);
        Assert.Equal(2, result.RetainedRoots.Count);
        Assert.All(opener.Opened, root => Assert.False(root.IsDisposed));
        Assert.Contains(result.RetainedRoots, root => root.Kind == DiscoveryRootKindV1.ProjectPath);
        Assert.Contains(result.RetainedRoots, root => root.Kind == DiscoveryRootKindV1.SkillDirectory);
    }

    [Fact]
    public void DisposingACertifiedResult_ReleasesEveryRetainedRoot()
    {
        var opener = new FakeOpener(handleSource);

        var result = SkillDiscoveryRootPreflightV1.Run(
            [ProjectRootOne],
            [SkillRootAlpha],
            new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));

        var roots = result.RetainedRoots.ToArray();
        Assert.All(roots, root => Assert.False(root.IsDisposed));

        result.Dispose();

        Assert.All(roots, root => Assert.True(root.IsDisposed));
    }

    [Fact]
    public void NoAbortReasonEverCarriesAConfiguredRootValue()
    {
        Assert.All(Enum.GetValues<DiscoveryRootOpenFailureV1>(), failure =>
        {
            var opener = new FakeOpener(handleSource);
            opener.FailAt(0, failure);

            var invalid = SkillDiscoveryRootPreflightV1.Run(
                [SecretProjectRoot],
                [SecretSkillRoot],
                new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, opener));
            var unsupported = SkillDiscoveryRootPreflightV1.Run(
                [SecretProjectRoot],
                [SecretSkillRoot],
                certifiedPlatform: null);

            Assert.Equal("skill_discovery_root_configuration_invalid", invalid.AbortReason);
            Assert.Equal("skill_discovery_platform_unsupported", unsupported.AbortReason);
            Assert.DoesNotContain("secret", invalid.AbortReason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", unsupported.AbortReason, StringComparison.OrdinalIgnoreCase);
        });
    }

    private const string ProjectRootOne = @"C:\repo\one";
    private const string ProjectRootTwo = @"C:\repo\two";
    private const string ProjectRootThree = @"C:\repo\three";
    private const string SkillRootAlpha = @"C:\skills\alpha";
    private const string LowerCaseRoot = @"C:\repo\a";
    private const string UpperCaseRoot = @"C:\repo\B";
    private const string SharedRoot = @"C:\shared";
    private const string SecretProjectRoot = @"C:\secret-project";
    private const string SecretSkillRoot = @"C:\secret-skills";

    private sealed class FakeOpener(TempHandleSource handleSource) : IDiscoveryRootOpenerV1
    {
        private readonly List<RetainedDiscoveryRootV1> opened = [];
        private readonly Dictionary<string, RetainedDiscoveryRootV1> openedByPath = [];
        private readonly Dictionary<int, DiscoveryRootOpenFailureV1> failures = [];
        private readonly Dictionary<string, ulong> identitySeeds = [];
        private readonly HashSet<int> refusedReproofs = [];

        public int OpenCallCount { get; private set; }

        public IReadOnlyList<RetainedDiscoveryRootV1> Opened => opened;

        public RetainedDiscoveryRootV1 OpenedFor(string configuredRootPath) => openedByPath[configuredRootPath];

        public void FailAt(int callIndex, DiscoveryRootOpenFailureV1 failure) => failures[callIndex] = failure;

        public void RefuseReproofAt(int openIndex) => refusedReproofs.Add(openIndex);

        public void UseIdentitySeed(string configuredRootPath, ulong seed) =>
            identitySeeds[configuredRootPath] = seed;

        public DiscoveryRootOpenResultV1 TryOpenRetainedRoot(string configuredRootPath, DiscoveryRootKindV1 kind)
        {
            var callIndex = OpenCallCount++;
            if (failures.TryGetValue(callIndex, out var failure))
            {
                return DiscoveryRootOpenResultV1.Failed(failure);
            }

            if (!SkillProducerPathKeyV1.TryParse(
                    configuredRootPath,
                    SkillProducerPathKeyPlatform.Windows,
                    out var pathKey,
                    out var reason))
            {
                throw new InvalidOperationException($"Test root path failed to parse ({reason}).");
            }

            var seed = identitySeeds.TryGetValue(configuredRootPath, out var configured)
                ? configured
                : (ulong)(callIndex + 1);
            var fileId = new byte[16];
            fileId[0] = (byte)(seed & 0xff);

            var root = new RetainedDiscoveryRootV1(
                kind,
                pathKey,
                DiscoveryRootNativeIdentityV1.CreateWindows(seed, fileId),
                handleSource.OpenHandle());
            opened.Add(root);
            openedByPath[configuredRootPath] = root;
            return DiscoveryRootOpenResultV1.Succeeded(root);
        }

        public bool TryReproveRetainedRoot(RetainedDiscoveryRootV1 root)
        {
            var index = opened.IndexOf(root);
            return !refusedReproofs.Contains(index) && !root.IsDisposed;
        }
    }

    private sealed class TempHandleSource : IDisposable
    {
        private readonly string directoryPath =
            Path.Combine(Path.GetTempPath(), $"cao-preflight-{Guid.NewGuid():N}");

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

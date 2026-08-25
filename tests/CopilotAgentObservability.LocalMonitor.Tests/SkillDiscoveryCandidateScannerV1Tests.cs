using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using Microsoft.Win32.SafeHandles;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class SkillDiscoveryCandidateScannerV1Tests : IDisposable
{
    private const string Revision = "revision-1";
    private readonly TempFileHandleSource handleSource = new();
    private readonly SkillProducerPathKeyPlatform platform = SkillProducerPathKeyPlatform.Windows;

    [Fact]
    public void NullFactListIsDiscoveryUnavailable()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md",
            null, [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.DiscoveryUnavailable, result.Outcome);
        Assert.Null(result.Target);
    }

    [Fact]
    public void NullItemIsDiscoveryUnavailableEvenAfterCandidate()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var facts = new CopilotDiscoveredSkillFactV1?[]
        {
            Fact("name", "project", @"C:\repo\team\SKILL.md", @"C:\repo"),
            null
        };

        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md", facts!, [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.DiscoveryUnavailable, result.Outcome);
    }

    [Fact]
    public void RelativeHistoricalPathIsNotDiscovered()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", "relative/path/SKILL.md",
            [Fact("name", "project", "relative/path/SKILL.md", @"C:\repo")], [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.NotDiscovered, result.Outcome);
    }

    [Fact]
    public void MatchingProjectCandidateProceedsWithTarget()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md",
            [Fact("name", "project", @"C:\repo\team\SKILL.md", @"C:\repo")], [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Proceed, result.Outcome);
        Assert.NotNull(result.Target);
        Assert.Equal(["team", "SKILL.md"], result.Target!.RelativeSegments);
        Assert.Equal(Revision, result.Target.ExpectedRevision);
        Assert.Equal(DiscoveryRootKindV1.ProjectPath, result.Target.RootRole);
    }

    [Fact]
    public void UnrelatedItemsAreIgnoredYieldingNotDiscovered()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md",
            [Fact("other", "project", @"C:\repo\team\SKILL.md", @"C:\repo"),
             Fact("name", "custom", @"C:\repo\team\SKILL.md", null)],
            [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.NotDiscovered, result.Outcome);
    }

    [Fact]
    public void ProjectMatchWithNullProjectPathIsUnsafe()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md",
            [Fact("name", "project", @"C:\repo\team\SKILL.md", null)], [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Unsafe, result.Outcome);
    }

    [Fact]
    public void ProjectMatchWithUnretainedProjectPathIsUnsafe()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md",
            [Fact("name", "project", @"C:\repo\team\SKILL.md", @"C:\elsewhere")], [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Unsafe, result.Outcome);
    }

    [Fact]
    public void ProjectMatchNotStrictDescendantIsUnsafe()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo2\team\SKILL.md",
            [Fact("name", "project", @"C:\repo2\team\SKILL.md", @"C:\repo")], [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Unsafe, result.Outcome);
    }

    [Fact]
    public void TwoDistinctEligibleCandidatesAreUnsafe()
    {
        using var projectRootA = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        using var projectRootB = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath, identitySeed: 99);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md",
            [Fact("name", "project", @"C:\repo\team\SKILL.md", @"C:\repo"),
             Fact("name", "project", @"C:\repo\team\SKILL.md", @"C:\repo", description: "alt")],
            [projectRootA], Revision);

        // Same eight facts except Description differ -> not collapsible -> two candidates.
        Assert.Equal(SkillDiscoveryScanOutcome.Unsafe, result.Outcome);
    }

    [Fact]
    public void ByteIdenticalDuplicateCandidatesCollapseToOne()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var fact = Fact("name", "project", @"C:\repo\team\SKILL.md", @"C:\repo");
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\SKILL.md",
            [fact, fact with { }], [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Proceed, result.Outcome);
        Assert.NotNull(result.Target);
    }

    [Fact]
    public void CustomMatchWithNonNullProjectPathIsUnsafe()
    {
        using var skillRoot = CreateRetainedRoot(@"C:\skills", DiscoveryRootKindV1.SkillDirectory);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "custom", @"C:\skills\mine\SKILL.md",
            [Fact("name", "custom", @"C:\skills\mine\SKILL.md", @"C:\skills")], [skillRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Unsafe, result.Outcome);
    }

    [Fact]
    public void CustomMatchDescendantOfSkillDirectoryProceeds()
    {
        using var skillRoot = CreateRetainedRoot(@"C:\skills", DiscoveryRootKindV1.SkillDirectory);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "custom", @"C:\skills\mine\SKILL.md",
            [Fact("name", "custom", @"C:\skills\mine\SKILL.md", null)], [skillRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Proceed, result.Outcome);
        Assert.Equal(["mine", "SKILL.md"], result.Target!.RelativeSegments);
        Assert.Equal(DiscoveryRootKindV1.SkillDirectory, result.Target.RootRole);
    }

    [Fact]
    public void CustomMatchWithNoAncestorSkillDirectoryIsUnsafe()
    {
        using var skillRoot = CreateRetainedRoot(@"C:\skills", DiscoveryRootKindV1.SkillDirectory);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "custom", @"C:\other\mine\SKILL.md",
            [Fact("name", "custom", @"C:\other\mine\SKILL.md", null)], [skillRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Unsafe, result.Outcome);
    }

    [Fact]
    public void BuiltinSourceYieldsNotDiscovered()
    {
        using var skillRoot = CreateRetainedRoot(@"C:\skills", DiscoveryRootKindV1.SkillDirectory);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "builtin", @"C:\skills\mine\SKILL.md",
            [Fact("name", "builtin", @"C:\skills\mine\SKILL.md", null)], [skillRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.NotDiscovered, result.Outcome);
    }

    [Fact]
    public void FinalSegmentOtherThanSkillMdIsUnsafe()
    {
        using var projectRoot = CreateRetainedRoot(@"C:\repo", DiscoveryRootKindV1.ProjectPath);
        var result = SkillDiscoveryCandidateScannerV1.Scan(
            "name", "project", @"C:\repo\team\OTHER.md",
            [Fact("name", "project", @"C:\repo\team\OTHER.md", @"C:\repo")], [projectRoot], Revision);

        Assert.Equal(SkillDiscoveryScanOutcome.Unsafe, result.Outcome);
    }

    private static CopilotDiscoveredSkillFactV1 Fact(
        string name,
        string source,
        string path,
        string? projectPath,
        string? description = null,
        string? argumentHint = null,
        bool enabled = true,
        bool userInvocable = true) =>
        new(name, source, path, projectPath, description, argumentHint, enabled, userInvocable);

    private RetainedDiscoveryRootV1 CreateRetainedRoot(
        string rootPath,
        DiscoveryRootKindV1 kind,
        ulong identitySeed = 1234)
    {
        if (!SkillProducerPathKeyV1.TryParse(rootPath, platform, out var pathKey, out var reason))
        {
            throw new InvalidOperationException($"Test root path failed to parse ({reason}).");
        }

        var identityBytes = new byte[16];
        identityBytes[0] = (byte)(identitySeed & 0xff);
        var identity = DiscoveryRootNativeIdentityV1.CreateWindows(identitySeed, identityBytes);

        return new RetainedDiscoveryRootV1(kind, pathKey, identity, handleSource.OpenHandle());
    }

    public void Dispose() => handleSource.Dispose();

    private sealed class TempFileHandleSource : IDisposable
    {
        private readonly string directoryPath =
            Path.Combine(Path.GetTempPath(), $"cao-scanner-{Guid.NewGuid():N}");

        private readonly string filePath;

        public TempFileHandleSource()
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

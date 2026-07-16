using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotManagedPolicyTests
{
    [Fact]
    public void Resolve_NativeEqualAndContradictoryFile_WinsNativeWithoutReadingFile()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            Present(new Dictionary<string, object?>
            {
                ["CopilotOtelEnabled"] = true,
                ["CopilotOtelEndpoint"] = "http://127.0.0.1:4318",
            }));
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileWindows,
            Present(new Dictionary<string, object?>
            {
                ["CopilotOtelEnabled"] = false,
                ["CopilotOtelEndpoint"] = "https://foreign.example.invalid",
            }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.Native, result.WinningChannel);
        Assert.True(result.ServerTierVerifiable);
        Assert.Equal(
            [
                new ManagedFieldConstraint("CopilotOtelEnabled", ManagedConstraintComparison.EqualToDesired),
                new ManagedFieldConstraint("CopilotOtelEndpoint", ManagedConstraintComparison.EqualToDesired),
            ],
            result.CopilotConstraints);
        Assert.DoesNotContain(
            "managed.read:GitHubCopilotFileWindows",
            platform.Operations);
    }

    [Fact]
    public void Resolve_NativeContainsOneDifferingDesiredField_ReportsOnlyThatConstraintAsDiffering()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            Present(new Dictionary<string, object?>
            {
                ["CopilotOtelEnabled"] = false,
                ["CopilotOtelEndpoint"] = "http://127.0.0.1:4318",
                ["UnknownManagedKey"] = "ignored",
            }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(
            [
                new ManagedFieldConstraint("CopilotOtelEnabled", ManagedConstraintComparison.DiffersFromDesired),
                new ManagedFieldConstraint("CopilotOtelEndpoint", ManagedConstraintComparison.EqualToDesired),
            ],
            result.CopilotConstraints);
    }

    [Fact]
    public void Resolve_NativeAbsentAndFileEqual_WinsFileButLeavesServerUnverifiable()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileWindows,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.File, result.WinningChannel);
        Assert.False(result.ServerTierVerifiable);
        Assert.Equal(
            [new ManagedFieldConstraint("CopilotOtelEnabled", ManagedConstraintComparison.EqualToDesired)],
            result.CopilotConstraints);
    }

    [Fact]
    public void Resolve_NativeAndFileAbsent_ReturnsNoConstraintsAndUnverifiableServer()
    {
        var platform = CreatePlatform();

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.None, result.WinningChannel);
        Assert.False(result.ServerTierVerifiable);
        Assert.Empty(result.CopilotConstraints);
        Assert.Empty(result.EnterprisePolicyConstraints);
    }

    [Fact]
    public void Resolve_Linux_DoesNotReadNativeManagedLocation()
    {
        var platform = CreatePlatform(SetupPlanningOs.Linux);
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileLinux,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));

        _ = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Linux, DesiredValues);

        Assert.DoesNotContain(
            platform.Operations,
            operation => operation.StartsWith("managed.read:GitHubCopilotNative", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_WindowsEnterpriseMachinePolicyWinsOverUserWithoutSuppressingCopilotFile()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileWindows,
            Present(new Dictionary<string, object?> { ["CopilotOtelEndpoint"] = "http://127.0.0.1:4318" }));
        platform.SeedManagedObservation(
            SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = false }));
        platform.SeedManagedObservation(
            SetupManagedLocation.VsCodeEnterpriseWindowsUserPolicy,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.File, result.WinningChannel);
        Assert.Equal(
            [new ManagedFieldConstraint("CopilotOtelEndpoint", ManagedConstraintComparison.EqualToDesired)],
            result.CopilotConstraints);
        Assert.Equal(
            [new ManagedFieldConstraint("CopilotOtelEnabled", ManagedConstraintComparison.DiffersFromDesired)],
            result.EnterprisePolicyConstraints);
    }

    [Fact]
    public void Resolve_EnterpriseEqualAndCopilotFileDiffering_ReportsIndependentConstraintLists()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileWindows,
            Present(new Dictionary<string, object?> { ["CopilotOtelEndpoint"] = "https://foreign.example.invalid" }));
        platform.SeedManagedObservation(
            SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(
            [new ManagedFieldConstraint("CopilotOtelEndpoint", ManagedConstraintComparison.DiffersFromDesired)],
            result.CopilotConstraints);
        Assert.Equal(
            [new ManagedFieldConstraint("CopilotOtelEnabled", ManagedConstraintComparison.EqualToDesired)],
            result.EnterprisePolicyConstraints);
    }

    [Fact]
    public void Resolve_MalformedWinningNativeContent_ReturnsMalformedMarkerWithoutExceptionText()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Present, Encoding.UTF8.GetBytes("{ unreadable-private-detail"), true));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.Malformed, result.WinningChannel);
        Assert.False(result.ServerTierVerifiable);
        Assert.Empty(result.CopilotConstraints);
        Assert.Empty(result.EnterprisePolicyConstraints);
        Assert.DoesNotContain("unreadable-private-detail", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ObservedValuesNeverAppearInSerializedResolution()
    {
        const string marker = "private-managed-value-marker";
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            Present(new Dictionary<string, object?> { ["CopilotOtelEndpoint"] = marker }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.DoesNotContain(marker, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_NativeValidAndEnterpriseMalformed_PreservesNativeProvenance()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));
        platform.SeedManagedObservation(
            SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Failed, [], true));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.Native, result.WinningChannel);
        Assert.True(result.ServerTierVerifiable);
        Assert.Equal(
            [new ManagedFieldConstraint("CopilotOtelEnabled", ManagedConstraintComparison.EqualToDesired)],
            result.CopilotConstraints);
        Assert.Equal(GitHubCopilotManagedPolicyMalformedSystems.Enterprise, result.MalformedSystems);
    }

    [Fact]
    public void Resolve_FileValidAndEnterpriseMalformed_PreservesFileProvenance()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileWindows,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));
        platform.SeedManagedObservation(
            SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Failed, [], true));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.File, result.WinningChannel);
        Assert.False(result.ServerTierVerifiable);
        Assert.Equal(GitHubCopilotManagedPolicyMalformedSystems.Enterprise, result.MalformedSystems);
    }

    [Fact]
    public void Resolve_CopilotMalformedAndEnterpriseValid_PreservesEnterpriseConstraints()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Failed, [], true));
        platform.SeedManagedObservation(
            SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.Malformed, result.WinningChannel);
        Assert.False(result.ServerTierVerifiable);
        Assert.Equal(GitHubCopilotManagedPolicyMalformedSystems.Copilot, result.MalformedSystems);
        Assert.Equal(
            [new ManagedFieldConstraint("CopilotOtelEnabled", ManagedConstraintComparison.EqualToDesired)],
            result.EnterprisePolicyConstraints);
    }

    [Fact]
    public void Resolve_BothPolicySystemsMalformed_ReportsBothMarkers()
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Failed, [], true));
        platform.SeedManagedObservation(
            SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy,
            new SetupManagedObservation(SetupManagedOutcome.Failed, [], true));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.Malformed, result.WinningChannel);
        Assert.Equal(
            GitHubCopilotManagedPolicyMalformedSystems.Copilot |
            GitHubCopilotManagedPolicyMalformedSystems.Enterprise,
            result.MalformedSystems);
    }

    [Theory]
    [MemberData(nameof(MalformedNativeObservations))]
    public void Resolve_MalformedNativeObservationShape_DoesNotReadLowerFileTier(
        SetupManagedObservation nativeObservation)
    {
        var platform = CreatePlatform();
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            nativeObservation);
        platform.SeedManagedObservation(
            SetupManagedLocation.GitHubCopilotFileWindows,
            Present(new Dictionary<string, object?> { ["CopilotOtelEnabled"] = true }));

        var result = GitHubCopilotManagedPolicyResolver.Resolve(platform, SetupPlanningOs.Windows, DesiredValues);

        Assert.Equal(GitHubCopilotManagedChannel.Malformed, result.WinningChannel);
        Assert.Equal(GitHubCopilotManagedPolicyMalformedSystems.Copilot, result.MalformedSystems);
        Assert.DoesNotContain("managed.read:GitHubCopilotFileWindows", platform.Operations);
    }

    public static IEnumerable<object[]> MalformedNativeObservations()
    {
        yield return [new SetupManagedObservation(SetupManagedOutcome.Failed, [], true)];
        yield return [new SetupManagedObservation(SetupManagedOutcome.Present, "{}"u8.ToArray(), false)];
        yield return [new SetupManagedObservation(SetupManagedOutcome.Present, "[]"u8.ToArray(), true)];
        yield return [new SetupManagedObservation(
            SetupManagedOutcome.Present,
            "{\"CopilotOtelEnabled\":true,\"CopilotOtelEnabled\":false}"u8.ToArray(),
            true)];
        yield return [new SetupManagedObservation(
            SetupManagedOutcome.Present,
            "{\"CopilotOtelEnabled\":{}}"u8.ToArray(),
            true)];
        yield return [new SetupManagedObservation(SetupManagedOutcome.Absent, [1], true)];
        yield return [new SetupManagedObservation(SetupManagedOutcome.Failed, [1], true)];
        yield return [new SetupManagedObservation((SetupManagedOutcome)99, [], true)];
        yield return [new SetupManagedObservation(SetupManagedOutcome.Present, null!, true)];
    }

    private static readonly IReadOnlyDictionary<string, string> DesiredValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CopilotOtelEnabled"] = "true",
            ["CopilotOtelEndpoint"] = "http://127.0.0.1:4318",
        };

    private static SetupTestPlatform CreatePlatform(SetupPlanningOs planningOs = SetupPlanningOs.Windows) =>
        planningOs == SetupPlanningOs.Windows
            ? new SetupTestPlatform(DateTimeOffset.UnixEpoch, planningOs: planningOs)
            : new SetupTestPlatform(
                DateTimeOffset.UnixEpoch,
                pathStyle: SetupPathStyle.Unix,
                planningOs: planningOs,
                applicationData: "/home/setup-test/.config",
                userProfile: "/home/setup-test");

    private static SetupManagedObservation Present(object content) =>
        new(SetupManagedOutcome.Present, JsonSerializer.SerializeToUtf8Bytes(content), true);
}

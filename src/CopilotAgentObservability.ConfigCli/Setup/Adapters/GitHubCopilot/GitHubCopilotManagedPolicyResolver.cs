using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal enum GitHubCopilotManagedChannel
{
    Native,
    File,
    None,
    Malformed,
}

internal enum ManagedConstraintComparison
{
    EqualToDesired,
    DiffersFromDesired,
}

internal sealed record GitHubCopilotManagedPolicyResolution(
    GitHubCopilotManagedChannel WinningChannel,
    bool ServerTierVerifiable,
    IReadOnlyList<ManagedFieldConstraint> CopilotConstraints,
    IReadOnlyList<ManagedFieldConstraint> EnterprisePolicyConstraints);

internal sealed record ManagedFieldConstraint(
    string SettingKey,
    ManagedConstraintComparison Comparison);

internal static class GitHubCopilotManagedPolicyResolver
{
    public static GitHubCopilotManagedPolicyResolution Resolve(
        ISetupPlatform platform,
        SetupPlanningOs planningOs,
        IReadOnlyDictionary<string, string> desiredValues)
    {
        if (platform is null || desiredValues is null)
        {
            return MalformedResolution();
        }

        var desired = desiredValues
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
        var copilot = ResolveCopilot(platform, planningOs, desired);
        var enterprise = ResolveEnterprise(platform, planningOs, desired);
        var malformed = copilot.IsMalformed || enterprise.IsMalformed;

        return new GitHubCopilotManagedPolicyResolution(
            malformed ? GitHubCopilotManagedChannel.Malformed : copilot.Channel,
            !malformed && copilot.ServerTierVerifiable,
            copilot.Constraints,
            enterprise.Constraints);
    }

    private static GitHubCopilotManagedPolicyResolution MalformedResolution() =>
        new(GitHubCopilotManagedChannel.Malformed, false, [], []);

    private static PolicyEvaluation ResolveCopilot(
        ISetupPlatform platform,
        SetupPlanningOs planningOs,
        IReadOnlyList<KeyValuePair<string, string>> desired)
    {
        SetupManagedLocation? nativeLocation = planningOs switch
        {
            SetupPlanningOs.Windows => SetupManagedLocation.GitHubCopilotNativeWindowsMachinePolicy,
            SetupPlanningOs.MacOs => SetupManagedLocation.GitHubCopilotNativeMacOsManagedPreferences,
            _ => null,
        };

        if (nativeLocation is { } location)
        {
            var native = ReadManagedObject(platform, location, desired);
            if (native.IsMalformed)
            {
                return PolicyEvaluation.Malformed;
            }

            if (native.HasSettings)
            {
                return new PolicyEvaluation(
                    GitHubCopilotManagedChannel.Native,
                    true,
                    native.Constraints,
                    false);
            }
        }

        SetupManagedLocation? fileLocation = planningOs switch
        {
            SetupPlanningOs.Windows => SetupManagedLocation.GitHubCopilotFileWindows,
            SetupPlanningOs.MacOs => SetupManagedLocation.GitHubCopilotFileMacOs,
            SetupPlanningOs.Linux => SetupManagedLocation.GitHubCopilotFileLinux,
            _ => null,
        };

        if (fileLocation is not { } file)
        {
            return PolicyEvaluation.Malformed;
        }

        var managedFile = ReadManagedObject(platform, file, desired);
        if (managedFile.IsMalformed)
        {
            return PolicyEvaluation.Malformed;
        }

        return managedFile.HasSettings
            ? new PolicyEvaluation(GitHubCopilotManagedChannel.File, false, managedFile.Constraints, false)
            : new PolicyEvaluation(GitHubCopilotManagedChannel.None, false, [], false);
    }

    private static PolicyEvaluation ResolveEnterprise(
        ISetupPlatform platform,
        SetupPlanningOs planningOs,
        IReadOnlyList<KeyValuePair<string, string>> desired)
    {
        if (planningOs == SetupPlanningOs.Windows)
        {
            var machine = ReadManagedObject(
                platform,
                SetupManagedLocation.VsCodeEnterpriseWindowsMachinePolicy,
                desired);
            if (machine.IsMalformed)
            {
                return PolicyEvaluation.Malformed;
            }

            if (machine.HasSettings)
            {
                return new PolicyEvaluation(GitHubCopilotManagedChannel.None, false, machine.Constraints, false);
            }

            var user = ReadManagedObject(
                platform,
                SetupManagedLocation.VsCodeEnterpriseWindowsUserPolicy,
                desired);
            return user.IsMalformed
                ? PolicyEvaluation.Malformed
                : new PolicyEvaluation(GitHubCopilotManagedChannel.None, false, user.Constraints, false);
        }

        SetupManagedLocation? location = planningOs switch
        {
            SetupPlanningOs.MacOs => SetupManagedLocation.VsCodeEnterpriseMacOsConfigurationProfile,
            SetupPlanningOs.Linux => SetupManagedLocation.VsCodeEnterpriseLinuxPolicyFile,
            _ => null,
        };
        if (location is not { } enterpriseLocation)
        {
            return PolicyEvaluation.Malformed;
        }

        var enterprise = ReadManagedObject(platform, enterpriseLocation, desired);
        return enterprise.IsMalformed
            ? PolicyEvaluation.Malformed
            : new PolicyEvaluation(GitHubCopilotManagedChannel.None, false, enterprise.Constraints, false);
    }

    private static ManagedObject ReadManagedObject(
        ISetupPlatform platform,
        SetupManagedLocation location,
        IReadOnlyList<KeyValuePair<string, string>> desired)
    {
        SetupManagedObservation observation;
        try
        {
            observation = platform.ManagedSettings.Read(location);
        }
        catch (Exception)
        {
            return ManagedObject.Malformed;
        }

        if (observation is null || observation.Outcome == SetupManagedOutcome.Failed || !observation.IsComplete)
        {
            return ManagedObject.Malformed;
        }

        if (observation.Outcome == SetupManagedOutcome.Absent)
        {
            return ManagedObject.Absent;
        }

        if (observation.Outcome != SetupManagedOutcome.Present || observation.Bytes is null)
        {
            return ManagedObject.Malformed;
        }

        try
        {
            using var document = JsonDocument.Parse(observation.Bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ManagedObject.Malformed;
            }

            var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.TryAdd(property.Name, property.Value))
                {
                    return ManagedObject.Malformed;
                }
            }

            if (properties.Count == 0)
            {
                return ManagedObject.Absent;
            }

            var constraints = new List<ManagedFieldConstraint>();
            foreach (var desiredSetting in desired)
            {
                if (!properties.TryGetValue(desiredSetting.Key, out var observed))
                {
                    continue;
                }

                if (!TryCompare(observed, desiredSetting.Value, out var comparison))
                {
                    return ManagedObject.Malformed;
                }

                constraints.Add(new ManagedFieldConstraint(desiredSetting.Key, comparison));
            }

            return new ManagedObject(true, constraints, false);
        }
        catch (JsonException)
        {
            return ManagedObject.Malformed;
        }
        catch (Exception)
        {
            return ManagedObject.Malformed;
        }
    }

    private static bool TryCompare(
        JsonElement observed,
        string desired,
        out ManagedConstraintComparison comparison)
    {
        string? observedValue = observed.ValueKind switch
        {
            JsonValueKind.String => observed.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => observed.GetRawText(),
            _ => null,
        };

        if (observedValue is null)
        {
            comparison = default;
            return false;
        }

        comparison = StringComparer.Ordinal.Equals(observedValue, desired)
            ? ManagedConstraintComparison.EqualToDesired
            : ManagedConstraintComparison.DiffersFromDesired;
        return true;
    }

    private sealed record PolicyEvaluation(
        GitHubCopilotManagedChannel Channel,
        bool ServerTierVerifiable,
        IReadOnlyList<ManagedFieldConstraint> Constraints,
        bool IsMalformed)
    {
        public static PolicyEvaluation Malformed { get; } =
            new(GitHubCopilotManagedChannel.Malformed, false, [], true);
    }

    private sealed record ManagedObject(
        bool HasSettings,
        IReadOnlyList<ManagedFieldConstraint> Constraints,
        bool IsMalformed)
    {
        public static ManagedObject Absent { get; } = new(false, [], false);

        public static ManagedObject Malformed { get; } = new(false, [], true);
    }
}

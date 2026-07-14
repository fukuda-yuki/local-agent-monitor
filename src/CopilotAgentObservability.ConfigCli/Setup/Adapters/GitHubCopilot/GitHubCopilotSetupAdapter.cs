using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;

internal sealed class GitHubCopilotSetupAdapter : ISetupAdapter
{
    private const string VsCodeTarget = "vscode";
    private const string CliTarget = "cli";
    private const string AppSdkTarget = "app-sdk";
    private const string AllTargets = "all";
    private const string VsCodeStableLabel = "vscode-stable-default-user-settings";
    private const string VsCodeInsidersLabel = "vscode-insiders-default-user-settings";
    private const string CliLabel = "copilot-cli-user-environment";
    private const string AppSdkLabel = "github-copilot-app-sdk-guidance";
    private static readonly string[] TargetOrder = [VsCodeTarget, CliTarget, AppSdkTarget];

    private readonly ISetupPlatform platform;
    private readonly IReadOnlyDictionary<string, IGitHubCopilotTargetPartition> partitions;

    internal GitHubCopilotSetupAdapter(
        ISetupPlatform platform,
        IReadOnlyList<IGitHubCopilotTargetPartition> partitions)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        ArgumentNullException.ThrowIfNull(partitions);

        var registered = new Dictionary<string, IGitHubCopilotTargetPartition>(StringComparer.Ordinal);
        foreach (var partition in partitions)
        {
            ArgumentNullException.ThrowIfNull(partition);
            if (!TargetOrder.Contains(partition.TargetToken, StringComparer.Ordinal) ||
                !registered.TryAdd(partition.TargetToken, partition))
            {
                throw new ArgumentException("GitHub Copilot partitions must own distinct frozen targets.", nameof(partitions));
            }
        }

        if (registered.Count != TargetOrder.Length || TargetOrder.Any(target => !registered.ContainsKey(target)))
        {
            throw new ArgumentException("GitHub Copilot partitions must own each frozen target.", nameof(partitions));
        }

        this.partitions = registered;
    }

    public string AdapterId => "github-copilot";

    public SetupPlanResult<SetupChangePlan> Plan(SetupPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryGetPlanTargets(request.SelectedTarget, out var selectedTargets))
        {
            return SetupPlanResult.Failure<SetupChangePlan>(SetupCodes.UnsupportedTarget);
        }

        var context = new GitHubCopilotPartitionContext(platform, request);
        var records = new List<SetupChangeRecord>();
        var warnings = new List<string>();
        var nextActions = new List<string>();
        foreach (var target in selectedTargets)
        {
            var partitionPlan = partitions[target].Plan(context) ?? throw SetupPlanResult.InvalidOutput();
            ValidatePartitionPlan(partitionPlan);
            if (partitionPlan.FailureCode is not null)
            {
                return SetupPlanResult.Failure<SetupChangePlan>(
                    partitionPlan.FailureCode,
                    partitionPlan.Warnings,
                    partitionPlan.NextActions);
            }

            records.AddRange(partitionPlan.Records.Select(record => AttachExpectedResult(target, record)));
            AddDistinct(warnings, partitionPlan.Warnings);
            AddDistinct(nextActions, partitionPlan.NextActions);
        }

        return SetupPlanResult.Planned(
            new SetupChangePlan(
                request.ChangeSetId,
                request.Adapter,
                request.SelectedTarget,
                request.CreatedAt,
                request.ToolVersion,
                records),
            warnings,
            nextActions);
    }

    public SetupPlanResult<SetupRevalidation> Revalidate(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plannedChangeSet);

        if (!TryGetPlanTargets(plan.SelectedTarget, out _))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedTarget);
        }

        var routedTargets = GetRevalidationTargets(plannedChangeSet);
        if (routedTargets is null)
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedTarget);
        }

        if (routedTargets.Count == 0)
        {
            return SetupPlanResult.Revalidated();
        }

        var context = new GitHubCopilotPartitionContext(platform, CreateRevalidationRequest(plan, plannedChangeSet, routedTargets));
        var warnings = new List<string>();
        var nextActions = new List<string>();
        foreach (var target in TargetOrder.Where(routedTargets.Contains))
        {
            var result = partitions[target].Revalidate(context, plan, plannedChangeSet) ?? throw SetupPlanResult.InvalidOutput();
            if (result is SetupPlanFailure<SetupRevalidation> failure)
            {
                return SetupPlanResult.Failure<SetupRevalidation>(failure.Code, failure.Warnings, failure.NextActions);
            }

            if (result is not SetupPlanSuccess<SetupRevalidation> success || success.Targets.Count != 0)
            {
                throw SetupPlanResult.InvalidOutput();
            }

            AddDistinct(warnings, success.Warnings);
            AddDistinct(nextActions, success.NextActions);
        }

        return SetupPlanResult.Revalidated(warnings, nextActions);
    }

    private static bool TryGetPlanTargets(string selectedTarget, out IReadOnlyList<string> targets)
    {
        switch (selectedTarget)
        {
            case VsCodeTarget:
            case CliTarget:
            case AppSdkTarget:
                targets = [selectedTarget];
                return true;
            case AllTargets:
                targets = TargetOrder;
                return true;
            default:
                targets = [];
                return false;
        }
    }

    private static void ValidatePartitionPlan(GitHubCopilotPartitionPlan plan)
    {
        if (plan.Records is null || plan.Warnings is null || plan.NextActions is null ||
            plan.FailureCode is not null && plan.Records.Count != 0)
        {
            throw SetupPlanResult.InvalidOutput();
        }
    }

    private static SetupChangeRecord AttachExpectedResult(string target, SetupChangeRecord record)
    {
        if (record?.StatusProjection is null || !IsOwnedRecordLabel(target, record.TargetLabel))
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var manifestTarget = target switch
        {
            VsCodeTarget => GitHubCopilotSetupTarget.VsCode,
            CliTarget => GitHubCopilotSetupTarget.Cli,
            AppSdkTarget => GitHubCopilotSetupTarget.AppSdk,
            _ => throw SetupPlanResult.InvalidOutput(),
        };
        var expectedResult = SourceCapabilityManifestLoader.LoadForTarget(manifestTarget)?.CanonicalJson;
        return record with
        {
            StatusProjection = record.StatusProjection with { ExpectedResult = expectedResult },
        };
    }

    private static List<string>? GetRevalidationTargets(SetupLedgerChangeSet plannedChangeSet)
    {
        if (plannedChangeSet.Targets is null)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var targets = new List<string>();
        foreach (var record in plannedChangeSet.Targets)
        {
            if (record is null || !TryGetTargetForLabel(record.TargetLabel, out var target))
            {
                return null;
            }

            if (target == AppSdkTarget)
            {
                if (record.TargetKind != SetupTargetKind.Guidance)
                {
                    return null;
                }

                continue;
            }

            if (record.TargetKind == SetupTargetKind.Guidance || !targets.Contains(target, StringComparer.Ordinal))
            {
                if (record.TargetKind == SetupTargetKind.Guidance)
                {
                    return null;
                }

                targets.Add(target);
            }
        }

        return targets;
    }

    private static SetupPlanRequest CreateRevalidationRequest(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet,
        IReadOnlyCollection<string> routedTargets)
    {
        var endpoints = plannedChangeSet.Targets
            .Where(target => target is not null && TryGetTargetForLabel(target.TargetLabel, out var targetName) && routedTargets.Contains(targetName))
            .Select(target => target!.StatusProjection?.Endpoint)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (endpoints.Length != 1 || string.IsNullOrEmpty(endpoints[0]))
        {
            throw SetupPlanResult.InvalidOutput();
        }

        return new SetupPlanRequest(
            plan.Adapter,
            plan.SelectedTarget,
            endpoints[0]!,
            false,
            plan.ChangeSetId,
            plan.CreatedAt,
            plan.ToolVersion);
    }

    private static bool TryGetTargetForLabel(string? targetLabel, out string target)
    {
        target = targetLabel switch
        {
            VsCodeStableLabel or VsCodeInsidersLabel => VsCodeTarget,
            CliLabel => CliTarget,
            AppSdkLabel => AppSdkTarget,
            _ => string.Empty,
        };
        return target.Length != 0;
    }

    private static bool IsOwnedRecordLabel(string target, string? targetLabel) =>
        TryGetTargetForLabel(targetLabel, out var owner) && string.Equals(owner, target, StringComparison.Ordinal);

    private static void AddDistinct(List<string> destination, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!destination.Contains(value, StringComparer.Ordinal))
            {
                destination.Add(value);
            }
        }
    }
}

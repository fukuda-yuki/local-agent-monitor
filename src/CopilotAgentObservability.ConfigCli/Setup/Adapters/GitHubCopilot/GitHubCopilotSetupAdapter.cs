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
    private static readonly string[] VsCodeMembers =
    [
        "github.copilot.chat.otel.enabled",
        "github.copilot.chat.otel.exporterType",
        "github.copilot.chat.otel.otlpEndpoint",
    ];
    private static readonly string[] CliMembers =
    [
        "COPILOT_OTEL_ENABLED",
        "COPILOT_OTEL_EXPORTER_TYPE",
        "OTEL_EXPORTER_OTLP_ENDPOINT",
        "OTEL_EXPORTER_OTLP_PROTOCOL",
    ];
    private static readonly IReadOnlyDictionary<string, string> VsCodeRequiredValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["github.copilot.chat.otel.enabled"] = "true",
            ["github.copilot.chat.otel.exporterType"] = "otlp-http",
        };
    private static readonly IReadOnlyDictionary<string, string> CliRequiredValues =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COPILOT_OTEL_ENABLED"] = "true",
            ["COPILOT_OTEL_EXPORTER_TYPE"] = "otlp-http",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        };
    private const string VsCodeCaptureMember = "github.copilot.chat.otel.captureContent";
    private const string CliCaptureMember = "OTEL_INSTRUMENTATION_GENAI_CAPTURE_MESSAGE_CONTENT";

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

            records.AddRange(partitionPlan.Records.Select(record => AttachExpectedResult(target, request.Endpoint, record)));
            AddDistinct(warnings, partitionPlan.Warnings);
            AddDistinct(nextActions, partitionPlan.NextActions);
        }

        ValidateAggregatedPlan(request.SelectedTarget, records);

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

        if (!TryCreateRevalidationInput(plan, plannedChangeSet, out var input))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedTarget);
        }

        if (input.RoutedTargets.Count == 0)
        {
            return SetupPlanResult.Revalidated();
        }

        var context = new GitHubCopilotPartitionContext(platform, new SetupPlanRequest(
            plan.Adapter,
            plan.SelectedTarget,
            input.Endpoint,
            input.IncludeContentCapture,
            plan.ChangeSetId,
            plan.CreatedAt,
            plan.ToolVersion));
        var warnings = new List<string>();
        var nextActions = new List<string>();
        var materializedTargets = new List<SetupMaterializedTarget>();
        foreach (var target in TargetOrder.Where(input.RoutedTargets.Contains))
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
            materializedTargets.AddRange(success.Value.MaterializedTargets);
        }

        return SetupPlanResult.Revalidated(materializedTargets, warnings, nextActions);
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

    private static SetupChangeRecord AttachExpectedResult(string target, string endpoint, SetupChangeRecord record)
    {
        if (record?.StatusProjection is null || !IsOwnedRecordLabel(target, record.TargetLabel) ||
            !HasExpectedPlanShape(target, endpoint, record))
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

    private static bool HasExpectedPlanShape(string target, string endpoint, SetupChangeRecord record) => target switch
    {
        VsCodeTarget => record.TargetKind == SetupTargetKind.Json &&
            record.TargetLabel is VsCodeStableLabel or VsCodeInsidersLabel &&
            record.StatusProjection.Endpoint == endpoint,
        CliTarget => record.TargetKind == SetupTargetKind.Env &&
            record.TargetLabel == CliLabel &&
            record.StatusProjection.Endpoint == endpoint,
        AppSdkTarget => record.TargetKind == SetupTargetKind.Guidance &&
            record.TargetLabel == AppSdkLabel &&
            record.Members.Count == 0 &&
            record.StatusProjection.Endpoint is null &&
            record.StatusProjection.ExpectedResult is null &&
            record.StatusProjection.Operation == SetupOperation.NoOp &&
            record.StatusProjection.EffectiveSource is null &&
            record.StatusProjection.Guidance is { Kind: "caller_managed_sample", Language: "dotnet" } &&
            record.StatusProjection.Changes.Count == 0 &&
            record.RestartRequirement == SetupRestartRequirement.None &&
            record.Guidance is { Kind: "caller_managed_sample", Language: "dotnet", Sample: var sample } &&
            sample == SetupContractValidator.RehydrateStatusGuidance(record.StatusProjection.Guidance!, record.TargetLabel).Sample,
        _ => false,
    };

    private static void ValidateAggregatedPlan(string selectedTarget, IReadOnlyList<SetupChangeRecord> records)
    {
        if (records.Count == 0 ||
            records.Select(record => record.TargetLabel).Distinct(StringComparer.Ordinal).Count() != records.Count ||
            !HasExpectedAggregateShape(selectedTarget, records))
        {
            throw SetupPlanResult.InvalidOutput();
        }
    }

    private static bool HasExpectedAggregateShape(string selectedTarget, IReadOnlyList<SetupChangeRecord> records)
    {
        var labels = records.Select(record => record.TargetLabel).ToArray();
        return selectedTarget switch
        {
            VsCodeTarget => labels.Length is 1 or 2 && labels.All(label => label is VsCodeStableLabel or VsCodeInsidersLabel) &&
                labels.SequenceEqual(labels.OrderBy(LabelOrder)),
            CliTarget => labels.SequenceEqual([CliLabel]),
            AppSdkTarget => labels.SequenceEqual([AppSdkLabel]),
            AllTargets => labels.Length is 3 or 4 &&
                labels.Count(label => label is VsCodeStableLabel or VsCodeInsidersLabel) is 1 or 2 &&
                labels.Count(label => label == CliLabel) == 1 &&
                labels.Count(label => label == AppSdkLabel) == 1 &&
                labels.SequenceEqual(labels.OrderBy(LabelOrder)),
            _ => false,
        };
    }

    private static int LabelOrder(string label) => label switch
    {
        VsCodeStableLabel => 0,
        VsCodeInsidersLabel => 1,
        CliLabel => 2,
        AppSdkLabel => 3,
        _ => int.MaxValue,
    };

    private static bool TryCreateRevalidationInput(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet plannedChangeSet,
        out RevalidationInput input)
    {
        input = new RevalidationInput([], string.Empty, false);
        if (plannedChangeSet.Targets is null || plannedChangeSet.Targets.Any(record => record is null))
        {
            throw SetupPlanResult.InvalidOutput();
        }

        if (plannedChangeSet.Targets.Any(record => !TryGetTargetForLabel(record.TargetLabel, out _)))
        {
            return false;
        }

        ValidatePersistedPlanAndLedger(plan, plannedChangeSet);
        if (plan.Adapter != "github-copilot" || plannedChangeSet.Adapter != "github-copilot" ||
            plan.SelectedTarget != plannedChangeSet.SelectedTarget)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var routedTargets = new List<string>();
        var endpoints = new List<string>();
        var captureFlags = new List<bool>();
        for (var index = 0; index < plannedChangeSet.Targets.Count; index++)
        {
            var ledgerTarget = plannedChangeSet.Targets[index];
            var planTarget = plan.Targets[index];
            if (!TryGetTargetForLabel(ledgerTarget.TargetLabel, out var target) ||
                !HasExpectedPersistedShape(target, ledgerTarget, planTarget, out var includeContentCapture))
            {
                throw SetupPlanResult.InvalidOutput();
            }

            if (target == AppSdkTarget)
            {
                continue;
            }

            if (!routedTargets.Contains(target, StringComparer.Ordinal))
            {
                routedTargets.Add(target);
            }

            endpoints.Add(ledgerTarget.StatusProjection.Endpoint!);
            captureFlags.Add(includeContentCapture);
        }

        if (!HasExpectedPersistedAggregateShape(plan.SelectedTarget, plannedChangeSet.Targets))
        {
            return false;
        }

        if (routedTargets.Count == 0)
        {
            input = new RevalidationInput([], string.Empty, false);
            return true;
        }

        var endpoint = endpoints[0];
        if (endpoints.Any(value => !string.Equals(value, endpoint, StringComparison.Ordinal)) ||
            captureFlags.Any(value => value != captureFlags[0]))
        {
            throw SetupPlanResult.InvalidOutput();
        }

        input = new RevalidationInput(routedTargets, endpoint, captureFlags[0]);
        return true;
    }

    private static void ValidatePersistedPlanAndLedger(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
    {
        try
        {
            SetupStorageValidation.ValidatePlanAndLedger(plan, plannedChangeSet);
        }
        catch (Exception exception) when (exception is SetupStorageException or FormatException or ArgumentException)
        {
            throw SetupPlanResult.InvalidOutput();
        }
    }

    private static bool HasExpectedPersistedShape(
        string target,
        SetupLedgerTarget ledgerTarget,
        SetupPrivatePlanTarget planTarget,
        out bool includeContentCapture)
    {
        includeContentCapture = false;
        if (ledgerTarget.OwningAdapter != "github-copilot" || ledgerTarget.StatusProjection is null ||
            ledgerTarget.TargetKind != planTarget.TargetKind)
        {
            return false;
        }

        return target switch
        {
            VsCodeTarget => ledgerTarget.TargetKind == SetupTargetKind.Json &&
                ledgerTarget.TargetLabel is VsCodeStableLabel or VsCodeInsidersLabel &&
                ledgerTarget.StatusProjection.ExpectedResult is { } vsCodeExpectedResult &&
                SourceCapabilityManifestLoader.IsValidLedgerManifest(vsCodeExpectedResult, "github-copilot-vscode") &&
                TryGetWritableCaptureFlag(planTarget.Members, VsCodeMembers, VsCodeRequiredValues, "github.copilot.chat.otel.otlpEndpoint", VsCodeCaptureMember, ledgerTarget.StatusProjection.Endpoint, out includeContentCapture),
            CliTarget => ledgerTarget.TargetKind == SetupTargetKind.Env &&
                ledgerTarget.TargetLabel == CliLabel &&
                ledgerTarget.StatusProjection.ExpectedResult is { } cliExpectedResult &&
                SourceCapabilityManifestLoader.IsValidLedgerManifest(cliExpectedResult, "github-copilot-cli") &&
                TryGetWritableCaptureFlag(planTarget.Members, CliMembers, CliRequiredValues, "OTEL_EXPORTER_OTLP_ENDPOINT", CliCaptureMember, ledgerTarget.StatusProjection.Endpoint, out includeContentCapture),
            AppSdkTarget => ledgerTarget.TargetKind == SetupTargetKind.Guidance &&
                ledgerTarget.TargetLabel == AppSdkLabel &&
                planTarget.Members.Count == 0 && ledgerTarget.Members.Count == 0 &&
                ledgerTarget.StatusProjection.Endpoint is null && ledgerTarget.StatusProjection.ExpectedResult is null &&
                ledgerTarget.StatusProjection.Operation == SetupOperation.NoOp &&
                ledgerTarget.StatusProjection.EffectiveSource is null &&
                ledgerTarget.StatusProjection.Guidance is { Kind: "caller_managed_sample", Language: "dotnet" } &&
                ledgerTarget.StatusProjection.Changes.Count == 0 &&
                ledgerTarget.RestartRequirement == SetupRestartRequirement.None,
            _ => false,
        };
    }

    private static bool TryGetWritableCaptureFlag(
        IReadOnlyList<SetupPrivatePlanMember> members,
        IReadOnlyList<string> requiredMembers,
        IReadOnlyDictionary<string, string> requiredValues,
        string endpointMember,
        string captureMember,
        string? endpoint,
        out bool includeContentCapture)
    {
        includeContentCapture = false;
        if (endpoint is null || members is null || members.Count is < 3 or > 5 ||
            members.Any(member => member is null) ||
            members.Select(member => member.SettingKey).Distinct(StringComparer.Ordinal).Count() != members.Count)
        {
            return false;
        }

        var byKey = members.ToDictionary(member => member.SettingKey, StringComparer.Ordinal);
        if (!requiredMembers.All(byKey.ContainsKey) ||
            byKey.Keys.Any(key => !requiredMembers.Contains(key, StringComparer.Ordinal) && key != captureMember) ||
            byKey[endpointMember].DesiredValue != endpoint ||
            requiredValues.Any(pair => byKey[pair.Key].DesiredValue != pair.Value) ||
            byKey.Values.Any(member => member.Operation == SetupOperation.Remove))
        {
            return false;
        }

        if (!byKey.TryGetValue(captureMember, out var capture))
        {
            return true;
        }

        includeContentCapture = capture.DesiredValue == "true";
        return includeContentCapture;
    }

    private static bool HasExpectedPersistedAggregateShape(string selectedTarget, IReadOnlyList<SetupLedgerTarget> targets)
    {
        var labels = targets.Select(target => target.TargetLabel).ToArray();
        if (labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
        {
            return false;
        }

        return selectedTarget switch
        {
            VsCodeTarget => labels.Length is 1 or 2 && labels.All(label => label is VsCodeStableLabel or VsCodeInsidersLabel) &&
                labels.SequenceEqual(labels.OrderBy(LabelOrder)),
            CliTarget => labels.SequenceEqual([CliLabel]),
            AppSdkTarget => labels.SequenceEqual([AppSdkLabel]),
            AllTargets => labels.Length is 3 or 4 &&
                labels.Count(label => label is VsCodeStableLabel or VsCodeInsidersLabel) is 1 or 2 &&
                labels.Count(label => label == CliLabel) == 1 &&
                labels.Count(label => label == AppSdkLabel) == 1 &&
                labels.SequenceEqual(labels.OrderBy(LabelOrder)),
            _ => false,
        };
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

    private sealed record RevalidationInput(
        IReadOnlyCollection<string> RoutedTargets,
        string Endpoint,
        bool IncludeContentCapture);
}

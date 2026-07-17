using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Documents;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;

internal sealed class ClaudeCodeSetupAdapter : ISetupAdapter
{
    private const string CliTarget = "cli";
    private const string AppSdkTarget = "app-sdk";
    private const string AllTargets = "all";
    private const string SettingsLabel = "claude-code-user-settings";
    private const int MaximumSettingsBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] ContentGateKeys =
    [
        "OTEL_LOG_USER_PROMPTS",
        "OTEL_LOG_TOOL_DETAILS",
        "OTEL_LOG_TOOL_CONTENT",
    ];
    private static readonly string[] HookEvents =
    [
        "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest",
        "PostToolUse", "PostToolUseFailure", "SubagentStart", "SubagentStop",
        "Stop", "StopFailure", "SessionEnd",
    ];

    private readonly ISetupPlatform platform;
    private readonly ClaudeAgentSdkTargetPartition agentSdk;
    private readonly IClaudeHookCommandProvider hookCommandProvider;
    private readonly ClaudeHigherPrecedenceObserver higherPrecedence;

    public ClaudeCodeSetupAdapter(
        ISetupPlatform platform,
        ClaudeAgentSdkTargetPartition agentSdk,
        IClaudeHookCommandProvider hookCommandProvider,
        ClaudeHigherPrecedenceObserver higherPrecedence)
    {
        this.platform = platform ?? throw new ArgumentNullException(nameof(platform));
        this.agentSdk = agentSdk ?? throw new ArgumentNullException(nameof(agentSdk));
        this.hookCommandProvider = hookCommandProvider ?? throw new ArgumentNullException(nameof(hookCommandProvider));
        this.higherPrecedence = higherPrecedence ?? throw new ArgumentNullException(nameof(higherPrecedence));
    }

    public string AdapterId => "claude-code";

    public SetupPlanResult<SetupChangePlan> Plan(SetupPlanRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Adapter != AdapterId)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        if (request.SelectedTarget is not (CliTarget or AppSdkTarget or AllTargets))
        {
            return SetupPlanResult.Failure<SetupChangePlan>(SetupCodes.UnsupportedTarget);
        }

        if (request.SelectedTarget == AppSdkTarget && request.AllowWsl2Routing)
        {
            return SetupPlanResult.Failure<SetupChangePlan>(SetupCodes.InvalidArguments);
        }

        var records = new List<SetupChangeRecord>();
        var warnings = new List<string>();
        var nextActions = new List<string>();
        if (request.SelectedTarget is CliTarget or AllTargets)
        {
            var cli = PlanCli(request);
            if (cli is SetupPlanFailure<CliPlan> failure)
            {
                return SetupPlanResult.Failure<SetupChangePlan>(failure.Code, failure.Warnings, failure.NextActions);
            }

            var success = (SetupPlanSuccess<CliPlan>)cli;
            records.Add(success.Value.Record);
            AddDistinct(warnings, success.Warnings);
            AddDistinct(nextActions, success.NextActions);
        }

        if (request.SelectedTarget is AppSdkTarget or AllTargets)
        {
            records.AddRange(agentSdk.Plan(platform, request));
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
        try
        {
            SetupStorageValidation.ValidatePlanAndLedger(plan, plannedChangeSet);
        }
        catch (Exception exception) when (exception is SetupStorageException or FormatException or ArgumentException)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var guidanceIncludesContent = ClaudeAgentSdkGuidanceVariant.ValidatePair(plan, plannedChangeSet);

        if (plan.Adapter != AdapterId || plannedChangeSet.Adapter != AdapterId ||
            plan.SelectedTarget != plannedChangeSet.SelectedTarget ||
            plan.SelectedTarget is not (CliTarget or AppSdkTarget or AllTargets))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.UnsupportedTarget);
        }

        var writablePlanTargets = plan.Targets
            .Where(target => target.TargetKind != SetupTargetKind.Guidance)
            .ToArray();
        if (writablePlanTargets.Length == 0)
        {
            return SetupPlanResult.Revalidated();
        }

        if (writablePlanTargets.Length != 1 || writablePlanTargets[0].DesiredState is not SetupClaudeSettingsOwnedValuesDesiredState desired ||
            plannedChangeSet.Targets.Single(target => target.RecordId == writablePlanTargets[0].RecordId).StatusProjection.Endpoint is not { } endpoint)
        {
            throw SetupPlanResult.InvalidOutput();
        }

        if (plan.SelectedTarget == AllTargets &&
            guidanceIncludesContent != desired.OwnedEnv.Any(value => ContentGateKeys.Contains(value.Key, StringComparer.Ordinal)))
        {
            throw SetupPlanResult.InvalidOutput();
        }

        var allowWsl2Routing = platform.OperatingSystem.Current == SetupPlanningOs.Linux;
        var request = new SetupPlanRequest(
            plan.Adapter,
            CliTarget,
            endpoint,
            desired.OwnedEnv.Any(value => ContentGateKeys.Contains(value.Key, StringComparer.Ordinal)),
            plan.ChangeSetId,
            plan.CreatedAt,
            plan.ToolVersion,
            allowWsl2Routing);
        var result = PlanCli(request, writablePlanTargets[0].RecordId, writablePlanTargets[0].TargetLocation);
        if (result is SetupPlanFailure<CliPlan> failure)
        {
            return SetupPlanResult.Failure<SetupRevalidation>(failure.Code, failure.Warnings, failure.NextActions);
        }

        var cliPlan = ((SetupPlanSuccess<CliPlan>)result).Value;
        var record = cliPlan.Record;
        if (record.DesiredState is not SetupClaudeSettingsOwnedValuesDesiredState actual ||
            !string.Equals(actual.ExpectedStateHash, desired.ExpectedStateHash, StringComparison.Ordinal) ||
            !actual.OwnedEnv.SequenceEqual(desired.OwnedEnv) ||
            !HooksEqual(actual.OwnedHooks, desired.OwnedHooks))
        {
            return SetupPlanResult.Failure<SetupRevalidation>(SetupCodes.StalePlan);
        }

        var materialized = writablePlanTargets[0].Members.Any(member => member.Operation != SetupOperation.NoOp)
            ? new[] { new SetupMaterializedTarget(record.RecordId, cliPlan.DesiredBytes, SetupHash.File(true, cliPlan.DesiredBytes)) }
            : [];
        var nextActions = ((SetupPlanSuccess<CliPlan>)result).NextActions.ToList();
        if (materialized.Length > 0
            && !nextActions.Contains(SetupCodes.RunFirstTraceDoctor, StringComparer.Ordinal))
        {
            var restartIndex = nextActions.IndexOf(SetupCodes.RestartClaudeProcess);
            nextActions.Insert(
                restartIndex < 0 ? nextActions.Count : restartIndex + 1,
                SetupCodes.RunFirstTraceDoctor);
        }
        return SetupPlanResult.Revalidated(
            materialized,
            ((SetupPlanSuccess<CliPlan>)result).Warnings,
            nextActions);
    }

    private SetupPlanResult<CliPlan> PlanCli(
        SetupPlanRequest request,
        Guid? recordId = null,
        string? expectedTargetLocation = null)
    {
        var version = ClaudeCodeVersionDetector.Detect(platform);
        if (!version.IsSupported)
        {
            return SetupPlanResult.Failure<CliPlan>(version.FailureCode!);
        }

        var execution = ClaudeCodeExecutionContextDetector.Detect(platform, request.AllowWsl2Routing);
        if (execution.FailureCode is not null)
        {
            return SetupPlanResult.Failure<CliPlan>(execution.FailureCode);
        }

        var settingsPath = Path.Combine(platform.OperatingSystem.UserProfile, ".claude", "settings.json");
        if (expectedTargetLocation is not null &&
            !string.Equals(settingsPath, expectedTargetLocation, StringComparison.Ordinal))
        {
            return SetupPlanResult.Failure<CliPlan>(SetupCodes.UnsupportedTarget);
        }

        var readiness = ClaudeCodeReadinessProbe.Probe(platform, request.Endpoint, execution.Context);
        if (!readiness.Reachable)
        {
            return SetupPlanResult.Failure<CliPlan>(readiness.FailureCode!);
        }

        var hookCommand = hookCommandProvider.Resolve();
        if (hookCommand is null)
        {
            return SetupPlanResult.Failure<CliPlan>(SetupCodes.InternalError);
        }

        if (execution.Context == ClaudeCodeExecutionContext.Wsl2Repository &&
            hookCommand.Mode != ClaudeHookCommandMode.Repository)
        {
            return SetupPlanResult.Failure<CliPlan>(SetupCodes.InternalError);
        }

        var requestedEnv = CreateOwnedEnv(request.Endpoint, request.IncludeContentCapture);
        var precedence = higherPrecedence.Observe(
            requestedEnv.Select(value => new KeyValuePair<string, string>(value.Key, value.Value)).ToArray(),
            request.IncludeContentCapture);
        if (precedence.FailureCode is not null)
        {
            return SetupPlanResult.Failure<CliPlan>(precedence.FailureCode);
        }

        var env = precedence.OwnedValues
            .Select(value => new SetupClaudeSettingsEnvValue(value.Key, value.Value))
            .ToArray();
        var userOwnedEnv = precedence.OwnedValues
            .Where(value => value.IsUserOwned)
            .Select(value => new ClaudeSettingsEnvValue(value.Key, value.Value))
            .ToArray();

        var hooks = CreateOwnedHooks(hookCommand, request.Endpoint, version.Version!);
        byte[] currentBytes;
        bool exists;
        try
        {
            exists = platform.FileSystem.FileExists(settingsPath);
            if (exists)
            {
                var read = platform.FileSystem.ReadAtMostBytes(settingsPath, MaximumSettingsBytes);
                if (!read.IsComplete)
                {
                    return SetupPlanResult.Failure<CliPlan>(SetupCodes.MalformedSettings);
                }

                currentBytes = read.Bytes;
            }
            else
            {
                currentBytes = "{}\n"u8.ToArray();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return SetupPlanResult.Failure<CliPlan>(SetupCodes.PermissionDenied);
        }

        ClaudeSettingsPlanResult documentPlan;
        try
        {
            var document = ClaudeSettingsDocument.Parse(StrictUtf8.GetString(currentBytes));
            documentPlan = document.Plan(
                userOwnedEnv,
                hooks.Select(hook => new ClaudeSettingsHook(hook.EventName, hook.Command, hook.Arguments, hook.TimeoutSeconds)).ToArray());
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException or ArgumentException)
        {
            return SetupPlanResult.Failure<CliPlan>(SetupCodes.MalformedSettings);
        }

        if (documentPlan.Disposition == ClaudeSettingsPlanDisposition.HookCommandConflict)
        {
            return SetupPlanResult.Failure<CliPlan>(SetupCodes.HookCommandConflict);
        }

        var desiredBytes = StrictUtf8.GetBytes(documentPlan.RenderedContent!);
        var memberOperations = ClassifyMemberOperations(currentBytes, precedence.OwnedValues, hooks);
        var members = env.Select(value => Member($"env.{value.Key}", memberOperations[$"env.{value.Key}"]))
            .Concat(hooks.Select(hook => Member($"hooks.{hook.EventName}", memberOperations[$"hooks.{hook.EventName}"])))
            .ToArray();
        var operation = AggregateOperation(members.Select(member => member.Operation));
        var changes = members.Select(member => new SetupMemberChangeResult(
            member.SettingKey,
            member.Operation,
            member.Operation == SetupOperation.Add ? "absent" : member.Operation == SetupOperation.NoOp ? "present_equal" : "present_different",
            member.SettingKey.StartsWith("hooks.", StringComparison.Ordinal) ? "configured_hook" :
                member.SettingKey.EndsWith("ENDPOINT", StringComparison.Ordinal) ? "configured_loopback" : "configured_enabled",
            "none",
            precedence.OwnedValues.SingleOrDefault(value => $"env.{value.Key}" == member.SettingKey)?.EffectiveSource == SetupEffectiveSource.ManagedPolicy)).ToArray();
        var desiredState = new SetupClaudeSettingsOwnedValuesDesiredState(
            SetupHash.File(true, desiredBytes), env, hooks);
        var effectiveSource = EffectiveSource(precedence.OwnedValues, currentBytes, env, hooks);
        var projection = new SetupStatusProjection(
            true,
            version.Version,
            operation,
            effectiveSource,
            request.Endpoint,
            SourceCapabilityManifestLoader.LoadForSurface("claude-code").CanonicalJson,
            null,
            changes);
        var record = new SetupChangeRecord(
            recordId ?? platform.Identifiers.CreateUuidV7(),
            SetupTargetKind.Json,
            settingsPath,
            SettingsLabel,
            SetupHash.File(exists, exists ? currentBytes : []),
            desiredState,
            members,
            operation == SetupOperation.NoOp ? SetupRestartRequirement.None : SetupRestartRequirement.RestartAgentProcess,
            projection);
        var warnings = request.IncludeContentCapture
            ? new[] { SetupCodes.ClaudeHooksCaptureRawContent, SetupCodes.ContentCaptureSensitive }
            : [SetupCodes.ClaudeHooksCaptureRawContent];
        var actions = new List<string>();
        if (operation != SetupOperation.NoOp)
        {
            actions.Add(SetupCodes.RestartClaudeProcess);
        }
        if (request.IncludeContentCapture)
        {
            actions.Add(SetupCodes.ReviewContentCaptureWarning);
        }
        return SetupPlanResult.Success(new CliPlan(record, desiredBytes), [SetupPlanTarget.FromRecord(record)], warnings, actions);
    }

    private static IReadOnlyList<SetupClaudeSettingsEnvValue> CreateOwnedEnv(string endpoint, bool includeContentCapture)
    {
        var values = new List<SetupClaudeSettingsEnvValue>
        {
            new("CLAUDE_CODE_ENABLE_TELEMETRY", "1"),
            new("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1"),
            new("OTEL_TRACES_EXPORTER", "otlp"),
            new("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf"),
            new("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", $"{endpoint}/v1/traces"),
        };
        if (includeContentCapture)
        {
            values.AddRange(ContentGateKeys.Select(key => new SetupClaudeSettingsEnvValue(key, "1")));
        }

        return values;
    }

    private static IReadOnlyList<SetupClaudeSettingsHook> CreateOwnedHooks(
        ClaudeHookCommand hookCommand,
        string endpoint,
        string version) =>
        HookEvents.Select(eventName => new SetupClaudeSettingsHook(
            eventName,
            hookCommand.Command,
            [
                .. hookCommand.ArgumentPrefix,
                "hook-forward", "--endpoint", endpoint, "--timeout-ms", "250",
                "--source", "claude-code", "--source-version", version,
            ],
            5)).ToArray();

    private static IReadOnlyDictionary<string, SetupOperation> ClassifyMemberOperations(
        byte[] currentBytes,
        IReadOnlyList<ClaudeObservedOwnedValue> env,
        IReadOnlyList<SetupClaudeSettingsHook> hooks)
    {
        using var document = JsonDocument.Parse(currentBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var operations = new Dictionary<string, SetupOperation>(StringComparer.Ordinal);
        var root = document.RootElement;
        var hasEnv = root.TryGetProperty("env", out var existingEnv);
        foreach (var desired in env)
        {
            operations[$"env.{desired.Key}"] = !desired.IsUserOwned
                ? SetupOperation.NoOp
                : !hasEnv || !existingEnv.TryGetProperty(desired.Key, out var current)
                ? SetupOperation.Add
                : current.GetString() == desired.Value ? SetupOperation.NoOp : SetupOperation.Replace;
        }

        var hasHooks = root.TryGetProperty("hooks", out var existingHooks);
        foreach (var desired in hooks)
        {
            operations[$"hooks.{desired.EventName}"] = !hasHooks ||
                !existingHooks.TryGetProperty(desired.EventName, out var groups) ||
                !groups.EnumerateArray().Any(group => IsExactOwned(group, desired))
                    ? SetupOperation.Add
                    : SetupOperation.NoOp;
        }

        return operations;
    }

    private static bool IsExactOwned(JsonElement group, SetupClaudeSettingsHook desired)
    {
        if (group.ValueKind != JsonValueKind.Object || group.EnumerateObject().Count() != 1 ||
            !group.TryGetProperty("hooks", out var handlers) || handlers.ValueKind != JsonValueKind.Array || handlers.GetArrayLength() != 1)
        {
            return false;
        }

        var handler = handlers[0];
        return handler.ValueKind == JsonValueKind.Object && handler.EnumerateObject().Count() == 4 &&
            handler.TryGetProperty("type", out var type) && type.GetString() == "command" &&
            handler.TryGetProperty("command", out var command) && command.GetString() == desired.Command &&
            handler.TryGetProperty("args", out var arguments) && arguments.ValueKind == JsonValueKind.Array &&
            handler.TryGetProperty("timeout", out var timeout) && timeout.TryGetInt32(out var seconds) && seconds == desired.TimeoutSeconds &&
            arguments.EnumerateArray().Select(argument => argument.GetString()).SequenceEqual(desired.Arguments, StringComparer.Ordinal);
    }

    private static SetupOperation AggregateOperation(IEnumerable<SetupOperation> operations)
    {
        var changed = operations.Where(operation => operation != SetupOperation.NoOp).Distinct().ToArray();
        return changed.Length switch
        {
            0 => SetupOperation.NoOp,
            1 => changed[0],
            _ => SetupOperation.Mixed,
        };
    }

    private static SetupEffectiveSource EffectiveSource(
        IReadOnlyList<ClaudeObservedOwnedValue> observed,
        byte[] currentBytes,
        IReadOnlyList<SetupClaudeSettingsEnvValue> env,
        IReadOnlyList<SetupClaudeSettingsHook> hooks)
    {
        if (observed.Any(value => value.EffectiveSource == SetupEffectiveSource.Environment))
        {
            return SetupEffectiveSource.Environment;
        }

        if (observed.Any(value => value.EffectiveSource == SetupEffectiveSource.ManagedPolicy))
        {
            return SetupEffectiveSource.ManagedPolicy;
        }

        if (observed.Any(value => value.EffectiveSource == SetupEffectiveSource.UserSetting) ||
            HasCurrentOwnedSetting(currentBytes, env, hooks))
        {
            return SetupEffectiveSource.UserSetting;
        }

        return SetupEffectiveSource.Default;
    }

    private static bool HasCurrentOwnedSetting(
        byte[] currentBytes,
        IReadOnlyList<SetupClaudeSettingsEnvValue> env,
        IReadOnlyList<SetupClaudeSettingsHook> hooks)
    {
        using var document = JsonDocument.Parse(currentBytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
        });
        var root = document.RootElement;
        if (root.TryGetProperty("env", out var currentEnv) &&
            env.Any(value => currentEnv.TryGetProperty(value.Key, out _)))
        {
            return true;
        }

        return root.TryGetProperty("hooks", out var currentHooks) &&
            hooks.Any(hook => currentHooks.TryGetProperty(hook.EventName, out _));
    }

    private static SetupPrivatePlanMember Member(string key, SetupOperation operation) =>
        new(key, operation, "configured");

    private static bool HooksEqual(
        IReadOnlyList<SetupClaudeSettingsHook> left,
        IReadOnlyList<SetupClaudeSettingsHook> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            pair.First.EventName == pair.Second.EventName &&
            pair.First.Command == pair.Second.Command &&
            pair.First.TimeoutSeconds == pair.Second.TimeoutSeconds &&
            pair.First.Arguments.SequenceEqual(pair.Second.Arguments, StringComparer.Ordinal));

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

    private sealed record CliPlan(SetupChangeRecord Record, byte[] DesiredBytes);
}

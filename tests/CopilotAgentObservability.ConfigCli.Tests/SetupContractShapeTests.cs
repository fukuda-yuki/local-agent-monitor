using System.Text.Json;
using System.Reflection;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupContractShapeTests
{
    [Fact]
    public void Serialize_PlanResult_WritesTheExactSetupV1TopLevelShapeInOrder()
    {
        var result = new SetupCommandResult(
            SetupCommand.Plan,
            Success: true,
            SetupCodes.PlanReady,
            ChangeSetId: "00000000-0000-7000-8000-000000000000",
            RecoveredChangeSetId: null,
            RecoveryOperation: null,
            Adapter: "github-copilot",
            Targets: [],
            ChangeSets: [],
            Warnings: [],
            NextActions: [],
            Truncated: false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;

        Assert.Equal(
            [
                "contract_version", "command", "success", "code", "change_set_id",
                "recovered_change_set_id", "recovery_operation", "adapter", "targets",
                "change_sets", "warnings", "next_actions", "truncated"
            ],
            root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(SetupCodes.ContractVersion, root.GetProperty("contract_version").GetString());
        Assert.Equal("plan", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(SetupCodes.PlanReady, root.GetProperty("code").GetString());
        Assert.Equal("00000000-0000-7000-8000-000000000000", root.GetProperty("change_set_id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recovered_change_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recovery_operation").ValueKind);
        Assert.Equal("github-copilot", root.GetProperty("adapter").GetString());
        Assert.Equal(JsonValueKind.Array, root.GetProperty("targets").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("change_sets").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("warnings").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("next_actions").ValueKind);
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void Serialize_StatusResult_WritesTargetMemberGuidanceAndChangeSetShapesWithoutReordering()
    {
        using var expectedResultDocument = JsonDocument.Parse("""{"source_surface":"github-copilot-vscode","signals":{"trace":{"availability":"available"}}}""");
        var target = new SetupTargetResult(
            RecordId: "00000000-0000-7000-8000-000000000001",
            TargetKind: SetupTargetKind.Json,
            TargetLabel: "vscode-user-settings",
            Detected: true,
            DetectedVersion: "1.0.4",
            Operation: SetupOperation.Replace,
            EffectiveSource: SetupEffectiveSource.Default,
            ReferenceState: SetupReferenceState.Desired,
            CurrentState: SetupCurrentState.Current,
            RestartRequirement: SetupRestartRequirement.None,
            RollbackAvailable: false,
            Endpoint: "http://127.0.0.1:4320",
            ExpectedResult: expectedResultDocument.RootElement.Clone(),
            Guidance: null,
            Changes:
            [
                new SetupMemberChangeResult(
                    "OTEL_EXPORTER_OTLP_ENDPOINT",
                    SetupOperation.Replace,
                    "present_different",
                    "configured_loopback",
                    "none",
                    Managed: false)
            ]);
        var guidanceTarget = new SetupTargetResult(
            RecordId: "00000000-0000-7000-8000-000000000004",
            TargetKind: SetupTargetKind.Guidance,
            TargetLabel: "app-sdk-guidance",
            Detected: true,
            DetectedVersion: "1.0.4",
            Operation: SetupOperation.NoOp,
            EffectiveSource: null,
            ReferenceState: SetupReferenceState.None,
            CurrentState: SetupCurrentState.NotApplicable,
            RestartRequirement: SetupRestartRequirement.None,
            RollbackAvailable: false,
            Endpoint: null,
            ExpectedResult: null,
            Guidance: new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()"),
            Changes: []);
        var changeSet = new SetupChangeSetStatusResult(
            ChangeSetId: "00000000-0000-7000-8000-000000000002",
            Adapter: "github-copilot",
            SelectedTarget: "all",
            CreatedAt: "2026-07-12T00:00:00Z",
            UpdatedAt: "2026-07-12T01:00:00Z",
            State: SetupChangeSetState.Applied,
            OutcomeCode: SetupCodes.ApplySucceeded,
            CurrentState: SetupCurrentState.Current,
            RollbackAvailable: true,
            Targets: [guidanceTarget]);
        var result = new SetupCommandResult(
            SetupCommand.Status,
            Success: true,
            SetupCodes.StatusReady,
            ChangeSetId: null,
            RecoveredChangeSetId: "00000000-0000-7000-8000-000000000003",
            RecoveryOperation: SetupRecoveryOperation.Apply,
            Adapter: "github-copilot",
            Targets: [target, guidanceTarget],
            ChangeSets: [changeSet],
            Warnings: [SetupCodes.MonitorNotRunning, SetupCodes.ContentCaptureSensitive],
            NextActions: [SetupCodes.StartLocalMonitor, SetupCodes.ReviewContentCaptureWarning],
            Truncated: false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;
        var serializedTarget = root.GetProperty("targets")[0];
        var serializedGuidanceTarget = root.GetProperty("targets")[1];
        var serializedChange = serializedTarget.GetProperty("changes")[0];
        var serializedStatus = root.GetProperty("change_sets")[0];

        Assert.Equal(
            [
                "record_id", "target_kind", "target_label", "detected", "detected_version",
                "operation", "effective_source", "reference_state", "current_state",
                "restart_requirement", "rollback_available", "endpoint", "expected_result",
                "guidance", "changes"
            ],
            serializedTarget.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("json", serializedTarget.GetProperty("target_kind").GetString());
        Assert.Equal("replace", serializedTarget.GetProperty("operation").GetString());
        Assert.Equal("default", serializedTarget.GetProperty("effective_source").GetString());
        Assert.Equal("desired", serializedTarget.GetProperty("reference_state").GetString());
        Assert.Equal("current", serializedTarget.GetProperty("current_state").GetString());
        Assert.Equal("none", serializedTarget.GetProperty("restart_requirement").GetString());
        Assert.Equal("github-copilot-vscode", serializedTarget.GetProperty("expected_result").GetProperty("source_surface").GetString());
        Assert.Equal(JsonValueKind.Null, serializedTarget.GetProperty("guidance").ValueKind);
        Assert.Equal("guidance", serializedGuidanceTarget.GetProperty("target_kind").GetString());
        Assert.Equal("no-op", serializedGuidanceTarget.GetProperty("operation").GetString());
        Assert.Equal("caller_managed_sample", serializedGuidanceTarget.GetProperty("guidance").GetProperty("kind").GetString());
        Assert.Equal("dotnet", serializedGuidanceTarget.GetProperty("guidance").GetProperty("language").GetString());
        Assert.Equal("new CopilotClientOptions()", serializedGuidanceTarget.GetProperty("guidance").GetProperty("sample").GetString());
        Assert.Equal("OTEL_EXPORTER_OTLP_ENDPOINT", serializedChange.GetProperty("setting_key").GetString());
        Assert.Equal("replace", serializedChange.GetProperty("operation").GetString());
        Assert.Equal("present_different", serializedChange.GetProperty("previous_state").GetString());
        Assert.Equal("configured_loopback", serializedChange.GetProperty("new_state").GetString());
        Assert.Equal("none", serializedChange.GetProperty("conflict").GetString());
        Assert.False(serializedChange.GetProperty("managed").GetBoolean());
        Assert.Equal(
            [
                "change_set_id", "adapter", "selected_target", "created_at", "updated_at", "state",
                "outcome_code", "current_state", "rollback_available", "targets"
            ],
            serializedStatus.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal("applied", serializedStatus.GetProperty("state").GetString());
        Assert.Equal("current", serializedStatus.GetProperty("current_state").GetString());
        Assert.False(serializedStatus.GetProperty("targets")[0].GetProperty("guidance").TryGetProperty("sample", out _));
        Assert.Equal(
            [SetupCodes.MonitorNotRunning, SetupCodes.ContentCaptureSensitive],
            root.GetProperty("warnings").EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            [SetupCodes.StartLocalMonitor, SetupCodes.ReviewContentCaptureWarning],
            root.GetProperty("next_actions").EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    [Fact]
    public void SetupCodes_DefinesEveryFixedResultCode()
    {
        Assert.Equal(
            [
                "plan_ready", "no_changes", "apply_succeeded", "rollback_succeeded", "status_ready",
                "interrupted_apply_recovered", "interrupted_rollback_recovered", "invalid_arguments",
                "unsupported_adapter", "unsupported_target", "target_not_installed", "unsupported_version",
                "managed_policy_conflict", "malformed_settings", "permission_denied", "unsafe_path",
                "stale_plan", "rollback_stale", "rollback_not_available", "port_owned_by_foreign_process",
                "partial_apply", "partial_rollback", "setup_busy", "recovery_required",
                "interrupted_recovery_failed", "ledger_corrupt", "ledger_version_unsupported", "internal_error"
            ],
            SetupCodes.ResultCodes);
    }

    [Fact]
    public void SetupCodes_DefinesEveryPublicFixedWarningAndNextActionCode()
    {
        var resultCodeValues = SetupCodes.ResultCodes.ToHashSet(StringComparer.Ordinal);
        var publicNonResultCodes = typeof(SetupCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly)
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .Where(field => field.Name != nameof(SetupCodes.ContractVersion) && !resultCodeValues.Contains(field.Value))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                (nameof(SetupCodes.ContentCaptureSensitive), SetupCodes.ContentCaptureSensitive),
                (nameof(SetupCodes.InstallCopilotCli), SetupCodes.InstallCopilotCli),
                (nameof(SetupCodes.InstallGitHubCopilotChatExtension), SetupCodes.InstallGitHubCopilotChatExtension),
                (nameof(SetupCodes.InstallVsCode), SetupCodes.InstallVsCode),
                (nameof(SetupCodes.ManagedPolicyUnverified), SetupCodes.ManagedPolicyUnverified),
                (nameof(SetupCodes.MonitorNotRunning), SetupCodes.MonitorNotRunning),
                (nameof(SetupCodes.RerunRequestedSetupCommand), SetupCodes.RerunRequestedSetupCommand),
                (nameof(SetupCodes.RestartTerminalSession), SetupCodes.RestartTerminalSession),
                (nameof(SetupCodes.RestartVsCode), SetupCodes.RestartVsCode),
                (nameof(SetupCodes.ReviewContentCaptureWarning), SetupCodes.ReviewContentCaptureWarning),
                (nameof(SetupCodes.RunFirstTraceDoctor), SetupCodes.RunFirstTraceDoctor),
                (nameof(SetupCodes.RunVsCodePolicyDiagnostics), SetupCodes.RunVsCodePolicyDiagnostics),
                (nameof(SetupCodes.SharedUserEnvironmentAffectsOtherProcesses), SetupCodes.SharedUserEnvironmentAffectsOtherProcesses),
                (nameof(SetupCodes.StartLocalMonitor), SetupCodes.StartLocalMonitor),
                (nameof(SetupCodes.UpgradeCopilotCli), SetupCodes.UpgradeCopilotCli),
                (nameof(SetupCodes.UpgradeVsCode), SetupCodes.UpgradeVsCode),
            ],
            publicNonResultCodes);
    }

    [Fact]
    public void Serialize_PlanGuidance_AlwaysWritesSampleAndNullableContractFields()
    {
        var sampleProperty = typeof(SetupGuidance).GetProperty(nameof(SetupGuidance.Sample))!;
        var nullability = new NullabilityInfoContext().Create(sampleProperty);
        var result = new SetupCommandResult(
            SetupCommand.Plan,
            Success: true,
            SetupCodes.PlanReady,
            ChangeSetId: "00000000-0000-7000-8000-000000000005",
            RecoveredChangeSetId: null,
            RecoveryOperation: null,
            Adapter: "github-copilot",
            Targets:
            [
                new SetupTargetResult(
                    "00000000-0000-7000-8000-000000000006",
                    SetupTargetKind.Guidance,
                    "app-sdk-guidance",
                    Detected: false,
                    DetectedVersion: null,
                    Operation: SetupOperation.NoOp,
                    EffectiveSource: null,
                    ReferenceState: null,
                    CurrentState: null,
                    SetupRestartRequirement.None,
                    RollbackAvailable: false,
                    Endpoint: null,
                    ExpectedResult: null,
                    Guidance: new SetupGuidance("caller_managed_sample", "dotnet", null!),
                    Changes: [])
            ],
            ChangeSets: [],
            Warnings: [],
            NextActions: [],
            Truncated: false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var target = document.RootElement.GetProperty("targets")[0];
        var guidance = target.GetProperty("guidance");

        Assert.Equal(NullabilityState.NotNull, nullability.ReadState);
        Assert.True(guidance.TryGetProperty("sample", out var sample));
        Assert.Equal(JsonValueKind.Null, sample.ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("detected_version").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("effective_source").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("reference_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("current_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("endpoint").ValueKind);
        Assert.Equal(JsonValueKind.Null, target.GetProperty("expected_result").ValueKind);
    }

    [Theory]
    [MemberData(nameof(CommandWireValues))]
    public void Serialize_Command_MapsEveryClosedEnumValue(SetupCommand command, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(command: command)));

        Assert.Equal(expected, document.RootElement.GetProperty("command").GetString());
    }

    [Theory]
    [MemberData(nameof(RecoveryOperationWireValues))]
    public void Serialize_RecoveryOperation_MapsEveryClosedEnumValue(SetupRecoveryOperation operation, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(recoveryOperation: operation)));

        Assert.Equal(expected, document.RootElement.GetProperty("recovery_operation").GetString());
    }

    [Theory]
    [MemberData(nameof(TargetKindWireValues))]
    public void Serialize_TargetKind_MapsEveryClosedEnumValue(SetupTargetKind targetKind, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(target: CreateTarget(targetKind: targetKind))));

        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("target_kind").GetString());
    }

    [Theory]
    [MemberData(nameof(OperationWireValues))]
    public void Serialize_Operation_MapsEveryClosedEnumValue(SetupOperation operation, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(target: CreateTarget(operation: operation))));

        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("operation").GetString());
        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("changes")[0].GetProperty("operation").GetString());
    }

    [Theory]
    [MemberData(nameof(EffectiveSourceWireValues))]
    public void Serialize_EffectiveSource_MapsEveryClosedEnumValue(SetupEffectiveSource source, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(target: CreateTarget(effectiveSource: source))));

        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("effective_source").GetString());
    }

    [Theory]
    [MemberData(nameof(ReferenceStateWireValues))]
    public void Serialize_ReferenceState_MapsEveryClosedEnumValue(SetupReferenceState state, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(target: CreateTarget(referenceState: state))));

        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("reference_state").GetString());
    }

    [Theory]
    [MemberData(nameof(CurrentStateWireValues))]
    public void Serialize_CurrentState_MapsEveryClosedEnumValue(SetupCurrentState state, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(target: CreateTarget(currentState: state))));

        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("current_state").GetString());
    }

    [Theory]
    [MemberData(nameof(RestartRequirementWireValues))]
    public void Serialize_RestartRequirement_MapsEveryClosedEnumValue(SetupRestartRequirement requirement, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(target: CreateTarget(restartRequirement: requirement))));

        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("restart_requirement").GetString());
    }

    [Theory]
    [MemberData(nameof(ChangeSetStateWireValues))]
    public void Serialize_ChangeSetState_MapsEveryClosedEnumValue(SetupChangeSetState state, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateResult(changeSetState: state)));

        Assert.Equal(expected, document.RootElement.GetProperty("change_sets")[0].GetProperty("state").GetString());
    }

    public static TheoryData<SetupCommand, string> CommandWireValues => new()
    {
        { SetupCommand.Plan, "plan" }, { SetupCommand.Apply, "apply" },
        { SetupCommand.Rollback, "rollback" }, { SetupCommand.Status, "status" },
    };

    public static TheoryData<SetupRecoveryOperation, string> RecoveryOperationWireValues => new()
    {
        { SetupRecoveryOperation.Apply, "apply" }, { SetupRecoveryOperation.Rollback, "rollback" },
    };

    public static TheoryData<SetupTargetKind, string> TargetKindWireValues => new()
    {
        { SetupTargetKind.Env, "env" }, { SetupTargetKind.Json, "json" }, { SetupTargetKind.Toml, "toml" },
        { SetupTargetKind.StartupTask, "startup_task" }, { SetupTargetKind.File, "file" }, { SetupTargetKind.Guidance, "guidance" },
    };

    public static TheoryData<SetupOperation, string> OperationWireValues => new()
    {
        { SetupOperation.Add, "add" }, { SetupOperation.Replace, "replace" }, { SetupOperation.Remove, "remove" },
        { SetupOperation.Mixed, "mixed" }, { SetupOperation.NoOp, "no-op" },
    };

    public static TheoryData<SetupEffectiveSource, string> EffectiveSourceWireValues => new()
    {
        { SetupEffectiveSource.ManagedPolicy, "managed_policy" }, { SetupEffectiveSource.Environment, "environment" },
        { SetupEffectiveSource.UserSetting, "user_setting" }, { SetupEffectiveSource.Default, "default" },
    };

    public static TheoryData<SetupReferenceState, string> ReferenceStateWireValues => new()
    {
        { SetupReferenceState.Base, "base" }, { SetupReferenceState.Desired, "desired" },
        { SetupReferenceState.Previous, "previous" }, { SetupReferenceState.None, "none" },
    };

    public static TheoryData<SetupCurrentState, string> CurrentStateWireValues => new()
    {
        { SetupCurrentState.Current, "current" }, { SetupCurrentState.Stale, "stale" },
        { SetupCurrentState.Diverged, "diverged" }, { SetupCurrentState.Unavailable, "unavailable" },
        { SetupCurrentState.NotApplicable, "not_applicable" },
    };

    public static TheoryData<SetupRestartRequirement, string> RestartRequirementWireValues => new()
    {
        { SetupRestartRequirement.None, "none" }, { SetupRestartRequirement.RestartVsCode, "restart_vscode" },
        { SetupRestartRequirement.RestartTerminalSession, "restart_terminal_session" },
    };

    public static TheoryData<SetupChangeSetState, string> ChangeSetStateWireValues => new()
    {
        { SetupChangeSetState.Planned, "planned" }, { SetupChangeSetState.Applying, "applying" },
        { SetupChangeSetState.Applied, "applied" }, { SetupChangeSetState.NoChanges, "no_changes" },
        { SetupChangeSetState.Compensating, "compensating" }, { SetupChangeSetState.Restored, "restored" },
        { SetupChangeSetState.RollingBack, "rolling_back" }, { SetupChangeSetState.Partial, "partial" },
        { SetupChangeSetState.RolledBack, "rolled_back" },
    };

    private static SetupCommandResult CreateResult(
        SetupCommand command = SetupCommand.Plan,
        SetupRecoveryOperation? recoveryOperation = null,
        SetupTargetResult? target = null,
        SetupChangeSetState? changeSetState = null)
    {
        target ??= CreateTarget();
        return new SetupCommandResult(
            command,
            Success: true,
            SetupCodes.PlanReady,
            ChangeSetId: null,
            RecoveredChangeSetId: recoveryOperation is null ? null : "00000000-0000-7000-8000-000000000007",
            RecoveryOperation: recoveryOperation,
            Adapter: "github-copilot",
            Targets: [target],
            ChangeSets: changeSetState is { } state
                ? [new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000008", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:00:00Z", state, null, SetupCurrentState.Current, true, [target])]
                : [],
            Warnings: [],
            NextActions: [],
            Truncated: false);
    }

    private static SetupTargetResult CreateTarget(
        SetupTargetKind targetKind = SetupTargetKind.Json,
        SetupOperation operation = SetupOperation.Replace,
        SetupEffectiveSource? effectiveSource = SetupEffectiveSource.UserSetting,
        SetupReferenceState? referenceState = SetupReferenceState.Desired,
        SetupCurrentState? currentState = SetupCurrentState.Current,
        SetupRestartRequirement restartRequirement = SetupRestartRequirement.RestartVsCode)
    {
        return new SetupTargetResult(
            "00000000-0000-7000-8000-000000000009",
            targetKind,
            "target",
            Detected: true,
            DetectedVersion: "1.128.0",
            operation,
            effectiveSource,
            referenceState,
            currentState,
            restartRequirement,
            RollbackAvailable: true,
            Endpoint: "http://127.0.0.1:4320",
            ExpectedResult: null,
            Guidance: null,
            Changes: [new SetupMemberChangeResult("setting", operation, "present_different", "configured_loopback", "none", Managed: false)]);
    }
}

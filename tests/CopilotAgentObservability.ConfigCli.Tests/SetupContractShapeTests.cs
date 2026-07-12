using System.Reflection;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupContractShapeTests
{
    [Fact]
    public void Serialize_RichPlan_WritesCompleteSetupV1StructureInContractOrder()
    {
        using var expectedResultDocument = JsonDocument.Parse("""{"source_surface":"github-copilot-vscode"}""");
        var writableTarget = new SetupTargetResult(
            "00000000-0000-7000-8000-000000000001", SetupTargetKind.Json, "vscode-user-settings", true, "1.128.0",
            SetupOperation.Replace, SetupEffectiveSource.UserSetting, null, null, SetupRestartRequirement.RestartVsCode, true,
            "http://127.0.0.1:4320", expectedResultDocument.RootElement.Clone(), null,
            [new SetupMemberChangeResult("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]);
        var guidanceTarget = new SetupTargetResult(
            "00000000-0000-7000-8000-000000000002", SetupTargetKind.Guidance, "app-sdk-guidance", false, null,
            SetupOperation.NoOp, null, null, null, SetupRestartRequirement.None, false, null, null,
            new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()"), []);
        var result = new SetupCommandResult(
            SetupCommand.Plan, true, "plan_ready", "00000000-0000-7000-8000-000000000003", null, null, "github-copilot",
            [writableTarget, guidanceTarget], [], ["monitor_not_running"], ["start_local_monitor"], false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;
        var writable = root.GetProperty("targets")[0];
        var guidance = root.GetProperty("targets")[1];
        var change = writable.GetProperty("changes")[0];

        AssertPropertyOrder(root, "contract_version", "command", "success", "code", "change_set_id", "recovered_change_set_id", "recovery_operation", "adapter", "targets", "change_sets", "warnings", "next_actions", "truncated");
        Assert.Equal("setup.v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("plan", root.GetProperty("command").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("plan_ready", root.GetProperty("code").GetString());
        Assert.Equal("00000000-0000-7000-8000-000000000003", root.GetProperty("change_set_id").GetString());
        Assert.Null(root.GetProperty("recovered_change_set_id").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recovery_operation").ValueKind);
        Assert.Equal("github-copilot", root.GetProperty("adapter").GetString());
        Assert.Equal("monitor_not_running", root.GetProperty("warnings")[0].GetString());
        Assert.Equal("start_local_monitor", root.GetProperty("next_actions")[0].GetString());
        Assert.False(root.GetProperty("truncated").GetBoolean());

        AssertTargetShape(writable);
        Assert.Equal("json", writable.GetProperty("target_kind").GetString());
        Assert.Equal("1.128.0", writable.GetProperty("detected_version").GetString());
        Assert.Equal("replace", writable.GetProperty("operation").GetString());
        Assert.Equal("user_setting", writable.GetProperty("effective_source").GetString());
        Assert.Equal(JsonValueKind.Null, writable.GetProperty("reference_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, writable.GetProperty("current_state").ValueKind);
        Assert.Equal("restart_vscode", writable.GetProperty("restart_requirement").GetString());
        Assert.Equal("http://127.0.0.1:4320", writable.GetProperty("endpoint").GetString());
        Assert.Equal("github-copilot-vscode", writable.GetProperty("expected_result").GetProperty("source_surface").GetString());
        Assert.Equal(JsonValueKind.Null, writable.GetProperty("guidance").ValueKind);
        AssertMemberShape(change);
        Assert.Equal("github.copilot.chat.otel.otlpEndpoint", change.GetProperty("setting_key").GetString());
        Assert.Equal("replace", change.GetProperty("operation").GetString());
        Assert.Equal("present_different", change.GetProperty("previous_state").GetString());
        Assert.Equal("configured_loopback", change.GetProperty("new_state").GetString());
        Assert.Equal("none", change.GetProperty("conflict").GetString());
        Assert.False(change.GetProperty("managed").GetBoolean());

        AssertTargetShape(guidance);
        Assert.Equal("guidance", guidance.GetProperty("target_kind").GetString());
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("detected_version").ValueKind);
        Assert.Equal("no-op", guidance.GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("effective_source").ValueKind);
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("reference_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("current_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("endpoint").ValueKind);
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("expected_result").ValueKind);
        Assert.Empty(guidance.GetProperty("changes").EnumerateArray());
        var planGuidance = guidance.GetProperty("guidance");
        AssertPropertyOrder(planGuidance, "kind", "language", "sample");
        Assert.Equal("caller_managed_sample", planGuidance.GetProperty("kind").GetString());
        Assert.Equal("dotnet", planGuidance.GetProperty("language").GetString());
        Assert.Equal("new CopilotClientOptions()", planGuidance.GetProperty("sample").GetString());
    }

    [Fact]
    public void Serialize_RecoveredStatus_WritesCorrelationAndNestedStatusSummaries()
    {
        var writable = CreateStatusWritableTarget();
        var guidance = CreateStatusGuidanceTarget();
        var result = new SetupCommandResult(
            SetupCommand.Status, true, "interrupted_apply_recovered", null, "00000000-0000-7000-8000-000000000010",
            SetupRecoveryOperation.Apply, "github-copilot", [],
            [
                new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000011", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T01:00:00Z", SetupChangeSetState.Applied, "apply_succeeded", SetupCurrentState.Current, true, [writable]),
                new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000012", "github-copilot", "app-sdk", "2026-07-12T02:00:00Z", "2026-07-12T03:00:00Z", SetupChangeSetState.Partial, null, SetupCurrentState.NotApplicable, false, [guidance]),
            ],
            [], ["rerun_requested_setup_command"], true);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;
        var applied = root.GetProperty("change_sets")[0];
        var partial = root.GetProperty("change_sets")[1];

        Assert.Equal("status", root.GetProperty("command").GetString());
        Assert.Equal("interrupted_apply_recovered", root.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("change_set_id").ValueKind);
        Assert.Equal("00000000-0000-7000-8000-000000000010", root.GetProperty("recovered_change_set_id").GetString());
        Assert.Equal("apply", root.GetProperty("recovery_operation").GetString());
        Assert.Empty(root.GetProperty("targets").EnumerateArray());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal("rerun_requested_setup_command", root.GetProperty("next_actions")[0].GetString());
        AssertChangeSetShape(applied);
        Assert.Equal("apply_succeeded", applied.GetProperty("outcome_code").GetString());
        Assert.Equal("current", applied.GetProperty("current_state").GetString());
        AssertChangeSetShape(partial);
        Assert.Equal(JsonValueKind.Null, partial.GetProperty("outcome_code").ValueKind);
        Assert.Equal("partial", partial.GetProperty("state").GetString());
        Assert.Equal("not_applicable", partial.GetProperty("current_state").GetString());
        var nestedGuidance = partial.GetProperty("targets")[0].GetProperty("guidance");
        AssertPropertyOrder(nestedGuidance, "kind", "language");
        Assert.False(nestedGuidance.TryGetProperty("sample", out _));
    }

    [Fact]
    public void Serialize_InvalidArguments_WritesFailureWithNullCorrelationAndEmptyArrays()
    {
        var result = new SetupCommandResult(SetupCommand.Plan, false, "invalid_arguments", null, null, null, null, [], [], [], [], false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;

        Assert.Equal("setup.v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("plan", root.GetProperty("command").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal("invalid_arguments", root.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("change_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recovered_change_set_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("recovery_operation").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("adapter").ValueKind);
        Assert.Empty(root.GetProperty("targets").EnumerateArray());
        Assert.Empty(root.GetProperty("change_sets").EnumerateArray());
        Assert.Empty(root.GetProperty("warnings").EnumerateArray());
        Assert.Empty(root.GetProperty("next_actions").EnumerateArray());
        Assert.False(root.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void SetupCodes_MatchTheIndependentSetupV1LiteralCatalog()
    {
        var actual = typeof(SetupCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && !field.IsInitOnly)
            .Select(field => (field.Name, Value: (string)field.GetRawConstantValue()!))
            .OrderBy(field => field.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                ("ApplySucceeded", "apply_succeeded"), ("ContentCaptureSensitive", "content_capture_sensitive"), ("ContractVersion", "setup.v1"),
                ("InstallCopilotCli", "install_copilot_cli"), ("InstallGitHubCopilotChatExtension", "install_github_copilot_chat_extension"), ("InstallVsCode", "install_vscode"),
                ("InternalError", "internal_error"), ("InterruptedApplyRecovered", "interrupted_apply_recovered"), ("InterruptedRecoveryFailed", "interrupted_recovery_failed"), ("InterruptedRollbackRecovered", "interrupted_rollback_recovered"),
                ("InvalidArguments", "invalid_arguments"), ("LedgerCorrupt", "ledger_corrupt"), ("LedgerVersionUnsupported", "ledger_version_unsupported"), ("MalformedSettings", "malformed_settings"),
                ("ManagedPolicyConflict", "managed_policy_conflict"), ("ManagedPolicyUnverified", "managed_policy_unverified"), ("MonitorNotRunning", "monitor_not_running"), ("NoChanges", "no_changes"),
                ("PartialApply", "partial_apply"), ("PartialRollback", "partial_rollback"), ("PermissionDenied", "permission_denied"), ("PlanReady", "plan_ready"), ("PortOwnedByForeignProcess", "port_owned_by_foreign_process"),
                ("RecoveryRequired", "recovery_required"), ("RerunRequestedSetupCommand", "rerun_requested_setup_command"), ("RestartTerminalSession", "restart_terminal_session"), ("RestartVsCode", "restart_vscode"),
                ("ReviewContentCaptureWarning", "review_content_capture_warning"), ("RollbackNotAvailable", "rollback_not_available"), ("RollbackStale", "rollback_stale"), ("RollbackSucceeded", "rollback_succeeded"),
                ("RunFirstTraceDoctor", "run_first_trace_doctor"), ("RunVsCodePolicyDiagnostics", "run_vscode_policy_diagnostics"), ("SetupBusy", "setup_busy"), ("SharedUserEnvironmentAffectsOtherProcesses", "shared_user_environment_affects_other_processes"),
                ("StalePlan", "stale_plan"), ("StartLocalMonitor", "start_local_monitor"), ("StatusReady", "status_ready"), ("TargetNotInstalled", "target_not_installed"), ("UnsafePath", "unsafe_path"),
                ("UnsupportedAdapter", "unsupported_adapter"), ("UnsupportedTarget", "unsupported_target"), ("UnsupportedVersion", "unsupported_version"), ("UpgradeCopilotCli", "upgrade_copilot_cli"), ("UpgradeVsCode", "upgrade_vscode"),
            ],
            actual);
    }

    [Theory]
    [MemberData(nameof(CommandWireValues))]
    public void Serialize_Command_MapsEveryExplicitEnumValue(SetupCommand value, string expected) => AssertWire(CreateCommandWireResult(value), "command", expected);

    [Theory]
    [MemberData(nameof(RecoveryOperationWireValues))]
    public void Serialize_RecoveryOperation_MapsEveryExplicitEnumValue(SetupRecoveryOperation value, string expected) => AssertWire(CreateRecoveredWireResult(value), "recovery_operation", expected);

    [Theory]
    [MemberData(nameof(TargetKindWireValues))]
    public void Serialize_TargetKind_MapsEveryExplicitEnumValue(SetupTargetKind value, string expected) => AssertWire(CreatePlanResult(CreatePlanTarget(targetKind: value)), "targets", 0, "target_kind", expected);

    [Theory]
    [MemberData(nameof(OperationWireValues))]
    public void Serialize_Operation_MapsEveryExplicitEnumValueInTargetAndMemberPositions(SetupOperation value, string expected)
    {
        var result = CreatePlanResult(CreatePlanTarget(operation: value));
        AssertWire(result, "targets", 0, "operation", expected);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal(expected, document.RootElement.GetProperty("targets")[0].GetProperty("changes")[0].GetProperty("operation").GetString());
    }

    [Theory]
    [MemberData(nameof(EffectiveSourceWireValues))]
    public void Serialize_EffectiveSource_MapsEveryExplicitEnumValue(SetupEffectiveSource value, string expected) => AssertWire(CreatePlanResult(CreatePlanTarget(effectiveSource: value)), "targets", 0, "effective_source", expected);

    [Theory]
    [MemberData(nameof(ReferenceStateWireValues))]
    public void Serialize_ReferenceState_MapsEveryExplicitEnumValue(SetupReferenceState value, string expected) => AssertNestedStatusTargetWire(CreateStatusResult(CreateStatusWritableTarget(referenceState: value)), "reference_state", expected);

    [Theory]
    [MemberData(nameof(CurrentStateWireValues))]
    public void Serialize_CurrentState_MapsEveryExplicitEnumValue(SetupCurrentState value, string expected) => AssertNestedStatusTargetWire(CreateStatusResult(CreateStatusWritableTarget(currentState: value)), "current_state", expected);

    [Theory]
    [MemberData(nameof(RestartRequirementWireValues))]
    public void Serialize_RestartRequirement_MapsEveryExplicitEnumValue(SetupRestartRequirement value, string expected) => AssertWire(CreatePlanResult(CreatePlanTarget(restartRequirement: value)), "targets", 0, "restart_requirement", expected);

    [Theory]
    [MemberData(nameof(ChangeSetStateWireValues))]
    public void Serialize_ChangeSetState_MapsEveryExplicitEnumValue(SetupChangeSetState value, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateStatusResult(CreateLifecycleStatusTarget(value), value)));
        Assert.Equal(expected, document.RootElement.GetProperty("change_sets")[0].GetProperty("state").GetString());
    }

    [Fact]
    public void ClosedEnums_ContainExactlyTheExplicitlyMappedMembers()
    {
        Assert.Equal([SetupCommand.Plan, SetupCommand.Apply, SetupCommand.Rollback, SetupCommand.Status], Enum.GetValues<SetupCommand>());
        Assert.Equal([SetupRecoveryOperation.Apply, SetupRecoveryOperation.Rollback], Enum.GetValues<SetupRecoveryOperation>());
        Assert.Equal([SetupTargetKind.Env, SetupTargetKind.Json, SetupTargetKind.Toml, SetupTargetKind.StartupTask, SetupTargetKind.File, SetupTargetKind.Guidance], Enum.GetValues<SetupTargetKind>());
        Assert.Equal([SetupOperation.Add, SetupOperation.Replace, SetupOperation.Remove, SetupOperation.Mixed, SetupOperation.NoOp], Enum.GetValues<SetupOperation>());
        Assert.Equal([SetupEffectiveSource.ManagedPolicy, SetupEffectiveSource.Environment, SetupEffectiveSource.UserSetting, SetupEffectiveSource.Default], Enum.GetValues<SetupEffectiveSource>());
        Assert.Equal([SetupReferenceState.Base, SetupReferenceState.Desired, SetupReferenceState.Previous, SetupReferenceState.None], Enum.GetValues<SetupReferenceState>());
        Assert.Equal([SetupCurrentState.Current, SetupCurrentState.Stale, SetupCurrentState.Diverged, SetupCurrentState.Unavailable, SetupCurrentState.NotApplicable], Enum.GetValues<SetupCurrentState>());
        Assert.Equal([SetupRestartRequirement.None, SetupRestartRequirement.RestartVsCode, SetupRestartRequirement.RestartTerminalSession], Enum.GetValues<SetupRestartRequirement>());
        Assert.Equal([SetupChangeSetState.Planned, SetupChangeSetState.Applying, SetupChangeSetState.Applied, SetupChangeSetState.NoChanges, SetupChangeSetState.Compensating, SetupChangeSetState.Restored, SetupChangeSetState.RollingBack, SetupChangeSetState.Partial, SetupChangeSetState.RolledBack], Enum.GetValues<SetupChangeSetState>());
    }

    public static TheoryData<SetupCommand, string> CommandWireValues => new() { { SetupCommand.Plan, "plan" }, { SetupCommand.Apply, "apply" }, { SetupCommand.Rollback, "rollback" }, { SetupCommand.Status, "status" } };
    public static TheoryData<SetupRecoveryOperation, string> RecoveryOperationWireValues => new() { { SetupRecoveryOperation.Apply, "apply" }, { SetupRecoveryOperation.Rollback, "rollback" } };
    public static TheoryData<SetupTargetKind, string> TargetKindWireValues => new() { { SetupTargetKind.Env, "env" }, { SetupTargetKind.Json, "json" }, { SetupTargetKind.Toml, "toml" }, { SetupTargetKind.StartupTask, "startup_task" }, { SetupTargetKind.File, "file" }, { SetupTargetKind.Guidance, "guidance" } };
    public static TheoryData<SetupOperation, string> OperationWireValues => new() { { SetupOperation.Add, "add" }, { SetupOperation.Replace, "replace" }, { SetupOperation.Remove, "remove" }, { SetupOperation.Mixed, "mixed" }, { SetupOperation.NoOp, "no-op" } };
    public static TheoryData<SetupEffectiveSource, string> EffectiveSourceWireValues => new() { { SetupEffectiveSource.ManagedPolicy, "managed_policy" }, { SetupEffectiveSource.Environment, "environment" }, { SetupEffectiveSource.UserSetting, "user_setting" }, { SetupEffectiveSource.Default, "default" } };
    public static TheoryData<SetupReferenceState, string> ReferenceStateWireValues => new() { { SetupReferenceState.Base, "base" }, { SetupReferenceState.Desired, "desired" }, { SetupReferenceState.Previous, "previous" }, { SetupReferenceState.None, "none" } };
    public static TheoryData<SetupCurrentState, string> CurrentStateWireValues => new() { { SetupCurrentState.Current, "current" }, { SetupCurrentState.Stale, "stale" }, { SetupCurrentState.Diverged, "diverged" }, { SetupCurrentState.Unavailable, "unavailable" }, { SetupCurrentState.NotApplicable, "not_applicable" } };
    public static TheoryData<SetupRestartRequirement, string> RestartRequirementWireValues => new() { { SetupRestartRequirement.None, "none" }, { SetupRestartRequirement.RestartVsCode, "restart_vscode" }, { SetupRestartRequirement.RestartTerminalSession, "restart_terminal_session" } };
    public static TheoryData<SetupChangeSetState, string> ChangeSetStateWireValues => new() { { SetupChangeSetState.Planned, "planned" }, { SetupChangeSetState.Applying, "applying" }, { SetupChangeSetState.Applied, "applied" }, { SetupChangeSetState.NoChanges, "no_changes" }, { SetupChangeSetState.Compensating, "compensating" }, { SetupChangeSetState.Restored, "restored" }, { SetupChangeSetState.RollingBack, "rolling_back" }, { SetupChangeSetState.Partial, "partial" }, { SetupChangeSetState.RolledBack, "rolled_back" } };

    private static SetupTargetResult CreateStatusWritableTarget(SetupReferenceState referenceState = SetupReferenceState.Desired, SetupCurrentState currentState = SetupCurrentState.Current, bool rollbackAvailable = true) => new("00000000-0000-7000-8000-000000000020", SetupTargetKind.Json, "vscode-user-settings", true, "1.128.0", SetupOperation.Replace, SetupEffectiveSource.UserSetting, referenceState, currentState, SetupRestartRequirement.RestartVsCode, rollbackAvailable, "http://127.0.0.1:4320", null, null, [new SetupMemberChangeResult("setting", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]);

    private static SetupTargetResult CreateStatusGuidanceTarget() => new("00000000-0000-7000-8000-000000000021", SetupTargetKind.Guidance, "app-sdk-guidance", false, null, SetupOperation.NoOp, null, SetupReferenceState.None, SetupCurrentState.NotApplicable, SetupRestartRequirement.None, false, null, null, new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()"), []);

    private static SetupCommandResult CreateCommandWireResult(SetupCommand command) => command switch
    {
        SetupCommand.Plan => CreatePlanResult(CreatePlanTarget()),
        SetupCommand.Apply => new SetupCommandResult(command, true, "apply_succeeded", "00000000-0000-7000-8000-000000000030", null, null, "github-copilot", [], [], [], [], false),
        SetupCommand.Rollback => new SetupCommandResult(command, true, "rollback_succeeded", "00000000-0000-7000-8000-000000000030", null, null, "github-copilot", [], [], [], [], false),
        SetupCommand.Status => new SetupCommandResult(command, true, "status_ready", null, null, null, "github-copilot", [], [], [], [], false),
        _ => throw new ArgumentOutOfRangeException(nameof(command)),
    };

    private static SetupCommandResult CreateRecoveredWireResult(SetupRecoveryOperation recoveryOperation) => new(SetupCommand.Plan, true, recoveryOperation == SetupRecoveryOperation.Apply ? "interrupted_apply_recovered" : "interrupted_rollback_recovered", null, "00000000-0000-7000-8000-000000000030", recoveryOperation, "github-copilot", [], [], [], ["rerun_requested_setup_command"], false);

    private static SetupCommandResult CreatePlanResult(SetupTargetResult target) => new(SetupCommand.Plan, true, "plan_ready", "00000000-0000-7000-8000-000000000030", null, null, "github-copilot", [target], [], [], [], false);

    private static SetupCommandResult CreateStatusResult(SetupTargetResult target, SetupChangeSetState state = SetupChangeSetState.Applied) => new(SetupCommand.Status, true, "status_ready", null, null, null, "github-copilot", [], [new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000031", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:00:00Z", state, null, SetupCurrentState.Current, state == SetupChangeSetState.Applied, [target])], [], [], false);

    private static SetupTargetResult CreateLifecycleStatusTarget(SetupChangeSetState state) => CreateStatusWritableTarget(
        state switch
        {
            SetupChangeSetState.Planned => SetupReferenceState.Base,
            SetupChangeSetState.Restored or SetupChangeSetState.RolledBack => SetupReferenceState.Previous,
            _ => SetupReferenceState.Desired,
        },
        rollbackAvailable: state == SetupChangeSetState.Applied);

    private static SetupTargetResult CreatePlanTarget(SetupTargetKind targetKind = SetupTargetKind.Json, SetupOperation operation = SetupOperation.Replace, SetupEffectiveSource? effectiveSource = SetupEffectiveSource.UserSetting, SetupRestartRequirement restartRequirement = SetupRestartRequirement.RestartVsCode) => targetKind == SetupTargetKind.Guidance
        ? new SetupTargetResult("00000000-0000-7000-8000-000000000032", targetKind, "app-sdk-guidance", false, null, SetupOperation.NoOp, null, null, null, SetupRestartRequirement.None, false, null, null, new SetupGuidance("caller_managed_sample", "dotnet", "new CopilotClientOptions()"), [])
        : new SetupTargetResult("00000000-0000-7000-8000-000000000032", targetKind, "target", true, "1.128.0", operation, effectiveSource, null, null, restartRequirement, true, "http://127.0.0.1:4320", null, null, [new SetupMemberChangeResult("setting", operation, "present_different", "configured_loopback", "none", false)]);

    private static void AssertWire(SetupCommandResult result, string property, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal(expected, document.RootElement.GetProperty(property).GetString());
    }

    private static void AssertWire(SetupCommandResult result, string arrayProperty, int index, string property, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal(expected, document.RootElement.GetProperty(arrayProperty)[index].GetProperty(property).GetString());
    }

    private static void AssertNestedStatusTargetWire(SetupCommandResult result, string property, string expected)
    {
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        Assert.Equal(expected, document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0].GetProperty(property).GetString());
    }

    private static void AssertTargetShape(JsonElement target) => AssertPropertyOrder(target, "record_id", "target_kind", "target_label", "detected", "detected_version", "operation", "effective_source", "reference_state", "current_state", "restart_requirement", "rollback_available", "endpoint", "expected_result", "guidance", "changes");

    private static void AssertMemberShape(JsonElement member) => AssertPropertyOrder(member, "setting_key", "operation", "previous_state", "new_state", "conflict", "managed");

    private static void AssertChangeSetShape(JsonElement changeSet) => AssertPropertyOrder(changeSet, "change_set_id", "adapter", "selected_target", "created_at", "updated_at", "state", "outcome_code", "current_state", "rollback_available", "targets");

    private static void AssertPropertyOrder(JsonElement element, params string[] expected) => Assert.Equal(expected, element.EnumerateObject().Select(property => property.Name).ToArray());
}

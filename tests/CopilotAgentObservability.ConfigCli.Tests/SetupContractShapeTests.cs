using System.Reflection;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupContractShapeTests
{
    private const string AppSdkGuidanceSample = """
        new CopilotClientOptions
        {
            Telemetry = new TelemetryConfig
            {
                OtlpEndpoint = "http://127.0.0.1:4320",
                OtlpProtocol = "http/protobuf"
            }
        }
        """;

    [Fact]
    public void Serialize_RichPlan_WritesCompleteSetupV1StructureInContractOrder()
    {
        using var expectedResultDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "specifications", "contracts", "source-capabilities", "v1", "manifests", "github-copilot-vscode.json")));
        var writableTarget = new SetupTargetResult(
            "00000000-0000-7000-8000-000000000001", SetupTargetKind.Json, "vscode-user-settings", true, "1.128.0",
            SetupOperation.Replace, SetupEffectiveSource.UserSetting, null, null, SetupRestartRequirement.RestartVsCode, true,
            "http://127.0.0.1:4320", expectedResultDocument.RootElement.Clone(), null,
            [new SetupMemberChangeResult("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]);
        var guidanceTarget = new SetupTargetResult(
            "00000000-0000-7000-8000-000000000002", SetupTargetKind.Guidance, "app-sdk-guidance", false, null,
            SetupOperation.NoOp, null, null, null, SetupRestartRequirement.None, false, null, null,
            new SetupGuidance("caller_managed_sample", "dotnet", AppSdkGuidanceSample), []);
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
        Assert.Equal("00000000-0000-7000-8000-000000000001", writable.GetProperty("record_id").GetString());
        Assert.Equal("json", writable.GetProperty("target_kind").GetString());
        Assert.Equal("vscode-user-settings", writable.GetProperty("target_label").GetString());
        Assert.True(writable.GetProperty("detected").GetBoolean());
        Assert.Equal("1.128.0", writable.GetProperty("detected_version").GetString());
        Assert.Equal("replace", writable.GetProperty("operation").GetString());
        Assert.Equal("user_setting", writable.GetProperty("effective_source").GetString());
        Assert.Equal(JsonValueKind.Null, writable.GetProperty("reference_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, writable.GetProperty("current_state").ValueKind);
        Assert.Equal("restart_vscode", writable.GetProperty("restart_requirement").GetString());
        Assert.True(writable.GetProperty("rollback_available").GetBoolean());
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
        Assert.Equal("00000000-0000-7000-8000-000000000002", guidance.GetProperty("record_id").GetString());
        Assert.Equal("guidance", guidance.GetProperty("target_kind").GetString());
        Assert.Equal("app-sdk-guidance", guidance.GetProperty("target_label").GetString());
        Assert.False(guidance.GetProperty("detected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("detected_version").ValueKind);
        Assert.Equal("no-op", guidance.GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("effective_source").ValueKind);
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("reference_state").ValueKind);
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("current_state").ValueKind);
        Assert.Equal("none", guidance.GetProperty("restart_requirement").GetString());
        Assert.False(guidance.GetProperty("rollback_available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("endpoint").ValueKind);
        Assert.Equal(JsonValueKind.Null, guidance.GetProperty("expected_result").ValueKind);
        Assert.Empty(guidance.GetProperty("changes").EnumerateArray());
        var planGuidance = guidance.GetProperty("guidance");
        AssertPropertyOrder(planGuidance, "kind", "language", "sample");
        Assert.Equal("caller_managed_sample", planGuidance.GetProperty("kind").GetString());
        Assert.Equal("dotnet", planGuidance.GetProperty("language").GetString());
        Assert.Equal(AppSdkGuidanceSample, planGuidance.GetProperty("sample").GetString());
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
                new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000012", "github-copilot", "app-sdk", "2026-07-12T02:00:00Z", "2026-07-12T03:00:00Z", SetupChangeSetState.Planned, null, SetupCurrentState.NotApplicable, false, [guidance]),
                new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000010", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T01:00:00Z", SetupChangeSetState.Applied, "interrupted_apply_recovered", SetupCurrentState.Current, true, [writable]),
            ],
            [], ["rerun_requested_setup_command"], false);

        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;
        var planned = root.GetProperty("change_sets")[0];
        var applied = root.GetProperty("change_sets")[1];

        Assert.Equal("status", root.GetProperty("command").GetString());
        Assert.Equal("interrupted_apply_recovered", root.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("change_set_id").ValueKind);
        Assert.Equal("00000000-0000-7000-8000-000000000010", root.GetProperty("recovered_change_set_id").GetString());
        Assert.Equal("apply", root.GetProperty("recovery_operation").GetString());
        Assert.Empty(root.GetProperty("targets").EnumerateArray());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal("rerun_requested_setup_command", root.GetProperty("next_actions")[0].GetString());
        AssertChangeSetShape(planned);
        Assert.Equal("00000000-0000-7000-8000-000000000012", planned.GetProperty("change_set_id").GetString());
        Assert.Equal("github-copilot", planned.GetProperty("adapter").GetString());
        Assert.Equal("app-sdk", planned.GetProperty("selected_target").GetString());
        Assert.Equal("2026-07-12T02:00:00Z", planned.GetProperty("created_at").GetString());
        Assert.Equal("2026-07-12T03:00:00Z", planned.GetProperty("updated_at").GetString());
        Assert.Equal("planned", planned.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, planned.GetProperty("outcome_code").ValueKind);
        Assert.Equal("not_applicable", planned.GetProperty("current_state").GetString());
        Assert.False(planned.GetProperty("rollback_available").GetBoolean());
        AssertChangeSetShape(applied);
        Assert.Equal("00000000-0000-7000-8000-000000000010", applied.GetProperty("change_set_id").GetString());
        Assert.Equal(root.GetProperty("recovered_change_set_id").GetString(), applied.GetProperty("change_set_id").GetString());
        Assert.Equal("github-copilot", applied.GetProperty("adapter").GetString());
        Assert.Equal("all", applied.GetProperty("selected_target").GetString());
        Assert.Equal("2026-07-12T00:00:00Z", applied.GetProperty("created_at").GetString());
        Assert.Equal("2026-07-12T01:00:00Z", applied.GetProperty("updated_at").GetString());
        Assert.Equal("applied", applied.GetProperty("state").GetString());
        Assert.Equal("interrupted_apply_recovered", applied.GetProperty("outcome_code").GetString());
        Assert.Equal("current", applied.GetProperty("current_state").GetString());
        Assert.True(applied.GetProperty("rollback_available").GetBoolean());

        var nestedWritable = applied.GetProperty("targets")[0];
        AssertTargetShape(nestedWritable);
        Assert.Equal("00000000-0000-7000-8000-000000000020", nestedWritable.GetProperty("record_id").GetString());
        Assert.Equal("json", nestedWritable.GetProperty("target_kind").GetString());
        Assert.Equal("vscode-user-settings", nestedWritable.GetProperty("target_label").GetString());
        Assert.True(nestedWritable.GetProperty("detected").GetBoolean());
        Assert.Equal("1.128.0", nestedWritable.GetProperty("detected_version").GetString());
        Assert.Equal("replace", nestedWritable.GetProperty("operation").GetString());
        Assert.Equal("user_setting", nestedWritable.GetProperty("effective_source").GetString());
        Assert.Equal("desired", nestedWritable.GetProperty("reference_state").GetString());
        Assert.Equal("current", nestedWritable.GetProperty("current_state").GetString());
        Assert.Equal("restart_vscode", nestedWritable.GetProperty("restart_requirement").GetString());
        Assert.True(nestedWritable.GetProperty("rollback_available").GetBoolean());
        Assert.Equal("http://127.0.0.1:4320", nestedWritable.GetProperty("endpoint").GetString());
        Assert.Equal("github-copilot-vscode", nestedWritable.GetProperty("expected_result").GetProperty("source_surface").GetString());
        Assert.Equal(JsonValueKind.Null, nestedWritable.GetProperty("guidance").ValueKind);
        var nestedMember = nestedWritable.GetProperty("changes")[0];
        AssertMemberShape(nestedMember);
        Assert.Equal("setting", nestedMember.GetProperty("setting_key").GetString());
        Assert.Equal("replace", nestedMember.GetProperty("operation").GetString());
        Assert.Equal("present_different", nestedMember.GetProperty("previous_state").GetString());
        Assert.Equal("configured_loopback", nestedMember.GetProperty("new_state").GetString());
        Assert.Equal("none", nestedMember.GetProperty("conflict").GetString());
        Assert.False(nestedMember.GetProperty("managed").GetBoolean());

        var nestedGuidanceTarget = planned.GetProperty("targets")[0];
        AssertTargetShape(nestedGuidanceTarget);
        Assert.Equal("00000000-0000-7000-8000-000000000021", nestedGuidanceTarget.GetProperty("record_id").GetString());
        Assert.Equal("guidance", nestedGuidanceTarget.GetProperty("target_kind").GetString());
        Assert.Equal("app-sdk-guidance", nestedGuidanceTarget.GetProperty("target_label").GetString());
        Assert.False(nestedGuidanceTarget.GetProperty("detected").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nestedGuidanceTarget.GetProperty("detected_version").ValueKind);
        Assert.Equal("no-op", nestedGuidanceTarget.GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, nestedGuidanceTarget.GetProperty("effective_source").ValueKind);
        Assert.Equal("none", nestedGuidanceTarget.GetProperty("reference_state").GetString());
        Assert.Equal("not_applicable", nestedGuidanceTarget.GetProperty("current_state").GetString());
        Assert.Equal("none", nestedGuidanceTarget.GetProperty("restart_requirement").GetString());
        Assert.False(nestedGuidanceTarget.GetProperty("rollback_available").GetBoolean());
        Assert.Equal(JsonValueKind.Null, nestedGuidanceTarget.GetProperty("endpoint").ValueKind);
        Assert.Equal(JsonValueKind.Null, nestedGuidanceTarget.GetProperty("expected_result").ValueKind);
        Assert.Empty(nestedGuidanceTarget.GetProperty("changes").EnumerateArray());
        var nestedGuidance = planned.GetProperty("targets")[0].GetProperty("guidance");
        AssertPropertyOrder(nestedGuidance, "kind", "language");
        Assert.Equal("caller_managed_sample", nestedGuidance.GetProperty("kind").GetString());
        Assert.Equal("dotnet", nestedGuidance.GetProperty("language").GetString());
        Assert.False(nestedGuidance.TryGetProperty("sample", out _));
    }

    [Fact]
    public void Serialize_StatusGuidanceRehydratedFromLedgerMetadata_OmitsSample()
    {
        var guidance = SetupContractValidator.RehydrateStatusGuidance(
            new SetupStatusGuidance("caller_managed_sample", "dotnet"));
        var target = CreateStatusGuidanceTarget() with { Guidance = guidance };

        using var document = JsonDocument.Parse(SetupJson.Serialize(CreateStatusResult(target, SetupChangeSetState.Planned)));

        var serializedGuidance = document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0].GetProperty("guidance");
        Assert.Equal(["kind", "language"], serializedGuidance.EnumerateObject().Select(property => property.Name));
        Assert.False(serializedGuidance.TryGetProperty("sample", out _));
        Assert.Equal(AppSdkGuidanceSample, guidance.Sample);
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
                ("ApplySucceeded", "apply_succeeded"), ("CliTraceProtocolOverrideNotModified", "cli_trace_protocol_override_not_modified"), ("ContentCaptureSensitive", "content_capture_sensitive"), ("ContractVersion", "setup.v1"),
                ("EnvironmentOverrideConflict", "environment_override_conflict"),
                ("InstallCopilotCli", "install_copilot_cli"), ("InstallGitHubCopilotChatExtension", "install_github_copilot_chat_extension"), ("InstallVsCode", "install_vscode"),
                ("InternalError", "internal_error"), ("InterruptedApplyRecovered", "interrupted_apply_recovered"), ("InterruptedRecoveryFailed", "interrupted_recovery_failed"), ("InterruptedRollbackRecovered", "interrupted_rollback_recovered"),
                ("InvalidArguments", "invalid_arguments"), ("LedgerCorrupt", "ledger_corrupt"), ("LedgerVersionUnsupported", "ledger_version_unsupported"), ("MalformedSettings", "malformed_settings"),
                ("ManagedPolicyConflict", "managed_policy_conflict"), ("ManagedPolicyUnverified", "managed_policy_unverified"), ("MonitorNotRunning", "monitor_not_running"), ("NoChanges", "no_changes"),
                ("PartialApply", "partial_apply"), ("PartialRollback", "partial_rollback"), ("PermissionDenied", "permission_denied"), ("PlanReady", "plan_ready"), ("PortOwnedByForeignProcess", "port_owned_by_foreign_process"),
                ("RecoveryRequired", "recovery_required"), ("RerunRequestedSetupCommand", "rerun_requested_setup_command"), ("RestartTerminalSession", "restart_terminal_session"), ("RestartVsCode", "restart_vscode"),
                ("ReviewCliTraceProtocolOverride", "review_cli_trace_protocol_override"), ("ReviewContentCaptureWarning", "review_content_capture_warning"), ("RollbackNotAvailable", "rollback_not_available"), ("RollbackStale", "rollback_stale"), ("RollbackSucceeded", "rollback_succeeded"),
                ("RunFirstTraceDoctor", "run_first_trace_doctor"), ("RunVsCodePolicyDiagnostics", "run_vscode_policy_diagnostics"), ("SetupBusy", "setup_busy"), ("SharedUserEnvironmentAffectsOtherProcesses", "shared_user_environment_affects_other_processes"),
                ("StalePlan", "stale_plan"), ("StartLocalMonitor", "start_local_monitor"), ("StatusReady", "status_ready"), ("TargetNotInstalled", "target_not_installed"), ("UnsafePath", "unsafe_path"),
                ("UnsupportedAdapter", "unsupported_adapter"), ("UnsupportedTarget", "unsupported_target"), ("UnsupportedVersion", "unsupported_version"), ("UpgradeCopilotCli", "upgrade_copilot_cli"), ("UpgradeVsCode", "upgrade_vscode"),
                ("VscodeNonDefaultProfilesNotModified", "vscode_non_default_profiles_not_modified"),
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
    public void Serialize_ReferenceState_MapsEveryExplicitEnumValue(SetupReferenceState value, string expected) => AssertNestedStatusTargetWire(CreateReferenceStateWireResult(value), "reference_state", expected);

    [Theory]
    [MemberData(nameof(CurrentStateWireValues))]
    public void Serialize_CurrentState_MapsEveryExplicitEnumValue(SetupCurrentState value, string expected) => AssertNestedStatusTargetWire(CreateCurrentStateWireResult(value), "current_state", expected);

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

    private static SetupTargetResult CreateStatusWritableTarget(SetupReferenceState referenceState = SetupReferenceState.Desired, SetupCurrentState currentState = SetupCurrentState.Current, bool rollbackAvailable = true) => new("00000000-0000-7000-8000-000000000020", SetupTargetKind.Json, "vscode-user-settings", true, "1.128.0", SetupOperation.Replace, SetupEffectiveSource.UserSetting, referenceState, currentState, SetupRestartRequirement.RestartVsCode, rollbackAvailable, "http://127.0.0.1:4320", SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!.CanonicalJson, null, [new SetupMemberChangeResult("setting", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]);

    private static SetupTargetResult CreateStatusGuidanceTarget() => new("00000000-0000-7000-8000-000000000021", SetupTargetKind.Guidance, "app-sdk-guidance", false, null, SetupOperation.NoOp, null, SetupReferenceState.None, SetupCurrentState.NotApplicable, SetupRestartRequirement.None, false, null, null, new SetupGuidance("caller_managed_sample", "dotnet", AppSdkGuidanceSample), []);

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

    private static SetupCommandResult CreateStatusResult(SetupTargetResult target, SetupChangeSetState state = SetupChangeSetState.Applied) => new(SetupCommand.Status, true, "status_ready", null, null, null, "github-copilot", [], [new SetupChangeSetStatusResult("00000000-0000-7000-8000-000000000031", "github-copilot", "all", "2026-07-12T00:00:00Z", "2026-07-12T00:00:00Z", state, null, target.TargetKind == SetupTargetKind.Guidance ? SetupCurrentState.NotApplicable : target.CurrentState!.Value, state == SetupChangeSetState.Applied && target.TargetKind != SetupTargetKind.Guidance && target.CurrentState == SetupCurrentState.Current && target.RollbackAvailable, [target])], [], [], false);

    private static SetupCommandResult CreateReferenceStateWireResult(SetupReferenceState value) => value switch
    {
        SetupReferenceState.Base => CreateStatusResult(CreateStatusWritableTarget(SetupReferenceState.Base, rollbackAvailable: false), SetupChangeSetState.Planned),
        SetupReferenceState.Desired => CreateStatusResult(CreateStatusWritableTarget(SetupReferenceState.Desired), SetupChangeSetState.Applied),
        SetupReferenceState.Previous => CreateStatusResult(CreateStatusWritableTarget(SetupReferenceState.Previous, rollbackAvailable: false), SetupChangeSetState.Restored),
        SetupReferenceState.None => CreateStatusResult(CreateStatusGuidanceTarget(), SetupChangeSetState.Planned),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SetupCommandResult CreateCurrentStateWireResult(SetupCurrentState value) => value switch
    {
        SetupCurrentState.Current => CreateStatusResult(CreateStatusWritableTarget()),
        SetupCurrentState.Stale => CreateStatusResult(CreateStatusWritableTarget(SetupReferenceState.Desired, SetupCurrentState.Stale, rollbackAvailable: false)),
        SetupCurrentState.Diverged => CreateStatusResult(CreateStatusWritableTarget(SetupReferenceState.None, SetupCurrentState.Diverged, rollbackAvailable: false)),
        SetupCurrentState.Unavailable => CreateStatusResult(CreateStatusWritableTarget(SetupReferenceState.None, SetupCurrentState.Unavailable, rollbackAvailable: false)),
        SetupCurrentState.NotApplicable => CreateStatusResult(CreateStatusGuidanceTarget()),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static SetupTargetResult CreateLifecycleStatusTarget(SetupChangeSetState state) => CreateStatusWritableTarget(
        state switch
        {
            SetupChangeSetState.Planned => SetupReferenceState.Base,
            SetupChangeSetState.Restored or SetupChangeSetState.RolledBack => SetupReferenceState.Previous,
            _ => SetupReferenceState.Desired,
        },
        rollbackAvailable: state == SetupChangeSetState.Applied);

    private static SetupTargetResult CreatePlanTarget(SetupTargetKind targetKind = SetupTargetKind.Json, SetupOperation operation = SetupOperation.Replace, SetupEffectiveSource? effectiveSource = SetupEffectiveSource.UserSetting, SetupRestartRequirement restartRequirement = SetupRestartRequirement.RestartVsCode) => targetKind == SetupTargetKind.Guidance
        ? new SetupTargetResult("00000000-0000-7000-8000-000000000032", targetKind, "app-sdk-guidance", false, null, SetupOperation.NoOp, null, null, null, SetupRestartRequirement.None, false, null, null, new SetupGuidance("caller_managed_sample", "dotnet", AppSdkGuidanceSample), [])
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

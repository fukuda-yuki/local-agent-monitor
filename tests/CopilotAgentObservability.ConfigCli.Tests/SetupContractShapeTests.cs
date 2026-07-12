using System.Text.Json;
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
}

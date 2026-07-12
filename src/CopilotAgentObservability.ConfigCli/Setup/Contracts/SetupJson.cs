using System.Text;
using System.Text.Json;

namespace CopilotAgentObservability.ConfigCli.Setup.Contracts;

public static class SetupJson
{
    public static string Serialize(SetupCommandResult result)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("contract_version", SetupCodes.ContractVersion);
            writer.WriteString("command", ToWireValue(result.Command));
            writer.WriteBoolean("success", result.Success);
            writer.WriteString("code", result.Code);
            WriteNullableString(writer, "change_set_id", result.ChangeSetId);
            WriteNullableString(writer, "recovered_change_set_id", result.RecoveredChangeSetId);
            WriteNullableEnum(writer, "recovery_operation", result.RecoveryOperation, ToWireValue);
            WriteNullableString(writer, "adapter", result.Adapter);
            WriteTargets(writer, "targets", result.Targets, includeGuidanceSample: true);
            WriteChangeSets(writer, result.ChangeSets);
            WriteStrings(writer, "warnings", result.Warnings);
            WriteStrings(writer, "next_actions", result.NextActions);
            writer.WriteBoolean("truncated", result.Truncated);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteTargets(Utf8JsonWriter writer, string propertyName, IReadOnlyList<SetupTargetResult> targets, bool includeGuidanceSample)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var target in targets)
        {
            WriteTarget(writer, target, includeGuidanceSample);
        }

        writer.WriteEndArray();
    }

    private static void WriteTarget(Utf8JsonWriter writer, SetupTargetResult target, bool includeGuidanceSample)
    {
        writer.WriteStartObject();
        writer.WriteString("record_id", target.RecordId);
        writer.WriteString("target_kind", ToWireValue(target.TargetKind));
        writer.WriteString("target_label", target.TargetLabel);
        writer.WriteBoolean("detected", target.Detected);
        WriteNullableString(writer, "detected_version", target.DetectedVersion);
        writer.WriteString("operation", ToWireValue(target.Operation));
        WriteNullableEnum(writer, "effective_source", target.EffectiveSource, ToWireValue);
        WriteNullableEnum(writer, "reference_state", target.ReferenceState, ToWireValue);
        WriteNullableEnum(writer, "current_state", target.CurrentState, ToWireValue);
        writer.WriteString("restart_requirement", ToWireValue(target.RestartRequirement));
        writer.WriteBoolean("rollback_available", target.RollbackAvailable);
        WriteNullableString(writer, "endpoint", target.Endpoint);
        writer.WritePropertyName("expected_result");
        if (target.ExpectedResult is { } expectedResult)
        {
            expectedResult.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("guidance");
        if (target.Guidance is { } guidance)
        {
            WriteGuidance(writer, guidance, includeGuidanceSample);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("changes");
        writer.WriteStartArray();
        foreach (var change in target.Changes)
        {
            writer.WriteStartObject();
            writer.WriteString("setting_key", change.SettingKey);
            writer.WriteString("operation", ToWireValue(change.Operation));
            writer.WriteString("previous_state", change.PreviousState);
            writer.WriteString("new_state", change.NewState);
            writer.WriteString("conflict", change.Conflict);
            writer.WriteBoolean("managed", change.Managed);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteGuidance(Utf8JsonWriter writer, SetupGuidance guidance, bool includeSample)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", guidance.Kind);
        writer.WriteString("language", guidance.Language);
        if (includeSample)
        {
            writer.WriteString("sample", guidance.Sample);
        }

        writer.WriteEndObject();
    }

    private static void WriteChangeSets(Utf8JsonWriter writer, IReadOnlyList<SetupChangeSetStatusResult> changeSets)
    {
        writer.WritePropertyName("change_sets");
        writer.WriteStartArray();
        foreach (var changeSet in changeSets)
        {
            writer.WriteStartObject();
            writer.WriteString("change_set_id", changeSet.ChangeSetId);
            writer.WriteString("adapter", changeSet.Adapter);
            writer.WriteString("selected_target", changeSet.SelectedTarget);
            writer.WriteString("created_at", changeSet.CreatedAt);
            writer.WriteString("updated_at", changeSet.UpdatedAt);
            writer.WriteString("state", ToWireValue(changeSet.State));
            WriteNullableString(writer, "outcome_code", changeSet.OutcomeCode);
            writer.WriteString("current_state", ToWireValue(changeSet.CurrentState));
            writer.WriteBoolean("rollback_available", changeSet.RollbackAvailable);
            WriteTargets(writer, "targets", changeSet.Targets, includeGuidanceSample: false);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string propertyName, IReadOnlyList<string> values)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }

    private static void WriteNullableEnum<T>(Utf8JsonWriter writer, string propertyName, T? value, Func<T, string> toWireValue)
        where T : struct
    {
        if (value is { } enumValue)
        {
            writer.WriteString(propertyName, toWireValue(enumValue));
            return;
        }

        writer.WriteNull(propertyName);
    }

    private static string ToWireValue(SetupCommand value) => value switch
    {
        SetupCommand.Plan => "plan",
        SetupCommand.Apply => "apply",
        SetupCommand.Rollback => "rollback",
        SetupCommand.Status => "status",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupRecoveryOperation value) => value switch
    {
        SetupRecoveryOperation.Apply => "apply",
        SetupRecoveryOperation.Rollback => "rollback",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupTargetKind value) => value switch
    {
        SetupTargetKind.Env => "env",
        SetupTargetKind.Json => "json",
        SetupTargetKind.Toml => "toml",
        SetupTargetKind.StartupTask => "startup_task",
        SetupTargetKind.File => "file",
        SetupTargetKind.Guidance => "guidance",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupOperation value) => value switch
    {
        SetupOperation.Add => "add",
        SetupOperation.Replace => "replace",
        SetupOperation.Remove => "remove",
        SetupOperation.Mixed => "mixed",
        SetupOperation.NoOp => "no-op",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupEffectiveSource value) => value switch
    {
        SetupEffectiveSource.ManagedPolicy => "managed_policy",
        SetupEffectiveSource.Environment => "environment",
        SetupEffectiveSource.UserSetting => "user_setting",
        SetupEffectiveSource.Default => "default",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupReferenceState value) => value switch
    {
        SetupReferenceState.Base => "base",
        SetupReferenceState.Desired => "desired",
        SetupReferenceState.Previous => "previous",
        SetupReferenceState.None => "none",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupCurrentState value) => value switch
    {
        SetupCurrentState.Current => "current",
        SetupCurrentState.Stale => "stale",
        SetupCurrentState.Diverged => "diverged",
        SetupCurrentState.Unavailable => "unavailable",
        SetupCurrentState.NotApplicable => "not_applicable",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupRestartRequirement value) => value switch
    {
        SetupRestartRequirement.None => "none",
        SetupRestartRequirement.RestartVsCode => "restart_vscode",
        SetupRestartRequirement.RestartTerminalSession => "restart_terminal_session",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToWireValue(SetupChangeSetState value) => value switch
    {
        SetupChangeSetState.Planned => "planned",
        SetupChangeSetState.Applying => "applying",
        SetupChangeSetState.Applied => "applied",
        SetupChangeSetState.NoChanges => "no_changes",
        SetupChangeSetState.Compensating => "compensating",
        SetupChangeSetState.Restored => "restored",
        SetupChangeSetState.RollingBack => "rolling_back",
        SetupChangeSetState.Partial => "partial",
        SetupChangeSetState.RolledBack => "rolled_back",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}

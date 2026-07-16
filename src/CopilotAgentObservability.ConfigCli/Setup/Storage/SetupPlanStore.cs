using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

internal sealed record SetupPrivatePlan(
    int SchemaVersion,
    Guid ChangeSetId,
    string Adapter,
    string SelectedTarget,
    DateTimeOffset CreatedAt,
    string ToolVersion,
    IReadOnlyList<SetupPrivatePlanTarget> Targets);

internal sealed record SetupPrivatePlanTarget(
    Guid RecordId,
    SetupTargetKind TargetKind,
    string TargetLocation,
    string BaseStateHash,
    SetupPrivateDesiredState DesiredState,
    IReadOnlyList<SetupPrivatePlanMember> Members);

internal abstract record SetupPrivateDesiredState
{
    public static implicit operator SetupPrivateDesiredState(string value) => new SetupInlineDesiredState(value);

    public static implicit operator string(SetupPrivateDesiredState value) => value switch
    {
        SetupInlineDesiredState inline => inline.Value,
        _ => throw new FormatException(),
    };

    internal abstract SetupPrivateDesiredState Snapshot();
}

internal sealed record SetupInlineDesiredState(string Value) : SetupPrivateDesiredState
{
    internal override SetupPrivateDesiredState Snapshot() => new SetupInlineDesiredState(Value);
}

internal sealed record SetupJsoncOwnedValuesDesiredState(
    string ExpectedStateHash,
    IReadOnlyList<SetupJsoncOwnedValue> OwnedValues) : SetupPrivateDesiredState
{
    internal override SetupPrivateDesiredState Snapshot() => new SetupJsoncOwnedValuesDesiredState(
        ExpectedStateHash,
        Array.AsReadOnly((OwnedValues ?? throw new FormatException())
            .Select(value => value is null
                ? throw new FormatException()
                : new SetupJsoncOwnedValue(value.SettingKey, value.ValueKind, value.Value))
            .ToArray()));
}

internal sealed record SetupJsoncOwnedValue(
    string SettingKey,
    string ValueKind,
    object Value);

internal sealed record SetupClaudeSettingsOwnedValuesDesiredState(
    string ExpectedStateHash,
    IReadOnlyList<SetupClaudeSettingsEnvValue> OwnedEnv,
    IReadOnlyList<SetupClaudeSettingsHook> OwnedHooks) : SetupPrivateDesiredState
{
    internal override SetupPrivateDesiredState Snapshot() => new SetupClaudeSettingsOwnedValuesDesiredState(
        ExpectedStateHash,
        Array.AsReadOnly((OwnedEnv ?? throw new FormatException())
            .Select(value => value is null
                ? throw new FormatException()
                : new SetupClaudeSettingsEnvValue(value.Key, value.Value))
            .ToArray()),
        Array.AsReadOnly((OwnedHooks ?? throw new FormatException())
            .Select(hook => hook is null
                ? throw new FormatException()
                : new SetupClaudeSettingsHook(
                    hook.EventName,
                    hook.Command,
                    Array.AsReadOnly((hook.Arguments ?? throw new FormatException()).ToArray()),
                    hook.TimeoutSeconds))
            .ToArray()));
}

internal sealed record SetupClaudeSettingsEnvValue(string Key, string Value);

internal sealed record SetupClaudeSettingsHook(
    string EventName,
    string Command,
    IReadOnlyList<string> Arguments,
    int TimeoutSeconds);

internal sealed record SetupPrivatePlanMember(
    string SettingKey,
    SetupOperation Operation,
    string? DesiredValue);

internal sealed class SetupPlanStore
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;

    public SetupPlanStore(ISetupPlatform platform, SetupRuntimePaths paths)
    {
        this.platform = platform;
        this.paths = paths;
    }

    public void Create(SetupLock setupLock, SetupPrivatePlan plan)
    {
        setupLock.ExecuteWhileHeld(platform, paths, () => CreateCore(plan));
    }

    public SetupPrivatePlan? Load(Guid changeSetId)
    {
        var planPath = paths.GetPlan(changeSetId);
        if (!platform.FileSystem.FileExists(planPath))
        {
            return null;
        }

        try
        {
            var plan = Deserialize(platform.FileSystem.ReadAllBytes(planPath));
            if (plan.ChangeSetId != changeSetId)
            {
                throw new FormatException();
            }

            return plan;
        }
        catch (Exception exception) when (SetupStorageException.ShouldMap(exception))
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }
    }

    public void Delete(SetupLock setupLock, Guid changeSetId)
    {
        setupLock.ExecuteWhileHeld(platform, paths, () => DeleteCore(changeSetId));
    }

    internal void CreateCore(SetupPrivatePlan plan)
    {
        SetupStorageValidation.ValidatePlan(plan);
        var destination = paths.GetPlan(plan.ChangeSetId);
        if (platform.FileSystem.FileExists(destination))
        {
            throw new SetupStorageException(SetupStorageCodes.PlanAlreadyExists);
        }

        platform.FileSystem.CreateDirectory(paths.Plans);
        SetupStorageFile.WriteNew(platform, destination, Serialize(plan));
    }

    internal void DeleteCore(Guid changeSetId)
    {
        try
        {
            platform.FileSystem.DeleteFile(paths.GetPlan(changeSetId));
        }
        catch (Exception)
        {
        }
    }

    internal static byte[] Serialize(SetupPrivatePlan plan)
    {
        SetupStorageValidation.ValidatePlan(plan);
        using var buffer = new MemoryStream();
        using (var writer = SetupStorageJson.CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", plan.SchemaVersion);
            writer.WriteString("change_set_id", plan.ChangeSetId.ToString("D"));
            writer.WriteString("adapter", plan.Adapter);
            writer.WriteString("selected_target", plan.SelectedTarget);
            writer.WriteString("created_at", SetupStorageJson.FormatTimestamp(plan.CreatedAt));
            writer.WriteString("tool_version", plan.ToolVersion);
            writer.WritePropertyName("targets");
            writer.WriteStartArray();
            foreach (var target in plan.Targets)
            {
                writer.WriteStartObject();
                writer.WriteString("record_id", target.RecordId.ToString("D"));
                writer.WriteString("target_kind", SetupStorageJson.TargetKind(target.TargetKind));
                writer.WriteString("target_location", target.TargetLocation);
                writer.WriteString("base_state_hash", target.BaseStateHash);
                WriteDesiredState(writer, target.DesiredState);
                writer.WritePropertyName("members");
                writer.WriteStartArray();
                foreach (var member in target.Members)
                {
                    writer.WriteStartObject();
                    writer.WriteString("setting_key", member.SettingKey);
                    writer.WriteString("operation", SetupStorageJson.Operation(member.Operation));
                    SetupStorageJson.WriteNullableString(writer, "desired_value", member.DesiredValue);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteDesiredState(Utf8JsonWriter writer, SetupPrivateDesiredState desiredState)
    {
        writer.WritePropertyName("desired_state");
        if ((object)desiredState is SetupInlineDesiredState inline)
        {
            writer.WriteStringValue(inline.Value);
            return;
        }

        if ((object)desiredState is SetupClaudeSettingsOwnedValuesDesiredState claude)
        {
            WriteClaudeDesiredState(writer, claude);
            return;
        }

        if ((object)desiredState is not SetupJsoncOwnedValuesDesiredState tagged)
        {
            throw new FormatException();
        }

        writer.WriteStartObject();
        writer.WriteString("kind", "jsonc_owned_values_v1");
        writer.WriteString("expected_state_hash", tagged.ExpectedStateHash);
        writer.WritePropertyName("owned_values");
        writer.WriteStartArray();
        foreach (var ownedValue in tagged.OwnedValues)
        {
            writer.WriteStartObject();
            writer.WriteString("setting_key", ownedValue.SettingKey);
            writer.WriteString("value_kind", ownedValue.ValueKind);
            writer.WritePropertyName("value");
            if (ownedValue.ValueKind == "boolean")
            {
                writer.WriteBooleanValue((bool)ownedValue.Value);
            }
            else
            {
                writer.WriteStringValue((string)ownedValue.Value);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteClaudeDesiredState(
        Utf8JsonWriter writer,
        SetupClaudeSettingsOwnedValuesDesiredState desiredState)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "claude_settings_owned_values_v1");
        writer.WriteString("expected_state_hash", desiredState.ExpectedStateHash);
        writer.WritePropertyName("owned_env");
        writer.WriteStartArray();
        foreach (var value in desiredState.OwnedEnv)
        {
            writer.WriteStartObject();
            writer.WriteString("key", value.Key);
            writer.WriteString("value", value.Value);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("owned_hooks");
        writer.WriteStartArray();
        foreach (var hook in desiredState.OwnedHooks)
        {
            writer.WriteStartObject();
            writer.WriteString("event", hook.EventName);
            writer.WriteString("command", hook.Command);
            writer.WritePropertyName("args");
            writer.WriteStartArray();
            foreach (var argument in hook.Arguments)
            {
                writer.WriteStringValue(argument);
            }

            writer.WriteEndArray();
            writer.WriteNumber("timeout_seconds", hook.TimeoutSeconds);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static SetupPrivatePlan Deserialize(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        SetupStorageJson.RequireProperties(root, "schema_version", "change_set_id", "adapter", "selected_target", "created_at", "tool_version", "targets");
        if (root.GetProperty("schema_version").GetInt32() != 1)
        {
            throw new FormatException();
        }

        var targets = new List<SetupPrivatePlanTarget>();
        foreach (var targetElement in SetupStorageJson.GetArray(root, "targets"))
        {
            SetupStorageJson.RequireProperties(targetElement, "record_id", "target_kind", "target_location", "base_state_hash", "desired_state", "members");
            var members = new List<SetupPrivatePlanMember>();
            foreach (var memberElement in SetupStorageJson.GetArray(targetElement, "members"))
            {
                SetupStorageJson.RequireProperties(memberElement, "setting_key", "operation", "desired_value");
                members.Add(new SetupPrivatePlanMember(
                    SetupStorageJson.GetString(memberElement, "setting_key"),
                    SetupStorageJson.ParseOperation(SetupStorageJson.GetString(memberElement, "operation")),
                    SetupStorageJson.GetNullableString(memberElement, "desired_value")));
            }

            targets.Add(new SetupPrivatePlanTarget(
                SetupStorageJson.GetGuid(targetElement, "record_id"),
                SetupStorageJson.ParseTargetKind(SetupStorageJson.GetString(targetElement, "target_kind")),
                SetupStorageJson.GetString(targetElement, "target_location"),
                SetupStorageJson.GetString(targetElement, "base_state_hash"),
                ReadDesiredState(targetElement.GetProperty("desired_state")),
                members));
        }

        var plan = new SetupPrivatePlan(
            1,
            SetupStorageJson.GetGuid(root, "change_set_id"),
            SetupStorageJson.GetString(root, "adapter"),
            SetupStorageJson.GetString(root, "selected_target"),
            SetupStorageJson.ParseTimestamp(SetupStorageJson.GetString(root, "created_at")),
            SetupStorageJson.GetString(root, "tool_version"),
            targets);
        SetupStorageValidation.ValidatePlan(plan);
        return plan;
    }

    private static SetupPrivateDesiredState ReadDesiredState(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return new SetupInlineDesiredState(element.GetString() ?? throw new FormatException());
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException();
        }

        if (!element.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
        {
            throw new FormatException();
        }

        return kindElement.GetString() switch
        {
            "jsonc_owned_values_v1" => ReadJsoncDesiredState(element),
            "claude_settings_owned_values_v1" => ReadClaudeDesiredState(element),
            _ => throw new FormatException(),
        };
    }

    private static SetupPrivateDesiredState ReadJsoncDesiredState(JsonElement element)
    {
        SetupStorageJson.RequireProperties(element, "kind", "expected_state_hash", "owned_values");

        var ownedValues = new List<SetupJsoncOwnedValue>();
        foreach (var ownedValueElement in SetupStorageJson.GetArray(element, "owned_values"))
        {
            SetupStorageJson.RequireProperties(ownedValueElement, "setting_key", "value_kind", "value");
            var valueKind = SetupStorageJson.GetString(ownedValueElement, "value_kind");
            var valueElement = ownedValueElement.GetProperty("value");
            object value = valueKind switch
            {
                "boolean" when valueElement.ValueKind is JsonValueKind.True or JsonValueKind.False => valueElement.GetBoolean(),
                "string" when valueElement.ValueKind == JsonValueKind.String => valueElement.GetString() ?? throw new FormatException(),
                _ => throw new FormatException(),
            };
            ownedValues.Add(new SetupJsoncOwnedValue(
                SetupStorageJson.GetString(ownedValueElement, "setting_key"),
                valueKind,
                value));
        }

        return new SetupJsoncOwnedValuesDesiredState(
            SetupStorageJson.GetString(element, "expected_state_hash"),
            Array.AsReadOnly(ownedValues.ToArray()));
    }

    private static SetupPrivateDesiredState ReadClaudeDesiredState(JsonElement element)
    {
        SetupStorageJson.RequireProperties(element, "kind", "expected_state_hash", "owned_env", "owned_hooks");
        var env = new List<SetupClaudeSettingsEnvValue>();
        foreach (var valueElement in SetupStorageJson.GetArray(element, "owned_env"))
        {
            SetupStorageJson.RequireProperties(valueElement, "key", "value");
            env.Add(new SetupClaudeSettingsEnvValue(
                SetupStorageJson.GetString(valueElement, "key"),
                SetupStorageJson.GetString(valueElement, "value")));
        }

        var hooks = new List<SetupClaudeSettingsHook>();
        foreach (var hookElement in SetupStorageJson.GetArray(element, "owned_hooks"))
        {
            SetupStorageJson.RequireProperties(hookElement, "event", "command", "args", "timeout_seconds");
            var arguments = new List<string>();
            foreach (var argumentElement in SetupStorageJson.GetArray(hookElement, "args"))
            {
                if (argumentElement.ValueKind != JsonValueKind.String)
                {
                    throw new FormatException();
                }

                arguments.Add(argumentElement.GetString() ?? throw new FormatException());
            }

            hooks.Add(new SetupClaudeSettingsHook(
                SetupStorageJson.GetString(hookElement, "event"),
                SetupStorageJson.GetString(hookElement, "command"),
                Array.AsReadOnly(arguments.ToArray()),
                hookElement.GetProperty("timeout_seconds").GetInt32()));
        }

        return new SetupClaudeSettingsOwnedValuesDesiredState(
            SetupStorageJson.GetString(element, "expected_state_hash"),
            Array.AsReadOnly(env.ToArray()),
            Array.AsReadOnly(hooks.ToArray()));
    }
}

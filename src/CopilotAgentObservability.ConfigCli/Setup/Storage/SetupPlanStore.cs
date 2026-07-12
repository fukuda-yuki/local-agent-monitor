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
    string DesiredState,
    IReadOnlyList<SetupPrivatePlanMember> Members);

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
        setupLock.AssertHeld(platform, paths);
        SetupStorageValidation.ValidatePlan(plan);
        var destination = paths.GetPlan(plan.ChangeSetId);
        if (platform.FileSystem.FileExists(destination))
        {
            throw new SetupStorageException(SetupStorageCodes.PlanAlreadyExists);
        }

        platform.FileSystem.CreateDirectory(paths.Plans);
        SetupStorageFile.WriteNew(platform, destination, Serialize(plan));
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
        setupLock.AssertHeld(platform, paths);
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
                writer.WriteString("desired_state", target.DesiredState);
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
                SetupStorageJson.GetString(targetElement, "desired_state"),
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
}

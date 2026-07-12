using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

internal static class SetupStorageCodes
{
    public const string PlanAlreadyExists = "setup_plan_already_exists";
    public const string PlanLedgerMismatch = "setup_plan_ledger_mismatch";
    public const string ChangeSetAlreadyExists = "setup_change_set_already_exists";
    public const string WriteFailed = "setup_storage_write_failed";
}

internal sealed class SetupStorageException : Exception
{
    public SetupStorageException(string code)
        : base(code)
    {
        Code = code;
    }

    public string Code { get; }

    internal static bool ShouldMap(Exception exception) => exception is not SetupStorageException &&
        exception is JsonException or FormatException or InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException;
}

internal sealed record SetupOwnershipLedger(
    int SchemaVersion,
    IReadOnlyList<SetupLedgerChangeSet> ChangeSets);

internal sealed record SetupLedgerChangeSet(
    Guid ChangeSetId,
    string Adapter,
    string SelectedTarget,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ToolVersion,
    string? OutcomeCode,
    SetupChangeSetState State,
    IReadOnlyList<SetupLedgerTarget> Targets);

internal sealed record SetupLedgerTarget(
    Guid RecordId,
    SetupTargetKind TargetKind,
    string TargetLabel,
    string OwningAdapter,
    IReadOnlyList<SetupLedgerMember> Members,
    string PreviousStateHash,
    string? AppliedStateHash,
    string? BackupReference,
    string? OutcomeCode,
    SetupLedgerRollbackStatus RollbackStatus,
    SetupRestartRequirement RestartRequirement,
    string ToolVersion);

internal sealed record SetupLedgerMember(
    string SettingKey,
    SetupOperation Operation);

internal enum SetupLedgerRollbackStatus
{
    NotAvailable,
    Pending,
    Succeeded,
    Failed,
    Stale,
}

internal sealed class SetupLedgerStore
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupPlanStore planStore;

    public SetupLedgerStore(ISetupPlatform platform, SetupRuntimePaths paths, SetupPlanStore planStore)
    {
        this.platform = platform;
        this.paths = paths;
        this.planStore = planStore;
    }

    public SetupOwnershipLedger Load()
    {
        if (!platform.FileSystem.FileExists(paths.OwnershipLedger))
        {
            return new SetupOwnershipLedger(1, []);
        }

        SetupOwnershipLedger ledger;
        try
        {
            ledger = Deserialize(platform.FileSystem.ReadAllBytes(paths.OwnershipLedger));
        }
        catch (SetupStorageException)
        {
            throw;
        }
        catch (Exception exception) when (SetupStorageException.ShouldMap(exception))
        {
            throw new SetupStorageException(SetupCodes.LedgerCorrupt);
        }

        foreach (var changeSet in ledger.ChangeSets.Where(changeSet => !IsTerminal(changeSet.State)))
        {
            if (planStore.Load(changeSet.ChangeSetId) is null)
            {
                throw new SetupStorageException(SetupCodes.RecoveryRequired);
            }
        }

        return ledger;
    }

    public void Save(SetupOwnershipLedger ledger)
    {
        var bytes = Serialize(ledger);
        paths.EnsureRoot();
        SetupStorageFile.WriteAtomic(platform, paths.OwnershipLedger, bytes);
    }

    public void PersistPlannedChangeSet(SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
    {
        SetupStorageValidation.ValidatePlanAndLedger(plan, plannedChangeSet);
        var ledger = Load();
        if (ledger.ChangeSets.Any(changeSet => changeSet.ChangeSetId == plan.ChangeSetId))
        {
            throw new SetupStorageException(SetupStorageCodes.ChangeSetAlreadyExists);
        }

        var planCreated = false;
        try
        {
            planStore.Create(plan);
            planCreated = true;
            platform.Execution.Checkpoint("after-plan-persisted-before-ledger");
            Save(ledger with { ChangeSets = [.. ledger.ChangeSets, plannedChangeSet] });
        }
        catch (SetupStorageException)
        {
            if (planCreated)
            {
                planStore.Delete(plan.ChangeSetId);
            }

            throw;
        }
        catch (Exception)
        {
            if (planCreated)
            {
                planStore.Delete(plan.ChangeSetId);
            }

            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }
    }

    internal static byte[] Serialize(SetupOwnershipLedger ledger)
    {
        SetupStorageValidation.ValidateLedger(ledger);
        using var buffer = new MemoryStream();
        using (var writer = SetupStorageJson.CreateWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", ledger.SchemaVersion);
            writer.WritePropertyName("change_sets");
            writer.WriteStartArray();
            foreach (var changeSet in ledger.ChangeSets)
            {
                writer.WriteStartObject();
                writer.WriteString("change_set_id", changeSet.ChangeSetId.ToString("D"));
                writer.WriteString("adapter", changeSet.Adapter);
                writer.WriteString("selected_target", changeSet.SelectedTarget);
                writer.WriteString("created_at", SetupStorageJson.FormatTimestamp(changeSet.CreatedAt));
                writer.WriteString("updated_at", SetupStorageJson.FormatTimestamp(changeSet.UpdatedAt));
                writer.WriteString("tool_version", changeSet.ToolVersion);
                SetupStorageJson.WriteNullableString(writer, "outcome_code", changeSet.OutcomeCode);
                writer.WriteString("state", SetupStorageJson.ChangeSetState(changeSet.State));
                writer.WritePropertyName("targets");
                writer.WriteStartArray();
                foreach (var target in changeSet.Targets)
                {
                    writer.WriteStartObject();
                    writer.WriteString("record_id", target.RecordId.ToString("D"));
                    writer.WriteString("target_kind", SetupStorageJson.TargetKind(target.TargetKind));
                    writer.WriteString("target_label", target.TargetLabel);
                    writer.WriteString("owning_adapter", target.OwningAdapter);
                    writer.WritePropertyName("members");
                    writer.WriteStartArray();
                    foreach (var member in target.Members)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("setting_key", member.SettingKey);
                        writer.WriteString("operation", SetupStorageJson.Operation(member.Operation));
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteString("previous_state_hash", target.PreviousStateHash);
                    SetupStorageJson.WriteNullableString(writer, "applied_state_hash", target.AppliedStateHash);
                    SetupStorageJson.WriteNullableString(writer, "backup_reference", target.BackupReference);
                    SetupStorageJson.WriteNullableString(writer, "outcome_code", target.OutcomeCode);
                    writer.WriteString("rollback_status", SetupStorageJson.RollbackStatus(target.RollbackStatus));
                    writer.WriteString("restart_requirement", SetupStorageJson.RestartRequirement(target.RestartRequirement));
                    writer.WriteString("tool_version", target.ToolVersion);
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

    private static SetupOwnershipLedger Deserialize(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schema_version", out var versionElement) ||
            versionElement.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException();
        }

        var version = versionElement.GetInt32();
        if (version != 1)
        {
            throw new SetupStorageException(SetupCodes.LedgerVersionUnsupported);
        }

        SetupStorageJson.RequireProperties(root, "schema_version", "change_sets");

        var changeSets = new List<SetupLedgerChangeSet>();
        foreach (var changeSetElement in root.GetProperty("change_sets").EnumerateArray())
        {
            SetupStorageJson.RequireProperties(changeSetElement, "change_set_id", "adapter", "selected_target", "created_at", "updated_at", "tool_version", "outcome_code", "state", "targets");
            var targets = new List<SetupLedgerTarget>();
            foreach (var targetElement in changeSetElement.GetProperty("targets").EnumerateArray())
            {
                SetupStorageJson.RequireProperties(targetElement, "record_id", "target_kind", "target_label", "owning_adapter", "members", "previous_state_hash", "applied_state_hash", "backup_reference", "outcome_code", "rollback_status", "restart_requirement", "tool_version");
                var members = new List<SetupLedgerMember>();
                foreach (var memberElement in targetElement.GetProperty("members").EnumerateArray())
                {
                    SetupStorageJson.RequireProperties(memberElement, "setting_key", "operation");
                    members.Add(new SetupLedgerMember(
                        memberElement.GetProperty("setting_key").GetString()!,
                        SetupStorageJson.ParseOperation(memberElement.GetProperty("operation").GetString()!)));
                }

                targets.Add(new SetupLedgerTarget(
                    Guid.Parse(targetElement.GetProperty("record_id").GetString()!),
                    SetupStorageJson.ParseTargetKind(targetElement.GetProperty("target_kind").GetString()!),
                    targetElement.GetProperty("target_label").GetString()!,
                    targetElement.GetProperty("owning_adapter").GetString()!,
                    members,
                    targetElement.GetProperty("previous_state_hash").GetString()!,
                    SetupStorageJson.GetNullableString(targetElement, "applied_state_hash"),
                    SetupStorageJson.GetNullableString(targetElement, "backup_reference"),
                    SetupStorageJson.GetNullableString(targetElement, "outcome_code"),
                    SetupStorageJson.ParseRollbackStatus(targetElement.GetProperty("rollback_status").GetString()!),
                    SetupStorageJson.ParseRestartRequirement(targetElement.GetProperty("restart_requirement").GetString()!),
                    targetElement.GetProperty("tool_version").GetString()!));
            }

            changeSets.Add(new SetupLedgerChangeSet(
                Guid.Parse(changeSetElement.GetProperty("change_set_id").GetString()!),
                changeSetElement.GetProperty("adapter").GetString()!,
                changeSetElement.GetProperty("selected_target").GetString()!,
                SetupStorageJson.ParseTimestamp(changeSetElement.GetProperty("created_at").GetString()!),
                SetupStorageJson.ParseTimestamp(changeSetElement.GetProperty("updated_at").GetString()!),
                changeSetElement.GetProperty("tool_version").GetString()!,
                SetupStorageJson.GetNullableString(changeSetElement, "outcome_code"),
                SetupStorageJson.ParseChangeSetState(changeSetElement.GetProperty("state").GetString()!),
                targets));
        }

        var ledger = new SetupOwnershipLedger(1, changeSets);
        SetupStorageValidation.ValidateLedger(ledger);
        return ledger;
    }

    private static bool IsTerminal(SetupChangeSetState state) => state is
        SetupChangeSetState.Applied or
        SetupChangeSetState.NoChanges or
        SetupChangeSetState.Restored or
        SetupChangeSetState.RolledBack;
}

internal static class SetupStorageFile
{
    public static void WriteNew(ISetupPlatform platform, string destination, byte[] bytes)
    {
        var temporary = destination + ".tmp";
        try
        {
            platform.FileSystem.WriteAllBytes(temporary, bytes);
            platform.FileSystem.FlushFile(temporary);
            platform.FileSystem.MoveFile(temporary, destination, overwrite: false);
        }
        catch (Exception)
        {
            TryDelete(platform, temporary);
            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }
    }

    public static void WriteAtomic(ISetupPlatform platform, string destination, byte[] bytes)
    {
        var temporary = destination + ".tmp";
        try
        {
            platform.FileSystem.WriteAllBytes(temporary, bytes);
            platform.FileSystem.FlushFile(temporary);
            if (platform.FileSystem.FileExists(destination))
            {
                platform.FileSystem.ReplaceFile(temporary, destination);
            }
            else
            {
                platform.FileSystem.MoveFile(temporary, destination, overwrite: false);
            }
        }
        catch (Exception)
        {
            TryDelete(platform, temporary);
            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }
    }

    private static void TryDelete(ISetupPlatform platform, string path)
    {
        try
        {
            platform.FileSystem.DeleteFile(path);
        }
        catch (Exception)
        {
        }
    }
}

internal static partial class SetupStorageValidation
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingKeyPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolVersionPattern();

    [GeneratedRegex("^[a-z0-9]+(?:[_-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static void ValidatePlan(SetupPrivatePlan plan)
    {
        Require(plan.SchemaVersion == 1 && IsUuidV7(plan.ChangeSetId));
        RequireSlug(plan.Adapter);
        RequireSlug(plan.SelectedTarget);
        RequireTimestamp(plan.CreatedAt);
        Require(ToolVersionPattern().IsMatch(plan.ToolVersion));
        Require(plan.Targets.Count <= 16);
        foreach (var target in plan.Targets)
        {
            Require(IsUuidV7(target.RecordId));
            Require(!string.IsNullOrWhiteSpace(target.TargetLocation));
            Require(HashPattern().IsMatch(target.BaseStateHash));
            Require(target.DesiredState is not null);
            Require(target.Members.Count <= 32);
            foreach (var member in target.Members)
            {
                Require(SettingKeyPattern().IsMatch(member.SettingKey));
                Require(member.DesiredValue is not null || member.Operation == SetupOperation.Remove);
            }
        }

        Require(plan.Targets.Select(target => target.RecordId).Distinct().Count() == plan.Targets.Count);
    }

    public static void ValidateLedger(SetupOwnershipLedger ledger)
    {
        Require(ledger.SchemaVersion == 1);
        Require(ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId).Distinct().Count() == ledger.ChangeSets.Count);
        foreach (var changeSet in ledger.ChangeSets)
        {
            Require(IsUuidV7(changeSet.ChangeSetId));
            RequireSlug(changeSet.Adapter);
            RequireSlug(changeSet.SelectedTarget);
            RequireTimestamp(changeSet.CreatedAt);
            RequireTimestamp(changeSet.UpdatedAt);
            Require(changeSet.UpdatedAt >= changeSet.CreatedAt);
            Require(ToolVersionPattern().IsMatch(changeSet.ToolVersion));
            RequireNullableCode(changeSet.OutcomeCode);
            Require(changeSet.Targets.Count <= 16);
            Require(changeSet.Targets.Select(target => target.RecordId).Distinct().Count() == changeSet.Targets.Count);
            foreach (var target in changeSet.Targets)
            {
                Require(IsUuidV7(target.RecordId));
                RequireSlug(target.TargetLabel);
                RequireSlug(target.OwningAdapter);
                Require(target.Members.Count <= 32);
                foreach (var member in target.Members)
                {
                    Require(SettingKeyPattern().IsMatch(member.SettingKey));
                }

                Require(HashPattern().IsMatch(target.PreviousStateHash));
                Require(target.AppliedStateHash is null || HashPattern().IsMatch(target.AppliedStateHash));
                RequireNullableSlug(target.BackupReference);
                RequireNullableCode(target.OutcomeCode);
                Require(ToolVersionPattern().IsMatch(target.ToolVersion));
            }
        }
    }

    public static void ValidatePlanAndLedger(SetupPrivatePlan plan, SetupLedgerChangeSet changeSet)
    {
        ValidatePlan(plan);
        ValidateLedger(new SetupOwnershipLedger(1, [changeSet]));
        var matches = changeSet.State == SetupChangeSetState.Planned &&
            plan.ChangeSetId == changeSet.ChangeSetId &&
            plan.Adapter == changeSet.Adapter &&
            plan.SelectedTarget == changeSet.SelectedTarget &&
            plan.CreatedAt == changeSet.CreatedAt &&
            plan.CreatedAt == changeSet.UpdatedAt &&
            plan.ToolVersion == changeSet.ToolVersion &&
            plan.Targets.Count == changeSet.Targets.Count;
        if (matches)
        {
            for (var index = 0; index < plan.Targets.Count; index++)
            {
                var planTarget = plan.Targets[index];
                var ledgerTarget = changeSet.Targets[index];
                matches = planTarget.RecordId == ledgerTarget.RecordId &&
                    planTarget.TargetKind == ledgerTarget.TargetKind &&
                    planTarget.BaseStateHash == ledgerTarget.PreviousStateHash &&
                    planTarget.Members.Count == ledgerTarget.Members.Count;
                if (!matches)
                {
                    break;
                }

                for (var memberIndex = 0; memberIndex < planTarget.Members.Count; memberIndex++)
                {
                    matches = planTarget.Members[memberIndex].SettingKey == ledgerTarget.Members[memberIndex].SettingKey &&
                        planTarget.Members[memberIndex].Operation == ledgerTarget.Members[memberIndex].Operation;
                    if (!matches)
                    {
                        break;
                    }
                }
            }
        }

        if (!matches)
        {
            throw new SetupStorageException(SetupStorageCodes.PlanLedgerMismatch);
        }
    }

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static void RequireSlug(string value) => Require(value.Length <= 128 && SlugPattern().IsMatch(value));

    private static void RequireNullableSlug(string? value)
    {
        if (value is not null)
        {
            RequireSlug(value);
        }
    }

    private static void RequireNullableCode(string? value)
    {
        if (value is not null)
        {
            Require(value.Length <= 128 && CodePattern().IsMatch(value));
        }
    }

    private static void RequireTimestamp(DateTimeOffset value) => Require(value.Offset == TimeSpan.Zero);

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new FormatException();
        }
    }
}

internal static class SetupStorageJson
{
    public static Utf8JsonWriter CreateWriter(Stream stream) => new(stream, new JsonWriterOptions { Indented = true });

    public static string FormatTimestamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseTimestamp(string value)
    {
        if (!DateTimeOffset.TryParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) || parsed.Offset != TimeSpan.Zero)
        {
            throw new FormatException();
        }

        return parsed;
    }

    public static void RequireProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException();
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || !actual.SequenceEqual(names, StringComparer.Ordinal))
        {
            throw new FormatException();
        }
    }

    public static string? GetNullableString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Null ? null : property.GetString();
    }

    public static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    public static string TargetKind(SetupTargetKind value) => value switch
    {
        SetupTargetKind.Env => "env",
        SetupTargetKind.Json => "json",
        SetupTargetKind.Toml => "toml",
        SetupTargetKind.StartupTask => "startup_task",
        SetupTargetKind.File => "file",
        SetupTargetKind.Guidance => "guidance",
        _ => throw new FormatException(),
    };

    public static SetupTargetKind ParseTargetKind(string value) => value switch
    {
        "env" => SetupTargetKind.Env,
        "json" => SetupTargetKind.Json,
        "toml" => SetupTargetKind.Toml,
        "startup_task" => SetupTargetKind.StartupTask,
        "file" => SetupTargetKind.File,
        "guidance" => SetupTargetKind.Guidance,
        _ => throw new FormatException(),
    };

    public static string Operation(SetupOperation value) => value switch
    {
        SetupOperation.Add => "add",
        SetupOperation.Replace => "replace",
        SetupOperation.Remove => "remove",
        SetupOperation.Mixed => "mixed",
        SetupOperation.NoOp => "no-op",
        _ => throw new FormatException(),
    };

    public static SetupOperation ParseOperation(string value) => value switch
    {
        "add" => SetupOperation.Add,
        "replace" => SetupOperation.Replace,
        "remove" => SetupOperation.Remove,
        "mixed" => SetupOperation.Mixed,
        "no-op" => SetupOperation.NoOp,
        _ => throw new FormatException(),
    };

    public static string ChangeSetState(SetupChangeSetState value) => value switch
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
        _ => throw new FormatException(),
    };

    public static SetupChangeSetState ParseChangeSetState(string value) => value switch
    {
        "planned" => SetupChangeSetState.Planned,
        "applying" => SetupChangeSetState.Applying,
        "applied" => SetupChangeSetState.Applied,
        "no_changes" => SetupChangeSetState.NoChanges,
        "compensating" => SetupChangeSetState.Compensating,
        "restored" => SetupChangeSetState.Restored,
        "rolling_back" => SetupChangeSetState.RollingBack,
        "partial" => SetupChangeSetState.Partial,
        "rolled_back" => SetupChangeSetState.RolledBack,
        _ => throw new FormatException(),
    };

    public static string RollbackStatus(SetupLedgerRollbackStatus value) => value switch
    {
        SetupLedgerRollbackStatus.NotAvailable => "not_available",
        SetupLedgerRollbackStatus.Pending => "pending",
        SetupLedgerRollbackStatus.Succeeded => "succeeded",
        SetupLedgerRollbackStatus.Failed => "failed",
        SetupLedgerRollbackStatus.Stale => "stale",
        _ => throw new FormatException(),
    };

    public static SetupLedgerRollbackStatus ParseRollbackStatus(string value) => value switch
    {
        "not_available" => SetupLedgerRollbackStatus.NotAvailable,
        "pending" => SetupLedgerRollbackStatus.Pending,
        "succeeded" => SetupLedgerRollbackStatus.Succeeded,
        "failed" => SetupLedgerRollbackStatus.Failed,
        "stale" => SetupLedgerRollbackStatus.Stale,
        _ => throw new FormatException(),
    };

    public static string RestartRequirement(SetupRestartRequirement value) => value switch
    {
        SetupRestartRequirement.None => "none",
        SetupRestartRequirement.RestartVsCode => "restart_vscode",
        SetupRestartRequirement.RestartTerminalSession => "restart_terminal_session",
        _ => throw new FormatException(),
    };

    public static SetupRestartRequirement ParseRestartRequirement(string value) => value switch
    {
        "none" => SetupRestartRequirement.None,
        "restart_vscode" => SetupRestartRequirement.RestartVsCode,
        "restart_terminal_session" => SetupRestartRequirement.RestartTerminalSession,
        _ => throw new FormatException(),
    };
}

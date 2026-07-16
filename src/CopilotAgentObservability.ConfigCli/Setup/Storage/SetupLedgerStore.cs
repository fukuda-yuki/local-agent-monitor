using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

internal static class SetupStorageCodes
{
    public const string PlanAlreadyExists = "setup_plan_already_exists";
    public const string PlanLedgerMismatch = "setup_plan_ledger_mismatch";
    public const string ChangeSetAlreadyExists = "setup_change_set_already_exists";
    public const string WriteFailed = "setup_storage_write_failed";
    public const string LockRequired = "setup_lock_required";
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
    SetupStatusProjection StatusProjection,
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
    internal const int MaximumLedgerBytes = 1024 * 1024;
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;
    private readonly SetupPlanStore planStore;

    public SetupLedgerStore(ISetupPlatform platform, SetupRuntimePaths paths, SetupPlanStore planStore)
    {
        this.platform = platform;
        this.paths = paths;
        this.planStore = planStore;
    }

    public SetupOwnershipLedger Load() => LoadCore(LedgerReadPolicy.RequireNonTerminalPlans);

    internal SetupOwnershipLedger LoadForRecovery() => LoadCore(LedgerReadPolicy.AllowMissingNonTerminalPlans);

    private SetupOwnershipLedger LoadCore(LedgerReadPolicy readPolicy)
    {
        var source = paths.OwnershipLedger;
        SetupOwnershipLedger ledger;
        try
        {
            var initialMetadata = platform.FileSystem.GetPathMetadata(source);
            if (!initialMetadata.Exists)
            {
                return new SetupOwnershipLedger(1, []);
            }

            ValidateLedgerMetadata(initialMetadata);
            var read = platform.FileSystem.ReadAtMostBytes(source, MaximumLedgerBytes);
            if (!read.IsComplete)
            {
                throw new FormatException();
            }

            ledger = Deserialize(read.Bytes);
            ValidateLedgerMetadata(platform.FileSystem.GetPathMetadata(source));
        }
        catch (SetupStorageException)
        {
            throw;
        }
        catch (Exception exception) when (SetupStorageException.ShouldMap(exception))
        {
            throw new SetupStorageException(SetupCodes.LedgerCorrupt);
        }

        if (readPolicy == LedgerReadPolicy.RequireNonTerminalPlans)
        {
            foreach (var changeSet in ledger.ChangeSets.Where(changeSet => !IsTerminal(changeSet.State)))
            {
                if (planStore.Load(changeSet.ChangeSetId) is null)
                {
                    throw new SetupStorageException(SetupCodes.RecoveryRequired);
                }
            }
        }

        return ledger;
    }

    private static void ValidateLedgerMetadata(SetupPathMetadata metadata)
    {
        if (!metadata.Exists ||
            metadata.Kind != SetupPathKind.File ||
            (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FormatException();
        }
    }

    public void Save(SetupLock setupLock, SetupOwnershipLedger ledger)
    {
        setupLock.ExecuteWhileHeld(platform, paths, () => SaveCore(ledger));
    }

    public void PersistPlannedChangeSet(SetupLock setupLock, SetupPrivatePlan plan, SetupLedgerChangeSet plannedChangeSet)
    {
        setupLock.ExecuteWhileHeld(platform, paths, () =>
        {
            SetupStorageValidation.ValidatePlanAndLedger(plan, plannedChangeSet);
            if (plannedChangeSet.OutcomeCode is not null ||
                plannedChangeSet.UpdatedAt != plannedChangeSet.CreatedAt)
            {
                throw new SetupStorageException(SetupStorageCodes.PlanLedgerMismatch);
            }

            var ledger = Load();
            if (ledger.ChangeSets.Any(changeSet => changeSet.ChangeSetId == plan.ChangeSetId))
            {
                throw new SetupStorageException(SetupStorageCodes.ChangeSetAlreadyExists);
            }

            var planCreated = false;
            try
            {
                planStore.CreateCore(plan);
                planCreated = true;
                platform.Execution.Checkpoint("after-plan-persisted-before-ledger");
                SaveCore(ledger with { ChangeSets = [.. ledger.ChangeSets, plannedChangeSet] });
            }
            catch (SetupStorageException exception)
            {
                if (!planCreated && exception.Code == SetupStorageCodes.WriteFailed)
                {
                    planStore.DeleteCore(plan.ChangeSetId);
                }
                else if (planCreated)
                {
                    DeletePlanWhenLedgerDefinitelyDoesNotReference(plan.ChangeSetId);
                }

                throw;
            }
            catch (Exception)
            {
                if (planCreated)
                {
                    DeletePlanWhenLedgerDefinitelyDoesNotReference(plan.ChangeSetId);
                }

                throw new SetupStorageException(SetupStorageCodes.WriteFailed);
            }
        });
    }

    private void SaveCore(SetupOwnershipLedger ledger)
    {
        var bytes = Serialize(ledger);
        paths.EnsureRoot();
        SetupStorageFile.WriteAtomic(platform, paths.OwnershipLedger, bytes);
    }

    private void DeletePlanWhenLedgerDefinitelyDoesNotReference(Guid changeSetId)
    {
        try
        {
            var durableLedger = Load();
            if (durableLedger.ChangeSets.All(changeSet => changeSet.ChangeSetId != changeSetId))
            {
                planStore.DeleteCore(changeSetId);
            }
        }
        catch (Exception)
        {
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
                    WriteStatusProjection(writer, target.StatusProjection);
                    writer.WriteString("tool_version", target.ToolVersion);
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var bytes = buffer.ToArray();
        if (bytes.Length > MaximumLedgerBytes)
        {
            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }

        return bytes;
    }

    private static void WriteStatusProjection(Utf8JsonWriter writer, SetupStatusProjection projection)
    {
        writer.WritePropertyName("status_projection");
        writer.WriteStartObject();
        writer.WriteBoolean("detected", projection.Detected);
        SetupStorageJson.WriteNullableString(writer, "detected_version", projection.DetectedVersion);
        writer.WriteString("operation", SetupStorageJson.Operation(projection.Operation));
        SetupStorageJson.WriteNullableEffectiveSource(writer, "effective_source", projection.EffectiveSource);
        SetupStorageJson.WriteNullableString(writer, "endpoint", projection.Endpoint);
        writer.WritePropertyName("expected_result");
        if (projection.ExpectedResult is { } expectedResult)
        {
            expectedResult.WriteTo(writer);
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("guidance");
        if (projection.Guidance is { } guidance)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", guidance.Kind);
            writer.WriteString("language", guidance.Language);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("changes");
        writer.WriteStartArray();
        foreach (var change in projection.Changes)
        {
            writer.WriteStartObject();
            writer.WriteString("setting_key", change.SettingKey);
            writer.WriteString("operation", SetupStorageJson.Operation(change.Operation));
            writer.WriteString("previous_state", change.PreviousState);
            writer.WriteString("new_state", change.NewState);
            writer.WriteString("conflict", change.Conflict);
            writer.WriteBoolean("managed", change.Managed);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
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
        foreach (var changeSetElement in SetupStorageJson.GetArray(root, "change_sets"))
        {
            SetupStorageJson.RequireProperties(changeSetElement, "change_set_id", "adapter", "selected_target", "created_at", "updated_at", "tool_version", "outcome_code", "state", "targets");
            var targets = new List<SetupLedgerTarget>();
            foreach (var targetElement in SetupStorageJson.GetArray(changeSetElement, "targets"))
            {
                SetupStorageJson.RequireProperties(targetElement, "record_id", "target_kind", "target_label", "owning_adapter", "members", "previous_state_hash", "applied_state_hash", "backup_reference", "outcome_code", "rollback_status", "restart_requirement", "status_projection", "tool_version");
                var members = new List<SetupLedgerMember>();
                foreach (var memberElement in SetupStorageJson.GetArray(targetElement, "members"))
                {
                    SetupStorageJson.RequireProperties(memberElement, "setting_key", "operation");
                    members.Add(new SetupLedgerMember(
                        SetupStorageJson.GetString(memberElement, "setting_key"),
                        SetupStorageJson.ParseOperation(SetupStorageJson.GetString(memberElement, "operation"))));
                }

                targets.Add(new SetupLedgerTarget(
                    SetupStorageJson.GetGuid(targetElement, "record_id"),
                    SetupStorageJson.ParseTargetKind(SetupStorageJson.GetString(targetElement, "target_kind")),
                    SetupStorageJson.GetString(targetElement, "target_label"),
                    SetupStorageJson.GetString(targetElement, "owning_adapter"),
                    members,
                    SetupStorageJson.GetString(targetElement, "previous_state_hash"),
                    SetupStorageJson.GetNullableString(targetElement, "applied_state_hash"),
                    SetupStorageJson.GetNullableString(targetElement, "backup_reference"),
                    SetupStorageJson.GetNullableString(targetElement, "outcome_code"),
                    SetupStorageJson.ParseRollbackStatus(SetupStorageJson.GetString(targetElement, "rollback_status")),
                    SetupStorageJson.ParseRestartRequirement(SetupStorageJson.GetString(targetElement, "restart_requirement")),
                    ReadStatusProjection(targetElement.GetProperty("status_projection")),
                    SetupStorageJson.GetString(targetElement, "tool_version")));
            }

            changeSets.Add(new SetupLedgerChangeSet(
                SetupStorageJson.GetGuid(changeSetElement, "change_set_id"),
                SetupStorageJson.GetString(changeSetElement, "adapter"),
                SetupStorageJson.GetString(changeSetElement, "selected_target"),
                SetupStorageJson.ParseTimestamp(SetupStorageJson.GetString(changeSetElement, "created_at")),
                SetupStorageJson.ParseTimestamp(SetupStorageJson.GetString(changeSetElement, "updated_at")),
                SetupStorageJson.GetString(changeSetElement, "tool_version"),
                SetupStorageJson.GetNullableString(changeSetElement, "outcome_code"),
                SetupStorageJson.ParseChangeSetState(SetupStorageJson.GetString(changeSetElement, "state")),
                targets));
        }

        var ledger = new SetupOwnershipLedger(1, changeSets);
        SetupStorageValidation.ValidateLedger(ledger);
        return ledger;
    }

    private static SetupStatusProjection ReadStatusProjection(JsonElement element)
    {
        SetupStorageJson.RequireProperties(element, "detected", "detected_version", "operation", "effective_source", "endpoint", "expected_result", "guidance", "changes");
        var detectedElement = element.GetProperty("detected");
        if (detectedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
        {
            throw new FormatException();
        }

        JsonElement? expectedResult = element.GetProperty("expected_result") is { ValueKind: not JsonValueKind.Null } expected
            ? expected.Clone()
            : null;
        SetupStatusGuidance? guidance = null;
        var guidanceElement = element.GetProperty("guidance");
        if (guidanceElement.ValueKind != JsonValueKind.Null)
        {
            SetupStorageJson.RequireProperties(guidanceElement, "kind", "language");
            guidance = new SetupStatusGuidance(
                SetupStorageJson.GetString(guidanceElement, "kind"),
                SetupStorageJson.GetString(guidanceElement, "language"));
        }

        var changes = new List<SetupMemberChangeResult>();
        foreach (var changeElement in SetupStorageJson.GetArray(element, "changes"))
        {
            SetupStorageJson.RequireProperties(changeElement, "setting_key", "operation", "previous_state", "new_state", "conflict", "managed");
            var managedElement = changeElement.GetProperty("managed");
            if (managedElement.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new FormatException();
            }

            changes.Add(new SetupMemberChangeResult(
                SetupStorageJson.GetString(changeElement, "setting_key"),
                SetupStorageJson.ParseOperation(SetupStorageJson.GetString(changeElement, "operation")),
                SetupStorageJson.GetString(changeElement, "previous_state"),
                SetupStorageJson.GetString(changeElement, "new_state"),
                SetupStorageJson.GetString(changeElement, "conflict"),
                managedElement.GetBoolean()));
        }

        return new SetupStatusProjection(
            detectedElement.GetBoolean(),
            SetupStorageJson.GetNullableString(element, "detected_version"),
            SetupStorageJson.ParseOperation(SetupStorageJson.GetString(element, "operation")),
            SetupStorageJson.GetNullableEffectiveSource(element, "effective_source"),
            SetupStorageJson.GetNullableString(element, "endpoint"),
            expectedResult,
            guidance,
            changes);
    }

    private static bool IsTerminal(SetupChangeSetState state) => state is
        SetupChangeSetState.Applied or
        SetupChangeSetState.NoChanges or
        SetupChangeSetState.Restored or
        SetupChangeSetState.RolledBack;

    private enum LedgerReadPolicy
    {
        RequireNonTerminalPlans,
        AllowMissingNonTerminalPlans,
    }
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
    private static readonly HashSet<string> OutcomeCodes = new(SetupCodes.ResultCodes, StringComparer.Ordinal);
    private static readonly string[] ClaudeEnvKeys =
    [
        "CLAUDE_CODE_ENABLE_TELEMETRY",
        "CLAUDE_CODE_ENHANCED_TELEMETRY_BETA",
        "OTEL_TRACES_EXPORTER",
        "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL",
        "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
        "OTEL_LOG_USER_PROMPTS",
        "OTEL_LOG_TOOL_DETAILS",
        "OTEL_LOG_TOOL_CONTENT",
    ];
    private static readonly string[] ClaudeHookEvents =
    [
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PermissionRequest",
        "PostToolUse",
        "PostToolUseFailure",
        "SubagentStart",
        "SubagentStop",
        "Stop",
        "StopFailure",
        "SessionEnd",
    ];

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SettingKeyPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    [GeneratedRegex("^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolVersionPattern();

    public static void ValidatePlan(SetupPrivatePlan plan)
    {
        Require(plan.SchemaVersion == 1 && IsUuidV7(plan.ChangeSetId));
        RequireSlug(plan.Adapter);
        RequireSlug(plan.SelectedTarget);
        RequireTimestamp(plan.CreatedAt);
        RequirePattern(plan.ToolVersion, ToolVersionPattern());
        var targets = RequireNotNull(plan.Targets);
        Require(targets.Count <= 16);
        foreach (var targetValue in targets)
        {
            var target = RequireNotNull(targetValue);
            Require(IsUuidV7(target.RecordId));
            Require(!string.IsNullOrWhiteSpace(target.TargetLocation));
            Require(Enum.IsDefined(target.TargetKind));
            RequirePattern(target.BaseStateHash, HashPattern());
            var members = RequireNotNull(target.Members);
            Require(members.Count <= 32);
            Require(members.Select(member => member?.SettingKey).Distinct(StringComparer.Ordinal).Count() == members.Count);
            foreach (var memberValue in members)
            {
                var member = RequireNotNull(memberValue);
                RequirePattern(member.SettingKey, SettingKeyPattern());
                Require(Enum.IsDefined(member.Operation));
                Require(IsValidDesiredValue(target.TargetKind, member));
            }

            ValidateDesiredState(plan.Adapter, target, members);
        }

        Require(targets.Select(target => target.RecordId).Distinct().Count() == targets.Count);
    }

    private static bool IsValidDesiredValue(SetupTargetKind targetKind, SetupPrivatePlanMember member) =>
        targetKind == SetupTargetKind.Env
            ? member.Operation switch
            {
                SetupOperation.NoOp => true,
                SetupOperation.Remove => member.DesiredValue is null,
                SetupOperation.Add or SetupOperation.Replace => member.DesiredValue is not null,
                _ => member.DesiredValue is not null,
            }
            : member.DesiredValue is not null || member.Operation == SetupOperation.Remove;

    private static void ValidateDesiredState(
        string adapter,
        SetupPrivatePlanTarget target,
        IReadOnlyList<SetupPrivatePlanMember> members)
    {
        if ((object)target.DesiredState is SetupInlineDesiredState inline)
        {
            Require(inline.Value is not null);
            return;
        }

        if ((object)target.DesiredState is SetupClaudeSettingsOwnedValuesDesiredState claude)
        {
            ValidateClaudeDesiredState(adapter, target, members, claude);
            return;
        }

        if ((object)target.DesiredState is not SetupJsoncOwnedValuesDesiredState tagged)
        {
            throw new FormatException();
        }

        Require(adapter == "github-copilot" && target.TargetKind == SetupTargetKind.Json);
        RequirePattern(tagged.ExpectedStateHash, HashPattern());
        var ownedValues = RequireNotNull(tagged.OwnedValues);
        Require(ownedValues.Count is >= 1 and <= 32 && ownedValues.Count == members.Count);
        for (var index = 0; index < ownedValues.Count; index++)
        {
            var ownedValue = RequireNotNull(ownedValues[index]);
            RequirePattern(ownedValue.SettingKey, SettingKeyPattern());
            Require(ownedValue.SettingKey == members[index].SettingKey);
            Require(ownedValue.ValueKind switch
            {
                "boolean" => ownedValue.Value is bool,
                "string" => ownedValue.Value is string { Length: >= 1 and <= 2048 },
                _ => false,
            });
        }

        Require(ownedValues
            .Select(ownedValue => ownedValue.SettingKey)
            .Distinct(StringComparer.Ordinal)
            .Count() == ownedValues.Count);
    }

    private static void ValidateClaudeDesiredState(
        string adapter,
        SetupPrivatePlanTarget target,
        IReadOnlyList<SetupPrivatePlanMember> members,
        SetupClaudeSettingsOwnedValuesDesiredState desiredState)
    {
        Require(adapter == "claude-code" && target.TargetKind == SetupTargetKind.Json);
        RequirePattern(desiredState.ExpectedStateHash, HashPattern());
        var env = RequireNotNull(desiredState.OwnedEnv);
        var hooks = RequireNotNull(desiredState.OwnedHooks);
        Require(env.Count is 5 or 8);
        Require(hooks.Count == ClaudeHookEvents.Length);
        Require(members.Count == env.Count + hooks.Count);

        for (var index = 0; index < env.Count; index++)
        {
            var value = RequireNotNull(env[index]);
            Require(value.Key == ClaudeEnvKeys[index]);
            Require(value.Value is { Length: >= 1 and <= 2048 });
            Require(members[index].SettingKey == $"env.{value.Key}");
        }

        Require(env.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() == env.Count);
        for (var index = 0; index < hooks.Count; index++)
        {
            var hook = RequireNotNull(hooks[index]);
            Require(hook.EventName == ClaudeHookEvents[index]);
            Require(hook.Command is { Length: >= 1 and <= 2048 });
            var arguments = RequireNotNull(hook.Arguments);
            Require(arguments.Count is >= 1 and <= 32);
            foreach (var argument in arguments)
            {
                Require(argument is { Length: >= 1 and <= 2048 });
            }

            Require(hook.TimeoutSeconds == 5);
            Require(members[env.Count + index].SettingKey == $"hooks.{hook.EventName}");
        }
    }

    public static void ValidateLedger(SetupOwnershipLedger ledger)
    {
        Require(ledger.SchemaVersion == 1);
        var changeSets = RequireNotNull(ledger.ChangeSets);
        Require(changeSets.Select(changeSet => changeSet?.ChangeSetId).Distinct().Count() == changeSets.Count);
        foreach (var changeSetValue in changeSets)
        {
            var changeSet = RequireNotNull(changeSetValue);
            Require(IsUuidV7(changeSet.ChangeSetId));
            RequireSlug(changeSet.Adapter);
            RequireSlug(changeSet.SelectedTarget);
            RequireTimestamp(changeSet.CreatedAt);
            RequireTimestamp(changeSet.UpdatedAt);
            Require(changeSet.UpdatedAt >= changeSet.CreatedAt);
            Require(Enum.IsDefined(changeSet.State));
            RequirePattern(changeSet.ToolVersion, ToolVersionPattern());
            RequireNullableCode(changeSet.OutcomeCode);
            var targets = RequireNotNull(changeSet.Targets);
            if (changeSet.State == SetupChangeSetState.Planned)
            {
                Require(changeSet.OutcomeCode is null or SetupCodes.StalePlan);
                Require(changeSet.OutcomeCode == SetupCodes.StalePlan ||
                    changeSet.UpdatedAt == changeSet.CreatedAt);
            }

            Require(targets.Count <= 16);
            Require(targets.Select(target => target?.RecordId).Distinct().Count() == targets.Count);
            foreach (var targetValue in targets)
            {
                var target = RequireNotNull(targetValue);
                Require(IsUuidV7(target.RecordId));
                Require(Enum.IsDefined(target.TargetKind));
                Require(Enum.IsDefined(target.RollbackStatus));
                Require(Enum.IsDefined(target.RestartRequirement));
                RequireSlug(target.TargetLabel);
                RequireSlug(target.OwningAdapter);
                Require(target.OwningAdapter == changeSet.Adapter);
                var members = RequireNotNull(target.Members);
                Require(members.Count <= 32);
                Require(members.Select(member => member?.SettingKey).Distinct(StringComparer.Ordinal).Count() == members.Count);
                foreach (var memberValue in members)
                {
                    var member = RequireNotNull(memberValue);
                    RequirePattern(member.SettingKey, SettingKeyPattern());
                    Require(Enum.IsDefined(member.Operation));
                }

                RequirePattern(target.PreviousStateHash, HashPattern());
                Require(target.AppliedStateHash is null || IsPattern(target.AppliedStateHash, HashPattern()));
                RequireNullableSlug(target.BackupReference);
                RequireNullableCode(target.OutcomeCode);
                RequirePattern(target.ToolVersion, ToolVersionPattern());
                Require(target.ToolVersion == changeSet.ToolVersion);
                var projection = RequireNotNull(target.StatusProjection);
                Require(SetupContractValidator.IsValidLedgerStatusProjection(projection, target.TargetKind, target.TargetLabel));
                Require(projection.Changes.Count == members.Count);
                for (var index = 0; index < members.Count; index++)
                {
                    Require(projection.Changes[index].SettingKey == members[index].SettingKey);
                    Require(projection.Changes[index].Operation == members[index].Operation);
                }

                var allNoOp = members.Count > 0 && members.All(member => member.Operation == SetupOperation.NoOp);
                if (target.TargetKind == SetupTargetKind.Guidance)
                {
                    Require(members.Count == 0);
                }
                else
                {
                    Require(members.Count > 0);
                }

                if (allNoOp || target.TargetKind == SetupTargetKind.Guidance)
                {
                    Require(target.AppliedStateHash is null);
                    Require(target.BackupReference is null);
                    Require(target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable);
                }

                if (changeSet.State == SetupChangeSetState.Planned)
                {
                    Require(target.AppliedStateHash is null);
                    Require(target.BackupReference is null);
                    Require(target.OutcomeCode is null);
                    Require(target.RollbackStatus == SetupLedgerRollbackStatus.NotAvailable);
                }
            }
        }
    }

    public static void ValidatePlanAndLedger(SetupPrivatePlan plan, SetupLedgerChangeSet changeSet)
    {
        ValidatePlan(plan);
        ValidateClaudeTargetBindings(plan, changeSet);
        ValidateLedger(new SetupOwnershipLedger(1, [changeSet]));
        var matches = changeSet.State == SetupChangeSetState.Planned &&
            plan.ChangeSetId == changeSet.ChangeSetId &&
            plan.Adapter == changeSet.Adapter &&
            plan.SelectedTarget == changeSet.SelectedTarget &&
            plan.CreatedAt == changeSet.CreatedAt &&
            (plan.CreatedAt == changeSet.UpdatedAt || changeSet.OutcomeCode == SetupCodes.StalePlan) &&
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

        ValidateDesiredStateBindings(plan, changeSet);
    }

    private static void ValidateClaudeTargetBindings(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet)
    {
        if (plan.Adapter != "claude-code" && changeSet.Adapter != "claude-code")
        {
            return;
        }

        var expected = plan.SelectedTarget switch
        {
            "cli" => new[]
            {
                (Kind: SetupTargetKind.Json, Label: "claude-code-user-settings"),
            },
            "app-sdk" => new[]
            {
                (Kind: SetupTargetKind.Guidance, Label: "claude-agent-sdk-python-guidance"),
                (Kind: SetupTargetKind.Guidance, Label: "claude-agent-sdk-typescript-guidance"),
            },
            "all" => new[]
            {
                (Kind: SetupTargetKind.Json, Label: "claude-code-user-settings"),
                (Kind: SetupTargetKind.Guidance, Label: "claude-agent-sdk-python-guidance"),
                (Kind: SetupTargetKind.Guidance, Label: "claude-agent-sdk-typescript-guidance"),
            },
            _ => [],
        };
        if (plan.Adapter != "claude-code" ||
            changeSet.Adapter != "claude-code" ||
            plan.SelectedTarget != changeSet.SelectedTarget ||
            expected.Length == 0 ||
            plan.Targets.Count != expected.Length ||
            changeSet.Targets is null ||
            changeSet.Targets.Count != expected.Length)
        {
            throw new SetupStorageException(SetupCodes.RecoveryRequired);
        }

        for (var index = 0; index < expected.Length; index++)
        {
            var planTarget = plan.Targets[index];
            var ledgerTarget = changeSet.Targets[index];
            if (planTarget.TargetKind != expected[index].Kind ||
                ledgerTarget is null ||
                ledgerTarget.TargetKind != expected[index].Kind ||
                ledgerTarget.TargetLabel != expected[index].Label ||
                expected[index].Kind == SetupTargetKind.Guidance &&
                planTarget.TargetLocation != expected[index].Label)
            {
                throw new SetupStorageException(SetupCodes.RecoveryRequired);
            }
        }
    }

    public static void ValidateDesiredStateBindings(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet)
    {
        ValidateClaudeTargetBindings(plan, changeSet);
        if (plan.Targets.Count != changeSet.Targets.Count)
        {
            throw new SetupStorageException(SetupStorageCodes.PlanLedgerMismatch);
        }

        for (var index = 0; index < plan.Targets.Count; index++)
        {
            var planTarget = plan.Targets[index];
            var ledgerTarget = changeSet.Targets[index];
            var requiresGithubArm = plan.Adapter == "github-copilot" &&
                planTarget.TargetKind == SetupTargetKind.Json &&
                ledgerTarget.TargetLabel is
                    "vscode-stable-default-user-settings" or
                    "vscode-insiders-default-user-settings";
            var requiresClaudeArm = plan.Adapter == "claude-code" &&
                planTarget.TargetKind == SetupTargetKind.Json &&
                ledgerTarget.TargetLabel == "claude-code-user-settings";
            var actualGithubArm = (object)planTarget.DesiredState is SetupJsoncOwnedValuesDesiredState;
            var actualClaudeArm = (object)planTarget.DesiredState is SetupClaudeSettingsOwnedValuesDesiredState;
            if (requiresGithubArm != actualGithubArm ||
                requiresClaudeArm != actualClaudeArm ||
                (requiresGithubArm && actualClaudeArm) ||
                (requiresClaudeArm && actualGithubArm))
            {
                throw new SetupStorageException(SetupCodes.RecoveryRequired);
            }
        }

        ValidateGuidanceBindings(plan, changeSet);
        if (plan.Adapter == "claude-code")
        {
            try
            {
                _ = ClaudeAgentSdkGuidanceVariant.ValidatePair(plan, changeSet);
            }
            catch (InvalidOperationException)
            {
                throw new SetupStorageException(SetupCodes.RecoveryRequired);
            }
        }
    }

    private static void ValidateGuidanceBindings(
        SetupPrivatePlan plan,
        SetupLedgerChangeSet changeSet)
    {
        var guidance = changeSet.Targets
            .Where(target => target.TargetKind == SetupTargetKind.Guidance)
            .ToArray();
        if (plan.Adapter == "github-copilot")
        {
            Require(guidance.All(target =>
                target.TargetLabel == "github-copilot-app-sdk-guidance" &&
                target.StatusProjection.Guidance is { Kind: "caller_managed_sample", Language: "dotnet" }));
            return;
        }

        if (plan.Adapter != "claude-code")
        {
            return;
        }

        var labels = guidance.Select(target => target.TargetLabel).ToArray();
        var expected = plan.SelectedTarget switch
        {
            "cli" => Array.Empty<string>(),
            "app-sdk" or "all" =>
            ["claude-agent-sdk-python-guidance", "claude-agent-sdk-typescript-guidance"],
            _ => throw new FormatException(),
        };
        Require(labels.SequenceEqual(expected, StringComparer.Ordinal));
        if (guidance.Length == 2)
        {
            Require(guidance[0].StatusProjection.Guidance is { Kind: "caller_managed_sample", Language: "python" });
            Require(guidance[1].StatusProjection.Guidance is { Kind: "caller_managed_sample", Language: "typescript" });
        }
    }

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static void RequireSlug(string? value) => Require(value is { Length: <= 128 } && SlugPattern().IsMatch(value));

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
            Require(OutcomeCodes.Contains(value));
        }
    }

    private static T RequireNotNull<T>(T? value)
        where T : class
    {
        Require(value is not null);
        return value!;
    }

    private static void RequirePattern(string? value, Regex pattern) => Require(IsPattern(value, pattern));

    private static bool IsPattern(string? value, Regex pattern) => value is not null && pattern.IsMatch(value);

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
    public static Utf8JsonWriter CreateWriter(Stream stream) => new(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" });

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

        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        var actual = new HashSet<string>(StringComparer.Ordinal);
        if (expected.Count != names.Length)
        {
            throw new FormatException();
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!expected.Contains(property.Name) || !actual.Add(property.Name))
            {
                throw new FormatException();
            }
        }

        if (actual.Count != expected.Count)
        {
            throw new FormatException();
        }
    }

    public static JsonElement.ArrayEnumerator GetArray(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException();
        }

        return property.EnumerateArray();
    }

    public static string GetString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } value)
        {
            throw new FormatException();
        }

        return value;
    }

    public static Guid GetGuid(JsonElement element, string propertyName)
    {
        if (!Guid.TryParseExact(GetString(element, propertyName), "D", out var value))
        {
            throw new FormatException();
        }

        return value;
    }

    public static string? GetNullableString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        if (property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } value)
        {
            throw new FormatException();
        }

        return value;
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

    public static void WriteNullableEffectiveSource(Utf8JsonWriter writer, string propertyName, SetupEffectiveSource? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, EffectiveSource(value.Value));
        }
    }

    public static SetupEffectiveSource? GetNullableEffectiveSource(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Null
            ? null
            : ParseEffectiveSource(GetString(element, propertyName));
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

    public static string EffectiveSource(SetupEffectiveSource value) => value switch
    {
        SetupEffectiveSource.ManagedPolicy => "managed_policy",
        SetupEffectiveSource.Environment => "environment",
        SetupEffectiveSource.UserSetting => "user_setting",
        SetupEffectiveSource.Default => "default",
        _ => throw new FormatException(),
    };

    public static SetupEffectiveSource ParseEffectiveSource(string value) => value switch
    {
        "managed_policy" => SetupEffectiveSource.ManagedPolicy,
        "environment" => SetupEffectiveSource.Environment,
        "user_setting" => SetupEffectiveSource.UserSetting,
        "default" => SetupEffectiveSource.Default,
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
        SetupRestartRequirement.RestartAgentProcess => "restart_agent_process",
        _ => throw new FormatException(),
    };

    public static SetupRestartRequirement ParseRestartRequirement(string value) => value switch
    {
        "none" => SetupRestartRequirement.None,
        "restart_vscode" => SetupRestartRequirement.RestartVsCode,
        "restart_terminal_session" => SetupRestartRequirement.RestartTerminalSession,
        "restart_agent_process" => SetupRestartRequirement.RestartAgentProcess,
        _ => throw new FormatException(),
    };
}

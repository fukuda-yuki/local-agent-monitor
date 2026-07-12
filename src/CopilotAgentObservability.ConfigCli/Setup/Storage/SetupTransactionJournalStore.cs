using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Setup.Storage;

internal static class SetupJournalStorageCodes
{
    public const string AlreadyExists = "setup_journal_already_exists";
    public const string Corrupt = "setup_journal_corrupt";
    public const string VersionUnsupported = "setup_journal_version_unsupported";
    public const string TransitionInvalid = "setup_journal_transition_invalid";
    public const string StaleUpdate = "setup_journal_stale_update";
}

internal enum SetupJournalOperation
{
    Apply,
    Rollback,
}

internal enum SetupJournalPhase
{
    Prepared,
    Applying,
    Compensating,
    RollingBack,
    Committed,
    Restored,
    Partial,
}

internal enum SetupJournalStepPhase
{
    Pending,
    MutationStarted,
    MutationCompleted,
    RestoreStarted,
    RestoreCompleted,
}

internal sealed record SetupTransactionJournal(
    int SchemaVersion,
    Guid ChangeSetId,
    SetupJournalOperation Operation,
    DateTimeOffset CreatedAt,
    SetupJournalPhase Phase,
    IReadOnlyList<SetupJournalTarget> Targets);

internal sealed record SetupJournalTarget(
    Guid RecordId,
    SetupTargetKind TargetKind,
    IReadOnlyList<SetupJournalStep> Steps);

internal sealed record SetupJournalStep(
    string? MemberKey,
    string PriorStateHash,
    string DesiredStateHash,
    string BackupReference,
    SetupJournalStepPhase Phase);

internal sealed partial class SetupTransactionJournalStore
{
    private readonly ISetupPlatform platform;
    private readonly SetupRuntimePaths paths;

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex MemberKeyPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex BackupReferencePattern();

    public SetupTransactionJournalStore(ISetupPlatform platform, SetupRuntimePaths paths)
    {
        this.platform = platform;
        this.paths = paths;
    }

    public SetupTransactionJournal CreatePrepared(
        SetupLock setupLock,
        Guid changeSetId,
        SetupJournalOperation operation,
        IReadOnlyList<SetupJournalTarget> targets)
    {
        setupLock.AssertHeld(platform, paths);
        var journal = new SetupTransactionJournal(
            1,
            changeSetId,
            operation,
            platform.Clock.UtcNow,
            SetupJournalPhase.Prepared,
            targets);
        ValidateForWrite(journal);

        var destination = paths.GetTransactionJournal(changeSetId);
        if (platform.FileSystem.FileExists(destination))
        {
            throw new SetupStorageException(SetupJournalStorageCodes.AlreadyExists);
        }

        platform.FileSystem.CreateDirectory(paths.Transactions);
        WriteCreateNew(destination, Serialize(journal));
        return journal;
    }

    public SetupTransactionJournal? Load(Guid changeSetId)
    {
        var source = paths.GetTransactionJournal(changeSetId);
        if (!platform.FileSystem.FileExists(source))
        {
            return null;
        }

        try
        {
            var journal = Deserialize(platform.FileSystem.ReadAllBytes(source));
            if (journal.ChangeSetId != changeSetId)
            {
                throw new FormatException();
            }

            return journal;
        }
        catch (SetupStorageException)
        {
            throw;
        }
        catch (Exception exception) when (SetupStorageException.ShouldMap(exception))
        {
            throw new SetupStorageException(SetupJournalStorageCodes.Corrupt);
        }
    }

    public void MarkTransactionPhase(SetupLock setupLock, Guid changeSetId, SetupJournalPhase nextPhase)
    {
        setupLock.AssertHeld(platform, paths);
        var journal = LoadRequired(changeSetId);
        if (!IsTransactionTransition(journal.Operation, journal.Phase, nextPhase))
        {
            throw new SetupStorageException(SetupJournalStorageCodes.TransitionInvalid);
        }

        var updated = journal with { Phase = nextPhase };
        if (!IsTerminalCoherent(updated))
        {
            throw new SetupStorageException(SetupJournalStorageCodes.TransitionInvalid);
        }

        Save(updated);
    }

    public void MarkStepPhase(
        SetupLock setupLock,
        Guid changeSetId,
        Guid recordId,
        string? memberKey,
        SetupJournalStepPhase expectedPhase,
        SetupJournalStepPhase nextPhase)
    {
        setupLock.AssertHeld(platform, paths);
        var journal = LoadRequired(changeSetId);
        var targetIndex = FindTarget(journal, recordId);
        var target = journal.Targets[targetIndex];
        var stepIndex = FindStep(target, memberKey);
        var step = target.Steps[stepIndex];
        if (step.Phase != expectedPhase)
        {
            throw new SetupStorageException(SetupJournalStorageCodes.StaleUpdate);
        }

        if (!IsStepTransition(journal.Operation, journal.Phase, expectedPhase, nextPhase))
        {
            throw new SetupStorageException(SetupJournalStorageCodes.TransitionInvalid);
        }

        var steps = target.Steps.ToArray();
        steps[stepIndex] = step with { Phase = nextPhase };
        var targets = journal.Targets.ToArray();
        targets[targetIndex] = target with { Steps = steps };
        Save(journal with { Targets = targets });
    }

    private void Save(SetupTransactionJournal journal)
    {
        ValidateForWrite(journal);
        WriteReplace(paths.GetTransactionJournal(journal.ChangeSetId), Serialize(journal));
    }

    private SetupTransactionJournal LoadRequired(Guid changeSetId) =>
        Load(changeSetId) ?? throw new SetupStorageException(SetupJournalStorageCodes.Corrupt);

    private static int FindTarget(SetupTransactionJournal journal, Guid recordId)
    {
        for (var index = 0; index < journal.Targets.Count; index++)
        {
            if (journal.Targets[index].RecordId == recordId)
            {
                return index;
            }
        }

        throw new SetupStorageException(SetupJournalStorageCodes.StaleUpdate);
    }

    private static int FindStep(SetupJournalTarget target, string? memberKey)
    {
        for (var index = 0; index < target.Steps.Count; index++)
        {
            if (string.Equals(target.Steps[index].MemberKey, memberKey, StringComparison.Ordinal))
            {
                return index;
            }
        }

        throw new SetupStorageException(SetupJournalStorageCodes.StaleUpdate);
    }

    private void WriteCreateNew(string destination, byte[] bytes)
    {
        var temporary = destination + ".tmp";
        try
        {
            platform.FileSystem.WriteNewAllBytes(temporary, bytes);
            platform.FileSystem.FlushFile(temporary);
            platform.FileSystem.MoveFile(temporary, destination, overwrite: false);
        }
        catch (Exception)
        {
            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }
    }

    private void WriteReplace(string destination, byte[] bytes)
    {
        var temporary = destination + ".tmp";
        try
        {
            platform.FileSystem.WriteNewAllBytes(temporary, bytes);
            platform.FileSystem.FlushFile(temporary);
            platform.FileSystem.ReplaceFile(temporary, destination);
        }
        catch (Exception)
        {
            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }
    }

    private static bool IsTransactionTransition(
        SetupJournalOperation operation,
        SetupJournalPhase current,
        SetupJournalPhase next) => (operation, current, next) switch
    {
        (SetupJournalOperation.Apply, SetupJournalPhase.Prepared, SetupJournalPhase.Applying) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalPhase.Compensating) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalPhase.Committed) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalPhase.Restored) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalPhase.Partial) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.Prepared, SetupJournalPhase.RollingBack) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Committed) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Partial) => true,
        _ => false,
    };

    private static bool IsStepTransition(
        SetupJournalOperation operation,
        SetupJournalPhase journalPhase,
        SetupJournalStepPhase current,
        SetupJournalStepPhase next) => (operation, journalPhase, current, next) switch
    {
        (SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.RestoreStarted) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalStepPhase.MutationCompleted, SetupJournalStepPhase.RestoreStarted) => true,
        (SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted) => true,
        _ => false,
    };

    private static bool IsTerminalCoherent(SetupTransactionJournal journal)
    {
        if (journal.Phase is not (SetupJournalPhase.Committed or SetupJournalPhase.Restored))
        {
            return true;
        }

        var phases = journal.Targets.SelectMany(target => target.Steps).Select(step => step.Phase).Distinct().ToArray();
        return phases.Length == 1 && (journal.Phase == SetupJournalPhase.Restored
            ? journal.Operation == SetupJournalOperation.Apply && phases[0] == SetupJournalStepPhase.RestoreCompleted
            : journal.Operation == SetupJournalOperation.Rollback
                ? phases[0] == SetupJournalStepPhase.RestoreCompleted
                : phases[0] == SetupJournalStepPhase.MutationCompleted);
    }

    private static byte[] Serialize(SetupTransactionJournal journal)
    {
        using var stream = new MemoryStream();
        using (var writer = SetupStorageJson.CreateWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", journal.SchemaVersion);
            writer.WriteString("change_set_id", journal.ChangeSetId.ToString("D"));
            writer.WriteString("operation", Operation(journal.Operation));
            writer.WriteString("created_at", SetupStorageJson.FormatTimestamp(journal.CreatedAt));
            writer.WriteString("phase", Phase(journal.Phase));
            writer.WritePropertyName("targets");
            writer.WriteStartArray();
            foreach (var target in journal.Targets)
            {
                writer.WriteStartObject();
                writer.WriteString("record_id", target.RecordId.ToString("D"));
                writer.WriteString("target_kind", SetupStorageJson.TargetKind(target.TargetKind));
                writer.WritePropertyName("steps");
                writer.WriteStartArray();
                foreach (var step in target.Steps)
                {
                    writer.WriteStartObject();
                    SetupStorageJson.WriteNullableString(writer, "member_key", step.MemberKey);
                    writer.WriteString("prior_state_hash", step.PriorStateHash);
                    writer.WriteString("desired_state_hash", step.DesiredStateHash);
                    writer.WriteString("backup_reference", step.BackupReference);
                    writer.WriteString("phase", StepPhase(step.Phase));
                    writer.WriteEndObject();
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static SetupTransactionJournal Deserialize(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException();
        }

        SetupStorageJson.RequireProperties(root, "schema_version", "change_set_id", "operation", "created_at", "phase", "targets");
        var versionElement = root.GetProperty("schema_version");
        if (versionElement.ValueKind != JsonValueKind.Number)
        {
            throw new FormatException();
        }

        var version = versionElement.GetInt32();
        if (version != 1)
        {
            throw new SetupStorageException(SetupJournalStorageCodes.VersionUnsupported);
        }

        var targets = new List<SetupJournalTarget>();
        foreach (var targetElement in SetupStorageJson.GetArray(root, "targets"))
        {
            SetupStorageJson.RequireProperties(targetElement, "record_id", "target_kind", "steps");
            var steps = new List<SetupJournalStep>();
            foreach (var stepElement in SetupStorageJson.GetArray(targetElement, "steps"))
            {
                SetupStorageJson.RequireProperties(stepElement, "member_key", "prior_state_hash", "desired_state_hash", "backup_reference", "phase");
                steps.Add(new SetupJournalStep(
                    SetupStorageJson.GetNullableString(stepElement, "member_key"),
                    SetupStorageJson.GetString(stepElement, "prior_state_hash"),
                    SetupStorageJson.GetString(stepElement, "desired_state_hash"),
                    SetupStorageJson.GetString(stepElement, "backup_reference"),
                    ParseStepPhase(SetupStorageJson.GetString(stepElement, "phase"))));
            }

            targets.Add(new SetupJournalTarget(
                SetupStorageJson.GetGuid(targetElement, "record_id"),
                SetupStorageJson.ParseTargetKind(SetupStorageJson.GetString(targetElement, "target_kind")),
                steps));
        }

        var journal = new SetupTransactionJournal(
            1,
            SetupStorageJson.GetGuid(root, "change_set_id"),
            ParseOperation(SetupStorageJson.GetString(root, "operation")),
            SetupStorageJson.ParseTimestamp(SetupStorageJson.GetString(root, "created_at")),
            ParsePhase(SetupStorageJson.GetString(root, "phase")),
            targets);
        Validate(journal);
        return journal;
    }

    private static void ValidateForWrite(SetupTransactionJournal journal)
    {
        try
        {
            Validate(journal);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new SetupStorageException(SetupJournalStorageCodes.Corrupt);
        }
    }

    private static void Validate(SetupTransactionJournal journal)
    {
        Require(journal.SchemaVersion == 1);
        Require(IsUuidV7(journal.ChangeSetId));
        Require(Enum.IsDefined(journal.Operation));
        Require(journal.CreatedAt.Offset == TimeSpan.Zero);
        Require(Enum.IsDefined(journal.Phase));
        var targets = journal.Targets ?? throw new FormatException();
        Require(targets.Count is >= 1 and <= 16);
        Require(targets.Select(target => target?.RecordId).Distinct().Count() == targets.Count);
        foreach (var targetValue in targets)
        {
            var target = targetValue ?? throw new FormatException();
            Require(IsUuidV7(target.RecordId));
            Require(Enum.IsDefined(target.TargetKind) && target.TargetKind != SetupTargetKind.Guidance);
            var steps = target.Steps ?? throw new FormatException();
            Require(steps.Count is >= 1 and <= 32);
            if (target.TargetKind == SetupTargetKind.Env)
            {
                Require(steps.All(step => step?.MemberKey is not null));
                Require(steps.Select(step => step?.MemberKey).Distinct(StringComparer.Ordinal).Count() == steps.Count);
            }
            else
            {
                Require(steps.Count == 1 && steps[0]?.MemberKey is null);
            }

            foreach (var stepValue in steps)
            {
                var step = stepValue ?? throw new FormatException();
                Require(step.MemberKey is null || MemberKeyPattern().IsMatch(step.MemberKey));
                Require(HashPattern().IsMatch(step.PriorStateHash ?? string.Empty));
                Require(HashPattern().IsMatch(step.DesiredStateHash ?? string.Empty));
                Require(BackupReferencePattern().IsMatch(step.BackupReference ?? string.Empty));
                Require(Enum.IsDefined(step.Phase));
                Require(IsStepCoherent(journal.Operation, journal.Phase, step.Phase));
            }
        }

        if (journal.Phase is SetupJournalPhase.Committed or SetupJournalPhase.Restored)
        {
            Require(IsTerminalCoherent(journal));
        }
    }

    private static bool IsStepCoherent(
        SetupJournalOperation operation,
        SetupJournalPhase journalPhase,
        SetupJournalStepPhase stepPhase) => journalPhase switch
    {
        SetupJournalPhase.Prepared => stepPhase == SetupJournalStepPhase.Pending,
        SetupJournalPhase.Applying => operation == SetupJournalOperation.Apply && stepPhase is
            SetupJournalStepPhase.Pending or SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.MutationCompleted,
        SetupJournalPhase.Compensating => operation == SetupJournalOperation.Apply,
        SetupJournalPhase.RollingBack => operation == SetupJournalOperation.Rollback && stepPhase is
            SetupJournalStepPhase.Pending or SetupJournalStepPhase.RestoreStarted or SetupJournalStepPhase.RestoreCompleted,
        SetupJournalPhase.Committed => true,
        SetupJournalPhase.Restored => operation == SetupJournalOperation.Apply && stepPhase == SetupJournalStepPhase.RestoreCompleted,
        SetupJournalPhase.Partial => operation == SetupJournalOperation.Apply
            ? stepPhase is SetupJournalStepPhase.Pending or SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.MutationCompleted or SetupJournalStepPhase.RestoreStarted or SetupJournalStepPhase.RestoreCompleted
            : stepPhase is SetupJournalStepPhase.Pending or SetupJournalStepPhase.RestoreStarted or SetupJournalStepPhase.RestoreCompleted,
        _ => false,
    };

    private static bool IsUuidV7(Guid value)
    {
        var text = value.ToString("D");
        return text[14] == '7' && text[19] is '8' or '9' or 'a' or 'b';
    }

    private static void Require(bool condition)
    {
        if (!condition)
        {
            throw new FormatException();
        }
    }

    private static string Operation(SetupJournalOperation operation) => operation switch
    {
        SetupJournalOperation.Apply => "apply",
        SetupJournalOperation.Rollback => "rollback",
        _ => throw new FormatException(),
    };

    private static SetupJournalOperation ParseOperation(string operation) => operation switch
    {
        "apply" => SetupJournalOperation.Apply,
        "rollback" => SetupJournalOperation.Rollback,
        _ => throw new FormatException(),
    };

    private static string Phase(SetupJournalPhase phase) => phase switch
    {
        SetupJournalPhase.Prepared => "prepared",
        SetupJournalPhase.Applying => "applying",
        SetupJournalPhase.Compensating => "compensating",
        SetupJournalPhase.RollingBack => "rolling_back",
        SetupJournalPhase.Committed => "committed",
        SetupJournalPhase.Restored => "restored",
        SetupJournalPhase.Partial => "partial",
        _ => throw new FormatException(),
    };

    private static SetupJournalPhase ParsePhase(string phase) => phase switch
    {
        "prepared" => SetupJournalPhase.Prepared,
        "applying" => SetupJournalPhase.Applying,
        "compensating" => SetupJournalPhase.Compensating,
        "rolling_back" => SetupJournalPhase.RollingBack,
        "committed" => SetupJournalPhase.Committed,
        "restored" => SetupJournalPhase.Restored,
        "partial" => SetupJournalPhase.Partial,
        _ => throw new FormatException(),
    };

    private static string StepPhase(SetupJournalStepPhase phase) => phase switch
    {
        SetupJournalStepPhase.Pending => "pending",
        SetupJournalStepPhase.MutationStarted => "mutation_started",
        SetupJournalStepPhase.MutationCompleted => "mutation_completed",
        SetupJournalStepPhase.RestoreStarted => "restore_started",
        SetupJournalStepPhase.RestoreCompleted => "restore_completed",
        _ => throw new FormatException(),
    };

    private static SetupJournalStepPhase ParseStepPhase(string phase) => phase switch
    {
        "pending" => SetupJournalStepPhase.Pending,
        "mutation_started" => SetupJournalStepPhase.MutationStarted,
        "mutation_completed" => SetupJournalStepPhase.MutationCompleted,
        "restore_started" => SetupJournalStepPhase.RestoreStarted,
        "restore_completed" => SetupJournalStepPhase.RestoreCompleted,
        _ => throw new FormatException(),
    };
}

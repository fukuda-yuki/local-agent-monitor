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

internal enum SetupEnvironmentNotification
{
    NotRequired,
    Pending,
    Completed,
}

internal enum SetupPreparedJournalOpenResult
{
    Created,
    Reused,
}

internal sealed record SetupTransactionJournal(
    int SchemaVersion,
    Guid ChangeSetId,
    SetupJournalOperation Operation,
    DateTimeOffset CreatedAt,
    SetupJournalPhase Phase,
    IReadOnlyList<SetupJournalTarget> Targets,
    SetupEnvironmentNotification EnvironmentNotification = SetupEnvironmentNotification.NotRequired);

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
    internal const int MaximumJournalBytes = 1024 * 1024;
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

    public SetupPreparedJournalOpenResult OpenOrCreatePrepared(
        SetupLock setupLock,
        Guid changeSetId,
        SetupJournalOperation operation,
        IReadOnlyList<SetupJournalTarget> targets)
    {
        setupLock.AssertHeld(platform, paths);
        var expected = new SetupTransactionJournal(
            1,
            changeSetId,
            operation,
            platform.Clock.UtcNow,
            SetupJournalPhase.Prepared,
            targets);
        ValidateForWrite(expected);

        var destination = paths.GetTransactionJournal(changeSetId);
        var metadata = platform.FileSystem.GetPathMetadata(destination);
        if (metadata.Exists)
        {
            ValidateReusablePrepared(destination, changeSetId, operation, targets, metadata);
            return SetupPreparedJournalOpenResult.Reused;
        }

        platform.FileSystem.CreateDirectory(paths.Transactions);
        bool created;
        try
        {
            created = platform.FileSystem.TryWriteNewAllBytesAndFlush(destination, Serialize(expected));
        }
        catch (Exception)
        {
            throw new SetupStorageException(SetupStorageCodes.WriteFailed);
        }

        if (created)
        {
            ValidateReusablePrepared(destination, changeSetId, operation, targets);
            return SetupPreparedJournalOpenResult.Created;
        }

        ValidateReusablePrepared(destination, changeSetId, operation, targets);
        return SetupPreparedJournalOpenResult.Reused;
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
            var read = platform.FileSystem.ReadAtMostBytes(source, MaximumJournalBytes);
            if (!read.IsComplete)
            {
                throw new FormatException();
            }

            var journal = Deserialize(read.Bytes);
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
        var notification = target.TargetKind == SetupTargetKind.Env &&
            nextPhase is SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.RestoreStarted &&
            journal.EnvironmentNotification == SetupEnvironmentNotification.NotRequired
                ? SetupEnvironmentNotification.Pending
                : journal.EnvironmentNotification;
        Save(journal with { Targets = targets, EnvironmentNotification = notification });
    }

    public void MarkEnvironmentNotificationCompleted(SetupLock setupLock, Guid changeSetId)
    {
        setupLock.AssertHeld(platform, paths);
        var journal = LoadRequired(changeSetId);
        if (journal.EnvironmentNotification != SetupEnvironmentNotification.Pending)
        {
            throw new SetupStorageException(SetupJournalStorageCodes.StaleUpdate);
        }

        if (journal.Phase is not (SetupJournalPhase.Committed or SetupJournalPhase.Restored))
        {
            throw new SetupStorageException(SetupJournalStorageCodes.TransitionInvalid);
        }

        Save(journal with { EnvironmentNotification = SetupEnvironmentNotification.Completed });
    }

    private void Save(SetupTransactionJournal journal)
    {
        ValidateForWrite(journal);
        WriteReplace(paths.GetTransactionJournal(journal.ChangeSetId), Serialize(journal));
    }

    private SetupTransactionJournal LoadRequired(Guid changeSetId) =>
        Load(changeSetId) ?? throw new SetupStorageException(SetupJournalStorageCodes.Corrupt);

    private void ValidateReusablePrepared(
        string source,
        Guid changeSetId,
        SetupJournalOperation operation,
        IReadOnlyList<SetupJournalTarget> targets,
        SetupPathMetadata? initialMetadata = null)
    {
        try
        {
            ValidateJournalMetadata(initialMetadata ?? platform.FileSystem.GetPathMetadata(source));
            var read = platform.FileSystem.ReadAtMostBytes(source, MaximumJournalBytes);
            if (!read.IsComplete)
            {
                throw new FormatException();
            }

            var existing = Deserialize(read.Bytes);
            ValidateJournalMetadata(platform.FileSystem.GetPathMetadata(source));
            if (existing.ChangeSetId != changeSetId ||
                existing.Operation != operation ||
                existing.Phase != SetupJournalPhase.Prepared ||
                existing.EnvironmentNotification != SetupEnvironmentNotification.NotRequired ||
                !TargetsExactlyEqual(existing.Targets, targets))
            {
                throw new SetupStorageException(SetupJournalStorageCodes.AlreadyExists);
            }
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

    private static bool TargetsExactlyEqual(
        IReadOnlyList<SetupJournalTarget> existing,
        IReadOnlyList<SetupJournalTarget> expected)
    {
        if (existing.Count != expected.Count)
        {
            return false;
        }

        for (var targetIndex = 0; targetIndex < existing.Count; targetIndex++)
        {
            var actualTarget = existing[targetIndex];
            var expectedTarget = expected[targetIndex];
            if (actualTarget.RecordId != expectedTarget.RecordId ||
                actualTarget.TargetKind != expectedTarget.TargetKind ||
                actualTarget.Steps.Count != expectedTarget.Steps.Count)
            {
                return false;
            }

            for (var stepIndex = 0; stepIndex < actualTarget.Steps.Count; stepIndex++)
            {
                var actualStep = actualTarget.Steps[stepIndex];
                var expectedStep = expectedTarget.Steps[stepIndex];
                if (actualStep.Phase != SetupJournalStepPhase.Pending ||
                    expectedStep.Phase != SetupJournalStepPhase.Pending ||
                    !string.Equals(actualStep.MemberKey, expectedStep.MemberKey, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.PriorStateHash, expectedStep.PriorStateHash, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.DesiredStateHash, expectedStep.DesiredStateHash, StringComparison.Ordinal) ||
                    !string.Equals(actualStep.BackupReference, expectedStep.BackupReference, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static void ValidateJournalMetadata(SetupPathMetadata metadata)
    {
        if (!metadata.Exists ||
            metadata.Kind != SetupPathKind.File ||
            (metadata.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new FormatException();
        }
    }

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
        var temporary = CreateTemporaryPath(destination);
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
        var temporary = CreateTemporaryPath(destination);
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

    private string CreateTemporaryPath(string destination) =>
        destination + ".cao-" + platform.Identifiers.CreateUuidV7().ToString("D") + ".tmp";

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
        (SetupJournalOperation.Apply, SetupJournalPhase.Partial, SetupJournalPhase.Compensating) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.Prepared, SetupJournalPhase.RollingBack) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Committed) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Partial) => true,
        (SetupJournalOperation.Rollback, SetupJournalPhase.Partial, SetupJournalPhase.RollingBack) => true,
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

        var phases = journal.Targets.SelectMany(target => target.Steps).Select(step => step.Phase).ToArray();
        return journal.Phase == SetupJournalPhase.Restored
            ? journal.Operation == SetupJournalOperation.Apply && phases.All(phase => phase is SetupJournalStepPhase.Pending or SetupJournalStepPhase.RestoreCompleted)
            : journal.Operation == SetupJournalOperation.Rollback
                ? phases.All(phase => phase == SetupJournalStepPhase.RestoreCompleted)
                : phases.All(phase => phase == SetupJournalStepPhase.MutationCompleted);
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
            writer.WriteString("environment_notification", EnvironmentNotification(journal.EnvironmentNotification));
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

        SetupStorageJson.RequireProperties(root, "schema_version", "change_set_id", "operation", "created_at", "phase", "environment_notification", "targets");
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
            targets,
            ParseEnvironmentNotification(SetupStorageJson.GetString(root, "environment_notification")));
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
        Require(Enum.IsDefined(journal.EnvironmentNotification));
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
                Require(steps.Select(step => step?.BackupReference).Distinct(StringComparer.Ordinal).Count() == 1);
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
                Require(step.BackupReference is { Length: <= 128 } && BackupReferencePattern().IsMatch(step.BackupReference));
                Require(Enum.IsDefined(step.Phase));
                Require(IsStepCoherent(journal.Operation, journal.Phase, step.Phase));
            }
        }

        var environmentSteps = targets
            .Where(target => target.TargetKind == SetupTargetKind.Env)
            .SelectMany(target => target.Steps)
            .ToArray();
        if (journal.EnvironmentNotification == SetupEnvironmentNotification.NotRequired)
        {
            Require(environmentSteps.All(step => step.Phase == SetupJournalStepPhase.Pending));
        }
        else
        {
            Require(environmentSteps.Any(step => step.Phase != SetupJournalStepPhase.Pending));
        }
        if (journal.EnvironmentNotification == SetupEnvironmentNotification.Completed)
        {
            Require(journal.Phase is SetupJournalPhase.Committed or SetupJournalPhase.Restored);
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
        SetupJournalPhase.Restored => operation == SetupJournalOperation.Apply && stepPhase is SetupJournalStepPhase.Pending or SetupJournalStepPhase.RestoreCompleted,
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

    private static string EnvironmentNotification(SetupEnvironmentNotification notification) => notification switch
    {
        SetupEnvironmentNotification.NotRequired => "not_required",
        SetupEnvironmentNotification.Pending => "pending",
        SetupEnvironmentNotification.Completed => "completed",
        _ => throw new FormatException(),
    };

    private static SetupEnvironmentNotification ParseEnvironmentNotification(string notification) => notification switch
    {
        "not_required" => SetupEnvironmentNotification.NotRequired,
        "pending" => SetupEnvironmentNotification.Pending,
        "completed" => SetupEnvironmentNotification.Completed,
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

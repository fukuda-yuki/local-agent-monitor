using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupJournalStoreTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 12, 1, 2, 3, TimeSpan.Zero);
    private static readonly Guid ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000101");
    private static readonly Guid FileRecordId = Guid.Parse("00000000-0000-7000-8000-000000000102");
    private static readonly Guid EnvironmentRecordId = Guid.Parse("00000000-0000-7000-8000-000000000103");
    private const string PriorHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string DesiredHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const int MaximumJournalBytes = 1024 * 1024;

    [Theory]
    [InlineData("apply")]
    [InlineData("rollback")]
    public void CreatePrepared_WritesCompleteV1JournalAndReopens(string operationValue)
    {
        var operation = operationValue == "apply" ? SetupJournalOperation.Apply : SetupJournalOperation.Rollback;
        var context = CreateContext();

        var created = context.Store.CreatePrepared(context.Lock, ChangeSetId, operation, ValidTargets());
        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId);

        Assert.Equal(1, created.SchemaVersion);
        Assert.Equal(CreatedAt, created.CreatedAt);
        Assert.Equal(SetupJournalPhase.Prepared, created.Phase);
        Assert.Equal(operation, reopened!.Operation);
        Assert.Equal(2, reopened.Targets.Count);
        Assert.Single(reopened.Targets[0].Steps);
        Assert.Equal(new[] { "COPILOT_A", "COPILOT_B" }, reopened.Targets[1].Steps.Select(step => step.MemberKey));
        Assert.All(reopened.Targets.SelectMany(target => target.Steps), step => Assert.Equal(SetupJournalStepPhase.Pending, step.Phase));
        Assert.DoesNotContain("previous-value", Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId))), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_MissingReturnsNull()
    {
        var context = CreateContext();

        Assert.Null(context.Store.Load(ChangeSetId));
    }

    [Theory]
    [MemberData(nameof(ValidTransactionTransitions))]
    public void MarkTransactionPhase_AllowsOnlyLifecycleTransitions(
        object operationValue,
        object fromValue,
        object toValue,
        object completedPhaseValue)
    {
        var operation = (SetupJournalOperation)operationValue;
        var from = (SetupJournalPhase)fromValue;
        var to = (SetupJournalPhase)toValue;
        var completedPhase = (SetupJournalStepPhase)completedPhaseValue;
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, operation, ValidTargets());
        AdvanceTransaction(context, from, completedPhase);

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, to);

        Assert.Equal(to, new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!.Phase);
    }

    public static TheoryData<object, object, object, object> ValidTransactionTransitions() => new()
    {
        { SetupJournalOperation.Apply, SetupJournalPhase.Prepared, SetupJournalPhase.Applying, SetupJournalStepPhase.Pending },
        { SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalPhase.Compensating, SetupJournalStepPhase.Pending },
        { SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalPhase.Committed, SetupJournalStepPhase.MutationCompleted },
        { SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalPhase.Restored, SetupJournalStepPhase.RestoreCompleted },
        { SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalPhase.Partial, SetupJournalStepPhase.Pending },
        { SetupJournalOperation.Rollback, SetupJournalPhase.Prepared, SetupJournalPhase.RollingBack, SetupJournalStepPhase.Pending },
        { SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Committed, SetupJournalStepPhase.RestoreCompleted },
        { SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Partial, SetupJournalStepPhase.Pending },
    };

    [Theory]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.Prepared, SetupJournalPhase.Committed)]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.Prepared, SetupJournalPhase.RollingBack)]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalPhase.RollingBack)]
    [InlineData(SetupJournalOperation.Rollback, SetupJournalPhase.Prepared, SetupJournalPhase.Applying)]
    [InlineData(SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Compensating)]
    [InlineData(SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalPhase.Prepared)]
    public void MarkTransactionPhase_RejectsInvalidTransitionWithoutWrite(
        object operationValue,
        object fromValue,
        object toValue)
    {
        var operation = (SetupJournalOperation)operationValue;
        var from = (SetupJournalPhase)fromValue;
        var to = (SetupJournalPhase)toValue;
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, operation, ValidTargets());
        if (from != SetupJournalPhase.Prepared)
        {
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, from);
        }
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, to));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    [Fact]
    public void MarkTransactionPhase_RejectsPrematureCommitWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkTransactionPhase(
            context.Lock, ChangeSetId, SetupJournalPhase.Committed));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    [Theory]
    [MemberData(nameof(ValidStepTransitions))]
    public void MarkStepPhase_AllowsExactApplyAndRollbackStepTransitions(
        object operationValue,
        object journalPhaseValue,
        object fromValue,
        object toValue)
    {
        var operation = (SetupJournalOperation)operationValue;
        var journalPhase = (SetupJournalPhase)journalPhaseValue;
        var from = (SetupJournalStepPhase)fromValue;
        var to = (SetupJournalStepPhase)toValue;
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, operation, ValidTargets());
        PrepareStepTransition(context, operation, journalPhase, from);

        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, from, to);

        Assert.Equal(to, new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!.Targets[0].Steps[0].Phase);
    }

    public static TheoryData<object, object, object, object> ValidStepTransitions() => new()
    {
        { SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted },
        { SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted },
        { SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.RestoreStarted },
        { SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalStepPhase.MutationCompleted, SetupJournalStepPhase.RestoreStarted },
        { SetupJournalOperation.Apply, SetupJournalPhase.Compensating, SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted },
        { SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted },
        { SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted },
    };

    [Fact]
    public void MarkStepPhase_StaleExpectedPhaseRejectsWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted));

        Assert.Equal(SetupJournalStorageCodes.StaleUpdate, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    [Theory]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted)]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.Applying, SetupJournalStepPhase.MutationCompleted, SetupJournalStepPhase.Pending)]
    [InlineData(SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted)]
    public void MarkStepPhase_RejectsInvalidTransitionWithoutWrite(
        object operationValue,
        object journalPhaseValue,
        object fromValue,
        object toValue)
    {
        var operation = (SetupJournalOperation)operationValue;
        var journalPhase = (SetupJournalPhase)journalPhaseValue;
        var from = (SetupJournalStepPhase)fromValue;
        var to = (SetupJournalStepPhase)toValue;
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, operation, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, journalPhase);
        if (from != SetupJournalStepPhase.Pending)
        {
            AdvanceStep(context, from);
        }
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, from, to));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    [Theory]
    [MemberData(nameof(InvalidTargets))]
    public void CreatePrepared_RejectsInvalidSchemaShapes(object targetsValue)
    {
        var targets = (IReadOnlyList<SetupJournalTarget>)targetsValue;
        var context = CreateContext();

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.CreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, targets));

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.False(context.Platform.FileSystem.FileExists(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    public static TheoryData<object> InvalidTargets()
    {
        var valid = ValidTargets();
        return new TheoryData<object>
        {
            Array.Empty<SetupJournalTarget>(),
            Enumerable.Repeat(valid[0], 17).ToArray(),
            new[] { valid[0], valid[0] },
            new[] { valid[0] with { Steps = Array.Empty<SetupJournalStep>() }, valid[1] },
            new[] { valid[0] with { Steps = new[] { valid[0].Steps[0], valid[0].Steps[0] } }, valid[1] },
            new[] { valid[0], valid[1] with { Steps = Enumerable.Repeat(valid[1].Steps[0], 33).ToArray() } },
            new[] { valid[0], valid[1] with { Steps = new[] { valid[1].Steps[0], valid[1].Steps[0] } } },
            new[] { valid[0] with { Steps = new[] { valid[0].Steps[0] with { MemberKey = "FILE_MEMBER" } } }, valid[1] },
            new[] { valid[0], valid[1] with { Steps = new[] { valid[1].Steps[0] with { MemberKey = null } } } },
            new[] { valid[0] with { Steps = new[] { valid[0].Steps[0] with { PriorStateHash = "previous-value" } } }, valid[1] },
            new[] { valid[0] with { Steps = new[] { valid[0].Steps[0] with { BackupReference = "C:\\private\\backup" } } }, valid[1] },
            new[] { valid[0] with { Steps = new[] { valid[0].Steps[0] with { BackupReference = "b" + new string('a', 128) } } }, valid[1] },
            new[] { valid[0], valid[1] with { Steps = new[] { valid[1].Steps[0], valid[1].Steps[1] with { BackupReference = "backup-env-other" } } } },
        };
    }

    [Fact]
    public void Load_OversizeJournalUsesBoundedReadAndReturnsFixedNonEchoError()
    {
        var context = CreateContext();
        var source = context.Paths.GetTransactionJournal(ChangeSetId);
        context.Platform.SeedFile(source, Enumerable.Repeat((byte)'P', MaximumJournalBytes + 1).ToArray());

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load(ChangeSetId));

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", exception.ToString(), StringComparison.Ordinal);
        Assert.Contains($"file.read-bounded:{source}:{MaximumJournalBytes}", context.Platform.Operations);
        Assert.DoesNotContain($"file.read:{source}", context.Platform.Operations);
    }

    [Theory]
    [MemberData(nameof(InvalidDocuments))]
    public void Load_RejectsUnknownDuplicateNullWrongTypeAndCorruptDocumentsWithFixedNonEchoError(string json, string expectedCode)
    {
        var context = CreateContext();
        context.Platform.SeedFile(context.Paths.GetTransactionJournal(ChangeSetId), Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load(ChangeSetId));

        Assert.Equal(expectedCode, exception.Code);
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> InvalidDocuments()
    {
        var valid = ValidJson();
        return new TheoryData<string, string>
        {
            { "{", SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"schema_version\": 1", "\"schema_version\": 2", StringComparison.Ordinal), SetupJournalStorageCodes.VersionUnsupported },
            { valid.Replace("\"schema_version\": 1", "\"schema_version\": null", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"operation\": \"apply\"", "\"operation\": 1", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"phase\": \"prepared\"", "\"phase\": \"prepared\", \"PRIVATE_PATH_MARKER\": true", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"phase\": \"prepared\"", "\"phase\": \"prepared\", \"phase\": \"prepared\"", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"schema_version\": 2", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
        };
    }

    [Fact]
    public void Load_ToleratesPropertyReorderingButPreservesExactTargetAndStepOrder()
    {
        var context = CreateContext();
        using var document = JsonDocument.Parse(ValidJson());
        var root = document.RootElement;
        var reordered = $$"""
            {"targets":{{root.GetProperty("targets").GetRawText()}},"phase":"prepared","created_at":"2026-07-12T01:02:03.0000000Z","operation":"apply","change_set_id":"{{ChangeSetId:D}}","schema_version":1}
            """;
        context.Platform.SeedFile(context.Paths.GetTransactionJournal(ChangeSetId), Encoding.UTF8.GetBytes(reordered));

        var loaded = context.Store.Load(ChangeSetId)!;

        Assert.Equal(new[] { FileRecordId, EnvironmentRecordId }, loaded.Targets.Select(target => target.RecordId));
        Assert.Equal(new[] { "COPILOT_A", "COPILOT_B" }, loaded.Targets[1].Steps.Select(step => step.MemberKey));
    }

    [Fact]
    public void CreateAndUpdates_RequireLiveSameRuntimeLock()
    {
        var context = CreateContext();
        var foreignPlatform = new SetupTestPlatform(CreatedAt, "D:\\foreign");
        var foreignPaths = new SetupRuntimePaths(foreignPlatform);
        using var foreignAcquire = SetupLock.TryAcquire(foreignPlatform, foreignPaths);

        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.CreatePrepared(foreignAcquire.Lock!, ChangeSetId, SetupJournalOperation.Apply, ValidTargets())).Code);

        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Lock.Dispose();

        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying)).Code);
    }

    [Fact]
    public void CreatePrepared_IsCreateNewAndNeverOverwritesExistingJournal()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.CreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Rollback, ValidTargets()));

        Assert.Equal(SetupJournalStorageCodes.AlreadyExists, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    [Fact]
    public void SystemFileSystem_CreateUpdateCloseAndReopenPreservesDurableJournal()
    {
        var localApplicationData = Path.Combine(Path.GetTempPath(), $"setup-journal-{Guid.NewGuid():N}");
        try
        {
            var firstPlatform = new SystemSetupPlatform(localApplicationData: localApplicationData);
            var firstPaths = new SetupRuntimePaths(firstPlatform);
            using (var acquired = SetupLock.TryAcquire(firstPlatform, firstPaths))
            {
                var store = new SetupTransactionJournalStore(firstPlatform, firstPaths);
                store.CreatePrepared(acquired.Lock!, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
                store.MarkTransactionPhase(acquired.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            }

            var reopenedPlatform = new SystemSetupPlatform(localApplicationData: localApplicationData);
            var reopenedPaths = new SetupRuntimePaths(reopenedPlatform);
            using var reopenedLock = SetupLock.TryAcquire(reopenedPlatform, reopenedPaths);

            Assert.Equal(
                SetupJournalPhase.Applying,
                new SetupTransactionJournalStore(reopenedPlatform, reopenedPaths).Load(ChangeSetId)!.Phase);
        }
        finally
        {
            if (Directory.Exists(localApplicationData))
            {
                Directory.Delete(localApplicationData, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("move", false)]
    [InlineData("move", true)]
    public void CreatePrepared_FaultBoundaryReopensAsMissingOrCompleteAndNeverDeletesTemporary(string boundary, bool afterEffect)
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var temporary = Temporary(destination, 1);
        var operation = boundary switch
        {
            "write" => $"file.write-new:{temporary}",
            "flush" => $"file.flush:{temporary}",
            _ => $"file.move:{temporary}->{destination}",
        };
        InjectFault(context.Platform, operation, afterEffect);

        Assert.Throws<SetupStorageException>(() => context.Store.CreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId);
        Assert.Equal(boundary == "move" && afterEffect, reopened is not null);
        if (afterEffect && boundary != "move")
        {
            Assert.True(context.Platform.FileSystem.FileExists(temporary));
        }
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public void Update_FaultBoundaryPreservesOldOrCompleteNewJournalAndNeverDeletesTemporary(string boundary, bool afterEffect)
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var temporary = Temporary(destination, 2);
        var operation = boundary switch
        {
            "write" => $"file.write-new:{temporary}",
            "flush" => $"file.flush:{temporary}",
            _ => $"file.replace:{temporary}->{destination}",
        };
        InjectFault(context.Platform, operation, afterEffect);

        Assert.Throws<SetupStorageException>(() => context.Store.MarkTransactionPhase(
            context.Lock, ChangeSetId, SetupJournalPhase.Applying));

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(boundary == "replace" && afterEffect ? SetupJournalPhase.Applying : SetupJournalPhase.Prepared, reopened.Phase);
        if (afterEffect && boundary != "replace")
        {
            Assert.True(context.Platform.FileSystem.FileExists(temporary));
        }
    }

    [Fact]
    public void CreatePrepared_PreMoveFaultCanReopenAndRetryWithoutReusingOrDeletingReboundOrphan()
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var orphan = Temporary(destination, 1);
        context.Platform.InjectFault($"file.move:{orphan}->{destination}", new IOException("PRIVATE_PATH_MARKER"));

        Assert.Throws<SetupStorageException>(() => context.Store.CreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));
        var reboundBytes = Encoding.UTF8.GetBytes("rebound-orphan");
        context.Platform.SeedFile(orphan, reboundBytes);

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths);
        reopened.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());

        Assert.Equal(reboundBytes, context.Platform.ReadSeededFile(orphan));
        Assert.NotNull(new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId));
        Assert.Contains($"file.move:{Temporary(destination, 2)}->{destination}", context.Platform.Operations);
        Assert.DoesNotContain($"file.delete:{orphan}", context.Platform.Operations);
    }

    [Fact]
    public void Update_PreReplaceFaultCanReopenAndRetryWithoutReusingOrDeletingReboundOrphan()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var orphan = Temporary(destination, 2);
        context.Platform.InjectFault($"file.replace:{orphan}->{destination}", new IOException("PRIVATE_PATH_MARKER"));

        Assert.Throws<SetupStorageException>(() => context.Store.MarkTransactionPhase(
            context.Lock, ChangeSetId, SetupJournalPhase.Applying));
        var reboundBytes = Encoding.UTF8.GetBytes("rebound-orphan");
        context.Platform.SeedFile(orphan, reboundBytes);

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths);
        reopened.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);

        Assert.Equal(reboundBytes, context.Platform.ReadSeededFile(orphan));
        Assert.Equal(SetupJournalPhase.Applying, new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!.Phase);
        Assert.Contains($"file.replace:{Temporary(destination, 3)}->{destination}", context.Platform.Operations);
        Assert.DoesNotContain($"file.delete:{orphan}", context.Platform.Operations);
    }

    private static void AdvanceTransaction(TestContext context, SetupJournalPhase phase, SetupJournalStepPhase completedPhase)
    {
        if (phase == SetupJournalPhase.Prepared)
        {
            return;
        }

        var first = context.Store.Load(ChangeSetId)!;
        if (first.Operation == SetupJournalOperation.Apply)
        {
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
            if (completedPhase == SetupJournalStepPhase.MutationCompleted)
            {
                CompleteAllSteps(context, mutation: true);
            }
            if (phase == SetupJournalPhase.Compensating)
            {
                if (completedPhase == SetupJournalStepPhase.RestoreCompleted)
                {
                    CompleteAllSteps(context, mutation: true);
                }
                context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
                if (completedPhase == SetupJournalStepPhase.RestoreCompleted)
                {
                    CompleteAllSteps(context, mutation: false);
                }
            }
        }
        else
        {
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
            if (completedPhase == SetupJournalStepPhase.RestoreCompleted)
            {
                CompleteAllSteps(context, mutation: false);
            }
        }
    }

    private static void CompleteAllSteps(TestContext context, bool mutation)
    {
        foreach (var target in context.Store.Load(ChangeSetId)!.Targets)
        {
            foreach (var step in target.Steps)
            {
                if (mutation)
                {
                    context.Store.MarkStepPhase(context.Lock, ChangeSetId, target.RecordId, step.MemberKey, SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
                    context.Store.MarkStepPhase(context.Lock, ChangeSetId, target.RecordId, step.MemberKey, SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
                }
                else
                {
                    var current = context.Store.Load(ChangeSetId)!.Targets.Single(item => item.RecordId == target.RecordId).Steps.Single(item => item.MemberKey == step.MemberKey).Phase;
                    context.Store.MarkStepPhase(context.Lock, ChangeSetId, target.RecordId, step.MemberKey, current, SetupJournalStepPhase.RestoreStarted);
                    context.Store.MarkStepPhase(context.Lock, ChangeSetId, target.RecordId, step.MemberKey, SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
                }
            }
        }
    }

    private static void AdvanceStep(TestContext context, SetupJournalStepPhase phase)
    {
        if (phase == SetupJournalStepPhase.Pending)
        {
            return;
        }

        if (phase is SetupJournalStepPhase.MutationStarted or SetupJournalStepPhase.MutationCompleted)
        {
            context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
            if (phase == SetupJournalStepPhase.MutationCompleted)
            {
                context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
            }
        }
        else if (phase == SetupJournalStepPhase.RestoreStarted)
        {
            context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
        }
    }

    private static void PrepareStepTransition(
        TestContext context,
        SetupJournalOperation operation,
        SetupJournalPhase journalPhase,
        SetupJournalStepPhase from)
    {
        if (operation == SetupJournalOperation.Rollback)
        {
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
            AdvanceStep(context, from);
            return;
        }

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        if (journalPhase == SetupJournalPhase.Applying)
        {
            AdvanceStep(context, from);
            return;
        }

        var mutationPhase = from == SetupJournalStepPhase.MutationCompleted
            ? SetupJournalStepPhase.MutationCompleted
            : SetupJournalStepPhase.MutationStarted;
        AdvanceStep(context, mutationPhase);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        if (from == SetupJournalStepPhase.RestoreStarted)
        {
            context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, mutationPhase, SetupJournalStepPhase.RestoreStarted);
        }
    }

    private static IReadOnlyList<SetupJournalTarget> ValidTargets() =>
    [
        new(FileRecordId, SetupTargetKind.Json,
        [
            new(null, PriorHash, DesiredHash, "backup-file-102", SetupJournalStepPhase.Pending),
        ]),
        new(EnvironmentRecordId, SetupTargetKind.Env,
        [
            new("COPILOT_A", PriorHash, DesiredHash, "backup-env-103", SetupJournalStepPhase.Pending),
            new("COPILOT_B", DesiredHash, PriorHash, "backup-env-103", SetupJournalStepPhase.Pending),
        ]),
    ];

    private static string ValidJson()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        return Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    private static TestContext CreateContext()
    {
        var platform = new SetupTestPlatform(CreatedAt);
        var paths = new SetupRuntimePaths(platform);
        var acquired = SetupLock.TryAcquire(platform, paths);
        return new TestContext(platform, paths, acquired.Lock!, new SetupTransactionJournalStore(platform, paths));
    }

    private static void InjectFault(SetupTestPlatform platform, string operation, bool afterEffect)
    {
        if (afterEffect)
        {
            platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_PATH_MARKER"));
        }
        else
        {
            platform.InjectFault(operation, new IOException("PRIVATE_PATH_MARKER"));
        }
    }

    private static string Temporary(string destination, int sequence) =>
        destination + $".cao-00000000-0000-7000-8000-{sequence:D12}.tmp";

    private sealed record TestContext(
        SetupTestPlatform Platform,
        SetupRuntimePaths Paths,
        SetupLock Lock,
        SetupTransactionJournalStore Store);
}

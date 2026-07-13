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

    [Fact]
    public void SupersedeWithPreparedRollback_TerminalApplyBecomesPreparedRollbackAtSamePath()
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var earlierCreatedAt = CreatedAt.AddDays(-1);
        var terminalBytes = Encoding.UTF8.GetString(context.Platform.ReadSeededFile(destination))
            .Replace(
                SetupStorageJson.FormatTimestamp(CreatedAt),
                SetupStorageJson.FormatTimestamp(earlierCreatedAt),
                StringComparison.Ordinal);
        context.Platform.SeedFile(destination, Encoding.UTF8.GetBytes(terminalBytes));
        var apply = context.Store.Load(ChangeSetId)!;
        Assert.Equal(earlierCreatedAt, apply.CreatedAt);

        var result = context.Store.SupersedeWithPreparedRollback(
            context.Lock, ChangeSetId, ValidTargets());

        var rollback = context.Store.Load(ChangeSetId)!;
        Assert.Equal(SetupPreparedJournalOpenResult.Created, result);
        Assert.Equal(destination, context.Paths.GetTransactionJournal(rollback.ChangeSetId));
        Assert.Equal(SetupJournalOperation.Rollback, rollback.Operation);
        Assert.Equal(SetupJournalPhase.Prepared, rollback.Phase);
        Assert.Equal(apply.CreatedAt, rollback.CreatedAt);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, rollback.EnvironmentNotification);
        Assert.Equivalent(ValidTargets(), rollback.Targets, strict: true);
        Assert.All(rollback.Targets.SelectMany(target => target.Steps),
            step => Assert.Equal(SetupJournalStepPhase.Pending, step.Phase));
        Assert.Contains(context.Platform.Operations,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith($"->{destination}", StringComparison.Ordinal));
    }

    [Fact]
    public void SupersedeWithPreparedRollback_ExactPreparedRollbackIsReusedWithoutWrite()
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets());
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var exact = context.Platform.ReadSeededFile(destination);
        var operationCount = context.Platform.Operations.Count;

        var result = new SetupTransactionJournalStore(context.Platform, context.Paths)
            .SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets());

        Assert.Equal(SetupPreparedJournalOpenResult.Reused, result);
        Assert.Equal(exact, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void SupersedeWithPreparedRollback_TerminalFileOnlyApplyDoesNotRequireNotification()
    {
        var context = CreateContext();
        var targets = FileOnlyTargets();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, targets);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        CompleteAllSteps(context, mutation: true);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Committed);

        var result = context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, targets);

        Assert.Equal(SetupPreparedJournalOpenResult.Created, result);
        var rollback = context.Store.Load(ChangeSetId)!;
        Assert.Equal(SetupJournalOperation.Rollback, rollback.Operation);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, rollback.EnvironmentNotification);
    }

    [Theory]
    [InlineData("prepared-apply")]
    [InlineData("applying-apply")]
    [InlineData("compensating-apply")]
    [InlineData("restored-apply")]
    [InlineData("partial-apply")]
    [InlineData("committed-apply-notification-pending")]
    [InlineData("rolling-back")]
    [InlineData("committed-rollback")]
    public void SupersedeWithPreparedRollback_NonTerminalApplyStateFailsClosedWithoutWrite(string state)
    {
        var context = CreateSupersessionState(state);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var exact = context.Platform.ReadSeededFile(destination);
        var operationCount = context.Platform.Operations.Count;
        var expected = PendingTargets(context.Store.Load(ChangeSetId)!.Targets);

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, expected));

        Assert.Equal(SetupJournalStorageCodes.AlreadyExists, exception.Code);
        Assert.Equal(exact, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData("target-count")]
    [InlineData("target-order")]
    [InlineData("target-kind")]
    [InlineData("record-id")]
    [InlineData("step-order")]
    [InlineData("member-key")]
    [InlineData("prior-hash")]
    [InlineData("desired-hash")]
    [InlineData("backup-reference")]
    public void SupersedeWithPreparedRollback_ExpectedIdentityMismatchFailsClosedWithoutWrite(string mismatch)
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var exact = context.Platform.ReadSeededFile(destination);
        var expected = ValidTargets().ToArray();
        switch (mismatch)
        {
            case "target-count": expected = [expected[0]]; break;
            case "target-order": expected = expected.Reverse().ToArray(); break;
            case "target-kind": expected[0] = expected[0] with { TargetKind = SetupTargetKind.Toml }; break;
            case "record-id": expected[0] = expected[0] with { RecordId = Guid.Parse("00000000-0000-7000-8000-000000000104") }; break;
            case "step-order": expected[1] = expected[1] with { Steps = expected[1].Steps.Reverse().ToArray() }; break;
            case "member-key": expected[1] = expected[1] with { Steps = ReplaceStep(expected[1], 0, step => step with { MemberKey = "COPILOT_C" }) }; break;
            case "prior-hash": expected[0] = expected[0] with { Steps = ReplaceStep(expected[0], 0, step => step with { PriorStateHash = DesiredHash }) }; break;
            case "desired-hash": expected[0] = expected[0] with { Steps = ReplaceStep(expected[0], 0, step => step with { DesiredStateHash = PriorHash }) }; break;
            case "backup-reference": expected[0] = expected[0] with { Steps = ReplaceStep(expected[0], 0, step => step with { BackupReference = "backup-file-other" }) }; break;
        }
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, expected));

        Assert.Equal(SetupJournalStorageCodes.AlreadyExists, exception.Code);
        Assert.Equal(exact, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData(SetupPathKind.File, FileAttributes.ReparsePoint)]
    [InlineData(SetupPathKind.Directory, FileAttributes.Directory)]
    public void SupersedeWithPreparedRollback_NonRegularOrReparseJournalFailsClosedWithoutWrite(
        SetupPathKind kind,
        FileAttributes attributes)
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        context.Platform.SeedPathMetadata(destination, new SetupPathMetadata(true, kind, attributes));
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets()));

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("unknown-version")]
    [InlineData("malformed")]
    public void SupersedeWithPreparedRollback_InvalidV1ArtifactFailsFixedWithoutWrite(string invalid)
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var valid = context.Platform.ReadSeededFile(destination);
        var bytes = invalid switch
        {
            "oversize" => Enumerable.Repeat((byte)'X', MaximumJournalBytes + 1).ToArray(),
            "unknown-version" => Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(valid).Replace(
                "\"schema_version\": 1", "\"schema_version\": 2", StringComparison.Ordinal)),
            _ => Encoding.UTF8.GetBytes("{\"PRIVATE_VALUE_MARKER\":"),
        };
        context.Platform.SeedFile(destination, bytes);
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets()));

        Assert.Equal(
            invalid == "unknown-version"
                ? SetupJournalStorageCodes.VersionUnsupported
                : SetupJournalStorageCodes.Corrupt,
            exception.Code);
        Assert.Equal(bytes, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
        Assert.DoesNotContain("PRIVATE_VALUE_MARKER", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(destination, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public void SupersedeWithPreparedRollback_FaultBoundaryReopensAsExactOldOrNewAndRetryPreservesReboundTemporary(
        string boundary,
        bool afterEffect)
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var oldBytes = context.Platform.ReadSeededFile(destination);
        var temporary = Temporary(destination, 11);
        var operation = boundary switch
        {
            "write" => $"file.write-new:{temporary}",
            "flush" => $"file.flush:{temporary}",
            _ => $"file.replace:{temporary}->{destination}",
        };
        InjectFault(context.Platform, operation, afterEffect);

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets()));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(destination, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths);
        var durable = reopened.Load(ChangeSetId)!;
        var replacementTookEffect = boundary == "replace" && afterEffect;
        Assert.Equal(replacementTookEffect ? SetupJournalOperation.Rollback : SetupJournalOperation.Apply, durable.Operation);
        Assert.Equal(replacementTookEffect ? SetupJournalPhase.Prepared : SetupJournalPhase.Committed, durable.Phase);
        Assert.Equal(CreatedAt, durable.CreatedAt);
        Assert.Equal(ChangeSetId, durable.ChangeSetId);
        Assert.Equivalent(ValidTargets(), PendingTargets(durable.Targets), strict: true);
        if (!replacementTookEffect)
        {
            Assert.Equal(oldBytes, context.Platform.ReadSeededFile(destination));
        }

        var durableBytes = context.Platform.ReadSeededFile(destination);
        var reboundBytes = Encoding.UTF8.GetBytes("foreign-rebound-temporary");
        context.Platform.SeedFile(temporary, reboundBytes);
        var operationsBeforeRetry = context.Platform.Operations.Count;

        var result = reopened.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets());

        Assert.Equal(
            replacementTookEffect ? SetupPreparedJournalOpenResult.Reused : SetupPreparedJournalOpenResult.Created,
            result);
        var rollbackBytes = context.Platform.ReadSeededFile(destination);
        if (replacementTookEffect)
        {
            Assert.Equal(durableBytes, rollbackBytes);
            Assert.DoesNotContain(context.Platform.Operations.Skip(operationsBeforeRetry), IsWriteOperation);
        }
        else
        {
            Assert.Contains($"file.replace:{Temporary(destination, 12)}->{destination}", context.Platform.Operations);
        }

        var rollback = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(SetupJournalOperation.Rollback, rollback.Operation);
        Assert.Equal(SetupJournalPhase.Prepared, rollback.Phase);
        Assert.Equal(CreatedAt, rollback.CreatedAt);
        Assert.Equal(ChangeSetId, rollback.ChangeSetId);
        Assert.Equivalent(ValidTargets(), rollback.Targets, strict: true);
        Assert.Equal(reboundBytes, context.Platform.ReadSeededFile(temporary));
        Assert.DoesNotContain($"file.delete:{temporary}", context.Platform.Operations);
    }

    [Theory]
    [InlineData("regular-content")]
    [InlineData("reparse")]
    public async Task SupersedeWithPreparedRollback_DestinationReboundBeforeReplaceFailsClosedWithoutOverwrite(
        string reboundKind)
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var oldBytes = context.Platform.ReadSeededFile(destination);
        var temporary = Temporary(destination, 11);
        using var barrier = context.Platform.AddBarrier($"file.flush:{temporary}");
        var operationsBefore = context.Platform.Operations.Count;

        var supersede = Task.Run(() => Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets())));
        barrier.WaitUntilReached(CancellationToken.None);
        var foreignBytes = Encoding.UTF8.GetBytes("foreign-destination-content");
        if (reboundKind == "reparse")
        {
            context.Platform.SeedPathMetadata(
                destination,
                new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }
        else
        {
            context.Platform.SeedFile(destination, foreignBytes);
        }

        barrier.Release();
        var exception = await supersede;

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.DoesNotContain(destination, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            $"file.replace:{temporary}->{destination}",
            context.Platform.Operations.Skip(operationsBefore));
        Assert.Equal(
            reboundKind == "reparse" ? oldBytes : foreignBytes,
            context.Platform.ReadSeededFile(destination));
        Assert.True(context.Platform.FileSystem.FileExists(temporary));

        var reboundTemporaryBytes = Encoding.UTF8.GetBytes("foreign-rebound-temporary");
        context.Platform.SeedFile(temporary, reboundTemporaryBytes);
        context.Platform.SeedFile(destination, oldBytes);
        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths);

        Assert.Equal(
            SetupPreparedJournalOpenResult.Created,
            reopened.SupersedeWithPreparedRollback(context.Lock, ChangeSetId, ValidTargets()));
        Assert.Equal(reboundTemporaryBytes, context.Platform.ReadSeededFile(temporary));
        Assert.Contains($"file.replace:{Temporary(destination, 12)}->{destination}", context.Platform.Operations);
        Assert.DoesNotContain($"file.delete:{temporary}", context.Platform.Operations);
    }

    [Fact]
    public void SupersedeWithPreparedRollback_RequiresLiveSameRuntimeLock()
    {
        var context = CreateCommittedApplyContext();
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);
        var foreignPlatform = new SetupTestPlatform(CreatedAt, "D:\\foreign");
        var foreignPaths = new SetupRuntimePaths(foreignPlatform);
        using var foreignAcquire = SetupLock.TryAcquire(foreignPlatform, foreignPaths);

        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(
                foreignAcquire.Lock!, ChangeSetId, ValidTargets())).Code);

        context.Lock.Dispose();
        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.SupersedeWithPreparedRollback(
                context.Lock, ChangeSetId, ValidTargets())).Code);
    }

    [Fact]
    public void OpenOrCreatePrepared_CloseAndReopenExactJournalReturnsReusedWithoutWriting()
    {
        var context = CreateContext();
        Assert.Equal(
            SetupPreparedJournalOpenResult.Created,
            context.Store.OpenOrCreatePrepared(
                context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var exact = context.Platform.ReadSeededFile(destination);
        var operationCount = context.Platform.Operations.Count;

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths);
        var result = reopened.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());

        Assert.Equal(SetupPreparedJournalOpenResult.Reused, result);
        Assert.Equal(exact, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("target-order")]
    [InlineData("target-kind")]
    [InlineData("record-id")]
    [InlineData("step-order")]
    [InlineData("member-key")]
    [InlineData("prior-hash")]
    [InlineData("desired-hash")]
    [InlineData("backup-reference")]
    public void OpenOrCreatePrepared_ExistingShapeMismatchFailsClosedWithoutWrite(string mismatch)
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var exact = context.Platform.ReadSeededFile(destination);
        var expected = ValidTargets().ToArray();
        var operation = SetupJournalOperation.Apply;
        switch (mismatch)
        {
            case "operation": operation = SetupJournalOperation.Rollback; break;
            case "target-order": expected = expected.Reverse().ToArray(); break;
            case "target-kind": expected[0] = expected[0] with { TargetKind = SetupTargetKind.Toml }; break;
            case "record-id": expected[0] = expected[0] with { RecordId = Guid.Parse("00000000-0000-7000-8000-000000000104") }; break;
            case "step-order": expected[1] = expected[1] with { Steps = expected[1].Steps.Reverse().ToArray() }; break;
            case "member-key": expected[1] = expected[1] with { Steps = ReplaceStep(expected[1], 0, step => step with { MemberKey = "COPILOT_C" }) }; break;
            case "prior-hash": expected[0] = expected[0] with { Steps = ReplaceStep(expected[0], 0, step => step with { PriorStateHash = DesiredHash }) }; break;
            case "desired-hash": expected[0] = expected[0] with { Steps = ReplaceStep(expected[0], 0, step => step with { DesiredStateHash = PriorHash }) }; break;
            case "backup-reference": expected[0] = expected[0] with { Steps = ReplaceStep(expected[0], 0, step => step with { BackupReference = "backup-file-other" }) }; break;
        }
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, operation, expected));

        Assert.Equal(SetupJournalStorageCodes.AlreadyExists, exception.Code);
        Assert.Equal(exact, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void OpenOrCreatePrepared_ExistingNonDormantJournalFailsClosedWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var exact = context.Platform.ReadSeededFile(destination);
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));

        Assert.Equal(SetupJournalStorageCodes.AlreadyExists, exception.Code);
        Assert.Equal(exact, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public async Task OpenOrCreatePrepared_SameLockCallersSerializeBeforeAtomicCreate()
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        using var barrier = context.Platform.AddBarrier($"file.try-write-new-flushed:{destination}");

        var first = Task.Run(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));
        barrier.WaitUntilReached(CancellationToken.None);

        using var secondStarted = new ManualResetEventSlim();
        var second = Task.Run(() => new SetupTransactionJournalStore(context.Platform, context.Paths).OpenOrCreatePrepared(
            StartedLock(secondStarted, context.Lock), ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(second.IsCompleted);
        barrier.Release();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, results.Count(result => result == SetupPreparedJournalOpenResult.Created));
        Assert.Equal(1, results.Count(result => result == SetupPreparedJournalOpenResult.Reused));
        Assert.Equal(1, context.Platform.Operations.Count(operation => operation == $"file.try-write-new-flushed:{destination}"));
        Assert.NotNull(context.Store.Load(ChangeSetId));
    }

    [Fact]
    public async Task JournalCreate_ExternalDisposeKeepsExclusiveLockUntilCreateExits()
    {
        using var disposeRequested = new ManualResetEventSlim();
        var context = CreateContext(disposeRequested.Set);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        using var barrier = context.Platform.AddBarrier($"file.try-write-new-flushed:{destination}");

        var write = Task.Run(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));
        barrier.WaitUntilReached(CancellationToken.None);
        var dispose = Task.Run(context.Lock.Dispose);
        try
        {
            Assert.True(disposeRequested.Wait(TimeSpan.FromSeconds(10)));
            using var contended = SetupLock.TryAcquire(context.Platform, context.Paths);
            Assert.False(contended.Acquired);
        }
        finally
        {
            barrier.Release();
        }

        await Task.WhenAll(write, dispose);
        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying)).Code);
        using var reacquired = SetupLock.TryAcquire(context.Platform, context.Paths);
        Assert.True(reacquired.Acquired);
    }

    [Fact]
    public async Task JournalUpdate_ExternalDisposeKeepsExclusiveLockUntilReplaceExits()
    {
        using var disposeRequested = new ManualResetEventSlim();
        var context = CreateContext(disposeRequested.Set);
        context.Store.OpenOrCreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        using var barrier = context.Platform.AddBarrier($"file.replace:{Temporary(destination, 1)}->{destination}");

        var write = Task.Run(() => context.Store.MarkTransactionPhase(
            context.Lock, ChangeSetId, SetupJournalPhase.Applying));
        barrier.WaitUntilReached(CancellationToken.None);
        var dispose = Task.Run(context.Lock.Dispose);
        try
        {
            Assert.True(disposeRequested.Wait(TimeSpan.FromSeconds(10)));
            using var contended = SetupLock.TryAcquire(context.Platform, context.Paths);
            Assert.False(contended.Acquired);
        }
        finally
        {
            barrier.Release();
        }

        await Task.WhenAll(write, dispose);
        Assert.Equal(SetupJournalPhase.Applying, context.Store.Load(ChangeSetId)!.Phase);
        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating)).Code);
        using var reacquired = SetupLock.TryAcquire(context.Platform, context.Paths);
        Assert.True(reacquired.Acquired);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("directory")]
    public void OpenOrCreatePrepared_MissingPathEvaluationFailureReturnsFixedNonEchoError(string boundary)
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var operation = boundary == "metadata"
            ? $"file.metadata:{destination}"
            : $"directory.create:{context.Paths.Transactions}";
        context.Platform.InjectFault(operation, new IOException($"PRIVATE_PATH_MARKER:{destination}"));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(destination, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpenOrCreatePrepared_RequiresLiveSameRuntimeLock()
    {
        var context = CreateContext();
        var foreignPlatform = new SetupTestPlatform(CreatedAt, "D:\\foreign");
        var foreignPaths = new SetupRuntimePaths(foreignPlatform);
        using var foreignAcquire = SetupLock.TryAcquire(foreignPlatform, foreignPaths);

        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.OpenOrCreatePrepared(
                foreignAcquire.Lock!, ChangeSetId, SetupJournalOperation.Apply, ValidTargets())).Code);

        context.Lock.Dispose();
        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.OpenOrCreatePrepared(
                context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets())).Code);
    }

    [Theory]
    [InlineData("notification")]
    [InlineData("step")]
    public void OpenOrCreatePrepared_ImpossibleDormantStateFailsFixedWithoutOverwrite(string mismatch)
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var json = ValidJson();
        json = mismatch == "notification"
            ? json.Replace("\"environment_notification\": \"not_required\"", "\"environment_notification\": \"pending\"", StringComparison.Ordinal)
            : ReplaceFirst(json, "\"phase\": \"pending\"", "\"phase\": \"mutation_started\"", json.IndexOf("\"member_key\": \"COPILOT_A\"", StringComparison.Ordinal));
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Platform.SeedFile(destination, bytes);
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.Equal(bytes, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void OpenOrCreatePrepared_EmbeddedChangeSetMismatchFailsClosedWithoutOverwrite()
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var bytes = Encoding.UTF8.GetBytes(ValidJson().Replace(
            ChangeSetId.ToString("D"),
            "00000000-0000-7000-8000-000000000105",
            StringComparison.Ordinal));
        context.Platform.SeedFile(destination, bytes);
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));

        Assert.Equal(SetupJournalStorageCodes.AlreadyExists, exception.Code);
        Assert.Equal(bytes, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Theory]
    [InlineData(SetupPathKind.File, FileAttributes.ReparsePoint)]
    [InlineData(SetupPathKind.Directory, FileAttributes.Directory)]
    public void OpenOrCreatePrepared_ExistingNonRegularOrReparsePathFailsClosed(
        SetupPathKind kind,
        FileAttributes attributes)
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        context.Platform.SeedFile(destination, Encoding.UTF8.GetBytes(ValidJson()));
        context.Platform.SeedPathMetadata(destination, new SetupPathMetadata(true, kind, attributes));
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public async Task OpenOrCreatePrepared_ReboundExistingJournalFailsClosedWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var operation = $"file.read-bounded:{destination}:{MaximumJournalBytes}";
        using var barrier = context.Platform.AddBarrier(operation);
        var operationCount = context.Platform.Operations.Count;

        var open = Task.Run(() => Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets())));
        barrier.WaitUntilReached(CancellationToken.None);
        context.Platform.SeedPathMetadata(destination, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        barrier.Release();
        var exception = await open;

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
    }

    [Fact]
    public void OpenOrCreatePrepared_OversizeExistingJournalFailsFixedAndDoesNotOverwrite()
    {
        var context = CreateContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var bytes = Enumerable.Repeat((byte)'X', MaximumJournalBytes + 1).ToArray();
        context.Platform.SeedFile(destination, bytes);
        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.OpenOrCreatePrepared(
            context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));

        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.Equal(bytes, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), IsWriteOperation);
        Assert.DoesNotContain(destination, exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.Compensating)]
    [InlineData(SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack)]
    public void MarkTransactionPhase_PartialRecoveryReopensOnlyItsOperationSpecificRecoveryPhase(
        object operationValue,
        object recoveryPhaseValue)
    {
        var operation = (SetupJournalOperation)operationValue;
        var recoveryPhase = (SetupJournalPhase)recoveryPhaseValue;
        var context = CreatePartialContext(operation);

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, recoveryPhase);

        Assert.Equal(recoveryPhase, context.Store.Load(ChangeSetId)!.Phase);
    }

    [Theory]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.RollingBack)]
    [InlineData(SetupJournalOperation.Apply, SetupJournalPhase.Applying)]
    [InlineData(SetupJournalOperation.Rollback, SetupJournalPhase.Compensating)]
    [InlineData(SetupJournalOperation.Rollback, SetupJournalPhase.Applying)]
    public void MarkTransactionPhase_PartialRejectsWrongOperationAndForwardResume(
        object operationValue,
        object nextPhaseValue)
    {
        var operation = (SetupJournalOperation)operationValue;
        var nextPhase = (SetupJournalPhase)nextPhaseValue;
        var context = CreatePartialContext(operation);
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, nextPhase));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

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
        Assert.Equal(SetupEnvironmentNotification.NotRequired, created.EnvironmentNotification);
        Assert.Equal(operation, reopened!.Operation);
        Assert.Equal(2, reopened.Targets.Count);
        Assert.Single(reopened.Targets[0].Steps);
        Assert.Equal(new[] { "COPILOT_A", "COPILOT_B" }, reopened.Targets[1].Steps.Select(step => step.MemberKey));
        Assert.All(reopened.Targets.SelectMany(target => target.Steps), step => Assert.Equal(SetupJournalStepPhase.Pending, step.Phase));
        Assert.DoesNotContain("previous-value", Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId))), StringComparison.Ordinal);
    }

    [Fact]
    public void MarkStepPhase_FirstEnvironmentWriteAtomicallyRecordsPendingNotificationButFileWriteDoesNot()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);

        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, context.Store.Load(ChangeSetId)!.EnvironmentNotification);

        context.Store.MarkStepPhase(context.Lock, ChangeSetId, EnvironmentRecordId, "COPILOT_A",
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(SetupEnvironmentNotification.Pending, reopened.EnvironmentNotification);
        Assert.Equal(SetupJournalStepPhase.MutationStarted, reopened.Targets[1].Steps[0].Phase);
    }

    [Fact]
    public void MarkStepPhase_FirstRollbackEnvironmentRestoreAtomicallyRecordsPendingNotification()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Rollback, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);

        context.Store.MarkStepPhase(context.Lock, ChangeSetId, EnvironmentRecordId, "COPILOT_A",
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(SetupEnvironmentNotification.Pending, reopened.EnvironmentNotification);
        Assert.Equal(SetupJournalStepPhase.RestoreStarted, reopened.Targets[1].Steps[0].Phase);
    }

    [Fact]
    public void MarkEnvironmentNotificationCompleted_RequiresLiveLockPendingNotificationAndTerminalPhase()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        CompleteAllSteps(context, mutation: true);

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId)).Code);

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Committed);
        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);

        Assert.Equal(
            SetupEnvironmentNotification.Completed,
            new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!.EnvironmentNotification);
        Assert.Equal(SetupJournalStorageCodes.StaleUpdate, Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId)).Code);
    }

    [Fact]
    public void MarkEnvironmentNotificationCompleted_RequiresLiveSameRuntimeLock()
    {
        var context = CreateCommittedApplyContext();
        var foreignPlatform = new SetupTestPlatform(CreatedAt, "D:\\foreign");
        var foreignPaths = new SetupRuntimePaths(foreignPlatform);
        using var foreignAcquire = SetupLock.TryAcquire(foreignPlatform, foreignPaths);

        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkEnvironmentNotificationCompleted(foreignAcquire.Lock!, ChangeSetId)).Code);

        context.Lock.Dispose();
        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId)).Code);
    }

    [Fact]
    public void ApplyRestored_AllowsPendingAndRestoreCompletedStepsIncludingAllPending()
    {
        var allPending = CreateContext();
        allPending.Store.CreatePrepared(allPending.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        allPending.Store.MarkTransactionPhase(allPending.Lock, ChangeSetId, SetupJournalPhase.Applying);
        allPending.Store.MarkTransactionPhase(allPending.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        allPending.Store.MarkTransactionPhase(allPending.Lock, ChangeSetId, SetupJournalPhase.Restored);
        Assert.Equal(SetupJournalPhase.Restored, new SetupTransactionJournalStore(allPending.Platform, allPending.Paths).Load(ChangeSetId)!.Phase);

        var mixed = CreateContext();
        mixed.Store.CreatePrepared(mixed.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        mixed.Store.MarkTransactionPhase(mixed.Lock, ChangeSetId, SetupJournalPhase.Applying);
        mixed.Store.MarkStepPhase(mixed.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
        mixed.Store.MarkTransactionPhase(mixed.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        mixed.Store.MarkStepPhase(mixed.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.RestoreStarted);
        mixed.Store.MarkStepPhase(mixed.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
        mixed.Store.MarkTransactionPhase(mixed.Lock, ChangeSetId, SetupJournalPhase.Restored);

        Assert.Equal(
            new[] { SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.Pending, SetupJournalStepPhase.Pending },
            new SetupTransactionJournalStore(mixed.Platform, mixed.Paths).Load(ChangeSetId)!.Targets.SelectMany(target => target.Steps).Select(step => step.Phase));
    }

    [Theory]
    [InlineData(SetupJournalStepPhase.MutationStarted)]
    [InlineData(SetupJournalStepPhase.MutationCompleted)]
    [InlineData(SetupJournalStepPhase.RestoreStarted)]
    public void ApplyRestored_RejectsInFlightStepPhasesWithoutWrite(object stepPhaseValue)
    {
        var stepPhase = (SetupJournalStepPhase)stepPhaseValue;
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
        if (stepPhase == SetupJournalStepPhase.MutationCompleted)
        {
            context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.MutationCompleted);
        }
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        if (stepPhase == SetupJournalStepPhase.RestoreStarted)
        {
            context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
                SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.RestoreStarted);
        }
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Restored));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    [Fact]
    public void CompensatingApply_RejectsPendingToRestoreStartedWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    [Fact]
    public void RestoredApply_CanDurablyCompletePendingEnvironmentNotification()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, EnvironmentRecordId, "COPILOT_A",
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, EnvironmentRecordId, "COPILOT_A",
            SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.RestoreStarted);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, EnvironmentRecordId, "COPILOT_A",
            SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Restored);

        context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(SetupJournalPhase.Restored, reopened.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, reopened.EnvironmentNotification);
    }

    [Fact]
    public void PartialTransaction_NeverCompletesPendingEnvironmentNotification()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, EnvironmentRecordId, "COPILOT_A",
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Partial);
        var before = context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
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
        { SetupJournalOperation.Rollback, SetupJournalPhase.RollingBack, SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RollbackRestoreCompleted_CanDurablyRecordFreshRestoreIntentWithoutResettingNotification(bool environmentStep)
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Rollback, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
        var recordId = environmentStep ? EnvironmentRecordId : FileRecordId;
        var memberKey = environmentStep ? "COPILOT_A" : null;
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, recordId, memberKey,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, recordId, memberKey,
            SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
        var expectedNotification = environmentStep
            ? SetupEnvironmentNotification.Pending
            : SetupEnvironmentNotification.NotRequired;

        context.Store.MarkStepPhase(context.Lock, ChangeSetId, recordId, memberKey,
            SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted);

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        var reopenedStep = reopened.Targets.Single(target => target.RecordId == recordId)
            .Steps.Single(step => step.MemberKey == memberKey);
        Assert.Equal(SetupJournalStepPhase.RestoreStarted, reopenedStep.Phase);
        Assert.Equal(expectedNotification, reopened.EnvironmentNotification);

        new SetupTransactionJournalStore(context.Platform, context.Paths).MarkStepPhase(
            context.Lock, ChangeSetId, recordId, memberKey,
            SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);

        var completed = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(
            SetupJournalStepPhase.RestoreCompleted,
            completed.Targets.Single(target => target.RecordId == recordId)
                .Steps.Single(step => step.MemberKey == memberKey).Phase);
        Assert.Equal(expectedNotification, completed.EnvironmentNotification);
    }

    [Fact]
    public void RollbackRestoreCompleted_PartialMustReopenRollingBackBeforeFreshRestoreIntent()
    {
        var context = CreateRollbackWithCompletedFileStep();
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Partial);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var before = context.Platform.ReadSeededFile(destination);

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(destination));

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted);

        Assert.Equal(
            SetupJournalStepPhase.RestoreStarted,
            new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!.Targets[0].Steps[0].Phase);
    }

    [Fact]
    public void ApplyRestoreCompleted_RejectsFreshRollbackRestoreIntentWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.MutationStarted, SetupJournalStepPhase.RestoreStarted);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var before = context.Platform.ReadSeededFile(destination);

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(destination));
    }

    [Fact]
    public void CommittedRollback_RejectsFreshRestoreIntentWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Rollback, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
        CompleteAllSteps(context, mutation: false);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Committed);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var before = context.Platform.ReadSeededFile(destination);

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted));

        Assert.Equal(SetupJournalStorageCodes.TransitionInvalid, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(destination));
    }

    [Fact]
    public void PreparedRollback_RejectsFreshRestoreIntentWithoutWrite()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Rollback, ValidTargets());
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var before = context.Platform.ReadSeededFile(destination);

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted));

        Assert.Equal(SetupJournalStorageCodes.StaleUpdate, exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(destination));
    }

    [Theory]
    [InlineData(SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted)]
    [InlineData(SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.MutationStarted)]
    [InlineData(SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.MutationCompleted)]
    public void RollbackRestoreCompleted_RejectsWrongExpectedOrForwardMutationPhaseWithoutWrite(
        object expectedPhaseValue,
        object nextPhaseValue)
    {
        var expectedPhase = (SetupJournalStepPhase)expectedPhaseValue;
        var nextPhase = (SetupJournalStepPhase)nextPhaseValue;
        var context = CreateRollbackWithCompletedFileStep();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var before = context.Platform.ReadSeededFile(destination);

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null, expectedPhase, nextPhase));

        Assert.Equal(
            expectedPhase == SetupJournalStepPhase.RestoreCompleted
                ? SetupJournalStorageCodes.TransitionInvalid
                : SetupJournalStorageCodes.StaleUpdate,
            exception.Code);
        Assert.Equal(before, context.Platform.ReadSeededFile(destination));
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

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
            { valid.Replace("\"schema_version\": 1", "\"schema_version\": 0", StringComparison.Ordinal), SetupJournalStorageCodes.VersionUnsupported },
            { valid.Replace("\"schema_version\": 1", "\"schema_version\": 2", StringComparison.Ordinal), SetupJournalStorageCodes.VersionUnsupported },
            { valid.Replace("\"schema_version\": 1", "\"schema_version\": null", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"operation\": \"apply\"", "\"operation\": 1", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"environment_notification\": \"not_required\",", string.Empty, StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"environment_notification\": \"not_required\"", "\"environment_notification\": null", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"environment_notification\": \"not_required\"", "\"environment_notification\": \"unknown\"", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"environment_notification\": \"not_required\"", "\"environment_notification\": \"not_required\", \"environment_notification\": \"pending\"", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"phase\": \"prepared\"", "\"phase\": \"prepared\", \"PRIVATE_PATH_MARKER\": true", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"phase\": \"prepared\"", "\"phase\": \"prepared\", \"phase\": \"prepared\"", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
            { valid.Replace("\"schema_version\": 1", "\"schema_version\": 1, \"schema_version\": 2", StringComparison.Ordinal), SetupJournalStorageCodes.Corrupt },
        };
    }

    [Theory]
    [InlineData("pending", "pending")]
    [InlineData("completed", "pending")]
    [InlineData("not_required", "mutation_started")]
    public void Load_RejectsImpossibleNotificationStateWithFixedNonEchoError(string notification, string environmentStepPhase)
    {
        var json = ValidJson()
            .Replace("\"environment_notification\": \"not_required\"", $"\"environment_notification\": \"{notification}\"", StringComparison.Ordinal);
        if (environmentStepPhase != "pending")
        {
            var marker = "\"member_key\": \"COPILOT_A\"";
            var member = json.IndexOf(marker, StringComparison.Ordinal);
            var phase = json.IndexOf("\"phase\": \"pending\"", member, StringComparison.Ordinal);
            json = json.Remove(phase, "\"phase\": \"pending\"".Length).Insert(phase, $"\"phase\": \"{environmentStepPhase}\"");
        }
        var transactionPhase = json.IndexOf("\"phase\": \"prepared\"", StringComparison.Ordinal);
        json = json.Remove(transactionPhase, "\"phase\": \"prepared\"".Length).Insert(transactionPhase, "\"phase\": \"applying\"");

        var context = CreateContext();
        context.Platform.SeedFile(context.Paths.GetTransactionJournal(ChangeSetId), Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load(ChangeSetId));
        Assert.Equal(SetupJournalStorageCodes.Corrupt, exception.Code);
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ToleratesPropertyReorderingButPreservesExactTargetAndStepOrder()
    {
        var context = CreateContext();
        using var document = JsonDocument.Parse(ValidJson());
        var root = document.RootElement;
        var reordered = $$"""
            {"targets":{{root.GetProperty("targets").GetRawText()}},"environment_notification":"not_required","phase":"prepared","created_at":"2026-07-12T01:02:03.0000000Z","operation":"apply","change_set_id":"{{ChangeSetId:D}}","schema_version":1}
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

    [Fact]
    public void SystemFileSystem_OpenOrCreatePreparedCloseAndReopenReturnsReusedWithoutChangingBytes()
    {
        var localApplicationData = Path.Combine(Path.GetTempPath(), $"setup-journal-reuse-{Guid.NewGuid():N}");
        try
        {
            var firstPlatform = new SystemSetupPlatform(localApplicationData: localApplicationData);
            var firstPaths = new SetupRuntimePaths(firstPlatform);
            byte[] exact;
            using (var acquired = SetupLock.TryAcquire(firstPlatform, firstPaths))
            {
                var store = new SetupTransactionJournalStore(firstPlatform, firstPaths);
                Assert.Equal(
                    SetupPreparedJournalOpenResult.Created,
                    store.OpenOrCreatePrepared(
                        acquired.Lock!, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));
                exact = File.ReadAllBytes(firstPaths.GetTransactionJournal(ChangeSetId));
            }

            var reopenedPlatform = new SystemSetupPlatform(localApplicationData: localApplicationData);
            var reopenedPaths = new SetupRuntimePaths(reopenedPlatform);
            using var reopenedLock = SetupLock.TryAcquire(reopenedPlatform, reopenedPaths);
            var reopened = new SetupTransactionJournalStore(reopenedPlatform, reopenedPaths);

            Assert.Equal(
                SetupPreparedJournalOpenResult.Reused,
                reopened.OpenOrCreatePrepared(
                    reopenedLock.Lock!, ChangeSetId, SetupJournalOperation.Apply, ValidTargets()));
            Assert.Equal(exact, File.ReadAllBytes(reopenedPaths.GetTransactionJournal(ChangeSetId)));
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

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public void RollbackRestoreRedo_FaultBoundaryReopensAsCompletedOrFreshIntentAndNeverDeletesTemporary(
        string boundary,
        bool afterEffect)
    {
        var context = CreateRollbackWithCompletedFileStep();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var temporary = Temporary(destination, 5);
        var operation = boundary switch
        {
            "write" => $"file.write-new:{temporary}",
            "flush" => $"file.flush:{temporary}",
            _ => $"file.replace:{temporary}->{destination}",
        };
        InjectFault(context.Platform, operation, afterEffect);

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreCompleted, SetupJournalStepPhase.RestoreStarted));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.DoesNotContain("PRIVATE_PATH_MARKER", exception.ToString(), StringComparison.Ordinal);
        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(
            boundary == "replace" && afterEffect
                ? SetupJournalStepPhase.RestoreStarted
                : SetupJournalStepPhase.RestoreCompleted,
            reopened.Targets[0].Steps[0].Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, reopened.EnvironmentNotification);
        if (afterEffect && boundary != "replace")
        {
            Assert.True(context.Platform.FileSystem.FileExists(temporary));
        }
        Assert.DoesNotContain($"file.delete:{temporary}", context.Platform.Operations);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public void EnvironmentPromotion_FaultBoundaryReopensAsCompleteOldOrNewPair(string boundary, bool afterEffect)
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var temporary = Temporary(destination, 3);
        var operation = boundary switch
        {
            "write" => $"file.write-new:{temporary}",
            "flush" => $"file.flush:{temporary}",
            _ => $"file.replace:{temporary}->{destination}",
        };
        InjectFault(context.Platform, operation, afterEffect);

        Assert.Throws<SetupStorageException>(() => context.Store.MarkStepPhase(
            context.Lock, ChangeSetId, EnvironmentRecordId, "COPILOT_A",
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.MutationStarted));

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        var newPair = boundary == "replace" && afterEffect;
        Assert.Equal(newPair ? SetupJournalStepPhase.MutationStarted : SetupJournalStepPhase.Pending, reopened.Targets[1].Steps[0].Phase);
        Assert.Equal(newPair ? SetupEnvironmentNotification.Pending : SetupEnvironmentNotification.NotRequired, reopened.EnvironmentNotification);
        if (afterEffect && boundary != "replace")
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
    public void NotificationCompletion_FaultBoundaryReopensAsPendingOrCompleted(string boundary, bool afterEffect)
    {
        var context = CreateCommittedApplyContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var temporary = Temporary(destination, 10);
        var operation = boundary switch
        {
            "write" => $"file.write-new:{temporary}",
            "flush" => $"file.flush:{temporary}",
            _ => $"file.replace:{temporary}->{destination}",
        };
        InjectFault(context.Platform, operation, afterEffect);

        Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId));

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths).Load(ChangeSetId)!;
        Assert.Equal(
            boundary == "replace" && afterEffect ? SetupEnvironmentNotification.Completed : SetupEnvironmentNotification.Pending,
            reopened.EnvironmentNotification);
        if (afterEffect && boundary != "replace")
        {
            Assert.True(context.Platform.FileSystem.FileExists(temporary));
        }
    }

    [Fact]
    public void NotificationCompletion_PreReplaceFaultCanRetryWithoutReusingOrDeletingReboundOrphan()
    {
        var context = CreateCommittedApplyContext();
        var destination = context.Paths.GetTransactionJournal(ChangeSetId);
        var orphan = Temporary(destination, 10);
        context.Platform.InjectFault($"file.replace:{orphan}->{destination}", new IOException("PRIVATE_PATH_MARKER"));

        Assert.Throws<SetupStorageException>(() =>
            context.Store.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId));
        var reboundBytes = Encoding.UTF8.GetBytes("rebound-orphan");
        context.Platform.SeedFile(orphan, reboundBytes);

        var reopened = new SetupTransactionJournalStore(context.Platform, context.Paths);
        reopened.MarkEnvironmentNotificationCompleted(context.Lock, ChangeSetId);

        Assert.Equal(reboundBytes, context.Platform.ReadSeededFile(orphan));
        Assert.Equal(SetupEnvironmentNotification.Completed, reopened.Load(ChangeSetId)!.EnvironmentNotification);
        Assert.Contains($"file.replace:{Temporary(destination, 11)}->{destination}", context.Platform.Operations);
        Assert.DoesNotContain($"file.delete:{orphan}", context.Platform.Operations);
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
        else if (phase == SetupJournalStepPhase.RestoreCompleted)
        {
            context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
            context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null, SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
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

    private static IReadOnlyList<SetupJournalTarget> FileOnlyTargets() =>
    [
        new(FileRecordId, SetupTargetKind.Json,
        [
            new(null, PriorHash, DesiredHash, "backup-file-102", SetupJournalStepPhase.Pending),
        ]),
    ];

    private static IReadOnlyList<SetupJournalTarget> PendingTargets(IReadOnlyList<SetupJournalTarget> targets) =>
        targets.Select(target => target with
        {
            Steps = target.Steps.Select(step => step with { Phase = SetupJournalStepPhase.Pending }).ToArray(),
        }).ToArray();

    private static TestContext CreateSupersessionState(string state)
    {
        var context = CreateContext();
        if (state is "rolling-back" or "committed-rollback")
        {
            context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Rollback, FileOnlyTargets());
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
            if (state == "committed-rollback")
            {
                CompleteAllSteps(context, mutation: false);
                context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Committed);
            }

            return context;
        }

        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        if (state == "prepared-apply")
        {
            return context;
        }

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        if (state == "applying-apply")
        {
            return context;
        }

        if (state == "committed-apply-notification-pending")
        {
            CompleteAllSteps(context, mutation: true);
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Committed);
            return context;
        }

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        if (state == "compensating-apply")
        {
            return context;
        }

        context.Store.MarkTransactionPhase(
            context.Lock,
            ChangeSetId,
            state == "restored-apply" ? SetupJournalPhase.Restored : SetupJournalPhase.Partial);
        return context;
    }

    private static IReadOnlyList<SetupJournalStep> ReplaceStep(
        SetupJournalTarget target,
        int index,
        Func<SetupJournalStep, SetupJournalStep> replace)
    {
        var steps = target.Steps.ToArray();
        steps[index] = replace(steps[index]);
        return steps;
    }

    private static SetupLock StartedLock(ManualResetEventSlim started, SetupLock setupLock)
    {
        started.Set();
        return setupLock;
    }

    private static string ReplaceFirst(string value, string oldValue, string newValue, int startIndex)
    {
        var index = value.IndexOf(oldValue, startIndex, StringComparison.Ordinal);
        return value.Remove(index, oldValue.Length).Insert(index, newValue);
    }

    private static bool IsWriteOperation(string operation) =>
        operation.StartsWith("file.write", StringComparison.Ordinal) ||
        operation.StartsWith("file.try-write", StringComparison.Ordinal) ||
        operation.StartsWith("file.flush", StringComparison.Ordinal) ||
        operation.StartsWith("file.move", StringComparison.Ordinal) ||
        operation.StartsWith("file.replace", StringComparison.Ordinal) ||
        operation.StartsWith("file.delete", StringComparison.Ordinal) ||
        operation.StartsWith("directory.create", StringComparison.Ordinal);

    private static TestContext CreatePartialContext(SetupJournalOperation operation)
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, operation, ValidTargets());
        if (operation == SetupJournalOperation.Apply)
        {
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Compensating);
        }
        else
        {
            context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
        }

        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Partial);
        return context;
    }

    private static string ValidJson()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        return Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.GetTransactionJournal(ChangeSetId)));
    }

    private static TestContext CreateContext(Action? disposeRequestedObserver = null)
    {
        var platform = new SetupTestPlatform(CreatedAt);
        var paths = new SetupRuntimePaths(platform);
        SetupLock setupLock;
        if (disposeRequestedObserver is null)
        {
            setupLock = SetupLock.TryAcquire(platform, paths).Lock!;
        }
        else
        {
            paths.EnsureRoot();
            var handle = platform.FileSystem.TryAcquireExclusiveFileLock(paths.Lock)!;
            setupLock = SetupLock.CreateForTesting(handle, platform, paths, disposeRequestedObserver);
        }

        return new TestContext(platform, paths, setupLock, new SetupTransactionJournalStore(platform, paths));
    }

    private static TestContext CreateCommittedApplyContext()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Apply, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Applying);
        CompleteAllSteps(context, mutation: true);
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.Committed);
        return context;
    }

    private static TestContext CreateRollbackWithCompletedFileStep()
    {
        var context = CreateContext();
        context.Store.CreatePrepared(context.Lock, ChangeSetId, SetupJournalOperation.Rollback, ValidTargets());
        context.Store.MarkTransactionPhase(context.Lock, ChangeSetId, SetupJournalPhase.RollingBack);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.Pending, SetupJournalStepPhase.RestoreStarted);
        context.Store.MarkStepPhase(context.Lock, ChangeSetId, FileRecordId, null,
            SetupJournalStepPhase.RestoreStarted, SetupJournalStepPhase.RestoreCompleted);
        return context;
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

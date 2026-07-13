using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupStorageTests
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 7, 12, 1, 2, 3, TimeSpan.Zero);
    private static readonly Guid ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000101");
    private static readonly Guid RecordId = Guid.Parse("00000000-0000-7000-8000-000000000201");
    private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Fact]
    public void PersistPlannedChangeSet_WritesAndFlushesImmutablePlanBeforeAtomicallyReplacingLedger()
    {
        var context = CreateContext();
        context.Store.PersistPlannedChangeSet(context.Lock, CreatePlan(), CreatePlannedChangeSet());

        var operations = context.Platform.Operations;
        var planFlush = IndexOf(operations, $"file.flush:{context.Paths.GetPlan(ChangeSetId)}.tmp");
        var planMove = IndexOf(operations, $"file.move:{context.Paths.GetPlan(ChangeSetId)}.tmp->{context.Paths.GetPlan(ChangeSetId)}");
        var checkpoint = IndexOf(operations, "checkpoint:after-plan-persisted-before-ledger");
        var ledgerFlush = IndexOf(operations, $"file.flush:{context.Paths.OwnershipLedger}.tmp");
        var ledgerMove = IndexOf(operations, $"file.move:{context.Paths.OwnershipLedger}.tmp->{context.Paths.OwnershipLedger}");

        Assert.True(planFlush < planMove);
        Assert.True(planMove < checkpoint);
        Assert.True(checkpoint < ledgerFlush);
        Assert.True(ledgerFlush < ledgerMove);
        Assert.False(context.Platform.FileSystem.FileExists($"{context.Paths.GetPlan(ChangeSetId)}.tmp"));
        Assert.False(context.Platform.FileSystem.FileExists($"{context.Paths.OwnershipLedger}.tmp"));
    }

    [Fact]
    public void PersistPlannedChangeSet_FaultAfterPlanBeforeLedgerLeavesNoLedgerRowAndCleansNormalOrphan()
    {
        var context = CreateContext();
        context.Platform.InjectFault("checkpoint:after-plan-persisted-before-ledger", new IOException("PREVIOUS_SECRET_MARKER"));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(context.Lock, CreatePlan(), CreatePlannedChangeSet()));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.False(context.Platform.FileSystem.FileExists(context.Paths.GetPlan(ChangeSetId)));
        Assert.False(context.Platform.FileSystem.FileExists(context.Paths.OwnershipLedger));
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_IgnoresOrphanPlanWithoutLedgerRow()
    {
        var context = CreateContext();
        context.PlanStore.Create(context.Lock, CreatePlan());

        var ledger = context.Store.Load();

        Assert.Equal(1, ledger.SchemaVersion);
        Assert.Empty(ledger.ChangeSets);
    }

    [Fact]
    public void Load_NonTerminalLedgerRowWithoutPlanReportsFixedRecoveryRequired()
    {
        var context = CreateContext();
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [CreatePlannedChangeSet()]));
        context.Platform.FileSystem.DeleteFile(context.Paths.GetPlan(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void LoadForRecovery_ReturnsExactMissingPlanRowAndPreservesMixedOrderWithoutLiveLock()
    {
        var context = CreateContext();
        var terminal = CreateAppliedChangeSet() with
        {
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000102"),
            Targets =
            [
                CreateLedgerTarget() with
                {
                    RecordId = Guid.Parse("00000000-0000-7000-8000-000000000202"),
                    AppliedStateHash = HashB,
                    BackupReference = "backup-00000000-0000-7000-8000-000000000202",
                    RollbackStatus = SetupLedgerRollbackStatus.Pending,
                    OutcomeCode = SetupCodes.ApplySucceeded,
                },
            ],
        };
        var expected = new SetupOwnershipLedger(1, [terminal, CreatePlannedChangeSet()]);
        context.Store.Save(context.Lock, expected);
        context.Lock.Dispose();
        var operationCount = context.Platform.Operations.Count;

        var recovered = context.Store.LoadForRecovery();

        Assert.Equivalent(expected, recovered, strict: true);
        Assert.Equal([terminal.ChangeSetId, ChangeSetId], recovered.ChangeSets.Select(changeSet => changeSet.ChangeSetId));
        Assert.DoesNotContain(context.Platform.Operations.Skip(operationCount), operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal) ||
            operation.StartsWith("file.flush", StringComparison.Ordinal) ||
            operation.StartsWith("file.move", StringComparison.Ordinal) ||
            operation.StartsWith("file.replace", StringComparison.Ordinal) ||
            operation.StartsWith("file.lock", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-json-PREVIOUS_SECRET_MARKER", SetupCodes.LedgerCorrupt)]
    [InlineData("{\"schema_version\":0,\"change_sets\":[],\"marker\":\"PREVIOUS_SECRET_MARKER\"}", SetupCodes.LedgerVersionUnsupported)]
    [InlineData("{\"schema_version\":2,\"change_sets\":[],\"marker\":\"PREVIOUS_SECRET_MARKER\"}", SetupCodes.LedgerVersionUnsupported)]
    public void LoadForRecovery_CorruptOrUnsupportedLedgerFailsClosedWithoutEcho(
        string content,
        string expectedCode)
    {
        var context = CreateContext();
        context.Platform.SeedFile(context.Paths.OwnershipLedger, Encoding.UTF8.GetBytes(content));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.LoadForRecovery());

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(expectedCode, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LoadForRecovery_OversizeLedgerUsesBoundedReadAndFailsFixedWithoutEcho()
    {
        var context = CreateContext();
        var source = context.Paths.OwnershipLedger;
        context.Platform.SeedFile(
            source,
            Enumerable.Repeat((byte)'P', SetupLedgerStore.MaximumLedgerBytes + 1).ToArray());

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.LoadForRecovery());

        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(source, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            $"file.read-bounded:{source}:{SetupLedgerStore.MaximumLedgerBytes}",
            context.Platform.Operations);
        Assert.DoesNotContain($"file.read:{source}", context.Platform.Operations);
    }

    [Theory]
    [InlineData(SetupPathKind.File, FileAttributes.ReparsePoint)]
    [InlineData(SetupPathKind.Directory, FileAttributes.Directory)]
    [InlineData(SetupPathKind.Other, FileAttributes.Normal)]
    public void LoadForRecovery_NonRegularOrReparseLedgerFailsFixedBeforeReading(
        SetupPathKind kind,
        FileAttributes attributes)
    {
        var context = CreateContext();
        var source = context.Paths.OwnershipLedger;
        context.Platform.SeedFile(source, SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [])));
        context.Platform.SeedPathMetadata(source, new SetupPathMetadata(true, kind, attributes));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.LoadForRecovery());

        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
        Assert.DoesNotContain(context.Platform.Operations, operation =>
            operation.StartsWith($"file.read-bounded:{source}:", StringComparison.Ordinal));
    }

    [Fact]
    public void LoadForRecovery_MetadataFailureMapsToFixedCorruptWithoutEcho()
    {
        var context = CreateContext();
        var source = context.Paths.OwnershipLedger;
        context.Platform.SeedFile(source, SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [])));
        context.Platform.InjectFault(
            $"file.metadata:{source}",
            new IOException("PREVIOUS_SECRET_MARKER"));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.LoadForRecovery());

        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadForRecovery_ReboundLedgerFailsFixedAfterBoundedRead()
    {
        var context = CreateContext();
        var source = context.Paths.OwnershipLedger;
        context.Platform.SeedFile(source, SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [])));
        using var barrier = context.Platform.AddBarrier(
            $"file.read-bounded:{source}:{SetupLedgerStore.MaximumLedgerBytes}");

        var load = Task.Run(() => Assert.Throws<SetupStorageException>(() => context.Store.LoadForRecovery()));
        barrier.WaitUntilReached(CancellationToken.None);
        context.Platform.SeedPathMetadata(
            source,
            new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        barrier.Release();
        var exception = await load;

        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void PlanCreate_IsImmutableAndRejectsOverwriteWithoutChangingBytes()
    {
        var context = CreateContext();
        context.PlanStore.Create(context.Lock, CreatePlan());
        var original = context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId));

        var changed = CreatePlan() with { ToolVersion = "9.9.9" };
        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Create(context.Lock, changed));

        Assert.Equal(SetupStorageCodes.PlanAlreadyExists, exception.Code);
        Assert.Equal(original, context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));
    }

    [Fact]
    public void Plan_RoundTripsExactPrivateLocationsAndDesiredStateWithoutPreviousValueField()
    {
        var context = CreateContext();
        var plan = CreatePlan();

        context.PlanStore.Create(context.Lock, plan);
        var reopened = new SetupPlanStore(context.Platform, context.Paths).Load(ChangeSetId);
        var json = Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));

        Assert.Equivalent(plan, reopened, strict: true);
        Assert.Contains("C:\\\\Synthetic\\\\settings.json", json, StringComparison.Ordinal);
        Assert.Contains("DESIRED_VALUE_MARKER", json, StringComparison.Ordinal);
        Assert.DoesNotContain("previous", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_EnvNoOpWithMissingDesiredValue_WritesVersionOneAndReopens()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"copilot-agent-observability-{Guid.NewGuid():N}");
        var plan = CreatePlanWithSingleMember(SetupTargetKind.Env, SetupOperation.NoOp, null);
        try
        {
            var firstPlatform = new CopilotAgentObservability.ConfigCli.Setup.Platform.SystemSetupPlatform(localApplicationData: temporaryRoot);
            var firstPaths = new SetupRuntimePaths(firstPlatform);
            var firstPlans = new SetupPlanStore(firstPlatform, firstPaths);
            using (var acquired = SetupLock.TryAcquire(firstPlatform, firstPaths))
            {
                firstPlans.Create(acquired.Lock!, plan);
            }

            var reopenedPlatform = new CopilotAgentObservability.ConfigCli.Setup.Platform.SystemSetupPlatform(localApplicationData: temporaryRoot);
            var reopenedPlans = new SetupPlanStore(reopenedPlatform, new SetupRuntimePaths(reopenedPlatform));
            var reopened = reopenedPlans.Load(ChangeSetId);

            Assert.Equivalent(plan, reopened, strict: true);
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData(SetupTargetKind.Env, SetupOperation.Add)]
    [InlineData(SetupTargetKind.Env, SetupOperation.Replace)]
    [InlineData(SetupTargetKind.File, SetupOperation.NoOp)]
    [InlineData(SetupTargetKind.Json, SetupOperation.NoOp)]
    public void Plan_RejectsInvalidMissingDesiredValueForTargetAndOperation(
        SetupTargetKind targetKind,
        SetupOperation operation)
    {
        var context = CreateContext();
        var plan = CreatePlanWithSingleMember(targetKind, operation, null);

        Assert.Throws<FormatException>(() => context.PlanStore.Create(context.Lock, plan));
        Assert.DoesNotContain(context.Platform.Operations, operationName => operationName.StartsWith("file.write:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SetupOperation.Remove)]
    public void Plan_EnvRemoveWithPresentDesiredValue_IsRejected(SetupOperation operation)
    {
        var context = CreateContext();
        var plan = CreatePlanWithSingleMember(SetupTargetKind.Env, operation, "DESIRED_VALUE_MARKER");

        Assert.Throws<FormatException>(() => context.PlanStore.Create(context.Lock, plan));
        Assert.DoesNotContain(context.Platform.Operations, operationName => operationName.StartsWith("file.write:", StringComparison.Ordinal));
    }

    [Fact]
    public void Ledger_RoundTripsOnlyRepositorySafeFieldsInDeterministicOrder()
    {
        var context = CreateContext();
        var second = CreatePlannedChangeSet() with
        {
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000102"),
            CreatedAt = CreatedAt.AddMinutes(1),
            UpdatedAt = CreatedAt.AddMinutes(1),
            State = SetupChangeSetState.Applied,
            Targets =
            [
                CreateLedgerTarget() with
                {
                    RecordId = Guid.Parse("00000000-0000-7000-8000-000000000202"),
                    AppliedStateHash = HashB,
                    BackupReference = "backup-00000000-0000-7000-8000-000000000202",
                    RollbackStatus = SetupLedgerRollbackStatus.Pending,
                },
            ],
        };
        var ledger = new SetupOwnershipLedger(1, [CreatePlannedChangeSet(), second]);

        context.PlanStore.Create(context.Lock, CreatePlan());
        context.Store.Save(context.Lock, ledger);
        var reopened = new SetupLedgerStore(context.Platform, context.Paths, context.PlanStore).Load();
        var json = Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.OwnershipLedger));

        Assert.Equivalent(ledger, reopened, strict: true);
        Assert.True(json.IndexOf(ChangeSetId.ToString("D"), StringComparison.Ordinal) <
            json.IndexOf(second.ChangeSetId.ToString("D"), StringComparison.Ordinal));
        Assert.DoesNotContain("C:\\Synthetic", json, StringComparison.Ordinal);
        Assert.DoesNotContain("DESIRED_VALUE_MARKER", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", json, StringComparison.Ordinal);
        Assert.DoesNotContain("target_location", json, StringComparison.Ordinal);
        Assert.DoesNotContain("desired_state", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_ReplacesExistingLedgerAtomicallyAndCleansTemporaryFileAfterFault()
    {
        var context = CreateContext();
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []));
        var original = context.Platform.ReadSeededFile(context.Paths.OwnershipLedger);
        context.Platform.InjectFault($"file.replace:{context.Paths.OwnershipLedger}.tmp->{context.Paths.OwnershipLedger}", new IOException("synthetic"));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.Equal(original, context.Platform.ReadSeededFile(context.Paths.OwnershipLedger));
        Assert.False(context.Platform.FileSystem.FileExists($"{context.Paths.OwnershipLedger}.tmp"));
    }

    [Fact]
    public void Load_MissingLedgerReturnsEmptyVersionOne()
    {
        var context = CreateContext();

        Assert.Equal(new SetupOwnershipLedger(1, []), context.Store.Load());
    }

    [Theory]
    [InlineData("not-json-PREVIOUS_SECRET_MARKER", SetupCodes.LedgerCorrupt)]
    [InlineData("{\"schema_version\":2,\"change_sets\":[],\"marker\":\"PREVIOUS_SECRET_MARKER\"}", SetupCodes.LedgerVersionUnsupported)]
    public void Load_CorruptOrUnknownLedgerFailsClosedWithoutEcho(string content, string expectedCode)
    {
        var context = CreateContext();
        context.Platform.SeedFile(context.Paths.OwnershipLedger, Encoding.UTF8.GetBytes(content));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load());

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(expectedCode, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PersistPlannedChangeSet_RejectsMismatchedPlanAndLedgerBeforeWriting()
    {
        var context = CreateContext();
        var ledgerRow = CreatePlannedChangeSet() with
        {
            Adapter = "other-adapter",
            Targets = [CreateLedgerTarget() with { OwningAdapter = "other-adapter" }],
        };

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(context.Lock, CreatePlan(), ledgerRow));

        Assert.Equal(SetupStorageCodes.PlanLedgerMismatch, exception.Code);
        Assert.DoesNotContain(context.Platform.Operations, operation => operation.StartsWith("file.write:", StringComparison.Ordinal));
    }

    [Fact]
    public void VersionOneFixture_IsExactlyTheProductionSerializedShippedState()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Setup", "v1", "ownership-ledger.v1.json");
        var expected = SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [CreateAppliedChangeSet()]));

        Assert.Equal(expected, File.ReadAllBytes(fixturePath));

        using var document = JsonDocument.Parse(expected);
        Assert.Equal(1, document.RootElement.GetProperty("schema_version").GetInt32());
        Assert.Single(document.RootElement.GetProperty("change_sets").EnumerateArray());
    }

    [Theory]
    [InlineData("adapter", "null")]
    [InlineData("adapter", "{\"marker\":\"PREVIOUS_SECRET_MARKER\"}")]
    [InlineData("targets", "null")]
    public void PlanLoad_NullOrWrongTypeMapsToFixedRecoveryRequiredWithoutEcho(string property, string replacement)
    {
        var context = CreateContext();
        var json = Encoding.UTF8.GetString(SetupPlanStore.Serialize(CreatePlan()))
            .Replace("DESIRED_VALUE_MARKER", "PREVIOUS_SECRET_MARKER", StringComparison.Ordinal);
        json = ReplacePropertyValue(json, property, replacement);
        context.Platform.SeedFile(context.Paths.GetPlan(ChangeSetId), Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Load(ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("adapter", "null")]
    [InlineData("adapter", "{\"marker\":\"PREVIOUS_SECRET_MARKER\"}")]
    [InlineData("change_sets", "null")]
    public void LedgerLoad_NullOrWrongTypeMapsToFixedCorruptWithoutEcho(string property, string replacement)
    {
        var context = CreateContext();
        var json = Encoding.UTF8.GetString(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));
        json = ReplacePropertyValue(json, property, replacement);
        context.Platform.SeedFile(context.Paths.OwnershipLedger, Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load());

        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerLoad_AcceptsReorderedExactPropertiesAndRejectsDuplicateOrUnknownProperties()
    {
        var reordered = CreateContext();
        reordered.Platform.SeedFile(reordered.Paths.OwnershipLedger, Encoding.UTF8.GetBytes("{\"change_sets\":[],\"schema_version\":1}"));
        Assert.Empty(reordered.Store.Load().ChangeSets);

        foreach (var invalid in new[]
        {
            "{\"schema_version\":1,\"schema_version\":1,\"change_sets\":[]}",
            "{\"schema_version\":1,\"change_sets\":[],\"marker\":\"PREVIOUS_SECRET_MARKER\"}",
        })
        {
            var context = CreateContext();
            context.Platform.SeedFile(context.Paths.OwnershipLedger, Encoding.UTF8.GetBytes(invalid));
            var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load());
            Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
            Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void LedgerValidation_RejectsUnknownOutcomeAndInvalidPlannedOwnershipFieldsBeforeWriting()
    {
        var invalidRows = new[]
        {
            CreateAppliedChangeSet() with { OutcomeCode = "synthetic_unknown_code" },
            CreatePlannedChangeSet() with { OutcomeCode = SetupCodes.PlanReady },
            CreatePlannedChangeSet() with { Targets = [CreateLedgerTarget() with { AppliedStateHash = HashB }] },
            CreatePlannedChangeSet() with { Targets = [CreateLedgerTarget() with { BackupReference = "backup-ref" }] },
            CreatePlannedChangeSet() with { Targets = [CreateLedgerTarget() with { OutcomeCode = SetupCodes.PlanReady }] },
            CreatePlannedChangeSet() with { Targets = [CreateLedgerTarget() with { RollbackStatus = SetupLedgerRollbackStatus.Pending }] },
            CreatePlannedChangeSet() with { Targets = [CreateLedgerTarget() with { OwningAdapter = "other-adapter" }] },
            CreatePlannedChangeSet() with { Targets = [CreateLedgerTarget() with { ToolVersion = "9.9.9" }] },
            CreatePlannedChangeSet() with
            {
                Targets =
                [
                    CreateLedgerTarget() with
                    {
                        Members =
                        [
                            new SetupLedgerMember("duplicate.setting", SetupOperation.Add),
                            new SetupLedgerMember("duplicate.setting", SetupOperation.Replace),
                        ],
                    },
                ],
            },
        };

        foreach (var row in invalidRows)
        {
            var context = CreateContext();
            Assert.Throws<FormatException>(() => context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [row])));
            Assert.DoesNotContain(context.Platform.Operations, operation => operation.StartsWith("file.write:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Mutations_RequireLiveLockForTheSamePlatformAndRuntime()
    {
        var context = CreateContext();
        using var contended = SetupLock.TryAcquire(context.Platform, context.Paths);
        Assert.False(contended.Acquired);
        Assert.Equal(SetupCodes.SetupBusy, contended.Code);

        var foreignPlatform = new SetupTestPlatform(CreatedAt, "C:\\foreign-runtime");
        var foreignPaths = new SetupRuntimePaths(foreignPlatform);
        using var foreignAcquire = SetupLock.TryAcquire(foreignPlatform, foreignPaths);

        var foreign = Assert.Throws<SetupStorageException>(() =>
            context.PlanStore.Create(foreignAcquire.Lock!, CreatePlan()));
        Assert.Equal(SetupStorageCodes.LockRequired, foreign.Code);

        context.Lock.Dispose();
        var disposed = Assert.Throws<SetupStorageException>(() =>
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [])));
        Assert.Equal(SetupStorageCodes.LockRequired, disposed.Code);
        Assert.DoesNotContain(context.Platform.Operations, operation => operation.Contains(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlanCreate_ExternalDisposeKeepsExclusiveLockUntilAtomicMoveExits()
    {
        using var disposeRequested = new ManualResetEventSlim();
        var context = CreateContext(disposeRequested.Set);
        var destination = context.Paths.GetPlan(ChangeSetId);
        using var barrier = context.Platform.AddBarrier($"file.move:{destination}.tmp->{destination}");

        var write = Task.Run(() => context.PlanStore.Create(context.Lock, CreatePlan()));
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
            context.PlanStore.Delete(context.Lock, ChangeSetId)).Code);
        using var reacquired = SetupLock.TryAcquire(context.Platform, context.Paths);
        Assert.True(reacquired.Acquired);
    }

    [Fact]
    public async Task LedgerSave_ExternalDisposeKeepsExclusiveLockUntilAtomicReplaceExits()
    {
        using var disposeRequested = new ManualResetEventSlim();
        var context = CreateContext(disposeRequested.Set);
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []));
        var destination = context.Paths.OwnershipLedger;
        using var barrier = context.Platform.AddBarrier($"file.replace:{destination}.tmp->{destination}");

        var write = Task.Run(() => context.Store.Save(
            context.Lock,
            new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));
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
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []))).Code);
        using var reacquired = SetupLock.TryAcquire(context.Platform, context.Paths);
        Assert.True(reacquired.Acquired);
    }

    [Fact]
    public async Task PersistPlannedChangeSet_DisposeRequestBetweenPlanAndLedgerDoesNotInterruptTheTransaction()
    {
        using var disposeRequested = new ManualResetEventSlim();
        var context = CreateContext(disposeRequested.Set);
        using var barrier = context.Platform.AddBarrier("checkpoint:after-plan-persisted-before-ledger");

        var write = Task.Run(() => context.Store.PersistPlannedChangeSet(
            context.Lock, CreatePlan(), CreatePlannedChangeSet()));
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
        Assert.NotNull(context.PlanStore.Load(ChangeSetId));
        Assert.Single(context.Store.Load().ChangeSets);
        Assert.Equal(SetupStorageCodes.LockRequired, Assert.Throws<SetupStorageException>(() =>
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []))).Code);
    }

    [Fact]
    public void PlanCreate_WriteFailureUnwindsOperationButKeepsTokenHeld()
    {
        var context = CreateContext();
        var destination = context.Paths.GetPlan(ChangeSetId);
        context.Platform.InjectFault($"file.flush:{destination}.tmp", new IOException("PRIVATE_MARKER"));

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Create(context.Lock, CreatePlan()));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []));
        using var contended = SetupLock.TryAcquire(context.Platform, context.Paths);
        Assert.False(contended.Acquired);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("move", false)]
    [InlineData("move", true)]
    public void PlanCreate_FaultMatrixLeavesExactDurableStateAndNoTemporaryFile(string boundary, bool afterEffect)
    {
        var context = CreateContext();
        var destination = context.Paths.GetPlan(ChangeSetId);
        var operation = FileOperation(boundary, destination, destination + ".tmp");
        InjectFault(context.Platform, operation, afterEffect);

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Create(context.Lock, CreatePlan()));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.False(context.Platform.FileSystem.FileExists(destination + ".tmp"));
        var reopened = new SetupPlanStore(context.Platform, context.Paths).Load(ChangeSetId);
        if (boundary == "move" && afterEffect)
        {
            Assert.Equivalent(CreatePlan(), reopened, strict: true);
        }
        else
        {
            Assert.Null(reopened);
        }
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("move", false)]
    [InlineData("move", true)]
    public void FirstLedgerSave_FaultMatrixLeavesExactDurableStateAndNoTemporaryFile(string boundary, bool afterEffect)
    {
        var context = CreateContext();
        var destination = context.Paths.OwnershipLedger;
        var operation = FileOperation(boundary, destination, destination + ".tmp");
        InjectFault(context.Platform, operation, afterEffect);

        Assert.Throws<SetupStorageException>(() =>
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));

        Assert.False(context.Platform.FileSystem.FileExists(destination + ".tmp"));
        var reopened = new SetupLedgerStore(context.Platform, context.Paths, context.PlanStore).Load();
        Assert.Equal(boundary == "move" && afterEffect ? 1 : 0, reopened.ChangeSets.Count);
    }

    [Theory]
    [InlineData("write", false)]
    [InlineData("write", true)]
    [InlineData("flush", false)]
    [InlineData("flush", true)]
    [InlineData("replace", false)]
    [InlineData("replace", true)]
    public void LedgerReplace_FaultMatrixPreservesOldOrDurableNewStateAndCleansTemporaryFile(string boundary, bool afterEffect)
    {
        var context = CreateContext();
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []));
        var destination = context.Paths.OwnershipLedger;
        var operation = FileOperation(boundary, destination, destination + ".tmp");
        InjectFault(context.Platform, operation, afterEffect);

        Assert.Throws<SetupStorageException>(() =>
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));

        Assert.False(context.Platform.FileSystem.FileExists(destination + ".tmp"));
        var reopened = new SetupLedgerStore(context.Platform, context.Paths, context.PlanStore).Load();
        Assert.Equal(boundary == "replace" && afterEffect ? 1 : 0, reopened.ChangeSets.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PersistPlannedChangeSet_AfterLedgerCommitFaultPreservesReferencedPlan(bool replacingExistingLedger)
    {
        var context = CreateContext();
        if (replacingExistingLedger)
        {
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []));
        }

        var destination = context.Paths.OwnershipLedger;
        var boundary = replacingExistingLedger ? "replace" : "move";
        var operation = FileOperation(boundary, destination, destination + ".tmp");
        context.Platform.InjectAfterEffectFault(operation, new IOException("PREVIOUS_SECRET_MARKER"));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(context.Lock, CreatePlan(), CreatePlannedChangeSet()));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.NotNull(context.PlanStore.Load(ChangeSetId));
        Assert.Single(new SetupLedgerStore(context.Platform, context.Paths, context.PlanStore).Load().ChangeSets);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PersistPlannedChangeSet_UnreadableLedgerAfterAmbiguousCommitPreservesPlanFailClosed()
    {
        var context = CreateContext();
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []));
        var destination = context.Paths.OwnershipLedger;
        var operation = FileOperation("replace", destination, destination + ".tmp");
        context.Platform.InjectAfterEffectFault(
            operation,
            new IOException("PREVIOUS_SECRET_MARKER"),
            () => context.Platform.SeedFile(destination, Encoding.UTF8.GetBytes("corrupt-PREVIOUS_SECRET_MARKER")));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(context.Lock, CreatePlan(), CreatePlannedChangeSet()));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.NotNull(context.PlanStore.Load(ChangeSetId));
        var corrupt = Assert.Throws<SetupStorageException>(() => context.Store.Load());
        Assert.Equal(SetupCodes.LedgerCorrupt, corrupt.Code);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", corrupt.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SystemFileSystem_AtomicReplacementSurvivesCloseAndReopen()
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cao-setup-storage-{Guid.NewGuid():N}");
        try
        {
            var firstPlatform = new CopilotAgentObservability.ConfigCli.Setup.Platform.SystemSetupPlatform(localApplicationData: temporaryRoot);
            var firstPaths = new SetupRuntimePaths(firstPlatform);
            var firstPlans = new SetupPlanStore(firstPlatform, firstPaths);
            var firstStore = new SetupLedgerStore(firstPlatform, firstPaths, firstPlans);
            using (var acquired = SetupLock.TryAcquire(firstPlatform, firstPaths))
            {
                firstStore.Save(acquired.Lock!, new SetupOwnershipLedger(1, []));
                firstStore.Save(acquired.Lock!, new SetupOwnershipLedger(1, [CreateAppliedChangeSet()]));
            }

            var reopenedPlatform = new CopilotAgentObservability.ConfigCli.Setup.Platform.SystemSetupPlatform(localApplicationData: temporaryRoot);
            var reopenedPaths = new SetupRuntimePaths(reopenedPlatform);
            var reopenedPlans = new SetupPlanStore(reopenedPlatform, reopenedPaths);
            var reopened = new SetupLedgerStore(reopenedPlatform, reopenedPaths, reopenedPlans).Load();

            Assert.Equivalent(new SetupOwnershipLedger(1, [CreateAppliedChangeSet()]), reopened, strict: true);
            Assert.False(File.Exists(reopenedPaths.OwnershipLedger + ".tmp"));
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    private static StorageContext CreateContext(Action? disposeRequestedObserver = null)
    {
        var platform = new SetupTestPlatform(CreatedAt);
        var paths = new SetupRuntimePaths(platform);
        var planStore = new SetupPlanStore(platform, paths);
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

        return new StorageContext(platform, paths, setupLock, planStore, new SetupLedgerStore(platform, paths, planStore));
    }

    private static SetupPrivatePlan CreatePlan() => new(
        1,
        ChangeSetId,
        "github-copilot",
        "vscode",
        CreatedAt,
        "1.2.3",
        [
            new SetupPrivatePlanTarget(
                RecordId,
                SetupTargetKind.Json,
                "C:\\Synthetic\\settings.json",
                HashA,
                "{\"github.copilot.chat.otel.enabled\":\"DESIRED_VALUE_MARKER\"}",
                [new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", SetupOperation.Replace, "DESIRED_VALUE_MARKER")]),
        ]);

    private static SetupPrivatePlan CreatePlanWithSingleMember(
        SetupTargetKind targetKind,
        SetupOperation operation,
        string? desiredValue) => new(
        1,
        ChangeSetId,
        "github-copilot",
        "vscode",
        CreatedAt,
        "1.2.3",
        [
            new SetupPrivatePlanTarget(
                RecordId,
                targetKind,
                targetKind == SetupTargetKind.Env ? "current-user-env" : "C:\\Synthetic\\settings.json",
                HashA,
                "desired-state",
                [new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", operation, desiredValue)]),
        ]);

    private static SetupLedgerChangeSet CreatePlannedChangeSet() => new(
        ChangeSetId,
        "github-copilot",
        "vscode",
        CreatedAt,
        CreatedAt,
        "1.2.3",
        null,
        SetupChangeSetState.Planned,
        [CreateLedgerTarget()]);

    private static SetupLedgerChangeSet CreateAppliedChangeSet() => CreatePlannedChangeSet() with
    {
        State = SetupChangeSetState.Applied,
        OutcomeCode = SetupCodes.ApplySucceeded,
        Targets =
        [
            CreateLedgerTarget() with
            {
                AppliedStateHash = HashB,
                BackupReference = "backup-00000000-0000-7000-8000-000000000201",
                RollbackStatus = SetupLedgerRollbackStatus.Pending,
                OutcomeCode = SetupCodes.ApplySucceeded,
            },
        ],
    };

    private static SetupLedgerTarget CreateLedgerTarget() => new(
        RecordId,
        SetupTargetKind.Json,
        "vscode-user-settings",
        "github-copilot",
        [new SetupLedgerMember("github.copilot.chat.otel.enabled", SetupOperation.Replace)],
        HashA,
        null,
        null,
        null,
        SetupLedgerRollbackStatus.NotAvailable,
        SetupRestartRequirement.RestartVsCode,
        "1.2.3");

    private static int IndexOf(IReadOnlyList<string> values, string expected) =>
        values.Select((value, index) => (value, index)).Single(pair => pair.value == expected).index;

    private static string FileOperation(string boundary, string destination, string temporary) => boundary switch
    {
        "write" => $"file.write:{temporary}",
        "flush" => $"file.flush:{temporary}",
        "move" => $"file.move:{temporary}->{destination}",
        "replace" => $"file.replace:{temporary}->{destination}",
        _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
    };

    private static void InjectFault(SetupTestPlatform platform, string operation, bool afterEffect)
    {
        if (afterEffect)
        {
            platform.InjectAfterEffectFault(operation, new IOException("PREVIOUS_SECRET_MARKER"));
        }
        else
        {
            platform.InjectFault(operation, new IOException("PREVIOUS_SECRET_MARKER"));
        }
    }

    private static string ReplacePropertyValue(string json, string propertyName, string replacement)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty(propertyName, out var property))
        {
            return json.Replace($"\"{propertyName}\": {property.GetRawText()}", $"\"{propertyName}\": {replacement}", StringComparison.Ordinal);
        }

        if (propertyName == "adapter")
        {
            return json.Replace("\"adapter\": \"github-copilot\"", $"\"adapter\": {replacement}", StringComparison.Ordinal);
        }

        throw new ArgumentOutOfRangeException(nameof(propertyName));
    }

    private sealed record StorageContext(
        SetupTestPlatform Platform,
        SetupRuntimePaths Paths,
        SetupLock Lock,
        SetupPlanStore PlanStore,
        SetupLedgerStore Store);
}

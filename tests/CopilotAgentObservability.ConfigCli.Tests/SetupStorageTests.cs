using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
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
        context.Store.PersistPlannedChangeSet(CreatePlan(), CreatePlannedChangeSet());

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
            context.Store.PersistPlannedChangeSet(CreatePlan(), CreatePlannedChangeSet()));

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
        context.PlanStore.Create(CreatePlan());

        var ledger = context.Store.Load();

        Assert.Equal(1, ledger.SchemaVersion);
        Assert.Empty(ledger.ChangeSets);
    }

    [Fact]
    public void Load_NonTerminalLedgerRowWithoutPlanReportsFixedRecoveryRequired()
    {
        var context = CreateContext();
        context.Store.Save(new SetupOwnershipLedger(1, [CreatePlannedChangeSet()]));
        context.Platform.FileSystem.DeleteFile(context.Paths.GetPlan(ChangeSetId));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.Load());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void PlanCreate_IsImmutableAndRejectsOverwriteWithoutChangingBytes()
    {
        var context = CreateContext();
        context.PlanStore.Create(CreatePlan());
        var original = context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId));

        var changed = CreatePlan() with { ToolVersion = "9.9.9" };
        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Create(changed));

        Assert.Equal(SetupStorageCodes.PlanAlreadyExists, exception.Code);
        Assert.Equal(original, context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));
    }

    [Fact]
    public void Plan_RoundTripsExactPrivateLocationsAndDesiredStateWithoutPreviousValueField()
    {
        var context = CreateContext();
        var plan = CreatePlan();

        context.PlanStore.Create(plan);
        var reopened = new SetupPlanStore(context.Platform, context.Paths).Load(ChangeSetId);
        var json = Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));

        Assert.Equivalent(plan, reopened, strict: true);
        Assert.Contains("C:\\\\Synthetic\\\\settings.json", json, StringComparison.Ordinal);
        Assert.Contains("DESIRED_VALUE_MARKER", json, StringComparison.Ordinal);
        Assert.DoesNotContain("previous", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", json, StringComparison.Ordinal);
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

        context.PlanStore.Create(CreatePlan());
        context.Store.Save(ledger);
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
        context.Store.Save(new SetupOwnershipLedger(1, []));
        var original = context.Platform.ReadSeededFile(context.Paths.OwnershipLedger);
        context.Platform.InjectFault($"file.replace:{context.Paths.OwnershipLedger}.tmp->{context.Paths.OwnershipLedger}", new IOException("synthetic"));

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.Save(new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));

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
        var ledgerRow = CreatePlannedChangeSet() with { Adapter = "other-adapter" };

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(CreatePlan(), ledgerRow));

        Assert.Equal(SetupStorageCodes.PlanLedgerMismatch, exception.Code);
        Assert.Empty(context.Platform.Operations);
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

    private static StorageContext CreateContext()
    {
        var platform = new SetupTestPlatform(CreatedAt);
        var paths = new SetupRuntimePaths(platform);
        var planStore = new SetupPlanStore(platform, paths);
        return new StorageContext(platform, paths, planStore, new SetupLedgerStore(platform, paths, planStore));
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

    private sealed record StorageContext(
        SetupTestPlatform Platform,
        SetupRuntimePaths Paths,
        SetupPlanStore PlanStore,
        SetupLedgerStore Store);
}

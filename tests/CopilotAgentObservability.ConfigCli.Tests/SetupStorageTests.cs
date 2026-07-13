using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
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
    public void PersistPlannedChangeSet_RejectsStaleOutcomeAsTheInitialPlanRow()
    {
        var context = CreateContext();
        var staleObservation = CreatePlannedChangeSet() with { OutcomeCode = SetupCodes.StalePlan };

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(context.Lock, CreatePlan(), staleObservation));

        Assert.Equal(SetupStorageCodes.PlanLedgerMismatch, exception.Code);
        Assert.False(context.Platform.FileSystem.FileExists(context.Paths.GetPlan(ChangeSetId)));
        Assert.False(context.Platform.FileSystem.FileExists(context.Paths.OwnershipLedger));
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

    [Fact]
    public void LedgerSerialization_WritesRequiredRepositorySafeStatusProjection()
    {
        using var document = JsonDocument.Parse(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));

        var projection = document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0].GetProperty("status_projection");
        Assert.Equal(
            ["detected", "detected_version", "operation", "effective_source", "endpoint", "expected_result", "guidance", "changes"],
            projection.EnumerateObject().Select(property => property.Name));
        Assert.True(projection.GetProperty("detected").GetBoolean());
        Assert.Equal("1.128.0", projection.GetProperty("detected_version").GetString());
        Assert.Equal("replace", projection.GetProperty("operation").GetString());
        Assert.Equal("user_setting", projection.GetProperty("effective_source").GetString());
        Assert.Equal("http://127.0.0.1:4320", projection.GetProperty("endpoint").GetString());
        Assert.Equal(JsonValueKind.Null, projection.GetProperty("guidance").ValueKind);
        Assert.Equal("present_different", projection.GetProperty("changes")[0].GetProperty("previous_state").GetString());

        var serialized = Encoding.UTF8.GetString(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [CreateAppliedChangeSet()])));
        Assert.DoesNotContain("target_location", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("backup-00000000", projection.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("sample", projection.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerLoad_RoundTripsExactStatusProjectionAndLifecycleUpdatesPreserveIt()
    {
        var context = CreateContext();
        var planned = CreatePlannedChangeSet();
        var expectedProjection = planned.Targets[0].StatusProjection;
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [planned]));
        var reopened = context.Store.LoadForRecovery();
        Assert.Equivalent(expectedProjection, reopened.ChangeSets[0].Targets[0].StatusProjection, strict: true);

        var applied = reopened.ChangeSets[0] with
        {
            UpdatedAt = CreatedAt.AddMinutes(1),
            State = SetupChangeSetState.Applied,
            OutcomeCode = SetupCodes.ApplySucceeded,
            Targets =
            [
                reopened.ChangeSets[0].Targets[0] with
                {
                    AppliedStateHash = HashB,
                    BackupReference = "backup-ref",
                    OutcomeCode = SetupCodes.ApplySucceeded,
                    RollbackStatus = SetupLedgerRollbackStatus.Pending,
                },
            ],
        };
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [applied]));

        var afterLifecycleUpdate = context.Store.LoadForRecovery();
        Assert.Equivalent(expectedProjection, afterLifecycleUpdate.ChangeSets[0].Targets[0].StatusProjection, strict: true);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("member_mismatch")]
    [InlineData("unsafe_version")]
    [InlineData("unsafe_endpoint")]
    [InlineData("surface_mismatch")]
    [InlineData("contract_version")]
    [InlineData("unknown_manifest_field")]
    [InlineData("unknown_support")]
    [InlineData("unknown_stability")]
    [InlineData("provenance_order")]
    [InlineData("completeness_status_order")]
    [InlineData("completeness_reason_order")]
    [InlineData("unknown_manifest_code")]
    [InlineData("wrong_manifest_adapter")]
    [InlineData("aggregate_operation_mismatch")]
    [InlineData("member_operation_mismatch")]
    [InlineData("malformed")]
    public void LedgerLoad_InvalidStatusProjectionFailsClosed(string mutation)
    {
        var context = CreateContext();
        var row = CreateAppliedChangeSetWithHistoricalManifest();
        var root = JsonNode.Parse(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [row])))!.AsObject();
        var target = root["change_sets"]![0]!["targets"]![0]!.AsObject();
        var projection = target["status_projection"]!.AsObject();
        switch (mutation)
        {
            case "missing":
                target.Remove("status_projection");
                break;
            case "unknown":
                projection["unknown"] = "PREVIOUS_SECRET_MARKER";
                break;
            case "member_mismatch":
                projection["changes"]![0]!["setting_key"] = "different.setting";
                break;
            case "unsafe_version":
                projection["detected_version"] = "1.0.0+C:/PREVIOUS_SECRET_MARKER";
                break;
            case "unsafe_endpoint":
                projection["endpoint"] = "http://token@127.0.0.1:4320";
                break;
            case "surface_mismatch":
                projection["expected_result"]!["source_surface"] = "github-copilot-cli";
                break;
            case "contract_version":
                projection["expected_result"]!["contract_version"] = "v2";
                break;
            case "unknown_manifest_field":
                projection["expected_result"]!["unknown"] = "PREVIOUS_SECRET_MARKER";
                break;
            case "unknown_support":
                projection["expected_result"]!["support_status"] = "invented";
                break;
            case "unknown_stability":
                projection["expected_result"]!["stability"] = "invented";
                break;
            case "provenance_order":
                SwapFirstTwo(projection["expected_result"]!["provenance"]!["required_keys"]!.AsArray());
                break;
            case "completeness_status_order":
                SwapFirstTwo(projection["expected_result"]!["completeness"]!["statuses"]!.AsArray());
                break;
            case "completeness_reason_order":
                SwapFirstTwo(projection["expected_result"]!["completeness"]!["reason_codes"]!.AsArray());
                break;
            case "unknown_manifest_code":
                projection["expected_result"]!["errors"]!["availability"] = "invented";
                break;
            case "wrong_manifest_adapter":
                projection["expected_result"]!["source_adapter"] = "otel-http";
                break;
            case "aggregate_operation_mismatch":
                projection["operation"] = "add";
                break;
            case "member_operation_mismatch":
                projection["changes"]![0]!["operation"] = "add";
                break;
            case "malformed":
                projection["changes"] = "not-an-array";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        context.Platform.SeedFile(context.Paths.OwnershipLedger, Encoding.UTF8.GetBytes(root.ToJsonString()));
        var exception = Assert.Throws<SetupStorageException>(() => context.Store.LoadForRecovery());

        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
        Assert.DoesNotContain("PREVIOUS_SECRET_MARKER", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerLoad_HistoricalSchemaSafeManifestNeedNotEqualCurrentEmbeddedManifest()
    {
        var context = CreateContext();
        var expected = CreateAppliedChangeSetWithHistoricalManifest();
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, [expected]));

        var reopened = context.Store.LoadForRecovery();

        var manifest = reopened.ChangeSets[0].Targets[0].StatusProjection.ExpectedResult!.Value;
        Assert.Equal("preview", manifest.GetProperty("stability").GetString());
        Assert.Equal("planned", manifest.GetProperty("support_status").GetString());
        Assert.False(SourceCapabilityManifestLoader.MatchesCanonical(manifest));
    }

    [Fact]
    public void LedgerLoad_DuplicateHistoricalManifestPropertyFailsClosed()
    {
        var context = CreateContext();
        var json = Encoding.UTF8.GetString(SetupLedgerStore.Serialize(
            new SetupOwnershipLedger(1, [CreateAppliedChangeSetWithHistoricalManifest()])))
            .Replace("\"contract_version\": \"v1\"", "\"contract_version\": \"v1\",\n              \"contract_version\": \"v1\"", StringComparison.Ordinal);
        context.Platform.SeedFile(context.Paths.OwnershipLedger, Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<SetupStorageException>(() => context.Store.LoadForRecovery());

        Assert.Equal(SetupCodes.LedgerCorrupt, exception.Code);
    }

    [Fact]
    public void LedgerValidation_StatusProjectionMemberOrderMustMatchLedgerOrder()
    {
        var members = new[]
        {
            new SetupLedgerMember("setting-a", SetupOperation.Add),
            new SetupLedgerMember("setting-b", SetupOperation.Replace),
        };
        var changes = members.Select(member => new SetupMemberChangeResult(
            member.SettingKey, member.Operation, "present_different", "configured_loopback", "none", false)).ToArray();
        var target = CreateLedgerTarget() with
        {
            Members = members,
            StatusProjection = CreateStatusProjection() with
            {
                Operation = SetupOperation.Mixed,
                Changes = [changes[1], changes[0]],
            },
        };

        Assert.Throws<FormatException>(() => SetupLedgerStore.Serialize(
            new SetupOwnershipLedger(1, [CreatePlannedChangeSet() with { Targets = [target] }])));
    }

    [Fact]
    public void LedgerSerialization_GuidanceStoresNoSampleAndRehydratesOnlyTheFixedPublicContract()
    {
        var guidanceTarget = CreateLedgerTarget() with
        {
            TargetKind = SetupTargetKind.Guidance,
            TargetLabel = "app-sdk-guidance",
            Members = [],
            RestartRequirement = SetupRestartRequirement.None,
            StatusProjection = new SetupStatusProjection(
                false, null, SetupOperation.NoOp, null, null, null,
                new SetupStatusGuidance("caller_managed_sample", "dotnet"), []),
        };
        var changeSet = CreatePlannedChangeSet() with { SelectedTarget = "app-sdk", Targets = [guidanceTarget] };

        var bytes = SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [changeSet]));
        using var document = JsonDocument.Parse(bytes);
        var storedGuidance = document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0]
            .GetProperty("status_projection").GetProperty("guidance");

        Assert.Equal(["kind", "language"], storedGuidance.EnumerateObject().Select(property => property.Name));
        Assert.False(storedGuidance.TryGetProperty("sample", out _));
        var rehydrated = SetupContractValidator.RehydrateStatusGuidance(guidanceTarget.StatusProjection.Guidance!);
        Assert.Contains("OtlpEndpoint", rehydrated.Sample, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", rehydrated.Sample, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other_kind", "dotnet")]
    [InlineData("caller_managed_sample", "typescript")]
    public void LedgerValidation_GuidanceMetadataMustMatchFixedContract(string kind, string language)
    {
        var guidanceTarget = CreateGuidanceLedgerTarget() with
        {
            StatusProjection = CreateGuidanceLedgerTarget().StatusProjection with
            {
                Guidance = new SetupStatusGuidance(kind, language),
            },
        };
        var row = CreatePlannedChangeSet() with { SelectedTarget = "app-sdk", Targets = [guidanceTarget] };

        Assert.Throws<FormatException>(() => SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [row])));
        Assert.Throws<InvalidOperationException>(() => SetupContractValidator.RehydrateStatusGuidance(
            guidanceTarget.StatusProjection.Guidance!));
    }

    [Fact]
    public void LedgerSerialization_LargestLegalSingleChangeSetFitsRetainedCompleteLedgerCap()
    {
        var largest = CreateLargestLegalChangeSet();

        var bytes = SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [largest]));

        Assert.True(bytes.Length < SetupLedgerStore.MaximumLedgerBytes, $"Serialized legal boundary was {bytes.Length} bytes.");
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(16, document.RootElement.GetProperty("change_sets")[0].GetProperty("targets").GetArrayLength());
        Assert.Equal(32, document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0]
            .GetProperty("status_projection").GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public void Save_WhenCompleteLedgerWouldExceedRetainedCap_DoesNotReplaceDurableLedger()
    {
        var context = CreateContext();
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, []));
        var durable = context.Platform.FileSystem.ReadAllBytes(context.Paths.OwnershipLedger);
        var largest = CreateLargestLegalChangeSet();
        var rows = Enumerable.Range(0, 8).Select(index => largest with
        {
            ChangeSetId = Guid.Parse($"00000000-0000-7000-8000-{index + 900:D12}"),
        }).ToArray();

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.Save(context.Lock, new SetupOwnershipLedger(1, rows)));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.Equal(durable, context.Platform.FileSystem.ReadAllBytes(context.Paths.OwnershipLedger));
    }

    [Fact]
    public void Serialize_WhenCompleteLedgerWouldExceedRetainedCap_FailsBeforeReturningBytes()
    {
        var largest = CreateLargestLegalChangeSet();
        var rows = Enumerable.Range(0, 8).Select(index => largest with
        {
            ChangeSetId = Guid.Parse($"00000000-0000-7000-8000-{index + 920:D12}"),
        }).ToArray();

        var exception = Assert.Throws<SetupStorageException>(() =>
            SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, rows)));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
    }

    [Fact]
    public void PersistPlannedChangeSet_WhenAppendWouldExceedRetainedCap_PreservesLedgerAndCleansPlan()
    {
        var context = CreateContext();
        var planned = CreatePlannedChangeSet();
        var rows = new List<SetupLedgerChangeSet>();
        for (var index = 1; ; index++)
        {
            var filler = CreateAppliedChangeSet() with
            {
                ChangeSetId = Guid.Parse($"00000000-0000-7000-8000-{index + 1000:D12}"),
            };
            var candidate = new SetupOwnershipLedger(1, [.. rows, filler, planned]);
            try
            {
                _ = SetupLedgerStore.Serialize(candidate);
                rows.Add(filler);
            }
            catch (SetupStorageException overflow) when (overflow.Code == SetupStorageCodes.WriteFailed)
            {
                _ = SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [.. rows, filler]));
                rows.Add(filler);
                break;
            }
        }

        Assert.NotEmpty(rows);
        context.Store.Save(context.Lock, new SetupOwnershipLedger(1, rows));
        var durable = context.Platform.FileSystem.ReadAllBytes(context.Paths.OwnershipLedger);

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(context.Lock, CreatePlan(), planned));

        Assert.Equal(SetupStorageCodes.WriteFailed, exception.Code);
        Assert.Equal(durable, context.Platform.FileSystem.ReadAllBytes(context.Paths.OwnershipLedger));
        Assert.Null(context.PlanStore.Load(ChangeSetId));
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
    public void LedgerValidation_AcceptsOnlyPristineOrStalePlannedOutcomeTimestampPairs()
    {
        var pristine = CreatePlannedChangeSet();
        var staleAtCreation = pristine with { OutcomeCode = SetupCodes.StalePlan };
        var staleAfterCreation = staleAtCreation with { UpdatedAt = CreatedAt.AddMinutes(1) };

        Assert.NotEmpty(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [pristine])));
        Assert.NotEmpty(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [staleAtCreation])));
        Assert.NotEmpty(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [staleAfterCreation])));

        foreach (var invalid in new[]
        {
            pristine with { UpdatedAt = CreatedAt.AddMinutes(1) },
            pristine with { OutcomeCode = SetupCodes.PlanReady },
            staleAfterCreation with { UpdatedAt = CreatedAt.AddTicks(-1) },
            staleAfterCreation with
            {
                Targets = [CreateLedgerTarget() with { OutcomeCode = SetupCodes.StalePlan }],
            },
        })
        {
            Assert.Throws<FormatException>(() => SetupLedgerStore.Serialize(
                new SetupOwnershipLedger(1, [invalid])));
        }
    }

    [Fact]
    public void PlanLedgerValidation_AcceptsStaleObservationForRetryButRejectsAgedPristineRow()
    {
        var stale = CreatePlannedChangeSet() with
        {
            UpdatedAt = CreatedAt.AddMinutes(1),
            OutcomeCode = SetupCodes.StalePlan,
        };

        SetupStorageValidation.ValidatePlanAndLedger(CreatePlan(), stale);

        var pristineWithLaterTimestamp = stale with { OutcomeCode = null };
        Assert.Throws<FormatException>(() =>
            SetupStorageValidation.ValidatePlanAndLedger(CreatePlan(), pristineWithLaterTimestamp));
    }

    [Fact]
    public void LedgerValidation_AllNoOpWritableTargetOwnsNoBackupOrAppliedHash()
    {
        var noOpTarget = CreateLedgerTarget() with
        {
            Members = [new SetupLedgerMember("github.copilot.chat.otel.enabled", SetupOperation.NoOp)],
            StatusProjection = CreateStatusProjection() with
            {
                Operation = SetupOperation.NoOp,
                Changes =
                [
                    new SetupMemberChangeResult(
                        "github.copilot.chat.otel.enabled", SetupOperation.NoOp,
                        "present_same", "configured_loopback", "none", false),
                ],
            },
        };
        var valid = CreatePlannedChangeSet() with
        {
            State = SetupChangeSetState.NoChanges,
            OutcomeCode = SetupCodes.NoChanges,
            Targets = [noOpTarget],
        };
        Assert.NotEmpty(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [valid])));

        foreach (var invalidTarget in new[]
        {
            noOpTarget with { AppliedStateHash = HashB },
            noOpTarget with { BackupReference = "backup-ref" },
            noOpTarget with { RollbackStatus = SetupLedgerRollbackStatus.Pending },
        })
        {
            Assert.Throws<FormatException>(() => SetupLedgerStore.Serialize(
                new SetupOwnershipLedger(1, [valid with { Targets = [invalidTarget] }])));
        }
    }

    [Theory]
    [MemberData(nameof(NoWriteLifecycleRows))]
    public void LedgerValidation_NoWriteTargetsNeverOwnRollbackAcrossLifecycle(
        SetupChangeSetState state,
        bool guidance)
    {
        var target = guidance ? CreateGuidanceLedgerTarget() : CreateAllNoOpLedgerTarget();
        var valid = CreatePlannedChangeSet() with
        {
            State = state,
            OutcomeCode = state == SetupChangeSetState.Planned ? null : SetupCodes.NoChanges,
            Targets = [target],
        };
        Assert.NotEmpty(SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [valid])));

        foreach (var invalid in new[]
        {
            target with { AppliedStateHash = HashB },
            target with { BackupReference = "backup-ref" },
            target with { RollbackStatus = SetupLedgerRollbackStatus.Pending },
        })
        {
            Assert.Throws<FormatException>(() => SetupLedgerStore.Serialize(
                new SetupOwnershipLedger(1, [valid with { Targets = [invalid] }])));
        }
    }

    public static TheoryData<SetupChangeSetState, bool> NoWriteLifecycleRows => new()
    {
        { SetupChangeSetState.Planned, false }, { SetupChangeSetState.Applying, false },
        { SetupChangeSetState.Applied, false }, { SetupChangeSetState.NoChanges, false },
        { SetupChangeSetState.Compensating, false }, { SetupChangeSetState.Restored, false },
        { SetupChangeSetState.RollingBack, false }, { SetupChangeSetState.Partial, false },
        { SetupChangeSetState.RolledBack, false }, { SetupChangeSetState.Planned, true },
        { SetupChangeSetState.Applying, true }, { SetupChangeSetState.Applied, true },
        { SetupChangeSetState.NoChanges, true }, { SetupChangeSetState.Compensating, true },
        { SetupChangeSetState.Restored, true }, { SetupChangeSetState.RollingBack, true },
        { SetupChangeSetState.Partial, true }, { SetupChangeSetState.RolledBack, true },
    };

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

    private static SetupLedgerChangeSet CreateAppliedChangeSetWithHistoricalManifest()
    {
        var manifestPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "specifications", "contracts",
            "source-capabilities", "v1", "manifests", "github-copilot-vscode.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["support_status"] = "planned";
        manifest["stability"] = "preview";
        using var historicalDocument = JsonDocument.Parse(manifest.ToJsonString());
        return CreateAppliedChangeSet() with
        {
            Targets =
            [
                CreateAppliedChangeSet().Targets[0] with
                {
                    StatusProjection = CreateStatusProjection() with
                    {
                        ExpectedResult = historicalDocument.RootElement.Clone(),
                    },
                },
            ],
        };
    }

    private static SetupLedgerChangeSet CreateLargestLegalChangeSet()
    {
        var version = "1.0.0+" + new string('a', 122);
        var fixedValue = "a" + new string('b', 127);
        var historicalManifest = LoadHistoricalManifest();
        var targets = Enumerable.Range(1, 16).Select(targetIndex =>
        {
            var members = Enumerable.Range(1, 32)
                .Select(memberIndex => new SetupLedgerMember(
                    $"s{memberIndex:D2}" + new string('a', 125),
                    SetupOperation.Replace))
                .ToArray();
            var changes = members.Select(member => new SetupMemberChangeResult(
                member.SettingKey,
                member.Operation,
                fixedValue,
                fixedValue,
                fixedValue,
                true)).ToArray();
            return new SetupLedgerTarget(
                Guid.Parse($"00000000-0000-7000-8000-{targetIndex:D12}"),
                SetupTargetKind.Json,
                "vscode-stable-default-user-settings",
                "github-copilot",
                members,
                HashA,
                null,
                null,
                null,
                SetupLedgerRollbackStatus.NotAvailable,
                SetupRestartRequirement.RestartVsCode,
                new SetupStatusProjection(
                    true,
                    version,
                    SetupOperation.Replace,
                    SetupEffectiveSource.UserSetting,
                    "http://127.0.0.1:65535",
                    historicalManifest,
                    null,
                    changes),
                "1.2.3");
        }).ToArray();

        return new SetupLedgerChangeSet(
            ChangeSetId,
            "github-copilot",
            new string('a', 128),
            CreatedAt,
            CreatedAt,
            "1.2.3",
            null,
            SetupChangeSetState.Planned,
            targets);
    }

    private static SetupLedgerTarget CreateLedgerTarget() => new(
        RecordId,
        SetupTargetKind.Json,
        "vscode-stable-default-user-settings",
        "github-copilot",
        [new SetupLedgerMember("github.copilot.chat.otel.enabled", SetupOperation.Replace)],
        HashA,
        null,
        null,
        null,
        SetupLedgerRollbackStatus.NotAvailable,
        SetupRestartRequirement.RestartVsCode,
        CreateStatusProjection(),
        "1.2.3");

    private static SetupStatusProjection CreateStatusProjection() => new(
        true,
        "1.128.0",
        SetupOperation.Replace,
        SetupEffectiveSource.UserSetting,
        "http://127.0.0.1:4320",
        LoadHistoricalManifest(),
        null,
        [new SetupMemberChangeResult("github.copilot.chat.otel.enabled", SetupOperation.Replace, "present_different", "configured_loopback", "none", false)]);

    private static SetupLedgerTarget CreateAllNoOpLedgerTarget()
    {
        var member = new SetupLedgerMember("github.copilot.chat.otel.enabled", SetupOperation.NoOp);
        return CreateLedgerTarget() with
        {
            Members = [member],
            StatusProjection = CreateStatusProjection() with
            {
                Operation = SetupOperation.NoOp,
                Changes = [new SetupMemberChangeResult(member.SettingKey, member.Operation, "present_same", "configured_loopback", "none", false)],
            },
        };
    }

    private static SetupLedgerTarget CreateGuidanceLedgerTarget() => CreateLedgerTarget() with
    {
        TargetKind = SetupTargetKind.Guidance,
        TargetLabel = "app-sdk-guidance",
        Members = [],
        RestartRequirement = SetupRestartRequirement.None,
        StatusProjection = new SetupStatusProjection(
            false, null, SetupOperation.NoOp, null, null, null,
            new SetupStatusGuidance("caller_managed_sample", "dotnet"), []),
    };

    private static JsonElement LoadHistoricalManifest()
    {
        var manifestPath = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "specifications", "contracts",
            "source-capabilities", "v1", "manifests", "github-copilot-vscode.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        manifest["support_status"] = "planned";
        manifest["stability"] = "preview";
        using var document = JsonDocument.Parse(manifest.ToJsonString());
        return document.RootElement.Clone();
    }

    private static void SwapFirstTwo(JsonArray values)
    {
        var first = values[0]!.DeepClone();
        values[0] = values[1]!.DeepClone();
        values[1] = first;
    }

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

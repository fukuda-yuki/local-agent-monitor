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
    public void Plan_RoundTripsTaggedDesiredStateInCanonicalPropertyOrderWithoutPreviousValueField()
    {
        var context = CreateContext();
        var plan = CreatePlan();

        context.PlanStore.Create(context.Lock, plan);
        var reopened = new SetupPlanStore(context.Platform, context.Paths).Load(ChangeSetId);
        var json = Encoding.UTF8.GetString(context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));
        using var document = JsonDocument.Parse(json);
        var desiredState = document.RootElement.GetProperty("targets")[0].GetProperty("desired_state");
        var ownedValue = desiredState.GetProperty("owned_values")[0];

        Assert.Equivalent(plan, reopened, strict: true);
        Assert.Contains("C:\\\\Synthetic\\\\settings.json", json, StringComparison.Ordinal);
        Assert.Contains("DESIRED_VALUE_MARKER", json, StringComparison.Ordinal);
        Assert.Equal(
            ["kind", "expected_state_hash", "owned_values"],
            desiredState.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["setting_key", "value_kind", "value"],
            ownedValue.EnumerateObject().Select(property => property.Name));
        Assert.Equal("jsonc_owned_values_v1", desiredState.GetProperty("kind").GetString());
        Assert.Equal(HashB, desiredState.GetProperty("expected_state_hash").GetString());
        Assert.Equal(JsonValueKind.String, ownedValue.GetProperty("value").ValueKind);
        Assert.DoesNotContain("previous", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TaggedCarrier_ExcludesUnownedPreviousStateFromTheV1StorageBoundary()
    {
        const string previousStateMarker = "PREVIOUS_SECRET_MARKER";
        var sourceBytes = Encoding.UTF8.GetBytes(
            """
            {
              // source-like JSONC retains an unrelated previous value
              "github.copilot.chat.otel.enabled": "DESIRED_VALUE_MARKER",
              "unrelated.extension.secret": "PREVIOUS_SECRET_MARKER",
            }
            """);
        var sourceText = Encoding.UTF8.GetString(sourceBytes);
        Assert.InRange(sourceBytes.Length, 1, 4096);
        Assert.Contains(previousStateMarker, sourceText, StringComparison.Ordinal);

        var members = CreatePlan().Targets[0].Members;
        var carrier = new SetupJsoncOwnedValuesDesiredState(
            HashB,
            members.Select(member => new SetupJsoncOwnedValue(
                member.SettingKey,
                "string",
                member.DesiredValue!)).ToArray());
        var plan = CreatePlan() with
        {
            Targets = [CreatePlan().Targets[0] with { DesiredState = carrier }],
        };
        var carrierJson = JsonSerializer.Serialize(carrier);
        var serializedPlan = SetupPlanStore.Serialize(plan);
        var serializedPlanText = Encoding.UTF8.GetString(serializedPlan);
        var ownershipFixture = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "Setup", "v1", "ownership-ledger.v1.json"));
        var privatePlanFixture = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "Setup", "v1", "private-plan.v1.json"));

        Assert.DoesNotContain(previousStateMarker, carrierJson, StringComparison.Ordinal);
        Assert.DoesNotContain(previousStateMarker, serializedPlanText, StringComparison.Ordinal);
        Assert.DoesNotContain(previousStateMarker, ownershipFixture, StringComparison.Ordinal);
        Assert.DoesNotContain(previousStateMarker, privatePlanFixture, StringComparison.Ordinal);

        var malformed = JsonNode.Parse(serializedPlan)!.AsObject();
        malformed["targets"]![0]!["desired_state"]!["unexpected_previous_state"] = sourceText;
        var context = CreateContext();
        context.Platform.SeedFile(
            context.Paths.GetPlan(ChangeSetId),
            Encoding.UTF8.GetBytes(malformed.ToJsonString()));

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Load(ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Message);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(previousStateMarker, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plan_RoundTripsTaggedBooleanAndStringValuesWithExactJsonTypes()
    {
        var context = CreateContext();
        var plan = CreateTaggedBooleanAndStringPlan("x");

        context.PlanStore.Create(context.Lock, plan);
        var reopened = context.PlanStore.Load(ChangeSetId);
        using var document = JsonDocument.Parse(context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));
        var values = document.RootElement.GetProperty("targets")[0].GetProperty("desired_state").GetProperty("owned_values");

        Assert.Equivalent(plan, reopened, strict: true);
        Assert.Equal("boolean", values[0].GetProperty("value_kind").GetString());
        Assert.True(values[0].GetProperty("value").GetBoolean());
        Assert.Equal(JsonValueKind.True, values[0].GetProperty("value").ValueKind);
        Assert.Equal("string", values[1].GetProperty("value_kind").GetString());
        Assert.Equal("x", values[1].GetProperty("value").GetString());
        Assert.Equal(JsonValueKind.String, values[1].GetProperty("value").ValueKind);
    }

    [Theory]
    [InlineData(SetupTargetKind.File)]
    [InlineData(SetupTargetKind.Toml)]
    [InlineData(SetupTargetKind.StartupTask)]
    public void Plan_RoundTripsCanonicalInlineArmForGenericTargets(SetupTargetKind targetKind)
    {
        var context = CreateContext();
        var plan = CreatePlanWithSingleMember(targetKind, SetupOperation.Replace, "DESIRED_VALUE_MARKER");

        context.PlanStore.Create(context.Lock, plan);
        var reopened = Assert.IsType<SetupPrivatePlan>(context.PlanStore.Load(ChangeSetId));
        using var document = JsonDocument.Parse(context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));

        Assert.IsType<SetupInlineDesiredState>(Assert.Single(reopened.Targets).DesiredState);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("targets")[0].GetProperty("desired_state").ValueKind);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2048, true)]
    [InlineData(2049, false)]
    public void Plan_TaggedStringValueEnforcesExactUtf16Boundary(int length, bool valid)
    {
        var plan = CreateTaggedBooleanAndStringPlan(new string('x', length));

        if (!valid)
        {
            Assert.Throws<FormatException>(() => SetupPlanStore.Serialize(plan));
            return;
        }

        var bytes = SetupPlanStore.Serialize(plan);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(
            length,
            document.RootElement.GetProperty("targets")[0]
                .GetProperty("desired_state")
                .GetProperty("owned_values")[1]
                .GetProperty("value")
                .GetString()!
                .Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    public void Plan_TaggedOwnedValuesAcceptsExactEntryCountBoundaries(int count)
    {
        var members = Enumerable.Range(1, count)
            .Select(index => new SetupPrivatePlanMember($"setting_{index}", SetupOperation.Replace, "configured"))
            .ToArray();
        var plan = CreatePlan() with
        {
            Targets =
            [
                CreatePlan().Targets[0] with
                {
                    Members = members,
                    DesiredState = new SetupJsoncOwnedValuesDesiredState(
                        HashB,
                        members.Select(member =>
                            new SetupJsoncOwnedValue(member.SettingKey, "string", "configured")).ToArray()),
                },
            ],
        };

        using var document = JsonDocument.Parse(SetupPlanStore.Serialize(plan));

        Assert.Equal(
            count,
            document.RootElement.GetProperty("targets")[0]
                .GetProperty("desired_state")
                .GetProperty("owned_values")
                .GetArrayLength());
    }

    [Theory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Plan_TaggedExpectedHashRejectsNonLowercaseSha256(string expectedHash)
    {
        var plan = CreatePlan() with
        {
            Targets =
            [
                CreatePlan().Targets[0] with
                {
                    DesiredState = new SetupJsoncOwnedValuesDesiredState(
                        expectedHash,
                        [new SetupJsoncOwnedValue("github.copilot.chat.otel.enabled", "string", "DESIRED_VALUE_MARKER")]),
                },
            ],
        };

        Assert.Throws<FormatException>(() => SetupPlanStore.Serialize(plan));
    }

    [Theory]
    [InlineData("desired_unknown")]
    [InlineData("desired_missing_kind")]
    [InlineData("desired_missing_hash")]
    [InlineData("desired_missing_values")]
    [InlineData("desired_duplicate")]
    [InlineData("desired_null")]
    [InlineData("desired_array")]
    [InlineData("desired_boolean")]
    [InlineData("desired_number")]
    [InlineData("wrong_tag")]
    [InlineData("uppercase_hash")]
    [InlineData("nonhex_hash")]
    [InlineData("kind_wrong_type")]
    [InlineData("hash_wrong_type")]
    [InlineData("owned_values_wrong_type")]
    [InlineData("owned_values_empty")]
    [InlineData("owned_values_33")]
    [InlineData("entry_unknown")]
    [InlineData("entry_missing_key")]
    [InlineData("entry_missing_kind")]
    [InlineData("entry_missing_value")]
    [InlineData("entry_duplicate")]
    [InlineData("setting_key_wrong_type")]
    [InlineData("value_kind_wrong_type")]
    [InlineData("wrong_value_kind")]
    [InlineData("boolean_value_string")]
    [InlineData("string_value_boolean")]
    [InlineData("string_value_null")]
    [InlineData("string_value_empty")]
    [InlineData("string_value_over_bound")]
    [InlineData("duplicate_key")]
    [InlineData("reordered_key")]
    [InlineData("missing_key")]
    [InlineData("extra_key")]
    public void PlanLoad_MalformedTaggedDesiredStateFailsRecoveryRequiredWithoutEcho(string mutation)
    {
        var context = CreateContext();
        var serialized = Encoding.UTF8.GetString(SetupPlanStore.Serialize(CreateTaggedBooleanAndStringPlan("DESIRED_VALUE_MARKER")));
        var json = MutateTaggedDesiredState(serialized, mutation);
        context.Platform.SeedFile(context.Paths.GetPlan(ChangeSetId), Encoding.UTF8.GetBytes(json));

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Load(ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void Plan_RoundTripsClaudeOwnedSettingsArmInCanonicalOrder()
    {
        var context = CreateContext();
        var plan = CreateClaudePlan("1");

        context.PlanStore.Create(context.Lock, plan);
        var reopened = context.PlanStore.Load(ChangeSetId);
        using var document = JsonDocument.Parse(context.Platform.ReadSeededFile(context.Paths.GetPlan(ChangeSetId)));
        var desired = document.RootElement.GetProperty("targets")[0].GetProperty("desired_state");

        Assert.Equivalent(plan, reopened, strict: true);
        Assert.Equal(
            ["kind", "expected_state_hash", "owned_env", "owned_hooks"],
            desired.EnumerateObject().Select(property => property.Name));
        Assert.Equal("claude_settings_owned_values_v1", desired.GetProperty("kind").GetString());
        Assert.Equal(["key", "value"], desired.GetProperty("owned_env")[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            ["event", "command", "args", "timeout_seconds"],
            desired.GetProperty("owned_hooks")[0].EnumerateObject().Select(property => property.Name));
        Assert.Equal(5, desired.GetProperty("owned_env").GetArrayLength());
        Assert.Equal(11, desired.GetProperty("owned_hooks").GetArrayLength());
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2048, true)]
    [InlineData(2049, false)]
    public void Plan_ClaudeOwnedStringsEnforceUtf16Boundary(int length, bool valid)
    {
        var plan = CreateClaudePlan(new string('x', length));

        if (!valid)
        {
            Assert.Throws<FormatException>(() => SetupPlanStore.Serialize(plan));
            return;
        }

        using var document = JsonDocument.Parse(SetupPlanStore.Serialize(plan));
        Assert.Equal(
            length,
            document.RootElement.GetProperty("targets")[0]
                .GetProperty("desired_state").GetProperty("owned_env")[0]
                .GetProperty("value").GetString()!.Length);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(8, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(9, false)]
    public void Plan_ClaudeOwnedEnvEnforcesEntryCountBoundaries(int count, bool valid)
    {
        var original = CreateClaudePlan("1");
        var state = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(original.Targets[0].DesiredState);
        var canonical = new[]
        {
            new SetupClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", "1"),
            new SetupClaudeSettingsEnvValue("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1"),
            new SetupClaudeSettingsEnvValue("OTEL_TRACES_EXPORTER", "otlp"),
            new SetupClaudeSettingsEnvValue("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf"),
            new SetupClaudeSettingsEnvValue("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://127.0.0.1:4320/v1/traces"),
            new SetupClaudeSettingsEnvValue("OTEL_LOG_USER_PROMPTS", "1"),
            new SetupClaudeSettingsEnvValue("OTEL_LOG_TOOL_DETAILS", "1"),
            new SetupClaudeSettingsEnvValue("OTEL_LOG_TOOL_CONTENT", "1"),
            new SetupClaudeSettingsEnvValue("UNKNOWN_ENV", "1"),
        }.Take(count).ToArray();
        var hookMembers = original.Targets[0].Members.Where(member => member.SettingKey.StartsWith("hooks.", StringComparison.Ordinal));
        var members = canonical.Select(value => new SetupPrivatePlanMember($"env.{value.Key}", SetupOperation.Replace, value.Value))
            .Concat(hookMembers)
            .ToArray();
        var plan = original with
        {
            Targets = [original.Targets[0] with { DesiredState = state with { OwnedEnv = canonical }, Members = members }],
        };

        if (valid)
        {
            _ = SetupPlanStore.Serialize(plan);
        }
        else
        {
            Assert.Throws<FormatException>(() => SetupPlanStore.Serialize(plan));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2048, true)]
    [InlineData(2049, false)]
    public void Plan_ClaudeHookCommandEnforcesUtf16Boundary(int length, bool valid)
    {
        var plan = CreateClaudePlan("1");
        var state = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(plan.Targets[0].DesiredState);
        var hooks = state.OwnedHooks.ToArray();
        hooks[0] = hooks[0] with { Command = new string('x', length) };
        plan = plan with
        {
            Targets = [plan.Targets[0] with { DesiredState = state with { OwnedHooks = hooks } }],
        };

        if (valid)
        {
            _ = SetupPlanStore.Serialize(plan);
        }
        else
        {
            Assert.Throws<FormatException>(() => SetupPlanStore.Serialize(plan));
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2048, true)]
    [InlineData(2049, false)]
    public void Plan_ClaudeHookArgumentEnforcesUtf16Boundary(int length, bool valid)
    {
        var plan = CreateClaudePlan("1");
        var state = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(plan.Targets[0].DesiredState);
        var hooks = state.OwnedHooks.ToArray();
        var arguments = hooks[0].Arguments.ToArray();
        arguments[0] = new string('x', length);
        hooks[0] = hooks[0] with { Arguments = arguments };
        plan = plan with
        {
            Targets = [plan.Targets[0] with { DesiredState = state with { OwnedHooks = hooks } }],
        };

        if (valid)
        {
            _ = SetupPlanStore.Serialize(plan);
        }
        else
        {
            Assert.Throws<FormatException>(() => SetupPlanStore.Serialize(plan));
        }
    }

    [Theory]
    [InlineData("claude-code", SetupTargetKind.File, "claude-code-user-settings")]
    [InlineData("github-copilot", SetupTargetKind.Json, "claude-code-user-settings")]
    [InlineData("claude-code", SetupTargetKind.Json, "wrong-label")]
    public void DesiredStateBinding_ClaudeArmRejectsWrongAdapterKindOrLabel(
        string adapter,
        SetupTargetKind targetKind,
        string label)
    {
        var plan = CreateClaudePlan("1") with
        {
            Adapter = adapter,
            Targets = [CreateClaudePlan("1").Targets[0] with { TargetKind = targetKind }],
        };
        var ledger = CreateClaudePlannedChangeSet() with
        {
            Adapter = adapter,
            Targets = [CreateClaudePlannedChangeSet().Targets[0] with { TargetKind = targetKind, TargetLabel = label, OwningAdapter = adapter }],
        };

        Assert.Throws<SetupStorageException>(() => SetupStorageValidation.ValidateDesiredStateBindings(plan, ledger));
    }

    [Fact]
    public void DesiredStateBinding_ClaudeArmAcceptsExactAdapterKindAndLabel()
    {
        SetupStorageValidation.ValidateDesiredStateBindings(
            CreateClaudePlan("1"),
            CreateClaudePlannedChangeSet());
    }

    [Theory]
    [InlineData("cli")]
    [InlineData("app-sdk")]
    [InlineData("all")]
    public void ValidatePlanAndLedger_ClaudeExactTargetPartitionsAreAccepted(string selectedTarget)
    {
        var cliPlan = CreateClaudePlan("1");
        var cliLedger = CreateClaudePlannedChangeSet();
        var pythonPlan = CreateClaudeGuidancePlanTarget(true);
        var typescriptPlan = CreateClaudeGuidancePlanTarget(false);
        var pythonLedger = CreateClaudeGuidanceLedgerTarget(true);
        var typescriptLedger = CreateClaudeGuidanceLedgerTarget(false);
        var plan = selectedTarget switch
        {
            "cli" => cliPlan,
            "app-sdk" => cliPlan with
            {
                SelectedTarget = selectedTarget,
                Targets = [pythonPlan, typescriptPlan],
            },
            _ => cliPlan with
            {
                SelectedTarget = selectedTarget,
                Targets = [cliPlan.Targets[0], pythonPlan, typescriptPlan],
            },
        };
        var changeSet = selectedTarget switch
        {
            "cli" => cliLedger,
            "app-sdk" => cliLedger with
            {
                SelectedTarget = selectedTarget,
                Targets = [pythonLedger, typescriptLedger],
            },
            _ => cliLedger with
            {
                SelectedTarget = selectedTarget,
                Targets = [cliLedger.Targets[0], pythonLedger, typescriptLedger],
            },
        };

        SetupStorageValidation.ValidatePlanAndLedger(plan, changeSet);
    }

    [Theory]
    [InlineData("cli_missing_writable")]
    [InlineData("all_guidance_only")]
    [InlineData("app_sdk_with_writable")]
    [InlineData("all_reordered")]
    [InlineData("all_extra")]
    [InlineData("cli_wrong_label")]
    public void PersistPlannedChangeSet_ClaudeTargetPartitionTamperRequiresRecoveryBeforeWriting(
        string mutation)
    {
        var context = CreateContext();
        var plan = CreateClaudePlan("1");
        var changeSet = CreateClaudePlannedChangeSet();
        var pythonPlan = CreateClaudeGuidancePlanTarget(true);
        var typescriptPlan = CreateClaudeGuidancePlanTarget(false);
        var pythonLedger = CreateClaudeGuidanceLedgerTarget(true);
        var typescriptLedger = CreateClaudeGuidanceLedgerTarget(false);
        switch (mutation)
        {
            case "cli_missing_writable":
                plan = plan with { Targets = [] };
                changeSet = changeSet with { Targets = [] };
                break;
            case "all_guidance_only":
                plan = plan with { SelectedTarget = "all", Targets = [pythonPlan, typescriptPlan] };
                changeSet = changeSet with { SelectedTarget = "all", Targets = [pythonLedger, typescriptLedger] };
                break;
            case "app_sdk_with_writable":
                plan = plan with
                {
                    SelectedTarget = "app-sdk",
                    Targets = [pythonPlan, typescriptPlan, plan.Targets[0]],
                };
                changeSet = changeSet with
                {
                    SelectedTarget = "app-sdk",
                    Targets = [pythonLedger, typescriptLedger, changeSet.Targets[0]],
                };
                break;
            case "all_reordered":
                plan = plan with
                {
                    SelectedTarget = "all",
                    Targets = [pythonPlan, plan.Targets[0], typescriptPlan],
                };
                changeSet = changeSet with
                {
                    SelectedTarget = "all",
                    Targets = [pythonLedger, changeSet.Targets[0], typescriptLedger],
                };
                break;
            case "all_extra":
                var extraPlan = typescriptPlan with
                {
                    RecordId = Guid.Parse("00000000-0000-7000-8000-000000000204"),
                };
                var extraLedger = typescriptLedger with
                {
                    RecordId = extraPlan.RecordId,
                };
                plan = plan with
                {
                    SelectedTarget = "all",
                    Targets = [plan.Targets[0], pythonPlan, typescriptPlan, extraPlan],
                };
                changeSet = changeSet with
                {
                    SelectedTarget = "all",
                    Targets = [changeSet.Targets[0], pythonLedger, typescriptLedger, extraLedger],
                };
                break;
            case "cli_wrong_label":
                changeSet = changeSet with
                {
                    Targets =
                    [
                        changeSet.Targets[0] with
                        {
                            TargetLabel = "rebound-claude-settings",
                            StatusProjection = changeSet.Targets[0].StatusProjection with { ExpectedResult = null },
                        },
                    ],
                };
                break;
        }

        var operationCount = context.Platform.Operations.Count;

        var exception = Assert.Throws<SetupStorageException>(() =>
            context.Store.PersistPlannedChangeSet(context.Lock, plan, changeSet));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(operationCount, context.Platform.Operations.Count);
        Assert.False(context.Platform.FileSystem.FileExists(context.Paths.GetPlan(ChangeSetId)));
        Assert.False(context.Platform.FileSystem.FileExists(context.Paths.OwnershipLedger));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(6)]
    [InlineData(7)]
    public void PlanLoad_ClaudePartialCanonicalEnvRequiresRecovery(int envCount)
    {
        var context = CreateContext();
        var sourceEnvCount = envCount < 5 ? 5 : 8;
        var plan = CreateClaudePlan("1", includeContentCapture: sourceEnvCount == 8);
        var root = JsonNode.Parse(SetupPlanStore.Serialize(plan))!.AsObject();
        var target = root["targets"]![0]!.AsObject();
        var desiredState = target["desired_state"]!.AsObject();
        var env = desiredState["owned_env"]!.AsArray();
        while (env.Count > envCount)
        {
            env.RemoveAt(env.Count - 1);
        }

        var members = target["members"]!.AsArray();
        var envMembers = members.Take(envCount).Select(member => member!.DeepClone()).ToArray();
        var hooks = members.Skip(sourceEnvCount).Select(member => member!.DeepClone()).ToArray();
        while (members.Count > 0)
        {
            members.RemoveAt(members.Count - 1);
        }
        foreach (var envMember in envMembers)
        {
            members.Add(envMember);
        }
        foreach (var hook in hooks)
        {
            members.Add(hook);
        }

        context.Platform.SeedFile(
            context.Paths.GetPlan(ChangeSetId),
            Encoding.UTF8.GetBytes(root.ToJsonString()));

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Load(ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.Equal(SetupCodes.RecoveryRequired, exception.Message);
        Assert.Null(exception.InnerException);
    }

    [Theory]
    [InlineData("reordered_hooks")]
    [InlineData("duplicate_hook")]
    [InlineData("wrong_timeout")]
    [InlineData("unknown_hook")]
    [InlineData("empty_args")]
    public void Plan_ClaudeArmRejectsNoncanonicalHookContract(string mutation)
    {
        var plan = CreateClaudePlan("1");
        var state = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(plan.Targets[0].DesiredState);
        var hooks = state.OwnedHooks.ToArray();
        switch (mutation)
        {
            case "reordered_hooks":
                (hooks[0], hooks[1]) = (hooks[1], hooks[0]);
                break;
            case "duplicate_hook":
                hooks[1] = hooks[0];
                break;
            case "wrong_timeout":
                hooks[0] = hooks[0] with { TimeoutSeconds = 4 };
                break;
            case "unknown_hook":
                hooks[0] = hooks[0] with { EventName = "UnknownEvent" };
                break;
            case "empty_args":
                hooks[0] = hooks[0] with { Arguments = [] };
                break;
        }

        plan = plan with
        {
            Targets = [plan.Targets[0] with { DesiredState = state with { OwnedHooks = hooks } }],
        };

        Assert.Throws<FormatException>(() => SetupPlanStore.Serialize(plan));
    }

    [Theory]
    [InlineData("unknown_arm")]
    [InlineData("unknown_field")]
    [InlineData("duplicate_env_key_field")]
    [InlineData("owned_env_wrong_type")]
    [InlineData("argument_wrong_type")]
    public void PlanLoad_MalformedClaudeArmFailsRecoveryWithoutEcho(string mutation)
    {
        const string privateMarker = "PRIVATE_COMMAND_MARKER";
        var context = CreateContext();
        var root = JsonNode.Parse(SetupPlanStore.Serialize(CreateClaudePlan("1")))!.AsObject();
        var desired = root["targets"]![0]!["desired_state"]!.AsObject();
        string serialized;
        switch (mutation)
        {
            case "unknown_arm":
                desired["kind"] = "claude_settings_owned_values_v2";
                serialized = root.ToJsonString();
                break;
            case "unknown_field":
                desired["private_marker"] = privateMarker;
                serialized = root.ToJsonString();
                break;
            case "duplicate_env_key_field":
                serialized = root.ToJsonString().Replace(
                    "\"key\":\"CLAUDE_CODE_ENABLE_TELEMETRY\"",
                    "\"key\":\"CLAUDE_CODE_ENABLE_TELEMETRY\",\"key\":\"CLAUDE_CODE_ENABLE_TELEMETRY\"",
                    StringComparison.Ordinal);
                break;
            case "owned_env_wrong_type":
                desired["owned_env"] = "private-marker";
                serialized = root.ToJsonString();
                break;
            case "argument_wrong_type":
                desired["owned_hooks"]![0]!["args"]![0] = true;
                serialized = root.ToJsonString();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        context.Platform.SeedFile(context.Paths.GetPlan(ChangeSetId), Encoding.UTF8.GetBytes(serialized));

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Load(ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.DoesNotContain(privateMarker, exception.ToString(), StringComparison.Ordinal);
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
            TargetLabel = "github-copilot-app-sdk-guidance",
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
        var rehydrated = SetupContractValidator.RehydrateStatusGuidance(
            guidanceTarget.StatusProjection.Guidance!,
            guidanceTarget.TargetLabel);
        Assert.Contains("OtlpEndpoint", rehydrated.Sample, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", rehydrated.Sample, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other_kind", "dotnet")]
    [InlineData("caller_managed_sample", "python")]
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

    [Theory]
    [InlineData("adapter", "other-adapter")]
    [InlineData("target_kind", "file")]
    [InlineData("target_kind", "toml")]
    public void PlanLoad_TaggedArmForInvalidAdapterOrTargetKindRequiresRecovery(string property, string value)
    {
        var context = CreateContext();
        var root = JsonNode.Parse(SetupPlanStore.Serialize(CreatePlan()))!.AsObject();
        if (property == "adapter")
        {
            root["adapter"] = value;
        }
        else
        {
            root["targets"]![0]![property] = value;
        }

        context.Platform.SeedFile(
            context.Paths.GetPlan(ChangeSetId),
            Encoding.UTF8.GetBytes(root.ToJsonString()));

        var exception = Assert.Throws<SetupStorageException>(() => context.PlanStore.Load(ChangeSetId));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
    }

    [Theory]
    [InlineData("inline_exact_label")]
    [InlineData("tagged_other_label")]
    public void ValidatePlanAndLedger_DesiredStateBindingMismatchRequiresRecovery(string variant)
    {
        var plan = variant == "inline_exact_label"
            ? CreateLegacyInlineFixturePlan()
            : CreatePlan();
        var changeSet = CreatePlannedChangeSet();
        if (variant == "tagged_other_label")
        {
            changeSet = changeSet with
            {
                Targets =
                [
                    changeSet.Targets[0] with
                    {
                        TargetLabel = "other-json-target",
                        StatusProjection = changeSet.Targets[0].StatusProjection with { ExpectedResult = null },
                    },
                ],
            };
        }

        var exception = Assert.Throws<SetupStorageException>(() =>
            SetupStorageValidation.ValidatePlanAndLedger(plan, changeSet));

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
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

    [Fact]
    public void CommittedV1Fixture_SurvivesProcessEquivalentRestartWithoutMigrationArtifacts()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Setup", "v1", "ownership-ledger.v1.json");
        var fixtureBytes = File.ReadAllBytes(fixturePath);
        var expected = new SetupOwnershipLedger(1, [CreateAppliedChangeSet()]);
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cao-setup-storage-{Guid.NewGuid():N}");
        byte[] preLoadBytes;

        try
        {
            {
                var firstPlatform = new SystemSetupPlatform(localApplicationData: temporaryRoot);
                var firstPaths = new SetupRuntimePaths(firstPlatform);
                var firstPlans = new SetupPlanStore(firstPlatform, firstPaths);
                var firstStore = new SetupLedgerStore(firstPlatform, firstPaths, firstPlans);
                firstPaths.EnsureRoot();
                firstPlatform.FileSystem.WriteAllBytes(firstPaths.OwnershipLedger, fixtureBytes);
                _ = firstStore.Load();
                preLoadBytes = firstPlatform.FileSystem.ReadAllBytes(firstPaths.OwnershipLedger);
            }

            {
                var reopenedPlatform = new SystemSetupPlatform(localApplicationData: temporaryRoot);
                var reopenedPaths = new SetupRuntimePaths(reopenedPlatform);
                var reopenedPlans = new SetupPlanStore(reopenedPlatform, reopenedPaths);
                var reopenedStore = new SetupLedgerStore(reopenedPlatform, reopenedPaths, reopenedPlans);
                var loaded = reopenedStore.Load();
                var postLoadBytes = reopenedPlatform.FileSystem.ReadAllBytes(reopenedPaths.OwnershipLedger);

                Assert.Equal(fixtureBytes, preLoadBytes);
                Assert.Equivalent(expected, loaded, strict: true);
                Assert.Equal(1, loaded.SchemaVersion);
                var changeSet = Assert.Single(loaded.ChangeSets);
                Assert.Equal(SetupChangeSetState.Applied, changeSet.State);
                Assert.Equal(SetupCodes.ApplySucceeded, changeSet.OutcomeCode);
                Assert.Equal(SetupTargetKind.Json, Assert.Single(changeSet.Targets).TargetKind);
                Assert.Equal(fixtureBytes, postLoadBytes);
                Assert.Equal(preLoadBytes, postLoadBytes);
                var entries = Directory
                    .EnumerateFileSystemEntries(reopenedPaths.Root, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(reopenedPaths.Root, path))
                    .ToArray();
                Assert.DoesNotContain(entries, path =>
                    path.Contains("v0", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("v2", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("migration", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".journal.json", StringComparison.OrdinalIgnoreCase) ||
                    path.EndsWith(".backup", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            if (Directory.Exists(temporaryRoot))
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void CommittedPrivatePlanV1Fixture_IsProductionSerializerOutputAndSurvivesRestartByteIdentical()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Setup", "v1", "private-plan.v1.json");
        var fixtureBytes = File.ReadAllBytes(fixturePath);
        var ownershipFixtureBytes = File.ReadAllBytes(Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures", "Setup", "v1", "ownership-ledger.v1.json"));
        var expected = CreateLegacyInlineFixturePlan();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"cao-setup-plan-fixture-{Guid.NewGuid():N}");
        try
        {
            var firstPlatform = new SystemSetupPlatform(localApplicationData: temporaryRoot);
            var firstPaths = new SetupRuntimePaths(firstPlatform);
            var firstStore = new SetupPlanStore(firstPlatform, firstPaths);
            using (var acquired = SetupLock.TryAcquire(firstPlatform, firstPaths))
            {
                firstStore.Create(acquired.Lock!, expected);
            }

            var writtenBytes = firstPlatform.FileSystem.ReadAllBytes(firstPaths.GetPlan(ChangeSetId));
            var reopenedPlatform = new SystemSetupPlatform(localApplicationData: temporaryRoot);
            var reopenedPaths = new SetupRuntimePaths(reopenedPlatform);
            var reopened = new SetupPlanStore(reopenedPlatform, reopenedPaths).Load(ChangeSetId);
            var reopenedBytes = reopenedPlatform.FileSystem.ReadAllBytes(reopenedPaths.GetPlan(ChangeSetId));

            Assert.Equivalent(expected, reopened, strict: true);
            Assert.Equal(fixtureBytes, writtenBytes);
            Assert.Equal(fixtureBytes, reopenedBytes);
            Assert.NotEqual(ownershipFixtureBytes, fixtureBytes);
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
                new SetupJsoncOwnedValuesDesiredState(
                    HashB,
                    [new SetupJsoncOwnedValue("github.copilot.chat.otel.enabled", "string", "DESIRED_VALUE_MARKER")]),
                [new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", SetupOperation.Replace, "DESIRED_VALUE_MARKER")]),
        ]);

    private static SetupPrivatePlan CreateClaudePlan(string firstEnvValue, bool includeContentCapture = false)
    {
        var env = new List<SetupClaudeSettingsEnvValue>
        {
            new SetupClaudeSettingsEnvValue("CLAUDE_CODE_ENABLE_TELEMETRY", firstEnvValue),
            new SetupClaudeSettingsEnvValue("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1"),
            new SetupClaudeSettingsEnvValue("OTEL_TRACES_EXPORTER", "otlp"),
            new SetupClaudeSettingsEnvValue("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf"),
            new SetupClaudeSettingsEnvValue("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", "http://127.0.0.1:4320/v1/traces"),
        };
        if (includeContentCapture)
        {
            env.AddRange(
            [
                new SetupClaudeSettingsEnvValue("OTEL_LOG_USER_PROMPTS", "1"),
                new SetupClaudeSettingsEnvValue("OTEL_LOG_TOOL_DETAILS", "1"),
                new SetupClaudeSettingsEnvValue("OTEL_LOG_TOOL_CONTENT", "1"),
            ]);
        }
        var events = new[]
        {
            "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest", "PostToolUse",
            "PostToolUseFailure", "SubagentStart", "SubagentStop", "Stop", "StopFailure", "SessionEnd",
        };
        var hooks = events.Select(eventName => new SetupClaudeSettingsHook(
            eventName,
            "monitor.exe",
            ["hook-forward", "--endpoint", "http://127.0.0.1:4320", "--timeout-ms", "250", "--source", "claude-code", "--source-version", "2.1.207"],
            5)).ToArray();
        var members = env.Select(value => new SetupPrivatePlanMember(
                $"env.{value.Key}", SetupOperation.Replace, value.Value))
            .Concat(hooks.Select(hook => new SetupPrivatePlanMember(
                $"hooks.{hook.EventName}", SetupOperation.Add, "configured")))
            .ToArray();
        return new SetupPrivatePlan(
            1,
            ChangeSetId,
            "claude-code",
            "cli",
            CreatedAt,
            "1.2.3",
            [
                new SetupPrivatePlanTarget(
                    RecordId,
                    SetupTargetKind.Json,
                    "C:\\Synthetic\\claude-settings.json",
                    HashA,
                    new SetupClaudeSettingsOwnedValuesDesiredState(HashB, env, hooks),
                    members),
            ]);
    }

    private static SetupPrivatePlanTarget CreateClaudeGuidancePlanTarget(bool python)
    {
        var label = python
            ? "claude-agent-sdk-python-guidance"
            : "claude-agent-sdk-typescript-guidance";
        return new SetupPrivatePlanTarget(
            Guid.Parse(python
                ? "00000000-0000-7000-8000-000000000202"
                : "00000000-0000-7000-8000-000000000203"),
            SetupTargetKind.Guidance,
            label,
            new string('0', 64),
            new SetupInlineDesiredState(label),
            []);
    }

    private static SetupLedgerTarget CreateClaudeGuidanceLedgerTarget(bool python)
    {
        var planTarget = CreateClaudeGuidancePlanTarget(python);
        var label = planTarget.TargetLocation;
        return new SetupLedgerTarget(
            planTarget.RecordId,
            SetupTargetKind.Guidance,
            label,
            "claude-code",
            [],
            planTarget.BaseStateHash,
            null,
            null,
            null,
            SetupLedgerRollbackStatus.NotAvailable,
            SetupRestartRequirement.None,
            new SetupStatusProjection(
                true,
                null,
                SetupOperation.NoOp,
                null,
                null,
                null,
                new SetupStatusGuidance("caller_managed_sample", python ? "python" : "typescript"),
                []),
            "1.2.3");
    }

    private static SetupLedgerChangeSet CreateClaudePlannedChangeSet()
    {
        var plan = CreateClaudePlan("1");
        var members = plan.Targets[0].Members
            .Select(member => new SetupLedgerMember(member.SettingKey, member.Operation))
            .ToArray();
        var changes = plan.Targets[0].Members
            .Select(member => new SetupMemberChangeResult(
                member.SettingKey,
                member.Operation,
                "redacted",
                "configured",
                "none",
                false))
            .ToArray();
        return new SetupLedgerChangeSet(
            ChangeSetId,
            "claude-code",
            "cli",
            CreatedAt,
            CreatedAt,
            "1.2.3",
            null,
            SetupChangeSetState.Planned,
            [
                new SetupLedgerTarget(
                    RecordId,
                    SetupTargetKind.Json,
                    "claude-code-user-settings",
                    "claude-code",
                    members,
                    HashA,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    new SetupStatusProjection(
                        true,
                        "2.1.207",
                        SetupOperation.Mixed,
                        SetupEffectiveSource.UserSetting,
                        "http://127.0.0.1:4320",
                        SourceCapabilityManifestLoader.LoadForSurface("claude-code").CanonicalJson,
                        null,
                        changes),
                    "1.2.3"),
            ]);
    }

    private static SetupPrivatePlan CreateLegacyInlineFixturePlan() => new(
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
                new SetupInlineDesiredState("{\"github.copilot.chat.otel.enabled\":\"DESIRED_VALUE_MARKER\"}"),
                [new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", SetupOperation.Replace, "DESIRED_VALUE_MARKER")]),
        ]);

    private static SetupPrivatePlan CreateTaggedBooleanAndStringPlan(string stringValue) => new(
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
                new SetupJsoncOwnedValuesDesiredState(
                    HashB,
                    [
                        new SetupJsoncOwnedValue("github.copilot.chat.otel.enabled", "boolean", true),
                        new SetupJsoncOwnedValue("github.copilot.chat.otel.otlpEndpoint", "string", stringValue),
                    ]),
                [
                    new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", SetupOperation.Replace, "true"),
                    new SetupPrivatePlanMember("github.copilot.chat.otel.otlpEndpoint", SetupOperation.Replace, stringValue),
                ]),
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
                new SetupInlineDesiredState("desired-state"),
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
        TargetLabel = "github-copilot-app-sdk-guidance",
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

    private static string MutateTaggedDesiredState(string serialized, string mutation)
    {
        if (mutation == "desired_duplicate")
        {
            return serialized.Replace(
                "\"kind\": \"jsonc_owned_values_v1\"",
                "\"kind\": \"jsonc_owned_values_v1\", \"kind\": \"jsonc_owned_values_v1\"",
                StringComparison.Ordinal);
        }

        if (mutation == "entry_duplicate")
        {
            return serialized.Replace(
                "\"setting_key\": \"github.copilot.chat.otel.enabled\"",
                "\"setting_key\": \"github.copilot.chat.otel.enabled\", \"setting_key\": \"github.copilot.chat.otel.enabled\"",
                StringComparison.Ordinal);
        }

        var root = JsonNode.Parse(serialized)!.AsObject();
        var target = root["targets"]![0]!.AsObject();
        var desired = target["desired_state"]!.AsObject();
        var values = desired["owned_values"]!.AsArray();
        var first = values[0]!.AsObject();
        switch (mutation)
        {
            case "desired_unknown":
                desired["unknown"] = "PREVIOUS_SECRET_MARKER";
                break;
            case "desired_missing_kind":
                desired.Remove("kind");
                break;
            case "desired_missing_hash":
                desired.Remove("expected_state_hash");
                break;
            case "desired_missing_values":
                desired.Remove("owned_values");
                break;
            case "desired_null":
                target["desired_state"] = null;
                break;
            case "desired_array":
                target["desired_state"] = new JsonArray();
                break;
            case "desired_boolean":
                target["desired_state"] = true;
                break;
            case "desired_number":
                target["desired_state"] = 1;
                break;
            case "wrong_tag":
                desired["kind"] = "jsonc_owned_values_v2";
                break;
            case "uppercase_hash":
                desired["expected_state_hash"] = HashB.ToUpperInvariant();
                break;
            case "nonhex_hash":
                desired["expected_state_hash"] = new string('g', 64);
                break;
            case "kind_wrong_type":
                desired["kind"] = 1;
                break;
            case "hash_wrong_type":
                desired["expected_state_hash"] = true;
                break;
            case "owned_values_wrong_type":
                desired["owned_values"] = "not-an-array";
                break;
            case "owned_values_empty":
                values.Clear();
                break;
            case "owned_values_33":
                values.Clear();
                for (var index = 0; index < 33; index++)
                {
                    values.Add(new JsonObject
                    {
                        ["setting_key"] = $"setting_{index}",
                        ["value_kind"] = "boolean",
                        ["value"] = true,
                    });
                }
                break;
            case "entry_unknown":
                first["unknown"] = "PREVIOUS_SECRET_MARKER";
                break;
            case "entry_missing_key":
                first.Remove("setting_key");
                break;
            case "entry_missing_kind":
                first.Remove("value_kind");
                break;
            case "entry_missing_value":
                first.Remove("value");
                break;
            case "setting_key_wrong_type":
                first["setting_key"] = true;
                break;
            case "value_kind_wrong_type":
                first["value_kind"] = true;
                break;
            case "wrong_value_kind":
                first["value_kind"] = "number";
                break;
            case "boolean_value_string":
                first["value"] = "true";
                break;
            case "string_value_boolean":
                values[1]!["value"] = true;
                break;
            case "string_value_null":
                values[1]!["value"] = null;
                break;
            case "string_value_empty":
                values[1]!["value"] = string.Empty;
                break;
            case "string_value_over_bound":
                values[1]!["value"] = new string('x', 2049);
                break;
            case "duplicate_key":
                values[1]!["setting_key"] = first["setting_key"]!.GetValue<string>();
                break;
            case "reordered_key":
                SwapFirstTwo(values);
                break;
            case "missing_key":
                values.RemoveAt(1);
                break;
            case "extra_key":
                values.Add(new JsonObject
                {
                    ["setting_key"] = "extra.setting",
                    ["value_kind"] = "string",
                    ["value"] = "extra",
                });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        return root.ToJsonString();
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

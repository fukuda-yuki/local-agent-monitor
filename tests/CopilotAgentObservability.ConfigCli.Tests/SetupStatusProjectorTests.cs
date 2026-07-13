using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Status;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupStatusProjectorTests
{
    [Fact]
    public void Project_AppliedTarget_UsesImmutableSnapshotAndFreshTargetAndRollbackEvidence()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();

        var current = fixture.Project();

        var target = Assert.Single(current.Targets);
        Assert.Equal(SetupReferenceState.Desired, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.True(target.RollbackAvailable);
        Assert.True(current.RollbackAvailable);
        Assert.Equal("1.128.7", target.DetectedVersion);
        Assert.Equal(SetupEffectiveSource.UserSetting, target.EffectiveSource);
        Assert.Equal("http://127.0.0.1:4320", target.Endpoint);
        Assert.Equal("planned_previous", Assert.Single(target.Changes).PreviousState);

        fixture.SeedTarget("third-party");
        var stale = fixture.Project();

        target = Assert.Single(stale.Targets);
        Assert.Equal(SetupReferenceState.Desired, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Stale, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.Equal(SetupCurrentState.Stale, stale.CurrentState);
        Assert.False(stale.RollbackAvailable);
        Assert.Equal("1.128.7", target.DetectedVersion);
        Assert.Equal("planned_previous", Assert.Single(target.Changes).PreviousState);
    }

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("target-drift", false)]
    [InlineData("missing-backup", false)]
    [InlineData("corrupt-backup", false)]
    [InlineData("reparse-backup", false)]
    [InlineData("all-noop-drift", false)]
    public void Project_RollbackAvailability_EqualsSharedFreshPreflight(string variant, bool expected)
    {
        var fixture = StatusFixture.Create(includeAllNoOpEnvironment: variant == "all-noop-drift");
        fixture.Apply();
        fixture.ApplyVariant(variant);

        var status = fixture.Project();
        var preflight = fixture.EvaluateRollbackPreflight();

        Assert.Equal(expected, preflight.IsAvailable);
        Assert.Equal(preflight.IsAvailable, status.RollbackAvailable);
        var changed = Assert.Single(status.Targets, target => target.Operation != SetupOperation.NoOp);
        Assert.Equal(variant is "fresh" or "all-noop-drift", changed.RollbackAvailable);
        var noOp = status.Targets.SingleOrDefault(target => target.Operation == SetupOperation.NoOp);
        if (noOp is not null)
        {
            Assert.False(noOp.RollbackAvailable);
            Assert.Equal(
                variant == "all-noop-drift" ? SetupCurrentState.Stale : SetupCurrentState.Current,
                noOp.CurrentState);
        }
    }

    [Theory]
    [InlineData(SetupChangeSetState.Planned, SetupReferenceState.Base)]
    [InlineData(SetupChangeSetState.NoChanges, SetupReferenceState.Desired)]
    [InlineData(SetupChangeSetState.Applied, SetupReferenceState.Desired)]
    [InlineData(SetupChangeSetState.Restored, SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.RolledBack, SetupReferenceState.Previous)]
    public void Project_TerminalAndPlannedLifecycle_UsesCanonicalReference(
        SetupChangeSetState state,
        SetupReferenceState expectedReference)
    {
        var fixture = StatusFixture.Create(operation: state == SetupChangeSetState.NoChanges
            ? SetupOperation.NoOp
            : SetupOperation.Replace);
        if (state is not SetupChangeSetState.Planned and not SetupChangeSetState.NoChanges)
        {
            fixture.Apply();
        }

        if (state is SetupChangeSetState.Restored or SetupChangeSetState.RolledBack)
        {
            fixture.Rollback();
        }

        fixture.SetLifecycle(state);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.Equal(state == SetupChangeSetState.Applied, result.RollbackAvailable);
    }

    [Theory]
    [InlineData("desired", SetupReferenceState.Desired, SetupCurrentState.Current)]
    [InlineData("previous", SetupReferenceState.Previous, SetupCurrentState.Current)]
    [InlineData("third-party", SetupReferenceState.None, SetupCurrentState.Diverged)]
    public void Project_PartialLifecycle_ClassifiesFreshAggregate(
        string currentValue,
        SetupReferenceState expectedReference,
        SetupCurrentState expectedCurrent)
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        fixture.SeedTarget(currentValue);
        fixture.SetLifecycle(SetupChangeSetState.Partial);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(expectedCurrent, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applying, SetupReferenceState.Base)]
    [InlineData(SetupChangeSetState.Compensating, SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.RollingBack, SetupReferenceState.Previous)]
    public void Project_ActiveLifecycle_UsesJournalRelativePriorReference(
        SetupChangeSetState state,
        SetupReferenceState expectedReference)
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        fixture.SeedTarget("previous");
        fixture.SetLifecycle(state);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void Project_PartialEnvironmentWithDesiredPreviousMixture_IsDiverged()
    {
        var fixture = StatusFixture.CreateEnvironment();
        fixture.Apply();
        fixture.Platform.SeedUserEnvironment("ENV_A", "old-a");
        fixture.SetLifecycle(SetupChangeSetState.Partial);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.None, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Diverged, target.CurrentState);
        Assert.Equal(SetupCurrentState.Diverged, result.CurrentState);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void Project_Guidance_RehydratesFixedSampleInMemoryButStatusJsonOmitsIt()
    {
        var fixture = StatusFixture.CreateGuidance();

        var projected = fixture.Project();
        var target = Assert.Single(projected.Targets);

        Assert.Equal(SetupReferenceState.None, target.ReferenceState);
        Assert.Equal(SetupCurrentState.NotApplicable, target.CurrentState);
        Assert.NotNull(target.Guidance);
        Assert.NotEmpty(target.Guidance.Sample);
        var json = SetupJson.Serialize(new SetupCommandResult(
            SetupCommand.Status,
            true,
            SetupCodes.StatusReady,
            null,
            null,
            null,
            "github-copilot",
            [],
            [projected],
            [],
            [],
            false));
        using var document = JsonDocument.Parse(json);
        var guidance = document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0].GetProperty("guidance");
        Assert.Equal(["kind", "language"], guidance.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain("TelemetryConfig", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Project_HistoricalManifest_UsesLedgerValidationAndProductionSerializer()
    {
        var fixture = StatusFixture.Create(historicalManifest: true);
        fixture.Apply();

        var projected = fixture.Project();
        var json = SetupJson.Serialize(new SetupCommandResult(
            SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null, "github-copilot",
            [], [projected], [], [], false));

        using var document = JsonDocument.Parse(json);
        var expected = document.RootElement.GetProperty("change_sets")[0].GetProperty("targets")[0].GetProperty("expected_result");
        Assert.Equal("planned", expected.GetProperty("support_status").GetString());
        Assert.Equal("preview", expected.GetProperty("stability").GetString());
        Assert.DoesNotContain(fixture.TargetPath, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE_PLAN_SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE_JOURNAL_SECRET", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-plan")]
    [InlineData("corrupt-plan")]
    [InlineData("rebound-plan")]
    [InlineData("missing-journal")]
    [InlineData("corrupt-journal")]
    public void Project_TerminalArtifactFailure_FailsClosedWithoutPrivateData(string variant)
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        fixture.ApplyArtifactVariant(variant);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.None, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Unavailable, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void Project_NonTerminalMissingPlan_RequiresRecovery()
    {
        var fixture = StatusFixture.Create();
        fixture.DeletePlan();

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
    }

    [Fact]
    public void Project_NonTerminalCorruptJournal_RequiresRecoveryWithoutLeakingArtifact()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        fixture.SetLifecycle(SetupChangeSetState.Applying);
        fixture.ApplyArtifactVariant("corrupt-journal");

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.DoesNotContain("PRIVATE_JOURNAL_SECRET", exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class StatusFixture
    {
        private static readonly DateTimeOffset Now = new(2026, 7, 14, 1, 2, 3, TimeSpan.Zero);
        private readonly SetupPlanStore planStore;
        private readonly SetupLedgerStore ledgerStore;
        private readonly SetupTransactionJournalStore journalStore;
        private readonly bool guidanceOnly;

        private StatusFixture(
            SetupTestPlatform platform,
            SetupRuntimePaths paths,
            Guid changeSetId,
            string targetPath,
            SetupPlanStore planStore,
            SetupLedgerStore ledgerStore,
            SetupTransactionJournalStore journalStore,
            bool guidanceOnly)
        {
            Platform = platform;
            Paths = paths;
            ChangeSetId = changeSetId;
            TargetPath = targetPath;
            this.planStore = planStore;
            this.ledgerStore = ledgerStore;
            this.journalStore = journalStore;
            this.guidanceOnly = guidanceOnly;
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public string TargetPath { get; }

        public static StatusFixture Create(
            SetupOperation operation = SetupOperation.Replace,
            bool includeAllNoOpEnvironment = false,
            bool historicalManifest = false)
        {
            var platform = CreatePlatform();
            var paths = new SetupRuntimePaths(platform);
            var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000662");
            var recordId = Guid.Parse("00000000-0000-7000-8000-000000000663");
            var targetPath = "C:\\private-status\\settings.json";
            platform.SeedDirectory("C:\\private-status");
            platform.SeedFile(targetPath, Encoding.UTF8.GetBytes("previous"));
            var fileStep = new AtomicFileSetupStep(platform);
            var baseHash = fileStep.Capture("C:\\private-status", targetPath).Hash;
            var desired = operation == SetupOperation.NoOp ? "previous" : "desired";
            var member = new SetupPrivatePlanMember("github.copilot.chat.otel.enabled", operation,
                operation == SetupOperation.Remove ? null : "safe-state");
            var planTargets = new List<SetupPrivatePlanTarget>
            {
                new(recordId, SetupTargetKind.Json, targetPath, baseHash, desired, [member]),
            };
            var ledgerTargets = new List<SetupLedgerTarget>
            {
                new(
                    recordId,
                    SetupTargetKind.Json,
                    "vscode-user-settings",
                    "github-copilot",
                    [new SetupLedgerMember(member.SettingKey, member.Operation)],
                    baseHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    new SetupStatusProjection(
                        true,
                        "1.128.7",
                        operation,
                        SetupEffectiveSource.UserSetting,
                        "http://127.0.0.1:4320",
                        CreateManifest(historicalManifest),
                        null,
                        [new SetupMemberChangeResult(member.SettingKey, operation, "planned_previous", "configured_loopback", "none", false)]),
                    "1.2.3"),
            };
            if (includeAllNoOpEnvironment)
            {
                var envRecordId = Guid.Parse("00000000-0000-7000-8000-000000000664");
                platform.SeedUserEnvironment("ENV_NOOP", "same");
                var environmentStep = new UserEnvironmentSetupStep(platform);
                var capture = environmentStep.Capture(["ENV_NOOP"]);
                planTargets.Add(new SetupPrivatePlanTarget(
                    envRecordId,
                    SetupTargetKind.Env,
                    "current-user",
                    capture.AggregateHash,
                    "environment-allowlist",
                    [new SetupPrivatePlanMember("ENV_NOOP", SetupOperation.NoOp, "same")]));
                ledgerTargets.Add(new SetupLedgerTarget(
                    envRecordId,
                    SetupTargetKind.Env,
                    "user-environment",
                    "github-copilot",
                    [new SetupLedgerMember("ENV_NOOP", SetupOperation.NoOp)],
                    capture.AggregateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
                    new SetupStatusProjection(
                        true,
                        "1.0.4",
                        SetupOperation.NoOp,
                        SetupEffectiveSource.Environment,
                        "http://127.0.0.1:4320",
                        SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.CanonicalJson,
                        null,
                        [new SetupMemberChangeResult("ENV_NOOP", SetupOperation.NoOp, "present_same", "present_same", "none", false)]),
                    "1.2.3"));
            }

            var plan = new SetupPrivatePlan(1, changeSetId, "github-copilot", "vscode", Now, "1.2.3", planTargets);
            var ledger = new SetupLedgerChangeSet(
                changeSetId,
                "github-copilot",
                "vscode",
                Now,
                Now,
                "1.2.3",
                null,
                SetupChangeSetState.Planned,
                ledgerTargets);
            var planStore = new SetupPlanStore(platform, paths);
            var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
            var journalStore = new SetupTransactionJournalStore(platform, paths);
            using (var acquisition = SetupLock.TryAcquire(platform, paths))
            {
                ledgerStore.PersistPlannedChangeSet(acquisition.Lock!, plan, ledger);
            }

            return new StatusFixture(platform, paths, changeSetId, targetPath, planStore, ledgerStore, journalStore, false);
        }

        public static StatusFixture CreateGuidance()
        {
            var platform = CreatePlatform();
            var paths = new SetupRuntimePaths(platform);
            var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000665");
            var recordId = Guid.Parse("00000000-0000-7000-8000-000000000666");
            var plan = new SetupPrivatePlan(1, changeSetId, "github-copilot", "app-sdk", Now, "1.2.3",
                [new SetupPrivatePlanTarget(recordId, SetupTargetKind.Guidance, "caller-managed", SetupHash.File(false, []), string.Empty, [])]);
            var ledger = new SetupLedgerChangeSet(
                changeSetId, "github-copilot", "app-sdk", Now, Now, "1.2.3", null, SetupChangeSetState.Planned,
                [new SetupLedgerTarget(
                    recordId, SetupTargetKind.Guidance, "app-sdk-guidance", "github-copilot", [], SetupHash.File(false, []),
                    null, null, null, SetupLedgerRollbackStatus.NotAvailable, SetupRestartRequirement.None,
                    new SetupStatusProjection(false, null, SetupOperation.NoOp, null, null, null,
                        new SetupStatusGuidance("caller_managed_sample", "dotnet"), []), "1.2.3")]);
            var planStore = new SetupPlanStore(platform, paths);
            var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
            var journalStore = new SetupTransactionJournalStore(platform, paths);
            using (var acquisition = SetupLock.TryAcquire(platform, paths))
            {
                ledgerStore.PersistPlannedChangeSet(acquisition.Lock!, plan, ledger);
            }

            return new StatusFixture(platform, paths, changeSetId, string.Empty, planStore, ledgerStore, journalStore, true);
        }

        public static StatusFixture CreateEnvironment()
        {
            var platform = CreatePlatform();
            var paths = new SetupRuntimePaths(platform);
            var changeSetId = Guid.Parse("00000000-0000-7000-8000-000000000667");
            var recordId = Guid.Parse("00000000-0000-7000-8000-000000000668");
            platform.SeedUserEnvironment("ENV_A", "old-a");
            platform.SeedUserEnvironment("ENV_B", "old-b");
            var environmentStep = new UserEnvironmentSetupStep(platform);
            var capture = environmentStep.Capture(["ENV_A", "ENV_B"]);
            var members = new[]
            {
                new SetupPrivatePlanMember("ENV_A", SetupOperation.Replace, "new-a"),
                new SetupPrivatePlanMember("ENV_B", SetupOperation.Replace, "new-b"),
            };
            var plan = new SetupPrivatePlan(
                1,
                changeSetId,
                "github-copilot",
                "copilot-cli",
                Now,
                "1.2.3",
                [new SetupPrivatePlanTarget(
                    recordId,
                    SetupTargetKind.Env,
                    "current-user",
                    capture.AggregateHash,
                    "environment-allowlist",
                    members)]);
            var changes = members.Select(member => new SetupMemberChangeResult(
                member.SettingKey,
                member.Operation,
                "present_different",
                "configured_loopback",
                "none",
                false)).ToArray();
            var ledger = new SetupLedgerChangeSet(
                changeSetId,
                "github-copilot",
                "copilot-cli",
                Now,
                Now,
                "1.2.3",
                null,
                SetupChangeSetState.Planned,
                [new SetupLedgerTarget(
                    recordId,
                    SetupTargetKind.Env,
                    "user-environment",
                    "github-copilot",
                    members.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray(),
                    capture.AggregateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
                    new SetupStatusProjection(
                        true,
                        "1.0.4",
                        SetupOperation.Replace,
                        SetupEffectiveSource.Environment,
                        "http://127.0.0.1:4320",
                        SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.CanonicalJson,
                        null,
                        changes),
                    "1.2.3")]);
            var planStore = new SetupPlanStore(platform, paths);
            var ledgerStore = new SetupLedgerStore(platform, paths, planStore);
            var journalStore = new SetupTransactionJournalStore(platform, paths);
            using (var acquisition = SetupLock.TryAcquire(platform, paths))
            {
                ledgerStore.PersistPlannedChangeSet(acquisition.Lock!, plan, ledger);
            }

            return new StatusFixture(platform, paths, changeSetId, string.Empty, planStore, ledgerStore, journalStore, false);
        }

        public void Apply()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            var result = new SetupApplyCoordinator(
                    Platform, Paths, planStore, ledgerStore, journalStore, new PassRevalidator())
                .Apply(acquisition.Lock!, ChangeSetId);
            Assert.Equal(guidanceOnly ? SetupChangeSetState.NoChanges : SetupChangeSetState.Applied, result.State);
        }

        public void Rollback()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            var result = new SetupRollbackCoordinator(Platform, Paths, planStore, ledgerStore, journalStore)
                .Rollback(acquisition.Lock!, ChangeSetId);
            Assert.True(result.Success);
        }

        public SetupChangeSetStatusResult Project()
        {
            var row = ledgerStore.LoadForRecovery().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            return new SetupStatusProjector(Platform, Paths, planStore, journalStore).Project(row);
        }

        public SetupRollbackPreflightResult EvaluateRollbackPreflight()
        {
            var row = ledgerStore.LoadForRecovery().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            SetupPrivatePlan? plan;
            SetupTransactionJournal? journal;
            try
            {
                plan = planStore.Load(ChangeSetId);
                journal = journalStore.Load(ChangeSetId);
            }
            catch (SetupStorageException)
            {
                plan = null;
                journal = null;
            }

            var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, row, journal);
            return preparation.Result ?? SetupRollbackPreflightEvaluator.Evaluate(
                preparation.Evidence!,
                new SetupRollbackPreflightObserver(Platform, Paths).Capture(preparation.Evidence!));
        }

        public void SetLifecycle(SetupChangeSetState state)
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            var ledger = ledgerStore.LoadForRecovery();
            var row = ledger.ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            var updatedTargets = row.Targets.Select(target => state switch
            {
                SetupChangeSetState.Planned or SetupChangeSetState.NoChanges => target with
                {
                    AppliedStateHash = null,
                    BackupReference = null,
                    RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
                },
                SetupChangeSetState.Restored or SetupChangeSetState.RolledBack => target with
                {
                    AppliedStateHash = null,
                    BackupReference = null,
                    RollbackStatus = SetupLedgerRollbackStatus.Succeeded,
                },
                _ => target,
            }).ToArray();
            ledgerStore.Save(acquisition.Lock!, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == ChangeSetId
                    ? row with { State = state, Targets = updatedTargets }
                    : changeSet).ToArray(),
            });
        }

        public void ApplyVariant(string variant)
        {
            var row = ledgerStore.LoadForRecovery().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            var changed = row.Targets.Single(target => target.Members.Any(member => member.Operation != SetupOperation.NoOp));
            var backup = Paths.GetBackup(ChangeSetId, changed.RecordId);
            switch (variant)
            {
                case "fresh":
                    break;
                case "target-drift":
                    SeedTarget("third-party");
                    break;
                case "missing-backup":
                    Platform.FileSystem.DeleteFile(backup);
                    break;
                case "corrupt-backup":
                    Platform.SeedFile(backup, Encoding.UTF8.GetBytes("PRIVATE_SECRET_BACKUP"));
                    break;
                case "reparse-backup":
                    Platform.SeedPathMetadata(backup, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                    break;
                case "all-noop-drift":
                    Platform.SeedUserEnvironment("ENV_NOOP", "drift");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public void ApplyArtifactVariant(string variant)
        {
            switch (variant)
            {
                case "missing-plan":
                    DeletePlan();
                    break;
                case "corrupt-plan":
                    Platform.SeedFile(Paths.GetPlan(ChangeSetId), Encoding.UTF8.GetBytes("PRIVATE_PLAN_SECRET"));
                    break;
                case "rebound-plan":
                    var plan = planStore.Load(ChangeSetId)!;
                    Platform.SeedFile(Paths.GetPlan(ChangeSetId), SetupPlanStore.Serialize(plan with { Adapter = "rebound" }));
                    break;
                case "missing-journal":
                    Platform.FileSystem.DeleteFile(Paths.GetTransactionJournal(ChangeSetId));
                    break;
                case "corrupt-journal":
                    Platform.SeedFile(Paths.GetTransactionJournal(ChangeSetId), Encoding.UTF8.GetBytes("PRIVATE_JOURNAL_SECRET"));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public void DeletePlan() => Platform.FileSystem.DeleteFile(Paths.GetPlan(ChangeSetId));

        public void SeedTarget(string value) => Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes(value));

        private static SetupTestPlatform CreatePlatform()
        {
            var platform = new SetupTestPlatform(Now);
            platform.SeedDirectory("C:\\");
            platform.SeedDirectory(platform.LocalApplicationData);
            return platform;
        }

        private static JsonElement CreateManifest(bool historical)
        {
            var current = SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.VsCode)!.CanonicalJson;
            if (!historical)
            {
                return current;
            }

            var node = JsonNode.Parse(current.GetRawText())!.AsObject();
            node["support_status"] = "planned";
            node["stability"] = "preview";
            using var document = JsonDocument.Parse(node.ToJsonString());
            return document.RootElement.Clone();
        }
    }

    private sealed class PassRevalidator : ISetupApplyRevalidator
    {
        public void Revalidate(SetupPrivatePlan plan, SetupLedgerChangeSet changeSet)
        {
        }
    }
}

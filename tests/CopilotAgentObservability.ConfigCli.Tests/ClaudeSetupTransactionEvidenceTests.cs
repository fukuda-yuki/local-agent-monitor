using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed partial class ClaudeCodeSetupAdapterTests
{
    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(false, "mixed")]
    [InlineData(false, "content-with-five-env")]
    [InlineData(true, "default-with-eight-env")]
    public void Rollback_TamperedSdkEvidenceFailsBeforeTargetObservation(
        bool includeContentCapture,
        string tamper)
    {
        var fixture = ClaudeTransactionEvidenceFixture.Create(includeContentCapture);
        fixture.TamperGuidance(tamper);
        var operationsBefore = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        fixture.AssertNoTargetActivity(operationsBefore);
    }

    [Theory]
    [InlineData(false, "unknown")]
    [InlineData(false, "mixed")]
    [InlineData(false, "content-with-five-env")]
    [InlineData(true, "default-with-eight-env")]
    public void Recovery_TamperedSdkEvidenceFailsBeforeTargetObservation(
        bool includeContentCapture,
        string tamper)
    {
        var fixture = ClaudeTransactionEvidenceFixture.Create(includeContentCapture);
        fixture.InterruptRollbackBeforeLedgerTransition();
        fixture.TamperGuidance(tamper);
        var operationsBefore = fixture.Platform.Operations.Count;

        var result = fixture.Recover();

        Assert.Equal(SetupRecoveryDisposition.Failed, result.Disposition);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        fixture.AssertNoTargetActivity(operationsBefore);
    }

    private sealed class ClaudeTransactionEvidenceFixture
    {
        private readonly SetupRuntimePaths paths;
        private readonly SetupPlanStore planStore;
        private readonly SetupLedgerStore ledgerStore;
        private readonly SetupTransactionJournalStore journalStore;

        private ClaudeTransactionEvidenceFixture(bool includeContentCapture)
        {
            Platform = ReadyWindowsPlatform("{}\n");
            SeedSettingsDirectoryChain();
            var registry = new SetupAdapterRegistry([CreateAdapter(Platform)]);
            var planned = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
                registry.Plan(Request("all", includeContentCapture))).Value;
            ChangeSetId = planned.PrivatePlan.ChangeSetId;
            paths = new SetupRuntimePaths(Platform);
            planStore = new SetupPlanStore(Platform, paths);
            ledgerStore = new SetupLedgerStore(Platform, paths, planStore);
            journalStore = new SetupTransactionJournalStore(Platform, paths);
            using var setupLock = Assert.IsType<SetupLock>(SetupLock.TryAcquire(Platform, paths).Lock);
            ledgerStore.PersistPlannedChangeSet(
                setupLock,
                planned.PrivatePlan,
                planned.PlannedChangeSet);
            ScriptVersionAndReadiness(Platform);
            var applied = new SetupApplyCoordinator(
                Platform,
                paths,
                planStore,
                ledgerStore,
                journalStore,
                registry).Apply(setupLock, ChangeSetId);
            Assert.Equal(SetupChangeSetState.Applied, applied.Value.State);
        }

        public SetupTestPlatform Platform { get; }

        private Guid ChangeSetId { get; }

        public static ClaudeTransactionEvidenceFixture Create(bool includeContentCapture) =>
            new(includeContentCapture);

        private void SeedSettingsDirectoryChain()
        {
            var directory = Path.GetDirectoryName(ClaudeSettingsPath)!;
            var current = Path.GetPathRoot(directory)!;
            Platform.SeedDirectory(current);
            foreach (var segment in directory[current.Length..].Split(
                         Path.DirectorySeparatorChar,
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                Platform.SeedDirectory(current);
            }
        }

        public void TamperGuidance(string tamper)
        {
            var plan = Assert.IsType<SetupPrivatePlan>(planStore.Load(ChangeSetId));
            var targets = plan.Targets.ToArray();
            var pythonIndex = Array.FindIndex(
                targets,
                target => target.TargetLocation == ClaudeAgentSdkGuidanceVariant.PythonLabel);
            var typeScriptIndex = Array.FindIndex(
                targets,
                target => target.TargetLocation == ClaudeAgentSdkGuidanceVariant.TypeScriptLabel);
            Assert.True(pythonIndex >= 0);
            Assert.True(typeScriptIndex >= 0);

            var defaultPython = ClaudeAgentSdkGuidanceVariant.DesiredState(
                ClaudeAgentSdkGuidanceVariant.PythonLabel,
                includeContentCapture: false);
            var defaultTypeScript = ClaudeAgentSdkGuidanceVariant.DesiredState(
                ClaudeAgentSdkGuidanceVariant.TypeScriptLabel,
                includeContentCapture: false);
            var contentPython = ClaudeAgentSdkGuidanceVariant.DesiredState(
                ClaudeAgentSdkGuidanceVariant.PythonLabel,
                includeContentCapture: true);
            var contentTypeScript = ClaudeAgentSdkGuidanceVariant.DesiredState(
                ClaudeAgentSdkGuidanceVariant.TypeScriptLabel,
                includeContentCapture: true);

            (SetupInlineDesiredState Python, SetupInlineDesiredState TypeScript) variants = tamper switch
            {
                "unknown" => (new SetupInlineDesiredState("unknown-sdk-variant"), defaultTypeScript),
                "mixed" => (contentPython, defaultTypeScript),
                "content-with-five-env" => (contentPython, contentTypeScript),
                "default-with-eight-env" => (defaultPython, defaultTypeScript),
                _ => throw new ArgumentOutOfRangeException(nameof(tamper)),
            };
            targets[pythonIndex] = targets[pythonIndex] with { DesiredState = variants.Python };
            targets[typeScriptIndex] = targets[typeScriptIndex] with { DesiredState = variants.TypeScript };
            Platform.SeedFile(
                paths.GetPlan(ChangeSetId),
                SetupPlanStore.Serialize(plan with { Targets = targets }));
        }

        public void InterruptRollbackBeforeLedgerTransition()
        {
            Platform.InjectFault(
                $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}",
                new IOException("PRIVATE_CLAUDE_ROLLBACK_PREPARED"));

            var result = Rollback();

            Assert.False(result.Success);
            Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
            Assert.Equal(
                SetupJournalPhase.Prepared,
                Assert.IsType<SetupTransactionJournal>(journalStore.Load(ChangeSetId)).Phase);
        }

        public SetupRollbackExecutionResult Rollback()
        {
            using var setupLock = Assert.IsType<SetupLock>(SetupLock.TryAcquire(Platform, paths).Lock);
            return new SetupRollbackCoordinator(
                Platform,
                paths,
                new SetupPlanStore(Platform, paths),
                new SetupLedgerStore(Platform, paths, new SetupPlanStore(Platform, paths)),
                new SetupTransactionJournalStore(Platform, paths)).Rollback(setupLock, ChangeSetId);
        }

        public SetupRecoveryResult Recover()
        {
            var reopenedPlanStore = new SetupPlanStore(Platform, paths);
            using var setupLock = Assert.IsType<SetupLock>(SetupLock.TryAcquire(Platform, paths).Lock);
            return new SetupRecoveryCoordinator(
                Platform,
                paths,
                reopenedPlanStore,
                new SetupLedgerStore(Platform, paths, reopenedPlanStore),
                new SetupTransactionJournalStore(Platform, paths)).RecoverNext(setupLock);
        }

        public void AssertNoTargetActivity(int operationsBefore)
        {
            var operations = Platform.Operations.Skip(operationsBefore).ToArray();
            Assert.DoesNotContain(
                operations,
                operation => operation.Contains(ClaudeSettingsPath, StringComparison.Ordinal));
            Assert.DoesNotContain(
                operations,
                operation => operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
                    operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
                    operation == "environment.notify");
        }
    }
}

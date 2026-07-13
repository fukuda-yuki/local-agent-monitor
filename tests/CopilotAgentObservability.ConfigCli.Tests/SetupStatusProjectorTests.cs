using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
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
        Assert.Equal(
            variant == "target-drift" ? SetupCurrentState.Stale : SetupCurrentState.Current,
            changed.CurrentState);
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
        if (state is not SetupChangeSetState.Planned and not SetupChangeSetState.NoChanges and not SetupChangeSetState.Restored)
        {
            fixture.Apply();
        }

        if (state == SetupChangeSetState.RolledBack)
        {
            fixture.Rollback();
        }

        if (state == SetupChangeSetState.Restored)
        {
            fixture.ArrangeLifecycle(state, "apply", "restored");
        }
        else
        {
            fixture.SetLifecycle(state);
        }

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
        fixture.MakeRollbackPartial();
        fixture.SeedTarget(currentValue);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(expectedCurrent, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public async Task Project_ApplyingLifecycle_UsesCoherentProducerJournalBaseReference()
    {
        var fixture = StatusFixture.Create();
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}");
        var apply = Task.Run(fixture.ApplyWithoutAssert);
        barrier.WaitUntilReached(CancellationToken.None);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.Base, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
        barrier.Release();
        Assert.Equal(SetupChangeSetState.Applied, (await apply).State);
    }

    [Fact]
    public void Project_PlannedWithPreparedApplyJournal_IsDormantBaseCurrentWithoutMutation()
    {
        var fixture = StatusFixture.Create();
        fixture.PrepareApplying(markApplying: false);
        fixture.SetLifecycle(SetupChangeSetState.Planned);
        var before = fixture.CaptureArtifacts();

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.Base, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
        Assert.Equal(before, fixture.CaptureArtifacts());
    }

    [Fact]
    public void Project_DormantPreparedJournalEvidenceMismatch_RequiresRecoveryWithoutPrivateData()
    {
        var fixture = StatusFixture.Create();
        fixture.PrepareApplying(markApplying: false);
        fixture.SetLifecycle(SetupChangeSetState.Planned);
        fixture.RebindJournal("desired-hash");

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.DoesNotContain(fixture.TargetPath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applying, "apply", "compensating", SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.Compensating, "apply", "partial", SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.RollingBack, "rollback", "partial", SetupReferenceState.Desired)]
    [InlineData(SetupChangeSetState.Partial, "apply", "compensating", SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.Partial, "rollback", "rolling_back", SetupReferenceState.Desired)]
    public void Project_LegalActiveLifecyclePair_UsesJournalBoundReference(
        SetupChangeSetState state,
        string operation,
        string phase,
        SetupReferenceState expectedReference)
    {
        var fixture = StatusFixture.Create();
        fixture.ArrangeLifecycle(state, operation, phase);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applying, "apply", "committed", SetupReferenceState.Desired)]
    [InlineData(SetupChangeSetState.Compensating, "apply", "restored", SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.RollingBack, "rollback", "committed", SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.Partial, "rollback", "committed", SetupReferenceState.Previous)]
    public void Project_LaggingTerminalJournalPair_UsesCompletedReference(
        SetupChangeSetState state,
        string operation,
        string phase,
        SetupReferenceState expectedReference)
    {
        var fixture = StatusFixture.Create();
        fixture.ArrangeLifecycle(state, operation, phase);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void Project_AppliedWithPreparedRollbackJournal_IsDormantDesiredCurrent()
    {
        var fixture = StatusFixture.Create();
        fixture.ArrangeLifecycle(
            SetupChangeSetState.Applied,
            "rollback",
            "prepared");

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.Desired, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void Project_AppliedWithPreparedRollbackArtifactFailure_IsUnavailableWithoutPrivateData()
    {
        var fixture = StatusFixture.Create();
        fixture.ArrangeLifecycle(
            SetupChangeSetState.Applied,
            "rollback",
            "prepared");
        fixture.ApplyArtifactVariant("missing-backup");

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.None, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Unavailable, target.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
        Assert.DoesNotContain(fixture.TargetPath, result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Planned, "apply", "applying")]
    [InlineData(SetupChangeSetState.Applying, "apply", "partial")]
    [InlineData(SetupChangeSetState.Compensating, "apply", "applying")]
    [InlineData(SetupChangeSetState.RollingBack, "rollback", "compensating")]
    [InlineData(SetupChangeSetState.Partial, "apply", "applying")]
    [InlineData(SetupChangeSetState.Partial, "rollback", "prepared")]
    public void Project_InvalidNeighborLifecyclePair_RequiresRecovery(
        SetupChangeSetState state,
        string operation,
        string phase)
    {
        var fixture = StatusFixture.Create();
        fixture.ArrangeLifecycle(state, operation, phase);

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applying, "apply", "applying", SetupReferenceState.Base, SetupReferenceState.Base)]
    [InlineData(SetupChangeSetState.Compensating, "apply", "compensating", SetupReferenceState.Previous, SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.RollingBack, "rollback", "rolling_back", SetupReferenceState.Desired, SetupReferenceState.Previous)]
    [InlineData(SetupChangeSetState.Partial, "apply", "compensating", SetupReferenceState.Previous, SetupReferenceState.Desired)]
    public void Project_ActiveMixedChangedAndAllNoOpTargets_UsesLazyReferencesInPlanOrder(
        SetupChangeSetState state,
        string operation,
        string phase,
        SetupReferenceState expectedChangedReference,
        SetupReferenceState expectedNoOpReference)
    {
        var fixture = StatusFixture.Create(includeAllNoOpEnvironment: true);
        fixture.ArrangeLifecycle(state, operation, phase);
        var expectedOrder = fixture.RecordIds;

        var result = fixture.Project();

        Assert.Equal(expectedOrder, result.Targets.Select(target => Guid.Parse(target.RecordId)));
        Assert.Equal(expectedChangedReference, result.Targets[0].ReferenceState);
        Assert.Equal(expectedNoOpReference, result.Targets[1].ReferenceState);
        Assert.All(result.Targets, target =>
        {
            Assert.Equal(SetupCurrentState.Current, target.CurrentState);
            Assert.False(target.RollbackAvailable);
        });
        Assert.Equal(SetupCurrentState.Current, result.CurrentState);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public async Task Project_CompensatingLifecycle_UsesCoherentProducerJournalDesiredReference()
    {
        var fixture = StatusFixture.Create();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterCompletionBeforeCommit}",
            new IOException("synthetic"));
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterRestoreIntentBeforeRestore}");
        var apply = Task.Run(fixture.ApplyWithoutAssert);
        barrier.WaitUntilReached(CancellationToken.None);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.Desired, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(result.RollbackAvailable);
        barrier.Release();
        await Assert.ThrowsAsync<SetupApplyException>(async () => await apply);
    }

    [Fact]
    public async Task Project_RollingBackLifecycle_UsesCoherentProducerJournalDesiredReference()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        using var barrier = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}");
        var rollback = Task.Run(fixture.RollbackWithoutAssert);
        barrier.WaitUntilReached(CancellationToken.None);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.Desired, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.False(result.RollbackAvailable);
        barrier.Release();
        Assert.True((await rollback).Success);
    }

    [Fact]
    public void Project_ActiveLifecycleRejectsCompletedJournalReboundToActiveLedger()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        fixture.SetLifecycle(SetupChangeSetState.Applying);

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
    }

    [Fact]
    public void Project_PartialEnvironmentWithDesiredPreviousMixture_IsDiverged()
    {
        var fixture = StatusFixture.CreateEnvironment();
        fixture.Apply();
        fixture.MakeRollbackPartial();
        fixture.Platform.SeedUserEnvironment("ENV_A", "old-a");

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

    [Theory]
    [InlineData("operation")]
    [InlineData("phase")]
    [InlineData("target-id")]
    [InlineData("target-kind")]
    [InlineData("prior-hash")]
    [InlineData("desired-hash")]
    [InlineData("backup-reference")]
    [InlineData("step-phase")]
    [InlineData("notification")]
    public void Project_ActiveJournalMismatch_RequiresRecoveryWithoutPrivateData(string variant)
    {
        var fixture = StatusFixture.Create();
        fixture.PrepareApplying();
        fixture.RebindJournal(variant);

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.DoesNotContain(fixture.TargetPath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE_ACTIVE_SECRET", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Project_ActiveEnvironmentMemberMismatch_RequiresRecovery()
    {
        var fixture = StatusFixture.CreateEnvironment();
        fixture.PrepareApplying();
        fixture.RebindJournal("member-key");

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
    }

    [Fact]
    public void Project_ActiveOwnershipMismatch_RequiresRecovery()
    {
        var fixture = StatusFixture.Create();
        fixture.PrepareApplying();
        fixture.RebindActiveOwnership();

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
    }

    [Fact]
    public async Task Project_AppliedTargets_UsesOneCoherentCurrentObservationForStateAndRollback()
    {
        var fixture = StatusFixture.Create(additionalFileCount: 1);
        fixture.Apply();
        using var barrier = fixture.Platform.AddBarrier($"file.read:{fixture.AdditionalTargetPath}");
        var projection = Task.Run(fixture.Project);
        barrier.WaitUntilReached(CancellationToken.None);
        fixture.SeedTarget("third-party-after-first-observation");
        barrier.Release();

        var result = await projection;

        var first = result.Targets[0];
        Assert.Equal(SetupCurrentState.Current, first.CurrentState);
        Assert.True(first.RollbackAvailable);
        Assert.True(result.RollbackAvailable);
    }

    [Theory]
    [InlineData("desired", SetupReferenceState.Desired, SetupCurrentState.Current)]
    [InlineData("diverged", SetupReferenceState.None, SetupCurrentState.Diverged)]
    [InlineData("unavailable", SetupReferenceState.None, SetupCurrentState.Unavailable)]
    public void Project_ApplyingTarget_ClassifiesFreshJournalBoundState(
        string variant,
        SetupReferenceState expectedReference,
        SetupCurrentState expectedCurrent)
    {
        var fixture = StatusFixture.Create();
        fixture.PrepareApplying();
        fixture.ApplyCurrentVariant(variant);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(expectedCurrent, target.CurrentState);
        Assert.False(target.RollbackAvailable);
    }

    [Theory]
    [InlineData("unavailable", SetupCurrentState.Unavailable)]
    [InlineData("missing", SetupCurrentState.Diverged)]
    public void Project_PartialTarget_DistinguishesUnavailableFromCanonicalMissing(
        string variant,
        SetupCurrentState expected)
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        fixture.MakeRollbackPartial();
        fixture.ApplyCurrentVariant(variant);

        var result = fixture.Project();

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupReferenceState.None, target.ReferenceState);
        Assert.Equal(expected, target.CurrentState);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void Project_MultiWritable_DivergedPrecedesUnavailableAndCurrent()
    {
        var fixture = StatusFixture.Create(additionalFileCount: 2);
        fixture.Apply();
        fixture.MakeRollbackPartial();
        fixture.SeedTarget("third-party");
        fixture.MakeAdditionalTargetUnavailable(1);

        var result = fixture.Project();

        Assert.Equal(SetupCurrentState.Diverged, result.CurrentState);
    }

    [Fact]
    public void Project_MultiWritable_StalePrecedesUnavailableAndCurrent()
    {
        var fixture = StatusFixture.Create(additionalFileCount: 2);
        fixture.Apply();
        fixture.SeedTarget("third-party");
        fixture.MakeAdditionalTargetUnavailable(1);

        var result = fixture.Project();

        Assert.Equal(SetupCurrentState.Stale, result.CurrentState);
    }

    [Fact]
    public void Project_MultiWritable_UnavailablePrecedesCurrentAndPreservesOrder()
    {
        var fixture = StatusFixture.Create(additionalFileCount: 2);
        fixture.MakeAdditionalTargetUnavailable(1);
        var expectedOrder = fixture.RecordIds;

        var result = fixture.Project();

        Assert.Equal(SetupCurrentState.Unavailable, result.CurrentState);
        Assert.Equal(expectedOrder, result.Targets.Select(target => Guid.Parse(target.RecordId)));
    }

    [Fact]
    public void Project_DoesNotMutateTargetLedgerPlanOrJournal()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        var before = fixture.CaptureArtifacts();

        _ = fixture.Project();

        Assert.Equal(before, fixture.CaptureArtifacts());
    }

    [Fact]
    public void Projector_HasNoAdapterDetectionDependency()
    {
        var parameters = typeof(SetupStatusProjector).GetConstructors()
            .SelectMany(constructor => constructor.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        Assert.DoesNotContain(parameters, type => typeof(ISetupAdapter).IsAssignableFrom(type));
        Assert.DoesNotContain(typeof(SetupAdapterRegistry), parameters);
    }

    [Theory]
    [InlineData("missing-plan")]
    [InlineData("corrupt-plan")]
    [InlineData("rebound-plan")]
    [InlineData("missing-journal")]
    [InlineData("corrupt-journal")]
    [InlineData("rebound-journal")]
    [InlineData("missing-backup")]
    public void Project_NonTerminalArtifactFailure_RequiresRecoveryWithoutPrivateData(string variant)
    {
        var fixture = StatusFixture.Create();
        fixture.PrepareApplying();
        fixture.ApplyArtifactVariant(variant);

        var exception = Assert.Throws<SetupStorageException>(() => fixture.Project());

        Assert.Equal(SetupCodes.RecoveryRequired, exception.Code);
        Assert.DoesNotContain(fixture.TargetPath, exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applied, "desired", SetupReferenceState.Desired, SetupCurrentState.Current)]
    [InlineData(SetupChangeSetState.Applied, "previous", SetupReferenceState.Previous, SetupCurrentState.Current)]
    [InlineData(SetupChangeSetState.Applied, "third-party", SetupReferenceState.None, SetupCurrentState.Diverged)]
    [InlineData(SetupChangeSetState.Applied, "unavailable", SetupReferenceState.None, SetupCurrentState.Unavailable)]
    [InlineData(SetupChangeSetState.Restored, "desired", SetupReferenceState.Desired, SetupCurrentState.Current)]
    [InlineData(SetupChangeSetState.Restored, "previous", SetupReferenceState.Previous, SetupCurrentState.Current)]
    [InlineData(SetupChangeSetState.Restored, "third-party", SetupReferenceState.None, SetupCurrentState.Diverged)]
    [InlineData(SetupChangeSetState.Restored, "unavailable", SetupReferenceState.None, SetupCurrentState.Unavailable)]
    [InlineData(SetupChangeSetState.RolledBack, "desired", SetupReferenceState.Desired, SetupCurrentState.Current)]
    [InlineData(SetupChangeSetState.RolledBack, "previous", SetupReferenceState.Previous, SetupCurrentState.Current)]
    [InlineData(SetupChangeSetState.RolledBack, "third-party", SetupReferenceState.None, SetupCurrentState.Diverged)]
    [InlineData(SetupChangeSetState.RolledBack, "unavailable", SetupReferenceState.None, SetupCurrentState.Unavailable)]
    public void ProjectFailedRecovery_UsesDurableTerminalEvidenceAndEffectivePartialLifecycle(
        SetupChangeSetState durableState,
        string current,
        SetupReferenceState expectedReference,
        SetupCurrentState expectedCurrent)
    {
        var fixture = StatusFixture.Create();
        fixture.ArrangeDurableTerminal(durableState);
        fixture.ApplyCurrentVariant(current);
        var durable = fixture.LoadChangeSet();
        var effective = FailedRecoveryOverlay(durable);

        var result = fixture.ProjectFailedRecovery(durable, effective);

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupChangeSetState.Partial, result.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.OutcomeCode);
        Assert.Equal(SetupStorageJson.FormatTimestamp(effective.UpdatedAt), result.UpdatedAt);
        Assert.Equal(expectedReference, target.ReferenceState);
        Assert.Equal(expectedCurrent, target.CurrentState);
        Assert.Equal(expectedCurrent, result.CurrentState);
        Assert.False(target.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void ProjectFailedRecovery_AllNoOpTargetUsesDesiredTieWithoutRollbackOwnership()
    {
        var fixture = StatusFixture.Create(includeAllNoOpEnvironment: true);
        fixture.Apply();
        var durable = fixture.LoadChangeSet();

        var result = fixture.ProjectFailedRecovery(durable, FailedRecoveryOverlay(durable));

        var noOp = Assert.Single(result.Targets, target => target.Operation == SetupOperation.NoOp);
        Assert.Equal(SetupReferenceState.Desired, noOp.ReferenceState);
        Assert.Equal(SetupCurrentState.Current, noOp.CurrentState);
        Assert.False(noOp.RollbackAvailable);
        Assert.False(result.RollbackAvailable);
    }

    [Theory]
    [InlineData("state")]
    [InlineData("outcome")]
    [InlineData("target-outcome")]
    [InlineData("target-rollback")]
    [InlineData("adapter")]
    [InlineData("snapshot")]
    public void ProjectFailedRecovery_RejectsInvalidOrReboundEffectiveOverlay(string variant)
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        var durable = fixture.LoadChangeSet();
        var effective = FailedRecoveryOverlay(durable);
        effective = variant switch
        {
            "state" => effective with { State = SetupChangeSetState.Applied },
            "outcome" => effective with { OutcomeCode = SetupCodes.ApplySucceeded },
            "target-outcome" => effective with
            {
                Targets = effective.Targets.Select(target => target with { OutcomeCode = SetupCodes.ApplySucceeded }).ToArray(),
            },
            "target-rollback" => effective with
            {
                Targets = effective.Targets.Select(target => target with { RollbackStatus = SetupLedgerRollbackStatus.Pending }).ToArray(),
            },
            "adapter" => effective with { Adapter = "other-adapter" },
            "snapshot" => effective with
            {
                Targets = effective.Targets.Select(target => target with
                {
                    StatusProjection = target.StatusProjection with { DetectedVersion = "9.9.9" },
                }).ToArray(),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(variant)),
        };

        Assert.Throws<FormatException>(() => fixture.ProjectFailedRecovery(durable, effective));
    }

    [Fact]
    public void ProjectFailedRecovery_MissingDurableArtifactFailsClosedAsEffectivePartialUnavailable()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        var durable = fixture.LoadChangeSet();
        fixture.DeletePlan();

        var result = fixture.ProjectFailedRecovery(durable, FailedRecoveryOverlay(durable));

        var target = Assert.Single(result.Targets);
        Assert.Equal(SetupChangeSetState.Partial, result.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.OutcomeCode);
        Assert.Equal(SetupReferenceState.None, target.ReferenceState);
        Assert.Equal(SetupCurrentState.Unavailable, target.CurrentState);
        Assert.False(result.RollbackAvailable);
    }

    [Fact]
    public void ProjectFailedRecovery_ObservesEachPhysicalTargetOnce()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        var durable = fixture.LoadChangeSet();
        var readsBefore = fixture.Platform.Operations.Count(operation =>
            operation == $"file.read:{fixture.TargetPath}");
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, Assert.Single(durable.Targets).RecordId);
        var backupReadsBefore = fixture.Platform.Operations.Count(operation =>
            operation == $"file.read:{backup}");

        fixture.ProjectFailedRecovery(durable, FailedRecoveryOverlay(durable));

        Assert.Equal(readsBefore + 1, fixture.Platform.Operations.Count(operation =>
            operation == $"file.read:{fixture.TargetPath}"));
        Assert.Equal(backupReadsBefore + 1, fixture.Platform.Operations.Count(operation =>
            operation == $"file.read:{backup}"));
    }

    [Fact]
    public void ProjectFailedRecovery_AcceptsSemanticallyEqualOverlayAfterLedgerRoundTrip()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        var durable = fixture.LoadChangeSet();
        var effective = RoundTripLedgerRow(FailedRecoveryOverlay(durable));

        var result = fixture.ProjectFailedRecovery(durable, effective);

        Assert.Equal(SetupChangeSetState.Partial, result.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.OutcomeCode);
    }

    [Fact]
    public void ProjectFailedRecovery_AcceptsExpectedResultWithReorderedObjectProperties()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        var durable = fixture.LoadChangeSet();
        var effective = FailedRecoveryOverlay(durable);
        effective = effective with
        {
            Targets = effective.Targets.Select(target => target with
            {
                StatusProjection = target.StatusProjection with
                {
                    ExpectedResult = ReverseObjectProperties(target.StatusProjection.ExpectedResult!.Value),
                },
            }).ToArray(),
        };

        var result = fixture.ProjectFailedRecovery(durable, effective);

        Assert.Equal(SetupChangeSetState.Partial, result.State);
        Assert.Equal(SetupCodes.InterruptedRecoveryFailed, result.OutcomeCode);
    }

    [Fact]
    public void Project_ExistingSingleRowApiRetainsAppliedProjectionParity()
    {
        var fixture = StatusFixture.Create();
        fixture.Apply();
        var durable = fixture.LoadChangeSet();

        var existing = fixture.Project();
        var direct = new SetupStatusProjector(fixture.Platform, fixture.Paths, fixture.PlanStore, fixture.JournalStore)
            .Project(durable);

        Assert.Equivalent(existing, direct, strict: true);
    }

    private static SetupLedgerChangeSet FailedRecoveryOverlay(SetupLedgerChangeSet durable) => durable with
    {
        UpdatedAt = durable.UpdatedAt.AddMinutes(1),
        State = SetupChangeSetState.Partial,
        OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
        Targets = durable.Targets.Select(target => target with
        {
            OutcomeCode = SetupCodes.InterruptedRecoveryFailed,
            RollbackStatus = SetupLedgerRollbackStatus.NotAvailable,
        }).ToArray(),
    };

    private static SetupLedgerChangeSet RoundTripLedgerRow(SetupLedgerChangeSet row)
    {
        var platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 14, 1, 2, 3, TimeSpan.Zero));
        var paths = new SetupRuntimePaths(platform);
        platform.SeedFile(
            paths.OwnershipLedger,
            SetupLedgerStore.Serialize(new SetupOwnershipLedger(1, [row])));
        return new SetupLedgerStore(platform, paths, new SetupPlanStore(platform, paths))
            .LoadForRecovery()
            .ChangeSets
            .Single();
    }

    private static JsonElement ReverseObjectProperties(JsonElement element)
    {
        var source = JsonNode.Parse(element.GetRawText())!.AsObject();
        var reordered = new JsonObject();
        foreach (var property in source.Reverse())
        {
            reordered[property.Key] = property.Value?.DeepClone();
        }

        return JsonDocument.Parse(reordered.ToJsonString()).RootElement.Clone();
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
        public SetupPlanStore PlanStore => planStore;
        public SetupTransactionJournalStore JournalStore => journalStore;
        public Guid ChangeSetId { get; }
        public string TargetPath { get; }
        public string AdditionalTargetPath => "C:\\private-status\\settings-1.json";
        public IReadOnlyList<Guid> RecordIds => ledgerStore.LoadForRecovery().ChangeSets
            .Single(changeSet => changeSet.ChangeSetId == ChangeSetId)
            .Targets.Select(target => target.RecordId).ToArray();

        public static StatusFixture Create(
            SetupOperation operation = SetupOperation.Replace,
            bool includeAllNoOpEnvironment = false,
            bool historicalManifest = false,
            int additionalFileCount = 0)
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
            for (var index = 1; index <= additionalFileCount; index++)
            {
                var additionalId = Guid.Parse($"00000000-0000-7000-8000-{670 + index:D12}");
                var additionalPath = $"C:\\private-status\\settings-{index}.json";
                var previous = $"previous-{index}";
                var additionalDesired = $"desired-{index}";
                const string settingKey = "github.copilot.chat.otel.enabled";
                platform.SeedFile(additionalPath, Encoding.UTF8.GetBytes(previous));
                var additionalBase = fileStep.Capture("C:\\private-status", additionalPath).Hash;
                planTargets.Add(new SetupPrivatePlanTarget(
                    additionalId,
                    SetupTargetKind.Json,
                    additionalPath,
                    additionalBase,
                    additionalDesired,
                    [new SetupPrivatePlanMember(settingKey, SetupOperation.Replace, "safe-state")]));
                ledgerTargets.Add(new SetupLedgerTarget(
                    additionalId,
                    SetupTargetKind.Json,
                    "vscode-user-settings",
                    "github-copilot",
                    [new SetupLedgerMember(settingKey, SetupOperation.Replace)],
                    additionalBase,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartVsCode,
                    new SetupStatusProjection(
                        true,
                        "1.128.7",
                        SetupOperation.Replace,
                        SetupEffectiveSource.UserSetting,
                        "http://127.0.0.1:4320",
                        CreateManifest(historicalManifest),
                        null,
                        [new SetupMemberChangeResult(
                            settingKey,
                            SetupOperation.Replace,
                            "planned_previous",
                            "configured_loopback",
                            "none",
                            false)]),
                    "1.2.3"));
            }
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
            var result = ApplyWithoutAssert();
            Assert.Equal(guidanceOnly ? SetupChangeSetState.NoChanges : SetupChangeSetState.Applied, result.State);
        }

        public SetupLedgerChangeSet ApplyWithoutAssert()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return new SetupApplyCoordinator(
                    Platform, Paths, planStore, ledgerStore, journalStore, new PassRevalidator())
                .Apply(acquisition.Lock!, ChangeSetId).Value;
        }

        public void Rollback()
        {
            var result = RollbackWithoutAssert();
            Assert.True(result.Success);
        }

        public SetupRollbackExecutionResult RollbackWithoutAssert()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return new SetupRollbackCoordinator(Platform, Paths, planStore, ledgerStore, journalStore)
                .Rollback(acquisition.Lock!, ChangeSetId);
        }

        public void ArrangeDurableTerminal(SetupChangeSetState state)
        {
            switch (state)
            {
                case SetupChangeSetState.Applied:
                    Apply();
                    break;
                case SetupChangeSetState.Restored:
                    ArrangeLifecycle(SetupChangeSetState.Restored, "apply", "restored");
                    break;
                case SetupChangeSetState.RolledBack:
                    Apply();
                    Rollback();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(state));
            }
        }

        public void MakeRollbackPartial()
        {
            var row = ledgerStore.LoadForRecovery().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            var plan = planStore.Load(ChangeSetId)!;
            var journal = journalStore.Load(ChangeSetId)!;
            var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, row, journal);
            var preflight = SetupRollbackPreflightEvaluator.Evaluate(
                preparation.Evidence!,
                new SetupRollbackPreflightObserver(Platform, Paths).Capture(preparation.Evidence!));
            Assert.True(preflight.IsAvailable);

            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            journalStore.SupersedeWithPreparedRollback(acquisition.Lock!, ChangeSetId, preflight.RollbackTargets);
            journalStore.MarkTransactionPhase(acquisition.Lock!, ChangeSetId, SetupJournalPhase.RollingBack);
            journalStore.MarkTransactionPhase(acquisition.Lock!, ChangeSetId, SetupJournalPhase.Partial);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(acquisition.Lock!, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == ChangeSetId
                    ? changeSet with
                    {
                        State = SetupChangeSetState.Partial,
                        OutcomeCode = SetupCodes.PartialRollback,
                    }
                    : changeSet).ToArray(),
            });
        }

        public void ArrangeLifecycle(
            SetupChangeSetState state,
            string operation,
            string phase)
        {
            var journalOperation = operation switch
            {
                "apply" => SetupJournalOperation.Apply,
                "rollback" => SetupJournalOperation.Rollback,
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            var journalPhase = phase switch
            {
                "prepared" => SetupJournalPhase.Prepared,
                "applying" => SetupJournalPhase.Applying,
                "compensating" => SetupJournalPhase.Compensating,
                "rolling_back" => SetupJournalPhase.RollingBack,
                "committed" => SetupJournalPhase.Committed,
                "restored" => SetupJournalPhase.Restored,
                "partial" => SetupJournalPhase.Partial,
                _ => throw new ArgumentOutOfRangeException(nameof(phase)),
            };
            if (journalOperation == SetupJournalOperation.Apply)
            {
                PrepareApplying(markApplying: false);
            }
            else
            {
                Apply();
                PrepareRollback();
            }

            RewriteJournalPhase(journalOperation, journalPhase);
            SetLifecycle(state);
        }

        private void PrepareRollback()
        {
            var row = ledgerStore.LoadForRecovery().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            var plan = planStore.Load(ChangeSetId)!;
            var journal = journalStore.Load(ChangeSetId)!;
            var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, row, journal);
            var preflight = SetupRollbackPreflightEvaluator.Evaluate(
                preparation.Evidence!,
                new SetupRollbackPreflightObserver(Platform, Paths).Capture(preparation.Evidence!));
            Assert.True(preflight.IsAvailable);

            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            journalStore.SupersedeWithPreparedRollback(acquisition.Lock!, ChangeSetId, preflight.RollbackTargets);
        }

        private void RewriteJournalPhase(SetupJournalOperation operation, SetupJournalPhase phase)
        {
            var path = Paths.GetTransactionJournal(ChangeSetId);
            var root = JsonNode.Parse(Platform.ReadSeededFile(path))!.AsObject();
            Assert.Equal(operation == SetupJournalOperation.Apply ? "apply" : "rollback", root["operation"]!.GetValue<string>());
            root["phase"] = phase switch
            {
                SetupJournalPhase.Prepared => "prepared",
                SetupJournalPhase.Applying => "applying",
                SetupJournalPhase.Compensating => "compensating",
                SetupJournalPhase.RollingBack => "rolling_back",
                SetupJournalPhase.Committed => "committed",
                SetupJournalPhase.Restored => "restored",
                SetupJournalPhase.Partial => "partial",
                _ => throw new ArgumentOutOfRangeException(nameof(phase)),
            };
            var stepPhase = phase switch
            {
                SetupJournalPhase.Committed when operation == SetupJournalOperation.Apply => "mutation_completed",
                SetupJournalPhase.Committed or SetupJournalPhase.Restored => "restore_completed",
                _ => "pending",
            };
            foreach (var target in root["targets"]!.AsArray())
            {
                foreach (var step in target!["steps"]!.AsArray())
                {
                    step!["phase"] = stepPhase;
                }
            }

            if (phase == SetupJournalPhase.Committed && operation == SetupJournalOperation.Apply)
            {
                SeedTarget("desired");
            }
            else if (phase is SetupJournalPhase.Committed or SetupJournalPhase.Restored)
            {
                SeedTarget("previous");
            }

            Platform.SeedFile(path, Encoding.UTF8.GetBytes(root.ToJsonString()));
        }

        public void PrepareApplying(bool markApplying = true)
        {
            var plan = planStore.Load(ChangeSetId)!;
            var journalTargets = new List<SetupJournalTarget>();
            Platform.FileSystem.CreateDirectory(Paths.Backups);
            foreach (var target in plan.Targets.Where(target =>
                         target.TargetKind != SetupTargetKind.Guidance &&
                         target.Members.Any(member => member.Operation != SetupOperation.NoOp)))
            {
                var backupReference = target.RecordId.ToString("D");
                if (target.TargetKind == SetupTargetKind.Env)
                {
                    var environmentStep = new UserEnvironmentSetupStep(Platform);
                    var capture = environmentStep.Capture(target.Members.Select(member => member.SettingKey).ToArray());
                    environmentStep.CreateBackup(Paths.GetBackup(ChangeSetId, target.RecordId), capture);
                    var steps = target.Members.Select((member, index) => (member, index))
                        .Where(item => item.member.Operation != SetupOperation.NoOp)
                        .Select(item => new SetupJournalStep(
                            item.member.SettingKey,
                            capture.Members[item.index].Hash,
                            UserEnvironmentSetupStep.HashPlannedMember(item.member),
                            backupReference,
                            SetupJournalStepPhase.Pending))
                        .ToArray();
                    journalTargets.Add(new SetupJournalTarget(target.RecordId, target.TargetKind, steps));
                }
                else
                {
                    var fileStep = new AtomicFileSetupStep(Platform);
                    var capture = fileStep.Capture(Path.GetDirectoryName(target.TargetLocation)!, target.TargetLocation);
                    fileStep.CreateBackup(Paths.GetBackup(ChangeSetId, target.RecordId), capture);
                    journalTargets.Add(new SetupJournalTarget(
                        target.RecordId,
                        target.TargetKind,
                        [new SetupJournalStep(
                            null,
                            capture.Hash,
                            SetupHash.File(true, Encoding.UTF8.GetBytes(target.DesiredState)),
                            backupReference,
                            SetupJournalStepPhase.Pending)]));
                }
            }

            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            journalStore.CreatePrepared(acquisition.Lock!, ChangeSetId, SetupJournalOperation.Apply, journalTargets);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(acquisition.Lock!, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == ChangeSetId
                    ? changeSet with
                    {
                        State = SetupChangeSetState.Applying,
                        Targets = changeSet.Targets.Select(target => journalTargets.Any(item => item.RecordId == target.RecordId)
                            ? target with
                            {
                                BackupReference = target.RecordId.ToString("D"),
                                RollbackStatus = SetupLedgerRollbackStatus.Pending,
                            }
                            : target).ToArray(),
                    }
                    : changeSet).ToArray(),
            });
            if (markApplying)
            {
                journalStore.MarkTransactionPhase(acquisition.Lock!, ChangeSetId, SetupJournalPhase.Applying);
            }
        }

        public void RebindJournal(string variant)
        {
            var path = Paths.GetTransactionJournal(ChangeSetId);
            var root = JsonNode.Parse(Platform.ReadSeededFile(path))!.AsObject();
            var target = root["targets"]!.AsArray()[0]!.AsObject();
            var step = target["steps"]!.AsArray()[0]!.AsObject();
            switch (variant)
            {
                case "operation":
                    root["operation"] = "rollback";
                    root["phase"] = "rolling_back";
                    break;
                case "phase":
                    root["phase"] = "partial";
                    break;
                case "target-id":
                    target["record_id"] = "00000000-0000-7000-8000-000000009999";
                    break;
                case "target-kind":
                    target["target_kind"] = "toml";
                    break;
                case "member-key":
                    step["member_key"] = "ENV_REBOUND";
                    break;
                case "prior-hash":
                    step["prior_state_hash"] = new string('0', 64);
                    break;
                case "desired-hash":
                    step["desired_state_hash"] = new string('f', 64);
                    break;
                case "backup-reference":
                    step["backup_reference"] = "rebound";
                    break;
                case "step-phase":
                    step["phase"] = "restore_started";
                    break;
                case "notification":
                    root["environment_notification"] = "pending";
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }

            Platform.SeedFile(path, Encoding.UTF8.GetBytes(root.ToJsonString()));
        }

        public void RebindActiveOwnership()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            var ledger = ledgerStore.LoadForRecovery();
            ledgerStore.Save(acquisition.Lock!, ledger with
            {
                ChangeSets = ledger.ChangeSets.Select(changeSet => changeSet.ChangeSetId == ChangeSetId
                    ? changeSet with
                    {
                        Targets = changeSet.Targets.Select(target => target.BackupReference is null
                            ? target
                            : target with { BackupReference = "rebound" }).ToArray(),
                    }
                    : changeSet).ToArray(),
            });
        }

        public SetupChangeSetStatusResult Project()
        {
            var row = ledgerStore.LoadForRecovery().ChangeSets.Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
            return new SetupStatusProjector(Platform, Paths, planStore, journalStore).Project(row);
        }

        public SetupChangeSetStatusResult ProjectFailedRecovery(
            SetupLedgerChangeSet evidence,
            SetupLedgerChangeSet effective) =>
            new SetupStatusProjector(Platform, Paths, planStore, journalStore)
                .ProjectFailedRecovery(evidence, effective);

        public SetupLedgerChangeSet LoadChangeSet() => ledgerStore.LoadForRecovery().ChangeSets
            .Single(changeSet => changeSet.ChangeSetId == ChangeSetId);

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
                SetupChangeSetState.RollingBack => target with
                {
                    OutcomeCode = null,
                    RollbackStatus = target.AppliedStateHash is null
                        ? SetupLedgerRollbackStatus.NotAvailable
                        : SetupLedgerRollbackStatus.Pending,
                },
                SetupChangeSetState.Partial => target with { OutcomeCode = null },
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
                case "rebound-journal":
                    RebindJournal("target-id");
                    break;
                case "missing-backup":
                    var changed = ledgerStore.LoadForRecovery().ChangeSets
                        .Single(changeSet => changeSet.ChangeSetId == ChangeSetId)
                        .Targets.Single(target => target.BackupReference is not null);
                    Platform.FileSystem.DeleteFile(Paths.GetBackup(ChangeSetId, changed.RecordId));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public void DeletePlan() => Platform.FileSystem.DeleteFile(Paths.GetPlan(ChangeSetId));

        public void SeedTarget(string value) => Platform.SeedFile(TargetPath, Encoding.UTF8.GetBytes(value));

        public void ApplyCurrentVariant(string variant)
        {
            switch (variant)
            {
                case "desired":
                    SeedTarget("desired");
                    break;
                case "previous":
                    SeedTarget("previous");
                    break;
                case "third-party":
                case "diverged":
                    SeedTarget("third-party");
                    break;
                case "unavailable":
                    Platform.SeedPathMetadata(
                        TargetPath,
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                    break;
                case "missing":
                    Platform.FileSystem.DeleteFile(TargetPath);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public void MakeAdditionalTargetUnavailable(int index)
        {
            var path = $"C:\\private-status\\settings-{index}.json";
            Platform.SeedPathMetadata(path, new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
        }

        public string CaptureArtifacts()
        {
            return string.Join("|",
                Convert.ToBase64String(Platform.ReadSeededFile(TargetPath)),
                Convert.ToBase64String(Platform.ReadSeededFile(Paths.OwnershipLedger)),
                Convert.ToBase64String(Platform.ReadSeededFile(Paths.GetPlan(ChangeSetId))),
                Convert.ToBase64String(Platform.ReadSeededFile(Paths.GetTransactionJournal(ChangeSetId))));
        }

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
        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet changeSet) => SetupPlanResult.Revalidated();
    }
}

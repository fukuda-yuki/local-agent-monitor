using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.ConfigCli.Setup.Transactions;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class SetupRollbackTests
{
    private static void AssertTrustedRequestedRow(SetupRollbackExecutionResult result)
    {
        Assert.Null(result.Recovery);
        var changeSet = Assert.IsType<SetupLedgerChangeSet>(result.ChangeSet);
        Assert.Equal(result.RequestedChangeSetId, changeSet.ChangeSetId);

        var otherRequestedId = Guid.Parse("00000000-0000-7000-8000-000000000999");
        Assert.Throws<ArgumentException>(() => new SetupRollbackExecutionResult(
            otherRequestedId,
            result.Success,
            result.Code,
            changeSet,
            null));
        Assert.Throws<ArgumentException>(() => new SetupRollbackExecutionResult(
            result.RequestedChangeSetId,
            result.Success,
            result.Code,
            changeSet,
            new SetupRecoveryResult(
                SetupRecoveryDisposition.Failed,
                SetupCodes.RecoveryRequired,
                result.RequestedChangeSetId,
                SetupRecoveryOperation.Rollback,
                changeSet)));
    }

    private static void AssertUntrustedDirectResult(SetupRollbackExecutionResult result)
    {
        Assert.Null(result.Recovery);
        Assert.Null(result.ChangeSet);
    }

    private static SetupStatusProjection CreateStatusProjection(
        IReadOnlyList<SetupLedgerMember> members,
        bool includeCliManifest = false)
    {
        var operations = members.Select(member => member.Operation).Where(operation => operation != SetupOperation.NoOp).Distinct().ToArray();
        var aggregate = operations.Length switch { 0 => SetupOperation.NoOp, 1 => operations[0], _ => SetupOperation.Mixed };
        var expectedResult = includeCliManifest
            ? SourceCapabilityManifestLoader.LoadForTarget(GitHubCopilotSetupTarget.Cli)!.CanonicalJson
            : (JsonElement?)null;
        return new SetupStatusProjection(true, null, aggregate, null, null, expectedResult, null,
            members.Select(member => new SetupMemberChangeResult(member.SettingKey, member.Operation, "present", "configured", "none", false)).ToArray());
    }

    [Fact]
    public void Rollback_Lifecycle_ineligible_rebound_identity_is_untrusted_recovery_required()
    {
        var fixture = RollbackFixture.Create();
        Assert.True(fixture.Rollback().Success);
        fixture.RebindLedgerToolVersion("2.0.0");

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        AssertUntrustedDirectResult(result);
    }

    [Fact]
    public void Rollback_Applied_rebound_identity_is_untrusted_recovery_required()
    {
        var fixture = RollbackFixture.Create();
        fixture.RebindLedgerToolVersion("2.0.0");

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        AssertUntrustedDirectResult(result);
    }

    [Theory]
    [InlineData("inline_exact_label")]
    [InlineData("tagged_other_label")]
    public void RollbackPreflight_DesiredStateBindingMismatchIsUntrustedRecoveryRequired(string variant)
    {
        var fixture = RollbackFixture.Create();
        var plan = new SetupPlanStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        var changeSet = fixture.LoadChangeSet();
        if (variant == "inline_exact_label")
        {
            changeSet = changeSet with
            {
                Targets =
                [
                    changeSet.Targets[0] with
                    {
                        TargetLabel = "vscode-stable-default-user-settings",
                    },
                ],
            };
        }
        else
        {
            var target = plan.Targets[0];
            plan = plan with
            {
                Targets =
                [
                    target with
                    {
                        DesiredState = new SetupJsoncOwnedValuesDesiredState(
                            new string('b', 64),
                            [new SetupJsoncOwnedValue("setting-0", "string", "new-0")]),
                    },
                ],
            };
        }

        var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, changeSet, journal: null);

        Assert.Null(preparation.Evidence);
        Assert.Null(preparation.TrustedChangeSet);
        var result = Assert.IsType<SetupRollbackPreflightResult>(preparation.Result);
        Assert.Equal(SetupRollbackPreflightClassification.RecoveryRequired, result.Classification);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
    }

    [Fact]
    public void RollbackPreflight_ValidTaggedDesiredStateUsesPersistedExpectedHash()
    {
        var fixture = RollbackFixture.Create();
        var plan = new SetupPlanStore(fixture.Platform, fixture.Paths).Load(fixture.ChangeSetId)!;
        var target = Assert.Single(plan.Targets);
        plan = plan with
        {
            Targets =
            [
                target with
                {
                    DesiredState = new SetupJsoncOwnedValuesDesiredState(
                        SetupHash.File(true, Encoding.UTF8.GetBytes("new-0")),
                        [new SetupJsoncOwnedValue("setting-0", "string", "new-0")]),
                },
            ],
        };
        var changeSet = fixture.LoadChangeSet();
        changeSet = changeSet with
        {
            Targets =
            [
                changeSet.Targets[0] with
                {
                    TargetLabel = "vscode-stable-default-user-settings",
                    StatusProjection = changeSet.Targets[0].StatusProjection with
                    {
                        ExpectedResult = SourceCapabilityManifestLoader
                            .LoadForSurface("github-copilot-vscode")
                            .CanonicalJson,
                    },
                },
            ],
        };

        var preparation = SetupRollbackPreflightEvaluator.Prepare(plan, changeSet, fixture.LoadJournal());

        Assert.Null(preparation.Result);
        var evidence = Assert.IsType<SetupRollbackPreflightEvidence>(preparation.Evidence);
        var result = SetupRollbackPreflightEvaluator.Evaluate(
            evidence,
            new SetupRollbackPreflightObserver(fixture.Platform, fixture.Paths).Capture(evidence));
        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void Rollback_Identity_success_then_journal_evidence_mismatch_is_trusted_recovery_required()
    {
        var fixture = RollbackFixture.Create();
        fixture.RebindJournalAndLedgerAppliedHash(new string('0', 64));

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        AssertTrustedRequestedRow(result);
    }

    [Fact]
    public void Rollback_Valid_lifecycle_ineligible_row_is_trusted_not_available()
    {
        var fixture = RollbackFixture.Create();
        Assert.True(fixture.Rollback().Success);

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, result.Code);
        AssertTrustedRequestedRow(result);
    }

    [Theory]
    [InlineData(SetupFaultPoint.AfterJournalPreparedBeforeLedger)]
    [InlineData(SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent)]
    public void Rollback_Post_validation_preparation_fault_is_trusted_recovery_required(
        string faultPoint)
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.InjectFault($"checkpoint:{faultPoint}", new IOException("private-preparation-boundary"));

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        AssertTrustedRequestedRow(result);
    }

    [Theory]
    [InlineData("observation")]
    [InlineData("attempt-persistence")]
    public void Rollback_Post_identity_internal_fault_is_trusted_internal_error(string variant)
    {
        var fixture = RollbackFixture.Create();
        switch (variant)
        {
            case "observation":
                fixture.Platform.InjectFault(
                    $"file.read:{fixture.TargetPaths[0]}",
                    new IOException("private-observation"));
                break;
            case "attempt-persistence":
                fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("third-party"));
                fixture.Platform.InjectFault(
                    $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}",
                    new IOException("private-attempt-persistence"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        AssertTrustedRequestedRow(result);
    }

    [Fact]
    public void Rollback_Preflight_evaluator_and_execution_accept_same_fresh_applied_state()
    {
        var fixture = RollbackFixture.Create();

        var preflight = fixture.EvaluatePreflight();
        var rollback = fixture.Rollback();

        Assert.True(preflight.IsAvailable);
        Assert.Equal(preflight.IsAvailable, rollback.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, rollback.Code);
    }

    [Theory]
    [InlineData(SetupChangeSetState.Applied, true)]
    [InlineData(SetupChangeSetState.Restored, false)]
    [InlineData(SetupChangeSetState.RolledBack, false)]
    [InlineData(SetupChangeSetState.Partial, false)]
    public void Rollback_Preflight_evaluator_uses_fresh_lifecycle(
        SetupChangeSetState state,
        bool expectedAvailable)
    {
        var fixture = RollbackFixture.Create();

        var preflight = fixture.EvaluatePreflight(state);

        Assert.Equal(expectedAvailable, preflight.IsAvailable);
        Assert.Equal(
            expectedAvailable
                ? SetupRollbackPreflightClassification.Available
                : SetupRollbackPreflightClassification.NotAvailable,
            preflight.Classification);
    }

    [Theory]
    [InlineData("all-noop-drift", SetupCodes.RollbackStale)]
    [InlineData("third-party-drift", SetupCodes.RollbackStale)]
    [InlineData("missing-plan", SetupCodes.RecoveryRequired)]
    [InlineData("corrupt-plan", SetupCodes.RecoveryRequired)]
    [InlineData("rebound-plan", SetupCodes.RecoveryRequired)]
    [InlineData("missing-backup", SetupCodes.InternalError)]
    [InlineData("corrupt-backup", SetupCodes.InternalError)]
    [InlineData("rebound-backup", SetupCodes.InternalError)]
    [InlineData("reparse-target", SetupCodes.UnsafePath)]
    [InlineData("reparse-backup", SetupCodes.InternalError)]
    public void Rollback_Preflight_evaluator_and_execution_reject_same_fresh_state(
        string variant,
        string expectedCode)
    {
        var fixture = RollbackFixture.Create(
            fileCount: variant == "rebound-backup" ? 2 : 1,
            includeEnvironment: variant == "all-noop-drift",
            environmentAllNoOp: variant == "all-noop-drift");
        fixture.ApplyPreflightVariant(variant);

        var preflight = fixture.EvaluatePreflight();
        var rollback = fixture.Rollback();

        Assert.False(preflight.IsAvailable);
        Assert.Equal(expectedCode, preflight.Code);
        Assert.False(rollback.Success);
        Assert.Equal(preflight.Code, rollback.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_File_with_unowned_all_missing_environment_uses_full_noop_guard(
        bool driftEnvironment)
    {
        var fixture = RollbackFixture.Create(
            includeEnvironment: true,
            environmentAllNoOp: true,
            environmentNoOpMissing: true);
        var environment = Assert.Single(
            fixture.LoadChangeSet().Targets,
            target => target.TargetKind == SetupTargetKind.Env);
        Assert.Null(environment.AppliedStateHash);
        Assert.Null(environment.BackupReference);
        Assert.False(fixture.Platform.FileSystem.FileExists(
            fixture.Paths.GetBackup(fixture.ChangeSetId, environment.RecordId)));
        if (driftEnvironment)
        {
            fixture.Platform.SeedUserEnvironment("ENV_NOOP", "third-party");
        }
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.Equal(!driftEnvironment, result.Success);
        Assert.Equal(
            driftEnvironment ? SetupCodes.RollbackStale : SetupCodes.RollbackSucceeded,
            result.Code);
        Assert.Equal(driftEnvironment ? "new-0" : "old-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(
            driftEnvironment ? "third-party" : null,
            fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        var rollbackOperations = fixture.Platform.Operations.Skip(baseline).ToArray();
        Assert.Contains("environment.get:ENV_A", rollbackOperations);
        Assert.Contains("environment.get:ENV_NOOP", rollbackOperations);
        Assert.DoesNotContain(rollbackOperations, operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
        Assert.Equal(
            driftEnvironment ? SetupJournalOperation.Apply : SetupJournalOperation.Rollback,
            fixture.LoadJournal().Operation);
    }

    [Fact]
    public void Rollback_Changed_environment_with_missing_noop_uses_full_aggregate_and_changed_only_journal()
    {
        var fixture = RollbackFixture.Create(
            fileCount: 0,
            includeEnvironment: true,
            includeEnvironmentNoOp: true,
            environmentNoOpMissing: true);
        var applied = Assert.Single(fixture.LoadChangeSet().Targets);
        var names = new[] { "ENV_A", "ENV_SECOND", "ENV_NOOP" };
        var environmentStep = new UserEnvironmentSetupStep(fixture.Platform);
        var appliedCapture = environmentStep.Capture(names);
        var backup = environmentStep.ReadBackup(
            fixture.Paths.GetBackup(fixture.ChangeSetId, applied.RecordId),
            names);
        Assert.Equal(applied.PreviousStateHash, backup.AggregateHash);
        Assert.Equal(applied.AppliedStateHash, appliedCapture.AggregateHash);
        Assert.Null(backup.Members[2].Value.Value);
        Assert.False(backup.Members[2].Value.Exists);
        Assert.Equal(
            ["ENV_A", "ENV_SECOND"],
            fixture.LoadJournal().Targets.Single().Steps.Select(step => step.MemberKey));
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-second", fixture.Platform.ReadUserEnvironment("ENV_SECOND"));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        var rollbackOperations = fixture.Platform.Operations.Skip(baseline).ToArray();
        Assert.Equal(
            ["environment.set:ENV_SECOND", "environment.set:ENV_A"],
            rollbackOperations.Where(operation =>
                operation.StartsWith("environment.set:", StringComparison.Ordinal)));
        Assert.DoesNotContain("environment.set:ENV_NOOP", rollbackOperations);
        Assert.Equal(1, rollbackOperations.Count(operation => operation == "environment.notify"));
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, journal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, journal.EnvironmentNotification);
        Assert.Equal(
            ["ENV_A", "ENV_SECOND"],
            journal.Targets.Single().Steps.Select(step => step.MemberKey));
        var terminal = fixture.LoadChangeSet().Targets.Single();
        Assert.Equal(backup.AggregateHash, terminal.PreviousStateHash);
        Assert.Null(terminal.AppliedStateHash);
        Assert.Null(terminal.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, terminal.RollbackStatus);
    }

    [Fact]
    public void Rollback_Restores_single_environment_aggregate_from_real_apply_evidence()
    {
        var fixture = RollbackFixture.Create(fileCount: 0, includeEnvironment: true);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(1, fixture.Platform.Operations.Skip(baseline)
            .Count(operation => operation == "environment.set:ENV_A"));
        Assert.Equal(1, fixture.Platform.Operations.Skip(baseline)
            .Count(operation => operation == "environment.notify"));
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, journal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, journal.EnvironmentNotification);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.Equal(SetupCodes.RollbackSucceeded, durable.OutcomeCode);
        var environment = Assert.Single(durable.Targets);
        Assert.Null(environment.AppliedStateHash);
        Assert.Null(environment.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, environment.RollbackStatus);
    }

    [Fact]
    public void Rollback_Environment_members_restore_missing_empty_and_value_in_reverse_changed_order()
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_VALUE", "old-value", SetupOperation.Replace, "desired-value"),
            new("ENV_EMPTY", "", SetupOperation.Remove, null),
            new("ENV_MISSING", null, SetupOperation.Add, "desired-missing"),
            new("ENV_NOOP", "unchanged", SetupOperation.NoOp, "unchanged"),
        ]);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal("old-value", fixture.Platform.ReadUserEnvironment("ENV_VALUE"));
        Assert.Equal("", fixture.Platform.ReadUserEnvironment("ENV_EMPTY"));
        Assert.Null(fixture.Platform.ReadUserEnvironment("ENV_MISSING"));
        Assert.Equal("unchanged", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        var writes = fixture.Platform.Operations.Skip(baseline)
            .Where(operation => operation.StartsWith("environment.set:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal([
            "environment.set:ENV_MISSING",
            "environment.set:ENV_EMPTY",
            "environment.set:ENV_VALUE",
        ], writes);
        Assert.DoesNotContain("environment.set:ENV_NOOP", writes);
        Assert.Equal(
            ["ENV_VALUE", "ENV_EMPTY", "ENV_MISSING"],
            fixture.LoadJournal().Targets.Single().Steps.Select(step => step.MemberKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_Environment_notification_delivery_ambiguity_is_recovered_without_target_io(
        bool afterEffect)
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_A", "old-a", SetupOperation.Replace, "desired-a"),
        ]);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                "environment.notify",
                new IOException("PRIVATE_NORMAL_ROLLBACK_NOTIFY_AFTER"));
        }
        else
        {
            fixture.Platform.InjectFault(
                "environment.notify",
                new IOException("PRIVATE_NORMAL_ROLLBACK_NOTIFY_BEFORE"));
        }
        var baseline = fixture.Platform.Operations.Count;

        var direct = fixture.Rollback();

        AssertPendingNotificationThenRecovery(fixture, baseline, direct);
    }

    [Theory]
    [InlineData("write", false, false)]
    [InlineData("write", true, false)]
    [InlineData("flush", false, false)]
    [InlineData("flush", true, false)]
    [InlineData("replace", false, false)]
    [InlineData("replace", true, true)]
    public async Task Rollback_Environment_notification_completion_ambiguity_is_proven_or_recovered(
        string boundary,
        bool afterEffect,
        bool completionProven)
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_A", "old-a", SetupOperation.Replace, "desired-a"),
        ]);
        var baseline = fixture.Platform.Operations.Count;
        using var notification = fixture.Platform.AddBarrier("environment.notify");
        var rollingBack = Task.Run(fixture.Rollback);
        notification.WaitUntilReached(CancellationToken.None);
        fixture.InjectNotificationCompletionFault(boundary, afterEffect);
        notification.Release();

        var direct = await rollingBack;

        if (completionProven)
        {
            Assert.True(direct.Success);
            Assert.Equal(SetupCodes.RollbackSucceeded, direct.Code);
            Assert.Null(direct.Recovery);
            Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
            Assert.Equal(SetupCodes.RollbackSucceeded, fixture.LoadChangeSet().OutcomeCode);
            AssertRollbackNotificationOrdering(fixture, baseline);
            Assert.Equal(1, fixture.Platform.Operations.Skip(baseline)
                .Count(operation => operation == "environment.notify"));

            AssertFinalRollbackNotAvailableWithoutEnvironmentIo(fixture);
            return;
        }

        AssertPendingNotificationThenRecovery(fixture, baseline, direct);
    }

    [Fact]
    public void Rollback_Environment_dormant_supersession_is_reused_after_interruption()
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_A", "old-a", SetupOperation.Replace, "desired-a"),
        ]);
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}",
            new IOException("PRIVATE_DORMANT_ENV_ROLLBACK"));

        var interrupted = fixture.Rollback();

        Assert.False(interrupted.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalOperation.Rollback, fixture.LoadJournal().Operation);
        Assert.Equal(SetupJournalPhase.Prepared, fixture.LoadJournal().Phase);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));

        var retried = fixture.Rollback(fixture.ReopenCoordinator());

        Assert.True(retried.Success);
        Assert.Null(retried.Recovery);
        Assert.Equal(SetupCodes.RollbackSucceeded, retried.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
    }

    [Fact]
    public void Rollback_Environment_noop_drift_blocks_supersession_and_preserves_all_members()
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_CHANGED", "old", SetupOperation.Replace, "desired"),
            new("ENV_NOOP", "same", SetupOperation.NoOp, "same"),
        ]);
        fixture.Platform.SeedUserEnvironment("ENV_NOOP", "third-party");
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackStale, result.Code);
        Assert.Equal("desired", fixture.Platform.ReadUserEnvironment("ENV_CHANGED"));
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public async Task Rollback_Environment_noop_drift_after_preflight_is_caught_by_full_final_guard()
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_CHANGED", "old", SetupOperation.Replace, "desired"),
            new("ENV_NOOP", "same", SetupOperation.NoOp, "same"),
        ]);
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        var rollingBack = Task.Run(fixture.Rollback);
        boundary.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedUserEnvironment("ENV_NOOP", "third-party");
        var baseline = fixture.Platform.Operations.Count;
        boundary.Release();

        var result = await rollingBack;

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.PartialRollback, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal("old", fixture.Platform.ReadUserEnvironment("ENV_CHANGED"));
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.DoesNotContain(
            "environment.set:ENV_NOOP",
            fixture.Platform.Operations.Skip(baseline));
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, durable.State);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, durable.Targets.Single().RollbackStatus);
    }

    [Theory]
    [InlineData("backup", SetupCodes.InternalError)]
    [InlineData("applied", SetupCodes.RollbackStale)]
    [InlineData("base", SetupCodes.RecoveryRequired)]
    [InlineData("private-read-fault", SetupCodes.InternalError)]
    public void Rollback_Environment_evidence_mismatch_fails_closed_without_artifacts_or_value_echo(
        string variant,
        string expectedCode)
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_PRIVATE", "old-private", SetupOperation.Replace, "desired-private"),
        ]);
        switch (variant)
        {
            case "backup":
                fixture.CorruptBackup(Encoding.UTF8.GetBytes("PRIVATE_BACKUP_BYTES"));
                break;
            case "applied":
                fixture.RebindLedgerAppliedHash(new string('0', 64));
                break;
            case "base":
                fixture.RebindLedgerPreviousHash(new string('1', 64));
                break;
            case "private-read-fault":
                fixture.Platform.InjectFault(
                    "environment.get:ENV_PRIVATE",
                    new IOException("PRIVATE_ENVIRONMENT_VALUE"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.DoesNotContain("PRIVATE", result.Code, StringComparison.Ordinal);
        Assert.Equal("desired-private", fixture.Platform.ReadUserEnvironment("ENV_PRIVATE"));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public async Task Rollback_Environment_edit_after_preflight_is_partial_then_external_resolution_recovers()
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_A", "old-a", SetupOperation.Replace, "desired-a"),
            new("ENV_B", "old-b", SetupOperation.Replace, "desired-b"),
        ]);
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        var rollingBack = Task.Run(fixture.Rollback);
        boundary.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedUserEnvironment("ENV_B", "third-party");
        boundary.Release();

        var partial = await rollingBack;

        Assert.False(partial.Success);
        Assert.Null(partial.Recovery);
        Assert.Equal(SetupCodes.PartialRollback, partial.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-party", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);

        fixture.Platform.SeedUserEnvironment("ENV_B", "desired-b");
        var recovered = fixture.Rollback(fixture.ReopenCoordinator());

        Assert.True(recovered.Success);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Equal(fixture.ChangeSetId, recovered.Recovery!.RecoveredChangeSetId);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);

        var repeated = fixture.Rollback(fixture.ReopenCoordinator());
        Assert.False(repeated.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, repeated.Code);
        Assert.Null(repeated.Recovery);
    }

    [Fact]
    public void Rollback_Multiple_environment_aggregates_record_not_available_without_mutation()
    {
        var fixture = EnvironmentRollbackFixture.CreateMultiple([
            [new("ENV_A", "old-a", SetupOperation.Replace, "desired-a")],
            [new("ENV_B", "old-b", SetupOperation.Replace, "desired-b")],
        ]);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, result.Code);
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("desired-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupCodes.RollbackNotAvailable, fixture.LoadChangeSet().OutcomeCode);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    [Fact]
    public async Task Rollback_Environment_uses_the_supplied_lock_for_the_whole_operation()
    {
        var fixture = EnvironmentRollbackFixture.Create([
            new("ENV_A", "old-a", SetupOperation.Replace, "desired-a"),
        ]);
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        using var secondStarted = new ManualResetEventSlim();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var first = Task.Run(() => fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId));
        boundary.WaitUntilReached(CancellationToken.None);
        var second = Task.Run(() =>
        {
            secondStarted.Set();
            return fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId);
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(second.IsCompleted);

        boundary.Release();
        var firstResult = await first;
        var secondResult = await second;

        Assert.True(firstResult.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, secondResult.Code);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
    }

    [Fact]
    public void Rollback_Restores_real_apply_artifacts_in_reverse_order_and_clears_ownership()
    {
        var fixture = RollbackFixture.Create(fileCount: 2);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.RequestedChangeSetId);
        Assert.Null(result.Recovery);
        Assert.Equal(["old-0", "old-1"], fixture.TargetPaths.Select(fixture.ReadText));
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, journal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.All(journal.Targets.SelectMany(target => target.Steps),
            step => Assert.Equal(SetupJournalStepPhase.RestoreCompleted, step.Phase));
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.Equal(SetupCodes.RollbackSucceeded, durable.OutcomeCode);
        Assert.All(durable.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus);
        });
        var restores = fixture.Platform.Operations.Skip(baseline)
            .Where(operation => fixture.TargetPaths.Any(path =>
                operation.EndsWith("->" + path, StringComparison.Ordinal)))
            .ToArray();
        Assert.EndsWith("->" + fixture.TargetPaths[1], restores[0], StringComparison.Ordinal);
        Assert.EndsWith("->" + fixture.TargetPaths[0], restores[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Rollback_Stale_preflight_preserves_every_target_and_apply_journal()
    {
        var fixture = RollbackFixture.Create(fileCount: 2);
        fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("third-party"));
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackStale, result.Code);
        Assert.Equal("third-party", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("new-1", fixture.ReadText(fixture.TargetPaths[1]));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.RollbackStale, durable.OutcomeCode);
        Assert.All(durable.Targets, target => Assert.Equal(SetupLedgerRollbackStatus.Pending, target.RollbackStatus));
    }

    [Fact]
    public void Rollback_Fault_after_supersession_leaves_dormant_pair_and_retry_reuses_it()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}",
            new IOException("private-supersession-boundary"));

        var interrupted = fixture.Rollback();

        Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalOperation.Rollback, fixture.LoadJournal().Operation);
        Assert.Equal(SetupJournalPhase.Prepared, fixture.LoadJournal().Phase);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));

        var retried = fixture.Rollback();

        Assert.True(retried.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, retried.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
    }

    [Fact]
    public void Rollback_Fault_after_ledger_before_restore_is_completed_by_next_mandatory_recovery()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}",
            new IOException("private-ledger-boundary"));

        var interrupted = fixture.Rollback();

        Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        Assert.Equal(SetupChangeSetState.RollingBack, fixture.LoadChangeSet().State);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));

        var recovered = fixture.Rollback();

        Assert.True(recovered.Success);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.NotNull(recovered.Recovery);
        Assert.Equal(fixture.ChangeSetId, recovered.RequestedChangeSetId);
        Assert.Equal(fixture.ChangeSetId, recovered.Recovery!.RecoveredChangeSetId);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
    }

    [Fact]
    public async Task Rollback_Edit_after_preflight_is_preserved_as_partial_while_safe_targets_continue()
    {
        var fixture = RollbackFixture.Create(fileCount: 2);
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var rollingBack = Task.Run(() => fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId));
        boundary.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("third-party"));
        boundary.Release();

        var result = await rollingBack;

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.PartialRollback, result.Code);
        Assert.Equal("third-party", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("old-1", fixture.ReadText(fixture.TargetPaths[1]));
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Partial, durable.State);
        Assert.Equal(SetupCodes.PartialRollback, durable.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Stale, durable.Targets[0].RollbackStatus);
        Assert.Equal(SetupLedgerRollbackStatus.Pending, durable.Targets[1].RollbackStatus);
    }

    [Fact]
    public async Task Rollback_Prior_state_after_preflight_is_accepted_idempotently()
    {
        var fixture = RollbackFixture.Create();
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var rollingBack = Task.Run(() => fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId));
        boundary.WaitUntilReached(CancellationToken.None);
        fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("old-0"));
        boundary.Release();

        var result = await rollingBack;

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
    }

    [Fact]
    public void Rollback_Mandatory_recovery_short_circuits_and_preserves_requested_correlation()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}",
            new IOException("private-ledger-boundary"));
        Assert.Equal(SetupCodes.RecoveryRequired, fixture.Rollback().Code);
        var requested = Guid.Parse("00000000-0000-7000-8000-000000000999");

        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var result = fixture.Coordinator.Rollback(acquisition.Lock!, requested);

        Assert.True(result.Success);
        Assert.Equal(requested, result.RequestedChangeSetId);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, result.Code);
        Assert.Equal(fixture.ChangeSetId, result.Recovery!.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, result.Recovery.Operation);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    public void Rollback_Invalid_backup_fails_before_supersession_or_target_write(string variant)
    {
        var fixture = RollbackFixture.Create();
        var backup = fixture.Paths.GetBackup(fixture.ChangeSetId, fixture.RecordIds[0]);
        if (variant == "missing")
        {
            fixture.Platform.FileSystem.DeleteFile(backup);
        }
        else
        {
            fixture.Platform.SeedFile(backup, Encoding.UTF8.GetBytes("not-a-backup"));
        }
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Mixed_repeated_request_is_not_available_and_does_not_restore_again()
    {
        var fixture = RollbackFixture.Create(includeEnvironment: true);
        Assert.True(fixture.Rollback().Success);
        var baseline = fixture.Platform.Operations.Count;

        var repeated = fixture.Rollback();

        Assert.False(repeated.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, repeated.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths) ||
                operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
                operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
                operation == "environment.notify");
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Reparse_target_is_rejected_before_supersession()
    {
        var fixture = RollbackFixture.Create();
        fixture.Platform.SeedPathMetadata(
            fixture.TargetPaths[0],
            new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.UnsafePath, result.Code);
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Rebound_plan_ledger_identity_fails_closed_before_supersession()
    {
        var fixture = RollbackFixture.Create();
        fixture.RebindLedgerToolVersion("2.0.0");
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, result.Code);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline),
            operation => IsTargetMutation(operation, fixture.TargetPaths));
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
    }

    [Fact]
    public void Rollback_Mixed_restores_missing_previous_file_and_environment()
    {
        var fixture = RollbackFixture.Create(previousMissing: true, includeEnvironment: true);
        Assert.True(fixture.Platform.FileSystem.FileExists(fixture.TargetPaths[0]));

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.False(fixture.Platform.FileSystem.FileExists(fixture.TargetPaths[0]));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
    }

    [Fact]
    public async Task Rollback_Mixed_same_lock_serializes_the_entire_operation_without_sleep_or_retry()
    {
        var fixture = RollbackFixture.Create(includeEnvironment: true);
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        using var secondStarted = new ManualResetEventSlim();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);
        var first = Task.Run(() => fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId));
        boundary.WaitUntilReached(CancellationToken.None);
        var second = Task.Run(() =>
        {
            secondStarted.Set();
            return fixture.Coordinator.Rollback(acquisition.Lock!, fixture.ChangeSetId);
        });
        Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(second.IsCompleted);

        boundary.Release();
        var firstResult = await first;
        var secondResult = await second;

        Assert.True(firstResult.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, secondResult.Code);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
    }

    [Theory]
    [InlineData("phase", false)]
    [InlineData("phase", true)]
    [InlineData("intent", false)]
    [InlineData("intent", true)]
    [InlineData("primitive", false)]
    [InlineData("primitive", true)]
    [InlineData("completion", false)]
    [InlineData("completion", true)]
    [InlineData("commit", false)]
    [InlineData("commit", true)]
    [InlineData("ledger", false)]
    [InlineData("ledger", true)]
    public async Task Rollback_Current_execution_fault_matrix_never_returns_recovery_correlation(
        string boundary,
        bool afterEffect)
    {
        var fixture = RollbackFixture.Create();
        SetupRollbackExecutionResult direct;
        if (boundary == "ledger")
        {
            using var checkpoint = fixture.Platform.AddBarrier(
                $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}");
            var rollingBack = Task.Run(fixture.Rollback);
            checkpoint.WaitUntilReached(CancellationToken.None);
            fixture.InjectTerminalLedgerFault(afterEffect);
            checkpoint.Release();
            direct = await rollingBack;
        }
        else
        {
            fixture.InjectNormalRollbackFault(boundary, afterEffect);
            direct = fixture.Rollback();
            Assert.Contains(fixture.InjectedOperation!, fixture.Platform.Operations);
        }

        Assert.Null(direct.Recovery);
        Assert.Equal(afterEffect, direct.Success);
        Assert.DoesNotContain("PRIVATE", direct.Code, StringComparison.Ordinal);
        Assert.Equal(
            afterEffect
                ? SetupCodes.RollbackSucceeded
                : boundary == "primitive" ? SetupCodes.PartialRollback : SetupCodes.RecoveryRequired,
            direct.Code);

        var reopened = fixture.Rollback(fixture.ReopenCoordinator());
        if (afterEffect)
        {
            Assert.Equal(SetupCodes.RollbackNotAvailable, reopened.Code);
            Assert.Null(reopened.Recovery);
        }
        else
        {
            Assert.Equal(SetupCodes.InterruptedRollbackRecovered, reopened.Code);
            Assert.Equal(fixture.ChangeSetId, reopened.Recovery!.RecoveredChangeSetId);
            Assert.Equal(SetupRecoveryOperation.Rollback, reopened.Recovery.Operation);
        }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("environment")]
    [InlineData("no-op")]
    public void Rollback_Mixed_preflight_drift_preserves_every_physical_target_and_apply_artifact(
        string drift)
    {
        var fixture = RollbackFixture.Create(
            fileCount: 2,
            includeEnvironment: true,
            includeEnvironmentNoOp: true);
        switch (drift)
        {
            case "file":
                fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("third-file"));
                break;
            case "environment":
                fixture.Platform.SeedUserEnvironment("ENV_A", "third-environment");
                break;
            case "no-op":
                fixture.Platform.SeedUserEnvironment("ENV_NOOP", "third-noop");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drift));
        }
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackStale, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal(drift == "file" ? "third-file" : "new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("new-1", fixture.ReadText(fixture.TargetPaths[1]));
        Assert.Equal(
            drift == "environment" ? "third-environment" : "desired-a",
            fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("desired-second", fixture.Platform.ReadUserEnvironment("ENV_SECOND"));
        Assert.Equal(
            drift == "no-op" ? "third-noop" : "stable",
            fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            IsTargetMutation(operation, fixture.TargetPaths) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.RollbackStale, durable.OutcomeCode);
    }

    [Theory]
    [InlineData("file")]
    [InlineData("environment")]
    [InlineData("no-op")]
    [InlineData("file-unavailable")]
    [InlineData("environment-unavailable")]
    public async Task Rollback_Mixed_post_preflight_conflict_restores_safe_targets_then_recovers_exact_partial(
        string conflict)
    {
        var fixture = RollbackFixture.Create(
            fileCount: 2,
            includeEnvironment: true,
            includeEnvironmentNoOp: true);
        using var boundary = fixture.Platform.AddBarrier(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}");
        var rollingBack = Task.Run(fixture.Rollback);
        boundary.WaitUntilReached(CancellationToken.None);
        switch (conflict)
        {
            case "file":
                fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("third-file"));
                break;
            case "environment":
                fixture.Platform.SeedUserEnvironment("ENV_A", "third-environment");
                break;
            case "no-op":
                fixture.Platform.SeedUserEnvironment("ENV_NOOP", "third-noop");
                break;
            case "file-unavailable":
                fixture.Platform.InjectFault(
                    $"file.read:{fixture.TargetPaths[0]}",
                    new IOException("PRIVATE_MIXED_FILE_UNAVAILABLE"));
                break;
            case "environment-unavailable":
                fixture.Platform.InjectFault(
                    "environment.get:ENV_A",
                    new IOException("PRIVATE_MIXED_ENV_UNAVAILABLE"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(conflict));
        }
        var directBaseline = fixture.Platform.Operations.Count;
        boundary.Release();

        var direct = await rollingBack;

        Assert.False(direct.Success);
        Assert.Equal(SetupCodes.PartialRollback, direct.Code);
        Assert.Null(direct.Recovery);
        Assert.DoesNotContain("PRIVATE", direct.Code, StringComparison.Ordinal);
        Assert.Equal("old-1", fixture.ReadText(fixture.TargetPaths[1]));
        Assert.Equal(
            conflict == "file" ? "third-file" : conflict == "file-unavailable" ? "new-0" : "old-0",
            fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal(
            conflict == "environment" ? "third-environment" :
                conflict == "environment-unavailable" ? "desired-a" : "old-a",
            fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-second", fixture.Platform.ReadUserEnvironment("ENV_SECOND"));
        Assert.Equal(
            conflict == "no-op" ? "third-noop" : "stable",
            fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.DoesNotContain("environment.set:ENV_NOOP", fixture.Platform.Operations.Skip(directBaseline));
        Assert.DoesNotContain("environment.notify", fixture.Platform.Operations.Skip(directBaseline));
        Assert.Equal(SetupJournalPhase.Partial, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Partial, fixture.LoadChangeSet().State);

        switch (conflict)
        {
            case "file":
                fixture.Platform.SeedFile(fixture.TargetPaths[0], Encoding.UTF8.GetBytes("new-0"));
                break;
            case "environment":
                fixture.Platform.SeedUserEnvironment("ENV_A", "desired-a");
                break;
            case "no-op":
                fixture.Platform.SeedUserEnvironment("ENV_NOOP", "stable");
                break;
        }
        var recoveryBaseline = fixture.Platform.Operations.Count;

        var recovered = fixture.Rollback(fixture.ReopenCoordinator());

        Assert.True(recovered.Success);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Null(recovered.ChangeSet);
        Assert.Equal(fixture.ChangeSetId, recovered.Recovery?.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, recovered.Recovery?.Operation);
        Assert.Equal(["old-0", "old-1"], fixture.TargetPaths.Select(fixture.ReadText));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-second", fixture.Platform.ReadUserEnvironment("ENV_SECOND"));
        Assert.Equal("stable", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.DoesNotContain("environment.set:ENV_NOOP", fixture.Platform.Operations.Skip(recoveryBaseline));
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.All(durable.Targets, target =>
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus));
    }

    [Fact]
    public void Rollback_Mixed_dormant_supersession_retry_reuses_exact_prepared_journal()
    {
        var fixture = RollbackFixture.Create(
            fileCount: 2,
            includeEnvironment: true,
            includeEnvironmentNoOp: true);
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterJournalPreparedBeforeLedger}",
            new IOException("PRIVATE_MIXED_DORMANT_ROLLBACK"));

        var interrupted = fixture.Rollback();

        Assert.False(interrupted.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        Assert.Null(interrupted.Recovery);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
        var prepared = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, prepared.Operation);
        Assert.Equal(SetupJournalPhase.Prepared, prepared.Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, prepared.EnvironmentNotification);
        Assert.Equal(["new-0", "new-1"], fixture.TargetPaths.Select(fixture.ReadText));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("desired-second", fixture.Platform.ReadUserEnvironment("ENV_SECOND"));
        Assert.Equal("stable", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));

        var retried = fixture.Rollback(fixture.ReopenCoordinator());

        Assert.True(retried.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, retried.Code);
        Assert.Null(retried.Recovery);
        Assert.Equal(["old-0", "old-1"], fixture.TargetPaths.Select(fixture.ReadText));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-second", fixture.Platform.ReadUserEnvironment("ENV_SECOND"));
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Mixed_interruption_after_ledger_is_next_mandatory_recovery_with_correlation()
    {
        var fixture = RollbackFixture.Create(includeEnvironment: true);
        fixture.Platform.InjectFault(
            $"checkpoint:{SetupFaultPoint.AfterLedgerTransitionBeforeMutationIntent}",
            new IOException("PRIVATE_MIXED_ACTIVE_ROLLBACK"));

        var interrupted = fixture.Rollback();

        Assert.False(interrupted.Success);
        Assert.Equal(SetupCodes.RecoveryRequired, interrupted.Code);
        Assert.Null(interrupted.Recovery);
        Assert.Equal(SetupChangeSetState.RollingBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupJournalPhase.Prepared, fixture.LoadJournal().Phase);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        var requested = Guid.Parse("00000000-0000-7000-8000-000000000998");
        var coordinator = fixture.ReopenCoordinator();
        using var acquisition = SetupLock.TryAcquire(fixture.Platform, fixture.Paths);

        var recovered = coordinator.Rollback(acquisition.Lock!, requested);

        Assert.True(recovered.Success);
        Assert.Equal(requested, recovered.RequestedChangeSetId);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Null(recovered.ChangeSet);
        Assert.Equal(fixture.ChangeSetId, recovered.Recovery?.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, recovered.Recovery?.Operation);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Rollback_Mixed_notification_delivery_ambiguity_replays_without_target_io(bool afterEffect)
    {
        var fixture = RollbackFixture.Create(fileCount: 2, includeEnvironment: true);
        if (afterEffect)
        {
            fixture.Platform.InjectAfterEffectFault(
                "environment.notify",
                new IOException("PRIVATE_MIXED_NOTIFY_AFTER"));
        }
        else
        {
            fixture.Platform.InjectFault(
                "environment.notify",
                new IOException("PRIVATE_MIXED_NOTIFY_BEFORE"));
        }

        var direct = fixture.Rollback();

        Assert.False(direct.Success);
        Assert.Equal(SetupCodes.PartialRollback, direct.Code);
        Assert.Null(direct.Recovery);
        Assert.Equal(["old-0", "old-1"], fixture.TargetPaths.Select(fixture.ReadText));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal(SetupChangeSetState.RolledBack, fixture.LoadChangeSet().State);
        Assert.Equal(SetupEnvironmentNotification.Pending, fixture.LoadJournal().EnvironmentNotification);
        var recoveryBaseline = fixture.Platform.Operations.Count;

        var recovered = fixture.Rollback(fixture.ReopenCoordinator());

        Assert.True(recovered.Success);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Equal(fixture.ChangeSetId, recovered.Recovery?.RecoveredChangeSetId);
        var recoveryOperations = fixture.Platform.Operations.Skip(recoveryBaseline).ToArray();
        Assert.Equal(1, recoveryOperations.Count(operation => operation == "environment.notify"));
        Assert.DoesNotContain(recoveryOperations, operation =>
            IsTargetMutation(operation, fixture.TargetPaths) ||
            operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, fixture.LoadChangeSet().OutcomeCode);
    }

    [Fact]
    public void Rollback_Mixed_multiple_environment_aggregates_fail_closed_before_supersession()
    {
        var fixture = RollbackFixture.Create(
            includeEnvironment: true,
            includeSecondEnvironment: true);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("desired-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("desired-b", fixture.Platform.ReadUserEnvironment("ENV_B"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            IsTargetMutation(operation, fixture.TargetPaths) ||
            operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
        Assert.Equal(SetupJournalOperation.Apply, fixture.LoadJournal().Operation);
        Assert.Equal(SetupJournalPhase.Committed, fixture.LoadJournal().Phase);
        Assert.Equal(SetupChangeSetState.Applied, fixture.LoadChangeSet().State);
    }

    [Fact]
    public void Rollback_Mixed_all_noop_environment_is_guarded_without_ownership_or_notification()
    {
        var fixture = RollbackFixture.Create(
            includeEnvironment: true,
            environmentAllNoOp: true);
        var applied = fixture.LoadChangeSet();
        var environmentBefore = Assert.Single(
            applied.Targets,
            target => target.TargetKind == SetupTargetKind.Env);
        Assert.Null(environmentBefore.AppliedStateHash);
        Assert.Null(environmentBefore.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, environmentBefore.RollbackStatus);
        Assert.False(fixture.Platform.FileSystem.GetPathMetadata(
            fixture.Paths.GetBackup(fixture.ChangeSetId, environmentBefore.RecordId)).Exists);
        var applyJournal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Apply, applyJournal.Operation);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, applyJournal.EnvironmentNotification);
        Assert.DoesNotContain(applyJournal.Targets, target => target.TargetKind == SetupTargetKind.Env);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal("old-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        var operations = fixture.Platform.Operations.Skip(baseline).ToArray();
        Assert.Equal(
            [
                "environment.get:ENV_A",
                "environment.get:ENV_NOOP",
                "environment.get:ENV_A",
                "environment.get:ENV_NOOP",
            ],
            operations.Where(operation => operation.StartsWith(
                "environment.get:", StringComparison.Ordinal)));
        Assert.DoesNotContain(operations, operation =>
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, journal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, journal.EnvironmentNotification);
        Assert.Single(journal.Targets);
        Assert.Equal(SetupTargetKind.Json, journal.Targets[0].TargetKind);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        var file = Assert.Single(durable.Targets, target => target.TargetKind == SetupTargetKind.Json);
        Assert.Null(file.AppliedStateHash);
        Assert.Null(file.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, file.RollbackStatus);
        var environment = Assert.Single(
            durable.Targets,
            target => target.TargetKind == SetupTargetKind.Env);
        Assert.Null(environment.AppliedStateHash);
        Assert.Null(environment.BackupReference);
        Assert.Null(environment.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, environment.RollbackStatus);
    }

    [Fact]
    public void Rollback_Mixed_all_noop_environment_drift_is_stale_before_supersession()
    {
        var fixture = RollbackFixture.Create(
            includeEnvironment: true,
            environmentAllNoOp: true);
        fixture.Platform.SeedUserEnvironment("ENV_NOOP", "third-noop");
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.False(result.Success);
        Assert.Equal(SetupCodes.RollbackStale, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal("new-0", fixture.ReadText(fixture.TargetPaths[0]));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("third-noop", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            IsTargetMutation(operation, fixture.TargetPaths) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Apply, journal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.NotRequired, journal.EnvironmentNotification);
        Assert.DoesNotContain(journal.Targets, target => target.TargetKind == SetupTargetKind.Env);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.Applied, durable.State);
        Assert.Equal(SetupCodes.RollbackStale, durable.OutcomeCode);
        var environment = Assert.Single(
            durable.Targets,
            target => target.TargetKind == SetupTargetKind.Env);
        Assert.Null(environment.AppliedStateHash);
        Assert.Null(environment.BackupReference);
        Assert.Equal(SetupLedgerRollbackStatus.NotAvailable, environment.RollbackStatus);
    }

    [Fact]
    public void Rollback_Mixed_file_and_environment_targets_restore_in_reverse_order_and_clear_ownership()
    {
        var fixture = RollbackFixture.Create(
            fileCount: 2,
            includeEnvironment: true,
            includeEnvironmentNoOp: true);
        var baseline = fixture.Platform.Operations.Count;

        var result = fixture.Rollback();

        Assert.True(result.Success);
        Assert.Equal(SetupCodes.RollbackSucceeded, result.Code);
        Assert.Null(result.Recovery);
        Assert.Equal(["old-0", "old-1"], fixture.TargetPaths.Select(fixture.ReadText));
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        Assert.Equal("old-second", fixture.Platform.ReadUserEnvironment("ENV_SECOND"));
        Assert.Equal("stable", fixture.Platform.ReadUserEnvironment("ENV_NOOP"));
        var mutations = fixture.Platform.Operations.Skip(baseline)
            .Where(operation => IsTargetMutation(operation, fixture.TargetPaths) ||
                operation.StartsWith("environment.set:", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal("environment.set:ENV_SECOND", mutations[0]);
        Assert.Equal("environment.set:ENV_A", mutations[1]);
        Assert.EndsWith("->" + fixture.TargetPaths[1], mutations[2], StringComparison.Ordinal);
        Assert.EndsWith("->" + fixture.TargetPaths[0], mutations[3], StringComparison.Ordinal);
        Assert.DoesNotContain("environment.set:ENV_NOOP", mutations);
        Assert.Equal(1, fixture.Platform.Operations.Skip(baseline)
            .Count(operation => operation == "environment.notify"));
        var operations = fixture.Platform.Operations.Skip(baseline).ToArray();
        var notification = Array.IndexOf(operations, "environment.notify");
        var beforeNotification = operations.Take(notification).ToArray();
        var journalBeforeNotification = Array.FindLastIndex(
            beforeNotification,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith(
                    $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}",
                    StringComparison.Ordinal));
        var ledgerBeforeNotification = Array.FindLastIndex(
            beforeNotification,
            operation => operation ==
                $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}");
        var notificationMarker = Array.FindIndex(
            operations,
            notification + 1,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith(
                    $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}",
                    StringComparison.Ordinal));
        var lastTargetMutation = Array.FindLastIndex(operations, operation =>
            IsTargetMutation(operation, fixture.TargetPaths) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.True(lastTargetMutation < journalBeforeNotification &&
            journalBeforeNotification < ledgerBeforeNotification &&
            ledgerBeforeNotification < notification &&
            notification < notificationMarker);
        var durable = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, durable.State);
        Assert.Equal(SetupCodes.RollbackSucceeded, durable.OutcomeCode);
        Assert.All(durable.Targets, target =>
        {
            Assert.Null(target.AppliedStateHash);
            Assert.Null(target.BackupReference);
            Assert.Equal(SetupLedgerRollbackStatus.Succeeded, target.RollbackStatus);
        });
        var journal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, journal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, journal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Completed, journal.EnvironmentNotification);
        Assert.Equal(
            ["ENV_A", "ENV_SECOND"],
            Assert.Single(journal.Targets, target => target.TargetKind == SetupTargetKind.Env)
                .Steps.Select(step => step.MemberKey));
    }

    private static bool IsTargetMutation(string operation, IReadOnlyList<string> targetPaths) =>
        targetPaths.Any(path =>
            operation.EndsWith("->" + path, StringComparison.Ordinal) ||
            string.Equals(operation, "file.delete:" + path, StringComparison.Ordinal));

    private static void AssertPendingNotificationThenRecovery(
        EnvironmentRollbackFixture fixture,
        int directBaseline,
        SetupRollbackExecutionResult direct)
    {
        Assert.False(direct.Success);
        Assert.Equal(SetupCodes.PartialRollback, direct.Code);
        Assert.Null(direct.Recovery);
        Assert.Equal(SetupChangeSetState.Partial, direct.ChangeSet?.State);
        Assert.Equal(SetupCodes.PartialRollback, direct.ChangeSet?.OutcomeCode);
        Assert.DoesNotContain("PRIVATE", direct.Code, StringComparison.Ordinal);
        Assert.Equal("old-a", fixture.Platform.ReadUserEnvironment("ENV_A"));
        AssertRollbackNotificationOrdering(fixture, directBaseline);
        var directOperations = fixture.Platform.Operations.Skip(directBaseline).ToArray();
        Assert.Equal(1, directOperations.Count(operation => operation == "environment.set:ENV_A"));
        Assert.Equal(1, directOperations.Count(operation => operation == "environment.notify"));

        var pendingJournal = fixture.LoadJournal();
        Assert.Equal(SetupJournalOperation.Rollback, pendingJournal.Operation);
        Assert.Equal(SetupJournalPhase.Committed, pendingJournal.Phase);
        Assert.Equal(SetupEnvironmentNotification.Pending, pendingJournal.EnvironmentNotification);
        var pendingLedger = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, pendingLedger.State);
        Assert.Equal(SetupCodes.PartialRollback, pendingLedger.OutcomeCode);
        var pendingTarget = Assert.Single(pendingLedger.Targets);
        Assert.Null(pendingTarget.AppliedStateHash);
        Assert.Null(pendingTarget.BackupReference);
        Assert.Equal(SetupCodes.PartialRollback, pendingTarget.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded, pendingTarget.RollbackStatus);

        var recoveryBaseline = fixture.Platform.Operations.Count;
        var recovered = fixture.Rollback(fixture.ReopenCoordinator());

        Assert.True(recovered.Success);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recovered.Code);
        Assert.Null(recovered.ChangeSet);
        Assert.NotNull(recovered.Recovery);
        Assert.Equal(SetupRecoveryDisposition.Recovered, recovered.Recovery.Disposition);
        Assert.Equal(fixture.ChangeSetId, recovered.Recovery.RecoveredChangeSetId);
        Assert.Equal(SetupRecoveryOperation.Rollback, recovered.Recovery.Operation);
        Assert.Equal(SetupChangeSetState.RolledBack, recovered.Recovery.EffectiveChangeSet?.State);
        var recoveryOperations = fixture.Platform.Operations.Skip(recoveryBaseline).ToArray();
        Assert.Equal(1, recoveryOperations.Count(operation => operation == "environment.notify"));
        Assert.DoesNotContain(recoveryOperations, operation =>
            operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal));
        Assert.Equal(SetupEnvironmentNotification.Completed, fixture.LoadJournal().EnvironmentNotification);
        var recoveredLedger = fixture.LoadChangeSet();
        Assert.Equal(SetupChangeSetState.RolledBack, recoveredLedger.State);
        Assert.Equal(SetupCodes.InterruptedRollbackRecovered, recoveredLedger.OutcomeCode);
        Assert.Equal(SetupLedgerRollbackStatus.Succeeded,
            Assert.Single(recoveredLedger.Targets).RollbackStatus);
        Assert.Equal(2, fixture.Platform.Operations.Skip(directBaseline)
            .Count(operation => operation == "environment.notify"));

        AssertFinalRollbackNotAvailableWithoutEnvironmentIo(fixture);
    }

    private static void AssertRollbackNotificationOrdering(
        EnvironmentRollbackFixture fixture,
        int baseline)
    {
        var operations = fixture.Platform.Operations.Skip(baseline).ToArray();
        var notify = Array.IndexOf(operations, "environment.notify");
        var restore = Array.LastIndexOf(operations, "environment.set:ENV_A");
        var beforeNotification = operations.Take(notify).ToArray();
        var journal = Array.FindLastIndex(
            beforeNotification,
            operation => operation.StartsWith("file.replace:", StringComparison.Ordinal) &&
                operation.EndsWith(
                    $"->{fixture.Paths.GetTransactionJournal(fixture.ChangeSetId)}",
                    StringComparison.Ordinal));
        var ledger = Array.FindLastIndex(
            beforeNotification,
            operation => operation ==
                $"file.replace:{fixture.Paths.OwnershipLedger}.tmp->{fixture.Paths.OwnershipLedger}");
        Assert.True(restore >= 0 && restore < journal && journal < ledger && ledger < notify);
        Assert.DoesNotContain(operations.Take(notify), operation => operation == "environment.notify");
    }

    private static void AssertFinalRollbackNotAvailableWithoutEnvironmentIo(
        EnvironmentRollbackFixture fixture)
    {
        var baseline = fixture.Platform.Operations.Count;

        var repeated = fixture.Rollback(fixture.ReopenCoordinator());

        Assert.False(repeated.Success);
        Assert.Equal(SetupCodes.RollbackNotAvailable, repeated.Code);
        Assert.Null(repeated.Recovery);
        Assert.DoesNotContain(fixture.Platform.Operations.Skip(baseline), operation =>
            operation.StartsWith("environment.get:", StringComparison.Ordinal) ||
            operation.StartsWith("environment.set:", StringComparison.Ordinal) ||
            operation == "environment.notify");
    }

    private sealed record EnvironmentMemberDefinition(
        string Name,
        string? InitialValue,
        SetupOperation Operation,
        string? DesiredValue);

    private sealed class EnvironmentRollbackFixture
    {
        private EnvironmentRollbackFixture(
            IReadOnlyList<IReadOnlyList<EnvironmentMemberDefinition>> targetDefinitions)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 8, 9, 10, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000661");
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            foreach (var member in targetDefinitions.SelectMany(target => target))
            {
                if (member.InitialValue is not null)
                {
                    Platform.SeedUserEnvironment(member.Name, member.InitialValue);
                }
            }

            var environmentStep = new UserEnvironmentSetupStep(Platform);
            var targets = targetDefinitions.Select((definitions, index) =>
            {
                var capture = environmentStep.Capture(definitions.Select(member => member.Name).ToArray());
                return new SetupPrivatePlanTarget(
                    Guid.Parse($"00000000-0000-7000-8000-{662 + index:000000000000}"),
                    SetupTargetKind.Env,
                    "current-user",
                    capture.AggregateHash,
                    new SetupInlineDesiredState("environment-allowlist"),
                    definitions.Select(member => new SetupPrivatePlanMember(
                        member.Name,
                        member.Operation,
                        member.DesiredValue)).ToArray());
            }).ToArray();
            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "copilot-cli",
                Platform.Clock.UtcNow,
                "1.0.0",
                targets);
            var ledger = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "copilot-cli",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                targets.Select((target, index) => new SetupLedgerTarget(
                    target.RecordId,
                    target.TargetKind,
                    $"user-environment-{index}",
                    "github-copilot",
                    target.Members.Select(member => new SetupLedgerMember(
                        member.SettingKey,
                        member.Operation)).ToArray(),
                    target.BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    SetupRestartRequirement.RestartTerminalSession,
                    CreateStatusProjection(target.Members.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray()),
                    "1.0.0")).ToArray());
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using (var acquisition = SetupLock.TryAcquire(Platform, Paths))
            {
                ledgerStore.PersistPlannedChangeSet(acquisition.Lock!, plan, ledger);
                var applied = new SetupApplyCoordinator(
                        Platform, Paths, planStore, ledgerStore, journalStore, new PassRevalidator())
                    .Apply(acquisition.Lock!, ChangeSetId).Value;
                if (applied.State != SetupChangeSetState.Applied)
                {
                    throw new InvalidOperationException("The real apply producer did not establish rollback evidence.");
                }
            }

            Coordinator = new SetupRollbackCoordinator(Platform, Paths, planStore, ledgerStore, journalStore);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public SetupRollbackCoordinator Coordinator { get; }

        public static EnvironmentRollbackFixture Create(
            IReadOnlyList<EnvironmentMemberDefinition> definitions) =>
            new([definitions]);

        public static EnvironmentRollbackFixture CreateMultiple(
            IReadOnlyList<IReadOnlyList<EnvironmentMemberDefinition>> targets) =>
            new(targets);

        public SetupRollbackExecutionResult Rollback() => Rollback(Coordinator);

        public SetupRollbackExecutionResult Rollback(SetupRollbackCoordinator coordinator)
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return coordinator.Rollback(acquisition.Lock!, ChangeSetId);
        }

        public SetupRollbackCoordinator ReopenCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRollbackCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public void InjectNotificationCompletionFault(string boundary, bool afterEffect)
        {
            var destination = Paths.GetTransactionJournal(ChangeSetId);
            var temporary = NextTemporaryPath(destination);
            var operation = boundary switch
            {
                "write" => $"file.write-new:{temporary}",
                "flush" => $"file.flush:{temporary}",
                "replace" => $"file.replace:{temporary}->{destination}",
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            };
            if (afterEffect)
            {
                Platform.InjectAfterEffectFault(
                    operation,
                    new IOException("PRIVATE_NORMAL_ROLLBACK_MARKER_AFTER"));
            }
            else
            {
                Platform.InjectFault(
                    operation,
                    new IOException("PRIVATE_NORMAL_ROLLBACK_MARKER_BEFORE"));
            }
        }

        public SetupLedgerChangeSet LoadChangeSet()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets
                .Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
        }

        public void CorruptBackup(byte[] bytes)
        {
            var recordId = LoadChangeSet().Targets.Single().RecordId;
            Platform.SeedFile(Paths.GetBackup(ChangeSetId, recordId), bytes);
        }

        public void RebindLedgerAppliedHash(string hash) =>
            RewriteSingleLedgerTarget(target => target with { AppliedStateHash = hash });

        public void RebindLedgerPreviousHash(string hash) =>
            RewriteSingleLedgerTarget(target => target with { PreviousStateHash = hash });

        private void RewriteSingleLedgerTarget(Func<SetupLedgerTarget, SetupLedgerTarget> rewrite)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = ledger.ChangeSets.Single();
            var target = rewrite(changeSet.Targets.Single());
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.Save(
                acquisition.Lock!,
                ledger with { ChangeSets = [changeSet with { Targets = [target] }] });
        }

        private string NextTemporaryPath(string destination)
        {
            var maximum = Platform.Operations
                .SelectMany(operation => Regex.Matches(operation,
                    @"\.cao-00000000-0000-7000-8000-(?<value>[0-9]{12})\.tmp"))
                .Select(match => long.Parse(match.Groups["value"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            return destination + ".cao-" +
                Guid.Parse($"00000000-0000-7000-8000-{maximum + 1:D12}").ToString("D") + ".tmp";
        }
    }

    private sealed class RollbackFixture
    {
        private RollbackFixture(
            int fileCount,
            bool previousMissing,
            bool includeEnvironment,
            bool includeEnvironmentNoOp,
            bool includeSecondEnvironment,
            bool environmentAllNoOp,
            bool environmentNoOpMissing)
        {
            Platform = new SetupTestPlatform(new DateTimeOffset(2026, 7, 13, 8, 9, 10, TimeSpan.Zero));
            Paths = new SetupRuntimePaths(Platform);
            ChangeSetId = Guid.Parse("00000000-0000-7000-8000-000000000601");
            RecordIds = Enumerable.Range(0, fileCount)
                .Select(index => Guid.Parse($"00000000-0000-7000-8000-{602 + index:000000000000}"))
                .ToArray();
            TargetPaths = Enumerable.Range(0, fileCount)
                .Select(index => Path.Combine(Platform.LocalApplicationData, $"settings-{index}.json"))
                .ToArray();
            Platform.SeedDirectory("C:\\");
            Platform.SeedDirectory(Platform.LocalApplicationData);
            for (var index = 0; index < fileCount; index++)
            {
                if (!previousMissing)
                {
                    Platform.SeedFile(TargetPaths[index], Encoding.UTF8.GetBytes($"old-{index}"));
                }
            }

            var targets = Enumerable.Range(0, fileCount).Select(index => new SetupPrivatePlanTarget(
                RecordIds[index],
                SetupTargetKind.Json,
                TargetPaths[index],
                SetupHash.File(!previousMissing, previousMissing ? [] : Encoding.UTF8.GetBytes($"old-{index}")),
                new SetupInlineDesiredState($"new-{index}"),
                [new SetupPrivatePlanMember($"setting-{index}", SetupOperation.Replace, $"new-{index}")])).ToList();
            if (includeEnvironment)
            {
                if (!environmentAllNoOp || !environmentNoOpMissing)
                {
                    Platform.SeedUserEnvironment("ENV_A", "old-a");
                }
                if (environmentAllNoOp && !environmentNoOpMissing)
                {
                    Platform.SeedUserEnvironment("ENV_NOOP", "");
                }
                else if (includeEnvironmentNoOp)
                {
                    Platform.SeedUserEnvironment("ENV_SECOND", "old-second");
                    if (!environmentNoOpMissing)
                    {
                        Platform.SeedUserEnvironment("ENV_NOOP", "stable");
                    }
                }

                var environmentMembers = environmentAllNoOp
                    ? new List<SetupPrivatePlanMember>
                    {
                        new("ENV_A", SetupOperation.NoOp, environmentNoOpMissing ? null : "old-a"),
                        new("ENV_NOOP", SetupOperation.NoOp, environmentNoOpMissing ? null : ""),
                    }
                    : new List<SetupPrivatePlanMember>
                {
                    new("ENV_A", SetupOperation.Replace, "desired-a"),
                };
                if (!environmentAllNoOp && includeEnvironmentNoOp)
                {
                    environmentMembers.Add(new SetupPrivatePlanMember(
                        "ENV_SECOND", SetupOperation.Replace, "desired-second"));
                    environmentMembers.Add(new SetupPrivatePlanMember(
                        "ENV_NOOP",
                        SetupOperation.NoOp,
                        environmentNoOpMissing ? null : "stable"));
                }

                var environmentCapture = new UserEnvironmentSetupStep(Platform).Capture(
                    environmentMembers.Select(member => member.SettingKey).ToArray());
                targets.Add(new SetupPrivatePlanTarget(
                    Guid.Parse("00000000-0000-7000-8000-000000000699"),
                    SetupTargetKind.Env,
                    "current-user",
                    environmentCapture.AggregateHash,
                    new SetupInlineDesiredState("environment-allowlist"),
                    environmentMembers));
            }
            if (includeSecondEnvironment)
            {
                Platform.SeedUserEnvironment("ENV_B", "old-b");
                var environmentCapture = new UserEnvironmentSetupStep(Platform).Capture(["ENV_B"]);
                targets.Add(new SetupPrivatePlanTarget(
                    Guid.Parse("00000000-0000-7000-8000-000000000700"),
                    SetupTargetKind.Env,
                    "current-user",
                    environmentCapture.AggregateHash,
                    new SetupInlineDesiredState("environment-allowlist"),
                    [new SetupPrivatePlanMember("ENV_B", SetupOperation.Replace, "desired-b")]));
            }
            var plan = new SetupPrivatePlan(
                1,
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                "1.0.0",
                targets);
            var ledger = new SetupLedgerChangeSet(
                ChangeSetId,
                "github-copilot",
                "vscode",
                Platform.Clock.UtcNow,
                Platform.Clock.UtcNow,
                "1.0.0",
                null,
                SetupChangeSetState.Planned,
                targets.Select((target, index) => new SetupLedgerTarget(
                    target.RecordId,
                    target.TargetKind,
                    target.TargetKind == SetupTargetKind.Env ? "copilot-cli-user-environment" : $"settings-{index}",
                    "github-copilot",
                    target.Members.Select(member => new SetupLedgerMember(
                        member.SettingKey,
                        member.Operation)).ToArray(),
                    target.BaseStateHash,
                    null,
                    null,
                    null,
                    SetupLedgerRollbackStatus.NotAvailable,
                    target.TargetKind == SetupTargetKind.Env
                        ? SetupRestartRequirement.RestartTerminalSession
                        : SetupRestartRequirement.RestartVsCode,
                    CreateStatusProjection(
                        target.Members.Select(member => new SetupLedgerMember(member.SettingKey, member.Operation)).ToArray(),
                        includeCliManifest: target.TargetKind == SetupTargetKind.Env),
                    "1.0.0")).ToArray());
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var journalStore = new SetupTransactionJournalStore(Platform, Paths);
            using (var acquisition = SetupLock.TryAcquire(Platform, Paths))
            {
                ledgerStore.PersistPlannedChangeSet(acquisition.Lock!, plan, ledger);
                _ = new SetupApplyCoordinator(
                    Platform, Paths, planStore, ledgerStore, journalStore, new PassRevalidator())
                    .Apply(acquisition.Lock!, ChangeSetId);
            }

            Coordinator = new SetupRollbackCoordinator(Platform, Paths, planStore, ledgerStore, journalStore);
        }

        public SetupTestPlatform Platform { get; }
        public SetupRuntimePaths Paths { get; }
        public Guid ChangeSetId { get; }
        public IReadOnlyList<Guid> RecordIds { get; }
        public IReadOnlyList<string> TargetPaths { get; }
        public SetupRollbackCoordinator Coordinator { get; }
        public string? InjectedOperation { get; private set; }

        public static RollbackFixture Create(
            int fileCount = 1,
            bool previousMissing = false,
            bool includeEnvironment = false,
            bool includeEnvironmentNoOp = false,
            bool includeSecondEnvironment = false,
            bool environmentAllNoOp = false,
            bool environmentNoOpMissing = false) =>
            new(
                fileCount,
                previousMissing,
                includeEnvironment,
                includeEnvironmentNoOp,
                includeSecondEnvironment,
                environmentAllNoOp,
                environmentNoOpMissing);

        public SetupRollbackExecutionResult Rollback()
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return Coordinator.Rollback(acquisition.Lock!, ChangeSetId);
        }

        public SetupRollbackExecutionResult Rollback(SetupRollbackCoordinator coordinator)
        {
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            return coordinator.Rollback(acquisition.Lock!, ChangeSetId);
        }

        public SetupRollbackCoordinator ReopenCoordinator()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupRollbackCoordinator(
                Platform,
                Paths,
                planStore,
                new SetupLedgerStore(Platform, Paths, planStore),
                new SetupTransactionJournalStore(Platform, Paths));
        }

        public void InjectNormalRollbackFault(string boundary, bool afterEffect)
        {
            var journalPath = Paths.GetTransactionJournal(ChangeSetId);
            var operation = boundary switch
            {
                "phase" => $"file.replace:{NextTemporaryPath(journalPath, 1)}->{journalPath}",
                "intent" => $"file.replace:{NextTemporaryPath(journalPath, 2)}->{journalPath}",
                "primitive" => $"file.replace:{NextTemporaryPath(TargetPaths[0], 3)}->{TargetPaths[0]}",
                "completion" => $"file.replace:{NextTemporaryPath(journalPath, 4)}->{journalPath}",
                "commit" => $"file.replace:{NextTemporaryPath(journalPath, 5)}->{journalPath}",
                _ => throw new ArgumentOutOfRangeException(nameof(boundary)),
            };
            InjectedOperation = operation;
            if (afterEffect)
            {
                Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_NORMAL_ROLLBACK_AFTER"));
            }
            else
            {
                Platform.InjectFault(operation, new IOException("PRIVATE_NORMAL_ROLLBACK_BEFORE"));
            }
        }

        public void InjectTerminalLedgerFault(bool afterEffect)
        {
            var operation = $"file.replace:{Paths.OwnershipLedger}.tmp->{Paths.OwnershipLedger}";
            InjectedOperation = operation;
            if (afterEffect)
            {
                Platform.InjectAfterEffectFault(operation, new IOException("PRIVATE_NORMAL_LEDGER_AFTER"));
            }
            else
            {
                Platform.InjectFault(operation, new IOException("PRIVATE_NORMAL_LEDGER_BEFORE"));
            }
        }

        private string NextTemporaryPath(string destination, int offset)
        {
            var maximum = Platform.Operations
                .SelectMany(operation => Regex.Matches(operation,
                    @"\.cao-00000000-0000-7000-8000-(?<value>[0-9]{12})\.tmp"))
                .Select(match => long.Parse(match.Groups["value"].Value,
                    System.Globalization.CultureInfo.InvariantCulture))
                .DefaultIfEmpty(0)
                .Max();
            return destination + ".cao-" +
                Guid.Parse($"00000000-0000-7000-8000-{maximum + offset + 1:D12}").ToString("D") + ".tmp";
        }

        public string ReadText(string path) => Encoding.UTF8.GetString(Platform.ReadSeededFile(path));

        public SetupTransactionJournal LoadJournal() =>
            new SetupTransactionJournalStore(Platform, Paths).Load(ChangeSetId)!;

        public SetupLedgerChangeSet LoadChangeSet()
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            return new SetupLedgerStore(Platform, Paths, planStore).LoadForRecovery().ChangeSets
                .Single(changeSet => changeSet.ChangeSetId == ChangeSetId);
        }

        public SetupRollbackPreflightResult EvaluatePreflight(SetupChangeSetState? state = null)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            SetupPrivatePlan? plan;
            try
            {
                plan = planStore.Load(ChangeSetId);
            }
            catch (SetupStorageException)
            {
                plan = null;
            }

            var changeSet = LoadChangeSet();
            if (state is not null)
            {
                changeSet = changeSet with { State = state.Value };
            }

            var preparation = SetupRollbackPreflightEvaluator.Prepare(
                plan,
                changeSet,
                LoadJournal());
            if (preparation.Result is not null)
            {
                return preparation.Result;
            }

            var evidence = Assert.IsType<SetupRollbackPreflightEvidence>(preparation.Evidence);
            var observations = new SetupRollbackPreflightObserver(Platform, Paths).Capture(evidence);
            return SetupRollbackPreflightEvaluator.Evaluate(evidence, observations);
        }

        public void ApplyPreflightVariant(string variant)
        {
            var backup = Paths.GetBackup(ChangeSetId, RecordIds[0]);
            switch (variant)
            {
                case "all-noop-drift":
                    Platform.SeedUserEnvironment("ENV_A", "third-party");
                    break;
                case "third-party-drift":
                    Platform.SeedFile(TargetPaths[0], Encoding.UTF8.GetBytes("third-party"));
                    break;
                case "missing-plan":
                    Platform.FileSystem.DeleteFile(Paths.GetPlan(ChangeSetId));
                    break;
                case "corrupt-plan":
                    Platform.SeedFile(Paths.GetPlan(ChangeSetId), Encoding.UTF8.GetBytes("corrupt"));
                    break;
                case "rebound-plan":
                    RebindLedgerToolVersion("2.0.0");
                    break;
                case "missing-backup":
                    Platform.FileSystem.DeleteFile(backup);
                    break;
                case "corrupt-backup":
                    Platform.SeedFile(backup, Encoding.UTF8.GetBytes("corrupt"));
                    break;
                case "rebound-backup":
                    Platform.SeedFile(
                        backup,
                        Platform.ReadSeededFile(Paths.GetBackup(ChangeSetId, RecordIds[1])));
                    break;
                case "reparse-target":
                    Platform.SeedPathMetadata(
                        TargetPaths[0],
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                    break;
                case "reparse-backup":
                    Platform.SeedPathMetadata(
                        backup,
                        new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.ReparsePoint));
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(variant));
            }
        }

        public void RebindLedgerToolVersion(string version)
        {
            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var ledger = ledgerStore.LoadForRecovery();
            var rebound = ledger.ChangeSets[0] with
            {
                ToolVersion = version,
                Targets = ledger.ChangeSets[0].Targets
                    .Select(target => target with { ToolVersion = version })
                    .ToArray(),
            };
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.Save(acquisition.Lock!, ledger with { ChangeSets = [rebound] });
        }

        public void RebindJournalAndLedgerAppliedHash(string hash)
        {
            var journalPath = Paths.GetTransactionJournal(ChangeSetId);
            var journal = LoadJournal();
            var desiredHash = journal.Targets[0].Steps[0].DesiredStateHash;
            var journalJson = Encoding.UTF8.GetString(Platform.ReadSeededFile(journalPath));
            Platform.SeedFile(
                journalPath,
                Encoding.UTF8.GetBytes(journalJson.Replace(desiredHash, hash, StringComparison.Ordinal)));

            var planStore = new SetupPlanStore(Platform, Paths);
            var ledgerStore = new SetupLedgerStore(Platform, Paths, planStore);
            var ledger = ledgerStore.LoadForRecovery();
            var changeSet = ledger.ChangeSets[0];
            var targets = changeSet.Targets.ToArray();
            targets[0] = targets[0] with { AppliedStateHash = hash };
            using var acquisition = SetupLock.TryAcquire(Platform, Paths);
            ledgerStore.Save(
                acquisition.Lock!,
                ledger with { ChangeSets = [changeSet with { Targets = targets }] });
        }
    }

    private sealed class PassRevalidator : ISetupApplyRevalidator
    {
        public SetupPlanResult<SetupRevalidation> Revalidate(
            SetupPrivatePlan plan,
            SetupLedgerChangeSet plannedChangeSet) => SetupPlanResult.Revalidated();
    }
}

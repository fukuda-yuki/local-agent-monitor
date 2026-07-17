using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.Doctor;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotDoctorOrchestratorTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-17T02:00:00Z");
    private static readonly DateTimeOffset ExpiresAt = Now.AddMinutes(5);
    private const string TraceId = "0123456789abcdef0123456789abcdef";

    public static TheoryData<string, SetupCommandResult> InvalidStarts => new()
    {
        { "cli", Direct("cli", SetupCommand.Plan, SetupCodes.PlanReady) },
        { "cli", Failed(SetupCommand.Apply, SetupCodes.StalePlan) },
        { "cli", Failed(SetupCommand.Rollback, SetupCodes.RollbackStale) },
        { "cli", Status("cli", SetupReferenceState.Base, SetupCurrentState.Current, SetupChangeSetState.Planned) },
        { "cli", Status("cli", SetupReferenceState.Previous, SetupCurrentState.Current, SetupChangeSetState.RolledBack) },
        { "cli", Status("cli", SetupReferenceState.Desired, SetupCurrentState.Stale, SetupChangeSetState.Applied) },
        { "cli", Status("cli", SetupReferenceState.None, SetupCurrentState.Diverged, SetupChangeSetState.Applied) },
        { "cli", MultipleStatus("cli") },
    };

    public static TheoryData<string, SetupCommandResult> ValidSetupEvaluations => new()
    {
        { "vscode", Direct("vscode", SetupCommand.Plan, SetupCodes.PlanReady) },
        { "cli", Direct("cli", SetupCommand.Plan, SetupCodes.PlanReady) },
        { "vscode", Direct("vscode", SetupCommand.Apply, SetupCodes.ApplySucceeded) },
        { "cli", Direct("cli", SetupCommand.Apply, SetupCodes.NoChanges) },
        { "app-sdk", Direct("app-sdk", SetupCommand.Apply, SetupCodes.NoChanges) },
        { "vscode", Direct("vscode", SetupCommand.Rollback, SetupCodes.RollbackSucceeded) },
        { "cli", Direct("cli", SetupCommand.Rollback, SetupCodes.RollbackSucceeded) },
        { "vscode", Status("vscode", SetupReferenceState.Desired, SetupCurrentState.Current, SetupChangeSetState.Applied) },
        { "vscode", Status("vscode", SetupReferenceState.Desired, SetupCurrentState.Current, SetupChangeSetState.NoChanges, vscodeTargets: 2) },
        { "cli", Status("cli", SetupReferenceState.Desired, SetupCurrentState.Current, SetupChangeSetState.Applied) },
        { "app-sdk", Status("app-sdk", SetupReferenceState.None, SetupCurrentState.NotApplicable, SetupChangeSetState.NoChanges) },
    };

    [Theory]
    [MemberData(nameof(ValidSetupEvaluations))]
    public void EvaluateSetup_AllMapperSupportedLifecyclesReturnExactEvaluationWithoutDatabaseEffects(
        string target,
        SetupCommandResult setup)
    {
        using var database = TempDatabase.Create();
        var expected = DoctorEvaluator.Evaluate(
            GitHubCopilotDoctorFactMapper.FromSetup(setup, target, Now));

        var actual = GitHubCopilotDoctorOrchestrator.EvaluateSetup(
            new FixedTimeProvider(Now), setup, target);

        Assert.Equal(DoctorJson.SerializeResult(expected), DoctorJson.SerializeResult(actual));
        Assert.Null(actual.Verification);
        Assert.False(File.Exists(database.Path));
    }

    [Theory]
    [MemberData(nameof(InvalidStarts))]
    public void Start_RejectsInvalidLifecycleBeforeCreatingDatabase(
        string target,
        SetupCommandResult setup)
    {
        using var database = TempDatabase.Create();

        var result = GitHubCopilotDoctorOrchestrator.Start(
            database.Path, new FixedTimeProvider(Now), setup, target, ExpiresAt);

        Assert.Equal(DoctorResultCode.InvalidArguments, result.Code);
        Assert.Null(result.Verification);
        Assert.False(File.Exists(database.Path));
    }

    [Theory]
    [InlineData("vscode", "github-copilot-vscode", SetupCodes.ApplySucceeded)]
    [InlineData("cli", "github-copilot-cli", SetupCodes.NoChanges)]
    [InlineData("app-sdk", "github-copilot-app-sdk", SetupCodes.NoChanges)]
    public void Start_PartitionsOneExactTargetWithoutClaimingReadiness(
        string target,
        string sourceSurface,
        string code)
    {
        using var database = TempDatabase.Create();
        var setup = Direct(target, SetupCommand.Apply, code);

        var result = GitHubCopilotDoctorOrchestrator.Start(
            database.Path, new FixedTimeProvider(Now), setup, target, ExpiresAt);

        Assert.Equal(DoctorResultCode.VerificationStarted, result.Code);
        Assert.Null(result.Evaluation);
        var verification = Assert.IsType<DoctorVerification>(result.Verification);
        Assert.Equal(sourceSurface, verification.ExpectedSourceSurface);
        Assert.Equal("github-copilot-doctor", verification.ExpectedSourceAdapter);
        Assert.Equal(ExpiresAt, verification.ExpiresAt);
        Assert.Empty(verification.AcceptedEvidenceRefs);
        Assert.Equal(1, Count(database.Path, "doctor_verifications"));
    }

    [Fact]
    public void Status_ReturnsFrozenActiveResultAndWrongRevisionIsStaleWithoutMutation()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, "cli");
        var status = Status("cli", SetupReferenceState.Desired, SetupCurrentState.Current, SetupChangeSetState.Applied);

        var stale = GitHubCopilotDoctorOrchestrator.Status(
            database.Path, new FixedTimeProvider(Now), status, "cli",
            verification.VerificationId, verification.Revision + 1);
        Assert.Equal(DoctorResultCode.VerificationStale, stale.Code);
        Assert.Null(stale.Evaluation);
        Assert.Equal(verification, Read(database.Path, verification.VerificationId));
        Assert.Equal(0, Count(database.Path, "doctor_verification_evidence"));

        var wrongTarget = GitHubCopilotDoctorOrchestrator.Status(
            database.Path, new FixedTimeProvider(Now), status, "vscode",
            verification.VerificationId, verification.Revision);
        Assert.Equal(DoctorResultCode.InvalidArguments, wrongTarget.Code);
        Assert.Equal(verification, Read(database.Path, verification.VerificationId));
        Assert.Equal(0, Count(database.Path, "doctor_verification_evidence"));

        var active = GitHubCopilotDoctorOrchestrator.Status(
            database.Path, new FixedTimeProvider(Now), status, "cli",
            verification.VerificationId, verification.Revision);
        Assert.Equal(DoctorResultCode.VerificationActive, active.Code);
        Assert.Null(active.Evaluation);
        Assert.Equal(verification, active.Verification);
        Assert.Equal(1, Count(database.Path, "doctor_verifications"));
    }

    [Fact]
    public void Status_CurrentDesiredMayStartButInvalidVsCodeAggregateCannot()
    {
        using var validDatabase = TempDatabase.Create();
        var valid = GitHubCopilotDoctorOrchestrator.Start(
            validDatabase.Path,
            new FixedTimeProvider(Now),
            Status("vscode", SetupReferenceState.Desired, SetupCurrentState.Current, SetupChangeSetState.NoChanges, vscodeTargets: 2),
            "vscode",
            ExpiresAt);
        Assert.Equal(DoctorResultCode.VerificationStarted, valid.Code);

        using var invalidDatabase = TempDatabase.Create();
        var mixed = Status(
            "vscode", SetupReferenceState.Desired, SetupCurrentState.Current,
            SetupChangeSetState.Applied, vscodeTargets: 2, secondCurrent: SetupCurrentState.Stale);
        var invalid = GitHubCopilotDoctorOrchestrator.Start(
            invalidDatabase.Path, new FixedTimeProvider(Now), mixed, "vscode", ExpiresAt);
        Assert.Equal(DoctorResultCode.InvalidArguments, invalid.Code);
        Assert.False(File.Exists(invalidDatabase.Path));
    }

    [Theory]
    [InlineData("cli", SetupCurrentState.Current, SetupChangeSetState.Applied, "github-copilot-cli")]
    [InlineData("app-sdk", SetupCurrentState.NotApplicable, SetupChangeSetState.NoChanges, "github-copilot-app-sdk")]
    public void Status_ValidCliAndAppSdkShapesMayStart(
        string target,
        SetupCurrentState current,
        SetupChangeSetState state,
        string sourceSurface)
    {
        using var database = TempDatabase.Create();

        var result = GitHubCopilotDoctorOrchestrator.Start(
            database.Path, new FixedTimeProvider(Now),
            Status(target, target == "app-sdk" ? SetupReferenceState.None : SetupReferenceState.Desired, current, state),
            target, ExpiresAt);

        Assert.Equal(DoctorResultCode.VerificationStarted, result.Code);
        Assert.Equal(sourceSurface, result.Verification?.ExpectedSourceSurface);
        Assert.Equal(1, Count(database.Path, "doctor_verifications"));
    }

    [Theory]
    [InlineData("vscode", "vscode-copilot-chat", "vscode", "copilot-compatible-hook")]
    [InlineData("cli", "copilot-cli", "copilot-cli", "copilot-compatible-hook")]
    public void ContinueSelected_ExactRealEvidenceMergesFactsAndCompletes(
        string target,
        string clientKind,
        string nativeSurface,
        string eventAdapter)
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, target);
        var rawRecordId = CommitRaw(database.Path, clientKind, supportedCompatibility: true);
        var compatibility = Assert.IsType<SourceCompatibilityRow>(
            new SqliteSourceCompatibilityStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter).GetByRawRecordId(rawRecordId));
        Assert.Equal(SourceCompatibilityState.Supported, compatibility.CompatibilityState);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, nativeSurface, eventAdapter);
        var setup = Direct(target, SetupCommand.Apply, SetupCodes.ApplySucceeded);

        var beforeEvidence = GitHubCopilotDoctorOrchestrator.EvaluateSetup(
            new FixedTimeProvider(Now), setup, target);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, beforeEvidence.Code);
        Assert.Null(beforeEvidence.Verification);
        Assert.Equal(DoctorStateCode.AgentRestartRequired, beforeEvidence.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorNextAction.RestartSourceProcess, beforeEvidence.Evaluation?.PrimaryState?.NextAction);
        Assert.DoesNotContain(
            beforeEvidence.Evaluation?.States ?? [],
            state => state.StateCode == DoctorStateCode.FirstTraceReady);

        var result = GitHubCopilotDoctorOrchestrator.ContinueSelected(
            database.Path,
            new FixedTimeProvider(Now),
            setup,
            target,
            verification.VerificationId,
            verification.Revision,
            rawRecordId,
            new GitHubCopilotNativeSessionSelection(nativeSurface, "exact-native"));

        Assert.Equal(DoctorResultCode.VerificationCompleted, result.Code);
        Assert.Equal(DoctorVerificationState.Completed, result.Verification?.State);
        Assert.Equal(DoctorStateCode.FirstTraceReady, result.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorNextAction.OpenVerifiedTraceOrSession, result.Evaluation?.PrimaryState?.NextAction);
        Assert.DoesNotContain(
            result.Evaluation?.States ?? [],
            state => state.StateCode == DoctorStateCode.AgentRestartRequired);
        Assert.Equal(verification.VerificationId, result.Evaluation?.PrimaryState?.VerificationId);
        Assert.Equal(5, result.Verification?.AcceptedEvidenceRefs.Count);
        Assert.All(result.Verification!.AcceptedEvidenceRefs, reference =>
        {
            Assert.DoesNotContain("exact-native", reference, StringComparison.Ordinal);
            Assert.DoesNotContain(TraceId, reference, StringComparison.Ordinal);
            Assert.DoesNotContain(database.Path, reference, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void ContinueSelected_UnknownExactCompatibilityCannotBePromotedBySetupVersion()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, "cli");
        var rawRecordId = CommitRaw(database.Path, "copilot-cli");
        var compatibility = Assert.IsType<SourceCompatibilityRow>(
            new SqliteSourceCompatibilityStore(
                database.Path,
                RawTelemetryStoreConnectionOptions.MonitorWriter).GetByRawRecordId(rawRecordId));
        Assert.Equal(SourceCompatibilityState.SchemaDriftDetected, compatibility.CompatibilityState);
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, "copilot-cli", "copilot-compatible-hook");

        var result = GitHubCopilotDoctorOrchestrator.ContinueSelected(
            database.Path,
            new FixedTimeProvider(Now),
            Direct("cli", SetupCommand.Apply, SetupCodes.ApplySucceeded),
            "cli",
            verification.VerificationId,
            verification.Revision,
            rawRecordId,
            new("copilot-cli", "exact-native"));

        Assert.Equal(DoctorResultCode.PartialFactSnapshot, result.Code);
        Assert.Equal(DoctorVerificationState.Active, result.Verification?.State);
        Assert.DoesNotContain(
            result.Evaluation?.States ?? [],
            state => state.StateCode == DoctorStateCode.FirstTraceReady);
        Assert.Empty(result.Verification!.AcceptedEvidenceRefs);
    }

    [Fact]
    public void ContinueSelected_NoExactCandidateOrSyntheticCandidateStaysActiveAndRestartRequired()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, "cli");
        var service = Doctor(database.Path);
        Assert.Equal(
            DoctorResultCode.VerificationActive,
            service.ObserveCandidate(new DoctorEvidenceCandidate(
                Guid.CreateVersion7().ToString("D"), verification.VerificationId,
                "github-copilot-cli", "github-copilot-doctor",
                DoctorEvidenceClass.SyntheticProbe, DoctorEvidenceKind.Ingest,
                "synthetic_receiver_probe", Now, ExpiresAt)).Code);
        var setupLikeRaw = CommitRaw(database.Path, "setup-success");
        var setup = Direct("cli", SetupCommand.Apply, SetupCodes.ApplySucceeded);

        var beforeEvidence = GitHubCopilotDoctorOrchestrator.EvaluateSetup(
            new FixedTimeProvider(Now), setup, "cli");
        Assert.Equal(DoctorStateCode.AgentRestartRequired, beforeEvidence.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorNextAction.RestartSourceProcess, beforeEvidence.Evaluation?.PrimaryState?.NextAction);

        var result = GitHubCopilotDoctorOrchestrator.ContinueSelected(
            database.Path, new FixedTimeProvider(Now),
            setup,
            "cli", verification.VerificationId, verification.Revision, setupLikeRaw, nativeSession: null);

        Assert.Equal(DoctorResultCode.VerificationActive, result.Code);
        Assert.Null(result.Evaluation);
        Assert.Equal(DoctorVerificationState.Active, result.Verification?.State);
        Assert.Empty(result.Verification!.AcceptedEvidenceRefs);
        Assert.Equal(1, Count(database.Path, "doctor_verification_evidence"));
    }

    [Fact]
    public void ContinueSelected_AppSdkRequiresNativeIdentityBeforeCandidateWrites()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, "app-sdk");
        var rawRecordId = CommitRaw(database.Path, "copilot-app-sdk");

        var result = GitHubCopilotDoctorOrchestrator.ContinueSelected(
            database.Path, new FixedTimeProvider(Now),
            Direct("app-sdk", SetupCommand.Apply, SetupCodes.NoChanges),
            "app-sdk", verification.VerificationId, verification.Revision, rawRecordId, nativeSession: null);

        Assert.Equal(DoctorResultCode.InvalidArguments, result.Code);
        Assert.Equal(0, Count(database.Path, "doctor_verification_evidence"));
        Assert.Equal(verification, Read(database.Path, verification.VerificationId));
    }

    [Fact]
    public void ContinueSelected_AppSdkExactEvidencePersistsTrustedCandidatesAndRemainsHonestlyPartial()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, "app-sdk");
        var rawRecordId = CommitRaw(database.Path, "copilot-app-sdk");
        CompleteProjection(database.Path, rawRecordId);
        WriteSession(database.Path, "copilot-sdk", "copilot-sdk-stream");
        var setup = Direct("app-sdk", SetupCommand.Apply, SetupCodes.NoChanges);

        var result = GitHubCopilotDoctorOrchestrator.ContinueSelected(
            database.Path, new FixedTimeProvider(Now), setup, "app-sdk",
            verification.VerificationId, verification.Revision, rawRecordId,
            new("copilot-sdk", "exact-native"));

        Assert.Equal(DoctorResultCode.PartialFactSnapshot, result.Code);
        Assert.Equal(DoctorVerificationState.Active, result.Verification?.State);
        Assert.NotNull(result.Evaluation);
        Assert.DoesNotContain(
            result.Evaluation.States,
            state => state.StateCode == DoctorStateCode.FirstTraceReady);
        Assert.Equal(5, Count(database.Path, "doctor_verification_evidence"));
        Assert.Empty(result.Verification!.AcceptedEvidenceRefs);
    }

    [Fact]
    public void ContinueSelected_WrongRevisionIsStaleBeforeCandidateWrites()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, "vscode");
        var rawRecordId = CommitRaw(database.Path, "vscode-copilot-chat");

        var result = GitHubCopilotDoctorOrchestrator.ContinueSelected(
            database.Path, new FixedTimeProvider(Now),
            Direct("vscode", SetupCommand.Apply, SetupCodes.ApplySucceeded),
            "vscode", verification.VerificationId, verification.Revision + 1, rawRecordId,
            new("vscode", "exact-native"));

        Assert.Equal(DoctorResultCode.VerificationStale, result.Code);
        Assert.Equal(0, Count(database.Path, "doctor_verification_evidence"));
        Assert.Equal(verification, Read(database.Path, verification.VerificationId));
    }

    [Fact]
    public void CancelAfterRollback_UsesExactRevisionAndStaleAttemptDoesNotMutate()
    {
        using var database = TempDatabase.Create();
        var verification = Start(database.Path, "cli");
        var rollback = Direct("cli", SetupCommand.Rollback, SetupCodes.RollbackSucceeded);

        var stale = GitHubCopilotDoctorOrchestrator.CancelAfterRollback(
            database.Path, new FixedTimeProvider(Now), rollback, "cli",
            verification.VerificationId, verification.Revision + 1);
        Assert.Equal(DoctorResultCode.VerificationStale, stale.Code);
        Assert.Equal(verification, Read(database.Path, verification.VerificationId));

        var cancelled = GitHubCopilotDoctorOrchestrator.CancelAfterRollback(
            database.Path, new FixedTimeProvider(Now), rollback, "cli",
            verification.VerificationId, verification.Revision);
        Assert.Equal(DoctorResultCode.VerificationCancelled, cancelled.Code);
        Assert.Equal(DoctorVerificationState.Cancelled, cancelled.Verification?.State);
        Assert.Equal(1, Count(database.Path, "doctor_verifications"));
    }

    [Fact]
    public void CancelAfterRollback_RejectsFailedNonmatchingCommandAndWrongTargetWithoutMutation()
    {
        var cases = new[]
        {
            (Setup: Failed(SetupCommand.Rollback, SetupCodes.RollbackStale), Target: "cli"),
            (Setup: Direct("cli", SetupCommand.Plan, SetupCodes.PlanReady), Target: "cli"),
            (Setup: Direct("cli", SetupCommand.Rollback, SetupCodes.RollbackSucceeded), Target: "vscode"),
        };

        foreach (var @case in cases)
        {
            using var database = TempDatabase.Create();
            var verification = Start(database.Path, "cli");

            var result = GitHubCopilotDoctorOrchestrator.CancelAfterRollback(
                database.Path, new FixedTimeProvider(Now), @case.Setup, @case.Target,
                verification.VerificationId, verification.Revision);

            Assert.Equal(DoctorResultCode.InvalidArguments, result.Code);
            Assert.Equal(verification, Read(database.Path, verification.VerificationId));
            Assert.Equal(0, Count(database.Path, "doctor_verification_evidence"));
        }
    }

    [Fact]
    public void OrchestratorAndSourceSpecificDtosRemainInternal()
    {
        var assembly = typeof(GitHubCopilotDoctorFactMapper).Assembly;
        var orchestrator = assembly.GetType(
            "CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot.GitHubCopilotDoctorOrchestrator",
            throwOnError: false);

        Assert.NotNull(orchestrator);
        Assert.False(orchestrator.IsVisible);
        Assert.DoesNotContain(
            assembly.ExportedTypes,
            type => type.Namespace == orchestrator.Namespace &&
                type.Name.StartsWith("GitHubCopilotDoctor", StringComparison.Ordinal));
    }

    private static DoctorVerification Start(string databasePath, string target)
    {
        var result = GitHubCopilotDoctorOrchestrator.Start(
            databasePath,
            new FixedTimeProvider(Now),
            Direct(target, SetupCommand.Apply, SetupCodes.NoChanges),
            target,
            ExpiresAt);
        return Assert.IsType<DoctorVerification>(result.Verification);
    }

    private static SetupCommandResult Direct(
        string target,
        SetupCommand command,
        string code)
    {
        var operation = code == SetupCodes.NoChanges
            ? SetupOperation.NoOp
            : command == SetupCommand.Rollback ? SetupOperation.Remove : SetupOperation.Add;
        var directTarget = Target(target, null, null, operation) with
        {
            RollbackAvailable = command == SetupCommand.Apply && code == SetupCodes.ApplySucceeded,
        };
        var result = new SetupCommandResult(
            command, true, code, Guid.CreateVersion7().ToString("D"), null, null,
            "github-copilot", [directTarget], [], [], [], false);
        SetupContractValidator.Validate(result);
        return result;
    }

    private static SetupCommandResult Failed(SetupCommand command, string code)
    {
        var result = new SetupCommandResult(
            command, false, code, Guid.CreateVersion7().ToString("D"), null, null,
            "github-copilot", [], [], [], [], false);
        SetupContractValidator.Validate(result);
        return result;
    }

    private static SetupCommandResult Status(
        string target,
        SetupReferenceState reference,
        SetupCurrentState current,
        SetupChangeSetState state,
        int vscodeTargets = 1,
        SetupReferenceState? secondReference = null,
        SetupCurrentState? secondCurrent = null)
    {
        var operation = state == SetupChangeSetState.NoChanges ? SetupOperation.NoOp : SetupOperation.Add;
        var targets = target == "vscode" && vscodeTargets == 2
            ? new[]
            {
                Target(target, reference, current, operation, "vscode-stable-default-user-settings"),
                Target(target, secondReference ?? reference, secondCurrent ?? current, operation, "vscode-insiders-default-user-settings"),
            }
            : new[] { Target(target, reference, current, operation) };
        var aggregate = target == "app-sdk"
            ? SetupCurrentState.NotApplicable
            : targets.Any(item => item.CurrentState == SetupCurrentState.Diverged) ? SetupCurrentState.Diverged
            : targets.Any(item => item.CurrentState == SetupCurrentState.Stale) ? SetupCurrentState.Stale
            : current;
        var rollbackAvailable = target != "app-sdk" && state == SetupChangeSetState.Applied &&
            operation != SetupOperation.NoOp && targets.All(item => item.CurrentState == SetupCurrentState.Current);
        targets = targets.Select(item => item with { RollbackAvailable = rollbackAvailable }).ToArray();
        var changeSet = new SetupChangeSetStatusResult(
            Guid.CreateVersion7().ToString("D"), "github-copilot", target,
            Now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
            Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), state,
            state == SetupChangeSetState.NoChanges ? SetupCodes.NoChanges : SetupCodes.ApplySucceeded,
            aggregate, rollbackAvailable, targets);
        var result = new SetupCommandResult(SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null,
            "github-copilot", [], [changeSet], [], [], false);
        SetupContractValidator.Validate(result);
        return result;
    }

    private static SetupCommandResult MultipleStatus(string target)
    {
        var first = Status(target, SetupReferenceState.Desired, SetupCurrentState.Current, SetupChangeSetState.Applied);
        var result = first with
        {
            ChangeSets = [first.ChangeSets[0], first.ChangeSets[0] with { ChangeSetId = Guid.CreateVersion7().ToString("D") }],
        };
        SetupContractValidator.Validate(result);
        return result;
    }

    private static SetupTargetResult Target(
        string target,
        SetupReferenceState? reference,
        SetupCurrentState? current,
        SetupOperation operation,
        string? label = null)
    {
        if (target == "app-sdk")
        {
            var guidance = SetupContractValidator.RehydrateStatusGuidance(
                new SetupStatusGuidance("caller_managed_sample", "dotnet"),
                "github-copilot-app-sdk-guidance");
            return new(Guid.CreateVersion7().ToString("D"), SetupTargetKind.Guidance, "github-copilot-app-sdk-guidance",
                true, "1.0.0", SetupOperation.NoOp, null, reference, current,
                SetupRestartRequirement.None, false, null, null, guidance, []);
        }

        var vscode = target == "vscode";
        var keys = vscode
            ? new[] { "github.copilot.chat.otel.enabled", "github.copilot.chat.otel.exporterType", "github.copilot.chat.otel.otlpEndpoint" }
            : new[] { "COPILOT_OTEL_ENABLED", "COPILOT_OTEL_EXPORTER_TYPE", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL" };
        return new(
            Guid.CreateVersion7().ToString("D"), vscode ? SetupTargetKind.Json : SetupTargetKind.Env,
            label ?? (vscode ? "vscode-stable-default-user-settings" : "copilot-cli-user-environment"),
            true, "1.0.4", operation,
            vscode ? SetupEffectiveSource.UserSetting : SetupEffectiveSource.Environment,
            reference, current,
            vscode ? SetupRestartRequirement.RestartVsCode : SetupRestartRequirement.RestartTerminalSession,
            current == SetupCurrentState.Current && operation != SetupOperation.NoOp,
            "http://127.0.0.1:4320", Expected(target), null,
            keys.Select(key => new SetupMemberChangeResult(
                key, operation,
                operation == SetupOperation.Add ? "absent" : "present_desired",
                operation == SetupOperation.Remove ? "absent" : "present_desired",
                "none", false)).ToArray());
    }

    private static System.Text.Json.JsonElement Expected(string target)
    {
        var surface = target == "vscode" ? "github-copilot-vscode" : "github-copilot-cli";
        return SourceCapabilityManifestLoader.LoadForSurface(surface).CanonicalJson.Clone();
    }

    private static long CommitRaw(
        string databasePath,
        string clientKind,
        bool supportedCompatibility = false)
    {
        new SqliteSourceCompatibilityStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        var payload = "{\"resourceSpans\":[{\"resource\":{\"attributes\":[{\"key\":\"client.kind\",\"value\":{\"stringValue\":\""
            + clientKind
            + "\"}}"
            + (clientKind == "copilot-cli"
                ? ",{\"key\":\"service.name\",\"value\":{\"stringValue\":\"github-copilot\"}}"
                : string.Empty)
            + "]},\"scopeSpans\":[{"
            + (clientKind == "copilot-cli" ? "\"scope\":{\"name\":\"github.copilot\"}," : string.Empty)
            + "\"spans\":[{\"traceId\":\""
            + TraceId
            + "\",\"spanId\":\"span\"}]}]}]}";
        var inventory = OtlpJsonStructuralWalker.Build(payload, Now);
        var registry = supportedCompatibility
            ? VerifiedSourceFingerprintRegistry.Create(
                [VerifiedSourceFingerprintEvidence.Create(
                    RawTelemetrySources.RawOtlp,
                    "fixture-v1",
                    inventory.SchemaFingerprint)],
                [],
                [])
            : VerifiedSourceFingerprintRegistry.Create([], [], []);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"), RawTelemetrySources.RawOtlp, null,
            RawTelemetrySources.RawOtlp, "1", inventory,
            SourceCompatibilityEvaluator.Assess(
                RawTelemetrySources.RawOtlp, null, inventory, 1,
                registry),
            SourceCaptureContentState.Available, Now);
        var raw = new RawTelemetryRecord(
            null, RawTelemetrySources.RawOtlp, TraceId, Now,
            "{\"client.kind\":\"" + clientKind + "\"}", payload);
        return new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(raw, observation)).RawRecordId;
    }

    private static void CompleteProjection(string databasePath, long rawRecordId)
    {
        var store = new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter);
        var disposition = Assert.IsType<ProjectionDisposition>(store.GetProjectionDisposition(rawRecordId));
        Assert.True(store.TryBeginProjection(rawRecordId, disposition.Revision, Now.AddSeconds(1)));
        disposition = Assert.IsType<ProjectionDisposition>(store.GetProjectionDisposition(rawRecordId));
        Assert.True(store.ApplyProjection(
            rawRecordId, RawTelemetrySources.RawOtlp, Now,
            new MonitorRecordProjection(null, null, 1, []), Now.AddSeconds(2), disposition.Revision));
    }

    private static void WriteSession(string databasePath, string nativeSurface, string eventAdapter)
    {
        var store = new SqliteSessionStore(databasePath, new FixedTimeProvider(Now));
        store.CreateSchema();
        var surface = SessionWire.ParseSourceSurface(nativeSurface);
        var session = ObservedSession.Create(
            ObservedSessionStatus.Completed, SessionCompleteness.Full, null, null,
            Now, Now, Now, SessionRawRetentionState.Expiring);
        var native = new SessionNativeId(session.SessionId, surface, "exact-native", SessionBindingKind.Native, Now);
        var run = ObservedSessionRun.Create(session.SessionId, ObservedSessionStatus.Completed) with
        { SourceSurface = surface, TraceId = TraceId, StartedAt = Now, EndedAt = Now };
        var @event = ObservedSessionEvent.Create(
            session.SessionId, run.RunId, eventAdapter, "event-1", "assistant.completed", Now,
            SessionContentState.Available) with { SourceSurface = surface, TraceId = TraceId };
        store.Write(new SessionWriteBatch(new SessionDetail(session, [native], [run], [@event]), []));
    }

    private static SqliteDoctorApplicationService Doctor(string databasePath) =>
        SqliteDoctorApplicationService.Create(
            new SqliteDoctorVerificationStore(databasePath, new FixedTimeProvider(Now)));

    private static DoctorVerification Read(string databasePath, string verificationId) =>
        Assert.IsType<DoctorVerification>(Doctor(databasePath).Status(verificationId).Verification);

    private static int Count(string databasePath, string table)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = databasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TempDatabase(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"issue103-orchestrator-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}

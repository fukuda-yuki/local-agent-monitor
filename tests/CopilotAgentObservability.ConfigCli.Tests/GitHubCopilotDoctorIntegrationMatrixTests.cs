using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Capabilities;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.Doctor;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotDoctorIntegrationMatrixTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-17T04:00:00Z");

    [Theory]
    [InlineData(SetupCurrentState.Stale)]
    [InlineData(SetupCurrentState.Diverged)]
    public void Start_RejectsNoncurrentSetupWithoutPersistingVerificationOrCandidates(SetupCurrentState current)
    {
        using var database = TempDatabase.Create();
        var setup = Status(
            "cli",
            current == SetupCurrentState.Diverged ? SetupReferenceState.None : SetupReferenceState.Desired,
            current,
            SetupChangeSetState.Applied);

        var result = GitHubCopilotDoctorOrchestrator.Start(
            database.Path, new FixedTimeProvider(Now), setup, "cli", Now.AddMinutes(5));

        Assert.Equal(DoctorResultCode.InvalidArguments, result.Code);
        Assert.Null(result.Verification);
        Assert.False(File.Exists(database.Path));
    }

    [Fact]
    public void Rollback_CancelsActiveVerificationThenProjectsDeterministicNonreadySetupState()
    {
        using var database = TempDatabase.Create();
        var started = GitHubCopilotDoctorOrchestrator.Start(
            database.Path, new FixedTimeProvider(Now), Direct("vscode", SetupCommand.Apply, SetupCodes.NoChanges),
            "vscode", Now.AddMinutes(5));
        var verification = Assert.IsType<DoctorVerification>(started.Verification);
        var rollback = Direct("vscode", SetupCommand.Rollback, SetupCodes.RollbackSucceeded);

        var cancelled = GitHubCopilotDoctorOrchestrator.CancelAfterRollback(
            database.Path, new FixedTimeProvider(Now), rollback, "vscode",
            verification.VerificationId, verification.Revision);
        var evaluation = GitHubCopilotDoctorOrchestrator.EvaluateSetup(
            new FixedTimeProvider(Now), rollback, "vscode");

        Assert.Equal(DoctorResultCode.VerificationCancelled, cancelled.Code);
        Assert.Equal(DoctorVerificationState.Cancelled, cancelled.Verification?.State);
        Assert.Equal(DoctorResultCode.EvaluationCompleted, evaluation.Code);
        Assert.NotEqual(DoctorStateCode.FirstTraceReady, evaluation.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorStateCode.AgentRestartRequired, evaluation.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorNextAction.RestartSourceProcess, evaluation.Evaluation?.PrimaryState?.NextAction);
    }

    [Fact]
    public void CliStatus_CurrentPersistedUserEnvironmentStillRequiresANewTerminalSession()
    {
        var baseline = Status(
            "cli", SetupReferenceState.Desired, SetupCurrentState.Current, SetupChangeSetState.Applied);
        var changeSet = Assert.Single(baseline.ChangeSets);
        var persistedUserCurrentProcessAbsent = Assert.Single(changeSet.Targets) with
        {
            Operation = SetupOperation.NoOp,
            Changes = Assert.Single(changeSet.Targets).Changes.Select(change => change with
            {
                Operation = SetupOperation.NoOp,
                PreviousState = "process_absent_user_present_desired",
                NewState = "present_desired",
            }).ToArray(),
        };
        var setup = baseline with
        {
            ChangeSets = [changeSet with { Targets = [persistedUserCurrentProcessAbsent] }],
        };
        SetupContractValidator.Validate(setup);
        var target = Assert.Single(Assert.Single(setup.ChangeSets).Targets);
        Assert.Equal(SetupRestartRequirement.RestartTerminalSession, target.RestartRequirement);
        Assert.All(target.Changes, change =>
            Assert.Equal("process_absent_user_present_desired", change.PreviousState));
        Assert.All(target.Changes, change => Assert.Equal("present_desired", change.NewState));

        var result = GitHubCopilotDoctorOrchestrator.EvaluateSetup(
            new FixedTimeProvider(Now), setup, "cli");

        Assert.Equal(DoctorStateCode.AgentRestartRequired, result.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(DoctorNextAction.RestartSourceProcess, result.Evaluation?.PrimaryState?.NextAction);
        Assert.DoesNotContain(result.Evaluation?.States ?? [], state => state.StateCode == DoctorStateCode.FirstTraceReady);
        Assert.Null(result.Verification);
    }

    [Theory]
    [InlineData("endpoint", DoctorStateCode.EndpointMismatch, DoctorNextAction.UpdateSourceEndpoint)]
    [InlineData("protocol", DoctorStateCode.ProtocolMismatch, DoctorNextAction.UseHttpProtobuf)]
    [InlineData("signal", DoctorStateCode.SignalDisabled, DoctorNextAction.EnableTraceSignal)]
    [InlineData("receiver", DoctorStateCode.AgentRestartRequired, DoctorNextAction.RestartSourceProcess)]
    public void MisconfigurationMapsToExactStateAndActionWithoutPromotingEvidence(
        string scenario,
        DoctorStateCode expectedState,
        DoctorNextAction expectedAction)
    {
        var setup = scenario == "receiver"
            ? Direct("cli", SetupCommand.Apply, SetupCodes.ApplySucceeded)
            : Direct("cli", SetupCommand.Plan, SetupCodes.PlanReady);
        setup = scenario == "receiver"
            ? setup with { Warnings = [SetupCodes.MonitorNotRunning] }
            : PlannedMismatch(setup, scenario switch
            {
                "endpoint" => "OTEL_EXPORTER_OTLP_ENDPOINT",
                "protocol" => "OTEL_EXPORTER_OTLP_PROTOCOL",
                _ => "COPILOT_OTEL_ENABLED",
            });
        SetupContractValidator.Validate(setup);

        var result = GitHubCopilotDoctorOrchestrator.EvaluateSetup(
            new FixedTimeProvider(Now), setup, "cli");

        var expectedStates = scenario == "receiver"
            ? new[] { DoctorStateCode.AgentRestartRequired, DoctorStateCode.EndpointUnreachable }
            : new[] { expectedState, DoctorStateCode.AgentRestartRequired };
        Assert.Equal(expectedState, result.Evaluation?.PrimaryState?.StateCode);
        Assert.Equal(expectedAction, result.Evaluation?.PrimaryState?.NextAction);
        Assert.Equal(expectedStates, result.Evaluation?.States.Select(state => state.StateCode));
        Assert.All(result.Evaluation?.States ?? [], state => Assert.Empty(state.EvidenceRefs));
        Assert.DoesNotContain(result.Evaluation?.States ?? [], state => state.StateCode == DoctorStateCode.FirstTraceReady);
        Assert.Null(result.Verification);
    }

    private static SetupCommandResult PlannedMismatch(SetupCommandResult setup, string settingKey) => setup with
    {
        Targets = setup.Targets.Select(target => target with
        {
            Operation = SetupOperation.Add,
            Changes = target.Changes.Select(change => change with
            {
                Operation = change.SettingKey == settingKey ? SetupOperation.Add : SetupOperation.NoOp,
                PreviousState = change.SettingKey == settingKey ? "absent" : "present_desired",
                NewState = "present_desired",
            }).ToArray(),
        }).ToArray(),
    };

    private static SetupCommandResult Direct(string target, SetupCommand command, string code)
    {
        var operation = code == SetupCodes.NoChanges
            ? SetupOperation.NoOp
            : command == SetupCommand.Rollback ? SetupOperation.Remove : SetupOperation.Add;
        var result = new SetupCommandResult(
            command, true, code, Guid.CreateVersion7().ToString("D"), null, null,
            "github-copilot", [Target(target, operation)], [], [], [], false);
        SetupContractValidator.Validate(result);
        return result;
    }

    private static SetupCommandResult Status(
        string target,
        SetupReferenceState reference,
        SetupCurrentState current,
        SetupChangeSetState state)
    {
        var targetResult = Target(target, SetupOperation.Add) with
        {
            ReferenceState = reference,
            CurrentState = current,
            RollbackAvailable = false,
        };
        var changeSet = new SetupChangeSetStatusResult(
            Guid.CreateVersion7().ToString("D"), "github-copilot", target,
            Now.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
            Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"), state,
            SetupCodes.ApplySucceeded, current, false, [targetResult]);
        var result = new SetupCommandResult(
            SetupCommand.Status, true, SetupCodes.StatusReady, null, null, null,
            "github-copilot", [], [changeSet], [], [], false);
        SetupContractValidator.Validate(result);
        return result;
    }

    private static SetupTargetResult Target(string target, SetupOperation operation)
    {
        if (target == "app-sdk")
        {
            var guidance = SetupContractValidator.RehydrateStatusGuidance(
                new SetupStatusGuidance("caller_managed_sample", "dotnet"),
                "github-copilot-app-sdk-guidance");
            return new(
                Guid.CreateVersion7().ToString("D"), SetupTargetKind.Guidance,
                "github-copilot-app-sdk-guidance", true, "1.0.0", SetupOperation.NoOp,
                null, null, null, SetupRestartRequirement.None, false, null, null, guidance, []);
        }

        var vscode = target == "vscode";
        var keys = vscode
            ? new[] { "github.copilot.chat.otel.enabled", "github.copilot.chat.otel.exporterType", "github.copilot.chat.otel.otlpEndpoint" }
            : new[] { "COPILOT_OTEL_ENABLED", "COPILOT_OTEL_EXPORTER_TYPE", "OTEL_EXPORTER_OTLP_ENDPOINT", "OTEL_EXPORTER_OTLP_PROTOCOL" };
        return new(
            Guid.CreateVersion7().ToString("D"), vscode ? SetupTargetKind.Json : SetupTargetKind.Env,
            vscode ? "vscode-stable-default-user-settings" : "copilot-cli-user-environment",
            true, "1.0.4", operation,
            vscode ? SetupEffectiveSource.UserSetting : SetupEffectiveSource.Environment,
            null, null,
            operation == SetupOperation.NoOp ? SetupRestartRequirement.None :
                vscode ? SetupRestartRequirement.RestartVsCode : SetupRestartRequirement.RestartTerminalSession,
            false, "http://127.0.0.1:4320", Expected(target), null,
            keys.Select(key => new SetupMemberChangeResult(
                key, operation,
                operation == SetupOperation.Add ? "absent" : "present_desired",
                operation == SetupOperation.Remove ? "absent" : "present_desired",
                "none", false)).ToArray());
    }

    private static System.Text.Json.JsonElement Expected(string target) =>
        SourceCapabilityManifestLoader.LoadForSurface(
            target == "vscode" ? "github-copilot-vscode" : "github-copilot-cli").CanonicalJson.Clone();

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TempDatabase(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TempDatabase Create() =>
            new(System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"issue103-integration-{Guid.NewGuid():N}.db"));

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}

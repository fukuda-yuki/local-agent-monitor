using CopilotAgentObservability.ConfigCli.Setup.Adapters.GitHubCopilot;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.Doctor;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotDoctorContractTests
{
    private const string Endpoint = "http://127.0.0.1:4320";
    private const string StableSettingsPath = "C:\\Users\\setup-test\\AppData\\Roaming\\Code\\User\\settings.json";
    private const string AppSdkProjectPath = "src\\CopilotAgentObservability.LocalMonitor\\CopilotAgentObservability.LocalMonitor.csproj";
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-07-17T01:02:03Z");
    private static readonly StaticFacts ConfiguredCliFacts = new(
        new(MonitorInstallStatus.Installed, SourceVersionStatus.Supported, SourceFeatureStatus.Available),
        new(MonitorProcessStatus.Running, ReceiverBindStatus.Bound, PortOwnerStatus.Monitor),
        new(EndpointAlignmentStatus.Match),
        new(ReachabilityStatus.Reachable),
        new(ProtocolStatus.HttpProtobuf, TraceSignalStatus.Enabled),
        new(SourceCompatibilityStatus.Unknown, SchemaStatus.Unknown),
        new(RestartRequirement.Required));

    [Fact]
    public void SetupResults_MapExactStaticFactsWithoutClaimingRealTraceReadiness()
    {
        var cases = new[]
        {
            CreateSetupCase(
                "vscode",
                "github-copilot-vscode",
                ConfigureVsCode,
                new StaticFacts(
                    new(MonitorInstallStatus.Installed, SourceVersionStatus.Supported, SourceFeatureStatus.Available),
                    new(MonitorProcessStatus.Running, ReceiverBindStatus.Bound, PortOwnerStatus.Monitor),
                    new(EndpointAlignmentStatus.Mismatch),
                    new(ReachabilityStatus.Reachable),
                    new(ProtocolStatus.Mismatch, TraceSignalStatus.Disabled),
                    new(SourceCompatibilityStatus.Unknown, SchemaStatus.Unknown),
                    new(RestartRequirement.NotRequired))),
            CreateSetupCase("cli", "github-copilot-cli", ConfigureNoOpCli, ConfiguredCliFacts),
            CreateSetupCase(
                "app-sdk",
                "github-copilot-app-sdk",
                ConfigureAppSdk,
                new StaticFacts(
                    new(MonitorInstallStatus.Unknown, SourceVersionStatus.Unknown, SourceFeatureStatus.Available),
                    new(MonitorProcessStatus.Unknown, ReceiverBindStatus.Unknown, PortOwnerStatus.Unknown),
                    new(EndpointAlignmentStatus.Unknown),
                    new(ReachabilityStatus.Unknown),
                    new(ProtocolStatus.Unknown, TraceSignalStatus.Unknown),
                    new(SourceCompatibilityStatus.Unknown, SchemaStatus.Unknown),
                    new(RestartRequirement.NotRequired))),
        };

        foreach (var setupCase in cases)
        {
            Assert.True(setupCase.Result.Success);
            Assert.Equal("github-copilot", setupCase.Result.Adapter);

            var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(
                setupCase.Result,
                setupCase.SelectedTarget,
                ObservedAt);

            Assert.Equal(DoctorSchemaVersions.FactsV1, snapshot.SchemaVersion);
            Assert.Equal(setupCase.ExpectedSourceSurface, snapshot.SourceSurface);
            Assert.Equal("github-copilot-doctor", snapshot.ExpectedSourceAdapter);
            Assert.Equal(ObservedAt, snapshot.ObservedAt);
            Assert.Null(snapshot.VerificationId);
            AssertStaticFacts(snapshot, setupCase.ExpectedFacts);
            AssertUnknownRuntimeFacts(snapshot);
            AssertNoRealTraceReadiness(snapshot);

            var canonicalJson = DoctorJson.SerializeResult(DoctorEvaluator.Evaluate(snapshot));
            Assert.Equal(canonicalJson, DoctorJson.SerializeResult(DoctorJson.DeserializeResult(canonicalJson)));
        }

        AssertApplyAndRollbackFacts();
    }

    private static SetupCase CreateSetupCase(
        string selectedTarget,
        string expectedSourceSurface,
        Action<SetupTestPlatform> configure,
        StaticFacts expectedFacts)
    {
        var platform = new SetupTestPlatform(ObservedAt);
        configure(platform);
        var result = SetupCompositionRoot.CreateSetupDispatch(platform)(PlanOptions(selectedTarget));
        return new SetupCase(selectedTarget, expectedSourceSurface, result, expectedFacts);
    }

    private static void AssertApplyAndRollbackFacts()
    {
        var platform = new SetupTestPlatform(ObservedAt);
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptLiveEndpoint(platform);
        ScriptLiveEndpoint(platform);
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(platform);
        var plan = dispatch(PlanOptions("cli"));
        var changeSetId = Guid.Parse(Assert.IsType<string>(plan.ChangeSetId));
        var apply = dispatch(new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId));
        var rollback = dispatch(new SetupOptions(SetupCommand.Rollback, null, null, null, false, changeSetId));

        Assert.Equal(SetupCodes.ApplySucceeded, apply.Code);
        Assert.Equal(SetupCodes.RollbackSucceeded, rollback.Code);

        var appliedSnapshot = GitHubCopilotDoctorFactMapper.FromSetup(apply, "cli", ObservedAt);
        AssertStaticFacts(appliedSnapshot, ConfiguredCliFacts);
        AssertUnknownRuntimeFacts(appliedSnapshot);
        AssertNoRealTraceReadiness(appliedSnapshot);

        var rolledBackSnapshot = GitHubCopilotDoctorFactMapper.FromSetup(rollback, "cli", ObservedAt);
        AssertStaticFacts(
            rolledBackSnapshot,
            new StaticFacts(
                new(MonitorInstallStatus.Unknown, SourceVersionStatus.Unknown, SourceFeatureStatus.Unknown),
                new(MonitorProcessStatus.Unknown, ReceiverBindStatus.Unknown, PortOwnerStatus.Unknown),
                new(EndpointAlignmentStatus.Mismatch),
                new(ReachabilityStatus.Unknown),
                new(ProtocolStatus.Mismatch, TraceSignalStatus.Disabled),
                new(SourceCompatibilityStatus.Unknown, SchemaStatus.Unknown),
                new(RestartRequirement.Required)));
        AssertUnknownRuntimeFacts(rolledBackSnapshot);
        AssertNoRealTraceReadiness(rolledBackSnapshot);
    }

    private static SetupOptions PlanOptions(string selectedTarget) => new(
        SetupCommand.Plan,
        "github-copilot",
        selectedTarget,
        Endpoint,
        IncludeContentCapture: false,
        ChangeSetId: null);

    private static void ConfigureVsCode(SetupTestPlatform platform)
    {
        platform.SeedFile(StableSettingsPath, "{}"u8.ToArray());
        ScriptVersion(platform, "code", ["--version"], "1.128.0");
        platform.ScriptProcess(
            "code",
            ["--list-extensions", "--show-versions"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "GitHub.copilot-chat@0.26.0\n"));
        ScriptLiveEndpoint(platform);
    }

    private static void ConfigureNoOpCli(SetupTestPlatform platform)
    {
        platform.SeedUserEnvironment("COPILOT_OTEL_ENABLED", "true");
        platform.SeedUserEnvironment("COPILOT_OTEL_EXPORTER_TYPE", "otlp-http");
        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", Endpoint);
        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptLiveEndpoint(platform);
    }

    private static void ConfigureAppSdk(SetupTestPlatform platform) =>
        platform.SeedFile(
            AppSdkProjectPath,
            "<Project><ItemGroup><PackageReference Include=\"GitHub.Copilot.SDK\" Version=\"1.0.4\" /></ItemGroup></Project>"u8.ToArray());

    private static void ScriptVersion(
        SetupTestPlatform platform,
        string executable,
        IReadOnlyList<string> arguments,
        string version) =>
        platform.ScriptProcess(
            executable,
            arguments,
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, version + Environment.NewLine));

    private static void ScriptLiveEndpoint(SetupTestPlatform platform) =>
        platform.ScriptHttpProbe(new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            17,
            "{\"status\":\"live\"}"u8.ToArray(),
            true));

    private static void AssertStaticFacts(DoctorFactSnapshot snapshot, StaticFacts expected)
    {
        Assert.Equal(expected.InstallAndSourceVersion, snapshot.InstallAndSourceVersion);
        Assert.Equal(expected.ProcessReceiverAndPort, snapshot.ProcessReceiverAndPort);
        Assert.Equal(expected.SourceEffectiveConfiguration, snapshot.SourceEffectiveConfiguration);
        Assert.Equal(expected.EndpointReachability, snapshot.EndpointReachability);
        Assert.Equal(expected.ProtocolAndSignalCompatibility, snapshot.ProtocolAndSignalCompatibility);
        Assert.Equal(expected.SourceVersionAndSchemaDiagnostics, snapshot.SourceVersionAndSchemaDiagnostics);
        Assert.Equal(expected.RestartOrNewProcess, snapshot.RestartOrNewProcess);
    }

    private static void AssertUnknownRuntimeFacts(DoctorFactSnapshot snapshot)
    {
        Assert.True(snapshot.LastIngest is null or { Outcome: LastIngestOutcome.Unknown });
        Assert.True(snapshot.RawPersistence is null or { Outcome: RawPersistenceOutcome.Unknown });
        Assert.True(snapshot.Projection is null or { Outcome: ProjectionOutcome.Unknown });
        Assert.True(snapshot.ExactSessionBinding is null or
        {
            Requirement: ExactSessionBindingRequirement.Unknown,
            Outcome: ExactSessionBindingOutcome.Unknown,
        });
        Assert.True(snapshot.CompletenessAndContent is null or
        {
            Completeness: DoctorCompleteness.Unknown,
            ContentCapture: ContentCaptureStatus.Unknown,
            RawAccess: RawAccessStatus.Unknown,
        });
    }

    private static void AssertNoRealTraceReadiness(DoctorFactSnapshot snapshot)
    {
        Assert.DoesNotContain(snapshot.Observations, observation => observation.EvidenceClass == DoctorEvidenceClass.RealSource);
        var result = DoctorEvaluator.Evaluate(snapshot);
        Assert.DoesNotContain(result.Evaluation?.States ?? [], state => state.StateCode == DoctorStateCode.FirstTraceReady);
    }

    private sealed record StaticFacts(
        InstallAndSourceVersionFacts InstallAndSourceVersion,
        ProcessReceiverAndPortFacts ProcessReceiverAndPort,
        SourceEffectiveConfigurationFacts SourceEffectiveConfiguration,
        EndpointReachabilityFacts EndpointReachability,
        ProtocolAndSignalCompatibilityFacts ProtocolAndSignalCompatibility,
        SourceVersionAndSchemaDiagnosticsFacts SourceVersionAndSchemaDiagnostics,
        RestartOrNewProcessFacts RestartOrNewProcess);

    private sealed record SetupCase(
        string SelectedTarget,
        string ExpectedSourceSurface,
        SetupCommandResult Result,
        StaticFacts ExpectedFacts);
}

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
    private const string InsidersSettingsPath = "C:\\Users\\setup-test\\AppData\\Roaming\\Code - Insiders\\User\\settings.json";
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
                    new(RestartRequirement.Unknown))),
            CreateSetupCase(
                "app-sdk",
                "github-copilot-app-sdk",
                _ => { },
                new StaticFacts(
                    new(MonitorInstallStatus.Unknown, SourceVersionStatus.Unknown, SourceFeatureStatus.Unavailable),
                    new(MonitorProcessStatus.Unknown, ReceiverBindStatus.Unknown, PortOwnerStatus.Unknown),
                    new(EndpointAlignmentStatus.Unknown),
                    new(ReachabilityStatus.Unknown),
                    new(ProtocolStatus.Unknown, TraceSignalStatus.Unknown),
                    new(SourceCompatibilityStatus.Unknown, SchemaStatus.Unknown),
                    new(RestartRequirement.Unknown))),
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

    [Theory]
    [InlineData("vscode", "endpoint")]
    [InlineData("vscode", "protocol")]
    [InlineData("vscode", "signal")]
    [InlineData("cli", "endpoint")]
    [InlineData("cli", "exporter_type")]
    [InlineData("cli", "otel_protocol")]
    [InlineData("cli", "signal")]
    public void Plan_MapsEndpointProtocolAndSignalIndependently(
        string selectedTarget,
        string differingCategory)
    {
        var platform = new SetupTestPlatform(ObservedAt);
        if (selectedTarget == "vscode")
        {
            ConfigureVsCodeMemberMatrix(platform, differingCategory);
        }
        else
        {
            ConfigureCliMemberMatrix(platform, differingCategory);
        }

        var result = SetupCompositionRoot.CreateSetupDispatch(platform)(PlanOptions(selectedTarget));
        var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(result, selectedTarget, ObservedAt);
        var expectedEndpoint = differingCategory == "endpoint"
            ? EndpointAlignmentStatus.Mismatch
            : EndpointAlignmentStatus.Match;
        var expectedProtocol = differingCategory is "protocol" or "exporter_type" or "otel_protocol"
            ? ProtocolStatus.Mismatch
            : ProtocolStatus.HttpProtobuf;
        var expectedSignal = differingCategory == "signal"
            ? TraceSignalStatus.Disabled
            : TraceSignalStatus.Enabled;

        Assert.True(result.Success);
        Assert.Equal(expectedEndpoint, snapshot.SourceEffectiveConfiguration?.EndpointAlignment);
        Assert.Equal(expectedProtocol, snapshot.ProtocolAndSignalCompatibility?.Protocol);
        Assert.Equal(expectedSignal, snapshot.ProtocolAndSignalCompatibility?.TraceSignal);
    }

    [Theory]
    [InlineData(SetupCurrentState.Current, EndpointAlignmentStatus.Match, ProtocolStatus.HttpProtobuf, TraceSignalStatus.Enabled)]
    [InlineData(SetupCurrentState.Stale, EndpointAlignmentStatus.Unknown, ProtocolStatus.Unknown, TraceSignalStatus.Unknown)]
    public void Status_MapsOneExactSelectedChangeSet(
        SetupCurrentState currentState,
        EndpointAlignmentStatus expectedEndpoint,
        ProtocolStatus expectedProtocol,
        TraceSignalStatus expectedSignal)
    {
        var status = CreateCliStatus(currentState);
        var changeSet = Assert.Single(status.ChangeSets);
        var target = Assert.Single(changeSet.Targets);
        Assert.Equal(currentState, changeSet.CurrentState);
        Assert.Equal(changeSet.CurrentState, target.CurrentState);
        var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(status, "cli", ObservedAt);

        Assert.Equal(expectedEndpoint, snapshot.SourceEffectiveConfiguration?.EndpointAlignment);
        Assert.Equal(expectedProtocol, snapshot.ProtocolAndSignalCompatibility?.Protocol);
        Assert.Equal(expectedSignal, snapshot.ProtocolAndSignalCompatibility?.TraceSignal);
        AssertStatusDetectionUnknown(snapshot);
    }

    [Theory]
    [InlineData("planned", "endpoint")]
    [InlineData("planned", "exporter_type")]
    [InlineData("planned", "otel_protocol")]
    [InlineData("planned", "signal")]
    [InlineData("applied", "endpoint")]
    [InlineData("applied", "exporter_type")]
    [InlineData("applied", "otel_protocol")]
    [InlineData("applied", "signal")]
    [InlineData("rolledback", "endpoint")]
    [InlineData("rolledback", "exporter_type")]
    [InlineData("rolledback", "otel_protocol")]
    [InlineData("rolledback", "signal")]
    public void Status_CurrentUsesThePublishedReferenceAndPerKeyPreviousDisposition(
        string lifecycle,
        string differingCategory)
    {
        var platform = new SetupTestPlatform(ObservedAt);
        SeedCliValues(platform, differingCategory);
        var operationCount = lifecycle == "planned" ? 1 : 2;
        for (var index = 0; index < operationCount; index++)
        {
            ScriptVersion(platform, "copilot", ["version"], "1.0.4");
            ScriptLiveEndpoint(platform);
        }
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(platform);
        var plan = dispatch(PlanOptions("cli"));
        var changeSetId = Guid.Parse(Assert.IsType<string>(plan.ChangeSetId));
        if (lifecycle is "applied" or "rolledback")
        {
            Assert.Equal(
                SetupCodes.ApplySucceeded,
                dispatch(new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId)).Code);
        }
        if (lifecycle == "rolledback")
        {
            Assert.Equal(
                SetupCodes.RollbackSucceeded,
                dispatch(new SetupOptions(SetupCommand.Rollback, null, null, null, false, changeSetId)).Code);
        }

        var status = dispatch(StatusOptions());
        var changeSet = Assert.Single(status.ChangeSets);
        var target = Assert.Single(changeSet.Targets);
        var expectedReference = lifecycle switch
        {
            "planned" => SetupReferenceState.Base,
            "applied" => SetupReferenceState.Desired,
            "rolledback" => SetupReferenceState.Previous,
            _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
        };
        Assert.Equal(SetupCurrentState.Current, changeSet.CurrentState);
        Assert.Equal(SetupCurrentState.Current, target.CurrentState);
        Assert.Equal(expectedReference, target.ReferenceState);
        AssertCliPreviousStates(target, differingCategory);

        var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(status, "cli", ObservedAt);
        var isDesired = expectedReference == SetupReferenceState.Desired;
        Assert.Equal(
            !isDesired && differingCategory == "endpoint" ? EndpointAlignmentStatus.Mismatch : EndpointAlignmentStatus.Match,
            snapshot.SourceEffectiveConfiguration?.EndpointAlignment);
        Assert.Equal(
            !isDesired && differingCategory is "exporter_type" or "otel_protocol" ? ProtocolStatus.Mismatch : ProtocolStatus.HttpProtobuf,
            snapshot.ProtocolAndSignalCompatibility?.Protocol);
        Assert.Equal(
            !isDesired && differingCategory == "signal" ? TraceSignalStatus.Disabled : TraceSignalStatus.Enabled,
            snapshot.ProtocolAndSignalCompatibility?.TraceSignal);
        AssertStatusDetectionUnknown(snapshot);
    }

    [Fact]
    public void Status_VsCodeCombinesCurrentAndStaleTargetsWithoutAggregateInference()
    {
        var platform = new SetupTestPlatform(ObservedAt);
        SeedDirectoryChain(platform, Path.GetDirectoryName(StableSettingsPath)!);
        SeedDirectoryChain(platform, Path.GetDirectoryName(InsidersSettingsPath)!);
        platform.SeedFile(StableSettingsPath, "{}"u8.ToArray());
        platform.SeedFile(InsidersSettingsPath, "{}"u8.ToArray());
        for (var index = 0; index < 2; index++)
        {
            ScriptVsCodeChannel(platform, "code");
            ScriptVsCodeChannel(platform, "code-insiders");
            ScriptLiveEndpoint(platform);
        }
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(platform);
        var plan = dispatch(PlanOptions("vscode"));
        var changeSetId = Guid.Parse(Assert.IsType<string>(plan.ChangeSetId));
        Assert.Equal(
            SetupCodes.ApplySucceeded,
            dispatch(new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId)).Code);
        platform.SeedFile(
            InsidersSettingsPath,
            System.Text.Encoding.UTF8.GetBytes(
                """{"github.copilot.chat.otel.enabled":true,"github.copilot.chat.otel.exporterType":"otlp-http","github.copilot.chat.otel.otlpEndpoint":"http://127.0.0.1:4999"}"""));

        var status = dispatch(StatusOptions());
        var changeSet = Assert.Single(status.ChangeSets);
        Assert.Equal(SetupCurrentState.Stale, changeSet.CurrentState);
        Assert.Collection(
            changeSet.Targets,
            stable =>
            {
                Assert.Equal("vscode-stable-default-user-settings", stable.TargetLabel);
                Assert.Equal(SetupReferenceState.Desired, stable.ReferenceState);
                Assert.Equal(SetupCurrentState.Current, stable.CurrentState);
            },
            insiders =>
            {
                Assert.Equal("vscode-insiders-default-user-settings", insiders.TargetLabel);
                Assert.Equal(SetupReferenceState.Desired, insiders.ReferenceState);
                Assert.Equal(SetupCurrentState.Stale, insiders.CurrentState);
            });

        var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(status, "vscode", ObservedAt);
        Assert.Equal(EndpointAlignmentStatus.Unknown, snapshot.SourceEffectiveConfiguration?.EndpointAlignment);
        Assert.Equal(ProtocolStatus.Unknown, snapshot.ProtocolAndSignalCompatibility?.Protocol);
        Assert.Equal(TraceSignalStatus.Unknown, snapshot.ProtocolAndSignalCompatibility?.TraceSignal);
        AssertStatusDetectionUnknown(snapshot);
    }

    [Theory]
    [InlineData("planned", SetupReferenceState.Base, RestartRequirement.Unknown)]
    [InlineData("applied", SetupReferenceState.Desired, RestartRequirement.NotRequired)]
    [InlineData("rolledback", SetupReferenceState.Previous, RestartRequirement.Required)]
    public void Status_VsCodeRestartFollowsLifecycleInsteadOfImmutablePlanRequirement(
        string lifecycle,
        SetupReferenceState expectedReference,
        RestartRequirement expectedRestart)
    {
        var platform = new SetupTestPlatform(ObservedAt);
        SeedDirectoryChain(platform, Path.GetDirectoryName(StableSettingsPath)!);
        platform.SeedFile(StableSettingsPath, "{}"u8.ToArray());
        ScriptVsCodeChannel(platform, "code");
        platform.ScriptProcess(
            "code",
            ["--status"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 1, "not running"));
        ScriptLiveEndpoint(platform);
        if (lifecycle is "applied" or "rolledback")
        {
            ScriptVsCodeChannel(platform, "code");
            ScriptLiveEndpoint(platform);
        }
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(platform);
        var plan = dispatch(PlanOptions("vscode"));
        var planTarget = Assert.Single(plan.Targets);
        Assert.Equal(SetupRestartRequirement.None, planTarget.RestartRequirement);
        var changeSetId = Guid.Parse(Assert.IsType<string>(plan.ChangeSetId));
        if (lifecycle is "applied" or "rolledback")
        {
            Assert.Equal(
                SetupCodes.ApplySucceeded,
                dispatch(new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId)).Code);
        }
        if (lifecycle == "rolledback")
        {
            Assert.Equal(
                SetupCodes.RollbackSucceeded,
                dispatch(new SetupOptions(SetupCommand.Rollback, null, null, null, false, changeSetId)).Code);
        }

        var status = dispatch(StatusOptions());
        var changeSet = Assert.Single(status.ChangeSets);
        var statusTarget = Assert.Single(changeSet.Targets);
        Assert.Equal(SetupCurrentState.Current, changeSet.CurrentState);
        Assert.Equal(SetupCurrentState.Current, statusTarget.CurrentState);
        Assert.Equal(expectedReference, statusTarget.ReferenceState);
        Assert.Equal(SetupRestartRequirement.None, statusTarget.RestartRequirement);

        var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(status, "vscode", ObservedAt);
        Assert.Equal(expectedRestart, snapshot.RestartOrNewProcess?.Requirement);
    }

    [Fact]
    public void Status_RejectsMultipleMatchingChangeSetsWithoutLatestInference()
    {
        var ambiguousPlatform = new SetupTestPlatform(ObservedAt);
        for (var index = 0; index < 2; index++)
        {
            ConfigureNoOpCli(ambiguousPlatform);
        }
        var ambiguousDispatch = SetupCompositionRoot.CreateSetupDispatch(ambiguousPlatform);
        _ = ambiguousDispatch(PlanOptions("cli"));
        _ = ambiguousDispatch(PlanOptions("cli"));
        var ambiguousStatus = ambiguousDispatch(StatusOptions());
        Assert.Equal(2, ambiguousStatus.ChangeSets.Count);
        Assert.Throws<ArgumentException>(() =>
            GitHubCopilotDoctorFactMapper.FromSetup(ambiguousStatus, "cli", ObservedAt));
    }

    [Theory]
    [InlineData(true, SourceFeatureStatus.Available)]
    [InlineData(false, SourceFeatureStatus.Unavailable)]
    public void AppSdkGuidance_PreservesDetectionWithoutInventingRestart(
        bool packagePresent,
        SourceFeatureStatus expectedFeature)
    {
        var platform = new SetupTestPlatform(ObservedAt);
        if (packagePresent)
        {
            ConfigureAppSdk(platform);
        }
        var result = SetupCompositionRoot.CreateSetupDispatch(platform)(PlanOptions("app-sdk"));
        var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(result, "app-sdk", ObservedAt);

        Assert.Equal(expectedFeature, snapshot.InstallAndSourceVersion?.SourceFeature);
        Assert.Equal(SourceVersionStatus.Unknown, snapshot.InstallAndSourceVersion?.SourceVersion);
        Assert.Equal(EndpointAlignmentStatus.Unknown, snapshot.SourceEffectiveConfiguration?.EndpointAlignment);
        Assert.Equal(MonitorProcessStatus.Unknown, snapshot.ProcessReceiverAndPort?.MonitorProcess);
        Assert.Equal(RestartRequirement.Unknown, snapshot.RestartOrNewProcess?.Requirement);
    }

    [Theory]
    [InlineData("endpoint", EndpointAlignmentStatus.Mismatch, ProtocolStatus.HttpProtobuf, TraceSignalStatus.Enabled)]
    [InlineData("exporter_type", EndpointAlignmentStatus.Match, ProtocolStatus.Mismatch, TraceSignalStatus.Enabled)]
    [InlineData("otel_protocol", EndpointAlignmentStatus.Match, ProtocolStatus.Mismatch, TraceSignalStatus.Enabled)]
    [InlineData("signal", EndpointAlignmentStatus.Match, ProtocolStatus.HttpProtobuf, TraceSignalStatus.Disabled)]
    public void Rollback_RestoresEachPriorConfigurationCategoryIndependently(
        string differingCategory,
        EndpointAlignmentStatus expectedEndpoint,
        ProtocolStatus expectedProtocol,
        TraceSignalStatus expectedSignal)
    {
        var platform = new SetupTestPlatform(ObservedAt);
        SeedCliValues(platform, differingCategory);
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptLiveEndpoint(platform);
        ScriptLiveEndpoint(platform);
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(platform);
        var plan = dispatch(PlanOptions("cli"));
        var changeSetId = Guid.Parse(Assert.IsType<string>(plan.ChangeSetId));
        Assert.Equal(
            SetupCodes.ApplySucceeded,
            dispatch(new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId)).Code);
        var rollback = dispatch(new SetupOptions(SetupCommand.Rollback, null, null, null, false, changeSetId));
        Assert.Equal(SetupCodes.RollbackSucceeded, rollback.Code);

        var snapshot = GitHubCopilotDoctorFactMapper.FromSetup(rollback, "cli", ObservedAt);
        Assert.Equal(expectedEndpoint, snapshot.SourceEffectiveConfiguration?.EndpointAlignment);
        Assert.Equal(expectedProtocol, snapshot.ProtocolAndSignalCompatibility?.Protocol);
        Assert.Equal(expectedSignal, snapshot.ProtocolAndSignalCompatibility?.TraceSignal);
    }

    private static SetupCommandResult CreateCliStatus(SetupCurrentState state)
    {
        var platform = new SetupTestPlatform(ObservedAt);
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptLiveEndpoint(platform);
        ScriptLiveEndpoint(platform);
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(platform);
        var plan = dispatch(PlanOptions("cli"));
        var changeSetId = Guid.Parse(Assert.IsType<string>(plan.ChangeSetId));

        Assert.Equal(
            SetupCodes.ApplySucceeded,
            dispatch(new SetupOptions(SetupCommand.Apply, null, null, null, false, changeSetId)).Code);
        if (state == SetupCurrentState.Stale)
        {
            platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", "http://127.0.0.1:4999");
        }

        var status = dispatch(StatusOptions());
        Assert.Equal(state, Assert.Single(status.ChangeSets).CurrentState);
        return status;
    }

    private static SetupOptions StatusOptions() => new(
        SetupCommand.Status,
        "github-copilot",
        null,
        null,
        IncludeContentCapture: false,
        ChangeSetId: null);

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

    private static void ConfigureVsCodeMemberMatrix(
        SetupTestPlatform platform,
        string differingCategory)
    {
        var enabled = differingCategory == "signal" ? "false" : "true";
        var exporter = differingCategory == "protocol" ? "grpc" : "otlp-http";
        var endpoint = differingCategory == "endpoint" ? "http://127.0.0.1:4999" : Endpoint;
        platform.SeedFile(
            StableSettingsPath,
            System.Text.Encoding.UTF8.GetBytes(
                $$"""{"github.copilot.chat.otel.enabled":{{enabled}},"github.copilot.chat.otel.exporterType":"{{exporter}}","github.copilot.chat.otel.otlpEndpoint":"{{endpoint}}"}"""));
        ScriptVersion(platform, "code", ["--version"], "1.128.0");
        platform.ScriptProcess(
            "code",
            ["--list-extensions", "--show-versions"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "GitHub.copilot-chat@0.26.0\n"));
        ScriptLiveEndpoint(platform);
    }

    private static void ScriptVsCodeChannel(SetupTestPlatform platform, string executable)
    {
        ScriptVersion(platform, executable, ["--version"], "1.128.0");
        platform.ScriptProcess(
            executable,
            ["--list-extensions", "--show-versions"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, "GitHub.copilot-chat@0.26.0\n"));
    }

    private static void SeedDirectoryChain(SetupTestPlatform platform, string directory)
    {
        var current = Path.GetPathRoot(directory)!;
        platform.SeedDirectory(current);
        foreach (var segment in directory[current.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            platform.SeedDirectory(current);
        }
    }

    private static void ConfigureCliMemberMatrix(
        SetupTestPlatform platform,
        string differingCategory)
    {
        SeedCliValues(platform, differingCategory);
        ScriptVersion(platform, "copilot", ["version"], "1.0.4");
        ScriptLiveEndpoint(platform);
    }

    private static void SeedCliValues(SetupTestPlatform platform, string differingCategory)
    {
        platform.SeedUserEnvironment("COPILOT_OTEL_ENABLED", differingCategory == "signal" ? "false" : "true");
        platform.SeedUserEnvironment("COPILOT_OTEL_EXPORTER_TYPE", differingCategory == "exporter_type" ? "grpc" : "otlp-http");
        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_ENDPOINT", differingCategory == "endpoint" ? "http://127.0.0.1:4999" : Endpoint);
        platform.SeedUserEnvironment("OTEL_EXPORTER_OTLP_PROTOCOL", differingCategory == "otel_protocol" ? "grpc" : "http/protobuf");
    }

    private static void AssertCliPreviousStates(SetupTargetResult target, string differingCategory)
    {
        Assert.Equal(
            differingCategory == "signal" ? "process_absent_user_present_different" : "process_absent_user_present_desired",
            target.Changes.Single(change => change.SettingKey == "COPILOT_OTEL_ENABLED").PreviousState);
        Assert.Equal(
            differingCategory == "exporter_type" ? "process_absent_user_present_different" : "process_absent_user_present_desired",
            target.Changes.Single(change => change.SettingKey == "COPILOT_OTEL_EXPORTER_TYPE").PreviousState);
        Assert.Equal(
            differingCategory == "endpoint" ? "process_absent_user_present_different" : "process_absent_user_present_desired",
            target.Changes.Single(change => change.SettingKey == "OTEL_EXPORTER_OTLP_ENDPOINT").PreviousState);
        Assert.Equal(
            differingCategory == "otel_protocol" ? "process_absent_user_present_different" : "process_absent_user_present_desired",
            target.Changes.Single(change => change.SettingKey == "OTEL_EXPORTER_OTLP_PROTOCOL").PreviousState);
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

    private static void AssertStatusDetectionUnknown(DoctorFactSnapshot snapshot) =>
        Assert.Equal(
            new InstallAndSourceVersionFacts(
                MonitorInstallStatus.Unknown,
                SourceVersionStatus.Unknown,
                SourceFeatureStatus.Unknown),
            snapshot.InstallAndSourceVersion);

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

using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Platform;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed partial class ClaudeCodeSetupAdapterTests
{
    private const string CanonicalOrigin = "http://127.0.0.1:4320";

    [Theory]
    [InlineData("2.1.207 (Claude Code)", "2.1.207")]
    [InlineData("2.1.207 (Claude Code)\n", "2.1.207")]
    [InlineData("2.1.207 (Claude Code)\r\n", "2.1.207")]
    [InlineData("2.2.0 (Claude Code)", "2.2.0")]
    [InlineData("3.0.0 (Claude Code)", "3.0.0")]
    [InlineData("10.123.456 (Claude Code)", "10.123.456")]
    public void VersionDetector_AcceptsExactMinimumAndNormalFutureVersions(string output, string expectedVersion)
    {
        var platform = Platform();
        ScriptClaudeVersion(platform, SetupProcessOutcome.Completed, 0, output);

        var result = ClaudeCodeVersionDetector.Detect(platform);

        Assert.True(result.IsSupported);
        Assert.True(result.Detected);
        Assert.Equal(expectedVersion, result.Version);
        Assert.Null(result.FailureCode);
        Assert.Equal(["process.run:claude:--version"], platform.Operations);
    }

    [Theory]
    [InlineData("2.1.206 (Claude Code)")]
    [InlineData("2.1.207-beta.1 (Claude Code)")]
    [InlineData("2.1.207+build.1 (Claude Code)")]
    [InlineData("02.1.207 (Claude Code)")]
    [InlineData("2.01.207 (Claude Code)")]
    [InlineData("2.1.0207 (Claude Code)")]
    [InlineData("v2.1.207 (Claude Code)")]
    [InlineData("2.1.207")]
    [InlineData("2.1.207 (Claude code)")]
    [InlineData(" 2.1.207 (Claude Code)")]
    [InlineData("2.1.207 (Claude Code) ")]
    [InlineData("2.1.207 (Claude Code)\nextra")]
    [InlineData("2.1.207 (Claude Code)\n\n")]
    [InlineData("")]
    public void VersionDetector_RejectsOlderPrereleaseMalformedOrAdditionalOutput(string output)
    {
        var platform = Platform();
        ScriptClaudeVersion(platform, SetupProcessOutcome.Completed, 0, output);

        var result = ClaudeCodeVersionDetector.Detect(platform);

        Assert.False(result.IsSupported);
        Assert.True(result.Detected);
        Assert.Null(result.Version);
        Assert.Equal("unsupported_version", result.FailureCode);
        if (output.Length > 0)
        {
            Assert.DoesNotContain(output, result.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(SetupProcessOutcome.Completed, 1)]
    [InlineData(SetupProcessOutcome.Completed, null)]
    [InlineData(SetupProcessOutcome.Failed, 1)]
    [InlineData(SetupProcessOutcome.TimedOut, null)]
    public void VersionDetector_RejectsNonzeroTimeoutAndFailureWithoutLeakingOutput(
        SetupProcessOutcome outcome,
        int? exitCode)
    {
        const string Marker = "sensitive-version-output";
        var platform = Platform();
        ScriptClaudeVersion(platform, outcome, exitCode, Marker);

        var result = ClaudeCodeVersionDetector.Detect(platform);

        Assert.False(result.IsSupported);
        Assert.True(result.Detected);
        Assert.Null(result.Version);
        Assert.Equal("unsupported_version", result.FailureCode);
        Assert.DoesNotContain(Marker, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void VersionDetector_MissingExecutableReturnsTargetNotInstalled()
    {
        var platform = Platform();
        ScriptClaudeVersion(platform, SetupProcessOutcome.NotFound, null, string.Empty);

        var result = ClaudeCodeVersionDetector.Detect(platform);

        Assert.False(result.IsSupported);
        Assert.False(result.Detected);
        Assert.Null(result.Version);
        Assert.Equal("target_not_installed", result.FailureCode);
    }

    [Fact]
    public void VersionDetector_ProcessExceptionReturnsSanitizedUnsupportedVersion()
    {
        var platform = Platform();
        platform.InjectFault("process.run:claude:--version", new InvalidOperationException("sensitive-exception"));

        var result = ClaudeCodeVersionDetector.Detect(platform);

        Assert.False(result.IsSupported);
        Assert.True(result.Detected);
        Assert.Equal("unsupported_version", result.FailureCode);
        Assert.DoesNotContain("sensitive-exception", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExecutionContext_WindowsNativeSucceedsWithoutWslObservation()
    {
        var platform = Platform(planningOs: SetupPlanningOs.Windows);

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: false);

        Assert.Equal(ClaudeCodeExecutionContext.WindowsNative, result.Context);
        Assert.Null(result.FailureCode);
        Assert.Empty(platform.Operations);
    }

    [Fact]
    public void ExecutionContext_WindowsNativeRejectsWslOptionWithoutWslObservation()
    {
        var platform = Platform(planningOs: SetupPlanningOs.Windows);

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: true);

        Assert.Equal(ClaudeCodeExecutionContext.WindowsNative, result.Context);
        Assert.Equal("invalid_arguments", result.FailureCode);
        Assert.Empty(platform.Operations);
    }

    [Theory]
    [InlineData(SetupPlanningOs.MacOs)]
    [InlineData(SetupPlanningOs.Linux)]
    public void ExecutionContext_NativeUnixIsUnsupportedWithoutWslOption(SetupPlanningOs planningOs)
    {
        var platform = Platform(planningOs: planningOs);

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: false);

        Assert.Equal(ClaudeCodeExecutionContext.UnsupportedNativeUnix, result.Context);
        Assert.Equal("unsupported_target", result.FailureCode);
        if (planningOs == SetupPlanningOs.Linux)
        {
            Assert.Equal(["process-environment.get:WSL_DISTRO_NAME"], platform.Operations);
        }
        else
        {
            Assert.Empty(platform.Operations);
        }
    }

    [Theory]
    [InlineData(SetupPlanningOs.MacOs)]
    [InlineData(SetupPlanningOs.Linux)]
    public void ExecutionContext_NativeUnixRejectsWslOption(SetupPlanningOs planningOs)
    {
        var platform = Platform(planningOs: planningOs);

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: true);

        Assert.Equal(ClaudeCodeExecutionContext.UnsupportedNativeUnix, result.Context);
        Assert.Equal("invalid_arguments", result.FailureCode);
    }

    [Fact]
    public void ExecutionContext_ThreeFactorWsl2RequiresExplicitOptIn()
    {
        var platform = WslPlatform("Ubuntu", "6.6.87.2-microsoft-standard-WSL2\n");

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: false);

        Assert.Equal(ClaudeCodeExecutionContext.Wsl2Repository, result.Context);
        Assert.Equal("wsl2_opt_in_required", result.FailureCode);
        Assert.Equal(
            ["process-environment.get:WSL_DISTRO_NAME", "process.run:uname:-r"],
            platform.Operations);
    }

    [Fact]
    public void ExecutionContext_ThreeFactorWsl2WithOptInSucceedsAndLeaksNoObservation()
    {
        const string Distro = "sensitive-distro";
        const string Kernel = "6.6.87.2-Microsoft-standard-WSL2";
        var platform = WslPlatform(Distro, Kernel);

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: true);

        Assert.Equal(ClaudeCodeExecutionContext.Wsl2Repository, result.Context);
        Assert.Null(result.FailureCode);
        Assert.DoesNotContain(Distro, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Kernel, result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "6.6.87.2-microsoft-standard-WSL2")]
    [InlineData("", "6.6.87.2-microsoft-standard-WSL2")]
    [InlineData("   ", "6.6.87.2-microsoft-standard-WSL2")]
    [InlineData("Ubuntu", "6.6.87.2-generic")]
    [InlineData("Ubuntu", "6.6.87.2-microsoft-standard-WSL2\nextra")]
    [InlineData("Ubuntu", "")]
    public void ExecutionContext_IncompleteWslObservationIsUnsupported(string? distro, string kernel)
    {
        var platform = Platform(
            pathStyle: SetupPathStyle.Unix,
            planningOs: SetupPlanningOs.Linux);
        platform.SeedProcessEnvironment("WSL_DISTRO_NAME", distro);
        platform.ScriptProcess(
            "uname",
            ["-r"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, kernel));

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: false);

        Assert.Equal(ClaudeCodeExecutionContext.UnsupportedNativeUnix, result.Context);
        Assert.Equal("unsupported_target", result.FailureCode);
        if (string.IsNullOrWhiteSpace(distro))
        {
            Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("process.run:uname:", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(SetupProcessOutcome.NotFound, null)]
    [InlineData(SetupProcessOutcome.Failed, 1)]
    [InlineData(SetupProcessOutcome.TimedOut, null)]
    [InlineData(SetupProcessOutcome.Completed, 1)]
    public void ExecutionContext_FailedKernelObservationIsUnsupported(
        SetupProcessOutcome outcome,
        int? exitCode)
    {
        var platform = Platform(pathStyle: SetupPathStyle.Unix, planningOs: SetupPlanningOs.Linux);
        platform.SeedProcessEnvironment("WSL_DISTRO_NAME", "Ubuntu");
        platform.ScriptProcess(
            "uname",
            ["-r"],
            new SetupProcessObservation(outcome, exitCode, "sensitive-kernel-microsoft"));

        var result = ClaudeCodeExecutionContextDetector.Detect(platform, allowWsl2Routing: false);

        Assert.Equal(ClaudeCodeExecutionContext.UnsupportedNativeUnix, result.Context);
        Assert.Equal("unsupported_target", result.FailureCode);
        Assert.DoesNotContain("sensitive-kernel", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ready", "[]")]
    [InlineData("degraded", "[\"projection_lag\"]")]
    [InlineData("degraded", "[\"ingestion_backpressure\"]")]
    [InlineData("degraded", "[\"span_projection_backlog\"]")]
    public void ReadinessProbe_AcceptsExactReadyAndDegradedBodies(string status, string reasonsJson)
    {
        var platform = Platform();
        var body = ReadinessBody(
            status: status,
            ingestionAccepting: reasonsJson != "[\"ingestion_backpressure\"]",
            projectionLagSeconds: reasonsJson == "[\"projection_lag\"]" ? 1 : 0,
            projectionBacklog: reasonsJson == "[\"projection_lag\"]" ? 1 : 0,
            spanProjectionLagSeconds: reasonsJson == "[\"span_projection_backlog\"]" ? 1 : 0,
            spanProjectionBacklog: reasonsJson == "[\"span_projection_backlog\"]" ? 1 : 0,
            reasonsJson: reasonsJson);
        platform.ScriptHttpProbe(Response(200, body));

        var result = ClaudeCodeReadinessProbe.Probe(
            platform,
            CanonicalOrigin,
            ClaudeCodeExecutionContext.WindowsNative);

        Assert.True(result.Reachable);
        Assert.Null(result.FailureCode);
        Assert.Equal(ClaudeCodeEndpointState.LoopbackReady, result.EndpointState);
        Assert.Equal(ClaudeCodeProtocolState.HttpProtobuf, result.ProtocolState);
        Assert.Equal(ClaudeCodeSignalState.Traces, result.SignalState);
        Assert.Equal(ClaudeCodeProcessInheritanceState.NewAgentProcessRequired, result.ProcessInheritanceState);
        Assert.Equal(["http.get:http://127.0.0.1:4320:/health/ready:500:4096"], platform.Operations);
    }

    [Fact]
    public void ReadinessProbe_AcceptsJsonWhitespacePropertyOrderAndCombinedDegradedReasons()
    {
        const string Body = """
            {
              "degraded_reasons": ["ingestion_backpressure", "projection_lag", "span_projection_backlog"],
              "checks": {
                "projection_failure_count": 2,
                "span_projection_backlog": 1,
                "span_projection_lag_seconds": 1,
                "projection_backlog": 3,
                "projection_lag_seconds": 1,
                "ingestion_accepting": false,
                "projection_worker_running": true,
                "writer_running": true,
                "migration_complete": true,
                "db_open": true,
                "loopback_bound": true
              },
              "status": "degraded"
            }
            """;
        var platform = Platform();
        platform.ScriptHttpProbe(Response(200, Body));

        var result = ClaudeCodeReadinessProbe.Probe(
            platform,
            CanonicalOrigin,
            ClaudeCodeExecutionContext.WindowsNative);

        Assert.True(result.Reachable);
    }

    [Theory]
    [InlineData((int)ClaudeCodeExecutionContext.WindowsNative, "endpoint_unreachable")]
    [InlineData((int)ClaudeCodeExecutionContext.Wsl2Repository, "wsl2_routing_unavailable")]
    public void ReadinessProbe_MapsEveryHardTransportFailureToContextCode(
        int contextValue,
        string expectedCode)
    {
        var context = (ClaudeCodeExecutionContext)contextValue;
        var observations = new[]
        {
            SetupHttpProbeObservation.Refused,
            SetupHttpProbeObservation.TimedOut,
            SetupHttpProbeObservation.TransportFailure,
            new SetupHttpProbeObservation(SetupHttpProbeOutcome.RedirectBlocked, 302, null, [], true),
            Response(503, ReadinessBody(status: "not_ready", reasonsJson: "[\"fatal_error\"]")),
            Response(201, ReadinessBody()),
            Response(204, string.Empty),
            Response(301, ReadinessBody()),
            Response(400, ReadinessBody()),
            Response(404, ReadinessBody()),
            Response(500, ReadinessBody()),
        };

        foreach (var observation in observations)
        {
            var platform = Platform();
            platform.ScriptHttpProbe(observation);

            var result = ClaudeCodeReadinessProbe.Probe(platform, CanonicalOrigin, context);

            Assert.False(result.Reachable);
            Assert.Equal(expectedCode, result.FailureCode);
            Assert.Null(result.EndpointState);
            Assert.Null(result.ProtocolState);
            Assert.Null(result.SignalState);
            Assert.Null(result.ProcessInheritanceState);
        }
    }

    [Theory]
    [MemberData(nameof(InvalidReadinessBodies))]
    public void ReadinessProbe_RejectsMalformedForeignAndInvariantBreakingBodies(string body)
    {
        const string Marker = "sensitive-marker";
        var platform = Platform();
        platform.ScriptHttpProbe(Response(200, body.Replace("MARKER", Marker, StringComparison.Ordinal)));

        var result = ClaudeCodeReadinessProbe.Probe(
            platform,
            CanonicalOrigin,
            ClaudeCodeExecutionContext.WindowsNative);

        Assert.False(result.Reachable);
        Assert.Equal("endpoint_unreachable", result.FailureCode);
        Assert.DoesNotContain(Marker, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadinessProbe_AcceptsCompleteBodyAt4096Bytes()
    {
        var body = ReadinessBody();
        body += new string(' ', 4096 - Encoding.UTF8.GetByteCount(body));
        var platform = Platform();
        platform.ScriptHttpProbe(Response(200, body));

        var result = ClaudeCodeReadinessProbe.Probe(platform, CanonicalOrigin, ClaudeCodeExecutionContext.WindowsNative);

        Assert.True(result.Reachable);
    }

    [Fact]
    public void ReadinessProbe_AcceptsTrustworthyContentLengthAt4096Bytes()
    {
        var body = ReadinessBody();
        body += new string(' ', 4096 - Encoding.UTF8.GetByteCount(body));
        var platform = Platform();
        platform.ScriptHttpProbe(new SetupHttpProbeObservation(
            SetupHttpProbeOutcome.Response,
            200,
            4096,
            Encoding.UTF8.GetBytes(body),
            true));

        var result = ClaudeCodeReadinessProbe.Probe(platform, CanonicalOrigin, ClaudeCodeExecutionContext.WindowsNative);

        Assert.True(result.Reachable);
    }

    [Fact]
    public void ReadinessProbe_RejectsSentinelAndTrustworthyOversizeLength()
    {
        var observations = new[]
        {
            new SetupHttpProbeObservation(
                SetupHttpProbeOutcome.Response,
                200,
                null,
                Enumerable.Repeat((byte)'x', 4097).ToArray(),
                false),
            new SetupHttpProbeObservation(
                SetupHttpProbeOutcome.Response,
                200,
                4097,
                [],
                true),
            new SetupHttpProbeObservation(
                SetupHttpProbeOutcome.Response,
                200,
                -1,
                [],
                true),
            new SetupHttpProbeObservation(
                SetupHttpProbeOutcome.Response,
                200,
                null,
                Encoding.UTF8.GetBytes(ReadinessBody()),
                false),
        };

        foreach (var observation in observations)
        {
            var platform = Platform();
            platform.ScriptHttpProbe(observation);

            var result = ClaudeCodeReadinessProbe.Probe(
                platform,
                CanonicalOrigin,
                ClaudeCodeExecutionContext.Wsl2Repository);

            Assert.False(result.Reachable);
            Assert.Equal("wsl2_routing_unavailable", result.FailureCode);
        }
    }

    public static TheoryData<string> InvalidReadinessBodies => new()
    {
        "MARKER",
        "[]",
        "{\"status\":\"ready\"}",
        ReadinessBody().Replace("\"degraded_reasons\":[]", "\"degraded_reasons\":[],\"extra\":1", StringComparison.Ordinal),
        ReadinessBody().Replace("\"status\":\"ready\"", "\"status\":\"ready\",\"status\":\"ready\"", StringComparison.Ordinal),
        ReadinessBody().Replace("\"loopback_bound\":true", "\"loopback_bound\":false", StringComparison.Ordinal),
        ReadinessBody().Replace("\"db_open\":true", "\"db_open\":false", StringComparison.Ordinal),
        ReadinessBody().Replace("\"projection_failure_count\":0", "\"projection_failure_count\":-1", StringComparison.Ordinal),
        ReadinessBody().Replace("\"projection_backlog\":0", "\"projection_backlog\":\"0\"", StringComparison.Ordinal),
        ReadinessBody().Replace("\"projection_backlog\":0", "\"projection_backlog\":0,\"unknown\":0", StringComparison.Ordinal),
        ReadinessBody().Replace("\"projection_backlog\":0", "\"projection_backlog\":0,\"projection_backlog\":0", StringComparison.Ordinal),
        ReadinessBody(status: "degraded", reasonsJson: "[]"),
        ReadinessBody(status: "ready", reasonsJson: "[\"projection_lag\"]"),
        ReadinessBody(status: "degraded", projectionLagSeconds: 0, reasonsJson: "[\"projection_lag\"]"),
        ReadinessBody(status: "degraded", projectionLagSeconds: 1, projectionBacklog: 0, reasonsJson: "[\"projection_lag\"]"),
        ReadinessBody(status: "degraded", projectionLagSeconds: 1, reasonsJson: "[]"),
        ReadinessBody(status: "degraded", ingestionAccepting: true, reasonsJson: "[\"ingestion_backpressure\"]"),
        ReadinessBody(status: "degraded", spanProjectionBacklog: 1, reasonsJson: "[]"),
        ReadinessBody(status: "degraded", spanProjectionLagSeconds: 1, spanProjectionBacklog: 0, reasonsJson: "[\"span_projection_backlog\"]"),
        ReadinessBody(status: "degraded", reasonsJson: "[\"unknown_reason\"]"),
        ReadinessBody(status: "degraded", reasonsJson: "[\"projection_lag\",\"projection_lag\"]"),
        ReadinessBody(status: "not_ready", reasonsJson: "[\"fatal_error\"]"),
    };

    private static SetupTestPlatform Platform(
        SetupPathStyle pathStyle = SetupPathStyle.Windows,
        SetupPlanningOs planningOs = SetupPlanningOs.Windows) =>
        new(
            DateTimeOffset.UnixEpoch,
            pathStyle == SetupPathStyle.Windows ? "C:\\setup-test-local-app-data" : "/tmp/setup-test",
            pathStyle,
            planningOs,
            pathStyle == SetupPathStyle.Windows ? "C:\\Users\\setup-test\\AppData\\Roaming" : "/tmp/config",
            pathStyle == SetupPathStyle.Windows ? "C:\\Users\\setup-test" : "/home/setup-test");

    private static SetupTestPlatform WslPlatform(string distro, string kernel)
    {
        var platform = Platform(pathStyle: SetupPathStyle.Unix, planningOs: SetupPlanningOs.Linux);
        platform.SeedProcessEnvironment("WSL_DISTRO_NAME", distro);
        platform.ScriptProcess(
            "uname",
            ["-r"],
            new SetupProcessObservation(SetupProcessOutcome.Completed, 0, kernel));
        return platform;
    }

    private static void ScriptClaudeVersion(
        SetupTestPlatform platform,
        SetupProcessOutcome outcome,
        int? exitCode,
        string output) =>
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new SetupProcessObservation(outcome, exitCode, output));

    private static SetupHttpProbeObservation Response(int statusCode, string body) =>
        new(SetupHttpProbeOutcome.Response, statusCode, null, Encoding.UTF8.GetBytes(body), true);

    private static string ReadinessBody(
        string status = "ready",
        bool ingestionAccepting = true,
        int projectionLagSeconds = 0,
        int projectionBacklog = 0,
        int spanProjectionLagSeconds = 0,
        int spanProjectionBacklog = 0,
        string reasonsJson = "[]") =>
        $$"""{"status":"{{status}}","checks":{"loopback_bound":true,"db_open":true,"migration_complete":true,"writer_running":true,"projection_worker_running":true,"ingestion_accepting":{{ingestionAccepting.ToString().ToLowerInvariant()}},"projection_lag_seconds":{{projectionLagSeconds}},"projection_backlog":{{projectionBacklog}},"span_projection_lag_seconds":{{spanProjectionLagSeconds}},"span_projection_backlog":{{spanProjectionBacklog}},"projection_failure_count":0},"degraded_reasons":{{reasonsJson}}}""";
}

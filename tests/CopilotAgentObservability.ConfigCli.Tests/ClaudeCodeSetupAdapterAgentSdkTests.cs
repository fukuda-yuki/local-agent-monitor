using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed partial class ClaudeCodeSetupAdapterTests
{
    private static readonly DateTimeOffset AgentSdkTimestamp = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public void AgentSdkPlan_ReturnsPinnedPythonThenTypeScriptNoWriteGuidance()
    {
        var platform = new SetupTestPlatform(AgentSdkTimestamp);

        var records = new ClaudeAgentSdkTargetPartition().Plan(platform, CreateAgentSdkRequest(false));

        Assert.Equal(
            ["claude-agent-sdk-python-guidance", "claude-agent-sdk-typescript-guidance"],
            records.Select(record => record.TargetLabel));
        Assert.All(records, record =>
        {
            Assert.Equal(SetupTargetKind.Guidance, record.TargetKind);
            Assert.True(record.StatusProjection.Detected);
            Assert.Null(record.StatusProjection.DetectedVersion);
            Assert.Equal(SetupOperation.NoOp, record.StatusProjection.Operation);
            Assert.Null(record.StatusProjection.EffectiveSource);
            Assert.Null(record.StatusProjection.Endpoint);
            Assert.Null(record.StatusProjection.ExpectedResult);
            Assert.Equal(SetupRestartRequirement.None, record.RestartRequirement);
            Assert.Empty(record.Members);
            Assert.Empty(record.StatusProjection.Changes);
            Assert.NotNull(record.StatusProjection.Guidance);
            Assert.Equal(record.StatusProjection.Guidance!.Kind, record.Guidance!.Kind);
            Assert.Equal(record.StatusProjection.Guidance.Language, record.Guidance.Language);
        });

        var plan = SetupPlanResult.Planned(new SetupChangePlan(
            Guid.Parse("00000000-0000-7000-8000-000000000068"),
            "claude-code",
            "app-sdk",
            AgentSdkTimestamp,
            "1.2.3",
            records));
        Assert.All(plan.Targets, target => Assert.False(target.ProspectiveRollbackAvailable));
        Assert.Empty(platform.Operations);
    }

    [Fact]
    public void AgentSdkPlan_DefaultContentPolicy_HasExactBoundedCallerManagedSamples()
    {
        var records = new ClaudeAgentSdkTargetPartition().Plan(
            new SetupTestPlatform(AgentSdkTimestamp),
            CreateAgentSdkRequest(false));

        var python = Assert.IsType<SetupGuidance>(records[0].Guidance);
        var typescript = Assert.IsType<SetupGuidance>(records[1].Guidance);

        Assert.Equal("caller_managed_sample", python.Kind);
        Assert.Equal("python", python.Language);
        Assert.Equal(PythonSample(includeContentCapture: false), python.Sample);
        Assert.Equal("caller_managed_sample", typescript.Kind);
        Assert.Equal("typescript", typescript.Language);
        Assert.Equal(TypeScriptSample(includeContentCapture: false), typescript.Sample);
        Assert.DoesNotContain("OTEL_LOG_USER_PROMPTS", python.Sample, StringComparison.Ordinal);
        Assert.DoesNotContain("OTEL_LOG_USER_PROMPTS", typescript.Sample, StringComparison.Ordinal);
        Assert.All(records, record => Assert.InRange(record.Guidance!.Sample.Length, 1, 2048));
    }

    [Fact]
    public void AgentSdkPlan_ExplicitContentPolicy_AddsExactlyTheThreeApprovedGates()
    {
        var partition = new ClaudeAgentSdkTargetPartition();
        var platform = new SetupTestPlatform(AgentSdkTimestamp);
        var defaultRecords = partition.Plan(platform, CreateAgentSdkRequest(false));
        var records = partition.Plan(platform, CreateAgentSdkRequest(true));

        var python = Assert.IsType<SetupGuidance>(records[0].Guidance);
        var typescript = Assert.IsType<SetupGuidance>(records[1].Guidance);

        Assert.Equal(
            defaultRecords.Select(record => (
                record.TargetLabel,
                record.Guidance!.Kind,
                record.Guidance.Language,
                record.StatusProjection.Guidance!.Kind,
                record.StatusProjection.Guidance.Language)),
            records.Select(record => (
                record.TargetLabel,
                record.Guidance!.Kind,
                record.Guidance.Language,
                record.StatusProjection.Guidance!.Kind,
                record.StatusProjection.Guidance.Language)));
        Assert.Equal("caller_managed_sample", python.Kind);
        Assert.Equal(PythonSample(includeContentCapture: true), python.Sample);
        Assert.Equal("caller_managed_sample", typescript.Kind);
        Assert.Equal(TypeScriptSample(includeContentCapture: true), typescript.Sample);
        foreach (var setting in new[]
                 {
                     "OTEL_LOG_USER_PROMPTS",
                     "OTEL_LOG_TOOL_DETAILS",
                     "OTEL_LOG_TOOL_CONTENT",
                 })
        {
            Assert.Contains(setting, python.Sample, StringComparison.Ordinal);
            Assert.Contains(setting, typescript.Sample, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AgentSdkPlan_DoesNotObserveOrExposeSensitiveOrUnapprovedConfiguration()
    {
        const string endpointMarker = "issue68-sensitive-endpoint-marker";
        const string toolVersionMarker = "issue68-sensitive-tool-version-marker";
        const string processEnvironmentMarker = "issue68-sensitive-process-environment-marker";
        const string userPathMarker = "issue68-sensitive-user-path-marker";
        var platform = new SetupTestPlatform(
            AgentSdkTimestamp,
            applicationData: $"C:\\Users\\{userPathMarker}\\AppData\\Roaming",
            userProfile: $"C:\\Users\\{userPathMarker}");
        platform.SeedProcessEnvironment("ISSUE68_CALLER_SECRET", processEnvironmentMarker);

        var records = new ClaudeAgentSdkTargetPartition().Plan(
            platform,
            CreateAgentSdkRequest(
                true,
                $"http://127.0.0.1:4320/?marker={endpointMarker}",
                $"1.2.3+{toolVersionMarker}"));
        var plan = SetupPlanResult.Planned(new SetupChangePlan(
            Guid.Parse("00000000-0000-7000-8000-000000000068"),
            "claude-code",
            "app-sdk",
            AgentSdkTimestamp,
            "public-projection-tool-version",
            records));
        var publicResult = new SetupCommandResult(
            SetupCommand.Plan,
            true,
            SetupCodes.PlanReady,
            "00000000-0000-7000-8000-000000000068",
            null,
            null,
            "claude-code",
            plan.Targets.Select(ProjectAgentSdkTarget).ToArray(),
            [],
            [],
            [],
            false);
        var completeRecords = JsonSerializer.Serialize(records);
        var samples = string.Join('\n', records.Select(record => record.Guidance!.Sample));
        var publicSerialization = JsonSerializer.Serialize(publicResult);

        foreach (var marker in new[]
                 {
                     endpointMarker,
                     toolVersionMarker,
                     processEnvironmentMarker,
                     userPathMarker,
                 })
        {
            Assert.DoesNotContain(marker, completeRecords, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, samples, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, publicSerialization, StringComparison.Ordinal);
        }

        foreach (var forbidden in new[]
                 {
                     "OTEL_LOG_RAW_API_BODIES",
                     "OTEL_METRICS_EXPORTER",
                     "OTEL_LOGS_EXPORTER",
                     "OTEL_EXPORTER_OTLP_HEADERS",
                     "OTEL_SERVICE_NAME",
                     "OTEL_RESOURCE_ATTRIBUTES",
                     "AUTHORIZATION",
                     "API_KEY",
                     "CREDENTIAL",
                     "TOKEN",
                     "C:\\\\",
                     "/home/",
        })
        {
            Assert.DoesNotContain(forbidden, completeRecords, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, samples, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(forbidden, publicSerialization, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Empty(platform.Operations);
    }

    private static SetupPlanRequest CreateAgentSdkRequest(
        bool includeContentCapture,
        string endpoint = "http://127.0.0.1:4320",
        string toolVersion = "1.2.3") => new(
        "claude-code",
        "app-sdk",
        endpoint,
        includeContentCapture,
        Guid.Parse("00000000-0000-7000-8000-000000000068"),
        AgentSdkTimestamp,
        toolVersion);

    private static SetupTargetResult ProjectAgentSdkTarget(SetupPlanTarget target) => new(
        target.RecordId.ToString("D"),
        target.TargetKind,
        target.TargetLabel,
        target.Detected,
        target.DetectedVersion,
        target.Operation,
        target.EffectiveSource,
        null,
        null,
        target.RestartRequirement,
        target.ProspectiveRollbackAvailable,
        target.Endpoint,
        target.ExpectedResult?.Clone(),
        target.Guidance,
        target.Changes);

    private static string PythonSample(bool includeContentCapture) => $$"""
        import os
        from claude_agent_sdk import ClaudeAgentOptions

        options = ClaudeAgentOptions(env={
            **os.environ,
            "CLAUDE_CODE_ENABLE_TELEMETRY": "1",
            "CLAUDE_CODE_ENHANCED_TELEMETRY_BETA": "1",
            "OTEL_TRACES_EXPORTER": "otlp",
            "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL": "http/protobuf",
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT": "<canonical-origin>/v1/traces",{{(includeContentCapture ? "\n    \"OTEL_LOG_USER_PROMPTS\": \"1\",\n    \"OTEL_LOG_TOOL_DETAILS\": \"1\",\n    \"OTEL_LOG_TOOL_CONTENT\": \"1\"," : string.Empty)}}
        })

        # Flush telemetry before a short-lived process exits.
        """;

    private static string TypeScriptSample(bool includeContentCapture) => $$"""
        const options = {
          env: {
            ...process.env,
            CLAUDE_CODE_ENABLE_TELEMETRY: "1",
            CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: "1",
            OTEL_TRACES_EXPORTER: "otlp",
            OTEL_EXPORTER_OTLP_TRACES_PROTOCOL: "http/protobuf",
            OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: "<canonical-origin>/v1/traces",{{(includeContentCapture ? "\n    OTEL_LOG_USER_PROMPTS: \"1\",\n    OTEL_LOG_TOOL_DETAILS: \"1\",\n    OTEL_LOG_TOOL_CONTENT: \"1\"," : string.Empty)}}
          },
        };

        // Pass options.env to the Agent SDK and flush telemetry before a short-lived process exits.
        """;
}

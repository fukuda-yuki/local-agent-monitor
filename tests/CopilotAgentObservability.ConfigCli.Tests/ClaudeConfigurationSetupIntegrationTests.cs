using System.Reflection;
using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;

namespace CopilotAgentObservability.ConfigCli.Tests;

[Collection(nameof(SetupPhysicalProcessCollection))]
public sealed class ClaudeConfigurationSetupIntegrationTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-07-16T00:00:00Z");

    [Fact]
    public void Plan_ClaudeCli_ProductionCompositionProjectsSetupV1()
    {
        var platform = new SetupTestPlatform(Timestamp);
        var parsed = SetupOptions.Parse(
        [
            "setup",
            "plan",
            "--adapter",
            "claude-code",
            "--target",
            "cli",
            "--endpoint",
            "http://127.0.0.1:4320",
        ]);
        var options = Assert.IsType<SetupOptions>(parsed.Options);

        var result = SetupCompositionRoot.CreateSetupDispatch(platform)(options);
        using var document = JsonDocument.Parse(SetupJson.Serialize(result));
        var root = document.RootElement;

        Assert.Equal("setup.v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("plan", root.GetProperty("command").GetString());
        Assert.Equal("claude-code", root.GetProperty("adapter").GetString());
        Assert.Equal(SetupCodes.TargetNotInstalled, root.GetProperty("code").GetString());
    }

    [Fact]
    public void Parse_ClaudeWslOptIn_AcceptsPublicFlag()
    {
        var parsed = SetupOptions.Parse(ClaudePlanArguments);

        Assert.Null(parsed.Code);
        var options = Assert.IsType<SetupOptions>(parsed.Options);
        Assert.True(options.AllowWsl2Routing);
    }

    [Fact]
    public void Parse_NonClaudeWslOptIn_IsInvalidArguments()
    {
        var parsed = SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", "github-copilot", "--target", "cli",
            "--allow-wsl2-routing",
        ]);

        Assert.Null(parsed.Options);
        Assert.Equal(SetupCodes.InvalidArguments, parsed.Code);
    }

    [Fact]
    public void Composition_UnavailableClaudeHookLayout_DoesNotBreakGitHubPlanOrStatus()
    {
        var platform = new SetupTestPlatform(Timestamp);
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(null));
        var github = Assert.IsType<SetupOptions>(SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", "github-copilot", "--target", "cli",
            "--endpoint", "http://127.0.0.1:4320",
        ]).Options);
        var status = Assert.IsType<SetupOptions>(SetupOptions.Parse(["setup", "status"]).Options);

        Assert.Equal(SetupCodes.TargetNotInstalled, dispatch(github).Code);
        Assert.True(dispatch(status).Success);
    }

    [Fact]
    public void Composition_UnavailableClaudeHookLayout_ClaudePlanFailsClosedWithoutArtifacts()
    {
        var platform = new SetupTestPlatform(Timestamp);
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new(CopilotAgentObservability.ConfigCli.Setup.Platform.SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
        platform.ScriptHttpProbe(new(
            CopilotAgentObservability.ConfigCli.Setup.Platform.SetupHttpProbeOutcome.Response,
            200,
            null,
            Encoding.UTF8.GetBytes("{\"status\":\"ready\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[]}"),
            true));
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(null));
        var options = Assert.IsType<SetupOptions>(SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", "claude-code", "--target", "cli",
            "--endpoint", "http://127.0.0.1:4320",
        ]).Options);

        var result = dispatch(options);

        Assert.Equal(SetupCodes.InternalError, result.Code);
        Assert.DoesNotContain(platform.Operations, operation =>
            operation.StartsWith("file.write", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("app-sdk")]
    [InlineData("all")]
    public void Composition_ExplicitContentAgentSdk_PreservesExactVariantAcrossPlanApplyAndStatus(string target)
    {
        var platform = new SetupTestPlatform(Timestamp);
        if (target == "all")
        {
            SeedDirectoryChain(platform, "C:\\Users\\setup-test\\.claude");
            platform.SeedFile(
                "C:\\Users\\setup-test\\.claude\\settings.json",
                "{}\n"u8.ToArray());
            ScriptReadyClaude(platform);
            ScriptReadyClaude(platform);
        }

        var dispatch = SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand("dotnet", ["run", "--no-build", "--project", "synthetic-monitor.csproj", "--"], ClaudeHookCommandMode.Repository)));
        var planOptions = Assert.IsType<SetupOptions>(SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", "claude-code", "--target", target,
            "--endpoint", "http://127.0.0.1:4320", "--include-content-capture",
        ]).Options);

        var plan = dispatch(planOptions);

        Assert.True(plan.Success);
        Assert.Equal(target == "app-sdk" ? SetupCodes.NoChanges : SetupCodes.PlanReady, plan.Code);
        AssertContentGuidancePair(plan.Targets);
        if (target == "app-sdk")
        {
            Assert.DoesNotContain(platform.Operations, operation =>
                operation.Contains(".claude\\settings.json", StringComparison.Ordinal));
        }

        var applyOptions = Assert.IsType<SetupOptions>(SetupOptions.Parse(
            ["setup", "apply", "--change-set", plan.ChangeSetId!]).Options);
        var apply = dispatch(applyOptions);

        Assert.True(apply.Success, apply.Code);
        Assert.Equal(target == "app-sdk" ? SetupCodes.NoChanges : SetupCodes.ApplySucceeded, apply.Code);
        AssertContentGuidancePair(apply.Targets);

        var statusOptions = Assert.IsType<SetupOptions>(SetupOptions.Parse(
            ["setup", "status", "--adapter", "claude-code"]).Options);
        var status = dispatch(statusOptions);

        Assert.True(status.Success);
        var changeSet = Assert.Single(status.ChangeSets);
        AssertContentGuidancePair(changeSet.Targets);
        using var statusJson = JsonDocument.Parse(SetupJson.Serialize(status));
        var statusGuidance = statusJson.RootElement.GetProperty("change_sets")[0]
            .GetProperty("targets")
            .EnumerateArray()
            .Where(element => element.GetProperty("target_kind").GetString() == "guidance")
            .Select(element => element.GetProperty("guidance"))
            .ToArray();
        Assert.Equal(2, statusGuidance.Length);
        Assert.All(statusGuidance, guidance =>
            Assert.Equal(["kind", "language"], guidance.EnumerateObject().Select(property => property.Name)));
    }

    [Fact]
    public void Composition_ChangedClaudeCliApplyEmitsHandoffButRollbackDoesNot()
    {
        var platform = new SetupTestPlatform(Timestamp);
        SeedDirectoryChain(platform, "C:\\Users\\setup-test\\.claude");
        platform.SeedFile("C:\\Users\\setup-test\\.claude\\settings.json", "{}\n"u8.ToArray());
        ScriptReadyClaude(platform);
        ScriptReadyClaude(platform);

        var dispatch = SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand("dotnet", ["run", "--no-build", "--project", "synthetic-monitor.csproj", "--"], ClaudeHookCommandMode.Repository)));
        var planOptions = Assert.IsType<SetupOptions>(SetupOptions.Parse(
        [
            "setup", "plan", "--adapter", "claude-code", "--target", "cli",
            "--endpoint", "http://127.0.0.1:4320",
        ]).Options);

        var plan = dispatch(planOptions);
        var apply = dispatch(Assert.IsType<SetupOptions>(SetupOptions.Parse(
            ["setup", "apply", "--change-set", plan.ChangeSetId!]).Options));

        Assert.True(apply.Success, apply.Code);
        Assert.Equal(
            [SetupCodes.RestartClaudeProcess, SetupCodes.RunFirstTraceDoctor],
            apply.NextActions);

        var rollback = dispatch(Assert.IsType<SetupOptions>(SetupOptions.Parse(
            ["setup", "rollback", "--change-set", plan.ChangeSetId!]).Options));

        Assert.True(rollback.Success, rollback.Code);
        Assert.DoesNotContain(SetupCodes.RunFirstTraceDoctor, rollback.NextActions);
    }

    [Fact]
    public async Task RepositorySetupWrapper_ForwardsClaudeWslOptInByteForByte()
    {
        var direct = await RunConfigCliAsync(DuplicateWslOptInActionArguments);
        var wrapper = await RunWrapperAsync(DuplicateWslOptInActionArguments);

        Assert.Equal(2, direct.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes("invalid_arguments\n"), direct.StandardError);
        Assert.Equal(direct.StandardOutput, wrapper.StandardOutput);
        Assert.Equal(direct.StandardError, wrapper.StandardError);
        Assert.Equal(direct.ExitCode, wrapper.ExitCode);
        using var document = JsonDocument.Parse(wrapper.StandardOutput);
        var root = document.RootElement;
        Assert.Equal("setup.v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("invalid_arguments", root.GetProperty("code").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("adapter").ValueKind);
    }

    private static string[] ClaudePlanArguments => ["setup", .. ClaudeActionArguments];

    private static string[] ClaudeActionArguments =>
    [
        "plan",
        "--adapter",
        "claude-code",
        "--target",
        "cli",
        "--endpoint",
        "http://127.0.0.1:4320",
        "--allow-wsl2-routing",
    ];

    private static string[] DuplicateWslOptInActionArguments =>
    [
        .. ClaudeActionArguments,
        "--allow-wsl2-routing",
    ];

    private static void AssertContentGuidancePair(IReadOnlyList<SetupTargetResult> targets)
    {
        var guidance = targets.Where(target => target.TargetKind == SetupTargetKind.Guidance).ToArray();
        Assert.Equal(
            ["claude-agent-sdk-python-guidance", "claude-agent-sdk-typescript-guidance"],
            guidance.Select(target => target.TargetLabel));
        Assert.Equal("caller_managed_sample", guidance[0].Guidance?.Kind);
        Assert.Equal("python", guidance[0].Guidance?.Language);
        Assert.Equal(PythonContentSample, guidance[0].Guidance?.Sample);
        Assert.Equal("caller_managed_sample", guidance[1].Guidance?.Kind);
        Assert.Equal("typescript", guidance[1].Guidance?.Language);
        Assert.Equal(TypeScriptContentSample, guidance[1].Guidance?.Sample);
    }

    private static void ScriptReadyClaude(SetupTestPlatform platform)
    {
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new(CopilotAgentObservability.ConfigCli.Setup.Platform.SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
        platform.ScriptHttpProbe(new(
            CopilotAgentObservability.ConfigCli.Setup.Platform.SetupHttpProbeOutcome.Response,
            200,
            null,
            Encoding.UTF8.GetBytes("{\"status\":\"ready\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[]}"),
            true));
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

    private const string PythonContentSample = """
        import os
        from claude_agent_sdk import ClaudeAgentOptions

        options = ClaudeAgentOptions(env={
            **os.environ,
            "CLAUDE_CODE_ENABLE_TELEMETRY": "1",
            "CLAUDE_CODE_ENHANCED_TELEMETRY_BETA": "1",
            "OTEL_TRACES_EXPORTER": "otlp",
            "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL": "http/protobuf",
            "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT": "<canonical-origin>/v1/traces",
            "OTEL_LOG_USER_PROMPTS": "1",
            "OTEL_LOG_TOOL_DETAILS": "1",
            "OTEL_LOG_TOOL_CONTENT": "1",
        })

        # Flush telemetry before a short-lived process exits.
        """;

    private const string TypeScriptContentSample = """
        const options = {
          env: {
            ...process.env,
            CLAUDE_CODE_ENABLE_TELEMETRY: "1",
            CLAUDE_CODE_ENHANCED_TELEMETRY_BETA: "1",
            OTEL_TRACES_EXPORTER: "otlp",
            OTEL_EXPORTER_OTLP_TRACES_PROTOCOL: "http/protobuf",
            OTEL_EXPORTER_OTLP_TRACES_ENDPOINT: "<canonical-origin>/v1/traces",
            OTEL_LOG_USER_PROMPTS: "1",
            OTEL_LOG_TOOL_DETAILS: "1",
            OTEL_LOG_TOOL_CONTENT: "1",
          },
        };

        // Pass options.env to the Agent SDK and flush telemetry before a short-lived process exits.
        """;

    private static Task<SetupPhysicalProcessResult> RunConfigCliAsync(IEnumerable<string> actionArguments)
    {
        var arguments = new List<string>
        {
            "run",
            "--verbosity",
            "quiet",
            "--project",
            ConfigCliProjectPath,
            "--",
            "setup",
        };
        arguments.AddRange(actionArguments);
        return SetupPhysicalProcessRunner.RunAsync("dotnet", RepositoryRoot, arguments);
    }

    private static Task<SetupPhysicalProcessResult> RunWrapperAsync(IEnumerable<string> actionArguments)
    {
        var arguments = new List<string>
        {
            "-NoProfile",
            "-File",
            SetupScriptPath,
        };
        arguments.AddRange(actionArguments);
        return SetupPhysicalProcessRunner.RunAsync("pwsh", RepositoryRoot, arguments);
    }

    private static string RepositoryRoot => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", ".."));

    private static string ConfigCliProjectPath => Path.Combine(
        RepositoryRoot,
        "src",
        "CopilotAgentObservability.ConfigCli",
        "CopilotAgentObservability.ConfigCli.csproj");

    private static string SetupScriptPath => Path.Combine(
        RepositoryRoot,
        "scripts",
        "local-monitor",
        "setup.ps1");
}

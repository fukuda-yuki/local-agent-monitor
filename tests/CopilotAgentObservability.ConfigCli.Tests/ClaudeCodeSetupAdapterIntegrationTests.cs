using System.Text;
using CopilotAgentObservability.ConfigCli.Setup.Adapters;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Storage;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed partial class ClaudeCodeSetupAdapterTests
{
    private static readonly DateTimeOffset AdapterTimestamp = DateTimeOffset.Parse("2026-07-16T00:00:00Z");
    private const string ClaudeSettingsPath = "C:\\Users\\setup-test\\.claude\\settings.json";

    [Fact]
    public void AdapterPlan_Cli_ProducesOwnedSettingsAndClosedPublicProjection()
    {
        var platform = ReadyWindowsPlatform("{}\n");
        var adapter = CreateAdapter(platform);

        var result = adapter.Plan(Request("cli", includeContentCapture: false));

        var success = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(result);
        var record = Assert.Single(success.Value.Records);
        Assert.Equal("claude-code-user-settings", record.TargetLabel);
        Assert.Equal(SetupTargetKind.Json, record.TargetKind);
        Assert.Equal(SetupRestartRequirement.RestartAgentProcess, record.RestartRequirement);
        Assert.Equal(CanonicalOrigin, record.StatusProjection.Endpoint);
        Assert.Equal("claude-code", record.StatusProjection.ExpectedResult?.GetProperty("source_surface").GetString());
        Assert.Equal(16, record.Members.Count);
        Assert.Equal(
            [
                "env.CLAUDE_CODE_ENABLE_TELEMETRY",
                "env.CLAUDE_CODE_ENHANCED_TELEMETRY_BETA",
                "env.OTEL_TRACES_EXPORTER",
                "env.OTEL_EXPORTER_OTLP_TRACES_PROTOCOL",
                "env.OTEL_EXPORTER_OTLP_TRACES_ENDPOINT",
            ],
            record.Members.Take(5).Select(member => member.SettingKey));
        var desired = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(record.DesiredState);
        Assert.Equal(5, desired.OwnedEnv.Count);
        Assert.Equal(
            [
                "SessionStart", "UserPromptSubmit", "PreToolUse", "PermissionRequest",
                "PostToolUse", "PostToolUseFailure", "SubagentStart", "SubagentStop",
                "Stop", "StopFailure", "SessionEnd",
            ],
            desired.OwnedHooks.Select(hook => hook.EventName));
        Assert.All(desired.OwnedHooks, hook =>
        {
            Assert.Equal(5, hook.TimeoutSeconds);
            Assert.Contains("hook-forward", hook.Arguments);
            Assert.Contains("claude-code", hook.Arguments);
            Assert.Contains("250", hook.Arguments);
        });
        Assert.Equal(["claude_hooks_capture_raw_content"], success.Warnings);
        Assert.Equal([SetupCodes.RestartClaudeProcess], success.NextActions);
        Assert.DoesNotContain(SetupCodes.RunFirstTraceDoctor, success.NextActions);
    }

    [Fact]
    public void AdapterRevalidate_ChangedCliApply_OrdersRestartBeforeFirstTraceHandoff()
    {
        var platform = ReadyWindowsPlatform("{}\n");
        var adapter = CreateAdapter(platform);
        var planned = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([adapter]).Plan(Request("cli", includeContentCapture: false))).Value;
        ScriptVersionAndReadiness(platform);

        var result = adapter.Revalidate(planned.PrivatePlan, planned.PlannedChangeSet);

        var success = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(result);
        Assert.Equal(
            [SetupCodes.RestartClaudeProcess, SetupCodes.RunFirstTraceDoctor],
            success.NextActions);
    }

    [Fact]
    public void AdapterPlan_CliExplicitContentCapture_ManagesExactlyThreeAdditionalGates()
    {
        var platform = ReadyWindowsPlatform("{}\n");

        var result = CreateAdapter(platform).Plan(Request("cli", includeContentCapture: true));

        var success = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(result);
        var record = Assert.Single(success.Value.Records);
        var desired = Assert.IsType<SetupClaudeSettingsOwnedValuesDesiredState>(record.DesiredState);
        Assert.Equal(19, record.Members.Count);
        Assert.Equal(
            ["OTEL_LOG_USER_PROMPTS", "OTEL_LOG_TOOL_DETAILS", "OTEL_LOG_TOOL_CONTENT"],
            desired.OwnedEnv.Skip(5).Select(value => value.Key));
        Assert.All(desired.OwnedEnv.Skip(5), value => Assert.Equal("1", value.Value));
        Assert.Equal(
            ["claude_hooks_capture_raw_content", SetupCodes.ContentCaptureSensitive],
            success.Warnings);
        Assert.Equal(
            [SetupCodes.RestartClaudeProcess, SetupCodes.ReviewContentCaptureWarning],
            success.NextActions);
    }

    [Fact]
    public void AdapterPlan_ExistingEqualAndDifferentEnvWithAbsentHooks_UsesPerMemberMixedOperations()
    {
        var platform = ReadyWindowsPlatform(
            "{\"env\":{\"CLAUDE_CODE_ENABLE_TELEMETRY\":\"1\",\"OTEL_TRACES_EXPORTER\":\"console\"}}\n");

        var result = CreateAdapter(platform).Plan(Request("cli", includeContentCapture: false));

        var success = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(result);
        var record = Assert.Single(success.Value.Records);
        Assert.Equal(SetupOperation.Mixed, record.StatusProjection.Operation);
        Assert.Equal(SetupOperation.NoOp, record.Members.Single(member => member.SettingKey == "env.CLAUDE_CODE_ENABLE_TELEMETRY").Operation);
        Assert.Equal(SetupOperation.Replace, record.Members.Single(member => member.SettingKey == "env.OTEL_TRACES_EXPORTER").Operation);
        Assert.All(record.Members.Where(member => member.SettingKey.StartsWith("hooks.", StringComparison.Ordinal)),
            member => Assert.Equal(SetupOperation.Add, member.Operation));
    }

    [Fact]
    public void AdapterPlan_All_OrdersCliThenPythonThenTypeScript()
    {
        var platform = ReadyWindowsPlatform("{}\n");

        var result = CreateAdapter(platform).Plan(Request("all", includeContentCapture: false));

        var success = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(result);
        Assert.Equal(
            [
                "claude-code-user-settings",
                "claude-agent-sdk-python-guidance",
                "claude-agent-sdk-typescript-guidance",
            ],
            success.Value.Records.Select(record => record.TargetLabel));
    }

    [Fact]
    public void GuidanceValidation_RejectsClaudeLanguageOnArbitraryLabelAndReorderedLedgerPair()
    {
        var guidanceRecords = new ClaudeAgentSdkTargetPartition().Plan(
            new SetupTestPlatform(AdapterTimestamp),
            Request("app-sdk", includeContentCapture: false));
        var python = SetupPlanTarget.FromRecord(guidanceRecords[0]);
        var invalidPublic = new SetupCommandResult(
            SetupCommand.Plan,
            true,
            SetupCodes.NoChanges,
            "00000000-0000-7000-8000-000000000068",
            null,
            null,
            "claude-code",
            [ProjectAgentSdkTarget(python) with { TargetLabel = "arbitrary-guidance" }],
            [],
            [],
            [],
            false);

        Assert.Throws<InvalidOperationException>(() => SetupContractValidator.Validate(invalidPublic));

        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([CreateAdapter(new SetupTestPlatform(AdapterTimestamp))])
                .Plan(Request("app-sdk", includeContentCapture: false))).Value;
        var reordered = bound.PlannedChangeSet with
        {
            Targets = bound.PlannedChangeSet.Targets.Reverse().ToArray(),
        };
        Assert.Throws<SetupStorageException>(() =>
            SetupStorageValidation.ValidatePlanAndLedger(bound.PrivatePlan, reordered));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("mixed-pair")]
    public void AdapterRevalidate_TamperedAgentSdkVariantFailsBeforeTargetActivity(string variant)
    {
        var platform = new SetupTestPlatform(AdapterTimestamp);
        var adapter = CreateAdapter(platform);
        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([adapter])
                .Plan(Request("app-sdk", includeContentCapture: true))).Value;
        var targets = bound.PrivatePlan.Targets.ToArray();
        targets[0] = targets[0] with
        {
            DesiredState = new SetupInlineDesiredState(variant == "unknown"
                ? "unknown-claude-agent-sdk-variant"
                : "claude-agent-sdk-python-guidance"),
        };
        var tampered = bound.PrivatePlan with { Targets = targets };

        Assert.Throws<InvalidOperationException>(() =>
            adapter.Revalidate(tampered, bound.PlannedChangeSet));
        Assert.Empty(platform.Operations);
    }

    [Fact]
    public void AdapterPlan_UnsupportedTarget_ReturnsClosedFailure()
    {
        var platform = new SetupTestPlatform(AdapterTimestamp);

        var result = CreateAdapter(platform).Plan(Request("vscode", includeContentCapture: false));

        var failure = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(result);
        Assert.Equal(SetupCodes.UnsupportedTarget, failure.Code);
        Assert.Empty(platform.Operations);
    }

    [Fact]
    public void AdapterPlan_ExplicitContentCaptureConflictingProcessValueStopsBeforeSettingsRead()
    {
        var platform = new SetupTestPlatform(AdapterTimestamp);
        ScriptVersionAndReadiness(platform);
        platform.SeedProcessEnvironment("OTEL_LOG_USER_PROMPTS", "0");

        var result = CreateAdapter(platform).Plan(Request("cli", includeContentCapture: true));

        var failure = Assert.IsType<SetupPlanFailure<SetupChangePlan>>(result);
        Assert.Equal(SetupCodes.ContentPolicyConflict, failure.Code);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith("file.read", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterRevalidate_MaterializesCurrentSettingsWithoutPersistingRenderedDocument()
    {
        var platform = ReadyWindowsPlatform("{}\n");
        var adapter = CreateAdapter(platform);
        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([adapter]).Plan(Request("cli", includeContentCapture: false))).Value;
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new(CopilotAgentObservability.ConfigCli.Setup.Platform.SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
        platform.ScriptHttpProbe(ReadyResponse());

        var result = adapter.Revalidate(bound.PrivatePlan, bound.PlannedChangeSet);

        var success = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(result);
        var materialized = Assert.Single(success.Value.MaterializedTargets);
        Assert.Equal(bound.PrivatePlan.Targets[0].RecordId, materialized.RecordId);
        Assert.Contains("CLAUDE_CODE_ENABLE_TELEMETRY", Encoding.UTF8.GetString(materialized.DesiredBytes.Span), StringComparison.Ordinal);
        Assert.DoesNotContain(Encoding.UTF8.GetString(materialized.DesiredBytes.Span),
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Setup", "v1", "private-plan.v1.json")),
            StringComparison.Ordinal);
    }


    [Fact]
    public void AdapterRevalidate_MissingSettingsAddPlanMaterializesWithoutSecondRead()
    {
        var platform = new SetupTestPlatform(AdapterTimestamp);
        ScriptVersionAndReadiness(platform);
        var adapter = CreateAdapter(platform);
        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([adapter]).Plan(Request("cli", includeContentCapture: false))).Value;
        ScriptVersionAndReadiness(platform);

        var result = adapter.Revalidate(bound.PrivatePlan, bound.PlannedChangeSet);

        var success = Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(result);
        Assert.Single(success.Value.MaterializedTargets);
        Assert.DoesNotContain(platform.Operations, operation => operation.StartsWith($"file.read-bounded:{ClaudeSettingsPath}", StringComparison.Ordinal));
    }

    [Fact]
    public void AdapterRevalidate_UnrelatedEditReturnsStalePlan()
    {
        var platform = ReadyWindowsPlatform("{\"unrelated\":1}\n");
        var adapter = CreateAdapter(platform);
        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([adapter]).Plan(Request("cli", includeContentCapture: false))).Value;
        platform.SeedFile(ClaudeSettingsPath, "{\"unrelated\":2}\n"u8.ToArray());
        ScriptVersionAndReadiness(platform);

        var result = adapter.Revalidate(bound.PrivatePlan, bound.PlannedChangeSet);

        Assert.Equal(SetupCodes.StalePlan, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(result).Code);
    }

    [Fact]
    public void AdapterRevalidate_EqualProcessValueDoesNotRewriteDifferentUserValue()
    {
        const string UserValue = "console";
        var platform = ReadyWindowsPlatform($"{{\"env\":{{\"OTEL_TRACES_EXPORTER\":\"{UserValue}\"}}}}\n");
        platform.SeedProcessEnvironment("OTEL_TRACES_EXPORTER", "otlp");
        var adapter = CreateAdapter(platform);
        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([adapter]).Plan(Request("cli", includeContentCapture: false))).Value;
        Assert.Equal(
            SetupOperation.NoOp,
            bound.PrivatePlan.Targets[0].Members.Single(member => member.SettingKey == "env.OTEL_TRACES_EXPORTER").Operation);
        ScriptVersionAndReadiness(platform);

        var result = adapter.Revalidate(bound.PrivatePlan, bound.PlannedChangeSet);

        var materialized = Assert.Single(Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(result).Value.MaterializedTargets);
        var rendered = Encoding.UTF8.GetString(materialized.DesiredBytes.Span);
        Assert.Contains($"\"OTEL_TRACES_EXPORTER\":\"{UserValue}\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"OTEL_TRACES_EXPORTER\":\"otlp\"", rendered, StringComparison.Ordinal);
        Assert.Equal(
            SetupEffectiveSource.Environment,
            bound.PlannedChangeSet.Targets[0].StatusProjection.EffectiveSource);
    }

    [Fact]
    public void AdapterPlan_AlreadyDesiredSettingsIsNoOpWithoutRestartAction()
    {
        var initial = new SetupTestPlatform(AdapterTimestamp);
        ScriptVersionAndReadiness(initial);
        var adapter = CreateAdapter(initial);
        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([adapter]).Plan(Request("cli", includeContentCapture: false))).Value;
        ScriptVersionAndReadiness(initial);
        var desiredBytes = Assert.Single(
            Assert.IsType<SetupPlanSuccess<SetupRevalidation>>(
                adapter.Revalidate(bound.PrivatePlan, bound.PlannedChangeSet)).Value.MaterializedTargets).DesiredBytes.ToArray();
        var noOp = new SetupTestPlatform(AdapterTimestamp);
        noOp.SeedFile(ClaudeSettingsPath, desiredBytes);
        ScriptVersionAndReadiness(noOp);

        var result = CreateAdapter(noOp).Plan(Request("cli", includeContentCapture: false));

        var success = Assert.IsType<SetupPlanSuccess<SetupChangePlan>>(result);
        var record = Assert.Single(success.Value.Records);
        Assert.Equal(SetupOperation.NoOp, record.StatusProjection.Operation);
        Assert.Equal(SetupRestartRequirement.None, record.RestartRequirement);
        Assert.DoesNotContain(SetupCodes.RestartClaudeProcess, success.NextActions);
    }

    [Fact]
    public void AdapterRevalidate_WindowsPlanInWslContextReturnsUnsupportedTargetBeforeMaterialization()
    {
        var windows = ReadyWindowsPlatform("{}\n");
        var bound = Assert.IsType<SetupPlanSuccess<SetupPlannedChangeSet>>(
            new SetupAdapterRegistry([CreateAdapter(windows)]).Plan(Request("cli", includeContentCapture: false))).Value;
        var wsl = new SetupTestPlatform(
            AdapterTimestamp,
            "/tmp/setup-test",
            CopilotAgentObservability.ConfigCli.Setup.Platform.SetupPathStyle.Unix,
            CopilotAgentObservability.ConfigCli.Setup.Platform.SetupPlanningOs.Linux,
            "/home/setup-test/.config",
            "/home/setup-test");
        wsl.SeedProcessEnvironment("WSL_DISTRO_NAME", "Ubuntu");
        wsl.ScriptProcess("uname", ["-r"], new(CopilotAgentObservability.ConfigCli.Setup.Platform.SetupProcessOutcome.Completed, 0, "6.6.0-microsoft-standard-WSL2"));
        ScriptVersionAndReadiness(wsl);
        var wslAdapter = CreateWslAdapter(wsl);

        var result = wslAdapter.Revalidate(bound.PrivatePlan, bound.PlannedChangeSet);

        Assert.Equal(SetupCodes.UnsupportedTarget, Assert.IsType<SetupPlanFailure<SetupRevalidation>>(result).Code);
        Assert.DoesNotContain(wsl.Operations, operation => operation.StartsWith("file.read-bounded:", StringComparison.Ordinal));
    }

    private static ClaudeCodeSetupAdapter CreateAdapter(SetupTestPlatform platform) =>
        new(
            platform,
            new ClaudeAgentSdkTargetPartition(),
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand("dotnet", ["run", "--no-build", "--project", "synthetic-monitor.csproj", "--"], ClaudeHookCommandMode.Repository)),
            new ClaudeHigherPrecedenceObserver(
                platform,
                "C:\\synthetic-repository",
                "C:\\Program Files\\ClaudeCode\\managed-settings.json"));

    private static ClaudeCodeSetupAdapter CreateWslAdapter(SetupTestPlatform platform) =>
        new(
            platform,
            new ClaudeAgentSdkTargetPartition(),
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand("dotnet", ["run", "--no-build", "--project", "/repo/monitor.csproj", "--"], ClaudeHookCommandMode.Repository)),
            new ClaudeHigherPrecedenceObserver(platform, "/repo", "/etc/claude-code/managed-settings.json"));

    private static SetupPlanRequest Request(string target, bool includeContentCapture) => new(
        "claude-code",
        target,
        CanonicalOrigin,
        includeContentCapture,
        Guid.Parse("00000000-0000-7000-8000-000000000068"),
        AdapterTimestamp,
        "1.2.3",
        AllowWsl2Routing: false);

    private static SetupTestPlatform ReadyWindowsPlatform(string settings)
    {
        var platform = new SetupTestPlatform(AdapterTimestamp);
        platform.SeedFile(ClaudeSettingsPath, Encoding.UTF8.GetBytes(settings));
        ScriptVersionAndReadiness(platform);
        return platform;
    }

    private static void ScriptVersionAndReadiness(SetupTestPlatform platform)
    {
        platform.ScriptProcess(
            "claude",
            ["--version"],
            new(CopilotAgentObservability.ConfigCli.Setup.Platform.SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)"));
        platform.ScriptHttpProbe(ReadyResponse());
    }

    private static CopilotAgentObservability.ConfigCli.Setup.Platform.SetupHttpProbeObservation ReadyResponse() => new(
        CopilotAgentObservability.ConfigCli.Setup.Platform.SetupHttpProbeOutcome.Response,
        200,
        null,
        Encoding.UTF8.GetBytes("{\"status\":\"ready\",\"checks\":{\"loopback_bound\":true,\"db_open\":true,\"migration_complete\":true,\"writer_running\":true,\"projection_worker_running\":true,\"ingestion_accepting\":true,\"projection_lag_seconds\":0,\"projection_backlog\":0,\"span_projection_lag_seconds\":0,\"span_projection_backlog\":0,\"projection_failure_count\":0},\"degraded_reasons\":[]}"),
        true);
}

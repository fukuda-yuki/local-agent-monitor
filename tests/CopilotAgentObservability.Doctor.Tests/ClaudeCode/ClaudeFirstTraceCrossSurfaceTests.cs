using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CopilotAgentObservability.ConfigCli.FirstTrace;
using CopilotAgentObservability.ConfigCli.FirstTrace.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode;
using CopilotAgentObservability.ConfigCli.Setup.Adapters.ClaudeCode.AgentSdk;
using CopilotAgentObservability.ConfigCli.Setup.Cli;
using CopilotAgentObservability.ConfigCli.Setup.Contracts;
using CopilotAgentObservability.ConfigCli.Setup.Platform;
using CopilotAgentObservability.ConfigCli.Setup.Storage;
using CopilotAgentObservability.Doctor.Tests.Persistence;
using CopilotAgentObservability.LocalMonitor.Health;
using CopilotAgentObservability.LocalMonitor.Ingestion;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.Persistence.Sqlite.Doctor.ClaudeCode;
using CopilotAgentObservability.Persistence.Sqlite.Ingestion;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using CopilotAgentObservability.Telemetry;
using CopilotAgentObservability.Telemetry.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace CopilotAgentObservability.Doctor.Tests.ClaudeCode;

public sealed class ClaudeFirstTraceCrossSurfaceTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 1, 2, 3, TimeSpan.Zero);

    private const string NativeSessionMarker = "SYNTHETIC_NATIVE_SESSION_X";
    private const string PromptMarker = "SYNTHETIC_PROMPT_MARKER";
    private const string PathMarker = "SYNTHETIC_PATH_MARKER";
    private const string TranscriptPathMarker = "SYNTHETIC_TRANSCRIPT_PATH";
    private const string CwdMarker = "SYNTHETIC_WORKING_DIRECTORY";
    private const string TraceId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string TraceContinuityTraceId = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
    private const string ContentDisabledTraceId = "ffffffffffffffffffffffffffffffff";
    private const string BaselineTraceId = "11111111111111111111111111111111";
    private const string SpanId = "bbbbbbbbbbbbbbbb";
    private const string InteractionSpanId = "cccccccccccccccc";
    private const string ToolSpanId = "dddddddddddddddd";

    [Fact]
    public async Task AlreadyCorrectConfiguration_BeginReportsHealthyWithoutSetupApply()
    {
        var directory = CreateDirectory("claude-first-trace-no-op");
        var databasePath = Path.Combine(directory, "monitor.db");
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);
            await using var monitor = await RunningMonitor.StartAsync(databasePath, time);
            var origin = monitor.Origin;
            var monitorProbe = new RunningMonitorHttpProbe(monitor.Client);
            var platform = CreatePlatform(databasePath, origin, monitorProbe);
            var orchestrator = CreateOrchestrator(platform, time);

            using var begin = RunFirstTrace(
                orchestrator,
                [
                    "begin", "--adapter", "claude-code", "--database", databasePath,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            Assert.Equal("first_trace_verification_started", begin.RootElement.GetProperty("code").GetString());
            var verificationId = begin.RootElement.GetProperty("verification_id").GetString();
            Assert.NotNull(verificationId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackThenBeginReportsRestoredEndpointAndSignalBlockersDeterministically()
    {
        var directory = CreateDirectory("claude-first-trace-post-rollback");
        var databasePath = Path.Combine(directory, "monitor.db");
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);
            await using var monitor = await RunningMonitor.StartAsync(databasePath, time);
            var origin = monitor.Origin;
            var monitorProbe = new RunningMonitorHttpProbe(monitor.Client);
            var platform = CreatePlatform(databasePath, origin, monitorProbe);
            var changeSetId = AssertChangedSetupHandoff(platform, origin);

            using var rollbackOutput = new StringWriter();
            using var rollbackError = new StringWriter();
            var rollbackExitCode = CliApplication.Run(
                ["setup", "rollback", "--change-set", changeSetId],
                rollbackOutput,
                rollbackError,
                SetupCompositionRoot.CreateSetupDispatch(platform));
            Assert.Equal(0, rollbackExitCode);
            Assert.Equal(string.Empty, rollbackError.ToString());
            using var rollback = JsonDocument.Parse(rollbackOutput.ToString());
            Assert.Equal(SetupCodes.RollbackSucceeded, rollback.RootElement.GetProperty("code").GetString());

            platform.ClearProcessEnvironment();
            using var begin = RunFirstTrace(
                CreateOrchestrator(platform, time),
                [
                    "begin", "--adapter", "claude-code", "--database", databasePath,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 3);
            var states = begin.RootElement.GetProperty("doctor").GetProperty("evaluation")
                .GetProperty("states").EnumerateArray()
                .Select(state => state.GetProperty("state_code").GetString())
                .ToArray();
            Assert.Contains("endpoint_mismatch", states);
            Assert.Contains("protocol_mismatch", states);
            Assert.Contains("signal_disabled", states);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OtelWithoutHookCompletesAsSessionUnbound()
    {
        var directory = CreateDirectory("claude-first-trace-otel-only");
        var databasePath = Path.Combine(directory, "monitor.db");
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);
            await using var monitor = await RunningMonitor.StartAsync(databasePath, time);
            var origin = monitor.Origin;
            var monitorProbe = new RunningMonitorHttpProbe(monitor.Client);
            var platform = CreatePlatform(databasePath, origin, monitorProbe);
            var orchestrator = CreateOrchestrator(platform, time);
            using var begin = RunFirstTrace(
                orchestrator,
                [
                    "begin", "--adapter", "claude-code", "--database", databasePath,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var verificationId = begin.RootElement.GetProperty("verification_id").GetString();
            Assert.NotNull(verificationId);

            await monitor.PostOtlpAsync(Payload(TraceId, NativeSessionMarker));
            await monitor.DrainAsync();
            new ClaudeDoctorCandidateObserver(databasePath, time).RunOnce();

            using var complete = RunFirstTrace(
                orchestrator,
                [
                    "complete", "--database", databasePath, "--verification-id", verificationId!,
                    "--expected-revision", "1", "--endpoint", origin, "--json",
                ],
                expectedExitCode: 3);
            Assert.Equal("first_trace_not_ready", complete.RootElement.GetProperty("code").GetString());
            Assert.Contains(
                complete.RootElement.GetProperty("doctor").GetProperty("evaluation").GetProperty("states").EnumerateArray(),
                state => state.GetProperty("state_code").GetString() == "session_unbound");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task HookOnlyWithoutOtelCompletesAsNotReadyWithoutCandidates()
    {
        var directory = CreateDirectory("claude-first-trace-hook-only");
        var databasePath = Path.Combine(directory, "monitor.db");
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);
            await using var monitor = await RunningMonitor.StartAsync(databasePath, time);
            var origin = monitor.Origin;
            var monitorProbe = new RunningMonitorHttpProbe(monitor.Client);
            var platform = CreatePlatform(databasePath, origin, monitorProbe);
            var orchestrator = CreateOrchestrator(platform, time);

            var verificationId = BeginVerification(orchestrator, databasePath, origin);
            await monitor.PostSessionStartAsync("SYNTHETIC_HOOK_ONLY_SESSION");
            await monitor.DrainAsync();
            new ClaudeDoctorCandidateObserver(databasePath, time).RunOnce();

            using var status = RunFirstTrace(
                orchestrator,
                [
                    "status", "--database", databasePath, "--verification-id", verificationId,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            Assert.Empty(status.RootElement.GetProperty("candidates").EnumerateArray());
            Assert.True(
                status.RootElement.GetProperty("evaluation_preview").ValueKind != JsonValueKind.Null,
                status.RootElement.GetRawText());
            Assert.Equal(
                "partial_fact_snapshot",
                status.RootElement.GetProperty("evaluation_preview").GetProperty("code").GetString());
            Assert.Equal(
                ["raw_persistence", "projection", "completeness_and_content"],
                status.RootElement.GetProperty("evaluation_preview").GetProperty("evaluation")
                    .GetProperty("missing_fact_families")
                    .EnumerateArray().Select(item => item.GetString()!).ToArray());

            using var complete = RunFirstTrace(
                orchestrator,
                [
                    "complete", "--database", databasePath, "--verification-id", verificationId,
                    "--expected-revision", "1", "--endpoint", origin, "--json",
                ],
                expectedExitCode: 3);
            Assert.Equal(FirstTraceCodes.NotReady, complete.RootElement.GetProperty("code").GetString());
            Assert.Equal("doctor.v1", complete.RootElement.GetProperty("doctor").GetProperty("schema_version").GetString());
            Assert.Equal("partial_fact_snapshot", complete.RootElement.GetProperty("doctor").GetProperty("code").GetString());
            Assert.False(complete.RootElement.GetProperty("doctor").GetProperty("success").GetBoolean());
            Assert.Equal(
                ["raw_persistence", "projection", "completeness_and_content"],
                complete.RootElement.GetProperty("doctor").GetProperty("evaluation")
                    .GetProperty("missing_fact_families")
                    .EnumerateArray().Select(item => item.GetString()!).ToArray());
            Assert.Empty(complete.RootElement.GetProperty("candidates").EnumerateArray());

            using var after = RunFirstTrace(
                orchestrator,
                [
                    "status", "--database", databasePath, "--verification-id", verificationId,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            Assert.Equal(
                "active",
                after.RootElement.GetProperty("doctor").GetProperty("verification").GetProperty("state").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CliJsonAndMonitorProjectionStayParityAcrossBindingAndContentStates()
    {
        var directory = CreateDirectory("claude-first-trace-projection-parity");
        var databasePath = Path.Combine(directory, "monitor.db");
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);
            await using var monitor = await RunningMonitor.StartAsync(databasePath, time);
            var origin = monitor.Origin;
            var monitorProbe = new RunningMonitorHttpProbe(monitor.Client);

            var exactPlatform = CreatePlatform(databasePath, origin, monitorProbe);
            var exactOrchestrator = CreateOrchestrator(exactPlatform, time);
            var exactVerificationId = BeginVerification(exactOrchestrator, databasePath, origin);
            await monitor.PostSessionStartAsync(NativeSessionMarker);
            await monitor.PostOtlpAsync(Payload(TraceId, NativeSessionMarker));
            await monitor.DrainAsync();
            new ClaudeDoctorCandidateObserver(databasePath, time).RunOnce();

            using var exactComplete = CompleteVerification(
                CreateOrchestrator(CreatePlatform(databasePath, origin, monitorProbe), time),
                databasePath,
                exactVerificationId,
                origin,
                expectedExitCode: 0);
            Assert.Equal(
                "first_trace_ready",
                exactComplete.RootElement.GetProperty("doctor").GetProperty("evaluation")
                    .GetProperty("primary_state").GetProperty("state_code").GetString());
            using var exactMonitor = await ReadMonitorTraceAsync(monitor, TraceId);
            Assert.Equal("exact_linked", exactMonitor.RootElement.GetProperty("binding_state").GetString());
            Assert.Equal("available", exactMonitor.RootElement.GetProperty("content_state").GetString());

            time.UtcNow = Now.AddSeconds(1);
            var continuityPlatform = CreatePlatform(databasePath, origin, monitorProbe);
            var continuityOrchestrator = CreateOrchestrator(continuityPlatform, time);
            var continuityVerificationId = BeginVerification(continuityOrchestrator, databasePath, origin);
            await monitor.PostSessionStartAsync("SYNTHETIC_TRACE_ONLY_SESSION");
            await monitor.DrainAsync();
            await monitor.PostOtlpAsync(Payload(TraceContinuityTraceId, sessionId: null));
            await monitor.ProjectOnlyAsync();
            AddTraceContinuityEvents(databasePath, time, TraceContinuityTraceId, "SYNTHETIC_TRACE_ONLY_SESSION");
            new ClaudeDoctorCandidateObserver(databasePath, time).RunOnce();

            using var continuityStatus = RunFirstTrace(
                continuityOrchestrator,
                [
                    "status", "--database", databasePath, "--verification-id", continuityVerificationId,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var continuityEvidence = continuityStatus.RootElement.GetProperty("candidates").EnumerateArray()
                .Select(candidate => candidate.GetProperty("evidence_ref").GetString()!)
                .ToArray();
            Assert.NotEmpty(continuityEvidence);
            Assert.InRange(continuityEvidence.Length, 1, DoctorValidation.MaximumAcceptedEvidenceReferences);
            using var continuityComplete = RunFirstTrace(
                CreateOrchestrator(CreatePlatform(databasePath, origin, monitorProbe), time),
                [
                    "complete", "--database", databasePath, "--verification-id", continuityVerificationId,
                    "--expected-revision", "1", "--endpoint", origin, "--json",
                    ..continuityEvidence.SelectMany(reference => new[] { "--evidence", reference }),
                ],
                expectedExitCode: 3);
            Assert.True(
                continuityComplete.RootElement.GetProperty("code").GetString() == "first_trace_not_ready",
                continuityComplete.RootElement.GetRawText());
            Assert.Contains(
                continuityComplete.RootElement.GetProperty("doctor").GetProperty("evaluation").GetProperty("states").EnumerateArray(),
                state => state.GetProperty("state_code").GetString() == "session_unbound");
            using var continuityMonitor = await ReadMonitorTraceAsync(monitor, TraceContinuityTraceId);
            Assert.Equal("hook_only", continuityMonitor.RootElement.GetProperty("binding_state").GetString());
            Assert.NotEqual("exact_linked", continuityMonitor.RootElement.GetProperty("binding_state").GetString());
            using var continuityCancel = RunFirstTrace(
                CreateOrchestrator(CreatePlatform(databasePath, origin, monitorProbe), time),
                [
                    "cancel", "--database", databasePath, "--verification-id", continuityVerificationId,
                    "--expected-revision", "1", "--json",
                ],
                expectedExitCode: 0);

            time.UtcNow = Now.AddSeconds(2);
            var contentDisabledPlatform = CreatePlatform(databasePath, origin, monitorProbe);
            contentDisabledPlatform.SeedProcessEnvironment("OTEL_LOG_TOOL_CONTENT", "0");
            var contentDisabledOrchestrator = CreateOrchestrator(contentDisabledPlatform, time);
            var contentDisabledVerificationId = BeginVerification(contentDisabledOrchestrator, databasePath, origin);
            await monitor.PostSessionStartAsync("SYNTHETIC_CONTENT_DISABLED_SESSION");
            await monitor.PostOtlpAsync(Payload(ContentDisabledTraceId, "SYNTHETIC_CONTENT_DISABLED_SESSION", includeContent: false));
            await monitor.DrainAsync();
            new ClaudeDoctorCandidateObserver(databasePath, time).RunOnce();

            using var contentDisabledComplete = CompleteVerification(
                CreateOrchestrator(
                    CreatePlatformWithContentDisabled(databasePath, origin, monitorProbe),
                    time),
                databasePath,
                contentDisabledVerificationId,
                origin,
                expectedExitCode: 0);
            var contentDisabledStates = contentDisabledComplete.RootElement.GetProperty("doctor").GetProperty("evaluation")
                .GetProperty("states").EnumerateArray().ToArray();
            Assert.Contains(contentDisabledStates, state => state.GetProperty("state_code").GetString() == "content_capture_disabled");
            Assert.Equal(
                "first_trace_ready",
                contentDisabledComplete.RootElement.GetProperty("doctor").GetProperty("evaluation")
                    .GetProperty("primary_state").GetProperty("state_code").GetString());
            using var contentDisabledMonitor = await ReadMonitorTraceAsync(monitor, ContentDisabledTraceId);
            Assert.Equal("exact_linked", contentDisabledMonitor.RootElement.GetProperty("binding_state").GetString());
            Assert.Equal("not_captured", contentDisabledMonitor.RootElement.GetProperty("content_state").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SetupBeginRealMonitorStatusAndCompleteProveClaudeFirstTraceAcrossSurfaces()
    {
        var directory = CreateDirectory("claude-first-trace-positive");
        var databasePath = Path.Combine(directory, "monitor.db");
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);

            await using var monitor = await RunningMonitor.StartAsync(databasePath, time);
            var origin = monitor.Origin;
            var monitorProbe = new RunningMonitorHttpProbe(monitor.Client);
            var setupPlatform = CreatePlatform(databasePath, origin, monitorProbe);
            AssertChangedSetupHandoff(setupPlatform, origin);

            var beginPlatform = CreateRestartedPlatform(setupPlatform, databasePath, origin, monitorProbe);
            var beginOrchestrator = CreateOrchestrator(beginPlatform, time);
            using var begin = RunFirstTrace(
                beginOrchestrator,
                [
                    "begin", "--adapter", "claude-code", "--database", databasePath,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var verificationId = begin.RootElement.GetProperty("verification_id").GetString();
            Assert.NotNull(verificationId);
            Assert.Equal("first_trace_verification_started", begin.RootElement.GetProperty("code").GetString());
            Assert.Equal("doctor.v1", begin.RootElement.GetProperty("doctor").GetProperty("schema_version").GetString());
            Assert.NotEmpty(begin.RootElement.GetProperty("guidance").EnumerateArray());
            AssertBeginObservedSetupState(beginOrchestrator, databasePath, verificationId!, origin);

            await monitor.PostSessionStartAsync(NativeSessionMarker);
            await monitor.PostOtlpAsync(Payload(TraceId, NativeSessionMarker));
            await monitor.DrainAsync();

            var observer = new ClaudeDoctorCandidateObserver(databasePath, time);
            observer.RunOnce();

            using var status = RunFirstTrace(
                beginOrchestrator,
                [
                    "status", "--database", databasePath, "--verification-id", verificationId!,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var candidates = status.RootElement.GetProperty("candidates").EnumerateArray().ToArray();
            Assert.Equal(
                Enum.GetValues<DoctorEvidenceKind>(),
                candidates.Select(candidate => ParseEvidenceKind(candidate.GetProperty("evidence_kind").GetString()!)).Distinct().OrderBy(kind => kind));
            Assert.All(candidates, candidate =>
            {
                var evidenceRef = candidate.GetProperty("evidence_ref").GetString()!;
                Assert.True(DoctorValidation.IsValidEvidenceReference(evidenceRef));
                Assert.Matches(
                    "^claude-otel-(ingest|raw|projection)-[0-9a-f]{32}-[0-9a-f]{16}$|^claude-otel-binding-[0-9a-f]{32}-[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$|^claude-otel-completeness-[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$",
                    evidenceRef);
            });
            AssertNoSensitiveMarkers(status.RootElement.GetProperty("candidates").GetRawText());

            var completionPlatform = CreatePlatform(databasePath, origin, monitorProbe);
            var completionOrchestrator = CreateOrchestrator(completionPlatform, time);
            using var complete = RunFirstTrace(
                completionOrchestrator,
                [
                    "complete", "--database", databasePath, "--verification-id", verificationId!,
                    "--expected-revision", "1", "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            Assert.Equal("first_trace_completed", complete.RootElement.GetProperty("code").GetString());
            Assert.Equal("doctor.v1", complete.RootElement.GetProperty("doctor").GetProperty("schema_version").GetString());
            Assert.Equal("verification_completed", complete.RootElement.GetProperty("doctor").GetProperty("code").GetString());
            Assert.Equal(
                "first_trace_ready",
                complete.RootElement.GetProperty("doctor").GetProperty("evaluation").GetProperty("primary_state").GetProperty("state_code").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SharedTraceIdWithoutSessionIdCannotPromoteClaudeFirstTrace()
    {
        var directory = CreateDirectory("claude-first-trace-negative");
        var databasePath = Path.Combine(directory, "monitor.db");
        var time = new DoctorTestTimeProvider(Now);
        try
        {
            PrepareBaselineDatabase(databasePath, time);
            await using var monitor = await RunningMonitor.StartAsync(databasePath, time);
            var origin = monitor.Origin;
            var monitorProbe = new RunningMonitorHttpProbe(monitor.Client);
            var setupPlatform = CreatePlatform(databasePath, origin, monitorProbe);
            AssertChangedSetupHandoff(setupPlatform, origin);

            var beginPlatform = CreateRestartedPlatform(setupPlatform, databasePath, origin, monitorProbe);
            var beginOrchestrator = CreateOrchestrator(beginPlatform, time);
            using var begin = RunFirstTrace(
                beginOrchestrator,
                [
                    "begin", "--adapter", "claude-code", "--database", databasePath,
                    "--endpoint", origin, "--json",
                ],
                expectedExitCode: 0);
            var verificationId = begin.RootElement.GetProperty("verification_id").GetString();
            Assert.NotNull(verificationId);

            AssertBeginObservedSetupState(beginOrchestrator, databasePath, verificationId!, origin);

            await monitor.PostSessionStartAsync(NativeSessionMarker);
            await monitor.PostOtlpAsync(Payload(TraceId, sessionId: null));
            await monitor.DrainAsync();
            new ClaudeDoctorCandidateObserver(databasePath, time).RunOnce();

            var completionPlatform = CreatePlatform(databasePath, origin, monitorProbe);
            using var complete = RunFirstTrace(
                CreateOrchestrator(completionPlatform, time),
                [
                    "complete", "--database", databasePath, "--verification-id", verificationId!,
                    "--expected-revision", "1", "--endpoint", origin, "--json",
                ],
                expectedExitCode: 3);
            Assert.Equal("first_trace_not_ready", complete.RootElement.GetProperty("code").GetString());
            Assert.Equal("evaluation_completed", complete.RootElement.GetProperty("doctor").GetProperty("code").GetString());
            Assert.Equal("active", complete.RootElement.GetProperty("doctor").GetProperty("verification").GetProperty("state").GetString());
            var states = complete.RootElement.GetProperty("doctor").GetProperty("evaluation").GetProperty("states").EnumerateArray();
            Assert.Contains(states, state => state.GetProperty("state_code").GetString() == "session_unbound");
            Assert.DoesNotContain(states, state => state.GetProperty("state_code").GetString() == "first_trace_ready");
            AssertNoSensitiveMarkers(complete.RootElement.GetRawText());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string AssertChangedSetupHandoff(TestSetupPlatform platform, string origin)
    {
        var dispatch = SetupCompositionRoot.CreateSetupDispatch(
            platform,
            new FixedClaudeHookCommandProvider(
                new ClaudeHookCommand(
                    "dotnet",
                    ["run", "--no-build", "--project", "synthetic-monitor.csproj", "--"],
                    ClaudeHookCommandMode.Repository)));

        using var planOutput = new StringWriter();
        using var planError = new StringWriter();
        var planExitCode = CliApplication.Run(
                [
                    "setup", "plan", "--adapter", "claude-code", "--target", "cli",
                    "--endpoint", origin,
                ],
                planOutput,
                planError,
                dispatch);
        Assert.True(planExitCode == 0, $"exit={planExitCode}; stdout={planOutput}; stderr={planError}");
        Assert.Equal(string.Empty, planError.ToString());
        AssertNoSensitiveMarkers(planOutput.ToString());
        AssertNoSensitiveMarkers(planError.ToString());
        using var plan = JsonDocument.Parse(planOutput.ToString());
        var changeSetId = plan.RootElement.GetProperty("change_set_id").GetString();
        Assert.NotNull(changeSetId);

        using var applyOutput = new StringWriter();
        using var applyError = new StringWriter();
        var applyExitCode = CliApplication.Run(
                ["setup", "apply", "--change-set", changeSetId!],
                applyOutput,
                applyError,
                dispatch);
        Assert.True(applyExitCode == 0, $"exit={applyExitCode}; stdout={applyOutput}; stderr={applyError}");
        Assert.Equal(string.Empty, applyError.ToString());
        AssertNoSensitiveMarkers(applyOutput.ToString());
        AssertNoSensitiveMarkers(applyError.ToString());
        using var apply = JsonDocument.Parse(applyOutput.ToString());
        Assert.True(apply.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(SetupCodes.ApplySucceeded, apply.RootElement.GetProperty("code").GetString());
        var cliTarget = Assert.Single(
            apply.RootElement.GetProperty("targets").EnumerateArray(),
            target => target.GetProperty("target_label").GetString() == "claude-code-user-settings");
        Assert.Equal("json", cliTarget.GetProperty("target_kind").GetString());
        Assert.NotEqual("no-op", cliTarget.GetProperty("operation").GetString());
        Assert.True(cliTarget.GetProperty("rollback_available").GetBoolean());
        Assert.Contains(
            cliTarget.GetProperty("changes").EnumerateArray(),
            change => change.GetProperty("operation").GetString() != "no-op");
        Assert.Equal(
            [SetupCodes.RestartClaudeProcess, SetupCodes.RunFirstTraceDoctor],
            apply.RootElement.GetProperty("next_actions").EnumerateArray().Select(item => item.GetString()!).ToArray());

        return changeSetId!;
    }

    private static void AssertBeginObservedSetupState(
        FirstTraceOrchestrator orchestrator,
        string databasePath,
        string verificationId,
        string origin)
    {
        using var status = RunFirstTrace(
            orchestrator,
            [
                "status", "--database", databasePath, "--verification-id", verificationId,
                "--endpoint", origin, "--json",
            ],
            expectedExitCode: 0);
        var preview = status.RootElement.GetProperty("evaluation_preview");
        Assert.Equal("evaluation_completed", preview.GetProperty("code").GetString());
        Assert.Equal(
            "agent_restart_required",
            preview.GetProperty("evaluation").GetProperty("primary_state").GetProperty("state_code").GetString());
        Assert.DoesNotContain(
            preview.GetProperty("evaluation").GetProperty("states").EnumerateArray(),
            state => state.GetProperty("state_code").GetString() == "endpoint_mismatch");
        AssertNoSensitiveMarkers(status.RootElement.GetProperty("candidates").GetRawText());
    }

    private static JsonDocument RunFirstTrace(
        FirstTraceOrchestrator orchestrator,
        string[] args,
        int expectedExitCode)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exitCode = FirstTraceCli.Run(args, output, error, orchestrator);
        Assert.Equal(expectedExitCode, exitCode);
        if (expectedExitCode == 0)
        {
            Assert.Equal(string.Empty, error.ToString());
        }
        else
        {
            Assert.NotEqual(string.Empty, error.ToString());
        }

        AssertNoSensitiveMarkers(output.ToString());
        AssertNoSensitiveMarkers(error.ToString());
        return JsonDocument.Parse(output.ToString());
    }

    private static FirstTraceOrchestrator CreateOrchestrator(TestSetupPlatform platform, TimeProvider time) =>
        new(
            [new ClaudeCodeFirstTraceAdapter(
                platform,
                platform.HttpProbe,
                platform.Clock,
                platform.InvocationDirectory)],
            time);

    private static TestSetupPlatform CreatePlatform(
        string databasePath,
        string endpoint,
        ISetupHttpProbe httpProbe)
    {
        var platform = new TestSetupPlatform(Now, httpProbe);
        platform.SeedFile(databasePath, []);
        platform.SeedProcessEnvironment("CLAUDE_CODE_ENABLE_TELEMETRY", "1");
        platform.SeedProcessEnvironment("CLAUDE_CODE_ENHANCED_TELEMETRY_BETA", "1");
        platform.SeedProcessEnvironment("OTEL_TRACES_EXPORTER", "otlp");
        platform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_PROTOCOL", "http/protobuf");
        platform.SeedProcessEnvironment("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT", endpoint + "/v1/traces");
        return platform;
    }

    private static TestSetupPlatform CreatePlatformWithContentDisabled(
        string databasePath,
        string endpoint,
        ISetupHttpProbe httpProbe)
    {
        var platform = CreatePlatform(databasePath, endpoint, httpProbe);
        platform.SeedProcessEnvironment("OTEL_LOG_TOOL_CONTENT", "0");
        return platform;
    }

    private static TestSetupPlatform CreateRestartedPlatform(
        TestSetupPlatform appliedPlatform,
        string databasePath,
        string endpoint,
        ISetupHttpProbe httpProbe)
    {
        var restarted = CreatePlatform(databasePath, endpoint, httpProbe);
        appliedPlatform.CopyPersistedSetupStateTo(restarted);
        return restarted;
    }

    private static void PrepareBaselineDatabase(string databasePath, TimeProvider time)
    {
        new RawTelemetryStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        new SqliteSourceCompatibilityStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter).CreateSchema();
        new SqliteMonitorRuntimeStateStore(databasePath, time, RawTelemetryStoreConnectionOptions.MonitorWriter).Upsert(false);

        var payload = Payload(BaselineTraceId, sessionId: "SYNTHETIC_BASELINE_SESSION");
        var decoded = OtlpTracePayloadDecoder.DecodeTracePayload("application/json", Encoding.UTF8.GetBytes(payload));
        var recognizedPayload = OtlpJsonRecognizedPayloadBuilder.Build(decoded.PayloadJson);
        var receivedAt = Now.AddSeconds(-1);
        var record = RawOtlpIngestor.CreateRecordFromPayloadJson(decoded.PayloadJson, recognizedPayload, receivedAt);
        var observation = SourceObservationBatchDraft.Create(
            Guid.CreateVersion7().ToString("D"),
            "claude-code",
            "2.1.207",
            "claude-code-otel",
            "claude-test-adapter-v1",
            decoded.StructuralInventory,
            SourceCompatibilityEvaluator.Assess(
                "claude-code",
                "2.1.207",
                decoded.StructuralInventory,
                observedRecognizedCount: 1,
                VerifiedSourceFingerprintRegistry.Create([], [], [])),
            SourceCaptureContentState.Available,
            receivedAt);
        new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter)
            .Commit(ValidatedIngestionBatch.Create(record, observation));
    }

    private static string Payload(string traceId, string? sessionId, bool includeContent = true)
    {
        var spans = new JsonArray
        {
            Span(InteractionSpanId, "claude_code.interaction", traceId, sessionId, parentSpanId: null, includeContent: includeContent),
            Span(SpanId, "claude_code.llm_request", traceId, sessionId, InteractionSpanId, includeContent: includeContent),
            Span(ToolSpanId, "claude_code.tool", traceId, sessionId, InteractionSpanId, includeContent: includeContent),
        };
        return new JsonObject
        {
            ["resourceSpans"] = new JsonArray
            {
                new JsonObject
                {
                    ["resource"] = new JsonObject
                    {
                        ["attributes"] = new JsonArray
                        {
                            Attribute("service.name", "claude-code"),
                            Attribute("service.version", "2.1.207"),
                        },
                    },
                    ["scopeSpans"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["scope"] = new JsonObject { ["name"] = "com.anthropic.claude_code" },
                            ["spans"] = spans,
                        },
                    },
                },
            },
        }.ToJsonString();
    }

    private static JsonObject Span(
        string spanId,
        string name,
        string traceId,
        string? sessionId,
        string? parentSpanId,
        bool includeContent = true)
    {
        var attributes = new JsonArray();
        if (includeContent)
        {
            attributes.Add(Attribute("user_prompt", PromptMarker));
            attributes.Add(Attribute("path", PathMarker));
        }
        if (sessionId is not null)
        {
            attributes.Add(Attribute("session.id", sessionId));
        }
        if (name == "claude_code.llm_request")
        {
            attributes.Add(Attribute("gen_ai.request.model", "claude-synthetic-model"));
            attributes.Add(Attribute("input_tokens", 11));
            attributes.Add(Attribute("output_tokens", 7));
        }
        if (name == "claude_code.tool")
        {
            attributes.Add(Attribute("tool_name", "synthetic_tool"));
        }

        var span = new JsonObject
        {
            ["traceId"] = traceId,
            ["spanId"] = spanId,
            ["name"] = name,
            ["startTimeUnixNano"] = "1784336523000000000",
            ["endTimeUnixNano"] = "1784336524000000000",
            ["status"] = new JsonObject { ["code"] = 1 },
            ["attributes"] = attributes,
        };
        if (parentSpanId is not null)
        {
            span["parentSpanId"] = parentSpanId;
        }
        return span;
    }

    private static string BeginVerification(
        FirstTraceOrchestrator orchestrator,
        string databasePath,
        string origin)
    {
        using var begin = RunFirstTrace(
            orchestrator,
            [
                "begin", "--adapter", "claude-code", "--database", databasePath,
                "--endpoint", origin, "--json",
            ],
            expectedExitCode: 0);
        return begin.RootElement.GetProperty("verification_id").GetString()!;
    }

    private static JsonDocument CompleteVerification(
        FirstTraceOrchestrator orchestrator,
        string databasePath,
        string verificationId,
        string origin,
        int expectedExitCode) =>
        RunFirstTrace(
            orchestrator,
            [
                "complete", "--database", databasePath, "--verification-id", verificationId,
                "--expected-revision", "1", "--endpoint", origin, "--json",
            ],
            expectedExitCode);

    private static async Task<JsonDocument> ReadMonitorTraceAsync(RunningMonitor monitor, string traceId)
    {
        using var document = JsonDocument.Parse(await monitor.Client.GetStringAsync("/api/monitor/traces"));
        var item = Assert.Single(
            document.RootElement.GetProperty("items").EnumerateArray(),
            candidate => candidate.GetProperty("trace_id").GetString() == traceId);
        return JsonDocument.Parse(item.GetRawText());
    }

    private static void AddTraceContinuityEvents(
        string databasePath,
        TimeProvider time,
        string traceId,
        string nativeSessionId)
    {
        var store = new SqliteSessionStore(databasePath, time);
        var detail = store.ListMostRecent(20)
            .Select(summary => store.GetDetail(summary.SessionId))
            .OfType<SessionDetail>()
            .Single(item => item.NativeIds.Any(native => native.NativeSessionId == nativeSessionId));
        var hookEvent = new ObservedSessionEvent(
            Guid.CreateVersion7(),
            detail.Session.SessionId,
            null,
            SessionSourceSurface.HookUnknown,
            null,
            traceId,
            null,
            "claude-code-hook",
            $"synthetic-trace-context-{traceId}",
            "UserPromptSubmit",
            time.GetUtcNow(),
            SessionContentState.NotCaptured);
        var otelEvents = new[] { InteractionSpanId, SpanId, ToolSpanId }
            .Select(spanId => new ObservedSessionEvent(
                Guid.CreateVersion7(),
                detail.Session.SessionId,
                null,
                SessionSourceSurface.ClaudeCode,
                null,
                traceId,
                null,
                "claude-code-otel",
                $"{traceId}/{spanId}",
                "otel.span",
                time.GetUtcNow(),
                SessionContentState.NotCaptured,
                MatchKind: SessionMatchKind.TraceContinuity))
            .ToArray();
        store.Write(new SessionWriteBatch(detail with { Events = detail.Events.Append(hookEvent).Concat(otelEvents).ToArray() }, []));
    }

    private static JsonObject Attribute(string key, string value) => new()
    {
        ["key"] = key,
        ["value"] = new JsonObject { ["stringValue"] = value },
    };

    private static JsonObject Attribute(string key, int value) => new()
    {
        ["key"] = key,
        ["value"] = new JsonObject { ["intValue"] = value.ToString() },
    };

    private static DoctorEvidenceKind ParseEvidenceKind(string value) => value switch
    {
        "ingest" => DoctorEvidenceKind.Ingest,
        "raw_persistence" => DoctorEvidenceKind.RawPersistence,
        "projection" => DoctorEvidenceKind.Projection,
        "exact_session_binding" => DoctorEvidenceKind.ExactSessionBinding,
        "completeness_content" => DoctorEvidenceKind.CompletenessContent,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, null),
    };

    private static void AssertNoSensitiveMarkers(string envelopeJson)
    {
        Assert.DoesNotContain(NativeSessionMarker, envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PromptMarker, envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain(PathMarker, envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain(TranscriptPathMarker, envelopeJson, StringComparison.Ordinal);
        Assert.DoesNotContain(CwdMarker, envelopeJson, StringComparison.Ordinal);
    }

    private static string CreateDirectory(string name)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RunningMonitor : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly IngestionWriterWorker ingestionWorker;
        private readonly SessionEventWriterWorker sessionWorker;
        private readonly ProjectionWorker projectionWorker;
        private readonly SqliteSessionOtelEnricher enricher;
        private readonly TimeProvider timeProvider;

        private RunningMonitor(
            WebApplication app,
            HttpClient client,
            string origin,
            IngestionWriterWorker ingestionWorker,
            SessionEventWriterWorker sessionWorker,
            ProjectionWorker projectionWorker,
            SqliteSessionOtelEnricher enricher,
            TimeProvider timeProvider)
        {
            this.app = app;
            Client = client;
            Origin = origin;
            this.ingestionWorker = ingestionWorker;
            this.sessionWorker = sessionWorker;
            this.projectionWorker = projectionWorker;
            this.enricher = enricher;
            this.timeProvider = timeProvider;
        }

        public HttpClient Client { get; }

        public string Origin { get; }

        public static async Task<RunningMonitor> StartAsync(
            string databasePath,
            TimeProvider time)
        {
            var queue = new IngestionQueue(time);
            var sessionQueue = new SessionEventQueue();
            var compatibility = new SqliteSourceCompatibilityStore(
                databasePath,
                RawTelemetryStoreConnectionOptions.MonitorWriter);
            var sessionStore = new SqliteSessionStore(databasePath, time);
            var health = new MonitorHealthState();
            health.SetProjectionWorkerRunning(true);
            health.SetProjectionStatus(0, null);
            var app = MonitorHost.Build(
                new MonitorOptions(databasePath, "http://127.0.0.1:0", false, MonitorOptions.DefaultMaxRequestBodyBytes),
                new MonitorHostTestOptions
                {
                    Queue = queue,
                    SourceCompatibilityStore = compatibility,
                    SourceMetadataProvider = new FixedOtlpTraceSourceMetadataProvider(
                        OtlpTraceSourceMetadata.Create(
                            "claude-code",
                            "2.1.207",
                            "claude-code-otel",
                            "claude-test-adapter-v1",
                            SourceCaptureContentState.Available)),
                    Health = health,
                    StartWriter = false,
                    StartProjectionWorker = false,
                    SessionStore = sessionStore,
                    SessionEventQueue = sessionQueue,
                    StartSessionWriter = false,
                    StartSessionOtelEnrichment = false,
                    TimeProvider = time,
                    UseUserSecrets = false,
                });
            try
            {
                await app.StartAsync();
                var origin = GetSingleBoundAddress(app);
                var ingestionWorker = new IngestionWriterWorker(
                    queue,
                    new SqliteIngestionCommitStore(databasePath, RawTelemetryStoreConnectionOptions.MonitorWriter),
                    compatibility,
                    health);
                await ingestionWorker.StartAsync(CancellationToken.None);
                var normalizer = new SessionEventNormalizer(sessionStore, time);
                var sessionWorker = new SessionEventWriterWorker(sessionQueue, normalizer);
                await sessionWorker.StartAsync(CancellationToken.None);
                var projectionWorker = new ProjectionWorker(
                    app.Services.GetRequiredService<IMonitorProjectionStore>(),
                    health,
                    compatibility,
                    time);
                var enricher = new SqliteSessionOtelEnricher(databasePath, sessionStore, time);
                return new(
                    app,
                    new HttpClient { BaseAddress = new Uri(origin) },
                    origin,
                    ingestionWorker,
                    sessionWorker,
                    projectionWorker,
                    enricher,
                    time);
            }
            catch
            {
                await app.DisposeAsync();
                throw;
            }
        }

        private static string GetSingleBoundAddress(WebApplication app)
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?
                .Addresses
                .ToArray();
            Assert.NotNull(addresses);
            var address = Assert.Single(addresses);
            Assert.StartsWith("http://127.0.0.1:", address, StringComparison.Ordinal);
            Assert.False(address.EndsWith(":0", StringComparison.Ordinal));
            return address;
        }

        public async Task PostSessionStartAsync(string nativeSessionId)
        {
            var envelope = new JsonObject
            {
                ["schema_version"] = 1,
                ["source_adapter"] = "claude-code-hook",
                ["source_surface"] = "claude-code",
                ["native_session_id"] = nativeSessionId,
                ["source_application_version"] = "2.1.207",
                ["adapter_version"] = "claude-hook-v1",
                ["normalization_version"] = "session-normalization-v1",
                ["events"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["source_event_id"] = $"synthetic-session-start-event-{nativeSessionId}",
                        ["type"] = "SessionStart",
                        ["occurred_at"] = timeProvider.GetUtcNow().ToString("O"),
                        ["payload"] = new JsonObject
                        {
                            ["session_id"] = nativeSessionId,
                            ["transcript_path"] = TranscriptPathMarker,
                            ["cwd"] = CwdMarker,
                            ["hook_event_name"] = "SessionStart",
                            ["source"] = "startup",
                        },
                    },
                },
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/session-ingest/v1/events")
            {
                Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("X-CAO-Session-Event-Version", "1");
            using var response = await Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public async Task PostOtlpAsync(string payload)
        {
            using var response = await Client.PostAsync(
                "/v1/traces",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        public async Task DrainAsync()
        {
            await projectionWorker.RunProjectionPassAsync();
            enricher.ProcessNextBatch();
        }

        public Task ProjectOnlyAsync() => projectionWorker.RunProjectionPassAsync();

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await sessionWorker.StopAsync(CancellationToken.None);
            await ingestionWorker.StopAsync(CancellationToken.None);
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private sealed class RunningMonitorHttpProbe(HttpClient client) : ISetupHttpProbe
    {
        public SetupHttpProbeObservation Get(
            string origin,
            string path,
            int totalBudgetMilliseconds,
            int maxBodyBytes)
        {
            using var cancellation = new CancellationTokenSource(totalBudgetMilliseconds);
            try
            {
                using var response = client.GetAsync(
                        new Uri(new Uri(origin, UriKind.Absolute), path),
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellation.Token)
                    .GetAwaiter()
                    .GetResult();
                using var stream = response.Content.ReadAsStream(cancellation.Token);
                var buffer = new byte[maxBodyBytes + 1];
                var length = 0;
                while (length < buffer.Length)
                {
                    var read = stream.Read(buffer, length, buffer.Length - length);
                    if (read == 0)
                    {
                        break;
                    }

                    length += read;
                }

                return new(
                    SetupHttpProbeOutcome.Response,
                    (int)response.StatusCode,
                    response.Content.Headers.ContentLength,
                    buffer[..length],
                    length <= maxBodyBytes);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return SetupHttpProbeObservation.TimedOut;
            }
            catch (HttpRequestException)
            {
                return SetupHttpProbeObservation.TransportFailure;
            }
        }
    }

    private sealed class TestSetupPlatform : ISetupPlatform
    {
        private readonly Dictionary<string, byte[]> files = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, SetupPathMetadata> paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> processEnvironment = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string?> userEnvironment = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> locks = new(StringComparer.OrdinalIgnoreCase);
        private long identifierSequence = 1;

        public TestSetupPlatform(DateTimeOffset utcNow, ISetupHttpProbe httpProbe)
        {
            InvocationDirectory = "C:\\Users\\first-trace";
            LocalApplicationData = "C:\\first-trace-local-app-data";
            PathStyle = SetupPathStyle.Windows;
            Clock = new TestClock(utcNow);
            Identifiers = new TestIdentifiers(this);
            FileSystem = new TestFileSystem(this);
            UserEnvironment = new TestUserEnvironment(this);
            ProcessEnvironment = new TestProcessEnvironment(this);
            Execution = new TestExecution();
            OperatingSystem = new TestOperatingSystem();
            ProcessRunner = new TestProcessRunner();
            ManagedSettings = new TestManagedSettings();
            HttpProbe = httpProbe;
            SeedDirectoryChain(InvocationDirectory);
            SeedDirectoryChain(OperatingSystem.UserProfile);
            SeedDirectoryChain(Path.Combine(OperatingSystem.UserProfile, ".claude"));
            SeedFile(
                Path.Combine(OperatingSystem.UserProfile, ".claude", "settings.json"),
                Encoding.UTF8.GetBytes("{}\n"));
        }

        public string InvocationDirectory { get; }
        public SetupPathStyle PathStyle { get; }
        public string LocalApplicationData { get; }
        public ISetupFileSystem FileSystem { get; }
        public ISetupUserEnvironment UserEnvironment { get; }
        public ISetupProcessEnvironment ProcessEnvironment { get; }
        public ISetupClock Clock { get; }
        public ISetupIdentifierGenerator Identifiers { get; }
        public ISetupExecution Execution { get; }
        public ISetupOperatingSystem OperatingSystem { get; }
        public ISetupProcessRunner ProcessRunner { get; }
        public ISetupManagedSettingsSource ManagedSettings { get; }
        public ISetupHttpProbe HttpProbe { get; }

        public void SeedFile(string path, byte[] bytes)
        {
            files[path] = bytes.ToArray();
            paths[path] = new SetupPathMetadata(true, SetupPathKind.File, FileAttributes.Normal);
        }

        public void SeedProcessEnvironment(string name, string value) => processEnvironment[name] = value;

        public void ClearProcessEnvironment() => processEnvironment.Clear();

        public void CopyPersistedSetupStateTo(TestSetupPlatform destination)
        {
            var settingsRoot = Path.Combine(OperatingSystem.UserProfile, ".claude");
            var setupRoot = new SetupRuntimePaths(this).Root;
            foreach (var file in files)
            {
                if (IsUnder(file.Key, settingsRoot) || IsUnder(file.Key, setupRoot))
                {
                    destination.SeedFile(file.Key, file.Value);
                }
            }
        }

        private static bool IsUnder(string path, string root) =>
            string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

        private void SeedDirectoryChain(string directory)
        {
            var current = Path.GetPathRoot(directory)!;
            paths[current] = new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory);
            foreach (var segment in directory[current.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                paths[current] = new SetupPathMetadata(true, SetupPathKind.Directory, FileAttributes.Directory);
            }
        }

        private sealed class TestFileSystem(TestSetupPlatform platform) : ISetupFileSystem
        {
            public void CreateDirectory(string path) => platform.paths[path] = new(true, SetupPathKind.Directory, FileAttributes.Directory);
            public bool FileExists(string path) => platform.files.ContainsKey(path);
            public byte[] ReadAllBytes(string path) => platform.files[path].ToArray();
            public SetupBoundedFileRead ReadAtMostBytes(string path, int maximumBytes)
            {
                var bytes = platform.files[path];
                var length = Math.Min(bytes.Length, maximumBytes + 1);
                return new(bytes[..length], bytes.Length <= maximumBytes);
            }
            public bool HasDirectories(string path) => platform.paths.Keys.Any(candidate =>
                candidate.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                && platform.paths[candidate].Kind == SetupPathKind.Directory);
            public void WriteAllBytes(string path, ReadOnlySpan<byte> bytes) => platform.SeedFile(path, bytes.ToArray());
            public void WriteNewAllBytes(string path, ReadOnlySpan<byte> bytes)
            {
                if (platform.paths.ContainsKey(path)) throw new IOException("Destination exists.");
                platform.SeedFile(path, bytes.ToArray());
            }
            public bool TryWriteNewAllBytesAndFlush(string path, ReadOnlySpan<byte> bytes)
            {
                if (platform.paths.ContainsKey(path)) return false;
                platform.SeedFile(path, bytes.ToArray());
                return true;
            }
            public void FlushFile(string path) { }
            public void ReplaceFile(string sourcePath, string destinationPath)
            {
                platform.SeedFile(destinationPath, platform.files[sourcePath]);
                platform.DeletePath(sourcePath);
            }
            public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
            {
                if (!overwrite && platform.paths.ContainsKey(destinationPath)) throw new IOException("Destination exists.");
                platform.SeedFile(destinationPath, platform.files[sourcePath]);
                platform.DeletePath(sourcePath);
            }
            public void DeleteFile(string path) => platform.DeletePath(path);
            public SetupPathMetadata GetPathMetadata(string path) => platform.paths.TryGetValue(path, out var metadata) ? metadata : SetupPathMetadata.Missing;
            public ISetupExclusiveFileLock? TryAcquireExclusiveFileLock(string path) =>
                platform.locks.Add(path) ? new TestLock(platform, path) : null;
        }

        private void DeletePath(string path)
        {
            files.Remove(path);
            paths.Remove(path);
        }

        private sealed class TestLock(TestSetupPlatform platform, string path) : ISetupExclusiveFileLock
        {
            public void Dispose() => platform.locks.Remove(path);
        }

        private sealed class TestUserEnvironment(TestSetupPlatform platform) : ISetupUserEnvironment
        {
            public string? Get(string name) => platform.userEnvironment.GetValueOrDefault(name);
            public void Set(string name, string? value) => platform.userEnvironment[name] = value;
            public void NotifyChange() { }
        }

        private sealed class TestProcessEnvironment(TestSetupPlatform platform) : ISetupProcessEnvironment
        {
            public string? Get(string name) => platform.processEnvironment.GetValueOrDefault(name);
        }

        private sealed class TestClock(DateTimeOffset utcNow) : ISetupClock
        {
            public DateTimeOffset UtcNow { get; } = utcNow;
        }

        private sealed class TestIdentifiers(TestSetupPlatform platform) : ISetupIdentifierGenerator
        {
            public Guid CreateUuidV7() => Guid.Parse($"00000000-0000-7000-8000-{platform.identifierSequence++:D12}");
        }

        private sealed class TestExecution : ISetupExecution
        {
            public void Checkpoint(string operation) { }
        }

        private sealed class TestOperatingSystem : ISetupOperatingSystem
        {
            public SetupPlanningOs Current => SetupPlanningOs.Windows;
            public string ApplicationData => "C:\\Users\\first-trace\\AppData\\Roaming";
            public string UserProfile => "C:\\Users\\first-trace";
        }

        private sealed class TestProcessRunner : ISetupProcessRunner
        {
            public SetupProcessObservation Run(string fileName, IReadOnlyList<string> arguments) =>
                new(SetupProcessOutcome.Completed, 0, "2.1.207 (Claude Code)");
        }

        private sealed class TestManagedSettings : ISetupManagedSettingsSource
        {
            public SetupManagedObservation Read(SetupManagedLocation location) => SetupManagedObservation.Absent;
        }

    }
}

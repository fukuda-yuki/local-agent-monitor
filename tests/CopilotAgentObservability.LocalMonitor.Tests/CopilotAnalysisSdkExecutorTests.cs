using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.Sessions;
using CopilotAgentObservability.LocalMonitor.Sessions.SkillInvocationV2;
using GitHub.Copilot;
using System.Security.Cryptography;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class CopilotAnalysisSdkExecutorTests
{
    private static readonly string[] CoreToolNames =
    [
        "get_raw_trace",
        "get_raw_record",
        "get_raw_span_context",
        "get_trace_summary",
        "get_trace_span_tree",
        "get_cache_summary",
        "get_instruction_evidence",
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_CapturesEmptyModeChildBoundCustomOnlyConfiguration(
        bool includesSubmissionTool)
    {
        using var temp = new MonitorTempDirectory();
        var ownedChild = Path.GetFullPath(Path.Combine(temp.Path, "owned-child"));
        Directory.CreateDirectory(ownedChild);
        CopilotClientOptions? capturedOptions = null;
        SessionConfig? capturedSessionConfig = null;
        var sentinel = new InvalidOperationException("client factory sentinel");
        await using var data = await CreateToolDataAsync(includesSubmissionTool
            ? MonitorAnalysisFocus.InstructionDiagnosis
            : MonitorAnalysisFocus.Errors);
        var executor = new CopilotAnalysisSdkExecutor((options, sessionConfig) =>
        {
            capturedOptions = options;
            capturedSessionConfig = sessionConfig;
            throw sentinel;
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            executor.ExecuteAsync(
                ownedChild,
                new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
                new CopilotAnalysisToolRequest("synthetic prompt", data),
                CancellationToken.None));

        Assert.Same(sentinel, exception);
        var options = Assert.IsType<CopilotClientOptions>(capturedOptions);
        Assert.Equal(CopilotClientMode.Empty, options.Mode);
        Assert.Equal(ownedChild, options.BaseDirectory);
        Assert.Equal(ownedChild, options.WorkingDirectory);
        var expectedToolNames = includesSubmissionTool
            ? [.. CoreToolNames, "submit_instruction_finding"]
            : CoreToolNames;
        await AssertClosedSessionBoundaryAsync(capturedSessionConfig, ownedChild, expectedToolNames);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsPresentCopilotCliPathBeforeClientFactory()
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        var factoryCalls = 0;
        var executor = new CopilotAnalysisSdkExecutor(
            (_, _) => { factoryCalls++; throw new InvalidOperationException("must not run"); },
            name => string.Equals(name, "COPILOT_CLI_PATH", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.ExecuteAsync(
            Path.GetFullPath("synthetic-owned-child"),
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("synthetic prompt", data), CancellationToken.None));

        Assert.Equal("The bundled Copilot runtime is unavailable.", error.Message);
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsChildDirectoryThatIsNotOwnedByAnalysisScopeBeforeRootLease()
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        var ownedChild = Path.GetFullPath("synthetic-owned-child");
        var suppliedChild = Path.GetFullPath("different-child");
        var scope = new FakeAnalysisScope(ownedChild);
        var context = new CopilotAnalysisRootsExecutionContext(
            scope, null!, null!, new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1()),
            null, new SessionEventQueue(), TimeSpan.FromSeconds(1),
            _ => throw new InvalidOperationException("must not create client"), _ => false, CancellationToken.None);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new CopilotAnalysisSdkExecutor().ExecuteAsync(
            suppliedChild,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("synthetic prompt", data), context, CancellationToken.None));

        Assert.Equal("The analysis directory ownership is invalid.", error.Message);
    }

    [Fact]
    public async Task ExecuteAsync_RootsEnabledZeroInvocationUsesProbeThenAutopilotExecutionAndReturnsCandidate()
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture();

        var result = await new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None);

        Assert.Equal("result", result.ResultMarkdown);
        var candidate = Assert.IsType<CopilotRuntimeGenerationV1>(result.UnpublishedCandidate);
        Assert.Equal(2, fixture.Client.Sessions.Count);
        Assert.All(fixture.Client.Configs, config => Assert.NotNull(config.OnEvent));
        Assert.Null(fixture.Client.Sessions[0].Prompt);
        Assert.True(fixture.Client.Sessions[0].DisposedBeforeNextCreate);
        Assert.Equal("unchanged prompt", fixture.Client.Sessions[1].Prompt);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Empty(fixture.Transport.Bodies);

        Assert.True(await fixture.Admission.DiscardCandidateAsync(candidate));
        Assert.Equal(1, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_RootsEnabledOneInvocationImportsAfterDisposeOrCleansUpOnImportFailure(bool refuseTransport)
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture(refuseTransport
            ? OwnedFailure.ImportTransportRefused
            : OwnedFailure.OneInvocation);
        using var workerCancellation = new CancellationTokenSource();
        var worker = Task.Run(async () =>
        {
            while (!workerCancellation.IsCancellationRequested)
            {
                SessionEventWriteRequest request;
                try { request = await fixture.Queue.Reader.ReadAsync(workerCancellation.Token); }
                catch (OperationCanceledException) { break; }
                fixture.Queue.MarkDequeued();
                Assert.True(request.TryClaim());
                fixture.Order.Add($"v1:{Assert.Single(request.Envelope.Events!).Type}");
                request.Complete(SessionEventCommitStatus.Committed);
            }
        });

        CopilotAnalysisExecutionResult? result = null;
        if (refuseTransport)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CopilotAnalysisSdkExecutor().ExecuteAsync(
                fixture.Scope.ChildDirectory,
                new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
                new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None));
        }
        else
        {
            result = await new CopilotAnalysisSdkExecutor().ExecuteAsync(
                fixture.Scope.ChildDirectory,
                new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
                new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None);
        }
        workerCancellation.Cancel();
        await worker;

        Assert.Equal("unchanged prompt", fixture.Client.Sessions[1].Prompt);
        Assert.Equal(1, fixture.Client.Sessions[1].DisposeCalls);
        Assert.Equal(refuseTransport ? ["execution.dispose", "v2", "client.dispose", "scope.dispose"] :
            ["execution.dispose", "v2", "v1:session.start", "v1:session.task_complete"], fixture.Order);
        Assert.Equal(0, fixture.RootGeneration.OutstandingLeaseCount);
        if (refuseTransport)
        {
            Assert.Null(result);
            Assert.Equal(1, fixture.Client.DisposeCalls);
            Assert.Equal(1, fixture.Scope.DisposeCalls);
            Assert.Null(fixture.Transport.ConsumedCandidate);
        }
        else
        {
            var candidate = Assert.IsType<CopilotRuntimeGenerationV1>(result!.UnpublishedCandidate);
            Assert.Same(candidate, fixture.Transport.ConsumedCandidate);
            Assert.Equal("result", result.ResultMarkdown);
            Assert.Equal(0, fixture.Client.DisposeCalls);
            Assert.Equal(0, fixture.Scope.DisposeCalls);
            Assert.True(await fixture.Admission.DiscardCandidateAsync(candidate));
        }
    }

    [Theory]
    [InlineData(OwnedFailure.Status)]
    [InlineData(OwnedFailure.ProbeUnexpectedRelevantEvent)]
    [InlineData(OwnedFailure.ExecutionUnsuccessfulTerminal)]
    [InlineData(OwnedFailure.ExecutionCreate)]
    [InlineData(OwnedFailure.ExecutionSend)]
    public async Task ExecuteAsync_RootsEnabledFailuresDiscardCandidateAndOwnedResourcesExactlyOnce(OwnedFailure failure)
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture(failure);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None));

        Assert.Equal(1, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
        Assert.Equal(0, fixture.RootGeneration.OutstandingLeaseCount);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Empty(fixture.Transport.Bodies);
    }

    [Theory]
    [InlineData(CancellationAuthority.HostStopping, OwnedExecutionPhase.Probe)]
    [InlineData(CancellationAuthority.HostStopping, OwnedExecutionPhase.Execution)]
    [InlineData(CancellationAuthority.HostStopping, OwnedExecutionPhase.Import)]
    [InlineData(CancellationAuthority.LeaseLoss, OwnedExecutionPhase.Probe)]
    [InlineData(CancellationAuthority.LeaseLoss, OwnedExecutionPhase.Execution)]
    [InlineData(CancellationAuthority.LeaseLoss, OwnedExecutionPhase.Import)]
    public async Task ExecuteAsync_AuthorityLossAtEachOwnedPhaseStopsWithoutUnauthorizedSuffix(
        CancellationAuthority authority,
        OwnedExecutionPhase phase)
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture(control: new OwnedExecutionControl(phase));

        var execution = new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None);

        await fixture.Control!.Barrier.Entered;
        if (authority == CancellationAuthority.HostStopping) fixture.StopHost();
        else fixture.Scope.LoseLease();
        await Assert.ThrowsAnyAsync<Exception>(() => execution);

        Assert.False(fixture.Admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(0, fixture.RootGeneration.OutstandingLeaseCount);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.All(fixture.Client.Sessions, session => Assert.Equal(1, session.DisposeCalls));
        Assert.Equal(1, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
        Assert.Equal(["client.dispose", "scope.dispose"], fixture.Order.TakeLast(2));
        Assert.Equal(0, fixture.Control.OutstandingBarriers);
        Assert.Equal(phase == OwnedExecutionPhase.Probe ? 1 : 2, fixture.Client.Sessions.Count);
        Assert.Equal(phase == OwnedExecutionPhase.Import ? 1 : 0, fixture.Transport.Bodies.Count);
        Assert.DoesNotContain(fixture.Order, entry => entry.StartsWith("v1:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_RelevantCallbackDuringExecutionDisposePoisonsBeforeFreeze()
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture(control: new OwnedExecutionControl(
            OwnedExecutionPhase.None, emitRelevantDuringExecutionDispose: true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None));

        Assert.Empty(fixture.Transport.Bodies);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.False(fixture.Admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(1, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
        Assert.All(fixture.Client.Sessions, session => Assert.Equal(1, session.DisposeCalls));
    }

    [Fact]
    public async Task ExecuteAsync_RelevantCallbackAfterExecutionDisposeCancelsBlockedImportAndPreventsPublication()
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture(control: new OwnedExecutionControl(OwnedExecutionPhase.Import));
        var execution = new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None);

        await fixture.Control!.Barrier.Entered;
        var executionSession = fixture.Client.Sessions[1];
        Assert.Equal(1, executionSession.DisposeCalls);
        executionSession.EmitLateRelevantEvent();
        await Assert.ThrowsAsync<InvalidOperationException>(() => execution);

        Assert.Equal(0, fixture.Queue.Count);
        Assert.False(fixture.Admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(1, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
        Assert.All(fixture.Client.Sessions, session => Assert.Equal(1, session.DisposeCalls));
        Assert.Equal(0, fixture.Control.OutstandingBarriers);
    }

    [Theory]
    [InlineData(CancellationAuthority.HostStopping, NativeReadPhase.ProbeInventory)]
    [InlineData(CancellationAuthority.HostStopping, NativeReadPhase.ExecutionInventory)]
    [InlineData(CancellationAuthority.HostStopping, NativeReadPhase.CallbackReproof)]
    [InlineData(CancellationAuthority.LeaseLoss, NativeReadPhase.ProbeInventory)]
    [InlineData(CancellationAuthority.LeaseLoss, NativeReadPhase.ExecutionInventory)]
    [InlineData(CancellationAuthority.LeaseLoss, NativeReadPhase.CallbackReproof)]
    public async Task ExecuteAsync_AuthorityLossCancelsEachNativeReadAndStopsOwnedSuffix(
        CancellationAuthority authority,
        NativeReadPhase phase)
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture(control: new OwnedExecutionControl(
            OwnedExecutionPhase.None, nativeReadPhase: phase));

        var execution = Task.Run(() => new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None));

        await fixture.Control!.NativeReadBarrier.Entered;
        if (authority == CancellationAuthority.HostStopping) fixture.StopHost();
        else fixture.Scope.LoseLease();
        fixture.Control.NativeReadBarrier.Release();
        await Assert.ThrowsAnyAsync<Exception>(() => execution);

        Assert.True(fixture.Control.NativeReadTokenCanBeCanceled);
        Assert.True(fixture.Control.NativeReadTokenWasCanceled);
        Assert.False(fixture.Admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Equal(0, fixture.RootGeneration.OutstandingLeaseCount);
        Assert.Empty(fixture.Transport.Bodies);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Equal(phase == NativeReadPhase.ProbeInventory ? 1 : 2, fixture.Client.Sessions.Count);
        Assert.All(fixture.Client.Sessions, session => Assert.Equal(1, session.DisposeCalls));
        Assert.Equal(1, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
        Assert.Equal(["client.dispose", "scope.dispose"], fixture.Order.TakeLast(2));
        Assert.Equal(0, fixture.Control.NativeReadBarrier.Outstanding);
    }

    [Theory]
    [InlineData(LateProbeEvent.DuplicateMatchingStart)]
    [InlineData(LateProbeEvent.DriftedStart)]
    [InlineData(LateProbeEvent.SkillInvoked)]
    [InlineData(LateProbeEvent.Terminal)]
    [InlineData(LateProbeEvent.Failure)]
    public async Task ExecuteAsync_RelevantProbeCallbackAfterProbeCloseSynchronouslyInvalidatesExactCandidate(
        LateProbeEvent lateEvent)
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture(control: new OwnedExecutionControl(OwnedExecutionPhase.Execution));
        var execution = new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), fixture.Context, CancellationToken.None);

        await fixture.Control!.Barrier.Entered;
        var probe = fixture.Client.Sessions[0];
        Assert.Equal(1, probe.DisposeCalls);
        probe.EmitLateProbeEvent(lateEvent);
        var synchronouslyCanceled = fixture.Client.Sessions[1].LastOperationToken.IsCancellationRequested;
        fixture.Control.Barrier.Release();
        await Assert.ThrowsAnyAsync<Exception>(() => execution);

        Assert.True(synchronouslyCanceled);
        Assert.False(fixture.Admission.TryGetCurrentAdmittedGeneration(out _));
        Assert.Empty(fixture.Transport.Bodies);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.All(fixture.Client.Sessions, session => Assert.Equal(1, session.DisposeCalls));
        Assert.Equal(1, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
        Assert.Equal(["client.dispose", "scope.dispose"], fixture.Order.TakeLast(2));
    }

    [Fact]
    public async Task ExecuteAsync_BridgeAbsentStopsBeforeSdkAndCleansOwnedScopeExactlyOnce()
    {
        await using var data = await CreateToolDataAsync(MonitorAnalysisFocus.Errors);
        using var fixture = new OwnedFixture();
        var clientFactoryCalls = 0;
        var context = fixture.Context with
        {
            Bridge = null,
            OwnedClientFactory = _ => { clientFactoryCalls++; return fixture.Client; },
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => new CopilotAnalysisSdkExecutor().ExecuteAsync(
            fixture.Scope.ChildDirectory,
            new CopilotAnalysisExecutionSettings("synthetic-model", 60, Provider: null),
            new CopilotAnalysisToolRequest("unchanged prompt", data), context, CancellationToken.None));

        Assert.Equal(0, clientFactoryCalls);
        Assert.Empty(fixture.Client.Sessions);
        Assert.Empty(fixture.Transport.Bodies);
        Assert.Equal(0, fixture.Queue.Count);
        Assert.Equal(0, fixture.Client.DisposeCalls);
        Assert.Equal(1, fixture.Scope.DisposeCalls);
        Assert.Equal(0, fixture.RootGeneration.OutstandingLeaseCount);
    }

    public enum OwnedFailure { None, Status, ProbeUnexpectedRelevantEvent, ExecutionUnsuccessfulTerminal, ExecutionCreate, ExecutionSend, OneInvocation, ImportTransportRefused }
    public enum CancellationAuthority { HostStopping, LeaseLoss }
    public enum OwnedExecutionPhase { None, Probe, Execution, Import }
    public enum NativeReadPhase { ProbeInventory, ExecutionInventory, CallbackReproof }
    public enum LateProbeEvent { DuplicateMatchingStart, DriftedStart, SkillInvoked, Terminal, Failure }

    private static async Task AssertClosedSessionBoundaryAsync(
        SessionConfig? captured,
        string expectedDirectory,
        IReadOnlyList<string> expectedToolNames)
    {
        var config = Assert.IsType<SessionConfig>(captured);
        var availableTools = Assert.IsAssignableFrom<IList<string>>(config.AvailableTools);
        var tools = Assert.IsAssignableFrom<IList<Microsoft.Extensions.AI.AIFunctionDeclaration>>(config.Tools);
        Assert.Equal(expectedToolNames, tools.Select(static tool => tool.Name));
        Assert.Equal(expectedToolNames.Select(static name => $"custom:{name}"), availableTools);
        Assert.DoesNotContain(availableTools, static pattern => pattern.Contains('*', StringComparison.Ordinal));
        Assert.DoesNotContain(availableTools, static pattern => pattern.StartsWith("builtin:", StringComparison.Ordinal));
        Assert.DoesNotContain(availableTools, static pattern => pattern.StartsWith("mcp:", StringComparison.Ordinal));
        Assert.Equal(expectedDirectory, config.WorkingDirectory);
        var largeOutput = Assert.IsType<LargeToolOutputConfig>(config.LargeOutput);
        Assert.True(largeOutput.Enabled);
        Assert.Equal(expectedDirectory, largeOutput.OutputDirectory);
        Assert.True(config.McpServers is null or { Count: 0 });
        Assert.False(config.EnableSkills);
#pragma warning disable GHCP001
        var permissionHandler = Assert.IsType<Func<PermissionRequest, PermissionInvocation, Task<GitHub.Copilot.Rpc.PermissionDecision>>>(config.OnPermissionRequest);
        var decision = await permissionHandler(
            new PermissionRequestRead { Intention = "synthetic", Path = expectedDirectory },
            new PermissionInvocation { SessionId = "synthetic-session" });
        Assert.Equal("user-not-available", decision.Kind);
#pragma warning restore GHCP001
    }

    private static ValueTask<MonitorAnalysisToolData> CreateToolDataAsync(MonitorAnalysisFocus focus) =>
        MonitorAnalysisToolData.CreateAsync(
                new EmptyProjectionStore(),
                new MonitorAnalysisContext(
                    7,
                    "trace-anchor",
                    RawRecordId: null,
                    SpanId: null,
                    focus,
                    OperationToken: new MonitorAnalysisOperationToken([1])),
                CancellationToken.None);

    private sealed class EmptyProjectionStore : ProjectionStoreTestDouble;

    private sealed class FakeAnalysisScope(string childDirectory) : IAnalysisSdkDirectoryScope
    {
        public string ChildDirectory { get; } = childDirectory;
        public CancellationToken LeaseLostToken => CancellationToken.None;
        public bool IsLeaseLost => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OwnedFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), $"cao-executor-{Guid.NewGuid():N}");
        private readonly SkillDiscoveryRootPreflightResultV1 preflight;

        private readonly CancellationTokenSource hostStopping = new();

        internal OwnedFixture(OwnedFailure failure = OwnedFailure.None, OwnedExecutionControl? control = null)
        {
            Directory.CreateDirectory(directory);
            var skillDirectory = Path.Combine(directory, "retained");
            Directory.CreateDirectory(skillDirectory);
            SkillPath = Path.Combine(skillDirectory, "SKILL.md");
            File.WriteAllText(SkillPath, SkillContent);
            Order = [];
            Scope = new CountingAnalysisScope(directory, Order);
            Control = control;
            var gate = new SkillHostShutdownGateV1();
            preflight = SkillDiscoveryRootPreflightV1.Run([], [directory],
                new CertifiedDiscoveryPlatformV1(SkillProducerPathKeyPlatform.Windows, new WindowsDiscoveryRootOpenerV1()));
            Assert.Equal(SkillDiscoveryRootPreflightOutcomeV1.Certified, preflight.Outcome);
            RootGeneration = new SkillDiscoveryRootGenerationV1(preflight, gate);
            Admission = new CopilotRuntimeAdmissionV1(gate);
            Client = new FakeOwnedClient(failure, SkillPath, SkillContent, Order, control);
            Queue = new SessionEventQueue();
            Transport = new RecordingTransport(failure == OwnedFailure.ImportTransportRefused, Order, control);
            var bridge = new SkillRuntimeCapabilityBridgeV1(Admission, Transport, () => 0, () => new byte[32]);
            Transport.Bridge = bridge;
            ICurrentSkillNativeFileReaderV1 nativeReader = control?.NativeReadPhase is null
                ? new WindowsCurrentSkillFileReaderV1()
                : new BlockingNativeReader(new WindowsCurrentSkillFileReaderV1(), control);
            Context = new(Scope, RootGeneration, nativeReader, Admission, bridge, Queue,
                TimeSpan.FromSeconds(1), _ => Client, _ => false, hostStopping.Token);
        }

        internal CountingAnalysisScope Scope { get; }
        internal SkillDiscoveryRootGenerationV1 RootGeneration { get; }
        internal CopilotRuntimeAdmissionV1 Admission { get; }
        internal FakeOwnedClient Client { get; }
        internal SessionEventQueue Queue { get; }
        internal RecordingTransport Transport { get; }
        internal List<string> Order { get; }
        internal OwnedExecutionControl? Control { get; }
        internal string SkillPath { get; }
        internal const string SkillContent = "---\nname: retained\ndescription: retained description\n---\nbody\n";
        internal CopilotAnalysisRootsExecutionContext Context { get; }

        internal void StopHost() => hostStopping.Cancel();

        public void Dispose()
        {
            preflight.Dispose();
            hostStopping.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private sealed class CountingAnalysisScope(string childDirectory, List<string> order) : IAnalysisSdkDirectoryScope
    {
        private readonly CancellationTokenSource leaseLost = new();
        public string ChildDirectory { get; } = childDirectory;
        public CancellationToken LeaseLostToken => leaseLost.Token;
        public bool IsLeaseLost => leaseLost.IsCancellationRequested;
        public int DisposeCalls { get; private set; }
        public void LoseLease() => leaseLost.Cancel();
        public ValueTask DisposeAsync() { DisposeCalls++; order.Add("scope.dispose"); leaseLost.Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class FakeOwnedClient(OwnedFailure failure, string skillPath, string skillContent, List<string> order,
        OwnedExecutionControl? control) : IOwnedCopilotClientV1, ICopilotSkillRuntimeClient
    {
        public List<SessionConfig> Configs { get; } = [];
        public List<FakeOwnedSession> Sessions { get; } = [];
        public int DisposeCalls { get; private set; }
        public ICopilotSkillRuntimeClient RuntimeClient => this;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CopilotRuntimeStatusObservationV1?>(failure == OwnedFailure.Status ? null : new("1.0.65", 3, null));
        public Task<IOwnedCopilotSessionV1> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            Assert.NotNull(config.OnEvent);
            if (Sessions.Count != 0) Sessions[^1].DisposedBeforeNextCreate = Sessions[^1].DisposeCalls == 1;
            if (Sessions.Count == 1 && failure == OwnedFailure.ExecutionCreate)
                throw new InvalidOperationException("execution create");
            Configs.Add(config);
            var session = new FakeOwnedSession($"session-{Sessions.Count}", config, Sessions.Count == 1, failure, skillPath, skillContent, order, control);
            Sessions.Add(session);
            return Task.FromResult<IOwnedCopilotSessionV1>(session);
        }
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(IReadOnlyList<string> projectPaths, IReadOnlyList<string> skillDirectories, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() { DisposeCalls++; order.Add("client.dispose"); return ValueTask.CompletedTask; }
    }

    private sealed class FakeOwnedSession(string sessionId, SessionConfig config, bool execution, OwnedFailure failure,
        string skillPath, string skillContent, List<string> order, OwnedExecutionControl? control) : IOwnedCopilotSessionV1
    {
        public string SessionId { get; } = sessionId;
        public string? Prompt { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool DisposedBeforeNextCreate { get; set; }
        public CancellationToken LastOperationToken { get; private set; }
        public async Task EnsureSkillsLoadedAsync(CancellationToken cancellationToken)
        {
            LastOperationToken = cancellationToken;
            config.OnEvent!(CreateStartEvent());
            if (!execution && failure == OwnedFailure.ProbeUnexpectedRelevantEvent)
                config.OnEvent!(CreateTerminalEvent(success: true));
            if (!execution && control?.Phase == OwnedExecutionPhase.Probe)
                await control.Barrier.WaitForCancellationAsync(cancellationToken);
        }
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> ListSkillsAsync(CancellationToken cancellationToken) =>
            CaptureListToken(cancellationToken);

        private Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> CaptureListToken(CancellationToken cancellationToken)
        {
            LastOperationToken = cancellationToken;
            return Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>(
                failure is OwnedFailure.OneInvocation or OwnedFailure.ImportTransportRefused || control is not null
                    ? [new("retained", "custom", skillPath, null, "retained description", null, true, true)]
                    : []);
        }
        public async Task SendAndWaitAsync(string prompt, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Prompt = prompt;
            LastOperationToken = cancellationToken;
            Assert.True(execution);
            if (failure == OwnedFailure.ExecutionSend) throw new InvalidOperationException("execution send");
            if (control?.Phase == OwnedExecutionPhase.Execution)
                await control.Barrier.WaitForCancellationAsync(cancellationToken);
            if (failure is OwnedFailure.OneInvocation or OwnedFailure.ImportTransportRefused || control is not null)
                config.OnEvent!(CreateInvocationEvent());
            config.OnEvent!(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "message", Content = "result" } });
            config.OnEvent!(CreateTerminalEvent(failure != OwnedFailure.ExecutionUnsuccessfulTerminal));
        }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            if (execution) order.Add("execution.dispose");
            if (execution && control?.EmitRelevantDuringExecutionDispose == true)
                config.OnEvent!(CreateInvocationEvent());
            return ValueTask.CompletedTask;
        }

        internal void EmitLateRelevantEvent() => config.OnEvent!(CreateInvocationEvent());

        internal void EmitLateProbeEvent(LateProbeEvent lateEvent) => config.OnEvent!(lateEvent switch
        {
            LateProbeEvent.DuplicateMatchingStart => CreateStartEvent(),
            LateProbeEvent.DriftedStart => CreateStartEvent("drifted-session"),
            LateProbeEvent.SkillInvoked => CreateInvocationEvent(),
            LateProbeEvent.Terminal => CreateTerminalEvent(success: true),
            LateProbeEvent.Failure => new SessionErrorEvent { Data = new SessionErrorData { ErrorType = "synthetic", Message = "synthetic" } },
            _ => throw new ArgumentOutOfRangeException(nameof(lateEvent)),
        });

        private SessionStartEvent CreateStartEvent(string? callbackSessionId = null) => new()
        {
            Id = Guid.Parse("11111111-1111-4111-8111-111111111111"),
            Timestamp = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            Data = new SessionStartData { SessionId = callbackSessionId ?? SessionId, CopilotVersion = "1.0.65", Producer = "copilot",
                StartTime = DateTimeOffset.Parse("2026-01-02T03:04:05Z"), Version = 1 }
        };

        private SkillInvokedEvent CreateInvocationEvent() => new()
        {
            Id = Guid.Parse("22222222-2222-4222-8222-222222222222"),
            Timestamp = DateTimeOffset.Parse("2026-01-02T03:04:06Z"),
            Data = new SkillInvokedData { Name = "retained", Source = "custom", Path = skillPath,
                Content = skillContent, Description = "retained description" }
        };

        private static SessionTaskCompleteEvent CreateTerminalEvent(bool success) => new()
        {
            Id = Guid.Parse("33333333-3333-4333-8333-333333333333"),
            Timestamp = DateTimeOffset.Parse("2026-01-02T03:04:07Z"),
            Data = new SessionTaskCompleteData { Success = success }
        };
    }

    private sealed class RecordingTransport(bool refuse, List<string> order, OwnedExecutionControl? control = null) : ISkillRuntimeBridgeTransport
    {
        public List<byte[]> Bodies { get; } = [];
        public SkillRuntimeCapabilityBridgeV1? Bridge { get; set; }
        public CopilotRuntimeGenerationV1? ConsumedCandidate { get; private set; }
        public async Task<bool> SendAsync(string capabilityToken, ReadOnlyMemory<byte> bodyUtf8, CancellationToken cancellationToken)
        {
            order.Add("v2");
            Bodies.Add(bodyUtf8.ToArray());
            if (control?.Phase == OwnedExecutionPhase.Import)
                await control.Barrier.WaitForCancellationAsync(cancellationToken);
            if (refuse) return false;
            Assert.True(Bridge!.TryConsume(capabilityToken, out var transfer));
            Assert.Equal(bodyUtf8.Length, transfer!.ExpectedBodyLength);
            Assert.Equal(SHA256.HashData(bodyUtf8.Span), transfer.ExpectedBodySha256);
            Assert.Equal(SkillInvocationV2TestIdentity.V1065, transfer.RuntimeCapability.CertifiedIdentity);
            ConsumedCandidate = Assert.IsType<CopilotRuntimeOperationCapabilityV1>(transfer.RuntimeCapability).Owner;
            transfer.ReleaseTransferredCapability();
            transfer.ReleaseTransferredCapability();
            return true;
        }
    }

    private sealed class OwnedExecutionControl(
        OwnedExecutionPhase phase,
        bool emitRelevantDuringExecutionDispose = false,
        NativeReadPhase? nativeReadPhase = null)
    {
        internal OwnedExecutionPhase Phase { get; } = phase;
        internal bool EmitRelevantDuringExecutionDispose { get; } = emitRelevantDuringExecutionDispose;
        internal NativeReadPhase? NativeReadPhase { get; } = nativeReadPhase;
        internal CancellationBarrier Barrier { get; } = new();
        internal SynchronousCancellationBarrier NativeReadBarrier { get; } = new();
        internal bool NativeReadTokenCanBeCanceled { get; set; }
        internal bool NativeReadTokenWasCanceled { get; set; }
        internal int OutstandingBarriers => Barrier.Outstanding;
    }

    private sealed class SynchronousCancellationBarrier
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEvent released = new(false);
        private int outstanding;
        internal Task Entered => entered.Task;
        internal int Outstanding => Volatile.Read(ref outstanding);
        internal void Release() => released.Set();

        internal void Wait(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref outstanding);
            entered.TrySetResult();
            try { WaitHandle.WaitAny([cancellationToken.WaitHandle, released]); }
            finally { Interlocked.Decrement(ref outstanding); }
        }
    }

    private sealed class BlockingNativeReader(
        ICurrentSkillNativeFileReaderV1 inner,
        OwnedExecutionControl control) : ICurrentSkillNativeFileReaderV1
    {
        private int calls;

        public CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            var expected = control.NativeReadPhase switch
            {
                NativeReadPhase.ProbeInventory => 1,
                NativeReadPhase.ExecutionInventory => 2,
                NativeReadPhase.CallbackReproof => 3,
                _ => int.MaxValue,
            };
            if (call == expected)
            {
                control.NativeReadTokenCanBeCanceled = cancellationToken.CanBeCanceled;
                control.NativeReadBarrier.Wait(cancellationToken);
                control.NativeReadTokenWasCanceled = cancellationToken.IsCancellationRequested;
                cancellationToken.ThrowIfCancellationRequested();
            }
            return inner.Read(target, cancellationToken);
        }
    }

    private sealed class CancellationBarrier
    {
        private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int outstanding;
        internal Task Entered => entered.Task;
        internal int Outstanding => Volatile.Read(ref outstanding);
        internal void Release() => released.TrySetResult();

        internal async Task WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref outstanding);
            entered.TrySetResult();
            try { await released.Task.WaitAsync(cancellationToken); }
            finally { Interlocked.Decrement(ref outstanding); }
        }
    }

    private sealed class UnusedNativeReader : ICurrentSkillNativeFileReaderV1
    {
        public CurrentSkillNativeReadResultV1 Read(CurrentSkillReadTargetV1 target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No retained skill should be read.");
    }
}

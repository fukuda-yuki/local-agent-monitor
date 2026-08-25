using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using GitHub.Copilot;
using System.Text;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class DotNetCopilotRawAnalysisRunnerTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 7, 19, 1, 2, 3, TimeSpan.Zero);

    [Fact]
    public async Task StartAsync_AlreadyStoppedHost_CancelsQueuedRunWithoutAnySideEffect()
    {
        using var temp = new MonitorTempDirectory();
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var store = new FakeStore(Run());
        var owner = new FakeOwner(new FakeScope("owned-child"));
        var executor = new FakeExecutor();
        var runner = CreateRunner(temp, store, owner, executor, hostStoppingToken: stopping.Token);

        await runner.StartAsync(Context(), CancellationToken.None);
        await store.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(MonitorAnalysisStatus.Canceled, store.FinishedStatus);
        Assert.Equal(0, store.MarkRunningCount);
        Assert.Equal(0, owner.OpenCount);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task RunAsync_HostStopsAfterDispatchClaim_CancelsBeforeRunningSideEffects()
    {
        using var temp = new MonitorTempDirectory();
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var store = new FakeStore(Run());
        var owner = new FakeOwner(new FakeScope("owned-child"));
        var executor = new FakeExecutor();

        await CreateRunner(temp, store, owner, executor).RunAsync(Context(), stopping.Token);

        Assert.Equal(MonitorAnalysisStatus.Canceled, store.FinishedStatus);
        Assert.Equal(0, store.MarkRunningCount);
        Assert.Equal(0, owner.OpenCount);
        Assert.Equal(0, executor.CallCount);
    }

    [Fact]
    public async Task RunAsync_UsesExactPersistedIdentityAndOwnedChildDirectory()
    {
        using var temp = new MonitorTempDirectory();
        var store = new FakeStore(Run());
        var scope = new FakeScope("owned-child");
        var owner = new FakeOwner(scope);
        var executor = new FakeExecutor { BeforeReturn = () => Assert.Equal(0, scope.DisposeCount) };
        var runner = CreateRunner(temp, store, owner, executor);

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(RequestedAt, owner.RequestedAt);
        Assert.Equal("configured-parent", owner.ConfiguredParent);
        Assert.Equal("owned-child", executor.ChildDirectory);
        Assert.Equal(1, store.CompleteCount);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("mismatch")]
    public async Task RunAsync_InvalidPersistedIdentity_DoesNotOpenOwnerOrStartExecutor(string kind)
    {
        using var temp = new MonitorTempDirectory();
        var run = kind == "missing" ? null : Run(RequestedAtText: kind == "malformed" ? "not-a-timestamp" : RequestedAt.ToString("O"), TraceId: kind == "mismatch" ? "other" : "trace");
        var store = new FakeStore(run);
        var owner = new FakeOwner(new FakeScope("owned-child"));
        var executor = new FakeExecutor();

        await CreateRunner(temp, store, owner, executor).RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, owner.OpenCount);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal("Local analysis ownership could not be established.", store.FinishedMessage);
    }

    [Fact]
    public async Task RunAsync_OwnerFailure_IsFixedAndDoesNotLeakConfiguredPath()
    {
        using var temp = new MonitorTempDirectory();
        var store = new FakeStore(Run());
        var owner = new FakeOwner(new InvalidOperationException("C:\\secret\\configured-parent"));
        var executor = new FakeExecutor();

        await CreateRunner(temp, store, owner, executor).RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, executor.CallCount);
        Assert.Equal("Local analysis ownership could not be established.", store.FinishedMessage);
        Assert.DoesNotContain("configured-parent", store.FinishedMessage!);
    }

    [Fact]
    public async Task RunAsync_LeaseLossPreventsSuccessfulCompletionAndDisposesScopeOnce()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child");
        var store = new FakeStore(Run());
        var owner = new FakeOwner(scope);
        var executor = new FakeExecutor { BeforeReturn = scope.LoseLease };

        await CreateRunner(temp, store, owner, executor).RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, store.CompleteCount);
        Assert.Equal("Local analysis ownership could not be established.", store.FinishedMessage);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_PrimaryExecutorFailureIsFixedAndNotMaskedByScopeDisposal()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child") { DisposeException = new InvalidOperationException("dispose") };
        var executor = new FakeExecutor { Exception = new InvalidOperationException("C:\\private\\owned-child raw-prompt") };
        var store = new FakeStore(Run());

        await CreateRunner(temp, store, new FakeOwner(scope), executor).RunAsync(Context(), CancellationToken.None);

        Assert.Equal("SDK analysis failed.", store.FinishedMessage);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_LeaseCancellationIsOwnershipFailureRatherThanCanceled()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child");
        var executor = new FakeExecutor { CancelLeaseBeforeWaiting = scope.LoseLease };
        var store = new FakeStore(Run());

        await CreateRunner(temp, store, new FakeOwner(scope), executor).RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(MonitorAnalysisStatus.Failed, store.FinishedStatus);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Equal("Local analysis ownership could not be established.", store.FinishedMessage);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_ScopeDisposalFailurePreventsCompletionAndDoesNotLeakPath()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child") { DisposeException = new InvalidOperationException("C:\\private\\owned-child") };
        var store = new FakeStore(Run());

        await CreateRunner(temp, store, new FakeOwner(scope), new FakeExecutor()).RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, store.CompleteCount);
        Assert.Equal("Local analysis ownership could not be established.", store.FinishedMessage);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_NoncanonicalRequestedAtFailsBeforeOwner()
    {
        using var temp = new MonitorTempDirectory();
        var noncanonical = RequestedAt.ToOffset(TimeSpan.FromHours(9)).ToString("O");
        var store = new FakeStore(Run(RequestedAtText: noncanonical));
        var owner = new FakeOwner(new FakeScope("owned-child"));

        await CreateRunner(temp, store, owner, new FakeExecutor()).RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, owner.OpenCount);
        Assert.Equal("Local analysis ownership could not be established.", store.FinishedMessage);
    }

    [Fact]
    public async Task LegacyConstructor_RejectsExecutionBeforeAnySdkOrSharedDirectoryUse()
    {
        using var temp = new MonitorTempDirectory();
        var store = new FakeStore(Run());
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var sharedRoot = Path.Combine(temp.Path, "shared-root");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = sharedRoot }).Build();
        var runner = new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw), configuration);

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal("Local analysis ownership could not be established.", store.FinishedMessage);
        Assert.False(Directory.Exists(sharedRoot));
    }

    [Fact]
    public async Task RunAsync_RealCatalogOwnerCreatesOwnedChildReleasesLeaseThenCompletes()
    {
        using var temp = new MonitorTempDirectory { TimeProvider = new MutableTimeProvider(RequestedAt.AddMinutes(1)) };
        var analysisStore = new SqliteMonitorAnalysisStore(temp.DatabasePath, temp.RetentionContext, temp.TimeProvider);
        analysisStore.CreateSchema();
        var start = analysisStore.StartRun("trace", null, "span", MonitorAnalysisFocus.Errors, RequestedAt);
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var parent = Path.Combine(temp.Path, "sdk-parent");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = parent }).Build();
        var executor = new FakeExecutor();
        var catalog = new RetentionCatalogStore(temp.RetentionContext);
        var runner = new DotNetCopilotRawAnalysisRunner(analysisStore, new RawTelemetryStoreProjectionStore(raw), configuration, new AnalysisSdkDirectoryOwner(catalog, temp.TimeProvider), executor, temp.TimeProvider);
        var context = new MonitorAnalysisContext(start.RunId, "trace", null, "span", MonitorAnalysisFocus.Errors, OperationToken: start.OperationToken);

        await runner.RunAsync(context, CancellationToken.None);

        Assert.NotNull(executor.ChildDirectory);
        Assert.NotEqual(parent, executor.ChildDirectory);
        Assert.True(Directory.Exists(executor.ChildDirectory!));
        Assert.Equal(MonitorAnalysisStatus.Succeeded, analysisStore.GetRun(start.RunId)!.Status);
        using var connection = new SqliteConnection($"Data Source={temp.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM retention_leases WHERE lease_kind='operation';";
        Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        command.CommandText = "SELECT COUNT(*) FROM retention_items WHERE store_kind='analysis_sdk_directory';";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public async Task RunAsync_InstructionDiagnosis_CompletesWithMachineReadableZeroFindingHandoff()
    {
        using var temp = new MonitorTempDirectory();
        var store = new FakeStore(Run(Focus: MonitorAnalysisFocus.InstructionDiagnosis));
        var runner = CreateRunner(temp, store, new FakeOwner(new FakeScope("owned-child")), new FakeExecutor());
        var context = new MonitorAnalysisContext(
            7,
            "trace",
            null,
            "span",
            MonitorAnalysisFocus.InstructionDiagnosis,
            OperationToken: new MonitorAnalysisOperationToken([1]));

        await runner.RunAsync(context, CancellationToken.None);

        var handoff = Assert.IsType<InstructionFindingHandoffV1>(store.CompletedHandoff);
        Assert.Equal(7, handoff.AnalysisRunId);
        Assert.Empty(handoff.Findings);
        Assert.Empty(handoff.Candidates);
    }

    [Fact]
    public async Task RunAsync_RootsFactory_SelectsRootsOverloadWithTheOpenedScope()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child");
        var executor = new FakeExecutor();
        CopilotAnalysisRootsExecutionContext? supplied = null;
        var runner = CreateRunner(temp, new FakeStore(Run()), new FakeOwner(scope), executor,
            openedScope => supplied = CreateRootsContext(openedScope));

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, executor.LegacyCallCount);
        Assert.Equal(1, executor.RootsCallCount);
        Assert.Same(scope, executor.RootsContext!.AnalysisScope);
        Assert.NotSame(supplied, executor.RootsContext);
        Assert.NotNull(executor.RootsContext.ScopeOwnership);
    }

    [Fact]
    public async Task RunAsync_PublicationRefusalBeforeDurableCompletion_FinishesFailed()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child");
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var ownership = new AnalysisSdkScopeOwnership(scope);
        Assert.True(ownership.TryTransferToExecutor());
        Assert.True(ownership.TryTransferToCandidate());
        var candidate = admission.CreateUnpublishedCandidate(new FakeRuntimeClient(), SkillInvocationV2TestIdentity.V1065, scope, ownership);
        var observations = new List<OwnedSessionExecutionEvidenceV1>();
        var checkpoints = new List<OwnedSessionExecutionCheckpointV1>();
        var executor = new FakeExecutor { Result = new("done", candidate, Evidence()) };
        var store = new FakeStore(Run());
        admission.CloseForShutdown();
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build();
        var runner = new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw), configuration,
            new FakeOwner(scope), executor, temp.TimeProvider, skillRuntimeAdmission: admission,
            rootsExecutionContextFactory: openedScope => CreateRootsContext(openedScope) with { Admission = admission, ScopeOwnership = ownership, ExecutionEvidenceObserver = observations.Add, ExecutionCheckpointObserver = checkpoints.Add });

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(MonitorAnalysisStatus.Failed, store.FinishedStatus);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Empty(observations);
        Assert.Empty(checkpoints);
    }

    [Fact]
    public async Task RunAsync_PublicationRefusalDoesNotEmitPublishedCheckpoint()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child") { DisposeException = new InvalidOperationException("synthetic") };
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var ownership = new AnalysisSdkScopeOwnership(scope);
        Assert.True(ownership.TryTransferToExecutor());
        Assert.True(ownership.TryTransferToCandidate());
        var candidate = admission.CreateUnpublishedCandidate(new FakeRuntimeClient(), SkillInvocationV2TestIdentity.V1065, scope, ownership);
        var executor = new FakeExecutor { Result = new("done", candidate, Evidence()) };
        admission.CloseForShutdown();
        var checkpoints = new List<OwnedSessionExecutionCheckpointV1>();
        var store = new FakeStore(Run());
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build();
        var runner = new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw), configuration,
            new FakeOwner(scope), executor, temp.TimeProvider, skillRuntimeAdmission: admission,
            rootsExecutionContextFactory: openedScope => CreateRootsContext(openedScope) with { Admission = admission, ScopeOwnership = ownership, ExecutionCheckpointObserver = checkpoints.Add });

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Empty(checkpoints);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(MonitorAnalysisStatus.Failed, store.FinishedStatus);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_PublishedEvidenceNotifiesOnceAndObserverFailureCannotUndoPublication(bool observerThrows)
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child");
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var ownership = new AnalysisSdkScopeOwnership(scope);
        Assert.True(ownership.TryTransferToExecutor());
        Assert.True(ownership.TryTransferToCandidate());
        var candidate = admission.CreateUnpublishedCandidate(new FakeRuntimeClient(), SkillInvocationV2TestIdentity.V1065, scope, ownership);
        Assert.True(candidate.TryMarkReady());
        var executor = new FakeExecutor { Result = new("done", candidate, Evidence()) };
        var calls = 0;
        var checkpoints = new List<OwnedSessionExecutionCheckpointV1>();
        var store = new FakeStore(Run());
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build();
        var runner = new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw), configuration,
            new FakeOwner(scope), executor, temp.TimeProvider, skillRuntimeAdmission: admission,
            rootsExecutionContextFactory: openedScope => CreateRootsContext(openedScope) with
            {
                Admission = admission,
                ScopeOwnership = ownership,
                ExecutionEvidenceObserver = _ => { calls++; if (observerThrows) throw new InvalidOperationException("test"); },
                ExecutionCheckpointObserver = checkpoint => { checkpoints.Add(checkpoint); if (observerThrows) throw new InvalidOperationException("test"); },
            });

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal([OwnedSessionExecutionCheckpointV1.CandidatePublished], checkpoints);
        Assert.True(admission.TryGetCurrentAdmittedGeneration(out var current));
        Assert.Same(candidate, current);
        Assert.Equal(1, store.CompleteCount);
        Assert.Null(store.FinishedStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_DurableCompletionFailureAfterReservation_AbortsCandidateAndKeepsCurrent(
        bool instructionDiagnosis)
    {
        var focus = instructionDiagnosis ? MonitorAnalysisFocus.InstructionDiagnosis : MonitorAnalysisFocus.Errors;
        using var temp = new MonitorTempDirectory();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var prior = admission.PublishReadyTestCandidate(new FakeRuntimeClient(), out _);
        var scope = new FakeScope("owned-child");
        var ownership = new AnalysisSdkScopeOwnership(scope);
        Assert.True(ownership.TryTransferToExecutor());
        Assert.True(ownership.TryTransferToCandidate());
        var candidate = admission.CreateUnpublishedCandidate(new FakeRuntimeClient(),
            SkillInvocationV2TestIdentity.V1065, scope, ownership);
        Assert.True(candidate.TryMarkReady());
        var observations = new List<OwnedSessionExecutionEvidenceV1>();
        var checkpoints = new List<OwnedSessionExecutionCheckpointV1>();
        var store = new FakeStore(Run(Focus: focus)) { CompleteException = new InvalidOperationException("complete") };
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var runner = new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build(),
            new FakeOwner(scope), new FakeExecutor { Result = new("done", candidate, Evidence()) }, temp.TimeProvider,
            skillRuntimeAdmission: admission,
            rootsExecutionContextFactory: opened => CreateRootsContext(opened) with
            {
                Admission = admission,
                ScopeOwnership = ownership,
                ExecutionEvidenceObserver = observations.Add,
                ExecutionCheckpointObserver = checkpoints.Add,
            });
        var context = Context() with { Focus = focus };

        await runner.RunAsync(context, CancellationToken.None);

        Assert.Equal(1, store.CompleteCount);
        Assert.Equal(MonitorAnalysisStatus.Failed, store.FinishedStatus);
        Assert.Same(prior, Assert.IsType<CopilotRuntimeGenerationV1>(
            admission.TryGetCurrentAdmittedGeneration(out var current) ? current : null));
        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Empty(observations);
        Assert.Empty(checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_AuthorityCancellationAfterReservedTransition_PreventsDurableCompletion(
        bool loseLease)
    {
        using var temp = new MonitorTempDirectory();
        using var callerCancellation = new CancellationTokenSource();
        FakeScope? scope = null;
        Thread? cancellationThread = null;
        Exception? cancellationException = null;
        var reservationTransitions = 0;
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1(), () =>
        {
            if (Interlocked.Increment(ref reservationTransitions) == 1) return;
            cancellationThread = new Thread(() =>
            {
                try
                {
                    if (loseLease) scope!.LoseLease();
                    else callerCancellation.Cancel();
                }
                catch (Exception exception) { cancellationException = exception; }
            });
            cancellationThread.IsBackground = true;
            cancellationThread.Start();
            var authorityToken = loseLease ? scope!.LeaseLostToken : callerCancellation.Token;
            Assert.True(authorityToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
        });
        var prior = admission.PublishReadyTestCandidate(new FakeRuntimeClient(), out _);
        scope = new FakeScope("owned-child");
        var ownership = new AnalysisSdkScopeOwnership(scope);
        Assert.True(ownership.TryTransferToExecutor());
        Assert.True(ownership.TryTransferToCandidate());
        var candidate = admission.CreateUnpublishedCandidate(new FakeRuntimeClient(),
            SkillInvocationV2TestIdentity.V1065, scope, ownership);
        Assert.True(candidate.TryMarkReady());
        var observations = new List<OwnedSessionExecutionEvidenceV1>();
        var checkpoints = new List<OwnedSessionExecutionCheckpointV1>();
        var store = new FakeStore(Run());
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var runner = new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build(),
            new FakeOwner(scope), new FakeExecutor { Result = new("done", candidate, Evidence()) }, temp.TimeProvider,
            skillRuntimeAdmission: admission,
            rootsExecutionContextFactory: opened => CreateRootsContext(opened) with
            {
                Admission = admission,
                ScopeOwnership = ownership,
                ExecutionEvidenceObserver = observations.Add,
                ExecutionCheckpointObserver = checkpoints.Add,
            });

        var cancellationJoined = false;
        try { await runner.RunAsync(Context(), callerCancellation.Token); }
        finally
        {
            cancellationJoined = cancellationThread?.Join(TimeSpan.FromSeconds(5)) ?? true;
        }

        Assert.True(cancellationJoined);
        Assert.Null(cancellationException);
        Assert.Equal(0, store.CompleteCount);
        Assert.NotEqual(MonitorAnalysisStatus.Succeeded, store.FinishedStatus);
        Assert.Same(prior, Assert.IsType<CopilotRuntimeGenerationV1>(
            admission.TryGetCurrentAdmittedGeneration(out var current) ? current : null));
        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Empty(observations);
        Assert.Empty(checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_QueuedReadyCandidateCancellation_PreventsDurableCompletion(bool loseLease)
    {
        using var temp = new MonitorTempDirectory();
        using var callerCancellation = new CancellationTokenSource();
        var admission = new CopilotRuntimeAdmissionV1(new SkillHostShutdownGateV1());
        var prior = admission.PublishReadyTestCandidate(new FakeRuntimeClient(), out _);
        var blockerScope = new FakeScope("blocker");
        var blocker = admission.CreateUnpublishedCandidate(new FakeRuntimeClient(),
            SkillInvocationV2TestIdentity.V1065, blockerScope);
        Assert.True(blocker.TryMarkReady());
        await using var blockerReservation = Assert.IsType<CopilotRuntimeAdmissionV1.PublicationReservation>(
            await admission.TryReservePublicationAsync(blocker));
        var scope = new FakeScope("owned-child");
        var ownership = new AnalysisSdkScopeOwnership(scope);
        Assert.True(ownership.TryTransferToExecutor());
        Assert.True(ownership.TryTransferToCandidate());
        var candidate = admission.CreateUnpublishedCandidate(new FakeRuntimeClient(),
            SkillInvocationV2TestIdentity.V1065, scope, ownership);
        Assert.True(candidate.TryMarkReady());
        var observations = new List<OwnedSessionExecutionEvidenceV1>();
        var checkpoints = new List<OwnedSessionExecutionCheckpointV1>();
        var store = new FakeStore(Run());
        var executor = new FakeExecutor { Result = new("done", candidate, Evidence()) };
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var runner = new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build(),
            new FakeOwner(scope), executor, temp.TimeProvider, skillRuntimeAdmission: admission,
            rootsExecutionContextFactory: opened => CreateRootsContext(opened) with
            {
                Admission = admission,
                ScopeOwnership = ownership,
                ExecutionEvidenceObserver = observations.Add,
                ExecutionCheckpointObserver = checkpoints.Add,
            });

        var running = runner.RunAsync(Context(), callerCancellation.Token);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (loseLease) scope.LoseLease();
        else callerCancellation.Cancel();
        await blockerReservation.DisposeAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, store.CompleteCount);
        Assert.NotEqual(MonitorAnalysisStatus.Succeeded, store.FinishedStatus);
        Assert.Same(prior, Assert.IsType<CopilotRuntimeGenerationV1>(
            admission.TryGetCurrentAdmittedGeneration(out var current) ? current : null));
        Assert.True(candidate.IsInvalid);
        Assert.Equal(1, scope.DisposeCount);
        Assert.Empty(observations);
        Assert.Empty(checkpoints);
    }

    [Fact]
    public async Task RunAsync_EvidenceWithoutCandidateDoesNotNotify()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child");
        var executor = new FakeExecutor { Result = new("done", ExecutionEvidence: Evidence()) };
        var calls = 0;
        var store = new FakeStore(Run());
        var runner = CreateRunner(temp, store, new FakeOwner(scope), executor,
            opened => CreateRootsContext(opened) with { ExecutionEvidenceObserver = _ => calls++ });

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, calls);
        Assert.Equal(1, store.CompleteCount);
    }

    [Fact]
    public async Task RunAsync_HostStopDuringExecution_CancelsAndDisposesScopeExactlyOnce()
    {
        using var temp = new MonitorTempDirectory();
        using var stopping = new CancellationTokenSource();
        var scope = new FakeScope("owned-child");
        var executor = new FakeExecutor { WaitForCancellation = true };
        var store = new FakeStore(Run());
        var runner = CreateRunner(temp, store, new FakeOwner(scope), executor,
            opened => CreateRootsContext(opened), stopping.Token);

        var running = runner.RunAsync(Context(), stopping.Token);
        await executor.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await running;

        Assert.Equal(MonitorAnalysisStatus.Canceled, store.FinishedStatus);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_CallbackPoisonCancelsExactCapabilityWorkAndPersistsCanceledWithoutAuthorityCancellation()
    {
        using var temp = new MonitorTempDirectory();
        using var outer = new CancellationTokenSource();
        using var host = new CancellationTokenSource();
        var scope = new FakeScope("owned-child");
        var store = new FakeStore(Run());
        var executor = new CallbackPoisonExecutor();
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var runner = new DotNetCopilotRawAnalysisRunner(
            store, new RawTelemetryStoreProjectionStore(raw),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build(),
            new FakeOwner(scope), executor, temp.TimeProvider, host.Token,
            rootsExecutionContextFactory: opened => CreateRootsContext(opened));
        await runner.RunAsync(Context(), outer.Token);
        await scope.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(executor.WorkTokenCanBeCanceled);
        Assert.True(executor.WorkTokenWasCanceled);
        Assert.True(executor.ObservedOperationCanceledException);
        Assert.False(outer.IsCancellationRequested);
        Assert.False(host.IsCancellationRequested);
        Assert.False(scope.LeaseLostToken.IsCancellationRequested);
        Assert.Equal(MonitorAnalysisStatus.Canceled, store.FinishedStatus);
        Assert.Equal(1, executor.Invalidations);
        Assert.Equal(1, scope.DisposeCount);
    }

    [Fact]
    public async Task RunAsync_RootsPresentAndRawAnalysisDisabledHasNoSdkOrDirectorySideEffect()
    {
        using var temp = new MonitorTempDirectory();
        var scope = new FakeScope("owned-child");
        var owner = new FakeOwner(scope);
        var executor = new FakeExecutor();
        var store = new FakeStore(Run());
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CopilotAnalysis:Enabled"] = "false",
            ["CopilotAnalysis:BaseDirectory"] = "configured-parent",
        }).Build();
        var runner = new DotNetCopilotRawAnalysisRunner(
            store, new RawTelemetryStoreProjectionStore(raw), configuration, owner, executor,
            temp.TimeProvider, rootsExecutionContextFactory: opened => CreateRootsContext(opened));

        await runner.RunAsync(Context(), CancellationToken.None);

        Assert.Equal(0, owner.OpenCount);
        Assert.Equal(0, executor.CallCount);
        Assert.Equal(0, scope.DisposeCount);
        Assert.Equal(0, store.CompleteCount);
        Assert.Equal(MonitorAnalysisStatus.Failed, store.FinishedStatus);
        Assert.Equal("SDK analysis failed.", store.FinishedMessage);
    }

    private static DotNetCopilotRawAnalysisRunner CreateRunner(MonitorTempDirectory temp, FakeStore store, FakeOwner owner, FakeExecutor executor,
        Func<IAnalysisSdkDirectoryScope, CopilotAnalysisRootsExecutionContext>? rootsFactory = null,
        CancellationToken hostStoppingToken = default)
    {
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build();
        return new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw), configuration, owner, executor, temp.TimeProvider, hostStoppingToken,
            rootsExecutionContextFactory: rootsFactory);
    }

    private static CopilotAnalysisRootsExecutionContext CreateRootsContext(IAnalysisSdkDirectoryScope scope)
    {
        var shutdownGate = new SkillHostShutdownGateV1();
        var admission = new CopilotRuntimeAdmissionV1(shutdownGate);
        return new(scope, null!, null!, admission, null, new Sessions.SessionEventQueue(), TimeSpan.FromSeconds(1), _ => null, _ => false, CancellationToken.None);
    }

    private static OwnedSessionExecutionEvidenceV1 Evidence() => new(
        "1.0.75", 3, 1, 1, 1, 1, 1, 1, 1, 1, 2,
        true, true, true, true, true, true);

    private static MonitorAnalysisContext Context() => new(7, "trace", null, "span", MonitorAnalysisFocus.Errors, OperationToken: new MonitorAnalysisOperationToken([1]));
    private static MonitorAnalysisRun Run(string? RequestedAtText = null, string TraceId = "trace", MonitorAnalysisFocus Focus = MonitorAnalysisFocus.Errors) => new(7, TraceId, null, "span", Focus, MonitorAnalysisStatus.Queued, RequestedAtText ?? RequestedAt.ToString("O"), null, null);

    private sealed class FakeOwner : IAnalysisSdkDirectoryOwner
    {
        private readonly IAnalysisSdkDirectoryScope? scope;
        private readonly Exception? exception;
        public FakeOwner(IAnalysisSdkDirectoryScope scope) => this.scope = scope;
        public FakeOwner(Exception exception) => this.exception = exception;
        public int OpenCount { get; private set; }
        public DateTimeOffset RequestedAt { get; private set; }
        public string? ConfiguredParent { get; private set; }
        public ValueTask<IAnalysisSdkDirectoryScope> OpenAsync(long runId, DateTimeOffset exactRequestedAt, string configuredParent, CancellationToken cancellationToken)
        {
            OpenCount++; RequestedAt = exactRequestedAt; ConfiguredParent = configuredParent;
            return exception is null ? ValueTask.FromResult(scope!) : ValueTask.FromException<IAnalysisSdkDirectoryScope>(exception);
        }
    }

    private sealed class FakeScope : IAnalysisSdkDirectoryScope
    {
        private readonly CancellationTokenSource leaseLost = new();
        public FakeScope(string childDirectory) => ChildDirectory = childDirectory;
        public string ChildDirectory { get; }
        public CancellationToken LeaseLostToken => leaseLost.Token;
        public bool IsLeaseLost => leaseLost.IsCancellationRequested;
        public int DisposeCount { get; private set; }
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? DisposeException { get; set; }
        public void LoseLease() => leaseLost.Cancel();
        public ValueTask DisposeAsync() { DisposeCount++; Disposed.TrySetResult(); if (DisposeException is not null) return ValueTask.FromException(DisposeException); return ValueTask.CompletedTask; }
    }

    private sealed class FakeExecutor : ICopilotAnalysisSdkExecutor
    {
        public int CallCount => LegacyCallCount + RootsCallCount;
        public int LegacyCallCount { get; private set; }
        public int RootsCallCount { get; private set; }
        public CopilotAnalysisRootsExecutionContext? RootsContext { get; private set; }
        public string? ChildDirectory { get; private set; }
        public Exception? Exception { get; set; }
        public Action? BeforeReturn { get; set; }
        public Action? CancelLeaseBeforeWaiting { get; set; }
        public CopilotAnalysisExecutionResult Result { get; set; } = new("done");
        public bool WaitForCancellation { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<CopilotAnalysisExecutionResult> ExecuteAsync(string childDirectory, CopilotAnalysisExecutionSettings settings, CopilotAnalysisToolRequest request, CancellationToken cancellationToken)
        {
            LegacyCallCount++; ChildDirectory = childDirectory;
            if (Exception is not null) return Task.FromException<CopilotAnalysisExecutionResult>(Exception);
            if (CancelLeaseBeforeWaiting is not null)
            {
                CancelLeaseBeforeWaiting();
                return WaitForCancellationAsync(cancellationToken);
            }
            BeforeReturn?.Invoke();
            return Task.FromResult(Result);
        }

        public Task<CopilotAnalysisExecutionResult> ExecuteAsync(string childDirectory, CopilotAnalysisExecutionSettings settings, CopilotAnalysisToolRequest request, CopilotAnalysisRootsExecutionContext context, CancellationToken cancellationToken)
        {
            RootsCallCount++;
            RootsContext = context;
            ChildDirectory = childDirectory;
            Entered.TrySetResult();
            if (WaitForCancellation) return WaitForCancellationAsync(cancellationToken);
            BeforeReturn?.Invoke();
            return Task.FromResult(Result);
        }

        private static async Task<CopilotAnalysisExecutionResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new("done");
        }
    }

    private sealed class FakeRuntimeClient : ICopilotSkillRuntimeClient
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<CopilotRuntimeStatusObservationV1?> GetStatusAsync(CancellationToken cancellationToken) => Task.FromResult<CopilotRuntimeStatusObservationV1?>(null);
        public Task<IReadOnlyList<CopilotDiscoveredSkillFactV1>?> DiscoverSkillsAsync(IReadOnlyList<string> projectPaths, IReadOnlyList<string> skillDirectories, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CopilotDiscoveredSkillFactV1>?>([]);
        public Task<GitHub.Copilot.CopilotSession> CreateSessionAsync(GitHub.Copilot.SessionConfig config, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void RecordSessionStartCopilotVersion(string? copilotVersion) { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CallbackPoisonExecutor : ICopilotAnalysisSdkExecutor
    {
        public bool WorkTokenCanBeCanceled { get; private set; }
        public bool WorkTokenWasCanceled { get; private set; }
        public bool ObservedOperationCanceledException { get; private set; }
        public int Invalidations { get; private set; }

        public Task<CopilotAnalysisExecutionResult> ExecuteAsync(string childDirectory, CopilotAnalysisExecutionSettings settings,
            CopilotAnalysisToolRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();

        public async Task<CopilotAnalysisExecutionResult> ExecuteAsync(string childDirectory, CopilotAnalysisExecutionSettings settings,
            CopilotAnalysisToolRequest request, CopilotAnalysisRootsExecutionContext context, CancellationToken cancellationToken)
        {
            var ownership = Assert.IsType<AnalysisSdkScopeOwnership>(context.ScopeOwnership);
            Assert.True(ownership.TryTransferToExecutor());
            Assert.True(ownership.TryTransferToCandidate());
            var candidate = context.Admission.CreateUnpublishedCandidate(
                new FakeRuntimeClient(), SkillInvocationV2TestIdentity.V1075, context.AnalysisScope, ownership);
            Assert.True(candidate.TryAcquireOperationCapability(cancellationToken, out var capability));
            try
            {
                var workToken = capability.WorkToken;
                WorkTokenCanBeCanceled = workToken.CanBeCanceled;
                var state = new OwnedSessionCallbackStateV1(
                    new OwnedSessionFrozenSkillInventoryV1(
                        new Dictionary<string, OwnedSessionFrozenSkillV1>(),
                        new Dictionary<string, CopilotDiscoveredSkillFactV1>(), []),
                    new NeverProof(), "1.0.75",
                    _ => Encoding.UTF8.GetBytes("start"), (_, _) => Encoding.UTF8.GetBytes("invocation"),
                    _ => Encoding.UTF8.GetBytes("terminal"), workToken,
                    () => { Invalidations++; context.Admission.InvalidateCandidate(candidate); });
                var pending = Task.Delay(Timeout.InfiniteTimeSpan, workToken);
                state.OnEvent(new SessionErrorEvent
                    { Data = new SessionErrorData { ErrorType = "synthetic", Message = "synthetic" } });
                WorkTokenWasCanceled = workToken.IsCancellationRequested;
                try { await pending; }
                catch (OperationCanceledException) { ObservedOperationCanceledException = true; throw; }
                throw new InvalidOperationException();
            }
            finally { capability.Release(); }
        }

        private sealed class NeverProof : IOwnedSessionSkillProofProviderV1
        {
            public bool TryProve(CopilotDiscoveredSkillFactV1 fact, IReadOnlyList<string> roots,
                out OwnedSessionSkillProofV1? proof) { proof = null; return false; }
        }
    }

    private sealed class FakeStore : IMonitorAnalysisStore
    {
        public FakeStore(MonitorAnalysisRun? run) => Run = run;
        public MonitorAnalysisRun? Run { get; }
        public int CompleteCount { get; private set; }
        public InstructionFindingHandoffV1? CompletedHandoff { get; private set; }
        public string? FinishedMessage { get; private set; }
        public MonitorAnalysisStatus? FinishedStatus { get; private set; }
        public int MarkRunningCount { get; private set; }
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? CompleteException { get; init; }
        public void CreateSchema() { }
        public MonitorAnalysisStartResult StartRun(string traceId, long? rawRecordId, string? spanId, MonitorAnalysisFocus focus, DateTimeOffset requestedAt) => throw new NotSupportedException();
        public MonitorAnalysisRun? GetRun(long runId) => Run;
        public IReadOnlyList<MonitorAnalysisRun> ListRunsForTrace(string traceId, int limit) => [];
        public ValueTask<RetentionReadResult<AnalysisRunRawSnapshot>> ReadRawSnapshotAsync(long runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void MarkRunning(long runId, DateTimeOffset startedAt) { MarkRunningCount++; }
        public RetentionRevisionFence AppendEvent(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string eventType, string message, DateTimeOffset occurredAt) => null!;
        public RetentionRevisionFence CompleteRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string resultMarkdown, DateTimeOffset completedAt) { CompleteCount++; if (CompleteException is not null) throw CompleteException; return null!; }
        public RetentionRevisionFence CompleteInstructionDiagnosisRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string resultMarkdown, InstructionFindingHandoffV1 handoff, DateTimeOffset completedAt) { CompleteCount++; if (CompleteException is not null) throw CompleteException; CompletedHandoff = handoff; return null!; }
        public RetentionRevisionFence? FinishRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt) { FinishedStatus = status; FinishedMessage = message; Finished.TrySetResult(); return null; }
        public MonitorAnalysisSafeSummary GenerateRepositorySafeSummary(long runId, DateTimeOffset generatedAt) => throw new NotSupportedException();
    }
}

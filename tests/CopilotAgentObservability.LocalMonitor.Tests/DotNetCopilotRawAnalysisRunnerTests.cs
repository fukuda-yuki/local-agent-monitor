using CopilotAgentObservability.LocalMonitor.Analysis;
using CopilotAgentObservability.LocalMonitor.Projection;
using CopilotAgentObservability.Persistence.Sqlite;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace CopilotAgentObservability.LocalMonitor.Tests;

public sealed class DotNetCopilotRawAnalysisRunnerTests
{
    private static readonly DateTimeOffset RequestedAt = new(2026, 7, 19, 1, 2, 3, TimeSpan.Zero);

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
        var start = analysisStore.StartRun("trace", 9, "span", MonitorAnalysisFocus.Errors, RequestedAt);
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var parent = Path.Combine(temp.Path, "sdk-parent");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = parent }).Build();
        var executor = new FakeExecutor();
        var catalog = new RetentionCatalogStore(temp.RetentionContext);
        var runner = new DotNetCopilotRawAnalysisRunner(analysisStore, new RawTelemetryStoreProjectionStore(raw), configuration, new AnalysisSdkDirectoryOwner(catalog, temp.TimeProvider), executor, temp.TimeProvider);
        var context = new MonitorAnalysisContext(start.RunId, "trace", 9, "span", MonitorAnalysisFocus.Errors, OperationToken: start.OperationToken);

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

    private static DotNetCopilotRawAnalysisRunner CreateRunner(MonitorTempDirectory temp, FakeStore store, FakeOwner owner, FakeExecutor executor)
    {
        var raw = temp.CreateRawStore();
        raw.CreateSchema();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["CopilotAnalysis:BaseDirectory"] = "configured-parent" }).Build();
        return new DotNetCopilotRawAnalysisRunner(store, new RawTelemetryStoreProjectionStore(raw), configuration, owner, executor, temp.TimeProvider);
    }

    private static MonitorAnalysisContext Context() => new(7, "trace", 9, "span", MonitorAnalysisFocus.Errors, OperationToken: new MonitorAnalysisOperationToken([1]));
    private static MonitorAnalysisRun Run(string? RequestedAtText = null, string TraceId = "trace") => new(7, TraceId, 9, "span", MonitorAnalysisFocus.Errors, MonitorAnalysisStatus.Queued, RequestedAtText ?? RequestedAt.ToString("O"), null, null);

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
        public Exception? DisposeException { get; set; }
        public void LoseLease() => leaseLost.Cancel();
        public ValueTask DisposeAsync() { DisposeCount++; if (DisposeException is not null) return ValueTask.FromException(DisposeException); return ValueTask.CompletedTask; }
    }

    private sealed class FakeExecutor : ICopilotAnalysisSdkExecutor
    {
        public int CallCount { get; private set; }
        public string? ChildDirectory { get; private set; }
        public Exception? Exception { get; set; }
        public Action? BeforeReturn { get; set; }
        public Action? CancelLeaseBeforeWaiting { get; set; }
        public Task<string> ExecuteAsync(string childDirectory, CopilotAnalysisExecutionSettings settings, CopilotAnalysisToolRequest request, CancellationToken cancellationToken)
        {
            CallCount++; ChildDirectory = childDirectory;
            if (Exception is not null) return Task.FromException<string>(Exception);
            if (CancelLeaseBeforeWaiting is not null)
            {
                CancelLeaseBeforeWaiting();
                return WaitForCancellationAsync(cancellationToken);
            }
            BeforeReturn?.Invoke();
            return Task.FromResult("done");
        }

        private static async Task<string> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "done";
        }
    }

    private sealed class FakeStore : IMonitorAnalysisStore
    {
        public FakeStore(MonitorAnalysisRun? run) => Run = run;
        public MonitorAnalysisRun? Run { get; }
        public int CompleteCount { get; private set; }
        public string? FinishedMessage { get; private set; }
        public MonitorAnalysisStatus? FinishedStatus { get; private set; }
        public void CreateSchema() { }
        public MonitorAnalysisStartResult StartRun(string traceId, long? rawRecordId, string? spanId, MonitorAnalysisFocus focus, DateTimeOffset requestedAt) => throw new NotSupportedException();
        public MonitorAnalysisRun? GetRun(long runId) => Run;
        public IReadOnlyList<MonitorAnalysisRun> ListRunsForTrace(string traceId, int limit) => [];
        public ValueTask<RetentionReadResult<AnalysisRunRawSnapshot>> ReadRawSnapshotAsync(long runId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void MarkRunning(long runId, DateTimeOffset startedAt) { }
        public RetentionRevisionFence AppendEvent(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string eventType, string message, DateTimeOffset occurredAt) => null!;
        public RetentionRevisionFence CompleteRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, string resultMarkdown, DateTimeOffset completedAt) { CompleteCount++; return null!; }
        public RetentionRevisionFence? FinishRun(long runId, MonitorAnalysisOperationToken operationToken, RetentionRevisionFence? expectedFence, MonitorAnalysisStatus status, string? message, DateTimeOffset completedAt) { FinishedStatus = status; FinishedMessage = message; return null; }
        public MonitorAnalysisSafeSummary GenerateRepositorySafeSummary(long runId, DateTimeOffset generatedAt) => throw new NotSupportedException();
    }
}

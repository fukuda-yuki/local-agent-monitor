namespace CopilotAgentObservability.LocalMonitor.Analysis;

using CopilotAgentObservability.LocalMonitor.SkillRuntime;
using CopilotAgentObservability.LocalMonitor.SkillNative;
using CopilotAgentObservability.LocalMonitor.Sessions;

internal interface IAnalysisSdkDirectoryOwner
{
    ValueTask<IAnalysisSdkDirectoryScope> OpenAsync(
        long runId,
        DateTimeOffset exactRequestedAt,
        string configuredParent,
        CancellationToken cancellationToken);
}

internal interface IAnalysisSdkDirectoryScope : IAsyncDisposable
{
    string ChildDirectory { get; }
    CancellationToken LeaseLostToken { get; }
    bool IsLeaseLost { get; }
}

internal sealed class AnalysisSdkScopeOwnership(IAnalysisSdkDirectoryScope scope) : IAsyncDisposable
{
    private const int Runner = 0;
    private const int Executor = 1;
    private const int Candidate = 2;
    private const int Disposed = 3;
    private int owner = Runner;

    internal IAnalysisSdkDirectoryScope Scope { get; } = scope;
    internal bool TryTransferToExecutor() => Interlocked.CompareExchange(ref owner, Executor, Runner) == Runner;
    internal bool TryTransferToCandidate() => Interlocked.CompareExchange(ref owner, Candidate, Executor) == Executor;
    internal ValueTask DisposeByRunnerAsync() => DisposeByOwnerAsync(Runner);
    internal ValueTask DisposeByExecutorAsync() => DisposeByOwnerAsync(Executor);
    public ValueTask DisposeAsync() => DisposeByOwnerAsync(Candidate);

    private ValueTask DisposeByOwnerAsync(int expectedOwner) =>
        Interlocked.CompareExchange(ref owner, Disposed, expectedOwner) == expectedOwner
            ? Scope.DisposeAsync()
            : ValueTask.CompletedTask;
}

internal interface ICopilotAnalysisSdkExecutor
{
    Task<CopilotAnalysisExecutionResult> ExecuteAsync(
        string childDirectory,
        CopilotAnalysisExecutionSettings settings,
        CopilotAnalysisToolRequest request,
        CancellationToken cancellationToken);

    Task<CopilotAnalysisExecutionResult> ExecuteAsync(
        string childDirectory,
        CopilotAnalysisExecutionSettings settings,
        CopilotAnalysisToolRequest request,
        CopilotAnalysisRootsExecutionContext context,
        CancellationToken cancellationToken) =>
        Task.FromException<CopilotAnalysisExecutionResult>(
            new InvalidOperationException("Roots-enabled SDK analysis is not configured."));
}

internal sealed record CopilotAnalysisRootsExecutionContext(
    IAnalysisSdkDirectoryScope AnalysisScope,
    SkillDiscoveryRootGenerationV1 RootGeneration,
    ICurrentSkillNativeFileReaderV1 NativeReader,
    CopilotRuntimeAdmissionV1 Admission,
    SkillRuntimeCapabilityBridgeV1? Bridge,
    SessionEventQueue SessionEventQueue,
    TimeSpan CommitTimeout,
    Func<string, IOwnedCopilotClientV1?> OwnedClientFactory,
    Func<string, bool> EnvironmentEntryPresent,
    CancellationToken HostStoppingToken,
    AnalysisSdkScopeOwnership? ScopeOwnership = null,
    IOwnedSessionExecutionDriverV1? ExecutionDriver = null,
    Action<OwnedSessionExecutionEvidenceV1>? ExecutionEvidenceObserver = null,
    Action<OwnedSessionExecutionCheckpointV1>? ExecutionCheckpointObserver = null,
    Action<OwnedSessionDiagnosticEventV1>? OwnedSessionDiagnosticObserver = null,
    Action<OwnedSessionPostFreezeOutcomeV1>? PostFreezeFailureObserver = null);

internal enum OwnedSessionDiagnosticEventV1
{
    CommandPending,
    SendPending,
    WorkTokenPreCanceled,
    ClosedRelevantEvent,
    SessionStartContract,
    SessionBindingContract,
    InvocationIdentity,
    InvocationDescription,
    InvocationContent,
    InvocationNativeReproof,
    InvocationPreparation,
    InvocationBuffer,
    TerminalContract,
    SessionError,
    ModelCallFailure,
    Abort,
    CallbackException,
}

internal static class OwnedSessionDiagnosticObservationV1
{
    internal static void Notify(Action<OwnedSessionDiagnosticEventV1>? observer, OwnedSessionDiagnosticEventV1 value)
    {
        if (observer is null) return;
        try { observer(value); }
        catch { }
    }
}

internal enum OwnedSessionExecutionCheckpointV1
{
    ClientStarted,
    IdentityCertified,
    CandidateCreated,
    ProbeCertified,
    ExecutionInventoryCertified,
    DriverCompleted,
    CallbacksFrozen,
    ImportCompleted,
    CandidateReady,
    CandidatePublished,
}

internal static class OwnedSessionExecutionCheckpointObservationV1
{
    internal static void Notify(Action<OwnedSessionExecutionCheckpointV1>? observer, OwnedSessionExecutionCheckpointV1 checkpoint)
    {
        if (observer is null) return;
        try { observer(checkpoint); }
        catch { }
    }
}

internal sealed record OwnedSessionExecutionEvidenceV1(
    string SourceApplicationVersion,
    int ProtocolVersion,
    int ClientStartCount,
    int StatusObservationCount,
    int ProbeSessionCount,
    int ExecutionSessionCount,
    int RetainedRootCount,
    int RetainedSkillCount,
    int ProbeInventoryCount,
    int ExecutionInventoryCount,
    int PreparedInvocationCount,
    bool SameClient,
    bool ExactToolUnion,
    bool RetainedOnlyInventory,
    bool ProbeNativeReproof,
    bool ExecutionNativeReproof,
    bool CallbackNativeReproof);

internal sealed record CopilotAnalysisExecutionResult(
    string ResultMarkdown,
    CopilotRuntimeGenerationV1? UnpublishedCandidate = null,
    OwnedSessionExecutionEvidenceV1? ExecutionEvidence = null)
{
    internal bool OwnsAnalysisScope => UnpublishedCandidate is not null;
}

internal sealed record CopilotAnalysisExecutionSettings(string Model, int TimeoutSeconds, GitHub.Copilot.ProviderConfig? Provider);

internal sealed record CopilotAnalysisToolRequest(string Prompt, MonitorAnalysisToolData Data);

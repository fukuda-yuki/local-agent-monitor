namespace CopilotAgentObservability.LocalMonitor.Analysis;

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

internal interface ICopilotAnalysisSdkExecutor
{
    Task<string> ExecuteAsync(
        string childDirectory,
        CopilotAnalysisExecutionSettings settings,
        CopilotAnalysisToolRequest request,
        CancellationToken cancellationToken);
}

internal sealed record CopilotAnalysisExecutionSettings(string Model, int TimeoutSeconds, GitHub.Copilot.ProviderConfig? Provider);

internal sealed record CopilotAnalysisToolRequest(string Prompt, MonitorAnalysisToolData Data);

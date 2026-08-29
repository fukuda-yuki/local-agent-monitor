namespace CopilotAgentObservability.LocalMonitor;

internal enum LocalMonitorV1ComparisonOperation { Preview, Create, Read, Rows, Evidence }

internal sealed record LocalMonitorV1ComparisonResponse(int StatusCode, byte[] Entity, string? Location = null);

internal interface ILocalMonitorV1ComparisonApplication
{
    ValueTask<LocalMonitorV1ComparisonResponse> ExecuteAsync(
        LocalMonitorV1ComparisonOperation operation,
        string repositoryId,
        string? comparisonId,
        ReadOnlyMemory<byte> requestBody,
        string query,
        CancellationToken cancellationToken);
}

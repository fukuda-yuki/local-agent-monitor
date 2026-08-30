using CopilotAgentObservability.Persistence.Sqlite;

namespace CopilotAgentObservability.LocalMonitor.LocalAi;

internal sealed class LocalAiRetentionRawReaderV1(ILocalWorkspaceNodeContentReader reader)
{
    internal async ValueTask<byte[]> ReadAsync(string sessionId, LocalAiRawEvidenceV1 evidence, CancellationToken token)
    {
        var result = await reader.ReadAsync(sessionId, evidence.NodeId, evidence.Locator, token).ConfigureAwait(false);
        if (result.Disposition != LocalWorkspaceNodeContentReadDisposition.Granted || result.Lease is null)
            throw new LocalAiRawReadException("raw_read_denied");
        await using var lease = result.Lease; byte[] bytes;
        using (var reference = lease.AcquireBytesReference()) bytes = reference.Value;
        if (lease.TrySealRawResponse() != LocalWorkspaceNodeContentTerminalResult.Sealed)
            throw new LocalAiRawReadException("raw_read_denied");
        return bytes;
    }
}

using CopilotAgentObservability.ConfigCli.HistoricalImport;

namespace CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;

internal sealed class SystemClaudeHistoricalFileSystem : IClaudeHistoricalFileSystem
{
    public ClaudeTranscriptReferenceInspection InspectExactReference(string exactReference)
    {
        return HistoricalImportLocalFile.Inspect(exactReference) switch
        {
            HistoricalImportPathKind.Missing => ClaudeTranscriptReferenceInspection.Missing,
            HistoricalImportPathKind.RegularFile => ClaudeTranscriptReferenceInspection.RegularFile,
            _ => ClaudeTranscriptReferenceInspection.NotRegularFile,
        };
    }
}

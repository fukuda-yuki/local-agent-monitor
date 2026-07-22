namespace CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;

internal sealed class SystemClaudeHistoricalFileSystem : IClaudeHistoricalFileSystem
{
    public ClaudeTranscriptReferenceInspection InspectExactReference(string exactReference)
    {
        try
        {
            var attributes = File.GetAttributes(exactReference);
            return (attributes & FileAttributes.Directory) == 0
                ? ClaudeTranscriptReferenceInspection.RegularFile
                : ClaudeTranscriptReferenceInspection.NotRegularFile;
        }
        catch (FileNotFoundException)
        {
            return ClaudeTranscriptReferenceInspection.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return ClaudeTranscriptReferenceInspection.Missing;
        }
    }

}

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

    public Stream OpenTranscriptBody(string exactReference) =>
        throw new NotSupportedException("The unsupported Claude history profile cannot read transcript content.");

    public IEnumerable<string> EnumerateReferences(string root) =>
        throw new NotSupportedException("The Claude history adapter does not scan for transcripts.");

    public void Write(string path, ReadOnlySpan<byte> content) =>
        throw new NotSupportedException("The Claude history detector has no write authority.");
}

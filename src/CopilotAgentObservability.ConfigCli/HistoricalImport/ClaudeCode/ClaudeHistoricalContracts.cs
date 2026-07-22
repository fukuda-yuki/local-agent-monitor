namespace CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;

internal enum ClaudeTranscriptReferenceKind
{
    OfficialHook,
    ExplicitUserSelection
}

internal enum ClaudeTranscriptReferenceInspection
{
    RegularFile,
    Missing,
    NotRegularFile
}

internal sealed record ClaudeTranscriptReference(
    ClaudeTranscriptReferenceKind Kind,
    string ExactReference);

internal sealed record ClaudeHistoricalProbeConsent(
    ClaudeTranscriptReferenceKind Kind,
    string ExactReference,
    string RequestedCapture);

internal sealed record ClaudeHistoricalProbeRequest(
    ClaudeTranscriptReference Reference,
    ClaudeHistoricalProbeConsent? Consent,
    string? SourceApplicationVersion);

internal interface IClaudeHistoricalFileSystem
{
    ClaudeTranscriptReferenceInspection InspectExactReference(string exactReference);
}

internal sealed class ClaudeHistoricalAdapterResult
{
    internal ClaudeHistoricalAdapterResult(
        string detectionState,
        string sourceReferenceState,
        string? sourceApplicationVersion,
        string diagnostic)
    {
        DetectionState = detectionState;
        SourceReferenceState = sourceReferenceState;
        SourceApplicationVersion = sourceApplicationVersion;
        Diagnostics = [diagnostic];
    }

    public string DetectionState { get; }

    public string SourceReferenceState { get; }

    public string? SourceApplicationVersion { get; }

    public bool SupportAuthorized => false;

    public string SourceFormatProfile => "none";

    public int CandidateCount => 0;

    public string ContentRisk => "not_read";

    public IReadOnlyList<string> Diagnostics { get; }
}

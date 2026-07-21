using System.Text.Json;
using System.Text.RegularExpressions;

namespace CopilotAgentObservability.ConfigCli.HistoricalImport.GitHubCopilot;

internal sealed class GitHubCopilotHistoricalAdapter
{
    private const string AdapterId = "github-copilot-cli-history-v1";
    private const string ProfileId = "github-copilot-cli-session-state";
    private const string SourceSurface = "github-copilot-cli";
    private const string MetadataOnly = "metadata_only";
    private const string ReferenceRequired = "historical_source_reference_required";
    private const string SourceMalformed = "historical_source_malformed";
    private const string FormatUnsupported = "historical_source_format_unsupported";
    private const string NoEligibleCandidates = "historical_import_no_eligible_candidates";
    private const int MaximumSelectedRootCharacters = 4096;
    private const int MaximumSessionIdCharacters = 255;
    private const int MaximumVersionCharacters = 64;

    private static readonly Regex ExactVersionPattern = new(
        "^[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly IGitHubCopilotHistoricalMetadataFileSystem fileSystem;

    public GitHubCopilotHistoricalAdapter(IGitHubCopilotHistoricalMetadataFileSystem fileSystem)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public GitHubCopilotHistoricalProbe Probe(GitHubCopilotHistoricalProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.ConsentGranted ||
            string.IsNullOrWhiteSpace(request.SelectedRoot) ||
            string.IsNullOrWhiteSpace(request.SessionId))
        {
            return CreateProbe(
                detectionState: "not_evaluated",
                sourceReferenceState: "missing",
                sourceApplicationVersion: null,
                ReferenceRequired);
        }

        if (!IsValidRequest(request))
        {
            return CreateProbe(
                detectionState: "not_evaluated",
                sourceReferenceState: "provided",
                sourceApplicationVersion: null,
                SourceMalformed);
        }

        try
        {
            var selectedRoot = request.SelectedRoot!;
            var sessionState = Path.Combine(selectedRoot, "session-state");
            var session = Path.Combine(sessionState, request.SessionId!);
            var events = Path.Combine(session, "events.jsonl");

            if (fileSystem.InspectPath(selectedRoot) != GitHubCopilotHistoricalPathKind.Directory ||
                fileSystem.InspectPath(sessionState) != GitHubCopilotHistoricalPathKind.Directory ||
                fileSystem.InspectPath(session) != GitHubCopilotHistoricalPathKind.Directory ||
                fileSystem.InspectPath(events) != GitHubCopilotHistoricalPathKind.RegularFile)
            {
                return CreateProbe(
                    detectionState: "detected",
                    sourceReferenceState: "provided",
                    request.SourceApplicationVersion,
                    SourceMalformed);
            }

            return CreateProbe(
                detectionState: "detected",
                sourceReferenceState: "provided",
                request.SourceApplicationVersion,
                FormatUnsupported);
        }
        catch (Exception exception) when (IsMetadataFailure(exception))
        {
            return CreateProbe(
                detectionState: "detected",
                sourceReferenceState: "provided",
                request.SourceApplicationVersion,
                SourceMalformed);
        }
    }

    private static bool IsValidRequest(GitHubCopilotHistoricalProbeRequest request)
    {
        var selectedRoot = request.SelectedRoot!;
        var sessionId = request.SessionId!;
        var version = request.SourceApplicationVersion;

        return string.Equals(request.RequestedCapture, MetadataOnly, StringComparison.Ordinal) &&
            selectedRoot.Length <= MaximumSelectedRootCharacters &&
            IsCanonicalAbsoluteRoot(selectedRoot) &&
            sessionId.Length <= MaximumSessionIdCharacters &&
            sessionId is not ("." or "..") &&
            sessionId.IndexOfAny(['/', '\\', ':']) < 0 &&
            sessionId.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
            !string.IsNullOrEmpty(version) &&
            version.Length <= MaximumVersionCharacters &&
            ExactVersionPattern.IsMatch(version);
    }

    private static bool IsCanonicalAbsoluteRoot(string selectedRoot)
    {
        try
        {
            return Path.IsPathFullyQualified(selectedRoot) &&
                string.Equals(Path.GetFullPath(selectedRoot), selectedRoot, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static bool IsMetadataFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException;

    private static GitHubCopilotHistoricalProbe CreateProbe(
        string detectionState,
        string sourceReferenceState,
        string? sourceApplicationVersion,
        string diagnostic)
    {
        var adapterResult = new GitHubCopilotHistoricalAdapterResult(
            ContractVersion: "historical-adapter-result/v1",
            AdapterId,
            ProfileId,
            SourceSurface,
            SourceTier: "tier_b",
            detectionState,
            sourceReferenceState,
            sourceApplicationVersion,
            SupportAuthorized: false,
            SourceFormatProfile: "none",
            CandidateCount: 0,
            ContentRisk: "not_read",
            RepositorySafe: true,
            Diagnostics: [diagnostic]);

        var preview = new GitHubCopilotHistoricalImportPreview(
            ContractVersion: "historical-import-preview/v1",
            SourceSurface,
            ProfileId,
            AdapterId,
            AdapterDiagnostics: adapterResult.Diagnostics,
            RequestedCapture: MetadataOnly,
            EligibleCandidateCount: 0,
            RejectedCandidateCount: 0,
            CommitAllowed: false,
            RejectionCode: NoEligibleCandidates,
            ContentRisk: "not_read");

        return new GitHubCopilotHistoricalProbe(
            adapterResult,
            preview,
            JsonSerializer.SerializeToUtf8Bytes(adapterResult, JsonOptions),
            JsonSerializer.SerializeToUtf8Bytes(preview, JsonOptions));
    }
}

internal sealed record GitHubCopilotHistoricalProbeRequest(
    string? SelectedRoot,
    string? SessionId,
    string? SourceApplicationVersion,
    bool ConsentGranted,
    string RequestedCapture);

internal sealed record GitHubCopilotHistoricalProbe(
    GitHubCopilotHistoricalAdapterResult AdapterResult,
    GitHubCopilotHistoricalImportPreview ImportPreview,
    byte[] AdapterResultJson,
    byte[] ImportPreviewJson);

internal sealed record GitHubCopilotHistoricalAdapterResult(
    string ContractVersion,
    string AdapterId,
    string ProfileId,
    string SourceSurface,
    string SourceTier,
    string DetectionState,
    string SourceReferenceState,
    string? SourceApplicationVersion,
    bool SupportAuthorized,
    string SourceFormatProfile,
    int CandidateCount,
    string ContentRisk,
    bool RepositorySafe,
    IReadOnlyList<string> Diagnostics);

internal sealed record GitHubCopilotHistoricalImportPreview(
    string ContractVersion,
    string SourceSurface,
    string ProfileId,
    string AdapterId,
    IReadOnlyList<string> AdapterDiagnostics,
    string RequestedCapture,
    int EligibleCandidateCount,
    int RejectedCandidateCount,
    bool CommitAllowed,
    string RejectionCode,
    string ContentRisk);

internal interface IGitHubCopilotHistoricalMetadataFileSystem
{
    GitHubCopilotHistoricalPathKind InspectPath(string path);
}

internal enum GitHubCopilotHistoricalPathKind
{
    Missing,
    Directory,
    RegularFile,
    Other,
}

internal sealed class SystemGitHubCopilotHistoricalMetadataFileSystem :
    IGitHubCopilotHistoricalMetadataFileSystem
{
    public GitHubCopilotHistoricalPathKind InspectPath(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return GitHubCopilotHistoricalPathKind.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return GitHubCopilotHistoricalPathKind.Missing;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            return GitHubCopilotHistoricalPathKind.Other;
        }

        return (attributes & FileAttributes.Directory) != 0
            ? GitHubCopilotHistoricalPathKind.Directory
            : GitHubCopilotHistoricalPathKind.RegularFile;
    }
}

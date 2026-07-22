using CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;
using CopilotAgentObservability.ConfigCli.HistoricalImport.GitHubCopilot;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;
using System.Security.Cryptography;

namespace CopilotAgentObservability.ConfigCli.HistoricalImport;

public sealed class HistoricalImportSourceGateway : IHistoricalSourceGateway
{
    private const string SnapshotVersion = "hsv_1";

    private readonly IGitHubCopilotHistoricalMetadataFileSystem githubFileSystem;
    private readonly IClaudeHistoricalFileSystem claudeFileSystem;

    public HistoricalImportSourceGateway()
        : this(
            new SystemGitHubCopilotHistoricalMetadataFileSystem(),
            new SystemClaudeHistoricalFileSystem())
    {
    }

    internal HistoricalImportSourceGateway(
        IGitHubCopilotHistoricalMetadataFileSystem githubFileSystem,
        IClaudeHistoricalFileSystem claudeFileSystem)
    {
        this.githubFileSystem = githubFileSystem ?? throw new ArgumentNullException(nameof(githubFileSystem));
        this.claudeFileSystem = claudeFileSystem ?? throw new ArgumentNullException(nameof(claudeFileSystem));
    }

    public HistoricalSourceProbe Probe(HistoricalSourceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!HistoricalSourcePathPolicy.IsCanonicalNativeAbsolute(selection.ExactReference))
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.RequestInvalid);
        }

        var result = selection.SourceSurface switch
        {
            "github-copilot-cli" => ProbeGitHub(selection),
            "claude-code" => ProbeClaude(selection),
            _ => throw new HistoricalImportException(HistoricalImportErrorCodes.RequestInvalid),
        };
        var canonicalResult = HistoricalImportJson.Serialize(result);
        var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(canonicalResult)).ToLowerInvariant();
        return new(
            result,
            CandidateBatch: null,
            AdmissionEvidence: null,
            CandidateBindings: [],
            SnapshotVersion,
            digest);
    }

    private HistoricalAdapterResult ProbeGitHub(HistoricalSourceSelection selection)
    {
        if (!string.Equals(selection.ReferenceKind, "selected_root", StringComparison.Ordinal))
        {
            throw new HistoricalImportException(HistoricalImportErrorCodes.RequestInvalid);
        }

        var probe = new GitHubCopilotHistoricalAdapter(githubFileSystem).Probe(new(
            selection.ExactReference,
            selection.SessionId,
            selection.SourceApplicationVersion,
            selection.ConsentGranted,
            selection.RequestedCapture));
        if (probe is null)
        {
            return ReferenceRequired(
                "github-copilot-cli-history-v1",
                "github-copilot-cli-session-state",
                "github-copilot-cli");
        }

        var result = probe.AdapterResult;
        return new(
            result.ContractVersion,
            result.AdapterId,
            result.ProfileId,
            result.SourceSurface,
            result.SourceTier,
            result.DetectionState,
            result.SourceReferenceState,
            result.SourceApplicationVersion,
            result.SupportAuthorized,
            result.SourceFormatProfile,
            result.CandidateCount,
            result.ContentRisk,
            result.RepositorySafe,
            result.Diagnostics);
    }

    private HistoricalAdapterResult ProbeClaude(HistoricalSourceSelection selection)
    {
        var referenceKind = selection.ReferenceKind switch
        {
            "official_hook" => ClaudeTranscriptReferenceKind.OfficialHook,
            "explicit_user_selection" => ClaudeTranscriptReferenceKind.ExplicitUserSelection,
            _ => throw new HistoricalImportException(HistoricalImportErrorCodes.RequestInvalid),
        };
        var exactReference = selection.ExactReference ?? string.Empty;
        var consent = selection.ConsentGranted
            ? new ClaudeHistoricalProbeConsent(referenceKind, exactReference, selection.RequestedCapture)
            : null;
        var result = new ClaudeHistoricalAdapter(claudeFileSystem).Probe(new(
            new ClaudeTranscriptReference(referenceKind, exactReference),
            consent,
            selection.SourceApplicationVersion));

        return new(
            "historical-adapter-result/v1",
            "claude-code-history-v1",
            "claude-code-transcript",
            "claude-code",
            "tier_b",
            result.DetectionState,
            result.SourceReferenceState,
            result.SourceApplicationVersion,
            result.SupportAuthorized,
            result.SourceFormatProfile,
            result.CandidateCount,
            result.ContentRisk,
            RepositorySafe: true,
            result.Diagnostics);
    }

    private static HistoricalAdapterResult ReferenceRequired(
        string adapterId,
        string profileId,
        string sourceSurface) => new(
            "historical-adapter-result/v1",
            adapterId,
            profileId,
            sourceSurface,
            "tier_b",
            "not_evaluated",
            "missing",
            SourceApplicationVersion: null,
            SupportAuthorized: false,
            SourceFormatProfile: "none",
            CandidateCount: 0,
            ContentRisk: "not_read",
            RepositorySafe: true,
            Diagnostics: ["historical_source_reference_required"]);
}

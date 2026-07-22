using CopilotAgentObservability.ConfigCli.HistoricalImport;
using CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;
using CopilotAgentObservability.ConfigCli.HistoricalImport.GitHubCopilot;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class HistoricalImportGatewayTests
{
    private static readonly string SelectedRoot = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "historical-import-gateway-tests",
        "copilot-root"));

    [Fact]
    public void Probe_GitHubSelection_UsesExactAdapterAndProducesDeterministicZeroCandidateSnapshot()
    {
        var github = ValidGitHubFileSystem();
        var claude = new RecordingClaudeFileSystem();
        var gateway = new HistoricalImportSourceGateway(github, claude);
        var selection = GitHubSelection();

        var first = gateway.Probe(selection);
        var second = gateway.Probe(selection);

        Assert.Equal("github-copilot-cli-history-v1", first.AdapterResult.AdapterId);
        Assert.Equal("github-copilot-cli-session-state", first.AdapterResult.ProfileId);
        Assert.Equal("github-copilot-cli", first.AdapterResult.SourceSurface);
        Assert.Equal(["historical_source_format_unsupported"], first.AdapterResult.Diagnostics);
        Assert.False(first.AdapterResult.SupportAuthorized);
        Assert.Equal(0, first.AdapterResult.CandidateCount);
        Assert.Null(first.CandidateBatch);
        Assert.Equal("hsv_1", first.SnapshotVersion);
        Assert.Matches("^sha256:[0-9a-f]{64}$", first.SnapshotDigest);
        Assert.Equal(first.SnapshotDigest, second.SnapshotDigest);
        Assert.Equal(
            [
                SelectedRoot,
                Path.Combine(SelectedRoot, "session-state"),
                Path.Combine(SelectedRoot, "session-state", "session-123"),
                Path.Combine(SelectedRoot, "session-state", "session-123", "events.jsonl"),
                SelectedRoot,
                Path.Combine(SelectedRoot, "session-state"),
                Path.Combine(SelectedRoot, "session-state", "session-123"),
                Path.Combine(SelectedRoot, "session-state", "session-123", "events.jsonl"),
            ],
            github.InspectedPaths);
        Assert.Empty(claude.InspectedReferences);
        Assert.DoesNotContain(SelectedRoot, first.SnapshotDigest, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_ClaudeSelection_UsesExactAdapterWithoutFallbackOrCandidateParsing()
    {
        var github = new RecordingGitHubFileSystem();
        var claude = new RecordingClaudeFileSystem
        {
            Inspection = ClaudeTranscriptReferenceInspection.RegularFile,
        };
        var gateway = new HistoricalImportSourceGateway(github, claude);
        var selection = ClaudeSelection(consentGranted: true);

        var result = gateway.Probe(selection);

        Assert.Equal("claude-code-history-v1", result.AdapterResult.AdapterId);
        Assert.Equal("claude-code-transcript", result.AdapterResult.ProfileId);
        Assert.Equal("claude-code", result.AdapterResult.SourceSurface);
        Assert.Equal(["historical_source_format_unsupported"], result.AdapterResult.Diagnostics);
        Assert.Null(result.CandidateBatch);
        Assert.Equal([selection.ExactReference!], claude.InspectedReferences);
        Assert.Empty(github.InspectedPaths);
    }

    [Fact]
    public void Probe_MissingConsent_NormalizesBothAdaptersToReferenceRequiredWithoutIo()
    {
        var github = ValidGitHubFileSystem();
        var claude = new RecordingClaudeFileSystem();
        var gateway = new HistoricalImportSourceGateway(github, claude);

        var githubResult = gateway.Probe(GitHubSelection() with { ConsentGranted = false });
        var claudeResult = gateway.Probe(ClaudeSelection(consentGranted: false));

        Assert.Equal(["historical_source_reference_required"], githubResult.AdapterResult.Diagnostics);
        Assert.Equal(["historical_source_reference_required"], claudeResult.AdapterResult.Diagnostics);
        Assert.Equal("not_evaluated", githubResult.AdapterResult.DetectionState);
        Assert.Equal("not_evaluated", claudeResult.AdapterResult.DetectionState);
        Assert.Null(githubResult.CandidateBatch);
        Assert.Null(claudeResult.CandidateBatch);
        Assert.Empty(github.InspectedPaths);
        Assert.Empty(claude.InspectedReferences);
    }

    [Fact]
    public void Probe_UnknownSurface_FailsClosedBeforeSourceIo()
    {
        var github = ValidGitHubFileSystem();
        var claude = new RecordingClaudeFileSystem();
        var gateway = new HistoricalImportSourceGateway(github, claude);

        var error = Assert.Throws<HistoricalImportException>(() => gateway.Probe(
            GitHubSelection() with { SourceSurface = "unknown-surface" }));
        Assert.Equal(HistoricalImportErrorCodes.RequestInvalid, error.Code);

        Assert.Empty(github.InspectedPaths);
        Assert.Empty(claude.InspectedReferences);
    }

    [Fact]
    public void Probe_NonCanonicalHostLocalReference_FailsBeforeAdapterDispatch()
    {
        var github = ValidGitHubFileSystem();
        var claude = new RecordingClaudeFileSystem();
        var gateway = new HistoricalImportSourceGateway(github, claude);
        var foreignReference = OperatingSystem.IsWindows()
            ? "/private/copilot"
            : @"C:\private\copilot";

        var error = Assert.Throws<HistoricalImportException>(() => gateway.Probe(
            GitHubSelection() with { ExactReference = foreignReference }));

        Assert.Equal(HistoricalImportErrorCodes.RequestInvalid, error.Code);
        Assert.Empty(github.InspectedPaths);
        Assert.Empty(claude.InspectedReferences);
    }

    private static HistoricalSourceSelection GitHubSelection() => new(
        SourceSurface: "github-copilot-cli",
        ReferenceKind: "selected_root",
        ExactReference: SelectedRoot,
        SessionId: "session-123",
        SourceApplicationVersion: "1.0.71",
        RequestedCapture: "metadata_only",
        ConsentGranted: true);

    private static HistoricalSourceSelection ClaudeSelection(bool consentGranted) => new(
        SourceSurface: "claude-code",
        ReferenceKind: "explicit_user_selection",
        ExactReference: Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "historical-import-gateway-tests",
            "transcript.jsonl")),
        SessionId: null,
        SourceApplicationVersion: "2.1.215",
        RequestedCapture: "metadata_only",
        ConsentGranted: consentGranted);

    private static RecordingGitHubFileSystem ValidGitHubFileSystem()
    {
        var fileSystem = new RecordingGitHubFileSystem();
        fileSystem.Kinds.AddRange([
            GitHubCopilotHistoricalPathKind.Directory,
            GitHubCopilotHistoricalPathKind.Directory,
            GitHubCopilotHistoricalPathKind.Directory,
            GitHubCopilotHistoricalPathKind.RegularFile,
        ]);
        return fileSystem;
    }

    private sealed class RecordingGitHubFileSystem : IGitHubCopilotHistoricalMetadataFileSystem
    {
        internal List<GitHubCopilotHistoricalPathKind> Kinds { get; } = [];
        internal List<string> InspectedPaths { get; } = [];

        public GitHubCopilotHistoricalPathKind InspectPath(string path)
        {
            InspectedPaths.Add(path);
            var index = InspectedPaths.Count - 1;
            return Kinds.Count == 0 ? GitHubCopilotHistoricalPathKind.Missing : Kinds[index % Kinds.Count];
        }
    }

    private sealed class RecordingClaudeFileSystem : IClaudeHistoricalFileSystem
    {
        internal ClaudeTranscriptReferenceInspection Inspection { get; init; } =
            ClaudeTranscriptReferenceInspection.Missing;
        internal List<string> InspectedReferences { get; } = [];

        public ClaudeTranscriptReferenceInspection InspectExactReference(string exactReference)
        {
            InspectedReferences.Add(exactReference);
            return Inspection;
        }
    }
}

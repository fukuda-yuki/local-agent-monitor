using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.HistoricalImport.GitHubCopilot;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotHistoricalAdapterTests
{
    private const string SelectedRoot = @"C:\selected-copilot-root";

    [Fact]
    public void Probe_WithoutConsent_DoesNotTouchTheFileSystem()
    {
        var fileSystem = new RecordingMetadataFileSystem();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SelectedRoot,
            "session-123",
            "1.0.71",
            ConsentGranted: false,
            RequestedCapture: "metadata_only"));

        Assert.Empty(fileSystem.InspectedPaths);
        AssertMissingReference(probe);
    }

    [Theory]
    [InlineData(null, "session-123")]
    [InlineData("", "session-123")]
    [InlineData(SelectedRoot, null)]
    [InlineData(SelectedRoot, "")]
    public void Probe_WithoutAnExactSelection_DoesNotTouchTheFileSystem(
        string? selectedRoot,
        string? sessionId)
    {
        var fileSystem = new RecordingMetadataFileSystem();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            selectedRoot,
            sessionId,
            "1.0.71",
            ConsentGranted: true,
            RequestedCapture: "metadata_only"));

        Assert.Empty(fileSystem.InspectedPaths);
        AssertMissingReference(probe);
    }

    [Fact]
    public void Probe_InspectsOnlyTheFixedDocumentedContainerReference()
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        adapter.Probe(ValidRequest("1.0.71"));

        Assert.Equal(
            [
                SelectedRoot,
                Path.Combine(SelectedRoot, "session-state"),
                Path.Combine(SelectedRoot, "session-state", "session-123"),
                Path.Combine(SelectedRoot, "session-state", "session-123", "events.jsonl"),
            ],
            fileSystem.InspectedPaths);
    }

    [Theory]
    [InlineData("1.0.71")]
    [InlineData("1.0.73")]
    public void Probe_DetectedExactVersion_RemainsUnsupportedWithoutReadingContent(string version)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(ValidRequest(version));

        Assert.Equal("detected", probe.AdapterResult.DetectionState);
        Assert.Equal("provided", probe.AdapterResult.SourceReferenceState);
        Assert.Equal(version, probe.AdapterResult.SourceApplicationVersion);
        Assert.False(probe.AdapterResult.SupportAuthorized);
        Assert.Equal("none", probe.AdapterResult.SourceFormatProfile);
        Assert.Equal(0, probe.AdapterResult.CandidateCount);
        Assert.Equal("not_read", probe.AdapterResult.ContentRisk);
        Assert.Equal(["historical_source_format_unsupported"], probe.AdapterResult.Diagnostics);
        Assert.Equal(4, fileSystem.InspectedPaths.Count);
    }

    [Theory]
    [InlineData("relative-root", "session-123")]
    [InlineData(@"C:\selected-copilot-root\..\adjacent", "session-123")]
    [InlineData(SelectedRoot, ".")]
    [InlineData(SelectedRoot, "..")]
    [InlineData(SelectedRoot, "nested/session")]
    [InlineData(SelectedRoot, "nested\\session")]
    [InlineData(SelectedRoot, "session:123")]
    [InlineData(SelectedRoot, "invalid version")]
    public void Probe_MalformedRequestReference_FailsClosedWithoutIo(string root, string value)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);
        var request = value == "invalid version"
            ? new GitHubCopilotHistoricalProbeRequest(root, "session-123", value, true, "metadata_only")
            : new GitHubCopilotHistoricalProbeRequest(root, value, "1.0.71", true, "metadata_only");

        var probe = adapter.Probe(request);

        Assert.Empty(fileSystem.InspectedPaths);
        AssertMalformed(probe);
    }

    [Theory]
    [InlineData("include_content")]
    [InlineData("")]
    [InlineData("METADATA_ONLY")]
    public void Probe_NonMetadataCapture_FailsClosedWithoutIo(string requestedCapture)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SelectedRoot,
            "session-123",
            "1.0.71",
            true,
            requestedCapture));

        Assert.Empty(fileSystem.InspectedPaths);
        AssertMalformed(probe);
    }

    [Fact]
    public void Probe_OversizeReference_FailsClosedWithoutIo()
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SelectedRoot,
            new string('s', 256),
            "1.0.71",
            true,
            "metadata_only"));

        Assert.Empty(fileSystem.InspectedPaths);
        AssertMalformed(probe);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 3)]
    [InlineData(2, 0)]
    [InlineData(2, 2)]
    [InlineData(3, 0)]
    [InlineData(3, 1)]
    [InlineData(3, 3)]
    public void Probe_InvalidContainerShape_FailsClosed(
        int invalidIndex,
        int invalidKindValue)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        fileSystem.Kinds[invalidIndex] = (GitHubCopilotHistoricalPathKind)invalidKindValue;
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(ValidRequest("1.0.71"));

        AssertMalformed(probe);
        Assert.InRange(fileSystem.InspectedPaths.Count, 1, invalidIndex + 1);
    }

    [Fact]
    public void Probe_PermissionFailure_UsesFixedSanitizedDiagnostic()
    {
        const string SensitivePath = @"C:\Users\person\secret-root";
        var fileSystem = new RecordingMetadataFileSystem
        {
            Failure = new UnauthorizedAccessException($"denied: {SensitivePath}"),
        };
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SensitivePath,
            "private-session",
            "1.0.73",
            true,
            "metadata_only"));

        AssertMalformed(probe);
        var serialized = Encoding.UTF8.GetString(probe.AdapterResultJson);
        Assert.DoesNotContain(SensitivePath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("denied", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_ProducesDeterministicContractBytes()
    {
        var first = new GitHubCopilotHistoricalAdapter(RecordingMetadataFileSystem.WithValidContainer())
            .Probe(ValidRequest("1.0.71"));
        var second = new GitHubCopilotHistoricalAdapter(RecordingMetadataFileSystem.WithValidContainer())
            .Probe(ValidRequest("1.0.71"));

        Assert.Equal(first.AdapterResultJson, second.AdapterResultJson);
        Assert.Equal(first.ImportPreviewJson, second.ImportPreviewJson);

        using var result = JsonDocument.Parse(first.AdapterResultJson);
        Assert.Equal("historical-adapter-result/v1", result.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal(14, result.RootElement.EnumerateObject().Count());

        using var preview = JsonDocument.Parse(first.ImportPreviewJson);
        Assert.Equal("historical-import-preview/v1", preview.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal(11, preview.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Probe_AlwaysProducesTheZeroCandidateIssue79PreviewWithoutMutationFields()
    {
        var probe = new GitHubCopilotHistoricalAdapter(RecordingMetadataFileSystem.WithValidContainer())
            .Probe(ValidRequest("1.0.73"));

        Assert.Equal(0, probe.ImportPreview.EligibleCandidateCount);
        Assert.Equal(0, probe.ImportPreview.RejectedCandidateCount);
        Assert.False(probe.ImportPreview.CommitAllowed);
        Assert.Equal("historical_import_no_eligible_candidates", probe.ImportPreview.RejectionCode);
        Assert.Equal("not_read", probe.ImportPreview.ContentRisk);

        var allJson = Encoding.UTF8.GetString(probe.AdapterResultJson) + Encoding.UTF8.GetString(probe.ImportPreviewJson);
        Assert.DoesNotContain("candidate_key", allJson, StringComparison.Ordinal);
        Assert.DoesNotContain("source_record", allJson, StringComparison.Ordinal);
        Assert.DoesNotContain("trace_id", allJson, StringComparison.Ordinal);
        Assert.DoesNotContain("span_id", allJson, StringComparison.Ordinal);
        Assert.DoesNotContain("session_id", allJson, StringComparison.Ordinal);
        Assert.DoesNotContain("path", allJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("write", allJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("commit_token", allJson, StringComparison.Ordinal);
    }

    private static GitHubCopilotHistoricalProbeRequest ValidRequest(string version) =>
        new(SelectedRoot, "session-123", version, true, "metadata_only");

    private static void AssertMissingReference(GitHubCopilotHistoricalProbe probe)
    {
        Assert.Equal("not_evaluated", probe.AdapterResult.DetectionState);
        Assert.Equal("missing", probe.AdapterResult.SourceReferenceState);
        Assert.Null(probe.AdapterResult.SourceApplicationVersion);
        Assert.Equal(["historical_source_reference_required"], probe.AdapterResult.Diagnostics);
        AssertZeroCandidate(probe);
    }

    private static void AssertMalformed(GitHubCopilotHistoricalProbe probe)
    {
        Assert.False(probe.AdapterResult.SupportAuthorized);
        Assert.Equal(0, probe.AdapterResult.CandidateCount);
        Assert.Equal("not_read", probe.AdapterResult.ContentRisk);
        Assert.Equal(["historical_source_malformed"], probe.AdapterResult.Diagnostics);
        AssertZeroCandidate(probe);
    }

    private static void AssertZeroCandidate(GitHubCopilotHistoricalProbe probe)
    {
        Assert.Equal(0, probe.ImportPreview.EligibleCandidateCount);
        Assert.False(probe.ImportPreview.CommitAllowed);
        Assert.Equal("historical_import_no_eligible_candidates", probe.ImportPreview.RejectionCode);
        Assert.Equal("not_read", probe.ImportPreview.ContentRisk);
    }

    private sealed class RecordingMetadataFileSystem : IGitHubCopilotHistoricalMetadataFileSystem
    {
        public List<string> InspectedPaths { get; } = [];

        public List<GitHubCopilotHistoricalPathKind> Kinds { get; } = [];

        public Exception? Failure { get; init; }

        public static RecordingMetadataFileSystem WithValidContainer()
        {
            var fileSystem = new RecordingMetadataFileSystem();
            fileSystem.Kinds.AddRange([
                GitHubCopilotHistoricalPathKind.Directory,
                GitHubCopilotHistoricalPathKind.Directory,
                GitHubCopilotHistoricalPathKind.Directory,
                GitHubCopilotHistoricalPathKind.RegularFile,
            ]);
            return fileSystem;
        }

        public GitHubCopilotHistoricalPathKind InspectPath(string path)
        {
            InspectedPaths.Add(path);
            if (Failure is not null)
            {
                throw Failure;
            }

            var index = InspectedPaths.Count - 1;
            return index < Kinds.Count ? Kinds[index] : GitHubCopilotHistoricalPathKind.Missing;
        }
    }
}

using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.HistoricalImport.GitHubCopilot;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class GitHubCopilotHistoricalAdapterTests
{
    private static readonly string SelectedRoot = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "copilot-history-adapter-tests",
        "selected-copilot-root"));
    private static readonly string ContractRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "historical-import", "v1");

    [Fact]
    public void ValidSelectedRootFixture_IsPlatformNativeAndCanonical()
    {
        Assert.True(Path.IsPathFullyQualified(SelectedRoot));
        Assert.Equal(Path.GetFullPath(SelectedRoot), SelectedRoot);
    }

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
        Assert.Null(probe);
    }

    [Theory]
    [MemberData(nameof(MissingSelections))]
    public void Probe_WithoutAnExactSelection_DoesNotTouchTheFileSystem(
        string? selectedRoot,
        string? sessionId)
    {
        var fileSystem = new RecordingMetadataFileSystem();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            selectedRoot,
            sessionId,
            "1.0.71",
            ConsentGranted: true,
            RequestedCapture: "metadata_only")));

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
    [InlineData("1.0.71", true)]
    [InlineData("1.0.73", false)]
    [InlineData("1.0.73-rc.1+build.5", false)]
    public void Probe_DetectedExactVersion_RemainsUnsupportedWithoutReadingContent(
        string version,
        bool observedByProfile)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(ValidRequest(version)));

        Assert.Equal("detected", probe.AdapterResult.DetectionState);
        Assert.Equal("provided", probe.AdapterResult.SourceReferenceState);
        Assert.Equal(version, probe.AdapterResult.SourceApplicationVersion);
        Assert.False(probe.AdapterResult.SupportAuthorized);
        Assert.Equal("none", probe.AdapterResult.SourceFormatProfile);
        Assert.Equal(0, probe.AdapterResult.CandidateCount);
        Assert.Equal("not_read", probe.AdapterResult.ContentRisk);
        Assert.Equal(["historical_source_format_unsupported"], probe.AdapterResult.Diagnostics);
        Assert.Equal(4, fileSystem.InspectedPaths.Count);
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            ContractRoot,
            "profiles",
            "github-copilot-cli-session-state.json")));
        Assert.Equal(
            observedByProfile,
            profile.RootElement.GetProperty("observed_detector_versions")
                .EnumerateArray()
                .Any(item => item.GetString() == version));
    }

    [Theory]
    [InlineData("01.0.71")]
    [InlineData("1.0.71-")]
    [InlineData("1.0.71+")]
    [InlineData("1.0.71-01")]
    [InlineData("1.0.71-alpha..1")]
    [InlineData("1.0.71+build..1")]
    [InlineData("v1.0.71")]
    [InlineData("build_2026")]
    public void Probe_MetadataTokenThatIsNotSemanticVersion_IsProbedButNotProjected(string version)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(ValidRequest(version)));

        Assert.Null(probe.AdapterResult.SourceApplicationVersion);
        Assert.Equal("detected", probe.AdapterResult.DetectionState);
        Assert.Equal(["historical_source_format_unsupported"], probe.AdapterResult.Diagnostics);
        Assert.Equal(4, fileSystem.InspectedPaths.Count);
    }

    [Theory]
    [MemberData(nameof(MalformedRequests))]
    public void Probe_MalformedRequestReference_FailsClosedWithoutIo(
        string root,
        string sessionId,
        string version)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);
        var request = new GitHubCopilotHistoricalProbeRequest(
            root,
            sessionId,
            version,
            true,
            "metadata_only");

        var probe = RequireProbe(adapter.Probe(request));

        Assert.Empty(fileSystem.InspectedPaths);
        AssertMalformed(probe);
    }

    [Fact]
    public void Probe_IncludeContent_IsRejectedBeforeAnySourceProbe()
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SelectedRoot,
            "session-123",
            "1.0.71",
            true,
            "include_content"));

        Assert.Null(probe);
        Assert.Empty(fileSystem.InspectedPaths);
    }

    [Theory]
    [InlineData("")]
    [InlineData("METADATA_ONLY")]
    [InlineData("include-content")]
    public void Probe_InvalidCapture_ProducesNoSchemaShapedPreviewOrIo(string requestedCapture)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SelectedRoot,
            "session-123",
            "1.0.71",
            true,
            requestedCapture));

        Assert.Null(probe);
        Assert.Empty(fileSystem.InspectedPaths);
    }

    [Fact]
    public void Probe_MaximumLengthSessionLocator_IsAccepted()
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SelectedRoot,
            new string('s', 256),
            "1.0.71",
            true,
            "metadata_only")));

        Assert.Equal(4, fileSystem.InspectedPaths.Count);
        Assert.Equal(["historical_source_format_unsupported"], probe.AdapterResult.Diagnostics);
    }

    [Fact]
    public void Probe_OversizeSessionLocator_FailsClosedWithoutIo()
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            SelectedRoot,
            new string('s', 257),
            "1.0.71",
            true,
            "metadata_only")));

        Assert.Empty(fileSystem.InspectedPaths);
        AssertMalformed(probe);
    }

    [Theory]
    [MemberData(nameof(UnsafeSourceRoots))]
    public void Probe_NonNativeOrDeviceRoot_FailsClosedBeforeFilesystemIo(string root)
    {
        var fileSystem = RecordingMetadataFileSystem.WithValidContainer();
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            root,
            "session-123",
            "1.0.71",
            true,
            "metadata_only")));

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

        var probe = RequireProbe(adapter.Probe(ValidRequest("1.0.71")));

        AssertMalformed(probe);
        Assert.InRange(fileSystem.InspectedPaths.Count, 1, invalidIndex + 1);
    }

    [Fact]
    public void Probe_PermissionFailure_UsesFixedSanitizedDiagnostic()
    {
        var sensitivePath = Path.Combine(SelectedRoot, "private-root");
        var fileSystem = new RecordingMetadataFileSystem
        {
            Failure = new UnauthorizedAccessException($"denied: {sensitivePath}"),
        };
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(new GitHubCopilotHistoricalProbeRequest(
            sensitivePath,
            "private-session",
            "1.0.73",
            true,
            "metadata_only")));

        AssertMalformed(probe);
        var serialized = Encoding.UTF8.GetString(probe.AdapterResultJson);
        Assert.Equal([sensitivePath], fileSystem.InspectedPaths);
        Assert.DoesNotContain(sensitivePath, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-session", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("denied", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Probe_UnexpectedNonFatalFailure_UsesFixedSanitizedDiagnostic()
    {
        const string SensitiveMessage = "secret record body and local path";
        var fileSystem = new RecordingMetadataFileSystem
        {
            Failure = new InvalidOperationException(SensitiveMessage),
        };
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var probe = RequireProbe(adapter.Probe(ValidRequest("1.0.71")));

        AssertMalformed(probe);
        var serialized = Encoding.UTF8.GetString(probe.AdapterResultJson) +
            Encoding.UTF8.GetString(probe.ImportPreviewJson);
        Assert.DoesNotContain(SensitiveMessage, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("InvalidOperationException", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ControlFlowMetadataFailures))]
    public void Probe_ControlFlowIsNotReportedAsASourceFailure(Exception failure)
    {
        var fileSystem = new RecordingMetadataFileSystem
        {
            Failure = failure,
        };
        var adapter = new GitHubCopilotHistoricalAdapter(fileSystem);

        var thrown = Assert.Throws(failure.GetType(), () => adapter.Probe(ValidRequest("1.0.71")));

        Assert.Same(failure, thrown);
        Assert.Equal([SelectedRoot], fileSystem.InspectedPaths);
    }

    public static TheoryData<Exception> ControlFlowMetadataFailures() => new()
    {
        new ThreadInterruptedException("thread interruption is control flow"),
        new OperationCanceledException("caller canceled metadata inspection"),
    };

    public static TheoryData<string?, string?> MissingSelections() => new()
    {
        { null, "session-123" },
        { string.Empty, "session-123" },
        { SelectedRoot, null },
        { SelectedRoot, string.Empty },
    };

    public static TheoryData<string, string, string> MalformedRequests() => new()
    {
        { "relative-root", "session-123", "1.0.71" },
        { Path.Combine(SelectedRoot, "..", "adjacent"), "session-123", "1.0.71" },
        { SelectedRoot, ".", "1.0.71" },
        { SelectedRoot, "..", "1.0.71" },
        { SelectedRoot, "nested/session", "1.0.71" },
        { SelectedRoot, "nested\\session", "1.0.71" },
        { SelectedRoot, "session:123", "1.0.71" },
        { SelectedRoot, "session 123", "1.0.71" },
        { SelectedRoot, "session\n123", "1.0.71" },
        { SelectedRoot, "-session-123", "1.0.71" },
        { SelectedRoot, "session-é", "1.0.71" },
        { SelectedRoot, "session-123", "invalid version" },
        { SelectedRoot, "session-123", "_1.0.71" },
        { SelectedRoot, "session-123", "1.0.71\n" },
        { SelectedRoot, "session-123", new string('v', 65) },
    };

    public static TheoryData<string> UnsafeSourceRoots()
    {
        var values = new TheoryData<string>
        {
            @"\\server\share\copilot",
            @"\\?\C:\private\copilot",
            @"\\.\C:\private\copilot",
        };
        if (OperatingSystem.IsWindows())
        {
            values.Add("/private/copilot");
            values.Add(@"C:\private\NUL\copilot");
            values.Add("C:/private/copilot");
            values.Add(SelectedRoot + "\\");
        }
        else
        {
            values.Add(@"C:\private\copilot");
            values.Add("//server/share/copilot");
            values.Add("/dev/copilot");
            values.Add("/proc/self/fd/0");
            values.Add(SelectedRoot + "/");
        }

        return values;
    }

    [Fact]
    public void Probe_ProducesDeterministicContractBytes()
    {
        var first = RequireProbe(new GitHubCopilotHistoricalAdapter(RecordingMetadataFileSystem.WithValidContainer())
            .Probe(ValidRequest("1.0.71")));
        var second = RequireProbe(new GitHubCopilotHistoricalAdapter(RecordingMetadataFileSystem.WithValidContainer())
            .Probe(ValidRequest("1.0.71")));

        Assert.Equal(first.AdapterResultJson, second.AdapterResultJson);
        Assert.Equal(first.ImportPreviewJson, second.ImportPreviewJson);

        using var result = JsonDocument.Parse(first.AdapterResultJson);
        Assert.Equal("historical-adapter-result/v1", result.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal(14, result.RootElement.EnumerateObject().Count());

        using var preview = JsonDocument.Parse(first.ImportPreviewJson);
        Assert.Equal("historical-import-preview/v1", preview.RootElement.GetProperty("contract_version").GetString());
        Assert.Equal(11, preview.RootElement.EnumerateObject().Count());

        Assert.Equal(
            "{\"contract_version\":\"historical-adapter-result/v1\",\"adapter_id\":\"github-copilot-cli-history-v1\",\"profile_id\":\"github-copilot-cli-session-state\",\"source_surface\":\"github-copilot-cli\",\"source_tier\":\"tier_b\",\"detection_state\":\"detected\",\"source_reference_state\":\"provided\",\"source_application_version\":\"1.0.71\",\"support_authorized\":false,\"source_format_profile\":\"none\",\"candidate_count\":0,\"content_risk\":\"not_read\",\"repository_safe\":true,\"diagnostics\":[\"historical_source_format_unsupported\"]}",
            Encoding.UTF8.GetString(first.AdapterResultJson));
        Assert.Equal(
            "{\"contract_version\":\"historical-import-preview/v1\",\"source_surface\":\"github-copilot-cli\",\"profile_id\":\"github-copilot-cli-session-state\",\"adapter_id\":\"github-copilot-cli-history-v1\",\"adapter_diagnostics\":[\"historical_source_format_unsupported\"],\"requested_capture\":\"metadata_only\",\"eligible_candidate_count\":0,\"rejected_candidate_count\":0,\"commit_allowed\":false,\"rejection_code\":\"historical_import_no_eligible_candidates\",\"content_risk\":\"not_read\"}",
            Encoding.UTF8.GetString(first.ImportPreviewJson));
    }

    [Fact]
    public void Probe_AlwaysProducesTheZeroCandidateIssue79PreviewWithoutMutationFields()
    {
        var probe = RequireProbe(new GitHubCopilotHistoricalAdapter(RecordingMetadataFileSystem.WithValidContainer())
            .Probe(ValidRequest("1.0.73")));

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

    private static GitHubCopilotHistoricalProbe RequireProbe(GitHubCopilotHistoricalProbe? probe) =>
        Assert.IsType<GitHubCopilotHistoricalProbe>(probe);

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
        AssertSchemaValid("historical-adapter-result.schema.json", probe.AdapterResultJson);
        AssertSchemaValid("historical-import-preview.schema.json", probe.ImportPreviewJson);
    }

    private static void AssertSchemaValid(string schemaFileName, byte[] json)
    {
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractRoot, schemaFileName)));
        using var value = JsonDocument.Parse(json);
        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, value));
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

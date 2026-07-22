using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeHistoricalAdapterTests
{
    private static readonly string ExactReference = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(),
        "claude-historical-adapter-tests",
        "transcript.jsonl"));
    private static readonly string ContractRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "historical-import", "v1");

    [Fact]
    public void Constructor_null_filesystem_is_rejected_at_the_boundary()
    {
        Assert.Throws<ArgumentNullException>(() => new ClaudeHistoricalAdapter(null!));
    }

    [Fact]
    public void Detector_filesystem_boundary_exposes_exact_reference_inspection_only()
    {
        var methods = typeof(IClaudeHistoricalFileSystem)
            .GetMethods()
            .Select(method => method.Name);

        Assert.Equal(["InspectExactReference"], methods);
    }

    [Fact]
    public void Missing_consent_returns_reference_required_without_filesystem_io()
    {
        var fileSystem = new RecordingFileSystem();
        var adapter = new ClaudeHistoricalAdapter(fileSystem);

        var result = adapter.Probe(new(
            new(ClaudeTranscriptReferenceKind.OfficialHook, "C:/private/transcript.jsonl"),
            Consent: null,
            SourceApplicationVersion: "2.1.215"));

        Assert.Equal("not_evaluated", result.DetectionState);
        Assert.Equal("missing", result.SourceReferenceState);
        Assert.Equal(["historical_source_reference_required"], result.Diagnostics);
        AssertNoFileSystemIo(fileSystem);
        AssertMatchesGolden(
            adapter.ProbeUtf8(new(
                new(ClaudeTranscriptReferenceKind.OfficialHook, "C:/private/transcript.jsonl"),
                Consent: null,
                SourceApplicationVersion: "2.1.215")),
            "missing-source-reference.json",
            "historical-adapter-result.schema.json");
    }

    [Fact]
    public void Include_content_consent_is_not_authorized_while_profile_is_unsupported()
    {
        var fileSystem = new RecordingFileSystem();
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        const string exactReference = "C:/private/transcript.jsonl";

        var result = adapter.Probe(new(
            new(ClaudeTranscriptReferenceKind.ExplicitUserSelection, exactReference),
            new(ClaudeTranscriptReferenceKind.ExplicitUserSelection, exactReference, "include_content"),
            "2.1.215"));

        Assert.Equal(["historical_source_reference_required"], result.Diagnostics);
        AssertNoFileSystemIo(fileSystem);
    }

    [Fact]
    public void Unknown_reference_kind_is_rejected_before_filesystem_io()
    {
        var fileSystem = new RecordingFileSystem();
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        const string exactReference = "C:/private/transcript.jsonl";
        var unknownKind = (ClaudeTranscriptReferenceKind)99;

        var result = adapter.Probe(new(
            new(unknownKind, exactReference),
            new(unknownKind, exactReference, "metadata_only"),
            "2.1.215"));

        Assert.Equal(["historical_source_reference_required"], result.Diagnostics);
        AssertNoFileSystemIo(fileSystem);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public void Consent_must_match_reference_kind_and_exact_reference(
        int referenceKindValue,
        int consentKindValue)
    {
        var referenceKind = (ClaudeTranscriptReferenceKind)referenceKindValue;
        var consentKind = (ClaudeTranscriptReferenceKind)consentKindValue;
        var fileSystem = new RecordingFileSystem();
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var reference = new ClaudeTranscriptReference(referenceKind, ExactReference);

        var wrongKind = adapter.Probe(new(
            reference,
            new(consentKind, reference.ExactReference, "metadata_only"),
            "2.1.215"));
        var wrongPath = adapter.Probe(new(
            reference,
            new(referenceKind, Path.Combine(Path.GetDirectoryName(ExactReference)!, "adjacent.jsonl"), "metadata_only"),
            "2.1.215"));

        Assert.Equal(["historical_source_reference_required"], wrongKind.Diagnostics);
        Assert.Equal(["historical_source_reference_required"], wrongPath.Diagnostics);
        AssertNoFileSystemIo(fileSystem);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Exact_authorized_reference_is_inspected_once_but_unsupported_body_is_not_read(
        int kindValue)
    {
        var kind = (ClaudeTranscriptReferenceKind)kindValue;
        var fileSystem = new RecordingFileSystem
        {
            Inspection = ClaudeTranscriptReferenceInspection.RegularFile
        };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var result = adapter.Probe(AuthorizedRequest(kind, ExactReference));

        Assert.Equal([ExactReference], fileSystem.InspectedReferences);
        Assert.Equal("detected", result.DetectionState);
        Assert.Equal("provided", result.SourceReferenceState);
        Assert.Equal("2.1.215", result.SourceApplicationVersion);
        Assert.False(result.SupportAuthorized);
        Assert.Equal("none", result.SourceFormatProfile);
        Assert.Equal(0, result.CandidateCount);
        Assert.Equal("not_read", result.ContentRisk);
        Assert.Equal(["historical_source_format_unsupported"], result.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(UnsafeExactReferences))]
    public void NonNativeOrDeviceReference_IsMalformedBeforeFilesystemIo(string exactReference)
    {
        var fileSystem = new RecordingFileSystem();
        var adapter = new ClaudeHistoricalAdapter(fileSystem);

        var result = adapter.Probe(AuthorizedRequest(
            ClaudeTranscriptReferenceKind.ExplicitUserSelection,
            exactReference));

        Assert.Equal("not_evaluated", result.DetectionState);
        Assert.Equal("provided", result.SourceReferenceState);
        Assert.Equal(["historical_source_malformed"], result.Diagnostics);
        AssertNoFileSystemIo(fileSystem);
    }

    [Theory]
    [InlineData("01.0.0")]
    [InlineData("2.1.215-")]
    [InlineData("2.1.215-01")]
    [InlineData("2.1.215-alpha..1")]
    [InlineData("2.1.215+")]
    [InlineData("2.1.215+build..1")]
    [InlineData("v2.1.215")]
    [InlineData("build_2026")]
    public void MetadataTokenThatIsNotSemanticVersion_IsProbedButNotProjected(string version)
    {
        var fileSystem = new RecordingFileSystem
        {
            Inspection = ClaudeTranscriptReferenceInspection.RegularFile,
        };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var request = AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            ExactReference) with
        {
            SourceApplicationVersion = version,
        };

        var result = adapter.Probe(request);

        Assert.Null(result.SourceApplicationVersion);
        Assert.Equal("detected", result.DetectionState);
        Assert.Equal(["historical_source_format_unsupported"], result.Diagnostics);
        Assert.Equal([ExactReference], fileSystem.InspectedReferences);
    }

    [Theory]
    [InlineData("invalid version")]
    [InlineData("_2.1.215")]
    [InlineData("2.1.215\n")]
    public void InvalidMetadataToken_IsMalformedBeforeFilesystemIo(string version)
    {
        var fileSystem = new RecordingFileSystem();
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var request = AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            ExactReference) with
        {
            SourceApplicationVersion = version,
        };

        var result = adapter.Probe(request);

        Assert.Null(result.SourceApplicationVersion);
        Assert.Equal(["historical_source_malformed"], result.Diagnostics);
        AssertNoFileSystemIo(fileSystem);
    }

    [Fact]
    public void OversizeMetadataToken_IsMalformedBeforeFilesystemIo()
    {
        var fileSystem = new RecordingFileSystem();
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var request = AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            ExactReference) with
        {
            SourceApplicationVersion = new string('v', 65),
        };

        var result = adapter.Probe(request);

        Assert.Null(result.SourceApplicationVersion);
        Assert.Equal(["historical_source_malformed"], result.Diagnostics);
        AssertNoFileSystemIo(fileSystem);
    }

    [Fact]
    public void SemanticVersionWithPrereleaseAndBuildMetadata_IsAcceptedExactly()
    {
        var fileSystem = new RecordingFileSystem
        {
            Inspection = ClaudeTranscriptReferenceInspection.RegularFile,
        };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var request = AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            ExactReference) with
        {
            SourceApplicationVersion = "2.1.215-rc.1+build.5",
        };

        var result = adapter.Probe(request);

        Assert.Equal("2.1.215-rc.1+build.5", result.SourceApplicationVersion);
        Assert.Equal([ExactReference], fileSystem.InspectedReferences);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Invalid_exact_reference_shape_is_malformed_without_body_read(
        int inspectionValue)
    {
        var inspection = (ClaudeTranscriptReferenceInspection)inspectionValue;
        var fileSystem = new RecordingFileSystem { Inspection = inspection };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);

        var result = adapter.Probe(AuthorizedRequest(
            ClaudeTranscriptReferenceKind.ExplicitUserSelection,
            ExactReference));

        Assert.Equal(["historical_source_malformed"], result.Diagnostics);
        AssertMatchesGolden(
            adapter.ProbeUtf8(AuthorizedRequest(
                ClaudeTranscriptReferenceKind.ExplicitUserSelection,
                ExactReference)),
            "malformed-source.json",
            "historical-adapter-result.schema.json");
    }

    [Theory]
    [MemberData(nameof(SanitizedInspectionFailures))]
    public void Permission_malformed_and_oversize_reference_failures_use_fixed_sanitized_diagnostic(Exception failure)
    {
        var fileSystem = new RecordingFileSystem { InspectionFailure = failure };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var sensitiveReference = Path.Combine(Path.GetDirectoryName(ExactReference)!, "secret-transcript.jsonl");

        var resultBytes = adapter.ProbeUtf8(AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            sensitiveReference));
        var json = Encoding.UTF8.GetString(resultBytes);

        Assert.Equal("historical_source_malformed", JsonDocument.Parse(resultBytes).RootElement
            .GetProperty("diagnostics")[0].GetString());
        Assert.DoesNotContain(sensitiveReference, json, StringComparison.Ordinal);
        Assert.DoesNotContain(failure.Message, json, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(NonFatalUnexpectedInspectionFailures))]
    public void Nonfatal_unexpected_metadata_failures_are_sanitized_and_fail_closed(Exception failure)
    {
        var fileSystem = new RecordingFileSystem { InspectionFailure = failure };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);

        var resultBytes = adapter.ProbeUtf8(AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            ExactReference));
        var json = Encoding.UTF8.GetString(resultBytes);
        using var result = JsonDocument.Parse(resultBytes);

        Assert.Equal("historical_source_malformed", result.RootElement
            .GetProperty("diagnostics")[0].GetString());
        Assert.False(result.RootElement.GetProperty("support_authorized").GetBoolean());
        Assert.Equal(0, result.RootElement.GetProperty("candidate_count").GetInt32());
        Assert.Equal("not_read", result.RootElement.GetProperty("content_risk").GetString());
        Assert.DoesNotContain(failure.Message, json, StringComparison.Ordinal);
        Assert.DoesNotContain("transcript.jsonl", json, StringComparison.Ordinal);
        AssertMatchesGolden(
            resultBytes,
            "malformed-source.json",
            "historical-adapter-result.schema.json");
    }

    [Theory]
    [MemberData(nameof(FatalOrControlFlowInspectionFailures))]
    public void Fatal_and_control_flow_metadata_failures_are_not_misreported_as_malformed(Exception failure)
    {
        var fileSystem = new RecordingFileSystem { InspectionFailure = failure };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);

        var thrown = Assert.Throws(failure.GetType(), () => adapter.Probe(AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            ExactReference)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public void Production_filesystem_invalid_metadata_reference_is_sanitized_without_opening_content()
    {
        var adapter = new ClaudeHistoricalAdapter(new SystemClaudeHistoricalFileSystem());

        var resultBytes = adapter.ProbeUtf8(AuthorizedRequest(
            ClaudeTranscriptReferenceKind.ExplicitUserSelection,
            "SYNTHETIC_INVALID\0TRANSCRIPT"));
        var json = Encoding.UTF8.GetString(resultBytes);

        Assert.Equal("historical_source_malformed", JsonDocument.Parse(resultBytes).RootElement
            .GetProperty("diagnostics")[0].GetString());
        Assert.DoesNotContain("SYNTHETIC_INVALID", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Unsupported_result_and_zero_candidate_preview_are_deterministic_and_contain_no_synthetic_authority()
    {
        var fileSystem = new RecordingFileSystem
        {
            Inspection = ClaudeTranscriptReferenceInspection.RegularFile
        };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        var request = AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            ExactReference);

        var firstResult = adapter.ProbeUtf8(request);
        var secondResult = adapter.ProbeUtf8(request);
        var result = adapter.Probe(request);
        var firstPreview = adapter.CreateZeroCandidatePreviewUtf8(result);
        var secondPreview = adapter.CreateZeroCandidatePreviewUtf8(result);

        Assert.Equal(firstResult, secondResult);
        Assert.Equal(firstPreview, secondPreview);
        AssertExactUnsupportedResult(firstResult);
        AssertMatchesGolden(
            firstResult,
            "detected-unsupported.json",
            "historical-adapter-result.schema.json");
        AssertExactZeroCandidatePreview(firstPreview);

        var combined = Encoding.UTF8.GetString(firstResult) + Encoding.UTF8.GetString(firstPreview);
        foreach (var forbidden in new[]
                 {
                     "trace_id", "span_id", "parent", "duration", "ttft", "agent_id",
                     "workspace", "source_path", "captured_at", "imported_at"
                 })
        {
            Assert.DoesNotContain(forbidden, combined, StringComparison.OrdinalIgnoreCase);
        }

    }

    public static TheoryData<Exception> SanitizedInspectionFailures() => new()
    {
        new UnauthorizedAccessException("denied at C:/Users/private/secret-transcript.jsonl"),
        new IOException("malformed source contains TOP_SECRET"),
        new PathTooLongException("oversize reference contains TOP_SECRET")
    };

    public static TheoryData<string> UnsafeExactReferences()
    {
        var values = new TheoryData<string>
        {
            @"\\server\share\transcript.jsonl",
            @"\\?\C:\private\transcript.jsonl",
            @"\\.\C:\private\transcript.jsonl",
            "relative-transcript.jsonl",
        };
        if (OperatingSystem.IsWindows())
        {
            values.Add("/private/transcript.jsonl");
            values.Add(@"C:\private\NUL.jsonl");
            values.Add("C:/private/transcript.jsonl");
            values.Add(ExactReference + "\\");
        }
        else
        {
            values.Add(@"C:\private\transcript.jsonl");
            values.Add("//server/share/transcript.jsonl");
            values.Add("/dev/null");
            values.Add("/proc/self/fd/0");
            values.Add(ExactReference + "/");
        }

        return values;
    }

    public static TheoryData<Exception> NonFatalUnexpectedInspectionFailures() => new()
    {
        new SecurityException("security policy exposed TOP_SECRET"),
        new InvalidOperationException("metadata adapter exposed TOP_SECRET")
    };

    public static TheoryData<Exception> FatalOrControlFlowInspectionFailures() => new()
    {
        new OutOfMemoryException("fatal runtime state"),
        new AccessViolationException("fatal runtime state"),
        new StackOverflowException("fatal runtime state"),
        new BadImageFormatException("fatal runtime state"),
        CreateThreadAbortException(),
        CreateExecutionEngineException(),
        new ThreadInterruptedException("thread interruption is control flow"),
        new OperationCanceledException("caller canceled metadata inspection")
    };

    private static Exception CreateThreadAbortException() =>
        (Exception)Activator.CreateInstance(typeof(System.Threading.ThreadAbortException), nonPublic: true)!;

    private static Exception CreateExecutionEngineException() =>
        (Exception)Activator.CreateInstance(
            Type.GetType("System.ExecutionEngineException", throwOnError: true)!)!;

    private static ClaudeHistoricalProbeRequest AuthorizedRequest(
        ClaudeTranscriptReferenceKind kind,
        string exactReference) => new(
            new(kind, exactReference),
            new(kind, exactReference, "metadata_only"),
            "2.1.215");

    private static void AssertNoFileSystemIo(RecordingFileSystem fileSystem)
    {
        Assert.Empty(fileSystem.InspectedReferences);
    }

    private static void AssertExactUnsupportedResult(byte[] utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(ContractRoot, "historical-adapter-result.schema.json")));
        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, document));
        var root = document.RootElement;
        Assert.Equal("historical-adapter-result/v1", root.GetProperty("contract_version").GetString());
        Assert.Equal("claude-code-history-v1", root.GetProperty("adapter_id").GetString());
        Assert.Equal("claude-code-transcript", root.GetProperty("profile_id").GetString());
        Assert.Equal("claude-code", root.GetProperty("source_surface").GetString());
        Assert.Equal("tier_b", root.GetProperty("source_tier").GetString());
        Assert.False(root.GetProperty("support_authorized").GetBoolean());
        Assert.Equal(0, root.GetProperty("candidate_count").GetInt32());
        Assert.True(root.GetProperty("repository_safe").GetBoolean());
        Assert.Equal(
        [
            "contract_version", "adapter_id", "profile_id", "source_surface", "source_tier",
            "detection_state", "source_reference_state", "source_application_version",
            "support_authorized", "source_format_profile", "candidate_count", "content_risk",
            "repository_safe", "diagnostics"
        ], root.EnumerateObject().Select(property => property.Name));
    }

    private static void AssertExactZeroCandidatePreview(byte[] utf8)
    {
        using var document = JsonDocument.Parse(utf8);
        using var schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(ContractRoot, "historical-import-preview.schema.json")));
        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, document));
        var root = document.RootElement;
        Assert.Equal("historical-import-preview/v1", root.GetProperty("contract_version").GetString());
        Assert.Equal(0, root.GetProperty("eligible_candidate_count").GetInt32());
        Assert.Equal(0, root.GetProperty("rejected_candidate_count").GetInt32());
        Assert.False(root.GetProperty("commit_allowed").GetBoolean());
        Assert.Equal("historical_import_no_eligible_candidates", root.GetProperty("rejection_code").GetString());
        Assert.Equal("not_read", root.GetProperty("content_risk").GetString());
    }

    private static void AssertMatchesGolden(byte[] actualUtf8, string fixtureName, string schemaName)
    {
        var fixturePath = Path.Combine(ContractRoot, "fixtures", "claude-code", fixtureName);
        using var actual = JsonDocument.Parse(actualUtf8);
        using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractRoot, schemaName)));
        Assert.Empty(HistoricalContractSchemaValidator.Validate(schema, actual));
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(actualUtf8), JsonNode.Parse(File.ReadAllText(fixturePath))),
            $"Production output did not match {fixtureName}.");
    }

    private sealed class RecordingFileSystem : IClaudeHistoricalFileSystem
    {
        public ClaudeTranscriptReferenceInspection Inspection { get; init; } =
            ClaudeTranscriptReferenceInspection.RegularFile;
        public Exception? InspectionFailure { get; init; }
        public List<string> InspectedReferences { get; } = [];

        public ClaudeTranscriptReferenceInspection InspectExactReference(string exactReference)
        {
            InspectedReferences.Add(exactReference);
            if (InspectionFailure is not null) throw InspectionFailure;
            return Inspection;
        }

    }
}

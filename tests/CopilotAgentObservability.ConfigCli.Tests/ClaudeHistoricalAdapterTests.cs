using System.Text;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;

namespace CopilotAgentObservability.ConfigCli.Tests;

public sealed class ClaudeHistoricalAdapterTests
{
    private static readonly string ContractRoot = Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "docs", "specifications", "contracts", "historical-import", "v1");

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
        var reference = new ClaudeTranscriptReference(referenceKind, "C:/private/transcript.jsonl");

        var wrongKind = adapter.Probe(new(
            reference,
            new(consentKind, reference.ExactReference, "metadata_only"),
            "2.1.215"));
        var wrongPath = adapter.Probe(new(
            reference,
            new(referenceKind, "C:/private/adjacent.jsonl", "metadata_only"),
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
        const string exactReference = "C:/private/transcript.jsonl";

        var result = adapter.Probe(AuthorizedRequest(kind, exactReference));

        Assert.Equal([exactReference], fileSystem.InspectedReferences);
        Assert.Equal(0, fileSystem.BodyReadCount);
        Assert.Equal(0, fileSystem.EnumerationCount);
        Assert.Equal(0, fileSystem.WriteCount);
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
            "C:/private/transcript.jsonl"));

        Assert.Equal(["historical_source_malformed"], result.Diagnostics);
        Assert.Equal(0, fileSystem.BodyReadCount);
        Assert.Equal(0, fileSystem.EnumerationCount);
        Assert.Equal(0, fileSystem.WriteCount);
    }

    [Theory]
    [MemberData(nameof(SanitizedInspectionFailures))]
    public void Permission_malformed_and_oversize_reference_failures_use_fixed_sanitized_diagnostic(Exception failure)
    {
        var fileSystem = new RecordingFileSystem { InspectionFailure = failure };
        var adapter = new ClaudeHistoricalAdapter(fileSystem);
        const string sensitiveReference = "C:/Users/private/secret-transcript.jsonl";

        var resultBytes = adapter.ProbeUtf8(AuthorizedRequest(
            ClaudeTranscriptReferenceKind.OfficialHook,
            sensitiveReference));
        var json = Encoding.UTF8.GetString(resultBytes);

        Assert.Equal("historical_source_malformed", JsonDocument.Parse(resultBytes).RootElement
            .GetProperty("diagnostics")[0].GetString());
        Assert.DoesNotContain(sensitiveReference, json, StringComparison.Ordinal);
        Assert.DoesNotContain(failure.Message, json, StringComparison.Ordinal);
        Assert.Equal(0, fileSystem.BodyReadCount);
        Assert.Equal(0, fileSystem.EnumerationCount);
        Assert.Equal(0, fileSystem.WriteCount);
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
            "C:/private/transcript.jsonl");

        var firstResult = adapter.ProbeUtf8(request);
        var secondResult = adapter.ProbeUtf8(request);
        var result = adapter.Probe(request);
        var firstPreview = adapter.CreateZeroCandidatePreviewUtf8(result);
        var secondPreview = adapter.CreateZeroCandidatePreviewUtf8(result);

        Assert.Equal(firstResult, secondResult);
        Assert.Equal(firstPreview, secondPreview);
        AssertExactUnsupportedResult(firstResult);
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

        Assert.Equal(0, fileSystem.BodyReadCount);
        Assert.Equal(0, fileSystem.EnumerationCount);
        Assert.Equal(0, fileSystem.WriteCount);
    }

    public static TheoryData<Exception> SanitizedInspectionFailures() => new()
    {
        new UnauthorizedAccessException("denied at C:/Users/private/secret-transcript.jsonl"),
        new IOException("malformed source contains TOP_SECRET"),
        new PathTooLongException("oversize reference contains TOP_SECRET")
    };

    private static ClaudeHistoricalProbeRequest AuthorizedRequest(
        ClaudeTranscriptReferenceKind kind,
        string exactReference) => new(
            new(kind, exactReference),
            new(kind, exactReference, "metadata_only"),
            "2.1.215");

    private static void AssertNoFileSystemIo(RecordingFileSystem fileSystem)
    {
        Assert.Empty(fileSystem.InspectedReferences);
        Assert.Equal(0, fileSystem.BodyReadCount);
        Assert.Equal(0, fileSystem.EnumerationCount);
        Assert.Equal(0, fileSystem.WriteCount);
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

    private sealed class RecordingFileSystem : IClaudeHistoricalFileSystem
    {
        public ClaudeTranscriptReferenceInspection Inspection { get; init; } =
            ClaudeTranscriptReferenceInspection.RegularFile;
        public Exception? InspectionFailure { get; init; }
        public List<string> InspectedReferences { get; } = [];
        public int BodyReadCount { get; private set; }
        public int EnumerationCount { get; private set; }
        public int WriteCount { get; private set; }

        public ClaudeTranscriptReferenceInspection InspectExactReference(string exactReference)
        {
            InspectedReferences.Add(exactReference);
            if (InspectionFailure is not null) throw InspectionFailure;
            return Inspection;
        }

        public Stream OpenTranscriptBody(string exactReference)
        {
            BodyReadCount++;
            throw new InvalidOperationException("Unsupported profile must not read transcript content.");
        }

        public IEnumerable<string> EnumerateReferences(string root)
        {
            EnumerationCount++;
            throw new InvalidOperationException("Claude adapter must not scan roots or adjacent storage.");
        }

        public void Write(string path, ReadOnlySpan<byte> content)
        {
            WriteCount++;
            throw new InvalidOperationException("Claude detector has no write authority.");
        }
    }
}

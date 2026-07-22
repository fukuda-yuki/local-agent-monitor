using System.Buffers;
using System.Text.Json;
using CopilotAgentObservability.ConfigCli.HistoricalImport;
using CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;

namespace CopilotAgentObservability.ConfigCli.HistoricalImport.ClaudeCode;

internal sealed class ClaudeHistoricalAdapter
{
    private const string AdapterId = "claude-code-history-v1";
    private const string ProfileId = "claude-code-transcript";
    private const string SourceSurface = "claude-code";

    private readonly IClaudeHistoricalFileSystem fileSystem;

    public ClaudeHistoricalAdapter(IClaudeHistoricalFileSystem fileSystem)
    {
        this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    public ClaudeHistoricalAdapterResult Probe(ClaudeHistoricalProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!HasExactMetadataOnlyConsent(request))
        {
            return new(
                "not_evaluated",
                "missing",
                null,
                "historical_source_reference_required");
        }

        var version = ToProducerVersion(request.SourceApplicationVersion);
        if (!HistoricalSourcePathPolicy.IsCanonicalNativeAbsolute(request.Reference.ExactReference)
            || !HistoricalSourceMetadataTokenPolicy.IsValid(request.SourceApplicationVersion))
        {
            return new(
                "not_evaluated",
                "provided",
                version,
                "historical_source_malformed");
        }

        try
        {
            var inspection = fileSystem.InspectExactReference(request.Reference.ExactReference);
            return inspection == ClaudeTranscriptReferenceInspection.RegularFile
                ? new(
                    "detected",
                    "provided",
                    version,
                    "historical_source_format_unsupported")
                : new(
                    "detected",
                    "provided",
                    version,
                    "historical_source_malformed");
        }
        catch (Exception exception) when (!IsFatalOrControlFlow(exception))
        {
            return new(
                "detected",
                "provided",
                version,
                "historical_source_malformed");
        }
    }

    public byte[] ProbeUtf8(ClaudeHistoricalProbeRequest request) => SerializeResult(Probe(request));

    public byte[] CreateZeroCandidatePreviewUtf8(ClaudeHistoricalAdapterResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("contract_version", "historical-import-preview/v1");
        writer.WriteString("source_surface", SourceSurface);
        writer.WriteString("profile_id", ProfileId);
        writer.WriteString("adapter_id", AdapterId);
        writer.WriteStartArray("adapter_diagnostics");
        foreach (var diagnostic in result.Diagnostics) writer.WriteStringValue(diagnostic);
        writer.WriteEndArray();
        writer.WriteString("requested_capture", "metadata_only");
        writer.WriteNumber("eligible_candidate_count", 0);
        writer.WriteNumber("rejected_candidate_count", 0);
        writer.WriteBoolean("commit_allowed", false);
        writer.WriteString("rejection_code", "historical_import_no_eligible_candidates");
        writer.WriteString("content_risk", "not_read");
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] SerializeResult(ClaudeHistoricalAdapterResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("contract_version", "historical-adapter-result/v1");
        writer.WriteString("adapter_id", AdapterId);
        writer.WriteString("profile_id", ProfileId);
        writer.WriteString("source_surface", SourceSurface);
        writer.WriteString("source_tier", "tier_b");
        writer.WriteString("detection_state", result.DetectionState);
        writer.WriteString("source_reference_state", result.SourceReferenceState);
        if (result.SourceApplicationVersion is null) writer.WriteNull("source_application_version");
        else writer.WriteString("source_application_version", result.SourceApplicationVersion);
        writer.WriteBoolean("support_authorized", result.SupportAuthorized);
        writer.WriteString("source_format_profile", result.SourceFormatProfile);
        writer.WriteNumber("candidate_count", result.CandidateCount);
        writer.WriteString("content_risk", result.ContentRisk);
        writer.WriteBoolean("repository_safe", true);
        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in result.Diagnostics) writer.WriteStringValue(diagnostic);
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private static bool HasExactMetadataOnlyConsent(ClaudeHistoricalProbeRequest request)
    {
        var reference = request.Reference;
        var consent = request.Consent;
        return consent is not null
            && reference.Kind is ClaudeTranscriptReferenceKind.OfficialHook
                or ClaudeTranscriptReferenceKind.ExplicitUserSelection
            && !string.IsNullOrWhiteSpace(reference.ExactReference)
            && consent.Kind == reference.Kind
            && string.Equals(consent.ExactReference, reference.ExactReference, StringComparison.Ordinal)
            && string.Equals(consent.RequestedCapture, "metadata_only", StringComparison.Ordinal);
    }

    private static string? ToProducerVersion(string? version) =>
        HistoricalImportSemanticVersionPolicy.IsValid(version) ? version : null;

    private static bool IsFatalOrControlFlow(Exception exception) =>
        IsLegacyFatal(exception) || exception is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException or
        AppDomainUnloadedException or
        BadImageFormatException or
        CannotUnloadAppDomainException or
        InvalidProgramException or
        System.Threading.ThreadAbortException or
        ThreadInterruptedException or
        OperationCanceledException;

#pragma warning disable CS0618 // Callers can still inject this obsolete fatal type even though the runtime no longer raises it.
    private static bool IsLegacyFatal(Exception exception) => exception is ExecutionEngineException;
#pragma warning restore CS0618

}

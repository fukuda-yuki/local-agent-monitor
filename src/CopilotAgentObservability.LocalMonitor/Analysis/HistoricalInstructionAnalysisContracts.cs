using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalInstructionAnalysisContractsV1
{
    internal const string RequestSchemaVersion = "historical-instruction-analysis.request.v1";
    internal const string ReceiptSchemaVersion = "historical-instruction-analysis.receipt.v1";
    internal const string ReadSchemaVersion = "historical-instruction-analysis.read.v1";
    internal const string PromptTemplateVersion = "historical-instruction-analysis.prompt.v1";
    internal const int MaximumTimeoutMilliseconds = 3_600_000;
    internal const int MaximumReceiptBytes = HistoricalEvidenceContractsV1.MaximumPayloadBytes;
}

internal enum HistoricalInstructionAnalysisStateV1
{
    Queued,
    Running,
    Succeeded,
    ZeroFindings,
    NoEligibleSessions,
    ContentUnavailable,
    StaleExtraction,
    ExtractionInvalid,
    InvalidCitation,
    ProviderPartial,
    ProviderFailed,
    TimedOut,
    Canceled,
}

internal enum HistoricalInstructionProviderCompletionV1 { Complete, Partial }
internal enum HistoricalInstructionSupportKindV1 { Recurring, SingleSession, InsufficientSupport }

internal enum HistoricalInstructionAnalysisValidationCodeV1
{
    InvalidContract,
    InvalidTransition,
    InvalidCitation,
    InvalidPersistence,
}

internal sealed class HistoricalInstructionAnalysisValidationException : Exception
{
    internal HistoricalInstructionAnalysisValidationException(HistoricalInstructionAnalysisValidationCodeV1 code)
        : base(code.ToString()) => Code = code;

    internal HistoricalInstructionAnalysisValidationException(
        HistoricalInstructionAnalysisValidationCodeV1 code,
        Exception innerException)
        : base(code.ToString(), innerException) => Code = code;

    internal HistoricalInstructionAnalysisValidationCodeV1 Code { get; }
}

internal sealed record HistoricalInstructionAnalysisRequestV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ExtractionId,
    [property: JsonPropertyOrder(2)] string ExtractionSha256,
    [property: JsonPropertyOrder(3)] string Model,
    [property: JsonPropertyOrder(4)] string Provider,
    [property: JsonPropertyOrder(5)] string ConfigurationSha256,
    [property: JsonPropertyOrder(6)] int TimeoutMilliseconds,
    [property: JsonPropertyOrder(7)] string PromptTemplateVersion);

internal sealed record HistoricalInstructionAnalysisDatasetProjectionV1(
    [property: JsonPropertyOrder(0)] bool TruncatedBefore,
    [property: JsonPropertyOrder(1)] bool SanitizedOnly,
    [property: JsonPropertyOrder(2)] bool ContentAvailable,
    [property: JsonPropertyOrder(3)] HistoricalEvidenceDistributionV1 DatasetDistribution);

internal sealed record HistoricalInstructionFindingSubmissionV1(
    InstructionFindingCategoryV1 Category,
    InstructionFindingVerdictV1 AssessedVerdict,
    InstructionFindingExtractorSourceV1 ExtractorSource,
    IReadOnlyList<InstructionRawEvidenceReferenceV1> EvidenceRefs,
    IReadOnlyList<string> SupportingGroupIds);

internal sealed record HistoricalInstructionProviderRequestV1(
    long RunId,
    HistoricalInstructionAnalysisRequestV1 Provenance,
    HistoricalEvidenceDatasetV1 Dataset,
    byte[] CanonicalDatasetBytes,
    string Prompt);

internal sealed record HistoricalInstructionProviderResultV1(
    HistoricalInstructionProviderCompletionV1 Completion,
    string AnchorTraceId,
    IReadOnlyList<HistoricalInstructionFindingSubmissionV1> Findings);

internal interface IHistoricalInstructionAnalysisProviderV1
{
    Task<HistoricalInstructionProviderResultV1> AnalyzeAsync(
        HistoricalInstructionProviderRequestV1 request,
        CancellationToken cancellationToken);
}

internal static class HistoricalInstructionAnalysisPromptV1
{
    internal const string Template =
        """
        Historical instruction analysis contract: historical-instruction-analysis.prompt.v1.
        Analyze only the supplied persisted historical-evidence.raw-local.v1 dataset. Do not search, query, or infer any Session, trace, repository, workspace, source, or history outside that dataset.
        Reuse only the instruction-finding.v1 categories: goal_clarity, ambiguity, acceptance_criteria_missing, scope_boundary_missing, task_too_large, test_requirement_missing, evidence_requirement_missing, and environment_assumption_missing.
        Every submission must use one shared exact anchor trace, exact evidence references from the unique Session owning that anchor (at least one anchor reference plus only same-Session non-anchor context valid under instruction-finding.v1), exact supporting group IDs, one closed verdict (supported, weak, or incomplete), and one closed extractor source (deterministic_prepass or prompt_only). Other Sessions are recurrence-only and cannot contribute final finding references.
        A recurring claim requires the same category to meet its instruction-finding.v1 evidence minimum independently inside at least two distinct included Sessions. Unrelated or under-minimum supporting groups do not count. One-Session support cannot be Recommended-equivalent. Do not upgrade weak or incomplete evidence.
        Submit no title, summary, explanation, instruction, target, rule text, source excerpt, prompt/response body, tool body, credential, personal data, or local path. Code generates fixed instruction-finding.v1 templates after exact citation validation.
        A complete response with zero submissions is valid. Mark an interrupted or incomplete response partial; do not present partial output as success.
        """;
}

internal sealed record HistoricalInstructionFindingSupportV1(
    [property: JsonPropertyOrder(0)] string FindingId,
    [property: JsonPropertyOrder(1)] InstructionFindingVerdictV1 Verdict,
    [property: JsonPropertyOrder(2)] InstructionCandidateEligibilityV1 CandidateEligibility,
    [property: JsonPropertyOrder(3)] HistoricalInstructionSupportKindV1 SupportKind,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> SupportingSessionIds,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string> SupportingGroupIds,
    [property: JsonPropertyOrder(6)] int RecurringCount,
    [property: JsonPropertyOrder(7)] IReadOnlyList<HistoricalDistributionCountV1> SourceSurfaceDistribution,
    [property: JsonPropertyOrder(8)] IReadOnlyList<HistoricalDistributionCountV1> SourceVersionDistribution,
    [property: JsonPropertyOrder(9)] IReadOnlyList<HistoricalDistributionCountV1> SourceKindDistribution,
    [property: JsonPropertyOrder(10)] IReadOnlyList<HistoricalDistributionCountV1> CompletenessDistribution,
    [property: JsonPropertyOrder(11)] IReadOnlyList<HistoricalEvidenceReferenceV1> EvidenceRefs);

internal sealed record HistoricalInstructionAnalysisReceiptV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] long RunId,
    [property: JsonPropertyOrder(2)] string ExtractionId,
    [property: JsonPropertyOrder(3)] string ExtractionSha256,
    [property: JsonPropertyOrder(4)] HistoricalInstructionAnalysisStateV1 State,
    [property: JsonPropertyOrder(5)] string Model,
    [property: JsonPropertyOrder(6)] string Provider,
    [property: JsonPropertyOrder(7)] string ConfigurationSha256,
    [property: JsonPropertyOrder(8)] int TimeoutMilliseconds,
    [property: JsonPropertyOrder(9)] string PromptTemplateVersion,
    [property: JsonPropertyOrder(10)] bool TruncatedBefore,
    [property: JsonPropertyOrder(11)] bool SanitizedOnly,
    [property: JsonPropertyOrder(12)] bool ContentAvailable,
    [property: JsonPropertyOrder(13)] HistoricalEvidenceDistributionV1 DatasetDistribution,
    [property: JsonPropertyOrder(14)] string HandoffSha256,
    [property: JsonPropertyOrder(15)] IReadOnlyList<HistoricalInstructionFindingSupportV1> Findings);

internal sealed record HistoricalInstructionAnalysisRunV1(
    long RunId,
    HistoricalInstructionAnalysisRequestV1 Request,
    HistoricalInstructionAnalysisDatasetProjectionV1 DatasetProjection,
    HistoricalInstructionAnalysisStateV1 State,
    DateTimeOffset RequestedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    HistoricalInstructionAnalysisReceiptV1? Receipt,
    byte[] HandoffBytes)
{
    internal HistoricalInstructionAnalysisReadV1 ToRead() =>
        new(
            HistoricalInstructionAnalysisContractsV1.ReadSchemaVersion,
            RunId,
            Request,
            DatasetProjection,
            State,
            RequestedAt,
            StartedAt,
            CompletedAt,
            Receipt,
            HandoffBytes.ToArray());
}

internal sealed record HistoricalInstructionAnalysisReadV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] long RunId,
    [property: JsonPropertyOrder(2)] HistoricalInstructionAnalysisRequestV1 Request,
    [property: JsonPropertyOrder(3)] HistoricalInstructionAnalysisDatasetProjectionV1 DatasetProjection,
    [property: JsonPropertyOrder(4)] HistoricalInstructionAnalysisStateV1 State,
    [property: JsonPropertyOrder(5)] DateTimeOffset RequestedAt,
    [property: JsonPropertyOrder(6)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset? CompletedAt,
    [property: JsonPropertyOrder(8)] HistoricalInstructionAnalysisReceiptV1? Receipt,
    [property: JsonPropertyOrder(9)] byte[] HandoffBytes);

internal static class HistoricalInstructionAnalysisStateWireV1
{
    internal static string ToWireValue(this HistoricalInstructionAnalysisStateV1 state) => state switch
    {
        HistoricalInstructionAnalysisStateV1.Queued => "queued",
        HistoricalInstructionAnalysisStateV1.Running => "running",
        HistoricalInstructionAnalysisStateV1.Succeeded => "succeeded",
        HistoricalInstructionAnalysisStateV1.ZeroFindings => "zero_findings",
        HistoricalInstructionAnalysisStateV1.NoEligibleSessions => "no_eligible_sessions",
        HistoricalInstructionAnalysisStateV1.ContentUnavailable => "content_unavailable",
        HistoricalInstructionAnalysisStateV1.StaleExtraction => "stale_extraction",
        HistoricalInstructionAnalysisStateV1.ExtractionInvalid => "extraction_invalid",
        HistoricalInstructionAnalysisStateV1.InvalidCitation => "invalid_citation",
        HistoricalInstructionAnalysisStateV1.ProviderPartial => "provider_partial",
        HistoricalInstructionAnalysisStateV1.ProviderFailed => "provider_failed",
        HistoricalInstructionAnalysisStateV1.TimedOut => "timed_out",
        HistoricalInstructionAnalysisStateV1.Canceled => "canceled",
        _ => throw new HistoricalInstructionAnalysisValidationException(HistoricalInstructionAnalysisValidationCodeV1.InvalidContract),
    };

    internal static HistoricalInstructionAnalysisStateV1 Parse(string value) => value switch
    {
        "queued" => HistoricalInstructionAnalysisStateV1.Queued,
        "running" => HistoricalInstructionAnalysisStateV1.Running,
        "succeeded" => HistoricalInstructionAnalysisStateV1.Succeeded,
        "zero_findings" => HistoricalInstructionAnalysisStateV1.ZeroFindings,
        "no_eligible_sessions" => HistoricalInstructionAnalysisStateV1.NoEligibleSessions,
        "content_unavailable" => HistoricalInstructionAnalysisStateV1.ContentUnavailable,
        "stale_extraction" => HistoricalInstructionAnalysisStateV1.StaleExtraction,
        "extraction_invalid" => HistoricalInstructionAnalysisStateV1.ExtractionInvalid,
        "invalid_citation" => HistoricalInstructionAnalysisStateV1.InvalidCitation,
        "provider_partial" => HistoricalInstructionAnalysisStateV1.ProviderPartial,
        "provider_failed" => HistoricalInstructionAnalysisStateV1.ProviderFailed,
        "timed_out" => HistoricalInstructionAnalysisStateV1.TimedOut,
        "canceled" => HistoricalInstructionAnalysisStateV1.Canceled,
        _ => throw new HistoricalInstructionAnalysisValidationException(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence),
    };
}

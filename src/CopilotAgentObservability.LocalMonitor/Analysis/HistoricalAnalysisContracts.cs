using System.Text.Json.Serialization;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalAnalysisContractsV1
{
    internal const string PreviewRequestSchemaVersion = "historical-analysis-preview.request.v1";
    internal const string PreviewResponseSchemaVersion = "historical-analysis-preview.response.v1";
    internal const string InstructionStartRequestSchemaVersion = "historical-analysis-instruction-start.request.v1";
    internal const string InstructionStartResponseSchemaVersion = "historical-analysis-instruction-start.response.v1";
    internal const string EfficiencyStartRequestSchemaVersion = "historical-analysis-efficiency-start.request.v1";
    internal const string EfficiencyStartResponseSchemaVersion = "historical-analysis-efficiency-start.response.v1";
    internal const string EfficiencyStatusSchemaVersion = "historical-analysis-efficiency-status.v1";
    internal const string EvidenceResolveRequestSchemaVersion = "historical-analysis-evidence-resolve.request.v1";
    internal const string EvidenceResolveResponseSchemaVersion = "historical-analysis-evidence-resolve.response.v1";
    internal const string ErrorSchemaVersion = "historical-analysis-error.v1";
    internal const int MaximumRequestBytes = 1_048_576;
    internal const int MaximumEvidenceReferences = 16;
}

internal static class HistoricalAnalysisErrorCodesV1
{
    internal const string InvalidRequest = "invalid_historical_analysis_request";
    internal const string RunNotFound = "historical_analysis_run_not_found";
    internal const string ExtractionNotFound = "historical_extraction_not_found";
    internal const string StaleExtraction = "stale_extraction";
    internal const string ProviderUnavailable = "provider_unavailable";
    internal const string PreconditionFailed = "precondition_failed";
    internal const string StoreUnavailable = "historical_analysis_store_unavailable";
}

internal sealed class HistoricalAnalysisException : Exception
{
    internal HistoricalAnalysisException(string code) : base(code) => Code = code;
    internal HistoricalAnalysisException(string code, Exception innerException) : base(code, innerException) => Code = code;

    internal string Code { get; }
}

internal sealed record HistoricalAnalysisPreviewRequestV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] HistoricalEvidenceSelectionV1 Selection);

internal sealed record HistoricalAnalysisPreviewResponseV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ExtractionId,
    [property: JsonPropertyOrder(2)] string RawLocalSha256,
    [property: JsonPropertyOrder(3)] string RepositorySafeSha256,
    [property: JsonPropertyOrder(4)] HistoricalEvidenceSelectionProjectionV1 Selection,
    [property: JsonPropertyOrder(5)] IReadOnlyList<HistoricalEvidenceSessionV1> Included,
    [property: JsonPropertyOrder(6)] IReadOnlyList<HistoricalExcludedSessionV1> Excluded,
    [property: JsonPropertyOrder(7)] bool TruncatedBefore,
    [property: JsonPropertyOrder(8)] long TruncatedSessionCount);

internal sealed record HistoricalAnalysisInstructionStartRequestV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ExtractionId,
    [property: JsonPropertyOrder(2)] string RawLocalSha256,
    [property: JsonPropertyOrder(3)] string Model,
    [property: JsonPropertyOrder(4)] string Provider,
    [property: JsonPropertyOrder(5)] string ConfigurationSha256,
    [property: JsonPropertyOrder(6)] int TimeoutMs,
    [property: JsonPropertyOrder(7)] string PromptTemplateVersion);

internal sealed record HistoricalAnalysisInstructionStartResponseV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string AnalysisRunId,
    [property: JsonPropertyOrder(2)] string State);

internal sealed record HistoricalAnalysisEfficiencyStartRequestV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ExtractionId,
    [property: JsonPropertyOrder(2)] string RepositorySafeSha256);

internal sealed record HistoricalAnalysisEfficiencyStartResponseV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string AnalysisRunId,
    [property: JsonPropertyOrder(2)] string State);

internal sealed record HistoricalAnalysisEfficiencyStatusResponseV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string AnalysisRunId,
    [property: JsonPropertyOrder(2)] string ExtractionId,
    [property: JsonPropertyOrder(3)] string RepositorySafeSha256,
    [property: JsonPropertyOrder(4)] string State,
    [property: JsonPropertyOrder(5)] DateTimeOffset RequestedAt,
    [property: JsonPropertyOrder(6)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset? CompletedAt,
    [property: JsonPropertyOrder(8)] HistoricalEfficiencyReceiptV1? Receipt,
    [property: JsonPropertyOrder(9)] string? ReceiptPayloadSha256);

internal sealed record HistoricalAnalysisEvidenceResolveRequestV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ExtractionId,
    [property: JsonPropertyOrder(2)] string RepositorySafeSha256,
    [property: JsonPropertyOrder(3)] IReadOnlyList<string> References);

internal sealed record HistoricalAnalysisEvidenceResolveResponseV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] IReadOnlyList<HistoricalAnalysisEvidenceResolutionV1> Resolutions);

internal sealed record HistoricalAnalysisEvidenceResolutionV1(
    [property: JsonPropertyOrder(0)] string Reference,
    [property: JsonPropertyOrder(1)] string ResolutionState,
    [property: JsonPropertyOrder(2)] string ContentState,
    [property: JsonPropertyOrder(3)] string? Target);

internal enum HistoricalAnalysisEfficiencyStateV1
{
    Queued,
    Running,
    Succeeded,
    ZeroDrivers,
    StaleExtraction,
    AnalysisFailed,
    TimedOut,
    Canceled,
}

internal static class HistoricalAnalysisEfficiencyStateWireV1
{
    internal static string ToWireValue(HistoricalAnalysisEfficiencyStateV1 state) => state switch
    {
        HistoricalAnalysisEfficiencyStateV1.Queued => "queued",
        HistoricalAnalysisEfficiencyStateV1.Running => "running",
        HistoricalAnalysisEfficiencyStateV1.Succeeded => "succeeded",
        HistoricalAnalysisEfficiencyStateV1.ZeroDrivers => "zero_drivers",
        HistoricalAnalysisEfficiencyStateV1.StaleExtraction => "stale_extraction",
        HistoricalAnalysisEfficiencyStateV1.AnalysisFailed => "analysis_failed",
        HistoricalAnalysisEfficiencyStateV1.TimedOut => "timed_out",
        HistoricalAnalysisEfficiencyStateV1.Canceled => "canceled",
        _ => throw new HistoricalAnalysisException(HistoricalAnalysisErrorCodesV1.InvalidRequest),
    };
}

internal interface IHistoricalEfficiencyExecutorV1
{
    Task<HistoricalEfficiencyAnalysisV1> AnalyzeAsync(
        HistoricalEvidenceExtractionV1 extraction,
        CancellationToken cancellationToken);
}

internal sealed record HistoricalAnalysisInstructionRequestReadV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ExtractionId,
    [property: JsonPropertyOrder(2)] string ExtractionSha256,
    [property: JsonPropertyOrder(3)] string Model,
    [property: JsonPropertyOrder(4)] string Provider,
    [property: JsonPropertyOrder(5)] string ConfigurationSha256,
    [property: JsonPropertyOrder(6)] int TimeoutMs,
    [property: JsonPropertyOrder(7)] string PromptTemplateVersion);

internal sealed record HistoricalAnalysisInstructionDatasetReadV1(
    [property: JsonPropertyOrder(0)] bool TruncatedBefore,
    [property: JsonPropertyOrder(1)] bool SanitizedOnly,
    [property: JsonPropertyOrder(2)] bool ContentAvailable,
    [property: JsonPropertyOrder(3)] HistoricalEvidenceDistributionV1 DatasetDistribution);

internal sealed record HistoricalAnalysisInstructionReceiptReadV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] long RunId,
    [property: JsonPropertyOrder(2)] string ExtractionId,
    [property: JsonPropertyOrder(3)] string ExtractionSha256,
    [property: JsonPropertyOrder(4)] string State,
    [property: JsonPropertyOrder(5)] string Model,
    [property: JsonPropertyOrder(6)] string Provider,
    [property: JsonPropertyOrder(7)] string ConfigurationSha256,
    [property: JsonPropertyOrder(8)] int TimeoutMs,
    [property: JsonPropertyOrder(9)] string PromptTemplateVersion,
    [property: JsonPropertyOrder(10)] bool TruncatedBefore,
    [property: JsonPropertyOrder(11)] bool SanitizedOnly,
    [property: JsonPropertyOrder(12)] bool ContentAvailable,
    [property: JsonPropertyOrder(13)] HistoricalEvidenceDistributionV1 DatasetDistribution,
    [property: JsonPropertyOrder(14)] string HandoffSha256,
    [property: JsonPropertyOrder(15)] IReadOnlyList<HistoricalInstructionFindingSupportV1> Findings);

internal sealed record HistoricalAnalysisInstructionReadResponseV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] long RunId,
    [property: JsonPropertyOrder(2)] HistoricalAnalysisInstructionRequestReadV1 Request,
    [property: JsonPropertyOrder(3)] HistoricalAnalysisInstructionDatasetReadV1 DatasetProjection,
    [property: JsonPropertyOrder(4)] string State,
    [property: JsonPropertyOrder(5)] DateTimeOffset RequestedAt,
    [property: JsonPropertyOrder(6)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(7)] DateTimeOffset? CompletedAt,
    [property: JsonPropertyOrder(8)] HistoricalAnalysisInstructionReceiptReadV1? Receipt,
    [property: JsonPropertyOrder(9)] byte[] HandoffBytes)
{
    internal static HistoricalAnalysisInstructionReadResponseV1 From(HistoricalInstructionAnalysisReadV1 read)
    {
        var request = new HistoricalAnalysisInstructionRequestReadV1(
            read.Request.SchemaVersion,
            read.Request.ExtractionId,
            read.Request.ExtractionSha256,
            read.Request.Model,
            read.Request.Provider,
            read.Request.ConfigurationSha256,
            read.Request.TimeoutMilliseconds,
            read.Request.PromptTemplateVersion);
        var dataset = new HistoricalAnalysisInstructionDatasetReadV1(
            read.DatasetProjection.TruncatedBefore,
            read.DatasetProjection.SanitizedOnly,
            read.DatasetProjection.ContentAvailable,
            read.DatasetProjection.DatasetDistribution);
        var receipt = read.Receipt is null
            ? null
            : new HistoricalAnalysisInstructionReceiptReadV1(
                read.Receipt.SchemaVersion,
                read.Receipt.RunId,
                read.Receipt.ExtractionId,
                read.Receipt.ExtractionSha256,
                read.Receipt.State.ToWireValue(),
                read.Receipt.Model,
                read.Receipt.Provider,
                read.Receipt.ConfigurationSha256,
                read.Receipt.TimeoutMilliseconds,
                read.Receipt.PromptTemplateVersion,
                read.Receipt.TruncatedBefore,
                read.Receipt.SanitizedOnly,
                read.Receipt.ContentAvailable,
                read.Receipt.DatasetDistribution,
                read.Receipt.HandoffSha256,
                read.Receipt.Findings);
        return new(
            read.SchemaVersion,
            read.RunId,
            request,
            dataset,
            read.State.ToWireValue(),
            read.RequestedAt,
            read.StartedAt,
            read.CompletedAt,
            receipt,
            read.HandoffBytes.ToArray());
    }
}

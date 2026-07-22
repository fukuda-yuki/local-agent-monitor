using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CopilotAgentObservability.LocalMonitor.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static partial class HistoricalInstructionAnalysisJsonV1
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    internal static byte[] Serialize(HistoricalInstructionAnalysisReceiptV1 receipt)
    {
        ValidateReceipt(receipt);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, Options);
        if (bytes.Length > HistoricalInstructionAnalysisContractsV1.MaximumReceiptBytes)
            throw Invalid();
        return bytes;
    }

    internal static HistoricalInstructionAnalysisReceiptV1 Deserialize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > HistoricalInstructionAnalysisContractsV1.MaximumReceiptBytes)
            throw Invalid();
        try
        {
            var receipt = JsonSerializer.Deserialize<HistoricalInstructionAnalysisReceiptV1>(bytes, Options) ?? throw Invalid();
            ValidateReceipt(receipt);
            if (!bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(receipt, Options))) throw Invalid();
            return receipt;
        }
        catch (HistoricalInstructionAnalysisValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException
            or InvalidOperationException or NullReferenceException or OverflowException)
        {
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence,
                exception);
        }
    }

    internal static byte[] SerializeDatasetProjection(HistoricalInstructionAnalysisDatasetProjectionV1 projection)
    {
        ValidateDatasetProjection(projection);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(projection, Options);
        if (bytes.Length > HistoricalInstructionAnalysisContractsV1.MaximumReceiptBytes) throw Invalid();
        return bytes;
    }

    internal static HistoricalInstructionAnalysisDatasetProjectionV1 DeserializeDatasetProjection(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > HistoricalInstructionAnalysisContractsV1.MaximumReceiptBytes) throw Invalid();
        try
        {
            var projection = JsonSerializer.Deserialize<HistoricalInstructionAnalysisDatasetProjectionV1>(bytes, Options)
                ?? throw Invalid();
            ValidateDatasetProjection(projection);
            if (!bytes.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(projection, Options))) throw Invalid();
            return projection;
        }
        catch (HistoricalInstructionAnalysisValidationException) { throw; }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException
            or InvalidOperationException or NullReferenceException or OverflowException)
        {
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence,
                exception);
        }
    }

    internal static void ValidateRequest(HistoricalInstructionAnalysisRequestV1 request)
    {
        if (request is null
            || request.SchemaVersion != HistoricalInstructionAnalysisContractsV1.RequestSchemaVersion
            || !ExtractionIdPattern().IsMatch(request.ExtractionId ?? string.Empty)
            || !HashPattern().IsMatch(request.ExtractionSha256 ?? string.Empty)
            || !IdentifierPattern().IsMatch(request.Model ?? string.Empty)
            || !IdentifierPattern().IsMatch(request.Provider ?? string.Empty)
            || SessionSecretFilter.IsSensitiveCarrier(request.Model!)
            || SessionSecretFilter.IsSensitiveCarrier(request.Provider!)
            || !HashPattern().IsMatch(request.ConfigurationSha256 ?? string.Empty)
            || request.TimeoutMilliseconds is <= 0 or > HistoricalInstructionAnalysisContractsV1.MaximumTimeoutMilliseconds
            || request.PromptTemplateVersion != HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion)
            throw new HistoricalInstructionAnalysisValidationException(HistoricalInstructionAnalysisValidationCodeV1.InvalidContract);
    }

    internal static void ValidateDatasetProjection(HistoricalInstructionAnalysisDatasetProjectionV1 projection)
    {
        if (projection is null
            || projection.SanitizedOnly && projection.ContentAvailable
            || projection.DatasetDistribution is null
            || !ValidDatasetDistribution(projection.DatasetDistribution)
            || !projection.SanitizedOnly && !projection.ContentAvailable
                && HasDistributionRows(projection.DatasetDistribution))
            throw Invalid();
    }

    internal static void ValidateReceipt(HistoricalInstructionAnalysisReceiptV1 receipt)
    {
        if (receipt is null
            || receipt.SchemaVersion != HistoricalInstructionAnalysisContractsV1.ReceiptSchemaVersion
            || receipt.RunId <= 0
            || !ExtractionIdPattern().IsMatch(receipt.ExtractionId ?? string.Empty)
            || !HashPattern().IsMatch(receipt.ExtractionSha256 ?? string.Empty)
            || receipt.State is not (HistoricalInstructionAnalysisStateV1.Succeeded or HistoricalInstructionAnalysisStateV1.ZeroFindings)
            || !IdentifierPattern().IsMatch(receipt.Model ?? string.Empty)
            || !IdentifierPattern().IsMatch(receipt.Provider ?? string.Empty)
            || SessionSecretFilter.IsSensitiveCarrier(receipt.Model!)
            || SessionSecretFilter.IsSensitiveCarrier(receipt.Provider!)
            || !HashPattern().IsMatch(receipt.ConfigurationSha256 ?? string.Empty)
            || receipt.TimeoutMilliseconds is <= 0 or > HistoricalInstructionAnalysisContractsV1.MaximumTimeoutMilliseconds
            || receipt.PromptTemplateVersion != HistoricalInstructionAnalysisContractsV1.PromptTemplateVersion
            || receipt.DatasetDistribution is null
            || !ValidDatasetDistribution(receipt.DatasetDistribution)
            || DatasetSessionCount(receipt.DatasetDistribution) <= 0
            || !HashPattern().IsMatch(receipt.HandoffSha256 ?? string.Empty)
            || receipt.Findings is null
            || receipt.Findings.Any(finding => finding is null)
            || (receipt.State == HistoricalInstructionAnalysisStateV1.Succeeded) != (receipt.Findings.Count > 0)
            || receipt.SanitizedOnly
            || !receipt.ContentAvailable)
            throw Invalid();

        var ids = receipt.Findings.Select(finding => finding.FindingId).ToArray();
        if (!ids.SequenceEqual(ids.Order(StringComparer.Ordinal), StringComparer.Ordinal)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw Invalid();

        foreach (var finding in receipt.Findings)
        {
            if (!FindingIdPattern().IsMatch(finding.FindingId ?? string.Empty)
                || !Enum.IsDefined(finding.Verdict)
                || !Enum.IsDefined(finding.CandidateEligibility)
                || !Enum.IsDefined(finding.SupportKind)
                || finding.SupportingSessionIds is null
                || finding.SupportingGroupIds is null
                || finding.EvidenceRefs is null
                || finding.RecurringCount < 0
                || finding.RecurringCount == 0
                    && finding.SupportingSessionIds.Count != 1
                || finding.RecurringCount > 0
                    && finding.RecurringCount != finding.SupportingSessionIds.Count
                || finding.SupportingGroupIds.Count < finding.SupportingSessionIds.Count
                || finding.SupportKind != SupportKind(finding.RecurringCount)
                || (finding.CandidateEligibility == InstructionCandidateEligibilityV1.Eligible)
                    != (finding.Verdict == InstructionFindingVerdictV1.Supported)
                || finding.Verdict == InstructionFindingVerdictV1.Supported
                    && finding.RecurringCount < 2
                || finding.SupportingSessionIds.Any(value => !SessionReferencePattern().IsMatch(value ?? string.Empty))
                || finding.SupportingGroupIds.Any(value => !GroupIdPattern().IsMatch(value ?? string.Empty))
                || finding.SupportingSessionIds.Distinct(StringComparer.Ordinal).Count() != finding.SupportingSessionIds.Count
                || finding.SupportingGroupIds.Distinct(StringComparer.Ordinal).Count() != finding.SupportingGroupIds.Count
                || !finding.SupportingSessionIds.SequenceEqual(finding.SupportingSessionIds.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || !finding.SupportingGroupIds.SequenceEqual(finding.SupportingGroupIds.Order(StringComparer.Ordinal), StringComparer.Ordinal)
                || finding.EvidenceRefs.Count == 0
                || finding.EvidenceRefs.Any(reference => reference is null
                    || !SessionReferencePattern().IsMatch(reference.SessionId ?? string.Empty)
                    || !finding.SupportingSessionIds.Contains(reference.SessionId, StringComparer.Ordinal)
                    || !TraceReferencePattern().IsMatch(reference.TraceId ?? string.Empty)
                    || reference.SpanId is not null && !SpanReferencePattern().IsMatch(reference.SpanId)
                    || reference.SpanId is null && reference.TurnIndex is null
                    || reference.TurnIndex is <= 0
                    || !Enum.IsDefined(reference.RelativePosition))
                || !finding.EvidenceRefs.Distinct().Order(HistoricalEvidenceReferenceComparerV1.Instance).SequenceEqual(finding.EvidenceRefs)
                || !finding.SupportingSessionIds.ToHashSet(StringComparer.Ordinal).SetEquals(
                    finding.EvidenceRefs.Select(reference => reference.SessionId))
                || !ValidDistribution(finding.SourceSurfaceDistribution, finding.SupportingSessionIds.Count)
                || !ValidDistribution(finding.SourceVersionDistribution, finding.SupportingSessionIds.Count)
                || !ValidDistribution(finding.SourceKindDistribution, finding.SupportingSessionIds.Count)
                || !ValidDistribution(finding.CompletenessDistribution, finding.SupportingSessionIds.Count))
                throw Invalid();
        }
    }

    private static HistoricalInstructionSupportKindV1 SupportKind(int recurringCount) => recurringCount switch
    {
        0 => HistoricalInstructionSupportKindV1.InsufficientSupport,
        1 => HistoricalInstructionSupportKindV1.SingleSession,
        _ => HistoricalInstructionSupportKindV1.Recurring,
    };

    private static bool ValidDatasetDistribution(HistoricalEvidenceDistributionV1 distribution)
    {
        if (!ValidDistribution(distribution.Completeness)
            || !ValidDistribution(distribution.SourceKinds)
            || !ValidDistribution(distribution.Capabilities)) return false;
        var sessionCount = DatasetSessionCount(distribution);
        return sessionCount == distribution.SourceKinds.Sum(value => (long)value.Count)
            && distribution.Capabilities.All(value => value.Count <= sessionCount);
    }

    private static long DatasetSessionCount(HistoricalEvidenceDistributionV1 distribution) =>
        distribution.Completeness.Sum(value => (long)value.Count);

    private static bool HasDistributionRows(HistoricalEvidenceDistributionV1 distribution) =>
        distribution.Completeness.Count != 0
        || distribution.SourceKinds.Count != 0
        || distribution.Capabilities.Count != 0;

    private static bool ValidDistribution(
        IReadOnlyList<HistoricalDistributionCountV1>? values,
        int? expectedTotal = null) =>
        values is not null
        && values.All(value => value is not null && value.Count > 0
            && DistributionKeyPattern().IsMatch(value.Key ?? string.Empty))
        && values.Select(value => value.Key).SequenceEqual(values.Select(value => value.Key).Order(StringComparer.Ordinal), StringComparer.Ordinal)
        && values.Select(value => value.Key).Distinct(StringComparer.Ordinal).Count() == values.Count
        && (expectedTotal is null || values.Sum(value => (long)value.Count) == expectedTotal);

    private static HistoricalInstructionAnalysisValidationException Invalid() =>
        new(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence);

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = false,
            MaxDepth = 16,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower, allowIntegerValues: false));
        return options;
    }

    [GeneratedRegex("^historical-extraction-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtractionIdPattern();
    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
    [GeneratedRegex("^[a-z0-9][-a-z0-9._:]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex DistributionKeyPattern();
    [GeneratedRegex("^instruction-finding-[0-9a-f]{24}$", RegexOptions.CultureInvariant)]
    private static partial Regex FindingIdPattern();
    [GeneratedRegex("^historical-group-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupIdPattern();
    [GeneratedRegex("^session-ref-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionReferencePattern();
    [GeneratedRegex("^trace-ref-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex TraceReferencePattern();
    [GeneratedRegex("^span-ref-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SpanReferencePattern();
}

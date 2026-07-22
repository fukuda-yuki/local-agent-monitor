using System.Security.Cryptography;
using CopilotAgentObservability.InstructionFindings;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalInstructionAnalysisReadConsumerV1
{
    internal static long Validate(HistoricalInstructionAnalysisReadV1 read)
    {
        try
        {
            if (read is null
                || read.SchemaVersion != HistoricalInstructionAnalysisContractsV1.ReadSchemaVersion
                || read.RunId <= 0
                || read.Request is null
                || read.DatasetProjection is null
                || !Enum.IsDefined(read.State)
                || read.RequestedAt.Offset != TimeSpan.Zero
                || read.StartedAt is { } startedAt && startedAt.Offset != TimeSpan.Zero
                || read.CompletedAt is { } completedAt && completedAt.Offset != TimeSpan.Zero
                || read.StartedAt < read.RequestedAt
                || read.CompletedAt < read.StartedAt
                || read.HandoffBytes is null)
                throw Invalid();

            HistoricalInstructionAnalysisJsonV1.ValidateRequest(read.Request);
            HistoricalInstructionAnalysisJsonV1.ValidateDatasetProjection(read.DatasetProjection);

            var terminal = read.State is not (HistoricalInstructionAnalysisStateV1.Queued
                or HistoricalInstructionAnalysisStateV1.Running);
            if (read.State == HistoricalInstructionAnalysisStateV1.Queued
                    && (read.StartedAt is not null || read.CompletedAt is not null)
                || read.State == HistoricalInstructionAnalysisStateV1.Running
                    && (read.StartedAt is null || read.CompletedAt is not null)
                || terminal && (read.StartedAt is null || read.CompletedAt is null))
                throw Invalid();

            if (read.State == HistoricalInstructionAnalysisStateV1.ContentUnavailable
                    && (!read.DatasetProjection.SanitizedOnly || read.DatasetProjection.ContentAvailable)
                || read.State == HistoricalInstructionAnalysisStateV1.NoEligibleSessions
                    && (read.DatasetProjection.SanitizedOnly
                        || !read.DatasetProjection.ContentAvailable
                        || HasDatasetRows(read.DatasetProjection.DatasetDistribution))
                || RequiresProviderInput(read.State)
                    && (read.DatasetProjection.SanitizedOnly || !read.DatasetProjection.ContentAvailable))
                throw Invalid();

            var successful = read.State is HistoricalInstructionAnalysisStateV1.Succeeded
                or HistoricalInstructionAnalysisStateV1.ZeroFindings;
            if (successful != (read.Receipt is not null && read.HandoffBytes.Length > 0))
                throw Invalid();
            if (!successful)
            {
                if (read.Receipt is not null || read.HandoffBytes.Length != 0) throw Invalid();
                return read.RunId;
            }

            var receipt = read.Receipt!;
            HistoricalInstructionAnalysisJsonV1.ValidateReceipt(receipt);
            if (receipt.RunId != read.RunId
                || receipt.State != read.State
                || receipt.ExtractionId != read.Request.ExtractionId
                || receipt.ExtractionSha256 != read.Request.ExtractionSha256
                || receipt.Model != read.Request.Model
                || receipt.Provider != read.Request.Provider
                || receipt.ConfigurationSha256 != read.Request.ConfigurationSha256
                || receipt.TimeoutMilliseconds != read.Request.TimeoutMilliseconds
                || receipt.PromptTemplateVersion != read.Request.PromptTemplateVersion
                || receipt.TruncatedBefore != read.DatasetProjection.TruncatedBefore
                || receipt.SanitizedOnly != read.DatasetProjection.SanitizedOnly
                || receipt.ContentAvailable != read.DatasetProjection.ContentAvailable
                || !DistributionMatches(receipt.DatasetDistribution.Completeness, read.DatasetProjection.DatasetDistribution.Completeness)
                || !DistributionMatches(receipt.DatasetDistribution.SourceKinds, read.DatasetProjection.DatasetDistribution.SourceKinds)
                || !DistributionMatches(receipt.DatasetDistribution.Capabilities, read.DatasetProjection.DatasetDistribution.Capabilities)
                || receipt.HandoffSha256 != Sha256(read.HandoffBytes)
                || InstructionFindingHandoffConsumerV1.Validate(read.HandoffBytes) != read.RunId)
                throw Invalid();
            var handoff = InstructionFindingJsonV1.Deserialize(read.HandoffBytes);
            if (!receipt.Findings.Select(finding => finding.FindingId)
                    .SequenceEqual(handoff.Findings.Select(finding => finding.FindingId), StringComparer.Ordinal)
                || (read.State == HistoricalInstructionAnalysisStateV1.ZeroFindings) != (handoff.Findings.Count == 0))
                throw Invalid();
            foreach (var support in receipt.Findings)
            {
                var finding = handoff.Findings.SingleOrDefault(value => value.FindingId == support.FindingId);
                if (finding is null
                    || finding.Verdict != support.Verdict
                    || finding.CandidateEligibility != support.CandidateEligibility)
                    throw Invalid();
            }
            return read.RunId;
        }
        catch (HistoricalInstructionAnalysisValidationException exception)
            when (exception.Code != HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence)
        {
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence,
                exception);
        }
        catch (HistoricalInstructionAnalysisValidationException) { throw; }
        catch (Exception exception) when (exception is InstructionFindingHandoffConsumerValidationException
            or InstructionFindingValidationException
            or ArgumentException or InvalidOperationException or NullReferenceException or OverflowException)
        {
            throw new HistoricalInstructionAnalysisValidationException(
                HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence,
                exception);
        }
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static bool DistributionMatches(
        IReadOnlyList<HistoricalDistributionCountV1> left,
        IReadOnlyList<HistoricalDistributionCountV1> right) =>
        left.SequenceEqual(right);

    private static bool HasDatasetRows(HistoricalEvidenceDistributionV1 distribution) =>
        distribution.Completeness.Count != 0
        || distribution.SourceKinds.Count != 0
        || distribution.Capabilities.Count != 0;

    private static bool RequiresProviderInput(HistoricalInstructionAnalysisStateV1 state) =>
        state is HistoricalInstructionAnalysisStateV1.Succeeded
            or HistoricalInstructionAnalysisStateV1.ZeroFindings
            or HistoricalInstructionAnalysisStateV1.InvalidCitation
            or HistoricalInstructionAnalysisStateV1.ProviderPartial
            or HistoricalInstructionAnalysisStateV1.ProviderFailed
            or HistoricalInstructionAnalysisStateV1.TimedOut
            or HistoricalInstructionAnalysisStateV1.Canceled;

    private static HistoricalInstructionAnalysisValidationException Invalid() =>
        new(HistoricalInstructionAnalysisValidationCodeV1.InvalidPersistence);
}

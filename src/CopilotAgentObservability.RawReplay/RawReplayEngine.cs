using System.Text.Json;

namespace CopilotAgentObservability.RawReplay;

public sealed class RawReplayEngine
{
    private readonly RawReplayArchiveService archiveService = new();

    public RawReplayExecutionResult Replay(
        string replayId,
        byte[] archiveBytes,
        string normalizationVersion = RawReplayContractVersions.Normalization,
        RawReplayReceipt? existing = null)
    {
        if (!ValidReplayId(replayId)) return Failure("replay_id_invalid");
        if (normalizationVersion != RawReplayContractVersions.Normalization) return Failure("normalization_version_mismatch");
        var inspection = archiveService.Inspect(archiveBytes);
        if (!inspection.Success || inspection.Bundle is null) return Failure(inspection.ErrorCode ?? "archive_invalid");
        var bundle = inspection.Bundle;
        if (bundle.Manifest.NormalizationVersion != normalizationVersion) return Failure("normalization_version_mismatch");
        var archiveSha = inspection.ArchiveSha256!;
        if (existing is not null)
        {
            if (existing.ReplayId != replayId || existing.ArchiveSha256 != archiveSha
                || existing.NormalizationVersion != normalizationVersion
                || existing.ProjectionVersion != RawReplayContractVersions.Projection
                || existing.DashboardVersion != RawReplayContractVersions.Dashboard) return Failure("replay_id_conflict");
        }

        RawReplayOutputs outputs;
        try { outputs = RawReplayOutputBuilder.Build(bundle.Records); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or OverflowException)
        { return Failure("normalization_failed"); }
        if (outputs.NormalizedSha256 != bundle.Manifest.ExpectedNormalizedSha256) return Failure("normalized_hash_mismatch");
        if (outputs.ProjectionSha256 != bundle.Manifest.ExpectedProjectionSha256) return Failure("projection_hash_mismatch");
        if (outputs.DashboardSha256 != bundle.Manifest.ExpectedDashboardSha256) return Failure("dashboard_hash_mismatch");
        var receipt = new RawReplayReceipt(
            RawReplayContractVersions.ReplayResult,
            replayId,
            RawReplayContractVersions.BundleProfile,
            archiveSha,
            normalizationVersion,
            RawReplayContractVersions.Projection,
            RawReplayContractVersions.Dashboard,
            outputs.NormalizedSha256,
            outputs.ProjectionSha256,
            outputs.DashboardSha256,
            bundle.Manifest.SourceVersions,
            bundle.Records.Count,
            bundle.SessionContents.Count,
            ExternalModelInvocations: 0);
        if (existing is not null && !RawReplayJson.SerializeCanonical(existing).AsSpan().SequenceEqual(RawReplayJson.SerializeCanonical(receipt)))
            return Failure("replay_id_conflict");
        return new(true, null, existing is not null, receipt, RawReplayJson.SerializeCanonical(receipt),
            outputs.Normalized, outputs.Projection, outputs.Dashboard, bundle.Records, bundle.SessionContents);
    }

    private static bool ValidReplayId(string? value) => value is { Length: >= 8 and <= 64 }
        && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')
        && value[0] is >= 'a' and <= 'z' or >= '0' and <= '9'
        && !RawReplayCredentialScanner.ContainsKnownCredential(value);

    private static RawReplayExecutionResult Failure(string code) => new(false, code, false, null, null, null, null, null, [], []);
}

using System.Security.Cryptography;
using CopilotAgentObservability.Persistence.Sqlite.Retention;
using CopilotAgentObservability.RawReplay;

namespace CopilotAgentObservability.ConfigCli;

internal sealed record RawReplayRetainedFile(string Path, long Size, string Sha256);

internal sealed record RawReplayRetentionManifest(
    string SchemaVersion,
    string Profile,
    string ReplayId,
    string CaptureId,
    DateTimeOffset ReservedAt,
    DateTimeOffset ExpiresAt,
    RawReplayReceipt Receipt,
    IReadOnlyList<RawReplayRetainedFile> Files);

internal static class RawReplaySensitiveBundlePlanBuilder
{
    internal const string SchemaVersion = "raw-local-replay-retention-manifest.v1";

    internal static SensitiveBundlePlan Build(
        RetentionFileCaptureReservation reservation,
        string replayId,
        byte[] archive,
        RawReplayExecutionResult execution)
    {
        if (reservation.Phase != RetentionCapturePhase.Reserved || execution is not { Success: true, Result: not null,
                ResultBytes: not null, NormalizedBytes: not null, ProjectionBytes: not null, DashboardBytes: not null })
            throw new ArgumentException("Invalid raw replay retention plan.");

        var payloads = new (string Path, byte[] Bytes)[]
        {
            ("input/archive.zip", archive),
            ("output/result.json", execution.ResultBytes),
            ("output/normalized.json", execution.NormalizedBytes),
            ("output/projection.json", execution.ProjectionBytes),
            ("output/dashboard.json", execution.DashboardBytes),
        };
        var inventory = payloads.Select(item => new RawReplayRetainedFile(item.Path, item.Bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(item.Bytes)))).ToArray();
        var reservedAt = DateTimeOffset.ParseExact(reservation.ReservedAtText, "O", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None);
        var manifest = RawReplayJson.SerializeCanonical(new RawReplayRetentionManifest(
            SchemaVersion, RawReplayContractVersions.BundleProfile, replayId, reservation.CaptureId,
            reservedAt, reservedAt.Add(RetentionV1Constants.SensitiveBundleTtl), execution.Result, inventory));
        var marker = RetentionFileCaptureOwnershipMarker.Create(reservation.StoreInstanceId, reservation.CaptureId,
            reservation.ReservedAtText, reservation.ReservedAtTicks, reservation.OwnerToken);

        var members = new List<SensitiveBundlePlannedMember>
        {
            new(0, RetentionFileCaptureContracts.OwnerMarkerName, RetentionFileCaptureMemberKind.OwnerMarker, marker, 8),
            new(1, "input", RetentionFileCaptureMemberKind.Directory, [], 5),
            new(2, "output", RetentionFileCaptureMemberKind.Directory, [], 6),
        };
        for (var index = 0; index < payloads.Length; index++)
            members.Add(new(index + 3, payloads[index].Path, RetentionFileCaptureMemberKind.File, payloads[index].Bytes, index));
        members.Add(new(8, "manifest.json", RetentionFileCaptureMemberKind.File, manifest, 7));

        var plan = new SensitiveBundlePlan(manifest, members, new Dictionary<string, SensitiveBundlePlanEntry>(StringComparer.Ordinal));
        if (!plan.ToCapturePlanInput().IsValid) throw new SensitiveBundlePlanLimitException();
        return plan;
    }
}

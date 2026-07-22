using System.Text.Json.Serialization;

namespace CopilotAgentObservability.RawReplay;

public static class RawReplayContractVersions
{
    public const string BundleSchema = "raw-local-replay-bundle.v1";
    public const string BundleProfile = "raw-local-replay";
    public const string Manifest = "raw-local-replay-manifest.v1";
    public const string CanonicalJson = "raw-local-replay-canonical-json.v1";
    public const string Archive = "raw-local-replay-zip-store.v1";
    public const string Checksum = "sha256.v1";
    public const string ExportControl = "raw-local-replay-export-control.v1";
    public const string ReplayControl = "raw-local-replay-control.v1";
    public const string ReplayResult = "raw-local-replay-result.v1";
    public const string Normalization = "raw-measurement-normalization.v1";
    public const string Projection = "raw-replay-monitor-projection.v1";
    public const string Dashboard = "raw-replay-dashboard.v1";
    public const string CredentialScanner = "raw-replay-credential-scan.v1";
}

public static class RawReplayLimits
{
    public const int MaximumControlBytes = 1024 * 1024;
    public const int MaximumArchiveEntries = 256;
    public const int MaximumPayloadEntries = MaximumArchiveEntries - 1;
    public const int MaximumSelectionValues = 256;
    public const int MaximumIdentifierLength = 256;
    public const int MaximumRawRecordBytes = 30 * 1024 * 1024;
    public const int MaximumSessionContentBytes = 8 * 1024 * 1024;
    public const int MaximumManifestBytes = 1024 * 1024;
    public const int MaximumArchiveBytes = 128 * 1024 * 1024;
}

public static class RawReplayWarnings
{
    public const string RawData = "Raw local replay data can contain prompts, responses, tool data, personal data, and secrets. Secret detection is incomplete. Keep it local.";
}

public sealed record RawReplaySelection(
    [property: JsonRequired] IReadOnlyList<string>? SessionIds = null,
    [property: JsonRequired] IReadOnlyList<string>? TraceIds = null,
    [property: JsonRequired] IReadOnlyList<long>? RawRecordIds = null,
    [property: JsonRequired] IReadOnlyList<string>? Sources = null,
    [property: JsonRequired] DateTimeOffset? StartInclusive = null,
    [property: JsonRequired] DateTimeOffset? EndExclusive = null);

public sealed record RawReplayConsent(string Profile, bool WarningAcknowledged, string ConfirmationPhrase)
{
    public const string RequiredPhrase = "I UNDERSTAND THIS IS RAW LOCAL DATA";

    internal bool IsValid => Profile == RawReplayContractVersions.BundleProfile
        && WarningAcknowledged
        && ConfirmationPhrase == RequiredPhrase;
}

public sealed record RawReplayExportControl(
    string SchemaVersion,
    string Profile,
    DateTimeOffset CreatedAt,
    RawReplaySelection Selection,
    bool IncludeSessionContent,
    bool SanitizedOnly,
    string? PreviewDigest,
    RawReplayConsent? Consent);

public sealed record RawReplayControl(
    string SchemaVersion,
    string Profile,
    string ReplayId,
    string ArchiveSha256,
    string NormalizationVersion,
    string ProjectionVersion,
    string DashboardVersion,
    bool SanitizedOnly,
    string? PreviewDigest,
    RawReplayConsent? Consent);

public sealed record RawReplaySourceProvenance(
    string? SourceSurface,
    string? SourceApplicationVersion,
    string? SourceAdapter,
    string? AdapterVersion,
    string? SchemaFingerprint,
    string? InventoryHash,
    string CompatibilityState,
    string CaptureContentState,
    string SecretFilterState,
    string SecretFilterVersion);

public sealed record RawReplayRecord(
    long RawRecordId,
    string Source,
    string? TraceId,
    DateTimeOffset ReceivedAt,
    string? ResourceAttributesJson,
    string PayloadJson,
    int SchemaVersion,
    RawReplaySourceProvenance Provenance);

public sealed record RawReplaySessionContent(
    string EventId,
    string SessionId,
    string? RunId,
    string? TraceId,
    string SourceAdapter,
    string SourceEventId,
    DateTimeOffset OccurredAt,
    string ContentState,
    string? SourceApplicationVersion,
    string? AdapterVersion,
    string? SchemaFingerprint,
    string? NormalizationVersion,
    string? MatchKind,
    string ContentKind,
    string ContentJson,
    DateTimeOffset CapturedAt,
    DateTimeOffset ExpiresAt,
    string SecretFilterState,
    string SecretFilterVersion);

public sealed record RawReplaySnapshot(
    string SnapshotId,
    DateTimeOffset CapturedAt,
    string LocalMonitorVersion,
    IReadOnlyList<RawReplayRecord> Records,
    IReadOnlyList<RawReplaySessionContent> SessionContents,
    IReadOnlyList<string> KnownMissing);

public sealed class RawReplaySnapshotLease : IAsyncDisposable
{
    private readonly Func<ValueTask> release;
    private int released;

    public RawReplaySnapshotLease(RawReplaySnapshot snapshot, Func<ValueTask> release)
    {
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        this.release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public RawReplaySnapshot Snapshot { get; }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref released, 1) == 0 ? release() : ValueTask.CompletedTask;
}

public sealed record RawReplaySnapshotCapture(bool Success, string? ErrorCode, RawReplaySnapshotLease? Lease)
{
    public RawReplaySnapshot? Snapshot => Lease?.Snapshot;
}

public interface IRawReplaySnapshotProvider
{
    ValueTask<RawReplaySnapshotCapture> CaptureAsync(RawReplaySelection selection, bool includeSessionContent, CancellationToken cancellationToken);
}

public sealed record RawReplayPreview(
    bool Success,
    string? ErrorCode,
    string Warning,
    string DataClassification,
    string Profile,
    int RawRecordCount,
    int SessionContentCount,
    DateTimeOffset? StartInclusive,
    DateTimeOffset? EndExclusive,
    IReadOnlyList<string> SourceVersions,
    IReadOnlyList<string> ContentStates,
    IReadOnlyList<string> SecretFilterStates,
    IReadOnlyList<string> KnownMissing,
    string NormalizationVersion,
    string ProjectionVersion,
    string DashboardVersion,
    string? ExpectedNormalizedSha256,
    string? ExpectedProjectionSha256,
    string? ExpectedDashboardSha256,
    long EstimatedUncompressedBytes,
    string? PreviewDigest);

public sealed record RawReplayManifestFile(string Path, string Kind, long Size, string Sha256);

public sealed record RawReplayManifest(
    string SchemaVersion,
    string BundleSchemaVersion,
    string Profile,
    string CanonicalJsonVersion,
    string ArchiveVersion,
    string ChecksumVersion,
    string DataClassification,
    DateTimeOffset CreatedAt,
    string SnapshotId,
    string LocalMonitorVersion,
    int RawRecordCount,
    int SessionContentCount,
    DateTimeOffset? StartInclusive,
    DateTimeOffset? EndExclusive,
    IReadOnlyList<string> SourceVersions,
    IReadOnlyList<string> ContentStates,
    IReadOnlyList<string> SecretFilterStates,
    IReadOnlyList<string> KnownMissing,
    string NormalizationVersion,
    string ProjectionVersion,
    string DashboardVersion,
    string ExpectedNormalizedSha256,
    string ExpectedProjectionSha256,
    string ExpectedDashboardSha256,
    IReadOnlyList<RawReplayManifestFile> Files);

public sealed record RawReplayBundle(
    RawReplayManifest Manifest,
    IReadOnlyList<RawReplayRecord> Records,
    IReadOnlyList<RawReplaySessionContent> SessionContents);

public sealed record RawReplayResult(
    bool Success,
    string? ErrorCode,
    RawReplayPreview Preview,
    byte[]? ManifestBytes,
    byte[]? ArchiveBytes,
    string? ArchiveSha256);

public sealed record RawReplayInspection(
    bool Success,
    string? ErrorCode,
    string? ArchiveSha256,
    string? BundleSchemaVersion = null,
    string? BundleProfile = null,
    int RawRecordCount = 0,
    int SessionContentCount = 0,
    long TotalUncompressedBytes = 0,
    [property: JsonIgnore] RawReplayBundle? Bundle = null);

public sealed record RawReplayReceipt(
    string SchemaVersion,
    string ReplayId,
    string Profile,
    string ArchiveSha256,
    string NormalizationVersion,
    string ProjectionVersion,
    string DashboardVersion,
    string NormalizedSha256,
    string ProjectionSha256,
    string DashboardSha256,
    IReadOnlyList<string> SourceVersions,
    int RawRecordCount,
    int SessionContentCount,
    int ExternalModelInvocations);

public sealed record RawReplayExecutionResult(
    bool Success,
    string? ErrorCode,
    bool IdempotentReplay,
    RawReplayReceipt? Result,
    [property: JsonIgnore] byte[]? ResultBytes,
    [property: JsonIgnore] byte[]? NormalizedBytes,
    [property: JsonIgnore] byte[]? ProjectionBytes,
    [property: JsonIgnore] byte[]? DashboardBytes,
    [property: JsonIgnore] IReadOnlyList<RawReplayRecord> StagedRecords,
    [property: JsonIgnore] IReadOnlyList<RawReplaySessionContent> StagedSessionContents);

using System.Runtime.InteropServices;
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
    private readonly Func<RawReplaySnapshotUseReference> acquire;
    private readonly Func<ValueTask> release;
    private readonly Func<RawReplaySnapshotTerminalOperation, RawReplaySnapshotTerminalResult>? terminal;
    private readonly bool terminalNotRequired;
    private int released;

    internal RawReplaySnapshotLease(
        Func<RawReplaySnapshotUseReference> acquire,
        Func<ValueTask> release,
        Func<RawReplaySnapshotTerminalOperation, RawReplaySnapshotTerminalResult> terminal)
    {
        this.acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        this.release = release ?? throw new ArgumentNullException(nameof(release));
        this.terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
    }

    internal RawReplaySnapshotLease(
        Func<RawReplaySnapshotUseReference> acquire,
        Func<ValueTask> release,
        bool terminalNotRequired)
    {
        this.acquire = acquire ?? throw new ArgumentNullException(nameof(acquire));
        this.release = release ?? throw new ArgumentNullException(nameof(release));
        this.terminalNotRequired = terminalNotRequired;
    }

    internal RawReplaySnapshotUseReference AcquireSnapshotReference() => acquire();

    internal bool TrySealRawReplayTransientPublication(out string? errorCode) =>
        TrySeal(RawReplaySnapshotTerminalOperation.SealTransientPublication, out errorCode);

    internal bool TrySealRawReplayFilePublication(
        string stagedPath,
        string outputPath,
        out Action? publicationTicket,
        out string? errorCode)
    {
        publicationTicket = null;
        var staged = Path.GetFullPath(stagedPath);
        var output = Path.GetFullPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(staged, output, comparison)
            || !string.Equals(Path.GetDirectoryName(staged), Path.GetDirectoryName(output), comparison))
            throw new ArgumentException("Raw replay staging and output must be distinct files in the same directory.");
        if (!TrySeal(RawReplaySnapshotTerminalOperation.SealFilePublication, out errorCode)) return false;

        var ticket = new RawReplayFileMoveTicket(staged, output);
        publicationTicket = ticket.Move;
        return true;
    }

    internal RawReplaySnapshotTerminalResult TryCompleteWithoutRaw() =>
        terminal?.Invoke(RawReplaySnapshotTerminalOperation.CompleteWithoutRaw)
        ?? (terminalNotRequired
            ? RawReplaySnapshotTerminalResult.NotRequired
            : RawReplaySnapshotTerminalResult.Lost);

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref released, 1) == 0 ? release() : ValueTask.CompletedTask;

    private bool TrySeal(RawReplaySnapshotTerminalOperation operation, out string? errorCode)
    {
        var result = terminal?.Invoke(operation) ?? RawReplaySnapshotTerminalResult.Lost;
        errorCode = result switch
        {
            RawReplaySnapshotTerminalResult.Sealed => null,
            RawReplaySnapshotTerminalResult.Busy => "snapshot_store_busy",
            _ => "snapshot_read_denied",
        };
        return result == RawReplaySnapshotTerminalResult.Sealed;
    }

    private sealed class RawReplayFileMoveTicket(string stagedPath, string outputPath)
    {
        private int used;

        internal void Move()
        {
            if (Interlocked.Exchange(ref used, 1) != 0) return;
            if (!OperatingSystem.IsLinux())
            {
                File.Move(stagedPath, outputPath, overwrite: false);
                return;
            }
            try
            {
                const int currentDirectory = -100;
                const uint noReplace = 1;
                // File.Move's Unix existence check and rename are not atomic.
                if (RenameAt2(currentDirectory, stagedPath, currentDirectory, outputPath, noReplace) != 0)
                    throw new IOException("Atomic raw replay publication failed.");
            }
            catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
            {
                throw new IOException("Atomic raw replay publication is unavailable.", exception);
            }
        }

        [DllImport("libc", EntryPoint = "renameat2", SetLastError = true)]
        private static extern int RenameAt2(
            int oldDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
            int newDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
            uint flags);
    }
}

internal sealed class RawReplaySnapshotUseReference : IDisposable
{
    private Func<RawReplaySnapshot>? read;
    private Action? releaseReference;

    internal RawReplaySnapshotUseReference(Func<RawReplaySnapshot> read, Action release)
    {
        this.read = read ?? throw new ArgumentNullException(nameof(read));
        releaseReference = release ?? throw new ArgumentNullException(nameof(release));
    }

    internal RawReplaySnapshot Snapshot =>
        (Volatile.Read(ref read) ?? throw new ObjectDisposedException(nameof(RawReplaySnapshotUseReference)))();

    public void Dispose()
    {
        Interlocked.Exchange(ref read, null);
        Interlocked.Exchange(ref releaseReference, null)?.Invoke();
    }
}

internal enum RawReplaySnapshotTerminalOperation
{
    SealTransientPublication,
    SealFilePublication,
    CompleteWithoutRaw,
}

internal enum RawReplaySnapshotTerminalResult
{
    Sealed,
    CompletedWithoutRaw,
    Lost,
    Busy,
    NotRequired,
}

public sealed record RawReplaySnapshotCapture(bool Success, string? ErrorCode, RawReplaySnapshotLease? Lease)
;

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

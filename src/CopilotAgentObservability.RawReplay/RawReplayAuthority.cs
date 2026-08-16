namespace CopilotAgentObservability.RawReplay;

public sealed class RawReplayAuthorizedService(IRawReplaySnapshotProvider snapshotProvider)
{
    private readonly RawReplayArchiveService service = new();

    internal RawReplayAuthorizedService(
        IRawReplaySnapshotProvider snapshotProvider,
        RawReplayArchiveService service)
        : this(snapshotProvider) =>
        this.service = service ?? throw new ArgumentNullException(nameof(service));

    public async ValueTask<RawReplayPreview> PreviewAsync(RawReplayExportControl control, CancellationToken cancellationToken = default)
        => (await PreviewCoreAsync(control, cancellationToken).ConfigureAwait(false)).Preview;

    internal ValueTask<RawReplayPreviewPublication> PreviewForHttpAsync(
        RawReplayExportControl control,
        CancellationToken cancellationToken = default) =>
        PreviewCoreAsync(control, cancellationToken);

    private async ValueTask<RawReplayPreviewPublication> PreviewCoreAsync(
        RawReplayExportControl control,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (RawReplayArchiveService.ValidateControl(control) is { } error)
            return new(FailurePreview(error), false);
        var capture = await snapshotProvider.CaptureAsync(control.Selection, control.IncludeSessionContent, cancellationToken).ConfigureAwait(false);
        if (!capture.Success || capture.Lease is null)
            return new(FailurePreview(ProviderError(capture.ErrorCode)), false);
        await using var lease = capture.Lease;
        var preview = service.Preview(lease.Snapshot, control);
        var terminal = lease.TryCompleteWithoutRaw();
        return terminal is RawReplaySnapshotTerminalResult.CompletedWithoutRaw or RawReplaySnapshotTerminalResult.NotRequired
            ? new(preview, false)
            : new(FailurePreview(TerminalError(terminal)), true);
    }

    public async ValueTask<RawReplayResult> CreateAndPublishAsync(
        RawReplayExportControl control,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (RawReplayArchiveService.ValidateCommitControl(control) is { } error) return FailureResult(error);
        if (!RawReplayArchiveService.ValidOutputName(outputPath)) return FailureResult("output_name_invalid");
        var capture = await snapshotProvider.CaptureAsync(control.Selection, control.IncludeSessionContent, cancellationToken).ConfigureAwait(false);
        if (!capture.Success || capture.Lease is null) return FailureResult(ProviderError(capture.ErrorCode));
        await using var lease = capture.Lease;
        var result = service.Create(lease.Snapshot, control);
        if (!result.Success) return CompleteFailure(lease, result);

        var staging = service.StageForPublication(result, outputPath);
        if (staging.StagedFile is null) return CompleteFailure(lease, staging.Result);
        using var staged = staging.StagedFile;
        if (!lease.TrySealRawReplayFilePublication(
                staged.StagedPath,
                staged.OutputPath,
                out var publicationTicket,
                out var terminalError))
            return DiscardRaw(result, terminalError!);
        try
        {
            publicationTicket!();
            return result;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or System.Security.SecurityException)
        {
            return DiscardRaw(result, "publish_failed");
        }
    }

    internal static string ProviderError(string? code) => code switch
    {
        "invalid_selection" or "request_invalid" => "request_invalid",
        "selection_limit_exceeded" => "selection_limit_exceeded",
        "entry_too_large" => "entry_too_large",
        "archive_too_large" => "archive_too_large",
        "snapshot_store_busy" => "snapshot_store_busy",
        "snapshot_member_missing" => "snapshot_member_missing",
        "snapshot_read_denied" => "snapshot_read_denied",
        "snapshot_store_unavailable" => "snapshot_store_unavailable",
        _ => "snapshot_store_unavailable",
    };

    private static RawReplayResult FailureResult(string code)
    {
        var preview = FailurePreview(code);
        return new(false, code, preview, null, null, null);
    }

    private static RawReplayResult CompleteFailure(RawReplaySnapshotLease lease, RawReplayResult result)
    {
        var errorCode = result.ErrorCode ?? "snapshot_read_denied";
        var safe = DiscardRaw(result, errorCode);
        var terminal = lease.TryCompleteWithoutRaw();
        return terminal is RawReplaySnapshotTerminalResult.CompletedWithoutRaw or RawReplaySnapshotTerminalResult.NotRequired
            ? safe
            : FailureResult(TerminalError(terminal));
    }

    internal static RawReplayResult DiscardRaw(RawReplayResult result, string errorCode)
    {
        if (result.ManifestBytes is { } manifest) Array.Clear(manifest);
        if (result.ArchiveBytes is { } archive) Array.Clear(archive);
        return result with
        {
            Success = false,
            ErrorCode = errorCode,
            ManifestBytes = null,
            ArchiveBytes = null,
            ArchiveSha256 = null,
        };
    }

    private static string TerminalError(RawReplaySnapshotTerminalResult result) =>
        result == RawReplaySnapshotTerminalResult.Busy
            ? "snapshot_store_busy"
            : "snapshot_read_denied";

    private static RawReplayPreview FailurePreview(string code) => new(false, code, RawReplayWarnings.RawData, "raw",
        RawReplayContractVersions.BundleProfile, 0, 0, null, null, [], [], [], [],
        RawReplayContractVersions.Normalization, RawReplayContractVersions.Projection, RawReplayContractVersions.Dashboard,
        null, null, null, 0, null);
}

internal sealed record RawReplayPreviewPublication(
    RawReplayPreview Preview,
    bool TerminalFailed);

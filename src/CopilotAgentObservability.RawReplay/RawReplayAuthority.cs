namespace CopilotAgentObservability.RawReplay;

public sealed class RawReplayAuthorizedService(IRawReplaySnapshotProvider snapshotProvider)
{
    private readonly RawReplayArchiveService service = new();

    public async ValueTask<RawReplayPreview> PreviewAsync(RawReplayExportControl control, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (RawReplayArchiveService.ValidateControl(control) is { } error) return FailurePreview(error);
        var capture = await snapshotProvider.CaptureAsync(control.Selection, control.IncludeSessionContent, cancellationToken).ConfigureAwait(false);
        if (!capture.Success || capture.Lease is null) return FailurePreview(ProviderError(capture.ErrorCode));
        await using var lease = capture.Lease;
        return service.Preview(lease.Snapshot, control);
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
        return service.CreateAndPublish(lease.Snapshot, control, outputPath);
    }

    public async ValueTask<RawReplayResult> CreateAsync(
        RawReplayExportControl control,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (RawReplayArchiveService.ValidateCommitControl(control) is { } error) return FailureResult(error);
        var capture = await snapshotProvider.CaptureAsync(control.Selection, control.IncludeSessionContent, cancellationToken).ConfigureAwait(false);
        if (!capture.Success || capture.Lease is null) return FailureResult(ProviderError(capture.ErrorCode));
        await using var lease = capture.Lease;
        return service.Create(lease.Snapshot, control);
    }

    private static string ProviderError(string? code) => code switch
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

    private static RawReplayPreview FailurePreview(string code) => new(false, code, RawReplayWarnings.RawData, "raw",
        RawReplayContractVersions.BundleProfile, 0, 0, null, null, [], [], [], [],
        RawReplayContractVersions.Normalization, RawReplayContractVersions.Projection, RawReplayContractVersions.Dashboard,
        null, null, null, 0, null);
}

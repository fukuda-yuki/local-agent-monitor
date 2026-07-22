namespace CopilotAgentObservability.SanitizedExport;

public sealed class SanitizedExportAuthorizedService(ISanitizedExportSnapshotProvider snapshotProvider)
{
    private static readonly SanitizedExportCapabilityStates UnavailableCapabilities =
        new("unavailable", "unavailable", "unavailable", "unavailable", "unavailable");
    private readonly SanitizedExportService service = new();

    public SanitizedExportPreview Preview(SanitizedExportControlRequest control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var capture = snapshotProvider.Capture();
        if (!capture.Success || capture.Snapshot is null)
            return FailurePreview(capture.ErrorCode ?? "snapshot_provider_unavailable");
        return service.Preview(new(control.CreatedAt, capture.Snapshot, control.Selection, control.ForbiddenMarkers));
    }

    public SanitizedExportResult Create(SanitizedExportControlRequest control)
    {
        ArgumentNullException.ThrowIfNull(control);
        var capture = snapshotProvider.Capture();
        if (!capture.Success || capture.Snapshot is null)
        {
            var preview = FailurePreview(capture.ErrorCode ?? "snapshot_provider_unavailable");
            return new(false, preview.ErrorCode, preview, null, null, null);
        }
        return service.Create(new(control.CreatedAt, capture.Snapshot, control.Selection, control.ForbiddenMarkers));
    }

    public SanitizedExportResult CreateAndPublish(SanitizedExportControlRequest control, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(control);
        var capture = snapshotProvider.Capture();
        if (!capture.Success || capture.Snapshot is null)
        {
            var preview = FailurePreview(capture.ErrorCode ?? "snapshot_provider_unavailable");
            return new(false, preview.ErrorCode, preview, null, null, null);
        }
        return service.CreateAndPublish(new(control.CreatedAt, capture.Snapshot, control.Selection, control.ForbiddenMarkers), outputPath);
    }

    private static SanitizedExportPreview FailurePreview(string errorCode) => new(
        false, errorCode, [], [], 0, UnavailableCapabilities, SanitizedExportContractVersions.Scanner);
}

public sealed class UnavailableSanitizedExportSnapshotProvider : ISanitizedExportSnapshotProvider
{
    public SanitizedExportSnapshotCapture Capture() => new(false, "snapshot_provider_unavailable", null);
}

public sealed class SanitizedExportBundleInspector
{
    private readonly SanitizedExportService service = new();

    public SanitizedExportInspectionResult Inspect(byte[] archiveBytes) => service.Inspect(archiveBytes);
}

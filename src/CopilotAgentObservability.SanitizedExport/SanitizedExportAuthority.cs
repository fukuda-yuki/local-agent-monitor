namespace CopilotAgentObservability.SanitizedExport;

public sealed class SanitizedExportAuthorizedService(ISanitizedExportSnapshotProvider snapshotProvider)
{
    private static readonly SanitizedExportCapabilityStates UnavailableCapabilities =
        new("unavailable", "unavailable", "unavailable", "unavailable", "unavailable");
    private readonly SanitizedExportService service = new();

    public SanitizedExportPreview Preview(SanitizedExportControlRequest control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (ControlError(control) is { } controlError)
            return FailurePreview(controlError);
        var capture = snapshotProvider.Capture(control.Selection);
        if (!capture.Success || capture.Snapshot is null)
            return FailurePreview(capture.ErrorCode ?? "snapshot_provider_unavailable");
        var preview = service.Preview(new(control.CreatedAt, capture.Snapshot, control.Selection));
        return preview.Success || !TrustedStoreFailure(preview.ErrorCode) ? preview : FailurePreview("snapshot_store_unavailable");
    }

    public SanitizedExportResult Create(SanitizedExportControlRequest control)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (ControlError(control) is { } controlError)
        {
            var invalid = FailurePreview(controlError);
            return new(false, invalid.ErrorCode, invalid, null, null, null);
        }
        var capture = snapshotProvider.Capture(control.Selection);
        if (!capture.Success || capture.Snapshot is null)
        {
            var preview = FailurePreview(capture.ErrorCode ?? "snapshot_provider_unavailable");
            return new(false, preview.ErrorCode, preview, null, null, null);
        }
        return Normalize(service.Create(new(control.CreatedAt, capture.Snapshot, control.Selection)));
    }

    public SanitizedExportResult CreateAndPublish(SanitizedExportControlRequest control, string outputPath)
    {
        ArgumentNullException.ThrowIfNull(control);
        if (ControlError(control) is { } controlError)
        {
            var invalid = FailurePreview(controlError);
            return new(false, invalid.ErrorCode, invalid, null, null, null);
        }
        var capture = snapshotProvider.Capture(control.Selection);
        if (!capture.Success || capture.Snapshot is null)
        {
            var preview = FailurePreview(capture.ErrorCode ?? "snapshot_provider_unavailable");
            return new(false, preview.ErrorCode, preview, null, null, null);
        }
        return Normalize(service.CreateAndPublish(new(control.CreatedAt, capture.Snapshot, control.Selection), outputPath));
    }

    private static SanitizedExportPreview FailurePreview(string errorCode) => new(
        false, errorCode, [], [], 0, UnavailableCapabilities, SanitizedExportContractVersions.Scanner);

    private static SanitizedExportResult Normalize(SanitizedExportResult result)
    {
        if (result.Success || !TrustedStoreFailure(result.ErrorCode)) return result;
        var preview = FailurePreview("snapshot_store_unavailable");
        return new(false, preview.ErrorCode, preview, null, null, null);
    }

    private static bool TrustedStoreFailure(string? code) => code is
        "invalid_request" or "invalid_capability_state" or "duplicate_record_identity" or "duplicate_entry"
        or "unexpected_entry" or "unsupported_record_type" or "producer_contract_invalid" or "producer_envelope_mismatch"
        or "invalid_canonical_content" or "forbidden_field" or "credential_pattern" or "local_path" or "pii_pattern";

    private static string? ControlError(SanitizedExportControlRequest control)
    {
        if (control.SchemaVersion != SanitizedExportContractVersions.ControlRequest || control.CreatedAt.Offset != TimeSpan.Zero)
            return "request_invalid";
        return SanitizedExportSelectionValidator.IsValid(control.Selection) ? null : "invalid_selection";
    }
}

public sealed class UnavailableSanitizedExportSnapshotProvider : ISanitizedExportSnapshotProvider
{
    public SanitizedExportSnapshotCapture Capture(SanitizedExportSelection selection) => new(false, "snapshot_provider_unavailable", null);
}

public sealed class SanitizedExportBundleInspector
{
    private readonly SanitizedExportService service = new();

    public SanitizedExportInspectionResult Inspect(byte[] archiveBytes) => service.Inspect(archiveBytes);
}

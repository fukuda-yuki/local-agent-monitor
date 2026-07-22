namespace CopilotAgentObservability.SanitizedExport;

public static class SanitizedExportContractVersions
{
    public const string BundleSchema = "sanitized-evidence-bundle.v1";
    public const string BundleProfile = "sanitized-evidence";
    public const string Manifest = "sanitized-evidence-manifest.v1";
    public const string CanonicalJson = "sanitized-evidence-canonical-json.v1";
    public const string Archive = "sanitized-evidence-zip-store.v1";
    public const string Checksum = "sha256.v1";
    public const string Scanner = "repository-safe-scanner.v1";
    public const string ProducerValidation = "sanitized-evidence-producers.v1";
    public const string ControlRequest = "sanitized-export-control.v1";
    public const string CompatibilityMinimum = "1";
    public const string CompatibilityMaximum = "1";
}

public static class SanitizedExportLimits
{
    public const int MaximumControlRequestBytes = 1024 * 1024;
    public const int MaximumArchiveEntries = 256;
    public const long MaximumUncompressedBytes = 128L * 1024 * 1024;
    public const int MaximumRecordBytes = 8 * 1024 * 1024;
    public const int MaximumRecords = MaximumArchiveEntries - 1;
    public const int MaximumDependenciesPerRecord = 256;
    public const int MaximumListValues = 256;
    public const int MaximumVersions = 256;
    public const int MaximumIdentifierLength = 256;
}

public enum SanitizedExportDependencyDisposition
{
    Required,
    External,
}

public sealed record SanitizedExportAgentVersion(string SourceSurface, string Version);

public sealed record SanitizedExportCapabilityStates(
    string InstructionFindings,
    string AlertReceipts,
    string HistoricalInstructionAnalysis,
    string HistoricalEfficiencyAnalysis,
    string AlertCenter);

public sealed record SanitizedExportDependency(
    string RecordType,
    string RecordId,
    SanitizedExportDependencyDisposition Disposition);

public sealed record SanitizedExportRecord(
    string EntryPath,
    string RecordType,
    string RecordId,
    string? SessionId,
    string? TraceId,
    string? SourceSurface,
    string? RepositoryName,
    string? WorkspaceLabel,
    string? RepoSnapshot,
    DateTimeOffset ObservedAt,
    byte[] CanonicalBytes,
    IReadOnlyList<SanitizedExportDependency> Dependencies,
    string Completeness = "unknown",
    string ContentState = "unknown",
    string RetentionState = "unknown");

public sealed record SanitizedExportSourceSnapshot(
    string SnapshotId,
    string LocalMonitorVersion,
    IReadOnlyList<SanitizedExportAgentVersion> AgentVersions,
    IReadOnlyList<SanitizedExportRecord> Records,
    SanitizedExportCapabilityStates Capabilities,
    IReadOnlyDictionary<string, string>? ProcessingVersions = null);

public sealed record SanitizedExportSelection(
    IReadOnlyList<string>? SessionIds = null,
    IReadOnlyList<string>? TraceIds = null,
    IReadOnlyList<string>? SourceSurfaces = null,
    IReadOnlyList<string>? RepositoryNames = null,
    IReadOnlyList<string>? WorkspaceLabels = null,
    DateTimeOffset? StartInclusive = null,
    DateTimeOffset? EndExclusive = null,
    IReadOnlyList<string>? ReceiptTypes = null);

internal static class SanitizedExportSelectionValidator
{
    internal static bool IsValid(SanitizedExportSelection? selection)
    {
        if (selection is null || selection.StartInclusive is { } startOffset && startOffset.Offset != TimeSpan.Zero
            || selection.EndExclusive is { } endOffset && endOffset.Offset != TimeSpan.Zero
            || selection.StartInclusive is { } start && selection.EndExclusive is { } end && start >= end) return false;
        IReadOnlyList<string>?[] lists =
        [
            selection.SessionIds, selection.TraceIds, selection.SourceSurfaces, selection.RepositoryNames,
            selection.WorkspaceLabels, selection.ReceiptTypes,
        ];
        return lists.All(list => list is null || list.Count <= SanitizedExportLimits.MaximumListValues
            && list.All(ValidValue)
            && list.Distinct(StringComparer.Ordinal).Count() == list.Count)
            && (selection.ReceiptTypes is null || selection.ReceiptTypes.All(value => value is
                "repository_metadata_projection" or "instruction_finding_handoff" or "alert_receipt"));
    }

    private static bool ValidValue(string value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= SanitizedExportLimits.MaximumIdentifierLength
        && !value.Any(character => char.IsControl(character) || character is '/' or '\\' or '?' or '#');
}

internal sealed record SanitizedExportRequest(
    DateTimeOffset CreatedAt,
    SanitizedExportSourceSnapshot Snapshot,
    SanitizedExportSelection Selection);

public sealed record SanitizedExportControlRequest(
    string SchemaVersion,
    DateTimeOffset CreatedAt,
    SanitizedExportSelection Selection);

public sealed record SanitizedExportSnapshotCapture(
    bool Success,
    string? ErrorCode,
    SanitizedExportSourceSnapshot? Snapshot);

public interface ISanitizedExportSnapshotProvider
{
    SanitizedExportSnapshotCapture Capture(SanitizedExportSelection selection);
}

public sealed record SanitizedExportPreviewEntry(string Path, string RecordType, string RecordId, long Size, string Sha256);

public sealed record SanitizedExportUnresolvedDependency(string RecordType, string RecordId, string State);

public sealed record SanitizedExportPreview(
    bool Success,
    string? ErrorCode,
    IReadOnlyList<SanitizedExportPreviewEntry> Entries,
    IReadOnlyList<SanitizedExportUnresolvedDependency> UnresolvedDependencies,
    long EstimatedUncompressedBytes,
    SanitizedExportCapabilityStates Capabilities,
    string ValidationProfile)
{
    public string BundleSchemaVersion => SanitizedExportContractVersions.BundleSchema;
    public string BundleProfile => SanitizedExportContractVersions.BundleProfile;
    public string ProducerValidationProfile => SanitizedExportContractVersions.ProducerValidation;
    public int RecordCount => Entries.Count;
}

public sealed record SanitizedExportResult(
    bool Success,
    string? ErrorCode,
    SanitizedExportPreview Preview,
    byte[]? ManifestBytes,
    byte[]? ArchiveBytes,
    string? ArchiveSha256,
    string? PublishedFileName = null);

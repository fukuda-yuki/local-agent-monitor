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
    public const string CompatibilityMinimum = "1";
    public const string CompatibilityMaximum = "1";
}

public static class SanitizedExportLimits
{
    public const int MaximumArchiveEntries = 256;
    public const long MaximumUncompressedBytes = 128L * 1024 * 1024;
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

public sealed record SanitizedExportRequest(
    DateTimeOffset CreatedAt,
    SanitizedExportSourceSnapshot Snapshot,
    SanitizedExportSelection Selection,
    IReadOnlyList<string> ForbiddenMarkers);

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

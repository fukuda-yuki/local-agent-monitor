namespace CopilotAgentObservability.SanitizedImport;

public static class SanitizedImportContractVersions
{
    public const string Preview = "sanitized-import-preview.v1";
    public const string Result = "sanitized-import-result.v1";
    public const string History = "sanitized-import-history.v1";
    public const string Store = "sanitized-import-store.v1";
    public const string SchemaComponent = "sanitized_import";
    public const int SchemaVersion = 1;
    public const string Compatibility = "compatible";
    public const string MigrationChain = "sanitized-evidence-bundle.v1->sanitized-import-store.v1";
    public const string MigrationStep = "identity-projection";
    public const string MigrationChainSha256 = "6f6ddfa063bc69f874b7540d1a0b1c9ebc8e5180f49b3a6f23220998606f9a42";
}

public static class SanitizedImportLimits
{
    public const int MaximumHistoryItems = 100;
    public const int DefaultHistoryItems = 50;
    public const int MaximumPreviewUnresolved = 256;
    public const int MaximumGraphNodes = 65_536;
    public const int MaximumGraphEdges = 131_072;
}

public sealed record SanitizedImportMigration(
    int Version,
    string Chain,
    string Step,
    string ChainSha256,
    bool Lossy);

public sealed record SanitizedImportExpectedChanges(
    int Records,
    int Origins,
    int GraphNodes,
    int GraphEdges,
    int HistoryRows,
    int RawRetentionItems);

public sealed record SanitizedImportConflict(
    string RecordType,
    string RecordId,
    string IncomingSha256,
    string ExistingSha256);

public sealed record SanitizedImportUnresolved(
    string NodeKind,
    string SourceId,
    string State);

public sealed record SanitizedImportSourceAgentVersion(string SourceSurface, string Version);

public sealed record SanitizedImportSourceLabel(
    string? RepositoryName,
    string? WorkspaceLabel,
    string? RepoSnapshot);

public sealed record SanitizedImportSelection(
    IReadOnlyList<string> SessionIds,
    IReadOnlyList<string> TraceIds,
    IReadOnlyList<string> SourceSurfaces,
    IReadOnlyList<string> RepositoryNames,
    IReadOnlyList<string> WorkspaceLabels,
    IReadOnlyList<string> ReceiptTypes,
    DateTimeOffset? StartInclusive,
    DateTimeOffset? EndExclusive);

public sealed record SanitizedImportDateRange(DateTimeOffset? Start, DateTimeOffset? End);

public sealed record SanitizedImportCapabilityStates(
    string InstructionFindings,
    string AlertReceipts,
    string HistoricalInstructionAnalysis,
    string HistoricalEfficiencyAnalysis,
    string AlertCenter);

public sealed record SanitizedImportPreview(
    bool Success,
    string? ErrorCode,
    string SchemaVersion,
    string? PreviewId,
    string? PreviewDigest,
    string? ArchiveSha256,
    string? ManifestSchemaVersion,
    string? BundleSchemaVersion,
    string? BundleProfile,
    string Compatibility,
    SanitizedImportMigration Migration,
    string? SourceSnapshotId,
    string? SourceLocalMonitorVersion,
    DateTimeOffset? SourceCreatedAt,
    IReadOnlyList<SanitizedImportSourceAgentVersion> SourceAgentVersions,
    SanitizedImportSelection? Selection,
    SanitizedImportDateRange? DateRange,
    IReadOnlyList<SanitizedImportSourceLabel> SourceLabels,
    IReadOnlyDictionary<string, int> RecordCounts,
    SanitizedImportCapabilityStates? Capabilities,
    IReadOnlyDictionary<string, int> CompletenessDistribution,
    IReadOnlyDictionary<string, int> ContentStateDistribution,
    IReadOnlyDictionary<string, int> RetentionStateDistribution,
    IReadOnlyDictionary<string, string> ProcessingVersions,
    int TotalRecords,
    long TotalUncompressedBytes,
    int NewRecords,
    int DuplicateRecords,
    int ConflictRecords,
    IReadOnlyList<SanitizedImportConflict> Conflicts,
    int UnresolvedReferenceCount,
    IReadOnlyList<SanitizedImportUnresolved> UnresolvedReferences,
    SanitizedImportExpectedChanges ExpectedChanges,
    bool CanCommit);

public sealed record SanitizedImportResult(
    bool Success,
    string? ErrorCode,
    string SchemaVersion,
    string? ImportId,
    string? ArchiveSha256,
    string? PreviewDigest,
    string? Status,
    int NewRecords,
    int DuplicateRecords,
    int GraphNodes,
    int GraphEdges,
    int RawRetentionItems,
    SanitizedImportMigration Migration,
    string? SourceSnapshotId,
    string? SourceLocalMonitorVersion,
    DateTimeOffset? ImportedAt,
    bool IdempotentReplay);

public sealed record SanitizedImportHistoryItem(
    string ImportId,
    string ArchiveSha256,
    string PreviewDigest,
    string Status,
    int NewRecords,
    int DuplicateRecords,
    int GraphNodes,
    int GraphEdges,
    int RawRetentionItems,
    SanitizedImportMigration Migration,
    string SourceSnapshotId,
    string SourceLocalMonitorVersion,
    DateTimeOffset ImportedAt);

public sealed record SanitizedImportHistoryPage(
    string SchemaVersion,
    IReadOnlyList<SanitizedImportHistoryItem> Items);

internal sealed record SanitizedImportRecord(
    string EntryPath,
    string RecordType,
    string RecordId,
    string LocalRecordId,
    string CanonicalSha256,
    byte[] CanonicalBytes);

internal sealed record SanitizedImportGraphNode(
    string LocalNodeId,
    string NodeKind,
    string SourceId,
    string State,
    string? DefiningRecordLocalId);

internal sealed record SanitizedImportGraphEdge(
    string LocalEdgeId,
    string SourceRecordLocalId,
    string SourceNodeId,
    string TargetNodeId,
    string Relation,
    int Ordinal,
    string ResolutionState,
    string ProvenanceJson);

internal sealed record SanitizedImportManifest(
    string ManifestSchemaVersion,
    string BundleSchemaVersion,
    string BundleProfile,
    DateTimeOffset CreatedAt,
    string SnapshotId,
    string SourceLocalMonitorVersion,
    IReadOnlyList<SanitizedImportSourceAgentVersion> SourceAgentVersions,
    SanitizedImportSelection Selection,
    SanitizedImportDateRange DateRange,
    IReadOnlyList<SanitizedImportSourceLabel> SourceLabels,
    IReadOnlyDictionary<string, int> RecordCounts,
    IReadOnlyList<SanitizedImportUnresolved> KnownMissingEvidence,
    SanitizedImportCapabilityStates Capabilities,
    IReadOnlyDictionary<string, int> CompletenessDistribution,
    IReadOnlyDictionary<string, int> ContentStateDistribution,
    IReadOnlyDictionary<string, int> RetentionStateDistribution,
    IReadOnlyDictionary<string, string> ProcessingVersions);

internal sealed record SanitizedImportBundle(
    string ArchiveSha256,
    long TotalUncompressedBytes,
    SanitizedImportManifest Manifest,
    IReadOnlyList<SanitizedImportRecord> Records,
    IReadOnlyList<SanitizedImportGraphNode> GraphNodes,
    IReadOnlyList<SanitizedImportGraphEdge> GraphEdges);

internal sealed record SanitizedImportBundleReadResult(
    bool Success,
    string? ErrorCode,
    SanitizedImportBundle? Bundle);

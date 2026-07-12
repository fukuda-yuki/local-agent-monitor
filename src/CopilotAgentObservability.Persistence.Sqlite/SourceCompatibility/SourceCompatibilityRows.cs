namespace CopilotAgentObservability.Persistence.Sqlite;

internal sealed record SourceCompatibilityRow(
    long Id,
    string ObservationId,
    long? RawRecordId,
    string? IngestBatchId,
    string? SourceSurface,
    string? SourceApplicationVersion,
    string? SourceAdapter,
    string? AdapterVersion,
    string? SchemaFingerprint,
    string? InventoryHash,
    SourceCompatibilityState CompatibilityState,
    IReadOnlyList<string> ReasonCodes,
    string NextAction,
    SourceCaptureContentState? CaptureContentState,
    long UnknownSpanCount,
    long UnknownEventCount,
    long UnknownAttributeCount,
    int OverflowDistinctCount,
    int OverflowOccurrenceCount,
    DateTimeOffset ObservedAt,
    IReadOnlyList<SourceUnknownObservationRow> UnknownObservations);

internal sealed record SourceUnknownObservationRow(
    long Id,
    long SourceObservationId,
    SourceUnknownKind Kind,
    string Name,
    int Count,
    string? SourceVersionLabel,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    string OpaqueSampleReference);

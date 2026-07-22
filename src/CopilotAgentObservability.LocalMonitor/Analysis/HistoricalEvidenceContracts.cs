using System.Text.Json.Serialization;
using CopilotAgentObservability.Telemetry.Sessions;

namespace CopilotAgentObservability.LocalMonitor.Analysis;

internal static class HistoricalEvidenceContractsV1
{
    internal const string RawLocalSchemaVersion = "historical-evidence.raw-local.v1";
    internal const string RepositorySafeSchemaVersion = "historical-evidence.repository-safe.v1";
    internal const int DefaultMaximumSessions = 50;
    internal const int MaximumSessions = 200;
    internal const int MaximumGroupsPerSession = 256;
    internal const int MaximumReferencesPerGroup = 16;
    internal const int MaximumNativeIdsPerSession = 256;
    internal const int MaximumRunsPerSession = 256;
    internal const int MaximumEventsPerSession = MaximumGroupsPerSession * MaximumReferencesPerGroup;
    internal const int MaximumInstructionFindingHandoffs = MaximumSessions * MaximumGroupsPerSession;
    internal const int MaximumInstructionFindingPayloadBytes = MaximumPayloadBytes;
    internal const int MaximumInstructionFindingTotalBytes = MaximumPayloadBytes;
    internal const int MaximumDescriptorLength = 160;
    internal const int MaximumPayloadBytes = 64 * 1024 * 1024;
}

internal enum HistoricalEvidenceSourceKindV1 { LiveOtel, SavedRaw, HistoricalSummary }
internal enum HistoricalEvidenceRelativePositionV1 { Anchor, Previous, Following }
internal enum HistoricalEvidenceRepresentationV1 { RawLocal, RepositorySafe }
internal enum HistoricalDescriptorStateV1 { NotRequested, Unavailable, Available, RejectedSensitive }
internal enum HistoricalSessionExclusionReasonV1 { FilterMismatch, WindowTruncated, Unbound, MissingEvidenceReference, MissingSessionReference, InvalidHistoricalCompleteness }
internal enum HistoricalEvidenceGroupKindV1
{
    TurnRollup,
    TokenRollup,
    CacheRollup,
    ErrorSpan,
    RetryChain,
    RepeatedToolCall,
    PermissionWait,
    SubagentFanOut,
    UserCorrection,
    QualityReference,
    SourceDifference,
    InstructionFinding,
}

internal enum HistoricalEvidenceValidationCodeV1
{
    InvalidContract,
    UnresolvedEvidenceReference,
    MissingExactCapability,
    InvalidSerialization,
    InvalidDerivedIdentity,
    ConflictingPersistence,
    InvalidPersistence,
}

internal sealed class HistoricalEvidenceValidationException : Exception
{
    internal HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1 code) : base(code.ToString()) => Code = code;
    internal HistoricalEvidenceValidationException(HistoricalEvidenceValidationCodeV1 code, Exception inner) : base(code.ToString(), inner) => Code = code;
    internal HistoricalEvidenceValidationCodeV1 Code { get; }
}

internal sealed record HistoricalEvidenceSelectionV1(
    [property: JsonPropertyOrder(0)] string? Repository,
    [property: JsonPropertyOrder(1)] string? Workspace,
    [property: JsonPropertyOrder(2)] DateTimeOffset? From,
    [property: JsonPropertyOrder(3)] DateTimeOffset? To,
    [property: JsonPropertyOrder(4)] IReadOnlyList<Guid> ExplicitSessionIds,
    [property: JsonPropertyOrder(5)] IReadOnlyList<SessionSourceSurface> SourceSurfaces,
    [property: JsonPropertyOrder(6)] string? TaskLabel,
    [property: JsonPropertyOrder(7)] string? ExperimentLabel,
    [property: JsonPropertyOrder(8)] int MaximumSessionCount,
    [property: JsonPropertyOrder(9)] bool SanitizedOnly)
{
    internal static HistoricalEvidenceSelectionV1 Create(
        string? repository = null,
        string? workspace = null,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        IReadOnlyList<Guid>? explicitSessionIds = null,
        IReadOnlyList<SessionSourceSurface>? sourceSurfaces = null,
        string? taskLabel = null,
        string? experimentLabel = null,
        int maximumSessionCount = HistoricalEvidenceContractsV1.DefaultMaximumSessions,
        bool sanitizedOnly = false) =>
        new(repository, workspace, from, to, explicitSessionIds ?? [], sourceSurfaces ?? [], taskLabel, experimentLabel, maximumSessionCount, sanitizedOnly);
}

internal sealed record HistoricalEvidenceSelectionProjectionV1(
    [property: JsonPropertyOrder(0)] string? Repository,
    [property: JsonPropertyOrder(1)] string? Workspace,
    [property: JsonPropertyOrder(2)] DateTimeOffset? From,
    [property: JsonPropertyOrder(3)] DateTimeOffset? To,
    [property: JsonPropertyOrder(4)] IReadOnlyList<string> ExplicitSessionIds,
    [property: JsonPropertyOrder(5)] IReadOnlyList<SessionSourceSurface> SourceSurfaces,
    [property: JsonPropertyOrder(6)] string? TaskLabel,
    [property: JsonPropertyOrder(7)] string? ExperimentLabel,
    [property: JsonPropertyOrder(8)] int MaximumSessionCount,
    [property: JsonPropertyOrder(9)] bool SanitizedOnly);

internal sealed record HistoricalSessionCapabilitiesV1(
    [property: JsonPropertyOrder(0)] bool TurnRollup,
    [property: JsonPropertyOrder(1)] bool TokenRollup,
    [property: JsonPropertyOrder(2)] bool CacheRollup,
    [property: JsonPropertyOrder(3)] bool ErrorSpan,
    [property: JsonPropertyOrder(4)] bool RetryChain,
    [property: JsonPropertyOrder(5)] bool RepeatedToolCall,
    [property: JsonPropertyOrder(6)] bool PermissionWait,
    [property: JsonPropertyOrder(7)] bool SubagentFanOut,
    [property: JsonPropertyOrder(8)] bool RawLocalDescriptor,
    [property: JsonPropertyOrder(9)] bool QualityReference,
    [property: JsonPropertyOrder(10)] bool SourceComparison,
    [property: JsonPropertyOrder(11)] bool InstructionFindingReference);

internal sealed record HistoricalRawEvidenceReferenceV1(
    Guid SessionId,
    string TraceId,
    string? SpanId,
    int? TurnIndex,
    HistoricalEvidenceRelativePositionV1 RelativePosition);

internal sealed record HistoricalEvidenceLocationV1(
    Guid SessionId,
    string TraceId,
    string? SpanId,
    int? TurnIndex,
    HistoricalEvidenceRelativePositionV1 RelativePosition);

internal sealed record HistoricalSessionMetadataV1(
    Guid SessionId,
    SessionSourceSurface SourceSurface,
    string? SourceVersion,
    string? AdapterVersion,
    SessionCompleteness Completeness,
    IReadOnlyList<string> CompletenessReasons,
    HistoricalEvidenceSourceKindV1 SourceKind,
    SessionContentState ContentState,
    string? Repository,
    string? Workspace,
    string? TaskLabel,
    string? ExperimentLabel,
    DateTimeOffset? StartedAt,
    DateTimeOffset LastSeenAt,
    HistoricalSessionCapabilitiesV1 Capabilities,
    IReadOnlyList<HistoricalEvidenceLocationV1> EvidenceLocations,
    IReadOnlyList<string> InstructionFindingIds)
{
    internal DateTimeOffset? EndedAt { get; init; }
    internal IReadOnlyList<SessionSourceSurface> SourceSurfaces { get; init; } = [];
    internal IReadOnlyList<HistoricalSourceProvenanceV1> SourceProvenance { get; init; } = [];
    internal IReadOnlyList<HistoricalRawModelObservationV1> ModelObservations { get; init; } = [];
    internal IReadOnlyList<HistoricalRawDurationObservationV1> DurationObservations { get; init; } = [];
}

internal sealed record HistoricalSourceProvenanceV1(
    [property: JsonPropertyOrder(0)] SessionSourceSurface SourceSurface,
    [property: JsonPropertyOrder(1)] string? SourceApplicationVersion,
    [property: JsonPropertyOrder(2)] string? AdapterVersion);

internal sealed record HistoricalRawModelObservationV1(string Model, HistoricalRawEvidenceReferenceV1 EvidenceRef);
internal sealed record HistoricalRawDurationObservationV1(long DurationMs, HistoricalRawEvidenceReferenceV1 EvidenceRef);

internal sealed record HistoricalModelObservationV1(
    [property: JsonPropertyOrder(0)] string Model,
    [property: JsonPropertyOrder(1)] HistoricalEvidenceReferenceV1 EvidenceRef);

internal sealed record HistoricalDurationObservationV1(
    [property: JsonPropertyOrder(0)] long DurationMs,
    [property: JsonPropertyOrder(1)] HistoricalEvidenceReferenceV1 EvidenceRef);

internal sealed record HistoricalDecisionMetadataV1(
    [property: JsonPropertyOrder(0)] string? Repository,
    [property: JsonPropertyOrder(1)] string? Workspace,
    [property: JsonPropertyOrder(2)] DateTimeOffset? StartedAt,
    [property: JsonPropertyOrder(3)] DateTimeOffset? EndedAt,
    [property: JsonPropertyOrder(4)] DateTimeOffset LastSeenAt,
    [property: JsonPropertyOrder(5)] IReadOnlyList<SessionSourceSurface> SourceSurfaces,
    [property: JsonPropertyOrder(6)] IReadOnlyList<HistoricalSourceProvenanceV1> SourceProvenance,
    [property: JsonPropertyOrder(7)] IReadOnlyList<HistoricalModelObservationV1> ModelObservations,
    [property: JsonPropertyOrder(8)] IReadOnlyList<HistoricalDurationObservationV1> DurationObservations,
    [property: JsonPropertyOrder(9)] SessionCompleteness Completeness,
    [property: JsonPropertyOrder(10)] IReadOnlyList<string> CompletenessReasons,
    [property: JsonPropertyOrder(11)] HistoricalEvidenceSourceKindV1 SourceKind,
    [property: JsonPropertyOrder(12)] SessionContentState ContentState,
    [property: JsonPropertyOrder(13)] HistoricalSessionCapabilitiesV1 Capabilities);

internal sealed record HistoricalEvidenceGroupDraftV1(
    HistoricalEvidenceGroupKindV1 Kind,
    IReadOnlyList<HistoricalRawEvidenceReferenceV1> References,
    long? NumericValue,
    string? Unit,
    string? Status,
    string? ExactCallId,
    string? CanonicalCallHash,
    string? ExactOwnershipId,
    string? FindingId,
    string? RawDescriptor,
    InstructionFindingReceiptV1? FindingReceipt = null,
    InstructionRuleCandidateV1? FindingCandidate = null);

internal interface IHistoricalEvidenceSnapshotSourceV1
{
    ValueTask<IHistoricalEvidenceSnapshotLeaseV1> OpenSnapshotAsync(HistoricalEvidenceSelectionV1 selection, CancellationToken cancellationToken);
}

internal interface IHistoricalEvidenceSnapshotLeaseV1 : IAsyncDisposable
{
    string SnapshotId { get; }
    IReadOnlyList<HistoricalSessionMetadataV1> Sessions { get; }
    long OmittedEarlierMatchingSessionCount { get; }
    ValueTask<IReadOnlyList<HistoricalEvidenceGroupDraftV1>> ReadEvidenceAsync(Guid sessionId, bool includeDescriptors, CancellationToken cancellationToken);
}

internal sealed record HistoricalEvidenceReferenceV1(
    [property: JsonPropertyOrder(0)] string SessionId,
    [property: JsonPropertyOrder(1)] string TraceId,
    [property: JsonPropertyOrder(2)] string? SpanId,
    [property: JsonPropertyOrder(3)] int? TurnIndex,
    [property: JsonPropertyOrder(4)] HistoricalEvidenceRelativePositionV1 RelativePosition);

internal sealed record HistoricalEvidenceSessionV1(
    [property: JsonPropertyOrder(0)] string SessionId,
    [property: JsonPropertyOrder(1)] SessionSourceSurface SourceSurface,
    [property: JsonPropertyOrder(2)] string? SourceVersion,
    [property: JsonPropertyOrder(3)] string? AdapterVersion,
    [property: JsonPropertyOrder(4)] SessionCompleteness Completeness,
    [property: JsonPropertyOrder(5)] IReadOnlyList<string> CompletenessReasons,
    [property: JsonPropertyOrder(6)] HistoricalEvidenceSourceKindV1 SourceKind,
    [property: JsonPropertyOrder(7)] SessionContentState ContentState,
    [property: JsonPropertyOrder(8)] HistoricalDescriptorStateV1 DescriptorState,
    [property: JsonPropertyOrder(9)] string? RawLocalDescriptor,
    [property: JsonPropertyOrder(10)] HistoricalSessionCapabilitiesV1 Capabilities,
    [property: JsonPropertyOrder(11)] HistoricalDecisionMetadataV1 Metadata);

internal sealed record HistoricalExcludedSessionV1(
    [property: JsonPropertyOrder(0)] string SessionId,
    [property: JsonPropertyOrder(1)] HistoricalSessionExclusionReasonV1 Reason,
    [property: JsonPropertyOrder(2)] HistoricalDecisionMetadataV1? Metadata);

internal sealed record HistoricalEvidenceGroupV1(
    [property: JsonPropertyOrder(0)] string GroupId,
    [property: JsonPropertyOrder(1)] HistoricalEvidenceGroupKindV1 Kind,
    [property: JsonPropertyOrder(2)] string SessionId,
    [property: JsonPropertyOrder(3)] IReadOnlyList<HistoricalEvidenceReferenceV1> References,
    [property: JsonPropertyOrder(4)] long? NumericValue,
    [property: JsonPropertyOrder(5)] string? Unit,
    [property: JsonPropertyOrder(6)] string? Status,
    [property: JsonPropertyOrder(7)] string? ExactCallId,
    [property: JsonPropertyOrder(8)] string? CanonicalCallHash,
    [property: JsonPropertyOrder(9)] string? ExactOwnershipId,
    [property: JsonPropertyOrder(10)] string? FindingId,
    [property: JsonPropertyOrder(11)] InstructionFindingReceiptV1? FindingReceipt,
    [property: JsonPropertyOrder(12)] InstructionRuleCandidateV1? FindingCandidate);

internal sealed record HistoricalDistributionCountV1(
    [property: JsonPropertyOrder(0)] string Key,
    [property: JsonPropertyOrder(1)] int Count);

internal sealed record HistoricalEvidenceDistributionV1(
    [property: JsonPropertyOrder(0)] IReadOnlyList<HistoricalDistributionCountV1> Completeness,
    [property: JsonPropertyOrder(1)] IReadOnlyList<HistoricalDistributionCountV1> SourceKinds,
    [property: JsonPropertyOrder(2)] IReadOnlyList<HistoricalDistributionCountV1> Capabilities);

internal sealed record HistoricalEvidenceDatasetV1(
    [property: JsonPropertyOrder(0)] string SchemaVersion,
    [property: JsonPropertyOrder(1)] string ExtractionId,
    [property: JsonPropertyOrder(2)] string SnapshotId,
    [property: JsonPropertyOrder(3)] HistoricalEvidenceRepresentationV1 Representation,
    [property: JsonPropertyOrder(4)] HistoricalEvidenceSelectionProjectionV1 Selection,
    [property: JsonPropertyOrder(5)] bool TruncatedBefore,
    [property: JsonPropertyOrder(6)] long TruncatedSessionCount,
    [property: JsonPropertyOrder(7)] IReadOnlyList<HistoricalEvidenceSessionV1> Sessions,
    [property: JsonPropertyOrder(8)] IReadOnlyList<HistoricalExcludedSessionV1> ExcludedSessions,
    [property: JsonPropertyOrder(9)] IReadOnlyList<HistoricalEvidenceGroupV1> EvidenceGroups,
    [property: JsonPropertyOrder(10)] HistoricalEvidenceDistributionV1 Distribution);

internal sealed record HistoricalEvidenceExtractionV1(
    HistoricalEvidenceDatasetV1 RawLocal,
    HistoricalEvidenceDatasetV1 RepositorySafe,
    byte[] RawLocalBytes,
    byte[] RepositorySafeBytes,
    string RawLocalSha256,
    string RepositorySafeSha256);

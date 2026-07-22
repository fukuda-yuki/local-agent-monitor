using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotAgentObservability.Persistence.Sqlite.HistoricalImport;

public static class HistoricalImportContractVersions
{
    public const string Workflow = "historical-import-workflow/v1";
    public const string SourceSelection = "historical-import-workflow-source-selection/v1";
    public const string Preview = "historical-import-workflow-preview/v1";
    public const string ConfirmationRequest = "historical-import-workflow-confirmation-request/v1";
    public const string Confirmation = "historical-import-workflow-confirmation/v1";
    public const string ImportRequest = "historical-import-workflow-import-request/v1";
    public const string ImportStatus = "historical-import-workflow-import-status/v1";
    public const string ImportResult = "historical-import-workflow-import-result/v1";
    public const string ImportHistory = "historical-import-workflow-import-history/v1";
    public const string ObservationList = "historical-import-workflow-observation-list/v1";
    public const string ObservationDetail = "historical-import-workflow-observation-detail/v1";
}

public static class HistoricalImportErrorCodes
{
    public const string RequestInvalid = "historical_import_request_invalid";
    public const string PreviewNotFound = "historical_import_preview_not_found";
    public const string PreviewExpired = "historical_import_preview_expired";
    public const string PreviewStale = "historical_import_preview_stale";
    public const string SourceChanged = "historical_import_source_changed";
    public const string NoEligibleCandidates = "historical_import_no_eligible_candidates";
    public const string ConfirmationInvalid = "historical_import_confirmation_invalid";
    public const string ConfirmationExpired = "historical_import_confirmation_expired";
    public const string ConfirmationConsumed = "historical_import_confirmation_consumed";
    public const string IdempotencyConflict = "historical_import_idempotency_conflict";
    public const string ResultNotAvailable = "historical_import_result_not_available";
    public const string OperationNotFound = "historical_import_operation_not_found";
    public const string ObservationNotFound = "historical_import_observation_not_found";
    public const string ProfileNotAdmitted = "historical_import_profile_not_admitted";
    public const string CandidateInvalid = "historical_import_candidate_invalid";
    public const string FixtureNotSourceSupportEvidence = "historical_import_fixture_not_source_support_evidence";
    public const string StoreBusy = "historical_import_store_busy";
    public const string StoreUnavailable = "historical_import_store_unavailable";
    public const string TransactionFailed = "historical_import_transaction_failed";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        RequestInvalid, PreviewNotFound, PreviewExpired, PreviewStale, SourceChanged,
        NoEligibleCandidates, ConfirmationInvalid, ConfirmationExpired, ConfirmationConsumed,
        IdempotencyConflict, ResultNotAvailable, OperationNotFound, ObservationNotFound,
        ProfileNotAdmitted, CandidateInvalid, FixtureNotSourceSupportEvidence, StoreBusy,
        StoreUnavailable, TransactionFailed,
    };
}

public sealed class HistoricalImportException(string code, string? operationId = null) : Exception(code)
{
    public string Code { get; } = HistoricalImportErrorCodes.All.Contains(code)
        ? code
        : HistoricalImportErrorCodes.StoreUnavailable;

    public string? OperationId { get; } = operationId;
}

public sealed record HistoricalSourceSelection(
    string SourceSurface,
    string ReferenceKind,
    string? ExactReference,
    string? SessionId,
    string? SourceApplicationVersion,
    string RequestedCapture,
    bool ConsentGranted);

public sealed record HistoricalImportPreviewRequest(
    string ContractVersion,
    string SchemaVersion,
    string SourceSurface,
    string ReferenceKind,
    string? ExactReference,
    string? SourceApplicationVersion,
    string RequestedCapture,
    bool ConsentGranted,
    string? SessionId = null)
{
    public HistoricalSourceSelection ToSelection() => new(
        SourceSurface,
        ReferenceKind,
        ExactReference,
        SessionId,
        SourceApplicationVersion,
        RequestedCapture,
        ConsentGranted);
}

public sealed record HistoricalAdapterResult(
    string ContractVersion,
    string AdapterId,
    string ProfileId,
    string SourceSurface,
    string SourceTier,
    string DetectionState,
    string SourceReferenceState,
    string? SourceApplicationVersion,
    bool SupportAuthorized,
    string SourceFormatProfile,
    int CandidateCount,
    string ContentRisk,
    bool RepositorySafe,
    IReadOnlyList<string> Diagnostics);

public sealed record HistoricalSourceProbe(
    HistoricalAdapterResult AdapterResult,
    HistoricalCandidateBatch? CandidateBatch,
    HistoricalAdmissionEvidence? AdmissionEvidence,
    IReadOnlyList<HistoricalCandidateBinding> CandidateBindings,
    string SnapshotVersion,
    string SnapshotDigest);

public interface IHistoricalSourceGateway
{
    HistoricalSourceProbe Probe(HistoricalSourceSelection selection);
}

public sealed record HistoricalCandidateBatch(
    string ContractVersion,
    bool FixtureOnlyNotSourceSupportEvidence,
    string ProfileId,
    string AdapterId,
    string SourceSurface,
    string SourceTier,
    string SourceApplicationVersion,
    string SourceFormatName,
    string SourceFormatVersion,
    string SourceFixtureSha256,
    string SourceSchemaFingerprint,
    string NormalizationVersion,
    string CompletenessCeiling,
    IReadOnlyList<HistoricalCandidate> Candidates);

public sealed record HistoricalCandidate(
    string CandidateKey,
    string SourceRecordKey,
    HistoricalCandidateValues Values,
    string Completeness,
    IReadOnlyList<string> CompletenessReasons,
    IReadOnlyList<HistoricalFieldProvenance> FieldProvenance);

public sealed record HistoricalFieldValue(string Field, string CanonicalJson);

public sealed record HistoricalCandidateValues(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HistoricalModelTokenValues? ModelTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HistoricalRetryAttemptValues? RetryAttempt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HistoricalErrorValues? Errors = null);

public sealed record HistoricalModelTokenValues(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? InputTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? OutputTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? TotalTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? CacheTokens = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ReasoningTokens = null);

public sealed record HistoricalRetryAttemptValues(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Retry = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Attempt = null);

public sealed record HistoricalErrorValues(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Present = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] JsonElement Code = default);

public sealed record HistoricalFieldProvenance(
    string Field,
    string AdapterId,
    string SourceSurface,
    string SourceApplicationVersion,
    string SourceFormatName,
    string SourceFormatVersion,
    string SourceFixtureSha256,
    string SourceSchemaFingerprint,
    string SourceRecordKey,
    string CaptureContentState,
    string NormalizationVersion);

public sealed record HistoricalCandidateBinding(string CandidateKey, string Basis, string TargetToken);

public sealed record HistoricalAdmissionEvidence(
    string ProfileId,
    string AdapterId,
    string SourceApplicationVersion,
    string SourceFormatName,
    string SourceFormatVersion,
    string SourceFixtureSha256,
    string SourceSchemaFingerprint,
    string GoldenTestId);

public sealed record HistoricalAdmissionProfile(
    string ProfileId,
    string AdapterId,
    string SourceSurface,
    string SourceApplicationVersion,
    string SourceFormatName,
    string SourceFormatVersion,
    string SourceFixtureSha256,
    string SourceSchemaFingerprint,
    string GoldenTestId,
    string NormalizationVersion,
    IReadOnlyList<string> ActiveFieldAllowlist);

public interface IHistoricalAdmissionRegistry
{
    bool TryResolve(
        HistoricalAdapterResult adapterResult,
        HistoricalCandidateBatch batch,
        HistoricalAdmissionEvidence evidence,
        out HistoricalAdmissionProfile profile);
}

public sealed class HistoricalAdmissionRegistry(IEnumerable<HistoricalAdmissionProfile> profiles) : IHistoricalAdmissionRegistry
{
    public static HistoricalAdmissionRegistry Empty { get; } = new([]);

    private readonly IReadOnlyList<HistoricalAdmissionProfile> profiles = profiles.ToArray();

    public bool TryResolve(
        HistoricalAdapterResult adapterResult,
        HistoricalCandidateBatch batch,
        HistoricalAdmissionEvidence evidence,
        out HistoricalAdmissionProfile profile)
    {
        var matches = profiles.Where(candidate =>
            candidate.ProfileId == evidence.ProfileId
            && candidate.AdapterId == evidence.AdapterId
            && candidate.SourceSurface == batch.SourceSurface
            && candidate.SourceApplicationVersion == evidence.SourceApplicationVersion
            && candidate.SourceFormatName == evidence.SourceFormatName
            && candidate.SourceFormatVersion == evidence.SourceFormatVersion
            && candidate.SourceFixtureSha256 == evidence.SourceFixtureSha256
            && candidate.SourceSchemaFingerprint == evidence.SourceSchemaFingerprint
            && candidate.GoldenTestId == evidence.GoldenTestId
            && candidate.NormalizationVersion == batch.NormalizationVersion
            && batch.ProfileId == evidence.ProfileId
            && batch.AdapterId == evidence.AdapterId
            && batch.SourceApplicationVersion == evidence.SourceApplicationVersion
            && batch.SourceFormatName == evidence.SourceFormatName
            && batch.SourceFormatVersion == evidence.SourceFormatVersion
            && batch.SourceFixtureSha256 == evidence.SourceFixtureSha256
            && batch.SourceSchemaFingerprint == evidence.SourceSchemaFingerprint
            && adapterResult.ProfileId == candidate.ProfileId
            && adapterResult.AdapterId == candidate.AdapterId
            && adapterResult.SourceSurface == candidate.SourceSurface
            && adapterResult.SourceApplicationVersion == candidate.SourceApplicationVersion)
            .Take(2)
            .ToArray();
        profile = matches.Length == 1 ? matches[0] : null!;
        return matches.Length == 1;
    }
}

public sealed record HistoricalImportCount(string Availability, int? Value)
{
    public static HistoricalImportCount Available(int value) => new("available", value);
    public static HistoricalImportCount Unavailable { get; } = new("unavailable", null);
}

public sealed record HistoricalImportTimeRange(string Availability, string? Start, string? End);
public sealed record HistoricalImportSourceFormat(string? Name, string? Version);
public sealed record HistoricalImportMergeCandidate(string MergeCandidateId, string BindingBasis);
public sealed record HistoricalImportRetentionImpact(string Disposition, int CreatedItemCount, int? DefaultTtlDays, bool AutomaticPin);
public sealed record HistoricalImportExclusion(string Code, int Count);

public sealed record HistoricalImportPreviewCounts(
    HistoricalImportCount Total,
    HistoricalImportCount Eligible,
    HistoricalImportCount Unsupported,
    HistoricalImportCount Malformed,
    HistoricalImportCount Duplicates,
    HistoricalImportCount Conflicts,
    HistoricalImportCount NewObservations,
    HistoricalImportCount NewSessions,
    HistoricalImportCount NewEvents,
    HistoricalImportCount MergeCandidates,
    HistoricalImportCount Excluded);

public sealed record HistoricalImportPreview(
    string ContractVersion,
    string SchemaVersion,
    string PreviewId,
    string PreviewDigest,
    string SnapshotVersion,
    int ExpiresAfterSeconds,
    string SourceSelectionId,
    string SourceKind,
    string SourceSurface,
    string SourceBadge,
    string SourceTier,
    string ProfileId,
    string AdapterId,
    string AdapterState,
    IReadOnlyList<string> AdapterDiagnostics,
    string EvidenceStatus,
    string? SourceApplicationVersion,
    HistoricalImportSourceFormat SourceFormat,
    string RequestedCapture,
    HistoricalImportTimeRange SourceTimeRange,
    HistoricalImportPreviewCounts Counts,
    string ContentRisk,
    string CompletenessCeiling,
    IReadOnlyList<string> CompletenessReasons,
    IReadOnlyList<string> MissingCapabilities,
    IReadOnlyList<HistoricalImportMergeCandidate> MergeCandidates,
    HistoricalImportRetentionImpact RetentionImpact,
    IReadOnlyList<HistoricalImportExclusion> Exclusions,
    bool CommitAllowed,
    string? RejectionCode);

public sealed record HistoricalImportConfirmationRequest(
    string ContractVersion,
    string SchemaVersion,
    string PreviewId,
    string PreviewDigest,
    string SnapshotVersion,
    string Decision);

public sealed record HistoricalImportConfirmation(
    string ContractVersion,
    string SchemaVersion,
    string ConfirmationId,
    string PreviewId,
    string PreviewDigest,
    string SnapshotVersion,
    string Eligibility,
    string Decision,
    int ExpiresAfterSeconds);

public sealed record HistoricalImportCommitRequest(
    string ContractVersion,
    string SchemaVersion,
    string RequestId,
    string IdempotencyKey,
    string ConfirmationId,
    string PreviewId,
    string PreviewDigest,
    string SnapshotVersion);

public sealed record HistoricalImportStatusCounts(
    int Total,
    int Processed,
    int NewObservations,
    int Duplicates,
    int Conflicts,
    int RecordRejections);

public sealed record HistoricalImportStatus(
    string ContractVersion,
    string SchemaVersion,
    string OperationId,
    string RequestId,
    int OperationVersion,
    string State,
    IReadOnlyList<string> Lifecycle,
    string TransactionOutcome,
    HistoricalImportStatusCounts Counts,
    bool ResultAvailable,
    string? FailureCode);

public sealed record HistoricalImportResultCounts(
    HistoricalImportCount Total,
    HistoricalImportCount NewObservations,
    HistoricalImportCount NewSessions,
    HistoricalImportCount NewEvents,
    HistoricalImportCount Duplicates,
    HistoricalImportCount Conflicts,
    HistoricalImportCount RecordRejections);

public sealed record HistoricalImportObservationResult(
    string ObservationId,
    string IdentityResolution,
    string BindingBasis,
    string Completeness,
    IReadOnlyList<string> CompletenessReasons,
    IReadOnlyList<string> MissingCapabilities,
    string ContentState);

public sealed record HistoricalImportDuplicateResult(
    string RecordResultId,
    string ObservationId,
    string CandidateFingerprint,
    string Decision);

public sealed record HistoricalImportConflictResult(
    string RecordResultId,
    string ObservationId,
    string Code,
    IReadOnlyList<string> ConflictFields,
    string ExistingFingerprint,
    string IncomingFingerprint,
    string Decision);

public sealed record HistoricalImportRetentionResult(
    string Disposition,
    int CreatedItemCount,
    string PinState,
    string DeletionState);

public sealed record HistoricalImportResult(
    string ContractVersion,
    string SchemaVersion,
    string OperationId,
    string RequestId,
    string PreviewId,
    string PreviewDigest,
    string SnapshotVersion,
    string Outcome,
    string TransactionOutcome,
    string IdempotencyOutcome,
    string SourceKind,
    string SourceSurface,
    string SourceTier,
    string ProfileId,
    string AdapterId,
    string EvidenceStatus,
    HistoricalImportResultCounts Counts,
    IReadOnlyList<HistoricalImportObservationResult> Observations,
    IReadOnlyList<HistoricalImportDuplicateResult> Duplicates,
    IReadOnlyList<HistoricalImportConflictResult> Conflicts,
    HistoricalImportRetentionResult Retention);

public sealed record HistoricalImportHistoryItem(
    string OperationId,
    string State,
    string Outcome,
    string SourceKind,
    string SourceSurface,
    string SourceBadge,
    string SourceTier,
    string ProfileId,
    string AdapterId,
    int NewObservationCount,
    int DuplicateCount,
    int ConflictCount,
    string Completeness,
    IReadOnlyList<string> CompletenessReasons,
    string ContentState,
    string RetentionDisposition);

public sealed record HistoricalImportHistory(
    string ContractVersion,
    string SchemaVersion,
    IReadOnlyList<HistoricalImportHistoryItem> Items);

public sealed record HistoricalObservationListItem(
    string ObservationId,
    string SourceKind,
    string SourceSurface,
    string SourceBadge,
    string SourceTier,
    string Completeness,
    IReadOnlyList<string> CompletenessReasons,
    IReadOnlyList<string> MissingCapabilities,
    string ContentState,
    bool TraceControlsEnabled);

public sealed record HistoricalObservationList(
    string ContractVersion,
    string SchemaVersion,
    string SourceFilter,
    IReadOnlyList<HistoricalObservationListItem> Items,
    string? NextCursor);

public sealed record HistoricalObservationDetail(
    string ContractVersion,
    string SchemaVersion,
    string ObservationId,
    string SourceKind,
    string SourceSurface,
    string SourceBadge,
    string SourceTier,
    string ProfileId,
    string AdapterId,
    string IdentityResolution,
    string BindingBasis,
    string Completeness,
    IReadOnlyList<string> CompletenessReasons,
    IReadOnlyList<string> MissingCapabilities,
    string ContentState,
    IReadOnlyList<string> SummaryFields,
    bool TraceControlsEnabled,
    string RetentionDisposition);

public interface IHistoricalImportApplication
{
    HistoricalImportPreview CreatePreview(HistoricalImportPreviewRequest request);
    HistoricalImportPreview ReadPreview(string previewId);
    HistoricalImportConfirmation IssueConfirmation(HistoricalImportConfirmationRequest request);
    HistoricalImportResult Commit(HistoricalImportCommitRequest request);
    HistoricalImportStatus ReadStatus(string operationId);
    HistoricalImportResult ReadResult(string operationId);
    HistoricalImportHistory ListHistory(int limit = 100);
    HistoricalObservationList ListObservations(int limit = 100, string? cursor = null);
    HistoricalObservationDetail GetObservation(string observationId);
}

public static class HistoricalImportJson
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static byte[] Serialize<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, Options);

    public static string SerializeString<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Deserialize<T>(ReadOnlySpan<byte> value)
    {
        EnsureNoDuplicateProperties(value);
        return JsonSerializer.Deserialize<T>(value, Options) ?? throw new JsonException();
    }

    public static T Deserialize<T>(string value) => Deserialize<T>(Encoding.UTF8.GetBytes(value));

    private static JsonSerializerOptions CreateOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 16,
    };

    private static void EnsureNoDuplicateProperties(ReadOnlySpan<byte> value)
    {
        var reader = new Utf8JsonReader(value, new JsonReaderOptions { MaxDepth = 16 });
        var objectProperties = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                objectProperties.Push(new HashSet<string>(StringComparer.Ordinal));
            }
            else if (reader.TokenType == JsonTokenType.EndObject)
            {
                objectProperties.Pop();
            }
            else if (reader.TokenType == JsonTokenType.PropertyName
                && !objectProperties.Peek().Add(reader.GetString()!))
            {
                throw new JsonException();
            }
        }
    }
}

internal static class HistoricalImportIdentifiers
{
    public static string New(string prefix) => prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Digest(string value) => "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string DigestBytes(ReadOnlySpan<byte> value) => "sha256:" + Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

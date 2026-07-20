using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public enum RetentionMutationTargetResolutionOutcome
{
    Resolved,
    EmptyNotApplicable,
    NotFound,
    NotApplicable
}

public sealed record RetentionMutationResolvedItem(
    string ItemId,
    RetentionStoreKind StoreKind,
    RetentionItemLifecycle State,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? ExpiresAt,
    string PolicyId,
    int PolicyVersion,
    DateTimeOffset? ReadDeniedAt,
    DateTimeOffset? QueuedAt,
    long Revision,
    bool RetryExhausted,
    RetentionErrorCode? ErrorCode);

public sealed record RetentionMutationTargetResolution(
    RetentionMutationTargetResolutionOutcome Outcome,
    string? ErrorCode,
    RetentionMutationEmptyReason? EmptyReason,
    IReadOnlyList<RetentionMutationResolvedItem> Items,
    int ExcludedItemCount,
    IReadOnlyList<RetentionExclusionSummary> ExcludedItemsByReason)
{
    public bool MutationAllowed => Outcome == RetentionMutationTargetResolutionOutcome.Resolved && Items.Count > 0;
}

public sealed partial class RetentionCatalogStore
{
    public RetentionMutationTargetResolution ResolveMutationTarget(RetentionMutationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!RetentionMutationTargetValidator.Validate(target).IsValid)
            throw new ArgumentException(RetentionMutationErrorCodes.RequestInvalid, nameof(target));

        using var connection = OpenExisting();
        using var transaction = connection.BeginTransaction();
        var result = target.Kind switch
        {
            RetentionMutationTargetKind.Session => ResolveSessionTarget(connection, transaction, target.Id),
            RetentionMutationTargetKind.Item => ResolveItemTarget(connection, transaction, target.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        transaction.Commit();
        return result;
    }

    private RetentionMutationTargetResolution ResolveSessionTarget(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sessionId)
    {
        using (var session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = "SELECT EXISTS(SELECT 1 FROM sessions WHERE session_id=$session COLLATE BINARY);";
            session.Parameters.AddWithValue("$session", sessionId);
            if (Convert.ToInt64(session.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                return NotFound();
        }

        var storeInstanceId = StoreId(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT i.item_id,i.store_instance_id,i.store_kind,i.source_item_id,i.receipt_version,i.ownership_receipt,
                   i.captured_at,i.expires_at,i.policy_id,i.policy_version,i.state,i.revision,
                   i.read_denied_at,i.queued_at,i.retry_exhausted,i.error_code,i.adapter_coverage_version
            FROM retention_items i
            JOIN session_events e ON e.event_id=i.source_item_id COLLATE BINARY
            WHERE i.store_instance_id=$store
              AND i.store_kind='session_event_content'
              AND e.session_id=$session COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$store", storeInstanceId);
        command.Parameters.AddWithValue("$session", sessionId);

        var candidates = new List<MutationCandidate>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            candidates.Add(ReadCandidate(reader));

        var selected = new List<RetentionMutationResolvedItem>();
        var missingOwnershipProof = 0;
        foreach (var candidate in candidates)
        {
            if (!TryCreateResolvedItem(candidate, out var item) || !CatalogOwnershipProofMatches(candidate, storeInstanceId, RetentionStoreKind.SessionEventContent))
            {
                missingOwnershipProof++;
                continue;
            }

            selected.Add(item!);
        }

        var exclusions = missingOwnershipProof == 0
            ? Array.Empty<RetentionExclusionSummary>()
            : [new(RetentionMutationExclusionCodes.MissingOwnershipProof, missingOwnershipProof)];
        if (selected.Count == 0)
        {
            return new(
                RetentionMutationTargetResolutionOutcome.EmptyNotApplicable,
                null,
                missingOwnershipProof == 0 ? RetentionMutationEmptyReason.NoExactOwnedItems : RetentionMutationEmptyReason.AllCandidatesExcluded,
                [],
                missingOwnershipProof,
                exclusions);
        }

        return new(RetentionMutationTargetResolutionOutcome.Resolved, null, null, selected, missingOwnershipProof, exclusions);
    }

    private RetentionMutationTargetResolution ResolveItemTarget(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId)
    {
        var storeInstanceId = StoreId(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,
                   captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,
                   retry_exhausted,error_code,adapter_coverage_version
            FROM retention_items
            WHERE store_instance_id=$store AND item_id=$item COLLATE BINARY;
            """;
        command.Parameters.AddWithValue("$store", storeInstanceId);
        command.Parameters.AddWithValue("$item", itemId);
        MutationCandidate candidate;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read()) return NotFound();
            candidate = ReadCandidate(reader);
        }
        if (!TryParseStoreKind(candidate.StoreKindWire, out var storeKind)
            || !TryCreateResolvedItem(candidate, out var item)
            || !CatalogOwnershipProofMatches(candidate, storeInstanceId, storeKind))
            return NotApplicable();

        return new(RetentionMutationTargetResolutionOutcome.Resolved, null, null, [item!], 0, []);
    }

    private static bool CatalogOwnershipProofMatches(
        MutationCandidate candidate,
        string storeInstanceId,
        RetentionStoreKind storeKind)
    {
        if (candidate.StoreInstanceId != storeInstanceId
            || candidate.StoreKindWire != RetentionSchemaMigrator.Wire(storeKind)
            || candidate.ReceiptVersion != 1
            || candidate.AdapterCoverageVersion != RetentionV1Constants.AdapterCoverageVersion
            || string.IsNullOrEmpty(candidate.SourceItemId)
            || candidate.OwnershipReceipt is not { Length: 32 }
            || !candidate.OwnershipReceipt.Any(static value => value != 0))
            return false;
        return true;
    }

    private static MutationCandidate ReadCandidate(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetInt32(4),
        reader.GetFieldValue<byte[]>(5),
        ParseOptionalTimestamp(reader, 6),
        ParseOptionalTimestamp(reader, 7),
        reader.GetString(8),
        reader.GetInt32(9),
        ParseLifecycle(reader.GetString(10)),
        reader.GetInt64(11),
        ParseOptionalTimestamp(reader, 12),
        ParseOptionalTimestamp(reader, 13),
        reader.GetInt64(14) != 0,
        reader.IsDBNull(15) ? null : ParseErrorCode(reader.GetString(15)),
        reader.GetInt32(16));

    private static bool TryCreateResolvedItem(MutationCandidate candidate, out RetentionMutationResolvedItem? item)
    {
        item = null;
        if (!TryParseStoreKind(candidate.StoreKindWire, out var storeKind)) return false;
        item = new(
            candidate.ItemId,
            storeKind,
            candidate.State,
            candidate.CapturedAt,
            candidate.ExpiresAt,
            candidate.PolicyId,
            candidate.PolicyVersion,
            candidate.ReadDeniedAt,
            candidate.QueuedAt,
            candidate.Revision,
            candidate.RetryExhausted,
            candidate.ErrorCode);
        return true;
    }

    private static bool TryParseStoreKind(string value, out RetentionStoreKind kind)
    {
        kind = value switch
        {
            "session_event_content" => RetentionStoreKind.SessionEventContent,
            "raw_record" => RetentionStoreKind.RawRecord,
            "analysis_run_raw" => RetentionStoreKind.AnalysisRunRaw,
            "sensitive_bundle" => RetentionStoreKind.SensitiveBundle,
            "analysis_sdk_directory" => RetentionStoreKind.AnalysisSdkDirectory,
            _ => default
        };
        return value is "session_event_content" or "raw_record" or "analysis_run_raw" or "sensitive_bundle" or "analysis_sdk_directory";
    }

    private static RetentionErrorCode? ParseErrorCode(string value) => value switch
    {
        "retention_migration_blocked" => RetentionErrorCode.MigrationBlocked,
        "retention_missing_timestamp" => RetentionErrorCode.MissingTimestamp,
        "retention_invalid_identity" => RetentionErrorCode.InvalidIdentity,
        "retention_ownership_mismatch" => RetentionErrorCode.OwnershipMismatch,
        "retention_capture_incomplete" => RetentionErrorCode.CaptureIncomplete,
        "retention_lease_conflict" => RetentionErrorCode.LeaseConflict,
        "retention_lease_lost" => RetentionErrorCode.LeaseLost,
        "retention_delete_busy" => RetentionErrorCode.DeleteBusy,
        "retention_delete_permission_denied" => RetentionErrorCode.DeletePermissionDenied,
        "retention_delete_io_failed" => RetentionErrorCode.DeleteIoFailed,
        "retention_unexpected_source_missing" => RetentionErrorCode.UnexpectedSourceMissing,
        "retention_maintenance_busy" => RetentionErrorCode.MaintenanceBusy,
        "retention_item_limit_exceeded" => RetentionErrorCode.ItemLimitExceeded,
        _ => null
    };

    private static DateTimeOffset? ParseOptionalTimestamp(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.ParseExact(reader.GetString(ordinal), "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static RetentionMutationTargetResolution NotFound() =>
        new(RetentionMutationTargetResolutionOutcome.NotFound, RetentionMutationErrorCodes.TargetNotFound, null, [], 0, []);

    private static RetentionMutationTargetResolution NotApplicable() =>
        new(RetentionMutationTargetResolutionOutcome.NotApplicable, RetentionMutationErrorCodes.TargetNotApplicable, null, [], 0, []);

    private sealed record MutationCandidate(
        string ItemId,
        string StoreInstanceId,
        string StoreKindWire,
        string SourceItemId,
        int ReceiptVersion,
        byte[] OwnershipReceipt,
        DateTimeOffset? CapturedAt,
        DateTimeOffset? ExpiresAt,
        string PolicyId,
        int PolicyVersion,
        RetentionItemLifecycle State,
        long Revision,
        DateTimeOffset? ReadDeniedAt,
        DateTimeOffset? QueuedAt,
        bool RetryExhausted,
        RetentionErrorCode? ErrorCode,
        int AdapterCoverageVersion);
}

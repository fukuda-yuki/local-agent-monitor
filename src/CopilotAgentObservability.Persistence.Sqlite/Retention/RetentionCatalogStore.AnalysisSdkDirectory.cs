using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    private SourceReceiptProof ValidateAnalysisSdkDirectoryFirstIntentEvidence(SqliteConnection connection, SqliteTransaction transaction, string itemId, string store, string captureId, byte[] receipt)
    {
        try
        {
            if (!IsCanonicalId(captureId) || !string.Equals(store, StoreId(connection, transaction), StringComparison.Ordinal)) return SourceReceiptProof.InvalidIdentity;
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText = "SELECT private_locator FROM retention_items WHERE item_id=$id;";
            item.Parameters.AddWithValue("$id", itemId);
            var locator = item.ExecuteScalar() as string;
            var row = LoadSdkReservationByCapture(connection, transaction, captureId);
            if (row is null) return SourceReceiptProof.Missing;
            if (locator is null || row.Phase != RetentionAnalysisSdkDirectoryPhase.Active || !string.Equals(locator, row.ChildLocator, StringComparison.Ordinal)
                || !string.Equals(row.StoreInstanceId, store, StringComparison.Ordinal)
                || HasActiveOperationLease(connection, transaction, itemId)
                || !RetentionOwnershipReceipt.Matches(receipt, RetentionOwnershipReceipt.CreateAnalysisSdkDirectory(new(row.StoreInstanceId, row.CaptureId, row.RunId, row.RequestedAtText, row.RequestedAtTicks, row.MarkerSha256, row.OwnerToken)))) return SourceReceiptProof.InvalidOrMismatched;

            var snapshot = SnapshotSdkDirectory(row);
            if (snapshot is null) return SourceReceiptProof.Missing;
            analysisSdkDirectoryCheckpoint?.Invoke("first_intent_snapshot_created");
            foreach (var member in snapshot)
                SdkExec(connection, transaction, "INSERT INTO retention_analysis_sdk_directory_members(capture_id,ordinal,relative_path,member_kind,byte_length,sha256,deletion_order) VALUES($capture,$ordinal,$path,$kind,$length,$sha,$order);",
                    ("$capture", row.CaptureId), ("$ordinal", member.Ordinal), ("$path", member.RelativePath), ("$kind", Wire(member.Kind)), ("$length", member.ByteLength ?? (object)DBNull.Value), ("$sha", member.Sha256 ?? (object)DBNull.Value), ("$order", member.DeletionOrder));
            analysisSdkDirectoryCheckpoint?.Invoke("first_intent_members_inserted");
            SdkExec(connection, transaction, "UPDATE retention_analysis_sdk_directory_reservations SET phase='sealed',revision=revision+1,updated_at=$now WHERE capture_id=$capture AND phase='active';", ("$now", Timestamp(timeProvider.GetUtcNow())), ("$capture", row.CaptureId));
            analysisSdkDirectoryCheckpoint?.Invoke("first_intent_phase_sealed");
            return SourceReceiptProof.Match;
        }
        catch (SdkMissingException) { return SourceReceiptProof.Missing; }
        catch (SdkIdentityException) { return SourceReceiptProof.InvalidOrMismatched; }
        catch (IOException) { return SourceReceiptProof.CatalogBusy; }
        catch (UnauthorizedAccessException) { return SourceReceiptProof.CatalogBusy; }
    }

    private static SourceReceiptProof ValidateAnalysisSdkDirectoryActiveEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string store,
        string captureId,
        byte[] receipt,
        bool verifyMarker)
    {
        try
        {
            if (!IsCanonicalId(captureId)
                || !string.Equals(store, StoreId(connection, transaction), StringComparison.Ordinal))
                return SourceReceiptProof.InvalidIdentity;
            using var item = connection.CreateCommand();
            item.Transaction = transaction;
            item.CommandText =
                "SELECT private_locator FROM retention_items WHERE store_instance_id=$store AND store_kind='analysis_sdk_directory' AND source_item_id=$capture;";
            item.Parameters.AddWithValue("$store", store);
            item.Parameters.AddWithValue("$capture", captureId);
            var locator = item.ExecuteScalar() as string;
            var row = LoadSdkReservationByCapture(connection, transaction, captureId);
            if (row is null) return SourceReceiptProof.Missing;
            var authority = LoadAnalysisRunAuthority(connection, transaction, row.RunId);
            if (locator is null
                || authority is null
                || row.Phase != RetentionAnalysisSdkDirectoryPhase.Active
                || !string.Equals(row.StoreInstanceId, store, StringComparison.Ordinal)
                || !string.Equals(row.ChildLocator, locator, StringComparison.Ordinal)
                || !string.Equals(row.RequestedAtText, authority.RequestedAtText, StringComparison.Ordinal)
                || row.RequestedAtTicks != authority.RequestedAtTicks
                || !RetentionOwnershipReceipt.Matches(row.AnalysisOwnerTokenSha256, SHA256.HashData(authority.OwnerToken))
                || !RetentionOwnershipReceipt.Matches(
                    receipt,
                    RetentionOwnershipReceipt.CreateAnalysisSdkDirectory(
                        new(
                            row.StoreInstanceId,
                            row.CaptureId,
                            row.RunId,
                            row.RequestedAtText,
                            row.RequestedAtTicks,
                            row.MarkerSha256,
                            row.OwnerToken))))
                return SourceReceiptProof.InvalidOrMismatched;
            if (!verifyMarker) return SourceReceiptProof.Match;

            if (EntryKind(row.ChildLocator) == SdkEntryKind.Absent)
                return SourceReceiptProof.Missing;
            if (EntryKind(row.ChildLocator) != SdkEntryKind.Directory)
                return SourceReceiptProof.InvalidOrMismatched;
            var markerPath = Path.Combine(row.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName);
            if (EntryKind(markerPath) == SdkEntryKind.Absent)
                return SourceReceiptProof.Missing;
            if (EntryKind(markerPath) != SdkEntryKind.File)
                return SourceReceiptProof.InvalidOrMismatched;
            var expectedMarker = RetentionAnalysisSdkDirectoryOwnershipMarker.Create(
                row.StoreInstanceId,
                row.CaptureId,
                row.RunId,
                row.RequestedAtText,
                row.RequestedAtTicks,
                row.OwnerToken);
            var marker = ReadBounded(markerPath, expectedMarker.Length);
            return marker.Length == expectedMarker.Length
                && CryptographicOperations.FixedTimeEquals(marker.Bytes, expectedMarker)
                && RetentionOwnershipReceipt.Matches(marker.Sha, row.MarkerSha256)
                    ? SourceReceiptProof.Match
                    : SourceReceiptProof.InvalidOrMismatched;
        }
        catch (SdkIdentityException) { return SourceReceiptProof.InvalidOrMismatched; }
        catch (ArgumentException) { return SourceReceiptProof.InvalidIdentity; }
        catch (FormatException) { return SourceReceiptProof.InvalidIdentity; }
        catch (UnauthorizedAccessException) { return SourceReceiptProof.CatalogBusy; }
        catch (IOException) { return SourceReceiptProof.CatalogBusy; }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return SourceReceiptProof.CatalogBusy; }
    }

    internal RetentionAnalysisSdkDirectoryDeletionPlanResult LoadAnalysisSdkDirectoryDeletionPlan(RetentionDeleteContext context, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            using var item = connection.CreateCommand(); item.Transaction = transaction;
            item.CommandText = "SELECT store_instance_id,source_item_id,ownership_receipt,private_locator,state,revision,adapter_coverage_version FROM retention_items WHERE item_id=$id;";
            item.Parameters.AddWithValue("$id", context.ItemId);
            using var reader = item.ExecuteReader();
            if (!reader.Read()) return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.LeaseLost, null);
            var store = reader.GetString(0); var capture = reader.GetString(1); var receipt = reader.GetFieldValue<byte[]>(2); var locator = reader.IsDBNull(3) ? null : reader.GetString(3); var state = reader.GetString(4); var revision = reader.GetInt64(5); var coverage = reader.GetInt32(6); reader.Close();
            if (context.StoreKind != RetentionStoreKind.AnalysisSdkDirectory || store != context.StoreInstanceId || capture != context.SourceIdentity.SourceItemId
                || !RetentionOwnershipReceipt.Matches(Convert.FromBase64String(context.SourceIdentity.OwnershipReceipt), receipt) || locator is null || context.PrivateLocator?.OpaqueHandle != locator
                || state != "deleting" || revision != context.ExpectedRevision || coverage != RetentionV1Constants.AdapterCoverageVersion
                || !CurrentJournalMatches(connection, transaction, context) || !Owns(connection, transaction, new(context.ItemId, context.ExpectedRevision, context.LeaseOwner, context.LeaseGeneration), now))
                return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.LeaseLost, null);
            var row = LoadSdkReservationByCapture(connection, transaction, capture);
            if (row is null) return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.Missing, null);
            if (row.Phase != RetentionAnalysisSdkDirectoryPhase.Sealed || row.ChildLocator != locator || row.StoreInstanceId != store || !RetentionOwnershipReceipt.Matches(receipt, RetentionOwnershipReceipt.CreateAnalysisSdkDirectory(new(row.StoreInstanceId, row.CaptureId, row.RunId, row.RequestedAtText, row.RequestedAtTicks, row.MarkerSha256, row.OwnerToken)))) return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.OwnershipMismatch, null);
            var members = LoadSdkMembers(connection, transaction, capture);
            if (members.Count == 0 || context.IntentCursor is < 0 or > 257 || context.IntentCursor > members.Count + 1) return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.InvalidIdentity, null);
            transaction.Commit();
            return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.Ready, new(row.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.Create(row.StoreInstanceId, row.CaptureId, row.RunId, row.RequestedAtText, row.RequestedAtTicks, row.OwnerToken), row.MarkerSha256, members, context.IntentCursor));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.Busy, null); }
        catch (FormatException) { return new(RetentionAnalysisSdkDirectoryDeletionPlanDisposition.InvalidIdentity, null); }
    }

    internal RetentionAnalysisSdkDirectoryReservation ReserveAnalysisSdkDirectory(long analysisRunId, string configuredParent)
    {
        if (context is null || analysisRunId <= 0 || !TryCanonicalParent(configuredParent, out var parent)) throw new RetentionCatalogUnavailableException();
        try
        {
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction();
            var store = StoreId(connection, transaction);
            if (!string.Equals(store, context.StoreInstanceId, StringComparison.Ordinal)) throw new RetentionCatalogUnavailableException();
            var existing = LoadSdkReservation(connection, transaction, analysisRunId);
            if (existing is not null)
            {
                if (!string.Equals(existing.ParentLocator, parent, StringComparison.Ordinal)) throw new RetentionCatalogUnavailableException();
                transaction.Commit();
                return existing.ToReservation();
            }

            var run = LoadAnalysisRunAuthority(connection, transaction, analysisRunId);
            if (run is null) throw new RetentionCatalogUnavailableException();
            var captureId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var token = RandomNumberGenerator.GetBytes(32);
            var child = Path.Combine(parent, captureId);
            var marker = RetentionAnalysisSdkDirectoryOwnershipMarker.Create(store, captureId, analysisRunId, run.RequestedAtText, run.RequestedAtTicks, token);
            var markerDigest = SHA256.HashData(marker);
            SdkExec(connection, transaction, "INSERT INTO retention_analysis_sdk_directory_reservations(capture_id,analysis_run_id,store_instance_id,requested_at,requested_at_utc_ticks,parent_locator,child_locator,analysis_owner_token_sha256,owner_token,marker_sha256,phase,error_code,revision,updated_at) VALUES($capture,$run,$store,$requested,$ticks,$parent,$child,$runToken,$token,$marker,'reserved',NULL,1,$updated);",
                ("$capture", captureId), ("$run", analysisRunId), ("$store", store), ("$requested", run.RequestedAtText), ("$ticks", run.RequestedAtTicks), ("$parent", parent), ("$child", child), ("$runToken", SHA256.HashData(run.OwnerToken)), ("$token", token), ("$marker", markerDigest), ("$updated", Timestamp(timeProvider.GetUtcNow())));
            transaction.Commit();
            return new RetentionAnalysisSdkDirectoryReservation(captureId, analysisRunId, store, parent, child, token, marker, markerDigest, run.RequestedAtText, run.RequestedAtTicks, RetentionAnalysisSdkDirectoryPhase.Reserved, 1);
        }
        catch (RetentionCatalogUnavailableException) { throw; }
        catch (Exception exception) when (exception is SqliteException or FormatException or ArgumentException or InvalidOperationException) { throw new RetentionCatalogUnavailableException(); }
    }

    internal RetentionAnalysisSdkDirectoryActivationResult ActivateAnalysisSdkDirectoryAndAcquireOperationLease(RetentionAnalysisSdkDirectoryReservation reservation, byte[] marker, bool exclusivelyCreatedEmptyChild)
        => ActivateAnalysisSdkDirectoryAndAcquireOperationLeaseCore(
            reservation,
            marker,
            exclusivelyCreatedEmptyChild,
            prepareOwnedChild: null);

    internal RetentionAnalysisSdkDirectoryActivationResult PrepareAndActivateAnalysisSdkDirectoryAndAcquireOperationLease(
        RetentionAnalysisSdkDirectoryReservation reservation,
        Action prepareOwnedChild)
    {
        ArgumentNullException.ThrowIfNull(prepareOwnedChild);
        return ActivateAnalysisSdkDirectoryAndAcquireOperationLeaseCore(
            reservation,
            reservation?.OwnershipMarker ?? [],
            exclusivelyCreatedEmptyChild: true,
            prepareOwnedChild);
    }

    private RetentionAnalysisSdkDirectoryActivationResult ActivateAnalysisSdkDirectoryAndAcquireOperationLeaseCore(
        RetentionAnalysisSdkDirectoryReservation reservation,
        byte[] marker,
        bool exclusivelyCreatedEmptyChild,
        Action? prepareOwnedChild)
    {
        if (reservation is null || marker is not { Length: > 0 } || !exclusivelyCreatedEmptyChild) return RetentionAnalysisSdkDirectoryActivationResult.Closed;
        try
        {
            using var connection = OpenExisting();
            analysisSdkDirectoryCheckpoint?.Invoke("activation_transaction_starting");
            using var transaction = connection.BeginTransaction(deferred: false);
            analysisSdkDirectoryCheckpoint?.Invoke("activation_transaction_began");
            var transactionNow = timeProvider.GetUtcNow();
            var store = StoreId(connection, transaction);
            var row = LoadSdkReservation(connection, transaction, reservation.AnalysisRunId);
            var authority = LoadAnalysisRunAuthority(connection, transaction, reservation.AnalysisRunId);
            if (row is null
                || authority is null
                || !row.Matches(reservation)
                || !string.Equals(row.StoreInstanceId, store, StringComparison.Ordinal)
                || !string.Equals(row.RequestedAtText, authority.RequestedAtText, StringComparison.Ordinal)
                || row.RequestedAtTicks != authority.RequestedAtTicks
                || !RetentionOwnershipReceipt.Matches(row.AnalysisOwnerTokenSha256, SHA256.HashData(authority.OwnerToken))
                || row.Phase != RetentionAnalysisSdkDirectoryPhase.Reserved
                || !RetentionOwnershipReceipt.Matches(row.MarkerSha256, SHA256.HashData(marker)))
            {
                transaction.Commit();
                return RetentionAnalysisSdkDirectoryActivationResult.Closed;
            }
            if (row.RequestedAt + RetentionV1Constants.RawDefaultTtl <= transactionNow) { transaction.Commit(); return RetentionAnalysisSdkDirectoryActivationResult.Closed; }
            prepareOwnedChild?.Invoke();
            var receipt = RetentionOwnershipReceipt.CreateAnalysisSdkDirectory(new(row.StoreInstanceId, row.CaptureId, row.RunId, row.RequestedAtText, row.RequestedAtTicks, row.MarkerSha256, row.OwnerToken));
            var itemId = Guid.NewGuid().ToString("N");
            var key = new RetentionOwnershipKey(row.StoreInstanceId, RetentionStoreKind.AnalysisSdkDirectory, row.CaptureId);
            SdkExec(connection, transaction, "INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,private_locator,captured_at,expires_at,policy_id,policy_version,state,revision,adapter_coverage_version) VALUES($item,$store,'analysis_sdk_directory',$source,1,$receipt,$locator,$captured,$expires,'raw-default-90d',1,'expiring',1,1);", ("$item", itemId), ("$store", row.StoreInstanceId), ("$source", row.CaptureId), ("$receipt", receipt), ("$locator", row.ChildLocator), ("$captured", row.RequestedAtText), ("$expires", Timestamp(row.RequestedAt + RetentionV1Constants.RawDefaultTtl)));
            analysisSdkDirectoryCheckpoint?.Invoke("activation_item_inserted");
            var catalogItem = FindForUpdate(connection, transaction, key);
            if (catalogItem is null
                || !string.Equals(catalogItem.ItemId, itemId, StringComparison.Ordinal)
                || ClassifyRowReadability(catalogItem, transactionNow) != RetentionRowReadability.Readable)
                throw new RetentionCatalogUnavailableException();
            var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            var generation = AcquireLease(connection, transaction, itemId, RetentionLeaseKind.Operation, owner, transactionNow);
            if (generation is null) throw new RetentionCatalogUnavailableException();
            analysisSdkDirectoryCheckpoint?.Invoke("activation_lease_inserted");
            using (var phase = connection.CreateCommand()) { phase.Transaction = transaction; phase.CommandText = "UPDATE retention_analysis_sdk_directory_reservations SET phase='active',revision=revision+1,updated_at=$updated WHERE capture_id=$capture AND phase='reserved';"; phase.Parameters.AddWithValue("$capture", row.CaptureId); phase.Parameters.AddWithValue("$updated", Timestamp(transactionNow)); if (phase.ExecuteNonQuery() != 1) throw new RetentionCatalogUnavailableException(); }
            analysisSdkDirectoryCheckpoint?.Invoke("activation_phase_updated");
            if (ValidateAnalysisSdkDirectoryActiveEvidence(connection, transaction, row.StoreInstanceId, row.CaptureId, receipt, verifyMarker: true) != SourceReceiptProof.Match)
                throw new RetentionCatalogUnavailableException();
            var sourceToken = SourceToken(connection, transaction, key);
            if (sourceToken is null) throw new RetentionCatalogUnavailableException();
            var grant = new RetentionReadGrant(
                key,
                itemId,
                catalogItem.Revision,
                RetentionLeaseKind.Operation,
                owner,
                generation.Value,
                transactionNow.Add(RetentionV1Constants.LeaseDuration),
                sourceToken);
            if (!IsGrantUsable(connection, transaction, grant, transactionNow))
                throw new RetentionCatalogUnavailableException();
            transaction.Commit();
            return RetentionAnalysisSdkDirectoryActivationResult.Active(new(grant, row.CaptureId, reservation));
        }
        catch (RetentionCatalogUnavailableException) { return RetentionAnalysisSdkDirectoryActivationResult.Closed; }
        catch (Exception exception) when (exception is SqliteException or ArgumentException or InvalidOperationException) { return RetentionAnalysisSdkDirectoryActivationResult.Closed; }
    }

    internal RetentionOperationRenewalDisposition RenewAnalysisSdkDirectoryOperationLease(RetentionAnalysisSdkDirectoryOperationLease lease)
    {
        if (lease is null) return RetentionOperationRenewalDisposition.LeaseLost;
        return RenewOperationLease(lease.Grant);
    }

    internal RetentionAnalysisSdkDirectoryLeaseObservation RenewAndObserveAnalysisSdkDirectoryOperationLease(
        RetentionAnalysisSdkDirectoryOperationLease lease)
    {
        if (lease is null)
        {
            var observedAt = timeProvider.GetUtcNow();
            return new(
                RetentionOperationRenewalDisposition.LeaseLost,
                observedAt,
                DateTimeOffset.MinValue);
        }

        var disposition = RenewOperationLease(lease.Grant);
        using var publication = lease.Grant.EnterLeasePublication();
        analysisSdkDirectoryCheckpoint?.Invoke("renewal_observation_starting");
        return new(disposition, timeProvider.GetUtcNow(), publication.LeaseExpiresAt);
    }

    internal RetentionMutationDisposition ReleaseAnalysisSdkDirectoryOperationLease(RetentionAnalysisSdkDirectoryOperationLease lease)
    {
        if (lease is null) return RetentionMutationDisposition.StaleNoOp;
        try { using var c = OpenExisting(); using var t = c.BeginTransaction(deferred: false); using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "DELETE FROM retention_leases WHERE item_id=$item AND lease_kind=$kind AND owner=$owner AND generation=$generation;"; q.Parameters.AddWithValue("$item", lease.Grant.ItemId); q.Parameters.AddWithValue("$kind", lease.Grant.LeaseKind.ToString().ToLowerInvariant()); q.Parameters.AddWithValue("$owner", lease.Grant.LeaseOwner); q.Parameters.AddWithValue("$generation", lease.Grant.LeaseGeneration); var count = q.ExecuteNonQuery(); t.Commit(); return count == 1 ? RetentionMutationDisposition.Applied : RetentionMutationDisposition.StaleNoOp; }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return RetentionMutationDisposition.CatalogBusy; }
        catch (SqliteException) { return RetentionMutationDisposition.StaleNoOp; }
    }

    internal RetentionAnalysisSdkDirectoryRecoverySnapshot? LoadAnalysisSdkDirectoryRecovery(long analysisRunId)
    {
        if (analysisRunId <= 0) return null;
        try { using var c = OpenExisting(); using var t = c.BeginTransaction(); return LoadSdkReservation(c, t, analysisRunId)?.ToRecovery(); }
        catch (SqliteException) { return null; }
    }

    internal RetentionCaptureMutationDisposition AbandonReservedAnalysisSdkDirectory(RetentionAnalysisSdkDirectoryReservation reservation)
    {
        if (reservation is null) return RetentionCaptureMutationDisposition.Conflict;
        try
        {
            using var connection = OpenExisting();
            analysisSdkDirectoryCheckpoint?.Invoke("abandon_transaction_starting");
            using var transaction = connection.BeginTransaction(deferred: false);
            var store = StoreId(connection, transaction);
            var row = LoadSdkReservation(connection, transaction, reservation.AnalysisRunId);
            var authority = LoadAnalysisRunAuthority(connection, transaction, reservation.AnalysisRunId);
            if (row is null
                || authority is null
                || row.Phase != RetentionAnalysisSdkDirectoryPhase.Reserved
                || !row.Matches(reservation)
                || !string.Equals(row.StoreInstanceId, store, StringComparison.Ordinal)
                || !string.Equals(row.RequestedAtText, authority.RequestedAtText, StringComparison.Ordinal)
                || row.RequestedAtTicks != authority.RequestedAtTicks
                || !RetentionOwnershipReceipt.Matches(row.AnalysisOwnerTokenSha256, SHA256.HashData(authority.OwnerToken)))
            {
                transaction.Commit();
                return RetentionCaptureMutationDisposition.StaleNoOp;
            }

            var childKind = EntryKind(row.ChildLocator);
            if (childKind == SdkEntryKind.Absent)
                analysisSdkDirectoryCheckpoint?.Invoke("abandon_child_observed_absent");
            var childCleanup = CleanupOwnedReservedSdkChild(
                row,
                allowMarkerlessEmptyChild: true);
            if (childCleanup == ReservedSdkChildCleanupDisposition.Conflict)
            {
                transaction.Commit();
                return RetentionCaptureMutationDisposition.Conflict;
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM retention_analysis_sdk_directory_reservations " +
                "WHERE capture_id=$capture AND analysis_run_id=$run AND store_instance_id=$store " +
                "AND requested_at=$requested AND requested_at_utc_ticks=$ticks " +
                "AND parent_locator=$parent AND child_locator=$child " +
                "AND analysis_owner_token_sha256=$runToken AND owner_token=$token AND marker_sha256=$marker " +
                "AND phase='reserved' AND revision=$revision;";
            delete.Parameters.AddWithValue("$capture", row.CaptureId);
            delete.Parameters.AddWithValue("$run", row.RunId);
            delete.Parameters.AddWithValue("$store", row.StoreInstanceId);
            delete.Parameters.AddWithValue("$requested", row.RequestedAtText);
            delete.Parameters.AddWithValue("$ticks", row.RequestedAtTicks);
            delete.Parameters.AddWithValue("$parent", row.ParentLocator);
            delete.Parameters.AddWithValue("$child", row.ChildLocator);
            delete.Parameters.AddWithValue("$runToken", row.AnalysisOwnerTokenSha256);
            delete.Parameters.AddWithValue("$token", row.OwnerToken);
            delete.Parameters.AddWithValue("$marker", row.MarkerSha256);
            delete.Parameters.AddWithValue("$revision", row.Revision);
            if (delete.ExecuteNonQuery() != 1)
                return RetentionCaptureMutationDisposition.Conflict;
            if (childCleanup == ReservedSdkChildCleanupDisposition.Deleted)
                analysisSdkDirectoryCheckpoint?.Invoke("abandon_child_cleaned_before_catalog_commit");
            transaction.Commit();
            return RetentionCaptureMutationDisposition.Applied;
        }
        catch (SqliteException) { return RetentionCaptureMutationDisposition.Conflict; }
        catch (Exception exception) when (exception is RetentionCatalogUnavailableException
            or SdkIdentityException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            return RetentionCaptureMutationDisposition.Conflict;
        }
    }

    internal RetentionCaptureMutationDisposition CleanupAndAbandonReservedAnalysisSdkDirectory(
        RetentionAnalysisSdkDirectoryReservation reservation,
        bool allowMarkerlessEmptyChild)
    {
        if (reservation is null) return RetentionCaptureMutationDisposition.Conflict;
        try
        {
            using var connection = OpenExisting();
            analysisSdkDirectoryCheckpoint?.Invoke("cleanup_and_abandon_transaction_starting");
            using var transaction = connection.BeginTransaction(deferred: false);
            var store = StoreId(connection, transaction);
            var row = LoadSdkReservation(connection, transaction, reservation.AnalysisRunId);
            var authority = LoadAnalysisRunAuthority(connection, transaction, reservation.AnalysisRunId);
            if (row is null
                || authority is null
                || row.Phase != RetentionAnalysisSdkDirectoryPhase.Reserved
                || !row.Matches(reservation)
                || !string.Equals(row.StoreInstanceId, store, StringComparison.Ordinal)
                || !string.Equals(row.RequestedAtText, authority.RequestedAtText, StringComparison.Ordinal)
                || row.RequestedAtTicks != authority.RequestedAtTicks
                || !RetentionOwnershipReceipt.Matches(row.AnalysisOwnerTokenSha256, SHA256.HashData(authority.OwnerToken)))
            {
                transaction.Commit();
                return RetentionCaptureMutationDisposition.StaleNoOp;
            }

            var childCleanup = CleanupOwnedReservedSdkChild(row, allowMarkerlessEmptyChild);
            if (childCleanup == ReservedSdkChildCleanupDisposition.Conflict)
            {
                transaction.Commit();
                return RetentionCaptureMutationDisposition.Conflict;
            }

            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText =
                "DELETE FROM retention_analysis_sdk_directory_reservations " +
                "WHERE capture_id=$capture AND analysis_run_id=$run AND store_instance_id=$store " +
                "AND requested_at=$requested AND requested_at_utc_ticks=$ticks " +
                "AND parent_locator=$parent AND child_locator=$child " +
                "AND analysis_owner_token_sha256=$runToken AND owner_token=$token AND marker_sha256=$marker " +
                "AND phase='reserved' AND revision=$revision;";
            delete.Parameters.AddWithValue("$capture", row.CaptureId);
            delete.Parameters.AddWithValue("$run", row.RunId);
            delete.Parameters.AddWithValue("$store", row.StoreInstanceId);
            delete.Parameters.AddWithValue("$requested", row.RequestedAtText);
            delete.Parameters.AddWithValue("$ticks", row.RequestedAtTicks);
            delete.Parameters.AddWithValue("$parent", row.ParentLocator);
            delete.Parameters.AddWithValue("$child", row.ChildLocator);
            delete.Parameters.AddWithValue("$runToken", row.AnalysisOwnerTokenSha256);
            delete.Parameters.AddWithValue("$token", row.OwnerToken);
            delete.Parameters.AddWithValue("$marker", row.MarkerSha256);
            delete.Parameters.AddWithValue("$revision", row.Revision);
            if (delete.ExecuteNonQuery() != 1)
                return RetentionCaptureMutationDisposition.Conflict;
            if (childCleanup == ReservedSdkChildCleanupDisposition.Deleted)
                analysisSdkDirectoryCheckpoint?.Invoke("abandon_child_cleaned_before_catalog_commit");
            transaction.Commit();
            return RetentionCaptureMutationDisposition.Applied;
        }
        catch (SqliteException) { return RetentionCaptureMutationDisposition.Conflict; }
        catch (Exception exception) when (exception is RetentionCatalogUnavailableException
            or SdkIdentityException
            or IOException
            or UnauthorizedAccessException
            or ArgumentException
            or InvalidOperationException)
        {
            return RetentionCaptureMutationDisposition.Conflict;
        }
    }

    private ReservedSdkChildCleanupDisposition CleanupOwnedReservedSdkChild(
        SdkRow row,
        bool allowMarkerlessEmptyChild)
    {
        try
        {
            if (EntryKind(row.ChildLocator) != SdkEntryKind.Directory)
                return ReservedSdkChildCleanupDisposition.Preserved;
            var entries = Directory.EnumerateFileSystemEntries(row.ChildLocator).Take(2).ToArray();
            if (entries.Length == 1
                && string.Equals(
                    Path.GetFileName(entries[0]),
                    RetentionAnalysisSdkDirectoryOwnershipMarker.FileName,
                    StringComparison.Ordinal)
                && EntryKind(entries[0]) == SdkEntryKind.File)
            {
                var expectedMarker = RetentionAnalysisSdkDirectoryOwnershipMarker.Create(
                    row.StoreInstanceId,
                    row.CaptureId,
                    row.RunId,
                    row.RequestedAtText,
                    row.RequestedAtTicks,
                    row.OwnerToken);
                if (new FileInfo(entries[0]).Length != expectedMarker.Length)
                    return ReservedSdkChildCleanupDisposition.Preserved;
                var marker = ReadBounded(entries[0], expectedMarker.Length);
                if (marker.Length != expectedMarker.Length
                    || !CryptographicOperations.FixedTimeEquals(marker.Bytes, expectedMarker)
                    || !RetentionOwnershipReceipt.Matches(marker.Sha, row.MarkerSha256))
                    return ReservedSdkChildCleanupDisposition.Preserved;
                File.Delete(entries[0]);
                analysisSdkDirectoryCheckpoint?.Invoke("abandon_marker_deleted_before_empty_recheck");
            }
            else if (!allowMarkerlessEmptyChild || entries.Length != 0)
            {
                return ReservedSdkChildCleanupDisposition.Preserved;
            }

            if (Directory.EnumerateFileSystemEntries(row.ChildLocator).Any())
                return ReservedSdkChildCleanupDisposition.Conflict;
            Directory.Delete(row.ChildLocator, recursive: false);
            return EntryKind(row.ChildLocator) == SdkEntryKind.Absent
                ? ReservedSdkChildCleanupDisposition.Deleted
                : ReservedSdkChildCleanupDisposition.Conflict;
        }
        catch
        {
            return ReservedSdkChildCleanupDisposition.Conflict;
        }
    }

    private static SdkRow? LoadSdkReservationByCapture(SqliteConnection c, SqliteTransaction t, string capture)
    {
        using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = "SELECT capture_id,analysis_run_id,store_instance_id,requested_at,requested_at_utc_ticks,parent_locator,child_locator,analysis_owner_token_sha256,owner_token,marker_sha256,phase,revision FROM retention_analysis_sdk_directory_reservations WHERE capture_id=$capture;";
        q.Parameters.AddWithValue("$capture", capture); using var r = q.ExecuteReader();
        if (!r.Read()) return null;
        var captureId = r.GetString(0); var run = r.GetInt64(1); var store = r.GetString(2); var requested = r.GetString(3); var ticks = r.GetInt64(4); var parent = r.GetString(5); var child = r.GetString(6); var runToken = r.GetFieldValue<byte[]>(7); var token = r.GetFieldValue<byte[]>(8); var marker = r.GetFieldValue<byte[]>(9);
        if (!IsCanonicalId(captureId) || !IsCanonicalId(store) || run <= 0 || !TryCanonicalParent(parent, out var canonicalParent) || child != Path.Combine(canonicalParent, captureId) || runToken.Length != 32 || token.Length != 32 || marker.Length != 32 || r.GetInt64(11) <= 0 || !DateTimeOffset.TryParseExact(requested, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.UtcDateTime.Ticks != ticks || !RetentionOwnershipReceipt.Matches(marker, SHA256.HashData(RetentionAnalysisSdkDirectoryOwnershipMarker.Create(store, captureId, run, requested, ticks, token)))) throw new SdkIdentityException();
        var phase = r.GetString(10) switch { "reserved" => RetentionAnalysisSdkDirectoryPhase.Reserved, "active" => RetentionAnalysisSdkDirectoryPhase.Active, "sealed" => RetentionAnalysisSdkDirectoryPhase.Sealed, _ => throw new SdkIdentityException() };
        return new(captureId, run, store, requested, ticks, parent, child, runToken, token, marker, phase, r.GetInt64(11), parsed);
    }

    private static bool HasActiveOperationLease(SqliteConnection c, SqliteTransaction t, string itemId)
    {
        using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT EXISTS(SELECT 1 FROM retention_leases WHERE item_id=$item AND lease_kind='operation');"; q.Parameters.AddWithValue("$item", itemId); return Convert.ToInt64(q.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static IReadOnlyList<RetentionFileCaptureMember>? SnapshotSdkDirectory(SdkRow row)
    {
        if (EntryKind(row.ChildLocator) == SdkEntryKind.Absent) return null;
        if (EntryKind(row.ChildLocator) != SdkEntryKind.Directory) throw new SdkIdentityException();
        var markerPath = Path.Combine(row.ChildLocator, RetentionAnalysisSdkDirectoryOwnershipMarker.FileName);
        if (EntryKind(markerPath) != SdkEntryKind.File) throw new SdkIdentityException();
        var marker = ReadBounded(markerPath, RetentionFileCaptureContracts.MaximumMemberBytes);
        if (!CryptographicOperations.FixedTimeEquals(marker.Bytes, RetentionAnalysisSdkDirectoryOwnershipMarker.Create(row.StoreInstanceId, row.CaptureId, row.RunId, row.RequestedAtText, row.RequestedAtTicks, row.OwnerToken)) || !RetentionOwnershipReceipt.Matches(SHA256.HashData(marker.Bytes), row.MarkerSha256)) throw new SdkIdentityException();
        var pending = new Stack<(string Path, string Relative)>(); pending.Push((row.ChildLocator, string.Empty));
        var entries = new List<(string Relative, RetentionFileCaptureMemberKind Kind, long? Bytes, byte[]? Sha)>(); long total = 0;
        while (pending.Count != 0)
        {
            var (directory, relative) = pending.Pop();
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(static value => value, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(path); var child = relative.Length == 0 ? name : relative + "/" + name;
                if (!RetentionFileCaptureContracts.IsCanonicalRelativePath(child)) throw new SdkIdentityException();
                var kind = EntryKind(path); if (kind is SdkEntryKind.Absent or SdkEntryKind.Reparse) throw new SdkIdentityException();
                if (kind == SdkEntryKind.Directory) { entries.Add((child, RetentionFileCaptureMemberKind.Directory, null, null)); pending.Push((path, child)); }
                else if (child == RetentionAnalysisSdkDirectoryOwnershipMarker.FileName) entries.Add((child, RetentionFileCaptureMemberKind.OwnerMarker, null, null));
                else { var value = ReadBounded(path, RetentionFileCaptureContracts.MaximumMemberBytes - total); total += value.Length; entries.Add((child, RetentionFileCaptureMemberKind.File, value.Length, value.Sha)); }
                if (entries.Count > RetentionFileCaptureContracts.MaximumMemberCount) throw new SdkIdentityException();
            }
        }
        if (entries.Count == 0 || entries.Count > RetentionFileCaptureContracts.MaximumMemberCount || total > RetentionFileCaptureContracts.MaximumMemberBytes || entries.Count(static e => e.Kind == RetentionFileCaptureMemberKind.OwnerMarker) != 1) throw new SdkIdentityException();
        var ordered = entries.Where(static e => e.Kind == RetentionFileCaptureMemberKind.File).OrderBy(static e => e.Relative, StringComparer.Ordinal)
            .Concat(entries.Where(static e => e.Kind == RetentionFileCaptureMemberKind.Directory).OrderByDescending(static e => e.Relative.Count(c => c == '/')).ThenBy(static e => e.Relative, StringComparer.Ordinal))
            .Concat(entries.Where(static e => e.Kind == RetentionFileCaptureMemberKind.OwnerMarker));
        return ordered.Select((entry, index) => new RetentionFileCaptureMember(index, entry.Relative, entry.Kind, entry.Bytes, entry.Sha, index)).ToArray();
    }

    private static IReadOnlyList<RetentionFileCaptureMember> LoadSdkMembers(SqliteConnection c, SqliteTransaction t, string capture)
    {
        using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT ordinal,relative_path,member_kind,byte_length,sha256,deletion_order FROM retention_analysis_sdk_directory_members WHERE capture_id=$capture ORDER BY deletion_order;"; q.Parameters.AddWithValue("$capture", capture); using var r = q.ExecuteReader(); var result = new List<RetentionFileCaptureMember>();
        while (r.Read()) result.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2) switch { "file" => RetentionFileCaptureMemberKind.File, "directory" => RetentionFileCaptureMemberKind.Directory, "owner_marker" => RetentionFileCaptureMemberKind.OwnerMarker, _ => throw new SdkIdentityException() }, r.IsDBNull(3) ? null : r.GetInt64(3), r.IsDBNull(4) ? null : r.GetFieldValue<byte[]>(4), r.GetInt32(5)));
        if (result.Count is 0 or > 256 || !result.All(static member => member.IsValid) || !result.Select(static member => member.DeletionOrder).SequenceEqual(Enumerable.Range(0, result.Count)) || result[^1].Kind != RetentionFileCaptureMemberKind.OwnerMarker) throw new SdkIdentityException();
        return result;
    }

    private static (byte[] Bytes, long Length, byte[] Sha) ReadBounded(string path, long remaining)
    {
        if (remaining < 0 || EntryKind(path) != SdkEntryKind.File) throw new SdkIdentityException();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        if (stream.Length > remaining) throw new SdkIdentityException();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); using var memory = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = stream.Read(buffer, 0, buffer.Length)) != 0) { memory.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); if (memory.Length > remaining) throw new SdkIdentityException(); }
        if (stream.Length != memory.Length || EntryKind(path) != SdkEntryKind.File) throw new SdkIdentityException();
        return (memory.ToArray(), memory.Length, hash.GetHashAndReset());
    }

    private static string Wire(RetentionFileCaptureMemberKind kind) => kind switch { RetentionFileCaptureMemberKind.File => "file", RetentionFileCaptureMemberKind.Directory => "directory", _ => "owner_marker" };
    private static SdkEntryKind EntryKind(string path) { try { var attributes = File.GetAttributes(path); return (attributes & FileAttributes.ReparsePoint) != 0 ? SdkEntryKind.Reparse : (attributes & FileAttributes.Directory) != 0 ? SdkEntryKind.Directory : SdkEntryKind.File; } catch (FileNotFoundException) { return SdkEntryKind.Absent; } catch (DirectoryNotFoundException) { return SdkEntryKind.Absent; } }
    private sealed class SdkMissingException : Exception;
    private sealed class SdkIdentityException : Exception;
    private enum SdkEntryKind { Absent, File, Directory, Reparse }
    private enum ReservedSdkChildCleanupDisposition { Preserved, Deleted, Conflict }
    private static bool TryCanonicalParent(string value, out string parent) { parent = string.Empty; if (string.IsNullOrWhiteSpace(value)) return false; try { parent = Path.GetFullPath(value); return parent == value; } catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; } }
    private static void SdkExec(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object Value)[] values) { using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = sql; foreach (var (name, value) in values) q.Parameters.AddWithValue(name, value); q.ExecuteNonQuery(); }
    private static AnalysisRunAuthority? LoadAnalysisRunAuthority(SqliteConnection c, SqliteTransaction t, long runId) { if (!TableExists(c, t, "monitor_analysis_runs")) return null; using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT requested_at,retention_owner_token FROM monitor_analysis_runs WHERE id=$id;"; q.Parameters.AddWithValue("$id", runId); using var r = q.ExecuteReader(); if (!r.Read() || r.IsDBNull(0) || r.IsDBNull(1) || r.GetFieldValue<byte[]>(1).Length != 32 || !DateTimeOffset.TryParseExact(r.GetString(0), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var requested)) return null; return new(r.GetString(0), requested.UtcDateTime.Ticks, r.GetFieldValue<byte[]>(1)); }
    private static SdkRow? LoadSdkReservation(SqliteConnection c, SqliteTransaction t, long runId) { using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT capture_id,store_instance_id,requested_at,requested_at_utc_ticks,parent_locator,child_locator,analysis_owner_token_sha256,owner_token,marker_sha256,phase,revision FROM retention_analysis_sdk_directory_reservations WHERE analysis_run_id=$run;"; q.Parameters.AddWithValue("$run", runId); using var r = q.ExecuteReader(); if (!r.Read()) return null; var capture = r.GetString(0); var store = r.GetString(1); var requestedText = r.GetString(2); var ticks = r.GetInt64(3); var parent = r.GetString(4); var child = r.GetString(5); var runToken = r.GetFieldValue<byte[]>(6); var token = r.GetFieldValue<byte[]>(7); var marker = r.GetFieldValue<byte[]>(8); if (!IsCanonicalId(capture) || !IsCanonicalId(store) || !TryCanonicalParent(parent, out var canonicalParent) || child != Path.Combine(canonicalParent, capture) || runToken.Length != 32 || token.Length != 32 || marker.Length != 32 || r.GetInt64(10) <= 0 || !DateTimeOffset.TryParseExact(requestedText, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var requested) || requested.UtcDateTime.Ticks != ticks || !RetentionOwnershipReceipt.Matches(marker, SHA256.HashData(RetentionAnalysisSdkDirectoryOwnershipMarker.Create(store, capture, runId, requestedText, ticks, token)))) throw new RetentionCatalogUnavailableException(); return new(capture, runId, store, requestedText, ticks, parent, child, runToken, token, marker, r.GetString(9) switch { "reserved" => RetentionAnalysisSdkDirectoryPhase.Reserved, "active" => RetentionAnalysisSdkDirectoryPhase.Active, "sealed" => RetentionAnalysisSdkDirectoryPhase.Sealed, _ => throw new RetentionCatalogUnavailableException() }, r.GetInt64(10), requested); }
    private static bool IsCanonicalId(string value) => value is { Length: 32 } && value.All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed record AnalysisRunAuthority(string RequestedAtText, long RequestedAtTicks, byte[] OwnerToken);
    private sealed record SdkRow(string CaptureId, long RunId, string StoreInstanceId, string RequestedAtText, long RequestedAtTicks, string ParentLocator, string ChildLocator, byte[] AnalysisOwnerTokenSha256, byte[] OwnerToken, byte[] MarkerSha256, RetentionAnalysisSdkDirectoryPhase Phase, long Revision, DateTimeOffset RequestedAt)
    { internal bool Matches(RetentionAnalysisSdkDirectoryReservation reservation) => CaptureId == reservation.CaptureId && RunId == reservation.AnalysisRunId && StoreInstanceId == reservation.StoreInstanceId && RequestedAtText == reservation.RequestedAtText && RequestedAtTicks == reservation.RequestedAtUtcTicks && ParentLocator == reservation.ParentLocator && ChildLocator == reservation.ChildLocator && Phase == reservation.Phase && Revision == reservation.Revision && RetentionOwnershipReceipt.Matches(OwnerToken, reservation.OwnerToken) && RetentionOwnershipReceipt.Matches(MarkerSha256, reservation.MarkerSha256); internal RetentionAnalysisSdkDirectoryReservation ToReservation() { var marker = RetentionAnalysisSdkDirectoryOwnershipMarker.Create(StoreInstanceId, CaptureId, RunId, RequestedAtText, RequestedAtTicks, OwnerToken); return new(CaptureId, RunId, StoreInstanceId, ParentLocator, ChildLocator, OwnerToken, marker, MarkerSha256, RequestedAtText, RequestedAtTicks, Phase, Revision); } internal RetentionAnalysisSdkDirectoryRecoverySnapshot ToRecovery() => new(CaptureId, RunId, StoreInstanceId, Phase, Revision, OwnerToken, MarkerSha256); }
}

internal enum RetentionAnalysisSdkDirectoryPhase { Reserved, Active, Sealed }
internal sealed class RetentionAnalysisSdkDirectoryReservation
{
    internal RetentionAnalysisSdkDirectoryReservation(string captureId, long runId, string store, string parent, string child, byte[] token, byte[] marker, byte[] markerDigest, string requested, long ticks, RetentionAnalysisSdkDirectoryPhase phase, long revision) => (CaptureId, AnalysisRunId, StoreInstanceId, ParentLocator, ChildLocator, OwnerToken, OwnershipMarker, MarkerSha256, RequestedAtText, RequestedAtUtcTicks, Phase, Revision) = (captureId, runId, store, parent, child, token.ToArray(), marker.ToArray(), markerDigest.ToArray(), requested, ticks, phase, revision);
    internal string CaptureId { get; }
    internal long AnalysisRunId { get; }
    internal string StoreInstanceId { get; }
    internal string ParentLocator { get; }
    internal string ChildLocator { get; }
    internal byte[] OwnerToken { get; }
    internal byte[] OwnershipMarker { get; }
    internal byte[] MarkerSha256 { get; }
    internal string RequestedAtText { get; }
    internal long RequestedAtUtcTicks { get; }
    internal RetentionAnalysisSdkDirectoryPhase Phase { get; }
    internal long Revision { get; }
    public override string ToString() => nameof(RetentionAnalysisSdkDirectoryReservation);
}
internal sealed class RetentionAnalysisSdkDirectoryOperationLease
{
    internal RetentionAnalysisSdkDirectoryOperationLease(RetentionReadGrant grant, string captureId, RetentionAnalysisSdkDirectoryReservation capability)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentNullException.ThrowIfNull(capability);
        if (grant.LeaseKind != RetentionLeaseKind.Operation
            || grant.OwnershipKey.StoreKind != RetentionStoreKind.AnalysisSdkDirectory
            || !string.Equals(grant.OwnershipKey.SourceItemId, captureId, StringComparison.Ordinal)
            || !string.Equals(capability.CaptureId, captureId, StringComparison.Ordinal))
            throw new ArgumentException("The SDK directory lease capability does not match the grant.", nameof(grant));
        Grant = grant;
        CaptureId = captureId;
        Capability = capability;
    }
    internal RetentionReadGrant Grant { get; }
    internal string ItemId => Grant.ItemId;
    internal long Revision => Grant.AdmissionRevision;
    internal string Owner => Grant.LeaseOwner;
    internal long Generation => Grant.LeaseGeneration;
    internal string CaptureId { get; }
    internal RetentionAnalysisSdkDirectoryReservation Capability { get; }
    public override string ToString() => nameof(RetentionAnalysisSdkDirectoryOperationLease);
}
internal sealed record RetentionAnalysisSdkDirectoryLeaseObservation(
    RetentionOperationRenewalDisposition Disposition,
    DateTimeOffset ObservedAt,
    DateTimeOffset PublishedLeaseExpiresAt);
internal sealed class RetentionAnalysisSdkDirectoryActivationResult
{
    private RetentionAnalysisSdkDirectoryActivationResult(RetentionAnalysisSdkDirectoryOperationLease? lease) => Lease = lease;
    internal RetentionAnalysisSdkDirectoryOperationLease? Lease { get; }
    internal bool IsActive => Lease is not null; internal static RetentionAnalysisSdkDirectoryActivationResult Closed { get; } = new(null); internal static RetentionAnalysisSdkDirectoryActivationResult Active(RetentionAnalysisSdkDirectoryOperationLease lease) => new(lease); public override string ToString() => nameof(RetentionAnalysisSdkDirectoryActivationResult);
}
internal sealed record RetentionAnalysisSdkDirectoryRecoverySnapshot(string CaptureId, long AnalysisRunId, string StoreInstanceId, RetentionAnalysisSdkDirectoryPhase Phase, long Revision, byte[] OwnerToken, byte[] MarkerSha256) { public override string ToString() => nameof(RetentionAnalysisSdkDirectoryRecoverySnapshot); }

internal enum RetentionAnalysisSdkDirectoryDeletionPlanDisposition { Ready, Missing, InvalidIdentity, OwnershipMismatch, LeaseLost, Busy }
internal sealed class RetentionAnalysisSdkDirectoryDeletionPlanResult
{
    internal RetentionAnalysisSdkDirectoryDeletionPlanResult(RetentionAnalysisSdkDirectoryDeletionPlanDisposition disposition, RetentionAnalysisSdkDirectoryDeletionPlan? plan) => (Disposition, Plan) = (disposition, plan);
    internal RetentionAnalysisSdkDirectoryDeletionPlanDisposition Disposition { get; }
    internal RetentionAnalysisSdkDirectoryDeletionPlan? Plan { get; }
    public override string ToString() => nameof(RetentionAnalysisSdkDirectoryDeletionPlanResult);
}
internal sealed class RetentionAnalysisSdkDirectoryDeletionPlan
{
    private readonly byte[] markerBytes; private readonly byte[] markerSha256; private readonly IReadOnlyList<RetentionFileCaptureMember> members;
    internal RetentionAnalysisSdkDirectoryDeletionPlan(string child, byte[] marker, byte[] markerDigest, IReadOnlyList<RetentionFileCaptureMember> values, int cursor) { Child = child; markerBytes = marker.ToArray(); markerSha256 = markerDigest.ToArray(); members = values.Select(static member => member with { Sha256 = member.Sha256?.ToArray() }).ToArray(); Cursor = cursor; }
    internal string Child { get; }
    internal byte[] MarkerBytes => markerBytes.ToArray(); internal byte[] MarkerSha256 => markerSha256.ToArray(); internal IReadOnlyList<RetentionFileCaptureMember> Members => members.Select(static member => member with { Sha256 = member.Sha256?.ToArray() }).ToArray(); internal int Cursor { get; }
    public override string ToString() => nameof(RetentionAnalysisSdkDirectoryDeletionPlan);
}

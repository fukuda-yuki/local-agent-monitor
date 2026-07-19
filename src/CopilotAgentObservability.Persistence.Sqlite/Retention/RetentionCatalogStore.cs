using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed partial class RetentionCatalogStore
{
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly Action<SqliteConnection, SqliteTransaction>? backfillValidationCheckpoint;
    private readonly Action? maintenanceCheckpoint;
    private readonly Func<SqliteConnection, CancellationToken, bool>? maintenanceProtocol;
    private readonly RetentionCatalogContext? context;
    private readonly Action<string>? fileCaptureCheckpoint;
    private readonly Action<string>? analysisSdkDirectoryCheckpoint;
    public RetentionCatalogStore(string databasePath, TimeProvider? timeProvider = null) { this.databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath)); this.timeProvider = timeProvider ?? TimeProvider.System; }
    internal RetentionCatalogStore(string databasePath, Action<SqliteConnection, SqliteTransaction> backfillValidationCheckpoint)
        : this(databasePath)
    {
        this.backfillValidationCheckpoint = backfillValidationCheckpoint;
    }
    internal RetentionCatalogStore(string databasePath, TimeProvider timeProvider, Action maintenanceCheckpoint)
        : this(databasePath, timeProvider)
    {
        this.maintenanceCheckpoint = maintenanceCheckpoint;
    }
    internal RetentionCatalogStore(string databasePath, TimeProvider timeProvider, Action? maintenanceCheckpoint, Func<SqliteConnection, CancellationToken, bool> maintenanceProtocol)
        : this(databasePath, timeProvider)
    {
        this.maintenanceCheckpoint = maintenanceCheckpoint;
        this.maintenanceProtocol = maintenanceProtocol;
    }
    public RetentionCatalogStore(RetentionCatalogContext context, TimeProvider? timeProvider = null)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        databasePath = context.DatabasePath;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }
    internal RetentionCatalogStore(RetentionCatalogContext context, TimeProvider timeProvider, Action<string> fileCaptureCheckpoint)
        : this(context, timeProvider) => this.fileCaptureCheckpoint = fileCaptureCheckpoint;
    internal RetentionCatalogStore(RetentionCatalogContext context, TimeProvider timeProvider, Action<string> fileCaptureCheckpoint, Action<string> analysisSdkDirectoryCheckpoint)
        : this(context, timeProvider) { this.fileCaptureCheckpoint = fileCaptureCheckpoint; this.analysisSdkDirectoryCheckpoint = analysisSdkDirectoryCheckpoint; }

    public void CreateSchema()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var connection = Open(enforceForeignKeys: false);
        try
        {
            if (HasSessionSchema(connection, null))
                SqliteSessionStore.ValidateSchemaBeforeInitialization(connection);
            using var transaction = connection.BeginTransaction();
            InitializeForWrite(connection, transaction);
            transaction.Commit();
        }
        catch (RetentionMigrationBlockedException) { throw; }
        catch (InvalidOperationException) { throw new RetentionMigrationBlockedException(); }
        catch (SqliteException) { throw new RetentionMigrationBlockedException(); }
    }

    internal void InitializeForWrite(SqliteConnection connection, SqliteTransaction transaction)
    {
        MonitorSchemaMigrator.ApplyBaseSchema(connection, transaction);
        if (HasSessionSchema(connection, transaction))
            SqliteSessionStore.InitializeSchema(connection, transaction);
        RetentionSchemaMigrator.Apply(connection, transaction);
        EnsureSourceTokens(connection, transaction);
        Backfill(connection, transaction, timeProvider.GetUtcNow());
        backfillValidationCheckpoint?.Invoke(connection, transaction);
        ValidateBackfill(connection, transaction);
    }

    internal void RegisterRawRecord(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rawRecordId,
        DateTimeOffset receivedAt,
        int schemaVersion,
        byte[] ownerToken)
    {
        try
        {
            var receivedAtText = Timestamp(receivedAt);
            var storeInstanceId = StoreId(connection, transaction);
            var receipt = RetentionOwnershipReceipt.CreateRawRecord(new(
                storeInstanceId, rawRecordId, receivedAtText,
                receivedAt.UtcDateTime.Ticks, schemaVersion, ownerToken));
            Add(connection, transaction, storeInstanceId, RetentionStoreKind.RawRecord,
                rawRecordId.ToString(CultureInfo.InvariantCulture), receivedAtText, null, receipt,
                timeProvider.GetUtcNow());
        }
        catch (RetentionMigrationBlockedException) { throw; }
        catch (ArgumentException) { throw new RetentionMigrationBlockedException(); }
        catch (SqliteException) { throw new RetentionMigrationBlockedException(); }
    }

    internal void RegisterAnalysisRunRaw(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        DateTimeOffset requestedAt,
        long? rawRecordId,
        string? spanId,
        byte[] ownerToken)
    {
        try
        {
            var requestedAtText = Timestamp(requestedAt);
            var storeInstanceId = StoreId(connection, transaction);
            var receipt = RetentionOwnershipReceipt.CreateAnalysisRun(new(
                storeInstanceId, runId, requestedAtText, requestedAt.UtcDateTime.Ticks,
                rawRecordId, spanId, ownerToken));
            Add(connection, transaction, storeInstanceId, RetentionStoreKind.AnalysisRunRaw,
                runId.ToString(CultureInfo.InvariantCulture), requestedAtText, null, receipt,
                timeProvider.GetUtcNow());
            AssertAnalysisRunRawWritable(connection, transaction, runId, requestedAt, rawRecordId, spanId, ownerToken);
        }
        catch (RetentionMigrationBlockedException) { throw; }
        catch (ArgumentException) { throw new RetentionMigrationBlockedException(); }
        catch (SqliteException) { throw new RetentionMigrationBlockedException(); }
    }

    internal void AssertAnalysisRunRawWritable(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long runId,
        DateTimeOffset requestedAt,
        long? rawRecordId,
        string? spanId,
        byte[] ownerToken)
    {
        try
        {
            var requestedAtText = Timestamp(requestedAt);
            var storeInstanceId = StoreId(connection, transaction);
            var receipt = RetentionOwnershipReceipt.CreateAnalysisRun(new(
                storeInstanceId, runId, requestedAtText, requestedAt.UtcDateTime.Ticks,
                rawRecordId, spanId, ownerToken));
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                SELECT ownership_receipt, state, revision, read_denied_at, expires_at
                FROM retention_items
                WHERE store_instance_id=$store AND store_kind='analysis_run_raw' AND source_item_id=$source;
                """;
            command.Parameters.AddWithValue("$store", storeInstanceId);
            command.Parameters.AddWithValue("$source", runId.ToString(CultureInfo.InvariantCulture));
            using var reader = command.ExecuteReader();
            if (!reader.Read()
                || !RetentionOwnershipReceipt.Matches(receipt, reader.GetFieldValue<byte[]>(0))
                || !reader.IsDBNull(3)
                || reader.GetString(1) is not "expiring" and not "retained_by_policy"
                || DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) <= timeProvider.GetUtcNow()
                || reader.GetInt64(2) <= 0)
                throw new RetentionMigrationBlockedException();
        }
        catch (RetentionMigrationBlockedException) { throw; }
        catch (ArgumentException) { throw new RetentionMigrationBlockedException(); }
        catch (SqliteException) { throw new RetentionMigrationBlockedException(); }
    }

    internal void RegisterSessionEventContent(SqliteConnection connection, SqliteTransaction transaction,
        string eventId, string contentKind, DateTimeOffset capturedAt, DateTimeOffset expiresAt,
        string sessionId, string? runId, string sourceAdapter, string sourceEventId, byte[] ownerToken)
    {
        try
        {
            var capturedAtText = Timestamp(capturedAt);
            var expiresAtText = Timestamp(expiresAt);
            var storeInstanceId = StoreId(connection, transaction);
            var receipt = RetentionOwnershipReceipt.CreateSession(new(storeInstanceId, eventId, contentKind,
                capturedAtText, capturedAt.UtcDateTime.Ticks, expiresAtText, expiresAt.UtcDateTime.Ticks,
                sessionId, runId, sourceAdapter, sourceEventId, ownerToken));
            Add(connection, transaction, storeInstanceId, RetentionStoreKind.SessionEventContent, eventId,
                capturedAtText, expiresAtText, receipt, timeProvider.GetUtcNow());
            using var readable = connection.CreateCommand();
            readable.Transaction = transaction;
            readable.CommandText = "SELECT state,read_denied_at,expires_at FROM retention_items WHERE store_instance_id=$store AND store_kind='session_event_content' AND source_item_id=$event;";
            readable.Parameters.AddWithValue("$store", storeInstanceId);
            readable.Parameters.AddWithValue("$event", eventId);
            using var reader = readable.ExecuteReader();
            if (!reader.Read() || !reader.IsDBNull(1)
                || reader.GetString(0) is not "expiring" and not "retained_by_policy"
                || DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) <= timeProvider.GetUtcNow())
                throw new RetentionMigrationBlockedException();
        }
        catch (RetentionMigrationBlockedException) { throw; }
        catch (ArgumentException) { throw new RetentionMigrationBlockedException(); }
        catch (SqliteException) { throw new RetentionMigrationBlockedException(); }
    }

    public string StoreInstanceId { get { using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT store_instance_id FROM retention_store_instances WHERE id=1;"; return (string)command.ExecuteScalar()!; } }

    public RetentionCatalogItem? Find(RetentionOwnershipKey key)
    {
        using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = "SELECT item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind AND source_item_id=$source;";
        command.Parameters.AddWithValue("$store", key.StoreInstanceId); command.Parameters.AddWithValue("$kind", RetentionSchemaMigrator.Wire(key.StoreKind)); command.Parameters.AddWithValue("$source", key.SourceItemId);
        using var reader = command.ExecuteReader(); return reader.Read() ? Item(reader, key) : null;
    }

    public ValueTask<RetentionReadLeaseHandle?> TryAcquireAsync(RetentionOwnershipKey key, long expectedRevision, RetentionLeaseKind leaseKind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var connection = Open(); using var transaction = connection.BeginTransaction();
        var item = FindForUpdate(connection, transaction, key);
        if (item is null || item.Revision != expectedRevision) { transaction.Commit(); return ValueTask.FromResult<RetentionReadLeaseHandle?>(null); }
        var sourceProof = SourceProof(connection, transaction, key);
        var deletionLease = leaseKind == RetentionLeaseKind.Deletion;
        var eligibleForDeletion = item.ReadDeniedAt is not null && item.State is RetentionItemLifecycle.DeletionQueued or RetentionItemLifecycle.Deleting;
        var deletionRecovery = deletionLease && item.State == RetentionItemLifecycle.Deleting && HasMatchingDeleteIntent(connection, transaction, item);
        if ((deletionLease && !eligibleForDeletion) || (!deletionLease && (item.ReadDeniedAt is not null || now >= item.ExpiresAt || item.State is not RetentionItemLifecycle.Expiring and not RetentionItemLifecycle.RetainedByPolicy)) || (sourceProof != SourceReceiptProof.Match && !deletionRecovery))
        {
            if (!deletionLease && item.ReadDeniedAt is null && item.State == RetentionItemLifecycle.Expiring && now >= item.ExpiresAt) DenyAndQueue(connection, transaction, item.ItemId, now);
            else if (item.ReadDeniedAt is null && sourceProof == SourceReceiptProof.Missing) DenyMissingSource(connection, transaction, item.ItemId, now);
            else if (item.ReadDeniedAt is null && sourceProof != SourceReceiptProof.Match) DenyInvalidSource(connection, transaction, item.ItemId, now, sourceProof);
            transaction.Commit(); return ValueTask.FromResult<RetentionReadLeaseHandle?>(null);
        }
        var owner = Guid.NewGuid().ToString("N");
        var generation = AcquireLease(connection, transaction, item.ItemId, leaseKind, owner, now);
        if (generation is null) { transaction.Commit(); return ValueTask.FromResult<RetentionReadLeaseHandle?>(null); }
        transaction.Commit();
        return ValueTask.FromResult<RetentionReadLeaseHandle?>(new RetentionReadLeaseHandle(item.ItemId, item.Revision, generation.Value, () => Release(item.ItemId, leaseKind, owner, generation.Value)));
    }

    internal ValueTask<RetentionPreparedBatch> PrepareCleanupBatchAsync(DateTimeOffset now, int promotionLimit, int claimLimit, TimeSpan elapsedBudget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var c = Open(); using var t = c.BeginTransaction(deferred: false);
        if (!CoverageMatches(c, t))
        {
            Exec(c, t, "UPDATE retention_worker_state SET worker_error_code='retention_adapter_coverage_mismatch' WHERE id=1 AND worker_error_code IS NOT 'retention_adapter_coverage_mismatch';");
            t.Commit();
            return ValueTask.FromResult(new RetentionPreparedBatch([], false, false, null, CoverageBlocked: true));
        }
        var started = timeProvider.GetTimestamp();
        var nowText = Timestamp(now);
        Exec(c,t,"DELETE FROM retention_leases WHERE lease_kind IN ('access','operation') AND expires_at <= $now;", ("$now",(object?)nowText));
        var mutations = 0;
        var hitElapsedBudget = false;
        using (var candidates = c.CreateCommand())
        {
            candidates.Transaction = t;
            candidates.CommandText = "SELECT item_id,revision,state FROM retention_items WHERE (state='deleting' AND NOT EXISTS(SELECT 1 FROM retention_leases l WHERE l.item_id=retention_items.item_id AND l.lease_kind='deletion' AND l.expires_at>$now) AND NOT EXISTS(SELECT 1 FROM retention_delete_journal j WHERE j.item_id=retention_items.item_id AND j.expected_revision=retention_items.revision)) OR ((state='expired_pending_deletion' OR (state='deletion_failed' AND retry_exhausted=0 AND next_retry_at<=$now AND attempt_count BETWEEN 1 AND 4)) AND read_denied_at IS NOT NULL) ORDER BY expires_at,item_id LIMIT $limit;";
            candidates.Parameters.AddWithValue("$now", nowText); candidates.Parameters.AddWithValue("$limit", promotionLimit);
            using var reader = candidates.ExecuteReader();
            var rows = new List<(string Id,long Revision,string State)>();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
            foreach (var row in rows)
            {
                if (timeProvider.GetElapsedTime(started) >= elapsedBudget) { hitElapsedBudget = true; break; }
                if (row.State == "deleting")
                    Exec(c,t,"UPDATE retention_items SET state='deletion_queued',revision=revision+1 WHERE item_id=$id AND revision=$revision AND state='deleting' AND NOT EXISTS(SELECT 1 FROM retention_leases l WHERE l.item_id=$id AND l.lease_kind='deletion' AND l.expires_at>$now) AND NOT EXISTS(SELECT 1 FROM retention_delete_journal j WHERE j.item_id=$id AND j.expected_revision=$revision);", ("$id",(object?)row.Id),("$revision",row.Revision),("$now",(object?)nowText));
                else
                    Exec(c,t,"UPDATE retention_items SET state='deletion_queued',revision=revision+1,next_retry_at=NULL,error_code=NULL WHERE item_id=$id AND revision=$revision AND read_denied_at IS NOT NULL AND (state='expired_pending_deletion' OR (state='deletion_failed' AND retry_exhausted=0 AND next_retry_at<=$now AND attempt_count BETWEEN 1 AND 4));", ("$id",(object?)row.Id),("$revision",row.Revision),("$now",(object?)nowText));
                mutations++;
            }
        }
        var work = new List<RetentionWorkReference>(); using (var q=c.CreateCommand()) { q.Transaction=t; q.CommandText="SELECT i.item_id,i.revision,i.state FROM retention_items i WHERE i.state='deletion_queued' OR (i.state='deleting' AND EXISTS(SELECT 1 FROM retention_delete_journal j WHERE j.item_id=i.item_id AND j.expected_revision=i.revision) AND NOT EXISTS(SELECT 1 FROM retention_leases l WHERE l.item_id=i.item_id AND l.lease_kind='deletion' AND l.expires_at>$now)) ORDER BY i.expires_at,i.item_id LIMIT $limit;"; q.Parameters.AddWithValue("$now",nowText);q.Parameters.AddWithValue("$limit",claimLimit);using var r=q.ExecuteReader();while(r.Read())work.Add(new(r.GetString(0),r.GetInt64(1),r.GetString(2)=="deleting"?RetentionWorkKind.IntentRecovery:RetentionWorkKind.Queued)); }
        var nextEligibleAt = NextEligibleAt(c, t, nowText);
        t.Commit(); return ValueTask.FromResult(new RetentionPreparedBatch(work, mutations >= promotionLimit, hitElapsedBudget, nextEligibleAt));
    }

    internal ValueTask<DateTimeOffset?> GetNextCleanupEligibilityAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); using var c=Open(); using var t=c.BeginTransaction();
        var next=NextEligibleAt(c,t,Timestamp(now)); t.Commit(); return ValueTask.FromResult(next);
    }

    internal ValueTask<RetentionClaimResult> TryClaimDeletionAsync(RetentionWorkReference work, string leaseOwner, DateTimeOffset now, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
        using var c=Open();using var t=c.BeginTransaction(deferred:false); var nowText=Timestamp(now);
        using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT item_id,store_instance_id,store_kind,source_item_id,ownership_receipt,private_locator,state,revision,adapter_coverage_version FROM retention_items WHERE item_id=$id;";q.Parameters.AddWithValue("$id",work.ItemId);using var r=q.ExecuteReader(); if(!r.Read() || r.GetInt64(7)!=work.ExpectedRevision){t.Commit();return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.StaleNoOp,null,null,null));}
        var state=r.GetString(6); var revision=r.GetInt64(7); var coverage=r.GetInt32(8); var store=r.GetString(1);var kind=TryParseKind(r.GetString(2));var source=r.GetString(3);var receipt=Convert.ToBase64String(r.GetFieldValue<byte[]>(4));var locator=r.IsDBNull(5)?null:new RetentionPrivateLocatorHandle(r.GetString(5));r.Close();
        if (kind is null || coverage != RetentionV1Constants.AdapterCoverageVersion || !CoverageMatches(c,t)) { Exec(c,t,"UPDATE retention_worker_state SET worker_error_code='retention_adapter_coverage_mismatch' WHERE id=1 AND worker_error_code IS NOT 'retention_adapter_coverage_mismatch';"); t.Commit(); return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.CoverageBlocked,null,null,null)); }
        cancellationToken.ThrowIfCancellationRequested();
        Exec(c,t,"DELETE FROM retention_leases WHERE item_id=$id AND lease_kind IN ('access','operation') AND expires_at <= $now;",("$id",work.ItemId),("$now",nowText));
        if (state=="deletion_queued") { using var blocker=c.CreateCommand();blocker.Transaction=t;blocker.CommandText="SELECT MIN(expires_at) FROM retention_leases WHERE item_id=$id AND lease_kind IN ('access','operation') AND expires_at>$now;";blocker.Parameters.AddWithValue("$id",work.ItemId);blocker.Parameters.AddWithValue("$now",nowText);var due=blocker.ExecuteScalar();if(due is string expiry){t.Commit();return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.Quiescing,null,DateTimeOffset.Parse(expiry,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),null));} var generation=AcquireLease(c,t,work.ItemId,RetentionLeaseKind.Deletion,leaseOwner,now);if(generation is null){t.Commit();return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.Contended,null,null,null));} Exec(c,t,"UPDATE retention_items SET state='deleting',revision=revision+1,deletion_started_at=COALESCE(deletion_started_at,$now) WHERE item_id=$id AND revision=$revision;",("$id",(object?)work.ItemId),("$revision",revision),("$now",nowText));revision++; var claim=new RetentionDeletionClaim {Fence=new(work.ItemId,revision,leaseOwner,generation.Value),StoreInstanceId=store,StoreKind=kind.Value,SourceIdentity=new(source,receipt),PrivateLocator=locator,IntentCursor=0,HasCurrentIntent=false,LeaseExpiresAt=now+RetentionV1Constants.LeaseDuration};t.Commit();return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.Claimed,claim,null,null)); }
        if(state!="deleting" || !HasCurrentDeleteJournal(c,t,work.ItemId,revision)){t.Commit();return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.StaleNoOp,null,null,null));} var recovered=AcquireLease(c,t,work.ItemId,RetentionLeaseKind.Deletion,leaseOwner,now);if(recovered is null){t.Commit();return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.Contended,null,null,null));} var cursor=JournalCursor(c,t,work.ItemId);var recovery=new RetentionDeletionClaim {Fence=new(work.ItemId,revision,leaseOwner,recovered.Value),StoreInstanceId=store,StoreKind=kind.Value,SourceIdentity=new(source,receipt),PrivateLocator=locator,IntentCursor=cursor,HasCurrentIntent=true,LeaseExpiresAt=now+RetentionV1Constants.LeaseDuration};t.Commit();return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.Claimed,recovery,null,null));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { return ValueTask.FromResult(new RetentionClaimResult(RetentionClaimDisposition.CatalogBusy,null,null,null)); }
    }

    internal ValueTask<RetentionIntentResult> EnsureDeleteIntentAsync(RetentionDeleteFence fence,int expectedCursor,DateTimeOffset now,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var c=Open();using var t=c.BeginTransaction(deferred:false);
            var fenceDisposition=CheckIntentFence(c,t,fence,now);
            if(fenceDisposition is not null){t.Commit();return ValueTask.FromResult(new RetentionIntentResult(fenceDisposition.Value,0,expectedCursor));}
            var attempt=ScalarInt(c,t,"SELECT attempt_count FROM retention_items WHERE item_id=$id",("$id",fence.ItemId));
            if(HasCurrentDeleteJournal(c,t,fence.ItemId,fence.ExpectedRevision)){var cursor=JournalCursor(c,t,fence.ItemId);t.Commit();return ValueTask.FromResult(new RetentionIntentResult(cursor==expectedCursor?RetentionIntentDisposition.AlreadyCommitted:RetentionIntentDisposition.StaleNoOp,attempt,cursor));}
            var journal=ExistingJournal(c,t,fence.ItemId);
            if(journal is { } existing)
            {
                if(existing.Cursor!=expectedCursor){t.Commit();return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.StaleNoOp,attempt,expectedCursor));}
                if(attempt>=RetentionV1Constants.MaximumDeleteAttempts){t.Commit();return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.AttemptLimitReached,attempt,existing.Cursor));}
                Exec(c,t,"UPDATE retention_delete_journal SET intent_at=$now,expected_revision=$revision WHERE item_id=$id AND durable_cursor=$cursor;",("$id",fence.ItemId),("$cursor",existing.Cursor),("$now",Timestamp(now)),("$revision",fence.ExpectedRevision));
                Exec(c,t,"UPDATE retention_items SET attempt_count=attempt_count+1 WHERE item_id=$id AND revision=$revision;",("$id",fence.ItemId),("$revision",fence.ExpectedRevision));
                t.Commit();return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.Committed,attempt+1,existing.Cursor));
            }
            var proof=ValidateFirstIntentEvidence(c,t,fence.ItemId);
            if(proof is SourceReceiptProof.Missing or SourceReceiptProof.InvalidIdentity or SourceReceiptProof.InvalidOrMismatched)
            {
                var code=proof==SourceReceiptProof.Missing?RetentionErrorCode.UnexpectedSourceMissing:proof==SourceReceiptProof.InvalidIdentity?RetentionErrorCode.InvalidIdentity:RetentionErrorCode.OwnershipMismatch;
                Exec(c,t,"UPDATE retention_items SET state='deletion_failed',revision=revision+1,error_code=$code,next_retry_at=NULL,retry_exhausted=0 WHERE item_id=$id AND state='deleting' AND revision=$revision;",("$id",fence.ItemId),("$revision",fence.ExpectedRevision),("$code",Wire(code)));
                Exec(c,t,"DELETE FROM retention_leases WHERE item_id=$id AND lease_kind='deletion' AND owner=$owner AND generation=$generation;",("$id",fence.ItemId),("$owner",fence.LeaseOwner),("$generation",fence.LeaseGeneration));
                t.Commit();return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.TerminalFailureRecorded,attempt,expectedCursor));
            }
            if(proof==SourceReceiptProof.CatalogBusy){t.Rollback();return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.CatalogBusy,0,expectedCursor));}
            if(attempt>=RetentionV1Constants.MaximumDeleteAttempts){t.Commit();return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.AttemptLimitReached,attempt,expectedCursor));}
            Exec(c,t,"INSERT INTO retention_delete_journal(item_id,durable_cursor,intent_at,expected_revision) VALUES($id,$cursor,$now,$revision);",("$id",fence.ItemId),("$cursor",expectedCursor),("$now",Timestamp(now)),("$revision",fence.ExpectedRevision));
            Exec(c,t,"UPDATE retention_items SET attempt_count=attempt_count+1 WHERE item_id=$id AND revision=$revision;",("$id",fence.ItemId),("$revision",fence.ExpectedRevision));
            t.Commit();return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.Committed,attempt+1,expectedCursor));
        }
        catch(SqliteException exception) when(exception.SqliteErrorCode is 5 or 6){return ValueTask.FromResult(new RetentionIntentResult(RetentionIntentDisposition.CatalogBusy,0,expectedCursor));}
    }

    internal ValueTask<RetentionMutationDisposition> TryCancelBeforeIntentAsync(RetentionDeleteFence fence,DateTimeOffset now,CancellationToken cancellationToken) => ValueTask.FromResult(Mutate(cancellationToken, c=> { using var t=c.BeginTransaction();if(!Owns(c,t,fence,now)||HasCurrentDeleteJournal(c,t,fence.ItemId,fence.ExpectedRevision)){t.Commit();return RetentionMutationDisposition.StaleNoOp;}Exec(c,t,"DELETE FROM retention_leases WHERE item_id=$id AND lease_kind='deletion' AND owner=$owner AND generation=$generation;",("$id",fence.ItemId),("$owner",fence.LeaseOwner),("$generation",fence.LeaseGeneration));Exec(c,t,"UPDATE retention_items SET state='deletion_queued',revision=revision+1 WHERE item_id=$id AND revision=$revision;",("$id",fence.ItemId),("$revision",fence.ExpectedRevision));t.Commit();return RetentionMutationDisposition.Applied;}));

    internal ValueTask<RetentionRenewalResult> TryRenewDeletionLeaseAsync(RetentionDeleteFence fence,DateTimeOffset now,DateTimeOffset? notAfter,CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested();using var c=Open();using var t=c.BeginTransaction();if(notAfter is { } cap && cap<=now){t.Commit();return ValueTask.FromResult(RetentionRenewalResult.LeaseLost);}var expiry=notAfter is { } capped && capped<now+RetentionV1Constants.LeaseDuration?capped:now+RetentionV1Constants.LeaseDuration;Exec(c,t,"UPDATE retention_leases SET expires_at=$expiry WHERE item_id=$id AND lease_kind='deletion' AND owner=$owner AND generation=$generation AND expires_at>$now AND EXISTS(SELECT 1 FROM retention_items WHERE item_id=$id AND state='deleting' AND revision=$revision);",("$expiry",Timestamp(expiry)),("$id",fence.ItemId),("$owner",fence.LeaseOwner),("$generation",fence.LeaseGeneration),("$now",Timestamp(now)),("$revision",fence.ExpectedRevision));using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT changes();";var changed=Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture);t.Commit();return ValueTask.FromResult(changed==1?RetentionRenewalResult.Renewed:RetentionRenewalResult.LeaseLost); }
    internal ValueTask<RetentionMutationDisposition> TryAdvanceDeleteCursorAsync(RetentionDeleteFence fence,int expectedCursor,int nextCursor,DateTimeOffset now,CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested();using var c=Open();using var t=c.BeginTransaction();if(!Owns(c,t,fence,now)){t.Commit();return ValueTask.FromResult(RetentionMutationDisposition.StaleNoOp);}Exec(c,t,"UPDATE retention_delete_journal SET durable_cursor=$next WHERE item_id=$id AND expected_revision=$revision AND durable_cursor=$expected;",("$next",nextCursor),("$id",fence.ItemId),("$revision",fence.ExpectedRevision),("$expected",expectedCursor));using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT changes();";var applied=Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture)==1;t.Commit();return ValueTask.FromResult(applied?RetentionMutationDisposition.Applied:RetentionMutationDisposition.StaleNoOp); }

    internal ValueTask RecordCleanCycleAsync(bool qualifyingSqliteBatch, DateTimeOffset now, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested();using var c=Open();using var t=c.BeginTransaction();Exec(c,t,"UPDATE retention_worker_state SET last_successful_run_at=CASE WHEN last_successful_run_at IS NULL OR last_successful_run_at<$now THEN $now ELSE last_successful_run_at END,maintenance_due_at=CASE WHEN $qualifying=1 AND maintenance_due_at IS NULL THEN $now ELSE maintenance_due_at END WHERE id=1;",("$now",Timestamp(now)),("$qualifying",qualifyingSqliteBatch?1:0));t.Commit();return ValueTask.CompletedTask; }
    internal ValueTask<bool> IsMaintenanceDueAsync(DateTimeOffset now, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); using var c=Open(); using var q=c.CreateCommand(); q.CommandText="SELECT EXISTS(SELECT 1 FROM retention_worker_state WHERE id=1 AND maintenance_due_at IS NOT NULL AND maintenance_due_at<=$now);"; q.Parameters.AddWithValue("$now",Timestamp(now)); return ValueTask.FromResult(Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture)!=0); }
    internal async ValueTask<bool> TryRunMaintenanceAsync(DateTimeOffset now,CancellationToken cancellationToken)
    {
        if(cancellationToken.IsCancellationRequested){RecordMaintenanceBusy(now);return false;}
        var gate=context?.Gate;
        if(gate is not null) await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        var owner=Guid.NewGuid().ToString("N");
        long generation=0;
        try
        {
            maintenanceCheckpoint?.Invoke();
            using(var c=OpenMaintenance()) using(var t=c.BeginTransaction())
            {
                var nowText=Timestamp(now);
                Exec(c,t,"DELETE FROM retention_leases WHERE expires_at <= $now;",("$now",nowText));
                using var acquire=c.CreateCommand();acquire.Transaction=t;acquire.CommandText="UPDATE retention_worker_state SET maintenance_owner=$owner,maintenance_lease_expires_at=$expiry,maintenance_generation=maintenance_generation+1 WHERE id=1 AND maintenance_due_at IS NOT NULL AND maintenance_due_at<=$now AND (maintenance_owner IS NULL OR maintenance_lease_expires_at<=$now);";acquire.Parameters.AddWithValue("$owner",owner);acquire.Parameters.AddWithValue("$expiry",Timestamp(now+RetentionV1Constants.LeaseDuration));acquire.Parameters.AddWithValue("$now",nowText);
                if(acquire.ExecuteNonQuery()!=1){t.Commit();return false;}
                using var selected=c.CreateCommand();selected.Transaction=t;selected.CommandText="SELECT maintenance_generation FROM retention_worker_state WHERE id=1 AND maintenance_owner=$owner;";selected.Parameters.AddWithValue("$owner",owner);generation=Convert.ToInt64(selected.ExecuteScalar(),CultureInfo.InvariantCulture);
                using var live=c.CreateCommand();live.Transaction=t;live.CommandText="SELECT EXISTS(SELECT 1 FROM retention_leases WHERE lease_kind IN ('access','operation','deletion') AND expires_at>$now);";live.Parameters.AddWithValue("$now",nowText);
                if(Convert.ToInt64(live.ExecuteScalar(),CultureInfo.InvariantCulture)!=0){t.Commit();CompleteMaintenance(owner,generation,now,false);return false;}
                t.Commit();
            }
            cancellationToken.ThrowIfCancellationRequested();
            var busy=false;
            try
            {
                using var checkpointConnection=OpenMaintenance();
                if (maintenanceProtocol is not null)
                    busy=!maintenanceProtocol(checkpointConnection,cancellationToken);
                else
                {
                    using var checkpoint=checkpointConnection.CreateCommand();checkpoint.CommandText="PRAGMA wal_checkpoint(TRUNCATE);";
                    using var reader=checkpoint.ExecuteReader();
                    busy=!reader.Read() || reader.GetInt64(0)!=0;
                }
            }
            catch(SqliteException){busy=true;}
            CompleteMaintenance(owner,generation,now,!busy);
            return !busy;
        }
        catch(OperationCanceledException){if(generation!=0)CompleteMaintenance(owner,generation,now,false);else RecordMaintenanceBusy(now);return false;}
        catch{if(generation!=0)CompleteMaintenance(owner,generation,now,false);else RecordMaintenanceBusy(now);return false;}
        finally{gate?.Release();}
    }

    internal ValueTask<RetentionMutationDisposition> TryCompleteDeletionAsync(RetentionDeleteFence fence,DateTimeOffset completedAt,CancellationToken cancellationToken) => ValueTask.FromResult(Complete(fence,completedAt,cancellationToken));
    internal RetentionMutationDisposition TryCompleteDeletion(SqliteConnection connection,SqliteTransaction transaction,RetentionDeleteFence fence,DateTimeOffset completedAt) => Complete(connection,transaction,fence,completedAt);
    internal ValueTask<RetentionFailureResult> TryRecordTransientFailureAsync(RetentionDeleteFence fence,RetentionErrorCode code,DateTimeOffset failedAt,CancellationToken cancellationToken) => ValueTask.FromResult(Fail(fence,code,failedAt,false,cancellationToken));
    internal ValueTask<RetentionFailureResult> TryRecordTerminalFailureAsync(RetentionDeleteFence fence,RetentionErrorCode code,DateTimeOffset failedAt,CancellationToken cancellationToken) => ValueTask.FromResult(Fail(fence,code,failedAt,true,cancellationToken));

    internal async ValueTask<RetentionReadResult<T>> ReadAsync<T>(RetentionReadRequest request, Func<SqliteConnection, SqliteTransaction, RetentionReadGrant, CancellationToken, ValueTask<T?>> selector, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(selector);
        var gate = context?.Gate;
        if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            var item = FindForUpdate(connection, transaction, request.OwnershipKey);
            if (item is null) { transaction.Commit(); return new(RetentionReadDisposition.NotFound, null); }
            if (request.ExpectedRevision is not null && item.Revision != request.ExpectedRevision) { transaction.Commit(); return new(RetentionReadDisposition.Denied, null); }
            var proof = SourceProof(connection, transaction, request.OwnershipKey);
            var now = request.Now;
            if (item.ReadDeniedAt is not null || item.State is not RetentionItemLifecycle.Expiring and not RetentionItemLifecycle.RetainedByPolicy || now >= item.ExpiresAt || proof != SourceReceiptProof.Match)
            {
                DenyForReadFailure(connection, transaction, item, now, proof);
                transaction.Commit();
                return new(RetentionReadDisposition.Denied, null);
            }
            var owner = Guid.NewGuid().ToString("N");
            var leaseKind = request.LeaseKind == RetentionReadKind.Access ? RetentionLeaseKind.Access : RetentionLeaseKind.Operation;
            var generation = AcquireLease(connection, transaction, item.ItemId, leaseKind, owner, now, item.ExpiresAt);
            if (generation is null) { transaction.Commit(); return new(RetentionReadDisposition.Busy, null); }
            var leaseExpiry = now.Add(RetentionV1Constants.LeaseDuration) < item.ExpiresAt ? now.Add(RetentionV1Constants.LeaseDuration) : item.ExpiresAt;
            var token = SourceToken(connection, transaction, request.OwnershipKey);
            if (token is null) { ReleaseWithinTransaction(connection, transaction, item.ItemId, owner, generation.Value); DenyInvalidSource(connection, transaction, item.ItemId, now, SourceReceiptProof.InvalidIdentity); transaction.Commit(); return new(RetentionReadDisposition.Denied, null); }
            var grant = new RetentionReadGrant(item.ItemId, item.Revision, owner, generation.Value, leaseExpiry, token);
            var value = await selector(connection, transaction, grant, cancellationToken).ConfigureAwait(false);
            if (value is null || timeProvider.GetUtcNow() >= item.ExpiresAt)
            {
                ReleaseWithinTransaction(connection, transaction, item.ItemId, owner, generation.Value);
                DenyForReadFailure(connection, transaction, item, timeProvider.GetUtcNow(), SourceReceiptProof.InvalidOrMismatched);
                transaction.Commit();
                return new(RetentionReadDisposition.Denied, null);
            }
            transaction.Commit();
            return new(RetentionReadDisposition.Granted, new RetentionReadLease<T>(value, RetentionRevisionFence.Create(), () => ReleaseAsync(item.ItemId, leaseKind, owner, generation.Value)));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(RetentionReadDisposition.Busy, null);
        }
        finally
        {
            gate?.Release();
        }
    }

    internal async ValueTask<RetentionBatchReadResult<T>> ReadBatchAsync<T>(
        IReadOnlyList<RetentionReadRequest> requests,
        Func<SqliteConnection, SqliteTransaction, IReadOnlyList<RetentionReadGrant>, CancellationToken, ValueTask<T?>> selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(selector);
        if (requests.Count == 0)
            return new(RetentionReadDisposition.Granted, new RetentionBatchReadLease<T>(default!, RetentionRevisionFence.Create(), static () => ValueTask.CompletedTask));

        var gate = context?.Gate;
        if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            var items = new List<RetentionCatalogItem>(requests.Count);
            foreach (var request in requests)
            {
                var item = FindForUpdate(connection, transaction, request.OwnershipKey);
                if (item is null) { transaction.Commit(); return new(RetentionReadDisposition.NotFound, null); }
                var now = request.Now;
                var proof = SourceProof(connection, transaction, request.OwnershipKey);
                if ((request.ExpectedRevision is not null && item.Revision != request.ExpectedRevision)
                    || item.ReadDeniedAt is not null
                    || item.State is not RetentionItemLifecycle.Expiring and not RetentionItemLifecycle.RetainedByPolicy
                    || now >= item.ExpiresAt
                    || proof != SourceReceiptProof.Match)
                {
                    DenyForReadFailure(connection, transaction, item, now, proof);
                    transaction.Commit();
                    return new(RetentionReadDisposition.Denied, null);
                }
                items.Add(item);
            }

            var owner = Guid.NewGuid().ToString("N");
            var grants = new List<RetentionReadGrant>(requests.Count);
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                var item = items[index];
                var kind = request.LeaseKind == RetentionReadKind.Access ? RetentionLeaseKind.Access : RetentionLeaseKind.Operation;
                var generation = AcquireLease(connection, transaction, item.ItemId, kind, owner, request.Now, item.ExpiresAt);
                if (generation is null) { transaction.Rollback(); return new(RetentionReadDisposition.Busy, null); }
                var token = SourceToken(connection, transaction, request.OwnershipKey);
                if (token is null) { ReleaseWithinTransaction(connection, transaction, grants); DenyInvalidSource(connection, transaction, item.ItemId, request.Now, SourceReceiptProof.InvalidIdentity); transaction.Commit(); return new(RetentionReadDisposition.Denied, null); }
                var expiry = request.Now.Add(RetentionV1Constants.LeaseDuration) < item.ExpiresAt ? request.Now.Add(RetentionV1Constants.LeaseDuration) : item.ExpiresAt;
                grants.Add(new RetentionReadGrant(item.ItemId, item.Revision, owner, generation.Value, expiry, token));
            }

            var value = await selector(connection, transaction, grants, cancellationToken).ConfigureAwait(false);
            var expired = false;
            for (var index = 0; index < items.Count; index++)
                expired |= timeProvider.GetUtcNow() >= items[index].ExpiresAt;
            if (value is null || expired)
            {
                ReleaseWithinTransaction(connection, transaction, grants);
                foreach (var item in items) DenyForReadFailure(connection, transaction, item, timeProvider.GetUtcNow(), SourceReceiptProof.InvalidOrMismatched);
                transaction.Commit();
                return new(RetentionReadDisposition.Denied, null);
            }
            transaction.Commit();
            return new(RetentionReadDisposition.Granted, new RetentionBatchReadLease<T>(value, RetentionRevisionFence.Create(), async () =>
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var kind = requests[index].LeaseKind == RetentionReadKind.Access ? RetentionLeaseKind.Access : RetentionLeaseKind.Operation;
                    await ReleaseAsync(items[index].ItemId, kind, owner, grants[index].LeaseGeneration).ConfigureAwait(false);
                }
            }));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(RetentionReadDisposition.Busy, null);
        }
        finally { gate?.Release(); }
    }

    internal async ValueTask<RetentionBatchReadResult<T>> ReadSelectedBatchAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<IReadOnlyList<RetentionReadRequest>>> candidateSelector,
        Func<SqliteConnection, SqliteTransaction, IReadOnlyList<RetentionReadGrant>, CancellationToken, ValueTask<T?>> selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidateSelector);
        ArgumentNullException.ThrowIfNull(selector);

        var gate = context?.Gate;
        if (gate is not null) await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var connection = OpenExisting();
            using var transaction = connection.BeginTransaction(deferred: false);
            var requests = await candidateSelector(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (requests.Count == 0)
            {
                var emptyValue = await selector(connection, transaction, Array.Empty<RetentionReadGrant>(), cancellationToken).ConfigureAwait(false);
                if (emptyValue is null)
                {
                    transaction.Rollback();
                    return new(RetentionReadDisposition.Denied, null);
                }
                transaction.Commit();
                return new(RetentionReadDisposition.Granted, new RetentionBatchReadLease<T>(emptyValue, RetentionRevisionFence.Create(), static () => ValueTask.CompletedTask));
            }

            var items = new List<RetentionCatalogItem>(requests.Count);
            foreach (var request in requests)
            {
                var item = FindForUpdate(connection, transaction, request.OwnershipKey);
                if (item is null) { transaction.Commit(); return new(RetentionReadDisposition.NotFound, null); }
                var proof = SourceProof(connection, transaction, request.OwnershipKey);
                if ((request.ExpectedRevision is not null && item.Revision != request.ExpectedRevision)
                    || item.ReadDeniedAt is not null
                    || item.State is not RetentionItemLifecycle.Expiring and not RetentionItemLifecycle.RetainedByPolicy
                    || request.Now >= item.ExpiresAt
                    || proof != SourceReceiptProof.Match)
                {
                    DenyForReadFailure(connection, transaction, item, request.Now, proof);
                    transaction.Commit();
                    return new(RetentionReadDisposition.Denied, null);
                }
                items.Add(item);
            }

            var owner = Guid.NewGuid().ToString("N");
            var grants = new List<RetentionReadGrant>(requests.Count);
            for (var index = 0; index < requests.Count; index++)
            {
                var request = requests[index];
                var item = items[index];
                var kind = request.LeaseKind == RetentionReadKind.Access ? RetentionLeaseKind.Access : RetentionLeaseKind.Operation;
                var generation = AcquireLease(connection, transaction, item.ItemId, kind, owner, request.Now, item.ExpiresAt);
                if (generation is null) { transaction.Rollback(); return new(RetentionReadDisposition.Busy, null); }
                var token = SourceToken(connection, transaction, request.OwnershipKey);
                if (token is null) { ReleaseWithinTransaction(connection, transaction, grants); DenyInvalidSource(connection, transaction, item.ItemId, request.Now, SourceReceiptProof.InvalidIdentity); transaction.Commit(); return new(RetentionReadDisposition.Denied, null); }
                var expiry = request.Now.Add(RetentionV1Constants.LeaseDuration) < item.ExpiresAt ? request.Now.Add(RetentionV1Constants.LeaseDuration) : item.ExpiresAt;
                grants.Add(new RetentionReadGrant(item.ItemId, item.Revision, owner, generation.Value, expiry, token));
            }

            var value = await selector(connection, transaction, grants, cancellationToken).ConfigureAwait(false);
            var expired = items.Any(item => timeProvider.GetUtcNow() >= item.ExpiresAt);
            if (value is null || expired)
            {
                ReleaseWithinTransaction(connection, transaction, grants);
                foreach (var item in items) DenyForReadFailure(connection, transaction, item, timeProvider.GetUtcNow(), SourceReceiptProof.InvalidOrMismatched);
                transaction.Commit();
                return new(RetentionReadDisposition.Denied, null);
            }
            transaction.Commit();
            return new(RetentionReadDisposition.Granted, new RetentionBatchReadLease<T>(value, RetentionRevisionFence.Create(), async () =>
            {
                for (var index = 0; index < items.Count; index++)
                {
                    var kind = requests[index].LeaseKind == RetentionReadKind.Access ? RetentionLeaseKind.Access : RetentionLeaseKind.Operation;
                    await ReleaseAsync(items[index].ItemId, kind, owner, grants[index].LeaseGeneration).ConfigureAwait(false);
                }
            }));
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6)
        {
            return new(RetentionReadDisposition.Busy, null);
        }
        finally { gate?.Release(); }
    }

    private void Backfill(SqliteConnection c, SqliteTransaction t, DateTimeOffset now)
    {
        var store = StoreId(c, t);
        if (TableExists(c, t, "session_event_content") && (!TableExists(c, t, "session_events") || Exists(c, t, "SELECT 1 FROM session_event_content c WHERE NOT EXISTS (SELECT 1 FROM session_events e WHERE e.event_id=c.event_id);")))
        {
            throw new RetentionMigrationBlockedException();
        }
        if (TableExists(c, t, "session_event_content"))
        {
            using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT c.event_id,c.captured_at,c.expires_at,c.retention_owner_token,c.content_kind,e.session_id,e.run_id,e.source_adapter,e.source_event_id FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id;"; using var r=q.ExecuteReader(); while(r.Read()) AddSession(c,t,store,r,now);
        }
        if (TableExists(c,t,"raw_records")) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT id,received_at,schema_version,retention_owner_token FROM raw_records;";using var r=q.ExecuteReader();while(r.Read()) AddRaw(c,t,store,r,now); }
        if (TableExists(c,t,"monitor_analysis_runs")) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT id,requested_at,raw_record_id,span_id,retention_owner_token FROM monitor_analysis_runs WHERE result_markdown IS NOT NULL OR error_message IS NOT NULL OR EXISTS(SELECT 1 FROM monitor_analysis_events e WHERE e.run_id=monitor_analysis_runs.id);";using var r=q.ExecuteReader();while(r.Read()) AddAnalysis(c,t,store,r,now); }
    }

    private static void ValidateBackfill(SqliteConnection connection, SqliteTransaction transaction)
    {
        foreach (var kind in new[] { RetentionStoreKind.SessionEventContent, RetentionStoreKind.RawRecord, RetentionStoreKind.AnalysisRunRaw })
        {
            foreach (var key in CatalogKeys(connection, transaction, kind))
            {
                if (SourceProof(connection, transaction, key) != SourceReceiptProof.Match)
                    throw new RetentionMigrationBlockedException();
            }

            if (CatalogCount(connection, transaction, kind) != SourceCount(connection, transaction, kind))
                throw new RetentionMigrationBlockedException();
        }
    }

    private static IEnumerable<RetentionOwnershipKey> CatalogKeys(SqliteConnection connection, SqliteTransaction transaction, RetentionStoreKind kind)
    {
        var store = StoreId(connection, transaction);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT source_item_id FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind;";
        command.Parameters.AddWithValue("$store", store);
        command.Parameters.AddWithValue("$kind", RetentionSchemaMigrator.Wire(kind));
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new RetentionOwnershipKey(store, kind, reader.GetString(0));
    }

    private static long CatalogCount(SqliteConnection connection, SqliteTransaction transaction, RetentionStoreKind kind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM retention_items WHERE store_kind=$kind;";
        command.Parameters.AddWithValue("$kind", RetentionSchemaMigrator.Wire(kind));
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static long SourceCount(SqliteConnection connection, SqliteTransaction transaction, RetentionStoreKind kind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = kind switch
        {
            RetentionStoreKind.SessionEventContent => TableExists(connection, transaction, "session_event_content")
                ? "SELECT COUNT(*) FROM session_event_content;"
                : "SELECT 0;",
            RetentionStoreKind.RawRecord => "SELECT COUNT(*) FROM raw_records;",
            RetentionStoreKind.AnalysisRunRaw => TableExists(connection, transaction, "monitor_analysis_runs")
                ? "SELECT COUNT(*) FROM monitor_analysis_runs WHERE result_markdown IS NOT NULL OR error_message IS NOT NULL OR EXISTS(SELECT 1 FROM monitor_analysis_events e WHERE e.run_id=monitor_analysis_runs.id);"
                : "SELECT 0;",
            _ => "SELECT 0;"
        };
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void EnsureSourceTokens(SqliteConnection c, SqliteTransaction t)
    {
        MonitorSchemaMigrator.EnsureAnalysisRetentionSchema(c, t);
    }

    private static bool HasSessionSchema(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='sessions') OR EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name='session_event_content');";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void Execute(SqliteConnection c, SqliteTransaction t, string sql) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;q.ExecuteNonQuery(); }
    private static DateTimeOffset Parse(string value) => DateTimeOffset.TryParseExact(value,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var parsed) ? parsed : throw new RetentionMigrationBlockedException();
    private static byte[] Blob(SqliteDataReader reader, int ordinal) => !reader.IsDBNull(ordinal) && reader.GetFieldValue<byte[]>(ordinal) is { Length: 32 } token ? token : throw new RetentionMigrationBlockedException();

    private static void AddSession(SqliteConnection c, SqliteTransaction t, string store, SqliteDataReader r, DateTimeOffset now)
    {
        var captured = r.GetString(1); var expires = r.GetString(2); var capturedAt = Parse(captured); var expiresAt = Parse(expires);
        var receipt = RetentionOwnershipReceipt.CreateSession(new(store, r.GetString(0), r.GetString(4), captured, capturedAt.UtcDateTime.Ticks, expires, expiresAt.UtcDateTime.Ticks, r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.GetString(7), r.GetString(8), Blob(r, 3)));
        Add(c,t,store,RetentionStoreKind.SessionEventContent,r.GetString(0),captured,expires,receipt,now);
    }
    private static void AddRaw(SqliteConnection c, SqliteTransaction t, string store, SqliteDataReader r, DateTimeOffset now)
    {
        var received = r.GetString(1); var at=Parse(received); var receipt=RetentionOwnershipReceipt.CreateRawRecord(new(store,r.GetInt64(0),received,at.UtcDateTime.Ticks,r.GetInt32(2),Blob(r,3)));
        Add(c,t,store,RetentionStoreKind.RawRecord,r.GetInt64(0).ToString(CultureInfo.InvariantCulture),received,null,receipt,now);
    }
    private static void AddAnalysis(SqliteConnection c, SqliteTransaction t, string store, SqliteDataReader r, DateTimeOffset now)
    {
        var requested=r.GetString(1); var at=Parse(requested); var receipt=RetentionOwnershipReceipt.CreateAnalysisRun(new(store,r.GetInt64(0),requested,at.UtcDateTime.Ticks,r.IsDBNull(2)?null:r.GetInt64(2),r.IsDBNull(3)?null:r.GetString(3),Blob(r,4)));
        Add(c,t,store,RetentionStoreKind.AnalysisRunRaw,r.GetInt64(0).ToString(CultureInfo.InvariantCulture),requested,null,receipt,now);
    }

    private static void Add(SqliteConnection c, SqliteTransaction t,string store,RetentionStoreKind kind,string source,string captured,string? existingExpiry,byte[] receipt,DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParseExact(captured,"O",CultureInfo.InvariantCulture,DateTimeStyles.None,out var capturedAt) || string.IsNullOrWhiteSpace(source) || receipt.Length != 32) throw new RetentionMigrationBlockedException();
        DateTimeOffset expiresAt;
        if (existingExpiry is not null) { if(!DateTimeOffset.TryParse(existingExpiry,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out expiresAt)) throw new RetentionMigrationBlockedException(); } else expiresAt=capturedAt+RetentionV1Constants.RawDefaultTtl;
        var denied = expiresAt <= now; using var q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,adapter_coverage_version) VALUES($id,$store,$kind,$source,1,$receipt,$captured,$expires,'raw-default-90d',1,$state,1,$denied,$queued,$coverage) ON CONFLICT(store_instance_id,store_kind,source_item_id) DO UPDATE SET ownership_receipt=retention_items.ownership_receipt WHERE retention_items.receipt_version=1 AND retention_items.ownership_receipt=$receipt;";q.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$store",store);q.Parameters.AddWithValue("$kind",RetentionSchemaMigrator.Wire(kind));q.Parameters.AddWithValue("$source",source);q.Parameters.AddWithValue("$receipt",receipt);q.Parameters.AddWithValue("$captured",captured);q.Parameters.AddWithValue("$expires",existingExpiry ?? Timestamp(expiresAt));q.Parameters.AddWithValue("$state",denied?"expired_pending_deletion":"expiring");q.Parameters.AddWithValue("$denied",denied?Timestamp(now):(object)DBNull.Value);q.Parameters.AddWithValue("$queued",denied?Timestamp(now):(object)DBNull.Value);q.Parameters.AddWithValue("$coverage",RetentionV1Constants.AdapterCoverageVersion);if(q.ExecuteNonQuery()!=1) throw new RetentionMigrationBlockedException();
    }

    private void CompleteMaintenance(string owner,long generation,DateTimeOffset now,bool succeeded)
    {
        using var c=OpenMaintenance();using var t=c.BeginTransaction();
        if(succeeded) Exec(c,t,"UPDATE retention_worker_state SET maintenance_due_at=NULL,maintenance_error_code=NULL,maintenance_owner=NULL,maintenance_lease_expires_at=NULL WHERE id=1 AND maintenance_owner=$owner AND maintenance_generation=$generation;",("$owner",owner),("$generation",generation));
        else Exec(c,t,"UPDATE retention_worker_state SET maintenance_due_at=$retry,maintenance_error_code='retention_maintenance_busy',maintenance_owner=NULL,maintenance_lease_expires_at=NULL WHERE id=1 AND maintenance_owner=$owner AND maintenance_generation=$generation;",("$retry",Timestamp(now+RetentionV1Constants.WalMaintenanceRetryDelay)),("$owner",owner),("$generation",generation));
        t.Commit();
    }
    private void RecordMaintenanceBusy(DateTimeOffset now)
    {
        using var c=OpenMaintenance();using var t=c.BeginTransaction();
        Exec(c,t,"UPDATE retention_worker_state SET maintenance_due_at=$retry,maintenance_error_code='retention_maintenance_busy',maintenance_owner=NULL,maintenance_lease_expires_at=NULL WHERE id=1 AND maintenance_owner IS NULL;",("$retry",Timestamp(now+RetentionV1Constants.WalMaintenanceRetryDelay)));
        t.Commit();
    }
    private RetentionMutationDisposition Mutate(CancellationToken token, Func<SqliteConnection,RetentionMutationDisposition> action) { token.ThrowIfCancellationRequested();using var c=Open();return action(c); }
    private static void Exec(SqliteConnection c, SqliteTransaction t, string sql, params (string Name, object? Value)[] values) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;foreach(var (name,value) in values)q.Parameters.AddWithValue(name,value??DBNull.Value);q.ExecuteNonQuery(); }
    private static int ScalarInt(SqliteConnection c,SqliteTransaction t,string sql,params (string Name,object? Value)[] values){using var q=c.CreateCommand();q.Transaction=t;q.CommandText=sql;foreach(var(name,value)in values)q.Parameters.AddWithValue(name,value??DBNull.Value);return Convert.ToInt32(q.ExecuteScalar(),CultureInfo.InvariantCulture);}
    private static bool HasCurrentDeleteJournal(SqliteConnection c,SqliteTransaction t,string itemId,long revision){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT EXISTS(SELECT 1 FROM retention_delete_journal WHERE item_id=$id AND expected_revision=$revision);";q.Parameters.AddWithValue("$id",itemId);q.Parameters.AddWithValue("$revision",revision);return Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture)!=0;}
    private static RetentionIntentDisposition? CheckIntentFence(SqliteConnection c, SqliteTransaction t, RetentionDeleteFence fence, DateTimeOffset now)
    {
        using var item=c.CreateCommand();item.Transaction=t;item.CommandText="SELECT state,revision FROM retention_items WHERE item_id=$id;";item.Parameters.AddWithValue("$id",fence.ItemId);
        using var reader=item.ExecuteReader();
        if(!reader.Read() || reader.GetString(0)!="deleting" || reader.GetInt64(1)!=fence.ExpectedRevision) return RetentionIntentDisposition.StaleNoOp;
        reader.Close();
        using var lease=c.CreateCommand();lease.Transaction=t;lease.CommandText="SELECT EXISTS(SELECT 1 FROM retention_leases WHERE item_id=$id AND lease_kind='deletion' AND owner=$owner AND generation=$generation AND expires_at>$now);";lease.Parameters.AddWithValue("$id",fence.ItemId);lease.Parameters.AddWithValue("$owner",fence.LeaseOwner);lease.Parameters.AddWithValue("$generation",fence.LeaseGeneration);lease.Parameters.AddWithValue("$now",Timestamp(now));
        return Convert.ToInt64(lease.ExecuteScalar(),CultureInfo.InvariantCulture)==1?null:RetentionIntentDisposition.LeaseLost;
    }
    private static (int Cursor,long Revision)? ExistingJournal(SqliteConnection c, SqliteTransaction t, string itemId)
    {
        using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT durable_cursor,expected_revision FROM retention_delete_journal WHERE item_id=$id;";q.Parameters.AddWithValue("$id",itemId);using var r=q.ExecuteReader();return r.Read()?(r.GetInt32(0),r.GetInt64(1)):null;
    }
    private SourceReceiptProof ValidateFirstIntentEvidence(SqliteConnection c, SqliteTransaction t, string itemId)
    {
        using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT store_instance_id,store_kind,source_item_id,receipt_version,ownership_receipt,adapter_coverage_version FROM retention_items WHERE item_id=$id;";q.Parameters.AddWithValue("$id",itemId);using var r=q.ExecuteReader();
        if(!r.Read()) return SourceReceiptProof.InvalidIdentity;
        var store=r.GetString(0);var kindWire=r.GetString(1);var source=r.GetString(2);var receiptVersion=r.GetInt32(3);var receipt=r.GetFieldValue<byte[]>(4);var coverage=r.GetInt32(5);
        if(receiptVersion!=1 || receipt.Length!=32 || coverage!=RetentionV1Constants.AdapterCoverageVersion || string.IsNullOrWhiteSpace(source)) return SourceReceiptProof.InvalidIdentity;
        var kind=kindWire switch { "session_event_content"=>RetentionStoreKind.SessionEventContent,"raw_record"=>RetentionStoreKind.RawRecord,"analysis_run_raw"=>RetentionStoreKind.AnalysisRunRaw,"sensitive_bundle"=>RetentionStoreKind.SensitiveBundle,"analysis_sdk_directory"=>RetentionStoreKind.AnalysisSdkDirectory,_=>(RetentionStoreKind?)null };
        if(kind is null || !string.Equals(store,StoreId(c,t),StringComparison.Ordinal)) return SourceReceiptProof.InvalidIdentity;
        if(kind is RetentionStoreKind.SensitiveBundle) return ValidateSensitiveBundleFirstIntentEvidence(c,t,itemId,store,source,receipt);
        if(kind is RetentionStoreKind.AnalysisSdkDirectory) return ValidateAnalysisSdkDirectoryFirstIntentEvidence(c,t,itemId,store,source,receipt);
        return StrictSourceProof(c,t,new RetentionOwnershipKey(store,kind.Value,source));
    }
    private static SourceReceiptProof StrictSourceProof(SqliteConnection c, SqliteTransaction t, RetentionOwnershipKey key)
    {
        try { return SourceProofCore(c,t,key); }
        catch(ArgumentException){return SourceReceiptProof.InvalidIdentity;}
        catch(FormatException){return SourceReceiptProof.InvalidIdentity;}
        catch(SqliteException){return SourceReceiptProof.CatalogBusy;}
    }
    private static bool CoverageMatches(SqliteConnection c,SqliteTransaction t){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT COUNT(*) FROM retention_adapter_coverage WHERE coverage_version=$coverage AND store_kind IN ('session_event_content','raw_record','analysis_run_raw','sensitive_bundle','analysis_sdk_directory');";q.Parameters.AddWithValue("$coverage",RetentionV1Constants.AdapterCoverageVersion);return Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture)==5;}
    private static int JournalCursor(SqliteConnection c,SqliteTransaction t,string itemId){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT durable_cursor FROM retention_delete_journal WHERE item_id=$id;";q.Parameters.AddWithValue("$id",itemId);var value=q.ExecuteScalar();return value is null or DBNull ? 0:int.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out var cursor)?cursor:0;}
    private static DateTimeOffset? NextEligibleAt(SqliteConnection c, SqliteTransaction t, string now)
    {
        using var q=c.CreateCommand();q.Transaction=t;
        q.CommandText="SELECT MIN(eligible_at) FROM (SELECT next_retry_at AS eligible_at FROM retention_items WHERE state='deletion_failed' AND retry_exhausted=0 AND read_denied_at IS NOT NULL AND next_retry_at>$now UNION ALL SELECT l.expires_at FROM retention_leases l JOIN retention_items i ON i.item_id=l.item_id WHERE i.state='deletion_queued' AND l.lease_kind IN ('access','operation') AND l.expires_at>$now UNION ALL SELECT l.expires_at FROM retention_leases l JOIN retention_items i ON i.item_id=l.item_id JOIN retention_delete_journal j ON j.item_id=i.item_id AND j.expected_revision=i.revision WHERE i.state='deleting' AND l.lease_kind='deletion' AND l.expires_at>$now UNION ALL SELECT maintenance_due_at FROM retention_worker_state WHERE id=1 AND maintenance_due_at>$now);";
        q.Parameters.AddWithValue("$now",now);var value=q.ExecuteScalar();return value is string timestamp?DateTimeOffset.Parse(timestamp,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind):null;
    }
    private static bool Owns(SqliteConnection c,SqliteTransaction t,RetentionDeleteFence fence,DateTimeOffset now){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT EXISTS(SELECT 1 FROM retention_items i JOIN retention_leases l ON l.item_id=i.item_id AND l.lease_kind='deletion' WHERE i.item_id=$id AND i.state='deleting' AND i.revision=$revision AND l.owner=$owner AND l.generation=$generation AND l.expires_at>$now);";q.Parameters.AddWithValue("$id",fence.ItemId);q.Parameters.AddWithValue("$revision",fence.ExpectedRevision);q.Parameters.AddWithValue("$owner",fence.LeaseOwner);q.Parameters.AddWithValue("$generation",fence.LeaseGeneration);q.Parameters.AddWithValue("$now",Timestamp(now));return Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture)!=0;}
    private RetentionMutationDisposition Complete(RetentionDeleteFence fence,DateTimeOffset at,CancellationToken token){token.ThrowIfCancellationRequested();using var c=Open();using var t=c.BeginTransaction();var result=Complete(c,t,fence,at);t.Commit();return result;}
    private static RetentionMutationDisposition Complete(SqliteConnection c,SqliteTransaction t,RetentionDeleteFence fence,DateTimeOffset at,Action<RetentionSqliteDeletePhase>? checkpoint=null){if(!Owns(c,t,fence,at)||!HasCurrentDeleteJournal(c,t,fence.ItemId,fence.ExpectedRevision)){using var final=c.CreateCommand();final.Transaction=t;final.CommandText="SELECT EXISTS(SELECT 1 FROM retention_items i JOIN retention_tombstones x ON x.item_id=i.item_id WHERE i.item_id=$id AND i.state='deleted');";final.Parameters.AddWithValue("$id",fence.ItemId);return Convert.ToInt64(final.ExecuteScalar(),CultureInfo.InvariantCulture)!=0?RetentionMutationDisposition.NoOpAlreadyFinalized:RetentionMutationDisposition.StaleNoOp;}var timestamp=Timestamp(at);Exec(c,t,"INSERT INTO retention_tombstones(item_id,receipt_at,deleted_at) VALUES($id,$at,$at) ON CONFLICT(item_id) DO NOTHING;",("$id",fence.ItemId),("$at",timestamp));checkpoint?.Invoke(RetentionSqliteDeletePhase.AfterTombstoneInserted);Exec(c,t,"UPDATE retention_items SET state='deleted',revision=revision+1,deleted_at=$at,error_code=NULL,next_retry_at=NULL WHERE item_id=$id AND revision=$revision;",("$id",fence.ItemId),("$revision",fence.ExpectedRevision),("$at",timestamp));checkpoint?.Invoke(RetentionSqliteDeletePhase.AfterDeletedStateUpdated);Exec(c,t,"DELETE FROM retention_leases WHERE item_id=$id AND lease_kind='deletion' AND owner=$owner AND generation=$generation;",("$id",fence.ItemId),("$owner",fence.LeaseOwner),("$generation",fence.LeaseGeneration));return RetentionMutationDisposition.Applied;}
    private RetentionFailureResult Fail(RetentionDeleteFence fence,RetentionErrorCode code,DateTimeOffset at,bool terminal,CancellationToken token){token.ThrowIfCancellationRequested();using var c=Open();using var t=c.BeginTransaction();if(!Owns(c,t,fence,at)||!HasCurrentDeleteJournal(c,t,fence.ItemId,fence.ExpectedRevision)){t.Commit();return new(RetentionMutationDisposition.StaleNoOp,0,null,false);}var attempt=ScalarInt(c,t,"SELECT attempt_count FROM retention_items WHERE item_id=$id",("$id",fence.ItemId));var exhausted=!terminal&&attempt>=RetentionV1Constants.MaximumDeleteAttempts;DateTimeOffset? retry=terminal||exhausted?null:at+RetentionV1Constants.RetryDelays[attempt-1];Exec(c,t,"UPDATE retention_items SET state='deletion_failed',revision=revision+1,error_code=$code,next_retry_at=$retry,retry_exhausted=$exhausted WHERE item_id=$id AND revision=$revision;",("$id",fence.ItemId),("$revision",fence.ExpectedRevision),("$code",Wire(code)),("$retry",retry is null?null:Timestamp(retry.Value)),("$exhausted",exhausted?1:0));Exec(c,t,"DELETE FROM retention_leases WHERE item_id=$id AND lease_kind='deletion' AND owner=$owner AND generation=$generation;",("$id",fence.ItemId),("$owner",fence.LeaseOwner),("$generation",fence.LeaseGeneration));t.Commit();return new(RetentionMutationDisposition.Applied,attempt,retry,exhausted);}
    private static RetentionStoreKind? TryParseKind(string value)=>value switch{"session_event_content"=>RetentionStoreKind.SessionEventContent,"raw_record"=>RetentionStoreKind.RawRecord,"analysis_run_raw"=>RetentionStoreKind.AnalysisRunRaw,"sensitive_bundle"=>RetentionStoreKind.SensitiveBundle,"analysis_sdk_directory"=>RetentionStoreKind.AnalysisSdkDirectory,_=>null};
    private static string Wire(RetentionErrorCode code)=>code switch{RetentionErrorCode.DeleteBusy=>"retention_delete_busy",RetentionErrorCode.DeletePermissionDenied=>"retention_delete_permission_denied",RetentionErrorCode.DeleteIoFailed=>"retention_delete_io_failed",RetentionErrorCode.InvalidIdentity=>"retention_invalid_identity",RetentionErrorCode.OwnershipMismatch=>"retention_ownership_mismatch",RetentionErrorCode.UnexpectedSourceMissing=>"retention_unexpected_source_missing",RetentionErrorCode.ItemLimitExceeded=>"retention_item_limit_exceeded",_=>"retention_lease_lost"};

    private static RetentionCatalogItem? FindForUpdate(SqliteConnection c,SqliteTransaction t,RetentionOwnershipKey key){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind AND source_item_id=$source;";q.Parameters.AddWithValue("$store",key.StoreInstanceId);q.Parameters.AddWithValue("$kind",RetentionSchemaMigrator.Wire(key.StoreKind));q.Parameters.AddWithValue("$source",key.SourceItemId);using var r=q.ExecuteReader();return r.Read()?Item(r,key):null;}
    private static RetentionCatalogItem Item(SqliteDataReader r,RetentionOwnershipKey key)=>new(r.GetString(0),key,DateTimeOffset.Parse(r.GetString(1),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),DateTimeOffset.Parse(r.GetString(2),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),Enum.Parse<RetentionItemLifecycle>(r.GetString(3).Replace("_",string.Empty),true),r.GetInt64(4),r.IsDBNull(5)?null:DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));
    private static void DenyAndQueue(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset now){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$now,queued_at=$now,revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.ExecuteNonQuery();}
    private static void DenyMissingSource(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset now){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='deletion_failed',read_denied_at=$now,error_code='retention_unexpected_source_missing',revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.ExecuteNonQuery();}
    private static void DenyInvalidSource(SqliteConnection c, SqliteTransaction t, string id, DateTimeOffset now, SourceReceiptProof proof) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='deletion_failed',read_denied_at=$now,error_code=$error,revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.Parameters.AddWithValue("$error", proof == SourceReceiptProof.InvalidIdentity ? "retention_invalid_identity" : "retention_ownership_mismatch");q.ExecuteNonQuery(); }
    private static void DenyForReadFailure(SqliteConnection c, SqliteTransaction t, RetentionCatalogItem item, DateTimeOffset now, SourceReceiptProof proof)
    {
        if (item.ReadDeniedAt is not null) return;
        if (now >= item.ExpiresAt) DenyAndQueue(c, t, item.ItemId, now);
        else if (proof == SourceReceiptProof.Missing) DenyMissingSource(c, t, item.ItemId, now);
        else DenyInvalidSource(c, t, item.ItemId, now, proof);
    }
    private static SourceReceiptProof SourceProof(SqliteConnection c, SqliteTransaction t, RetentionOwnershipKey key)
    {
        try
        {
            return SourceProofCore(c, t, key);
        }
        catch (ArgumentException) { return SourceReceiptProof.InvalidIdentity; }
        catch (FormatException) { return SourceReceiptProof.InvalidIdentity; }
        catch (SqliteException) { return SourceReceiptProof.Missing; }
    }
    private static SourceReceiptProof SourceProofCore(SqliteConnection c, SqliteTransaction t, RetentionOwnershipKey key)
    {
        var catalogReceipt = CatalogReceipt(c, t, key);
        if (catalogReceipt is null) return SourceReceiptProof.InvalidIdentity;
        using var q = c.CreateCommand(); q.Transaction = t;
        q.CommandText = key.StoreKind switch
        {
            RetentionStoreKind.SessionEventContent => "SELECT c.event_id,c.content_kind,c.captured_at,c.expires_at,e.session_id,e.run_id,e.source_adapter,e.source_event_id,c.retention_owner_token FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id WHERE c.event_id=$id;",
            RetentionStoreKind.RawRecord => "SELECT id,received_at,schema_version,retention_owner_token FROM raw_records WHERE id=$id;",
            RetentionStoreKind.AnalysisRunRaw => "SELECT id,requested_at,raw_record_id,span_id,retention_owner_token FROM monitor_analysis_runs WHERE id=$id AND (result_markdown IS NOT NULL OR error_message IS NOT NULL OR EXISTS(SELECT 1 FROM monitor_analysis_events e WHERE e.run_id=monitor_analysis_runs.id));",
            _ => "SELECT NULL WHERE 0;"
        };
        if (key.StoreKind == RetentionStoreKind.SessionEventContent) q.Parameters.AddWithValue("$id", key.SourceItemId);
        else if (TrySourceId(key.SourceItemId, out var id)) q.Parameters.AddWithValue("$id", id); else return SourceReceiptProof.InvalidIdentity;
        using var r = q.ExecuteReader(); if (!r.Read()) return SourceReceiptProof.Missing;
        var receipt = key.StoreKind switch
        {
            RetentionStoreKind.SessionEventContent => RetentionOwnershipReceipt.CreateSession(new(key.StoreInstanceId,r.GetString(0),r.GetString(1),r.GetString(2),Parse(r.GetString(2)).UtcDateTime.Ticks,r.GetString(3),Parse(r.GetString(3)).UtcDateTime.Ticks,r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),r.GetString(6),r.GetString(7),Blob(r,8))),
            RetentionStoreKind.RawRecord => RetentionOwnershipReceipt.CreateRawRecord(new(key.StoreInstanceId,r.GetInt64(0),r.GetString(1),Parse(r.GetString(1)).UtcDateTime.Ticks,r.GetInt32(2),Blob(r,3))),
            RetentionStoreKind.AnalysisRunRaw => RetentionOwnershipReceipt.CreateAnalysisRun(new(key.StoreInstanceId,r.GetInt64(0),r.GetString(1),Parse(r.GetString(1)).UtcDateTime.Ticks,r.IsDBNull(2)?null:r.GetInt64(2),r.IsDBNull(3)?null:r.GetString(3),Blob(r,4))),
            _ => []
        };
        return RetentionOwnershipReceipt.Matches(receipt, catalogReceipt) ? SourceReceiptProof.Match : SourceReceiptProof.InvalidOrMismatched;
    }
    private static byte[]? SourceToken(SqliteConnection connection, SqliteTransaction transaction, RetentionOwnershipKey key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = key.StoreKind switch
        {
            RetentionStoreKind.SessionEventContent => "SELECT retention_owner_token FROM session_event_content WHERE event_id=$id;",
            RetentionStoreKind.RawRecord => "SELECT retention_owner_token FROM raw_records WHERE id=$id;",
            RetentionStoreKind.AnalysisRunRaw => "SELECT retention_owner_token FROM monitor_analysis_runs WHERE id=$id;",
            _ => "SELECT NULL WHERE 0;"
        };
        if (key.StoreKind == RetentionStoreKind.SessionEventContent) command.Parameters.AddWithValue("$id", key.SourceItemId);
        else if (TrySourceId(key.SourceItemId, out var id)) command.Parameters.AddWithValue("$id", id);
        else return null;
        return command.ExecuteScalar() is byte[] token && token.Length == 32 ? token : null;
    }
    private static void ReleaseWithinTransaction(SqliteConnection connection, SqliteTransaction transaction, IEnumerable<RetentionReadGrant> grants)
    {
        foreach (var grant in grants)
            ReleaseWithinTransaction(connection, transaction, grant.ItemId, grant.LeaseOwner, grant.LeaseGeneration);
    }
    private static void ReleaseWithinTransaction(SqliteConnection connection, SqliteTransaction transaction, string itemId, string owner, long generation)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM retention_leases WHERE item_id=$id AND owner=$owner AND generation=$generation;";
        command.Parameters.AddWithValue("$id", itemId);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }
    private static byte[]? CatalogReceipt(SqliteConnection c, SqliteTransaction t, RetentionOwnershipKey key) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT ownership_receipt FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind AND source_item_id=$source;";q.Parameters.AddWithValue("$store",key.StoreInstanceId);q.Parameters.AddWithValue("$kind",RetentionSchemaMigrator.Wire(key.StoreKind));q.Parameters.AddWithValue("$source",key.SourceItemId);return q.ExecuteScalar() is byte[] receipt && receipt.Length == 32 ? receipt : null; }
    private static bool HasMatchingDeleteIntent(SqliteConnection c, SqliteTransaction t, RetentionCatalogItem item) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT EXISTS(SELECT 1 FROM retention_delete_journal WHERE item_id=$id AND expected_revision=$revision);";q.Parameters.AddWithValue("$id",item.ItemId);q.Parameters.AddWithValue("$revision",item.Revision);return Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture)==1; }
    private enum SourceReceiptProof { Match, Missing, InvalidIdentity, InvalidOrMismatched, CatalogBusy }
    private static bool TrySourceId(string sourceItemId, out long id) => long.TryParse(sourceItemId, CultureInfo.InvariantCulture, out id);
    private static long? AcquireLease(SqliteConnection c, SqliteTransaction t, string itemId, RetentionLeaseKind kind, string owner, DateTimeOffset now, DateTimeOffset? maximumExpiry = null) { var wireKind=kind.ToString().ToLowerInvariant(); using (var expired=c.CreateCommand()) { expired.Transaction=t;expired.CommandText="DELETE FROM retention_leases WHERE item_id=$id AND lease_kind <> $kind AND expires_at <= $now;";expired.Parameters.AddWithValue("$id",itemId);expired.Parameters.AddWithValue("$kind",wireKind);expired.Parameters.AddWithValue("$now",Timestamp(now));expired.ExecuteNonQuery(); } using var q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) SELECT $id,$kind,$owner,$expires,1 WHERE NOT EXISTS (SELECT 1 FROM retention_worker_state WHERE id=1 AND maintenance_owner IS NOT NULL AND maintenance_lease_expires_at>$now) AND NOT EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$id AND (($kind='deletion' AND lease_kind IN ('access','operation')) OR ($kind IN ('access','operation') AND lease_kind='deletion'))) ON CONFLICT(item_id,lease_kind) DO UPDATE SET owner=excluded.owner,expires_at=excluded.expires_at,generation=retention_leases.generation+1 WHERE retention_leases.expires_at <= $now AND NOT EXISTS (SELECT 1 FROM retention_worker_state WHERE id=1 AND maintenance_owner IS NOT NULL AND maintenance_lease_expires_at>$now) RETURNING generation;";q.Parameters.AddWithValue("$id",itemId);q.Parameters.AddWithValue("$kind",wireKind);q.Parameters.AddWithValue("$owner",owner);q.Parameters.AddWithValue("$expires",Timestamp(maximumExpiry is { } cap && cap < now + RetentionV1Constants.LeaseDuration ? cap : now + RetentionV1Constants.LeaseDuration));q.Parameters.AddWithValue("$now",Timestamp(now));var value=q.ExecuteScalar();return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
    private void Release(string id,RetentionLeaseKind kind,string owner,long generation){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM retention_leases WHERE item_id=$id AND lease_kind=$kind AND owner=$owner AND generation=$generation;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$kind",kind.ToString().ToLowerInvariant());q.Parameters.AddWithValue("$owner",owner);q.Parameters.AddWithValue("$generation",generation);q.ExecuteNonQuery();}
    private ValueTask ReleaseAsync(string id, RetentionLeaseKind kind, string owner, long generation) { try { Release(id, kind, owner, generation); } catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6) { } return ValueTask.CompletedTask; }
    private SqliteConnection Open(bool enforceForeignKeys = true){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());c.Open();using var foreignKeys=c.CreateCommand();foreignKeys.CommandText=$"PRAGMA foreign_keys={(enforceForeignKeys ? "ON" : "OFF")};";foreignKeys.ExecuteNonQuery();return c;}
    private SqliteConnection OpenExisting(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());c.Open();using var foreignKeys=c.CreateCommand();foreignKeys.CommandText="PRAGMA foreign_keys=ON;";foreignKeys.ExecuteNonQuery();return c;}
    private SqliteConnection OpenMaintenance(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Mode=SqliteOpenMode.ReadWrite,Pooling=false,DefaultTimeout=0}.ToString());c.Open();using var busy=c.CreateCommand();busy.CommandText="PRAGMA busy_timeout=0;";busy.ExecuteNonQuery();return c;}
    private static bool TableExists(SqliteConnection c,SqliteTransaction t,string name){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";q.Parameters.AddWithValue("$name",name);return Convert.ToInt64(q.ExecuteScalar())==1;}
    private static bool Exists(SqliteConnection c, SqliteTransaction t, string sql) { using var q=c.CreateCommand(); q.Transaction=t; q.CommandText=sql; return q.ExecuteScalar() is not null; }
    private static string StoreId(SqliteConnection c,SqliteTransaction t){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT store_instance_id FROM retention_store_instances WHERE id=1;";return (string)q.ExecuteScalar()!;}
    private static string Timestamp(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
}

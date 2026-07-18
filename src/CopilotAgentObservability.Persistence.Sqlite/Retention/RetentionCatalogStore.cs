using System.Globalization;
using CopilotAgentObservability.Persistence.Sqlite.Sessions;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed class RetentionCatalogStore
{
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    private readonly Action<SqliteConnection, SqliteTransaction>? backfillValidationCheckpoint;
    public RetentionCatalogStore(string databasePath, TimeProvider? timeProvider = null) { this.databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath)); this.timeProvider = timeProvider ?? TimeProvider.System; }
    internal RetentionCatalogStore(string databasePath, Action<SqliteConnection, SqliteTransaction> backfillValidationCheckpoint)
        : this(databasePath)
    {
        this.backfillValidationCheckpoint = backfillValidationCheckpoint;
    }

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

    private static RetentionCatalogItem? FindForUpdate(SqliteConnection c,SqliteTransaction t,RetentionOwnershipKey key){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind AND source_item_id=$source;";q.Parameters.AddWithValue("$store",key.StoreInstanceId);q.Parameters.AddWithValue("$kind",RetentionSchemaMigrator.Wire(key.StoreKind));q.Parameters.AddWithValue("$source",key.SourceItemId);using var r=q.ExecuteReader();return r.Read()?Item(r,key):null;}
    private static RetentionCatalogItem Item(SqliteDataReader r,RetentionOwnershipKey key)=>new(r.GetString(0),key,DateTimeOffset.Parse(r.GetString(1),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),DateTimeOffset.Parse(r.GetString(2),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),Enum.Parse<RetentionItemLifecycle>(r.GetString(3).Replace("_",string.Empty),true),r.GetInt64(4),r.IsDBNull(5)?null:DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));
    private static void DenyAndQueue(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset now){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$now,queued_at=$now,revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.ExecuteNonQuery();}
    private static void DenyMissingSource(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset now){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='deletion_failed',read_denied_at=$now,error_code='retention_unexpected_source_missing',revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.ExecuteNonQuery();}
    private static void DenyInvalidSource(SqliteConnection c, SqliteTransaction t, string id, DateTimeOffset now, SourceReceiptProof proof) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='deletion_failed',read_denied_at=$now,error_code=$error,revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.Parameters.AddWithValue("$error", proof == SourceReceiptProof.InvalidIdentity ? "retention_invalid_identity" : "retention_ownership_mismatch");q.ExecuteNonQuery(); }
    private static SourceReceiptProof SourceProof(SqliteConnection c, SqliteTransaction t, RetentionOwnershipKey key)
    {
        try
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
        catch (ArgumentException) { return SourceReceiptProof.InvalidIdentity; }
        catch (FormatException) { return SourceReceiptProof.InvalidIdentity; }
        catch (SqliteException) { return SourceReceiptProof.Missing; }
    }
    private static byte[]? CatalogReceipt(SqliteConnection c, SqliteTransaction t, RetentionOwnershipKey key) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT ownership_receipt FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind AND source_item_id=$source;";q.Parameters.AddWithValue("$store",key.StoreInstanceId);q.Parameters.AddWithValue("$kind",RetentionSchemaMigrator.Wire(key.StoreKind));q.Parameters.AddWithValue("$source",key.SourceItemId);return q.ExecuteScalar() is byte[] receipt && receipt.Length == 32 ? receipt : null; }
    private static bool HasMatchingDeleteIntent(SqliteConnection c, SqliteTransaction t, RetentionCatalogItem item) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT EXISTS(SELECT 1 FROM retention_delete_journal WHERE item_id=$id AND expected_revision=$revision);";q.Parameters.AddWithValue("$id",item.ItemId);q.Parameters.AddWithValue("$revision",item.Revision);return Convert.ToInt64(q.ExecuteScalar(),CultureInfo.InvariantCulture)==1; }
    private enum SourceReceiptProof { Match, Missing, InvalidIdentity, InvalidOrMismatched }
    private static bool TrySourceId(string sourceItemId, out long id) => long.TryParse(sourceItemId, CultureInfo.InvariantCulture, out id);
    private static long? AcquireLease(SqliteConnection c, SqliteTransaction t, string itemId, RetentionLeaseKind kind, string owner, DateTimeOffset now) { var wireKind=kind.ToString().ToLowerInvariant(); using (var expired=c.CreateCommand()) { expired.Transaction=t;expired.CommandText="DELETE FROM retention_leases WHERE item_id=$id AND lease_kind <> $kind AND expires_at <= $now;";expired.Parameters.AddWithValue("$id",itemId);expired.Parameters.AddWithValue("$kind",wireKind);expired.Parameters.AddWithValue("$now",Timestamp(now));expired.ExecuteNonQuery(); } using var q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) SELECT $id,$kind,$owner,$expires,1 WHERE NOT EXISTS (SELECT 1 FROM retention_leases WHERE item_id=$id AND (($kind='deletion' AND lease_kind IN ('access','operation')) OR ($kind IN ('access','operation') AND lease_kind='deletion'))) ON CONFLICT(item_id,lease_kind) DO UPDATE SET owner=excluded.owner,expires_at=excluded.expires_at,generation=retention_leases.generation+1 WHERE retention_leases.expires_at <= $now RETURNING generation;";q.Parameters.AddWithValue("$id",itemId);q.Parameters.AddWithValue("$kind",wireKind);q.Parameters.AddWithValue("$owner",owner);q.Parameters.AddWithValue("$expires",Timestamp(now + RetentionV1Constants.LeaseDuration));q.Parameters.AddWithValue("$now",Timestamp(now));var value=q.ExecuteScalar();return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
    private void Release(string id,RetentionLeaseKind kind,string owner,long generation){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM retention_leases WHERE item_id=$id AND lease_kind=$kind AND owner=$owner AND generation=$generation;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$kind",kind.ToString().ToLowerInvariant());q.Parameters.AddWithValue("$owner",owner);q.Parameters.AddWithValue("$generation",generation);q.ExecuteNonQuery();}
    private SqliteConnection Open(bool enforceForeignKeys = true){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());c.Open();using var foreignKeys=c.CreateCommand();foreignKeys.CommandText=$"PRAGMA foreign_keys={(enforceForeignKeys ? "ON" : "OFF")};";foreignKeys.ExecuteNonQuery();return c;}
    private static bool TableExists(SqliteConnection c,SqliteTransaction t,string name){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";q.Parameters.AddWithValue("$name",name);return Convert.ToInt64(q.ExecuteScalar())==1;}
    private static bool Exists(SqliteConnection c, SqliteTransaction t, string sql) { using var q=c.CreateCommand(); q.Transaction=t; q.CommandText=sql; return q.ExecuteScalar() is not null; }
    private static string StoreId(SqliteConnection c,SqliteTransaction t){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT store_instance_id FROM retention_store_instances WHERE id=1;";return (string)q.ExecuteScalar()!;}
    private static string Timestamp(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
}

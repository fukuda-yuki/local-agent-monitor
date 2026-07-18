using System.Globalization;
using Microsoft.Data.Sqlite;

namespace CopilotAgentObservability.Persistence.Sqlite.Retention;

public sealed class RetentionCatalogStore
{
    private readonly string databasePath;
    private readonly TimeProvider timeProvider;
    public RetentionCatalogStore(string databasePath, TimeProvider? timeProvider = null) { this.databasePath = databasePath ?? throw new ArgumentNullException(nameof(databasePath)); this.timeProvider = timeProvider ?? TimeProvider.System; }

    public void CreateSchema()
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var connection = Open(); using var transaction = connection.BeginTransaction();
        try { RetentionSchemaMigrator.Apply(connection, transaction); Backfill(connection, transaction, timeProvider.GetUtcNow()); transaction.Commit(); }
        catch (RetentionMigrationBlockedException) { transaction.Rollback(); throw; }
        catch (SqliteException) { transaction.Rollback(); throw new RetentionMigrationBlockedException(); }
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
        var sourceExists = SourceExists(connection, transaction, key);
        if (item.ReadDeniedAt is not null || now >= item.ExpiresAt || item.State is not RetentionItemLifecycle.Expiring and not RetentionItemLifecycle.RetainedByPolicy || !sourceExists)
        {
            if (item.ReadDeniedAt is null && item.State == RetentionItemLifecycle.Expiring && now >= item.ExpiresAt) DenyAndQueue(connection, transaction, item.ItemId, now);
            else if (item.ReadDeniedAt is null && !sourceExists) DenyMissingSource(connection, transaction, item.ItemId, now);
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
            using var q = c.CreateCommand(); q.Transaction = t; q.CommandText = "SELECT c.event_id,c.captured_at,c.expires_at FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id;"; using var r=q.ExecuteReader(); while(r.Read()) Add(c,t,store,RetentionStoreKind.SessionEventContent,r.GetString(0),r.GetString(0),r.GetString(1),r.GetString(2),now);
        }
        if (TableExists(c,t,"raw_records")) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT id,received_at FROM raw_records;";using var r=q.ExecuteReader();while(r.Read()){var id=r.GetInt64(0).ToString(CultureInfo.InvariantCulture);var at=r.GetString(1);Add(c,t,store,RetentionStoreKind.RawRecord,id,id,at,null,now);} }
        if (TableExists(c,t,"monitor_analysis_runs")) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT id,requested_at FROM monitor_analysis_runs WHERE result_markdown IS NOT NULL OR error_message IS NOT NULL OR EXISTS(SELECT 1 FROM monitor_analysis_events e WHERE e.run_id=monitor_analysis_runs.id);";using var r=q.ExecuteReader();while(r.Read()){var id=r.GetInt64(0).ToString(CultureInfo.InvariantCulture);var at=r.GetString(1);Add(c,t,store,RetentionStoreKind.AnalysisRunRaw,id,id,at,null,now);} }
    }

    private static void Add(SqliteConnection c, SqliteTransaction t,string store,RetentionStoreKind kind,string source,string owner,string captured,string? existingExpiry,DateTimeOffset now)
    {
        if (!DateTimeOffset.TryParse(captured,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var capturedAt) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(owner)) throw new RetentionMigrationBlockedException();
        DateTimeOffset expiresAt;
        if (existingExpiry is not null) { if(!DateTimeOffset.TryParse(existingExpiry,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out expiresAt)) throw new RetentionMigrationBlockedException(); } else expiresAt=capturedAt+RetentionV1Constants.RawDefaultTtl;
        var denied = expiresAt <= now; using var q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT INTO retention_items(item_id,store_instance_id,store_kind,source_item_id,owner_reference,captured_at,expires_at,policy_id,policy_version,state,revision,read_denied_at,queued_at,adapter_coverage_version) VALUES($id,$store,$kind,$source,$owner,$captured,$expires,'raw-default-90d',1,$state,1,$denied,$queued,$coverage) ON CONFLICT(store_instance_id,store_kind,source_item_id) DO NOTHING;";q.Parameters.AddWithValue("$id",Guid.NewGuid().ToString("N"));q.Parameters.AddWithValue("$store",store);q.Parameters.AddWithValue("$kind",RetentionSchemaMigrator.Wire(kind));q.Parameters.AddWithValue("$source",source);q.Parameters.AddWithValue("$owner",owner);q.Parameters.AddWithValue("$captured",captured);q.Parameters.AddWithValue("$expires",existingExpiry ?? Timestamp(expiresAt));q.Parameters.AddWithValue("$state",denied?"expired_pending_deletion":"expiring");q.Parameters.AddWithValue("$denied",denied?Timestamp(now):(object)DBNull.Value);q.Parameters.AddWithValue("$queued",denied?Timestamp(now):(object)DBNull.Value);q.Parameters.AddWithValue("$coverage",RetentionV1Constants.AdapterCoverageVersion);q.ExecuteNonQuery();
    }

    private static RetentionCatalogItem? FindForUpdate(SqliteConnection c,SqliteTransaction t,RetentionOwnershipKey key){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT item_id,captured_at,expires_at,state,revision,read_denied_at FROM retention_items WHERE store_instance_id=$store AND store_kind=$kind AND source_item_id=$source;";q.Parameters.AddWithValue("$store",key.StoreInstanceId);q.Parameters.AddWithValue("$kind",RetentionSchemaMigrator.Wire(key.StoreKind));q.Parameters.AddWithValue("$source",key.SourceItemId);using var r=q.ExecuteReader();return r.Read()?Item(r,key):null;}
    private static RetentionCatalogItem Item(SqliteDataReader r,RetentionOwnershipKey key)=>new(r.GetString(0),key,DateTimeOffset.Parse(r.GetString(1),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),DateTimeOffset.Parse(r.GetString(2),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind),Enum.Parse<RetentionItemLifecycle>(r.GetString(3).Replace("_",string.Empty),true),r.GetInt64(4),r.IsDBNull(5)?null:DateTimeOffset.Parse(r.GetString(5),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind));
    private static void DenyAndQueue(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset now){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='expired_pending_deletion',read_denied_at=$now,queued_at=$now,revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.ExecuteNonQuery();}
    private static void DenyMissingSource(SqliteConnection c,SqliteTransaction t,string id,DateTimeOffset now){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="UPDATE retention_items SET state='deletion_failed',read_denied_at=$now,error_code='retention_unexpected_source_missing',revision=revision+1 WHERE item_id=$id AND read_denied_at IS NULL;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$now",Timestamp(now));q.ExecuteNonQuery();}
    private static bool SourceExists(SqliteConnection c, SqliteTransaction t, RetentionOwnershipKey key) { var table = key.StoreKind switch { RetentionStoreKind.SessionEventContent => "session_event_content", RetentionStoreKind.RawRecord => "raw_records", RetentionStoreKind.AnalysisRunRaw => "monitor_analysis_runs", _ => null }; if (table is null || !TableExists(c, t, table) || (key.StoreKind == RetentionStoreKind.SessionEventContent && !TableExists(c, t, "session_events"))) return false; var sourceId = 0L; if (key.StoreKind != RetentionStoreKind.SessionEventContent && !TrySourceId(key.SourceItemId, out sourceId)) return false; using var q=c.CreateCommand();q.Transaction=t;q.CommandText=key.StoreKind switch { RetentionStoreKind.SessionEventContent => "SELECT EXISTS(SELECT 1 FROM session_event_content c JOIN session_events e ON e.event_id=c.event_id WHERE c.event_id=$id);", RetentionStoreKind.RawRecord => "SELECT EXISTS(SELECT 1 FROM raw_records WHERE id=$id);", RetentionStoreKind.AnalysisRunRaw when TableExists(c, t, "monitor_analysis_events") => "SELECT EXISTS(SELECT 1 FROM monitor_analysis_runs WHERE id=$id AND (result_markdown IS NOT NULL OR error_message IS NOT NULL OR EXISTS(SELECT 1 FROM monitor_analysis_events e WHERE e.run_id=monitor_analysis_runs.id)));", RetentionStoreKind.AnalysisRunRaw => "SELECT EXISTS(SELECT 1 FROM monitor_analysis_runs WHERE id=$id AND (result_markdown IS NOT NULL OR error_message IS NOT NULL));", _ => "SELECT 0;"};q.Parameters.AddWithValue("$id",key.StoreKind == RetentionStoreKind.SessionEventContent ? key.SourceItemId : sourceId);return Convert.ToInt64(q.ExecuteScalar())==1; }
    private static bool TrySourceId(string sourceItemId, out long id) => long.TryParse(sourceItemId, CultureInfo.InvariantCulture, out id);
    private static long? AcquireLease(SqliteConnection c, SqliteTransaction t, string itemId, RetentionLeaseKind kind, string owner, DateTimeOffset now) { using var q=c.CreateCommand();q.Transaction=t;q.CommandText="INSERT INTO retention_leases(item_id,lease_kind,owner,expires_at,generation) VALUES($id,$kind,$owner,$expires,1) ON CONFLICT(item_id,lease_kind) DO UPDATE SET owner=excluded.owner,expires_at=excluded.expires_at,generation=retention_leases.generation+1 WHERE retention_leases.expires_at <= $now RETURNING generation;";q.Parameters.AddWithValue("$id",itemId);q.Parameters.AddWithValue("$kind",kind.ToString().ToLowerInvariant());q.Parameters.AddWithValue("$owner",owner);q.Parameters.AddWithValue("$expires",Timestamp(now + RetentionV1Constants.LeaseDuration));q.Parameters.AddWithValue("$now",Timestamp(now));var value=q.ExecuteScalar();return value is null ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture); }
    private void Release(string id,RetentionLeaseKind kind,string owner,long generation){using var c=Open();using var q=c.CreateCommand();q.CommandText="DELETE FROM retention_leases WHERE item_id=$id AND lease_kind=$kind AND owner=$owner AND generation=$generation;";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$kind",kind.ToString().ToLowerInvariant());q.Parameters.AddWithValue("$owner",owner);q.Parameters.AddWithValue("$generation",generation);q.ExecuteNonQuery();}
    private SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,Mode=SqliteOpenMode.ReadWriteCreate,Pooling=false}.ToString());c.Open();using var foreignKeys=c.CreateCommand();foreignKeys.CommandText="PRAGMA foreign_keys=ON;";foreignKeys.ExecuteNonQuery();return c;}
    private static bool TableExists(SqliteConnection c,SqliteTransaction t,string name){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name);";q.Parameters.AddWithValue("$name",name);return Convert.ToInt64(q.ExecuteScalar())==1;}
    private static bool Exists(SqliteConnection c, SqliteTransaction t, string sql) { using var q=c.CreateCommand(); q.Transaction=t; q.CommandText=sql; return q.ExecuteScalar() is not null; }
    private static string StoreId(SqliteConnection c,SqliteTransaction t){using var q=c.CreateCommand();q.Transaction=t;q.CommandText="SELECT store_instance_id FROM retention_store_instances WHERE id=1;";return (string)q.ExecuteScalar()!;}
    private static string Timestamp(DateTimeOffset value)=>value.ToUniversalTime().ToString("O",CultureInfo.InvariantCulture);
}
